using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Agent;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

internal static class AgentNamePrefixResolver
{
    private const string DefaultAgentNamePrefix = "^4[^1>XI< BOT^4]^7";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<string> ResolveAsync(
        IRepositoryApiClient repositoryClient,
        ILogger logger,
        Guid serverId,
        CancellationToken ct,
        IMemoryCache? cache = null)
    {
        var cacheKey = $"agent-name-prefix:{serverId}";
        if (cache?.TryGetValue(cacheKey, out string? cached) == true && cached is not null)
        {
            return cached;
        }

        async Task<(bool Success, string? Prefix)> ReadGlobalAsync()
        {
            try
            {
                var result = await repositoryClient.GlobalConfigurations.V1.GetConfigurations(ct).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    logger.LogWarning("Unable to resolve global agent prefix for server {ServerId}: {StatusCode}", serverId, result.StatusCode);
                    return (false, null);
                }
                var config = result.Result?.Data?.Items?.FirstOrDefault(x =>
                    string.Equals(x.Namespace, AgentSettingsConstants.Namespace, StringComparison.OrdinalIgnoreCase));
                var parsed = TryReadAgentName(config, out var prefix);
                return (config is null || parsed, parsed ? prefix : null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Unable to resolve global agent prefix for server {ServerId}; using default", serverId);
                return (false, null);
            }
        }

        async Task<(bool Success, string? Prefix)> ReadServerAsync()
        {
            try
            {
                var result = await repositoryClient.GameServerConfigurations.V1.GetConfigurations(serverId, ct).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    logger.LogWarning("Unable to resolve server agent prefix for server {ServerId}: {StatusCode}", serverId, result.StatusCode);
                    return (false, null);
                }
                var config = result.Result?.Data?.Items?.FirstOrDefault(x =>
                    string.Equals(x.Namespace, AgentSettingsConstants.Namespace, StringComparison.OrdinalIgnoreCase));
                var parsed = TryReadAgentName(config, out var prefix);
                return (config is null || parsed, parsed ? prefix : null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Unable to resolve server agent prefix for server {ServerId}; using global/default", serverId);
                return (false, null);
            }
        }

        var globalTask = ReadGlobalAsync();
        var serverTask = ReadServerAsync();
        await Task.WhenAll(globalTask, serverTask).ConfigureAwait(false);
        var global = await globalTask.ConfigureAwait(false);
        var server = await serverTask.ConfigureAwait(false);
        var resolved = server.Prefix ?? global.Prefix ?? DefaultAgentNamePrefix;
        if (global.Success && server.Success)
        {
            cache?.Set(cacheKey, resolved, TimeSpan.FromSeconds(30));
        }
        return resolved;
    }

    private static bool TryReadAgentName(ConfigurationDto? config, out string agentName)
    {
        agentName = string.Empty;

        if (string.IsNullOrWhiteSpace(config?.Configuration))
        {
            return false;
        }

        try
        {
            var document = JsonSerializer.Deserialize<AgentSettingsDocument>(config.Configuration, JsonOptions);
            if (document is null)
            {
                return false;
            }

            var validation = new AgentSettingsValidator().Validate(document);
            if (!validation.IsValid)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(document.AgentName))
            {
                return false;
            }

            agentName = document.AgentName;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
