using System.Text.RegularExpressions;

using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.WelcomeMessages;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

/// <summary>
/// Resolved token values for a single welcome-message delivery. Keys align with
/// <see cref="WelcomeMessageTokens"/>.
/// </summary>
public sealed record WelcomeMessageTokenValues
{
    public string Name { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;
    public string PlayerGuid { get; init; } = string.Empty;
    public string SteamId { get; init; } = string.Empty;
    public string PlayerCount { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> ToMap() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = Name,
        ["country"] = Country,
        ["ipaddress"] = IpAddress,
        ["tags"] = Tags,
        ["guid"] = PlayerGuid,
        ["steamid"] = SteamId,
        ["playercount"] = PlayerCount
    };
}

public sealed partial class WelcomeMessageTemplateRenderer
{
    [GeneratedRegex("\\{(?<key>[a-zA-Z0-9]+)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    /// <summary>
    /// Renders a welcome-message template, substituting known <see cref="WelcomeMessageTokens"/>
    /// case-insensitively in a single pass. Unknown <c>{tokens}</c> are left untouched, and token
    /// values are never re-scanned, so a value containing braces cannot inject further tokens.
    /// </summary>
    public string Render(string template, WelcomeMessageTokenValues values)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var map = values.ToMap();

        return TokenRegex().Replace(template, match =>
        {
            var key = match.Groups["key"].Value;
            return map.TryGetValue(key, out var value) ? value : match.Value;
        });
    }
}
