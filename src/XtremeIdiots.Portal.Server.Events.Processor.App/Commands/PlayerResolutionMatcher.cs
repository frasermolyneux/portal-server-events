using System.Text.RegularExpressions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

internal static class PlayerResolutionMatcher
{
    private const int ExactScore = 1000;
    private const int PrefixScore = 800;
    private const int ContainsScore = 600;
    private const int TokenOverlapScore = 400;
    private const int AmbiguityThreshold = 25;

    private static readonly Regex QuakeColorCodeRegex = new(@"\^[0-9A-Za-z]", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex NonAlphaNumericRegex = new(@"[^a-z0-9]+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex MultiWhitespaceRegex = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static ResolvePlayerResponseDto ResolvePlayer(IEnumerable<CoD4xStatusPlayerDto>? players, string playerQuery, int maxSuggestions)
    {
        var normalizedQuery = NormalizeCompact(playerQuery);
        var normalizedQueryTokens = NormalizeTokens(playerQuery);

        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return new ResolvePlayerResponseDto
            {
                Status = ResolvePlayerStatus.NotFound,
                Suggestions = []
            };
        }

        var rankedMatches = (players ?? [])
            .Select(player => TryRankCandidate(player, normalizedQuery, normalizedQueryTokens))
            .Where(result => result is not null)
            .Select(result => result!)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Player.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Player.Num)
            .ToList();

        if (rankedMatches.Count == 0)
        {
            return new ResolvePlayerResponseDto
            {
                Status = ResolvePlayerStatus.NotFound,
                Suggestions = []
            };
        }

        if (rankedMatches.Count == 1)
        {
            return new ResolvePlayerResponseDto
            {
                Status = ResolvePlayerStatus.Resolved,
                ResolvedPlayer = ToSuggestion(rankedMatches[0]),
                Suggestions = []
            };
        }

        var top = rankedMatches[0];
        var second = rankedMatches[1];
        var isAmbiguous = top.MatchClass == second.MatchClass && Math.Abs(top.Score - second.Score) <= AmbiguityThreshold;

        if (isAmbiguous)
        {
            return new ResolvePlayerResponseDto
            {
                Status = ResolvePlayerStatus.Ambiguous,
                Suggestions = rankedMatches.Take(maxSuggestions).Select(ToSuggestion).ToList()
            };
        }

        return new ResolvePlayerResponseDto
        {
            Status = ResolvePlayerStatus.Resolved,
            ResolvedPlayer = ToSuggestion(top),
            Suggestions = []
        };
    }

    private static MatchResult? TryRankCandidate(CoD4xStatusPlayerDto player, string normalizedQuery, string normalizedQueryTokens)
    {
        var candidateName = string.IsNullOrWhiteSpace(player.Name) ? player.RawName : player.Name;
        var normalizedName = NormalizeCompact(candidateName);

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        if (normalizedName == normalizedQuery)
        {
            return new MatchResult(player, ExactScore, MatchClass.Exact);
        }

        if (normalizedName.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            var score = PrefixScore + MatchLengthBonus(normalizedQuery, normalizedName);
            return new MatchResult(player, score, MatchClass.Prefix);
        }

        if (normalizedName.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            var score = ContainsScore + MatchLengthBonus(normalizedQuery, normalizedName);
            return new MatchResult(player, score, MatchClass.Contains);
        }

        var overlap = TokenOverlap(normalizedQueryTokens, NormalizeTokens(candidateName));
        if (overlap <= 0)
        {
            return null;
        }

        var overlapScore = TokenOverlapScore + (overlap * 100);
        return new MatchResult(player, overlapScore, MatchClass.TokenOverlap);
    }

    private static ResolvePlayerSuggestionDto ToSuggestion(MatchResult match)
    {
        return new ResolvePlayerSuggestionDto
        {
            Name = string.IsNullOrWhiteSpace(match.Player.Name) ? match.Player.RawName : match.Player.Name,
            Slot = match.Player.Num,
            Guid = match.Player.PlayerIdentifier,
            Score = Math.Round(match.Score, 2, MidpointRounding.AwayFromZero)
        };
    }

    private static string NormalizeCompact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutColorCodes = QuakeColorCodeRegex.Replace(value, string.Empty);
        var lowered = withoutColorCodes.ToLowerInvariant();
        var compact = NonAlphaNumericRegex.Replace(lowered, string.Empty);

        return MultiWhitespaceRegex.Replace(compact, " ").Trim();
    }

    private static string NormalizeTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutColorCodes = QuakeColorCodeRegex.Replace(value, string.Empty);
        var lowered = withoutColorCodes.ToLowerInvariant();
        var withSpaces = NonAlphaNumericRegex.Replace(lowered, " ");

        return MultiWhitespaceRegex.Replace(withSpaces, " ").Trim();
    }

    private static int MatchLengthBonus(string normalizedQuery, string normalizedName)
    {
        if (normalizedName.Length == 0)
        {
            return 0;
        }

        var ratio = (double)normalizedQuery.Length / normalizedName.Length;
        return (int)Math.Round(ratio * 100, MidpointRounding.AwayFromZero);
    }

    private static double TokenOverlap(string normalizedQuery, string normalizedCandidate)
    {
        var queryTokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidateTokens = normalizedCandidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (queryTokens.Length == 0 || candidateTokens.Length == 0)
        {
            return 0;
        }

        var candidateSet = new HashSet<string>(candidateTokens, StringComparer.Ordinal);
        var overlapCount = queryTokens.Count(candidateSet.Contains);

        return (double)overlapCount / queryTokens.Length;
    }

    private sealed record MatchResult(CoD4xStatusPlayerDto Player, double Score, MatchClass MatchClass);

    private enum MatchClass
    {
        Exact,
        Prefix,
        Contains,
        TokenOverlap
    }
}
