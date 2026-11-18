using DWM157Gateway.Core.Sip;

namespace DWM157Gateway.Core.Tests;

public sealed class IncomingCallerIdentityTests
{
    [Theory]
    [InlineData("0555 000-0000", "05550000000")]
    [InlineData("+1 555 000 0000", "+15550000000")]
    public void CallerIdentityKeepsAValidSipTelephoneUsername(string callerId, string expected)
    {
        var identity = IncomingCellularSipCallCoordinator.CreateCallerIdentity(callerId);

        Assert.True(identity.IsAvailable);
        Assert.Equal(expected, identity.DisplayName);
        Assert.Equal(expected, identity.Username);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void MissingCallerIdUsesAnonymousIdentity(string? callerId)
    {
        var identity = IncomingCellularSipCallCoordinator.CreateCallerIdentity(callerId);

        Assert.False(identity.IsAvailable);
        Assert.Equal("Private caller", identity.DisplayName);
        Assert.Equal("anonymous", identity.Username);
    }
}
