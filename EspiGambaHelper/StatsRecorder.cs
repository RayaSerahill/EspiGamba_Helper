using Dalamud.Plugin.Ipc.Exceptions;
using EspiGambaHelper.Core;

namespace EspiGambaHelper;

/// <summary>
/// Owns the local stats database, records games as SimpleBlackjack, SimpleScratch and SimpleWheel announce them,
/// and imports whatever those plugins have archived. Live events arrive on the sending plugin's thread.
/// </summary>
internal sealed class StatsRecorder : IDisposable
{
    private const int RecentLimit = 200;

    private readonly StatsStore store;
    private readonly ScratchIpc scratch;
    private readonly BlackjackIpc? blackjack;
    private readonly WheelIpc? wheel;
    private long version;
    private int importing;
    private volatile bool disposed;

    internal string DatabasePath { get; }
    /// <summary>The logged-in character, kept current by the plugin on the game thread; null while logged out.</summary>
    internal string? Character { get; private set; }
    internal bool LiveEnabled { get; private set; }
    internal string? LastLiveStatus { get; private set; }
    internal string? LastImportStatus { get; private set; }
    internal bool Importing => Volatile.Read(ref importing) == 1;
    /// <summary>Changes whenever rows were added, so the UI knows to re-query.</summary>
    internal long Version => Interlocked.Read(ref version);

    public StatsRecorder(string databasePath, ScratchIpc scratch, bool liveEnabled)
    {
        DatabasePath = databasePath;
        store = StatsStore.Open(databasePath);
        this.scratch = scratch;
        LiveEnabled = liveEnabled;
        blackjack = TryInit(() => new BlackjackIpc(), "SimpleBlackjack");
        wheel = TryInit(() => new WheelIpc(), "SimpleWheel");
        if (blackjack != null) blackjack.RoundFinished += OnBlackjackRound;
        scratch.GameEnded += OnScratchGame;
        if (wheel != null) wheel.GameEnded += OnWheelGame;
        Plugin.Log.Information($"Stats database opened at {databasePath} ({store.Count()} games recorded).");
    }

