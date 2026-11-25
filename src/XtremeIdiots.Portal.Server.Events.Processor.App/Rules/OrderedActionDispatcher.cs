using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal enum ActionExecutionOutcome
{
    Claimed,
    Dispatched,
    Applied,
    Failed,
    Stale,
    SkippedUnsupported,
    SkippedDependency,
    WouldExecute,
    OutcomeUnknown
}

internal sealed record ActionExecutionResult(
    ActionExecutionOutcome Outcome,
    string? Detail = null,
    Repository.Abstractions.Models.V1.RulesEngine.RuleActionCommand? NativeCommand = null,
    Guid? AdminActionId = null)
{
    public bool IsPending => Outcome is ActionExecutionOutcome.Claimed or ActionExecutionOutcome.Dispatched;
    public bool SatisfiesDependency => Outcome is ActionExecutionOutcome.Applied or ActionExecutionOutcome.WouldExecute;
}

internal sealed record PlannedRuleAction(
    string ActionId,
    IReadOnlyList<string> DependsOnActionIds,
    string? UnsupportedReason,
    Func<CancellationToken, Task<ActionExecutionResult>> DispatchAsync,
    Func<CancellationToken, Task<ActionAvailability>>? AvailabilityAsync = null);

internal sealed record ActionAvailability(
    bool Deferred = false, ActionExecutionResult? Unavailable = null, bool ConnectionUnavailable = false);

internal sealed record ActionExecutionClaim(bool Acquired, ActionExecutionResult State);

internal interface IRuleActionExecutionStore
{
    Task<ActionExecutionClaim> ClaimAsync(Guid executionId, string actionId, CancellationToken ct);
    Task RecordAsync(Guid executionId, string actionId, ActionExecutionResult result, CancellationToken ct);
}

internal sealed record ActionSequenceResult(
    bool AwaitingOutcome,
    IReadOnlyDictionary<string, ActionExecutionResult> Outcomes,
    bool AwaitingContext = false);

internal sealed class OrderedActionDispatcher(
    IRuleActionExecutionStore store,
    ILogger<OrderedActionDispatcher> logger)
{
    public async Task<ActionSequenceResult> DispatchAsync(
        Guid executionId,
        IReadOnlyList<PlannedRuleAction> actions,
        bool dryRun,
        CancellationToken ct,
        IReadOnlyDictionary<string, ActionExecutionResult>? precedingOutcomes = null)
    {
        ValidatePlan(actions, precedingOutcomes?.Keys ?? []);
        var outcomes = precedingOutcomes is null
            ? new Dictionary<string, ActionExecutionResult>(StringComparer.Ordinal)
            : new Dictionary<string, ActionExecutionResult>(precedingOutcomes, StringComparer.Ordinal);
        foreach (var action in actions)
        {
            ct.ThrowIfCancellationRequested();
            var dependencyFailed = action.DependsOnActionIds.Any(id => !outcomes[id].SatisfiesDependency);
            var availability = !dependencyFailed && action.AvailabilityAsync is not null
                ? await action.AvailabilityAsync(ct).ConfigureAwait(false)
                : new ActionAvailability();
            if (availability.Deferred)
            {
                return new(false, outcomes, AwaitingContext: true);
            }

            var claim = await store.ClaimAsync(executionId, action.ActionId, ct).ConfigureAwait(false);
            if (!claim.Acquired)
            {
                outcomes.Add(action.ActionId, claim.State);
                if (claim.State.IsPending)
                {
                    return new(true, outcomes);
                }

                continue;
            }

            ActionExecutionResult result;
            if (dependencyFailed)
            {
                result = new(ActionExecutionOutcome.SkippedDependency, "A prerequisite action did not succeed.");
            }
            else if (availability.Unavailable is { } unavailable)
            {
                result = unavailable;
            }
            else if (action.UnsupportedReason is not null)
            {
                result = new(ActionExecutionOutcome.SkippedUnsupported, action.UnsupportedReason);
            }
            else if (dryRun)
            {
                result = new(ActionExecutionOutcome.WouldExecute);
            }
            else
            {
                result = await DispatchClaimedAsync(executionId, action, ct).ConfigureAwait(false);
            }

            await store.RecordAsync(executionId, action.ActionId, result, ct).ConfigureAwait(false);
            outcomes.Add(action.ActionId, result);
            if (result.IsPending)
            {
                return new(true, outcomes);
            }
        }

        return new(false, outcomes);
    }

    private async Task<ActionExecutionResult> DispatchClaimedAsync(
        Guid executionId,
        PlannedRuleAction action,
        CancellationToken ct)
    {
        try
        {
            var result = await action.DispatchAsync(ct).ConfigureAwait(false);
            if (result.Outcome == ActionExecutionOutcome.Claimed)
            {
                throw new InvalidOperationException("An executor must report dispatch or a terminal action outcome.");
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation after a claim does not prove that the external effect did not happen.
            await store.RecordAsync(executionId, action.ActionId,
                new(ActionExecutionOutcome.OutcomeUnknown, "Execution was cancelled after claiming the action."),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rules action {ActionId} in execution {ExecutionId} has an ambiguous outcome",
                action.ActionId, executionId);
            return new(ActionExecutionOutcome.OutcomeUnknown, "Executor failed without a confirmed outcome.");
        }
    }

    private static void ValidatePlan(IReadOnlyList<PlannedRuleAction> actions, IEnumerable<string> completedIds)
    {
        var precedingIds = new HashSet<string>(completedIds, StringComparer.Ordinal);
        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.ActionId) ||
                precedingIds.Contains(action.ActionId) ||
                action.DependsOnActionIds.Any(id => !precedingIds.Contains(id)))
            {
                throw new ArgumentException("Actions require unique IDs and dependencies on preceding actions.", nameof(actions));
            }

            precedingIds.Add(action.ActionId);
        }
    }
}
