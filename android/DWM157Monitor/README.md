# DWM157 Monitor Android

Read-only Gateway PC monitoring dashboard for Galaxy S25 Ultra.

## Version 0.6 UI

- Native light/dark semantic colors, rounded cards, 48dp minimum controls,
  labeled inputs, system-bar/keyboard insets and readable scaled text.
- The overview leads with live state and readiness, followed by compact
  navigation, connection quality and the separate NUC Observer viewpoint.
  Recent connection events are visible without exposing incident IDs/tokens.
- Human-facing times use `America/Los_Angeles`, never a fixed UTC offset or
  the phone's timezone: `Today · 4:30 PM PDT`, `Yesterday · 1:30 AM PST`,
  or a short calendar date. PST/PDT resolves from the event's actual instant.
  Call durations retain seconds; uptime uses compact days/hours.
- Dashboard, observations, events, call history, chart ranges, registration
  and diagnostics display use the shared formatter. API values and saved
  diagnostic JSON retain their original timestamps.
- VersionCode 6 / 0.6.0, compatible with Monitoring/Observer 0.5.0 and existing
  encrypted settings. Firebase-configured APK:
  `artifacts/DWM157Monitor-0.6.0-firebase-debug.apk`.
- Build, 12 timezone/DST tests and lint pass (zero errors, 50 warnings).
  Installed on S25 over NUC USB with settings/Push registration retained.
  Physical light/dark, portrait/landscape, 130% text and keyboard checks passed;
  a screen-off test was received/posted by Android. See
  `docs/runbooks/android-monitor-ui.md` (not included) and
  `docs/runbooks/s25-usb-via-laptop.md` (not included).

## Version 0.5

- Tailscale installed/running version observations, explicit source/time and
  same-track Windows comparison through the independent observer.
- Distinct NUC and S25 direct connection viewpoints; a failed API call does not
  claim the Gateway service stopped.
- Debounced foreground S25 notifications, independent observer Push support,
  permission/registration status and an explicit test-Push action.
- FCM registration/token rotation is a durable one-time background task;
  Gateway health polling still stops when the dashboard is paused.
- Firebase is enabled only when a matching, ignored `app/google-services.json`
  is supplied at build time. The retained account-free build visibly reports
  Push configuration as missing. A separate `0.5.0-firebase-debug.apk` now uses
  project `dwm157-monitor`. Sender authorization, S25 installation and actual
  screen-off delivery passed on 2026-10-28; 0.6 retains this configuration.
  See `docs/runbooks/monitoring-alerts.md` (not included).

## Version 0.4

- Demo and Live modes
- Polls the versioned Gateway PC summary endpoint while the app is visible
- Shows a rolling five-minute S25-to-Gateway PC RTT, jitter, and failure window
- Shows the Tailscale Direct/DERP path, Gateway PC Internet RTT and route changes
- Shows Galaxy A07 USB/ADB presence without treating USB as a network path
- Clears all prior live values when the Monitoring host becomes unreachable
- Provides a separate auto-refreshing page for the ten most recent calls
- Shows compact RTP loss, jitter, RTT, ordering, and dropped-frame metrics per call
- Shows a graphical five-minute live RTT chart
- Shows SQLite-backed charts for 1h, 24h, 7d, and 30d ranges
- Shows a call-readiness score with concrete blocking/degraded reasons
- Shows available Galaxy A07 battery, charging, temperature, model, and Android fields
- Provides a sanitized Diagnostics screen with user-selected JSON export
- Shows Gateway, modem/SIM, network/Tailscale, Windows, and call status
- Stores the API token encrypted with an Android Keystore key
- Provides an explicit lightweight Internet probe button
- Does not dial, answer, hang up, restart, or modify settings

## Build

The project matches the repository's Android projects: Android Gradle Plugin
8.13.2, Java 17, compile/target SDK 36, and minimum SDK 26.

Set `ANDROID_HOME` and `JAVA_HOME`, then run Gradle from this directory:

```powershell
gradle assembleDebug
```

The initial private deployment may use HTTP only over Tailscale. The app rejects
plain HTTP endpoints outside loopback, RFC1918, link-local, and the Tailscale
CGNAT range.
