namespace EspiGambaHelper.Core;

public sealed record PlayerIdentity(string Name, uint WorldId, string WorldName)
{
    public string Key => $"{Name}@{WorldId}";
}

public static class RosterMatcher
{
    public static PlayerIdentity? Find(IEnumerable<PlayerIdentity> roster, string name, uint? worldId = null)
    {
        // Cross-world chat uses a private-use world separator. Never strip and ignore its world.
        var parts = name.Split('\uE05D', 2, StringSplitOptions.TrimEntries);
        var candidates = roster.Where(p => p.Name == parts[0]
            && (worldId == null || p.WorldId == worldId)
            && (parts.Length == 1 || p.WorldName == parts[1])).DistinctBy(p => p.Key).Take(2).ToArray();
        // A plain name shared by multiple worlds is ambiguous: do not give the wrong player a game.
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
