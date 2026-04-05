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
                .FirstOrDefault(c => c.Category == TextCategory.Hate)?.Severity ?? 0;
            var selfHarm = response.Value.CategoriesAnalysis
                .FirstOrDefault(c => c.Category == TextCategory.SelfHarm)?.Severity ?? 0;
            var sexual = response.Value.CategoriesAnalysis
                .FirstOrDefault(c => c.Category == TextCategory.Sexual)?.Severity ?? 0;
            var violence = response.Value.CategoriesAnalysis
                .FirstOrDefault(c => c.Category == TextCategory.Violence)?.Severity ?? 0;

            var max = new[] { hate, selfHarm, sexual, violence }.Max();
            var category = max switch
            {
                _ when hate == max => "Hate",
                _ when selfHarm == max => "SelfHarm",
                _ when sexual == max => "Sexual",
                _ => "Violence"
            };

            return new ChatModerationResult(hate, selfHarm, sexual, violence, max, category);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Content Safety API analysis failed for message");
            return null;
        }
    }
}
