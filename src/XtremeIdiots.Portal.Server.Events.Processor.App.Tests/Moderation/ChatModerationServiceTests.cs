using Azure;
using Azure.AI.ContentSafety;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Moderation;

public sealed class ChatModerationServiceTests
{
    [Fact]
    public async Task AnalyseAsync_PreservesMissingAndZeroCategorySeverities()
    {
        var client = new Mock<ContentSafetyClient>();
        var response = ContentSafetyModelFactory.AnalyzeTextResult([], [
            ContentSafetyModelFactory.TextCategoriesAnalysis(TextCategory.Hate, 0),
            ContentSafetyModelFactory.TextCategoriesAnalysis(TextCategory.Sexual, null),
            ContentSafetyModelFactory.TextCategoriesAnalysis(TextCategory.Violence, 4)
        ]);
        client.Setup(item => item.AnalyzeTextAsync(It.IsAny<AnalyzeTextOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(response, Mock.Of<Response>()));
        var service = new ChatModerationService(client.Object, NullLogger<ChatModerationService>.Instance);

        var result = await service.AnalyseAsync("message");

        Assert.NotNull(result);
        Assert.Equal(0, result.HateSeverity);
        Assert.Null(result.SelfHarmSeverity);
        Assert.Null(result.SexualSeverity);
        Assert.Equal(4, result.ViolenceSeverity);
        client.Verify(item => item.AnalyzeTextAsync(It.IsAny<AnalyzeTextOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnalyseAsync_MissingCategoriesRemainUnknown()
    {
        var client = new Mock<ContentSafetyClient>();
        var response = ContentSafetyModelFactory.AnalyzeTextResult([], []);
        client.Setup(item => item.AnalyzeTextAsync(It.IsAny<AnalyzeTextOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(response, Mock.Of<Response>()));
        var service = new ChatModerationService(client.Object, NullLogger<ChatModerationService>.Instance);

        var result = await service.AnalyseAsync("message");

        Assert.NotNull(result);
        Assert.Null(result.HateSeverity);
        Assert.Null(result.SelfHarmSeverity);
        Assert.Null(result.SexualSeverity);
        Assert.Null(result.ViolenceSeverity);
    }
}
