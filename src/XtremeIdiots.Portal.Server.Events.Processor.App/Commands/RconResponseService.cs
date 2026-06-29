using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;

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

    public async Task<bool> TrySayAsync(Guid serverId, string message, DateTime eventGeneratedUtc, CancellationToken ct = default)
    {
        if (!IsFresh(serverId, eventGeneratedUtc))
        {
            return false;
        }

        try
        {
            var result = await _serversApiClient.CoD4xRcon.V1.ConSay(
                serverId,
                new CoD4xMessageRequestDto { Message = message },
                ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                _logger.LogWarning("RCON Say failed for server {ServerId}: {StatusCode}",
                    serverId, result.StatusCode);
                return false;
            }

            _logger.LogInformation("RCON Say sent to server {ServerId}", serverId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RCON Say threw for server {ServerId}", serverId);
            return false;
        }
    }

    public async Task<bool> TryTellAsync(
        Guid serverId,
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

        try
        {
            var statusResult = await _serversApiClient.CoD4xRcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!statusResult.IsSuccess || statusResult.Result?.Data is null)
            {
                _logger.LogWarning("Unable to resolve player slot for server {ServerId}: {StatusCode}", serverId, statusResult.StatusCode);
                return false;
            }

            var player = statusResult.Result.Data.Players.FirstOrDefault(p =>
                string.Equals(p.PlayerIdentifier, playerGuid, StringComparison.OrdinalIgnoreCase));

            if (player is null)
            {
                _logger.LogWarning("Unable to resolve player slot for guid {PlayerGuid} on server {ServerId}", playerGuid, serverId);
                return false;
            }

            if (!NamesMatch(expectedPlayerName, player))
            {
                _logger.LogWarning(
                    "Resolved player name mismatch for guid {PlayerGuid} on server {ServerId}. Expected {ExpectedPlayerName}, got {ActualPlayerName}",
                    playerGuid,
                    serverId,
                    expectedPlayerName,
                    player.Name);
                return false;
            }

            var result = await _serversApiClient.CoD4xRcon.V1.Tell(
                serverId,
                new CoD4xTargetMessageRequestDto
                {
                    Target = player.Num.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Message = message
                },
                ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                var errorSummary = SummarizeApiErrors(result);
                _logger.LogWarning(
                    "RCON Tell failed for server {ServerId}, client {ClientId}: {StatusCode}. Errors: {ErrorSummary}",
                    serverId,
                    player.Num,
                    result.StatusCode,
                    errorSummary);
                return false;
            }

            _logger.LogInformation("RCON Tell sent to server {ServerId}, client {ClientId}", serverId, player.Num);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RCON Tell threw for server {ServerId}, player {PlayerGuid}", serverId, playerGuid);
            return false;
        }
    }

    public async Task<bool> TryTellAsync(
        Guid serverId,
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

        try
        {
            var statusResult = await _serversApiClient.CoD4xRcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (statusResult.IsSuccess && statusResult.Result?.Data is not null)
            {
                var slotPlayer = statusResult.Result.Data.Players.FirstOrDefault(p => p.Num == slotId);
                if (slotPlayer is not null
                    && string.Equals(slotPlayer.PlayerIdentifier, playerGuid, StringComparison.OrdinalIgnoreCase)
                    && NamesMatch(expectedPlayerName, slotPlayer))
                {
                    var sendResult = await _serversApiClient.CoD4xRcon.V1.Tell(
                        serverId,
                        new CoD4xTargetMessageRequestDto
                        {
                            Target = slotId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            Message = message
                        },
                        ct).ConfigureAwait(false);

                    if (sendResult.IsSuccess)
                    {
                        _logger.LogInformation("RCON Tell sent to server {ServerId}, client {ClientId}", serverId, slotId);
                        return true;
                    }

                    var sendErrorSummary = SummarizeApiErrors(sendResult);
                    _logger.LogWarning(
                        "RCON Tell failed for server {ServerId}, client {ClientId}: {StatusCode}. Errors: {ErrorSummary}. Falling back to guid lookup.",
                        serverId,
                        slotId,
                        sendResult.StatusCode,
                        sendErrorSummary);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RCON Tell threw for server {ServerId}, client {ClientId}. Falling back to guid lookup.",
                serverId, slotId);
        }

        return await TryTellAsync(serverId, playerGuid, message, expectedPlayerName, eventGeneratedUtc, ct)
            .ConfigureAwait(false);
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

    private static bool NamesMatch(string? expectedPlayerName, CoD4xStatusPlayerDto player)
    {
        if (string.IsNullOrWhiteSpace(expectedPlayerName))
        {
            return true;
        }

        var resolvedName = string.IsNullOrWhiteSpace(player.Name) ? player.RawName : player.Name;
        return IsLikelySamePlayerName(expectedPlayerName, resolvedName);
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
}
