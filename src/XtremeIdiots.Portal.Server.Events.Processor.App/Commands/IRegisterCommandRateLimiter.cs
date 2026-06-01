namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public interface IRegisterCommandRateLimiter
{
    bool TryAcquire(Guid playerId, DateTime utcNow, out TimeSpan retryAfter);
}
