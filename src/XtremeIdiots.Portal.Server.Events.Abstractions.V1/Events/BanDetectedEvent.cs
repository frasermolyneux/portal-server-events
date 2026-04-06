namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

/// <summary>
/// Published when new untagged ban entries are detected in a game server's ban.txt.
/// Only contains the NEW bans found since the last check — not the full file content.
/// </summary>
public class BanDetectedEvent : ServerEventBase
{
    public required IReadOnlyList<DetectedBan> NewBans { get; init; }
}

/// <summary>
/// A single ban entry parsed from a game server's ban file.
/// </summary>
public class DetectedBan
{
    public required string PlayerGuid { get; init; }
    public required string PlayerName { get; init; }
}
