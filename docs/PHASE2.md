# Phase 2 Scope And Acceptance

## Goals

Phase 2 extends the verified Phase 1 voice bridge with:

- Incoming cellular call detection and routing to the actively registered MicroSIP contact.
- Bidirectional Opus media after MicroSIP answers and the modem accepts `ATA`.
- Symmetric SIP/cellular hangup and bounded media cleanup.
- A shared headless Gateway runtime used by both WinForms and Windows Service hosts.
- Delayed automatic startup after Windows boot.
- Runtime restart after modem polling failures and Windows Service recovery actions after process failure.

SMS and Mobile Data remain outside this phase.

## Incoming Call Flow

1. The Gateway enables caller-ID reporting and polls `AT+CLCC` every 1500 ms.
2. `RING`, `+CLIP`, or a mobile-terminated `+CLCC` entry changes the modem state to incoming ringing.
3. The Gateway obtains the authenticated packet sources from all unexpired softphone `REGISTER` requests.
4. The Gateway forks SIP INVITEs to all registered Contacts using independent provisional media branches.
5. The first softphone to answer wins; the other branches are cancelled and only the winner is attached to the modem PCM transport.
6. The Gateway sends `ATA` to the DWM-157.
7. The existing Opus/PCM bridge carries audio in both directions.
8. SIP hangup ends the cellular call; cellular end cancels or hangs up the SIP call.
9. If no softphone is registered, available, or answered before timeout, the cellular call is rejected safely.

The Gateway routes to the authenticated REGISTER packet source rather than trusting an arbitrary Contact host or port.

## Service Hosting

- Service name: `DWM157Gateway`
- Release: `C:\DWM157Gateway\releases\0.2.1`
- Executable: `C:\DWM157Gateway\releases\0.2.1\service\DWM157Gateway.Service.exe`
- Runtime config: `C:\DWM157Gateway\data\config.json`
- Service log: `C:\DWM157Gateway\logs\gateway-service.log`
- Startup: delayed automatic
- Account: LocalSystem
- Failure recovery: restart after 5 seconds, 15 seconds, then 60 seconds
- In-process recovery: restart the Gateway runtime after three consecutive modem call-state polling failures

## Live Acceptance

1. Confirm MicroSIP shows registered.
2. From a separate telephone, call the SIM installed in the DWM-157.
3. Confirm MicroSIP rings.
4. Answer in MicroSIP.
5. Confirm audio is clear in both directions.
6. Hang up from MicroSIP and confirm the cellular call ends.
7. Repeat and hang up from the cellular side; confirm MicroSIP ends.
8. Reboot Gateway PC.
9. Without signing in or opening RDP, confirm the Service reaches Running, modem preflight passes, and MicroSIP registers.
10. Repeat one incoming call after reboot.

## Rollback

The latest pre-Phase-2 deployment backup is:

`C:\DWM157Gateway\rollback-phase2-20260820-112256`

To return temporarily to the Phase 1 GUI runtime, stop and disable the `DWM157Gateway` Service, restore the saved config if needed, and start:

`C:\DWM157Gateway\release-20260816\DWM157Gateway.WinForms.exe --start`
