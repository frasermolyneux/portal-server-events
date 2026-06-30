using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class CommandsCommand : IChatCommand
{
    private static readonly ChatCommandDescriptor Descriptor = ChatCommandDescriptorCatalog.Commands;

    private readonly IChatCommandCatalog _catalog;
    private readonly IRconResponseService _rconResponseService;
    private readonly ILogger<CommandsCommand> _logger;

    public CommandsCommand(
        IChatCommandCatalog catalog,
        IRconResponseService rconResponseService,
        ILogger<CommandsCommand> logger)
    {
        _catalog = catalog;
        _rconResponseService = rconResponseService;
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
        var parsed = context.ParsedCommand;
        if (parsed is not null)
        {
            if (!parsed.PrefixToken.Equals(Prefix, StringComparison.OrdinalIgnoreCase) ||
                parsed.Arguments.Count != 0)
            {
                return await FailAsync(context, $"Usage: {Descriptor.Usage}", ct).ConfigureAwait(false);
            }
        }
        else
        {
            var parts = context.Message
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length != 1 || !parts[0].Equals(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return await FailAsync(context, $"Usage: {Descriptor.Usage}", ct).ConfigureAwait(false);
            }
        }

        var commands = (await _catalog.GetAvailableCommandsAsync(context, ct).ConfigureAwait(false))
            .Select(x => x.Prefix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var response = commands.Length == 0
            ? "No commands are currently available."
            : $"Available commands: {string.Join(", ", commands)}";

        var sent = await TryTellAsync(context, response, ct).ConfigureAwait(false);
        if (!sent)
        {
            _logger.LogWarning(
                "Private commands response not delivered for {Username} on server {ServerId} (player {PlayerGuid}, slot {SlotId})",
                context.Username,
                context.ServerId,
                context.PlayerGuid,
                context.SlotId);
        }

        return CommandResult.Ok(response);
    }

    private async Task<CommandResult> FailAsync(CommandContext context, string reason, CancellationToken ct)
    {
        var sent = await TryTellAsync(context, reason, ct).ConfigureAwait(false);
        if (!sent)
        {
            _logger.LogWarning(
                "Private commands failure response not delivered for {Username} on server {ServerId} (player {PlayerGuid}, slot {SlotId})",
                context.Username,
                context.ServerId,
                context.PlayerGuid,
                context.SlotId);
        }

        return CommandResult.Failed(reason);
    }

    private async Task<bool> TryTellAsync(CommandContext context, string message, CancellationToken ct)
    {
        return await _rconResponseService.TryTellAsync(
            context.ServerId,
            context.GameType,
            context.PlayerGuid,
            context.SlotId,
            message,
            context.Username,
            context.EventGeneratedUtc,
            ct).ConfigureAwait(false);
    }
}
