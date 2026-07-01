using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Cod4xPlugin;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

internal static class Cod4xPluginSourceResolver
{
    private const string CacheKeyPrefix = "cod4x-plugin-source-enabled-";
    private static readonly TimeSpan CacheSlidingExpiration = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<bool> IsPluginSourceEnabledAsync(
        IRepositoryApiClient repositoryApiClient,
        IMemoryCache cache,
        ILogger logger,
        Guid serverId,
        CancellationToken ct)
    {
        var cacheKey = CacheKeyPrefix + serverId;
        if (cache.TryGetValue(cacheKey, out bool cachedValue))
        {
            return cachedValue;
        }

        var enabled = false;

        try
        {
            var globalConfigsResult = await repositoryApiClient.GlobalConfigurations.V1
                .GetConfigurations(ct)
                .ConfigureAwait(false);

            var globalEnabled = false;
            if (globalConfigsResult.IsSuccess)
            {
                globalEnabled = ReadEnabled(
                    globalConfigsResult.Result?.Data?.Items,
                    logger,
                    "global") ?? false;
            }
            else
            {
                logger.LogWarning(
                    "Unable to read global cod4xPlugin settings. StatusCode: {StatusCode}",
                    globalConfigsResult.StatusCode);
            }

            var serverConfigsResult = await repositoryApiClient.GameServerConfigurations.V1
                .GetConfigurations(serverId, ct)
                .ConfigureAwait(false);

            if (serverConfigsResult.IsSuccess)
            {
                var serverOverride = ReadEnabled(
                    serverConfigsResult.Result?.Data?.Items,
                    logger,
                    $"server:{serverId}");

                enabled = serverOverride ?? globalEnabled;
            }
            else
            {
                logger.LogWarning(
                    "Unable to read server cod4xPlugin settings for {ServerId}. StatusCode: {StatusCode}. Falling back to global/default.",
                    serverId,
                    serverConfigsResult.StatusCode);

                enabled = globalEnabled;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to resolve cod4xPlugin source flag for server {ServerId}; defaulting to backend execution",
                serverId);
        }

        cache.Set(
            cacheKey,
            enabled,
            new MemoryCacheEntryOptions().SetSlidingExpiration(CacheSlidingExpiration));

        return enabled;
    }

    private static bool? ReadEnabled(
        IEnumerable<ConfigurationDto>? configurations,
        ILogger logger,
        string scope)
    {
        if (configurations is null)
        {
            return null;
        }

        var pluginSettings = configurations.FirstOrDefault(static config =>
            string.Equals(config.Namespace, Cod4xPluginSettingsConstants.Namespace, StringComparison.OrdinalIgnoreCase));

        if (pluginSettings is null || string.IsNullOrWhiteSpace(pluginSettings.Configuration))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<Cod4xPluginSettingsDocument>(pluginSettings.Configuration, JsonOptions);
            if (document is null)
            {
                return null;
            }

            var validation = new Cod4xPluginSettingsValidator().Validate(document);
            if (!validation.IsValid)
            {
                logger.LogWarning(
                    "Ignoring invalid cod4xPlugin settings for {Scope}: {ValidationErrors}",
                    scope,
                    string.Join("; ", validation.Errors));
                return null;
            }

            return document.Enabled;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Ignoring malformed cod4xPlugin settings payload for {Scope}",
                scope);
            return null;
        }
    }
}
