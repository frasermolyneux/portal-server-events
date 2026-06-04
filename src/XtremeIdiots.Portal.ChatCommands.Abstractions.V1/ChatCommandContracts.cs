using System.Text.Json;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

/// <summary>
/// Shared constants for the chat command settings contract.
/// </summary>
public static class ChatCommandSettingsConstants
{
    /// <summary>
    /// Configuration namespace used to store chat command settings.
    /// </summary>
    public const string Namespace = "chatCommands";

    /// <summary>
    /// Supported schema version for chat command settings documents.
    /// </summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>
    /// Hardcoded fallback freshness for default command category.
    /// </summary>
    public const int HardcodedDefaultFreshnessSeconds = 5;

    /// <summary>
    /// Hardcoded fallback freshness for read-only commands.
    /// </summary>
    public const int HardcodedReadOnlyFreshnessSeconds = 5;

    /// <summary>
    /// Hardcoded fallback freshness for mutating commands.
    /// </summary>
    public const int HardcodedMutatingFreshnessSeconds = 3;
}

/// <summary>
/// Root chat command settings document.
/// </summary>
public sealed class ChatCommandSettingsDocument
{
    /// <summary>
    /// Schema version for this document.
    /// </summary>
    public int SchemaVersion { get; set; } = ChatCommandSettingsConstants.SupportedSchemaVersion;

    /// <summary>
    /// Global defaults applied when command-specific values are absent.
    /// </summary>
    public ChatCommandSettingsDefaults? Defaults { get; set; }

    /// <summary>
    /// Per-command settings keyed by command name.
    /// </summary>
    public Dictionary<string, ChatCommandSettingsEntry> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Global default settings for all commands.
/// </summary>
public sealed class ChatCommandSettingsDefaults
{
    /// <summary>
    /// Default enabled flag for commands.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Default freshness values by category.
    /// </summary>
    public ChatCommandFreshnessDefaults? FreshnessSeconds { get; set; }

    /// <summary>
    /// Default required authorization tags.
    /// </summary>
    public string[]? RequiredTags { get; set; }

    /// <summary>
    /// Default required authorization claims.
    /// </summary>
    public string[]? RequiredClaims { get; set; }
}

/// <summary>
/// Freshness defaults by command category.
/// </summary>
public sealed class ChatCommandFreshnessDefaults
{
    /// <summary>
    /// Default freshness for general commands.
    /// </summary>
    public int? Default { get; set; }

    /// <summary>
    /// Default freshness for read-only commands.
    /// </summary>
    public int? ReadOnly { get; set; }

    /// <summary>
    /// Default freshness for mutating commands.
    /// </summary>
    public int? Mutating { get; set; }
}

/// <summary>
/// Per-command settings entry.
/// </summary>
public sealed class ChatCommandSettingsEntry
{
    /// <summary>
    /// Optional explicit enabled override.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Optional explicit freshness override.
    /// </summary>
    public int? FreshnessSeconds { get; set; }

    /// <summary>
    /// Optional required authorization tags override.
    /// </summary>
    public string[]? RequiredTags { get; set; }

    /// <summary>
    /// Optional required authorization claims override.
    /// </summary>
    public string[]? RequiredClaims { get; set; }

    /// <summary>
    /// Optional command-specific payload.
    /// </summary>
    public JsonElement? Settings { get; set; }
}

/// <summary>
/// Indicates where an effective setting value came from.
/// </summary>
public enum SettingsValueSource
{
    /// <summary>
    /// Value came from hardcoded defaults.
    /// </summary>
    Hardcoded,

    /// <summary>
    /// Value came from global defaults.
    /// </summary>
    GlobalDefaults,

    /// <summary>
    /// Value came from global per-command settings.
    /// </summary>
    GlobalCommand,

    /// <summary>
    /// Value came from server per-command settings.
    /// </summary>
    ServerCommand,

    /// <summary>
    /// Value came from fail-closed validation behavior.
    /// </summary>
    ValidationFailure
}

/// <summary>
/// Effective command settings resolved from all sources.
/// </summary>
public sealed record EffectiveChatCommandSettings
{
    /// <summary>
    /// Command name.
    /// </summary>
    public required string CommandName { get; init; }

    /// <summary>
    /// Effective enabled flag.
    /// </summary>
    public required bool Enabled { get; init; }

    /// <summary>
    /// Effective freshness seconds.
    /// </summary>
    public required int FreshnessSeconds { get; init; }

    /// <summary>
    /// Effective required tags.
    /// </summary>
    public string[] RequiredTags { get; init; } = [];

    /// <summary>
    /// Effective required claims.
    /// </summary>
    public string[] RequiredClaims { get; init; } = [];

    /// <summary>
    /// Effective command-specific payload.
    /// </summary>
    public JsonElement? Settings { get; init; }

    /// <summary>
    /// Source for enabled value.
    /// </summary>
    public SettingsValueSource EnabledSource { get; init; }

    /// <summary>
    /// Source for freshness value.
    /// </summary>
    public SettingsValueSource FreshnessSource { get; init; }

    /// <summary>
    /// Source for authorization requirements.
    /// </summary>
    public SettingsValueSource AuthorizationSource { get; init; }

    /// <summary>
    /// Source for command-specific payload.
    /// </summary>
    public SettingsValueSource PayloadSource { get; init; }

    /// <summary>
    /// Indicates the settings document failed validation.
    /// </summary>
    public bool ValidationFailed { get; init; }

    /// <summary>
    /// Builds a fail-closed disabled settings object for the specified command.
    /// </summary>
    /// <param name="commandName">Command name.</param>
    /// <returns>Fail-closed effective settings object.</returns>
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

/// <summary>
/// Result of validating a chat command settings document.
/// </summary>
public sealed record ChatCommandSettingsValidationResult
{
    /// <summary>
    /// Gets a value indicating whether validation produced no errors.
    /// </summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>
    /// Validation error messages.
    /// </summary>
    public List<string> Errors { get; } = [];
}
