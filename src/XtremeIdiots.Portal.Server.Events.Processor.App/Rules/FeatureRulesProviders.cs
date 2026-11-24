using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.SiegeMode;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.SiegeMode;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal sealed record FeatureRulesSnapshot(
    string FeatureId,
    IReadOnlyList<RuleDefinition> Rules,
    RuleExecutionMode Mode,
    Guid? SiegeSessionId = null,
    DateTimeOffset? ExpiresAtUtc = null,
    ActivationIdentifierHistory? Baseline = null)
{
    public string RulesJson => JsonSerializer.Serialize(new FeatureRuleSetDocument
    {
        FeatureId = FeatureId,
        Enabled = true,
        Rules = Rules.OrderBy(rule => rule.Order).ToList()
    }, RulesSerialization.Options);
    public string Revision => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RulesJson)));
}

internal static class RulesSerialization
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

internal interface IFeatureRulesProvider
{
    string FeatureId { get; }
    Task<FeatureRulesSnapshot?> GetAsync(RuleTriggerEnvelope trigger, CancellationToken ct);
}

internal sealed class ConfiguredFeatureRulesProvider(
    IRepositoryApiClient repository,
    IMemoryCache cache,
    ILogger<ConfiguredFeatureRulesProvider> logger)
{
    public async Task<EffectiveFeatureRuleSet> GetAsync(Guid serverId, string featureId, string settingsNamespace, CancellationToken ct)
    {
        var key = $"rules:{serverId:N}:{featureId}";
        if (cache.TryGetValue(key, out EffectiveFeatureRuleSet? cached) && cached is not null)
        {
            return cached;
        }

        var globalTask = repository.GlobalConfigurations.V1.GetConfiguration(settingsNamespace, ct);
        var serverTask = repository.GameServerConfigurations.V1.GetConfiguration(serverId, settingsNamespace, ct);
        await Task.WhenAll(globalTask, serverTask).ConfigureAwait(false);
        var globalResponse = await globalTask.ConfigureAwait(false);
        var serverResponse = await serverTask.ConfigureAwait(false);
        if ((!globalResponse.IsSuccess && !globalResponse.IsNotFound) ||
            (!serverResponse.IsSuccess && !serverResponse.IsNotFound))
        {
            throw new InvalidOperationException(
                $"Rules settings lookup failed for {featureId}: global={globalResponse.StatusCode}, server={serverResponse.StatusCode}.");
        }

        try
        {
            var globalJson = globalResponse.Result?.Data?.Configuration;
            var serverJson = serverResponse.Result?.Data?.Configuration;
            var global = string.IsNullOrWhiteSpace(globalJson)
                ? new FeatureRuleSetDocument { FeatureId = featureId, Enabled = false }
                : JsonSerializer.Deserialize<FeatureRuleSetDocument>(globalJson, RulesSerialization.Options);
            var server = string.IsNullOrWhiteSpace(serverJson)
                ? null
                : JsonSerializer.Deserialize<FeatureRuleSetOverrideDocument>(serverJson, RulesSerialization.Options);
            var validator = new RuleSetValidator();
            var options = RuleSetValidationOptions.ForFeature(featureId);
            var globalValidation = validator.Validate(global, options);
            var overrideValidation = server is null ? null : validator.ValidateOverride(server, options);
            if (!globalValidation.IsValid || overrideValidation?.IsValid == false)
            {
                throw new InvalidOperationException($"Invalid rules configuration for {featureId}: " +
                    string.Join("; ", globalValidation.Errors.Concat(overrideValidation?.Errors ?? [])));
            }

            var effective = FeatureRuleSetMerger.Merge(global!, server);
            cache.Set(key, effective, TimeSpan.FromMinutes(3));
            return effective;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Malformed rules configuration for feature {FeatureId} on server {ServerId}", featureId, serverId);
            throw new InvalidOperationException($"Malformed rules configuration for {featureId}.", ex);
        }
    }
}

internal sealed class VpnFeatureRulesProvider(ConfiguredFeatureRulesProvider settings) : IFeatureRulesProvider
{
    public string FeatureId => RulesEngineCatalogueV1.VpnProtectionFeature;

    public async Task<FeatureRulesSnapshot?> GetAsync(RuleTriggerEnvelope trigger, CancellationToken ct)
    {
        var rules = await settings.GetAsync(trigger.GameServerId, FeatureId, VpnProtectionSettingsConstants.Namespace, ct).ConfigureAwait(false);
        return rules.Enabled ? new(FeatureId, rules.Rules, RuleExecutionMode.Enforce) : null;
    }
}

internal interface ISiegeActivationHistoryProvider
{
    Task<ActivationIdentifierHistory> GetAsync(SiegeSessionDto session, RuleTriggerEnvelope trigger, CancellationToken ct);
}

internal sealed class SiegeFeatureRulesProvider(
    ConfiguredFeatureRulesProvider settings,
    IRepositoryApiClient repository,
    ISiegeActivationHistoryProvider history) : IFeatureRulesProvider
{
    public string FeatureId => RulesEngineCatalogueV1.SiegeModeFeature;

    public async Task<FeatureRulesSnapshot?> GetAsync(RuleTriggerEnvelope trigger, CancellationToken ct)
    {
        var configured = await settings.GetAsync(trigger.GameServerId, FeatureId, SiegeModeSettingsConstants.Namespace, ct).ConfigureAwait(false);
        if (!configured.Enabled)
        {
            return null;
        }

        var response = await repository.SiegeMode.V1.GetCurrentSession(trigger.GameServerId, ct).ConfigureAwait(false);
        if (response.IsNotFound || response.IsSuccess && response.Result?.Data is null)
        {
            return null;
        }
        if (!response.IsSuccess || response.Result?.Data is not { } session)
        {
            throw new InvalidOperationException($"Siege session lookup failed: {response.StatusCode}.");
        }
        if (session.GameServerId != trigger.GameServerId || session.GameType != (int)trigger.GameType ||
            session.EndedAtUtc is not null || session.ExpiresAtUtc <= DateTime.UtcNow ||
            trigger.OccurredAtUtc.UtcDateTime <= session.StartedAtUtc ||
            session.Mode is not (SiegeSessionMode.DryRun or SiegeSessionMode.Enforce))
        {
            return null;
        }

        var captured = JsonSerializer.Deserialize<SiegeModeSettingsDocument>(session.SettingsJson, RulesSerialization.Options);
        var validation = new SiegeModeSettingsValidator().Validate(captured);
        if (!validation.IsValid || captured is null)
        {
            throw new InvalidOperationException("Invalid captured Siege rules: " + string.Join("; ", validation.Errors));
        }

        var baseline = await history.GetAsync(session, trigger, ct).ConfigureAwait(false);
        return new FeatureRulesSnapshot(FeatureId, captured.Rules,
            session.Mode == SiegeSessionMode.DryRun ? RuleExecutionMode.DryRun : RuleExecutionMode.Enforce,
            session.SessionId, new DateTimeOffset(DateTime.SpecifyKind(session.ExpiresAtUtc, DateTimeKind.Utc)), baseline);
    }
}
