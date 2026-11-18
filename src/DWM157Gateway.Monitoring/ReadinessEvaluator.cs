namespace DWM157Gateway.Monitoring;

public static class ReadinessEvaluator
{
    public static ReadinessStatus Evaluate(
        GatewayStatus gateway,
        ModemStatus modem,
        NetworkStatus network,
        SystemStatus system)
    {
        var score = 100;
        var reasons = new List<string>();
        Deduct(!gateway.Running, 45, "Gateway process is stopped");
        Deduct(!gateway.SnapshotAvailable || gateway.SnapshotStale, 15, "Gateway telemetry is unavailable or stale");
        Deduct(gateway.SipListening != true, 15, "SIP listener is not ready");
        Deduct(modem.Detected != true, 35, "DWM-157 is not detected");
        Deduct(modem.SimReady != true, 30, "SIM is not ready");
        Deduct(modem.CellularRegistered != true, 30, "Cellular network is not registered");
        Deduct(!network.NetworkAvailable, 25, "Gateway PC network is unavailable");
        Deduct(!network.TailscaleAvailable, 20, "Tailscale is unavailable");
        Deduct(!network.Quality.S25ToGatewayPc.Success, 20, "Galaxy S25 Ultra cannot reach Gateway PC");
        Deduct(!network.Quality.Internet.Success, 20, "Gateway PC Internet probe failed");
        Deduct(string.Equals(network.Quality.S25ToGatewayPc.Path, "DERP", StringComparison.OrdinalIgnoreCase), 5,
            "Tailscale is using DERP relay");
        Deduct(system.SystemDriveFreeBytes is < 1073741824L, 10, "Gateway PC system drive has less than 1 GB free");

        score = Math.Clamp(score, 0, 100);
        var state = score >= 80 ? "Ready" : score >= 50 ? "Degraded" : "NotReady";
        return new(score, state, reasons);

        void Deduct(bool condition, int amount, string reason)
        {
            if (!condition) return;
            score -= amount;
            reasons.Add(reason);
        }
    }
}
