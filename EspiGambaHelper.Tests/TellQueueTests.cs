using EspiGambaHelper.Core;
using Xunit;

namespace EspiGambaHelper.Tests;

public sealed class TellQueueTests
{
    private static readonly PlayerIdentity Alice = new("Alice Example", 21, "Ravana");
    private const string Url = "https://scratch.example/game/abc?code=123";

    private sealed class TestClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(int milliseconds) => timestamp += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }

    [Fact]
    public void TwelveSimultaneousCreationsSendOneEverySecond()
    {
        var clock = new TestClock();
        var queue = new TellQueue(clock);
        queue.SetEnabled(true);
        for (var i = 0; i < 12; i++)
            Assert.True(queue.Enqueue(queue.CaptureTicket(), Alice with { Name = $"Player Member{(char)('a' + i)}" }, $"https://scratch.example/{i}"));
        for (var i = 0; i < 12; i++)
        {
            var next = Assert.IsType<QueuedTell>(queue.TakeNext());
            Assert.Equal($"/tell Player Member{(char)('a' + i)}@Ravana https://scratch.example/{i}", next.Command);
            Assert.Equal(11 - i, queue.Count);
            Assert.Null(queue.TakeNext());
            clock.Advance(999);
            Assert.Null(queue.TakeNext());
            clock.Advance(1);
        }
        Assert.Null(queue.TakeNext());
    }

    [Fact]
    public void DelayedFramesDoNotCauseCatchUpBursts()
    {
        var clock = new TestClock();
        var queue = new TellQueue(clock);
        queue.SetEnabled(true);
        for (var i = 0; i < 3; i++) queue.Enqueue(queue.CaptureTicket(), Alice, Url);
        Assert.NotNull(queue.TakeNext());
        clock.Advance(10000);
        Assert.NotNull(queue.TakeNext());
        Assert.Null(queue.TakeNext());
        clock.Advance(1000);
        Assert.NotNull(queue.TakeNext());
    }

    [Fact]
    public void TellQueuedAfterQueueDrainedStillWaitsFullIntervalSinceLastSend()
    {
        var clock = new TestClock();
        var queue = new TellQueue(clock);
        queue.SetEnabled(true);
        queue.Enqueue(queue.CaptureTicket(), Alice, Url);
        Assert.NotNull(queue.TakeNext());
        Assert.Equal(0, queue.Count);
        // The queue sits empty for a while, then a new game completes.
        clock.Advance(300);
        queue.Enqueue(queue.CaptureTicket(), Alice with { Name = "Bob Example" }, Url);
        Assert.Null(queue.TakeNext());
        clock.Advance(699);
        Assert.Null(queue.TakeNext());
        clock.Advance(1);
        Assert.NotNull(queue.TakeNext());
    }

    [Fact]
    public void DisablingDropsQueueAndRejectsLateCompletionsEvenAfterReenable()
    {
        var queue = new TellQueue(new TestClock());
        queue.SetEnabled(true);
        var oldTicket = queue.CaptureTicket();
        queue.Enqueue(oldTicket, Alice, Url);
        queue.SetEnabled(false);
        Assert.Equal(0, queue.Count);
        Assert.Null(queue.TakeNext());
        Assert.False(queue.Enqueue(oldTicket, Alice, Url));
        queue.SetEnabled(true);
        Assert.False(queue.Enqueue(oldTicket, Alice, Url));
        Assert.True(queue.Enqueue(queue.CaptureTicket(), Alice, Url));
    }

    [Fact]
    public void CreationsStartedWithOptionOffNeverSendRetroactively()
    {
        var queue = new TellQueue(new TestClock());
        var ticket = queue.CaptureTicket();
        Assert.Null(ticket);
        Assert.False(queue.Enqueue(ticket, Alice, Url));
        queue.SetEnabled(true);
        Assert.False(queue.Enqueue(ticket, Alice, Url));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void CancelForRoundEndOrLogoutInvalidatesPendingWorkAndPreservesRateLimit()
    {
        var clock = new TestClock();
        var queue = new TellQueue(clock);
        queue.SetEnabled(true);
        var oldTicket = queue.CaptureTicket();
        queue.Enqueue(oldTicket, Alice, Url);
        Assert.NotNull(queue.TakeNext());
        queue.Enqueue(oldTicket, Alice, Url);
        queue.Cancel();
        Assert.True(queue.Enabled);
        Assert.Equal(0, queue.Count);
        Assert.False(queue.Enqueue(oldTicket, Alice, Url));
        queue.Enqueue(queue.CaptureTicket(), Alice, Url);
        Assert.Null(queue.TakeNext());
        clock.Advance(1000);
        Assert.NotNull(queue.TakeNext());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("not a URL")]
    [InlineData("https://scratch.example/abc\n/say something")]
    [InlineData("file:///tmp/game")]
    public void InvalidResponsesAreNotSent(string response)
    {
        var queue = new TellQueue(new TestClock());
        queue.SetEnabled(true);
        Assert.Throws<ArgumentException>(() => queue.Enqueue(queue.CaptureTicket(), Alice, response));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void PreservesExactUrlAndTargetsHomeWorld()
    {
        var queue = new TellQueue(new TestClock());
        queue.SetEnabled(true);
        queue.Enqueue(queue.CaptureTicket(), Alice, Url);
        Assert.Equal($"/tell Alice Example@Ravana {Url}", queue.TakeNext()!.Command);
    }

    [Fact]
    public void LongUrlIsRejectedWithoutBlockingFollowingMessages()
    {
        var queue = new TellQueue(new TestClock());
        queue.SetEnabled(true);
        Assert.Throws<ArgumentException>(() => queue.Enqueue(queue.CaptureTicket(), Alice, "https://scratch.example/" + new string('a', 500)));
        Assert.True(queue.Enqueue(queue.CaptureTicket(), Alice, Url));
        Assert.NotNull(queue.TakeNext());
    }
}
