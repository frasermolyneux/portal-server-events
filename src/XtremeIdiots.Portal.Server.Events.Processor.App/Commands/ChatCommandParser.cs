using System.Text;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class ChatCommandParser : ICommandParser
{
    public CommandParseResult Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return CommandParseResult.NotACommand("Message is empty");
        }

        var normalized = message.Trim();
        if (!normalized.StartsWith('!'))
        {
            return CommandParseResult.NotACommand("Message is not a command");
        }

        var tokens = Tokenize(normalized, out var hasUnbalancedQuotes);
        if (hasUnbalancedQuotes)
        {
            return CommandParseResult.NotACommand("Command has unbalanced quotes");
        }

        if (tokens.Count == 0)
        {
            return CommandParseResult.NotACommand("Command has no tokens");
        }

        var prefixToken = tokens[0];
        if (prefixToken.Length <= 1)
        {
            return CommandParseResult.NotACommand("Command prefix is missing");
        }

        var command = new ChatCommandEnvelope
        {
            RawMessage = message,
            NormalizedMessage = normalized,
            PrefixToken = prefixToken.ToLowerInvariant(),
            Verb = prefixToken[1..].ToLowerInvariant(),
            Arguments = tokens.Skip(1).ToArray(),
            ArgumentText = BuildArgumentText(normalized, prefixToken)
        };

        return CommandParseResult.Parsed(command);
    }

    private static string BuildArgumentText(string normalized, string prefixToken)
    {
        if (normalized.Length <= prefixToken.Length)
        {
            return string.Empty;
        }

        return normalized[prefixToken.Length..].TrimStart();
    }

    private static List<string> Tokenize(string input, out bool hasUnbalancedQuotes)
    {
        var tokens = new List<string>();
        var buffer = new StringBuilder();
        var inQuotes = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                Flush();
                continue;
            }

            buffer.Append(c);
        }

        Flush();
        hasUnbalancedQuotes = inQuotes;
        return tokens;

        void Flush()
        {
            if (buffer.Length == 0)
            {
                return;
            }

            tokens.Add(buffer.ToString());
            buffer.Clear();
        }
    }
}
