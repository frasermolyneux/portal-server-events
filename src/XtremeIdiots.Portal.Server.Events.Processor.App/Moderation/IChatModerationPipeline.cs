namespace XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

public interface IChatModerationPipeline
{
    /// <summary>
    /// Run the moderation pipeline on a chat message. Creates admin observations if violations found.
    /// Returns without throwing on any failure — moderation never blocks message processing.
    /// </summary>
    Task RunAsync(ModerationContext context, CancellationToken ct = default);
}
