# Remote Development and Deployment

> **Public repository note:** the progress log this document mentions is kept private and is not part of this repository.

## Topology

Codex executes on the source Windows computer.

Gateway PC is reached over Tailscale + OpenSSH.

Known destination Tailscale IP:

`100.64.0.12`

Use SSH key authentication. Do not store an SSH password or private key in repository files/prompts.

## SSH Alias

Recommended source-side SSH config:

```sshconfig
Host dwm157-dest
    HostName 100.64.0.12
    User Administrator
    IdentityFile <PROJECT_SSH_PRIVATE_KEY_PATH>
```

Verify before remote work:

```powershell
ssh dwm157-dest "hostname"
```

Use the `dwm157-dest` alias for all normal SSH/SCP operations. The project key is
selected by the alias-specific `IdentityFile`; connecting directly to
`Administrator@100.64.0.12` does not apply that option and can fail with
`Permission denied` even when the server and migrated key are healthy. If a
direct-IP diagnostic is unavoidable, pass the same private key explicitly with
`-i <PROJECT_SSH_PRIVATE_KEY_PATH>`. Diagnose selection with `ssh -G
dwm157-dest` and authentication with `ssh -vvv dwm157-dest hostname`. Never
commit the private key.

Codex may use `ssh`, `scp`, or equivalent Windows-native tooling for deployment/testing/log collection.

## Permissions

The destination SSH account may use Administrator privileges for approved project tasks including PnP inspection, project process management, normal development/runtime dependency installation, project-scoped firewall rules, deployment, execution, log collection, and approved real-call tests.

Do not modify/reinstall modem driver or firmware without explicit approval.

## Local Repository

Local project path is provided as:

`<LOCAL_PROJECT_PATH>`

Use a local Git repository.

Suggested layout:

```text
<LOCAL_PROJECT_PATH>\
  AGENTS.md
  docs\
  research\
  src\
  tests\
  scripts\
  .gitignore
  README.md
  CONFIGURATION.md
  TESTING.md
  TROUBLESHOOTING.md
  PROGRESS.md
```

## Destination Layout

Default:

```text
C:\DWM157Gateway\
  releases\
    0.1.0\
    0.1.1\
  data\
  logs\
  audio-debug\
  config.json
```

Keep real config outside Git-controlled release content where practical. Deploy root remains configurable.

## Release Flow

1. Build/test locally as appropriate.
2. Create framework-dependent and self-contained x64 artifacts.
3. Copy candidate into a new versioned destination release directory.
4. Preserve current known-good release.
5. Run non-call Health Check.
6. Activate only if Health Check passes.
7. If it fails, retain/restore last known-good release.
8. Collect logs and update `PROGRESS.md`.

Do not place a real cellular call as part of normal release Health Check.

## Real-Call Rule

Codex may place a real call automatically only when `TestNumber` is configured and explicitly allowed by `AllowedDialNumbers` or equivalent policy. Never select a number from logs, contacts, web search, or guesswork.

## Firewall

Defaults:

- SIP UDP 5060
- RTP UDP 40000-40020

Rules must be limited to approved Tailscale peers/interfaces. Use detected source Tailscale IP where possible, with explicit override. Never create public-anywhere inbound rules.

## MicroSIP

Codex should output exact settings: SIP server/address, port, username, password, transport, and codec priority.

If MicroSIP can be safely auto-configured, back up existing configuration first. Do not destroy unrelated accounts/settings.
