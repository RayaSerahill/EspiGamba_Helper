using Dalamud.Game.ClientState.Party;
using EspiGambaHelper.Core;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace EspiGambaHelper;

internal static class PartyRoster
{
    public static unsafe List<PlayerIdentity> Read()
    {
        var members = new Dictionary<string, PlayerIdentity>();
        if (!Plugin.PlayerState.IsLoaded) return [];

        void Add(string name, uint worldId, string worldName)
        {
            if (string.IsNullOrWhiteSpace(name) || worldId == 0) return;
            var player = new PlayerIdentity(name, worldId, worldName);
            members[player.Key] = player;
        }

        void AddMember(IPartyMember? member)
        {
            if (member == null || member.ContentId == 0) return;
            Add(member.Name.TextValue, member.World.RowId, member.World.Value.Name.ToString());
        }

        for (var i = 0; i < Plugin.PartyList.Length; i++) AddMember(Plugin.PartyList[i]);
        if (Plugin.PartyList.IsAlliance)
        {
            // These slots contain both other alliance groups, regardless of our A/B/C placement.
            for (var i = 0; i < 20; i++)
                AddMember(Plugin.PartyList.CreateAllianceMemberReference(Plugin.PartyList.GetAllianceMemberAddress(i)));
        }

        // Cross-world groups can exist outside an instance, where the normal party list is empty.
        var crossRealm = InfoProxyCrossRealm.Instance();
        if (crossRealm != null && crossRealm->IsInCrossRealmParty)
        {
            var groups = crossRealm->CrossRealmGroups;
            for (var g = 0; g < Math.Min(crossRealm->GroupCount, groups.Length); g++)
            {
                ref var group = ref groups[g];
                var party = group.GroupMembers;
                for (var i = 0; i < Math.Min(group.GroupMemberCount, party.Length); i++)
                {
                    ref var member = ref party[i];
                    if (member.ContentId == 0 || member.HomeWorld <= 0) continue;
                    var world = Plugin.DataManager.GetExcelSheet<World>().GetRow((uint)member.HomeWorld);
                    Add(member.NameString, (uint)member.HomeWorld, world.Name.ToString());
                }
            }
        }
        return members.Values.ToList();
    }
}
