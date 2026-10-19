using ECommons.EzIpcManager;
using EspiGambaHelper.Core;

namespace EspiGambaHelper;

public sealed class ScratchIpc
{
    [EzIPC] private Func<CreatePlayerMessage, Task<string>> CreatePlayerIPC = null!;
    [EzIPC] private Func<string> GetPresetNamesIPC = null!;
    [EzIPC] private Func<string> GetThemesIPC = null!;

    public ScratchIpc() => EzIPC.Init(this, "SimpleScratch", SafeWrapper.None);

    public (string[] Presets, string[] Themes) GetChoices() =>
        (ChoiceList.Parse(GetPresetNamesIPC(), "presets"), ChoiceList.Parse(GetThemesIPC(), "themes"));

    public Task<string> Create(GameRequest request) => CreatePlayerIPC(new CreatePlayerMessage
    {
        PlayerName = request.Player.Name,
        CardCount = request.CardCount,
        PresetName = request.Settings.PresetName,
        ThemeName = string.IsNullOrWhiteSpace(request.Settings.ThemeName) ? null : request.Settings.ThemeName,
    });

    // Contract from IPC.txt. EzIPC/Dalamud converts this DTO across plugin assembly boundaries.
    public sealed class CreatePlayerMessage
    {
        public string PlayerName { get; set; } = "";
        public int CardCount { get; set; } = 5;
        public string? ThemeName { get; set; }
        public string? PresetName { get; set; }
        public string? PresetJson { get; set; }
        public bool ShowPrizeText { get; set; } = true;
    }
}
