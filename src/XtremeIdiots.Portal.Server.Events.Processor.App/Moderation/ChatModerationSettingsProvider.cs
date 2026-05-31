using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

public sealed class ChatModerationSettingsProvider(
    IRepositoryApiClient repositoryApiClient,
    IConfiguration configuration,
    IMemoryCache memoryCache,
    ILogger<ChatModerationSettingsProvider> logger) : IChatModerationSettingsProvider
{
    private const int DisabledThresholdValue = -1;
    private const int MinThresholdValue = 0;
    private const int MaxThresholdValue = 6;
    private const int LegacyDefaultThresholdValue = 4;
    private const string ModerationNamespace = "moderation";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public async Task<ChatModerationSettings> GetForServerAsync(Guid serverId, CancellationToken ct = default)
    {
        var cacheKey = $"chat-moderation-settings:{serverId}";

        if (memoryCache.TryGetValue(cacheKey, out ChatModerationSettings? cached) && cached is not null)
        {
            return cached;
        }

        var defaults = BuildDefaultsFromConfiguration();

        try
        {
            var globalConfig = await GetGlobalModerationConfigAsync(ct).ConfigureAwait(false);
            var serverConfig = await GetServerModerationConfigAsync(serverId, ct).ConfigureAwait(false);

            var globalSettings = ParseGlobalSettings(globalConfig, defaults);
            var effectiveSettings = ApplyServerOverrides(globalSettings, serverConfig);

            memoryCache.Set(cacheKey, effectiveSettings, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheDuration
            });

            return effectiveSettings;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve chat moderation settings for server {ServerId}; using defaults", serverId);
            return defaults;
        }
    }

    private ChatModerationSettings BuildDefaultsFromConfiguration()
    {
        var minMessageLength = int.TryParse(configuration["ContentSafety:MinMessageLength"], out var configuredMinLength)
            ? configuredMinLength
            : 5;

        var legacyThreshold = TryGetIntFromConfiguration("ContentSafety:SeverityThreshold");

        var hateThreshold = TryGetIntFromConfiguration("ContentSafety:HateSeverityThreshold");
        var violenceThreshold = TryGetIntFromConfiguration("ContentSafety:ViolenceSeverityThreshold");
        var sexualThreshold = TryGetIntFromConfiguration("ContentSafety:SexualSeverityThreshold");
        var selfHarmThreshold = TryGetIntFromConfiguration("ContentSafety:SelfHarmSeverityThreshold");

        return new ChatModerationSettings(
            MinMessageLength: minMessageLength,
            HateSeverityThreshold: ResolveCategoryThreshold(hateThreshold, legacyThreshold, LegacyDefaultThresholdValue),
            ViolenceSeverityThreshold: ResolveCategoryThreshold(violenceThreshold, legacyThreshold, LegacyDefaultThresholdValue),
            SexualSeverityThreshold: ResolveCategoryThreshold(sexualThreshold, legacyThreshold, LegacyDefaultThresholdValue),
            SelfHarmSeverityThreshold: ResolveCategoryThreshold(selfHarmThreshold, legacyThreshold, LegacyDefaultThresholdValue));
    }

    private int? TryGetIntFromConfiguration(string key)
    {
        return int.TryParse(configuration[key], out var value) ? value : null;
    }

    private async Task<ConfigurationDto?> GetGlobalModerationConfigAsync(CancellationToken ct)
    {
        var globalConfigs = await repositoryApiClient.GlobalConfigurations.V1
            .GetConfigurations(ct)
            .ConfigureAwait(false);

        if (!globalConfigs.IsSuccess || globalConfigs.Result?.Data?.Items is null)
        {
            return null;
        }

        return globalConfigs.Result.Data.Items.FirstOrDefault(x =>
            string.Equals(x.Namespace, ModerationNamespace, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ConfigurationDto?> GetServerModerationConfigAsync(Guid serverId, CancellationToken ct)
    {
        var serverConfigs = await repositoryApiClient.GameServerConfigurations.V1
            .GetConfigurations(serverId, ct)
            .ConfigureAwait(false);

        if (!serverConfigs.IsSuccess || serverConfigs.Result?.Data?.Items is null)
        {
            return null;
        }

        return serverConfigs.Result.Data.Items.FirstOrDefault(x =>
            string.Equals(x.Namespace, ModerationNamespace, StringComparison.OrdinalIgnoreCase));
    }

    private static ChatModerationSettings ParseGlobalSettings(ConfigurationDto? moderationConfig, ChatModerationSettings defaults)
    {
        if (string.IsNullOrWhiteSpace(moderationConfig?.Configuration))
        {
            return defaults;
        }

        try
        {
            using var document = JsonDocument.Parse(moderationConfig.Configuration);
            var root = document.RootElement;

            var legacyThreshold = TryGetThresholdValue(root, "contentSafetySeverityThreshold");

            return new ChatModerationSettings(
                MinMessageLength: TryGetInt(root, "minMessageLength") ?? defaults.MinMessageLength,
                HateSeverityThreshold: ResolveCategoryThreshold(
                    categoryValue: TryGetThresholdValue(root, "contentSafetyHateSeverityThreshold"),
                    legacyValue: legacyThreshold,
                    fallbackValue: defaults.HateSeverityThreshold),
                ViolenceSeverityThreshold: ResolveCategoryThreshold(
                    categoryValue: TryGetThresholdValue(root, "contentSafetyViolenceSeverityThreshold"),
                    legacyValue: legacyThreshold,
                    fallbackValue: defaults.ViolenceSeverityThreshold),
                SexualSeverityThreshold: ResolveCategoryThreshold(
                    categoryValue: TryGetThresholdValue(root, "contentSafetySexualSeverityThreshold"),
                    legacyValue: legacyThreshold,
                    fallbackValue: defaults.SexualSeverityThreshold),
                SelfHarmSeverityThreshold: ResolveCategoryThreshold(
                    categoryValue: TryGetThresholdValue(root, "contentSafetySelfHarmSeverityThreshold"),
                    legacyValue: legacyThreshold,
                    fallbackValue: defaults.SelfHarmSeverityThreshold));
        }
        catch (JsonException)
        {
            return defaults;
        }
    }

    private static ChatModerationSettings ApplyServerOverrides(ChatModerationSettings globalSettings, ConfigurationDto? serverConfig)
    {
        if (string.IsNullOrWhiteSpace(serverConfig?.Configuration))
        {
            return globalSettings;
        }

        try
        {
            using var document = JsonDocument.Parse(serverConfig.Configuration);
            var root = document.RootElement;

            var legacyThreshold = TryGetThresholdOverride(root, "contentSafetySeverityThreshold");

            var hateOverride = ResolveCategoryThresholdOverride(root, "contentSafetyHateSeverityThreshold", legacyThreshold);
            var violenceOverride = ResolveCategoryThresholdOverride(root, "contentSafetyViolenceSeverityThreshold", legacyThreshold);
            var sexualOverride = ResolveCategoryThresholdOverride(root, "contentSafetySexualSeverityThreshold", legacyThreshold);
            var selfHarmOverride = ResolveCategoryThresholdOverride(root, "contentSafetySelfHarmSeverityThreshold", legacyThreshold);

            return new ChatModerationSettings(
                MinMessageLength: TryGetInt(root, "minMessageLength") ?? globalSettings.MinMessageLength,
                HateSeverityThreshold: hateOverride.HasValue ? hateOverride.Value : globalSettings.HateSeverityThreshold,
                ViolenceSeverityThreshold: violenceOverride.HasValue ? violenceOverride.Value : globalSettings.ViolenceSeverityThreshold,
                SexualSeverityThreshold: sexualOverride.HasValue ? sexualOverride.Value : globalSettings.SexualSeverityThreshold,
                SelfHarmSeverityThreshold: selfHarmOverride.HasValue ? selfHarmOverride.Value : globalSettings.SelfHarmSeverityThreshold);
        }
        catch (JsonException)
        {
            return globalSettings;
        }
    }

    private static int? ResolveCategoryThreshold(int? categoryValue, int? legacyValue, int? fallbackValue)
    {
        return NormalizeThreshold(categoryValue ?? legacyValue ?? fallbackValue);
    }

    private static ThresholdOverride ResolveCategoryThresholdOverride(JsonElement root, string propertyName, ThresholdOverride legacyOverride)
    {
        var categoryOverride = TryGetThresholdOverride(root, propertyName);
        if (categoryOverride.HasValue)
        {
            return new ThresholdOverride(true, NormalizeThreshold(categoryOverride.Value));
        }

        if (legacyOverride.HasValue)
        {
            return new ThresholdOverride(true, NormalizeThreshold(legacyOverride.Value));
        }

        return ThresholdOverride.None;
    }

    private static int? NormalizeThreshold(int? value)
    {
        if (!value.HasValue || value.Value == DisabledThresholdValue)
        {
            return null;
        }

        if (value.Value < MinThresholdValue)
        {
            return MinThresholdValue;
        }

        if (value.Value > MaxThresholdValue)
        {
            return MaxThresholdValue;
        }

        return value.Value;
    }

    private static int? TryGetInt(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }

    private static int? TryGetThresholdValue(JsonElement root, string propertyName)
        => TryGetInt(root, propertyName);

    private static ThresholdOverride TryGetThresholdOverride(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return ThresholdOverride.None;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
        {
            return new ThresholdOverride(true, value);
        }

        return ThresholdOverride.None;
    }

    private readonly record struct ThresholdOverride(bool HasValue, int? Value)
    {
        public static ThresholdOverride None => new(false, null);
    }
}
