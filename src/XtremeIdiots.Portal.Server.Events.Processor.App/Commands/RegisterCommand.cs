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
    private readonly IRconResponseService _rconResponseService;
    private readonly IRegisterCommandRateLimiter _rateLimiter;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<RegisterCommand> _logger;

    public RegisterCommand(
        IRepositoryApiClient repositoryClient,
        IRconResponseService rconResponseService,
        IRegisterCommandRateLimiter rateLimiter,
        IAuditLogger auditLogger,
        ILogger<RegisterCommand> logger)
    {
        _repositoryClient = repositoryClient;
        _rconResponseService = rconResponseService;
        _rateLimiter = rateLimiter;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public string Prefix => "!register";
    public ChatCommandMetadata Metadata => new()
    {
        Name = "register",
        Prefix = Prefix,
        Usage = "!register CODE",
        Description = "Links your in-game identity to a portal profile using an activation code.",
        IsMutating = true
    };

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        if (context.PlayerId is null)
        {
            return await FailAsync(context, "Player context unavailable", ct).ConfigureAwait(false);
        }

        if (!_rateLimiter.TryAcquire(context.PlayerId.Value, DateTime.UtcNow, out var retryAfter))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            return await FailAsync(context, $"Too many !register attempts. Please wait {seconds} seconds and try again.", ct)
                .ConfigureAwait(false);
        }

        string code;
        var parsed = context.ParsedCommand;
        if (parsed is not null)
        {
            if (!parsed.PrefixToken.Equals(Prefix, StringComparison.OrdinalIgnoreCase) ||
                parsed.Arguments.Count != 1)
            {
                return await FailAsync(context, "Usage: !register CODE", ct).ConfigureAwait(false);
            }

            code = parsed.Arguments[0].Trim().ToUpperInvariant();
        }
        else
        {
            var parts = context.Message
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length != 2 || !parts[0].Equals(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return await FailAsync(context, "Usage: !register CODE", ct).ConfigureAwait(false);
            }

            code = parts[1].Trim().ToUpperInvariant();
        }
        if (!ActivationCodeRegex.IsMatch(code))
        {
            return await FailAsync(context, "Activation code must be 6 characters [0-9A-Z]", ct).ConfigureAwait(false);
        }

        MX.Api.Abstractions.ApiResult<XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers.ConnectedPlayerDto> consumeResult;
        try
        {
            consumeResult = await _repositoryClient.ConnectedPlayers.V1
                .ConsumeConnectedPlayerActivationCode(new ConsumeConnectedPlayerActivationCodeDto
                {
                    PlayerId = context.PlayerId.Value,
                    Code = code
                }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Connected player registration API threw for {Username} on server {ServerId}",
                context.Username, context.ServerId);

            return await FailAsync(context, "Registration failed due to a temporary error. Please try again.", ct)
                .ConfigureAwait(false);
        }

        if (consumeResult.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            _auditLogger.LogAudit(AuditEvent.ServerAction("ConnectedPlayerRegisterSucceeded", AuditAction.Update)
                .WithGameContext(context.GameType, context.ServerId)
                .WithPlayer(context.PlayerGuid, context.Username)
                .WithSource("RegisterCommand")
                .Build());

            _logger.LogInformation("Connected player registration succeeded for {Username} on server {ServerId}",
                context.Username, context.ServerId);

            await TryTellAsync(context, "Registration successful. Your account is now linked.", ct).ConfigureAwait(false);

            return CommandResult.Ok();
        }

        var failureReason = consumeResult.StatusCode switch
        {
            HttpStatusCode.Conflict => "Player is already linked to a different profile",
            HttpStatusCode.BadRequest => "Activation code is invalid, expired, inactive, or exhausted",
            _ => "Registration failed due to an unexpected API response"
        };

        return await FailAsync(context, failureReason, ct).ConfigureAwait(false);
    }

    private async Task<CommandResult> FailAsync(CommandContext context, string reason, CancellationToken ct)
    {
        _auditLogger.LogAudit(AuditEvent.ServerAction("ConnectedPlayerRegisterFailed", AuditAction.Update)
            .WithGameContext(context.GameType, context.ServerId)
            .WithPlayer(context.PlayerGuid, context.Username)
            .WithSource("RegisterCommand")
            .WithProperty("Reason", reason)
            .Build());

        _logger.LogInformation("Connected player registration failed for {Username} on server {ServerId}: {Reason}",
            context.Username, context.ServerId, reason);

        await TryTellAsync(context, reason, ct).ConfigureAwait(false);

        return CommandResult.Failed(reason);
    }

    private async Task TryTellAsync(CommandContext context, string message, CancellationToken ct)
    {
        var sent = await _rconResponseService.TryTellAsync(
            context.ServerId,
            context.PlayerGuid,
            context.SlotId,
            message,
            context.Username,
            context.EventGeneratedUtc,
            ct).ConfigureAwait(false);

        if (!sent)
        {
            _logger.LogWarning(
                "Private register response not delivered for {Username} on server {ServerId} (player {PlayerGuid}, slot {SlotId})",
                context.Username,
                context.ServerId,
                context.PlayerGuid,
                context.SlotId);
        }
    }
}
