# Monitoring Testing

## Local

1. Start the monitoring host on loopback.
2. Set `DWM157_MONITOR_TOKEN` to a non-empty development token.
3. Request `/api/v1/summary` with `X-Monitor-Token`.
4. Request `/api/v1/calls?limit=10` and verify newest-first ordering, the ten-item
   cap, compact media metrics, and the absence of credentials, DTMF, and raw audio.
5. Confirm requests without the token return HTTP 401.
6. Confirm a missing snapshot is represented as unknown and does not fail the
   host.
7. On a non-loopback bind, confirm a client outside
   `Monitoring.AllowedRemoteAddresses` receives HTTP 403 even with a valid token.
8. Request history with no query parameters and with each supported range; verify
   the default is `24h`, points are bounded, and the SQLite retention database is created.
9. Verify CSV export and diagnostics contain no token, credential, DTMF, or raw audio.

## Android

1. Start in Demo mode and inspect every status card.
2. Configure the Gateway PC Tailscale URL and monitoring token.
3. Confirm a valid response switches the banner to Live.
4. Open Call History and confirm no more than ten newest-first calls are shown.
5. Open Charts and verify every range renders offline gaps without joining them.
6. Disable Tailscale and confirm all old values and call records are removed
   immediately while Offline and last-success information remain explicit.
7. Confirm rotation and app restart do not reveal the token in UI or logs.

## Deployment gate

Deployment requires an explicit Gateway PC bind address, a generated token, an
application-level peer allowlist, and a peer-restricted Firewall rule. No
deployment step may change SIP/RTP rules.
