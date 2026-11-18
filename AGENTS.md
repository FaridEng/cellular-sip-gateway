# AGENTS.md

> **Public repository note:** this is the original brief the coding agents worked from. The progress log, troubleshooting log and runbooks it mentions are kept private and are not part of this repository.

## Mission

Build and validate Phase 1 of **DWM157Gateway**: a Windows gateway that bridges a D-Link DWM-157 cellular voice call to a SIP softphone over Tailscale.

## Site Terminology

- **Point A**: the user's current/source PC and SIP-client side.
- **Gateway PC**: the remote Windows gateway system formerly called Point B, including the DWM-157 modem environment.
- **Galaxy A07**: the separate Android device attached to Gateway PC by USB; do not treat the Gateway PC-to-A07 USB link as a network-quality path.

## Communication Preference

When writing Persian, start every Persian sentence with a Persian word so mixed-direction text remains readable.
Keep established computer, networking, telecom, and software technical terms in their original English form in Persian responses unless the user explicitly requests a translation.

Phase 1 is complete only when MicroSIP can register, place an allowed outgoing cellular call through the DWM-157, and bidirectional audio is verified end-to-end.

After Phase 1 passes, **STOP and ask the user for approval before Phase 2**. Phase 2 includes incoming cellular calls, Windows Service hosting, and autostart.

## Read First

Before changing code, read:

- `docs/PROJECT_CONTEXT.md`
- `docs/REQUIREMENTS.md`
- `docs/ACCEPTANCE_TESTS.md`
- `docs/REMOTE_DEPLOYMENT.md`
- `research/README.md`

Inspect all files under `research/` before making assumptions about modem behavior. Keep `PROGRESS.md` current.

## Working Mode

Operate autonomously inside the approved software scope:

`inspect -> implement -> build -> test -> deploy -> run -> collect logs -> diagnose -> fix -> retest`

You may change C#/.NET source, project structure, NuGet packages, SIP/RTP implementation, codec/resampler/buffering, tests, build configuration, deploy scripts, and project-scoped firewall rules limited to approved Tailscale peers/interfaces.

## Approval Required

STOP and ask before:

- changing/reinstalling the DWM-157 driver
- changing modem firmware
- adding a D-Link DLL as a runtime dependency
- dialing any number not explicitly allowed by configuration
- changing SSH or Tailscale security
- exposing SIP/RTP/SSH/gateway services to the public Internet
- making a major architecture change outside Phase 1
- starting Phase 2

D-Link DLLs under `research/` are reverse-engineering references only unless explicitly approved for runtime use.

## Safety Rules

- Never hard-code COM numbers.
- Never hard-code or commit real SIP credentials.
- Real `config.json` must be ignored by Git; commit only `config.example.json`.
- Never log SIP passwords.
- Never invent a real test telephone number.
- Automated real calls are allowed only to `TestNumber` and/or numbers explicitly in `AllowedDialNumbers`.
- Emergency numbers must be blocked by default.
- D-Link Connection Manager must not own modem ports while the gateway is active.
- No destination microphone, speaker, Voicemeeter, or virtual audio device is part of the Phase 1 path.

## Preferred Technology

Primary implementation:

- C#
- .NET
- WinForms
- SIPSorcery preferred for SIP/RTP

If SIPSorcery becomes a real blocker, perform a short documented technical spike first. A fallback such as PJSIP/PJSUA2 may be proposed only after documenting the blocker and tradeoffs.

Python is not the primary implementation language. Native C++ SIP is a fallback, not the default.

## Architecture Expectations

Keep the core independent from the WinForms host so it can later be reused by a Windows Service host.

Suggested components:

- Device discovery
- AT/control channel
- Voice serial/PCM transport
- Cellular call state machine
- SIP registrar/authentication
- SIP call control
- RTP/media session
- Codec/resampler
- DTMF bridge
- Network recovery
- Configuration
- Logging/telemetry
- WinForms UI
- Fake/mock modem and test harness

Design interfaces so future SMS and Mobile Data modules can be added without rewriting the voice core. Do not implement SMS or Mobile Data in Phase 1.

## Documentation

Create and maintain:

- `README.md`
- `CONFIGURATION.md`
- `TESTING.md`
- `TROUBLESHOOTING.md`
- `PROGRESS.md`

Every operational change, deployment, Scheduled Task change, network setting,
or recovery-behavior change must be documented in the same unit of work:

- update the applicable Runbook under `docs/runbooks/` (create one when the
  operational area has no Runbook yet)
- add the verified change and outcome to `PROGRESS.md`
- update `TROUBLESHOOTING.md` when diagnosis, recovery, rollback, or operator
  actions changed

An operational change is not complete until its documentation reflects the
deployed behavior. Keep documentation changes with the related source or
deployment changes in the same Git commit whenever practical.

At Phase 1 completion provide: active release version, Git commit, test results, real DWM-157 call results, bidirectional audio verification, RTP/network statistics, known issues, rollback status, and exact MicroSIP settings.

## Phase Gate

When Phase 1 acceptance criteria pass:

1. Mark Phase 1 complete in `PROGRESS.md`.
2. Produce the completion report.
3. STOP.
4. Ask for approval before implementing incoming calls, Windows Service, or autostart.
