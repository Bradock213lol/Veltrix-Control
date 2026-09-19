# Veltrix-Control v0.3.0 result report

## Status

Phase 3 is implemented: authorized operators can maintain files inside the configured
managed root, transfer large files with progress and verification, and edit text or
configuration files with automatic backups and restore. Phase 1 and Phase 2 workflows
remain intact.

## Delivered workflows

- Create folder, create file, rename, move, copy, and delete with explicit confirmation
  for destructive actions and `device.files` authorization.
- Wildcard search, bounded folder sizing, ZIP creation, and ZIP extraction.
- Chunked upload and download between the desktop app and managed nodes with progress,
  cancellation, sequential-offset enforcement, and SHA-256 verification before a transfer
  can complete.
- Text/code editor with line numbers, find/replace, atomic saves, automatic pre-save
  backups, bounded per-file history, restore, and a line-level comparison view.
- Hardened path policy enforced on the node: managed-root protection, absolute/UNC/device
  path rejection, alternate data stream rejection, null-byte rejection, reparse-point
  escape detection, and archive zip-slip validation.
- Per-operation argument validation and a centralized permission taxonomy for current and
  future operations.

## Verification

- Release build: **PASS**, zero compiler warnings and zero errors.
- Automated tests: **56 PASS** across unit, security, Agent, integration, and
  Controller-to-Agent end-to-end suites.
- New coverage: file-engine round trips, stale-write conflict detection, backup/restore,
  archive round trip, zip-slip rejection, traversal rejection for mutations, binary-file
  rejection, transfer chunk sequencing, checksum rejection, permission enforcement, and
  reparse/stream path rejection.
- Transfers report success only after size and checksum verification on the receiving side.

## Release artifacts

- Tag: `v0.3.0`
- Installer: `Veltrix-Control-Setup.exe`
- Checksum manifest: `SHA256SUMS.txt`
- Archive: `Veltrix-Control-Windows-x64.zip`
- Version file and changelog are included in the release payload.

## Known limitations

- Binaries are not Authenticode-signed because no organization signing certificate was
  supplied; Windows may display an unknown-publisher warning.
- Transfers resume from the last sequential offset rather than arbitrary byte ranges.
- The editor targets text and configuration files up to 2 MB and rejects binary content.
- Process/service mutation, terminal sessions, software deployment, updates, backups,
  automation, compute scheduling, and game-server adapters remain planned phases.
