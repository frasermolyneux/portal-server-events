using System.Text.RegularExpressions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

internal static class BotMessageHeader
{
    private static readonly Regex CodColourCodes = new(@"\^[0-9]", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static string Create(string agentName, bool privateMessage, bool useCodColorCodes)
    {
        var name = agentName.Trim();
        if (!useCodColorCodes)
        {
            name = CodColourCodes.Replace(name, string.Empty).Trim();
        }

        var marker = privateMessage ? useCodColorCodes ? "^5(PM)^7 " : "(PM) " : string.Empty;
        return $"{marker}{name} ";
    }
}
