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
    /// <summary>Record games into the local stats database as the gamba plugins announce them.</summary>
    public bool RecordLiveStats { get; set; } = true;
    // Enable is deliberately session-only: loading the plugin never starts giving out games.
}
