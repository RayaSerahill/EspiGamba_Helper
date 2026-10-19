using EspiGambaHelper.Core;
using Xunit;

namespace EspiGambaHelper.Tests;

public sealed class VipTests
{
    private static readonly PlayerIdentity Alice = new("Alice Example", 21, "Ravana");
    private static readonly GameSettings Settings = new(10, "Preset", "Theme");

    [Theory]
    [InlineData(0, 5, 5)]
    [InlineData(3, 5, 8)]
    [InlineData(10, 5, 15)]
    [InlineData(7, 2, 9)]
    [InlineData(3, 0, 3)]
    [InlineData(0, 0, 0)]
    public void VipReceivesRollResultPlusConfiguredBonus(int result, int bonus, int expected)
    {
        var session = new ActivationSession();
        session.Enable(Settings with { VipBonus = bonus });
        var roll = DiceParser.Parse($"Random! Alice Example rolls a {result} (out of 10).")!;
        var request = session.TryEnter(Alice, roll.Maximum, roll.Result, isVip: true)!;
        Assert.Equal(expected, request.CardCount);
        Assert.Equal("Preset", request.Settings.PresetName);
        Assert.Equal("Theme", request.Settings.ThemeName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(10)]
    public void NonVipReceivesTheirRollWithoutTheVipBonus(int rollResult)
    {
        var session = new ActivationSession();
        session.Enable(Settings);
        Assert.Equal(rollResult, session.TryEnter(Alice, 10, rollResult, isVip: false)!.CardCount);
    }

    [Fact]
    public void VipStillRequiresMatchingMaximumAndCanOnlyEnterOncePerRound()
    {
        var session = new ActivationSession();
        session.Enable(Settings);
        Assert.Null(session.TryEnter(Alice, 100, 10, isVip: true));
        var first = session.TryEnter(Alice, 10, 3, isVip: true)!;
        Assert.Null(session.TryEnter(Alice, 10, 10, isVip: true));
        session.Complete(first, true, "Created");
        Assert.Null(session.TryEnter(Alice, 10, 10, isVip: true));
        session.Disable();
        Assert.Null(session.TryEnter(Alice, 10, 10, isVip: true));
        session.Enable(Settings with { VipBonus = 2 });
        Assert.Equal(12, session.TryEnter(Alice, 10, 10, isVip: true)!.CardCount);
        Assert.Equal(8, first.CardCount); // In-flight requests keep their original card count.
    }

    [Fact]
    public void VipAddTrimsFieldsAndRejectsCaseInsensitiveDuplicates()
    {
        var vips = new List<VipPlayer>();
        Assert.True(VipList.TryAdd(vips, " Alice Example ", " Ravana ", out var error));
        Assert.Null(error);
        Assert.Equal(new VipPlayer("Alice Example", "Ravana"), Assert.Single(vips));
        Assert.False(VipList.TryAdd(vips, "alice example", "RAVANA", out error));
        Assert.NotNull(error);
        Assert.Single(vips);
        Assert.True(VipList.TryAdd(vips, "Alice Example", "Bismarck", out _));
        Assert.Equal(2, vips.Count);
    }

    [Theory]
    [InlineData("", "Ravana")]
    [InlineData("Alice Example", "  ")]
    public void VipNeedsBothNameAndWorld(string name, string world)
    {
        var vips = new List<VipPlayer>();
        Assert.False(VipList.TryAdd(vips, name, world, out var error));
        Assert.NotNull(error);
        Assert.Empty(vips);
    }

    [Fact]
    public void VipMatchingRequiresBothFieldsAndDeletionRemovesBonus()
    {
        var vips = new List<VipPlayer> { new("alice example", "RAVANA") };
        Assert.True(VipList.Contains(vips, Alice));
        Assert.False(VipList.Contains(vips, Alice with { WorldId = 22, WorldName = "Bismarck" }));
        Assert.False(VipList.Contains(vips, Alice with { Name = "Another Player" }));
        var session = new ActivationSession();
        session.Enable(Settings);
        vips.RemoveAt(0);
        Assert.False(VipList.Contains(vips, Alice));
        Assert.Equal(9, session.TryEnter(Alice, 10, 9, VipList.Contains(vips, Alice))!.CardCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void InvalidBonusCannotCreateNegativeOrOverflowedCardCounts(int bonus)
    {
        var session = new ActivationSession();
        Assert.Throws<ArgumentException>(() => session.Enable(Settings with { VipBonus = bonus }));
        Assert.False(session.Enabled);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void InvalidRollDoesNotConsumeVipEntry(int result)
    {
        var session = new ActivationSession();
        session.Enable(Settings);
        Assert.Null(session.TryEnter(Alice, 10, result, isVip: true));
        Assert.NotNull(session.TryEnter(Alice, 10, 3, isVip: true));
    }
}
