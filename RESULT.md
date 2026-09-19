# Veltrix-Control v0.8.0 result report

## Status

Phase 8 is implemented: game servers can be provisioned, started, stopped, updated, and
operated through a live console with crash recovery and crash-loop protection. Phases 1-7
remain intact.

## Delivered workflows

- Versioned adapter contract with a production Minecraft Java adapter that downloads the
  official server jar, verifies the published SHA-1, accepts the EULA explicitly, and
  writes safe default configuration.
- Instance lifecycle: provision, start, stop, update, console input, and streamed console
  output with bounded buffers.
- Crash detection with automatic restart, crash-loop protection, and actionable alerts.
- Complete instance event history and audit trail for every action.
- Desktop Game servers workspace with creation, lifecycle actions, and a live console.

## Verification

- Release build: **PASS**, zero compiler warnings and zero errors.
- Automated tests: **127 PASS** across unit, security, Agent, integration, and
  Controller-to-Agent end-to-end suites.
- New coverage: adapter validation, unsafe install paths, unprovisioned starts, process
  manager fixture lifecycle, full lifecycle through agent operations, automatic restart
  after a crash, invalid request rejection, and viewer permission denial.

## Release artifacts

- Tag: `v0.8.0`
- Installer: `Veltrix-Control-Setup.exe`
- Checksum manifest: `SHA256SUMS.txt`
- Archive: `Veltrix-Control-Windows-x64.zip`
- Version file and changelog are included in the release payload.

## Known limitations

- Minecraft Java requires Java 21 or newer on the managed node; provisioning fails with a
  clear message when Java is missing.
- Only the Minecraft Java adapter is present; the contract is ready for additional
  adapters in later phases.
- Binaries are not Authenticode-signed because no organization signing certificate was
  supplied; Windows may display an unknown-publisher warning.
- Optional Pterodactyl and Docker integrations remain a planned phase.