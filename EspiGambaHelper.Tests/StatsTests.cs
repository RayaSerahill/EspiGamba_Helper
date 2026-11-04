using EspiGambaHelper.Core;
using Xunit;

namespace EspiGambaHelper.Tests;

public sealed class GilLabelTests
{
    [Theory]
    [InlineData("1M gil", 1_000_000)]
    [InlineData("500K gil", 500_000)]
    [InlineData("2.5m Gil", 2_500_000)]
    [InlineData("250 gil", 250)]
    [InlineData("1,000,000 gil", 1_000_000)]
    [InlineData("  10k   GIL ", 10_000)]
    public void ParsesGilLabels(string label, long expected)
    {
        Assert.True(GilLabel.TryParse(label, out var gil));
        Assert.Equal(expected, gil);
    }

    [Theory]
    [InlineData("Rare Mount")]
    [InlineData("1M")]
    [InlineData("gil")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("-5 gil")]
    [InlineData("1M gil bonus")]
    public void RejectsItemPrizesAndMalformedLabels(string? label) => Assert.False(GilLabel.TryParse(label, out _));

    [Fact]
    public void SumsOnlyGilPrizes() => Assert.Equal(1_250_000, GilLabel.Sum(["1M gil", "Rare Mount", "250K gil", null]));
}

