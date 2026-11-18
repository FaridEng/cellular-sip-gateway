# Monitoring API

The authoritative contract is `monitoring/contracts/openapi.yaml`.

The initial base path is `/api/v1`. Every non-loopback deployment requires the
`X-Monitor-Token` header. A token may be omitted only for loopback development.
JSON uses camelCase names and UTC timestamps.

The host reads an optional gateway snapshot. A snapshot older than the
configured stale threshold is reported as stale. Missing fields remain unknown;
the host does not invent modem, SIP, or call state.

The `network.quality` object reports the bounded Tailscale peer probe, Gateway PC
Internet TCP RTT, current and previous route, route-change timestamp, and Galaxy
A07 USB/ADB state, battery, charging, temperature, model, Android version,
storage, and memory when ADB permits collection. The Android client independently measures the actual
S25-to-Gateway PC HTTP request RTT and calculates its rolling statistics locally.

`GET /api/v1/calls?limit=10` returns at most the ten newest persistent Gateway
call records. Each record contains direction, remote number when available, the
winning or originating SIP peer, status, timestamps, connected duration, end
reason, codec, and compact RTP loss/jitter/RTT/drop counters. It never returns
credentials, authorization data, DTMF, or raw audio. The endpoint uses the same token
and remote-peer allowlist as every other Monitoring endpoint.

`GET /api/v1/history` returns downsampled SQLite-backed telemetry for `1h`,
`24h`, `7d`, or `30d`. `GET /api/v1/history/export` returns CSV. `GET
/api/v1/diagnostics` returns a sanitized diagnostic bundle containing the
current summary, ten recent calls, and one hour of history.
