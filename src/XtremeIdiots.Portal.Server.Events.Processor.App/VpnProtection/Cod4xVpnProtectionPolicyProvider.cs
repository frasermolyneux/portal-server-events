using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Cod4xPlugin;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public sealed class Cod4xVpnProtectionPolicyProvider(
    IRepositoryApiClient repositoryApiClient,
    IMemoryCache memoryCache,
    ILogger<Cod4xVpnProtectionPolicyProvider> logger) : ICod4xVpnProtectionPolicyProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<bool> IsEnabledAsync(Guid serverId, CancellationToken ct = default)
    {
        var cacheKey = $"cod4x-vpn-protection-policy:{serverId}";
        if (memoryCache.TryGetValue(cacheKey, out bool cached))
        {
            return cached;
        }

        var enabled = false;
        try
        {
            var globalTask = repositoryApiClient.GlobalConfigurations.V1
                .GetConfiguration(Cod4xPluginSettingsConstants.Namespace, ct);
            var serverTask = repositoryApiClient.GameServerConfigurations.V1
                .GetConfiguration(serverId, Cod4xPluginSettingsConstants.Namespace, ct);
            await Task.WhenAll(globalTask, serverTask).ConfigureAwait(false);

            var globalResult = await globalTask.ConfigureAwait(false);
            var serverResult = await serverTask.ConfigureAwait(false);
            if ((!globalResult.IsSuccess && !globalResult.IsNotFound) ||
                (!serverResult.IsSuccess && !serverResult.IsNotFound))
            {
                logger.LogWarning(
                    "Failed to load CoD4x VPN Protection gates for server {ServerId}. Global status: {GlobalStatus}; server status: {ServerStatus}",
                    serverId,
                    globalResult.StatusCode,
                    serverResult.StatusCode);
                return false;
            }

            var globalDocument = Deserialize(globalResult.Result?.Data, "global", serverId);
            var serverDocument = Deserialize(serverResult.Result?.Data, "server", serverId);
            if (!IsValid(globalDocument) || !IsValid(serverDocument))
            {
                return false;
            }

            var pluginEnabled = serverDocument?.Enabled ?? globalDocument?.Enabled ?? false;
            var vpnProtectionEnabled = serverDocument?.VpnProtectionEnabled ??
                globalDocument?.VpnProtectionEnabled ??
                false;
            enabled = pluginEnabled && vpnProtectionEnabled;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve CoD4x VPN Protection gates for server {ServerId}", serverId);
        }

        memoryCache.Set(cacheKey, enabled, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration
        });
        return enabled;
    }

    private Cod4xPluginSettingsDocument? Deserialize(
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
            return JsonSerializer.Deserialize<Cod4xPluginSettingsDocument>(configuration.Configuration, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse {Scope} CoD4x plugin settings for server {ServerId}", scope, serverId);
            return new Cod4xPluginSettingsDocument { SchemaVersion = -1 };
        }
    }

    private bool IsValid(Cod4xPluginSettingsDocument? document)
    {
        var validation = new Cod4xPluginSettingsValidator().Validate(document);
        if (validation.IsValid)
        {
            return true;
        }

        logger.LogWarning(
            "Invalid CoD4x plugin settings for VPN Protection: {Errors}",
            string.Join("; ", validation.Errors));
        return false;
    }
}