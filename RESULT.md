# Veltrix-Control v0.4.0 result report

## Status

Phase 4 is implemented: authorized administrators can control processes, Windows services,
power state, and audited terminal sessions on explicitly enrolled nodes, with protection
policies enforced on the node. Phases 1–3 remain intact.

## Delivered workflows

- Process management: start approved executables, stop processes, and change priority with
  identity re-verification and a protected-process policy.
- Windows service management: start, stop, and startup-type changes with a protected-service
  policy and confirmation for stop operations.
- Scheduled restart/shutdown, logoff, sleep, hibernation, and Wake-on-LAN for nodes that
  report a MAC address.
- Audited PowerShell and CMD terminal sessions with streaming output, sequence-based
  draining, bounded buffers, idle cleanup, and multiple sessions per node.
- Desktop Administration workspace covering processes, services, and terminal actions.
- Four independent safety gates per action: role permission, explicit confirmation, node
  local policy, and typed argument validation.

## Verification

- Release build: **PASS**, zero compiler warnings and zero errors.
- Automated tests: **87 PASS** across unit, security, Agent, integration, and
  Controller-to-Agent end-to-end suites.
- New coverage: protection policies, local opt-in denial, process identity mismatch,
  fixture process start/stop, terminal lifecycle and streaming, incremental output
  sequences, invalid shell rejection, viewer denial, wake without MAC rejection, wake audit,
  and wake packet layout.
- Every administrative action and terminal command produces audit events; the audit chain
  remains valid.

## Release artifacts

- Tag: `v0.4.0`
- Installer: `Veltrix-Control-Setup.exe`
- Checksum manifest: `SHA256SUMS.txt`
- Archive: `Veltrix-Control-Windows-x64.zip`
- Version file and changelog are included in the release payload.

## Known limitations

- Binaries are not Authenticode-signed because no organization signing certificate was
  supplied; Windows may display an unknown-publisher warning.
- RealTime priority may be refused by Windows unless the service account holds the
  privilege.
- Wake-on-LAN depends on the reported MAC address and broadcast-capable networking.
- Software deployment, updates, backups, automation, compute scheduling, and game-server
  adapters remain planned phases.

