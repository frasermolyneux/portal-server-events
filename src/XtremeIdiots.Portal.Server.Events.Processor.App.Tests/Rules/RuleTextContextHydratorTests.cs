using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using MX.Api.Abstractions;

using Newtonsoft.Json;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class RuleTextContextHydratorTests
{
    [Fact]
    public async Task ProfileAndTagsShareOneLookupAcrossConcurrentActions()
    {
        var repository = new Mock<IRepositoryApiClient>();
        var servers = new Mock<IServersApiClient>(MockBehavior.Strict);
        var player = JsonConvert.DeserializeObject<PlayerDto>(
            // lang=json
            """{"guid":"PLAYER","gameType":"CallOfDuty4","steamId":"steam","tags":[{"tag":{"name":"Admin"}}]}""")!;
        repository.Setup(item => item.Players.V1.GetPlayerByGameType(GameType.CallOfDuty4, "player", PlayerEntityOptions.Tags))
            .ReturnsAsync(new ApiResult<PlayerDto>(HttpStatusCode.OK, new ApiResponse<PlayerDto>(player)));
        var invocation = new RuleInvocation(new() { GameType = GameType.CallOfDuty4, PlayerGuid = "player" }, new(), null);
        var hydrator = new RuleTextContextHydrator(repository.Object, servers.Object,
            NullLogger<RuleTextContextHydrator>.Instance);

        var values = await Task.WhenAll(
            hydrator.HydrateAsync(invocation, new HashSet<RuleTextTokenSource> { RuleTextTokenSource.PlayerProfile }, default),
            hydrator.HydrateAsync(invocation, new HashSet<RuleTextTokenSource> { RuleTextTokenSource.PlayerTags }, default));

        Assert.All(values, value =>
        {
            Assert.Equal("steam", value.Player!.SteamId);
            Assert.Equal(["Admin"], value.Player.Tags);
            Assert.Null(value.Player.FirstSeenUtc);
        });
        repository.Verify(item => item.Players.V1.GetPlayerByGameType(
            GameType.CallOfDuty4, "player", PlayerEntityOptions.Tags), Times.Once);
        servers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProfileTransportFailureProducesUnavailableFactsForBothProfileAndTags()
    {
        var repository = new Mock<IRepositoryApiClient>();
        repository.Setup(item => item.Players.V1.GetPlayerByGameType(
                GameType.CallOfDuty4, "player", PlayerEntityOptions.Tags))
            .ThrowsAsync(new HttpRequestException("Player service unavailable."));
        var invocation = new RuleInvocation(new() { GameType = GameType.CallOfDuty4, PlayerGuid = "player" }, new(), null);
        var hydrator = new RuleTextContextHydrator(repository.Object, Mock.Of<IServersApiClient>(),
            NullLogger<RuleTextContextHydrator>.Instance);

        var values = await hydrator.HydrateAsync(invocation,
            new HashSet<RuleTextTokenSource> { RuleTextTokenSource.PlayerProfile, RuleTextTokenSource.PlayerTags }, default);

        Assert.Null(values.Player!.SteamId);
        Assert.Null(values.Player.Tags);
        Assert.Contains(RuleTextTokenSource.PlayerProfile, values.SourceDiagnostics.Keys);
        Assert.Contains(RuleTextTokenSource.PlayerTags, values.SourceDiagnostics.Keys);
    }

    [Fact]
    public async Task MissingIntelligenceDoesNotInventProviderFactsOrStartUnrelatedLookups()
    {
        var repository = new Mock<IRepositoryApiClient>(MockBehavior.Strict);
        var servers = new Mock<IServersApiClient>(MockBehavior.Strict);
        var invocation = new RuleInvocation(new(), new(), null);
        var hydrator = new RuleTextContextHydrator(repository.Object, servers.Object,
            NullLogger<RuleTextContextHydrator>.Instance);

        var values = await hydrator.HydrateAsync(invocation,
            new HashSet<RuleTextTokenSource> { RuleTextTokenSource.IpIntelligence }, default);

        Assert.Null(values.Player!.Geo);
        Assert.Null(values.Player.Network);
        Assert.Null(values.Server!.CurrentPlayerCount);
        Assert.Null(values.Server.MaxPlayerCount);
        Assert.Contains(RuleTextTokenSource.IpIntelligence, values.SourceDiagnostics.Keys);
        repository.VerifyNoOtherCalls();
        servers.VerifyNoOtherCalls();
    }
}
