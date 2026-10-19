using Dalamud.Configuration;
using EspiGambaHelper.Core;

namespace EspiGambaHelper;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public int DiceMaximum { get; set; } = 10;
    public int VipBonus { get; set; } = 5;
    public bool SendTellsAutomatically { get; set; }
    public List<VipPlayer> Vips { get; set; } = [];
    public string PresetName { get; set; } = "";
    public string ThemeName { get; set; } = "";
    // Enable is deliberately session-only: loading the plugin never starts giving out games.
}
