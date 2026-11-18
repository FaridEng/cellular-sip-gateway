using System.Text.RegularExpressions;
using DWM157Gateway.Core.Configuration;
using System.Management;

namespace DWM157Gateway.Core.Modem;

public sealed class PnpComPortDiscovery
{
    private static readonly Regex ComRegex = new(@"\((COM\d+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Dwm157Ports Discover(ModemConfig config)
    {
        var ports = EnumeratePorts().ToList();
        var control = ResolvePort(ports, config.ControlFriendlyName, config.ControlPortOverride, "control");
        var service = ResolvePort(ports, config.ServiceFriendlyName, config.ServicePortOverride, "service");
        return new Dwm157Ports(control, service);
    }

    public IEnumerable<ComPortInfo> EnumeratePorts()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Ports'");

        foreach (ManagementObject device in searcher.Get())
        {
            var friendlyName = device["Name"] as string;
            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                continue;
            }

            var portName = ExtractPortName(friendlyName);
            if (!string.IsNullOrWhiteSpace(portName))
            {
                yield return new ComPortInfo(portName, friendlyName);
            }
        }
    }

    private static ComPortInfo ResolvePort(
        IReadOnlyCollection<ComPortInfo> ports,
        string friendlyName,
        string? overridePort,
        string role)
    {
        if (!string.IsNullOrWhiteSpace(overridePort))
        {
            return new ComPortInfo(overridePort.Trim(), $"{role} override");
        }

        var match = ports.FirstOrDefault(p => p.FriendlyName.Contains(friendlyName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new InvalidOperationException($"Could not find DWM-157 {role} port by FriendlyName '{friendlyName}'.");
        }

        return match;
    }

    private static string? ExtractPortName(string friendlyName)
    {
        var match = ComRegex.Match(friendlyName);
        return match.Success ? match.Groups[1].Value : null;
    }
}
