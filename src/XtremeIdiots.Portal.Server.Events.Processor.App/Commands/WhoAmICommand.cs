using MX.GeoLocation.Api.Client.V1;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Tags;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class WhoAmICommand : IChatCommand
{
    private static readonly ChatCommandDescriptor Descriptor = ChatCommandDescriptorCatalog.WhoAmI;
    private const string PersistedSuccessMessage = "WhoAmI response delivered.";

    private readonly IRepositoryApiClient _repositoryClient;
    private readonly IGeoLocationApiClient? _geoLocationApiClient;
    private readonly IRconResponseService _rconResponseService;
    private readonly ILogger<WhoAmICommand> _logger;

    public WhoAmICommand(
        IRepositoryApiClient repositoryClient,
        IServiceProvider serviceProvider,
        IRconResponseService rconResponseService,
        ILogger<WhoAmICommand> logger)
    {
        _repositoryClient = repositoryClient;
        _geoLocationApiClient = serviceProvider.GetService<IGeoLocationApiClient>();
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

        if (!Enum.TryParse<GameType>(context.GameType, true, out var gameType))
        {
            return await FailAsync(context, "Unable to resolve your game type.", ct).ConfigureAwait(false);
        }

        var playerResult = await _repositoryClient.Players.V1
            .GetPlayerByGameType(gameType, context.PlayerGuid, PlayerEntityOptions.Tags)
            .ConfigureAwait(false);

        if (!playerResult.IsSuccess || playerResult.Result?.Data is null)
        {
            return await FailAsync(context, "Unable to load your profile right now.", ct).ConfigureAwait(false);
        }

        var player = playerResult.Result.Data;
        var ipAddress = string.IsNullOrWhiteSpace(player.IpAddress) ? "unknown" : player.IpAddress;
        var location = await ResolveLocationAsync(ipAddress, ct).ConfigureAwait(false);
        var roles = ResolveRoles(context.AuthorizationSnapshot?.Tags, player.Tags);
        var name = string.IsNullOrWhiteSpace(player.Username) ? context.Username : player.Username;

        var response = $"Your name is {name}, ip is {ipAddress}, location is {location}, your roles are {roles}.";

        var sent = await _rconResponseService.TryTellAsync(
            context.ServerId,
            context.GameType,
            context.PlayerGuid,
            context.SlotId,
            response,
            context.Username,
            context.EventGeneratedUtc,
            ct).ConfigureAwait(false);

        if (!sent)
        {
            _logger.LogWarning(
                "Private whoami response not delivered for {Username} on server {ServerId} (player {PlayerGuid}, slot {SlotId})",
                context.Username,
                context.ServerId,
                context.PlayerGuid,
                context.SlotId);

            return CommandResult.Failed("Unable to send !whoami response right now. Please try again.");
        }

        return CommandResult.Ok(PersistedSuccessMessage);
    }

    private async Task<CommandResult> FailAsync(CommandContext context, string reason, CancellationToken ct)
    {
        var sent = await _rconResponseService.TryTellAsync(
            context.ServerId,
            context.GameType,
            context.PlayerGuid,
            context.SlotId,
            reason,
            context.Username,
            context.EventGeneratedUtc,
            ct).ConfigureAwait(false);

        if (!sent)
        {
            _logger.LogWarning(
                "Private whoami failure response not delivered for {Username} on server {ServerId} (player {PlayerGuid}, slot {SlotId})",
                context.Username,
                context.ServerId,
                context.PlayerGuid,
                context.SlotId);
        }

        return CommandResult.Failed(reason);
    }

    private async Task<string> ResolveLocationAsync(string ipAddress, CancellationToken ct)
    {
        if (string.Equals(ipAddress, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "unknown";
        }

        if (_geoLocationApiClient is null)
        {
            return "unknown";
        }

        try
        {
            var geoResult = await _geoLocationApiClient.GeoLookup.V1_1
                .GetIpIntelligence(ipAddress, ct)
                .ConfigureAwait(false);

            if (!geoResult.IsSuccess || geoResult.Result?.Data is null)
            {
                return "unknown";
            }

            var intel = geoResult.Result.Data;
            if (!string.IsNullOrWhiteSpace(intel.CityName) && !string.IsNullOrWhiteSpace(intel.CountryName))
            {
                return $"{intel.CityName}, {intel.CountryName}";
            }

            if (!string.IsNullOrWhiteSpace(intel.CountryName))
            {
                return intel.CountryName;
            }

            if (!string.IsNullOrWhiteSpace(intel.CountryCode))
            {
                return intel.CountryCode;
            }

            return "unknown";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "IP intelligence lookup failed for whoami command");
            return "unknown";
        }
    }

    private static string ResolveRoles(IReadOnlySet<string>? snapshotTags, IReadOnlyCollection<PlayerTagDto> playerTags)
    {
        var tags = snapshotTags is { Count: > 0 }
            ? snapshotTags
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Select(static x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : playerTags
                .Select(static x => x.Tag?.Name)
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Select(static x => x!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        if (tags.Length == 0)
        {
            return "none";
        }

        const int maxDisplayed = 6;
        if (tags.Length > maxDisplayed)
        {
            var shown = string.Join(", ", tags.Take(maxDisplayed));
            return $"{shown} (+{tags.Length - maxDisplayed} more)";
        }

        return string.Join(", ", tags);
    }
}
