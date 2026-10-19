using System.Threading.Channels;

namespace EspiGambaHelper.Core;

/// <summary>
/// Waits off the game thread. The callback marshals one send to a game tick;
/// neither RequestSend nor Dispose waits for that callback or the cooldown.
/// </summary>
public sealed class AsyncTellDispatcher : IDisposable
{
    private readonly Channel<byte> wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource shutdown = new();
    private readonly Func<CancellationToken, Task> dispatchOne;
    private readonly Action<Exception> reportError;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private int disposed;

    public Task Completion { get; }

    public AsyncTellDispatcher(Func<CancellationToken, Task> dispatchOne, Action<Exception> reportError,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.dispatchOne = dispatchOne;
        this.reportError = reportError;
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        Completion = Task.Run(RunAsync);
    }

    public void RequestSend()
    {
        if (Volatile.Read(ref disposed) == 0) wake.Writer.TryWrite(0);
    }

    private async Task RunAsync()
    {
        var token = shutdown.Token;
        try
        {
            while (await wake.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                if (!wake.Reader.TryRead(out _)) continue;
                try
                {
                    await dispatchOne(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { reportError(ex); }
                // Start the cooldown after the previous send finishes, even if it failed.
                await delay(TimeSpan.FromMilliseconds(800), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        wake.Writer.TryComplete();
        // Never join the worker on the game thread: it may be awaiting a game-tick callback.
        _ = Completion.ContinueWith(task =>
        {
            _ = task.Exception;
            shutdown.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
