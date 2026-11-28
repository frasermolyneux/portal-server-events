namespace XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

internal sealed class ChatModerationAnalysis
{
    private const int MaxApiTextLength = 10_000;
    private readonly Lazy<Task<ChatModerationResult?>> analysis;

    public ChatModerationAnalysis(IChatModerationService service, string message, CancellationToken ct)
    {
        var text = message.Length > MaxApiTextLength ? message[..MaxApiTextLength] : message;
        analysis = new(() => service.AnalyseAsync(text, ct));
    }

    public Task<ChatModerationResult?> GetAsync() => analysis.Value;
}
