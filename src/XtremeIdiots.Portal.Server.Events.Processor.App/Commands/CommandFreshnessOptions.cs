namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class CommandFreshnessOptions
{
    public int DefaultSeconds { get; set; } = 5;
    public int ReadOnlySeconds { get; set; } = 5;
    public int MutatingSeconds { get; set; } = 3;

    public Dictionary<string, int> CommandSeconds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