public sealed class PayloadTests
{
    [Fact]
    public void BlackjackRoundUsesMillisecondTimeAsKey()
    {
        var json = """{"Time":1758196800123,"BetsCollected":300000,"Payouts":450000,"Players":["Alice Example","Bob Example"],"Saved":false,"ArchiveID":"00000000-0000-0000-0000-000000000000","Hands":[{"a":"Alice Example","b":[1,10]}]}""";
        var round = Assert.Single(BlackjackRounds.Parse(json, live: true, "Raya Serahill"));
        Assert.Equal(GameSource.Blackjack, round.Source);
        Assert.Equal("Raya Serahill", round.Host);
        Assert.Equal("1758196800123", round.Key);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1758196800123), round.PlayedAt);
        Assert.Equal("Alice Example, Bob Example", round.Player);
        Assert.Equal(-150000, round.Profit);
        Assert.True(round.Live);
        Assert.Null(round.MatchKey);
    }

    [Fact]
    public void BlackjackArchiveListParsesEveryRoundAndSkipsJunk()
    {
        var json = """[{"Time":1758196800000,"BetsCollected":100,"Payouts":0},{"Time":0,"BetsCollected":5},{"Time":1758196900000,"BetsCollected":50,"Payouts":80},"junk"]""";
        var rounds = BlackjackRounds.Parse(json, live: false);
        Assert.Equal(2, rounds.Count);
        Assert.All(rounds, r => Assert.False(r.Live));
        Assert.All(rounds, r => Assert.Equal("", r.Host));
        Assert.Equal(70, rounds.Sum(r => r.Profit));
        Assert.Empty(BlackjackRounds.Parse("", live: false));
    }

    [Fact]
    public void ScratchLiveGameRecordsPrizesWithoutCardSales()
    {
        var at = new DateTimeOffset(2026, 11, 4, 20, 0, 0, TimeSpan.Zero);
        var json = """{"player_id":4711,"player_name":"Alice Example","wins":2,"total_cards":5,"prizes_won":["1M gil","Rare Mount"]}""";
        var game = Assert.IsType<GameRecord>(ScratchGames.ParseLive(json, at, "Raya Serahill"));
        Assert.Equal("live:4711", game.Key);
        Assert.Equal("Raya Serahill", game.Host);
        Assert.Equal(at, game.PlayedAt);
        Assert.Equal(0, game.Collected);
        Assert.Equal(1_000_000, game.PaidOut);
        Assert.True(game.Live);
        Assert.Equal(ScratchGames.MatchKey("alice example", 5, 2, 2), game.MatchKey);
        Assert.Null(ScratchGames.ParseLive("""{"player_name":"nobody"}""", at));
    }

    [Fact]
    public void ScratchArchiveComputesCardSalesAndParsesTimestamp()
    {
        var json = """[{"archive_id":99,"player_name":"Alice Example","archived_at":"2026-11-04T18:30:00","dealer":"Dealer","is_mine":true,"preset_name":"P","theme_name":"T","stats":{"total_cards":5,"completed_cards":5,"wins":2,"total_prizes":2,"prizes_won":["1M gil","Rare Mount"],"price_per_card":100000,"purchased_cards":5}},{"archive_id":100,"player_name":"No Time","archived_at":""}]""";
        var game = Assert.Single(ScratchGames.ParseArchive(json));
        Assert.Equal("archive:99", game.Key);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 18, 30, 0, TimeSpan.Zero), game.PlayedAt);
        Assert.Equal(500000, game.Collected);
        Assert.Equal(1_000_000, game.PaidOut);
        Assert.False(game.Live);
        Assert.Equal(ScratchGames.MatchKey("Alice Example", 5, 2, 2), game.MatchKey);
        Assert.Equal("Dealer", game.Host);
    }

    [Fact]
    public void ScratchArchiveHostIsTheDealerUnlessFlaggedMine()
    {
        var json = """[{"archive_id":1,"player_name":"A","archived_at":"2026-11-04T18:30:00","dealer":"Other Dealer@Odin","is_mine":false},{"archive_id":2,"player_name":"B","archived_at":"2026-11-04T18:31:00","dealer":"Shared Account","is_mine":true}]""";
        var games = ScratchGames.ParseArchive(json, "Raya Serahill");
        Assert.Equal(["Other Dealer@Odin", "Raya Serahill"], games.Select(g => g.Host));
        Assert.Equal(["Other Dealer@Odin", "Shared Account"], ScratchGames.ParseArchive(json).Select(g => g.Host));
        Assert.Equal("Other Dealer@Odin", ScratchGames.HostFromDetails(games[0].Details));
    }

    [Fact]
    public void WheelLiveGameChargesPaidSpinsAndReadsPrizeArrays()
    {
        var at = DateTimeOffset.UtcNow;
        var json = """{"game_uuid":"ab12cd34","player_name":"Player Title","player_homeworld":"Balmung","host_name":"Asuna Pahlo","theme":"urban","preset":"MyPreset","max_spins":5,"spins_paid":3,"spins_used":5,"spin_cost":100000,"prizes_won":["1M gil","Rare Mount"]}""";
        var game = Assert.IsType<GameRecord>(WheelGames.ParseLive(json, at));
        Assert.Equal("live:ab12cd34", game.Key);
        Assert.Equal(300000, game.Collected);
        Assert.Equal(1_000_000, game.PaidOut);
        Assert.Equal(WheelGames.MatchKey("Player Title", "Asuna Pahlo", 5, 100000, 2), game.MatchKey);
        Assert.Equal("Asuna Pahlo", game.Host);
        // Live games belong to whoever is logged in; the payload's host only fills in when nobody is.
        Assert.Equal("Raya Serahill", WheelGames.ParseLive(json, at, "Raya Serahill")!.Host);
        Assert.Equal("Asuna Pahlo", WheelGames.ParseLive(json, at, " ")!.Host);
    }

    [Fact]
    public void WheelArchiveFallsBackToAllSpinsAndJsonStringPrizes()
    {
        var json = """[{"id":17,"host_name":"Asuna Pahlo","player_nickname":"Player Title","theme":"urban","spins":5,"spins_paid":0,"spin_cost":100000,"preset":"MyPreset","created_at":"2026-11-04T18:30:00Z","prizes":"[\"500K gil\",\"Rare Mount\"]"}]""";
        var game = Assert.Single(WheelGames.ParseArchive(json));
        Assert.Equal("archive:17", game.Key);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 18, 30, 0, TimeSpan.Zero), game.PlayedAt);
        Assert.Equal(500000, game.Collected);
        Assert.Equal(500000, game.PaidOut);
        Assert.Equal(WheelGames.MatchKey("Player Title", "Asuna Pahlo", 5, 100000, 2), game.MatchKey);
        Assert.Equal("Asuna Pahlo", game.Host);
        Assert.Equal("Asuna Pahlo", WheelGames.HostFromDetails(game.Details));
    }

    [Theory]
    [InlineData("Raya Serahill", "Raya Serahill", true)]
    [InlineData("raya serahill@Odin", "Raya Serahill", true)]
    [InlineData("Raya Serahill", "Raya Serahill@Odin", true)]
    [InlineData("Raya Serahill@Odin", "Raya Serahill@Alpha", false)]
    [InlineData("Other Dealer", "Raya Serahill", false)]
    [InlineData("Raya Serahill Jr", "Raya Serahill", false)]
    [InlineData("", "Raya Serahill", false)]
    [InlineData("Raya Serahill", null, false)]
    public void HostMatchingIgnoresCaseAndOptionalWorld(string host, string? character, bool expected) =>
        Assert.Equal(expected, GameHosts.Matches(host, character));

    [Fact]
    public void TimestampsAcceptSecondsMillisecondsAndIsoText()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1758196800), UnixTime.FromSecondsOrMilliseconds(1758196800));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1758196800123), UnixTime.FromSecondsOrMilliseconds(1758196800123));
        Assert.True(UnixTime.TryParse("2026-11-04 18:30:00", out var parsed));
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 18, 30, 0, TimeSpan.Zero), parsed);
        Assert.True(UnixTime.TryParse("2026-11-04T18:30:00+02:00", out var offset));
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 16, 30, 0, TimeSpan.Zero), offset);
        Assert.False(UnixTime.TryParse("yesterday", out _));
    }
}

