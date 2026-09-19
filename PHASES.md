# Veltrix-Control delivery plan

This plan deliberately ships a small number of complete workflows at a time. Every
phase ends in an independently installable Windows release that can be demonstrated,
upgraded, rolled back, and tested without relying on unfinished work from the next
phase.

Current state: **Phase 9 implemented** for `v0.9.0`; publication follows the same clean
build, installer lifecycle, pull-request, and tag gates as earlier phases.

## Release contract for every phase

A phase is complete only when all of these gates are green:

- The release installs from one genuine `Veltrix-Control-Setup.exe` with Controller, Managed
  Node, and combined role choices.
- Clean install, repair, uninstall, and—once an earlier release exists—upgrade from the
  previous release are exercised on `windows-latest`; user data survives repair and upgrade.
- Unit, integration, security, and Controller-to-Agent end-to-end suites pass with no
  skipped release-critical tests.
- A real packaged Controller and Agent complete the phase's primary workflow. Simulator-
  only success is not enough for Windows-specific behavior.
- Authentication, permission checks, input validation, audit events, cancellation, and
  understandable error states cover every new administrative action.
- Database changes use migrations and are tested against a copy of the previous phase's
  data.
- The management UI has loading, empty, offline, denied, error, and confirmation states,
  plus keyboard and contrast checks.
- Release binaries are self-contained `win-x64` PE files. CI publishes the installer,
  release archive, version file, changelog, test results, and SHA-256 manifest.
- Dependency vulnerability scanning, secret scanning, and a review of changed trust
  boundaries are complete.
- `README.md`, relevant documents, `CHANGELOG.md`, and `RESULT.md` describe verified
  behavior only. `main` remains releasable.

## Phase 1 — Secure fleet foundation (v0.1.1)

User outcome: an administrator can install a Controller and an explicitly authorized
Windows node, enroll it securely, see live health, browse an approved root, request a
guarded restart or shutdown, and verify the audit trail.

Included:

- Modular Controller, Agent, shared contracts, infrastructure, and simulator projects.
- First-run Owner creation, secure sessions, Owner/Administrator/Operator/Viewer roles,
  and permission enforcement.
- Expiring, single-use, revocable enrollment codes and persistent device identity.
- Certificate-pinned HTTPS, signed heartbeats, clock-skew limits, and replay rejection.
- Windows inventory plus CPU, RAM, disk, uptime, agent version, and online/offline state.
- SignalR dashboard updates, health score, device detail, read-only managed-root browser,
  guarded power queue, and hash-chained audit history.
- One role-selecting installer, development simulator, Windows CI, and release packaging.

Phase-specific acceptance:

- A clean Windows runner installs the combined role; both services reach `Running`; the
  local Agent enrolls once and appears online without manual bootstrap steps.
- Invalid, expired, revoked, reused, and replayed credentials are rejected.
- A real TLS handshake succeeds with the generated Controller certificate.
- Traversal outside the managed root and unconfirmed power requests are rejected.
- The simulator completes enrollment, heartbeat, file listing, and operation-result flow.

## Phase 2 — Fleet observability (v0.2.0)

User outcome: operators can diagnose a Windows fleet from one live view without making
changes to a node.

Included:

- Native Windows control center as the primary interface; web UI is a recovery fallback.
- First-run setup, login/logout, saved Controller connection, secure URL validation,
  manual refresh, 5–60 second automatic refresh, and connection status.
- Fleet cards for online devices, average CPU, memory use, and nodes needing attention.
- Searchable/filterable/sortable device directory and profiles with inventory, telemetry,
  disk capacity, health, heartbeat, agent version, and copyable device ID.
- Read-only remote running-process, Windows-service, installed-software, and network-adapter
  inventories, with searchable results and bounded collection sizes.
- In-app enrollment code creation/copy/revoke, managed-root navigation, guarded power
  actions, audit search/integrity verification/CSV export, and role-aware controls.
- System light/dark adaptation, semantic design tokens, resizable layouts, keyboard
  shortcuts, accessible names, busy states, understandable errors, and destructive-action
  confirmations.
- Native launcher, self-contained packaging, and an automatic browser-fallback offer only
  when the app cannot start.

Phase-specific acceptance:

- Agent tests execute all four diagnostic collectors and verify typed JSON output.
- Native app and launcher compile without warnings and publish as self-contained Windows
  PE executables.
