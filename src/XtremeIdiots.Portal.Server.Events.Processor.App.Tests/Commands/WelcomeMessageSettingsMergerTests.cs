using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class WelcomeMessageSettingsMergerTests
{
    private readonly WelcomeMessageSettingsMerger _sut = new();

    [Fact]
    public void Merge_WhenNoDocuments_UsesFailOpenDefaults()
    {
        var result = _sut.Merge(globalDocument: null, serverDocument: null);

        Assert.True(result.Enabled);
        Assert.Equal(WelcomeMessageSettingsConstants.DefaultCountryFallback, result.CountryFallback);
        Assert.Equal(WelcomeMessageSettingsConstants.DefaultStaleThresholdSeconds, result.StaleThresholdSeconds);
        Assert.Empty(result.Rules);
        Assert.False(result.ValidationFailed);
    }

    [Fact]
    public void Merge_WhenServerOverridesAndAddsRule_AppliesExpectedPrecedence()
    {
        var global = new WelcomeMessageSettingsDocument
        {
            Enabled = true,
            Defaults = new WelcomeMessageDefaults
            {
                CountryFallback = "GlobalFallback",
                StaleThresholdSeconds = 200,
                ConnectionDelaySeconds = 5
            },
            Rules =
            [
                new WelcomeMessageRule
                {
                    Id = "global-a",
                    Enabled = true,
                    Priority = 10,
                    Visibility = WelcomeMessageVisibility.Private,
                    MessageTemplate = "Global A",
                    RequiredTags = ["global"],
                    ConnectionDelaySeconds = 6
                }
            ]
        };

        var server = new WelcomeMessageSettingsDocument
        {
            Defaults = new WelcomeMessageDefaults
            {
                CountryFallback = "ServerFallback",
                StaleThresholdSeconds = 300,
                ConnectionDelaySeconds = 7
            },
            RuleOverrides =
            [
                new WelcomeMessageRuleOverride
                {
                    Id = "global-a",
                    Enabled = false,
                    Priority = 20,
                    Visibility = WelcomeMessageVisibility.Public,
                    MessageTemplate = "Server override",
                    RequiredTags = ["server"],
                    ConnectionDelaySeconds = 8
                }
            ],
            Rules =
            [
                new WelcomeMessageRule
                {
                    Id = "server-b",
                    Enabled = true,
                    Priority = 30,
                    Visibility = WelcomeMessageVisibility.Private,
                    MessageTemplate = "Server B",
                    RequiredTags = ["extra"],
                    ConnectionDelaySeconds = 9
                }
            ]
        };

        var result = _sut.Merge(global, server);

        Assert.True(result.Enabled);
        Assert.Equal("ServerFallback", result.CountryFallback);
        Assert.Equal(300, result.StaleThresholdSeconds);
        Assert.Equal(2, result.Rules.Count);

        var overridden = result.Rules.Single(r => r.Id == "global-a");
        Assert.False(overridden.Enabled);
        Assert.Equal(20, overridden.Priority);
        Assert.Equal(WelcomeMessageVisibility.Public, overridden.Visibility);
        Assert.Equal("Server override", overridden.MessageTemplate);
        Assert.Equal(["server"], overridden.RequiredTags);
        Assert.Equal(8, overridden.ConnectionDelaySeconds);
        Assert.Equal(0, overridden.OrderIndex);

        var serverOnly = result.Rules.Single(r => r.Id == "server-b");
        Assert.True(serverOnly.Enabled);
        Assert.Equal(30, serverOnly.Priority);
        Assert.Equal(WelcomeMessageVisibility.Private, serverOnly.Visibility);
        Assert.Equal("Server B", serverOnly.MessageTemplate);
        Assert.Equal(["extra"], serverOnly.RequiredTags);
        Assert.Equal(9, serverOnly.ConnectionDelaySeconds);
        Assert.Equal(1, serverOnly.OrderIndex);
    }
}
