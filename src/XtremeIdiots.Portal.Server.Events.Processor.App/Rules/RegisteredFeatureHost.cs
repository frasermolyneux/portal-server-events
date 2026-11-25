using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using MX.GeoLocation.Abstractions.Models.V1_1;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal sealed record RuleInvocation(RuleTriggerEnvelope Trigger, RuleFactSnapshotDto Facts, IpIntelligenceDto? Intelligence)
{
    public ConcurrentBag<RuleActionCommand> Commands { get; } = [];
    public ConcurrentDictionary<string, Guid> ExecutionIds { get; } = new(StringComparer.Ordinal);
    public ConcurrentBag<Guid> PendingExecutionIds { get; } = [];
    public Lazy<Task<IpIntelligenceDto?>>? IntelligenceLookup { get; init; }
    public bool SuppressWelcome { get; set; }
    public IReadOnlyList<FeatureProcessingResult> ProtectionResults { get; set; } = [];
}

internal interface IRegisteredRuleFeature
{
    string FeatureId { get; }
    IReadOnlySet<string> TriggerIds { get; }
    Task<Guid?> ProcessAsync(RuleInvocation invocation, CancellationToken ct);
}

internal sealed record FeatureProcessingResult(string FeatureId, Guid? ExecutionId, Exception? Failure = null);

internal sealed class RegisteredFeatureHost
{
    private readonly IReadOnlyDictionary<string, IRegisteredRuleFeature[]> subscriptions;
    private readonly ILogger<RegisteredFeatureHost> logger;

    public RegisteredFeatureHost(IEnumerable<IRegisteredRuleFeature> features, ILogger<RegisteredFeatureHost> logger)
    {
        this.logger = logger;
        var registrations = features.ToArray();
        if (registrations.Any(static feature => string.IsNullOrWhiteSpace(feature.FeatureId)) ||
            registrations.Select(static feature => feature.FeatureId).Distinct(StringComparer.Ordinal).Count() != registrations.Length)
        {
            throw new ArgumentException("Rule features must have unique, nonempty IDs.", nameof(features));
        }

        subscriptions = registrations
            .SelectMany(static feature => feature.TriggerIds.Select(triggerId => (triggerId, feature)))
            .GroupBy(static subscription => subscription.triggerId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static subscription => subscription.feature).ToArray(),
                StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<FeatureProcessingResult>> ProcessAsync(
        RuleInvocation invocation, CancellationToken ct, string? featureId = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!subscriptions.TryGetValue(invocation.Trigger.TriggerId, out var features))
        {
            return [];
        }

        var selected = features.Where(feature => featureId is null || feature.FeatureId == featureId).ToArray();
        var protectionTasks = selected.Where(IsProtection)
            .Select(feature => ProcessFeatureAsync(feature, invocation, ct)).ToArray();
        var otherTasks = selected.Where(feature => !IsProtection(feature)).Select(async feature =>
        {
            if (feature.FeatureId == "welcome-messages")
            {
                invocation.ProtectionResults = await Task.WhenAll(protectionTasks).ConfigureAwait(false);
            }
            return await ProcessFeatureAsync(feature, invocation, ct).ConfigureAwait(false);
        }).ToArray();
        return await Task.WhenAll(protectionTasks.Concat(otherTasks)).ConfigureAwait(false);
    }

    private static bool IsProtection(IRegisteredRuleFeature feature) =>
        feature.FeatureId is RulesEngineCatalogueV1.VpnProtectionFeature or RulesEngineCatalogueV1.SiegeModeFeature;

    private async Task<FeatureProcessingResult> ProcessFeatureAsync(
        IRegisteredRuleFeature feature,
        RuleInvocation invocation,
        CancellationToken ct)
    {
        try
        {
            var executionId = await feature.ProcessAsync(invocation, ct).ConfigureAwait(false);
            return new(feature.FeatureId, executionId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rules feature {FeatureId} failed for connection {ConnectionId} on server {ServerId}",
                feature.FeatureId, invocation.Trigger.ConnectionId, invocation.Trigger.GameServerId);
            return new(feature.FeatureId,
                invocation.ExecutionIds.TryGetValue(feature.FeatureId, out var executionId) ? executionId : null, ex);
        }
    }
}
