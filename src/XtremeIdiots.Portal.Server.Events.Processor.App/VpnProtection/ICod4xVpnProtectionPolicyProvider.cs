namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public interface ICod4xVpnProtectionPolicyProvider
{
    Task<bool> IsEnabledAsync(Guid serverId, CancellationToken ct = default);
}