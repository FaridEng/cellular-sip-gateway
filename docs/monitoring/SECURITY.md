# Monitoring Security

- Bind the production host only to the Gateway PC Tailscale address.
- Never bind it to `0.0.0.0` or expose it to the public Internet.
- Configure a strong independent monitoring token outside the repository.
- Restrict any future Firewall rule to approved Tailscale peers.
- Configure `Monitoring.AllowedRemoteAddresses` with the same approved peers so
  the host enforces the allowlist even when a Windows Firewall profile is disabled.
- Never reuse or return SIP credentials.
- Never return authorization headers, DTMF digits, raw audio, or unmasked
  destination numbers.
- Plain HTTP is acceptable only inside the encrypted tailnet for the initial
  private deployment. Application authentication remains mandatory.

The checked-in configuration binds to loopback. A non-loopback bind refuses to
start when no token is configured.