public sealed class StatsStoreTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static GameRecord Game(GameSource source, string key, DateTimeOffset at, long collected, long paid, bool live = false, string? match = null, string host = "") =>
        new(source, key, at, "Alice Example", collected, paid, "{}", live, match, host);

    [Fact]
    public void SameKeyIsRecordedOnce()
    {
        using var store = StatsStore.InMemory();
        Assert.Equal(AddOutcome.Added, store.Add(Game(GameSource.Blackjack, "1", Noon, 100, 20, live: true)));
        Assert.Equal(AddOutcome.Duplicate, store.Add(Game(GameSource.Blackjack, "1", Noon, 100, 20)));
        Assert.Equal(AddOutcome.Added, store.Add(Game(GameSource.Wheel, "1", Noon, 100, 20)));
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void ArchiveRowUpgradesMatchingLiveRowInsteadOfDoubleCounting()
    {
        using var store = StatsStore.InMemory();
        store.Add(Game(GameSource.Scratch, "live:4711", Noon, 0, 1_000_000, live: true, match: "m"));
        Assert.Equal(AddOutcome.Linked, store.Add(Game(GameSource.Scratch, "archive:99", Noon.AddMinutes(5), 500000, 1_000_000, match: "m")));
        Assert.Equal(AddOutcome.Duplicate, store.Add(Game(GameSource.Scratch, "archive:99", Noon.AddMinutes(5), 500000, 1_000_000, match: "m")));
        var scratch = store.Summarize(Noon.AddHours(-1), Noon.AddHours(1)).For(GameSource.Scratch);
        Assert.Equal(1, scratch.Games);
        Assert.Equal(500000, scratch.Collected);
        Assert.Equal(-500000, scratch.Profit);
        var row = Assert.Single(store.Recent(Noon.AddHours(-1), Noon.AddHours(1), 10));
        Assert.Equal("archive:99", row.Key);
        Assert.False(row.Live);
        Assert.Equal(Noon, row.PlayedAt);
    }

    [Fact]
    public void LinkingTakesTheArchiveHostWhenItHasOne()
    {
        using var store = StatsStore.InMemory();
        store.Add(Game(GameSource.Wheel, "live:a", Noon, 0, 0, live: true, match: "m", host: "Raya Serahill"));
        store.Add(Game(GameSource.Wheel, "live:b", Noon, 0, 0, live: true, match: "n", host: "Raya Serahill"));
        Assert.Equal(AddOutcome.Linked, store.Add(Game(GameSource.Wheel, "archive:1", Noon, 0, 0, match: "m", host: "Other Dealer")));
        Assert.Equal(AddOutcome.Linked, store.Add(Game(GameSource.Wheel, "archive:2", Noon, 0, 0, match: "n")));
        var hosts = store.Recent(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 10).ToDictionary(r => r.Key, r => r.Host);
        Assert.Equal("Other Dealer", hosts["archive:1"]);
        Assert.Equal("Raya Serahill", hosts["archive:2"]);
    }

    [Fact]
    public void HostFilterKeepsBlackjackAndOwnScratchAndWheelGames()
    {
        using var store = StatsStore.InMemory();
        store.Add(Game(GameSource.Blackjack, "1", Noon, 100, 0, host: "Someone Else"));
        store.Add(Game(GameSource.Wheel, "2", Noon, 200, 0, host: "Raya Serahill"));
        store.Add(Game(GameSource.Wheel, "3", Noon, 400, 0, host: "Other Dealer"));
        store.Add(Game(GameSource.Scratch, "4", Noon, 800, 0, host: "raya serahill@Odin"));
        store.Add(Game(GameSource.Scratch, "5", Noon, 1600, 0, host: ""));
        var mine = store.Summarize(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, "Raya Serahill");
        Assert.Equal(3, mine.Games);
        Assert.Equal(1100, mine.Collected);
        Assert.Equal(["4", "2", "1"], store.Recent(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 10, "Raya Serahill").Select(r => r.Key));
        Assert.Equal(1, store.Summarize(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, "").Games);
        Assert.Equal(1, store.Summarize(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, "Nobody Here").Games);
        Assert.Equal(5, store.Summarize(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Games);
        Assert.Equal("raya serahill@Odin", store.Recent(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 10, "raya serahill").First().Host);
    }

    [Fact]
    public void OlderDatabaseGetsHostColumnFilledFromPayloads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"espigamba-{Guid.NewGuid():N}", "stats.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE games (id INTEGER PRIMARY KEY AUTOINCREMENT, source INTEGER NOT NULL, key TEXT NOT NULL,
                        played_at INTEGER NOT NULL, player TEXT NOT NULL, collected INTEGER NOT NULL, paid_out INTEGER NOT NULL,
                        live INTEGER NOT NULL, match_key TEXT, details TEXT NOT NULL, recorded_at INTEGER NOT NULL, UNIQUE (source, key));
                    INSERT INTO games (source, key, played_at, player, collected, paid_out, live, details, recorded_at) VALUES
                        (2, 'archive:1', 1, 'P', 0, 0, 0, '{"id":1,"host_name":"Asuna Pahlo"}', 1),
                        (1, 'archive:2', 1, 'P', 0, 0, 0, '{"archive_id":2,"dealer":"Raya Serahill@Odin"}', 1),
                        (1, 'live:3', 1, 'P', 0, 0, 1, '{"player_id":3}', 1),
                        (0, '4', 1, 'P', 0, 0, 0, 'not json', 1);
                    """;
                command.ExecuteNonQuery();
            }
            using var store = StatsStore.Open(path);
            var hosts = store.Recent(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 10).ToDictionary(r => r.Key, r => r.Host);
            Assert.Equal("Asuna Pahlo", hosts["archive:1"]);
            Assert.Equal("Raya Serahill@Odin", hosts["archive:2"]);
            Assert.Equal("", hosts["live:3"]);
            Assert.Equal("", hosts["4"]);
            Assert.Equal(AddOutcome.Added, store.Add(Game(GameSource.Wheel, "5", Noon, 1, 0, host: "Raya Serahill")));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void LinkingNeedsSameSourceMatchKeyAndNearbyTime()
    {
        using var store = StatsStore.InMemory();
        store.Add(Game(GameSource.Scratch, "live:1", Noon, 0, 0, live: true, match: "m"));
        Assert.Equal(AddOutcome.Added, store.Add(Game(GameSource.Wheel, "archive:1", Noon, 0, 0, match: "m")));
        Assert.Equal(AddOutcome.Added, store.Add(Game(GameSource.Scratch, "archive:2", Noon, 0, 0, match: "other")));
        Assert.Equal(AddOutcome.Added, store.Add(Game(GameSource.Scratch, "archive:3", Noon + StatsStore.LinkWindow + TimeSpan.FromDays(1), 0, 0, match: "m")));
        Assert.Equal(AddOutcome.Linked, store.Add(Game(GameSource.Scratch, "archive:4", Noon.AddDays(1), 0, 0, match: "m")));
        Assert.Equal(4, store.Count());
    }

    [Fact]
    public void AddRangeCountsOutcomesInOneTransaction()
    {
        using var store = StatsStore.InMemory();
        store.Add(Game(GameSource.Wheel, "live:a", Noon, 100, 0, live: true, match: "w"));
        var result = store.AddRange(
        [
            Game(GameSource.Wheel, "archive:1", Noon, 100, 0, match: "w"),
            Game(GameSource.Wheel, "archive:2", Noon, 50, 0),
            Game(GameSource.Wheel, "archive:2", Noon, 50, 0),
        ]);
        Assert.Equal(new ImportResult(1, 1, 1), result);
        Assert.Equal("1 new, 1 updated, 1 already recorded", result.ToString());
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void SummaryHonorsRangeBoundsAndGroupsBySource()
    {
        using var store = StatsStore.InMemory();
        store.Add(Game(GameSource.Blackjack, "1", Noon.AddHours(-13), 1000, 0));
        store.Add(Game(GameSource.Blackjack, "2", Noon.AddHours(-1), 300, 100));
        store.Add(Game(GameSource.Scratch, "3", Noon.AddMinutes(-30), 0, 250));
        store.Add(Game(GameSource.Wheel, "4", Noon, 500, 50));
        // The lower bound is inclusive, the upper bound exclusive.
        var summary = store.Summarize(Noon.AddHours(-12), Noon);
        Assert.Equal(2, summary.Games);
        Assert.Equal(300, summary.Collected);
        Assert.Equal(350, summary.PaidOut);
        Assert.Equal(-50, summary.Profit);
        Assert.Equal(0, summary.For(GameSource.Wheel).Games);
        var all = store.Summarize(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Equal(4, all.Games);
        Assert.Equal(1400, all.Profit);
        Assert.Equal(["4", "3"], store.Recent(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 2).Select(r => r.Key));
    }

    [Fact]
    public void ReopeningTheFileKeepsRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"espigamba-{Guid.NewGuid():N}", "stats.db");
        try
        {
            using (var store = StatsStore.Open(path)) store.Add(Game(GameSource.Wheel, "1", Noon, 5, 0));
            using (var store = StatsStore.Open(path)) Assert.Equal(1, store.Count());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void DisposedStoreRejectsWrites()
    {
        var store = StatsStore.InMemory();
        store.Dispose();
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.Add(Game(GameSource.Wheel, "1", Noon, 0, 0)));
    }
}

public sealed class StatsRangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DefaultsToRollingTwelveHoursWithOpenEnd()
    {
        var range = new StatsRange();
        Assert.Equal(RangePreset.Last12Hours, range.Preset);
        Assert.Equal((Now.AddHours(-12), DateTimeOffset.MaxValue), range.Resolve(Now));
    }

    [Fact]
    public void EditingABoundFreezesTheShownWindow()
    {
        var range = new StatsRange();
        range.SetTo(Now.AddHours(-2), Now);
        Assert.Equal(RangePreset.Custom, range.Preset);
        Assert.Equal((Now.AddHours(-12), Now.AddHours(-2)), range.Resolve(Now.AddHours(5)));
        range.SetFrom(Now, Now);
        Assert.Equal((Now, Now), range.Resolve(Now));
    }

    [Fact]
    public void AllTimeAndPresetsSwitchBackAndForth()
    {
        var range = new StatsRange();
        range.Use(RangePreset.AllTime, Now);
        Assert.Equal((DateTimeOffset.MinValue, DateTimeOffset.MaxValue), range.Resolve(Now));
        range.Use(RangePreset.Custom, Now);
        Assert.Equal((Now.AddHours(-12), Now), range.Resolve(Now));
        range.Use(RangePreset.Last7Days, Now);
        Assert.Equal(Now.AddDays(-7), range.Resolve(Now).From);
    }
}
