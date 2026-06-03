namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public interface ICommandParser
{
    CommandParseResult Parse(string? message);
}
