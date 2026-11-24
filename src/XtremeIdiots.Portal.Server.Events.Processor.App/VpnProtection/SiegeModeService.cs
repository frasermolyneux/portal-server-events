using System.Text.Json;

using Microsoft.Extensions.Logging;

using MX.Api.Abstractions;
using MX.GeoLocation.Abstractions.Models.V1_1;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.SiegeMode;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.SiegeMode;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.VpnProtection;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public sealed class SiegeModeService(
    IRepositoryApiClient repositoryApiClient,
    IVpnProtectionRconEnforcer rconEnforcer,
    IAuditLogger auditLogger,
    ILogger<SiegeModeService> logger) : ISiegeModeService
{
    private static readonly JsonSerializerOptions SettingsJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<SiegeModeProcessingResult> EvaluateAsync(
        SiegeModeEvaluationContext context,
        CancellationToken ct = default)
    {
        ApiResult<SiegeSessionDto> sessionResult;
        try
        {
            sessionResult = await repositoryApiClient.SiegeMode.V1
                .GetCurrentSession(context.ServerId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get current Siege Mode session for server {ServerId}", context.ServerId);
            return SiegeModeProcessingResult.NoMatch;
        }

        var session = sessionResult.Result?.Data;
        if (!sessionResult.IsSuccess || session is null ||
            session.GameServerId != context.ServerId ||
            session.GameType != (int)context.GameType ||
            session.EndedAtUtc is not null ||
            session.ExpiresAtUtc <= DateTime.UtcNow ||
            session.Mode is not (SiegeSessionMode.Enforce or SiegeSessionMode.DryRun))
        {
            return SiegeModeProcessingResult.NoMatch;
        }

        if (context.ConnectionId is not Guid connectionId || connectionId == Guid.Empty ||
            context.SlotId is not int slotId || slotId < 0 ||
            string.IsNullOrWhiteSpace(context.PlayerGuid))
        {
            logger.LogWarning(
                "Siege Mode evaluation skipped because connection identity is incomplete for server {ServerId}; no action was taken",
                context.ServerId);
            await RecordCheckAsync(session, context, "ConnectionUnknown", "Connection identity is incomplete.", ct).ConfigureAwait(false);
            return SiegeModeProcessingResult.NoMatch;
        }

        if (context.EventGeneratedUtc <= session.StartedAtUtc)
        {
            await RecordCheckAsync(session, context, "BeforeActivation", "Observation predates session activation.", ct).ConfigureAwait(false);
            return SiegeModeProcessingResult.NoMatch;
        }

        SiegeModeSettingsDocument? settings;
        try
        {
            settings = JsonSerializer.Deserialize<SiegeModeSettingsDocument>(session.SettingsJson, SettingsJsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid captured Siege Mode settings for session {SessionId}", session.SessionId);
            await RecordCheckAsync(session, context, "SettingsInvalid", "Captured settings are invalid.", ct).ConfigureAwait(false);
            return SiegeModeProcessingResult.NoMatch;
        }

        var validation = new SiegeModeSettingsValidator().Validate(settings);
        if (!validation.IsValid || settings is null)
        {
            logger.LogWarning(
                "Captured Siege Mode settings failed validation for session {SessionId}: {Errors}",
                session.SessionId,
                string.Join("; ", validation.Errors));
            await RecordCheckAsync(session, context, "SettingsInvalid", "Captured settings failed validation.", ct).ConfigureAwait(false);
            return SiegeModeProcessingResult.NoMatch;
        }

        if (context.PlayerTags is null)
        {
            await RecordCheckAsync(session, context, "PlayerTagsUnknown", "Player tag lookup is unavailable.", ct).ConfigureAwait(false);
        }

        if (context.Intelligence is null)
        {
            await RecordCheckAsync(session, context, "IntelligenceUnknown", "IP intelligence is unavailable.", ct).ConfigureAwait(false);
        }

        var baselineResult = await GetBaselineAsync(session, context, ct).ConfigureAwait(false);
        var baseline = baselineResult?.Result?.Data;
        if (baseline is null)
        {
            logger.LogWarning("Siege Mode baseline is unavailable for session {SessionId}; evaluation fails open", session.SessionId);
            await RecordCheckAsync(session, context, "BaselineUnknown", "Activation baseline is unavailable.", ct).ConfigureAwait(false);
            return SiegeModeProcessingResult.NoMatch;
        }

        var matchedRule = settings.Rules
            .Where(static rule => rule.Enabled)
            .FirstOrDefault(rule => EvaluateCondition(rule.Condition, baseline, context) == true);

        if (matchedRule is null)
        {
            var hasUnknown = settings.Rules
                .Where(static rule => rule.Enabled)
                .Any(rule => EvaluateCondition(rule.Condition, baseline, context) is null);
            if (hasUnknown)
            {
                await RecordCheckAsync(session, context, "RuleUnknown", "One or more Siege conditions are unknown.", ct).ConfigureAwait(false);
            }

            return SiegeModeProcessingResult.NoMatch;
        }

        var reason = $"Siege Mode rule {matchedRule.Id}";
        if (session.Mode == SiegeSessionMode.Enforce && context.SuppressEnforcement)
        {
            await RecordCheckAsync(session, context, "SuppressedByVpnProtection", "Siege enforcement suppressed by a separate VPN action.", ct).ConfigureAwait(false);
            return new SiegeModeProcessingResult { Session = session, Matched = true, Reason = reason };
        }

        if (!context.IsPluginPath && IpAddressGuard.IsPersistable(context.IpAddress))
        {
            var freshness = await GetObservationFreshnessAsync(context, ct).ConfigureAwait(false);
            if (freshness != PlayerIpObservationStatuses.Current)
            {
                await RecordCheckAsync(session, context, FreshnessCheckOutcome(freshness), "IP observation is not confirmed current.", ct).ConfigureAwait(false);
                return new SiegeModeProcessingResult { Session = session, Matched = true, Reason = reason };
            }
        }

        var actionId = Guid.NewGuid();
        ApiResult<SiegeActionClaimDto> claimResult;
        try
        {
            claimResult = await repositoryApiClient.SiegeMode.V1
                .ClaimAction(
                    context.ServerId,
                    session.SessionId,
                    new ClaimSiegeActionDto
                    {
                        ActionId = actionId,
                        ConnectionId = connectionId,
                        PlayerGuid = context.PlayerGuid,
                        SlotId = slotId,
                        RuleId = matchedRule.Id,
                        BaselineRevision = baseline.BaselineRevision
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Siege Mode action claim failed for session {SessionId}", session.SessionId);
            await RecordCheckAsync(session, context, "ClaimUnavailable", "Action claim is unavailable.", ct).ConfigureAwait(false);
            return SiegeModeProcessingResult.NoMatch;
        }

        var claim = claimResult.Result?.Data;
        if (!claimResult.IsSuccess || claim is null)
        {
            var code = claimResult.IsConflict ? "ClaimConflict" : "ClaimUnavailable";
            await RecordCheckAsync(session, context, code, "Action claim was rejected or unavailable.", ct).ConfigureAwait(false);
            return SiegeModeProcessingResult.NoMatch;
        }

        var result = new SiegeModeProcessingResult
        {
            Session = session,
            Action = claim.Action,
            Matched = true,
            ShouldDispatch = claim.ShouldDispatch,
            Reason = reason
        };

        if (!claim.ShouldDispatch || session.Mode != SiegeSessionMode.Enforce)
        {
            if (session.Mode == SiegeSessionMode.DryRun && claim.Action.Outcome == SiegeActionOutcomes.WouldKick)
            {
                auditLogger.LogAudit(AuditEvent.ServerAction("SiegeModeWouldKick", AuditAction.Moderate)
                    .WithGameContext(context.GameType.ToString(), context.ServerId)
                    .WithPlayer(context.PlayerGuid, null)
                    .WithSource(nameof(SiegeModeService))
                    .WithProperty("SessionId", session.SessionId.ToString())
                    .WithProperty("RuleId", matchedRule.Id)
                    .Build());
            }

            return result with { ShouldDispatch = false };
        }

        if (context.IsPluginPath)
        {
            return result;
        }

        if (session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            await UpdateOutcomeAsync(session, context, claim.Action.ActionId, SiegeActionOutcomes.Stale, "Decision expired", ct)
                .ConfigureAwait(false);
            return result with { ShouldDispatch = false };
        }

        if (IpAddressGuard.IsPersistable(context.IpAddress))
        {
            var freshness = await GetObservationFreshnessAsync(context, ct).ConfigureAwait(false);
            if (freshness != PlayerIpObservationStatuses.Current)
            {
                var actionOutcome = freshness == PlayerIpObservationStatuses.NotCurrent
                    ? SiegeActionOutcomes.Stale
                    : SiegeActionOutcomes.Failed;
                await UpdateOutcomeAsync(session, context, claim.Action.ActionId, actionOutcome, "IP observation is not confirmed current", ct)
                    .ConfigureAwait(false);
                await RecordCheckAsync(session, context, FreshnessCheckOutcome(freshness), "IP observation is not confirmed current.", ct).ConfigureAwait(false);
                return result with { ShouldDispatch = false };
            }
        }

        if (session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            await UpdateOutcomeAsync(session, context, claim.Action.ActionId, SiegeActionOutcomes.Stale, "Decision expired", ct)
                .ConfigureAwait(false);
            return result with { ShouldDispatch = false };
        }

        var rconOutcome = await rconEnforcer.EnforceAsync(
            new VpnProtectionContext
            {
                ServerId = context.ServerId,
                GameType = context.GameType,
                PlayerId = Guid.Empty,
                PlayerGuid = context.PlayerGuid,
                Username = context.Username,
                PlayerTags = context.PlayerTags ?? [],
                SlotId = slotId
            },
            VpnProtectionAction.Kick,
            $"[PORTAL-AUTOMATION] SIEGE:{matchedRule.Id}",
            ct).ConfigureAwait(false);

        var outcome = rconOutcome switch
        {
            VpnProtectionRconOutcome.Succeeded => SiegeActionOutcomes.Applied,
            VpnProtectionRconOutcome.PlayerNotConnected => SiegeActionOutcomes.Stale,
            _ => SiegeActionOutcomes.Failed
        };
        await UpdateOutcomeAsync(session, context, claim.Action.ActionId, outcome, null, ct).ConfigureAwait(false);

        auditLogger.LogAudit(AuditEvent.ServerAction("SiegeModeActionDispatched", AuditAction.Moderate)
            .WithGameContext(context.GameType.ToString(), context.ServerId)
            .WithPlayer(context.PlayerGuid, null)
            .WithSource(nameof(SiegeModeService))
            .WithProperty("SessionId", session.SessionId.ToString())
            .WithProperty("RuleId", matchedRule.Id)
            .WithProperty("Outcome", outcome)
            .Build());

        return result with { ShouldDispatch = false, WasEnforced = outcome is SiegeActionOutcomes.Applied or SiegeActionOutcomes.Queued or SiegeActionOutcomes.Dispatched };
    }

    private static string FreshnessCheckOutcome(string freshness) =>
        freshness == PlayerIpObservationStatuses.NotCurrent ? "FreshnessNotCurrent" : "FreshnessUnknown";

    private async Task<string> GetObservationFreshnessAsync(SiegeModeEvaluationContext context, CancellationToken ct)
    {
        if (context.ObservationPlayerId is not Guid playerId || playerId == Guid.Empty ||
            context.IpObservation is not { } observation ||
            observation.IpAddress != context.IpAddress ||
            string.IsNullOrWhiteSpace(observation.MessageId) ||
            observation.ObservedAtUtc is not DateTimeOffset observedAt ||
            observedAt.Offset != TimeSpan.Zero ||
            observedAt.UtcDateTime != DateTime.SpecifyKind(context.EventGeneratedUtc, DateTimeKind.Utc))
        {
            logger.LogWarning("Siege Mode IP observation identity is unavailable for server {ServerId}", context.ServerId);
            return PlayerIpObservationStatuses.Unknown;
        }

        try
        {
            var result = await repositoryApiClient.Players.V1
                .RecordPlayerIpObservationWithStatus(playerId, observation, ct).ConfigureAwait(false);
            if (result.IsSuccess && result.Result?.Data?.Status == PlayerIpObservationStatuses.Current)
            {
                return PlayerIpObservationStatuses.Current;
            }

            logger.LogWarning("Siege Mode IP observation is not confirmed current for server {ServerId}: HTTP {StatusCode}, freshness {Freshness}",
                context.ServerId, result.StatusCode, result.Result?.Data?.Status);
            return result.IsSuccess && result.Result?.Data?.Status == PlayerIpObservationStatuses.NotCurrent
                ? PlayerIpObservationStatuses.NotCurrent
                : PlayerIpObservationStatuses.Unknown;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Siege Mode IP observation freshness check failed for server {ServerId}", context.ServerId);
            return PlayerIpObservationStatuses.Unknown;
        }
    }

    private async Task<ApiResult<SiegeBaselineDto>?> GetBaselineAsync(
        SiegeSessionDto session,
        SiegeModeEvaluationContext context,
        CancellationToken ct)
    {
        try
        {
            var result = await repositoryApiClient.SiegeMode.V1.GetBaseline(
                context.ServerId,
                session.SessionId,
                new SiegeBaselineRequestDto
                {
                    PlayerGuid = context.PlayerGuid,
                    IpAddress = IpAddressGuard.IsPersistable(context.IpAddress) ? context.IpAddress : null
                },
                ct).ConfigureAwait(false);
            return result.IsSuccess ? result : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Siege Mode baseline lookup failed for session {SessionId}", session.SessionId);
            return null;
        }
    }

    private async Task RecordCheckAsync(
        SiegeSessionDto session,
        SiegeModeEvaluationContext context,
        string outcome,
        string detail,
        CancellationToken ct)
    {
        try
        {
            var result = await repositoryApiClient.SiegeMode.V1.RecordCheck(
                context.ServerId,
                session.SessionId,
                new RecordSiegeCheckDto
                {
                    CheckId = Guid.NewGuid(),
                    ConnectionId = context.ConnectionId,
                    PlayerGuid = string.IsNullOrWhiteSpace(context.PlayerGuid) ? null : context.PlayerGuid,
                    Outcome = outcome,
                    Detail = detail
                },
                ct).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                logger.LogWarning("Failed to record Siege Mode check {Outcome} for session {SessionId}: {StatusCode}",
                    outcome, session.SessionId, result.StatusCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record Siege Mode check {Outcome} for session {SessionId}", outcome, session.SessionId);
        }
    }

    private async Task UpdateOutcomeAsync(
        SiegeSessionDto session,
        SiegeModeEvaluationContext context,
        Guid actionId,
        string outcome,
        string? detail,
        CancellationToken ct)
    {
        try
        {
            var result = await repositoryApiClient.SiegeMode.V1.UpdateActionOutcome(
                context.ServerId,
                session.SessionId,
                actionId,
                new UpdateSiegeActionOutcomeDto
                {
                    ConnectionId = context.ConnectionId!.Value,
                    Outcome = outcome,
                    Detail = detail
                },
                ct).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                logger.LogWarning("Failed to update Siege Mode action outcome {Outcome} for action {ActionId}: {StatusCode}",
                    outcome, actionId, result.StatusCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update Siege Mode action outcome {Outcome} for action {ActionId}", outcome, actionId);
        }
    }

    private static bool? EvaluateCondition(
        SiegeModeCondition? condition,
        SiegeBaselineDto baseline,
        SiegeModeEvaluationContext context)
    {
        return RuleMatcher.Evaluate(
            condition,
            static node => (node.GroupOperator, node.Conditions),
            node => EvaluateLeaf(node, baseline, context));
    }

    private static bool? EvaluateLeaf(
        SiegeModeCondition condition,
        SiegeBaselineDto baseline,
        SiegeModeEvaluationContext context)
    {
        if (condition.Operator is not { } comparisonOperator || condition.ExpectedValue is null)
        {
            return null;
        }

        if (condition.PlayerSignal is { } playerSignal)
        {
            return playerSignal switch
            {
                SiegePlayerSignal.NewGuid => CompareBoolean(baseline.NewGuid, comparisonOperator, condition.ExpectedValue),
                SiegePlayerSignal.NewIp => CompareBoolean(baseline.NewIp, comparisonOperator, condition.ExpectedValue),
                SiegePlayerSignal.PlayerTag => ComparePlayerTag(context.PlayerTags, comparisonOperator, condition.ExpectedValue),
                _ => null
            };
        }

        if (condition.Signal is { } signal)
        {
            return VpnProtectionEvaluator.EvaluateSignal(
                signal,
                comparisonOperator,
                condition.ExpectedValue,
                context.Intelligence,
                out _);
        }

        return null;
    }

    private static bool? CompareBoolean(
        bool? actual,
        RuleComparisonOperator comparisonOperator,
        string expectedValue)
    {
        if (actual is null || !bool.TryParse(expectedValue, out var expected))
        {
            return null;
        }

        return comparisonOperator switch
        {
            RuleComparisonOperator.Equal => actual == expected,
            RuleComparisonOperator.NotEqual => actual != expected,
            _ => false
        };
    }

    private static bool? ComparePlayerTag(
        IReadOnlyCollection<string>? tags,
        RuleComparisonOperator comparisonOperator,
        string expectedTag)
    {
        if (tags is null)
        {
            return null;
        }

        var contains = tags.Contains(expectedTag, StringComparer.OrdinalIgnoreCase);
        return comparisonOperator switch
        {
            RuleComparisonOperator.Equal => contains,
            RuleComparisonOperator.NotEqual => !contains,
            _ => false
        };
    }
}
