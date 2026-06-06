using System.Text.Json.Serialization;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

#pragma warning disable CS1591

public static class WelcomeMessageSettingsConstants
{
    public const string Namespace = "welcomeMessages";
    public const int SupportedSchemaVersion = 1;

    public const int MinPriority = -100000;
    public const int MaxPriority = 100000;
    public const int MaxMessageTemplateLength = 256;
    public const int MaxRequiredTags = 20;

    public const int MinConnectionDelaySeconds = 0;
    public const int MaxConnectionDelaySeconds = 60;

    public const int DefaultStaleThresholdSeconds = 120;
    public const int MinStaleThresholdSeconds = 1;
    public const int MaxStaleThresholdSeconds = 3600;

    public const int DefaultConnectionDelaySeconds = 0;
    public const string DefaultCountryFallback = "Unknown";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WelcomeMessageVisibility
{
    Private,
    Public
}

public sealed class WelcomeMessageSettingsDocument
{
    public int SchemaVersion { get; set; } = WelcomeMessageSettingsConstants.SupportedSchemaVersion;

    public bool? Enabled { get; set; }

    public bool? InheritGlobalRules { get; set; }

    public WelcomeMessageDefaults? Defaults { get; set; }

    public List<WelcomeMessageRule> Rules { get; set; } = [];

    public List<WelcomeMessageRuleOverride> RuleOverrides { get; set; } = [];
}

public sealed class WelcomeMessageDefaults
{
    public string? CountryFallback { get; set; }

    public int? ConnectionDelaySeconds { get; set; }

    public int? StaleThresholdSeconds { get; set; }
}

public sealed class WelcomeMessageRule
{
    public string Id { get; set; } = string.Empty;

    public bool? Enabled { get; set; }

    public int? Priority { get; set; }

    public WelcomeMessageVisibility? Visibility { get; set; }

    public string MessageTemplate { get; set; } = string.Empty;

    public string[] RequiredTags { get; set; } = [];

    public int? ConnectionDelaySeconds { get; set; }
}

public sealed class WelcomeMessageRuleOverride
{
    public string Id { get; set; } = string.Empty;

    public bool? Enabled { get; set; }

    public int? Priority { get; set; }

    public WelcomeMessageVisibility? Visibility { get; set; }

    public string? MessageTemplate { get; set; }

    public string[]? RequiredTags { get; set; }

    public int? ConnectionDelaySeconds { get; set; }
}

public sealed record EffectiveWelcomeMessageSettings
{
    public bool Enabled { get; init; }

    public string CountryFallback { get; init; } = WelcomeMessageSettingsConstants.DefaultCountryFallback;

    public int StaleThresholdSeconds { get; init; } = WelcomeMessageSettingsConstants.DefaultStaleThresholdSeconds;

    public IReadOnlyList<EffectiveWelcomeMessageRule> Rules { get; init; } = [];

    public bool ValidationFailed { get; init; }

    public static EffectiveWelcomeMessageSettings Disabled(bool validationFailed = false) => new()
    {
        Enabled = false,
        ValidationFailed = validationFailed
    };
}

public sealed record EffectiveWelcomeMessageRule
{
    public required string Id { get; init; }

    public required bool Enabled { get; init; }

    public required int Priority { get; init; }

    public required WelcomeMessageVisibility Visibility { get; init; }

    public required string MessageTemplate { get; init; }

    public required string[] RequiredTags { get; init; }

    public required int ConnectionDelaySeconds { get; init; }

    public required int OrderIndex { get; init; }
}

public sealed record WelcomeMessageSettingsValidationResult
{
    public bool IsValid => Errors.Count == 0;

    public List<string> Errors { get; } = [];
}

#pragma warning restore CS1591
