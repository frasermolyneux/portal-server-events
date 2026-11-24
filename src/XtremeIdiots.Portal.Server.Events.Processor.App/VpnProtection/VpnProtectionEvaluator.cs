using System.Globalization;

using MX.GeoLocation.Abstractions.Models.V1_1;

using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public sealed class VpnProtectionEvaluator : IVpnProtectionEvaluator
{
    public VpnProtectionDecision Evaluate(
        EffectiveVpnProtectionSettings settings,
        IReadOnlyCollection<string> playerTags,
        IpIntelligenceDto intelligence)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(playerTags);
        ArgumentNullException.ThrowIfNull(intelligence);

        if (!settings.Enabled || settings.ValidationFailed)
        {
            return VpnProtectionDecision.NoMatch;
        }

        var excludedTag = playerTags.FirstOrDefault(settings.ExcludedPlayerTags.Contains);
        if (excludedTag is not null)
        {
            return VpnProtectionDecision.Excluded(excludedTag);
        }

        return Evaluate(settings, intelligence);
    }

    public VpnProtectionDecision Evaluate(
        EffectiveVpnProtectionSettings settings,
        IpIntelligenceDto intelligence)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(intelligence);

        if (!settings.Enabled || settings.ValidationFailed)
        {
            return VpnProtectionDecision.NoMatch;
        }

        var matches = settings.Rules
            .Where(static rule => rule.Enabled)
            .Select(rule => TryEvaluateRule(rule, intelligence))
            .OfType<VpnProtectionRuleMatch>()
            .ToArray();

        if (matches.Length == 0)
        {
            return VpnProtectionDecision.NoMatch;
        }

        var selectedMatch = matches
            .OrderByDescending(static match => GetActionSeverity(match.Action))
            .ThenBy(static match => match.OrderIndex)
            .First();

        return new VpnProtectionDecision
        {
            Action = selectedMatch.Action,
            PostObservationToForums = selectedMatch.PostObservationToForums,
            Reason = selectedMatch.Reason,
            MatchedRules = matches
        };
    }

    private static VpnProtectionRuleMatch? TryEvaluateRule(
        EffectiveVpnProtectionRule rule,
        IpIntelligenceDto intelligence)
    {
        var result = RuleMatcher.EvaluateWithEvidence(
            rule.Condition,
            static node => (node.GroupOperator, node.Conditions),
            node =>
            {
                if (node.Signal is not { } signal || node.Operator is not { } op || node.ExpectedValue is null)
                {
                    return new RuleMatch<VpnProtectionSignalEvidence>(null, []);
                }

                var match = EvaluateSignal(signal, op, node.ExpectedValue, intelligence, out var actual);
                return new RuleMatch<VpnProtectionSignalEvidence>(
                    match, match == true ? [new(signal, actual, node.ExpectedValue)] : []);
            });
        if (result.Value != true || result.Evidence.Count == 0)
        {
            return null;
        }

        var first = result.Evidence[0];
        return new VpnProtectionRuleMatch
        {
            RuleId = rule.Id,
            Signal = first.Signal,
            ActualValue = first.ActualValue,
            ExpectedValue = first.ExpectedValue,
            Evidence = result.Evidence,
            Action = rule.Action,
            PostObservationToForums = rule.PostObservationToForums,
            Reason = RenderReason(rule, result.Evidence),
            OrderIndex = rule.OrderIndex
        };
    }

    internal static bool? EvaluateSignal(
        VpnProtectionSignal signal,
        RuleComparisonOperator comparisonOperator,
        string expectedValue,
        IpIntelligenceDto? intelligence,
        out string actualValue)
    {
        actualValue = string.Empty;
        if (intelligence is null || !TryGetSignalValue(signal, intelligence, out actualValue, out var valueKind))
        {
            return null;
        }

        return valueKind switch
        {
            SignalValueKind.Boolean => CompareBoolean(actualValue, expectedValue, comparisonOperator),
            SignalValueKind.Numeric => CompareNumeric(actualValue, expectedValue, comparisonOperator),
            SignalValueKind.String => CompareString(actualValue, expectedValue, comparisonOperator),
            _ => false
        };
    }

    private static bool TryGetSignalValue(
        VpnProtectionSignal signal,
        IpIntelligenceDto intelligence,
        out string value,
        out SignalValueKind valueKind)
    {
        value = string.Empty;
        valueKind = SignalValueKind.String;

        switch (signal)
        {
            case VpnProtectionSignal.Unknown:
                return false;
            case VpnProtectionSignal.ProxyCheckRiskScore:
                if (intelligence.ProxyCheck is null)
                {
                    return false;
                }

                value = intelligence.ProxyCheck.RiskScore.ToString(CultureInfo.InvariantCulture);
                valueKind = SignalValueKind.Numeric;
                return true;
            case VpnProtectionSignal.ProxyCheckIsProxy:
                if (intelligence.ProxyCheck is null)
                {
                    return false;
                }

                value = intelligence.ProxyCheck.IsProxy.ToString();
                valueKind = SignalValueKind.Boolean;
                return true;
            case VpnProtectionSignal.ProxyCheckIsVpn:
                if (intelligence.ProxyCheck is null)
                {
                    return false;
                }

                value = intelligence.ProxyCheck.IsVpn.ToString();
                valueKind = SignalValueKind.Boolean;
                return true;
            case VpnProtectionSignal.ProxyCheckProxyType:
                if (intelligence.ProxyCheck is null)
                {
                    return false;
                }

                return TrySetStringValue(intelligence.ProxyCheck.ProxyType, out value);
            case VpnProtectionSignal.ProxyCheckAsNumber:
                if (intelligence.ProxyCheck is null)
                {
                    return false;
                }

                return TrySetStringValue(intelligence.ProxyCheck.AsNumber, out value);
            case VpnProtectionSignal.ProxyCheckAsOrganization:
                if (intelligence.ProxyCheck is null)
                {
                    return false;
                }

                return TrySetStringValue(intelligence.ProxyCheck.AsOrganization, out value);
            case VpnProtectionSignal.MaxMindAnonymizerConfidence:
                if (intelligence.Anonymizer?.Confidence is not int confidence)
                {
                    return false;
                }

                value = confidence.ToString(CultureInfo.InvariantCulture);
                valueKind = SignalValueKind.Numeric;
                return true;
            case VpnProtectionSignal.MaxMindIsAnonymous:
                if (intelligence.Anonymizer is null)
                {
                    return false;
                }

                return SetBooleanValue(intelligence.Anonymizer.IsAnonymous, out value, out valueKind);
            case VpnProtectionSignal.MaxMindIsAnonymousVpn:
                if (intelligence.Anonymizer is null)
                {
                    return false;
                }

                return SetBooleanValue(intelligence.Anonymizer.IsAnonymousVpn, out value, out valueKind);
            case VpnProtectionSignal.MaxMindIsHostingProvider:
                if (intelligence.Anonymizer is null)
                {
                    return false;
                }

                return SetBooleanValue(intelligence.Anonymizer.IsHostingProvider, out value, out valueKind);
            case VpnProtectionSignal.MaxMindIsPublicProxy:
                if (intelligence.Anonymizer is null)
                {
                    return false;
                }

                return SetBooleanValue(intelligence.Anonymizer.IsPublicProxy, out value, out valueKind);
            case VpnProtectionSignal.MaxMindIsResidentialProxy:
                if (intelligence.Anonymizer is null)
                {
                    return false;
                }

                return SetBooleanValue(intelligence.Anonymizer.IsResidentialProxy, out value, out valueKind);
            case VpnProtectionSignal.MaxMindIsTorExitNode:
                if (intelligence.Anonymizer is null)
                {
                    return false;
                }

                return SetBooleanValue(intelligence.Anonymizer.IsTorExitNode, out value, out valueKind);
            case VpnProtectionSignal.MaxMindProviderName:
                if (intelligence.Anonymizer is null)
                {
                    return false;
                }

                return TrySetStringValue(intelligence.Anonymizer.ProviderName, out value);
            case VpnProtectionSignal.MaxMindStatus:
                value = intelligence.MaxMindStatus.ToString();
                return true;
            case VpnProtectionSignal.ProxyCheckStatus:
                value = intelligence.ProxyCheckStatus.ToString();
                return true;
            case VpnProtectionSignal.IsPartial:
                return SetBooleanValue(intelligence.IsPartial, out value, out valueKind);
            default:
                return false;
        }
    }

    private static bool TrySetStringValue(string? source, out string value)
    {
        value = source?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool SetBooleanValue(bool source, out string value, out SignalValueKind valueKind)
    {
        value = source.ToString();
        valueKind = SignalValueKind.Boolean;
        return true;
    }

    private static bool CompareBoolean(
        string actualValue,
        string expectedValue,
        RuleComparisonOperator comparisonOperator)
    {
        if (!bool.TryParse(actualValue, out var actual) || !bool.TryParse(expectedValue, out var expected))
        {
            return false;
        }

        return comparisonOperator switch
        {
            RuleComparisonOperator.Equal => actual == expected,
            RuleComparisonOperator.NotEqual => actual != expected,
            _ => false
        };
    }

    private static bool CompareNumeric(
        string actualValue,
        string expectedValue,
        RuleComparisonOperator comparisonOperator)
    {
        if (!int.TryParse(actualValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actual) ||
            !int.TryParse(expectedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected))
        {
            return false;
        }

        return comparisonOperator switch
        {
            RuleComparisonOperator.Equal => actual == expected,
            RuleComparisonOperator.NotEqual => actual != expected,
            RuleComparisonOperator.GreaterThan => actual > expected,
            RuleComparisonOperator.GreaterThanOrEqual => actual >= expected,
            RuleComparisonOperator.LessThan => actual < expected,
            RuleComparisonOperator.LessThanOrEqual => actual <= expected,
            _ => false
        };
    }

    private static bool CompareString(
        string actualValue,
        string expectedValue,
        RuleComparisonOperator comparisonOperator)
    {
        return comparisonOperator switch
        {
            RuleComparisonOperator.Equal => string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase),
            RuleComparisonOperator.NotEqual => !string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase),
            RuleComparisonOperator.Contains => actualValue.Contains(expectedValue, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string RenderReason(EffectiveVpnProtectionRule rule, IReadOnlyList<VpnProtectionSignalEvidence> evidence)
    {
        return rule.ReasonTemplate
            .Replace("{ruleId}", rule.Id, StringComparison.OrdinalIgnoreCase)
            .Replace("{signal}", string.Join(", ", evidence.Select(static item => item.Signal)), StringComparison.OrdinalIgnoreCase)
            .Replace("{actualValue}", string.Join(", ", evidence.Select(static item => item.ActualValue)), StringComparison.OrdinalIgnoreCase)
            .Replace("{expectedValue}", string.Join(", ", evidence.Select(static item => item.ExpectedValue)), StringComparison.OrdinalIgnoreCase);
    }

    private static int GetActionSeverity(VpnProtectionAction action) => action switch
    {
        VpnProtectionAction.Ban => 3,
        VpnProtectionAction.Kick => 2,
        VpnProtectionAction.Observation => 1,
        _ => 0
    };

    private enum SignalValueKind
    {
        String,
        Boolean,
        Numeric
    }
}
