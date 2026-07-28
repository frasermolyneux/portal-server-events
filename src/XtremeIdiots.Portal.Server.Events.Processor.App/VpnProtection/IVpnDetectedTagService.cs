using MX.GeoLocation.Abstractions.Models.V1_1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

/// <summary>
/// Adds the system-managed <c>vpn-detected</c> tag when GeoLocation confirms VPN use.
/// </summary>
public interface IVpnDetectedTagService
{
    Task AddIfDetectedAsync(Guid playerId, IpIntelligenceDto? intelligence, CancellationToken ct = default);
}
