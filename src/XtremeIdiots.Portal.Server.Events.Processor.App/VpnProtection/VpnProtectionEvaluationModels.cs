using System.Text.Json.Serialization;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public sealed record VpnProtectionEvaluationRequest
{
    [JsonPropertyName("serverId")]
    public Guid ServerId { get; init; }

    [JsonPropertyName("ipAddress")]
    public string? IpAddress { get; init; }

    [JsonPropertyName("playerGuid")]
    public string PlayerGuid { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("slotId")]
    public int SlotId { get; init; } = -1;

    [JsonPropertyName("siegeEvaluation")]
    public bool SiegeEvaluation { get; init; }

    [JsonPropertyName("connectionId")]
    public Guid? ConnectionId { get; init; }
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

    [JsonPropertyName("vpnUnavailable")]
    public bool VpnUnavailable { get; init; }

    [JsonPropertyName("siege")]
    public SiegeModeEvaluationResponse? Siege { get; init; }

    public static VpnProtectionEvaluationResponse NoMatch { get; } = new();
}

public sealed record SiegeModeEvaluationResponse
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public Guid SessionId { get; init; }

    [JsonPropertyName("actionId")]
    public Guid ActionId { get; init; }

    [JsonPropertyName("connectionId")]
    public Guid ConnectionId { get; init; }

    [JsonPropertyName("expiresAtUtc")]
    public DateTime ExpiresAtUtc { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}
