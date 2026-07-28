namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public interface IVpnProtectionRconEnforcer
{
    Task<VpnProtectionRconOutcome> EnforceAsync(
        VpnProtectionContext context,
        VpnProtectionAction action,
        string reason,
        CancellationToken ct = default);
}

public enum VpnProtectionRconOutcome
{
    NotRequired = 0,
    Succeeded,
    PlayerNotConnected,
    UnsupportedGame,
    Failed
}
