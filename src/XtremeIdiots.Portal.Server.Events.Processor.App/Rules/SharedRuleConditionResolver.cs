using System.Globalization;
using System.Text.Json;

using MX.GeoLocation.Abstractions.Models.V1_1;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.RulesEngine.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal sealed record ActivationIdentifierHistory(IdentifierHistory Guid, IdentifierHistory Ip);

internal sealed class SharedRuleConditionResolver(
    RuleFactSnapshotDto facts,
    IpIntelligenceDto? intelligence,
    ActivationIdentifierHistory? baseline,
    IReadOnlySet<string> allowedConditions) : IRuleConditionResolver
{
    public ConditionEvaluation Resolve(RuleConditionNode condition, RuleEvaluationContext context)
    {
        if (condition.ConditionId is not { } id || !allowedConditions.Contains(id) ||
            condition.Parameters is not { } parameters)
        {
            return ConditionEvaluation.Unknown("Condition is unsupported by this feature or has invalid parameters.");
        }

        var definition = RulesEngineCatalogueV1.Current.Conditions.SingleOrDefault(item => item.ConditionId == id);
        if (definition is null ||
            definition.GameTypes.Count != 0 && !definition.GameTypes.Contains(context.GameType) ||
            definition.RuntimeCapabilities.Any(capability => !context.RuntimeCapabilities.Contains(capability)))
        {
            return ConditionEvaluation.Unknown("Required condition capability is unavailable.");
        }

        var operation = String(parameters, "operator");
        bool? result = id switch
        {
            "always" => true,
            "player.tags" => EvaluateTags(parameters, operation),
            "player.first-seen-guid" => EvaluateIdentifier(parameters, operation, true, context.OccurredAtUtc),
            "player.first-seen-ip" => EvaluateIdentifier(parameters, operation, false, context.OccurredAtUtc),
            "player.profile-age" => PlayerRuleConditions.ProfileAge(
                facts.ProfileHistoryAvailable ? facts.ProfileFirstSeenAtUtc : null,
                operation ?? "", String(parameters, "duration"), context.OccurredAtUtc),
            "vpn.intelligence" => IpIntelligenceRuleSignals.Evaluate(
                intelligence, String(parameters, "signal"), operation, String(parameters, "expectedValue")),
            _ => null
        };
        return result switch
        {
            true => ConditionEvaluation.True,
            false => ConditionEvaluation.False,
            null => ConditionEvaluation.Unknown($"Condition '{id}' has unavailable data, unsupported parameters or malformed configuration.")
        };
    }

    private bool? EvaluateTags(IReadOnlyDictionary<string, JsonElement> parameters, string? operation)
    {
        if (!parameters.TryGetValue("tags", out var tags) || tags.ValueKind != JsonValueKind.Array ||
            tags.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
        {
            return null;
        }

        return PlayerRuleConditions.Tags(facts.TagsAvailable ? facts.TagIds : null,
            tags.EnumerateArray().Select(static item => item.GetString()!).ToArray(), operation ?? "");
    }

    private bool? EvaluateIdentifier(
        IReadOnlyDictionary<string, JsonElement> parameters,
        string? operation,
        bool guid,
        DateTimeOffset occurredAtUtc)
    {
        var history = String(parameters, "referenceScope") switch
        {
            "ObservedHistory" => guid
                ? new IdentifierHistory(facts.GuidHistoryAvailable, facts.GuidFirstSeenAtUtc)
                : new IdentifierHistory(facts.IpHistoryAvailable, facts.IpFirstSeenAtUtc),
            "SiegeBaseline" => guid ? baseline?.Guid : baseline?.Ip,
            _ => null
        };
        return history is null ? null : PlayerRuleConditions.FirstSeen(
            history, operation ?? "", String(parameters, "value"), occurredAtUtc);
    }

    private static string? String(IReadOnlyDictionary<string, JsonElement> parameters, string name) =>
        parameters.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal static class IpIntelligenceRuleSignals
{
    public static bool? Evaluate(IpIntelligenceDto? intelligence, string? signal, string? operation, string? expected)
    {
        if (intelligence is null || expected is null)
        {
            return null;
        }

        object? actual = signal switch
        {
            "ProxyCheckRiskScore" => intelligence.ProxyCheck?.RiskScore,
            "ProxyCheckIsProxy" => intelligence.ProxyCheck?.IsProxy,
            "ProxyCheckIsVpn" => intelligence.ProxyCheck?.IsVpn,
            "ProxyCheckProxyType" => intelligence.ProxyCheck?.ProxyType,
            "ProxyCheckAsNumber" => intelligence.ProxyCheck?.AsNumber,
            "ProxyCheckAsOrganization" => intelligence.ProxyCheck?.AsOrganization,
            "MaxMindAnonymizerConfidence" => intelligence.Anonymizer?.Confidence,
            "MaxMindIsAnonymous" => intelligence.Anonymizer?.IsAnonymous,
            "MaxMindIsAnonymousVpn" => intelligence.Anonymizer?.IsAnonymousVpn,
            "MaxMindIsHostingProvider" => intelligence.Anonymizer?.IsHostingProvider,
            "MaxMindIsPublicProxy" => intelligence.Anonymizer?.IsPublicProxy,
            "MaxMindIsResidentialProxy" => intelligence.Anonymizer?.IsResidentialProxy,
            "MaxMindIsTorExitNode" => intelligence.Anonymizer?.IsTorExitNode,
            "MaxMindProviderName" => intelligence.Anonymizer?.ProviderName,
            "MaxMindStatus" => intelligence.MaxMindStatus.ToString(),
            "ProxyCheckStatus" => intelligence.ProxyCheckStatus.ToString(),
            "IsPartial" => intelligence.IsPartial,
            _ => null
        };
        return actual switch
        {
            bool boolean => bool.TryParse(expected, out var parsed) ? operation switch
            {
                "Equal" => boolean == parsed,
                "NotEqual" => boolean != parsed,
                _ => null
            } : null,
            int integer => int.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? CompareNumber(integer, parsed, operation)
                : null,
            string text when !string.IsNullOrWhiteSpace(text) => operation switch
            {
                "Equal" => string.Equals(text.Trim(), expected, StringComparison.OrdinalIgnoreCase),
                "NotEqual" => !string.Equals(text.Trim(), expected, StringComparison.OrdinalIgnoreCase),
                "Contains" => text.Contains(expected, StringComparison.OrdinalIgnoreCase),
                _ => null
            },
            _ => null
        };
    }

    private static bool? CompareNumber(int actual, int expected, string? operation) => operation switch
    {
        "Equal" => actual == expected,
        "NotEqual" => actual != expected,
        "GreaterThan" => actual > expected,
        "GreaterThanOrEqual" => actual >= expected,
        "LessThan" => actual < expected,
        "LessThanOrEqual" => actual <= expected,
        _ => null
    };
}
