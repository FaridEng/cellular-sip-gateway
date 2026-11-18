# Phase 1 Completion Report

## Result

Phase 1 is complete. MicroSIP registration, allowlisted outgoing cellular calls, bidirectional audio, synchronized call handling, and SIP telephone-event to modem DTMF were verified on the real DWM-157 path.

## Active Release

- Destination path: `C:\DWM157Gateway\release-20260816\DWM157Gateway.WinForms.exe`
- Active destination PID at completion: `3788`
- Core SHA256: `D4BD9BF17C50F3E3D846A64B84E24E988298D5011467DBEF12765E721FD36463`
- WinForms SHA256: `81BD389B787D317FB7052110C003145F6B8E8EF524EC1D5002B4B6C725E602F3`
- Source control status: no Git metadata is present in the workspace, so no Git commit can be reported.

## Automated Tests

- Command: `dotnet test DWM157Gateway.sln -c Release --no-restore`
- Result: `36 passed, 0 failed, 0 skipped`

## Real Device Acceptance

- DWM-157 discovery: Control COM6 and Service COM5 discovered by PnP FriendlyName.
- Modem preflight: AT, SIM, cellular registration, and `AT+VTS` support passed.
- SIP: MicroSIP authenticated registration accepted over Tailscale.
- Outgoing calling: explicitly allowlisted numbers were dialed successfully.
- Bidirectional audio: user verified audio in both directions with forced Opus.
- DTMF: the final live IVR call offered `telephone-event`; 2 events were received and 2 were forwarded through `AT+VTS`, with 0 failed and 0 dropped. The user confirmed the new-version test worked.

## Final Call Telemetry

- Codec: Opus, 48 kHz RTP clock, 8 kHz mono PCM, 20 ms packets.
- RTP receive: 1881 received, 24 lost, 2 out of order, 2.2 ms jitter.
- Gateway-to-softphone RTCP: 0% reported loss, 10.0 ms latest jitter, 247.7 ms latest RTT.
- RTP pacing: 20.00 ms average, no sub-15 ms bursts.
- Audio level: +6 dB modem-to-RTP gain, 0 clipped samples.
- DTMF: received 2, forwarded 2, failed 0, dropped 0.

## Active Media Configuration

- Preferred/forced codec: Opus.
- Opus: 24 kbit/s, VBR enabled, complexity 10, FEC disabled, expected loss 0%, DTX disabled.
- Fallback codec configuration: PCMU and PCMA remain configured, but forced preferred codec advertises Opus only.
- Modem-to-RTP buffer: 300 ms target, 600 ms maximum.
- RTP-to-modem jitter buffer: 120 ms target, 300 ms maximum.
- RTP range: UDP 40000-40020.
- Modem-to-RTP gain: +6 dB.

## MicroSIP Settings

- SIP server: `100.64.0.12:5060`
- Transport: UDP.
- Username: `dwm157`.
- Codec order: Opus, PCMU, PCMA.
- STUN: disabled.
- ICE: disabled.
- DTMF method: `RFC2833` / RTP Events (`DTMFMethod=2`).
- The runtime SIP password remains excluded from this report.

## Safety And Rollback

- Dialing remains restricted to the runtime allowlist.
- Emergency numbers remain blocked.
- SIP/RTP is limited to the approved Tailscale path.
- Runtime config and SIP credentials are not committed.
- DTMF release rollback: `C:\DWM157Gateway\rollback-dtmf-20260820-095408`.
- Pre-DTMF MicroSIP backup: `%APPDATA%\MicroSIP\MicroSIP.ini.pre-dtmf-20260819-232543`.

## Known Issues

- The final call had 24 lost inbound RTP packets out of 1905 expected (about 1.26%) and 2 out-of-order packets. The call and DTMF test still passed.
- The final call reported 16 modem-to-RTP stale-frame drops and 22 RTP-to-modem underruns. Bounded buffers prevented permanent queue growth.
- Tailscale RTT remains around 248 ms, so some unavoidable conversational delay remains.
- The workspace has no Git repository metadata.
- Windows Service hosting, autostart, and incoming cellular-call routing are intentionally deferred to Phase 2.
