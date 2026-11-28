using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

public sealed class ChatModerationPipeline(
    IChatModerationService contentSafety,
    IChatModerationSettingsProvider settingsProvider,
    IRepositoryApiClient repositoryClient,
    IConfiguration configuration,
    IFeatureManager featureManager,
    IAuditLogger auditLogger,
    IChatModerationRulesEvaluator rulesEvaluator,
    ILogger<ChatModerationPipeline> logger) : IChatModerationPipeline
{
    public async Task RunAsync(ModerationContext context, CancellationToken ct = default)
    {
        var result = await RunLegacyAsync(context, ct).ConfigureAwait(false);
        try
        {
            await rulesEvaluator.EvaluateAsync(context, result, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat moderation rule evaluation failed for player {PlayerId} on {ServerId}",
                context.PlayerId, context.ServerId);
        }
    }

    private async Task<ChatModerationResult?> RunLegacyAsync(ModerationContext context, CancellationToken ct)
    {
        ChatModerationResult? moderationResult = null;
        try
        {
            if (!await featureManager.IsEnabledAsync("EventIngest.ChatToxicityDetection"))
            {
                return null;
            }

            var moderationSettings = await settingsProvider
                .GetForServerAsync(context.ServerId, ct)
                .ConfigureAwait(false);

            if (!moderationSettings.IsCategoryEnabled)
            {
                return null;
            }

            var minLength = moderationSettings.MinMessageLength;

            if (context.Message.Length < minLength)
            {
                return null;
            }

            if (context.Message.StartsWith("QUICKMESSAGE_", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Cost control: only analyse new or tagged players via the paid API
            var newPlayerDays = int.TryParse(configuration["ContentSafety:NewPlayerWindowDays"], out var npd) ? npd : 7;
            var isNewPlayer = newPlayerDays > 0
                && context.PlayerFirstSeen > DateTime.UtcNow.AddDays(-newPlayerDays);

            if (!isNewPlayer && !context.HasModerateChatTag)
            {
                return null;
            }

            var analysis = new ChatModerationAnalysis(contentSafety, context.Message, ct);
            moderationResult = await analysis.GetAsync().ConfigureAwait(false);
            if (moderationResult is null)
            {
                return null;
            }

            var triggeredCategories = GetTriggeredCategories(moderationResult, moderationSettings);
            if (triggeredCategories.Count == 0)
            {
                return moderationResult;
            }

            var reason = BuildObservationReason(moderationResult, moderationSettings, context.Message, triggeredCategories);

            await CreateObservationAsync(context, reason, "AI Content Safety", ct);
            TrackModerationEvent(context, "AI Content Safety");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat moderation pipeline failed for {Username} on {ServerId}",
                context.Username, context.ServerId);
        }
        return moderationResult;
    }

    private async Task CreateObservationAsync(ModerationContext context, string reason, string source, CancellationToken ct)
    {
        var botAdminId = configuration["ContentSafety:BotAdminId"];

        var adminAction = new CreateAdminActionDto(context.PlayerId, AdminActionType.Observation, reason)
        {
            AdminId = botAdminId
        };

        var result = await repositoryClient.AdminActions.V1.CreateAdminAction(adminAction, ct);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Failed to create chat moderation observation. Repository status: {result.StatusCode}.");
        }

        auditLogger.LogAudit(AuditEvent.ServerAction("ChatModerationObservationCreated", AuditAction.Create)
            .WithGameContext(context.GameType, context.ServerId)
            .WithPlayer(string.Empty, context.Username)
            .WithSource("ChatModerationPipeline")
            .WithProperty("ModerationSource", source)
            .Build());

        logger.LogInformation(
            "Chat moderation triggered for player {PlayerId} via {Source}",
            context.PlayerId, source);
    }

    private void TrackModerationEvent(ModerationContext context, string source)
    {
        auditLogger.LogAudit(AuditEvent.ServerAction("ChatModerationTriggered", AuditAction.Moderate)
            .WithGameContext(context.GameType, context.ServerId)
            .WithPlayer(string.Empty, context.Username)
            .WithProperty("ModerationSource", source)
            .Build());
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    private static List<string> GetTriggeredCategories(ChatModerationResult result, ChatModerationSettings settings)
    {
        var triggered = new List<string>();

        // Preserve the legacy observation behavior while retaining missing categories for future rule checks.
        if (settings.HateSeverityThreshold.HasValue && (result.HateSeverity ?? 0) >= settings.HateSeverityThreshold.Value)
        {
            triggered.Add("Hate");
        }

        if (settings.ViolenceSeverityThreshold.HasValue && (result.ViolenceSeverity ?? 0) >= settings.ViolenceSeverityThreshold.Value)
        {
            triggered.Add("Violence");
        }

        if (settings.SexualSeverityThreshold.HasValue && (result.SexualSeverity ?? 0) >= settings.SexualSeverityThreshold.Value)
        {
            triggered.Add("Sexual");
        }

        if (settings.SelfHarmSeverityThreshold.HasValue && (result.SelfHarmSeverity ?? 0) >= settings.SelfHarmSeverityThreshold.Value)
        {
            triggered.Add("SelfHarm");
        }

        return triggered;
    }

    private static string BuildObservationReason(
        ChatModerationResult result,
        ChatModerationSettings settings,
        string message,
        IReadOnlyList<string> triggeredCategories)
    {
        var lines = new List<string>
        {
            "[AI Content Safety]",
            $"Triggered categories: {string.Join(", ", triggeredCategories)}",
            "",
            $"Hate: {result.HateSeverity ?? 0}/6 (threshold: {FormatThreshold(settings.HateSeverityThreshold)})",
            $"Violence: {result.ViolenceSeverity ?? 0}/6 (threshold: {FormatThreshold(settings.ViolenceSeverityThreshold)})",
            $"Sexual: {result.SexualSeverity ?? 0}/6 (threshold: {FormatThreshold(settings.SexualSeverityThreshold)})",
            $"SelfHarm: {result.SelfHarmSeverity ?? 0}/6 (threshold: {FormatThreshold(settings.SelfHarmSeverityThreshold)})",
            "",
            "Message excerpt:",
            $"\"{Truncate(message, 200)}\""
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatThreshold(int? value)
        => value.HasValue ? value.Value.ToString() : "Disabled";
}
