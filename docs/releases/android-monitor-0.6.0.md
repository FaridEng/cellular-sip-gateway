# Android Monitor 0.6.0

## Release state

Private Firebase-configured debug build, versionCode 6. Installed in place on
Galaxy S25 Ultra SM-S938U over NUC USB on 2026-10-28. The installed APK SHA256
matches `artifacts/DWM157Monitor-0.6.0-firebase-debug.apk`:

`FE5FDE2D694C11AD83DF7D6BBAB2376EE209CD33997F60EA395E7BDEEC5F0522`

Size: 4,441,158 bytes. Signature verifies and matches the retained 0.5.0 build.
APK, Firebase configuration, private tokens and screenshots remain local,
outside Git. This is not a Play Store publication or a new production signing key.

## Changes

- Shared native cards, light/dark semantic colors, labeled controls, system
  insets, accessible touch targets and a readiness-first overview.
- Compact Pacific Time display using `America/Los_Angeles`, event-correct
  PST/PDT, Today/Yesterday/date labels and readable durations. API/storage and
  exported diagnostic timestamps retain their original values.
- Restyled Calls, Charts, Alerts and Diagnostics; explicit local Demo charts,
  stable range selection and protection against stale chart responses.
- Retained encrypted configuration, notification permission and Push registration.

## Verified

- Android build, 12 timezone/DST tests and lint: zero errors, 50 warnings.
- Release validation: 72 Core, 14 Monitoring and 20 Observer tests passed in a
  sequential solution run. The initial run concurrent with Gradle failed the
  existing wall-clock media-pacing test (38.67 ms outside 18-22 ms); the full
  sequential rerun passed without changing source or weakening that assertion.
- On S25: light/dark, portrait/landscape, 130% text, settings with Keyboard,
  Alerts, Demo calls/charts, restored Live/Healthy with readiness 100.
- Readiness clipping found on-device was corrected before the final install.
- One 0.6 screen-Dozing Push was FCM-accepted, persisted by the app and posted
  by Android. No separate operator confirmation of this particular banner.
- Prior 0.5 real screen-off outage/recovery notifications were operator-confirmed.

Local evidence: `artifacts/android-monitor-0.6.0-validation.json` and
`artifacts/android-monitor-0.6.0-physical/`. Operations and rollback:
`docs/runbooks/android-monitor-ui.md` (not included).

## Limits and deferred checks

The operator deferred S25 reboot acceptance and the additional 0.6 UI
Offline/recovery test. New JSON export, 200% text, token rotation and Android
downgrade have not been physically verified. These are not implied by the
completed UI tests. Monitoring/Observer remain 0.5.0 and Gateway remains
0.2.11; no call-path or modem change is part of this release.
