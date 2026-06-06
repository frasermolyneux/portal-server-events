using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class WelcomeMessageSettingsProvider : IWelcomeMessageSettingsProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InvalidCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IRepositoryApiClient _repositoryClient;
    private readonly IMemoryCache _memoryCache;
    private readonly WelcomeMessageSettingsValidator _validator;
    private readonly WelcomeMessageSettingsMerger _merger;
    private readonly ILogger<WelcomeMessageSettingsProvider> _logger;

    public WelcomeMessageSettingsProvider(
        IRepositoryApiClient repositoryClient,
        IMemoryCache memoryCache,
        WelcomeMessageSettingsValidator validator,
        WelcomeMessageSettingsMerger merger,
        ILogger<WelcomeMessageSettingsProvider> logger)
    {
        _repositoryClient = repositoryClient;
        _memoryCache = memoryCache;
        _validator = validator;
        _merger = merger;
        _logger = logger;
    }

    public async Task<EffectiveWelcomeMessageSettings> GetEffectiveSettingsAsync(Guid serverId, CancellationToken ct = default)
    {
        var cacheKey = $"welcome-message-settings:{serverId}";

        if (_memoryCache.TryGetValue(cacheKey, out CachedDocuments? cached) && cached is not null)
        {
            return BuildEffective(cached);
        }

        try
        {
            var loaded = await LoadDocumentsAsync(serverId, ct).ConfigureAwait(false);
            _memoryCache.Set(cacheKey, loaded, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = loaded.ValidationFailed ? InvalidCacheDuration : CacheDuration
            });

            return BuildEffective(loaded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve welcome message settings for server {ServerId}", serverId);
            return EffectiveWelcomeMessageSettings.Disabled(validationFailed: true);
        }
    }

    private EffectiveWelcomeMessageSettings BuildEffective(CachedDocuments cached)
    {
        if (cached.ValidationFailed)
        {
            _logger.LogWarning(
                "Welcome message settings validation failed for server {ServerId}. Sending is fail-closed until corrected.",
                cached.ServerId);

            return EffectiveWelcomeMessageSettings.Disabled(validationFailed: true);
        }

        return _merger.Merge(cached.GlobalDocument, cached.ServerDocument);
    }

    private async Task<CachedDocuments> LoadDocumentsAsync(Guid serverId, CancellationToken ct)
    {
        var globalConfig = await GetGlobalWelcomeMessagesConfigAsync(ct).ConfigureAwait(false);
        var serverConfig = await GetServerWelcomeMessagesConfigAsync(serverId, ct).ConfigureAwait(false);

        var globalDocument = Deserialize(globalConfig, "global", serverId);
        var serverDocument = Deserialize(serverConfig, "server", serverId);

        var globalValidation = _validator.Validate(globalDocument);
        var serverValidation = _validator.Validate(serverDocument);
        var validationFailed = !globalValidation.IsValid || !serverValidation.IsValid;

        if (!globalValidation.IsValid)
        {
            _logger.LogWarning(
                "Invalid global welcome message settings for server {ServerId}: {Errors}",
                serverId,
                string.Join("; ", globalValidation.Errors));
        }

        if (!serverValidation.IsValid)
        {
            _logger.LogWarning(
                "Invalid server welcome message settings for server {ServerId}: {Errors}",
                serverId,
                string.Join("; ", serverValidation.Errors));
        }

        return new CachedDocuments(serverId, globalDocument, serverDocument, validationFailed);
    }

    private async Task<ConfigurationDto?> GetGlobalWelcomeMessagesConfigAsync(CancellationToken ct)
    {
        var globalConfigs = await _repositoryClient.GlobalConfigurations.V1
            .GetConfigurations(ct)
            .ConfigureAwait(false);

        if (!globalConfigs.IsSuccess)
        {
            throw new InvalidOperationException("Failed to fetch global welcome message settings.");
        }

        if (globalConfigs.Result?.Data?.Items is null)
        {
            return null;
        }

        return globalConfigs.Result.Data.Items.FirstOrDefault(x =>
            string.Equals(x.Namespace, WelcomeMessageSettingsConstants.Namespace, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ConfigurationDto?> GetServerWelcomeMessagesConfigAsync(Guid serverId, CancellationToken ct)
    {
        var serverConfigs = await _repositoryClient.GameServerConfigurations.V1
            .GetConfigurations(serverId, ct)
            .ConfigureAwait(false);

        if (!serverConfigs.IsSuccess)
        {
            throw new InvalidOperationException($"Failed to fetch server welcome message settings for server '{serverId}'.");
        }

        if (serverConfigs.Result?.Data?.Items is null)
        {
            return null;
        }

        return serverConfigs.Result.Data.Items.FirstOrDefault(x =>
            string.Equals(x.Namespace, WelcomeMessageSettingsConstants.Namespace, StringComparison.OrdinalIgnoreCase));
    }

    private WelcomeMessageSettingsDocument? Deserialize(ConfigurationDto? config, string scope, Guid serverId)
    {
        if (string.IsNullOrWhiteSpace(config?.Configuration))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WelcomeMessageSettingsDocument>(config.Configuration, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse {Scope} welcome message settings for server {ServerId}", scope, serverId);
            return new WelcomeMessageSettingsDocument { SchemaVersion = -1 };
        }
    }

    private sealed record CachedDocuments(
        Guid ServerId,
        WelcomeMessageSettingsDocument? GlobalDocument,
        WelcomeMessageSettingsDocument? ServerDocument,
        bool ValidationFailed);
}
