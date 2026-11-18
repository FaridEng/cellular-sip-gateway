# Research notes

These notes summarize how the DWM-157 voice path was worked out before the gateway was built. The original evidence (vendor DLLs and INI files, AT-command logs, and a raw voice capture) is not included in this repository: the vendor files are not ours to redistribute, and the logs contain line-specific data.

## What the evidence showed

- The modem exposes three USB serial ports. Windows names them `D-Link HSPADatacard Control Port`, `Service Port` and `Extension Port`.
- COM numbers change between machines and reboots, so the gateway finds the ports by their Plug and Play FriendlyName instead.
- The Control Port takes AT commands for dialing, call state and DTMF (`AT+VTS`).
- During a connected call the Service Port carries continuous PCM audio. A direct capture of about 196 KB over roughly 12 seconds matched S16LE, 8 kHz, mono: 320 bytes are 20 ms.
- Writing generated tone frames to the Service Port during a call completed without serial errors. End-to-end uplink was verified later, during Phase 1 acceptance, not by that early test.
- The vendor's connection-manager DLLs were inspected for strings only. The gateway does not load or depend on them; the runtime design uses the serial ports directly.
