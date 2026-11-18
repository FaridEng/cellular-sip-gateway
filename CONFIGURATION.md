# Configuration

## Android display time

Monitor UI 0.6.0 displays all human-facing timestamps in
`America/Los_Angeles` (Pacific Time with PST/PDT resolved per event). This is
independent of phone/Windows timezone preferences. API/storage and exported
diagnostic JSON retain their original timestamps. No server configuration or
token change is needed; see `docs/runbooks/android-monitor-ui.md` (not included).

## Independent monitoring observer

On 2026-10-29 the production NUC Observer's `TailscaleApiCredentialPath` was
activated with a protected OAuth file outside Git and exactly
`devices:core:read`. Laptop running-version inventory now refreshes via the
Tailnet API every 30 minutes. No local Laptop Reporter is required for this
source; an installed binary version may remain unknown. See
`docs/runbooks/tailscale-version-inventory.md` (not included) for verified state and rollback.

Monitoring 0.5 adds `tailscaleVersion` to summary responses. It reads the
installed CLI and active daemon locally and does not contact update websites
from Gateway PC. The separate NUC observer uses `monitoring/observer.example.json`
as a template for private `observer.json`; its observer API token, Gateway API
token, reporter token and optional cloud credentials are distinct roles.
Do not commit production configuration or service accounts. The complete
configuration/activation and current pending steps are documented in
`docs/runbooks/monitoring-alerts.md` (not included).

`config.json` is created at runtime and must not be committed. Use `config.example.json` as the template.

Important safe defaults:

- Real SIP password is generated only in runtime `config.json`.
- `DialPolicy.AllowUnlistedNumbers` defaults to `false`; set it to `true` only for an explicitly approved unrestricted runtime.
- The current MicroSIP source Tailscale IPv4 is `100.64.0.16`; keep it in `AllowedTailscaleIPs` on the destination gateway.
- Emergency numbers are blocked by default.
- COM ports are discovered by PnP FriendlyName. Overrides exist only for diagnostics.
- `AutoCloseConnectionManager` defaults to `false`.
- The RTP-to-modem adaptive jitter buffer defaults to a `120ms` target and `300ms` maximum.
- The modem-to-RTP serial smoothing buffer defaults to a `300ms` target and `600ms` maximum.
- When either audio queue reaches its maximum, stale frames are trimmed back to the current adaptive target instead of preserving a permanent latency backlog.

MicroSIP Phase 1 defaults:

- SIP server: destination Tailscale address, default `100.64.0.12`
- SIP port: UDP `5060`
- RTP: UDP `40000-40020`
- Username: configured `Sip.Username`
- Password: configured runtime `Sip.Password`
- Preferred codec: forced Opus at `16 kbit/s`, 20 ms packets, VBR enabled, complexity 10, in-band FEC enabled, expected packet loss 15%, and DTX disabled. PCMU and PCMA remain configured as fallbacks for a non-forced profile.

## Destination launch

1. On the destination, close **D-Link Connection Manager** before starting the gateway. It must not own the modem ports while the gateway is active.
2. Run `C:\DWM157Gateway\release-20260816\DWM157Gateway.WinForms.exe` from the destination desktop or an RDP session.
3. Click **Start Gateway** and confirm the log reports the discovered Control and Service ports, modem preflight success, and `SIP listening`.
4. The first start replaces the placeholder password in `config.json` with a random runtime password. Read it only on the destination with:

```powershell
(Get-Content C:\DWM157Gateway\release-20260816\config.json | ConvertFrom-Json).Sip.Password
```

Do not copy the runtime `config.json` into source control or paste the password into chat.

## Phase 2 Windows Service

- Service name: `DWM157Gateway`
- Release: `C:\DWM157Gateway\releases\0.2.11`
- Runtime config: `C:\DWM157Gateway\data\config.json`
- Runtime log: `C:\DWM157Gateway\logs\gateway-service.log`
- Startup: delayed automatic

Incoming-call settings:

```json
"incomingCalls": {
  "enabled": true,
  "pollIntervalMilliseconds": 1500,
  "callerIdWaitMilliseconds": 1000,
  "sipRingTimeoutSeconds": 35,
  "cellularAnswerTimeoutSeconds": 15
}
```

`IncomingCalls.CallerIdWaitMilliseconds` is the maximum time to wait for a delayed modem `+CLIP` indication before sending SIP `INVITE` branches. The wait ends immediately when caller ID is already available and does not affect connected-call media latency.

The Service and WinForms host must never run simultaneously because both require exclusive access to the modem COM ports and SIP endpoint.

The approved SIP/RTP Tailscale peers are the Windows source `100.64.0.16`, primary Android Linphone source `100.64.0.14`, and iPhone sources `100.64.0.18` and `100.64.0.11`. Multiple registrations are retained by peer IP; incoming calls ring all current registrations and use first-answer-wins media selection.

Release `0.2.10` honors a Contact-specific `expires` parameter when present, otherwise the global SIP `Expires` header, and otherwise a bounded `300` second default. The accepted expiry is returned in both the `200 OK` Contact and `Expires` header. This matches Linphone's one-hour registration with an approximately 54-minute refresh cadence and prevents Android from disappearing from incoming routing after five minutes.

`Sip.AuthenticatedPeerContactLifetimeSeconds` controls how long an authenticated SIP request source remains usable as an incoming fallback Contact. The default is `86400` seconds. This supports Linphone clients that can send authenticated outgoing `INVITE` requests but do not reliably refresh `REGISTER`.

