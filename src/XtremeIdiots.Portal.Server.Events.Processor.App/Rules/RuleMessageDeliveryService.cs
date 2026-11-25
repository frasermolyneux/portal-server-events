using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal sealed record RuleMessageBudgetResult(
    MessageChannelBudget? Budget, string? UnsupportedReason = null, bool Unavailable = false,
    RconMessageCharacterConstraintsDto? Constraints = null);

internal interface IRuleMessageBudgetProvider
{
    Task<RuleMessageBudgetResult> GetAsync(RuleInvocation invocation, string actionType, CancellationToken ct);
}

internal sealed record RuleMessageDispatchResult(
    RuleFeatureExecutionDto Execution,
    ActionExecutionResult? Outcome = null,
    bool AwaitingOutcome = false,
    DateTimeOffset? ResumeAtUtc = null,
    string? Detail = null,
    RuleExecutionDiagnosticCode? DiagnosticCode = null)
{
    public IReadOnlyList<RuleActionCommand> Commands { get; init; } = [];
}

internal interface IRuleMessageDeliveryService
{
    Task<RuleMessageDispatchResult> DispatchAsync(
        RuleFeatureExecutionDto execution, RuleDefinition rule, int ruleIndex, int actionIndex,
        RuleActionDefinition action, Func<RuleActionDefinition, RuleActionCommand> commandFactory,
        RuleInvocation invocation, RuleExecutionProgress progress, CancellationToken ct);
}

