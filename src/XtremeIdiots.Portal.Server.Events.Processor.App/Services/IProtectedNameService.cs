namespace XtremeIdiots.Portal.Server.Events.Processor.App.Services;

public interface IProtectedNameService
{
    /// <summary>
    /// Check if a player's name violates any protected name rules.
    /// If a violation is found, creates a ban admin action and kicks the player.
    /// Best-effort — never throws.
    /// </summary>
    Task CheckAsync(ProtectedNameContext context, CancellationToken ct = default);
}
