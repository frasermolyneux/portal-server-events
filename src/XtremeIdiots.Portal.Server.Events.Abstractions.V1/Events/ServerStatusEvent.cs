namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

public class ServerStatusEvent : ServerEventBase
{
    public required string MapName { get; init; }
    public required string GameName { get; init; }
    public required int PlayerCount { get; init; }
    public required IReadOnlyList<ConnectedPlayer> Players { get; init; }
    public string? ServerTitle { get; init; }
    public string? ServerMod { get; init; }
    public int? MaxPlayers { get; init; }
}

public class ConnectedPlayer
{
    public required string PlayerGuid { get; init; }
    public required string Username { get; init; }
    public required string IpAddress { get; init; }
    public required int SlotId { get; init; }
    public required DateTime ConnectedAtUtc { get; init; }
    public int Score { get; init; }
    public int Ping { get; init; }
    public int Rate { get; init; }
}
