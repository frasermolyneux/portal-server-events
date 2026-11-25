using System.Net;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class RegisterCommand : IChatCommand
{
    private static readonly ChatCommandDescriptor Descriptor = ChatCommandDescriptorCatalog.Register;
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

    public string Prefix => Descriptor.Prefix;
    public ChatCommandMetadata Metadata => new()
    {
        Name = Descriptor.Name,
        Prefix = Prefix,
        Usage = Descriptor.Usage,
        Description = Descriptor.Description,
        IsMutating = Descriptor.IsMutating,
        Aliases = Descriptor.Aliases
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
            return await FailAsync(context, $"Too many {Prefix} attempts. Please wait {seconds} seconds and try again.", ct)
                .ConfigureAwait(false);
        }

        string code;
        var parsed = context.ParsedCommand;
        if (parsed is not null)
        {
            if (!parsed.PrefixToken.Equals(Prefix, StringComparison.OrdinalIgnoreCase) ||
                parsed.Arguments.Count != 1)
            {
                return await FailAsync(context, $"Usage: {Descriptor.Usage}", ct).ConfigureAwait(false);
            }

            code = parsed.Arguments[0].Trim().ToUpperInvariant();
        }
        else
        {
            var parts = context.Message
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length != 2 || !parts[0].Equals(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return await FailAsync(context, $"Usage: {Descriptor.Usage}", ct).ConfigureAwait(false);
            }

            code = parts[1].Trim().ToUpperInvariant();
        }
        if (!ActivationCodeRegex.IsMatch(code))
        {
            return await FailAsync(context, "Activation code must be 6 characters [0-9A-Z]", ct).ConfigureAwait(false);
        }

        var requestId = ConnectedPlayerClaimRequestId.FromEvent(context);
        MX.Api.Abstractions.ApiResult<ConnectedPlayerClaimDto> submitResult;
        try
        {
            submitResult = await _repositoryClient.ConnectedPlayers.V1
                .SubmitConnectedPlayerClaim(new SubmitConnectedPlayerClaimDto
                {
                    RequestId = requestId,
                    PlayerId = context.PlayerId.Value,
                    GameServerId = context.ServerId,
                    Code = code
                }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Connected player registration API threw for {Username} on server {ServerId}",
                context.Username, context.ServerId);

            throw;
        }

        if (submitResult.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            if (submitResult.StatusCode == HttpStatusCode.Created)
            {
                _auditLogger.LogAudit(AuditEvent.ServerAction("ConnectedPlayerClaimSubmitted", AuditAction.Create)
                    .WithGameContext(context.GameType, context.ServerId)
                    .WithPlayer(context.PlayerGuid, context.Username)
                    .WithSource("RegisterCommand")
                    .WithProperty("RequestId", requestId.ToString())
                    .Build());
            }

            _logger.LogInformation("Connected player claim submitted for {Username} on server {ServerId}",
                context.Username, context.ServerId);

            var response = submitResult.Result?.Data?.Status is "Approved" or "Declined" or "Expired"
                ? "Registration request already processed. Check its status on the website."
                : "Registration request submitted. Approve it on the website within 24 hours. Your account is not linked by this command.";
            await TryTellAsync(context, response, ct).ConfigureAwait(false);

            return CommandResult.Ok(response);
        }

        if ((int)submitResult.StatusCode >= 500 || submitResult.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException("Registration submission failed temporarily.", null, submitResult.StatusCode);
        }

        if (submitResult.StatusCode == HttpStatusCode.BadRequest &&
            submitResult.Result?.Errors?.Any(error => error.Code == ApiErrorCodes.EntityConflict &&
                error.Message == "The registration code is invalid or expired.") == true)
        {
            return await TryConsumeLegacyCodeAsync(context, context.PlayerId.Value, code, ct).ConfigureAwait(false);
        }

        var failureReason = submitResult.StatusCode switch
        {
            HttpStatusCode.Conflict => "Player is already linked or the registration request conflicts with an existing claim",
            HttpStatusCode.BadRequest => "Activation code is invalid, expired, inactive, or exhausted",
            _ => "Registration failed due to an unexpected API response"
        };

        return await FailAsync(context, failureReason, ct).ConfigureAwait(false);
    }

    private async Task<CommandResult> TryConsumeLegacyCodeAsync(CommandContext context, Guid playerId, string code, CancellationToken ct)
    {
        // Temporary rollout compatibility: Repository atomically rejects ApprovalRequired codes here.
        var result = await _repositoryClient.ConnectedPlayers.V1
            .ConsumeConnectedPlayerActivationCode(new ConsumeConnectedPlayerActivationCodeDto
            {
                PlayerId = playerId,
                Code = code
            }, ct).ConfigureAwait(false);

        if (result.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            _auditLogger.LogAudit(AuditEvent.ServerAction("ConnectedPlayerLegacyRegistrationSucceeded", AuditAction.Update)
                .WithGameContext(context.GameType, context.ServerId)
                .WithPlayer(context.PlayerGuid, context.Username)
                .WithSource("RegisterCommand")
                .Build());
            const string response = "Legacy registration completed. Future registration requests require approval on the website.";
            await TryTellAsync(context, response, ct).ConfigureAwait(false);
            return CommandResult.Ok(response);
        }

        if ((int)result.StatusCode >= 500 || result.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException("Legacy registration failed temporarily.", null, result.StatusCode);
        }

        return await FailAsync(context, result.StatusCode == HttpStatusCode.Conflict
            ? "Player is already linked or this code requires approval on the website"
            : "Activation code is invalid, expired, inactive, or exhausted", ct).ConfigureAwait(false);
    }

    private async Task<CommandResult> FailAsync(CommandContext context, string reason, CancellationToken ct)
    {
        _auditLogger.LogAudit(AuditEvent.ServerAction("ConnectedPlayerRegisterFailed", AuditAction.Update)
            .WithGameContext(context.GameType, context.ServerId)
            .WithPlayer(context.PlayerGuid, context.Username)
            .WithSource("RegisterCommand")
            .WithOutcome(AuditOutcome.Failure)
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
            context.GameType,
            context.PlayerGuid,
            context.SlotId,
            message,
            context.Username,
            context.ResponseDeadline,
            ct).ConfigureAwait(false);

        if (!sent)
        {
            _logger.LogWarning(
                "Private register response dispatch not confirmed for {Username} on server {ServerId} (player {PlayerGuid}, slot {SlotId})",
                context.Username,
                context.ServerId,
                context.PlayerGuid,
                context.SlotId);
        }
    }
}
