using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

/// <summary>
/// Processes player disconnected events. Currently logs the event for observability.
/// Live player list management will be added when the server-status processor is implemented.
/// </summary>
public sealed class PlayerDisconnectedProcessor
{
    private readonly ILogger<PlayerDisconnectedProcessor> _logger;
    private readonly IAuditLogger _auditLogger;
    private readonly IRepositoryApiClient _repository;

    public PlayerDisconnectedProcessor(
        ILogger<PlayerDisconnectedProcessor> logger, IAuditLogger auditLogger, IRepositoryApiClient repository)
    {
        _logger = logger;
        _auditLogger = auditLogger;
        _repository = repository;
    }

    [Function(nameof(ProcessPlayerDisconnected))]
    public async Task ProcessPlayerDisconnected(
        [ServiceBusTrigger(Queues.PlayerDisconnected, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        FunctionContext context)
    {
        try
        {
            var evt = message.Body.ToObjectFromJson<PlayerDisconnectedEvent>(JsonOptions.Default);

            if (evt is null)
            {
                _logger.LogWarning("PlayerDisconnected was not in expected format. MessageId: {MessageId}", message.MessageId);
                return;
            }

            await RuleConnectionEvidence.ObserveAsync(_repository, evt, _logger, context.CancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Player disconnected: {Username} ({PlayerGuid}) from server {ServerId}",
                evt.Username, evt.PlayerGuid, evt.ServerId);

            _auditLogger.LogAudit(AuditEvent.ServerAction("PlayerDisconnected", AuditAction.Disconnect)
                .WithGameContext(evt.GameType, evt.ServerId)
                .WithPlayer(evt.PlayerGuid, evt.Username)
                .WithSource("PlayerDisconnectedProcessor")
                .Build());
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "PlayerDisconnected was not in expected format. MessageId: {MessageId}", message.MessageId);
        }

    }
}
