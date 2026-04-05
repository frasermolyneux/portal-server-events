using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public abstract class MapVoteCommandBase : IChatCommand
{
    private readonly IRepositoryApiClient _repositoryClient;
    private readonly IServersApiClient _serversClient;
    private readonly IRconResponseService _rconService;
    private readonly ILogger _logger;

    protected MapVoteCommandBase(
        IRepositoryApiClient repositoryClient,
        IServersApiClient serversClient,
        IRconResponseService rconService,
        ILogger logger)
    {
        _repositoryClient = repositoryClient;
        _serversClient = serversClient;
        _rconService = rconService;
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

        await _rconService.TrySayAsync(
            context.ServerId,
            FormatRconMessage(context.Username),
            context.EventGeneratedUtc,
            ct);

        return CommandResult.Ok();
    }
}
