using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class PlayerRuleConditionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid FirstTag = Guid.NewGuid();
    private static readonly Guid SecondTag = Guid.NewGuid();

    [Theory]
    [InlineData("ContainsAny", true)]
    [InlineData("ContainsAll", false)]
    [InlineData("ContainsNone", false)]
    public void Tags_UsesSelectedSetSemantics(string operation, bool expected)
    {
        Assert.Equal(expected, PlayerRuleConditions.Tags(
            [FirstTag], [FirstTag.ToString(), SecondTag.ToString()], operation));
    }

    [Fact]
    public void Tags_UnavailableOrMalformedSelection_IsUnknownRatherThanAnExemption()
    {
        Assert.Null(PlayerRuleConditions.Tags(null, [FirstTag.ToString()], "ContainsNone"));
        Assert.Null(PlayerRuleConditions.Tags([], [], "ContainsAll"));
        Assert.Null(PlayerRuleConditions.Tags([], ["not-a-tag-id"], "ContainsNone"));
    }

    [Fact]
    public void FirstSeen_DistinguishesNeverObservedFromUnavailableHistory()
    {
        Assert.True(PlayerRuleConditions.FirstSeen(new(true, null), "IsNew", null, Now));
        Assert.False(PlayerRuleConditions.FirstSeen(new(true, null), "IsKnown", null, Now));
        Assert.Null(PlayerRuleConditions.FirstSeen(new(false, null), "IsNew", null, Now));
        Assert.Null(PlayerRuleConditions.FirstSeen(new(false, null), "IsKnown", null, Now));
    }

    [Fact]
    public void FirstSeen_DateAndAgeUseActualTimestampsNotMembership()
    {
        var firstSeen = new IdentifierHistory(true, Now.AddDays(-2));

        Assert.True(PlayerRuleConditions.FirstSeen(firstSeen, "Before", Now.AddDays(-1).ToString("O"), Now));
        Assert.False(PlayerRuleConditions.FirstSeen(firstSeen, "After", Now.AddDays(-1).ToString("O"), Now));
        Assert.True(PlayerRuleConditions.FirstSeen(firstSeen, "WithinAge", "2.00:00:00", Now));
        Assert.False(PlayerRuleConditions.FirstSeen(firstSeen, "WithinAge", "1.23:59:59", Now));
        Assert.Null(PlayerRuleConditions.FirstSeen(new(true, null), "Before", Now.ToString("O"), Now));
        Assert.Null(PlayerRuleConditions.FirstSeen(new(true, null), "WithinAge", "1.00:00:00", Now));
    }

    [Theory]
    [InlineData("LessThan", false)]
    [InlineData("LessThanOrEqual", true)]
    [InlineData("GreaterThan", false)]
    [InlineData("GreaterThanOrEqual", true)]
    public void ProfileAge_UsesObservationTimeAndExactBoundary(string operation, bool expected)
    {
        Assert.Equal(expected, PlayerRuleConditions.ProfileAge(Now.AddHours(-2), operation, "02:00:00", Now));
    }

    [Fact]
    public void InvalidOrFutureHistory_DoesNotAct()
    {
        Assert.Null(PlayerRuleConditions.FirstSeen(new(true, Now.AddSeconds(1)), "IsKnown", null, Now));
        Assert.Null(PlayerRuleConditions.FirstSeen(new(true, Now.AddDays(-1)), "WithinAge", "-1.00:00:00", Now));
        Assert.Null(PlayerRuleConditions.ProfileAge(null, "LessThan", "01:00:00", Now));
        Assert.Null(PlayerRuleConditions.ProfileAge(Now.AddSeconds(1), "LessThan", "01:00:00", Now));
        Assert.Null(PlayerRuleConditions.ProfileAge(Now.AddHours(-1), "Equal", "01:00:00", Now));
    }
}
