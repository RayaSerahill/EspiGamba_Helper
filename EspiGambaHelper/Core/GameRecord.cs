using System.Globalization;

namespace EspiGambaHelper.Core;

public enum GameSource
{
    Blackjack = 0,
    Scratch = 1,
    Wheel = 2,
}

/// <summary>One finished blackjack round, scratch game or wheel game, normalized to gil taken in and gil paid out.</summary>
/// <param name="Key">Unique within the source. Live rows of web games use "live:{id}", archive rows "archive:{id}".</param>
/// <param name="MatchKey">Content fingerprint that links a live row to its archive row when the two carry different ids.</param>
/// <param name="Host">Character that hosted the game, as the source plugin reports it ("Name" or "Name@World"); empty when unknown.</param>
public sealed record GameRecord(
    GameSource Source,
    string Key,
    DateTimeOffset PlayedAt,
    string Player,
    long Collected,
    long PaidOut,
    string Details,
    bool Live,
    string? MatchKey = null,
    string Host = "")
{
    public long Profit => Collected - PaidOut;
}

public static class GameHosts
{
    /// <summary>
    /// SimpleScratch and SimpleWheel log in with the local character name, so their host and dealer fields hold that name,
    /// sometimes followed by "@World". Only the name decides; the world is checked when both sides have one.
    /// </summary>
    public static bool Matches(string? host, string? character)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(character)) return false;
        var (hostName, hostWorld) = Split(host);
        var (name, world) = Split(character);
        return hostName.Equals(name, StringComparison.OrdinalIgnoreCase)
            && (hostWorld.Length == 0 || world.Length == 0 || hostWorld.Equals(world, StringComparison.OrdinalIgnoreCase));
    }

    private static (string Name, string World) Split(string identity)
    {
        var parts = identity.Split(['@', '\uE05D'], 2, StringSplitOptions.TrimEntries);
        return (parts[0], parts.Length > 1 ? parts[1] : "");
    }
}

public static class GameSources
{
    public static readonly GameSource[] All = [GameSource.Blackjack, GameSource.Scratch, GameSource.Wheel];

    public static string DisplayName(this GameSource source) => source switch
    {
        GameSource.Blackjack => "Blackjack",
        GameSource.Scratch => "Scratch",
        GameSource.Wheel => "Wheel",
        _ => source.ToString(),
    };
}

/// <summary>
/// SimpleScratch and SimpleWheel label gil prizes as "&lt;amount&gt;[K|M] gil" (their own archive views parse the same
/// shape). Any other label is an item prize and counts as 0 gil.
/// </summary>
public static class GilLabel
{
    public static bool TryParse(string? label, out long gil)
    {
        gil = 0;
        if (string.IsNullOrWhiteSpace(label)) return false;
        var parts = label.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !parts[1].Equals("gil", StringComparison.OrdinalIgnoreCase)) return false;
        var number = parts[0];
        var multiplier = 1d;
        switch (char.ToUpperInvariant(number[^1]))
        {
            case 'K': multiplier = 1_000; number = number[..^1]; break;
            case 'M': multiplier = 1_000_000; number = number[..^1]; break;
        }
        if (!double.TryParse(number, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var amount)
            || !double.IsFinite(amount) || amount < 0 || amount * multiplier > long.MaxValue)
            return false;
        gil = (long)Math.Round(amount * multiplier);
        return true;
    }

    public static long Sum(IEnumerable<string?> labels)
    {
        long total = 0;
        foreach (var label in labels)
            if (TryParse(label, out var gil)) total += gil;
        return total;
    }

    public static string Format(long gil) => gil.ToString("N0", CultureInfo.InvariantCulture);
}
