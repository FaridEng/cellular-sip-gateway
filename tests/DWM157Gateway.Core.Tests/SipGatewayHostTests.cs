using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DWM157Gateway.Core.Sip;

namespace DWM157Gateway.Core.Tests;

public sealed class SipGatewayHostTests
{
    [Fact]
    public async Task ChallengesRegisterAndHonorsExpiresHeader()
    {
        int port;
        using (var portProbe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)portProbe.Client.LocalEndPoint!).Port;
        }
        await using var host = new SipGatewayHost("127.0.0.1", port, "dwm157", "test-password", ["127.0.0.1", "127.0.0.2"]);
        host.Start();

        using var client = new UdpClient();
        client.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        client.Client.ReceiveTimeout = 3000;
        var request = string.Join("\r\n", [
            $"REGISTER sip:127.0.0.1:{port} SIP/2.0",
            "Via: SIP/2.0/UDP 127.0.0.1:5090;branch=z9hG4bKtest",
            "From: <sip:dwm157@127.0.0.1>;tag=test",
            "To: <sip:dwm157@127.0.0.1>",
            "Call-ID: register-test",
            "CSeq: 1 REGISTER",
            "Contact: <sip:dwm157@127.0.0.1:5090>",
            "Max-Forwards: 70",
            "Content-Length: 0",
            "",
            ""]);

        await client.SendAsync(Encoding.ASCII.GetBytes(request), request.Length, new IPEndPoint(IPAddress.Loopback, port));
        var response = await client.ReceiveAsync();

        var responseText = Encoding.ASCII.GetString(response.Buffer);
        Assert.Contains("SIP/2.0 401 Unauthorised", responseText);
        Assert.Contains("WWW-Authenticate", responseText);

        var nonce = Regex.Match(responseText, "nonce=\\\"(?<nonce>[^\\\"]+)\\\"").Groups["nonce"].Value;
        var realm = Regex.Match(responseText, "realm=\\\"(?<realm>[^\\\"]+)\\\"").Groups["realm"].Value;
        var uri = $"sip:127.0.0.1:{port}";
        var digest = string.Join(":", [
            Md5Hex("dwm157:" + realm + ":test-password"),
            nonce,
            Md5Hex("REGISTER:" + uri)]) ;

        var authenticated = string.Join("\r\n", [
            $"REGISTER {uri} SIP/2.0",
            "Via: SIP/2.0/UDP 127.0.0.1:5090;branch=z9hG4bKtest2",
            "From: <sip:dwm157@127.0.0.1>;tag=test",
            "To: <sip:dwm157@127.0.0.1>",
            "Call-ID: register-test",
            "CSeq: 2 REGISTER",
            "Contact: <sip:dwm157@127.0.0.1:5090>",
            "Expires: 3600",
            "Authorization: Digest username=\"dwm157\", realm=\"" + realm + "\", nonce=\"" + nonce + "\", uri=\"" + uri + "\", response=\"" + Md5Hex(digest) + "\"",
            "Max-Forwards: 70",
            "Content-Length: 0",
            "",
            ""]);

        await client.SendAsync(Encoding.ASCII.GetBytes(authenticated), authenticated.Length, new IPEndPoint(IPAddress.Loopback, port));
        var authenticatedResponse = await client.ReceiveAsync();
        var authenticatedResponseText = Encoding.ASCII.GetString(authenticatedResponse.Buffer);
        Assert.Contains("SIP/2.0 200 Ok", authenticatedResponseText);
        Assert.Contains("Expires: 3600", authenticatedResponseText);
        Assert.Matches("Contact:.*expires=3600", authenticatedResponseText);
        Assert.True(host.TryGetRegisteredContact(out var contact));
        Assert.Equal(IPAddress.Loopback, contact.PeerAddress);
        Assert.Equal(clientPort, contact.Port);
        Assert.True(contact.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(59));
        Assert.Single(host.GetRegisteredContacts());

        using var secondClient = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        var secondPort = ((IPEndPoint)secondClient.Client.LocalEndPoint!).Port;
        var secondRegistration = authenticated
            .Replace("branch=z9hG4bKtest2", "branch=z9hG4bKtest3", StringComparison.Ordinal)
            .Replace("Call-ID: register-test", "Call-ID: register-test-second", StringComparison.Ordinal)
            .Replace("CSeq: 2 REGISTER", "CSeq: 3 REGISTER", StringComparison.Ordinal);
        await secondClient.SendAsync(Encoding.ASCII.GetBytes(secondRegistration), new IPEndPoint(IPAddress.Loopback, port));
        var secondResponse = await secondClient.ReceiveAsync();

        Assert.Contains("SIP/2.0 200 Ok", Encoding.ASCII.GetString(secondResponse.Buffer));
        Assert.Equal(2, host.GetRegisteredContacts().Count);
        Assert.Contains(host.GetRegisteredContacts(), registered => registered.Port == secondPort);
    }

    [Fact]
    public async Task RefusesToStartWithoutPeerAllowlist()
    {
        int port;
        using (var portProbe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)portProbe.Client.LocalEndPoint!).Port;
        }
        await using var host = new SipGatewayHost("127.0.0.1", port, "dwm157", "test-password", []);

        Assert.Throws<InvalidOperationException>(() => host.Start());
    }

    [Fact]
    public async Task RemembersAuthenticatedInvitePeerForIncomingRouting()
    {
        await using var host = new SipGatewayHost(
            "127.0.0.1",
            5060,
            "dwm157",
            "test-password",
            ["127.0.0.1"],
            authenticatedContactLifetimeSeconds: 3600);

        var remembered = host.RememberAuthenticatedPeer(new IPEndPoint(IPAddress.Loopback, 54321));
        var contacts = host.GetRegisteredContacts();

        Assert.Single(contacts);
        Assert.Equal(remembered, contacts[0]);
        Assert.Equal("sip:dwm157@127.0.0.1:54321", contacts[0].DestinationUri);
        Assert.True(contacts[0].ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(59));
    }

    [Fact]
    public async Task LatestAuthenticatedPortReplacesStalePortForSamePeer()
    {
        await using var host = new SipGatewayHost(
            "127.0.0.1",
            5060,
            "dwm157",
            "test-password",
            ["127.0.0.1"]);

        host.RememberAuthenticatedPeer(new IPEndPoint(IPAddress.Loopback, 50000));
        host.RememberAuthenticatedPeer(new IPEndPoint(IPAddress.Loopback, 50001));

        var contact = Assert.Single(host.GetRegisteredContacts());
        Assert.Equal(50001, contact.Port);
    }

    [Fact]
    public async Task RestoresUnexpiredAuthenticatedContactAfterGatewayRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dwm157-sip-contact-{Guid.NewGuid():N}");
        var cachePath = Path.Combine(directory, "contacts.json");
        try
        {
            await using (var first = new SipGatewayHost(
                "127.0.0.1",
                5060,
                "dwm157",
                "test-password",
                ["127.0.0.1"],
                3600,
                cachePath))
            {
                first.RememberAuthenticatedPeer(new IPEndPoint(IPAddress.Loopback, 50123));
            }

            int port;
            using (var portProbe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                port = ((IPEndPoint)portProbe.Client.LocalEndPoint!).Port;
            }

            await using var restored = new SipGatewayHost(
                "127.0.0.1",
                port,
                "dwm157",
                "test-password",
                ["127.0.0.1"],
                3600,
                cachePath);
            restored.Start();

            var contact = Assert.Single(restored.GetRegisteredContacts());
            Assert.Equal(50123, contact.Port);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task DoesNotRespondToAck()
    {
        int port;
        using (var portProbe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)portProbe.Client.LocalEndPoint!).Port;
        }

        await using var host = new SipGatewayHost("127.0.0.1", port, "dwm157", "test-password", ["127.0.0.1"]);
        host.Start();

        using var client = new UdpClient();
        var request = string.Join("\r\n", [
            $"ACK sip:dwm157@127.0.0.1:{port} SIP/2.0",
            "Via: SIP/2.0/UDP 127.0.0.1:5090;branch=z9hG4bKack",
            "From: <sip:dwm157@127.0.0.1>;tag=test",
            "To: <sip:123@127.0.0.1>;tag=remote",
            "Call-ID: ack-test",
            "CSeq: 1 ACK",
            "Max-Forwards: 70",
            "Content-Length: 0",
            "",
            ""]);

        await client.SendAsync(Encoding.ASCII.GetBytes(request), new IPEndPoint(IPAddress.Loopback, port));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.ReceiveAsync(timeout.Token));
    }

    private static string Md5Hex(string value)
    {
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
