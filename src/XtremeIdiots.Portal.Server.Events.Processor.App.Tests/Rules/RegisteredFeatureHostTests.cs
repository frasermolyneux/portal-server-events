using Microsoft.Extensions.Logging.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

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

        public Task<Guid?> ProcessAsync(RuleInvocation invocation, CancellationToken ct)
        {
            Calls++;
            return process is null ? Task.FromResult<Guid?>(Guid.NewGuid()) : process();
        }
    }
}
