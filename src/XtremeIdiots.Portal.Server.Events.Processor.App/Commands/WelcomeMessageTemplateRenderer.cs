namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class WelcomeMessageTemplateRenderer
{
    public string Render(string template, string playerName, string country)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return template
            .Replace("{name}", playerName, StringComparison.Ordinal)
            .Replace("{country}", country, StringComparison.Ordinal);
    }
}
