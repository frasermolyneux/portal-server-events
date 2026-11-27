using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using MX.Api.Abstractions;
using MX.Api.Client;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class RconResponseService : IRconResponseService
{
    private static readonly TimeSpan FreshnessThreshold = TimeSpan.FromSeconds(5);
    private static readonly Regex QuakeColorCodeRegex = new(@"\^[0-9A-Za-z]", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex NonAlphaNumericRegex = new(@"[^a-z0-9]+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly IServersApiClient _serversApiClient;
    private readonly IRepositoryApiClient _repositoryClient;
    private readonly ILogger<RconResponseService> _logger;
    private readonly IMemoryCache? _cache;

    public RconResponseService(
        IServersApiClient serversApiClient,
        IRepositoryApiClient repositoryClient,
        ILogger<RconResponseService> logger,
        IMemoryCache? cache = null)
    {
        _serversApiClient = serversApiClient;
        _repositoryClient = repositoryClient;
        _logger = logger;
        _cache = cache;
    }

    public Task<bool> TrySayAsync(
        Guid serverId,
        string message,
        DateTime eventGeneratedUtc,
        CancellationToken ct = default)
    {
        // Preserve existing behavior for callers that don't provide game type.
        return TrySayAsync(serverId, GameType.CallOfDuty4x.ToString(), message, eventGeneratedUtc, ct);
    }

    public async Task<bool> TrySayAsync(
        Guid serverId,
        string gameType,
        string message,
        DateTime eventGeneratedUtc,
        CancellationToken ct = default) =>
        await TrySayCoreAsync(serverId, gameType, message, null, Deadline(eventGeneratedUtc), ct).ConfigureAwait(false) == RconSayOutcome.Accepted;

    public async Task<bool> TrySayAsync(
        Guid serverId,
        string gameType,
        string message,
        DateTimeOffset deadlineUtc,
        CancellationToken ct = default)
    {
        return await SayWithOutcomeAsync(serverId, gameType, message, deadlineUtc, ct).ConfigureAwait(false) == RconSayOutcome.Accepted;
    }

    public async Task<RconSayOutcome> SayWithOutcomeAsync(
        Guid serverId, string gameType, string message, DateTimeOffset deadlineUtc, CancellationToken ct = default)
    {
        using var deadlineCancellation = CreateDeadlineCancellation(deadlineUtc, ct);
        return await TrySayCoreAsync(serverId, gameType, message, null, deadlineUtc, deadlineCancellation.Token).ConfigureAwait(false);
    }

    public async Task<RconSayOutcome> SayWithOutcomeAsync(
        Guid serverId, string gameType, string message, string firstFragmentHeader, DateTimeOffset deadlineUtc, CancellationToken ct = default)
    {
        using var deadlineCancellation = CreateDeadlineCancellation(deadlineUtc, ct);
        return await TrySayCoreAsync(serverId, gameType, message, firstFragmentHeader, deadlineUtc, deadlineCancellation.Token).ConfigureAwait(false);
    }

    private async Task<RconSayOutcome> TrySayCoreAsync(
        Guid serverId,
        string gameType,
        string message,
        string? firstFragmentHeader,
        DateTimeOffset deadlineUtc,
        CancellationToken ct)
    {
        if (!IsFresh(serverId, deadlineUtc))
        {
            return RconSayOutcome.NotAttempted;
        }

        if (!TryParseGameType(gameType, out var parsedGameType))
        {
            _logger.LogWarning("RCON Say skipped for server {ServerId}: unsupported game type '{GameType}'", serverId, gameType);
            return RconSayOutcome.NotAttempted;
        }

        if (!IsSupportedGameType(parsedGameType))
        {
            _logger.LogWarning("RCON Say skipped for server {ServerId}: unsupported game type '{GameType}'", serverId, parsedGameType);
            return RconSayOutcome.NotAttempted;
        }

        try
        {
            var header = firstFragmentHeader ?? await CreateHeaderAsync(serverId, privateMessage: false, ct).ConfigureAwait(false);
            if (!IsFresh(serverId, deadlineUtc))
            {
                return RconSayOutcome.NotAttempted;
            }

            if (parsedGameType == GameType.CallOfDuty4x)
            {
                ApiResult<string> cod4xResult;
                try
                {
                    cod4xResult = await _serversApiClient.CoD4xRcon.V1.Say(
                        serverId, new CoD4xMessageRequestDto { Message = message, FirstFragmentHeader = header }, ct).ConfigureAwait(false);
                }
                catch (ApiHttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
                {
                    _logger.LogWarning(ex, "RCON Say rejected for server {ServerId} ({GameType})", serverId, parsedGameType);
                    return ClassifyCod4xSayFailure(ex.StatusCode, ex.ErrorResponse?.Errors);
                }

                if (!cod4xResult.IsSuccess)
                {
                    _logger.LogWarning("RCON Say failed for server {ServerId} ({GameType}): {StatusCode}",
                        serverId, parsedGameType, cod4xResult.StatusCode);
                    return ClassifyCod4xSayFailure(cod4xResult.StatusCode, cod4xResult.Result?.Errors);
                }
                _logger.LogInformation("RCON Say request accepted for server {ServerId} ({GameType}); player delivery unconfirmed", serverId, parsedGameType);
                return RconSayOutcome.Accepted;
            }
            ApiResult result = parsedGameType switch
            {
                GameType.CallOfDuty2 => await _serversApiClient.Cod2Rcon.V1.Say(
                    serverId,
                    new SayRequest { Message = message, FirstFragmentHeader = header },
                    ct).ConfigureAwait(false),
                GameType.CallOfDuty4 => await _serversApiClient.Cod4Rcon.V1.Say(
                    serverId,
                    new SayRequest { Message = message, FirstFragmentHeader = header },
                    ct).ConfigureAwait(false),
                GameType.CallOfDuty5 => await _serversApiClient.Cod5Rcon.V1.Say(
                    serverId,
                    new SayRequest { Message = message, FirstFragmentHeader = header },
                    ct).ConfigureAwait(false),
                _ => throw new UnreachableException()
            };

            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "RCON Say failed for server {ServerId} ({GameType}): {StatusCode}",
                    serverId,
                    parsedGameType,
                    result.StatusCode);
                return RconSayOutcome.Unknown;
            }

            _logger.LogInformation("RCON Say request accepted for server {ServerId} ({GameType}); player delivery unconfirmed", serverId, parsedGameType);
            return RconSayOutcome.Accepted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RCON Say threw for server {ServerId} ({GameType})", serverId, parsedGameType);
            return RconSayOutcome.Unknown;
        }
    }

    private static RconSayOutcome ClassifyCod4xSayFailure(HttpStatusCode? status, ApiError[]? errors)
    {
        if (status != HttpStatusCode.BadRequest || errors?.Any(error => error?.Code == "INVALID_REQUEST") != true)
        {
            return RconSayOutcome.Unknown;
        }

        return errors.Any(error => error?.Code == "INVALID_REQUEST" && error.Message ==
            "A message contains characters that cannot be safely represented by this RCON transport.")
            ? RconSayOutcome.InvalidMessage
            : RconSayOutcome.Rejected;
    }

    public Task<bool> TryTellAsync(
        Guid serverId,
        string playerGuid,
        string message,
        string? expectedPlayerName,
        DateTime eventGeneratedUtc,
        CancellationToken ct = default)
    {
        // Preserve existing behavior for callers that don't provide game type.
        return TryTellAsync(
            serverId,
            GameType.CallOfDuty4x.ToString(),
            playerGuid,
            message,
            expectedPlayerName,
            eventGeneratedUtc,
            ct);
    }

    public Task<bool> TryTellAsync(
        Guid serverId,
        string gameType,
        string playerGuid,
        string message,
        string? expectedPlayerName,
        DateTime eventGeneratedUtc,
        CancellationToken ct = default) =>
        TryTellByGuidAsync(serverId, gameType, playerGuid, message, expectedPlayerName, Deadline(eventGeneratedUtc), ct);

    private async Task<bool> TryTellByGuidAsync(
        Guid serverId,
        string gameType,
        string playerGuid,
        string message,
        string? expectedPlayerName,
        DateTimeOffset deadlineUtc,
        CancellationToken ct,
        string? firstFragmentHeader = null)
    {
        if (!IsFresh(serverId, deadlineUtc))
        {
            return false;
        }

        if (!TryParseGameType(gameType, out var parsedGameType))
        {
            _logger.LogWarning(
                "RCON Tell skipped for server {ServerId}, player {PlayerGuid}: unsupported game type '{GameType}'",
                serverId,
                playerGuid,
                gameType);
            return false;
        }

        try
        {
            var header = firstFragmentHeader ?? await CreateHeaderAsync(serverId, privateMessage: true, ct).ConfigureAwait(false);
            if (!IsFresh(serverId, deadlineUtc))
            {
                return false;
            }

            var player = await ResolvePlayerByGuidAsync(serverId, parsedGameType, playerGuid, ct).ConfigureAwait(false);
            if (player is null)
            {
                _logger.LogWarning(
                    "Unable to resolve player slot for guid {PlayerGuid} on server {ServerId} ({GameType})",
                    playerGuid,
                    serverId,
                    parsedGameType);
                return false;
            }

            if (!NamesMatch(expectedPlayerName, player.Name))
            {
                _logger.LogWarning(
                    "Resolved player name mismatch for guid {PlayerGuid} on server {ServerId} ({GameType}). Expected {ExpectedPlayerName}, got {ActualPlayerName}",
                    playerGuid,
                    serverId,
                    parsedGameType,
                    expectedPlayerName,
                    player.Name);
                return false;
            }

            if (!IsFresh(serverId, deadlineUtc))
            {
                return false;
            }

            var result = await SendTellAsync(serverId, parsedGameType, player.Slot, message, header, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                var errorSummary = SummarizeApiErrors(result);
                _logger.LogWarning(
                    "RCON Tell failed for server {ServerId} ({GameType}), client {ClientId}: {StatusCode}. Errors: {ErrorSummary}",
                    serverId,
                    parsedGameType,
                    player.Slot,
                    result.StatusCode,
                    errorSummary);
                return false;
            }

            _logger.LogInformation(
                "RCON Tell request accepted for server {ServerId} ({GameType}), client {ClientId}; player delivery unconfirmed",
                serverId,
                parsedGameType,
                player.Slot);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "RCON Tell threw for server {ServerId} ({GameType}), player {PlayerGuid}",
                serverId,
                parsedGameType,
                playerGuid);
            return false;
        }
    }

    public Task<bool> TryTellAsync(
        Guid serverId,
        string playerGuid,
        int slotId,
        string message,
        string? expectedPlayerName,
        DateTime eventGeneratedUtc,
        CancellationToken ct = default)
    {
        // Preserve existing behavior for callers that don't provide game type.
        return TryTellAsync(
            serverId,
            GameType.CallOfDuty4x.ToString(),
            playerGuid,
            slotId,
            message,
            expectedPlayerName,
            eventGeneratedUtc,
            ct);
    }

    public Task<bool> TryTellAsync(
        Guid serverId,
        string gameType,
        string playerGuid,
        int slotId,
        string message,
        string? expectedPlayerName,
        DateTime eventGeneratedUtc,
        CancellationToken ct = default) =>
        TryTellCoreAsync(serverId, gameType, playerGuid, slotId, message, expectedPlayerName, Deadline(eventGeneratedUtc), ct);

    public async Task<bool> TryTellAsync(
        Guid serverId,
        string gameType,
        string playerGuid,
        int slotId,
        string message,
        string? expectedPlayerName,
        DateTimeOffset deadlineUtc,
        CancellationToken ct = default)
    {
        using var deadlineCancellation = CreateDeadlineCancellation(deadlineUtc, ct);
        return await TryTellCoreAsync(serverId, gameType, playerGuid, slotId, message, expectedPlayerName, deadlineUtc, deadlineCancellation.Token)
            .ConfigureAwait(false);
    }

    private async Task<bool> TryTellCoreAsync(
        Guid serverId,
        string gameType,
        string playerGuid,
        int slotId,
        string message,
        string? expectedPlayerName,
        DateTimeOffset deadlineUtc,
        CancellationToken ct)
    {
        if (!IsFresh(serverId, deadlineUtc))
        {
            return false;
        }

        if (!TryParseGameType(gameType, out var parsedGameType))
        {
            _logger.LogWarning(
                "RCON Tell skipped for server {ServerId}, player {PlayerGuid}: unsupported game type '{GameType}'",
                serverId,
                playerGuid,
                gameType);
            return false;
        }

        string header;
        try
        {
            header = await CreateHeaderAsync(serverId, privateMessage: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RCON Tell skipped for server {ServerId}, client {ClientId}: unable to prepare message header.",
                serverId, slotId);
            return false;
        }

        if (!IsFresh(serverId, deadlineUtc))
        {
            return false;
        }

        ResolvedPlayer? player;
        try
        {
            player = await ResolvePlayerFromStatusAsync(serverId, parsedGameType, slotId, playerGuid, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RCON slot lookup failed for server {ServerId}, client {ClientId}; falling back to guid lookup.",
                serverId, slotId);
            return await TryTellByGuidAsync(serverId, gameType, playerGuid, message, expectedPlayerName, deadlineUtc, ct, header)
                .ConfigureAwait(false);
        }

        if (player is null || !NamesMatch(expectedPlayerName, player.Name))
        {
            return await TryTellByGuidAsync(serverId, gameType, playerGuid, message, expectedPlayerName, deadlineUtc, ct, header)
                .ConfigureAwait(false);
        }

        try
        {
            if (IsFresh(serverId, deadlineUtc))
            {
                var sendResult = await SendTellAsync(serverId, parsedGameType, player.Slot, message, header, ct).ConfigureAwait(false);

                if (sendResult.IsSuccess)
                {
                    _logger.LogInformation(
                        "RCON Tell request accepted for server {ServerId} ({GameType}), client {ClientId}; player delivery unconfirmed",
                        serverId,
                        parsedGameType,
                        player.Slot);
                    return true;
                }

                var sendErrorSummary = SummarizeApiErrors(sendResult);
                _logger.LogWarning(
                    "RCON Tell failed for server {ServerId} ({GameType}), client {ClientId}: {StatusCode}. Errors: {ErrorSummary}; dispatch is unconfirmed.",
                    serverId,
                    parsedGameType,
                    player.Slot,
                    sendResult.StatusCode,
                    sendErrorSummary);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RCON Tell threw for server {ServerId} ({GameType}), client {ClientId}; dispatch is unconfirmed.",
                serverId,
                parsedGameType,
                slotId);
        }
        return false;
    }

    private Task<ResolvedPlayer?> ResolvePlayerByGuidAsync(
        Guid serverId,
        GameType gameType,
        string playerGuid,
        CancellationToken ct) =>
        gameType switch
        {
            GameType.CallOfDuty2 or GameType.CallOfDuty4 or GameType.CallOfDuty5 =>
                ResolveStandardPlayerByGuidAsync(serverId, gameType, playerGuid, ct),
            GameType.CallOfDuty4x => ResolveCoD4xPlayerByGuidAsync(serverId, playerGuid, ct),
            _ => LogUnsupportedGameType(serverId, gameType)
        };

    private Task<ResolvedPlayer?> LogUnsupportedGameType(Guid serverId, GameType gameType)
    {
        _logger.LogWarning("RCON Tell skipped for server {ServerId}: unsupported game type {GameType}", serverId, gameType);
        return Task.FromResult<ResolvedPlayer?>(null);
    }

    private async Task<ResolvedPlayer?> ResolveStandardPlayerByGuidAsync(
        Guid serverId,
        GameType gameType,
        string playerGuid,
        CancellationToken ct)
    {
        var status = await GetStandardStatusAsync(serverId, gameType, ct).ConfigureAwait(false);
        if (!status.IsSuccess || status.Result?.Data is null)
        {
            _logger.LogWarning("Unable to resolve player slot for server {ServerId}: {StatusCode}", serverId, status.StatusCode);
            return null;
        }

        var player = status.Result.Data.Players.FirstOrDefault(p =>
            string.Equals(p.Guid, playerGuid, StringComparison.OrdinalIgnoreCase));

        return player is null ? null : new ResolvedPlayer(player.Num, player.Guid, player.Name);
    }

    private async Task<ResolvedPlayer?> ResolveCoD4xPlayerByGuidAsync(
        Guid serverId,
        string playerGuid,
        CancellationToken ct)
    {
        var status = await _serversApiClient.CoD4xRcon.V1.Status(serverId, ct).ConfigureAwait(false);
        if (!status.IsSuccess || status.Result?.Data is null)
        {
            _logger.LogWarning("Unable to resolve player slot for server {ServerId}: {StatusCode}", serverId, status.StatusCode);
            return null;
        }

        var player = status.Result.Data.Players.FirstOrDefault(p =>
            string.Equals(p.PlayerIdentifier, playerGuid, StringComparison.OrdinalIgnoreCase));

        return player is null ? null : ToResolvedPlayer(player);
    }

    private Task<ApiResult<RconStatusResponseDto>> GetStandardStatusAsync(
        Guid serverId,
        GameType gameType,
        CancellationToken ct)
    {
        return gameType switch
        {
            GameType.CallOfDuty2 => _serversApiClient.Cod2Rcon.V1.Status(serverId, ct),
            GameType.CallOfDuty4 => _serversApiClient.Cod4Rcon.V1.Status(serverId, ct),
            GameType.CallOfDuty5 => _serversApiClient.Cod5Rcon.V1.Status(serverId, ct),
            _ => throw new UnreachableException()
        };
    }

    private static ResolvedPlayer ToResolvedPlayer(CoD4xStatusPlayerDto player)
    {
        var resolvedName = string.IsNullOrWhiteSpace(player.Name)
            ? player.RawName
            : player.Name;

        return new ResolvedPlayer(player.Num, player.PlayerIdentifier, resolvedName);
    }

    private async Task<ResolvedPlayer?> ResolvePlayerFromStatusAsync(
        Guid serverId,
        GameType gameType,
        int slotId,
        string playerGuid,
        CancellationToken ct)
    {
        if (gameType == GameType.CallOfDuty4x)
        {
            var cod4xStatus = await _serversApiClient.CoD4xRcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!cod4xStatus.IsSuccess || cod4xStatus.Result?.Data is null)
            {
                return null;
            }
            var cod4xPlayers = cod4xStatus.Result.Data.Players;
            var match = cod4xPlayers.FirstOrDefault(p => p.Num == slotId && string.Equals(p.PlayerIdentifier, playerGuid, StringComparison.OrdinalIgnoreCase))
                ?? cod4xPlayers.FirstOrDefault(p => string.Equals(p.PlayerIdentifier, playerGuid, StringComparison.OrdinalIgnoreCase));
            return match is null ? null : ToResolvedPlayer(match);
        }
        if (!IsSupportedGameType(gameType))
        {
            return null;
        }
        var status = await GetStandardStatusAsync(serverId, gameType, ct).ConfigureAwait(false);
        if (!status.IsSuccess || status.Result?.Data is null)
        {
            return null;
        }
        var players = status.Result.Data.Players;
        var player = players.FirstOrDefault(p => p.Num == slotId && string.Equals(p.Guid, playerGuid, StringComparison.OrdinalIgnoreCase))
            ?? players.FirstOrDefault(p => string.Equals(p.Guid, playerGuid, StringComparison.OrdinalIgnoreCase));
        return player is null ? null : new ResolvedPlayer(player.Num, player.Guid, player.Name);
    }

    private Task<ApiResult<string>> SendTellAsync(
        Guid serverId,
        GameType gameType,
        int slotId,
        string message,
        string firstFragmentHeader,
        CancellationToken ct)
    {
        var request = new CoD4xTargetMessageRequestDto
        {
            Target = slotId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Message = message,
            FirstFragmentHeader = firstFragmentHeader
        };

        return gameType switch
        {
            GameType.CallOfDuty2 => _serversApiClient.Cod2Rcon.V1.Tell(serverId, request, ct),
            GameType.CallOfDuty4 => _serversApiClient.Cod4Rcon.V1.Tell(serverId, request, ct),
            GameType.CallOfDuty5 => _serversApiClient.Cod5Rcon.V1.Tell(serverId, request, ct),
            GameType.CallOfDuty4x => _serversApiClient.CoD4xRcon.V1.Tell(serverId, request, ct),
            _ => throw new InvalidOperationException($"Unsupported game type: {gameType}")
        };
    }

    private async Task<string> CreateHeaderAsync(Guid serverId, bool privateMessage, CancellationToken ct)
    {
        var prefix = await AgentNamePrefixResolver.ResolveAsync(_repositoryClient, _logger, serverId, ct, _cache).ConfigureAwait(false);
        return BotMessageHeader.Create(prefix, privateMessage, useCodColorCodes: true);
    }

    private static string SummarizeApiErrors(ApiResult result)
    {
        var errors = result.Result?.Errors;
        if (errors is null || !errors.Any())
        {
            return "none";
        }

        return string.Join("; ", errors.Select(static e => e is null ? "<null-error>" : $"{e.Code}:{e.Message}"));
    }

    private static bool NamesMatch(string? expectedPlayerName, string? resolvedName)
    {
        if (string.IsNullOrWhiteSpace(expectedPlayerName))
        {
            return true;
        }

        return IsLikelySamePlayerName(expectedPlayerName, resolvedName);
    }

    private static bool TryParseGameType(string gameType, out GameType parsedGameType)
    {
        return Enum.TryParse(gameType, true, out parsedGameType);
    }

    private static bool IsSupportedGameType(GameType gameType)
    {
        return gameType is GameType.CallOfDuty2 or GameType.CallOfDuty4 or GameType.CallOfDuty5 or GameType.CallOfDuty4x;
    }

    private static bool IsLikelySamePlayerName(string expectedName, string? resolvedName)
    {
        var normalizedExpected = NormalizePlayerName(expectedName);
        var normalizedResolved = NormalizePlayerName(resolvedName);

        if (normalizedExpected.Length == 0 || normalizedResolved.Length == 0)
        {
            return false;
        }

        return string.Equals(normalizedExpected, normalizedResolved, StringComparison.Ordinal);
    }

    private static string NormalizePlayerName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutColorCodes = QuakeColorCodeRegex.Replace(value, string.Empty);
        var lowered = withoutColorCodes.ToLowerInvariant();
        var alphanumericOnly = NonAlphaNumericRegex.Replace(lowered, string.Empty);

        return alphanumericOnly.Trim();
    }

    private static DateTimeOffset Deadline(DateTime eventGeneratedUtc) =>
        new DateTimeOffset(DateTime.SpecifyKind(eventGeneratedUtc, DateTimeKind.Utc)).Add(FreshnessThreshold);

    private static CancellationTokenSource CreateDeadlineCancellation(DateTimeOffset deadlineUtc, CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remaining = deadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            source.Cancel();
        }
        else if (remaining.TotalMilliseconds <= uint.MaxValue - 1)
        {
            source.CancelAfter(remaining);
        }
        return source;
    }

    private bool IsFresh(Guid serverId, DateTimeOffset deadlineUtc)
    {
        if (DateTimeOffset.UtcNow <= deadlineUtc)
        {
            return true;
        }

        _logger.LogInformation(
            "Skipping RCON response for server {ServerId} - deadline {DeadlineUtc} passed",
            serverId, deadlineUtc);
        return false;
    }

    private sealed record ResolvedPlayer(int Slot, string Guid, string? Name);
}
