namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public interface IWelcomeMessageSettingsProvider
{
    Task<EffectiveWelcomeMessageSettings> GetEffectiveSettingsAsync(Guid serverId, CancellationToken ct = default);
}
