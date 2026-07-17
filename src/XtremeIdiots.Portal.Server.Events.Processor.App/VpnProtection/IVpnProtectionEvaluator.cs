using MX.GeoLocation.Abstractions.Models.V1_1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public interface IVpnProtectionEvaluator
{
    VpnProtectionDecision Evaluate(
        EffectiveVpnProtectionSettings settings,
        IpIntelligenceDto intelligence);
}