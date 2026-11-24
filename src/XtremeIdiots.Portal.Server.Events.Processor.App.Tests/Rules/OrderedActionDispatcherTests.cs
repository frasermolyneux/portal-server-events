using Microsoft.Extensions.Logging.Abstractions;

using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class OrderedActionDispatcherTests
{
    [Fact]
    public async Task UnsupportedAction_DoesNotPreventIndependentActions()
    {
        var effects = new List<string>();
        var store = new Store();
        var result = await Create(store).DispatchAsync(Guid.NewGuid(),
        [
            Action("unsupported", effects, unsupported: "No native duration-ban capability."),
            Action("dependent", effects, ["unsupported"]),
            Action("independent", effects)
        ], false, default);

        Assert.False(result.AwaitingOutcome);
        Assert.Equal(["independent"], effects);
        Assert.Equal(ActionExecutionOutcome.SkippedUnsupported, result.Outcomes["unsupported"].Outcome);
        Assert.Equal(ActionExecutionOutcome.SkippedDependency, result.Outcomes["dependent"].Outcome);
    }

    [Fact]
    public async Task NativeDispatch_WaitsForOutcomeBeforeFollowingActions()
    {
        var effects = new List<string>();
        var store = new Store();
        var executionId = Guid.NewGuid();
        var actions = new[]
        {
            Action("drop", effects, outcome: ActionExecutionOutcome.Dispatched),
            Action("message", effects)
        };
        var dispatcher = Create(store);

        var first = await dispatcher.DispatchAsync(executionId, actions, false, default);
        var duplicate = await dispatcher.DispatchAsync(executionId, actions, false, default);

        Assert.True(first.AwaitingOutcome);
        Assert.True(duplicate.AwaitingOutcome);
        Assert.Equal(["drop"], effects);

        await store.RecordAsync(executionId, "drop", new(ActionExecutionOutcome.Applied), default);
        var resumed = await dispatcher.DispatchAsync(executionId, actions, false, default);

        Assert.False(resumed.AwaitingOutcome);
        Assert.Equal(["drop", "message"], effects);
    }

    [Fact]
    public async Task ExecutorException_IsNotRetriedAndIndependentActionsContinue()
    {
        var effects = new List<string>();
        var store = new Store();
        var executionId = Guid.NewGuid();
        var attempts = 0;
        PlannedRuleAction[] actions =
        [
            new("drop", [], null, _ =>
            {
                attempts++;
                throw new IOException("Response lost after request was sent.");
            }),
            Action("dependent", effects, ["drop"]),
            Action("independent", effects)
        ];
        var dispatcher = Create(store);

        var first = await dispatcher.DispatchAsync(executionId, actions, false, default);
        await dispatcher.DispatchAsync(executionId, actions, false, default);

        Assert.Equal(1, attempts);
        Assert.Equal(ActionExecutionOutcome.OutcomeUnknown, first.Outcomes["drop"].Outcome);
        Assert.Equal(ActionExecutionOutcome.SkippedDependency, first.Outcomes["dependent"].Outcome);
        Assert.Equal(["independent"], effects);
    }

    [Fact]
    public async Task ClaimedButUnconfirmedAction_IsNeverRedispatched()
    {
        var effects = new List<string>();
        var store = new Store();
        var executionId = Guid.NewGuid();
        await store.ClaimAsync(executionId, "drop", default);

        var result = await Create(store).DispatchAsync(executionId, [Action("drop", effects)], false, default);

        Assert.True(result.AwaitingOutcome);
        Assert.Empty(effects);
    }

    [Fact]
    public async Task DryRun_DoesNotInvokeAnyExecutorIncludingDependentActions()
    {
        var effects = new List<string>();
        var store = new Store();

        var result = await Create(store).DispatchAsync(Guid.NewGuid(),
            [Action("record", effects), Action("forum", effects, ["record"])], true, default);

        Assert.Empty(effects);
        Assert.All(result.Outcomes.Values, static state => Assert.Equal(ActionExecutionOutcome.WouldExecute, state.Outcome));
    }

    [Fact]
    public async Task OutcomePersistenceFailure_StopsSequenceWithoutReplayingEffect()
    {
        var effects = new List<string>();
        var store = new Store { FailWrites = true };
        var executionId = Guid.NewGuid();
        var actions = new[] { Action("drop", effects), Action("message", effects) };
        var dispatcher = Create(store);

        await Assert.ThrowsAsync<IOException>(() => dispatcher.DispatchAsync(executionId, actions, false, default));
        store.FailWrites = false;
        var retry = await dispatcher.DispatchAsync(executionId, actions, false, default);

        Assert.True(retry.AwaitingOutcome);
        Assert.Equal(["drop"], effects);
    }

    [Fact]
    public async Task CancellationAfterClaim_IsPersistedAsUnknown()
    {
        using var cts = new CancellationTokenSource();
        var store = new Store();
        PlannedRuleAction action = new("drop", [], null, _ =>
        {
            cts.Cancel();
            return Task.FromCanceled<ActionExecutionResult>(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(store).DispatchAsync(Guid.NewGuid(), [action], false, cts.Token));

        Assert.Equal(ActionExecutionOutcome.OutcomeUnknown, Assert.Single(store.States.Values).Outcome);
    }

    [Fact]
    public async Task InvalidDependency_IsRejectedBeforeAnyClaim()
    {
        var store = new Store();
        await Assert.ThrowsAsync<ArgumentException>(() => Create(store).DispatchAsync(
            Guid.NewGuid(), [Action("first", [], ["later"]), Action("later", [])], false, default));

        Assert.Empty(store.States);
    }

    private static OrderedActionDispatcher Create(Store store) =>
        new(store, NullLogger<OrderedActionDispatcher>.Instance);

    private static PlannedRuleAction Action(
        string id,
        List<string> effects,
        string[]? dependencies = null,
        string? unsupported = null,
        ActionExecutionOutcome outcome = ActionExecutionOutcome.Applied) =>
        new(id, dependencies ?? [], unsupported, _ =>
        {
            effects.Add(id);
            return Task.FromResult(new ActionExecutionResult(outcome));
        });

    private sealed class Store : IRuleActionExecutionStore
    {
        public Dictionary<(Guid ExecutionId, string ActionId), ActionExecutionResult> States { get; } = [];
        public bool FailWrites { get; set; }

        public Task<ActionExecutionClaim> ClaimAsync(Guid executionId, string actionId, CancellationToken ct)
        {
            var key = (executionId, actionId);
            if (States.TryGetValue(key, out var existing))
            {
                return Task.FromResult(new ActionExecutionClaim(false, existing));
            }

            var claimed = new ActionExecutionResult(ActionExecutionOutcome.Claimed);
            States.Add(key, claimed);
            return Task.FromResult(new ActionExecutionClaim(true, claimed));
        }

        public Task RecordAsync(Guid executionId, string actionId, ActionExecutionResult result, CancellationToken ct)
        {
            if (FailWrites)
            {
                throw new IOException("Outcome store unavailable.");
            }

            States[(executionId, actionId)] = result;
            return Task.CompletedTask;
        }
    }
}
