namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public interface IChatCommandSettingsProvider
{
    Task<EffectiveChatCommandSettings> GetEffectiveSettingsAsync(
        Guid serverId,
        string commandName,
        bool isMutating,
        CancellationToken ct = default);
}
