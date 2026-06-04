namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed record ChatCommandMetadata
{
    public required string Name { get; init; }
    public required string Prefix { get; init; }
    public required string Usage { get; init; }
    public string Description { get; init; } = string.Empty;
    public bool Hidden { get; init; }
    public bool IsMutating { get; init; }
    public string? RequiredPolicy { get; init; }
    public string? FeatureFlag { get; init; }
    public IReadOnlyList<string>? Aliases { get; init; }
}
