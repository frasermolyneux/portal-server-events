using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.SiegeMode;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal sealed class RepositorySiegeActivationHistoryProvider(IRepositoryApiClient repository) : ISiegeActivationHistoryProvider
{
    public async Task<ActivationIdentifierHistory> GetAsync(
        SiegeSessionDto session, RuleTriggerEnvelope trigger, CancellationToken ct)
    {
        var facts = FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.CreateFactSnapshot(
            trigger.GameServerId, new RuleFactSnapshotRequestDto
            {
                ObservationId = trigger.ObservationId,
                ConnectionId = trigger.ConnectionId,
                SiegeSessionId = session.SessionId,
                GameType = trigger.GameType,
                PlayerGuid = trigger.PlayerGuid,
                PlayerIpAddress = trigger.PlayerIpAddress,
                ObservedAtUtc = trigger.OccurredAtUtc
            }, ct).ConfigureAwait(false), "capture timestamped Siege activation history");
        var startedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(session.StartedAtUtc, DateTimeKind.Utc));
        if (session.GameServerId != trigger.GameServerId || session.GameType != (int)trigger.GameType ||
            facts.SnapshotId == Guid.Empty || facts.SiegeSessionId != session.SessionId ||
            facts.GameServerId != trigger.GameServerId || facts.ConnectionId != trigger.ConnectionId ||
            facts.GameType != trigger.GameType || facts.PlayerGuid != trigger.PlayerGuid ||
            trigger.PlayerIpAddress is not null && facts.PlayerIpAddress != trigger.PlayerIpAddress ||
            facts.GuidBaselineFirstSeenAtUtc > startedAtUtc || facts.IpBaselineFirstSeenAtUtc > startedAtUtc)
        {
            throw new InvalidOperationException("Siege activation history does not match the session and connection observation.");
        }

        return new(
            new(facts.GuidBaselineAvailable, facts.GuidBaselineFirstSeenAtUtc),
            new(facts.IpBaselineAvailable, facts.IpBaselineFirstSeenAtUtc));
    }
}
