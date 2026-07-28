using System.Net;
using System.Text.Json;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

using MX.GeoLocation.Api.Client.V1;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Publishing;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;
using XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public sealed class VpnProtectionEvaluation(
    ICod4xVpnProtectionPolicyProvider cod4xPolicyProvider,
    IVpnProtectionSettingsProvider settingsProvider,
    IVpnProtectionEvaluator evaluator,
    IGeoLocationApiClient geoLocationApiClient,
    IRepositoryApiClient repositoryApiClient,
    IBanAppliedPublisher banAppliedPublisher,
    ILogger<VpnProtectionEvaluation> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    [Function(nameof(EvaluateVpnProtection))]
    public async Task<HttpResponseData> EvaluateVpnProtection(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "vpn-protection/evaluate")] HttpRequestData request,
        FunctionContext context)
    {
        VpnProtectionEvaluationRequest? evaluationRequest;
        try
        {
            evaluationRequest = await JsonSerializer.DeserializeAsync<VpnProtectionEvaluationRequest>(
                request.Body,
                JsonOptions,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return await WriteErrorResponse(request, HttpStatusCode.BadRequest, "Request body must be valid JSON", context.CancellationToken).ConfigureAwait(false);
        }

        if (evaluationRequest is null ||
            evaluationRequest.ServerId == Guid.Empty ||
            !IpAddressGuard.IsPersistable(evaluationRequest.IpAddress) ||
            string.IsNullOrWhiteSpace(evaluationRequest.PlayerGuid) ||
            string.IsNullOrWhiteSpace(evaluationRequest.Username) ||
            evaluationRequest.SlotId < 0)
        {
            return await WriteErrorResponse(request, HttpStatusCode.BadRequest, "serverId, ipAddress, playerGuid, username, and slotId are required", context.CancellationToken).ConfigureAwait(false);
        }

        if (!await cod4xPolicyProvider.IsEnabledAsync(evaluationRequest.ServerId, context.CancellationToken).ConfigureAwait(false))
        {
            return await WriteDecisionResponse(request, VpnProtectionEvaluationResponse.NoMatch, context.CancellationToken).ConfigureAwait(false);
        }

        var settings = await settingsProvider
            .GetEffectiveSettingsAsync(evaluationRequest.ServerId, context.CancellationToken)
            .ConfigureAwait(false);
        if (!settings.Enabled || settings.ValidationFailed)
        {
            return await WriteDecisionResponse(request, VpnProtectionEvaluationResponse.NoMatch, context.CancellationToken).ConfigureAwait(false);
        }

        try
        {
            var intelligenceResult = await geoLocationApiClient.GeoLookup.V1_1
                .GetIpIntelligence(evaluationRequest.IpAddress, context.CancellationToken)
                .ConfigureAwait(false);
            if (!intelligenceResult.IsSuccess || intelligenceResult.Result?.Data is null)
            {
                logger.LogWarning(
                    "CoD4x VPN Protection intelligence lookup failed for server {ServerId}. Status: {StatusCode}",
                    evaluationRequest.ServerId,
                    intelligenceResult.StatusCode);
                return await WriteErrorResponse(request, HttpStatusCode.ServiceUnavailable, "IP intelligence is unavailable", context.CancellationToken).ConfigureAwait(false);
            }

            var playerTags = await ResolvePlayerTagsAsync(evaluationRequest.PlayerGuid).ConfigureAwait(false);

            var decision = evaluator.Evaluate(settings, playerTags, intelligenceResult.Result.Data);

            if (decision.IsMatch && decision.Action == VpnProtectionAction.Ban)
            {
                // The plugin enforces the ban locally from this decision. Hand the portal import to the
                // shared ban-applied queue so BanAppliedProcessor creates the admin action + forum topic
                // off this request's hot path — keeping the response well inside the plugin's evaluation
                // deadline so the player is always dropped this session. Best-effort: a publish failure
                // never blocks the decision, and the agent's dumpbanlist reconcile remains a backstop.
                //
                // No double-import: this endpoint is only called by the CoD4x plugin, and the agent
                // suppresses parsed player events (PlayerConnected / PlayerIpResolved) for plugin-source
                // CoD4x servers (GameServerAgent.ShouldPublishParsedEvent), so the event-path VPN
                // protection (PlayerConnectedProcessor -> VpnProtectionService) never runs for these
                // servers. This publish is therefore the sole VPN ban source for a plugin server, and it
                // shares the RconBanImport idempotency key ({serverId}:{playerGuid}) with the reconcile.
                await PublishBanAppliedAsync(evaluationRequest, decision, context.CancellationToken).ConfigureAwait(false);
            }

            var response = decision.IsMatch
                ? new VpnProtectionEvaluationResponse
                {
                    Matched = true,
                    Action = decision.Action,
                    Reason = decision.Reason,
                    MatchedRuleIds = decision.MatchedRules.Select(static match => match.RuleId).ToArray()
                }
                : VpnProtectionEvaluationResponse.NoMatch;
            return await WriteDecisionResponse(request, response, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "CoD4x VPN Protection evaluation failed for server {ServerId}",
                evaluationRequest.ServerId);
            return await WriteErrorResponse(request, HttpStatusCode.ServiceUnavailable, "IP intelligence is unavailable", context.CancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyCollection<string>> ResolvePlayerTagsAsync(string playerGuid)
    {
        // Excluded-tag exemptions live in the portal, so the plugin (which only sends the GUID) can
        // never supply them. Resolve the player's tags here — CoD4x players are stored under the
        // CallOfDuty4x game type — so the shared evaluator can honour the exemption. On lookup
        // failure fall back to no tags (fail-closed against the exemption, matching the event path).
        try
        {
            var response = await repositoryApiClient.Players.V1
                .GetPlayerByGameType(GameType.CallOfDuty4x, playerGuid, PlayerEntityOptions.Tags)
                .ConfigureAwait(false);

            if (!response.IsSuccess || response.Result?.Data is null)
            {
                logger.LogWarning(
                    "CoD4x VPN Protection could not resolve player tags for {PlayerGuid}; excluded-tag exemption cannot be applied",
                    playerGuid);
                return [];
            }

            return response.Result.Data.Tags
                .Select(static playerTag => playerTag.Tag?.Name)
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "CoD4x VPN Protection failed to resolve player tags for {PlayerGuid}; excluded-tag exemption cannot be applied",
                playerGuid);
            return [];
        }
    }

    private async Task PublishBanAppliedAsync(
        VpnProtectionEvaluationRequest request,
        VpnProtectionDecision decision,
        CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;
            await banAppliedPublisher
                .PublishAsync(
                    new BanAppliedEvent
                    {
                        EventGeneratedUtc = now,
                        EventPublishedUtc = now,
                        ServerId = request.ServerId,
                        GameType = nameof(GameType.CallOfDuty4x),
                        SequenceId = now.Ticks,
                        PlayerGuid = request.PlayerGuid,
                        PlayerName = request.Username,
                        IsTemporary = false,
                        ExpiresUtc = null,
                        Source = BanImportSources.Cod4xVpnProtection,
                        Reason = decision.Reason
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "CoD4x VPN Protection failed to publish ban-applied import for {PlayerGuid} on server {ServerId}",
                request.PlayerGuid,
                request.ServerId);
        }
    }

    private static async Task<HttpResponseData> WriteDecisionResponse(
        HttpRequestData request,
        VpnProtectionEvaluationResponse decision,
        CancellationToken ct)
    {
        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(decision, ct).ConfigureAwait(false);
        return response;
    }

    private static async Task<HttpResponseData> WriteErrorResponse(
        HttpRequestData request,
        HttpStatusCode statusCode,
        string error,
        CancellationToken ct)
    {
        var response = request.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new { error }, ct).ConfigureAwait(false);
        return response;
    }
}
