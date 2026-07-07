using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Services;

public class IpAddressGuardTests
{
    [Theory]
    [InlineData("192.168.1.100")]
    [InlineData("8.8.8.8")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData(" 10.0.0.1 ")]
    public void IsPersistable_ReturnsTrue_ForRealAddresses(string ipAddress)
    {
        Assert.True(IpAddressGuard.IsPersistable(ipAddress));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::ffff:0.0.0.0")]
    public void IsPersistable_ReturnsFalse_ForPlaceholdersAndInvalid(string? ipAddress)
    {
        Assert.False(IpAddressGuard.IsPersistable(ipAddress));
    }
}
