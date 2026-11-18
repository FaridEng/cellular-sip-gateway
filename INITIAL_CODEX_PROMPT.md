# Initial Codex Task

> **Public repository note:** this is the original task prompt. The progress log and research evidence it mentions are kept private and are not part of this repository.

You are responsible for implementing Phase 1 of the DWM157Gateway project in this repository.

Start by reading `AGENTS.md` and every document it references. Then inspect the entire `research/` directory. Treat the supplied direct modem logs/raw captures as the evidence base for DWM-157 serial voice behavior.

## Environment

- Local repository: `<LOCAL_PROJECT_PATH>`
- Destination Windows host: `100.64.0.12` over Tailscale
- Preferred SSH alias: `dwm157-dest`
- Destination default deploy root: `C:\DWM157Gateway`
- Initial SIP client: MicroSIP on source Windows
- Implementation: C#/.NET + WinForms
- Prefer SIPSorcery for SIP/RTP unless a short documented technical spike demonstrates a real blocker

Before invasive changes, verify SSH connectivity and perform a non-destructive destination preflight.

## First Actions

1. Read all project instructions/docs.
2. Inventory `research/` and summarize the evidence you will rely on.
3. Inspect current repository state.
4. Verify `ssh dwm157-dest` works.
5. Non-destructively inspect destination:
   - D-Link PnP devices/FriendlyNames
   - detected Control/Service ports
   - whether D-Link Connection Manager is running or locking ports
   - Tailscale state/address
   - required .NET/developer dependencies
6. Do not change/reinstall modem driver or firmware.
7. Create/update `PROGRESS.md` with current facts, unverified assumptions, implementation plan, and acceptance-test matrix.
8. Implement in small testable milestones.
9. Use fake-modem/tests before real cellular calls whenever possible.
10. Build, deploy, health-check, collect logs, diagnose, fix, and retest autonomously.

## Core Technical Requirement

Runtime must directly bridge:

`MicroSIP <-> SIP/RTP <-> codec/resampler <-> S16LE 8k mono PCM <-> DWM-157 Service Port`

Call control uses DWM-157 Control Port and the AT commands/events documented in `docs/PROJECT_CONTEXT.md`.

Never hard-code COM numbers.

Detect by Windows PnP FriendlyName:

- `D-Link HSPADatacard Control Port`
- `D-Link HSPADatacard Service Port`

with config overrides.

Known successful call-control sequence includes:

- modem/SIM/network preflight
- `AT+SSAM=5`
- `ATD<number>;`
- wait for cellular connected event such as `^CONN: 1,0`
- bridge media
- `ATH` for hangup

Process unsolicited modem events asynchronously.

Initial modem media assumption:

- signed 16-bit little-endian PCM
- 8000 Hz
- mono
- 20 ms = 160 samples = 320 bytes

Verify this from research evidence before relying on it.

## SIP / Media

Phase 1 uses direct SIP/RTP over Tailscale:

- SIP UDP 5060 default
- RTP UDP 40000-40020 default
- bind to destination Tailscale interface/address by default
- one MicroSIP client
- one simultaneous call
- SIP username/password authentication
- no TLS/SRTP in Phase 1

Codec strategy:

1. Opus preferred, roughly 12-16 kbps initially, mono VoIP, 20 ms, FEC enabled.
2. Adaptive mode enabled by default where practical.
3. PCMU and PCMA fallbacks.
4. Verify codec interoperability with installed MicroSIP.
5. Resample as needed between RTP codec rate and modem 8 kHz PCM.

DTMF:

- primary: SIP telephone-event -> modem DTMF command (`AT+VTS` path where validated)
- in-band fallback

No destination microphone, speaker, Voicemeeter, or virtual audio device.

## Real Calls

You are authorized to place real test calls autonomously only to the explicit `TestNumber` / `AllowedDialNumbers` configured by the user.

Never invent, infer, or select another real number. Emergency numbers remain blocked by default. Do not make real calls merely for Health Check.

## End-to-End Audio Rule

Do not claim uplink success merely because `SerialPort.Write` succeeds.

The status `Bidirectional Audio Verified` is allowed only after a human or echo/recording endpoint confirms that MicroSIP audio is actually received on the cellular side while cellular-side audio is also heard in MicroSIP.

## First Vertical Slice

Before polishing the complete GUI, get the smallest end-to-end slice working:

1. modem discovery
2. AT preflight
3. MicroSIP registration
4. outgoing SIP call mapped to approved cellular dial
5. cellular-connect synchronization
6. cellular downlink audio to MicroSIP
7. MicroSIP uplink audio to Service Port
8. real bidirectional verification
9. synchronized hangup

Then harden recovery, GUI, settings, deployment, rollback, and documentation. Do not let GUI polish block the core proof.

## Required Stop Conditions

Stop and ask the user before:

- driver changes
- firmware changes
- adding D-Link vendor DLLs as runtime dependencies
- dialing outside the allowlist
- weakening/changing SSH or Tailscale security
- major scope/architecture expansion

When Phase 1 acceptance criteria pass, produce the Phase 1 report and STOP. Do not implement incoming calls, Windows Service, or autostart until the user explicitly approves Phase 2.
