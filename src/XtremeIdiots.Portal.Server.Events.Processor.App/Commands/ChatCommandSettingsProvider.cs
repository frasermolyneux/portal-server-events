using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class ChatCommandSettingsProvider : IChatCommandSettingsProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InvalidCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IRepositoryApiClient _repositoryClient;
    private readonly IMemoryCache _memoryCache;
    private readonly ChatCommandSettingsValidator _validator;
    private readonly ChatCommandSettingsMerger _merger;
    private readonly ILogger<ChatCommandSettingsProvider> _logger;

    public ChatCommandSettingsProvider(
        IRepositoryApiClient repositoryClient,
        IMemoryCache memoryCache,
        ChatCommandSettingsValidator validator,
        ChatCommandSettingsMerger merger,
        ILogger<ChatCommandSettingsProvider> logger)
    {
        _repositoryClient = repositoryClient;
        _memoryCache = memoryCache;
        _validator = validator;
        _merger = merger;
        _logger = logger;
    }

    public async Task<EffectiveChatCommandSettings> GetEffectiveSettingsAsync(
        Guid serverId,
        string commandName,
        bool isMutating,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return EffectiveChatCommandSettings.Disabled("unknown");
        }

        var normalizedCommand = commandName.Trim();
        var cacheKey = $"chat-command-settings:{serverId}";

        if (_memoryCache.TryGetValue(cacheKey, out CachedDocuments? cached) && cached is not null)
        {
            return BuildEffective(cached, normalizedCommand, isMutating);
        }

        try
        {
            var loaded = await LoadDocumentsAsync(serverId, ct).ConfigureAwait(false);
            _memoryCache.Set(cacheKey, loaded, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = loaded.ValidationFailed ? InvalidCacheDuration : CacheDuration
            });

            return BuildEffective(loaded, normalizedCommand, isMutating);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve chat command settings for server {ServerId}", serverId);
            return EffectiveChatCommandSettings.Disabled(normalizedCommand);
        }
    }

    private EffectiveChatCommandSettings BuildEffective(CachedDocuments cached, string commandName, bool isMutating)
    {
        if (cached.ValidationFailed)
        {
            _logger.LogWarning(
                "Chat command settings validation failed for server {ServerId}. Commands fail closed until corrected.",
                cached.ServerId);

            return EffectiveChatCommandSettings.Disabled(commandName);
        }

        return _merger.Merge(commandName, isMutating, cached.GlobalDocument, cached.ServerDocument);
    }

    private async Task<CachedDocuments> LoadDocumentsAsync(Guid serverId, CancellationToken ct)
    {
        var globalConfig = await GetGlobalChatCommandsConfigAsync(ct).ConfigureAwait(false);
        var serverConfig = await GetServerChatCommandsConfigAsync(serverId, ct).ConfigureAwait(false);

        var globalDocument = Deserialize(globalConfig, "global", serverId);
        var serverDocument = Deserialize(serverConfig, "server", serverId);

        var globalValidation = _validator.Validate(globalDocument);
        var serverValidation = _validator.Validate(serverDocument);
        var validationFailed = !globalValidation.IsValid || !serverValidation.IsValid;

        if (!globalValidation.IsValid)
        {
            _logger.LogWarning(
                "Invalid global chat command settings for server {ServerId}: {Errors}",
                serverId,
                string.Join("; ", globalValidation.Errors));
        }

        if (!serverValidation.IsValid)
        {
            _logger.LogWarning(
                "Invalid server chat command settings for server {ServerId}: {Errors}",
                serverId,
                string.Join("; ", serverValidation.Errors));
        }

        return new CachedDocuments(serverId, globalDocument, serverDocument, validationFailed);
    }

    private async Task<ConfigurationDto?> GetGlobalChatCommandsConfigAsync(CancellationToken ct)
    {
        var globalConfigs = await _repositoryClient.GlobalConfigurations.V1
            .GetConfigurations(ct)
            .ConfigureAwait(false);

        if (!globalConfigs.IsSuccess)
        {
            throw new InvalidOperationException("Failed to fetch global chat command settings.");
        }

        if (globalConfigs.Result?.Data?.Items is null)
        {
            return null;
        }

        return globalConfigs.Result.Data.Items.FirstOrDefault(x =>
            string.Equals(x.Namespace, ChatCommandSettingsConstants.Namespace, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ConfigurationDto?> GetServerChatCommandsConfigAsync(Guid serverId, CancellationToken ct)
    {
        var serverConfigs = await _repositoryClient.GameServerConfigurations.V1
            .GetConfigurations(serverId, ct)
            .ConfigureAwait(false);

        if (!serverConfigs.IsSuccess)
        {
            throw new InvalidOperationException($"Failed to fetch server chat command settings for server '{serverId}'.");
        }

        if (serverConfigs.Result?.Data?.Items is null)
        {
            return null;
        }

        return serverConfigs.Result.Data.Items.FirstOrDefault(x =>
            string.Equals(x.Namespace, ChatCommandSettingsConstants.Namespace, StringComparison.OrdinalIgnoreCase));
    }

    private ChatCommandSettingsDocument? Deserialize(ConfigurationDto? config, string scope, Guid serverId)
    {
        if (string.IsNullOrWhiteSpace(config?.Configuration))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<ChatCommandSettingsDocument>(config.Configuration, JsonOptions);
            return document;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse {Scope} chat command settings for server {ServerId}", scope, serverId);
            return new ChatCommandSettingsDocument { SchemaVersion = -1 };
        }
    }

    private sealed record CachedDocuments(
        Guid ServerId,
        ChatCommandSettingsDocument? GlobalDocument,
        ChatCommandSettingsDocument? ServerDocument,
        bool ValidationFailed);
}
