namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed record CommandResult
{
    public bool Handled { get; init; }
    public bool Success { get; init; }
    public string? ResponseMessage { get; init; }

    public static CommandResult NotHandled => new() { Handled = false, Success = false };

    public static CommandResult Ok(string? responseMessage = null) =>
        new() { Handled = true, Success = true, ResponseMessage = responseMessage };

    public static CommandResult Failed(string? reason = null) =>
        new() { Handled = true, Success = false, ResponseMessage = reason };
}
