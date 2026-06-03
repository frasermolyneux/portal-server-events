using System.Text.Json;

using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class ChatCommandSettingsMergerTests
{
    private readonly ChatCommandSettingsMerger _sut = new();

    [Fact]
    public void Merge_WhenNoDocuments_UsesHardcodedDefaults()
    {
        var result = _sut.Merge("register", isMutating: true, globalDocument: null, serverDocument: null);

        Assert.True(result.Enabled);
        Assert.Equal(ChatCommandSettingsConstants.HardcodedMutatingFreshnessSeconds, result.FreshnessSeconds);
        Assert.Equal(SettingsValueSource.Hardcoded, result.EnabledSource);
        Assert.Equal(SettingsValueSource.Hardcoded, result.FreshnessSource);
        Assert.Empty(result.RequiredTags);
        Assert.Empty(result.RequiredClaims);
    }

    [Fact]
    public void Merge_WhenGlobalDefaultsPresent_AppliesDefaults()
    {
        var global = new ChatCommandSettingsDocument
        {
            Defaults = new ChatCommandSettingsDefaults
            {
                Enabled = false,
                FreshnessSeconds = new ChatCommandFreshnessDefaults
                {
                    Default = 9,
                    ReadOnly = 7,
                    Mutating = 4
                },
                RequiredTags = ["TagA"],
                RequiredClaims = ["ClaimA"]
            }
        };

        var result = _sut.Merge("register", isMutating: false, globalDocument: global, serverDocument: null);

        Assert.False(result.Enabled);
        Assert.Equal(7, result.FreshnessSeconds);
        Assert.Equal(["TagA"], result.RequiredTags);
        Assert.Equal(["ClaimA"], result.RequiredClaims);
        Assert.Equal(SettingsValueSource.GlobalDefaults, result.EnabledSource);
        Assert.Equal(SettingsValueSource.GlobalDefaults, result.FreshnessSource);
        Assert.Equal(SettingsValueSource.GlobalDefaults, result.AuthorizationSource);
    }

    [Fact]
    public void Merge_WhenServerCommandPresent_ServerOverridesGlobal()
    {
        var global = new ChatCommandSettingsDocument
        {
            Defaults = new ChatCommandSettingsDefaults
            {
                Enabled = true,
                FreshnessSeconds = new ChatCommandFreshnessDefaults
                {
                    ReadOnly = 7
                },
                RequiredTags = ["TagA"],
                RequiredClaims = ["ClaimA"]
            },
            Commands = new Dictionary<string, ChatCommandSettingsEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["fu"] = new ChatCommandSettingsEntry
                {
                    Enabled = true,
                    FreshnessSeconds = 6,
                    RequiredTags = ["TagB"],
                    RequiredClaims = ["ClaimB"],
                    Settings = JsonSerializer.SerializeToElement(new { mode = "global" })
                }
            }
        };

        var server = new ChatCommandSettingsDocument
        {
            Commands = new Dictionary<string, ChatCommandSettingsEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["fu"] = new ChatCommandSettingsEntry
                {
                    Enabled = false,
                    FreshnessSeconds = 2,
                    RequiredTags = ["TagC", "TagC", ""],
                    RequiredClaims = ["ClaimC"],
                    Settings = JsonSerializer.SerializeToElement(new { mode = "server" })
                }
            }
        };

        var result = _sut.Merge("fu", isMutating: false, globalDocument: global, serverDocument: server);

        Assert.False(result.Enabled);
        Assert.Equal(2, result.FreshnessSeconds);
        Assert.Equal(["TagC"], result.RequiredTags);
        Assert.Equal(["ClaimC"], result.RequiredClaims);
        Assert.Equal("server", result.Settings?.GetProperty("mode").GetString());
        Assert.Equal(SettingsValueSource.ServerCommand, result.EnabledSource);
        Assert.Equal(SettingsValueSource.ServerCommand, result.FreshnessSource);
        Assert.Equal(SettingsValueSource.ServerCommand, result.AuthorizationSource);
        Assert.Equal(SettingsValueSource.ServerCommand, result.PayloadSource);
    }
}
