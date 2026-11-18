# Project Context

## Objective

The destination Windows PC contains a D-Link DWM-157 USB cellular modem with a SIM. The source Windows PC runs MicroSIP.

Project terminology:

- **Point A** is the user's current/source PC and SIP-client side.
- **Gateway PC** is the remote Windows gateway system formerly called Point B, including the DWM-157 modem environment.
- **Galaxy A07** is a separate Android device attached to Gateway PC by USB.

Phase 1 goal:

`MicroSIP <-> SIP/RTP over Tailscale <-> DWM157Gateway <-> DWM-157 <-> cellular network`

No Asterisk or FreePBX is required for Phase 1.

## Remote Environment

Destination Tailscale IP currently known:

`100.64.0.12`

Codex runs on the source computer and reaches Gateway PC over SSH/Tailscale.

Default destination deploy root:

`C:\DWM157Gateway`

Use SSH key authentication. Do not expose SSH, SIP, RTP, or gateway endpoints to the public Internet.

## Modem

- D-Link DWM-157
- HSPA+/3G cellular USB modem
- Circuit-switched voice is known to work through D-Link Connection Manager
- Not a VoLTE modem
- USB identity observed: `USB\VID_2001&PID_7600`

Platform information observed via AT commands:

- Manufacturer: `Spreadtrum Communication CO.`
- Model response: `V1.0.1-B7`
- Platform: `MOCOR_MOCORTM_13D_MP_W14.09_P5_Release`
- Project: `sc7702_datacard`
- Base: `DM_BASE_DM_WBASE_13D_MP_W14.09_P5`
- Hardware: `sc7702_datacard`
- Release date: `2016-02-25 10:53:21`

Do not depend on historical IMEI or other unit-specific identifiers.

## Windows PnP Ports

Observed FriendlyNames:

- `D-Link HSPADatacard Control Port`
- `D-Link HSPADatacard Service Port`
- `D-Link HSPADatacard Extension Port`
- `D-Link HSPA Modem`

COM numbers changed after driver reinstall. Examples: Control COM10 then COM6; Service/Voice COM9 then COM5.

**Never hard-code COM numbers.** Detect by PnP FriendlyName on every startup and after reconnect. Allow manual override.

Known serial settings: 115200 baud, 8 data bits, no parity, 1 stop bit.

## AT Control Findings

Known working commands include:

- `AT`
- `AT+CPIN?`
- `AT+CSQ`
- `AT+CREG?`
- `AT+COPS?`
- `AT+CLCC`
- `AT+CLIP=1`
- `AT+SSAM=5`
- `ATD<number>;`
- `ATH`

Observed call events include:

- `*SPVTCP: VOICE`
- `^ORIG`
- `^CONF`
- `^CONN: 1,0`
- `NO CARRIER`
- `^CEND...`

Known outgoing flow:

1. Confirm modem/SIM/network readiness.
2. Send `AT+SSAM=5`.
3. Send `ATD<number>;`.
4. Process unsolicited events asynchronously.
5. Treat `^CONN: 1,0` as the known connected indication.
6. Open/activate the voice stream at the correct time.
7. Use `ATH` for hangup.
8. Close media/serial resources safely.

## Voice Port Findings

`SpeechCall.ini` from D-Link Connection Manager identified the voice device as the Service Port.

Reverse-engineering strings in `Speech_Interface.dll` referenced Audio Capture/Receiver/Renderer/Sender and big-endian PCM encoder/decoder internals. However, **direct serial capture is the source of truth for runtime design**.

## Direct PCM Evidence

A successful direct call test, without D-Link Connection Manager handling media, used the Control Port for AT call control, waited for connection, opened the Service Port, and captured approximately 196,352 bytes over roughly 12 seconds.

Direct captures and D-Link raw debug files support this Windows-side stream interpretation:

- signed 16-bit PCM
- little-endian
- mono
- 8000 Hz
- nominal 16,000 bytes/sec
- 20 ms = 160 samples = 320 bytes

Verify again from supplied evidence before depending on it.

## TX Evidence

A direct test opened the Service Port during a connected call and wrote generated 1 kHz S16LE/8k/mono tone frames without local serial error.

This makes TX over the Service Port plausible, but it is **not sufficient to claim end-to-end cellular uplink verification**. Phase 1 must not be marked `Bidirectional Audio Verified` until a human or echo/recording endpoint confirms that MicroSIP audio is received on the cellular side.

## D-Link Software

Observed D-Link Connection Manager version: `D-Link_V4.0.2MES`.

Research files may include:

- `Speech_Interface.dll`
- `Channel.dll`
- `SuiteCommand.dll`
- `SpeechCall.ini`
- `Channel.ini`

These are research references only. Primary runtime design is direct Serial/AT/PCM.

## Operational Detail

D-Link Connection Manager may lock the Control and Service ports.

Default gateway behavior: warn in GUI and refuse to start while required ports are locked.

Config option: `AutoCloseConnectionManager`, default `false`. If enabled, the gateway may close relevant D-Link processes before acquiring the ports.

## Historical Troubleshooting Note

A previous `SIM not inserted` condition was caused by physical SIM seating/contact and was resolved. This is troubleshooting history, not a software requirement.
