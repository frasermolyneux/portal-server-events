using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class InMemoryCommandIdempotencyStoreTests
{
    [Fact]
    public async Task TryBeginAsync_WhenNotSeenBefore_Acquires()
    {
        var sut = new InMemoryCommandIdempotencyStore();
        var key = new CommandIdempotencyKey("server:1:!register");

        var decision = await sut.TryBeginAsync(key, DateTime.UtcNow);

        Assert.Equal(CommandIdempotencyState.Acquired, decision.State);
    }

    [Fact]
    public async Task TryBeginAsync_WhenInProgress_ReturnsInProgress()
    {
        var sut = new InMemoryCommandIdempotencyStore();
        var key = new CommandIdempotencyKey("server:1:!register");
        var now = DateTime.UtcNow;

        await sut.TryBeginAsync(key, now);
        var second = await sut.TryBeginAsync(key, now.AddSeconds(1));

        Assert.Equal(CommandIdempotencyState.InProgress, second.State);
    }

    [Fact]
    public async Task TryBeginAsync_AfterCompletion_ReplaysStoredResult()
    {
        var sut = new InMemoryCommandIdempotencyStore();
        var key = new CommandIdempotencyKey("server:1:!register");
        var now = DateTime.UtcNow;

        await sut.TryBeginAsync(key, now);
        await sut.CompleteAsync(key, CommandResult.Ok("done"), now.AddSeconds(1));

        var replay = await sut.TryBeginAsync(key, now.AddSeconds(2));

        Assert.Equal(CommandIdempotencyState.Completed, replay.State);
        Assert.Equal("done", replay.ExistingResult?.ResponseMessage);
    }

    [Fact]
    public async Task TryBeginAsync_DoesNotPruneInProgressEntry()
    {
        var sut = new InMemoryCommandIdempotencyStore();
        var key = new CommandIdempotencyKey("server:1:!register");
        var now = DateTime.UtcNow;

        await sut.TryBeginAsync(key, now);
        var afterRetention = await sut.TryBeginAsync(key, now.AddMinutes(2));

        Assert.Equal(CommandIdempotencyState.InProgress, afterRetention.State);
    }

    [Fact]
    public async Task TryBeginAsync_WhenInProgressEntryIsStale_Reacquires()
    {
        var sut = new InMemoryCommandIdempotencyStore();
        var key = new CommandIdempotencyKey("server:1:!register");
        var now = DateTime.UtcNow;

        await sut.TryBeginAsync(key, now);
        var reacquired = await sut.TryBeginAsync(key, now.AddMinutes(6));

        Assert.Equal(CommandIdempotencyState.Acquired, reacquired.State);
    }
}
