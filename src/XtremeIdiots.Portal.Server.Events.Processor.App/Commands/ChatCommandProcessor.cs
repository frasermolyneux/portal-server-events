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
    private readonly ISystemClock _clock;
    private readonly IChatCommandSettingsProvider _settingsProvider;
    private readonly IRconResponseService _rconResponseService;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<ChatCommandProcessor> _logger;

    public ChatCommandProcessor(
        IEnumerable<IChatCommand> commands,
        ICommandParser parser,
        ICommandAuthorizationService authorizationService,
        ICommandIdempotencyStore idempotencyStore,
        ISystemClock clock,
        IChatCommandSettingsProvider settingsProvider,
        IRconResponseService rconResponseService,
        IAuditLogger auditLogger,
        ILogger<ChatCommandProcessor> logger)
    {
        _parser = parser;
        _authorizationService = authorizationService;
        _idempotencyStore = idempotencyStore;
        _clock = clock;
        _settingsProvider = settingsProvider;
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

        var prefixMapping = new Dictionary<string, IChatCommand>(StringComparer.OrdinalIgnoreCase);

        // Register by primary prefix
        foreach (var command in registeredCommands)
        {
            var prefixKey = command.Metadata.Prefix.ToLowerInvariant();
            if (prefixMapping.ContainsKey(prefixKey))
            {
                _logger.LogWarning("Duplicate chat command prefix registration detected for {Prefix}; first registration will be used.", prefixKey);
                continue;
            }

            prefixMapping[prefixKey] = command;
        }

        // Register by aliases
        foreach (var command in registeredCommands)
        {
            if (command.Metadata.Aliases is null || command.Metadata.Aliases.Count == 0)
            {
                continue;
            }

            foreach (var alias in command.Metadata.Aliases)
            {
                var aliasKey = alias.ToLowerInvariant();
                if (prefixMapping.ContainsKey(aliasKey))
                {
                    _logger.LogWarning("Duplicate chat command alias registration detected for {Alias} (conflicts with {CommandName}); first registration will be used.",
                        aliasKey, command.Metadata.Name);
                    continue;
                }

                prefixMapping[aliasKey] = command;
            }
        }

        _commandsByPrefix = prefixMapping;
    }

    public async Task<CommandResult> ProcessAsync(CommandContext context, CancellationToken ct = default)
    {
        var parseResult = _parser.Parse(context.Message);
        if (!parseResult.IsCommand || parseResult.Command is null)
        {
            return CommandResult.NotHandled;
        }

        if (!_commandsByPrefix.TryGetValue(parseResult.Command.PrefixToken, out var command))
        {
            return CommandResult.NotHandled;
        }

        var canonicalCommand = parseResult.Command with
        {
            PrefixToken = command.Prefix,
            Verb = command.Metadata.Name
        };

        _logger.LogInformation("Command {CommandPrefix} matched for player {Username} on server {ServerId}",
            command.Prefix, context.Username, context.ServerId);

        var dispatchTimestamp = _clock.UtcNow;
        var commandSettings = await _settingsProvider
            .GetEffectiveSettingsAsync(context.ServerId, command.Metadata.Name, command.Metadata.IsMutating, ct)
            .ConfigureAwait(false);

        if (!commandSettings.Enabled)
        {
            _logger.LogInformation(
                "Command {CommandPrefix} is disabled for server {ServerId}; skipping execution.",
                command.Prefix,
                context.ServerId);

            return CommandResult.NotHandled;
        }

        var threshold = TimeSpan.FromSeconds(Math.Max(0, commandSettings.FreshnessSeconds));
        var age = dispatchTimestamp - context.EventGeneratedUtc;
        if (age > threshold)
        {
            _logger.LogInformation(
                "Skipping stale command {CommandPrefix} for player {Username}. Category={Category}, AgeMs={AgeMs}, ThresholdMs={ThresholdMs}",
                command.Prefix,
                context.Username,
                command.Metadata.IsMutating ? "Mutating" : "ReadOnly",
                (long)age.TotalMilliseconds,
                (long)threshold.TotalMilliseconds);

            _auditLogger.LogAudit(AuditEvent.ServerAction("ChatCommandSkippedStale", AuditAction.Update)
                .WithGameContext(context.GameType, context.ServerId)
                .WithPlayer(context.PlayerGuid, context.Username)
                .WithSource("ChatCommandProcessor")
                .WithProperty("CommandPrefix", command.Prefix)
                .WithProperty("CommandCategory", command.Metadata.IsMutating ? "Mutating" : "ReadOnly")
                .WithProperty("AgeMs", ((long)age.TotalMilliseconds).ToString())
                .WithProperty("ThresholdMs", ((long)threshold.TotalMilliseconds).ToString())
                .Build());

            return CommandResult.Failed("Command expired. Please run it again.");
        }

        var authorizationResult = await _authorizationService.AuthorizeAsync(new CommandAuthorizationContext
        {
            CommandPrefix = command.Prefix,
            RequiredPolicy = command.Metadata.RequiredPolicy,
            RequiredTags = commandSettings.RequiredTags,
            Privileged = true,
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
                context.GameType,
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

            idempotencyKey = BuildIdempotencyKey(context, canonicalCommand);
            var decision = await _idempotencyStore
                .TryBeginAsync(idempotencyKey, _clock.UtcNow, ct)
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
            result = await command.ExecuteAsync(context with { ParsedCommand = canonicalCommand }, ct).ConfigureAwait(false);
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
                .CompleteAsync(idempotencyKey, result, _clock.UtcNow, ct)
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
