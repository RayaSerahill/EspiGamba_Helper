using System.Text;

namespace EspiGambaHelper.Core;

public sealed record QueuedTell(PlayerIdentity Player, string Command);

/// <summary>Game-thread-only queue; one send attempt per <see cref="SendInterval"/>, with no catch-up bursts.</summary>
public sealed class TellQueue(TimeProvider? clock = null)
{
    /// <summary>
    /// Minimum spacing between tells. The game drops chat sent too quickly with
    /// "Your message was not heard. You must wait before using /tell, /say, /yell, or /shout again."
    /// (LogMessage 749); the exact server cooldown is undocumented, so this is a conservative value.
    /// </summary>
    public static readonly TimeSpan SendInterval = TimeSpan.FromMilliseconds(1000);

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Queue<QueuedTell> messages = new();
    private long generation;
    private long? lastSend;

    public bool Enabled { get; private set; }
    public int Count => messages.Count;
    public long? CaptureTicket() => Enabled ? generation : null;

    public void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        Cancel();
    }

    public void Cancel()
    {
        generation++;
        messages.Clear();
        // Preserve lastSend across toggles so they cannot bypass the rate limit.
    }

    public bool Enqueue(long? ticket, PlayerIdentity player, string url)
    {
        if (!Enabled || ticket == null || ticket != generation) return false;
        if (string.IsNullOrWhiteSpace(url) || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("SimpleScratch did not return a single HTTP(S) URL.");
        if (player.Name.Split(' ').Length != 2 || player.Name.Any(c => !(char.IsLetter(c) || c is ' ' or '\'' or '-'))
            || string.IsNullOrWhiteSpace(player.WorldName) || player.WorldName.Any(c => !char.IsLetterOrDigit(c)))
            throw new ArgumentException("The tell recipient has an invalid name or home world.");
        var command = $"/tell {player.Name}@{player.WorldName} {url}";
        if (Encoding.UTF8.GetByteCount(command) > 500)
            throw new ArgumentException("The game URL is too long to send in one tell.");
        messages.Enqueue(new QueuedTell(player, command));
        return true;
    }

    public QueuedTell? TakeNext()
    {
        if (!Enabled || messages.Count == 0) return null;
        var now = clock.GetTimestamp();
        if (lastSend is { } last && clock.GetElapsedTime(last, now) < SendInterval) return null;
        lastSend = now;
        return messages.Dequeue();
    }
}
