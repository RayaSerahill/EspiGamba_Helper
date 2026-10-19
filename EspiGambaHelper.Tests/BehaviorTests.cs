using EspiGambaHelper.Core;
using Xunit;

namespace EspiGambaHelper.Tests;

public sealed class BehaviorTests
{
    private static readonly PlayerIdentity Alice = new("Alice Example", 21, "Ravana");
    private static readonly GameSettings Settings = new(10, "Preset", "Theme");

    // Actual /dice messages are Addon 3567/3568, not the named /random LogMessage format.
    [Theory]
    [InlineData("Random! (1-10) 3", 3, 10)]
    [InlineData("Random! (1-10) 10", 10, 10)]
    [InlineData("Random! (1-10) 0", 0, 10)]
    [InlineData("Random! 456", 456, 999)]
    [InlineData("Random! (1-999) 999", 999, 999)]
    public void ParsesDiceChatBodyAndUsesSeparateSender(string message, int result, int maximum)
    {
        var roll = Assert.IsType<DiceRoll>(DiceParser.Parse(message));
        Assert.True(roll.UsesChatSender);
        Assert.False(roll.IsSelf);
        Assert.Equal(result, roll.Result);
        Assert.Equal(maximum, roll.Maximum);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 8)]
    public void FriendDiceMessageResolvesSenderAndCreatesOneRequest(bool vip, int expectedCards)
    {
        var roll = DiceParser.Parse("Random! (1-10) 3")!;
        PlayerIdentity[] roster = [Alice];
        var senderName = "Alice Example";
        var player = RosterMatcher.Find(roster, roll.UsesChatSender ? senderName : roll.Name, 21)!;
        var session = new ActivationSession();
        session.Enable(Settings);
        var request = Assert.IsType<GameRequest>(session.TryEnter(player, roll.Maximum, roll.Result, vip));
        Assert.Equal(Alice, request.Player);
        Assert.Equal(expectedCards, request.CardCount);
        Assert.Null(session.TryEnter(player, roll.Maximum, roll.Result, vip));
    }

    [Theory]
    [InlineData("Random! (1-10) 11")]
    [InlineData("Random! (1-10) -1")]
    [InlineData("Random! (1-1000) 3")]
    [InlineData("Random! (1-10) 3 trailing text")]
    [InlineData("Someone said Random! (1-10) 3")]
    public void RejectsInvalidDiceChatBody(string message) => Assert.Null(DiceParser.Parse(message));

    [Theory]
    [InlineData("Random! Alice Example rolls a 0 (out of 10).", 0, 10, false)]
    [InlineData("Random! You roll a 10 (out of 10).", 10, 10, true)]
    [InlineData("Random! Alice Example rolls a 999.", 999, 999, false)]
    [InlineData("Random! Alice ExampleRavana rolls a 3 (out of 10).", 3, 10, false)]
    public void ReadsActualGameFormats(string message, int result, int maximum, bool self)
    {
        var roll = Assert.IsType<DiceRoll>(DiceParser.Parse(message));
        Assert.Equal(result, roll.Result);
        Assert.Equal(maximum, roll.Maximum);
        Assert.Equal(self, roll.IsSelf);
    }

    [Theory]
    [InlineData("I said Random! Alice Example rolls a 10 (out of 10).")]
    [InlineData("Random! Alice Example rolls a 11 (out of 10).")]
    [InlineData("Random! Alice Example rolls a 1 (out of 1000).")]
    [InlineData("Random! Alice Example rolls a -1 (out of 10).")]
    [InlineData("Random! Alice Example rolls a 1 (out of 999999999999999).")]
    public void RejectsUnrelatedOrInvalidText(string message) => Assert.Null(DiceParser.Parse(message));

    [Fact]
    public void RangeMatchesNotResultAndEntryIsOncePerActivation()
    {
        var session = new ActivationSession();
        Assert.Null(session.TryEnter(Alice, 10));
        session.Enable(Settings);
        Assert.Null(session.TryEnter(Alice, 100));
        var request = Assert.IsType<GameRequest>(session.TryEnter(Alice, 10));
        Assert.Equal(Settings, request.Settings);
        Assert.Null(session.TryEnter(Alice, 10)); // Pending IPC is already reserved.
        session.Complete(request, true, "OK");
        Assert.Null(session.TryEnter(Alice, 10));
        session.Enable(Settings); // Repeated enable alone is not a reset.
        Assert.Null(session.TryEnter(Alice, 10));
        session.Disable();
        Assert.Null(session.TryEnter(Alice, 10));
        session.Enable(Settings);
        Assert.NotNull(session.TryEnter(Alice, 10));
    }

    [Fact]
    public void OldCompletionsCannotChangeNewActivation()
    {
        var session = new ActivationSession();
        session.Enable(Settings);
        var old = session.TryEnter(Alice, 10)!;
        session.Disable();
        session.Enable(Settings with { ThemeName = "Other" });
        var current = session.TryEnter(Alice, 10)!;
        session.Complete(old, false, "old failure");
        Assert.Equal(0, session.FailedCount);
        Assert.Null(session.LastStatus);
        Assert.Equal("Other", current.Settings.ThemeName);
        session.Complete(current, true, "created");
        Assert.Equal(1, session.CreatedCount);
    }

    [Fact]
    public void FailedIpcDoesNotAutomaticallyRetryAnUncertainCreation()
    {
        var session = new ActivationSession();
        session.Enable(Settings);
        var request = session.TryEnter(Alice, 10)!;
        session.Complete(request, false, "uncertain");
        Assert.Null(session.TryEnter(Alice, 10));
        Assert.Equal(1, session.FailedCount);
    }

    [Fact]
    public void WorldIdentityPreventsCollisionsAndRejectsOutsiders()
    {
        PlayerIdentity[] roster = [Alice, Alice with { WorldId = 22, WorldName = "Bismarck" }];
        Assert.Null(RosterMatcher.Find(roster, "Outsider Example"));
        Assert.Null(RosterMatcher.Find(roster, Alice.Name));
        Assert.Equal(Alice, RosterMatcher.Find(roster, Alice.Name, 21));
        Assert.Equal(Alice, RosterMatcher.Find(roster, "Alice ExampleRavana"));
        Assert.Null(RosterMatcher.Find(roster, "Alice ExampleOther"));
        var session = new ActivationSession();
        session.Enable(Settings);
        Assert.NotNull(session.TryEnter(roster[0], 10));
        Assert.NotNull(session.TryEnter(roster[1], 10));
    }

    [Fact]
    public void AllTwentyFourMembersAreEligibleIndependently()
    {
        var session = new ActivationSession();
        session.Enable(Settings);
        var roster = Enumerable.Range(0, 24).Select(i => new PlayerIdentity($"Player Member{i}", 21, "Ravana")).ToArray();
        foreach (var member in roster)
        {
            Assert.Equal(member, RosterMatcher.Find(roster, member.Name));
            Assert.NotNull(session.TryEnter(member, 10));
            Assert.Null(session.TryEnter(member, 10));
        }
        Assert.Equal(24, session.AttemptedCount);
    }

    [Theory]
    [InlineData("[\"B\",\"A\",\"B\"]")]
    [InlineData("[{\"Name\":\"B\"},{\"name\":\"A\"}]")]
    [InlineData("{\"themes\":[\"B\",\"A\"]}")]
    public void ReadsChoiceNames(string json) => Assert.Equal(["A", "B"], ChoiceList.Parse(json, "themes"));

    [Theory]
    [InlineData("null")]
    [InlineData("{\"error\":\"offline\"}")]
    [InlineData("[{\"id\":42}]")]
    public void DoesNotTreatMalformedChoicesAsUsable(string json) => Assert.Throws<FormatException>(() => ChoiceList.Parse(json, "themes"));
}
