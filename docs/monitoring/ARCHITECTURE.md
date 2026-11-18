# Monitoring Architecture

## Scope

Android 0.6 remains a read-only Gateway dashboard for the Galaxy S25 Ultra. It observes
Gateway PC without participating in SIP signaling, RTP, modem PCM, dialing, or
hangup.

## Components

1. `DWM157Gateway.Monitoring` runs as a separate process on Gateway PC.
   Its Scheduled Task starts 60 seconds after Windows startup so Tailscale and
   network interfaces can initialize first.
2. The gateway atomically publishes `data/monitoring/gateway-status.json` every
   five seconds. The monitoring host only reads that snapshot.
3. The monitoring host adds passive Windows and network-interface status.
4. The Monitoring host stores one bounded telemetry sample per minute in a
   30-day SQLite history database and exposes downsampled range queries.
5. `DWM157Monitor` polls `/api/v1/summary` while visible, shows a five-minute
   live chart, and renders persistent range charts from the host.

## Failure isolation

- Monitoring has no reference to `DWM157Gateway.Core`.
- Monitoring cannot dial, answer, hang up, restart, or edit configuration.

## Network quality

- The Android app times every visible-app request from Galaxy S25 Ultra to Gateway PC
  and keeps at most 60 five-second samples (about five minutes) for current RTT,
  average RTT, mean absolute RTT variation, and request failure rate.
- Gateway PC probes its Internet TCP reachability and reports the active and previous
  default routes, including the last route-change time.
- A bounded `tailscale ping` reports whether the current peer path is direct or
  relayed through a DERP region. This is path diagnostics, while the Android HTTP
  timer is the actual S25-to-Gateway PC measurement.
- Galaxy A07 is reported with USB/ADB presence and permitted device health
  fields. Because it is
  physically attached to Gateway PC by USB, it is not treated as a separate network-
  quality path.
- Monitoring reads Gateway `call-history.json` through a bounded, shared-read
  parser and exposes at most ten recent records plus compact media metrics. It does not reference or modify
  the Gateway call runtime.
- Android clears every live card after a polling failure so stale values cannot
  be mistaken for current status. Only the explicit Offline state, error, and
  time of the last successful update remain visible.
- A missing or malformed gateway snapshot produces an `unknown` status rather
  than changing gateway behavior.
- The active Internet test is bounded and only runs when explicitly requested.

## Deferred extensions

Version 0.5 adds cached local Tailscale installed/daemon versions and a separate
`DWM157Gateway.Observer` host intended for NUC. The observer debounces outages,
persists incident/recovery events and can send FCM data messages independently
of the Gateway host. Android exposes observer setup, runtime notification
permission, version warnings and separate NUC/S25 viewpoints. Firebase and
persistent observer activation are configured; screen-off delivery and a
controlled Monitoring outage/recovery pair were verified on 2026-10-28.
Android 0.6 adds shared light/dark styling and Pacific Time display formatting
without changing API/storage timestamps or the call path. Exact deployed state
and rollout boundaries are in `docs/runbooks/monitoring-alerts.md` (not included).

- Remaining Push lifecycle scenarios, including device reboot and token rotation.
- Android home-screen widget and multiple gateway profiles.
- Streaming updates after polling behavior is validated.
- Any control operation; the Monitoring boundary remains read-only.
