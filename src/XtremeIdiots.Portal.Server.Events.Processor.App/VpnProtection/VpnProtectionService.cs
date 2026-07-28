using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MX.GeoLocation.Abstractions.Models.V1_1;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public sealed class VpnProtectionService(
    IVpnProtectionSettingsProvider settingsProvider,
    IVpnProtectionEvaluator evaluator,
    IVpnProtectionRconEnforcer rconEnforcer,
    IRepositoryApiClient repositoryApiClient,
    IAdminActionTopics adminActionTopics,
    IConfiguration configuration,
    IAuditLogger auditLogger,
    ILogger<VpnProtectionService> logger) : IVpnProtectionService
{
    private const string AutomationReasonMarker = "[PORTAL-AUTOMATION]";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<VpnProtectionProcessingResult> ProcessAsync(
        VpnProtectionContext context,
        IpIntelligenceDto intelligence,
        CancellationToken ct = default)
    {
        var settings = await settingsProvider.GetEffectiveSettingsAsync(context.ServerId, ct).ConfigureAwait(false);
        if (!settings.Enabled || settings.ValidationFailed)
        {
            return new VpnProtectionProcessingResult();
        }

        var decision = evaluator.Evaluate(settings, context.PlayerTags, intelligence);
        if (decision.WasExcluded)
        {
            logger.LogInformation(
                "VPN Protection skipped player {PlayerId} on server {ServerId} due to excluded tag {ExcludedTag}",
                context.PlayerId,
                context.ServerId,
                decision.ExcludedTag);
            return new VpnProtectionProcessingResult { WasExcluded = true };
        }

        if (!decision.IsMatch)
        {
            return new VpnProtectionProcessingResult();
        }

        if (IsUnsupportedDestructiveAction(context.GameType, decision.Action))
        {
            logger.LogWarning(
                "VPN Protection skipped unsupported {Action} for game {GameType} on server {ServerId}",
                decision.Action,
                context.GameType,
                context.ServerId);
            return new VpnProtectionProcessingResult
            {
                Decision = decision,
                RconOutcome = VpnProtectionRconOutcome.UnsupportedGame
            };
        }

        var selectedRuleId = GetSelectedRuleId(decision);
        var actionText = BuildAdminActionText(decision);
        var adminActionType = ToAdminActionType(decision.Action);
        var botAdminId = configuration["ContentSafety:BotAdminId"];
        var adminActionCreated = false;
        EnsureAutomatedActionResultDto? ensuredAction;

        try
        {
            var ensureResult = await repositoryApiClient.AdminActions.V1
                .EnsureAutomatedAction(
                    new EnsureAutomatedActionDto(
                        context.PlayerId,
                        adminActionType,
                        actionText,
                        AutomationFeature.VpnProtection,
                        selectedRuleId)
                    {
                        AdminId = botAdminId
                    },
                    ct)
                .ConfigureAwait(false);

            if (!ensureResult.IsSuccess || ensureResult.Result?.Data is null)
            {
                logger.LogWarning(
                    "Failed to ensure VPN Protection admin action for player {PlayerId}. Status: {StatusCode}",
                    context.PlayerId,
                    ensureResult.StatusCode);
                return new VpnProtectionProcessingResult { Decision = decision };
            }

            ensuredAction = ensureResult.Result.Data;
            adminActionCreated = ensuredAction.Created;
            if (adminActionCreated)
            {
                await CreateAndLinkForumTopicAsync(
                        ensuredAction.AdminAction,
                        adminActionType,
                        context,
                        actionText,
                        botAdminId,
                        ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to ensure VPN Protection admin action for player {PlayerId}", context.PlayerId);
            return new VpnProtectionProcessingResult { Decision = decision };
        }

        var rconOutcome = await rconEnforcer
            .EnforceAsync(context, decision.Action, BuildRconReason(decision, selectedRuleId), ct)
            .ConfigureAwait(false);

        var eventData = JsonSerializer.Serialize(new
        {
            Action = decision.Action.ToString(),
            RconOutcome = rconOutcome.ToString(),
            context.PlayerId,
            context.PlayerGuid,
            context.Username,
            context.SlotId,
            decision.Reason,
            MatchedRuleIds = decision.MatchedRules.Select(static match => match.RuleId).ToArray()
        }, JsonOptions);
        try
        {
            var eventResult = await repositoryApiClient.GameServersEvents.V1
                .CreateGameServerEvent(
                    new CreateGameServerEventDto(context.ServerId, "VpnProtectionAction", eventData),
                    ct)
                .ConfigureAwait(false);

            if (!eventResult.IsSuccess)
            {
                logger.LogWarning(
                    "Failed to persist VPN Protection game server event for server {ServerId}. Status: {StatusCode}",
                    context.ServerId,
                    eventResult.StatusCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to persist VPN Protection game server event for server {ServerId}",
                context.ServerId);
        }

        if (adminActionCreated)
        {
            auditLogger.LogAudit(AuditEvent.ServerAction("VpnProtectionAdminActionCreated", AuditAction.Moderate)
                .WithGameContext(context.GameType.ToString(), context.ServerId)
                .WithPlayer(context.PlayerGuid, context.Username)
                .WithSource("VpnProtectionService")
                .WithProperty("Action", decision.Action.ToString())
                .WithProperty("MatchedRuleIds", string.Join(",", decision.MatchedRules.Select(static match => match.RuleId)))
                .WithProperty("RconOutcome", rconOutcome.ToString())
                .Build());
        }

        logger.LogInformation(
            "VPN Protection processed {Action} for player {PlayerId} on server {ServerId}; admin action created: {AdminActionCreated}; RCON outcome: {RconOutcome}",
            decision.Action,
            context.PlayerId,
            context.ServerId,
            adminActionCreated,
            rconOutcome);

        return new VpnProtectionProcessingResult
        {
            AdminActionCreated = adminActionCreated,
            Decision = decision,
            RconOutcome = rconOutcome
        };
    }

    private static string BuildAdminActionText(VpnProtectionDecision decision)
    {
        var evidence = string.Join(
            "; ",
            decision.MatchedRules.Select(static match =>
                $"{match.RuleId}: {match.Signal}={match.ActualValue} (expected {match.ExpectedValue})"));
        return $"{decision.Reason}\n\nMatched rules: {evidence}";
    }

    private async Task CreateAndLinkForumTopicAsync(
        AdminActionDto adminAction,
        AdminActionType actionType,
        VpnProtectionContext context,
        string actionText,
        string? adminId,
        CancellationToken ct)
    {
        var forumTopicId = await adminActionTopics.CreateTopicForAdminAction(
            actionType,
            context.GameType,
            context.PlayerId,
            context.Username,
            adminAction.Created,
            actionText,
            adminId,
            ct).ConfigureAwait(false);

        if (forumTopicId <= 0)
        {
            logger.LogWarning("Failed to create VPN Protection forum topic for admin action {AdminActionId}", adminAction.AdminActionId);
            return;
        }

        var updateResult = await repositoryApiClient.AdminActions.V1
            .UpdateAdminAction(
                new EditAdminActionDto(adminAction.AdminActionId)
                {
                    ForumTopicId = forumTopicId
                },
                ct)
            .ConfigureAwait(false);

        if (!updateResult.IsSuccess)
        {
            logger.LogWarning("Failed to link VPN Protection forum topic {ForumTopicId} to admin action {AdminActionId}", forumTopicId, adminAction.AdminActionId);
        }
    }

    private static string BuildRconReason(VpnProtectionDecision decision, string selectedRuleId)
        => $"{AutomationReasonMarker} VPN:{selectedRuleId} {decision.Reason}";

    private static string GetSelectedRuleId(VpnProtectionDecision decision)
    {
        return decision.MatchedRules
            .Where(match => match.Action == decision.Action)
            .OrderBy(match => match.OrderIndex)
            .Select(match => match.RuleId)
            .FirstOrDefault() ?? throw new InvalidOperationException("VPN Protection decision has no selected rule.");
    }

    private static bool IsUnsupportedDestructiveAction(GameType gameType, VpnProtectionAction action)
        => action is VpnProtectionAction.Kick or VpnProtectionAction.Ban
            && gameType is not (GameType.CallOfDuty2 or GameType.CallOfDuty4 or GameType.CallOfDuty5 or GameType.CallOfDuty4x);

    private static AdminActionType ToAdminActionType(VpnProtectionAction action) => action switch
    {
        VpnProtectionAction.Observation => AdminActionType.Observation,
        VpnProtectionAction.Kick => AdminActionType.Kick,
        VpnProtectionAction.Ban => AdminActionType.Ban,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported VPN Protection action.")
    };
}
