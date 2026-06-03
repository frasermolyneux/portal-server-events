using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class ChatCommandParserTests
{
    private readonly ChatCommandParser _sut = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello there")]
    public void Parse_WhenNotACommand_ReturnsNotACommand(string? message)
    {
        var result = _sut.Parse(message);

        Assert.False(result.IsCommand);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_WhenValidCommand_ParsesNormalizedEnvelope()
    {
        var result = _sut.Parse("   !register  ABC123   ");

        Assert.True(result.IsCommand);
        Assert.NotNull(result.Command);
        Assert.Equal("!register", result.Command.PrefixToken);
        Assert.Equal("register", result.Command.Verb);
        Assert.Equal(["ABC123"], result.Command.Arguments);
        Assert.Equal("ABC123", result.Command.ArgumentText);
    }

    [Fact]
    public void Parse_WhenQuotedArguments_PreservesGroupedTokens()
    {
        var result = _sut.Parse("!fu \"John Doe\" please");

        Assert.True(result.IsCommand);
        Assert.NotNull(result.Command);
        Assert.Equal(["John Doe", "please"], result.Command.Arguments);
        Assert.Equal("\"John Doe\" please", result.Command.ArgumentText);
    }

    [Fact]
    public void Parse_WhenQuotesUnbalanced_ReturnsNotACommand()
    {
        var result = _sut.Parse("!fu \"John Doe");

        Assert.False(result.IsCommand);
        Assert.Null(result.Command);
        Assert.Equal("Command has unbalanced quotes", result.Reason);
    }
}
