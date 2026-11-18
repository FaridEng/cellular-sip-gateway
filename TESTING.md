# Testing

## Android UI 0.6.0 installed and physically checked

Release finalization reran Android assemble/tests/lint successfully and the
complete .NET solution sequentially: 72 Core + 14 Monitoring + 20 Observer
tests passed. The initial concurrent .NET/Gradle run failed the existing
real-time pacing test (38.67 ms versus 18-22 ms); the sequential rerun passed
with no source/assertion changes. Avoid concurrent build load when interpreting
that wall-clock test; do not infer a live call regression from it alone.

`assembleDebug`, `testDebugUnitTest` and `lintDebug` passed: 12 Pacific Time/DST
tests, no test failures, zero lint errors and 50 warnings. Tests cover Pacific
day boundaries, both DST transitions, repeated-hour disambiguation, .NET
offset/fraction inputs, device locale/timezone independence, compact durations
and unchanged source diagnostic JSON. APK signing verifies and matches 0.5.0;
Firebase configuration remains enabled. Evidence:
`artifacts/android-monitor-0.6.0-validation.json`.

Installed directly over NUC USB on S25 with retained Live settings and Push
registration; installed APK hash matches the artifact. Physical screenshots
cover portrait/landscape, light/dark, 130% text, settings above the keyboard,
Alerts and Demo call/chart pages. Readiness baseline clipping found during QA
was fixed and the corrected APK rebuilt/reinstalled. Display settings and Live
mode were restored. A 0.6 screen-Dozing test was FCM-accepted; persisted delivery
sequence advanced 10 -> 11 and Android posted the package notification.
This is device evidence; no separate operator banner confirmation is claimed.
Offline simulation, new JSON export, 200% text and token rotation/reboot were
not included in this physical pass. Evidence:
`artifacts/android-monitor-0.6.0-physical/`. Procedures:
`docs/runbooks/android-monitor-ui.md` (not included), `docs/runbooks/s25-usb-via-laptop.md` (not included).

## Monitoring 0.5 and independent observer

Controlled Monitoring outage/recovery passed on 2026-10-28. The approved test
held the Gateway PC Monitoring host down for 90.07 seconds, then restored its listener
after 6.96 more seconds. Gateway PID 592 stayed Running in all 16 samples and
before/after checks. The production Observer emitted one same-incident pair
(sequences 6/7), FCM accepted both, and the operator individually confirmed
both notifications arrived with the S25 screen off. The temporary SYSTEM
recovery guard was removed. Final probes are Healthy with no incident,
pending alert, Push error or storage error. This verifies Monitoring API
unreachability/recovery, not stopping the real Gateway service or PC power.
Evidence: `artifacts/monitoring-outage-20261028/summary.json`; procedure:
`docs/runbooks/monitoring-alerts.md` (not included).

Live activation test on 2026-10-28: the production NUC Observer authenticated
with its dedicated sender account and the actual S25 registration; FCM accepted
the test, and the operator explicitly confirmed `Gateway observer test` arrived
with the S25 screen off. Final Observer readback was Healthy, Push configured,
one registered device, empty outbox and no storage error. This is operator-
observed screen-off delivery, not ADB log evidence. Monitoring outage/recovery
subsequently passed as described above; stopped-Gateway/power-loss scenarios,
token rotation and device reboot behavior remain separate tests. Sanitized evidence:
`artifacts/observer-push-activation-20261028.json`.

`dotnet test DWM157Gateway.sln -c Release` covers existing Core/Monitoring
tests; the added Observer suite covers debouncing, recovery, restart
deduplication, maintenance expiry, HTTP 401/403/500/redirect classification,
stale/malformed telemetry, numeric/platform-aware version comparison and
signed FCM HTTP v1 requests using a fake provider. Its real loopback HTTP test
simulates stop/recovery without touching the production Gateway.

Build and lint Android with `:app:assembleDebug :app:lintDebug`. A build without
Firebase configuration must show that Push is unavailable, not claim successful
delivery. After Firebase and the independent observer are activated, verify
actual S25 notifications with the screen off, recovery, token rotation,
duplicate suppression and notification denial. See the pending acceptance
steps in `docs/runbooks/monitoring-alerts.md` (not included).

Run after installing a .NET SDK:

```powershell
dotnet test DWM157Gateway.sln
```

Current automated coverage:

- Emergency number blocking
- Unlisted number blocking
- Explicit unrestricted dialing with emergency blocking preserved
- Allowed `TestNumber`
- DialPlan transformation
- AT preflight command sequence
- `AT+SSAM=5` before `ATD<number>;`
- `^CONN: 1,0` state transition
- SIP digest challenge and authenticated registration
- SIP host fail-closed behavior when no peer allowlist is configured
- Configurable codec ordering and PCMU-only rollback
- Narrowband PCM to Opus encode/decode round trip
- Opus SDP/RTP clock and FEC parameters
- In-place PCM fixed gain, saturation safety, RMS/peak/clipping telemetry, and zero-allocation performance
- Safe mapping of all RFC telephone-event DTMF IDs to `AT+VTS`
- Rejection of invalid DTMF events and DTMF outside a connected cellular call
- Ordered asynchronous DTMF forwarding with received/forwarded/failed/dropped telemetry
- Encoded RTP payload byte/bitrate accounting and packet-arrival gap telemetry
- Privacy-safe SDP media diagnostics for packetization, Opus parameters, ICE, RTCP mux, and direction
- Delayed `+CLIP` caller-ID delivery after `RING`, caller-ID timeout, and SIP-safe caller identity normalization

Current local Release result: 56 passed, 0 failed.

## Final Phase 1 test

Use a number explicitly approved for the test. When `DialPolicy.AllowUnlistedNumbers` is `false`, it must also be present in `TestNumber` or `AllowedDialNumbers`.

1. Close D-Link Connection Manager on the destination.
2. Start the deployed WinForms gateway and click **Start Gateway**.
3. Configure MicroSIP using the settings in `CONFIGURATION.md`.
4. Confirm MicroSIP registers successfully.
5. Place one call to an explicitly allowed test number.
6. Confirm the call connects and speak in both directions. Verify that MicroSIP audio is heard through the cellular side and cellular-side audio is heard in MicroSIP.
7. For a suitable allowed IVR, open the MicroSIP keypad and press one non-sensitive digit requested by the IVR.
8. Confirm that the IVR reacts to the digit and the gateway reports `DTMF bridge: received=1, forwarded=1, failed=0, dropped=0` at call end.
9. Hang up from MicroSIP and confirm the gateway returns to idle without leaving the modem port locked.

Record the gateway log, MicroSIP registration result, call result, codec, RTP receive statistics, RTCP remote-receiver statistics, and the two-way audio result.

Real Phase 1 acceptance requires MicroSIP registration, an allowed cellular call, human confirmation of bidirectional audio, and one cellular-side IVR recognition of forwarded DTMF.

## Phase 2 incoming-call test

1. Confirm the destination `DWM157Gateway` Service is Running and MicroSIP is registered.
2. Call the modem SIM from a separate telephone.
3. Confirm MicroSIP rings, then answer it.
4. Verify bidirectional audio.
5. Hang up once from MicroSIP and once from the cellular side in separate calls; confirm symmetric cleanup.
6. Inspect `C:\DWM157Gateway\logs\gateway-service.log` for incoming detection, SIP Contact routing, `ATA`, media start, and clean end telemetry.
7. Reboot the destination and repeat without opening an interactive Windows session.
