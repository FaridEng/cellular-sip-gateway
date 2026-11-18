# Engineering notes

The main problems found while running the gateway on real hardware, and how each was fixed. They are summarized from the project's private engineering log, which also holds the deployment details. Numbers are the measurements recorded at the time; they are not a benchmark.

## 1. An undocumented voice path

**Problem.** The DWM-157 is a 3G data stick. Its vendor software can make voice calls, but there is no documented voice API.

**Approach.** AT-command probes and direct captures of the modem's USB serial ports showed that the Control Port handles call control and the Service Port carries raw call audio as S16LE, 8 kHz, mono PCM (320 bytes per 20 ms). The gateway talks to those ports directly. It finds them by their Plug and Play names, because COM numbers change, and it never loads the vendor DLLs. See [`research/README.md`](research/README.md).

## 2. Choppy audio with zero network loss

**Problem.** Early Opus calls had audible cuts and stale audio.

**Cause.** Full packet accounting ruled out the network: one call received all 2,787 RTP packets with zero loss and 4.0 ms jitter. The cause was inside the gateway. Both nominal 20 ms output clocks averaged 22.1 to 22.3 ms, because cumulative loop delays drifted, and the Service Port delivered audio in bursts. Together they produced 241 and 260 internal frame drops and a 1.76 s average queue age.

**Fix.**
- Absolute-clock pacing with bounded catch-up instead of cumulative delays.
- Separate adaptive buffers for each direction, sized for the bursty serial delivery.
- A minimum spacing between RTP packets instead of catch-up bursts.
- Per-call telemetry for queue age, gaps, bursts and pacing.

**Result.** A later downlink test had zero drops and zero underruns in both buffers. The Service Port averaged 50.05 frames per second despite 306 gaps over 40 ms and 2,144 bursts under 5 ms, and the buffers absorbed all of them.

## 3. Latency that never recovered after a stall

**Problem.** Calls had no packet loss but a measured round-trip path latency of about 1.54 s.

**Cause.** When an audio buffer reached its maximum, it discarded only one old frame per new frame. A single scheduler or serial stall could therefore leave the queue near its maximum for the rest of the call, even after pacing returned to 20 ms.

**Fix.** On overflow, the buffer now trims stale audio back to its current target in one step. The defaults became 200/300/600 ms (minimum/target/maximum) from modem to RTP and 80/120/300 ms from RTP to modem.

**Result.** An 11-minute real call on the fixed build had 0% RTCP-reported loss from gateway to softphone and 0.039% RTP loss in the other direction. Internal round-trip buffering was about 463 ms, or about 736 ms including network RTT: a 52% reduction from the earlier 1.54 s.

## 4. Incoming calls that never rang

**Problem.** Incoming cellular calls did not ring on the Android softphone, although it could still place outgoing calls.

**Cause.** Two separate issues.
- The softphone refreshed its SIP registration about every 54 minutes, but the gateway expired its contact after 300 seconds. SIPSorcery represents a missing Contact-level expiry as a negative value, so the gateway was ignoring the request-level `Expires` header.
- Android background suspension was the likely reason the softphone's registration refreshes sometimes stopped altogether.

**Fix.**
- Fall back to the request-level `Expires` header, cap accepted registrations at 24 hours, and echo the accepted lifetime in the response.
- Remember authenticated peers as bounded fallback contacts, persist them across restarts without credentials, and let an incoming call wait a configurable time for a contact instead of failing at once.

**Result.** In the acceptance retest of version 0.2.11, a real incoming cellular call rang on the Android softphone, and the user reported correct audio with no problems observed.

## 5. Caller ID missing on incoming SIP calls

**Cause.** The modem controller captured the caller's number from `+CLIP` and `+CLCC`, but the incoming-call coordinator did not pass it into the SIP `From` header.

**Fix.** An event-driven wait for the caller ID, capped at 1 second; SIP-safe number normalization; and an explicit `From` identity. A withheld number becomes a private caller. Logs record only whether a number was available, never the number itself.

**Status.** Implemented. On-screen display has not been confirmed on every softphone.

## 6. DTMF into phone menus

**Approach.** `AT+VTS=?` confirmed that the modem supports in-call DTMF. SIP telephone-events are forwarded to the modem with `AT+VTS` through an ordered, bounded queue. The queue accepts only standard DTMF events, requires a connected call, isolates per-event failures and keeps the digits out of logs.

**Result.** In the Phase 1 acceptance call to an automated phone menu, 2 events were received and 2 were forwarded, with none failed or dropped.

## 7. Monitoring that cannot break calls

**Design.**
- Monitoring is a separate process with no reference to the gateway core, so it cannot dial, answer, hang up or change configuration.
- The gateway publishes a status snapshot every five seconds by writing a temporary file in the same folder and replacing the old one atomically. It reuses data it already has and sends no extra AT commands.
- The Android app clears every live value after a failed poll and shows only an explicit Offline state, so stale numbers cannot be mistaken for current ones.

## 8. Quiet audio from the cellular side

**Problem.** The softphone user reported weak received volume.

**Approach.** Measure before changing anything. Modem audio measured -39.2 dBFS active RMS and -15.7 dBFS peak before gain. A fixed gain stage, applied just before encoding, adds no buffering or delay and reports RMS, peak and clipping for every call.

**Status.** A trial at +9 dB, up from the +6 dB default, started on 2026-11-05. The listening comparison is still pending.
