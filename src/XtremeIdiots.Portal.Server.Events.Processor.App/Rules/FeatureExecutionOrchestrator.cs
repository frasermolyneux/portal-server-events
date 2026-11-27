using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using MX.Api.Abstractions;
using MX.Api.Client;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.RulesEngine.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

using PortableRuleMatcher = XtremeIdiots.Portal.RulesEngine.V1.RuleMatcher;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal interface IUniversalRuleActionExecutor
{
    Task<string?> UnsupportedReasonAsync(RuleActionDefinition action, RuleInvocation invocation, CancellationToken ct);
    Task<ActionAvailability> AvailabilityAsync(
        RuleActionDefinition action, RuleActionCommand command, RuleInvocation invocation, DateTimeOffset waitUntilUtc, CancellationToken ct);
    Task<ActionExecutionResult> ExecuteAsync(
        RuleActionDefinition action, RuleActionCommand command, RuleInvocation invocation, CancellationToken ct);
}

internal sealed class RuleExecutionProgress
{
    public RuleMatchEvidence? MatchEvidence { get; set; }
    public HashSet<string> MatchedRuleIds { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> CompletedRuleIds { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> UnknownRuleIds { get; set; } = new(StringComparer.Ordinal);
    public RuleExecutionMode Mode { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public ActivationIdentifierHistory? Baseline { get; set; }
    public DateTimeOffset? ContextWaitUntilUtc { get; set; }
    public DateTimeOffset ContextObservedAtUtc { get; set; }
    public RuleTriggerEnvelope? ContextTrigger { get; set; }
    public Dictionary<string, RuleExecutionCoordination> RuleWindows { get; set; } = new(StringComparer.Ordinal);
    public string? WaitingReason { get; set; }
    public Dictionary<string, ActionExecutionResult> ActionOutcomes { get; set; } = new(StringComparer.Ordinal);
    public RuleExecutionDiagnostics? Diagnostics { get; set; }
    public Dictionary<string, DateTimeOffset> MessagePreparationExpiries { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class RuleMatchEvidence
{
    public int Version { get; set; }
    public bool ObservedMatch { get; set; }
    public HashSet<string> FalseRuleIds { get; set; } = new(StringComparer.Ordinal);
    public bool NoMatchConfirmed { get; set; }
}

internal sealed class RegisteredRuleFeature(
    IFeatureRulesProvider provider,
    FeatureExecutionOrchestrator orchestrator) : IRegisteredRuleFeature
{
    private readonly FeatureDefinition definition = RulesEngineCatalogueV1.Current.Features.Single(item => item.FeatureId == provider.FeatureId);
    public string FeatureId => definition.FeatureId;
    public IReadOnlySet<string> TriggerIds { get; } = RulesEngineCatalogueV1.Current.Features
        .Single(item => item.FeatureId == provider.FeatureId).TriggerIds.ToHashSet(StringComparer.Ordinal);

    public async Task<Guid?> ProcessAsync(RuleInvocation invocation, CancellationToken ct)
    {
        var rules = await provider.GetAsync(invocation.Trigger, ct).ConfigureAwait(false);
        return rules is null ? null : await orchestrator.ProcessAsync(invocation, rules, ct).ConfigureAwait(false);
    }
}

internal sealed class FeatureExecutionOrchestrator(
    IRepositoryApiClient repository,
    IUniversalRuleActionExecutor executor,
    IRuleExecutionContinuationPublisher continuations,
    ILogger<FeatureExecutionOrchestrator> logger,
    ILogger<OrderedActionDispatcher> dispatcherLogger,
    IRuleActionTextPreparer textPreparer,
    IRuleMessageDeliveryService messages,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<Guid> ProcessAsync(RuleInvocation invocation, FeatureRulesSnapshot configured, CancellationToken ct)
    {
        var trigger = invocation.Trigger;
        var start = await repository.RuleExecutions.V1.StartExecution(trigger.GameServerId, new StartRuleFeatureExecutionDto
        {
            Trigger = trigger,
            FeatureId = configured.FeatureId,
            SiegeSessionId = configured.SiegeSessionId,
            RuleSetRevision = configured.Revision,
            FrozenRulesJson = configured.RulesJson,
            FactSnapshotId = invocation.Facts.SnapshotId,
            Coordination = configured.Timing is { } timing ? new()
            {
                DueAtUtc = trigger.OccurredAtUtc.AddSeconds(timing.ConnectionDelaySeconds),
                ExpiresAtUtc = trigger.OccurredAtUtc.AddSeconds(timing.FreshnessSeconds),
                PredecessorExecutionIds = invocation.ProtectionResults
                    .Where(item => item.ExecutionId.HasValue).Select(item => item.ExecutionId!.Value).ToArray()
            } : null
        }, ct).ConfigureAwait(false);
        var execution = Require(start, "start rules execution");
        invocation.ExecutionIds[execution.FeatureId] = execution.ExecutionId;
        if (execution.State is RuleFeatureExecutionState.Stopped or RuleFeatureExecutionState.Completed)
        {
            return execution.ExecutionId;
        }

        var frozen = JsonSerializer.Deserialize<FeatureRuleSetDocument>(execution.FrozenRulesJson, RulesSerialization.Options)
            ?? throw new InvalidOperationException("Frozen rules are missing.");
        var validation = new RuleSetValidator().Validate(frozen, RuleSetValidationOptions.ForFeature(execution.FeatureId));
        if (!validation.IsValid || frozen.FeatureId != execution.FeatureId || frozen.Enabled != true)
        {
            throw new InvalidOperationException("Frozen rules are invalid: " + string.Join("; ", validation.Errors));
        }
        var rules = frozen.Rules.ToArray();

        var progress = execution.DecisionSnapshotJson is null
            ? new RuleExecutionProgress { Mode = configured.Mode, ExpiresAtUtc = configured.ExpiresAtUtc, Baseline = configured.Baseline }
            : JsonSerializer.Deserialize<RuleExecutionProgress>(execution.DecisionSnapshotJson, RulesSerialization.Options)
                ?? throw new InvalidOperationException("Persisted rule progress is invalid.");
        var matchEvidence = progress.MatchEvidence ??= new() { Version = 1 };
        if (matchEvidence.Version != 1)
        {
            throw new InvalidOperationException("Persisted rule match evidence version is unsupported.");
        }
        if (execution.State == RuleFeatureExecutionState.Waiting)
        {
            progress.WaitingReason = null;
        }
        var previousWaiting = progress.Diagnostics?.Waiting;
        if (progress.Diagnostics is not null)
        {
            progress.Diagnostics.Waiting = null;
        }
        if (progress.Baseline is { } frozenBaseline && configured.Baseline is { } supplementalBaseline)
        {
            progress.Baseline = new(
                frozenBaseline.Guid.Available ? frozenBaseline.Guid : supplementalBaseline.Guid,
                frozenBaseline.Ip.Available ? frozenBaseline.Ip : supplementalBaseline.Ip);
        }
        if (execution.SiegeSessionId is not null && execution.SiegeSessionId == configured.SiegeSessionId)
        {
            progress.ExpiresAtUtc = configured.ExpiresAtUtc;
        }
        if (progress.ContextTrigger is null || trigger.OccurredAtUtc > progress.ContextObservedAtUtc ||
            trigger.OccurredAtUtc == progress.ContextObservedAtUtc &&
            progress.ContextTrigger.PlayerIpAddress is null && trigger.PlayerIpAddress is not null)
        {
            progress.ContextObservedAtUtc = trigger.OccurredAtUtc;
            progress.ContextTrigger = trigger;
        }
        var feature = RulesEngineCatalogueV1.Current.Features.Single(item => item.FeatureId == execution.FeatureId);
        var context = new RuleEvaluationContext(trigger.GameServerId, (int)trigger.GameType, trigger.ConnectionId,
            execution.FeatureId, execution.SiegeSessionId, progress.ContextObservedAtUtc,
            (progress.ContextTrigger.Runtime?.Capabilities ?? []).ToHashSet(StringComparer.Ordinal));
        for (var ruleIndex = 0; ruleIndex < rules.Length; ruleIndex++)
        {
            var rule = rules[ruleIndex];
            if (!rule.Enabled || progress.CompletedRuleIds.Contains(rule.Id))
            {
                continue;
            }

            RuleExecutionCoordination? window = null;
            if (frozen.Timing is { } featureTiming)
            {
                if (!progress.RuleWindows.TryGetValue(rule.Id, out window))
                {
                    var effectiveTiming = featureTiming.Apply(rule.Timing);
                    if (!effectiveTiming.IsValid)
                    {
                        throw new InvalidOperationException("Frozen timing leaves no valid delivery window.");
                    }
                    window = new()
                    {
                        DueAtUtc = execution.Trigger.OccurredAtUtc.AddSeconds(effectiveTiming.ConnectionDelaySeconds),
                        ExpiresAtUtc = execution.Trigger.OccurredAtUtc.AddSeconds(effectiveTiming.FreshnessSeconds),
                        PredecessorExecutionIds = execution.Coordination?.PredecessorExecutionIds ?? []
                    };
                    progress.RuleWindows.Add(rule.Id, window);
                }
                progress.ExpiresAtUtc = window.ExpiresAtUtc;
                if (clock.GetUtcNow() >= window.ExpiresAtUtc &&
                    !execution.PreparedActions.Any(item => item.RuleIndex == ruleIndex) &&
                    !execution.Actions.Any(item => item.Correlation.RuleId == rule.Id &&
                        item.Outcome is RuleActionExecutionOutcome.Claimed or RuleActionExecutionOutcome.Dispatched))
                {
                    progress.CompletedRuleIds.Add(rule.Id);
                    progress.UnknownRuleIds.Remove(rule.Id);
                    progress.WaitingReason = "Original connection freshness window expired.";
                    AddDiagnostic(RuleExecutionDiagnosticCode.Expired, rule.Id, detail: progress.WaitingReason);
                    continue;
                }
            }

            if (!progress.MatchedRuleIds.Contains(rule.Id))
            {
                var intelligence = invocation.Intelligence;
                if (intelligence is null && UsesIntelligence(rule.Condition) && invocation.IntelligenceLookup is { } lookup)
                {
                    intelligence = await lookup.Value.ConfigureAwait(false);
                }
                var resolver = new SharedRuleConditionResolver(invocation.Facts, intelligence, progress.Baseline,
                    feature.ConditionIds.ToHashSet(StringComparer.Ordinal));
                var match = PortableRuleMatcher.EvaluateCondition(rule.Condition, node => resolver.Resolve(node, context));
                if (match.Value == RuleTruthValue.Unknown)
                {
                    progress.UnknownRuleIds.Add(rule.Id);
                    logger.LogInformation("Rules condition is unknown for feature {FeatureId}, rule {RuleId}: {Diagnostic}",
                        execution.FeatureId, rule.Id, match.Diagnostic);
                    if (execution.FeatureId == WelcomeFeatureRulesProvider.Id && window is not null)
                    {
                        return await WaitAsync("Waiting for higher-priority rule context.",
                            clock.GetUtcNow().AddSeconds(10), ruleIndex, RuleExecutionDiagnosticCode.WaitingForContext)
                            .ConfigureAwait(false);
                    }
                    continue;
                }

                progress.UnknownRuleIds.Remove(rule.Id);
                if (match.Value == RuleTruthValue.False)
                {
                    matchEvidence.FalseRuleIds.Add(rule.Id);
                    progress.CompletedRuleIds.Add(rule.Id);
                    AddDiagnostic(RuleExecutionDiagnosticCode.NoMatch, rule.Id, detail: match.Diagnostic);
                    continue;
                }

                progress.WaitingReason = null;

                matchEvidence.ObservedMatch = true;
                progress.MatchedRuleIds.Add(rule.Id);
                execution = await SaveProgressAsync(execution, progress, ruleIndex, 0,
                    RuleFeatureExecutionState.Evaluating, ct).ConfigureAwait(false);
            }
            if (execution.State == RuleFeatureExecutionState.Waiting)
            {
                execution = await SaveProgressAsync(execution, progress, ruleIndex, execution.ActionIndex,
                    RuleFeatureExecutionState.Evaluating, ct).ConfigureAwait(false);
            }

            var store = new RepositoryRuleActionStore(repository, execution, rule, ruleIndex, progress, progress.ContextTrigger);
            var outcomes = new Dictionary<string, ActionExecutionResult>(StringComparer.Ordinal);
            ActionSequenceResult dispatched = new(false, outcomes);
            string? pendingActionId = null;
            foreach (var configuredAction in rule.Actions)
            {
                var action = configuredAction;
                var outcomeKey = JsonSerializer.Serialize(new[] { rule.Id, action.Id });
                if (progress.ActionOutcomes.TryGetValue(outcomeKey, out var completedOutcome))
                {
                    outcomes.Add(action.Id, completedOutcome);
                    continue;
                }
                if (window is not null && action.TypeId != "message.private-tagged" && clock.GetUtcNow() < window.DueAtUtc)
                {
                    execution = store.Execution;
                    return await WaitAsync("Waiting for original connection delay.", window.DueAtUtc, ruleIndex,
                        RuleExecutionDiagnosticCode.WaitingForDelay, action.Id).ConfigureAwait(false);
                }
                var legacyClaim = store.Execution.Actions.Any(item =>
                    item.Correlation.RuleId == rule.Id && item.Correlation.ActionId == action.Id &&
                    item.Command.DeliveryIdentity is null);
                if (!legacyClaim && action.TypeId is ("message.public" or "message.private" or "message.private-tagged"))
                {
                    if (!action.DependsOnActionIds.Any(id => !outcomes[id].SatisfiesDependency) &&
                        progress.ExpiresAtUtc is null &&
                        !store.Execution.PreparedActions.Any(item => item.RuleIndex == ruleIndex &&
                            item.ActionIndex == rule.Actions.IndexOf(configuredAction)) &&
                        !progress.MessagePreparationExpiries.ContainsKey(outcomeKey))
                    {
                        progress.MessagePreparationExpiries.Add(outcomeKey, store.Command(action).ExpiresAtUtc);
                        store.Execution = await SaveProgressAsync(store.Execution, progress, ruleIndex,
                            store.Execution.ActionIndex, store.Execution.State, ct).ConfigureAwait(false);
                        execution = store.Execution;
                    }
                    var messageResult = action.DependsOnActionIds.Any(id => !outcomes[id].SatisfiesDependency)
                        ? new RuleMessageDispatchResult(store.Execution,
                            new(ActionExecutionOutcome.SkippedDependency, "A prerequisite action did not succeed."))
                        : await messages.DispatchAsync(store.Execution, rule, ruleIndex,
                            rule.Actions.IndexOf(configuredAction), action, store.Command, invocation, progress, ct).ConfigureAwait(false);
                    store.Execution = messageResult.Execution;
                    execution = store.Execution;
                    if (messageResult.ResumeAtUtc is { } resumeAtUtc)
                    {
                        foreach (var command in messageResult.Commands)
                        {
                            invocation.Commands.Add(command);
                        }
                        return await WaitAsync(messageResult.Detail ?? "Waiting for message delivery.", resumeAtUtc, ruleIndex,
                            messageResult.DiagnosticCode ?? RuleExecutionDiagnosticCode.WaitingForDelivery, action.Id)
                            .ConfigureAwait(false);
                    }
                    if (messageResult.AwaitingOutcome)
                    {
                        dispatched = new(true, outcomes);
                        foreach (var command in messageResult.Commands)
                        {
                            invocation.Commands.Add(command);
                        }
                        pendingActionId = action.Id;
                        break;
                    }
                    var messageOutcome = messageResult.Outcome
                        ?? throw new InvalidOperationException("Message preparation returned no outcome or continuation.");
                    if (messageResult.DiagnosticCode is { } messageDiagnostic)
                    {
                        AddDiagnostic(messageDiagnostic, rule.Id, action.Id, messageOutcome.Detail);
                    }
                    outcomes.Add(action.Id, messageOutcome);
                    progress.ActionOutcomes[outcomeKey] = messageOutcome;
                    store.Execution = await SaveProgressAsync(store.Execution, progress, ruleIndex,
                        rule.Actions.IndexOf(configuredAction) + 1, RuleFeatureExecutionState.Evaluating, ct).ConfigureAwait(false);
                    execution = store.Execution;
                    continue;
                }
                if (action.TemplateParameters.Count != 0 &&
                    !store.Execution.Actions.Any(item => item.Correlation.RuleId == rule.Id && item.Correlation.ActionId == action.Id))
                {
                    var prepared = await textPreparer.PrepareAsync(action, invocation, execution.Trigger.OccurredAtUtc,
                        execution.FeatureId, rule.Id, ct).ConfigureAwait(false);
                    if ((progress.Diagnostics is not null || prepared.Diagnostics.Count != 0) &&
                        (progress.Diagnostics ??= new()).ReplaceActionText(rule.Id, action.Id, prepared.Diagnostics, clock.GetUtcNow(), logger))
                    {
                        store.Execution = await SaveProgressAsync(store.Execution, progress, ruleIndex,
                            store.Execution.ActionIndex, store.Execution.State, ct).ConfigureAwait(false);
                        execution = store.Execution;
                    }
                    action = prepared.Action;
                    store.ResolvedActions[action.Id] = action;
                }
                var unsupported = await executor.UnsupportedReasonAsync(action, invocation, ct).ConfigureAwait(false);
                var planned = new PlannedRuleAction(action.Id, action.DependsOnActionIds, unsupported, token =>
                {
                    var command = store.Command(action);
                    if (command.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                    {
                        return Task.FromResult(new ActionExecutionResult(ActionExecutionOutcome.Stale, "Action decision expired."));
                    }

                    return executor.ExecuteAsync(action, command, invocation, token);
                }, token =>
                {
                    if (unsupported is not null ||
                        store.Execution.Actions.Any(item => item.Correlation.RuleId == rule.Id && item.Correlation.ActionId == action.Id))
                    {
                        return Task.FromResult(new ActionAvailability());
                    }
                    progress.ContextWaitUntilUtc ??= DateTimeOffset.UtcNow.AddMinutes(2);
                    return executor.AvailabilityAsync(action, store.Command(action), invocation, progress.ContextWaitUntilUtc.Value, token);
                });
                dispatched = await new OrderedActionDispatcher(store, dispatcherLogger)
                    .DispatchAsync(execution.ExecutionId, [planned], progress.Mode == RuleExecutionMode.DryRun, ct, outcomes)
                    .ConfigureAwait(false);
                foreach (var pair in dispatched.Outcomes)
                {
                    outcomes[pair.Key] = pair.Value;
                }
                if (dispatched.AwaitingContext || dispatched.AwaitingOutcome)
                {
                    pendingActionId = action.Id;
                    break;
                }
                progress.ActionOutcomes[outcomeKey] = outcomes[action.Id] with { NativeCommand = null };
            }
            dispatched = dispatched with { Outcomes = outcomes };
            execution = store.Execution;
            if (dispatched.AwaitingContext)
            {
                SetWaitingDiagnostic(RuleExecutionDiagnosticCode.WaitingForContext, rule.Id, pendingActionId,
                    "Waiting for action context.");
                execution = await SaveProgressAsync(execution, progress, ruleIndex, execution.ActionIndex,
                    RuleFeatureExecutionState.Evaluating, ct).ConfigureAwait(false);
                if (invocation.Trigger.Runtime?.Id == "server-agent")
                {
                    await continuations.ScheduleAsync(execution.GameServerId, execution.ExecutionId,
                        clock.GetUtcNow().AddSeconds(10), ct).ConfigureAwait(false);
                }
                invocation.PendingExecutionIds.Add(execution.ExecutionId);
                return execution.ExecutionId;
            }
            foreach (var command in dispatched.Outcomes.Values.Select(item => item.NativeCommand).OfType<RuleActionCommand>())
            {
                invocation.Commands.Add(command);
                if (command.Effect.Type == RuleNativeEffectType.DropPlayer)
                {
                    invocation.SuppressWelcome = true;
                }
            }
            if (dispatched.AwaitingOutcome)
            {
                SetWaitingDiagnostic(RuleExecutionDiagnosticCode.WaitingForDelivery, rule.Id, pendingActionId,
                    "Waiting for the claimed action outcome.");
                if (execution.DecisionSnapshotJson != JsonSerializer.Serialize(progress, RulesSerialization.Options))
                {
                    execution = await SaveProgressAsync(execution, progress, execution.RuleIndex, execution.ActionIndex,
                        execution.State, ct).ConfigureAwait(false);
                }
                if (execution.Trigger.Runtime?.Id == "server-agent")
                {
                    var pending = execution.Actions.Single(item =>
                        item.Outcome is RuleActionExecutionOutcome.Claimed or RuleActionExecutionOutcome.Dispatched);
                    await continuations.ScheduleAsync(execution.GameServerId, execution.ExecutionId,
                        pending.Command.ExpiresAtUtc, ct).ConfigureAwait(false);
                }
                invocation.PendingExecutionIds.Add(execution.ExecutionId);
                return execution.ExecutionId;
            }

            progress.CompletedRuleIds.Add(rule.Id);
            progress.UnknownRuleIds.Remove(rule.Id);
            var nextState = rule.AfterMatch == RuleAfterMatch.StopProcessingRules
                ? RuleFeatureExecutionState.Stopped
                : RuleFeatureExecutionState.Evaluating;
            execution = await SaveProgressAsync(execution, progress, ruleIndex + 1, 0, nextState, ct).ConfigureAwait(false);
            if (nextState == RuleFeatureExecutionState.Stopped)
            {
                return execution.ExecutionId;
            }
        }

        var eligibleRules = rules.Where(rule => rule.Enabled).ToArray();
        matchEvidence.NoMatchConfirmed = !matchEvidence.ObservedMatch && eligibleRules.Length != 0 &&
            eligibleRules.All(rule => matchEvidence.FalseRuleIds.Contains(rule.Id));
        await SaveProgressAsync(execution, progress, rules.Length, 0,
            progress.UnknownRuleIds.Count == 0 ? RuleFeatureExecutionState.Completed : RuleFeatureExecutionState.Evaluating, ct).ConfigureAwait(false);
        return execution.ExecutionId;

        async Task<Guid> WaitAsync(string reason, DateTimeOffset dueAtUtc, int ruleIndex,
            RuleExecutionDiagnosticCode code, string? actionId = null)
        {
            progress.WaitingReason = reason;
            SetWaitingDiagnostic(code, rules[ruleIndex].Id, actionId, reason);
            execution = await SaveProgressAsync(execution, progress, ruleIndex, execution.ActionIndex,
                RuleFeatureExecutionState.Waiting, ct).ConfigureAwait(false);
            if (trigger.Runtime?.Id == "server-agent")
            {
                var expiresAtUtc = progress.ExpiresAtUtc ?? dueAtUtc;
                await continuations.ScheduleAsync(execution.GameServerId, execution.ExecutionId,
                    dueAtUtc < expiresAtUtc ? dueAtUtc : expiresAtUtc, ct).ConfigureAwait(false);
            }
            invocation.PendingExecutionIds.Add(execution.ExecutionId);
            return execution.ExecutionId;
        }

        void SetWaitingDiagnostic(RuleExecutionDiagnosticCode code, string ruleId, string? actionId, string detail)
        {
            var diagnostic = NewDiagnostic(code, ruleId, actionId, detail);
            (progress.Diagnostics ??= new()).Waiting =
                previousWaiting is { } existing &&
                RuleExecutionDiagnosticIdentity.From(existing) == RuleExecutionDiagnosticIdentity.From(diagnostic) &&
                existing.Detail == diagnostic.Detail ? existing : diagnostic;
        }

        void AddDiagnostic(RuleExecutionDiagnosticCode code, string ruleId, string? actionId = null, string? detail = null) =>
            (progress.Diagnostics ??= new()).Add(NewDiagnostic(code, ruleId, actionId, detail));

        RuleExecutionDiagnostic NewDiagnostic(RuleExecutionDiagnosticCode code, string ruleId, string? actionId, string? detail) =>
            new()
            {
                Code = code,
                RuleId = ruleId,
                ActionId = actionId,
                Detail = RuleExecutionDiagnostics.SafeDetail(detail, logger),
                ObservedAtUtc = clock.GetUtcNow()
            };
    }

    private static bool UsesIntelligence(RuleConditionNode? condition) =>
        condition?.ConditionId == "vpn.intelligence" || condition?.Conditions?.Any(UsesIntelligence) == true;

    private Task<RuleFeatureExecutionDto> SaveProgressAsync(
        RuleFeatureExecutionDto execution, RuleExecutionProgress progress, int ruleIndex, int actionIndex,
        RuleFeatureExecutionState state, CancellationToken ct) =>
        PersistProgressAsync(repository, execution, progress, ruleIndex, actionIndex, state, logger, clock.GetUtcNow(), ct);

    internal static async Task<RuleFeatureExecutionDto> PersistProgressAsync(
        IRepositoryApiClient repository, RuleFeatureExecutionDto execution, RuleExecutionProgress progress,
        int ruleIndex, int actionIndex, RuleFeatureExecutionState state, ILogger logger, DateTimeOffset now, CancellationToken ct)
    {
        var diagnostics = progress.Diagnostics?.Project(now);
        if (diagnostics?.Any(item => item.Code == RuleExecutionDiagnosticCode.DiagnosticsOmitted) == true &&
            progress.Diagnostics is { OverflowReported: false } diagnosticProgress)
        {
            logger.LogWarning("Execution {ExecutionId} has more diagnostic evidence than the API can expose.",
                execution.ExecutionId);
            diagnosticProgress.OverflowReported = true;
        }
        var request = new UpdateRuleFeatureExecutionProgressDto
        {
            ExpectedVersion = execution.Version,
            RuleIndex = ruleIndex,
            ActionIndex = actionIndex,
            State = state,
            DecisionSnapshotJson = JsonSerializer.Serialize(progress, RulesSerialization.Options),
            MatchStatus = execution.MatchStatus == RuleExecutionMatchStatus.Matched ||
                    progress.MatchEvidence?.ObservedMatch is true
                    ? RuleExecutionMatchStatus.Matched
                    : state == RuleFeatureExecutionState.Completed && progress.MatchEvidence?.NoMatchConfirmed is true
                        ? RuleExecutionMatchStatus.NoMatch
                        : null,
            FactSnapshotId = execution.FactSnapshotId,
            Coordination = execution.Coordination is { } coordination
                    ? coordination with { WaitingReason = progress.WaitingReason }
                    : null,
            Diagnostics = diagnostics
        };
        ApiResult<RuleFeatureExecutionDto> response;
        try
        {
            response = await repository.RuleExecutions.V1.UpdateProgress(
                execution.GameServerId, execution.ExecutionId, request, ct).ConfigureAwait(false);
        }
        catch (ApiHttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            var completed = await ReconcileCompletedNoMatchAsync(
                repository, execution, progress, request, logger, ct).ConfigureAwait(false);
            if (completed is not null)
            {
                return completed;
            }
            throw;
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var completed = await ReconcileCompletedNoMatchAsync(
                repository, execution, progress, request, logger, ct).ConfigureAwait(false);
            if (completed is not null)
            {
                return completed;
            }
        }
        return Require(response, "persist rules cursor");
    }

    private static async Task<RuleFeatureExecutionDto?> ReconcileCompletedNoMatchAsync(
        IRepositoryApiClient repository, RuleFeatureExecutionDto execution, RuleExecutionProgress progress,
        UpdateRuleFeatureExecutionProgressDto request, ILogger logger, CancellationToken ct)
    {
        if (request.State != RuleFeatureExecutionState.Completed ||
            request.MatchStatus != RuleExecutionMatchStatus.NoMatch ||
            !HasNoActions(execution, progress))
        {
            return null;
        }

        var current = Require(await repository.RuleExecutions.V1.GetExecution(
            execution.GameServerId, execution.ExecutionId, ct).ConfigureAwait(false), "reconcile rules cursor conflict");
        if (current.ExecutionId != execution.ExecutionId || current.GameServerId != execution.GameServerId ||
            current.ConnectionId != execution.ConnectionId || current.FeatureId != execution.FeatureId ||
            current.SiegeSessionId != execution.SiegeSessionId || current.FactSnapshotId != execution.FactSnapshotId ||
            current.RuleSetRevision != execution.RuleSetRevision || current.FrozenRulesJson != execution.FrozenRulesJson ||
            current.Version <= execution.Version || current.State != RuleFeatureExecutionState.Completed ||
            current.MatchStatus != RuleExecutionMatchStatus.NoMatch ||
            current.RuleIndex != request.RuleIndex || current.ActionIndex != request.ActionIndex ||
            JsonSerializer.Serialize(current.Trigger, RulesSerialization.Options) !=
                JsonSerializer.Serialize(execution.Trigger, RulesSerialization.Options) ||
            current.DecisionSnapshotJson is null)
        {
            return null;
        }
        var completedProgress = JsonSerializer.Deserialize<RuleExecutionProgress>(
            current.DecisionSnapshotJson, RulesSerialization.Options);
        if (completedProgress is null || completedProgress.Mode != progress.Mode ||
            !HasNoActions(current, completedProgress) ||
            !completedProgress.MatchEvidence!.FalseRuleIds.SetEquals(progress.MatchEvidence!.FalseRuleIds))
        {
            return null;
        }

        logger.LogWarning("Execution {ExecutionId} completed with no match concurrently; discarding stale cursor progress.",
            execution.ExecutionId);
        return current;
    }

    private static bool HasNoActions(RuleFeatureExecutionDto execution, RuleExecutionProgress progress) =>
        execution.Actions.Length == 0 && execution.PreparedActions.Length == 0 &&
        progress.ActionOutcomes.Count == 0 && progress.MatchedRuleIds.Count == 0 &&
        progress.UnknownRuleIds.Count == 0 &&
        progress.MatchEvidence is { NoMatchConfirmed: true, ObservedMatch: false };

    internal static T Require<T>(ApiResult<T> result, string operation) where T : class =>
        result.IsSuccess && result.Result?.Data is { } data ? data :
            throw new InvalidOperationException($"Unable to {operation}: {result.StatusCode}.");
}

internal sealed class RepositoryRuleActionStore(
    IRepositoryApiClient repository,
    RuleFeatureExecutionDto execution,
    RuleDefinition rule,
    int ruleIndex,
    RuleExecutionProgress progress,
    RuleTriggerEnvelope currentTrigger) : IRuleActionExecutionStore
{
    public RuleFeatureExecutionDto Execution { get; set; } = execution;
    public Dictionary<string, RuleActionDefinition> ResolvedActions { get; } = new(StringComparer.Ordinal);

    public RuleActionCommand Command(RuleActionDefinition action)
    {
        action = ResolvedActions.GetValueOrDefault(action.Id) ?? action;
        var stored = Execution.Actions.SingleOrDefault(item =>
            item.Correlation.RuleId == rule.Id && item.Correlation.ActionId == action.Id);
        if (stored is not null)
        {
            return stored.Command;
        }

        var trigger = Execution.Trigger;
        var logicalId = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Execution.GameServerId,
            Execution.ConnectionId,
            Execution.FeatureId,
            Execution.SiegeSessionId,
            RuleId = rule.Id,
            ActionId = action.Id
        }, RulesSerialization.Options)))).ToLowerInvariant();
        var message = String(action, "message");
        var reason = String(action, "reason");
        return new RuleActionCommand
        {
            ExecutionId = Execution.ExecutionId,
            ActionId = action.Id,
            LogicalEffectId = logicalId,
            GameServerId = Execution.GameServerId,
            ConnectionId = Execution.ConnectionId,
            ConnectionGeneration = currentTrigger.ConnectionGeneration,
            Runtime = currentTrigger.Runtime ?? new(),
            PlayerGuid = trigger.PlayerGuid ?? "",
            SlotId = trigger.SlotId ?? -1,
            Mode = progress.Mode,
            ExpiresAtUtc = progress.ExpiresAtUtc is { } expiry && expiry < DateTimeOffset.UtcNow.AddSeconds(30)
                ? expiry
                : DateTimeOffset.UtcNow.AddSeconds(30),
            Correlation = new RuleActionCorrelation
            {
                ConnectionId = Execution.ConnectionId,
                FeatureId = Execution.FeatureId,
                SiegeSessionId = Execution.SiegeSessionId,
                RuleId = rule.Id,
                ActionId = action.Id,
                RuleSetRevision = Execution.RuleSetRevision,
                TriggerId = currentTrigger.TriggerId,
                TriggerOccurrenceId = currentTrigger.TriggerOccurrenceId
            },
            Effect = new RuleNativeEffect
            {
                Type = action.TypeId switch
                {
                    "message.public" => RuleNativeEffectType.PublicMessage,
                    "message.private" => RuleNativeEffectType.PrivateMessage,
                    "player.kick" or "moderation.kick-with-record" or "moderation.ban" or "moderation.temporary-ban" => RuleNativeEffectType.DropPlayer,
                    _ => RuleNativeEffectType.Unknown
                },
                Message = message,
                Reason = reason
            }
        };
    }

    public async Task<ActionExecutionClaim> ClaimAsync(Guid executionId, string actionId, CancellationToken ct)
    {
        var configuredAction = rule.Actions.Single(item => item.Id == actionId);
        var action = ResolvedActions.GetValueOrDefault(actionId) ?? configuredAction;
        var command = Command(action);
        var stored = Execution.Actions.SingleOrDefault(item => item.LogicalEffectId == command.LogicalEffectId);
        if (stored is not null)
        {
            if (stored.Outcome is RuleActionExecutionOutcome.Claimed or RuleActionExecutionOutcome.Dispatched &&
                stored.Command.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                var expired = new ActionExecutionResult(ActionExecutionOutcome.OutcomeUnknown,
                    "The claimed action expired without a confirmed correlated outcome; it will not be resent.");
                await RecordAsync(executionId, actionId, expired, ct).ConfigureAwait(false);
                return new(false, expired);
            }
            return new(false, Convert(stored));
        }

        var response = await repository.RuleExecutions.V1.ClaimAction(
            Execution.GameServerId, executionId, new ClaimRuleActionDto
            {
                ExpectedVersion = Execution.Version,
                RuleIndex = ruleIndex,
                ActionIndex = rule.Actions.IndexOf(configuredAction),
                LogicalEffectId = command.LogicalEffectId,
                Command = command,
                ResolvedParametersJson = JsonSerializer.Serialize(action.Parameters, RulesSerialization.Options)
            }, ct).ConfigureAwait(false);
        var claimed = FeatureExecutionOrchestrator.Require(response, "claim rules action");
        Execution = claimed.Execution;
        return new(response.StatusCode == HttpStatusCode.Created, Convert(claimed.Action));
    }

    public async Task RecordAsync(Guid executionId, string actionId, ActionExecutionResult result, CancellationToken ct)
    {
        var action = rule.Actions.Single(item => item.Id == actionId);
        var command = Command(action);
        var updated = FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.UpdateActionOutcome(
            Execution.GameServerId, executionId, actionId, new UpdateRuleActionOutcomeDto
            {
                ExpectedVersion = Execution.Version,
                AdminActionId = result.AdminActionId,
                Result = new RuleActionResult
                {
                    ExecutionId = executionId,
                    GameServerId = Execution.GameServerId,
                    ActionId = actionId,
                    LogicalEffectId = command.LogicalEffectId,
                    Correlation = command.Correlation,
                    Outcome = Enum.Parse<RuleActionExecutionOutcome>(result.Outcome.ToString()),
                    Detail = result.Detail,
                    OccurredAtUtc = DateTimeOffset.UtcNow
                },
                NextRuleIndex = ruleIndex,
                NextActionIndex = result.IsPending ? rule.Actions.IndexOf(action) : rule.Actions.IndexOf(action) + 1,
                NextState = result.IsPending ? RuleFeatureExecutionState.AwaitingActionOutcome : RuleFeatureExecutionState.Evaluating,
                DecisionSnapshotJson = JsonSerializer.Serialize(progress, RulesSerialization.Options)
            }, ct).ConfigureAwait(false), "persist rules action outcome");
        Execution = updated.Execution;
    }

    private static ActionExecutionResult Convert(RuleActionExecutionDto action) =>
        new(Enum.Parse<ActionExecutionOutcome>(action.Outcome.ToString()), action.Detail);

    private static string? String(RuleActionDefinition action, string key) =>
        action.Parameters.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
