namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public interface ICommandIdempotencyStore
{
    Task<CommandIdempotencyDecision> TryBeginAsync(
        CommandIdempotencyKey key,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        CommandIdempotencyKey key,
        CommandResult result,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}
