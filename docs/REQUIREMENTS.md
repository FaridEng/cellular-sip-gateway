# Phase 1 Requirements

## Scope

Implement only outgoing cellular calls initiated from MicroSIP, SIP registration/authentication, SIP signaling, RTP media, bidirectional audio, DTMF, WinForms status/control UI, diagnostics, recovery, deployment, and tests.

Do not implement incoming calls, Windows Service, autostart, SMS, Mobile Data, Asterisk, or FreePBX in Phase 1. Keep architecture extensible for later SMS/Mobile Data modules.

## SIP

- Direct SIP over Tailscale
- One MicroSIP client
- One simultaneous call
- SIP username/password authentication
- SIP over UDP
- RTP over UDP
- No TLS/SRTP in Phase 1
- Default SIP port: UDP 5060
- Default RTP range: UDP 40000-40020
- All ports configurable

Gateway should support the simple workflow where MicroSIP registers, user dials a telephone number, gateway validates/transforms it, and then sends the cellular AT dial command.

Generate strong random SIP credentials on first setup. Store real credentials only in destination `config.json`; never commit or log the password.

## Call State

1. MicroSIP sends outgoing SIP call.
2. Validate account and dial target.
3. Start cellular dialing.
4. Keep SIP caller in progress while GSM setup is pending.
5. Do not send final SIP connected state until modem reports cellular connection such as `^CONN: 1,0`.
6. Map Busy/No Answer/Failure sensibly where evidence permits.
7. Start/bridge media at the correct state.
8. Synchronize hangup in either direction.

MicroSIP BYE/CANCEL must clean cellular side. Modem `NO CARRIER`/`^CEND` must clean SIP side. GUI Hangup cleans both. Cleanup must be idempotent.

## Dial Policy

Default: pass number unchanged to `ATD<number>;`.

Provide configurable `DialPlan` for prefix add/remove/replace.

Provide `TestNumber` and `AllowedDialNumbers`. Automated real calls are permitted only to explicitly allowed numbers. Emergency numbers are blocked by default. Never invent a test number.

## Audio

No local destination microphone, speaker, Voicemeeter, or virtual audio device.

Required bridge:

`DWM-157 Service Port <-> PCM <-> codec/RTP <-> MicroSIP`

Initial modem PCM assumption:

- S16LE
- 8000 Hz
- mono
- 20 ms / 160 samples / 320 bytes

Continuously drain/read the voice port. Use bounded async queues, cancellation, controlled pacing, and no unbounded memory growth.

## Codec Strategy

Primary: Opus, mono VoIP, initial target roughly 12-16 kbps, 20 ms packetization, in-band FEC enabled, adaptive behavior where practical.

Fallbacks: PCMU then PCMA.

Use SDP negotiation and resample between modem 8 kHz PCM and codec media rate as required. Verify interoperability with the installed MicroSIP version.

Adaptive mode is enabled by default but bitrate/FEC/packetization are manually overridable.

## DTMF

Primary: SIP telephone-event -> modem DTMF command (`AT+VTS` path where validated).

Also support in-band DTMF as fallback where practical.

## Network Interruption

Configurable disruption timeout, default 10 seconds.

During temporary SIP/RTP/network loss, keep the cellular call alive during the grace period and attempt recovery. If connectivity does not recover before timeout, safely hang up the cellular call.

Use adaptive jitter buffering/FEC where appropriate.

## Tailscale Binding

Default bind only to destination Tailscale address/interface. Known destination address: `100.64.0.12`.

Provide `BindAddress` override. If Tailscale address cannot be determined, fail clearly instead of guessing another interface.

Automatically detect source Tailscale IP when possible; allow override. Maintain `AllowedTailscaleIPs`. Default intent is only the source client.

## Firewall

Codex may create/update Windows Firewall rules for project needs. SIP/RTP rules must be limited to approved Tailscale interfaces/peers and `AllowedTailscaleIPs`. Never create public-anywhere inbound gateway rules.

## COM Discovery and Recovery

Auto-detect by PnP FriendlyName every startup and after reconnect:

- Control: `D-Link HSPADatacard Control Port`
- Voice: `D-Link HSPADatacard Service Port`

Provide manual override.

Auto-Recovery enabled by default. On USB disconnect/reconnect: clean active call/media state, retry according to configurable interval/max duration, rediscover COM assignments, reopen/reinitialize safely, and return to `Ready` without app restart when possible.

