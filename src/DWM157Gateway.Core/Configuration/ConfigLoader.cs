using System.Security.Cryptography;
using System.Text.Json;

namespace DWM157Gateway.Core.Configuration;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static GatewayConfig LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            var existing = JsonSerializer.Deserialize<GatewayConfig>(json, JsonOptions)
                ?? throw new InvalidOperationException($"Configuration file '{path}' is empty or invalid.");

            if (string.IsNullOrWhiteSpace(existing.Sip.Password)
                || existing.Sip.Password.StartsWith("GENERATE_ON_DESTINATION", StringComparison.OrdinalIgnoreCase))
            {
                existing.Sip.Password = GeneratePassword();
                File.WriteAllText(path, JsonSerializer.Serialize(existing, JsonOptions));
            }

            return existing;
        }

        var config = new GatewayConfig();
        config.Sip.Password = GeneratePassword();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        return config;
    }

    private static string GeneratePassword()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }
}
