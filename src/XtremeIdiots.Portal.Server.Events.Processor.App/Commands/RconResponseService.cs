using System.Diagnostics;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class RconResponseService : IRconResponseService
{
    private static readonly TimeSpan FreshnessThreshold = TimeSpan.FromSeconds(5);
    private static readonly Regex QuakeColorCodeRegex = new(@"\^[0-9A-Za-z]", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex NonAlphaNumericRegex = new(@"[^a-z0-9]+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly IServersApiClient _serversApiClient;
    private readonly ILogger<RconResponseService> _logger;

    public RconResponseService(IServersApiClient serversApiClient, ILogger<RconResponseService> logger)
    {
        _serversApiClient = serversApiClient;
        _logger = logger;
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
        CancellationToken ct = default)
    {
        if (!IsFresh(serverId, eventGeneratedUtc))
        {
            return false;
        }

        if (!TryParseGameType(gameType, out var parsedGameType))
        {
            _logger.LogWarning("RCON Say skipped for server {ServerId}: unsupported game type '{GameType}'", serverId, gameType);
            return false;
        }

        if (!IsSupportedGameType(parsedGameType))
        {
            _logger.LogWarning("RCON Say skipped for server {ServerId}: unsupported game type '{GameType}'", serverId, parsedGameType);
            return false;
        }

        try
        {
            ApiResult result = parsedGameType switch
            {
                GameType.CallOfDuty2 => await _serversApiClient.Cod2Rcon.V1.Say(
                    serverId,
                    new SayRequest { Message = message },
                    ct).ConfigureAwait(false),
                GameType.CallOfDuty4 => await _serversApiClient.Cod4Rcon.V1.Say(
                    serverId,
                    new SayRequest { Message = message },
                    ct).ConfigureAwait(false),
                GameType.CallOfDuty5 => await _serversApiClient.Cod5Rcon.V1.Say(
                    serverId,
                    new SayRequest { Message = message },
                    ct).ConfigureAwait(false),
                GameType.CallOfDuty4x => await _serversApiClient.CoD4xRcon.V1.Say(
                    serverId,
                    new CoD4xMessageRequestDto { Message = message },
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
                return false;
            }

            _logger.LogInformation("RCON Say sent to server {ServerId} ({GameType})", serverId, parsedGameType);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RCON Say threw for server {ServerId} ({GameType})", serverId, parsedGameType);
            return false;
        }
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

    public async Task<bool> TryTellAsync(
        Guid serverId,
        string gameType,
        string playerGuid,
        string message,
        string? expectedPlayerName,
        DateTime eventGeneratedUtc,
        CancellationToken ct = default)
    {
        if (!IsFresh(serverId, eventGeneratedUtc))
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

            var result = await SendTellAsync(serverId, parsedGameType, player.Slot, message, ct).ConfigureAwait(false);

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
                "RCON Tell sent to server {ServerId} ({GameType}), client {ClientId}",
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

    public async Task<bool> TryTellAsync(
        Guid serverId,
        string gameType,
        string playerGuid,
        int slotId,
        string message,
        string? expectedPlayerName,
        DateTime eventGeneratedUtc,
        CancellationToken ct = default)
    {
        if (!IsFresh(serverId, eventGeneratedUtc))
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
            var slotPlayer = await ResolvePlayerBySlotAsync(serverId, parsedGameType, slotId, ct).ConfigureAwait(false);
            if (slotPlayer is not null
                && string.Equals(slotPlayer.Guid, playerGuid, StringComparison.OrdinalIgnoreCase)
                && NamesMatch(expectedPlayerName, slotPlayer.Name))
            {
                var sendResult = await SendTellAsync(serverId, parsedGameType, slotId, message, ct).ConfigureAwait(false);

                if (sendResult.IsSuccess)
                {
                    _logger.LogInformation(
                        "RCON Tell sent to server {ServerId} ({GameType}), client {ClientId}",
                        serverId,
                        parsedGameType,
                        slotId);
                    return true;
                }

                var sendErrorSummary = SummarizeApiErrors(sendResult);
                _logger.LogWarning(
                    "RCON Tell failed for server {ServerId} ({GameType}), client {ClientId}: {StatusCode}. Errors: {ErrorSummary}. Falling back to guid lookup.",
                    serverId,
                    parsedGameType,
                    slotId,
                    sendResult.StatusCode,
                    sendErrorSummary);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RCON Tell threw for server {ServerId} ({GameType}), client {ClientId}. Falling back to guid lookup.",
                serverId,
                parsedGameType,
                slotId);
        }

        return await TryTellAsync(serverId, gameType, playerGuid, message, expectedPlayerName, eventGeneratedUtc, ct)
            .ConfigureAwait(false);
    }

    private async Task<ResolvedPlayer?> ResolvePlayerByGuidAsync(
        Guid serverId,
        GameType gameType,
        string playerGuid,
        CancellationToken ct)
    {
        if (gameType == GameType.CallOfDuty2)
        {
            var status = await _serversApiClient.Cod2Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!status.IsSuccess || status.Result?.Data is null)
            {
                _logger.LogWarning("Unable to resolve player slot for server {ServerId}: {StatusCode}", serverId, status.StatusCode);
                return null;
            }

            var player = status.Result.Data.Players.FirstOrDefault(p =>
                string.Equals(p.Guid, playerGuid, StringComparison.OrdinalIgnoreCase));

            return player is null ? null : new ResolvedPlayer(player.Num, player.Guid, player.Name);
        }

        if (gameType == GameType.CallOfDuty4)
        {
            var status = await _serversApiClient.Cod4Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!status.IsSuccess || status.Result?.Data is null)
            {
                _logger.LogWarning("Unable to resolve player slot for server {ServerId}: {StatusCode}", serverId, status.StatusCode);
                return null;
            }

            var player = status.Result.Data.Players.FirstOrDefault(p =>
                string.Equals(p.Guid, playerGuid, StringComparison.OrdinalIgnoreCase));

            return player is null ? null : new ResolvedPlayer(player.Num, player.Guid, player.Name);
        }

        if (gameType == GameType.CallOfDuty5)
        {
            var status = await _serversApiClient.Cod5Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!status.IsSuccess || status.Result?.Data is null)
            {
                _logger.LogWarning("Unable to resolve player slot for server {ServerId}: {StatusCode}", serverId, status.StatusCode);
                return null;
            }

            var player = status.Result.Data.Players.FirstOrDefault(p =>
                string.Equals(p.Guid, playerGuid, StringComparison.OrdinalIgnoreCase));

            return player is null ? null : new ResolvedPlayer(player.Num, player.Guid, player.Name);
        }

        if (gameType == GameType.CallOfDuty4x)
        {
            var status = await _serversApiClient.CoD4xRcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!status.IsSuccess || status.Result?.Data is null)
            {
                _logger.LogWarning("Unable to resolve player slot for server {ServerId}: {StatusCode}", serverId, status.StatusCode);
                return null;
            }

            var player = status.Result.Data.Players.FirstOrDefault(p =>
                string.Equals(p.PlayerIdentifier, playerGuid, StringComparison.OrdinalIgnoreCase));

            if (player is null)
            {
                return null;
            }

            var resolvedName = string.IsNullOrWhiteSpace(player.Name)
                ? player.RawName
                : player.Name;

            return new ResolvedPlayer(player.Num, player.PlayerIdentifier, resolvedName);
        }

        _logger.LogWarning("RCON Tell skipped for server {ServerId}: unsupported game type {GameType}", serverId, gameType);
        return null;
    }

    private async Task<ResolvedPlayer?> ResolvePlayerBySlotAsync(
        Guid serverId,
        GameType gameType,
        int slotId,
        CancellationToken ct)
    {
        if (gameType == GameType.CallOfDuty2)
        {
            var status = await _serversApiClient.Cod2Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!status.IsSuccess || status.Result?.Data is null)
            {
                return null;
            }

            var player = status.Result.Data.Players.FirstOrDefault(p => p.Num == slotId);
            return player is null ? null : new ResolvedPlayer(player.Num, player.Guid, player.Name);
        }

        if (gameType == GameType.CallOfDuty4)
        {
            var status = await _serversApiClient.Cod4Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!status.IsSuccess || status.Result?.Data is null)
            {
                return null;
            }

            var player = status.Result.Data.Players.FirstOrDefault(p => p.Num == slotId);
            return player is null ? null : new ResolvedPlayer(player.Num, player.Guid, player.Name);
        }

        if (gameType == GameType.CallOfDuty5)
        {
            var status = await _serversApiClient.Cod5Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!status.IsSuccess || status.Result?.Data is null)
            {
                return null;
            }

            var player = status.Result.Data.Players.FirstOrDefault(p => p.Num == slotId);
            return player is null ? null : new ResolvedPlayer(player.Num, player.Guid, player.Name);
        }

        if (gameType == GameType.CallOfDuty4x)
        {
            var status = await _serversApiClient.CoD4xRcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!status.IsSuccess || status.Result?.Data is null)
            {
                return null;
            }

            var player = status.Result.Data.Players.FirstOrDefault(p => p.Num == slotId);
            if (player is null)
            {
                return null;
            }

            var resolvedName = string.IsNullOrWhiteSpace(player.Name)
                ? player.RawName
                : player.Name;

            return new ResolvedPlayer(player.Num, player.PlayerIdentifier, resolvedName);
        }

        return null;
    }

    private Task<ApiResult<string>> SendTellAsync(
        Guid serverId,
        GameType gameType,
        int slotId,
        string message,
        CancellationToken ct)
    {
        var request = new CoD4xTargetMessageRequestDto
        {
            Target = slotId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Message = message
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

    private bool IsFresh(Guid serverId, DateTime eventGeneratedUtc)
    {
        var age = DateTime.UtcNow - eventGeneratedUtc;

        if (age <= FreshnessThreshold)
        {
            return true;
        }

        _logger.LogInformation(
            "Skipping RCON response for server {ServerId} - event is {Age} old (threshold {Threshold})",
            serverId, age, FreshnessThreshold);
        return false;
    }

    private sealed record ResolvedPlayer(int Slot, string Guid, string? Name);
}
