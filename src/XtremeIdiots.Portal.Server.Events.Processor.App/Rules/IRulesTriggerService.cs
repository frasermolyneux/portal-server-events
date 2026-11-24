using MX.GeoLocation.Abstractions.Models.V1_1;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

public sealed record PreparedRulesTrigger(RuleTriggerEnvelope Trigger, RuleFactSnapshotDto Facts);

public sealed record RulesProcessingSummary(bool SuppressWelcome = false);

public interface IRulesTriggerService
{
    Task<PreparedRulesTrigger?> PrepareAsync(PlayerConnectedEvent source, CancellationToken ct);
    Task<PreparedRulesTrigger?> PrepareAsync(PlayerIpResolvedEvent source, CancellationToken ct);
    Task<RulesProcessingSummary> ProcessAsync(PreparedRulesTrigger? prepared, IpIntelligenceDto? intelligence, CancellationToken ct);
}
