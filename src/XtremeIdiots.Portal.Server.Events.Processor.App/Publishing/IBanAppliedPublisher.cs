using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Publishing;

/// <summary>
/// Publishes <see cref="BanAppliedEvent"/> messages to the shared ban-applied queue so that
/// <c>BanAppliedProcessor</c> persists the portal admin action and forum topic asynchronously,
/// away from a latency-sensitive request path.
/// </summary>
public interface IBanAppliedPublisher
{
    Task PublishAsync(BanAppliedEvent banAppliedEvent, CancellationToken ct = default);
}
