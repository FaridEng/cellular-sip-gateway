# Phase 1 Acceptance Tests

> **Public repository note:** the progress log this document mentions is kept private and is not part of this repository.

## Non-Call Preflight

All must pass without placing a real call:

- Gateway starts without immediate crash.
- Correct destination Tailscale BindAddress is selected.
- Firewall rules are restricted to approved Tailscale peers/interfaces.
- Control and Service ports are discovered by PnP FriendlyName, not hard-coded COM numbers.
- `AT` returns `OK`.
- SIM is ready.
- Cellular registration is checked and acceptable.
- SIP listener is active.
- MicroSIP can register using generated credentials.
- RTP/media subsystem is ready.
- GUI/Core reaches `Ready`.

## Dial Policy

Verify:

- configured allowed number can proceed
- unlisted number is blocked
- emergency numbers are blocked by default
- DialPlan rules work only as configured
- Codex never invents a test number

## Outgoing Call Signaling

From MicroSIP:

1. Dial `TestNumber`.
2. Gateway validates allowlist.
3. Gateway performs cellular setup.
4. SIP side remains in progress while GSM setup is pending.
5. SIP must not be marked connected prematurely.
6. Modem connected event leads to SIP connected state.
7. GUI shows correct state/duration.

Also test busy/no-answer/failure mappings where practical.

## Cellular Downlink Audio

Verify real path:

`cellular -> DWM-157 -> Service Port -> PCM -> codec -> RTP -> MicroSIP`

Success means the source user clearly hears cellular-side audio.

Record codec, RTP counters, jitter, loss, and buffer health.

## Cellular Uplink Audio

Verify real path:

`MicroSIP -> RTP -> decode/resample -> S16LE/8k -> Service Port -> DWM-157 -> cellular`

A successful serial write is not enough.

Success requires a human or echo/recording endpoint on the cellular side to confirm receipt of MicroSIP audio.

Only then may the project state be recorded as:

`Bidirectional Audio Verified`

## DTMF

Verify SIP telephone-event reception and primary translation to modem DTMF command. When a suitable IVR is available, verify at least one digit is recognized on the cellular side. Document/test in-band fallback where practical.

## Hangup

Verify:

- MicroSIP BYE -> cellular hangup
- MicroSIP CANCEL during setup -> cellular cleanup
- modem `NO CARRIER` / `^CEND` -> SIP cleanup
- GUI Hangup -> both sides cleanup

After each, Voice Port/RTP/queues are cleaned and state returns to `Ready`. Repeated cleanup must not corrupt state.

## Temporary Network Failure

During an active call, briefly disrupt the SIP/RTP/Tailscale path. Verify GSM call is retained during configured grace period, recovery is attempted, and failure beyond the timeout causes safe cellular hangup.

Default grace period: 10 seconds.

## Modem Disconnect/Reconnect

When practical, test safe USB disconnect/reconnect without changing/reinstalling the driver. Verify state cleanup, COM rediscovery, and return to Ready without app restart if recovery succeeds.

## Restart / Orphan Recovery

Verify startup `AT+CLCC` handling, stale/orphan cleanup, no stale media sessions, and return to Ready.

## Release Health / Rollback

Verify a candidate is deployed side-by-side, non-call Health Check passes before activation, a deliberately failed candidate does not replace the last known-good release, and rollback reason is logged.

## Final Usability Test

Final acceptance requires:

- real DWM-157
- real SIM
- MicroSIP on source Windows
- real allowed test number
- confirmed bidirectional voice
- practically usable conversation quality
- no destination microphone/speaker/Voicemeeter
- no D-Link DLL runtime dependency unless separately approved

Report: codec, bitrate, packetization, RTT, packet loss, jitter, buffering, observed stability, approximate audio latency if credible, and known limitations.

There is no mandatory numeric latency threshold for Phase 1.

## Stop Condition

When required Phase 1 acceptance tests pass:

1. update `PROGRESS.md`
2. produce final Phase 1 report
3. identify active version and Git commit
4. list known issues
5. STOP
6. request user approval for Phase 2
