# NexaGrid delivery phases

NexaGrid is delivered as independently usable, testable releases. A phase is not
complete until its build, automated tests, documentation, and release packaging
are green.

## Phase 1 — Secure fleet foundation (v0.1.0)

Outcome: an administrator can install a Controller and/or Managed Node from one
installer, enroll an explicitly authorized Windows device, see live inventory and
CPU/RAM health, browse an approved filesystem root, queue guarded restart/shutdown
operations, and inspect the audit trail. A simulator makes the complete workflow
testable on one PC.

Release gate:

- Controller starts and exposes the authenticated management interface.
- One-time, expiring enrollment codes create persistent device credentials.
- Signed heartbeats reject expired, invalid, and replayed requests.
- Online/offline state and hardware telemetry update without manual refresh.
- Role and permission checks protect sensitive operations.
- Power operations require explicit confirmation and agent opt-in.
- File browsing is confined to a configured root and rejects traversal.
- Unit, integration, security, and end-to-end tests pass.
- Self-contained Windows x64 binaries and a genuine single installer EXE build.

## Phase 2 — Windows administration (v0.2.0)

Outcome: the Phase 1 product remains usable and gains process and service control,
an audited PowerShell/CMD terminal, a safe text editor, software deployment,
Windows Update workflows, and network/storage diagnostics.

Release gate: every remote operation is permission checked, audited, cancellable
where practical, and covered by tests; destructive operations have confirmations;
the installer upgrades v0.1 without losing state.

## Phase 3 — Workload orchestration (v0.3.0)

Outcome: the fleet can schedule bounded compute jobs, manage game-server instances,
run verified backups, evaluate guarded automation rules, and connect to optional
Pterodactyl and Docker adapters.

Release gate: resource reservations are enforced, workloads recover predictably,
backups are integrity-verified, automation loops are prevented, integrations can be
disabled independently, and the installer upgrades v0.2 safely.

## Later production hardening (v1.0)

PostgreSQL deployment, staged signed agent updates, high-availability Controller,
large-fleet performance validation, enterprise identity integration, and third-party
security assessment are intentionally deferred until the three product phases have
proved the core model.
