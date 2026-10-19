namespace EspiGambaHelper.Core;

public sealed record VipPlayer(string Name, string World)
{
    public bool Matches(string name, string world) =>
        string.Equals(Name, name.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(World, world.Trim(), StringComparison.OrdinalIgnoreCase);
}

public static class VipList
{
    public static bool Contains(IEnumerable<VipPlayer> players, PlayerIdentity player) =>
        players.Any(vip => vip.Matches(player.Name, player.WorldName));

    public static bool TryAdd(List<VipPlayer> players, string name, string world, out string? error)
    {
        name = name.Trim();
        world = world.Trim();
        if (name.Length == 0 || world.Length == 0)
        {
            error = "Enter both a character name and home world.";
            return false;
        }
        if (players.Any(vip => vip.Matches(name, world)))
        {
            error = "This player is already a VIP.";
            return false;
        }
        players.Add(new VipPlayer(name, world));
        error = null;
        return true;
    }
}
