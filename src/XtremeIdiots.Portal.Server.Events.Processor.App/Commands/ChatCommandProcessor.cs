using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class ChatCommandProcessor : IChatCommandProcessor
{
    private readonly IReadOnlyDictionary<string, IChatCommand> _commandsByPrefix;
    private readonly ICommandParser _parser;
    private readonly ICommandAuthorizationService _authorizationService;
    private readonly ICommandIdempotencyStore _idempotencyStore;
    private readonly IRconResponseService _rconResponseService;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<ChatCommandProcessor> _logger;

    public ChatCommandProcessor(
        IEnumerable<IChatCommand> commands,
        ICommandParser parser,
        ICommandAuthorizationService authorizationService,
        ICommandIdempotencyStore idempotencyStore,
        IRconResponseService rconResponseService,
        IAuditLogger auditLogger,
        ILogger<ChatCommandProcessor> logger)
    {
        _parser = parser;
        _authorizationService = authorizationService;
        _idempotencyStore = idempotencyStore;
        _rconResponseService = rconResponseService;
        _auditLogger = auditLogger;
        _logger = logger;

        var registeredCommands = commands.ToArray();
        foreach (var command in registeredCommands)
        {
            if (!string.Equals(command.Prefix, command.Metadata.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Command {command.GetType().Name} has inconsistent prefixes. Prefix='{command.Prefix}', Metadata.Prefix='{command.Metadata.Prefix}'.");
            }
        }

        _commandsByPrefix = registeredCommands
            .GroupBy(c => c.Metadata.Prefix, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key.ToLowerInvariant(), g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var duplicate in registeredCommands
                     .GroupBy(c => c.Metadata.Prefix, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            _logger.LogWarning("Duplicate chat command prefix registration detected for {Prefix}; first registration will be used.", duplicate.Key);
        }
    }

    public async Task<CommandResult> ProcessAsync(CommandContext context, CancellationToken ct = default)
    {
        var parseResult = _parser.Parse(context.Message);
        if (!parseResult.IsCommand || parseResult.Command is null)
            return CommandResult.NotHandled;

        if (!_commandsByPrefix.TryGetValue(parseResult.Command.PrefixToken, out var command))
            return CommandResult.NotHandled;

        _logger.LogInformation("Command {CommandPrefix} matched for player {Username} on server {ServerId}",
            command.Prefix, context.Username, context.ServerId);

        var authorizationResult = await _authorizationService.AuthorizeAsync(new CommandAuthorizationContext
        {
            CommandPrefix = command.Prefix,
            RequiredPolicy = command.Metadata.RequiredPolicy,
            GameType = context.GameType,
            ServerId = context.ServerId,
            PlayerId = context.PlayerId,
            Snapshot = context.AuthorizationSnapshot
        }, ct).ConfigureAwait(false);

        if (!authorizationResult.Allowed)
        {
            const string denialMessage = "You are not authorized to use this command.";
            var denialSent = await _rconResponseService.TryTellAsync(
                context.ServerId,
                context.PlayerGuid,
                context.SlotId,
                denialMessage,
                context.Username,
                context.EventGeneratedUtc,
                ct).ConfigureAwait(false);

            if (!denialSent)
            {
                _logger.LogWarning(
                    "Private authorization denial response not delivered for {Username} on server {ServerId} (player {PlayerGuid}, slot {SlotId})",
                    context.Username,
                    context.ServerId,
                    context.PlayerGuid,
                    context.SlotId);
            }

            _auditLogger.LogAudit(AuditEvent.ServerAction("ChatCommandDenied", AuditAction.Update)
                .WithGameContext(context.GameType, context.ServerId)
                .WithPlayer(context.PlayerGuid, context.Username)
                .WithSource("ChatCommandProcessor")
                .WithProperty("CommandPrefix", command.Prefix)
                .WithProperty("Reason", authorizationResult.Reason ?? "Unknown")
                .Build());

            return CommandResult.DeniedByPolicy(denialMessage);
        }

        CommandIdempotencyKey? idempotencyKey = null;
        if (command.Metadata.IsMutating)
        {
            if (context.SequenceId <= 0)
            {
                _logger.LogWarning(
                    "Mutating command {CommandPrefix} rejected because source sequence is missing or invalid for {Username} on server {ServerId}",
                    command.Prefix,
                    context.Username,
                    context.ServerId);

                return CommandResult.Failed("Command cannot be processed right now.");
            }

            idempotencyKey = BuildIdempotencyKey(context, parseResult.Command);
            var decision = await _idempotencyStore
                .TryBeginAsync(idempotencyKey, DateTime.UtcNow, ct)
                .ConfigureAwait(false);

            if (decision.State is CommandIdempotencyState.InProgress)
            {
                return CommandResult.Failed("Command is already being processed. Please try again.");
            }

            if (decision.State is CommandIdempotencyState.Completed && decision.ExistingResult is not null)
            {
                return decision.ExistingResult;
            }
        }

        CommandResult result;
        try
        {
            result = await command.ExecuteAsync(context with { ParsedCommand = parseResult.Command }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {CommandPrefix} failed for player {Username}",
                command.Prefix, context.Username);
            result = CommandResult.Failed($"Command failed: {ex.Message}");
        }

        if (idempotencyKey is not null)
        {
            await _idempotencyStore
                .CompleteAsync(idempotencyKey, result, DateTime.UtcNow, ct)
                .ConfigureAwait(false);
        }

        return result;
    }

    private static CommandIdempotencyKey BuildIdempotencyKey(CommandContext context, ChatCommandEnvelope command)
    {
        var normalizedArguments = command.Arguments.Select(arg => arg.Trim().ToLowerInvariant());
        var argumentKey = string.Join("|", normalizedArguments);
        var playerIdentity = context.PlayerId?.ToString() ?? context.PlayerGuid;

        var key = string.Join(
            ":",
            context.ServerId,
            context.SequenceId,
            command.PrefixToken,
            command.Verb,
            argumentKey,
            playerIdentity);

        return new CommandIdempotencyKey(key);
    }
}
