using MX.Api.Abstractions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public interface ICommandSafetyService
{
    Task<MapValidationResult> ValidateMapTargetAsync(Guid serverId, string mapName, CancellationToken cancellationToken = default);

    Task<PlayerResolutionResult> ResolvePlayerTargetAsync(Guid serverId, string query, CancellationToken cancellationToken = default);

    Task<PlayerSlotVerificationResult> VerifyPlayerSlotAsync(
        Guid serverId,
        int slotId,
        string expectedPlayerName,
        CancellationToken cancellationToken = default);

    Task<CommandResult> ExecuteVerifiedRconActionAsync(
        Guid serverId,
        int slotId,
        string expectedPlayerName,
        Func<CancellationToken, Task<ApiResult>> action,
        string actionName,
        CancellationToken cancellationToken = default);
}
