using MX.GeoLocation.Abstractions.Models.V1_1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public interface IVpnProtectionEvaluator
{
    VpnProtectionDecision Evaluate(
        EffectiveVpnProtectionSettings settings,
        IpIntelligenceDto intelligence);

    // Tag-aware evaluation: honours the configured excluded player tags before scoring the IP
    // intelligence rules. This is the single shared entry point every enforcement path (event
    // processors and the CoD4x plugin endpoint) must use so the exemption cannot be bypassed.
    VpnProtectionDecision Evaluate(
        EffectiveVpnProtectionSettings settings,
        IReadOnlyCollection<string> playerTags,
        IpIntelligenceDto intelligence);
}