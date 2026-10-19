using EspiGambaHelper.Core;
using Xunit;

namespace EspiGambaHelper.Tests;

public sealed class GameCreationQueueTests
{
    private static readonly GameSettings Settings = new(10, "Preset", "Theme");

    private static GameRequest Request(int index, long activation = 1) =>
        new(activation, new PlayerIdentity($"Player Member{index}", 21, "Ravana"), Settings, index % 10 + 5);

    [Fact]
    public void MixedVipAndNonVipRollsKeepTheirCorrectCountsThroughTheQueue()
    {
        var session = new ActivationSession();
        session.Enable(Settings);
        using var queue = new GameCreationQueue();
        (string Name, int Roll, bool Vip, int Cards)[] rolls =
        [
            ("Nixell Lenia", 4, false, 4),
            ("Lini Espi", 7, true, 12),
            ("Yaya Lenia", 2, false, 2),
            ("Chaery Blossom", 1, false, 1),
            ("Yasuppoi Kanto", 10, false, 10),
            ("Lexie Chatinou", 2, false, 2),
        ];
        foreach (var roll in rolls)
        {
            var player = new PlayerIdentity(roll.Name, 21, "Ravana");
            queue.Enqueue(session.TryEnter(player, 10, roll.Roll, roll.Vip)!, null);
        }

        var created = new List<(string Name, int Cards)>();
        foreach (var roll in rolls)
        {
            queue.StartNext(request =>
            {
                created.Add((request.Player.Name, request.CardCount));
                return Task.FromResult("https://scratch.example/game");
            });
            Assert.NotNull(queue.TakeCompleted());
        }
        Assert.Equal(rolls.Select(roll => (roll.Name, roll.Cards)), created);
    }

    [Fact]
    public async Task AllianceBurstCreatesOneAtATimeAndPreservesCountsRecipientsAndTickets()
    {
        using var queue = new GameCreationQueue();
        var requests = Enumerable.Range(0, 24).Select(i => Request(i)).ToArray();
        var calls = new List<GameRequest>();
        TaskCompletionSource<string>? response = null;
        Task<string> Create(GameRequest request)
        {
            Assert.True(response == null || response.Task.IsCompleted, "Creation requests overlapped.");
            calls.Add(request);
            response = new TaskCompletionSource<string>();
            return response.Task;
        }

        for (var i = 0; i < requests.Length; i++)
            queue.Enqueue(requests[i], i);

        for (var i = 0; i < requests.Length; i++)
        {
            queue.StartNext(Create);
            Assert.Equal(i + 1, calls.Count);
            Assert.Equal(requests[i], calls[i]);
            for (var tick = 0; tick < 10; tick++)
            {
                queue.StartNext(Create);
                Assert.Null(queue.TakeCompleted());
                Assert.Equal(i + 1, calls.Count);
            }

            var url = $"https://scratch.example/game/{i}";
            response!.SetResult(url);
            queue.StartNext(Create);
            Assert.Equal(i + 1, calls.Count); // The previous completion must be consumed first.
            var completed = Assert.IsType<PendingGame>(queue.TakeCompleted());
            Assert.Equal(requests[i], completed.Request);
            Assert.Equal((long)i, completed.TellTicket);
            Assert.Equal(url, await completed.Task);
            Assert.Null(queue.TakeCompleted());
        }
        queue.StartNext(Create);
        Assert.Equal(requests, calls);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("fault")]
    [InlineData("cancel")]
    [InlineData("no task")]
    public async Task FailedCreationIsReturnedOnceAndDoesNotBlockOrRetryTheNextPlayer(string failure)
    {
        using var queue = new GameCreationQueue();
        var first = Request(0);
        var second = Request(1);
        queue.Enqueue(first, null);
        queue.Enqueue(second, 42);
        var calls = new List<GameRequest>();
        Task<string> Create(GameRequest request)
        {
            calls.Add(request);
            if (request == second) return Task.FromResult("https://scratch.example/second");
            return failure switch
            {
                "throw" => throw new InvalidOperationException("IPC unavailable"),
                "fault" => Task.FromException<string>(new InvalidOperationException("Creation failed")),
                "cancel" => Task.FromCanceled<string>(new CancellationToken(true)),
                _ => null!,
            };
        }

        queue.StartNext(Create);
        var failed = Assert.IsType<PendingGame>(queue.TakeCompleted());
        Assert.Equal(first, failed.Request);
        Assert.Null(failed.TellTicket);
        await Assert.ThrowsAnyAsync<Exception>(async () => await failed.Task);
        Assert.Null(queue.TakeCompleted());
        queue.StartNext(Create);
        var completed = Assert.IsType<PendingGame>(queue.TakeCompleted());
        Assert.Equal(second, completed.Request);
        Assert.Equal(42L, completed.TellTicket);
        Assert.Equal("https://scratch.example/second", await completed.Task);
        queue.StartNext(Create);
        Assert.Equal(new[] { first, second }, calls);
    }

    [Fact]
    public void NewRoundWaitsForOldActiveCallAndDoesNotCreateCancelledWaitingGames()
    {
        using var queue = new GameCreationQueue();
        var old = Request(0);
        var current = Request(0, activation: 2) with { CardCount = 15 };
        var response = new TaskCompletionSource<string>();
        var calls = new List<GameRequest>();
        Task<string> Create(GameRequest request)
        {
            calls.Add(request);
            return request == old ? response.Task : Task.FromResult("https://scratch.example/new");
        }

        queue.Enqueue(old, 1);
        queue.Enqueue(Request(1), 1);
        queue.StartNext(Create);
        queue.CancelWaiting(); // Disable or character change.
        queue.Enqueue(current, 2);
        queue.StartNext(Create);
        Assert.Equal(new[] { old }, calls);
        Assert.Null(queue.TakeCompleted());

        response.SetResult("https://scratch.example/old");
        var completed = Assert.IsType<PendingGame>(queue.TakeCompleted());
        Assert.Equal(old, completed.Request);
        Assert.Equal(1L, completed.TellTicket);
        queue.StartNext(Create);
        completed = Assert.IsType<PendingGame>(queue.TakeCompleted());
        Assert.Equal(current, completed.Request);
        Assert.Equal(2L, completed.TellTicket);
        Assert.Equal(new[] { old, current }, calls);
    }

    [Fact]
    public void UnloadDoesNotWaitForActiveCreationOrStartQueuedWork()
    {
        var queue = new GameCreationQueue();
        var response = new TaskCompletionSource<string>();
        queue.Enqueue(Request(0), 1);
        queue.Enqueue(Request(1), 1);
        queue.StartNext(_ => response.Task);
        queue.Dispose();
        Assert.False(response.Task.IsCompleted);
        response.SetResult("https://scratch.example/old");
        queue.StartNext(_ => throw new Exception("Must not start after unload"));
        Assert.Null(queue.TakeCompleted());
        Assert.Throws<ObjectDisposedException>(() => queue.Enqueue(Request(2), 1));
        queue.Dispose();
    }
}
