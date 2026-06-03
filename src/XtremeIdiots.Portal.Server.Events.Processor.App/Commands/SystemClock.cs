namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class SystemClock : ISystemClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
