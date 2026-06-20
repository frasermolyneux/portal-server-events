using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Moderation;

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
    private const int DefaultThresholdValue = 4;
    private const string ModerationNamespace = "moderation";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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

            var globalDocument = Deserialize(globalConfig, "global", serverId);
            var serverDocument = Deserialize(serverConfig, "server", serverId);

            var globalSettings = ParseGlobalSettings(globalDocument, defaults, serverId);
            var effectiveSettings = ApplyServerOverrides(globalSettings, serverDocument, serverId);

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

        var hateThreshold = TryGetIntFromConfiguration("ContentSafety:HateSeverityThreshold");
        var violenceThreshold = TryGetIntFromConfiguration("ContentSafety:ViolenceSeverityThreshold");
        var sexualThreshold = TryGetIntFromConfiguration("ContentSafety:SexualSeverityThreshold");
        var selfHarmThreshold = TryGetIntFromConfiguration("ContentSafety:SelfHarmSeverityThreshold");

        return new ChatModerationSettings(
            MinMessageLength: minMessageLength,
            HateSeverityThreshold: ResolveCategoryThreshold(hateThreshold, DefaultThresholdValue),
            ViolenceSeverityThreshold: ResolveCategoryThreshold(violenceThreshold, DefaultThresholdValue),
            SexualSeverityThreshold: ResolveCategoryThreshold(sexualThreshold, DefaultThresholdValue),
            SelfHarmSeverityThreshold: ResolveCategoryThreshold(selfHarmThreshold, DefaultThresholdValue));
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

    private ChatModerationSettings ParseGlobalSettings(ModerationSettingsDocument? moderationDocument, ChatModerationSettings defaults, Guid serverId)
    {
        if (moderationDocument is null)
        {
            return defaults;
        }

        var validation = new ModerationSettingsValidator().Validate(moderationDocument);
        if (!validation.IsValid)
        {
            logger.LogWarning(
                "Invalid global moderation settings for server {ServerId}: {Errors}",
                serverId,
                string.Join("; ", validation.Errors));
            return defaults;
        }

        return new ChatModerationSettings(
            MinMessageLength: moderationDocument.MinMessageLength ?? defaults.MinMessageLength,
            HateSeverityThreshold: ResolveCategoryThreshold(
                categoryValue: moderationDocument.ContentSafetyHateSeverityThreshold,
                fallbackValue: defaults.HateSeverityThreshold),
            ViolenceSeverityThreshold: ResolveCategoryThreshold(
                categoryValue: moderationDocument.ContentSafetyViolenceSeverityThreshold,
                fallbackValue: defaults.ViolenceSeverityThreshold),
            SexualSeverityThreshold: ResolveCategoryThreshold(
                categoryValue: moderationDocument.ContentSafetySexualSeverityThreshold,
                fallbackValue: defaults.SexualSeverityThreshold),
            SelfHarmSeverityThreshold: ResolveCategoryThreshold(
                categoryValue: moderationDocument.ContentSafetySelfHarmSeverityThreshold,
                fallbackValue: defaults.SelfHarmSeverityThreshold));
    }

    private ChatModerationSettings ApplyServerOverrides(ChatModerationSettings globalSettings, ModerationSettingsDocument? serverDocument, Guid serverId)
    {
        if (serverDocument is null)
        {
            return globalSettings;
        }

        var validation = new ModerationSettingsValidator().Validate(serverDocument);
        if (!validation.IsValid)
        {
            logger.LogWarning(
                "Invalid server moderation settings for server {ServerId}: {Errors}",
                serverId,
                string.Join("; ", validation.Errors));
            return globalSettings;
        }

        return new ChatModerationSettings(
            MinMessageLength: serverDocument.MinMessageLength ?? globalSettings.MinMessageLength,
            HateSeverityThreshold: ResolveServerOverride(serverDocument.ContentSafetyHateSeverityThreshold, globalSettings.HateSeverityThreshold),
            ViolenceSeverityThreshold: ResolveServerOverride(serverDocument.ContentSafetyViolenceSeverityThreshold, globalSettings.ViolenceSeverityThreshold),
            SexualSeverityThreshold: ResolveServerOverride(serverDocument.ContentSafetySexualSeverityThreshold, globalSettings.SexualSeverityThreshold),
            SelfHarmSeverityThreshold: ResolveServerOverride(serverDocument.ContentSafetySelfHarmSeverityThreshold, globalSettings.SelfHarmSeverityThreshold));
    }

    private static int? ResolveCategoryThreshold(int? categoryValue, int? fallbackValue)
    {
        return NormalizeThreshold(categoryValue ?? fallbackValue);
    }

    private static int? ResolveServerOverride(int? overrideValue, int? globalValue)
    {
        return overrideValue.HasValue
            ? NormalizeThreshold(overrideValue)
            : globalValue;
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

    private ModerationSettingsDocument? Deserialize(ConfigurationDto? config, string scope, Guid serverId)
    {
        if (string.IsNullOrWhiteSpace(config?.Configuration))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ModerationSettingsDocument>(config.Configuration, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse {Scope} moderation settings for server {ServerId}", scope, serverId);
            return new ModerationSettingsDocument { SchemaVersion = -1 };
        }
    }
}
