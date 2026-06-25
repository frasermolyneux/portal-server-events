using Microsoft.Extensions.Logging;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class RconResponseService : IRconResponseService
{
    private static readonly TimeSpan FreshnessThreshold = TimeSpan.FromSeconds(5);

    private readonly IRconApi _rconApi;
    private readonly ILogger<RconResponseService> _logger;

    public RconResponseService(IRconApi rconApi, ILogger<RconResponseService> logger)
    {
        _rconApi = rconApi;
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
            var result = await _rconApi.Say(serverId, message);

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
            var statusResult = await _rconApi.GetServerStatus(serverId);
            if (!statusResult.IsSuccess || statusResult.Result?.Data is null)
            {
                _logger.LogWarning("Unable to resolve player slot for server {ServerId}: {StatusCode}", serverId, statusResult.StatusCode);
                return false;
            }

            var player = statusResult.Result.Data.Players.FirstOrDefault(p =>
                string.Equals(p.Guid, playerGuid, StringComparison.OrdinalIgnoreCase));

            if (player is null)
            {
                _logger.LogWarning("Unable to resolve player slot for guid {PlayerGuid} on server {ServerId}", playerGuid, serverId);
                return false;
            }

            var result = await _rconApi.TellPlayerWithVerification(serverId, player.Num, message, expectedPlayerName);

            if (!result.IsSuccess)
            {
                var errorSummary = SummarizeApiErrors(result);
                _logger.LogWarning(
                    "RCON TellPlayerWithVerification failed for server {ServerId}, client {ClientId}: {StatusCode}. Errors: {ErrorSummary}",
                    serverId,
                    player.Num,
                    result.StatusCode,
                    errorSummary);
                return false;
            }

            _logger.LogInformation("RCON TellPlayerWithVerification sent to server {ServerId}, client {ClientId}", serverId, player.Num);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RCON TellPlayerWithVerification threw for server {ServerId}, player {PlayerGuid}", serverId, playerGuid);
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
            var result = await _rconApi.TellPlayerWithVerification(serverId, slotId, message, expectedPlayerName);
            if (result.IsSuccess)
            {
                _logger.LogInformation("RCON TellPlayerWithVerification sent to server {ServerId}, client {ClientId}", serverId, slotId);
                return true;
            }

            var errorSummary = SummarizeApiErrors(result);
            _logger.LogWarning(
                "RCON TellPlayerWithVerification failed for server {ServerId}, client {ClientId}: {StatusCode}. Errors: {ErrorSummary}. Falling back to guid lookup.",
                serverId,
                slotId,
                result.StatusCode,
                errorSummary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RCON TellPlayerWithVerification threw for server {ServerId}, client {ClientId}. Falling back to guid lookup.",
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
