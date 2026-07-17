using MX.GeoLocation.Abstractions.Models.V1_1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public interface IVpnProtectionService
{
    Task<VpnProtectionProcessingResult> ProcessAsync(
        VpnProtectionContext context,
        IpIntelligenceDto intelligence,
        CancellationToken ct = default);
}

public sealed record VpnProtectionProcessingResult
{
    public bool WasExcluded { get; init; }

    public bool AdminActionCreated { get; init; }

    public VpnProtectionDecision Decision { get; init; } = VpnProtectionDecision.NoMatch;

    public VpnProtectionRconOutcome RconOutcome { get; init; } = VpnProtectionRconOutcome.NotRequired;
}