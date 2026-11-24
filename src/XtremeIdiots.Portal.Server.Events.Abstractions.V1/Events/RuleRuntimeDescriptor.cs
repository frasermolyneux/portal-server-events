namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

public sealed record RuleRuntimeDescriptor
{
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public int ProtocolVersion { get; init; } = 1;
    public string[] Capabilities { get; init; } = [];
    public Guid? InstanceId { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
}
