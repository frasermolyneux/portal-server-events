using Moq;

using XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Moderation;

public sealed class ChatModerationAnalysisTests
{
    [Fact]
    public async Task GetAsync_SharesOneCallWithinAttemptButNotBetweenAttempts()
    {
        var service = new Mock<IChatModerationService>();
        var completion = new TaskCompletionSource<ChatModerationResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Setup(x => x.AnalyseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(completion.Task);
        var firstAttempt = new ChatModerationAnalysis(service.Object, "message", CancellationToken.None);
        var secondAttempt = new ChatModerationAnalysis(service.Object, "message", CancellationToken.None);

        var first = firstAttempt.GetAsync();
        var shared = firstAttempt.GetAsync();
        var second = secondAttempt.GetAsync();
        completion.SetResult(new ChatModerationResult(0, 0, 0, 0, 0, "Hate"));
        await Task.WhenAll(first, shared, second);

        Assert.Same(first, shared);
        service.Verify(x => x.AnalyseAsync("message", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