## Crash / Orphan Recovery

On normal shutdown, safely hang up active cellular calls.

On startup, query `AT+CLCC` and clean orphaned old calls where appropriate.

Serial/SIP/RTP/Audio subsystem exceptions should be contained and recovered where practical. Unsafe/unknown call state -> safe hangup -> resource cleanup -> Ready.

## Connection Manager

Detect D-Link Connection Manager port ownership.

`AutoCloseConnectionManager=false` by default. If false, warn and do not start. If true, safely close relevant D-Link processes then retry port acquisition.

## Logging

Default `Info`; support `Debug`.

Debug may include AT traffic/events, SIP signaling, RTP statistics, jitter, loss, buffer events, audio queue health, reconnect/recovery, and release health information. Never log SIP passwords.

The next release that changes Gateway code for a bug fix or behavioral correction must also add a persistent structured call history. Each call record must include a correlation ID, direction, dialed destination or incoming caller ID when available, authenticated SIP peer, request/start/connect/end timestamps, final cellular and SIP outcome, end origin/reason, negotiated codec, and a compact media-statistics summary. Records must distinguish unanswered, cancelled, rejected, failed-before-connect, connected, and normally completed calls. Never include SIP credentials, authorization headers, DTMF digits, or raw audio. Provide bounded retention/rotation and a documented way to retrieve the latest calls.

Incoming cellular calls must not be filtered by caller number. Route every detected incoming call to all currently routable authenticated SIP peers. A successfully authenticated SIP peer may be retained as a bounded fallback Contact when its client does not maintain standard `REGISTER`; when no Contact is immediately available, keep the cellular call ringing for the configured Contact-wait interval instead of rejecting it immediately.

## Optional Raw Audio Capture

`EnableAudioCapture=false` by default.

When enabled, timestamp RX/TX files, document format, and use simple retention/rotation. Modem-side raw capture should identify S16LE/8k/mono when applicable.

## GUI

Use WinForms. Keep it simple.

Display: modem status, SIM status, registration status, detected ports, SIP listener/status, MicroSIP registration, dialed number, call state, duration, approximate RX/TX audio level, live log, recovery/error state, active version.

Controls: Start Gateway, Stop Gateway, Hangup.

Common settings editable in GUI: TestNumber, AllowedDialNumbers, SIP credentials, codec/bitrate, logging, audio capture, auto-recovery. Advanced settings remain in `config.json`. Validate before saving.

## First Run

If `config.json` is missing, open a lightweight WinForms setup for modem detection status, TestNumber, AllowedDialNumbers, generated SIP credentials, Tailscale BindAddress, AllowedTailscaleIPs, and safe defaults. Do not build a large wizard.

## Build

Produce both framework-dependent development/debug build and self-contained Windows x64 release build.

Use a supported .NET LTS target compatible with selected packages and document the exact choice/rationale.

## Deployment / Versioning

Portable folder deployment, not MSI in Phase 1. Default deploy root: `C:\DWM157Gateway`, configurable.

Use Semantic Versioning and versioned releases, e.g. `releases\0.1.0\`. Include version, Git commit, and build date in logs/About/status.

Deploy side-by-side, Health Check before activation, and automatic rollback/retention of last known-good release if a candidate fails.

Health Check must not place a real cellular call.

## Testing

Create unit tests, practical integration tests, Fake/Mock modem, and lightweight test harness. Simulate `^CONN`, `NO CARRIER`, busy/failure, modem disconnect, PCM RX/TX, and call-state transitions.

Final acceptance still requires real DWM-157 + real SIM + real allowed call + real bidirectional audio verification.

## Performance

Measure/report where practical: RTT, RTP loss, jitter, buffering, underrun/overrun, and approximate audio latency if credible.

No hard numeric latency threshold for Phase 1. Acceptance means conversation is clear, stable, and practically usable.

## D-Link DLL Policy

Direct Serial/AT/PCM is the primary runtime design. If blocked, perform and document a short technical spike proving why a D-Link DLL is needed, then STOP for user approval before adding it to runtime.

## Phase Gate

Do not declare Phase 1 successful based only on AT dialing, voice-port RX, or successful `SerialPort.Write`.

Phase 1 requires real end-to-end bidirectional audio verification. After it passes, STOP before incoming calls, Windows Service, and autostart.
