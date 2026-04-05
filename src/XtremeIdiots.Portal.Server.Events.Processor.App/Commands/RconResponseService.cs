using Microsoft.Extensions.Logging;

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
        var age = DateTime.UtcNow - eventGeneratedUtc;

        if (age > FreshnessThreshold)
        {
            _logger.LogInformation(
                "Skipping RCON response for server {ServerId} — event is {Age} old (threshold {Threshold})",
                serverId, age, FreshnessThreshold);
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
}