internal sealed class RuleMessageDeliveryService(
    IRepositoryApiClient repository,
    IUniversalRuleActionExecutor executor,
    IRuleActionTextPreparer text,
    IRuleMessageBudgetProvider budgets,
    ILogger<RuleMessageDeliveryService> logger,
    TimeProvider? timeProvider = null) : IRuleMessageDeliveryService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<RuleMessageDispatchResult> DispatchAsync(
        RuleFeatureExecutionDto execution, RuleDefinition rule, int ruleIndex, int actionIndex,
        RuleActionDefinition action, Func<RuleActionDefinition, RuleActionCommand> commandFactory,
        RuleInvocation invocation, RuleExecutionProgress progress, CancellationToken ct)
    {
        var tagged = action.TypeId == "message.private-tagged";
        var plan = execution.PreparedActions.SingleOrDefault(item =>
            item.RuleIndex == ruleIndex && item.ActionIndex == actionIndex);
        var preparationKey = JsonSerializer.Serialize(new[] { rule.Id, action.Id });
        var featureExpiry = progress.ExpiresAtUtc;
        if (progress.MessagePreparationExpiries.TryGetValue(preparationKey, out var preparationExpiry) &&
            (featureExpiry is null || preparationExpiry < featureExpiry))
        {
            featureExpiry = preparationExpiry;
        }
        var expiry = plan?.RuleCoordination?.ExpiresAtUtc ?? plan?.Commands.FirstOrDefault()?.ExpiresAtUtc ??
            featureExpiry ?? commandFactory(action).ExpiresAtUtc;
        (bool Deferred, string Detail)? protectionDecision = null;
        if (execution.FeatureId == WelcomeFeatureRulesProvider.Id && !tagged)
        {
            protectionDecision = await ProtectionAsync(execution, invocation, ct).ConfigureAwait(false);
            if (protectionDecision is { } decision && plan is null)
            {
                return decision.Deferred && clock.GetUtcNow() < expiry
                    ? Wait(decision.Detail, clock.GetUtcNow().AddSeconds(10), RuleExecutionDiagnosticCode.WaitingForProtection)
                    : decision.Deferred
                        ? Complete(ActionExecutionOutcome.Stale, "Original delivery window expired while waiting for protection.",
                            RuleExecutionDiagnosticCode.Expired)
                        : Complete(ActionExecutionOutcome.SkippedDependency, decision.Detail,
                            RuleExecutionDiagnosticCode.SuppressedByProtection);
            }
        }

        if (plan is null)
        {
            if (clock.GetUtcNow() >= expiry)
            {
                return Complete(ActionExecutionOutcome.Stale, "Original message delivery window expired before preparation.",
                    RuleExecutionDiagnosticCode.Expired);
            }
            var unsupported = await executor.UnsupportedReasonAsync(action, invocation, ct).ConfigureAwait(false);
            if (unsupported is not null)
            {
                return Complete(ActionExecutionOutcome.SkippedUnsupported, unsupported, RuleExecutionDiagnosticCode.UnsupportedDelivery);
            }
            var budget = await budgets.GetAsync(invocation, action.TypeId, ct).ConfigureAwait(false);
            if (budget.Budget is null)
            {
                return budget.Unavailable
                    ? Wait(budget.UnsupportedReason ?? "Waiting for channel budget evidence.", clock.GetUtcNow().AddSeconds(10),
                        RuleExecutionDiagnosticCode.WaitingForDelivery)
                    : Complete(ActionExecutionOutcome.SkippedUnsupported, budget.UnsupportedReason,
                        RuleExecutionDiagnosticCode.UnsupportedDelivery);
            }
            RuleTaggedAudienceDto? audience = null;
            if (tagged)
            {
                audience = FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.GetTaggedAudience(
                    execution.GameServerId, new()
                    {
                        TagIds = action.Parameters["recipientTags"].EnumerateArray().Select(value => value.GetGuid()).ToArray(),
                        Match = Enum.Parse<RuleRecipientTagMatch>(action.Parameters["recipientTagMatch"].GetString()!)
                    }, ct).ConfigureAwait(false), "resolve tagged message audience");
                if (audience.GameServerId != execution.GameServerId)
                {
                    throw new InvalidOperationException("Tagged audience belongs to another server.");
                }
                if (!audience.Available || audience.Partial)
                {
                    logger.LogWarning("Tagged message audience unavailable for execution {ExecutionId}: {Detail}",
                        execution.ExecutionId, audience.Detail);
                    return Wait(audience.Detail ?? "Waiting for a complete authoritative recipient roster.",
                        clock.GetUtcNow().AddSeconds(10), RuleExecutionDiagnosticCode.AudienceUnavailable);
                }
                audience = audience with { Recipients = audience.Recipients.DistinctBy(item => item.ConnectionId).ToArray() };
            }

            var preparedText = await text.PrepareAsync(action, invocation, execution.Trigger.OccurredAtUtc,
                execution.FeatureId, rule.Id, ct).ConfigureAwait(false);
            if ((progress.Diagnostics is not null || preparedText.Diagnostics.Count != 0) &&
                (progress.Diagnostics ??= new()).ReplaceActionText(rule.Id, action.Id, preparedText.Diagnostics, clock.GetUtcNow(), logger))
            {
                execution = await FeatureExecutionOrchestrator.PersistProgressAsync(repository, execution, progress,
                    execution.RuleIndex, execution.ActionIndex, execution.State, logger, clock.GetUtcNow(), ct).ConfigureAwait(false);
            }
            var resolved = preparedText.Action;
            var prepared = MessagePreparation.Prepare(String(resolved, "message"), Strings(resolved, "messages"), budget.Budget);
            if (!prepared.IsValid)
            {
                if (prepared.Errors.All(error => error.Code != "InvalidBudget"))
                {
                    return Complete(ActionExecutionOutcome.SkippedUnsupported,
                        "Message preparation failed: " + string.Join("; ", prepared.Errors.Select(error => error.Message)),
                        RuleExecutionDiagnosticCode.UnsupportedDelivery);
                }
                throw new InvalidOperationException("Message preparation failed: " + string.Join("; ", prepared.Errors));
            }
            if (budget.Constraints is { } constraints &&
                (prepared.Items.Any(item => ViolatesConstraints(item, constraints)) ||
                    prepared.Fragments.Any(fragment => ViolatesConstraints(fragment.Text, constraints))))
            {
                return Complete(ActionExecutionOutcome.SkippedUnsupported,
                    "The rendered message contains characters unsupported by the RCON transport.",
                    RuleExecutionDiagnosticCode.UnsupportedDelivery);
            }
            var parent = commandFactory(resolved);
            var targets = audience is null ? new RuleMessageRecipientTarget?[] { null } : audience.Recipients;
            var commands = new List<RuleActionCommand>();
            foreach (var target in targets)
            {
                foreach (var fragment in prepared.Fragments)
                {
                    var identity = new RuleDeliveryIdentity
                    {
                        ParentLogicalEffectId = parent.LogicalEffectId,
                        RecipientConnectionId = target?.ConnectionId,
                        MessageIndex = fragment.ItemIndex,
                        FragmentIndex = fragment.FragmentIndex
                    };
                    commands.Add(parent with
                    {
                        LogicalEffectId = identity.ToLogicalEffectId(),
                        DeliveryIdentity = identity,
                        RecipientTarget = target,
                        ExpiresAtUtc = expiry,
                        Effect = new()
                        {
                            Type = action.TypeId == "message.public" ? RuleNativeEffectType.PublicMessage : RuleNativeEffectType.PrivateMessage,
                            Message = fragment.Text
                        }
                    });
                }
            }
            foreach (var command in commands)
            {
                var validation = await executor.AvailabilityAsync(resolved, command, invocation, expiry, ct).ConfigureAwait(false);
                if (validation.Deferred)
                {
                    return Wait("Waiting for complete message delivery eligibility.", clock.GetUtcNow().AddSeconds(10),
                        RuleExecutionDiagnosticCode.WaitingForDelivery);
                }
                if (validation.Unavailable is { Outcome: not ActionExecutionOutcome.Stale } invalid)
                {
                    return Complete(invalid.Outcome, invalid.Detail,
                        invalid.Outcome == ActionExecutionOutcome.SkippedUnsupported ? RuleExecutionDiagnosticCode.UnsupportedDelivery : null);
                }
            }
            var preparationResponse = await repository.RuleExecutions.V1.PrepareAction(execution.GameServerId, execution.ExecutionId, new()
            {
                ExpectedVersion = execution.Version,
                RuleIndex = ruleIndex,
                ActionIndex = actionIndex,
                ResolvedParametersJson = JsonSerializer.Serialize(resolved.Parameters, RulesSerialization.Options),
                Commands = commands.ToArray(),
                AudienceSnapshot = audience
            }, ct).ConfigureAwait(false);
            FeatureExecutionOrchestrator.Require(preparationResponse, "freeze message delivery plan");
            execution = FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.GetExecution(
                execution.GameServerId, execution.ExecutionId, ct).ConfigureAwait(false), "load frozen message delivery plan");
            plan = execution.PreparedActions.Single(item => item.RuleIndex == ruleIndex && item.ActionIndex == actionIndex);
        }
        var resolvedAction = new RuleActionDefinition
        {
            Id = action.Id,
            TypeId = action.TypeId,
            Parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(plan.ResolvedParametersJson, RulesSerialization.Options)
                ?? throw new InvalidOperationException("Frozen message parameters are missing.")
        };
        var completed = new List<RuleActionExecutionDto>();
        foreach (var command in plan.Commands)
        {
            var stored = execution.Actions.SingleOrDefault(item => item.LogicalEffectId == command.LogicalEffectId);
            if (stored is { Outcome: RuleActionExecutionOutcome.Claimed or RuleActionExecutionOutcome.Dispatched })
            {
                if (command.ExpiresAtUtc > clock.GetUtcNow())
                {
                    return new(execution, AwaitingOutcome: true);
                }
                execution = await RecordAsync(execution, command, new(ActionExecutionOutcome.OutcomeUnknown,
                    "Claim expired without confirmed receipt; this fragment will not be resent."), ruleIndex, actionIndex, progress, ct)
                    .ConfigureAwait(false);
                stored = execution.Actions.Single(item => item.LogicalEffectId == command.LogicalEffectId);
            }
            if (stored is not null)
            {
                completed.Add(stored);
                continue;
            }
            if (protectionDecision is { Deferred: true } waiting && clock.GetUtcNow() < expiry)
            {
                return Wait(waiting.Detail, clock.GetUtcNow().AddSeconds(10), RuleExecutionDiagnosticCode.WaitingForProtection);
            }
            var failedEarlierFragment = completed.Any(item =>
                item.Command.DeliveryIdentity?.RecipientConnectionId == command.DeliveryIdentity?.RecipientConnectionId &&
                item.Outcome is RuleActionExecutionOutcome.Stale or RuleActionExecutionOutcome.Failed or
                    RuleActionExecutionOutcome.SkippedUnsupported or RuleActionExecutionOutcome.SkippedDependency);
            var lastDispatch = completed.Where(item => item.Outcome is RuleActionExecutionOutcome.Applied or
                RuleActionExecutionOutcome.OutcomeUnknown or RuleActionExecutionOutcome.Dispatched)
                .Select(item => item.UpdatedAtUtc > item.ClaimedAtUtc ? item.UpdatedAtUtc : item.ClaimedAtUtc)
                .DefaultIfEmpty(DateTimeOffset.MinValue).Max();
            if (command.ExpiresAtUtc > clock.GetUtcNow() && protectionDecision is null && !failedEarlierFragment &&
                clock.GetUtcNow() < lastDispatch.AddSeconds(1))
            {
                return Wait("Pacing ordered message fragments.", lastDispatch.AddSeconds(1), RuleExecutionDiagnosticCode.WaitingForDelivery);
            }
            var expiredBeforeEligibility = command.ExpiresAtUtc <= clock.GetUtcNow();
            var availability = expiredBeforeEligibility
                ? new ActionAvailability(Unavailable: new(ActionExecutionOutcome.Stale, "Message delivery window expired."))
                : protectionDecision is { } suppressed
                    ? new(Unavailable: new(ActionExecutionOutcome.SkippedDependency, suppressed.Detail))
                : failedEarlierFragment
                    ? new(Unavailable: new(ActionExecutionOutcome.SkippedDependency, "An earlier fragment for this recipient failed."))
                    : await executor.AvailabilityAsync(resolvedAction, command, invocation, expiry, ct).ConfigureAwait(false);
            if (availability.Deferred)
            {
                return Wait("Waiting for message recipient connection evidence.", clock.GetUtcNow().AddSeconds(10),
                    RuleExecutionDiagnosticCode.WaitingForDelivery);
            }
            if (availability.Unavailable is { Outcome: ActionExecutionOutcome.Failed } failed)
            {
                logger.LogWarning("Message eligibility failed for fragment {LogicalEffectId}: {Detail}",
                    command.LogicalEffectId, failed.Detail);
                return Wait(failed.Detail ?? "Native message eligibility failed.", clock.GetUtcNow().AddSeconds(10),
                    RuleExecutionDiagnosticCode.WaitingForDelivery);
            }
            var response = await repository.RuleExecutions.V1.ClaimAction(execution.GameServerId, execution.ExecutionId, new()
            {
                ExpectedVersion = execution.Version,
                RuleIndex = ruleIndex,
                ActionIndex = actionIndex,
                LogicalEffectId = command.LogicalEffectId,
                Command = command,
                ResolvedParametersJson = plan.ResolvedParametersJson,
                TerminalOutcome = availability.Unavailable is { } terminal
                    ? Enum.Parse<RuleActionExecutionOutcome>(terminal.Outcome.ToString()) : null,
                TerminalDetail = availability.Unavailable?.Detail
            }, ct).ConfigureAwait(false);
            var claim = FeatureExecutionOrchestrator.Require(response, "claim frozen message fragment");
            execution = claim.Execution;
            if (claim.Action.Outcome is not (RuleActionExecutionOutcome.Claimed or RuleActionExecutionOutcome.Dispatched))
            {
                var unavailableDiagnostic = expiredBeforeEligibility
                    ? RuleExecutionDiagnosticCode.Expired
                    : availability.ConnectionUnavailable ? RuleExecutionDiagnosticCode.ConnectionUnavailable : (RuleExecutionDiagnosticCode?)null;
                if (response.StatusCode == HttpStatusCode.Created &&
                    claim.Action.Outcome == RuleActionExecutionOutcome.Stale && unavailableDiagnostic is { } code)
                {
                    var added = (progress.Diagnostics ??= new()).Add(new()
                    {
                        Code = code,
                        RuleId = rule.Id,
                        ActionId = action.Id,
                        Detail = RuleExecutionDiagnostics.SafeDetail(claim.Action.Detail, logger),
                        ObservedAtUtc = clock.GetUtcNow()
                    });
                    if (added)
                    {
                        execution = await FeatureExecutionOrchestrator.PersistProgressAsync(repository, execution, progress,
                            execution.RuleIndex, execution.ActionIndex, execution.State, logger, clock.GetUtcNow(), ct).ConfigureAwait(false);
                    }
                }
                completed.Add(claim.Action);
                continue;
            }
            if (response.StatusCode != HttpStatusCode.Created)
            {
                return new(execution, AwaitingOutcome: true);
            }
            ActionExecutionResult result;
            if (availability.Unavailable is { } unavailable)
            {
                result = unavailable;
            }
            else if (command.Mode == RuleExecutionMode.DryRun)
            {
                result = new(ActionExecutionOutcome.WouldExecute);
            }
            else
            {
                try
                {
                    result = await executor.ExecuteAsync(resolvedAction, claim.Action.Command, invocation, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await RecordAsync(execution, command, new(ActionExecutionOutcome.OutcomeUnknown,
                        "Message execution cancelled after claim."), ruleIndex, actionIndex, progress, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Message fragment {LogicalEffectId} has an ambiguous outcome", command.LogicalEffectId);
                    result = new(ActionExecutionOutcome.OutcomeUnknown, "Message executor failed after claim; fragment will not be resent.");
                }
            }
            execution = await RecordAsync(execution, command, result, ruleIndex, actionIndex, progress, ct).ConfigureAwait(false);
            if (result.IsPending)
            {
                return new(execution, AwaitingOutcome: true)
                {
                    Commands = result.NativeCommand is { } native ? [native] : []
                };
            }
            completed.Add(execution.Actions.Single(item => item.LogicalEffectId == command.LogicalEffectId));
        }

        var outcome = completed.Count == 0
            ? progress.Mode == RuleExecutionMode.DryRun ? ActionExecutionOutcome.WouldExecute : ActionExecutionOutcome.Applied :
            completed.Any(item => item.Outcome is RuleActionExecutionOutcome.Failed or RuleActionExecutionOutcome.Stale or
                RuleActionExecutionOutcome.SkippedUnsupported or RuleActionExecutionOutcome.SkippedDependency)
                ? ActionExecutionOutcome.Failed :
            completed.All(item => item.Outcome is RuleActionExecutionOutcome.Applied or RuleActionExecutionOutcome.WouldExecute)
                ? progress.Mode == RuleExecutionMode.DryRun ? ActionExecutionOutcome.WouldExecute : ActionExecutionOutcome.Applied
                : completed.Any(item => item.Outcome == RuleActionExecutionOutcome.OutcomeUnknown)
                    ? ActionExecutionOutcome.OutcomeUnknown
                    : ActionExecutionOutcome.Failed;
        var terminalDiagnostic = protectionDecision is { Deferred: false } suppression &&
            completed.Any(item => item.Outcome == RuleActionExecutionOutcome.SkippedDependency && item.Detail == suppression.Detail)
            ? RuleExecutionDiagnosticCode.SuppressedByProtection
            : tagged && plan.Commands.Length == 0
                ? RuleExecutionDiagnosticCode.AudienceEmpty
                : (RuleExecutionDiagnosticCode?)null;
        return Complete(outcome, completed.Count == 0 ? "No recipients." :
            $"{completed.Count}/{plan.Commands.Length} fragments terminal; transport acceptance is not confirmed delivery.",
            terminalDiagnostic);

        RuleMessageDispatchResult Complete(ActionExecutionOutcome outcome, string? detail, RuleExecutionDiagnosticCode? code = null) =>
            new(execution, new(outcome, detail), DiagnosticCode: code);

        RuleMessageDispatchResult Wait(string detail, DateTimeOffset dueAtUtc, RuleExecutionDiagnosticCode code) =>
            new(execution, ResumeAtUtc: dueAtUtc < expiry ? dueAtUtc : expiry, Detail: detail, DiagnosticCode: code);
    }

    private async Task<(bool Deferred, string Detail)?> ProtectionAsync(
        RuleFeatureExecutionDto execution, RuleInvocation invocation, CancellationToken ct)
    {
        var connections = FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.GetConnectionExecutions(
            execution.GameServerId, execution.ConnectionId, ct).ConfigureAwait(false), "check durable connection protection");
        var protections = connections.Where(item =>
            item.FeatureId is RulesEngineCatalogueV1.VpnProtectionFeature or RulesEngineCatalogueV1.SiegeModeFeature).ToArray();
        if (protections.SelectMany(item => item.Actions).Any(item => item.Mode == RuleExecutionMode.Enforce &&
            item.Command.Effect.Type == RuleNativeEffectType.DropPlayer &&
            item.Outcome is RuleActionExecutionOutcome.Claimed or RuleActionExecutionOutcome.Dispatched or
                RuleActionExecutionOutcome.Applied or RuleActionExecutionOutcome.OutcomeUnknown))
        {
            return (false, "A durable protection decision suppresses the greeting.");
        }
        if (invocation.ProtectionResults.Any(item => item.Failure is not null) ||
            protections.Any(item => item.State is not (RuleFeatureExecutionState.Completed or RuleFeatureExecutionState.Stopped)) ||
            execution.Coordination?.PredecessorExecutionIds.Any(id => protections.All(item => item.ExecutionId != id)) == true)
        {
            return (true, "Waiting for durable protection evaluations.");
        }
        return null;
    }

    private async Task<RuleFeatureExecutionDto> RecordAsync(
        RuleFeatureExecutionDto execution, RuleActionCommand command, ActionExecutionResult result,
        int ruleIndex, int actionIndex, RuleExecutionProgress progress, CancellationToken ct)
    {
        var updated = FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.UpdateActionOutcome(
            execution.GameServerId, execution.ExecutionId, command.ActionId, new()
            {
                ExpectedVersion = execution.Version,
                Result = new()
                {
                    GameServerId = execution.GameServerId,
                    ExecutionId = execution.ExecutionId,
                    ActionId = command.ActionId,
                    LogicalEffectId = command.LogicalEffectId,
                    Correlation = command.Correlation,
                    Outcome = Enum.Parse<RuleActionExecutionOutcome>(result.Outcome.ToString()),
                    Detail = result.Detail,
                    OccurredAtUtc = clock.GetUtcNow()
                },
                NextRuleIndex = ruleIndex,
                NextActionIndex = actionIndex,
                NextState = result.IsPending ? RuleFeatureExecutionState.AwaitingActionOutcome : RuleFeatureExecutionState.Evaluating,
                DecisionSnapshotJson = JsonSerializer.Serialize(progress, RulesSerialization.Options)
            }, ct).ConfigureAwait(false), "record message fragment outcome");
        return updated.Execution;
    }

    private static string? String(RuleActionDefinition action, string key) =>
        action.Parameters.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[]? Strings(RuleActionDefinition action, string key) =>
        action.Parameters.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString()!).ToArray() : null;

    private static bool ViolatesConstraints(string text, RconMessageCharacterConstraintsDto constraints) =>
        text.Any(character => (constraints.AsciiOnly && character > 127) ||
            (constraints.ControlsDisallowed && char.IsControl(character)) ||
            constraints.DisallowedCharacters.Contains(character)) ||
        (constraints.DisallowLocalizationInserts && ContainsLocalizationInsert(text));

    private static bool ContainsLocalizationInsert(string text)
    {
        for (var index = 0; index + 2 < text.Length; index++)
        {
            if (text[index] == '&' && text[index + 1] == '&' && char.IsAsciiDigit(text[index + 2]))
            {
                return true;
            }
        }
        return false;
    }
}
