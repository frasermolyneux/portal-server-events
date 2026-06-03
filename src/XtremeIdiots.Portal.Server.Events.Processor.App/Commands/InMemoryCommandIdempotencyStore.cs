using System.Collections.Concurrent;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class InMemoryCommandIdempotencyStore : ICommandIdempotencyStore
{
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InProgressTimeout = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Task<CommandIdempotencyDecision> TryBeginAsync(
        CommandIdempotencyKey key,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            PruneExpiredEntries(utcNow);

            if (_entries.TryGetValue(key.Value, out var existing))
            {
                if (existing.CompletedResult is null)
                {
                    if (utcNow - existing.UpdatedUtc > InProgressTimeout)
                    {
                        _entries[key.Value] = new Entry(utcNow, null);
                        return Task.FromResult(CommandIdempotencyDecision.Acquired());
                    }

                    return Task.FromResult(CommandIdempotencyDecision.InProgress());
                }

                return Task.FromResult(CommandIdempotencyDecision.Completed(existing.CompletedResult));
            }

            _entries[key.Value] = new Entry(utcNow, null);
            return Task.FromResult(CommandIdempotencyDecision.Acquired());
        }
    }

    public Task CompleteAsync(
        CommandIdempotencyKey key,
        CommandResult result,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _entries[key.Value] = new Entry(utcNow, result);
            PruneExpiredEntries(utcNow);
        }

        return Task.CompletedTask;
    }

    private void PruneExpiredEntries(DateTime utcNow)
    {
        foreach (var kvp in _entries)
        {
            if (kvp.Value.CompletedResult is null)
            {
                continue;
            }

            if (utcNow - kvp.Value.UpdatedUtc <= RetentionWindow)
            {
                continue;
            }

            _entries.TryRemove(kvp.Key, out _);
        }
    }

    private sealed record Entry(DateTime UpdatedUtc, CommandResult? CompletedResult);
}
