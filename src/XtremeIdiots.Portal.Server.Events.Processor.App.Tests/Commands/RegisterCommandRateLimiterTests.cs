using Microsoft.Extensions.Caching.Memory;

using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class RegisterCommandRateLimiterTests
{
    [Fact]
    public void TryAcquire_AllowsFirstFiveAttempts_BlocksSixth()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new RegisterCommandRateLimiter(cache);
        var playerId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            var allowed = sut.TryAcquire(playerId, now, out var retryAfter);
            Assert.True(allowed);
            Assert.Equal(TimeSpan.Zero, retryAfter);
        }

        var blocked = sut.TryAcquire(playerId, now, out var blockedRetryAfter);

        Assert.False(blocked);
        Assert.True(blockedRetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void TryAcquire_AfterWindowElapsed_AllowsAgain()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new RegisterCommandRateLimiter(cache);
        var playerId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            Assert.True(sut.TryAcquire(playerId, now, out _));
        }

        Assert.False(sut.TryAcquire(playerId, now, out _));

        var allowedAfterWindow = sut.TryAcquire(playerId, now.AddMinutes(1).AddSeconds(1), out var retryAfter);

        Assert.True(allowedAfterWindow);
        Assert.Equal(TimeSpan.Zero, retryAfter);
    }

    [Fact]
    public void TryAcquire_UsesIndependentBucketsPerPlayer()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new RegisterCommandRateLimiter(cache);
        var firstPlayerId = Guid.NewGuid();
        var secondPlayerId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            Assert.True(sut.TryAcquire(firstPlayerId, now, out _));
        }

        Assert.False(sut.TryAcquire(firstPlayerId, now, out _));
        Assert.True(sut.TryAcquire(secondPlayerId, now, out var secondRetryAfter));
        Assert.Equal(TimeSpan.Zero, secondRetryAfter);
    }
}
