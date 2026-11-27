using Azure.AI.ContentSafety;

using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

public class ChatModerationService(
    ContentSafetyClient client,
    ILogger<ChatModerationService> logger) : IChatModerationService
{
    public async Task<ChatModerationResult?> AnalyseAsync(string message, CancellationToken ct = default)
    {
        try
        {
            var response = await client.AnalyzeTextAsync(new AnalyzeTextOptions(message), ct);

            var hate = response.Value.CategoriesAnalysis
                .FirstOrDefault(c => c.Category == TextCategory.Hate)?.Severity;
            var selfHarm = response.Value.CategoriesAnalysis
                .FirstOrDefault(c => c.Category == TextCategory.SelfHarm)?.Severity;
            var sexual = response.Value.CategoriesAnalysis
                .FirstOrDefault(c => c.Category == TextCategory.Sexual)?.Severity;
            var violence = response.Value.CategoriesAnalysis
                .FirstOrDefault(c => c.Category == TextCategory.Violence)?.Severity;

            return new ChatModerationResult(hate, selfHarm, sexual, violence);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Content Safety API analysis failed for message");
            return null;
        }
    }
}
