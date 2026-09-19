# Veltrix-Control v0.6.0 result report

## Status

Phase 6 is implemented: verified backups with retention, hosted alerts with
acknowledgement, and operator-controlled automation with run history. Phases 1–5 remain
intact.

## Delivered workflows

- Backup creation, verification, and restore for managed-root folders and files with
  SHA-256 integrity, entry-count and size limits, zip-slip protection, and restore to a
  new destination only.
- Retention-based backup expiry that queues a safe archive cleanup operation.
- Alert evaluation for offline, CPU, memory, and low-disk conditions with open-alert
  deduplication, automatic resolution, acknowledgement, and audit events.
- Automation rules with schedule, alert, device-online, and device-offline triggers;
  device-name conditions; allow-listed actions; cooldowns; recursion protection; and run
  history.
- Desktop Operations workspace covering backups, alerts, and automation.

## Verification

- Release build: **PASS**, zero compiler warnings and zero errors.
- Automated tests: **111 PASS** across unit, security, Agent, integration, and
  Controller-to-Agent end-to-end suites.
- New coverage: backup round trip with entry counts and hashes, outside-root rejection,
  existing destination and missing source failures, restore destination protection, backup
  completion and failure propagation, alert list/acknowledge, automation create/disable/
  delete, unsafe automation rejection, and viewer permission denial.

## Release artifacts

- Tag: `v0.6.0`
- Installer: `Veltrix-Control-Setup.exe`
- Checksum manifest: `SHA256SUMS.txt`
- Archive: `Veltrix-Control-Windows-x64.zip`
- Version file and changelog are included in the release payload.

## Known limitations

- Backups are stored inside the managed root; use a separate volume for stronger
  protection.
- Alert evaluation runs on a 30-second cycle against the latest heartbeat telemetry.
- Binaries are not Authenticode-signed because no organization signing certificate was
  supplied; Windows may display an unknown-publisher warning.
- Compute scheduling, game-server adapters, and integrations remain planned phases.
