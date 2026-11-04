using System.Globalization;
using System.Text.Json;

namespace EspiGambaHelper.Core;

/// <summary>Lenient readers for the JSON the gamba plugins hand over IPC. Missing or oddly typed fields degrade to defaults.</summary>
internal static class JsonFields
{
    public static bool TryGet(this JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        return false;
    }

    public static string GetString(this JsonElement element, string name, string fallback = "")
    {
        if (!element.TryGet(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => fallback,
        };
    }

    public static long? GetInt64(this JsonElement element, string name)
    {
        if (!element.TryGet(name, out var value)) return null;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var number)) return number;
                if (value.TryGetDouble(out var real) && double.IsFinite(real) && Math.Abs(real) < long.MaxValue) return (long)Math.Round(real);
                return null;
            case JsonValueKind.String:
                return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            default:
                return null;
        }
    }

    /// <summary>Prize lists arrive as JSON arrays, as JSON-encoded strings of arrays, or as a single label.</summary>
    public static List<string> GetLabels(this JsonElement element, string name) =>
        element.TryGet(name, out var value) ? Labels(value) : [];

    private static List<string> Labels(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                var labels = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) labels.Add(item.GetString() ?? "");
                    else if (item.ValueKind == JsonValueKind.Object)
                        labels.Add(item.GetString("label", item.GetString("value", item.GetString("name"))));
                }
                return labels;
            case JsonValueKind.String:
                var text = value.GetString() ?? "";
                if (text.TrimStart().StartsWith('['))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(text);
                        return Labels(document.RootElement);
                    }
                    catch (JsonException) { }
                }
                return string.IsNullOrWhiteSpace(text) ? [] : [text];
            default:
                return [];
        }
    }

    public static DateTimeOffset? GetTimestamp(this JsonElement element, string name)
    {
        if (!element.TryGet(name, out var value)) return null;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetInt64(out var number) ? UnixTime.FromSecondsOrMilliseconds(number) : null;
            case JsonValueKind.String:
                return UnixTime.TryParse(value.GetString(), out var parsed) ? parsed : null;
            default:
                return null;
        }
    }
}

public static class UnixTime
{
    /// <summary>SimpleBlackjack stamps rounds in milliseconds; the web games use seconds or ISO-8601 text.</summary>
    public static DateTimeOffset FromSecondsOrMilliseconds(long value) =>
        Math.Abs(value) >= 100_000_000_000L ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value);

    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            value = FromSecondsOrMilliseconds(number);
            return true;
        }
        // Timestamps without a zone come from the game servers, which store UTC.
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }
}

/// <summary>
/// SimpleBlackjack rounds: OnGameFinishedEx sends one round, GetStats(archive) a list. Member names come from its
/// JsonProperty attributes (Time, BetsCollected, Payouts, Players, Saved, ArchiveID, Hands), so they survive obfuscation.
/// A round keeps its millisecond Time when it is archived later, which makes Time the stable key.
/// </summary>
public static class BlackjackRounds
{
    /// <param name="host">The local character; SimpleBlackjack keeps no shared stats, so every round is this install's own.</param>
    public static List<GameRecord> Parse(string json, bool live, string host = "")
    {
        var records = new List<GameRecord>();
        if (string.IsNullOrWhiteSpace(json)) return records;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
            foreach (var round in root.EnumerateArray()) Add(records, round, live, host);
        else Add(records, root, live, host);
        return records;
    }

    private static void Add(List<GameRecord> records, JsonElement round, bool live, string host)
    {
        if (round.ValueKind != JsonValueKind.Object || round.GetInt64("Time") is not { } time || time <= 0) return;
        var players = "";
        if (round.TryGet("Players", out var list) && list.ValueKind == JsonValueKind.Array)
            players = string.Join(", ", list.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.String)
                .Select(p => p.GetString())
                .Where(n => !string.IsNullOrWhiteSpace(n)));
        records.Add(new GameRecord(GameSource.Blackjack, time.ToString(CultureInfo.InvariantCulture),
            UnixTime.FromSecondsOrMilliseconds(time), players,
            round.GetInt64("BetsCollected") ?? 0, round.GetInt64("Payouts") ?? 0, round.GetRawText(), live, null, host));
    }
}

