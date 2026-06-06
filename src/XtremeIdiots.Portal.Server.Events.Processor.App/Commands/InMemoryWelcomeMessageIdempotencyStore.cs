using System.Collections.Concurrent;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class InMemoryWelcomeMessageIdempotencyStore : IWelcomeMessageIdempotencyStore
{
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InProgressTimeout = TimeSpan.FromMinutes(5);

    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Task<bool> TryBeginAsync(string key, DateTime utcNow, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            PruneExpiredEntries(utcNow);

            if (_entries.TryGetValue(key, out var existing))
            {
                if (!existing.Completed)
                {
                    if (utcNow - existing.UpdatedUtc > InProgressTimeout)
                    {
                        _entries[key] = new Entry(utcNow, Completed: false);
                        return Task.FromResult(true);
                    }

                    return Task.FromResult(false);
                }

                return Task.FromResult(false);
            }

            _entries[key] = new Entry(utcNow, Completed: false);
            return Task.FromResult(true);
        }
    }

    public Task CompleteAsync(string key, DateTime utcNow, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _entries[key] = new Entry(utcNow, Completed: true);
            PruneExpiredEntries(utcNow);
        }

        return Task.CompletedTask;
    }

    private void PruneExpiredEntries(DateTime utcNow)
    {
        foreach (var kvp in _entries)
        {
            if (!kvp.Value.Completed)
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

    private sealed record Entry(DateTime UpdatedUtc, bool Completed);
}
