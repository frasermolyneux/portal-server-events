using System.Text.Json;

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
        CancellationToken ct)
    {
        var globalPrefix = DefaultAgentNamePrefix;

        try
        {
            var globalConfigs = await repositoryClient.GlobalConfigurations.V1
                .GetConfigurations(ct)
                .ConfigureAwait(false);

            var globalAgentConfig = globalConfigs.Result?.Data?.Items?
                .FirstOrDefault(x => string.Equals(x.Namespace, AgentSettingsConstants.Namespace, StringComparison.OrdinalIgnoreCase));

            if (TryReadAgentName(globalAgentConfig, out var parsedGlobalPrefix))
            {
                globalPrefix = parsedGlobalPrefix;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to resolve global agent prefix for server {ServerId}; using default", serverId);
        }

        try
        {
            var serverConfigs = await repositoryClient.GameServerConfigurations.V1
                .GetConfigurations(serverId, ct)
                .ConfigureAwait(false);

            var serverAgentConfig = serverConfigs.Result?.Data?.Items?
                .FirstOrDefault(x => string.Equals(x.Namespace, AgentSettingsConstants.Namespace, StringComparison.OrdinalIgnoreCase));

            if (TryReadAgentName(serverAgentConfig, out var parsedServerPrefix))
            {
                return parsedServerPrefix;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to resolve server agent prefix for server {ServerId}; using global/default", serverId);
        }

        return globalPrefix;
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
