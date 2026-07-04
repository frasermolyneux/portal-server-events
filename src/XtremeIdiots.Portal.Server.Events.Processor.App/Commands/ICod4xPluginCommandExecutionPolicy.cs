namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public interface ICod4xPluginCommandExecutionPolicy
{
    Task<bool> ShouldSkipBackendExecutionAsync(
        Guid serverId,
        string gameType,
        string message,
        CancellationToken ct = default);
}
