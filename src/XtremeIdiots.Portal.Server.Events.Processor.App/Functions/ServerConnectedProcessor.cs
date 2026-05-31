using System.Reflection;
using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public class ServerConnectedProcessor(
    ILogger<ServerConnectedProcessor> logger,
    IRepositoryApiClient repositoryApiClient,
    IAuditLogger auditLogger,
    IRconResponseService rconResponseService)
{
    private const string AgentNamespace = "agent";
    private const string AgentNameKey = "agentName";
    private const string DefaultAgentNamePrefix = "^4[^1>XI< BOT^4]^7";

    [Function(nameof(ProcessServerConnected))]
    public async Task ProcessServerConnected(
        [ServiceBusTrigger(Queues.ServerConnected, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        ServerConnectedEvent? serverEvent;
        try
        {
            serverEvent = JsonSerializer.Deserialize<ServerConnectedEvent>(message.Body, JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "ServerConnected message was not in expected format. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (serverEvent is null)
        {
            logger.LogWarning("ServerConnected deserialized to null. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (serverEvent.ServerId == Guid.Empty)
        {
            logger.LogWarning("ServerConnected has empty ServerId. MessageId: {MessageId}", message.MessageId);
            return;
        }

        if (string.IsNullOrWhiteSpace(serverEvent.GameType))
        {
            logger.LogWarning("ServerConnected has empty GameType. MessageId: {MessageId}", message.MessageId);
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["GameType"] = serverEvent.GameType,
            ["ServerId"] = serverEvent.ServerId
        });

        var eventData = JsonSerializer.Serialize(serverEvent, JsonOptions.Default);

        var gameServerEventDto = new CreateGameServerEventDto(
            serverEvent.ServerId,
            "OnServerConnected",
            eventData);

        await repositoryApiClient.GameServersEvents.V1
            .CreateGameServerEvent(gameServerEventDto)
            .ConfigureAwait(false);

        var prefix = await ResolveAgentNamePrefixAsync(serverEvent.ServerId, context.CancellationToken).ConfigureAwait(false);
        var version = typeof(ServerConnectedProcessor).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? typeof(ServerConnectedProcessor).Assembly.GetName().Version?.ToString()
            ?? "unknown";

        var startupMessage = BuildPrefixedMessage(prefix, $"Server Events is now online (version {version})");
        await rconResponseService.TrySayAsync(serverEvent.ServerId, startupMessage, DateTime.UtcNow, context.CancellationToken)
            .ConfigureAwait(false);

        auditLogger.LogAudit(AuditEvent.ServerAction("ServerConnected", AuditAction.Connect)
            .WithGameContext(serverEvent.GameType, serverEvent.ServerId)
            .Build());
    }

    private async Task<string> ResolveAgentNamePrefixAsync(Guid serverId, CancellationToken ct)
    {
        var globalPrefix = DefaultAgentNamePrefix;

        try
        {
            var globalConfigsResult = await repositoryApiClient.GlobalConfigurations.V1
                .GetConfigurations(ct)
                .ConfigureAwait(false);

            var globalAgentConfig = globalConfigsResult.Result?.Data?.Items?
                .FirstOrDefault(x => string.Equals(x.Namespace, AgentNamespace, StringComparison.OrdinalIgnoreCase));

            if (TryReadAgentName(globalAgentConfig, out var parsedGlobalPrefix))
            {
                globalPrefix = parsedGlobalPrefix;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve global agent prefix for server {ServerId}; using default", serverId);
        }

        try
        {
            var serverConfigsResult = await repositoryApiClient.GameServerConfigurations.V1
                .GetConfigurations(serverId, ct)
                .ConfigureAwait(false);

            var serverAgentConfig = serverConfigsResult.Result?.Data?.Items?
                .FirstOrDefault(x => string.Equals(x.Namespace, AgentNamespace, StringComparison.OrdinalIgnoreCase));

            if (TryReadAgentName(serverAgentConfig, out var parsedServerPrefix))
            {
                return parsedServerPrefix;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve server agent prefix for server {ServerId}; using global/default", serverId);
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
            using var document = JsonDocument.Parse(config.Configuration);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(AgentNameKey, out var agentNameProperty) &&
                agentNameProperty.ValueKind == JsonValueKind.String)
            {
                var parsed = agentNameProperty.GetString();
                if (!string.IsNullOrWhiteSpace(parsed))
                {
                    agentName = parsed;
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static string BuildPrefixedMessage(string prefix, string message)
    {
        var trimmedPrefix = prefix?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(trimmedPrefix)
            ? message
            : $"{trimmedPrefix} {message}";
    }
}
