using System.Text.Json;

using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public abstract class MapVoteCommandBase : IChatCommand
{
    private const string AgentNamespace = "agent";
    private const string AgentNameKey = "agentName";
    private const string DefaultAgentNamePrefix = "^4[^1>XI< BOT^4]^7";

    private readonly IRepositoryApiClient _repositoryClient;
    private readonly IServersApiClient _serversClient;
    private readonly ICommandSafetyService _commandSafetyService;
    private readonly IRconResponseService _rconService;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger _logger;

    protected MapVoteCommandBase(
        IRepositoryApiClient repositoryClient,
        IServersApiClient serversClient,
        ICommandSafetyService commandSafetyService,
        IRconResponseService rconService,
        IAuditLogger auditLogger,
        ILogger logger)
    {
        _repositoryClient = repositoryClient;
        _serversClient = serversClient;
        _commandSafetyService = commandSafetyService;
        _rconService = rconService;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public abstract string Prefix { get; }
    protected abstract bool IsLike { get; }
    protected abstract string FormatRconMessage(string username);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        if (context.PlayerId is null)
            return CommandResult.Failed("Player not found");

        var mapResult = await _serversClient.Rcon.V1.GetCurrentMap(context.ServerId);

        if (!mapResult.IsSuccess || mapResult.Result?.Data is null)
            return CommandResult.Failed("Could not fetch current map from server");

        var currentMap = mapResult.Result.Data.MapName;

        if (string.IsNullOrEmpty(currentMap))
            return CommandResult.Failed("Current map unknown");

        var mapValidation = await _commandSafetyService
            .ValidateMapTargetAsync(context.ServerId, currentMap, ct)
            .ConfigureAwait(false);

        if (!mapValidation.IsValid)
        {
            if (mapValidation.IsLiveMapListMismatch)
            {
                _logger.LogWarning(
                    "Current map {MapName} was not present in the live server map list for server {ServerId}; proceeding with vote.",
                    currentMap,
                    context.ServerId);
            }
            else
            {
                return CommandResult.Failed(mapValidation.Reason ?? "Map validation failed");
            }
        }

        if (!Enum.TryParse<GameType>(context.GameType, out var gameType))
            return CommandResult.Failed("Invalid game type");

        var repoMapResult = await _repositoryClient.Maps.V1.GetMap(gameType, currentMap, ct);

        if (!repoMapResult.IsSuccess || repoMapResult.Result?.Data is null)
        {
            _logger.LogWarning("Map {MapName} not found for {GameType}", currentMap, context.GameType);
            return CommandResult.Failed("Map not found");
        }

        var mapId = repoMapResult.Result.Data.MapId;

        await _repositoryClient.Maps.V1.UpsertMapVote(
            new UpsertMapVoteDto(mapId, context.PlayerId.Value, context.ServerId, like: IsLike), ct);

        _auditLogger.LogAudit(AuditEvent.ServerAction("MapVoteRecorded", AuditAction.Create)
            .WithGameContext(context.GameType, context.ServerId)
            .WithPlayer(context.PlayerGuid, context.Username)
            .WithSource("MapVoteCommand")
            .WithProperty("MapName", currentMap)
            .WithProperty("VoteType", IsLike ? "Like" : "Dislike")
            .Build());

        var agentNamePrefix = await ResolveAgentNamePrefixAsync(context.ServerId, ct).ConfigureAwait(false);
        var responseMessage = BuildPrefixedMessage(agentNamePrefix, FormatRconMessage(context.Username));

        await _rconService.TrySayAsync(
            context.ServerId,
            responseMessage,
            context.EventGeneratedUtc,
            ct);

        return CommandResult.Ok();
    }

    private async Task<string> ResolveAgentNamePrefixAsync(Guid serverId, CancellationToken ct)
    {
        var globalPrefix = DefaultAgentNamePrefix;

        try
        {
            var globalConfigs = await _repositoryClient.GlobalConfigurations.V1
                .GetConfigurations(ct)
                .ConfigureAwait(false);

            var globalAgentConfig = globalConfigs.Result?.Data?.Items?
                .FirstOrDefault(x => string.Equals(x.Namespace, AgentNamespace, StringComparison.OrdinalIgnoreCase));

            if (TryReadAgentName(globalAgentConfig, out var parsedGlobalPrefix))
            {
                globalPrefix = parsedGlobalPrefix;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to resolve global agent prefix for server {ServerId}; using default", serverId);
        }

        try
        {
            var serverConfigs = await _repositoryClient.GameServerConfigurations.V1
                .GetConfigurations(serverId, ct)
                .ConfigureAwait(false);

            var serverAgentConfig = serverConfigs.Result?.Data?.Items?
                .FirstOrDefault(x => string.Equals(x.Namespace, AgentNamespace, StringComparison.OrdinalIgnoreCase));

            if (TryReadAgentName(serverAgentConfig, out var parsedServerPrefix))
            {
                return parsedServerPrefix;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to resolve server agent prefix for server {ServerId}; using global/default", serverId);
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
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(AgentNameKey, out var agentNameProperty) &&
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

    private static string BuildPrefixedMessage(string agentNamePrefix, string message)
    {
        var trimmedPrefix = agentNamePrefix?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedPrefix))
        {
            return message;
        }

        return $"{trimmedPrefix} {message}";
    }
}
