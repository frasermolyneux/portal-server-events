namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed record CommandAuthorizationSnapshot
{
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool TagsResolved { get; init; } = true;
}
