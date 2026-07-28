using System.Globalization;

using MX.GeoLocation.Abstractions.Models.V1_1;

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
            Reason = selectedMatch.Reason,
            MatchedRules = matches
        };
    }

    private static VpnProtectionRuleMatch? TryEvaluateRule(
        EffectiveVpnProtectionRule rule,
        IpIntelligenceDto intelligence)
    {
        if (!TryGetSignalValue(rule.Signal, intelligence, out var actualValue, out var valueKind))
        {
            return null;
        }

        var isMatch = valueKind switch
        {
            SignalValueKind.Boolean => CompareBoolean(actualValue, rule.ExpectedValue, rule.Operator),
            SignalValueKind.Numeric => CompareNumeric(actualValue, rule.ExpectedValue, rule.Operator),
            SignalValueKind.String => CompareString(actualValue, rule.ExpectedValue, rule.Operator),
            _ => false
        };

        if (!isMatch)
        {
            return null;
        }

        return new VpnProtectionRuleMatch
        {
            RuleId = rule.Id,
            Signal = rule.Signal,
            ActualValue = actualValue,
            ExpectedValue = rule.ExpectedValue,
            Action = rule.Action,
            Reason = RenderReason(rule, actualValue),
            OrderIndex = rule.OrderIndex
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
        VpnProtectionComparisonOperator comparisonOperator)
    {
        if (!bool.TryParse(actualValue, out var actual) || !bool.TryParse(expectedValue, out var expected))
        {
            return false;
        }

        return comparisonOperator switch
        {
            VpnProtectionComparisonOperator.Equal => actual == expected,
            VpnProtectionComparisonOperator.NotEqual => actual != expected,
            _ => false
        };
    }

    private static bool CompareNumeric(
        string actualValue,
        string expectedValue,
        VpnProtectionComparisonOperator comparisonOperator)
    {
        if (!int.TryParse(actualValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actual) ||
            !int.TryParse(expectedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected))
        {
            return false;
        }

        return comparisonOperator switch
        {
            VpnProtectionComparisonOperator.Equal => actual == expected,
            VpnProtectionComparisonOperator.NotEqual => actual != expected,
            VpnProtectionComparisonOperator.GreaterThan => actual > expected,
            VpnProtectionComparisonOperator.GreaterThanOrEqual => actual >= expected,
            VpnProtectionComparisonOperator.LessThan => actual < expected,
            VpnProtectionComparisonOperator.LessThanOrEqual => actual <= expected,
            _ => false
        };
    }

    private static bool CompareString(
        string actualValue,
        string expectedValue,
        VpnProtectionComparisonOperator comparisonOperator)
    {
        return comparisonOperator switch
        {
            VpnProtectionComparisonOperator.Equal => string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase),
            VpnProtectionComparisonOperator.NotEqual => !string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase),
            VpnProtectionComparisonOperator.Contains => actualValue.Contains(expectedValue, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string RenderReason(EffectiveVpnProtectionRule rule, string actualValue)
    {
        return rule.ReasonTemplate
            .Replace("{ruleId}", rule.Id, StringComparison.OrdinalIgnoreCase)
            .Replace("{signal}", rule.Signal.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{actualValue}", actualValue, StringComparison.OrdinalIgnoreCase)
            .Replace("{expectedValue}", rule.ExpectedValue, StringComparison.OrdinalIgnoreCase);
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
