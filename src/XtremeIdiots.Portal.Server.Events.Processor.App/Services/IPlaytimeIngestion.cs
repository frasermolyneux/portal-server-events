using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Services;

public interface IPlaytimeIngestion
{
    Task ProcessAsync(PlaytimeObservationEvent serverEvent, CancellationToken cancellationToken);
}
