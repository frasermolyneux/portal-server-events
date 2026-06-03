namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class CommandAuthorizationOptions
{
    public Dictionary<string, CommandPolicyOptions> Policies { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CommandPolicyOptions
{
    public string[] RequiredTags { get; init; } = [];
    public string[] RequiredClaims { get; init; } = [];
    public string[] AllowedGameTypes { get; init; } = [];
    public Guid[] AllowedServerIds { get; init; } = [];
    public bool Privileged { get; init; } = true;
}

public sealed record CommandAuthorizationContext
{
    public required string CommandPrefix { get; init; }
    public required string? RequiredPolicy { get; init; }
    public required string GameType { get; init; }
    public required Guid ServerId { get; init; }
    public required Guid? PlayerId { get; init; }
    public required CommandAuthorizationSnapshot? Snapshot { get; init; }
}

public sealed record CommandAuthorizationResult
{
    public bool Allowed { get; init; }
    public string? Reason { get; init; }

    public static CommandAuthorizationResult Allow() => new() { Allowed = true };

    public static CommandAuthorizationResult Deny(string reason) => new()
    {
        Allowed = false,
        Reason = reason
    };
}
