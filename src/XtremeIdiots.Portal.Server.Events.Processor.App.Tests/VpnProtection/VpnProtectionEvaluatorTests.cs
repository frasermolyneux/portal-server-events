using MX.GeoLocation.Abstractions.Models.V1_1;

using Newtonsoft.Json;

using XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.VpnProtection;

public sealed class VpnProtectionEvaluatorTests
{
    private readonly VpnProtectionEvaluator evaluator = new();

    [Fact]
    public void Evaluate_DisabledSettings_ReturnsNoMatch()
    {
        var settings = new EffectiveVpnProtectionSettings
        {
            Enabled = false,
            Rules = [CreateRule("vpn", VpnProtectionSignal.ProxyCheckIsVpn, VpnProtectionComparisonOperator.Equal, "true", VpnProtectionAction.Ban, 0)]
        };

        var decision = evaluator.Evaluate(settings, CreateIntelligence(isVpn: true));

        Assert.False(decision.IsMatch);
    }

    [Fact]
    public void Evaluate_MultipleRulesMatch_SelectsStrongestActionAndKeepsEvidence()
    {
        var settings = new EffectiveVpnProtectionSettings
        {
            Enabled = true,
            Rules =
            [
                CreateRule("vpn", VpnProtectionSignal.ProxyCheckIsVpn, VpnProtectionComparisonOperator.Equal, "true", VpnProtectionAction.Kick, 0),
                CreateRule("risk", VpnProtectionSignal.ProxyCheckRiskScore, VpnProtectionComparisonOperator.GreaterThanOrEqual, "75", VpnProtectionAction.Ban, 1),
                CreateRule("proxy-type", VpnProtectionSignal.ProxyCheckProxyType, VpnProtectionComparisonOperator.Contains, "vpn", VpnProtectionAction.Observation, 2)
            ]
        };

        var decision = evaluator.Evaluate(settings, CreateIntelligence(isVpn: true, riskScore: 90, proxyType: "VPN"));

        Assert.True(decision.IsMatch);
        Assert.Equal(VpnProtectionAction.Ban, decision.Action);
        Assert.Equal(3, decision.MatchedRules.Count);
        Assert.Equal("risk", decision.MatchedRules.Single(match => match.Action == VpnProtectionAction.Ban).RuleId);
    }

    [Fact]
    public void Evaluate_MissingRequiredProviderData_FailsOpenForRule()
    {
        var settings = new EffectiveVpnProtectionSettings
        {
            Enabled = true,
            Rules = [CreateRule("vpn", VpnProtectionSignal.ProxyCheckIsVpn, VpnProtectionComparisonOperator.Equal, "true", VpnProtectionAction.Ban, 0)]
        };
        var intelligence = DeserializeIntelligence(new
        {
            ProxyCheck = (object?)null,
            ProxyCheckStatus = SourceStatus.Failed,
            MaxMindStatus = SourceStatus.Success,
            IsPartial = true
        });

        var decision = evaluator.Evaluate(settings, intelligence);

        Assert.False(decision.IsMatch);
    }

    [Fact]
    public void Evaluate_SourceStatusRule_CanMatchUnavailableProvider()
    {
        var settings = new EffectiveVpnProtectionSettings
        {
            Enabled = true,
            Rules = [CreateRule("source", VpnProtectionSignal.ProxyCheckStatus, VpnProtectionComparisonOperator.Equal, "Failed", VpnProtectionAction.Observation, 0)]
        };
        var intelligence = DeserializeIntelligence(new
        {
            ProxyCheck = (object?)null,
            ProxyCheckStatus = SourceStatus.Failed,
            MaxMindStatus = SourceStatus.Success,
            IsPartial = true
        });

        var decision = evaluator.Evaluate(settings, intelligence);

        Assert.True(decision.IsMatch);
        Assert.Equal(VpnProtectionAction.Observation, decision.Action);
    }

    [Fact]
    public void Evaluate_ReasonTemplate_RendersSupportedPlaceholders()
    {
        var rule = CreateRule(
            "risk",
            VpnProtectionSignal.ProxyCheckRiskScore,
            VpnProtectionComparisonOperator.GreaterThanOrEqual,
            "75",
            VpnProtectionAction.Ban,
            0) with
        {
            ReasonTemplate = "Rule {ruleId}: {signal} was {actualValue}, expected {expectedValue}"
        };
        var settings = new EffectiveVpnProtectionSettings { Enabled = true, Rules = [rule] };

        var decision = evaluator.Evaluate(settings, CreateIntelligence(riskScore: 90));

        Assert.Equal("Rule risk: ProxyCheckRiskScore was 90, expected 75", decision.Reason);
    }

