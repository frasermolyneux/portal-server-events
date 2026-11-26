using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public sealed class BotMessageHeaderTests
{
    [Theory]
    [InlineData(false, true, "^2[Bot]^7 ")]
    [InlineData(true, true, "^5(PM)^7 ^2[Bot]^7 ")]
    [InlineData(false, false, "[Bot] ")]
    [InlineData(true, false, "(PM) [Bot] ")]
    public void HeaderUsesMessagePrivacyAndColourCapability(bool privateMessage, bool useCodColorCodes, string expected)
    {
        var result = BotMessageHeader.Create(" ^2[Bot]^7 ", privateMessage, useCodColorCodes);

        Assert.Equal(expected, result);
    }
}