- A packaged combined-role install starts both services, enrolls its Agent, preserves
  state across repair, and includes the desktop and launcher binaries.
- Upgrade from v0.1 uses the stable installer identity and retains Controller/Agent data.

## Phase 3 — Files, transfers, and editor (v0.3.0)

User outcome: authorized operators can safely maintain files inside explicitly configured
roots, including large transfers and configuration editing.

Included:

- Create, rename, copy, move, delete, search, directory sizing, ZIP, and extraction.
- Chunked upload/download with progress, cancellation, retry, bounded resource use, and
  resumable transfer where the endpoint supports it.
- Text/code editor with syntax highlighting, search/replace, diff, atomic save, backups,
  unsaved-change protection, and bounded file history.
- Permission display plus clear handling for locked, denied, reparse-point, and changing
  files.

Phase-specific acceptance:

- Security tests cover traversal, alternate separators, UNC/device paths, reparse points,
  archive escape, race-sensitive path changes, oversized requests, and malformed chunks.
- Transfer tests verify hashes after interruption/resume and cancellation leaves no file
  reported as complete.
- Editor tests prove atomic replacement, conflict detection, recovery backup, and exact
  preservation of unchanged encodings where supported.
- Upgrade from v0.2 preserves managed-root policy and all existing state.

## Phase 4 — Controlled Windows administration (v0.4.0)

User outcome: administrators can perform routine process, service, power, and terminal
work remotely with explicit policy and a complete audit trail.

Included:

- Start/stop/restart processes, priority and affinity changes, plus protected critical-
  process policy.
- Start/stop/restart services, startup type, dependencies, recovery details, and sensitive-
  service confirmation policy.
- Scheduled shutdown/restart, logoff, sleep, hibernate, and Wake-on-LAN capability checks.
- Audited PowerShell and CMD sessions with streaming output, working directory, timeout,
  cancellation, bounded buffers, and multiple-session lifecycle management.
- Operation queue states: Queued, Running, Succeeded, Failed, Cancelled, and TimedOut.

Phase-specific acceptance:

- Every mutation is denied without the exact permission and local Agent opt-in.
- Critical-process/service test fixtures cannot be stopped, even through malformed or
  stale requests.
- Terminal tests cover disconnect, cancellation, output limits, command timeout, session
  cleanup, and audit records without logging secrets.
- End-to-end tests use harmless fixture processes/services; no test targets a production
  Windows component.

## Phase 5 — Software and Windows Update lifecycle (v0.5.0)

User outcome: administrators can deploy approved software and Windows updates in visible,
recoverable maintenance workflows.

Included:

- WinGet, MSI, and approved EXE adapters with inventory, install, uninstall, upgrade,
  progress, logs, deployment groups, and restart requirements.
- Windows Update scan, approval, installation, history, maintenance windows, and reboot
  coordination without disabling Windows security mechanisms.
- Package allowlists, checksum/signature policy, staged deployment, concurrency limits,
  retry, cancellation, and per-device results.

Phase-specific acceptance:

- Adapter tests use signed fixture packages and mocked catalogs; arbitrary URLs and
  unapproved commands are rejected.
- A staged deployment can stop after a canary failure and never reports partial success as
  complete.
- Update tests cover no-updates, pending reboot, failed update, maintenance-window, and
  offline-node cases.
- Upgrade from v0.4 preserves queued operations and resumes only explicitly resumable work.

## Phase 6 — Backups, alerts, and automation (v0.6.0)

User outcome: the platform can protect selected data and react to health conditions while
remaining predictable and operator-controlled.

Included:

- Scheduled folder/application/game-data backups with compression, retention, restore,
  progress, history, and integrity verification.
- Info/Warning/Critical alerts, acknowledgement, history, deduplication, and notification
  adapter boundary.
- Rules with triggers, conditions, actions, schedules, enable/disable, dry-run, history,
  cooldowns, recursion limits, and maintenance suppression.

Phase-specific acceptance:

- Restore tests compare file hashes and metadata from verified backup fixtures.
- Interrupted/corrupt backups remain failed and are never eligible for automatic retention
  cleanup as successful recovery points.
- Deterministic rule tests cover cooldowns, duplicate events, cycles, retries, and an
  enforced maximum action depth.
- A full scenario detects low disk, emits one alert, runs one approved action, and records
  the entire chain in audit history.

## Phase 7 — Compute scheduling (v0.7.0)

