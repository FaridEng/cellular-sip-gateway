# Android Monitoring Phase 2 Status

Active version: `0.4.0`  
Status: built, locally validated, and deployed to Gateway PC; Galaxy S25 Ultra installation and live UI validation remain pending.

| Capability | Status | Version / notes |
| --- | --- | --- |
| Five-minute foreground RTT/variation/failure statistics | Implemented | Existing in 0.3.0; retained |
| Graphical live RTT chart | Implemented | 0.4.0, offline gaps are not connected |
| Persistent 1h/24h/7d/30d history | Implemented | 0.4.0, Gateway PC SQLite, 30-day retention |
| Range charts | Implemented | 0.4.0: peer RTT, Internet RTT, modem CSQ, RTP loss/jitter/RTT, A07 battery/temperature |
| History CSV export API | Implemented | 0.4.0 `/api/v1/history/export` |
| Sanitized diagnostic bundle and Android JSON export | Implemented | 0.4.0 `/api/v1/diagnostics`; view and save from the app |
| Call readiness score and reasons | Implemented | 0.4.0 `Ready`, `Degraded`, or `NotReady` |
| Detailed recent-call media summary | Implemented | 0.4.0 loss, jitter, RTT, ordering, dropped frames |
| Galaxy A07 USB/ADB presence | Implemented | Existing in 0.3.0; retained |
| Galaxy A07 health telemetry | Partially implemented | 0.4.0 collects ADB-visible battery, charging, temperature, model, Android, storage and memory; no companion agent yet |
| Internet bandwidth and data usage | Not implemented | A bounded speed-test design and data-budget policy are still required to avoid consuming cellular data unexpectedly |
| Background alerts and notifications | Not implemented | Requires Android foreground/background execution policy, runtime notification permission, threshold UI, and anti-flapping validation |
| Tailscale route-change alerts | Not implemented | Route changes are stored and shown, but no notification engine exists yet |
| Android home-screen widget | Not implemented | Deferred after background refresh design |
| Dark theme | Not implemented | Current programmatic UI uses a fixed light palette |
| Multiple gateways/profiles | Not implemented | Current secure storage holds one Gateway PC endpoint/token pair |
| SSE/WebSocket streaming | Not implemented | Polling remains the validated transport; history removes the main persistence limitation |
| Remote Restart/Hangup/Dial controls | Intentionally not implemented | Monitoring remains read-only; controls require a separate authenticated command API and explicit safety approval |
| Direct A07-to-Gateway PC network-quality measurement | Intentionally not implemented | The A07 link is USB and must not be represented as an independent network path |

## Verification

- Monitoring Release tests: `14/14` passed; full solution: `86/86` passed.
- Monitoring API smoke test: summary, default `24h` history, diagnostics, CSV, and SQLite creation passed.
- Android `assembleDebug` and `lintDebug`: passed with Microsoft OpenJDK 17 and SDK 36 (`0` lint errors; translation warnings remain).
- Candidate host: `artifacts/DWM157Gateway.Monitoring-0.4.0-self-contained`; EXE SHA256 `41E06E4C99DC5384F14AC9554ECA7173C4464B4B369C4E07AB654E73AE46C946`.
- Candidate APK: `artifacts/DWM157Monitor-0.4.0-debug.apk`; 64,255 bytes; SHA256 `BC697B19759E5EF4D13C46867441D46CB5DC51DF9CCDDACECE7CBC3CA616028B`.
- Gateway PC deployment: active at `C:\DWM157Gateway\monitoring\releases\0.4.0` as PID `3436`, listening only on `100.64.0.12:5088`.
- Deployment verification: Scheduled Task Running, executable hash matched, retained `0.3.0` configuration matched byte-for-byte, SQLite database created, Gateway Service remained Running, and Point A received the expected HTTP `403` from the S25-only allowlist.
- Rollback: release `0.3.0` remains intact; Scheduled Task XML backup is `C:\DWM157Gateway\monitoring\rollback\task-before-0.4.0-20260829-004424.xml`.
- Galaxy S25 Ultra installation, authenticated API validation from the approved peer, and visual chart validation remain pending.
