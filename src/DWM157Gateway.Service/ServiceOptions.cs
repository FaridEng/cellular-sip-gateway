namespace DWM157Gateway.Service;

public sealed record ServiceOptions(string ConfigPath, string LogPath)
{
    public static ServiceOptions Parse(string[] args)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var configPath = GetOption(args, "--config")
            ?? Path.Combine(baseDirectory, "config.json");
        var logPath = GetOption(args, "--log")
            ?? Path.Combine(baseDirectory, "gateway-service.log");
        return new ServiceOptions(Path.GetFullPath(configPath), Path.GetFullPath(logPath));
    }

    private static string? GetOption(string[] args, string option)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
