using Microsoft.Extensions.Caching.Memory;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class RegisterCommandRateLimiter : IRegisterCommandRateLimiter
{
    private const int MaxAttemptsPerWindow = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly IMemoryCache _memoryCache;

    public RegisterCommandRateLimiter(IMemoryCache memoryCache)
    {
        _memoryCache = memoryCache;
    }

    public bool TryAcquire(Guid playerId, DateTime utcNow, out TimeSpan retryAfter)
    {
        var cacheKey = $"register-rate-limit:{playerId:N}";

        var state = _memoryCache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Window;
            return new RegisterRateLimitState(utcNow);
        });

        if (state is null)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }

        lock (state)
        {
            var elapsed = utcNow - state.WindowStartUtc;
            if (elapsed >= Window)
            {
                state.WindowStartUtc = utcNow;
                state.AttemptCount = 0;
                retryAfter = TimeSpan.Zero;
                state.AttemptCount++;
                return true;
            }

            if (state.AttemptCount >= MaxAttemptsPerWindow)
            {
                retryAfter = Window - elapsed;
                return false;
            }

            state.AttemptCount++;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private sealed class RegisterRateLimitState
    {
        public RegisterRateLimitState(DateTime windowStartUtc)
        {
            WindowStartUtc = windowStartUtc;
            AttemptCount = 0;
        }

        public DateTime WindowStartUtc { get; set; }

        public int AttemptCount { get; set; }
    }
}
