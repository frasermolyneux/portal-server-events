namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public interface IWelcomeMessageIdempotencyStore
{
    Task<bool> TryBeginAsync(string key, DateTime utcNow, CancellationToken cancellationToken = default);

    Task CompleteAsync(string key, DateTime utcNow, CancellationToken cancellationToken = default);
}
