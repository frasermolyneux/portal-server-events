namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public enum CommandIdempotencyState
{
    Acquired,
    InProgress,
    Completed
}

public sealed record CommandIdempotencyKey(string Value);

public sealed record CommandIdempotencyDecision(
    CommandIdempotencyState State,
    CommandResult? ExistingResult = null)
{
    public static CommandIdempotencyDecision Acquired() =>
        new(CommandIdempotencyState.Acquired);

    public static CommandIdempotencyDecision InProgress() =>
        new(CommandIdempotencyState.InProgress);

    public static CommandIdempotencyDecision Completed(CommandResult existingResult) =>
        new(CommandIdempotencyState.Completed, existingResult);
}
