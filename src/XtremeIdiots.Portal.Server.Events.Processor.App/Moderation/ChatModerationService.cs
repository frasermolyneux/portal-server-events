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

            var max = new[] { hate ?? 0, selfHarm ?? 0, sexual ?? 0, violence ?? 0 }.Max();
            var category = max switch
            {
                _ when (hate ?? 0) == max => "Hate",
                _ when (selfHarm ?? 0) == max => "SelfHarm",
                _ when (sexual ?? 0) == max => "Sexual",
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
