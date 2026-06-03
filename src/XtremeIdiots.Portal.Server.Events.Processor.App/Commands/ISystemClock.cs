namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public interface ISystemClock
{
    DateTime UtcNow { get; }
}
