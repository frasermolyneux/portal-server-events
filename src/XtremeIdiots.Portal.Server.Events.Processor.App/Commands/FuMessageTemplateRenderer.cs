using System.Text.RegularExpressions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class FuMessageTemplateRenderer
{
    private static readonly Regex NameTokenRegex = new("\\{name\\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string Render(string template, string resolvedPlayerName)
    {
        var sourceTemplate = template ?? string.Empty;
        var playerName = resolvedPlayerName ?? string.Empty;

        return NameTokenRegex.Replace(sourceTemplate, playerName);
    }
}
