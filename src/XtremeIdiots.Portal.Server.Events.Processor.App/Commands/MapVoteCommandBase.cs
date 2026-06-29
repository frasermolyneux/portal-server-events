using System.Net;

using Microsoft.Extensions.Logging;

using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public abstract class MapVoteCommandBase : IChatCommand
{
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
        {
            return CommandResult.Failed("Player not found");
        }

        if (!Enum.TryParse<GameType>(context.GameType, out var gameType))
        {
            return CommandResult.Failed("Invalid game type");
        }

        var mapResult = await GetCurrentMapAsync(context.ServerId, gameType, ct).ConfigureAwait(false);

        if (!mapResult.IsSuccess || mapResult.Result?.Data is null)
        {
            return CommandResult.Failed("Could not fetch current map from server");
        }

        var currentMap = mapResult.Result.Data.MapName;

        if (string.IsNullOrEmpty(currentMap))
        {
            return CommandResult.Failed("Current map unknown");
        }

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

        var repoMapResult = await _repositoryClient.Maps.V1.GetMap(gameType, currentMap, ct);

        if (repoMapResult is null || !repoMapResult.IsSuccess || repoMapResult.Result?.Data is null)
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

        var agentNamePrefix = await AgentNamePrefixResolver.ResolveAsync(_repositoryClient, _logger, context.ServerId, ct).ConfigureAwait(false);
        var responseMessage = BuildPrefixedMessage(agentNamePrefix, FormatRconMessage(context.Username));

        await _rconService.TrySayAsync(
            context.ServerId,
            responseMessage,
            context.EventGeneratedUtc,
            ct);

        return CommandResult.Ok();
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

    private Task<ApiResult<RconCurrentMapDto>> GetCurrentMapAsync(Guid serverId, GameType gameType, CancellationToken ct)
    {
        return gameType switch
        {
            GameType.CallOfDuty2 => _serversClient.Cod2Rcon.V1.GetCurrentMap(serverId, ct),
            GameType.CallOfDuty4 => _serversClient.Cod4Rcon.V1.GetCurrentMap(serverId, ct),
            GameType.CallOfDuty5 => _serversClient.Cod5Rcon.V1.GetCurrentMap(serverId, ct),
            GameType.CallOfDuty4x => GetCoD4xCurrentMapAsync(serverId, ct),
            GameType.Insurgency => _serversClient.InsurgencyRcon.V1.GetCurrentMap(serverId, ct),
            GameType.Rust => _serversClient.RustRcon.V1.GetCurrentMap(serverId, ct),
            GameType.Left4Dead2 => _serversClient.L4d2Rcon.V1.GetCurrentMap(serverId, ct),
            _ => Task.FromResult(new ApiResult<RconCurrentMapDto>(
                HttpStatusCode.BadRequest,
                new ApiResponse<RconCurrentMapDto>(new ApiError("UNSUPPORTED_GAME", $"Unsupported game type: {gameType}"))))
        };
    }

    private async Task<ApiResult<RconCurrentMapDto>> GetCoD4xCurrentMapAsync(Guid serverId, CancellationToken ct)
    {
        var statusResult = await _serversClient.CoD4xRcon.V1.Status(serverId, ct).ConfigureAwait(false);
        if (statusResult.IsSuccess && !string.IsNullOrWhiteSpace(statusResult.Result?.Data?.MapName))
        {
            return new ApiResult<RconCurrentMapDto>(
                HttpStatusCode.OK,
                new ApiResponse<RconCurrentMapDto>(new RconCurrentMapDto(statusResult.Result.Data.MapName!)));
        }

        return new ApiResult<RconCurrentMapDto>(
            HttpStatusCode.BadGateway,
            new ApiResponse<RconCurrentMapDto>(new ApiError("COD4X_STATUS_UNAVAILABLE", "Unable to resolve current map from CoD4x status.")));
    }
}
