using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class ChatCommandSettingsValidatorTests
{
    private readonly ChatCommandSettingsValidator _sut = new();

    [Fact]
    public void Validate_WhenDocumentIsNull_ReturnsValid()
    {
        var result = _sut.Validate(null);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WhenSchemaVersionUnsupported_ReturnsInvalid()
    {
        var result = _sut.Validate(new ChatCommandSettingsDocument
        {
            SchemaVersion = 99
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("Unsupported schemaVersion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_WhenFreshnessNegative_ReturnsInvalid()
    {
        var result = _sut.Validate(new ChatCommandSettingsDocument
        {
            SchemaVersion = ChatCommandSettingsConstants.SupportedSchemaVersion,
            Defaults = new ChatCommandSettingsDefaults
            {
                FreshnessSeconds = new ChatCommandFreshnessDefaults
                {
                    Default = -1
                }
            }
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("defaults.freshnessSeconds.default", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_WhenEntryHasEmptyTag_ReturnsInvalid()
    {
        var result = _sut.Validate(new ChatCommandSettingsDocument
        {
            SchemaVersion = ChatCommandSettingsConstants.SupportedSchemaVersion,
            Commands = new Dictionary<string, ChatCommandSettingsEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["fu"] = new ChatCommandSettingsEntry
                {
                    RequiredTags = ["", "Admin"]
                }
            }
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("commands.fu.requiredTags[0]", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_WhenCommandKeyContainsWhitespace_ReturnsInvalid()
    {
        var result = _sut.Validate(new ChatCommandSettingsDocument
        {
            SchemaVersion = ChatCommandSettingsConstants.SupportedSchemaVersion,
            Commands = new Dictionary<string, ChatCommandSettingsEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [" fu "] = new()
            }
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("cannot contain leading or trailing whitespace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_WhenCommandEntryIsNull_ReturnsInvalid()
    {
        var result = _sut.Validate(new ChatCommandSettingsDocument
        {
            SchemaVersion = ChatCommandSettingsConstants.SupportedSchemaVersion,
            Commands = new Dictionary<string, ChatCommandSettingsEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["fu"] = null!
            }
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("commands.fu must be an object", StringComparison.OrdinalIgnoreCase));
    }
}
