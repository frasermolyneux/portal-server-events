using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class CommandSafetyService : ICommandSafetyService
{
    private readonly IServersApiClient _serversApiClient;
    private readonly ILogger<CommandSafetyService> _logger;

    public CommandSafetyService(
        IServersApiClient serversApiClient,
        ILogger<CommandSafetyService> logger)
    {
        _serversApiClient = serversApiClient;
        _logger = logger;
    }

    public async Task<MapValidationResult> ValidateMapTargetAsync(
        Guid serverId,
        string mapName,
        CancellationToken cancellationToken = default)
    {
        var mapsResult = await _serversApiClient.Rcon.V1
            .GetServerMaps(serverId)
            .ConfigureAwait(false);

        if (!mapsResult.IsSuccess || mapsResult.Result?.Data is null)
        {
            return new MapValidationResult(false, "Unable to verify map against live server map list.");
        }

        var mapItems = mapsResult.Result.Data.Items;
        if (mapItems is null)
        {
            return new MapValidationResult(false, "Live server map list was unavailable.");
        }

        var isKnownMap = mapItems.Any(m =>
            string.Equals(m.MapName, mapName, StringComparison.OrdinalIgnoreCase));

        return isKnownMap
            ? new MapValidationResult(true)
            : new MapValidationResult(false, "Map was not found in the live server map list.");
    }

    public async Task<PlayerResolutionResult> ResolvePlayerTargetAsync(
        Guid serverId,
        string query,
        CancellationToken cancellationToken = default)
    {
        var resolveResult = await _serversApiClient.Rcon.V1
            .ResolvePlayer(serverId, new ResolvePlayerRequestDto { PlayerQuery = query }, cancellationToken)
            .ConfigureAwait(false);

        if (!resolveResult.IsSuccess || resolveResult.Result?.Data is null)
        {
            return new PlayerResolutionResult(false, null, "Unable to resolve player on live server state.");
        }

        return new PlayerResolutionResult(true, resolveResult.Result.Data);
    }

    public async Task<PlayerSlotVerificationResult> VerifyPlayerSlotAsync(
        Guid serverId,
        int slotId,
        string expectedPlayerName,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolvePlayerTargetAsync(serverId, expectedPlayerName, cancellationToken)
            .ConfigureAwait(false);

        if (!resolution.Success || resolution.Response?.ResolvedPlayer is null)
        {
            return new PlayerSlotVerificationResult(false, "Unable to verify player slot using live server state.");
        }

        if (resolution.Response.ResolvedPlayer.Slot != slotId)
        {
            return new PlayerSlotVerificationResult(false, "Target player slot no longer matches live server state.");
        }

        return new PlayerSlotVerificationResult(true);
    }

    public async Task<CommandResult> ExecuteVerifiedRconActionAsync(
        Guid serverId,
        int slotId,
        string expectedPlayerName,
        Func<CancellationToken, Task<MX.Api.Abstractions.ApiResult>> action,
        string actionName,
        CancellationToken cancellationToken = default)
    {
        var verification = await VerifyPlayerSlotAsync(serverId, slotId, expectedPlayerName, cancellationToken)
            .ConfigureAwait(false);

        if (!verification.IsValid)
        {
            return CommandResult.Failed(verification.Reason ?? "Player verification failed.");
        }

        var result = await action(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "Verified RCON action {ActionName} failed for server {ServerId}.",
                actionName,
                serverId);

            return CommandResult.Failed("Action failed.");
        }

        return CommandResult.Ok();
    }
}
