using System.Net;
using System.Text;
using System.Text.Json;

using Azure.Messaging.ServiceBus;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests;

/// <summary>
/// Helpers for creating ServiceBusReceivedMessage instances and API results in tests.
/// </summary>
internal static class ServiceBusTestHelpers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Creates a ServiceBusReceivedMessage with the given object serialized as the body.
    /// </summary>
    public static ServiceBusReceivedMessage CreateMessage<T>(T body)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        return CreateMessage(json);
    }

    /// <summary>
    /// Creates a ServiceBusReceivedMessage with the given raw JSON string as the body.
    /// </summary>
    public static ServiceBusReceivedMessage CreateMessage(string jsonBody)
    {
        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(Encoding.UTF8.GetBytes(jsonBody)),
            messageId: Guid.NewGuid().ToString());
    }

    public static ApiResult SuccessResult() => new(HttpStatusCode.OK);
    public static ApiResult NotFoundResult() => new(HttpStatusCode.NotFound);
    public static ApiResult ConflictResult() => new(HttpStatusCode.Conflict);

    public static ApiResult<T> SuccessResult<T>(T data)
        => new(HttpStatusCode.OK, new ApiResponse<T>(data));

    public static ApiResult<T> NotFoundResult<T>()
        => new(HttpStatusCode.NotFound);

    /// <summary>
    /// Creates a PlayerDto with the given PlayerId using Newtonsoft.Json round-trip 
    /// (PlayerId has internal set but [JsonProperty] allows Newtonsoft deserialization).
    /// </summary>
    public static PlayerDto CreatePlayerDto(Guid playerId)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new { PlayerId = playerId });
        return Newtonsoft.Json.JsonConvert.DeserializeObject<PlayerDto>(json)!;
    }
}
