using Microsoft.Extensions.Logging;

using MX.GeoLocation.Abstractions.Models.V1_1;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

/// <summary>
/// Best-effort near-live convergence for the VPN evidence tag.
/// Removal is handled by the periodic reconciliation flow after it evaluates all recent IPs.
/// </summary>
public sealed class VpnDetectedTagService(
    IRepositoryApiClient repositoryApiClient,
    ILogger<VpnDetectedTagService> logger) : IVpnDetectedTagService
{
    public async Task AddIfDetectedAsync(Guid playerId, IpIntelligenceDto? intelligence, CancellationToken ct = default)
    {
        if (playerId == Guid.Empty || intelligence?.ProxyCheck?.IsVpn != true)
        {
            return;
        }

        try
        {
            var result = await repositoryApiClient.Players.V1
                .SetVpnDetectedTag(playerId, new SetVpnDetectedTagDto(true))
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                logger.LogWarning(
                    "Failed to add vpn-detected tag for player {PlayerId}. Status: {StatusCode}",
                    playerId,
                    result.StatusCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to add vpn-detected tag for player {PlayerId}", playerId);
        }
    }
}