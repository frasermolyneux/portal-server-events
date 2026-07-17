namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public interface IVpnProtectionSettingsProvider
{
    Task<EffectiveVpnProtectionSettings> GetEffectiveSettingsAsync(
        Guid serverId,
        CancellationToken ct = default);
}