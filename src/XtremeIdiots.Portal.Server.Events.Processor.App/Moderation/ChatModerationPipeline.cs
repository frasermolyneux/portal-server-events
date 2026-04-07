using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

public sealed class ChatModerationPipeline(
    IChatModerationService contentSafety,
    IRepositoryApiClient repositoryClient,
    IConfiguration configuration,
    IFeatureManager featureManager,
    TelemetryClient telemetryClient,
    ILogger<ChatModerationPipeline> logger) : IChatModerationPipeline
{
    public async Task RunAsync(ModerationContext context, CancellationToken ct = default)
    {
        try
        {
            if (!await featureManager.IsEnabledAsync("EventIngest.ChatToxicityDetection"))
                return;

            var minLength = int.TryParse(configuration["ContentSafety:MinMessageLength"], out var ml) ? ml : 5;

            if (context.Message.Length < minLength)
                return;

            if (context.Message.StartsWith("QUICKMESSAGE_", StringComparison.OrdinalIgnoreCase))
                return;

            // Azure Content Safety API has a 10,000 character limit; truncate to stay within bounds
            const int maxApiTextLength = 10_000;
            var textToAnalyse = context.Message.Length > maxApiTextLength
                ? context.Message[..maxApiTextLength]
                : context.Message;

            // Cost control: only analyse new or tagged players via the paid API
            var newPlayerDays = int.TryParse(configuration["ContentSafety:NewPlayerWindowDays"], out var npd) ? npd : 7;
            var isNewPlayer = newPlayerDays > 0
                && context.PlayerFirstSeen > DateTime.UtcNow.AddDays(-newPlayerDays);

            if (!isNewPlayer && !context.HasModerateChatTag)
                return;

            var moderationResult = await contentSafety.AnalyseAsync(textToAnalyse, ct);
            if (moderationResult is null)
                return;

            var threshold = int.TryParse(configuration["ContentSafety:SeverityThreshold"], out var st) ? st : 4;
            if (moderationResult.MaxSeverity < threshold)
                return;

            var reason = $"[AI Content Safety] {moderationResult.Category} (severity {moderationResult.MaxSeverity}/6). " +
                $"Message: \"{Truncate(context.Message, 200)}\" | " +
                $"Scores: Hate={moderationResult.HateSeverity}, Violence={moderationResult.ViolenceSeverity}, " +
                $"Sexual={moderationResult.SexualSeverity}, SelfHarm={moderationResult.SelfHarmSeverity}";

            await CreateObservationAsync(context, reason, "AI Content Safety", ct);
            TrackModerationEvent(context, "AI Content Safety");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat moderation pipeline failed for {Username} on {ServerId}",
                context.Username, context.ServerId);
        }
    }

    private async Task CreateObservationAsync(ModerationContext context, string reason, string source, CancellationToken ct)
    {
        var botAdminId = configuration["ContentSafety:BotAdminId"];

        var adminAction = new CreateAdminActionDto(context.PlayerId, AdminActionType.Observation, reason)
        {
            AdminId = botAdminId
        };

        await repositoryClient.AdminActions.V1.CreateAdminAction(adminAction, ct);

        logger.LogInformation(
            "Chat moderation triggered for player {PlayerId} via {Source}",
            context.PlayerId, source);
    }

    private void TrackModerationEvent(ModerationContext context, string source)
    {
        telemetryClient.TrackEvent("ChatModerationTriggered", new Dictionary<string, string>
        {
            ["GameType"] = context.GameType,
            ["ServerId"] = context.ServerId.ToString(),
            ["Source"] = source,
            ["Username"] = context.Username
        });
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
