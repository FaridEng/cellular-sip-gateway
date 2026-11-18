using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DWM157Gateway.Observer;

public sealed class FcmPushSender(ObserverOptions options, HttpClient client)
{
    private string? _accessToken;
    private DateTimeOffset _expires;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    public bool Configured => !string.IsNullOrWhiteSpace(options.FirebaseProjectId) && File.Exists(options.FirebaseServiceAccountPath);

    public async Task<bool> SendAsync(AlertEvent alert, string registrationToken, CancellationToken cancellationToken)
    {
        if (!Configured) return false;
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is null || DateTimeOffset.UtcNow >= _expires.AddMinutes(-5))
                await AuthenticateAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://fcm.googleapis.com/v1/projects/" + Uri.EscapeDataString(options.FirebaseProjectId) + "/messages:send");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            request.Content = JsonContent.Create(new
            {
                message = new
                {
                    token = registrationToken,
                    data = new Dictionary<string, string>
                    {
                        ["eventId"] = alert.Id, ["sequence"] = alert.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["kind"] = alert.Kind, ["incidentId"] = alert.IncidentId, ["code"] = alert.Code,
                        ["title"] = alert.Title, ["message"] = alert.Message,
                        ["timestampUtc"] = alert.TimestampUtc.ToString("O"), ["observer"] = options.Name,
                        ["observerId"] = string.IsNullOrEmpty(alert.SourceId) ? options.Name : alert.SourceId
                    },
                    android = new { priority = "high", ttl = "300s", collapse_key = alert.Kind == "test" ? "gateway-test" : "gateway-health" }
                }
            });
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) _accessToken = null;
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or
                                          CryptographicException or OperationCanceledException or KeyNotFoundException or ArgumentException or InvalidOperationException)
        { return false; } // never log provider bodies, registration tokens or private keys
        finally { _sendGate.Release(); }
    }

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(options.FirebaseServiceAccountPath, cancellationToken));
        var root = document.RootElement;
        if (root.GetProperty("project_id").GetString() != options.FirebaseProjectId)
            throw new IOException("Service account project does not match the configured project");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = root.GetProperty("client_email").GetString(),
            scope = "https://www.googleapis.com/auth/firebase.messaging",
            aud = "https://oauth2.googleapis.com/token", iat = now, exp = now + 3600
        }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(root.GetProperty("private_key").GetString());
        var unsigned = header + "." + payload;
        var signature = B64(rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var response = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = unsigned + "." + signature
        }), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        _accessToken = result.RootElement.GetProperty("access_token").GetString();
        _expires = DateTimeOffset.UtcNow.AddSeconds(result.RootElement.GetProperty("expires_in").GetInt32());
    }
}
