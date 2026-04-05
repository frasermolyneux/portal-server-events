namespace XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

public sealed record ChatModerationResult(
    int HateSeverity,
    int SelfHarmSeverity,
    int SexualSeverity,
    int ViolenceSeverity,
    int MaxSeverity,
    string Category);
