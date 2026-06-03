namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed record ChatCommandDefinition
{
    public required string Prefix { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Usage { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}
