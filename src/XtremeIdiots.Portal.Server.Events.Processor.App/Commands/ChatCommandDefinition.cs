namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed record ChatCommandDefinition
{
    public required string Prefix { get; init; }
}
