using ECommons.EzIpcManager;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EspiGambaHelper;

/// <summary>
/// SimpleBlackjack IPC. The round payloads are subscribed as <see cref="object"/> so Dalamud hands over SimpleBlackjack's
/// own objects; they are re-serialized to JSON here because their JsonProperty names are the only stable member names.
/// </summary>
internal sealed class BlackjackIpc
{
    [EzIPC("GetStats")] private Func<string, object?> GetStatsIPC = null!;
    [EzIPC("GetArchives")] private Func<Dictionary<string, string>?> GetArchivesIPC = null!;

    public event Action<string>? RoundFinished;

    public BlackjackIpc() => EzIPC.Init(this, "SimpleBlackjack", SafeWrapper.None);

    [EzIPCEvent("OnGameFinishedEx")]
    private void OnGameFinishedEx(object? payload) => RoundFinished?.Invoke(ToJson(payload));

    /// <summary>One JSON array of rounds per archive. Must run on the game thread: SimpleBlackjack reads its own state.</summary>
    public List<string> GetArchivedRoundsJson()
    {
        var archives = GetArchivesIPC() ?? [];
        return archives.Keys.Select(archiveId => ToJson(GetStatsIPC(archiveId))).ToList();
    }

    private static string ToJson(object? payload) => payload switch
    {
        null => "",
        string text => text,
        JToken token => token.ToString(Formatting.None),
        _ => JsonConvert.SerializeObject(payload),
    };
}

/// <summary>SimpleWheel IPC: GameEndedIPC fires once per game after its last spin; the archive is newest first, capped at 20000.</summary>
internal sealed class WheelIpc
{
    public const int MaxArchiveLimit = 20000;

    [EzIPC] private Func<Task<string>> GetArchiveIPC = null!;
    [EzIPC] private Func<int, Task<string>> GetArchiveLimitIPC = null!;

    public event Action<string>? GameEnded;

    public WheelIpc() => EzIPC.Init(this, "SimpleWheel", SafeWrapper.None);

    [EzIPCEvent("GameEndedIPC")]
    private void OnGameEnded(string json) => GameEnded?.Invoke(json);

    public Task<string> GetArchiveAsync()
    {
        try { return GetArchiveLimitIPC(MaxArchiveLimit); }
        catch (Dalamud.Plugin.Ipc.Exceptions.IpcNotReadyError) { return GetArchiveIPC(); }
    }
}
