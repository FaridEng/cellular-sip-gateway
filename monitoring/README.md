# DWM157 Monitoring

This directory owns the versioned contract shared by the Gateway PC monitoring
host and the Android monitor. Monitoring is intentionally separate from the
SIP/RTP/PCM call path.

Active components (verified 2026-10-28):

- `contracts/openapi.yaml`: HTTP API contract.
- `samples/summary.json`: deterministic Android demo payload.
- `src/DWM157Gateway.Monitoring`: read-only Gateway PC HTTP host.
- `android/DWM157Monitor`: Android dashboard intended for the Galaxy S25 Ultra.
- `src/DWM157Gateway.Observer`: independent NUC health observer and FCM sender.

Gateway PC runs Monitoring 0.5.0 through its existing scheduled host task; NUC runs
Observer 0.5.0 as a SYSTEM Boot task. Both use authenticated, peer-restricted
Tailscale listeners. Android 0.6.0 is installed on S25. Real screen-off Push
and controlled Monitoring outage/recovery notifications are verified.
See `docs/monitoring/SECURITY.md`, `docs/runbooks/monitoring-alerts.md` (not included) and
`docs/releases/android-monitor-0.6.0.md` for deployment and acceptance limits.
