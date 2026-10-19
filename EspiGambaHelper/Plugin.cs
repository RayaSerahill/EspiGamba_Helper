using System.Diagnostics;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using EspiGambaHelper.Core;

namespace EspiGambaHelper;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    private static readonly HashSet<XivChatType> DiceChannels =
    [
        XivChatType.Say, XivChatType.Shout, XivChatType.Yell,
        XivChatType.Party, XivChatType.CrossParty, XivChatType.Alliance,
        XivChatType.TellIncoming, XivChatType.TellOutgoing, XivChatType.FreeCompany,
        XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4,
        XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
        XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3, XivChatType.CrossLinkShell4,
        XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6, XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
    ];

    private readonly WindowSystem windows = new("EspiGambaHelper");
    private readonly MainWindow window;
    private readonly ScratchIpc scratch;
    private readonly List<(GameRequest Request, Task<string> Task, long? TellTicket)> pending = [];
    private readonly TellQueue tells = new();
    private readonly AsyncTellDispatcher tellDispatcher;
    private bool disposed;
    private ulong tellOwnerContentId;

    internal Configuration Configuration { get; }
    internal ActivationSession Session { get; } = new();
    internal string[] Presets { get; private set; } = [];
    internal string[] Themes { get; private set; } = [];
    internal string? Error { get; private set; }
    internal bool ChoicesLoaded { get; private set; }
    internal string? LastRollStatus { get; private set; }
    internal int QueuedTellCount => tells.Count;
    internal string? LastTellStatus { get; private set; }

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        ECommonsMain.Init(pluginInterface, this);
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.DiceMaximum = Math.Clamp(Configuration.DiceMaximum, 2, 999);
        tells.SetEnabled(Configuration.SendTellsAutomatically);
        SyncTellOwner();
        scratch = new ScratchIpc();
        tellDispatcher = new AsyncTellDispatcher(
            token => Framework.RunOnTick(DispatchOneTell, delayTicks: 1, cancellationToken: token),
            ex => Log.Error(ex, "Could not schedule the next game URL tell"));
        window = new MainWindow(this);
        windows.AddWindow(window);
        CommandManager.AddHandler("/espigamba", new CommandInfo(OnCommand) { HelpMessage = "Open EspiGamba Helper." });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
        ChatGui.ChatMessage += OnChatMessage;
        Framework.Update += OnUpdate;
        Log.Information("EspiGamba Helper loaded. Roll listener registered; enable Scratch to start a round.");
    }

    internal void Save() => PluginInterface.SavePluginConfig(Configuration);

    internal void SetAutomaticTells(bool enabled)
    {
        Configuration.SendTellsAutomatically = enabled;
        tells.SetEnabled(enabled);
        LastTellStatus = null;
        Save();
    }

    private void SyncTellOwner()
    {
        var current = PlayerState.IsLoaded ? PlayerState.ContentId : 0;
        if (tellOwnerContentId == current) return;
        tellOwnerContentId = current;
        tells.Cancel();
    }

    internal void RefreshChoices()
    {
        try
        {
            (Presets, Themes) = scratch.GetChoices();
            ChoicesLoaded = true;
            Error = null;
        }
        catch (Exception ex)
        {
            ChoicesLoaded = false;
            Error = "Could not read SimpleScratch presets/themes. Enable SimpleScratch, then click Refresh.";
            Log.Error(ex, "Could not read SimpleScratch choices");
        }
    }

    internal void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            Session.Disable();
            tells.Cancel();
            Log.Information("Scratch disabled.");
            return;
        }
        if (Configuration.DiceMaximum is < 2 or > 999)
        {
            Error = "Enter a dice maximum from 2 to 999 before enabling.";
            return;
        }
        if (Configuration.VipBonus < 0 || Configuration.VipBonus > int.MaxValue - Configuration.DiceMaximum)
        {
            Error = "Enter a VIP bonus of 0 or more that fits within the card count limit.";
            return;
        }
        RefreshChoices();
        if (!ChoicesLoaded) return;
        if (!Presets.Contains(Configuration.PresetName) ||
            (Configuration.ThemeName.Length > 0 && !Themes.Contains(Configuration.ThemeName)))
        {
            Error = "Choose an available preset and theme before enabling.";
            return;
        }
        Error = null;
        Session.Enable(new GameSettings(Configuration.DiceMaximum, Configuration.PresetName, Configuration.ThemeName, Configuration.VipBonus));
        LastRollStatus = null;
        Log.Information($"Scratch enabled: dice maximum {Configuration.DiceMaximum}, preset '{Configuration.PresetName}', theme '{Configuration.ThemeName}', VIP bonus {Configuration.VipBonus}.");
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            // Use original data so chat-formatting plugins cannot change the roller or number.
            var original = SeString.Parse(message.OriginalMessage.Data.Span);
            var hasDiceIcon = original.Payloads.OfType<IconPayload>().Any(p => p.Icon == BitmapFontIcon.Dice);
            if (!hasDiceIcon && !original.TextValue.Contains("Random!", StringComparison.Ordinal)) return;
            var sender = SeString.Parse(message.OriginalSender.Data.Span);
            Log.Information($"Roll received: channel={message.LogKind} ({(int)message.LogKind}), sender='{sender.TextValue}', message='{original.TextValue}', diceIcon={hasDiceIcon}.");
            if (!Session.Enabled) { RollStatus("Ignored roll: Scratch is disabled."); return; }
            if (!PlayerState.IsLoaded) { RollStatus("Ignored roll: local player is not loaded."); return; }
            if (!DiceChannels.Contains(message.LogKind))
            {
                RollStatus($"Ignored roll: unsupported channel {message.LogKind}; use /dice in party or alliance chat.");
                return;
            }
            // The game's dice icon is a structured payload, unlike a typed imitation of a roll.
            if (!hasDiceIcon) { RollStatus("Ignored roll: the game dice icon is missing."); return; }
            var roll = DiceParser.Parse(original.TextValue);
            if (roll == null)
            {
                RollStatus("Ignored roll: unrecognized roll format. See the received message in /xllog.");
                Log.Information($"Unrecognized roll payload: {message.OriginalMessage.ToMacroString()}");
                return;
            }
            if (roll.Maximum != Configuration.DiceMaximum)
            {
                RollStatus($"Ignored roll: maximum {roll.Maximum} does not match configured maximum {Configuration.DiceMaximum}.");
                return;
            }
            var roster = PartyRoster.Read();
            PlayerIdentity? player;
            if (roll.IsSelf)
                player = RosterMatcher.Find(roster, PlayerState.CharacterName, PlayerState.HomeWorld.RowId);
            else
            {
                var payload = roll.UsesChatSender
                    ? sender.Payloads.OfType<PlayerPayload>().FirstOrDefault()
                    : original.Payloads.OfType<PlayerPayload>().FirstOrDefault()
                        ?? sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
                player = payload != null
                    ? RosterMatcher.Find(roster, payload.PlayerName, payload.World.RowId)
                    : RosterMatcher.Find(roster, roll.UsesChatSender ? sender.TextValue : roll.Name);
            }
            if (player == null)
            {
                RollStatus($"Ignored roll: could not match '{(roll.UsesChatSender ? sender.TextValue : roll.Name)}' to one party/alliance member ({roster.Count} members found).");
                Log.Information($"Party/alliance roster: {string.Join(", ", roster.Select(p => $"{p.Name}@{p.WorldName} ({p.WorldId})"))}");
                return;
            }
            var request = Session.TryEnter(player, roll.Maximum, roll.Result, VipList.Contains(Configuration.Vips, player));
            if (request == null) { RollStatus($"Ignored roll: {player.Name} already entered this round."); return; }
            try
            {
                RollStatus($"Creating {request.CardCount} cards for {player.Name} (roll {roll.Result}).");
                SyncTellOwner();
                var tellTicket = tells.CaptureTicket();
                var start = Stopwatch.GetTimestamp();
                Task<string> task;
                try { task = scratch.Create(request) ?? throw new InvalidOperationException("SimpleScratch returned no task."); }
                finally { LogSlowCall("SimpleScratch CreatePlayerIPC initial invocation", start); }
                pending.Add((request, task, tellTicket));
            }
            catch (Exception ex) { Failed(request, ex); }
        }
        catch (Exception ex)
        {
            RollStatus("Could not process the roll. See /xllog for the error.");
            Log.Error(ex, "Could not process a dice roll");
        }
    }

    private void RollStatus(string status)
    {
        LastRollStatus = status;
        Log.Information(status);
    }

    private void OnUpdate(IFramework framework)
    {
        SyncTellOwner();
        // Observe completed requests on the game thread. No background continuation mutates UI state.
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            var (request, task, tellTicket) = pending[i];
            if (!task.IsCompleted) continue;
            pending.RemoveAt(i);
            try
            {
                var result = task.GetAwaiter().GetResult();
                Log.Information($"SimpleScratch CreatePlayerIPC response for {request.Player.Key}: {result ?? "<null>"}");
                if (string.IsNullOrWhiteSpace(result) || result.Trim() == "null")
                    throw new InvalidOperationException("SimpleScratch returned no game.");
                Session.Complete(request, true, $"Created a scratch game with {request.CardCount} cards for {request.Player.Name}.");
                Log.Information($"Created a scratch game with {request.CardCount} cards for {request.Player.Key}.");
                // Tell failures must not turn a successfully created game into a failed game.
                try
                {
                    if (tells.Enqueue(tellTicket, request.Player, result))
                        Log.Information($"Queued game URL tell for {request.Player.Name}@{request.Player.WorldName}.");
                }
                catch (Exception ex)
                {
                    LastTellStatus = $"Game created, but its URL could not be queued for {request.Player.Name}. See /xllog.";
                    Log.Error(ex, LastTellStatus);
                }
            }
            catch (Exception ex) { Failed(request, ex); }
        }

        if (PlayerState.IsLoaded && Session.Enabled && tells.Count > 0)
            tellDispatcher.RequestSend();
    }

    private void DispatchOneTell()
    {
        // Called only by the scheduled game-tick callback; native chat APIs stay on the game thread.
        if (disposed) return;
        SyncTellOwner();
        if (!PlayerState.IsLoaded || !Session.Enabled) return;
        var tell = tells.TakeNext();
        if (tell == null) return;
        try
        {
            Log.Debug($"Submitting queued tell to {tell.Player.Name}@{tell.Player.WorldName}.");
            var start = Stopwatch.GetTimestamp();
            try { Chat.SendMessage(tell.Command); }
            finally { LogSlowCall("Native chat send (including chat hooks)", start); }
            LastTellStatus = $"Submitted game URL tell to {tell.Player.Name}@{tell.Player.WorldName}.";
            Log.Information(LastTellStatus);
        }
        catch (Exception ex)
        {
            LastTellStatus = $"Could not send the game URL to {tell.Player.Name}. See /xllog.";
            Log.Error(ex, LastTellStatus);
        }
    }

    private static void LogSlowCall(string operation, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed.TotalMilliseconds >= 100)
            Log.Warning($"{operation} blocked the game thread for {elapsed.TotalMilliseconds:F0} ms.");
    }

    private void Failed(GameRequest request, Exception ex)
    {
        Session.Complete(request, false, $"Could not confirm the game for {request.Player.Name}. Check SimpleScratch before starting a new activation.");
        Log.Error(ex, $"SimpleScratch creation failed for {request.Player.Key}");
    }

    private void OnCommand(string command, string arguments) => OpenWindow();
    private void OpenWindow()
    {
        if (!ChoicesLoaded) RefreshChoices();
        window.IsOpen = true;
    }

    public void Dispose()
    {
        disposed = true;
        tellDispatcher.Dispose();
        Session.Disable();
        tells.Cancel();
        ChatGui.ChatMessage -= OnChatMessage;
        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
        CommandManager.RemoveHandler("/espigamba");
        windows.RemoveAllWindows();
        // Calls already accepted by SimpleScratch cannot be cancelled. Observe faults after unload.
        foreach (var (_, task, _) in pending)
            _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        pending.Clear();
        ECommonsMain.Dispose();
    }
}
