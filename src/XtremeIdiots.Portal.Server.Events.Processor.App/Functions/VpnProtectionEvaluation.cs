using System.Net;
using System.Text.Json;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

using MX.GeoLocation.Abstractions.Models.V1_1;
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
    ISiegeModeService siegeModeService,
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
            (!evaluationRequest.SiegeEvaluation && !IpAddressGuard.IsPersistable(evaluationRequest.IpAddress)) ||
            string.IsNullOrWhiteSpace(evaluationRequest.PlayerGuid) ||
            string.IsNullOrWhiteSpace(evaluationRequest.Username) ||
            evaluationRequest.SlotId < 0)
        {
            return await WriteErrorResponse(request, HttpStatusCode.BadRequest, "serverId, playerGuid, username, and slotId are required; ipAddress is required for VPN evaluation", context.CancellationToken).ConfigureAwait(false);
        }

        var hasIpAddress = IpAddressGuard.IsPersistable(evaluationRequest.IpAddress);
        var vpnUnavailable = false;
        var vpnDecision = VpnProtectionDecision.NoMatch;
        var vpnSettings = EffectiveVpnProtectionSettings.Disabled(validationFailed: true);
        var vpnPolicyEnabled = false;
        if (hasIpAddress || evaluationRequest.SiegeEvaluation)
        {
            try
            {
                vpnPolicyEnabled = await cod4xPolicyProvider
                    .IsEnabledAsync(evaluationRequest.ServerId, context.CancellationToken)
                    .ConfigureAwait(false);
                if (vpnPolicyEnabled && hasIpAddress)
                {
                    vpnSettings = await settingsProvider
                        .GetEffectiveSettingsAsync(evaluationRequest.ServerId, context.CancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                vpnUnavailable = true;
                logger.LogWarning(ex, "CoD4x VPN/Siege Mode policy lookup failed for server {ServerId}", evaluationRequest.ServerId);
            }
        }
        var vpnEnabled = hasIpAddress && vpnPolicyEnabled && vpnSettings.Enabled && !vpnSettings.ValidationFailed;
        if (evaluationRequest.SiegeEvaluation && vpnPolicyEnabled && !hasIpAddress)
        {
            vpnUnavailable = true;
        }

        IpIntelligenceDto? intelligence = null;
        try
        {
            if (hasIpAddress && (vpnEnabled || evaluationRequest.SiegeEvaluation))
            {
                var intelligenceResult = await geoLocationApiClient.GeoLookup.V1_1
                    .GetIpIntelligence(evaluationRequest.IpAddress!, context.CancellationToken)
                    .ConfigureAwait(false);
                if (!intelligenceResult.IsSuccess || intelligenceResult.Result?.Data is null)
                {
                    vpnUnavailable |= vpnEnabled || evaluationRequest.SiegeEvaluation;
                    logger.LogWarning(
                        "CoD4x VPN/Siege Mode intelligence lookup failed for server {ServerId}. Status: {StatusCode}",
                        evaluationRequest.ServerId,
                        intelligenceResult.StatusCode);
                }
                else
                {
                    intelligence = intelligenceResult.Result.Data;
                }
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            vpnUnavailable |= vpnEnabled || evaluationRequest.SiegeEvaluation;
            logger.LogWarning(ex, "CoD4x VPN/Siege Mode intelligence lookup failed for server {ServerId}", evaluationRequest.ServerId);
        }

        var tagResolution = evaluationRequest.SiegeEvaluation || vpnEnabled
            ? await ResolvePlayerTagsAsync(evaluationRequest.PlayerGuid, context.CancellationToken).ConfigureAwait(false)
            : new PlayerTagResolution([], false);
        if (vpnEnabled && intelligence is not null)
        {
            vpnDecision = evaluator.Evaluate(vpnSettings, tagResolution.Tags, intelligence);
        }

        SiegeModeProcessingResult siegeResult = SiegeModeProcessingResult.NoMatch;
        if (evaluationRequest.SiegeEvaluation)
        {
            siegeResult = await siegeModeService.EvaluateAsync(
                new SiegeModeEvaluationContext
                {
                    ServerId = evaluationRequest.ServerId,
                    GameType = GameType.CallOfDuty4x,
                    PlayerGuid = evaluationRequest.PlayerGuid,
                    Username = evaluationRequest.Username,
                    SlotId = evaluationRequest.SlotId,
                    ConnectionId = evaluationRequest.ConnectionId,
                    IpAddress = hasIpAddress ? evaluationRequest.IpAddress : null,
                    EventGeneratedUtc = DateTime.UtcNow,
                    PlayerTags = tagResolution.IsKnown ? tagResolution.Tags : null,
                    Intelligence = intelligence,
                    IsPluginPath = true,
                    SuppressEnforcement = IsDestructiveVpnMatch(vpnDecision)
                },
                context.CancellationToken).ConfigureAwait(false);
        }

        if (vpnDecision.IsMatch && vpnDecision.Action == VpnProtectionAction.Ban)
        {
            await PublishBanAppliedAsync(
                evaluationRequest,
                vpnDecision,
                context.CancellationToken).ConfigureAwait(false);
        }

        var response = vpnDecision.IsMatch
            ? new VpnProtectionEvaluationResponse
            {
                Matched = true,
                Action = vpnDecision.Action,
                Reason = vpnDecision.Reason,
                MatchedRuleIds = vpnDecision.MatchedRules.Select(static match => match.RuleId).ToArray()
            }
            : VpnProtectionEvaluationResponse.NoMatch;
        response = response with
        {
            VpnUnavailable = vpnUnavailable,
            Siege = siegeResult.ShouldDispatch &&
                siegeResult.Session is not null &&
                siegeResult.Action is not null &&
                evaluationRequest.ConnectionId is Guid responseConnectionId
                ? new SiegeModeEvaluationResponse
                {
                    Mode = "Enforce",
                    SessionId = siegeResult.Session.SessionId,
                    ActionId = siegeResult.Action.ActionId,
                    ConnectionId = responseConnectionId,
                    ExpiresAtUtc = siegeResult.Session.ExpiresAtUtc,
                    Reason = siegeResult.Reason
                }
                : null
        };
        if (!evaluationRequest.SiegeEvaluation && vpnUnavailable)
        {
            return await WriteErrorResponse(request, HttpStatusCode.ServiceUnavailable, "IP intelligence is unavailable", context.CancellationToken).ConfigureAwait(false);
        }
        return await WriteDecisionResponse(request, response, context.CancellationToken).ConfigureAwait(false);
    }

    private async Task<PlayerTagResolution> ResolvePlayerTagsAsync(string playerGuid, CancellationToken ct)
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
                return new PlayerTagResolution([], false);
            }

            return new PlayerTagResolution(response.Result.Data.Tags
                .Select(static playerTag => playerTag.Tag?.Name)
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(), true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "CoD4x VPN Protection failed to resolve player tags for {PlayerGuid}; excluded-tag exemption cannot be applied",
                playerGuid);
            return new PlayerTagResolution([], false);
        }
    }

    private static bool IsDestructiveVpnMatch(VpnProtectionDecision decision) =>
        decision.IsMatch && decision.Action is VpnProtectionAction.Kick or VpnProtectionAction.Ban;

    private sealed record PlayerTagResolution(IReadOnlyCollection<string> Tags, bool IsKnown);

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