    private static T? TryInit<T>(Func<T> create, string plugin) where T : class
    {
        try { return create(); }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Could not set up {plugin} IPC; its games will not be recorded.");
            return null;
        }
    }

    internal void SetCharacter(string? character) =>
        Character = string.IsNullOrWhiteSpace(character) ? null : character;

    internal void SetLiveEnabled(bool enabled)
    {
        LiveEnabled = enabled;
        LastLiveStatus = enabled ? "Live recording on." : "Live recording off.";
    }

    private void OnBlackjackRound(string json) =>
        Record("blackjack round", json, () => BlackjackRounds.Parse(json, live: true, Character ?? ""));

    // Games announced live are the logged-in character's own; only imported archive rows carry another dealer's name.
    private void OnScratchGame(string json) =>
        Record("scratch game", json, () => ScratchGames.ParseLive(json, DateTimeOffset.UtcNow, Character ?? "") is { } game ? [game] : []);

    private void OnWheelGame(string json) =>
        Record("wheel game", json, () => WheelGames.ParseLive(json, DateTimeOffset.UtcNow, Character) is { } game ? [game] : []);

    private void Record(string what, string json, Func<List<GameRecord>> parse)
    {
        if (disposed) return;
        if (!LiveEnabled)
        {
            LastLiveStatus = $"Ignored a {what}: live recording is off.";
            return;
        }
        try
        {
            var records = parse();
            if (records.Count == 0)
            {
                LastLiveStatus = $"Ignored a {what} with no usable data. See /xllog.";
                Plugin.Log.Warning($"Unusable {what} payload: {json}");
                return;
            }
            foreach (var record in records)
            {
                var outcome = store.Add(record);
                LastLiveStatus = outcome == AddOutcome.Duplicate
                    ? $"Skipped an already recorded {what} for {record.Player}."
                    : $"Recorded a {what} for {record.Player}: {GilLabel.Format(record.Profit)} gil profit.";
                Plugin.Log.Information(LastLiveStatus);
            }
            Interlocked.Increment(ref version);
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            LastLiveStatus = $"Could not record a {what}. See /xllog.";
            Plugin.Log.Error(ex, $"Could not record a {what}: {json}");
        }
    }

    internal void StartImport()
    {
        if (disposed || Interlocked.CompareExchange(ref importing, 1, 0) != 0) return;
        LastImportStatus = "Importing...";
        _ = Task.Run(ImportAsync);
    }

    private async Task ImportAsync()
    {
        try
        {
            var parts = new List<string>
            {
                await ImportSourceAsync("Blackjack", blackjack == null ? null : ImportBlackjackAsync),
                await ImportSourceAsync("Scratch", ImportScratchAsync),
                await ImportSourceAsync("Wheel", wheel == null ? null : ImportWheelAsync),
            };
            LastImportStatus = $"Import finished at {DateTime.Now:HH:mm}. " + string.Join(" | ", parts);
        }
        catch (Exception ex)
        {
            LastImportStatus = "Import failed. See /xllog.";
            Plugin.Log.Error(ex, "Stats import failed");
        }
        finally
        {
            Interlocked.Increment(ref version);
            Interlocked.Exchange(ref importing, 0);
        }
    }

    private async Task<string> ImportSourceAsync(string name, Func<Task<ImportResult>>? import)
    {
        if (import == null) return $"{name}: IPC unavailable";
        try
        {
            var result = await import().ConfigureAwait(false);
            Plugin.Log.Information($"Stats import from {name}: {result}.");
            return $"{name}: {result}";
        }
        catch (IpcNotReadyError)
        {
            return $"{name}: plugin not loaded";
        }
        catch (ObjectDisposedException)
        {
            return $"{name}: cancelled";
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Stats import from {name} failed");
            return $"{name}: failed (see /xllog)";
        }
    }

    private async Task<ImportResult> ImportBlackjackAsync()
    {
        List<string> archives = [];
        var self = "";
        await Plugin.Framework.RunOnFrameworkThread(() => { self = Character ?? ""; archives = blackjack!.GetArchivedRoundsJson(); }).ConfigureAwait(false);
        return store.AddRange(archives.SelectMany(json => BlackjackRounds.Parse(json, live: false, self)));
    }

    private async Task<ImportResult> ImportScratchAsync()
    {
        // Invoke the gate on the game thread like any other IPC call; the web request behind it completes off-thread.
        Task<string> archive = null!;
        string? self = null;
        await Plugin.Framework.RunOnFrameworkThread(() => { self = Character; archive = scratch.GetArchiveAsync(); }).ConfigureAwait(false);
        return store.AddRange(ScratchGames.ParseArchive(await archive.ConfigureAwait(false), self));
    }

    private async Task<ImportResult> ImportWheelAsync()
    {
        Task<string> archive = null!;
        await Plugin.Framework.RunOnFrameworkThread(() => { archive = wheel!.GetArchiveAsync(); }).ConfigureAwait(false);
        return store.AddRange(WheelGames.ParseArchive(await archive.ConfigureAwait(false)));
    }

    /// <summary>Games of the logged-in character; blackjack rounds always count, scratch and wheel only when hosted by them.</summary>
    internal StatsSummary Summarize(DateTimeOffset from, DateTimeOffset to) => store.Summarize(from, to, Character ?? "");

    internal List<GameRecord> Recent(DateTimeOffset from, DateTimeOffset to) => store.Recent(from, to, RecentLimit, Character ?? "");

    public void Dispose()
    {
        disposed = true;
        if (blackjack != null) blackjack.RoundFinished -= OnBlackjackRound;
        scratch.GameEnded -= OnScratchGame;
        if (wheel != null) wheel.GameEnded -= OnWheelGame;
        // A live write in progress holds the store lock; Dispose waits for it rather than racing it.
        store.Dispose();
    }
}