/// <summary>
/// SimpleScratch games. GameEndedIPC sends {player_id, player_name, wins, total_cards, prizes_won} when a player
/// finishes a game; only the archive (GetArchiveIPC) knows price_per_card and purchased_cards, and it uses its own
/// archive_id. Live rows therefore record prizes only and get their card sales filled in when the archive is imported.
/// The archive is shared between every dealer using a theme: is_mine and dealer ("Name@World") tell whose game it is.
/// Live events name no dealer and are taken to be the logged-in character's own games.
/// </summary>
public static class ScratchGames
{
    /// <param name="host">The logged-in character, who is assumed to have hosted every game announced live.</param>
    public static GameRecord? ParseLive(string json, DateTimeOffset receivedAt, string host = "")
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        var game = document.RootElement;
        if (game.ValueKind != JsonValueKind.Object) return null;
        var id = game.GetString("player_id");
        if (id.Length == 0) return null;
        var player = game.GetString("player_name");
        var prizes = game.GetLabels("prizes_won");
        var totalCards = game.GetInt64("total_cards") ?? 0;
        var wins = game.GetInt64("wins") ?? 0;
        return new GameRecord(GameSource.Scratch, "live:" + id, receivedAt, player, 0, GilLabel.Sum(prizes),
            game.GetRawText(), true, MatchKey(player, totalCards, wins, prizes.Count), host);
    }

    /// <param name="self">The local character; archive rows flagged is_mine belong to it even when their dealer text differs.</param>
    public static List<GameRecord> ParseArchive(string json, string? self = null)
    {
        var records = new List<GameRecord>();
        if (string.IsNullOrWhiteSpace(json)) return records;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGet("archive", out var wrapped)) root = wrapped;
        if (root.ValueKind != JsonValueKind.Array) return records;
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var id = entry.GetString("archive_id", entry.GetString("id"));
            if (id.Length == 0 || entry.GetTimestamp("archived_at") is not { } playedAt) continue;
            var stats = entry.TryGet("stats", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : entry;
            var player = entry.GetString("player_name");
            var prizes = stats.GetLabels("prizes_won");
            var totalCards = stats.GetInt64("total_cards") ?? 0;
            var wins = stats.GetInt64("wins") ?? 0;
            var collected = (stats.GetInt64("price_per_card") ?? 0) * (stats.GetInt64("purchased_cards") ?? 0);
            records.Add(new GameRecord(GameSource.Scratch, "archive:" + id, playedAt, player, collected, GilLabel.Sum(prizes),
                entry.GetRawText(), false, MatchKey(player, totalCards, wins, prizes.Count), Host(entry, entry.GetString("dealer"), self)));
        }
        return records;
    }

    private static string Host(JsonElement entry, string dealer, string? self) =>
        entry.TryGet("is_mine", out var mine) && mine.ValueKind == JsonValueKind.True && !string.IsNullOrWhiteSpace(self) ? self : dealer;

    public static string MatchKey(string player, long totalCards, long wins, int prizeCount) =>
        $"{player.Trim().ToLowerInvariant()}|{totalCards}|{wins}|{prizeCount}";

    /// <summary>Host of a stored row, read back from its raw payload (used to fill the host column of older databases).</summary>
    public static string HostFromDetails(string details)
    {
        if (string.IsNullOrWhiteSpace(details)) return "";
        using var document = JsonDocument.Parse(details);
        return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.GetString("dealer", document.RootElement.GetString("dealer_name")) : "";
    }
}

/// <summary>
/// SimpleWheel games. GameEndedIPC sends {game_uuid, player_name, player_homeworld, host_name, theme, preset, max_spins,
/// spins_paid, spins_used, spin_cost, prizes_won} after the last spin; the archive rows use {id, host_name,
/// player_nickname, theme, spins, spins_paid, spin_cost, preset, created_at, prizes}. Gil collected is spin_cost times
/// the spins paid for (all spins when spins_paid is 0), matching SimpleWheel's own archive totals. host_name is the
/// hosting dealer's character name; the archive lists every dealer who shares the theme.
/// </summary>
public static class WheelGames
{
    /// <param name="self">The logged-in character, taken as the host of every game announced live; null keeps the payload's host_name.</param>
    public static GameRecord? ParseLive(string json, DateTimeOffset receivedAt, string? self = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        var game = document.RootElement;
        if (game.ValueKind != JsonValueKind.Object) return null;
        var id = game.GetString("game_uuid");
        if (id.Length == 0) return null;
        var player = game.GetString("player_name");
        var host = game.GetString("host_name");
        var maxSpins = game.GetInt64("max_spins") ?? 0;
        var spinCost = game.GetInt64("spin_cost") ?? 0;
        var prizes = game.GetLabels("prizes_won");
        return new GameRecord(GameSource.Wheel, "live:" + id, receivedAt, player,
            Collected(spinCost, game.GetInt64("spins_paid") ?? 0, maxSpins), GilLabel.Sum(prizes),
            game.GetRawText(), true, MatchKey(player, host, maxSpins, spinCost, prizes.Count), string.IsNullOrWhiteSpace(self) ? host : self);
    }

    public static List<GameRecord> ParseArchive(string json)
    {
        var records = new List<GameRecord>();
        if (string.IsNullOrWhiteSpace(json)) return records;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return records;
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var id = entry.GetString("id", entry.GetString("game_uuid"));
            if (id.Length == 0) continue;
            if ((entry.GetTimestamp("created_at") ?? entry.GetTimestamp("archived_at")) is not { } playedAt) continue;
            var player = entry.GetString("player_nickname", entry.GetString("player_name"));
            var host = entry.GetString("host_name");
            var maxSpins = entry.GetInt64("spins") ?? entry.GetInt64("max_spins") ?? 0;
            var spinCost = entry.GetInt64("spin_cost") ?? 0;
            var prizes = entry.TryGet("prizes", out _) ? entry.GetLabels("prizes") : entry.GetLabels("prizes_won");
            records.Add(new GameRecord(GameSource.Wheel, "archive:" + id, playedAt, player,
                Collected(spinCost, entry.GetInt64("spins_paid") ?? 0, maxSpins), GilLabel.Sum(prizes),
                entry.GetRawText(), false, MatchKey(player, host, maxSpins, spinCost, prizes.Count), host));
        }
        return records;
    }

    public static string HostFromDetails(string details)
    {
        if (string.IsNullOrWhiteSpace(details)) return "";
        using var document = JsonDocument.Parse(details);
        return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.GetString("host_name") : "";
    }

    private static long Collected(long spinCost, long spinsPaid, long maxSpins) =>
        Math.Max(0, spinCost) * Math.Max(0, spinsPaid > 0 ? spinsPaid : maxSpins);

    public static string MatchKey(string player, string host, long maxSpins, long spinCost, int prizeCount) =>
        $"{player.Trim().ToLowerInvariant()}|{host.Trim().ToLowerInvariant()}|{maxSpins}|{spinCost}|{prizeCount}";
}
