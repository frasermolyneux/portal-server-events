using System.Net;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class RegisterCommand : IChatCommand
{
    private static readonly Regex ActivationCodeRegex = new("^[0-9A-Z]{6}$", RegexOptions.Compiled);

    private readonly IRepositoryApiClient _repositoryClient;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<RegisterCommand> _logger;

    public RegisterCommand(
        IRepositoryApiClient repositoryClient,
        IAuditLogger auditLogger,
        ILogger<RegisterCommand> logger)
    {
        _repositoryClient = repositoryClient;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public string Prefix => "!register";

    public bool CanHandle(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var trimmed = message.TrimStart();
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return trimmed.Length == Prefix.Length || char.IsWhiteSpace(trimmed[Prefix.Length]);
    }

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        if (context.PlayerId is null)
        {
            return Fail(context, "Player context unavailable");
        }

        var parts = context.Message
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 2 || !parts[0].Equals(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(context, "Usage: !register CODE");
        }

        var code = parts[1].Trim().ToUpperInvariant();
        if (!ActivationCodeRegex.IsMatch(code))
        {
            return Fail(context, "Activation code must be 6 characters [0-9A-Z]");
        }

        var consumeResult = await _repositoryClient.ConnectedPlayers.V1
            .ConsumeConnectedPlayerActivationCode(new ConsumeConnectedPlayerActivationCodeDto
            {
                PlayerId = context.PlayerId.Value,
                Code = code
            }, ct)
            .ConfigureAwait(false);

        if (consumeResult.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            _auditLogger.LogAudit(AuditEvent.ServerAction("ConnectedPlayerRegisterSucceeded", AuditAction.Update)
                .WithGameContext(context.GameType, context.ServerId)
                .WithPlayer(context.PlayerGuid, context.Username)
                .WithSource("RegisterCommand")
                .Build());

            _logger.LogInformation("Connected player registration succeeded for {Username} on server {ServerId}",
                context.Username, context.ServerId);

            return CommandResult.Ok();
        }

        var failureReason = consumeResult.StatusCode switch
        {
            HttpStatusCode.Conflict => "Player is already linked to a different profile",
            HttpStatusCode.BadRequest => "Activation code is invalid, expired, inactive, or exhausted",
            _ => "Registration failed due to an unexpected API response"
        };

        return Fail(context, failureReason);
    }

    private CommandResult Fail(CommandContext context, string reason)
    {
        _auditLogger.LogAudit(AuditEvent.ServerAction("ConnectedPlayerRegisterFailed", AuditAction.Update)
            .WithGameContext(context.GameType, context.ServerId)
            .WithPlayer(context.PlayerGuid, context.Username)
            .WithSource("RegisterCommand")
            .WithProperty("Reason", reason)
            .Build());

        _logger.LogInformation("Connected player registration failed for {Username} on server {ServerId}: {Reason}",
            context.Username, context.ServerId, reason);

        return CommandResult.Failed(reason);
    }
}
