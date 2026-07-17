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

        var excludedTag = context.PlayerTags.FirstOrDefault(settings.ExcludedPlayerTags.Contains);
        if (excludedTag is not null)
        {
            logger.LogInformation(
                "VPN Protection skipped player {PlayerId} on server {ServerId} due to excluded tag {ExcludedTag}",
                context.PlayerId,
                context.ServerId,
                excludedTag);
            return new VpnProtectionProcessingResult { WasExcluded = true };
        }

        var decision = evaluator.Evaluate(settings, intelligence);
        if (!decision.IsMatch)
        {
            return new VpnProtectionProcessingResult();
        }

        var rconOutcome = await rconEnforcer
            .EnforceAsync(context, decision.Action, decision.Reason, ct)
            .ConfigureAwait(false);
        if (rconOutcome == VpnProtectionRconOutcome.UnsupportedGame &&
            decision.Action is VpnProtectionAction.Kick or VpnProtectionAction.Ban)
        {
            logger.LogWarning(
                "VPN Protection skipped unsupported {Action} for game {GameType} on server {ServerId}",
                decision.Action,
                context.GameType,
                context.ServerId);
            return new VpnProtectionProcessingResult
            {
                Decision = decision,
                RconOutcome = rconOutcome
            };
        }

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

        var actionText = BuildAdminActionText(decision, rconOutcome);
        var adminActionType = ToAdminActionType(decision.Action);
        var botAdminId = configuration["ContentSafety:BotAdminId"];
        var createdUtc = DateTime.UtcNow;
        var adminActionCreated = false;
        try
        {
            var forumTopicId = await adminActionTopics.CreateTopicForAdminAction(
                adminActionType,
                context.GameType,
                context.PlayerId,
                context.Username,
                createdUtc,
                actionText,
                botAdminId,
                ct).ConfigureAwait(false);

            var adminAction = new CreateAdminActionDto(context.PlayerId, adminActionType, actionText)
            {
                AdminId = botAdminId,
                ForumTopicId = forumTopicId > 0 ? forumTopicId : null
            };
            var createResult = await repositoryApiClient.AdminActions.V1
                .CreateAdminAction(adminAction, ct)
                .ConfigureAwait(false);

            if (createResult.IsSuccess)
            {
                adminActionCreated = true;
                auditLogger.LogAudit(AuditEvent.ServerAction("VpnProtectionAdminActionCreated", AuditAction.Moderate)
                    .WithGameContext(context.GameType.ToString(), context.ServerId)
                    .WithPlayer(context.PlayerGuid, context.Username)
                    .WithSource("VpnProtectionService")
                    .WithProperty("Action", decision.Action.ToString())
                    .WithProperty("MatchedRuleIds", string.Join(",", decision.MatchedRules.Select(static match => match.RuleId)))
                    .WithProperty("RconOutcome", rconOutcome.ToString())
                    .Build());
            }
            else
            {
                logger.LogWarning(
                    "Failed to create VPN Protection admin action for player {PlayerId}. Status: {StatusCode}",
                    context.PlayerId,
                    createResult.StatusCode);
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
                "Failed to create VPN Protection admin action for player {PlayerId}",
                context.PlayerId);
        }

        logger.LogInformation(
            "VPN Protection created {Action} for player {PlayerId} on server {ServerId}; RCON outcome: {RconOutcome}",
            decision.Action,
            context.PlayerId,
            context.ServerId,
            rconOutcome);

        return new VpnProtectionProcessingResult
        {
            AdminActionCreated = adminActionCreated,
            Decision = decision,
            RconOutcome = rconOutcome
        };
    }

    private static string BuildAdminActionText(
        VpnProtectionDecision decision,
        VpnProtectionRconOutcome rconOutcome)
    {
        var evidence = string.Join(
            "; ",
            decision.MatchedRules.Select(static match =>
                $"{match.RuleId}: {match.Signal}={match.ActualValue} (expected {match.ExpectedValue})"));
        return $"{decision.Reason}\n\nMatched rules: {evidence}\nRCON outcome: {rconOutcome}";
    }

    private static AdminActionType ToAdminActionType(VpnProtectionAction action) => action switch
    {
        VpnProtectionAction.Observation => AdminActionType.Observation,
        VpnProtectionAction.Kick => AdminActionType.Kick,
        VpnProtectionAction.Ban => AdminActionType.Ban,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported VPN Protection action.")
    };
}