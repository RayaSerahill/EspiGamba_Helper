namespace EspiGambaHelper.Core;

public sealed record PendingGame(GameRequest Request, long? TellTicket, Task<string> Task);

/// <summary>Game-thread-only FIFO. SimpleScratch's response handler supports one active creation.</summary>
public sealed class GameCreationQueue : IDisposable
{
    private readonly Queue<(GameRequest Request, long? TellTicket)> waiting = new();
    private PendingGame? active;
    private bool disposed;

    public void Enqueue(GameRequest request, long? tellTicket)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        waiting.Enqueue((request, tellTicket));
    }

    public void StartNext(Func<GameRequest, Task<string>> create)
    {
        if (disposed || active != null || !waiting.TryDequeue(out var next)) return;
        Task<string> task;
        try { task = create(next.Request) ?? throw new InvalidOperationException("SimpleScratch returned no task."); }
        catch (Exception ex) { task = Task.FromException<string>(ex); }
        active = new PendingGame(next.Request, next.TellTicket, task);
    }

    public PendingGame? TakeCompleted()
    {
        if (active == null || !active.Task.IsCompleted) return null;
        var completed = active;
        active = null;
        return completed;
    }

    // Disabling or changing character drops unsent work, but cannot cancel an accepted IPC call.
    // Keep the active call until it finishes, including across a disable/re-enable.
    public void CancelWaiting() => waiting.Clear();

    public void Dispose()
    {
        disposed = true;
        waiting.Clear();
        if (active != null)
            _ = active.Task.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        active = null;
    }
}
