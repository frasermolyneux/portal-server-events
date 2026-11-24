using System.Globalization;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal sealed record IdentifierHistory(bool Available, DateTimeOffset? FirstSeenAtUtc);

internal static class PlayerRuleConditions
{
    public static bool? Tags(IReadOnlyCollection<Guid>? actual, IReadOnlyCollection<string> expected, string operation)
    {
        if (actual is null || expected.Count == 0)
        {
            return null;
        }

        var expectedIds = new HashSet<Guid>();
        foreach (var value in expected)
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
            {
                return null;
            }

            expectedIds.Add(id);
        }

        return operation switch
        {
            "ContainsAny" => actual.Any(expectedIds.Contains),
            "ContainsAll" => expectedIds.All(actual.Contains),
            "ContainsNone" => !actual.Any(expectedIds.Contains),
            _ => null
        };
    }

    public static bool? FirstSeen(
        IdentifierHistory history,
        string operation,
        string? value,
        DateTimeOffset evaluatedAtUtc)
    {
        if (!history.Available)
        {
            return null;
        }

        if (history.FirstSeenAtUtc > evaluatedAtUtc)
        {
            return null;
        }

        if (operation == "IsNew")
        {
            return history.FirstSeenAtUtc is null;
        }

        if (operation == "IsKnown")
        {
            return history.FirstSeenAtUtc is not null;
        }

        if (history.FirstSeenAtUtc is not { } firstSeenAtUtc)
        {
            return null;
        }

        if (operation == "WithinAge")
        {
            return TryDuration(value, out var duration)
                ? evaluatedAtUtc - firstSeenAtUtc <= duration
                : null;
        }

        if (!TryUtcDate(value, out var date))
        {
            return null;
        }

        return operation switch
        {
            "Before" => firstSeenAtUtc < date,
            "After" => firstSeenAtUtc > date,
            _ => null
        };
    }

    public static bool? ProfileAge(
        DateTimeOffset? createdAtUtc,
        string operation,
        string? durationValue,
        DateTimeOffset evaluatedAtUtc)
    {
        if (createdAtUtc is not { } created || created > evaluatedAtUtc ||
            !TryDuration(durationValue, out var duration))
        {
            return null;
        }

        var age = evaluatedAtUtc - created;
        return operation switch
        {
            "LessThan" => age < duration,
            "LessThanOrEqual" => age <= duration,
            "GreaterThan" => age > duration,
            "GreaterThanOrEqual" => age >= duration,
            _ => null
        };
    }

    private static bool TryDuration(string? value, out TimeSpan duration) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration) && duration >= TimeSpan.Zero;

    private static bool TryUtcDate(string? value, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
}
