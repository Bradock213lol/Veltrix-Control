# Veltrix-Control v0.5.0 result report

## Status

Phase 5 is implemented: administrators can register approved software, deploy it to one or
many nodes with per-device tracking, and manage Windows Update with scan, selective
install, and reboot reporting. Phases 1–4 remain intact.

## Delivered workflows

- Package registry for WinGet identifiers and checksum-pinned HTTPS MSI/EXE packages.
- Multi-device install, uninstall, and upgrade deployments with confirmation, per-device
  operations, success/failure counts, cancellation, and restart handling.
- Agent software execution with silent arguments, bounded installer output, exit-code
  interpretation, and mandatory checksum verification before execution.
- Windows Update scan and selective installation via the official Windows Update API, with
  history, KB/severity/size details, and reboot-required reporting.
- Desktop Deployment workspace: packages, deployments, per-device results, update scanning,
  and update installation.
- Background agent operation worker with persisted result delivery and automatic retry, so
  long-running installs no longer interrupt heartbeats.

## Verification

- Release build: **PASS**, zero compiler warnings and zero errors.
- Automated tests: **102 PASS** across unit, security, Agent, integration, and
  Controller-to-Agent end-to-end suites.
- New coverage: deployment completion and failure propagation, cancellation, unsigned MSI
  rejection, operator permission denial, package argument validation, non-WinGet uninstall
  rejection, malformed update identifiers, and the complete update scan/install flow.

## Release artifacts

- Tag: `v0.5.0`
- Installer: `Veltrix-Control-Setup.exe`
- Checksum manifest: `SHA256SUMS.txt`
- Archive: `Veltrix-Control-Windows-x64.zip`
- Version file and changelog are included in the release payload.

## Known limitations

- WinGet requires a reachable WinGet installation for the service account.
- Binaries are not Authenticode-signed because no organization signing certificate was
  supplied; Windows may display an unknown-publisher warning.
- Backups, automation, compute scheduling, game-server adapters, and integrations remain
  planned phases.
