namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public sealed record VpnProtectionDecision
{
    public bool IsMatch => MatchedRules.Count > 0;

    public VpnProtectionAction Action { get; init; }

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<VpnProtectionRuleMatch> MatchedRules { get; init; } = [];

    public static VpnProtectionDecision NoMatch { get; } = new();
}

public sealed record VpnProtectionRuleMatch
{
    public required string RuleId { get; init; }

    public required VpnProtectionSignal Signal { get; init; }

    public required string ActualValue { get; init; }

    public required string ExpectedValue { get; init; }

    public required VpnProtectionAction Action { get; init; }

    public required string Reason { get; init; }

    public required int OrderIndex { get; init; }
}