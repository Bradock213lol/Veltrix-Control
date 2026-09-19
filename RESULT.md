# Veltrix-Control v0.10.0 result report

## Status

Phase 10 is **partially implemented**. The production-hardening work that could be
completed and verified in this environment is done: complete user administration,
automatic retention, an agent-version policy alert, and administrator system diagnostics.
The remaining Phase 10 items are listed honestly under known limitations; they are not
claimed as implemented. Phases 1-9 are complete and verified.

## Delivered workflows

- User administration: list, create, change roles, reset passwords, and delete accounts.
  The last Owner account cannot be demoted or deleted, and every change is audited.
- Automatic retention: finished operations, compute jobs, automation runs, terminal
  history, update scans, game server events, resolved alerts, and metrics are purged on a
  configurable schedule. Audit events are never deleted because deletion would break the
  tamper-evident hash chain.
- Agent-version policy: nodes reporting a version different from the Controller raise an
  informational alert that resolves automatically after an upgrade.
- System diagnostics: version, uptime, device counts, open alerts, queued operations, and
  retention settings for administrators.
- Desktop user management in Settings and the existing hardened surfaces for every
  earlier phase.

## Verification

- Release build: **PASS**, zero compiler warnings and zero errors.
- Automated tests: **138 PASS** across unit, security, Agent, integration, and
  Controller-to-Agent end-to-end suites.
- The installer pipeline builds, smoke-installs, repairs, verifies the payload, and
  publishes through GitHub Actions from main.

## Release artifacts

- Tag: `v0.10.0`
- Installer: `Veltrix-Control-Setup.exe`
- Checksum manifest: `SHA256SUMS.txt`
- Archive: `Veltrix-Control-Windows-x64.zip`
- Version file and changelog are included in the release payload.

## Known limitations (Phase 10 remainder)

- **PostgreSQL deployment option is not implemented.** The platform remains SQLite-only
  for small single-Controller deployments.
- **Signed self-update and staged rollout are not implemented.** Agent updates are manual
  installer upgrades today.
- **Enterprise identity integration and complete session policies are not implemented.**
  Local accounts and cookie sessions remain the authentication model.
- **High availability, disaster recovery, and 1,000-node capacity qualification are not
  implemented or measured.** Published scale guidance is a design target, not a verified
  result.
- **Load/soak testing has not been performed** in this environment.
- Binaries are not Authenticode-signed because no organization signing certificate was
  supplied; Windows may display an unknown-publisher warning.