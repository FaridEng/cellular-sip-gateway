namespace DWM157Gateway.Core.Modem;

public sealed record ComPortInfo(string PortName, string FriendlyName);

public sealed record Dwm157Ports(ComPortInfo ControlPort, ComPortInfo ServicePort);
