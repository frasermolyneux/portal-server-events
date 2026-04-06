using System.Net;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public class ReprocessDeadLetterQueue(
    ILogger<ReprocessDeadLetterQueue> logger,
    ServiceBusClient serviceBusClient)
{
    private const int BatchSize = 20;
    private const int MaxReplayAttempts = 3;
    private static readonly TimeSpan BatchThrottle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> ValidQueues = new(StringComparer.OrdinalIgnoreCase)
    {
        Queues.PlayerConnected,
        Queues.PlayerDisconnected,
        Queues.ChatMessage,
        Queues.ServerConnected,
        Queues.MapChange,
        Queues.ServerStatus,
        Queues.BanFileChanged
    };

    [Function(nameof(ReprocessDeadLetterQueue))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "v1/ReprocessDeadLetterQueue")] HttpRequestData req,
        FunctionContext context)
    {
        var queryParams = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var queueName = queryParams["queueName"];
        var maxMessages = int.TryParse(queryParams["maxMessages"], out var mm) ? mm : 50;
        var dryRun = bool.TryParse(queryParams["dryRun"], out var dr) && dr;

        if (string.IsNullOrWhiteSpace(queueName))
        {
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteAsJsonAsync(new { error = "queueName query parameter is required" });
            return badResponse;
        }

        if (!ValidQueues.Contains(queueName))
        {
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteAsJsonAsync(new { error = $"Unknown queue: {queueName}", validQueues = ValidQueues });
            return badResponse;
        }

        logger.LogInformation("Starting DLQ reprocess for queue '{QueueName}', maxMessages={MaxMessages}, dryRun={DryRun}",
            queueName, maxMessages, dryRun);

        int processedCount = 0;

        if (dryRun)
        {
            processedCount = await PeekDeadLetterMessages(queueName, maxMessages);
        }
        else
        {
            processedCount = await ReplayDeadLetterMessages(queueName, maxMessages);
        }

        logger.LogInformation("DLQ reprocess complete for queue '{QueueName}': {Count} messages {Action}",
            queueName, processedCount, dryRun ? "peeked" : "replayed");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            queue = queueName,
            mode = dryRun ? "dryRun" : "live",
            messagesProcessed = processedCount
        });
        return response;
    }

    internal async Task<int> PeekDeadLetterMessages(string queueName, int maxMessages)
    {
        await using var receiver = serviceBusClient.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter
        });

        int totalPeeked = 0;
        long fromSequenceNumber = 0;

        while (totalPeeked < maxMessages)
        {
            var batchSize = Math.Min(BatchSize, maxMessages - totalPeeked);
            var messages = await receiver.PeekMessagesAsync(batchSize, fromSequenceNumber);

            if (messages.Count == 0)
                break;

            foreach (var message in messages)
            {
                var bodyPreview = message.Body?.ToString() ?? "";
                if (bodyPreview.Length > 500) bodyPreview = bodyPreview[..500] + "...";

                logger.LogInformation(
                    "[DryRun] DLQ message: MessageId={MessageId}, DeadLetterReason={Reason}, ErrorDescription={Error}, Body={Body}",
                    message.MessageId,
                    message.DeadLetterReason,
                    message.DeadLetterErrorDescription,
                    bodyPreview);

                fromSequenceNumber = message.SequenceNumber + 1;
            }

            totalPeeked += messages.Count;

            if (messages.Count < batchSize)
                break;

            await Task.Delay(BatchThrottle);
        }

        return totalPeeked;
    }

    internal async Task<int> ReplayDeadLetterMessages(string queueName, int maxMessages)
    {
        await using var receiver = serviceBusClient.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter
        });

        await using var sender = serviceBusClient.CreateSender(queueName);

        int totalReplayed = 0;

        while (totalReplayed < maxMessages)
        {
            var batchSize = Math.Min(BatchSize, maxMessages - totalReplayed);
            var messages = await receiver.ReceiveMessagesAsync(batchSize, ReceiveTimeout);

            if (messages.Count == 0)
                break;

            foreach (var message in messages)
            {
                var bodyPreview = message.Body?.ToString() ?? "";
                if (bodyPreview.Length > 500) bodyPreview = bodyPreview[..500] + "...";

                var replayCount = message.ApplicationProperties.TryGetValue("DlqReplayCount", out var rc)
                    ? Convert.ToInt32(rc)
                    : 0;

                logger.LogInformation(
                    "Replaying DLQ message: MessageId={MessageId}, DeadLetterReason={Reason}, ErrorDescription={Error}, ReplayCount={ReplayCount}, Body={Body}",
                    message.MessageId,
                    message.DeadLetterReason,
                    message.DeadLetterErrorDescription,
                    replayCount,
                    bodyPreview);

                if (replayCount >= MaxReplayAttempts)
                {
                    logger.LogWarning(
                        "Skipping message {MessageId} — already replayed {ReplayCount} times (max {Max})",
                        message.MessageId, replayCount, MaxReplayAttempts);
                    await receiver.CompleteMessageAsync(message);
                    continue;
                }

                var replayMessage = new ServiceBusMessage(message)
                {
                    MessageId = message.MessageId
                };
                replayMessage.ApplicationProperties["DlqReplayCount"] = replayCount + 1;

                await sender.SendMessageAsync(replayMessage);
                await receiver.CompleteMessageAsync(message);
                totalReplayed++;
            }

            if (messages.Count < batchSize)
                break;

            await Task.Delay(BatchThrottle);
        }

        return totalReplayed;
    }
}
