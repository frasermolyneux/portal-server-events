using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.SiegeMode;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public sealed class SiegeModeActionOutcome(
    IRepositoryApiClient repositoryApiClient,
    ILogger<SiegeModeActionOutcome> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> AllowedDetails = new(StringComparer.Ordinal)
    {
        "Invalid expiry",
        "Superseded by VPN decision",
        "Connection no longer matches",
        "Invalid reason",
        "Decision expired"
    };

    [Function(nameof(RecordSiegeModeActionOutcome))]
    public async Task<HttpResponseData> RecordSiegeModeActionOutcome(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "siege-mode/outcome")] HttpRequestData request,
        FunctionContext context)
    {
        SiegeModeOutcomeRequest? outcomeRequest;
        try
        {
            outcomeRequest = await JsonSerializer.DeserializeAsync<SiegeModeOutcomeRequest>(
                request.Body,
                JsonOptions,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return await WriteErrorAsync(request, HttpStatusCode.BadRequest, "Request body must be valid JSON.", context.CancellationToken).ConfigureAwait(false);
        }

        if (outcomeRequest is null ||
            outcomeRequest.ServerId == Guid.Empty ||
            outcomeRequest.SessionId == Guid.Empty ||
            outcomeRequest.ActionId == Guid.Empty ||
            outcomeRequest.ConnectionId == Guid.Empty ||
            outcomeRequest.Outcome is not (SiegeActionOutcomes.Queued or SiegeActionOutcomes.Dispatched or
                SiegeActionOutcomes.Applied or SiegeActionOutcomes.Failed or SiegeActionOutcomes.Stale) ||
            (outcomeRequest.Detail is not null && !AllowedDetails.Contains(outcomeRequest.Detail)))
        {
            return await WriteErrorAsync(request, HttpStatusCode.BadRequest, "Invalid Siege Mode action outcome.", context.CancellationToken).ConfigureAwait(false);
        }

        try
        {
            var result = await repositoryApiClient.SiegeMode.V1.UpdateActionOutcome(
                outcomeRequest.ServerId,
                outcomeRequest.SessionId,
                outcomeRequest.ActionId,
                new UpdateSiegeActionOutcomeDto
                {
                    ConnectionId = outcomeRequest.ConnectionId,
                    Outcome = outcomeRequest.Outcome,
                    Detail = outcomeRequest.Detail
                },
                context.CancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                var status = result.IsConflict
                    ? HttpStatusCode.Conflict
                    : result.IsNotFound
                        ? HttpStatusCode.NotFound
                        : HttpStatusCode.ServiceUnavailable;
                logger.LogWarning(
                    "Failed to persist Siege Mode outcome {Outcome} for action {ActionId}: {StatusCode}",
                    outcomeRequest.Outcome,
                    outcomeRequest.ActionId,
                    result.StatusCode);
                return await WriteErrorAsync(request, status, "Unable to persist Siege Mode action outcome.", context.CancellationToken).ConfigureAwait(false);
            }

            return request.CreateResponse(HttpStatusCode.NoContent);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Siege Mode outcome for action {ActionId}", outcomeRequest.ActionId);
            return await WriteErrorAsync(request, HttpStatusCode.ServiceUnavailable, "Unable to persist Siege Mode action outcome.", context.CancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<HttpResponseData> WriteErrorAsync(
        HttpRequestData request,
        HttpStatusCode status,
        string error,
        CancellationToken ct)
    {
        var response = request.CreateResponse(status);
        await response.WriteAsJsonAsync(new { error }, ct).ConfigureAwait(false);
        return response;
    }

    private sealed record SiegeModeOutcomeRequest
    {
        [JsonPropertyName("serverId")]
        public Guid ServerId { get; init; }

        [JsonPropertyName("sessionId")]
        public Guid SessionId { get; init; }

        [JsonPropertyName("actionId")]
        public Guid ActionId { get; init; }

        [JsonPropertyName("connectionId")]
        public Guid ConnectionId { get; init; }

        [JsonPropertyName("outcome")]
        public string Outcome { get; init; } = string.Empty;

        [JsonPropertyName("detail")]
        public string? Detail { get; init; }
    }
}
