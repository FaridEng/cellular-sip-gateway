using DWM157Gateway.Monitoring;

namespace DWM157Gateway.Monitoring.Tests;

public sealed class MonitoringOptionsTests
{
    [Fact]
    public void LoopbackMayRunWithoutTokenForLocalDevelopment()
    {
        var options = new MonitoringOptions { BindAddress = "127.0.0.1", ApiToken = string.Empty };

        var address = options.GetValidatedBindAddress();

        Assert.True(System.Net.IPAddress.IsLoopback(address));
    }

    [Fact]
    public void RemoteBindRequiresToken()
    {
        var options = new MonitoringOptions { BindAddress = "100.64.0.12", ApiToken = string.Empty };

        Assert.Throws<InvalidOperationException>(() => options.GetValidatedBindAddress());
    }

    [Fact]
    public void RemoteBindRequiresAnAllowedPeer()
    {
        var options = new MonitoringOptions
        {
            BindAddress = "100.64.0.12",
            ApiToken = "test-token"
        };

        Assert.Throws<InvalidOperationException>(() => options.GetValidatedBindAddress());
    }

    [Fact]
    public void PeerAllowlistHandlesIpv4MappedAddresses()
    {
        var options = new MonitoringOptions
        {
            BindAddress = "100.64.0.12",
            AllowedRemoteAddresses = ["100.64.0.14"]
        };

        Assert.True(options.IsRemoteAddressAllowed(System.Net.IPAddress.Parse("::ffff:100.64.0.14")));
        Assert.False(options.IsRemoteAddressAllowed(System.Net.IPAddress.Parse("100.64.0.13")));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void WildcardBindIsRejected(string address)
    {
        var options = new MonitoringOptions
        {
            BindAddress = address,
            ApiToken = "test-token",
            AllowedRemoteAddresses = ["100.64.0.14"]
        };

        Assert.Throws<InvalidOperationException>(() => options.GetValidatedBindAddress());
    }
}
