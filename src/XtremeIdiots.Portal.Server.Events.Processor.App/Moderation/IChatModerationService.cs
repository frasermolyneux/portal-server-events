namespace XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

public interface IChatModerationService
{
    Task<ChatModerationResult?> AnalyseAsync(string message, CancellationToken ct = default);
}
