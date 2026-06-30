namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

/// <summary>
/// Sends RCON responses to game servers. Only sends if the event is recent (within threshold).
/// </summary>
public interface IRconResponseService
{
    /// <summary>
    /// Send a broadcast message to a game server via RCON for a specific game type,
    /// but only if the event is fresh.
    /// </summary>
    /// <param name="serverId">Game server ID.</param>
    /// <param name="gameType">Game type for endpoint routing.</param>
    /// <param name="message">Message to broadcast.</param>
    /// <param name="eventGeneratedUtc">When the triggering event was generated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the message was sent, false if skipped due to staleness or unsupported game.</returns>
    Task<bool> TrySayAsync(
        Guid serverId,
        string gameType,
        string message,
        DateTime eventGeneratedUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Send a broadcast message to a game server via RCON, but only if the event is fresh.
    /// </summary>
    /// <param name="serverId">Game server ID.</param>
    /// <param name="message">Message to broadcast.</param>
    /// <param name="eventGeneratedUtc">When the triggering event was generated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the message was sent, false if skipped due to staleness.</returns>
    Task<bool> TrySayAsync(Guid serverId, string message, DateTime eventGeneratedUtc, CancellationToken ct = default);

    /// <summary>
    /// Send a private message to a specific player via RCON, but only if the event is fresh.
    /// The player is resolved from current server status by player guid.
    /// </summary>
    /// <param name="serverId">Game server ID.</param>
    /// <param name="gameType">Game type for endpoint routing.</param>
    /// <param name="playerGuid">Target player's game guid.</param>
    /// <param name="message">Message to deliver.</param>
    /// <param name="expectedPlayerName">Optional player name for verification.</param>
    /// <param name="eventGeneratedUtc">When the triggering event was generated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the message was sent, false if skipped, unsupported, or failed.</returns>
    Task<bool> TryTellAsync(
        Guid serverId,
        string gameType,
        string playerGuid,
        string message,
        string? expectedPlayerName,
        DateTime eventGeneratedUtc,
        CancellationToken ct = default);

    Task<bool> TryTellAsync(
        Guid serverId,
        string playerGuid,
        string message,
        string? expectedPlayerName,
        DateTime eventGeneratedUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Send a private message to a specific player using a known slot id, but only if the event is fresh.
    /// Implementations should gracefully fall back to guid-based lookup if slot delivery fails.
    /// </summary>
    /// <param name="serverId">Game server ID.</param>
    /// <param name="gameType">Game type for endpoint routing.</param>
    /// <param name="playerGuid">Target player's game guid.</param>
    /// <param name="slotId">Current target slot id.</param>
    /// <param name="message">Message to deliver.</param>
    /// <param name="expectedPlayerName">Optional player name for verification.</param>
    /// <param name="eventGeneratedUtc">When the triggering event was generated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if sent, false when skipped, unsupported, or failed.</returns>
    Task<bool> TryTellAsync(
        Guid serverId,
        string gameType,
        string playerGuid,
        int slotId,
        string message,
        string? expectedPlayerName,
        DateTime eventGeneratedUtc,
        CancellationToken ct = default);

    Task<bool> TryTellAsync(
        Guid serverId,
        string playerGuid,
        int slotId,
        string message,
        string? expectedPlayerName,
        DateTime eventGeneratedUtc,
        CancellationToken ct = default);
}
