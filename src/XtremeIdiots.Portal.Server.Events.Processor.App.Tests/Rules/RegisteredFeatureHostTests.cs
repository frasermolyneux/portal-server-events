using Microsoft.Extensions.Logging.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class RegisteredFeatureHostTests
{
    private static readonly RuleInvocation Trigger = new(new RuleTriggerEnvelope
    {
        TriggerId = "player.connected",
        GameServerId = Guid.NewGuid(),
        GameType = GameType.CallOfDuty4x,
        ConnectionId = Guid.NewGuid(),
        PlayerGuid = "guid",
        SlotId = 4,
        OccurredAtUtc = DateTimeOffset.UtcNow
    }, new RuleFactSnapshotDto(), null);

    [Fact]
    public async Task RoutesOnlySubscribedFeatures()
    {
        var subscribed = new Feature("vpn", ["player.connected"]);
        var unrelated = new Feature("unrelated", ["other.trigger"]);
        var host = Create(subscribed, unrelated);

        var results = await host.ProcessAsync(Trigger, default);

        Assert.Equal("vpn", Assert.Single(results).FeatureId);
        Assert.Equal(1, subscribed.Calls);
        Assert.Equal(0, unrelated.Calls);
    }

    [Fact]
    public async Task FailureInOneFeature_DoesNotPreventIndependentFeatureProcessing()
    {
        var failure = new InvalidOperationException("Provider unavailable.");
        var broken = new Feature("vpn", ["player.connected"], () => throw failure);
        var working = new Feature("siege", ["player.connected"]);

        var results = await Create(broken, working).ProcessAsync(Trigger, default);

        Assert.Same(failure, results.Single(static result => result.FeatureId == "vpn").Failure);
        Assert.NotNull(results.Single(static result => result.FeatureId == "siege").ExecutionId);
        Assert.Equal(1, working.Calls);
    }

    [Fact]
    public async Task FeatureAwaitingProvider_DoesNotSerializeOtherFeature()
    {
        var release = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new Feature("vpn", ["player.connected"], () => release.Task);
        var independent = new Feature("siege", ["player.connected"]);

        var processing = Create(waiting, independent).ProcessAsync(Trigger, default);

        Assert.Equal(1, independent.Calls);
        Assert.False(processing.IsCompleted);
        release.SetResult(Guid.NewGuid());
        Assert.Equal(2, (await processing).Count);
    }

    [Fact]
    public async Task WelcomeWaitsForConcurrentProtectionAdmissionsButNotUnrelatedFeatures()
    {
        var releaseVpn = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSiege = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIndependent = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vpn = new Feature(RulesEngineCatalogueV1.VpnProtectionFeature, ["player.connected"], () => releaseVpn.Task);
        var siege = new Feature(RulesEngineCatalogueV1.SiegeModeFeature, ["player.connected"], () => releaseSiege.Task);
        var independent = new Feature("independent", ["player.connected"], () => releaseIndependent.Task);
        var welcome = new Feature("welcome-messages", ["player.connected"]);
        var invocation = Trigger with { };
        var processing = Create(welcome, vpn, independent, siege).ProcessAsync(invocation, default);

        Assert.Equal(1, vpn.Calls);
        Assert.Equal(1, siege.Calls);
        Assert.Equal(1, independent.Calls);
        Assert.Equal(0, welcome.Calls);
        releaseVpn.SetResult(Guid.NewGuid());
        releaseSiege.SetResult(Guid.NewGuid());
        await welcome.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(processing.IsCompleted);
        Assert.Equal(2, invocation.ProtectionResults.Count);
        Assert.All(invocation.ProtectionResults, result => Assert.NotNull(result.ExecutionId));
        releaseIndependent.SetResult(Guid.NewGuid());
        await processing;
    }

    [Fact]
    public async Task WelcomeReceivesProtectionFailureInsteadOfTreatingItAsNoEnforcement()
    {
        var failure = new InvalidOperationException("Protection unavailable.");
        var protection = new Feature(RulesEngineCatalogueV1.VpnProtectionFeature, ["player.connected"], () => throw failure);
        var welcome = new Feature("welcome-messages", ["player.connected"]);
        var invocation = Trigger with { };

        await Create(welcome, protection).ProcessAsync(invocation, default);

        Assert.Same(failure, Assert.Single(invocation.ProtectionResults).Failure);
        Assert.Equal(1, welcome.Calls);
    }

    [Fact]
    public async Task Cancellation_PropagatesRatherThanBecomingFeatureFailure()
    {
        using var cts = new CancellationTokenSource();
        var feature = new Feature("vpn", ["player.connected"], () =>
        {
            cts.Cancel();
            return Task.FromCanceled<Guid?>(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(feature).ProcessAsync(Trigger, cts.Token));
    }

    [Fact]
    public void DuplicateFeatureRegistration_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(
            new Feature("vpn", ["player.connected"]),
            new Feature("vpn", ["player.ip-enriched"])));
    }

    private static RegisteredFeatureHost Create(params IRegisteredRuleFeature[] features) =>
        new(features, NullLogger<RegisteredFeatureHost>.Instance);

    private sealed class Feature(string featureId, string[] triggerIds, Func<Task<Guid?>>? process = null) : IRegisteredRuleFeature
    {
        public string FeatureId => featureId;
        public IReadOnlySet<string> TriggerIds { get; } = triggerIds.ToHashSet(StringComparer.Ordinal);
        public int Calls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Guid?> ProcessAsync(RuleInvocation invocation, CancellationToken ct)
        {
            Calls++;
            Started.TrySetResult();
            return process is null ? Task.FromResult<Guid?>(Guid.NewGuid()) : process();
        }
    }
}
