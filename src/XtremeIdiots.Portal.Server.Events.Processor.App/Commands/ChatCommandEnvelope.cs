namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed record ChatCommandEnvelope
{
    public required string RawMessage { get; init; }
    public required string NormalizedMessage { get; init; }
    public required string PrefixToken { get; init; }
    public required string Verb { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required string ArgumentText { get; init; }
}
