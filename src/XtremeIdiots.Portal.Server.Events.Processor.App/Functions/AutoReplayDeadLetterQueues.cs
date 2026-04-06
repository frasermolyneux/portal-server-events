using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public class AutoReplayDeadLetterQueues(
    ILogger<AutoReplayDeadLetterQueues> logger,
    ServiceBusClient serviceBusClient,
    IFeatureManager featureManager)
{
    private const int MaxMessagesPerQueue = 100;
    private const int BatchSize = 20;
    private const int MaxReplayAttempts = 3;
    private static readonly TimeSpan BatchThrottle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] AllQueues =
    [
        Queues.PlayerConnected,
        Queues.PlayerDisconnected,
        Queues.ChatMessage,
        Queues.ServerConnected,
        Queues.MapChange,
        Queues.ServerStatus,
        Queues.BanFileChanged
    ];

    [Function(nameof(AutoReplayDeadLetterQueues))]
    public async Task Run(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        FunctionContext context)
    {
        if (!await featureManager.IsEnabledAsync("ServerEvents.AutoDlqReplay"))
        {
            logger.LogDebug("AutoDlqReplay feature flag is disabled, skipping");
            return;
        }

        logger.LogInformation("Starting automatic DLQ replay across {QueueCount} queues", AllQueues.Length);

        int totalReplayed = 0;

        foreach (var queueName in AllQueues)
        {
            try
            {
                var count = await ReplayQueueDlq(queueName);
                totalReplayed += count;

                if (count > 0)
                    logger.LogInformation("Auto-replayed {Count} DLQ messages from queue '{QueueName}'", count, queueName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to auto-replay DLQ for queue '{QueueName}', continuing to next queue", queueName);
            }
        }

        logger.LogInformation("Automatic DLQ replay complete: {TotalReplayed} messages replayed across all queues", totalReplayed);
    }

    internal async Task<int> ReplayQueueDlq(string queueName)
    {
        await using var receiver = serviceBusClient.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter
        });

        await using var sender = serviceBusClient.CreateSender(queueName);

        int replayed = 0;

        while (replayed < MaxMessagesPerQueue)
        {
            var batchSize = Math.Min(BatchSize, MaxMessagesPerQueue - replayed);
            var messages = await receiver.ReceiveMessagesAsync(batchSize, ReceiveTimeout);

            if (messages.Count == 0)
                break;

            foreach (var message in messages)
            {
                var replayCount = message.ApplicationProperties.TryGetValue("DlqReplayCount", out var rc)
                    ? Convert.ToInt32(rc)
                    : 0;

                if (replayCount >= MaxReplayAttempts)
                {
                    logger.LogWarning(
                        "Auto-replay skipping message {MessageId} on queue '{QueueName}' — already replayed {ReplayCount} times (max {Max})",
                        message.MessageId, queueName, replayCount, MaxReplayAttempts);
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
                replayed++;
            }

            if (messages.Count < batchSize)
                break;

            await Task.Delay(BatchThrottle);
        }

        return replayed;
    }
}
