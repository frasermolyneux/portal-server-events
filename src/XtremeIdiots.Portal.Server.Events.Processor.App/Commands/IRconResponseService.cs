namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

/// <summary>
/// Sends RCON responses to game servers. Only sends if the event is recent (within threshold).
/// </summary>
public interface IRconResponseService
{
    /// <summary>
    /// Send a broadcast message to a game server via RCON, but only if the event is fresh.
    /// </summary>
    /// <param name="serverId">Game server ID.</param>
    /// <param name="message">Message to broadcast.</param>
    /// <param name="eventGeneratedUtc">When the triggering event was generated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the message was sent, false if skipped due to staleness.</returns>
    Task<bool> TrySayAsync(Guid serverId, string message, DateTime eventGeneratedUtc, CancellationToken ct = default);
}
