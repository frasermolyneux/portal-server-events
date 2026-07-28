using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public sealed class VpnProtectionSettingsProvider(
    IRepositoryApiClient repositoryApiClient,
    IMemoryCache memoryCache,
    ILogger<VpnProtectionSettingsProvider> logger) : IVpnProtectionSettingsProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InvalidCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly VpnProtectionSettingsMerger merger = new();

    public async Task<EffectiveVpnProtectionSettings> GetEffectiveSettingsAsync(
        Guid serverId,
        CancellationToken ct = default)
    {
        var cacheKey = $"vpn-protection-settings:{serverId}";
        if (memoryCache.TryGetValue(cacheKey, out EffectiveVpnProtectionSettings? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var globalConfigTask = repositoryApiClient.GlobalConfigurations.V1
                .GetConfiguration(VpnProtectionSettingsConstants.Namespace, ct);
            var serverConfigTask = repositoryApiClient.GameServerConfigurations.V1
                .GetConfiguration(serverId, VpnProtectionSettingsConstants.Namespace, ct);

            await Task.WhenAll(globalConfigTask, serverConfigTask).ConfigureAwait(false);

            var globalConfig = await globalConfigTask.ConfigureAwait(false);
            var serverConfig = await serverConfigTask.ConfigureAwait(false);

            if ((!globalConfig.IsSuccess && !globalConfig.IsNotFound) ||
                (!serverConfig.IsSuccess && !serverConfig.IsNotFound))
            {
                logger.LogWarning(
                    "Failed to load VPN Protection settings for server {ServerId}. Global status: {GlobalStatus}; server status: {ServerStatus}",
                    serverId,
                    globalConfig.StatusCode,
                    serverConfig.StatusCode);
                return EffectiveVpnProtectionSettings.Disabled(validationFailed: true);
            }

            var globalDocument = Deserialize(globalConfig.Result?.Data, "global", serverId);
            var serverDocument = Deserialize(serverConfig.Result?.Data, "server", serverId);
            var effectiveSettings = merger.Merge(globalDocument, serverDocument);

            if (effectiveSettings.ValidationFailed)
            {
                logger.LogWarning(
                    "VPN Protection settings validation failed for server {ServerId}; enforcement is disabled until corrected",
                    serverId);
            }

            memoryCache.Set(cacheKey, effectiveSettings, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = effectiveSettings.ValidationFailed
                    ? InvalidCacheDuration
                    : CacheDuration
            });

            return effectiveSettings;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve VPN Protection settings for server {ServerId}", serverId);
            return EffectiveVpnProtectionSettings.Disabled(validationFailed: true);
        }
    }

    private VpnProtectionSettingsDocument? Deserialize(
        ConfigurationDto? configuration,
        string scope,
        Guid serverId)
    {
        if (string.IsNullOrWhiteSpace(configuration?.Configuration))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VpnProtectionSettingsDocument>(configuration.Configuration, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse {Scope} VPN Protection settings for server {ServerId}", scope, serverId);
            return new VpnProtectionSettingsDocument { SchemaVersion = -1 };
        }
    }
}
