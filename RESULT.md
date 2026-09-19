# Veltrix-Control v0.7.0 result report

## Status

Phase 7 is implemented: a resource-aware compute scheduler places bounded jobs on eligible
nodes with per-node workload policies, retries, cancellation, and full audit history.
Phases 1-6 remain intact.

## Delivered workflows

- Compute job queue with requirements (CPU, memory, disk, optional device target), command,
  priority, timeout, attempts, and explicit confirmation.
- Scheduler placement by priority and available capacity after reservations and running
  jobs, with per-node Idle, Server, Compute, Gaming, and Maintenance policies.
- Job lifecycle: queued, running, succeeded, failed, cancelled, and timed-out, with
  automatic retry and process-tree cancellation.
- Agent execution of approved .exe jobs on a dedicated worker, one job per node, bounded
  output, and timeout enforcement.
- GPU jobs are rejected with an explicit capability message.
- Desktop Compute workspace with policy editor, job queue, and create/cancel actions.

## Verification

- Release build: **PASS**, zero compiler warnings and zero errors.
- Automated tests: **119 PASS** across unit, security, Agent, integration, and
  Controller-to-Agent end-to-end suites.
- New coverage: scheduler assignment end to end, Gaming-mode blocking, GPU refusal,
  queued-job cancellation, invalid job rejection, unsafe path rejection, fixture
  execution with exit codes, timeout reporting, and unknown-job cancellation.

## Release artifacts

- Tag: `v0.7.0`
- Installer: `Veltrix-Control-Setup.exe`
- Checksum manifest: `SHA256SUMS.txt`
- Archive: `Veltrix-Control-Windows-x64.zip`
- Version file and changelog are included in the release payload.

## Known limitations

- Capacity is enforced by scheduler reservation, not by operating-system quotas.
- GPU scheduling awaits node GPU capability reporting.
- Binaries are not Authenticode-signed because no organization signing certificate was
  supplied; Windows may display an unknown-publisher warning.
- Game-server adapters and integrations remain planned phases.