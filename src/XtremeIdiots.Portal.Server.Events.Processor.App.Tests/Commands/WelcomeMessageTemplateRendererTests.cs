using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class WelcomeMessageTemplateRendererTests
{
    private readonly WelcomeMessageTemplateRenderer _sut = new();

    [Fact]
    public void Render_ReplacesAllKnownTokens()
    {
        var values = new WelcomeMessageTokenValues
        {
            Name = "^1Frenzy^7",
            Country = "United Kingdom",
            IpAddress = "203.0.113.42",
            Tags = "Veteran, Donator",
            PlayerGuid = "110000112345abc",
            SteamId = "76561198000000000",
            PlayerCount = "12"
        };

        var result = _sut.Render(
            "{name} from {country} ({ipaddress}) [{tags}] {guid} {steamid} {playercount}",
            values);

        Assert.Equal(
            "^1Frenzy^7 from United Kingdom (203.0.113.42) [Veteran, Donator] 110000112345abc 76561198000000000 12",
            result);
    }

    [Fact]
    public void Render_IsCaseInsensitive()
    {
        var values = new WelcomeMessageTokenValues { Name = "Bob", Country = "GB" };

        Assert.Equal("Bob GB", _sut.Render("{NAME} {Country}", values));
    }

    [Fact]
    public void Render_LeavesUnknownTokensIntact()
    {
        var values = new WelcomeMessageTokenValues { Name = "Bob" };

        Assert.Equal("Bob {mystery}", _sut.Render("{name} {mystery}", values));
    }

    [Fact]
    public void Render_DoesNotReScanTokenValues()
    {
        // A player named "{country}" must not be expanded into the country value.
        var values = new WelcomeMessageTokenValues { Name = "{country}", Country = "GB" };

        Assert.Equal("{country}", _sut.Render("{name}", values));
    }

    [Fact]
    public void Render_EmptyTemplate_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, _sut.Render(string.Empty, new WelcomeMessageTokenValues()));
    }

    [Fact]
    public void Render_MissingValues_RenderEmptyString()
    {
        var values = new WelcomeMessageTokenValues { Name = "Bob" };

        Assert.Equal("Bob||", _sut.Render("{name}|{steamid}|{tags}", values));
    }
}
