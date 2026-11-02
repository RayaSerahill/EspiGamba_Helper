using System.Threading.Channels;
using EspiGambaHelper.Core;
using Xunit;

namespace EspiGambaHelper.Tests;

public sealed class AsyncTellDispatcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task SignalsNeverWaitForSendAndCooldownIsAfterSendCompletion()
    {
        var sends = Channel.CreateUnbounded<TaskCompletionSource>();
        var delays = Channel.CreateUnbounded<(TimeSpan Duration, TaskCompletionSource Gate)>();
        using var dispatcher = new AsyncTellDispatcher(token =>
        {
            var gate = Gate();
            sends.Writer.TryWrite(gate);
            return gate.Task.WaitAsync(token);
        }, _ => Assert.Fail("Unexpected dispatch failure"), (duration, token) =>
        {
            var gate = Gate();
            delays.Writer.TryWrite((duration, gate));
            return gate.Task.WaitAsync(token);
        });

        dispatcher.RequestSend();
        var first = await sends.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        // Simulate many framework updates while the first game-tick callback is still pending.
        for (var i = 0; i < 12; i++) dispatcher.RequestSend();
        Assert.False(sends.Reader.TryRead(out _));
        Assert.False(delays.Reader.TryRead(out _));
        first.SetResult();
        var cooldown = await delays.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        Assert.Equal(TellQueue.SendInterval, cooldown.Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), cooldown.Duration);
        Assert.False(sends.Reader.TryRead(out _));
        cooldown.Gate.SetResult();
        var second = await sends.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        Assert.False(sends.Reader.TryRead(out _));
        second.SetResult();
        var secondCooldown = await delays.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        secondCooldown.Gate.SetResult();
        // The 12 signals coalesce; they do not schedule 12 callbacks at once.
        dispatcher.Dispose();
        await dispatcher.Completion.WaitAsync(Timeout);
    }

    [Fact]
    public async Task DisposeCancelsPendingGameTickWithoutWaitingForIt()
    {
        var started = Gate();
        var neverRuns = Gate();
        using var dispatcher = new AsyncTellDispatcher(token =>
        {
            started.SetResult();
            return neverRuns.Task.WaitAsync(token);
        }, _ => Assert.Fail("Cancellation must not be reported as an error"));
        dispatcher.RequestSend();
        await started.Task.WaitAsync(Timeout);
        dispatcher.Dispose();
        dispatcher.RequestSend();
        await dispatcher.Completion.WaitAsync(Timeout);
        Assert.False(neverRuns.Task.IsCompleted);
    }

    [Fact]
    public async Task DisposeCancelsCooldownWithoutBlocking()
    {
        var coolingDown = Gate();
        var neverEnds = Gate();
        using var dispatcher = new AsyncTellDispatcher(_ => Task.CompletedTask,
            _ => Assert.Fail("Cancellation must not be reported as an error"), (_, token) =>
            {
                coolingDown.SetResult();
                return neverEnds.Task.WaitAsync(token);
            });
        dispatcher.RequestSend();
        await coolingDown.Task.WaitAsync(Timeout);
        dispatcher.Dispose();
        await dispatcher.Completion.WaitAsync(Timeout);
        Assert.False(neverEnds.Task.IsCompleted);
    }

    [Fact]
    public async Task DispatchFailureStillWaitsBeforeNextAttempt()
    {
        var failure = new InvalidOperationException("chat failed");
        var errors = Channel.CreateUnbounded<Exception>();
        var delays = Channel.CreateUnbounded<TaskCompletionSource>();
        var second = Gate();
        var attempts = 0;
        using var dispatcher = new AsyncTellDispatcher(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw failure;
            second.TrySetResult();
            return Task.CompletedTask;
        }, ex => errors.Writer.TryWrite(ex), (_, token) =>
        {
            var gate = Gate();
            delays.Writer.TryWrite(gate);
            return gate.Task.WaitAsync(token);
        });
        dispatcher.RequestSend();
        Assert.Same(failure, await errors.Reader.ReadAsync().AsTask().WaitAsync(Timeout));
        var cooldown = await delays.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        dispatcher.RequestSend();
        Assert.False(second.Task.IsCompleted);
        cooldown.SetResult();
        await second.Task.WaitAsync(Timeout);
        dispatcher.Dispose();
        await dispatcher.Completion.WaitAsync(Timeout);
    }
}
