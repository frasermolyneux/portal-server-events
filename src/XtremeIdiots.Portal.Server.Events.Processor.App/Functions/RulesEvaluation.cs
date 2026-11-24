using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public interface IRulesPluginTransport
{
    Task<RuleEvaluationResponse> EvaluateAsync(RuleTriggerEnvelope request, CancellationToken ct);
    Task<RuleEvaluationResponse> OutcomeAsync(RuleActionResult request, CancellationToken ct);
    Task<RuleEvaluationResponse> PollAsync(RulePollRequest request, CancellationToken ct);
}

public sealed class RulesEvaluation(IRulesPluginTransport transport)
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    [Function("RulesEvaluate")]
    public Task<HttpResponseData> Evaluate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/evaluate")] HttpRequestData request,
        FunctionContext context) =>
        HandleAsync<RuleTriggerEnvelope>(request, context.CancellationToken, transport.EvaluateAsync);

    [Function("RulesOutcomes")]
    public Task<HttpResponseData> Outcomes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/outcomes")] HttpRequestData request,
        FunctionContext context) =>
        HandleAsync<RuleActionResult>(request, context.CancellationToken, transport.OutcomeAsync);

    [Function("RulesPoll")]
    public Task<HttpResponseData> Poll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/poll")] HttpRequestData request,
        FunctionContext context) =>
        HandleAsync<RulePollRequest>(request, context.CancellationToken, transport.PollAsync);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var enumConverter = new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false);
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            // Shared DTO property converters otherwise take precedence and accept numeric enum values.
            foreach (var property in typeInfo.Properties.Where(property => property.PropertyType.IsEnum))
            {
                property.CustomConverter = enumConverter.CreateConverter(property.PropertyType, typeInfo.Options);
            }
        });
        var options = new JsonSerializerOptions(JsonOptions.Default)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = resolver
        };
        options.Converters.Add(enumConverter);
        return options;
    }

    private static async Task<HttpResponseData> HandleAsync<T>(
        HttpRequestData request,
        CancellationToken ct,
        Func<T, CancellationToken, Task<RuleEvaluationResponse>> evaluate) where T : class
    {
        T? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<T>(request.Body, SerializerOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return await WriteErrorAsync(request, ct).ConfigureAwait(false);
        }

        if (payload is null)
        {
            return await WriteErrorAsync(request, ct).ConfigureAwait(false);
        }

        RuleEvaluationResponse result;
        try
        {
            result = await evaluate(payload, ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return await WriteErrorAsync(request, ct).ConfigureAwait(false);
        }

        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await JsonSerializer.SerializeAsync(response.Body, result, SerializerOptions, ct).ConfigureAwait(false);
        return response;
    }

    private static async Task<HttpResponseData> WriteErrorAsync(HttpRequestData request, CancellationToken ct)
    {
        var response = request.CreateResponse(HttpStatusCode.BadRequest);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await JsonSerializer.SerializeAsync(response.Body, new { error = "Invalid rules request." }, SerializerOptions, ct).ConfigureAwait(false);
        return response;
    }
}