User outcome: administrators can queue bounded, explicit jobs and have Veltrix-Control choose a
suitable authorized worker without pretending multiple PCs are one machine.

Included:

- Resource pools and CPU, RAM, disk, and GPU capability/reservation models.
- Gaming, Server, Compute, Maintenance, and Idle policies.
- Job queue, priority, placement, cancellation, retry, timeout, logs, limits, node health,
  and workload history.
- Scheduler decisions that account for available capacity, requirements, load, policy,
  and unhealthy/offline nodes.

Phase-specific acceptance:

- Deterministic and property-based scheduler tests prove no overcommit and stable priority
  behavior across competing jobs.
- A job is requeued or failed according to policy when its node disconnects; capacity is
  released exactly once.
- Gaming/Maintenance policy prevents incompatible placement and preemption is explicit.
- End-to-end fixture jobs are harmless, resource-bounded, cancellable, and fully audited.

## Phase 8 — Game server platform (v0.8.0)

User outcome: operators can install and run isolated game-server instances with reliable
console, configuration, updates, schedules, and backups.

Included:

- Versioned adapter contract and first production adapter for Minecraft Java.
- Instance creation, approved download verification, ports, environment, resource policy,
  start/stop/restart, console, logs, player status, crash recovery, updates, and schedules.
- Configuration editing through the Phase 3 file boundary and verified backups through
  Phase 6.
- Additional adapters added only after the common lifecycle passes for Terraria, Valheim,
  Minecraft Bedrock, and CS2 fixtures.

Phase-specific acceptance:

- Adapter contract tests cover install, first start, normal stop, crash, update, backup,
  restore, port conflict, and invalid configuration.
- Downloads require an allowlisted source and checksum/signature verification.
- Crash-loop protection stops repeated restarts and raises one actionable alert.
- Upgrade from v0.7 leaves existing compute jobs and non-game workloads unaffected.

## Phase 9 — Optional integrations (v0.9.0)

User outcome: existing Pterodactyl and Docker environments can be observed and controlled
without becoming dependencies of Veltrix-Control's core workflows.

Included:

- Pterodactyl Panel API adapter for supported nodes, servers, allocations, resources,
  lifecycle actions, console/log links, backups, and schedules.
- Docker adapter for engine health, containers, images, volumes, networks, logs, resource
  use, health checks, and Compose projects.
- Per-integration credentials, least-privilege validation, rate limits, circuit breakers,
  health state, disable/uninstall behavior, and versioned capability discovery.

Phase-specific acceptance:

- Contract tests run against pinned disposable test environments and recorded official-API
  fixtures; unsupported API behavior is surfaced clearly.
- Integration outages and credential failures cannot degrade enrollment, telemetry, files,
  backups, or native game-server management.
- Secrets are encrypted at rest, redacted from logs/audit metadata, and removed when an
  integration is deleted.

## Phase 10 — Production hardening and GA (v1.0.0)

User outcome: a supported deployment can operate reliably beyond a single small Controller
and receive verified staged updates.

Included:

- PostgreSQL deployment option and migration tooling while retaining supported SQLite
  small-install mode.
- Signed Controller/Agent packages, verified self-update, staged rollout, health gates,
  rollback, and fleet version policy.
- Enterprise identity integration, complete role/user administration, session controls,
  external audit export, and documented certificate lifecycle.
- High-availability design where justified, disaster recovery, log retention/rotation,
  performance budgets, and operations runbooks.
- Accessibility, localization readiness, support diagnostics, privacy review, threat-model
  refresh, and third-party security assessment remediation.

Phase-specific acceptance:

- Upgrade tests cover every supported prior minor release, failed migrations, failed agent
  rollout, rollback, backup/restore, and certificate renewal.
- Load tests prove published targets for 10 and 100 real/simulated nodes; a separate 1,000-
  node capacity test documents bottlenecks and safe operating limits.
- A release candidate completes a sustained soak with no lost operations, identity churn,
  unbounded queues, or unexplained resource growth.
- GA is tagged only after Windows CI, installer artifact inspection, security review, and
  a clean-machine acceptance run all pass.

## Branch and promotion model

- One feature branch and focused pull request per acceptance slice; incomplete work remains
  behind disabled modules or outside the release branch.
- Merge only when its slice is green and the current phase remains fully usable.
- Tag phase releases from `main` as `v0.x.0`; use patch releases only for compatible fixes.
- Begin the next phase from the tagged, verified release rather than from an unverified
  collection of parallel features.
