namespace XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

public interface IChatModerationSettingsProvider
{
    Task<ChatModerationSettings> GetForServerAsync(Guid serverId, CancellationToken ct = default);
}
