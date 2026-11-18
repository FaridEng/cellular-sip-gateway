using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DWM157Gateway.Observer;
using Xunit;

namespace DWM157Gateway.Observer.Tests;

public sealed class FcmTests
{
    [Fact]
    public async Task SignedOAuthRequestAndFcmDataPayloadUseSameStableEventId()
    {
        using var rsa = RSA.Create(2048);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            { project_id = "test-project", client_email = "test@test-project.iam.gserviceaccount.com", private_key = rsa.ExportPkcs8PrivateKeyPem() }));
            var handler = new FcmHandler(rsa);
            using var client = new HttpClient(handler);
            var sender = new FcmPushSender(new() { FirebaseProjectId = "test-project", FirebaseServiceAccountPath = path }, client);
            var alert = new AlertEvent("stable-event", 12, "outage", "incident", "unreachable", "Gateway unavailable", "Observer cannot reach API", DateTimeOffset.UtcNow);
            Assert.True(await sender.SendAsync(alert, "fake-test-registration", CancellationToken.None));
            Assert.True(await sender.SendAsync(alert, "fake-test-registration", CancellationToken.None));
            Assert.Equal(1, handler.AuthRequests);
            Assert.Equal(2, handler.SendRequests);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingFirebaseConfigurationCannotClaimPushSuccess()
    {
        using var client = new HttpClient();
        var sender = new FcmPushSender(new(), client);
        Assert.False(sender.Configured);
        Assert.False(await sender.SendAsync(new("event", 1, "outage", "incident", "unreachable", "title", "body", DateTimeOffset.UtcNow), "fake", CancellationToken.None));
    }

    private sealed class FcmHandler(RSA rsa) : HttpMessageHandler
    {
        public int AuthRequests, SendRequests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                AuthRequests++;
                var encoded = await request.Content!.ReadAsStringAsync(cancellationToken);
                var assertion = Uri.UnescapeDataString(encoded.Split('&').Single(p => p.StartsWith("assertion=")).Substring(10));
                var parts = assertion.Split('.');
                static byte[] Decode(string value) { var normalized = value.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '=')); }
                Assert.True(rsa.VerifyData(System.Text.Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                using var payload = JsonDocument.Parse(Decode(parts[1]));
                Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", payload.RootElement.GetProperty("scope").GetString());
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"test-access-token\",\"expires_in\":3600}") };
            }
            Assert.Equal("fcm.googleapis.com", request.RequestUri.Host);
            SendRequests++;
            Assert.Equal("test-access-token", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var message = body.RootElement.GetProperty("message");
            Assert.Equal("stable-event", message.GetProperty("data").GetProperty("eventId").GetString());
            Assert.Equal("high", message.GetProperty("android").GetProperty("priority").GetString());
            Assert.False(message.TryGetProperty("notification", out _)); // data-only: client deduplicates before notifying
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"name\":\"test-message\"}") };
        }
    }
}
