using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class ConnectedPlayerClaimRequestIdTests
{
    [Fact]
    public void FromEvent_UsesImmutableEventIdentityAcrossRetries()
    {
        var context = CreateContext();

        Assert.Equal(ConnectedPlayerClaimRequestId.FromEvent(context),
            ConnectedPlayerClaimRequestId.FromEvent(context with
            {
                Username = "Changed name",
                SlotId = 9,
                EventPublishedUtc = context.EventPublishedUtc.AddMinutes(1)
            }));
    }

    [Fact]
    public void FromEvent_DistinguishesServerSequenceAndAgentRestart()
    {
        var context = CreateContext();
        var requestId = ConnectedPlayerClaimRequestId.FromEvent(context);

        Assert.NotEqual(requestId, ConnectedPlayerClaimRequestId.FromEvent(context with { ServerId = Guid.NewGuid() }));
        Assert.NotEqual(requestId, ConnectedPlayerClaimRequestId.FromEvent(context with { SequenceId = 2 }));
        Assert.NotEqual(requestId, ConnectedPlayerClaimRequestId.FromEvent(context with
        {
            EventGeneratedUtc = context.EventGeneratedUtc.AddTicks(1)
        }));
        Assert.NotEqual(requestId, ConnectedPlayerClaimRequestId.FromEvent(context with { SourceMessageId = "other" }));
    }

    [Fact]
    public void FromEvent_RejectsMissingSourceMessageId()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ConnectedPlayerClaimRequestId.FromEvent(CreateContext() with { SourceMessageId = null }));
    }

    private static CommandContext CreateContext() => new()
    {
        ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        GameType = "CallOfDuty4",
        PlayerGuid = "player",
        Username = "Player",
        SlotId = 3,
        Message = "!register AB12CD",
        EventGeneratedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EventPublishedUtc = new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc),
        SequenceId = 1,
        SourceMessageId = "source-message"
    };
}