`Sip.ContactCacheFile` defaults to `sip-contacts.json` beside the runtime config. The cache preserves only allowlisted peer IP, source port, and expiry so incoming routing survives a Gateway restart; it contains no credentials.

`IncomingCalls.SipContactWaitSeconds` controls how long an incoming cellular call remains ringing while the Gateway waits for a SIP client to become routable. The default is `20` seconds. Incoming caller numbers are never checked against the outgoing dial allowlist.

Structured call history defaults to `C:\DWM157Gateway\data\call-history.json` with `Logging.MaxCallHistoryRecords=500`. Read the latest records on Gateway PC with:

```powershell
powershell -ExecutionPolicy Bypass -File C:\DWM157Gateway\get-recent-calls.ps1 -Count 10
```

Dial authorization settings:

```json
"dialPolicy": {
  "allowUnlistedNumbers": false,
  "testNumber": "123",
  "allowedDialNumbers": [ "123" ],
  "blockedNumbers": [ "911", "112", "999", "000" ]
}
```

When `AllowUnlistedNumbers` is `true`, any non-empty normalized dial target is allowed unless it appears in `BlockedNumbers`. Emergency and explicitly blocked numbers are checked first and remain blocked. The active mode is reported at startup as `Dial policy: allow-unlisted=...`.

## MicroSIP settings

- SIP server: `100.64.0.12`
- SIP port: `5060`
- Transport: UDP
- Username: `dwm157`
- Password: the runtime `Sip.Password` from the destination config
- Audio codecs: Opus first, PCMU second, PCMA third
- Disable STUN/ICE for this Tailscale-only test
- DTMF method: `RTP Events` / `RFC2833` / `telephone-event`

DTMF forwarding:

- The gateway accepts completed SIP `telephone-event` tones from MicroSIP and serializes them to the DWM-157 Control Port with `AT+VTS`.
- The modem startup preflight checks `AT+VTS=?`; a healthy destination reports `DTMF=True`.
- The gateway supports `0-9`, `*`, `#`, and `A-D`, rejects other event IDs, and only sends tones while the cellular call is connected.
- DTMF digits are deliberately omitted from runtime logs because they can contain PINs or account data.
- DTMF uses the Control Port and does not enter the PCM, codec, RTP pacing, or audio buffer paths.

Audio buffering:

- `Audio.ModemToRtpBuffer`: smooths bursty Service Port delivery before RTP encoding.
- `Audio.JitterBuffer`: buffers RTP received from MicroSIP before writing to the modem.
- Increasing gateway buffers does not repair RTP packets lost after the gateway sends them. That direction is protected by Opus FEC and the softphone's receive jitter buffer.

Audio level:

- `Audio.ModemToRtpGainDb`: fixed gain applied in place to each modem PCM frame immediately before encoding. The default is 6 dB. The active Gateway PC trial uses 9 dB as of 2026-11-05. See `docs/runbooks/gateway-service-restart.md` (not included) for verification and rollback.
- The gain stage does not add buffering, resampling, packetization, or network delay.
- End-of-call telemetry reports active input/output RMS, input/output peak, and clipped sample count. Reduce the gain if clipping is observed.

Opus tuning:

- `Sip.PreferredCodec`: first local codec, normally `OPUS`.
- `Sip.EnabledCodecs`: codecs the gateway may negotiate, in preferred fallback order.
- `Sip.ForcePreferredCodec`: when `true`, advertise only the preferred codec. If it is absent from the incoming SDP offer, reject the SIP call before dialing the cellular number.
- `Sip.OpusBitrateBps`: outgoing Opus target bitrate; start with `16000`.
- `Sip.OpusFec`: includes recovery data for a previous packet in the following packet.
- `Sip.OpusExpectedPacketLossPercent`: tunes FEC allocation; start with `10` and adjust from measured RTCP loss.
- `Sip.OpusDtx`: keep `false` for continuous telephony audio and predictable packet timing.
- `Sip.OpusComplexity`: encoder quality/CPU tradeoff from `0` to `10`.
- `Sip.OpusVbr`: variable bitrate; normally `true`.

To return to the previous G.711 path, set:

```json
"preferredCodec": "PCMU",
"enabledCodecs": [ "PCMU", "PCMA" ]
```

Restart the gateway and put PCMU first in MicroSIP after changing codec settings.

RTP pacing:

- `Audio.Pacing.PreventBurstCatchUp`: prevents overdue frames from being emitted back-to-back.
- `Audio.Pacing.MinimumPacketSpacingMilliseconds`: minimum spacing while recovering from scheduler delay; currently `15` for 20 ms frames. Missed deadlines are skipped on the original media timeline instead of slowing the clock.
- `Audio.Pacing.PrecisionWaitMilliseconds`: final high-precision wait window; currently `2` ms.
- `Audio.Pacing.RequestOneMillisecondTimerResolution`: requests 1 ms Windows timer resolution while an audio bridge is active.
- `Audio.Pacing.UseHighResolutionWaitableTimer`: uses a dedicated Windows high-resolution waitable timer for media deadlines; disable it to use the `Task.Delay` fallback.
- `Audio.Pacing.GapWarningMilliseconds` and `BurstWarningMilliseconds`: thresholds used by per-call pacing telemetry.
- Set `PreventBurstCatchUp` to `false` to restore the legacy bounded catch-up behavior.

Release `0.2.3` and later also record the negotiated SDP packetization/ICE summary, encoded RTP payload sizes and bitrate, and packet arrival/send gaps. The SDP summary intentionally omits ICE credentials, candidate addresses, and raw SDP.

The source MicroSIP device must use Tailscale IPv4 `100.64.0.16`. Only that peer is allowed by the gateway and firewall rules.