    [Fact]
    public void Evaluate_DisabledRule_DoesNotMatch()
    {
        var rule = CreateRule("vpn", VpnProtectionSignal.ProxyCheckIsVpn, VpnProtectionComparisonOperator.Equal, "true", VpnProtectionAction.Ban, 0) with
        {
            Enabled = false
        };
        var settings = new EffectiveVpnProtectionSettings { Enabled = true, Rules = [rule] };

        var decision = evaluator.Evaluate(settings, CreateIntelligence(isVpn: true));

        Assert.False(decision.IsMatch);
    }

    [Fact]
    public void Evaluate_TagAware_WithExcludedTag_ReturnsExcludedWithoutScoringRules()
    {
        var settings = new EffectiveVpnProtectionSettings
        {
            Enabled = true,
            ExcludedPlayerTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Trusted VPN" },
            Rules = [CreateRule("vpn", VpnProtectionSignal.ProxyCheckIsVpn, VpnProtectionComparisonOperator.Equal, "true", VpnProtectionAction.Ban, 0)]
        };

        var decision = evaluator.Evaluate(settings, ["Donator", "Trusted VPN"], CreateIntelligence(isVpn: true));

        Assert.True(decision.WasExcluded);
        Assert.Equal("Trusted VPN", decision.ExcludedTag);
        Assert.False(decision.IsMatch);
    }

    [Fact]
    public void Evaluate_TagAware_WithoutExcludedTag_ScoresRulesAsUsual()
    {
        var settings = new EffectiveVpnProtectionSettings
        {
            Enabled = true,
            ExcludedPlayerTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Trusted VPN" },
            Rules = [CreateRule("vpn", VpnProtectionSignal.ProxyCheckIsVpn, VpnProtectionComparisonOperator.Equal, "true", VpnProtectionAction.Ban, 0)]
        };

        var decision = evaluator.Evaluate(settings, ["Donator"], CreateIntelligence(isVpn: true));

        Assert.False(decision.WasExcluded);
        Assert.True(decision.IsMatch);
        Assert.Equal(VpnProtectionAction.Ban, decision.Action);
    }

    [Fact]
    public void Evaluate_TagAware_DisabledSettings_ReturnsNoMatchNotExcluded()
    {
        var settings = new EffectiveVpnProtectionSettings
        {
            Enabled = false,
            ExcludedPlayerTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Trusted VPN" },
            Rules = [CreateRule("vpn", VpnProtectionSignal.ProxyCheckIsVpn, VpnProtectionComparisonOperator.Equal, "true", VpnProtectionAction.Ban, 0)]
        };

        var decision = evaluator.Evaluate(settings, ["Trusted VPN"], CreateIntelligence(isVpn: true));

        Assert.False(decision.WasExcluded);
        Assert.False(decision.IsMatch);
    }

    private static EffectiveVpnProtectionRule CreateRule(
        string id,
        VpnProtectionSignal signal,
        VpnProtectionComparisonOperator comparisonOperator,
        string expectedValue,
        VpnProtectionAction action,
        int orderIndex) => new()
        {
            Id = id,
            Enabled = true,
            Signal = signal,
            Operator = comparisonOperator,
            ExpectedValue = expectedValue,
            Action = action,
            ReasonTemplate = VpnProtectionSettingsConstants.DefaultReasonTemplate,
            OrderIndex = orderIndex
        };

    private static IpIntelligenceDto CreateIntelligence(
        bool isVpn = false,
        int riskScore = 0,
        string proxyType = "") => DeserializeIntelligence(new
        {
            ProxyCheck = new
            {
                RiskScore = riskScore,
                IsProxy = isVpn,
                IsVpn = isVpn,
                ProxyType = proxyType,
                AsNumber = "AS123",
                AsOrganization = "Example Network"
            },
            ProxyCheckStatus = SourceStatus.Success,
            MaxMindStatus = SourceStatus.Success,
            IsPartial = false
        });

    private static IpIntelligenceDto DeserializeIntelligence(object value)
    {
        return JsonConvert.DeserializeObject<IpIntelligenceDto>(JsonConvert.SerializeObject(value))!;
    }
}