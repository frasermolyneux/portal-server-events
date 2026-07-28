using System.Text.Json.Serialization;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public sealed record VpnProtectionEvaluationRequest
{
    [JsonPropertyName("serverId")]
    public Guid ServerId { get; init; }

    [JsonPropertyName("ipAddress")]
    public string IpAddress { get; init; } = string.Empty;

    [JsonPropertyName("playerGuid")]
    public string PlayerGuid { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("slotId")]
    public int SlotId { get; init; } = -1;
}

public sealed record VpnProtectionEvaluationResponse
{
    [JsonPropertyName("matched")]
    public bool Matched { get; init; }

    [JsonPropertyName("action")]
    public VpnProtectionAction Action { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;

    [JsonPropertyName("matchedRuleIds")]
    public IReadOnlyList<string> MatchedRuleIds { get; init; } = [];

    public static VpnProtectionEvaluationResponse NoMatch { get; } = new();
}
