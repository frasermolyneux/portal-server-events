namespace XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

public sealed record ChatModerationSettings(
    int MinMessageLength,
    int? HateSeverityThreshold,
    int? ViolenceSeverityThreshold,
    int? SexualSeverityThreshold,
    int? SelfHarmSeverityThreshold)
{
    public bool IsCategoryEnabled =>
        HateSeverityThreshold.HasValue ||
        ViolenceSeverityThreshold.HasValue ||
        SexualSeverityThreshold.HasValue ||
        SelfHarmSeverityThreshold.HasValue;
}
