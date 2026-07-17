using System.Net;
using System.Text.Json;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

using MX.GeoLocation.Api.Client.V1;

using XtremeIdiots.Portal.Server.Events.Processor.App.Services;
using XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public sealed class VpnProtectionEvaluation(
    ICod4xVpnProtectionPolicyProvider cod4xPolicyProvider,
    IVpnProtectionSettingsProvider settingsProvider,
    IVpnProtectionEvaluator evaluator,
    IGeoLocationApiClient geoLocationApiClient,
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

            var decision = evaluator.Evaluate(settings, intelligenceResult.Result.Data);
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