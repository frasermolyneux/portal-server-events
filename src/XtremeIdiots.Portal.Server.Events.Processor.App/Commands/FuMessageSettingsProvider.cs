using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class FuMessageSettingsProvider(
    IRepositoryApiClient repositoryApiClient,
    IMemoryCache memoryCache,
    ILogger<FuMessageSettingsProvider> logger) : IFuMessageSettingsProvider
{
    private const string FunnyMessagesNamespace = "funnyMessages";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<string>> GetEffectiveMessagesAsync(Guid serverId, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(serverId, ct).ConfigureAwait(false);
        return settings.EffectiveMessages;
    }

    public async Task<bool> IsEnabledAsync(Guid serverId, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(serverId, ct).ConfigureAwait(false);
        return settings.IsEnabled;
    }

    private async Task<FuMessageSettings> GetSettingsAsync(Guid serverId, CancellationToken ct)
    {
        var cacheKey = $"fu-message-settings:{serverId}";

        if (memoryCache.TryGetValue(cacheKey, out FuMessageSettings? cached) && cached is not null)
        {
            return cached;
        }

        FuMessageSettings resolvedSettings;

        try
        {
            var globalConfig = await GetGlobalFunnyMessagesConfigAsync(ct).ConfigureAwait(false);
            var serverConfig = await GetServerFunnyMessagesConfigAsync(serverId, ct).ConfigureAwait(false);

            var globalMessages = ParseEnabledMessages(globalConfig);
            var serverMessages = ParseEnabledMessages(serverConfig);

            resolvedSettings = BuildEffectiveSettings(globalMessages, serverMessages);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve fu message settings for server {ServerId}", serverId);
            return FuMessageSettings.Disabled;
        }

        memoryCache.Set(cacheKey, resolvedSettings, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration
        });

        return resolvedSettings;
    }

    private async Task<ConfigurationDto?> GetGlobalFunnyMessagesConfigAsync(CancellationToken ct)
    {
        var globalConfigs = await repositoryApiClient.GlobalConfigurations.V1
            .GetConfigurations(ct)
            .ConfigureAwait(false);

        if (!globalConfigs.IsSuccess || globalConfigs.Result?.Data?.Items is null)
        {
            return null;
        }

        return globalConfigs.Result.Data.Items.FirstOrDefault(x =>
            string.Equals(x.Namespace, FunnyMessagesNamespace, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ConfigurationDto?> GetServerFunnyMessagesConfigAsync(Guid serverId, CancellationToken ct)
    {
        var serverConfigs = await repositoryApiClient.GameServerConfigurations.V1
            .GetConfigurations(serverId, ct)
            .ConfigureAwait(false);

        if (!serverConfigs.IsSuccess || serverConfigs.Result?.Data?.Items is null)
        {
            return null;
        }

        return serverConfigs.Result.Data.Items.FirstOrDefault(x =>
            string.Equals(x.Namespace, FunnyMessagesNamespace, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ParseEnabledMessages(ConfigurationDto? config)
    {
        if (string.IsNullOrWhiteSpace(config?.Configuration))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(config.Configuration);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("messages", out var messagesElement) ||
                messagesElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var messages = new List<string>();
            foreach (var item in messagesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("message", out var messageElement) ||
                    messageElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var isEnabled = true;
                if (item.TryGetProperty("enabled", out var enabledElement))
                {
                    if (enabledElement.ValueKind != JsonValueKind.True && enabledElement.ValueKind != JsonValueKind.False)
                    {
                        continue;
                    }

                    isEnabled = enabledElement.GetBoolean();
                }

                if (!isEnabled)
                {
                    continue;
                }

                var template = messageElement.GetString();
                if (!string.IsNullOrWhiteSpace(template))
                {
                    messages.Add(template);
                }
            }

            return messages;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static FuMessageSettings BuildEffectiveSettings(IReadOnlyList<string> globalMessages, IReadOnlyList<string> serverMessages)
    {
        if (globalMessages.Count == 0)
        {
            return FuMessageSettings.Disabled;
        }

        if (serverMessages.Count > 0)
        {
            return new FuMessageSettings(serverMessages);
        }

        return new FuMessageSettings(globalMessages);
    }

    private sealed record FuMessageSettings(IReadOnlyList<string> EffectiveMessages)
    {
        public bool IsEnabled => EffectiveMessages.Count > 0;

        public static FuMessageSettings Disabled { get; } = new([]);
    }
}
