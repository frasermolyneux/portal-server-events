using System.Text.Json;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public static class ChatCommandSettingsConstants
{
    public const string Namespace = "chatCommands";
    public const int SupportedSchemaVersion = 1;

    public const int HardcodedDefaultFreshnessSeconds = 5;
    public const int HardcodedReadOnlyFreshnessSeconds = 5;
    public const int HardcodedMutatingFreshnessSeconds = 3;
}

public sealed class ChatCommandSettingsDocument
{
    public int SchemaVersion { get; set; } = ChatCommandSettingsConstants.SupportedSchemaVersion;

    public ChatCommandSettingsDefaults? Defaults { get; set; }

    public Dictionary<string, ChatCommandSettingsEntry> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ChatCommandSettingsDefaults
{
    public bool? Enabled { get; set; }

    public ChatCommandFreshnessDefaults? FreshnessSeconds { get; set; }

    public string[]? RequiredTags { get; set; }

    public string[]? RequiredClaims { get; set; }
}

public sealed class ChatCommandFreshnessDefaults
{
    public int? Default { get; set; }

    public int? ReadOnly { get; set; }

    public int? Mutating { get; set; }
}

public sealed class ChatCommandSettingsEntry
{
    public bool? Enabled { get; set; }

    public int? FreshnessSeconds { get; set; }

    public string[]? RequiredTags { get; set; }

    public string[]? RequiredClaims { get; set; }

    public JsonElement? Settings { get; set; }
}

public enum SettingsValueSource
{
    Hardcoded,
    GlobalDefaults,
    GlobalCommand,
    ServerCommand,
    ValidationFailure
}

public sealed record EffectiveChatCommandSettings
{
    public required string CommandName { get; init; }

    public required bool Enabled { get; init; }

    public required int FreshnessSeconds { get; init; }

    public string[] RequiredTags { get; init; } = [];

    public string[] RequiredClaims { get; init; } = [];

    public JsonElement? Settings { get; init; }

    public SettingsValueSource EnabledSource { get; init; }

    public SettingsValueSource FreshnessSource { get; init; }

    public SettingsValueSource AuthorizationSource { get; init; }

    public SettingsValueSource PayloadSource { get; init; }

    public bool ValidationFailed { get; init; }

    public static EffectiveChatCommandSettings Disabled(string commandName) => new()
    {
        CommandName = commandName,
        Enabled = false,
        FreshnessSeconds = ChatCommandSettingsConstants.HardcodedReadOnlyFreshnessSeconds,
        EnabledSource = SettingsValueSource.ValidationFailure,
        FreshnessSource = SettingsValueSource.Hardcoded,
        AuthorizationSource = SettingsValueSource.Hardcoded,
        PayloadSource = SettingsValueSource.Hardcoded,
        ValidationFailed = true
    };
}

public sealed record ChatCommandSettingsValidationResult
{
    public bool IsValid => Errors.Count == 0;

    public List<string> Errors { get; } = [];
}
