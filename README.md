# DWM157 SIP Gateway

A Windows gateway that bridges voice calls on a D-Link DWM-157 USB cellular modem to SIP/RTP. A softphone anywhere on a private Tailscale network can place and receive calls on a remote SIM line. A separate, read-only monitoring stack adds an Android dashboard and push alerts.

> **Status: working prototype for personal use, not production software.** Outgoing calls with two-way audio and DTMF, and incoming calls to a softphone, were verified on real hardware during development. It is built for one modem family and one deployment, so expect to adapt it.

## The problem

The DWM-157 is a 3G USB data stick. It can make circuit-switched voice calls through its vendor software, but it has no documented voice API. The voice path had to be worked out from the modem's AT command set and its USB serial ports:

- the **Control Port** takes AT commands for call setup, state and DTMF;
- the **Service Port** carries raw call audio as S16LE, 8 kHz, mono PCM (20 ms = 320 bytes).

The gateway turns that into a standards-based SIP endpoint, so any ordinary softphone can use the line.

## Features

- **Modem control:** finds the COM ports by their Windows Plug and Play names, because port numbers change; runs an AT preflight (SIM, network registration, DTMF support); sequences calls and polls call state.
- **SIP:** UDP registration and calls through SIPSorcery. Only allowlisted Tailscale peers are accepted, and nothing is exposed to the public internet.
- **Media:** Opus (through Concentus), PCMU and PCMA; resampling between 8 kHz modem PCM and the RTP codec; adaptive jitter buffers with bounded latency in both directions; paced RTP sending; configurable gain; RTP and RTCP statistics.
- **DTMF:** SIP telephone-events are forwarded to the modem with `AT+VTS`.
- **Dial policy:** only allowlisted numbers by default, emergency numbers blocked, optional dial-plan prefixes.
- **Incoming calls:** caller ID is passed through, the gateway waits a bounded time for a registered softphone, and authenticated peers have a fallback contact.
- **Hosting:** a WinForms app or a Windows Service, with a bounded, structured call history.
- **Monitoring, kept separate and read-only by design:**
  - a monitoring API that reads the gateway's status snapshot and keeps 30 days of telemetry in SQLite;
  - an independent Observer on a second machine that debounces outages and sends Firebase Cloud Messaging alerts;
  - an Android app (Java) with live status, charts, call history and alerts.

## Architecture

```text
 Softphone (MicroSIP, Linphone)
        │  SIP + RTP (Opus / PCMU / PCMA) over Tailscale
        ▼
┌──────────────────────── Gateway PC (Windows) ─────────────────────────┐
│ DWM157Gateway.Service or .WinForms                                    │
│  └─ DWM157Gateway.Core                                                │
│      · SipGatewayHost (SIPSorcery)        · DialPolicy                │
│      · codec, resampler, jitter buffers, RTP pacing                   │
│      · CellularCallController (AT commands)                           │
│      · VoiceSerialPcmTransport (S16LE 8 kHz mono PCM)                 │
│  └─ writes a status snapshot every 5 s                                │
│                     │ (read only)                                     │
│ DWM157Gateway.Monitoring: HTTP API + 30-day SQLite history            │
└──────────┬────────────────────────────────────────┬───────────────────┘
           │ USB: Control Port (AT), Service Port (PCM)
           ▼                                        │ HTTP over Tailscale
   D-Link DWM-157 ─── 3G cellular network           ▼
                                  Observer (second PC) ── FCM push ──┐
                                  Android Monitor app ◄──────────────┘
                                  (also polls the monitoring API)
```

Design boundaries:

- Monitoring has no reference to the gateway core. It cannot dial, answer, hang up, restart anything or change configuration.
- If the status snapshot is missing or malformed, monitoring reports "unknown" and the gateway is not affected.

## Repository layout

| Path | Contents |
|---|---|
| `src/DWM157Gateway.Core` | modem control, SIP, audio, configuration, dial policy |
| `src/DWM157Gateway.Service` | Windows Service host |
| `src/DWM157Gateway.WinForms` | desktop host with call controls |
| `src/DWM157Gateway.Monitoring` | read-only monitoring API |
| `src/DWM157Gateway.Observer` | independent outage observer with FCM push |
| `android/DWM157Monitor` | Android dashboard (Java) |
| `tests/` | xUnit tests for Core, Monitoring and Observer, using a fake AT transport |
| `ENGINEERING-NOTES.md` | the main problems found on real hardware, and their fixes |
| `docs/` | requirements, phase notes, monitoring architecture |
| `research/` | notes on the modem's serial voice path (vendor binaries and raw captures are not included) |
| `scripts/` | setup and deployment scripts (PowerShell), written for one deployment |

## Getting started

You need:

- a Windows 10 or 11 PC with a DWM-157 and a SIM that supports circuit-switched voice (the modem does not do VoLTE);
- Tailscale on the gateway PC and on the softphone device;
- a SIP softphone such as MicroSIP or Linphone;
- the .NET 8 SDK.

Build and test:

```powershell
git clone https://github.com/FaridEng/cellular-sip-gateway.git
cd cellular-sip-gateway
dotnet build DWM157Gateway.sln -c Release
dotnet test DWM157Gateway.sln -c Release
```

Configure:

1. Copy `config.example.json` to `config.json` next to the executable. Never commit `config.json`.
2. Set `sip.bindAddress` to the gateway's Tailscale address and `sip.allowedTailscaleIPs` to the softphone's Tailscale address.
3. Set a strong `sip.password`, and list the numbers you allow in `dialPolicy.allowedDialNumbers`.

[`CONFIGURATION.md`](CONFIGURATION.md) describes every setting.

Run `DWM157Gateway.WinForms` for interactive use, or install the Windows Service with `scripts/install-windows-service.ps1`. Read each script before you run it: they were written for one specific deployment.

On the softphone, register to the gateway's Tailscale address on UDP port 5060 with the configured username and password. Opus is the preferred codec.

## Status

- **Phase 1** (outgoing calls, two-way audio, DTMF): accepted on real hardware in August 2026. See [`PHASE1_COMPLETION_REPORT.md`](PHASE1_COMPLETION_REPORT.md).
- **Phase 2** (incoming calls, resilient routing, call history): gateway version 0.2.11. Incoming calls to a softphone were verified. Caller ID display on the softphone is still open.
- **Monitoring:** monitoring host 0.5.0, Observer 0.5.0 and Android Monitor 0.6.0. Push alerts were verified with a controlled outage. Device reboot and token rotation have not been tested yet.
- **Not production:** one deployment, one modem model, no load testing and no long-term stability data.

[`ENGINEERING-NOTES.md`](ENGINEERING-NOTES.md) describes the main problems found on real hardware and how they were fixed.

## How this was built

I defined the requirements and designed the architecture. The code was written by AI coding agents, mainly OpenAI Codex, working under my direction. I set up the modem, the network and Tailscale myself, and I tested real calls on the hardware.

[`INITIAL_CODEX_PROMPT.md`](INITIAL_CODEX_PROMPT.md) and [`AGENTS.md`](AGENTS.md) are the briefs the agents worked from.

## Responsible use

Use the gateway only with a SIM and a line that you own, and within your carrier's terms and local telecom rules. Emergency numbers are blocked by default because the gateway cannot provide a reliable emergency service or location.

## License

MIT. See [LICENSE](LICENSE).

Dependencies keep their own licenses. SIPSorcery is BSD 3-Clause with an additional use restriction, and Concentus is BSD-style. Read their license files before you redistribute a build.
