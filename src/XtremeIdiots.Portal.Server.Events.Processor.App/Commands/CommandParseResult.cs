namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed record CommandParseResult
{
    public bool IsCommand { get; init; }
    public ChatCommandEnvelope? Command { get; init; }
    public string? Reason { get; init; }

    public static CommandParseResult NotACommand(string? reason = null) => new()
    {
        IsCommand = false,
        Command = null,
        Reason = reason
    };

    public static CommandParseResult Parsed(ChatCommandEnvelope command) => new()
    {
        IsCommand = true,
        Command = command,
        Reason = null
    };
}
