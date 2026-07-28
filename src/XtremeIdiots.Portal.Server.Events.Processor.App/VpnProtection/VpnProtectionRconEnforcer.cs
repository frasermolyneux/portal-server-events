using Microsoft.Extensions.Logging;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public sealed class VpnProtectionRconEnforcer(
    IServersApiClient serversApiClient,
    ILogger<VpnProtectionRconEnforcer> logger) : IVpnProtectionRconEnforcer
{
    public async Task<VpnProtectionRconOutcome> EnforceAsync(
        VpnProtectionContext context,
        VpnProtectionAction action,
        string reason,
        CancellationToken ct = default)
    {
        if (action == VpnProtectionAction.Observation)
        {
            return VpnProtectionRconOutcome.NotRequired;
        }

        if (action is not (VpnProtectionAction.Kick or VpnProtectionAction.Ban))
        {
            return VpnProtectionRconOutcome.Failed;
        }

        try
        {
            return context.GameType switch
            {
                GameType.CallOfDuty2 => await EnforceCod2Async(context, action, ct).ConfigureAwait(false),
                GameType.CallOfDuty4 => await EnforceCod4Async(context, action, ct).ConfigureAwait(false),
                GameType.CallOfDuty5 => await EnforceCod5Async(context, action, ct).ConfigureAwait(false),
                GameType.CallOfDuty4x => await EnforceCod4xAsync(context, action, reason, ct).ConfigureAwait(false),
                _ => VpnProtectionRconOutcome.UnsupportedGame
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "VPN Protection RCON {Action} failed for player {PlayerGuid} on server {ServerId}",
                action,
                context.PlayerGuid,
                context.ServerId);
            return VpnProtectionRconOutcome.Failed;
        }
    }

    private async Task<VpnProtectionRconOutcome> EnforceCod2Async(
        VpnProtectionContext context,
        VpnProtectionAction action,
        CancellationToken ct)
    {
        var status = await serversApiClient.Cod2Rcon.V1.Status(context.ServerId, ct).ConfigureAwait(false);
        var player = status.Result?.Data?.Players.FirstOrDefault(candidate => IsExpectedPlayer(candidate, context));
        if (!status.IsSuccess || player is null)
        {
            return VpnProtectionRconOutcome.PlayerNotConnected;
        }

        var request = new ClientSlotRequest { ClientId = player.Num };
        var result = action == VpnProtectionAction.Ban
            ? await serversApiClient.Cod2Rcon.V1.Ban(context.ServerId, request, ct).ConfigureAwait(false)
            : await serversApiClient.Cod2Rcon.V1.Kick(context.ServerId, request, ct).ConfigureAwait(false);
        return ToOutcome(result);
    }

    private async Task<VpnProtectionRconOutcome> EnforceCod4Async(
        VpnProtectionContext context,
        VpnProtectionAction action,
        CancellationToken ct)
    {
        var status = await serversApiClient.Cod4Rcon.V1.Status(context.ServerId, ct).ConfigureAwait(false);
        var player = status.Result?.Data?.Players.FirstOrDefault(candidate => IsExpectedPlayer(candidate, context));
        if (!status.IsSuccess || player is null)
        {
            return VpnProtectionRconOutcome.PlayerNotConnected;
        }

        var request = new ClientSlotRequest { ClientId = player.Num };
        var result = action == VpnProtectionAction.Ban
            ? await serversApiClient.Cod4Rcon.V1.Ban(context.ServerId, request, ct).ConfigureAwait(false)
            : await serversApiClient.Cod4Rcon.V1.Kick(context.ServerId, request, ct).ConfigureAwait(false);
        return ToOutcome(result);
    }

    private async Task<VpnProtectionRconOutcome> EnforceCod5Async(
        VpnProtectionContext context,
        VpnProtectionAction action,
        CancellationToken ct)
    {
        var status = await serversApiClient.Cod5Rcon.V1.Status(context.ServerId, ct).ConfigureAwait(false);
        var player = status.Result?.Data?.Players.FirstOrDefault(candidate => IsExpectedPlayer(candidate, context));
        if (!status.IsSuccess || player is null)
        {
            return VpnProtectionRconOutcome.PlayerNotConnected;
        }

        var request = new ClientSlotRequest { ClientId = player.Num };
        var result = action == VpnProtectionAction.Ban
            ? await serversApiClient.Cod5Rcon.V1.Ban(context.ServerId, request, ct).ConfigureAwait(false)
            : await serversApiClient.Cod5Rcon.V1.Kick(context.ServerId, request, ct).ConfigureAwait(false);
        return ToOutcome(result);
    }

    private async Task<VpnProtectionRconOutcome> EnforceCod4xAsync(
        VpnProtectionContext context,
        VpnProtectionAction action,
        string reason,
        CancellationToken ct)
    {
        var status = await serversApiClient.CoD4xRcon.V1.Status(context.ServerId, ct).ConfigureAwait(false);
        var player = status.Result?.Data?.Players.FirstOrDefault(candidate =>
            string.Equals(candidate.PlayerIdentifier, context.PlayerGuid, StringComparison.OrdinalIgnoreCase) &&
            (!context.SlotId.HasValue || candidate.Num == context.SlotId.Value));
        if (!status.IsSuccess || player is null)
        {
            return VpnProtectionRconOutcome.PlayerNotConnected;
        }

        var request = new CoD4xClientReasonRequestDto
        {
            ClientId = player.Num,
            Reason = reason
        };
        var result = action == VpnProtectionAction.Ban
            ? await serversApiClient.CoD4xRcon.V1.BanClient(context.ServerId, request, ct).ConfigureAwait(false)
            : await serversApiClient.CoD4xRcon.V1.OnlyKick(context.ServerId, request, ct).ConfigureAwait(false);
        return ToOutcome(result);
    }

    private static bool IsExpectedPlayer(RconStatusPlayerDto player, VpnProtectionContext context)
    {
        return string.Equals(player.Guid, context.PlayerGuid, StringComparison.OrdinalIgnoreCase) &&
            (!context.SlotId.HasValue || player.Num == context.SlotId.Value);
    }

    private static VpnProtectionRconOutcome ToOutcome(ApiResult result) =>
        result.IsSuccess ? VpnProtectionRconOutcome.Succeeded : VpnProtectionRconOutcome.Failed;
}
