namespace EspiGambaHelper.Core;

public sealed record GameSettings(int DiceMaximum, string PresetName, string ThemeName, int VipBonus = 5);
public sealed record GameRequest(long Activation, PlayerIdentity Player, GameSettings Settings, int CardCount = 5);

public sealed class ActivationSession
{
    private readonly HashSet<string> entered = [];
    private GameSettings? settings;
    private long activation;

    public bool Enabled => settings != null;
    public int AttemptedCount => entered.Count;
    public int CreatedCount { get; private set; }
    public int FailedCount { get; private set; }
    public string? LastStatus { get; private set; }

    public void Enable(GameSettings newSettings)
    {
        if (Enabled) return;
        if (newSettings.DiceMaximum is < 2 or > 999 || string.IsNullOrWhiteSpace(newSettings.PresetName))
            throw new ArgumentException("Select a preset and a dice limit from 2 to 999.");
        if (newSettings.VipBonus < 0 || newSettings.VipBonus > int.MaxValue - newSettings.DiceMaximum)
            throw new ArgumentException("The VIP bonus must be nonnegative and fit within the card count limit.");
        activation++;
        settings = newSettings;
        entered.Clear();
        CreatedCount = FailedCount = 0;
        LastStatus = null;
    }

    public void Disable() => settings = null;

    public GameRequest? TryEnter(PlayerIdentity player, int diceMaximum, int rollResult = 0, bool isVip = false)
    {
        if (settings == null || settings.DiceMaximum != diceMaximum
            || rollResult < 0 || rollResult > diceMaximum || !entered.Add(player.Key))
            return null;
        return new GameRequest(activation, player, settings, rollResult + (isVip ? settings.VipBonus : 0));
    }

    public void Complete(GameRequest request, bool success, string status)
    {
        // An older request can finish after a disable/re-enable. It must not alter the new round.
        if (request.Activation != activation) return;
        if (success) CreatedCount++;
        else FailedCount++;
        LastStatus = status;
        // Keep failed/uncertain requests reserved too: the remote game may already exist.
    }
}
