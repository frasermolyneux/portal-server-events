namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public interface IFuMessageSettingsProvider
{
    Task<IReadOnlyList<string>> GetEffectiveMessagesAsync(Guid serverId, CancellationToken ct = default);

    Task<bool> IsEnabledAsync(Guid serverId, CancellationToken ct = default);
}
