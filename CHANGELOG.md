# Changelog

All notable changes follow semantic versioning.

## [0.10.0] - 2026-09-19

### Added

- Complete user administration: list, create, role changes, password reset, and deletion with last-Owner protection and audit events.
- Automatic retention of terminal history, finished operations, compute jobs, automation runs, update scans, game server events, resolved alerts, and metrics.
- Agent-version policy alert when a node reports a version different from the Controller.
- Administrator system diagnostics endpoint with version, uptime, device counts, open alerts, queued operations, and retention settings.
- End-to-end coverage for user administration, last-Owner protection, invalid input, permission denial, and system diagnostics.

### Changed

- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.10.0.

### Known remaining work

- PostgreSQL deployment option, signed self-update and staged rollout, enterprise identity integration, high availability, and load/soak qualification are not yet implemented and are tracked in `PHASES.md`.

## [0.9.0] - 2026-09-19

### Added

- Optional Pterodactyl integration for nodes and servers with health checks, resource listing, confirmed power actions, and clear API limitations.
- Optional Docker integration for containers, images, and volumes with health checks, lifecycle actions, and bounded container logs.
- AES-GCM credential protection with a Controller-local key; credentials are never returned to clients and are removed with the integration.
- A dedicated `Veltrix-Control.Integrations` module so integrations never become dependencies of core workflows.
- Desktop Integrations workspace with creation, health, resources, actions, logs, and deletion.
- Coverage for credential round trips, tamper rejection, key reuse, endpoint validation, health reporting, confirmation requirements, and permissions.

### Changed

- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.9.0.

## [0.8.0] - 2026-09-19

### Added

- Game server platform with a versioned adapter contract and a production Minecraft Java adapter.
- Provisioning that downloads the official server jar, verifies the published SHA-1 from the Mojang manifest, accepts the EULA explicitly, and writes safe default server.properties.
- Instance lifecycle: create (provision), start, stop, update, console input, and streamed console output with bounded buffers.
- Crash detection with automatic restart when enabled, crash-loop protection that stops repeated restarts and raises one actionable alert, and complete instance event history.
- Desktop Game servers workspace with creation, lifecycle actions, and a live console window.
- Agent and end-to-end coverage for adapter validation, process management, lifecycle operations, automatic restart, and permissions.

### Changed

- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.8.0.

## [0.7.0] - 2026-09-19

### Added

- Resource-aware compute scheduler that places bounded jobs on eligible online nodes by capacity, priority, and per-node workload policy.
- Compute policies per node: Idle, Server, Compute, Gaming, and Maintenance modes with reserved CPU, memory, and disk.
- Job queue with priorities, bounded timeouts and attempts, automatic retry, cancellation that kills the running process, and complete run history.
- Agent compute execution for approved `.exe` fixtures with bounded output, process-tree termination on timeout/cancel, and one concurrent job per node.
- GPU requirements are refused with a clear message instead of pretending unsupported hardware.
- Desktop Compute workspace: policy editor, job queue, create/cancel actions, and live states.

### Changed

- Compute jobs execute on a dedicated agent worker so long jobs no longer block other operations.
- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.7.0.

## [0.6.0] - 2026-09-19

### Added

- Verified folder/file backups with zip archives, SHA-256 integrity, automatic pre-restore checks, retention-based expiry that queues a safe archive cleanup operation, and restore to a new destination only.
- Alert engine with offline, CPU, memory, and low-disk detection, deduplication while an alert is open, automatic resolution when conditions clear, acknowledgement, and run history.
- Automation rules with schedule, alert, device-online, and device-offline triggers; optional device-name conditions; allow-listed actions (create alert, queue restart/shutdown/scan); cooldowns; recursion protection; and complete run history.
- Desktop Operations workspace with Backups, Alerts, and Automation tabs.
- End-to-end and Agent coverage for backup round trips, escapes, alert acknowledgement, automation lifecycle and validation, and permission enforcement.

### Changed

- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.6.0.

## [0.5.0] - 2026-09-19

### Added

- Software package registry with WinGet catalog identifiers or checksum-pinned HTTPS MSI/EXE downloads.
- Multi-device deployments with install, uninstall, and upgrade actions, per-device operation
  tracking, progress counts, cancellation, and audit events.
- Agent software execution with silent install support, restart-required handling, bounded
  output, and mandatory SHA-256 verification before any downloaded installer runs.
- Windows Update scan and install through supported Windows APIs, with stored scan history,
  selective installation, reboot-required reporting, and Administrator-only access.
- Desktop Deployment workspace covering packages, deployments, per-device results, scans,
  and update installation.
- Background agent operation execution so long installs and updates no longer delay
  heartbeats, with persisted result delivery and automatic retry.

### Changed

- Agent operations now run on a dedicated background worker instead of the heartbeat cycle.
- Enum values serialize by name across stores and contracts for interoperable payloads.
- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.5.0.

## [0.4.0] - 2026-09-19

### Added

- Remote process control: start an approved executable, stop a process, and change process
  priority, with identity re-verification and a protected-process policy.
- Windows service control: start, stop, and change startup type with a protected-service
  policy and explicit confirmation for stop actions.
- Scheduled restart and shutdown, logoff, sleep, hibernation, and Wake-on-LAN for nodes
  that report a MAC address.
- Audited PowerShell and CMD terminal sessions with pooled agents, bounded buffers,
  sequence-based streaming, command length limits, multiple sessions per device, and
  session lifecycle tracking.
- A dedicated Administration surface in the desktop app: processes, services, and terminal
  with device selection, confirmations, and clear errors.
- Unit, Agent, and end-to-end coverage for protection policies, local opt-in denial,
  terminal lifecycle and streaming, wake behavior, and role enforcement.

### Changed

- Every administrative operation requires its exact permission, explicit confirmation,
  the node's local policy opt-in, and produces audit events.
- Home inventories now report the primary MAC address to support Wake-on-LAN.
- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.4.0.

## [0.3.0] - 2026-09-19

### Added

- Write-capable managed file operations: create folder, create file, rename, move, copy,
  delete, wildcard search, folder sizing, ZIP creation, and ZIP extraction.
- Chunked, resumable file transfers between the desktop app and managed nodes, with
  progress, cancellation, sequential-offset enforcement, size limits, and SHA-256
  verification before a transfer is reported complete.
- Built-in text/code editor with line numbers, find/replace, atomic saves, automatic
  pre-save backups, bounded per-file history, restore, and a line-level comparison view.
- Hardened path policy: managed-root protection, UNC/device path rejection, alternate
  data stream rejection, null-byte rejection, reparse-point escape detection, and
  archive entry (zip-slip) validation.
- Typed permission taxonomy (`device.processes`, `device.services`, `device.terminal`,
  `device.software`, `device.updates`, `device.backup`, `device.compute`, `game.manage`,
  `docker.manage`, `integration.manage`) in preparation for later phases.
- Agent test coverage for the file engine and security test coverage for path escapes,
  archive escapes, and transfer authorization.

### Changed

- Operation arguments are validated per operation kind before an operation is queued.
- The permission mapping for operations is centralized in `RolePermissions.PermissionFor`.
- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.3.0.

## [0.2.0] - 2026-09-13

### Added

- Native WPF Windows control center as the primary interface, including setup, sign-in,
  overview metrics, device directory, responsive navigation, settings, and shortcuts.
- Device search/status filters, detailed inventory, disk capacity, identifier copy, and
  guarded restart/shutdown actions.
- Read-only remote process, Windows service, installed-software, and network-adapter
  diagnostics with searchable result tables.
- In-app managed-root file navigation, enrollment code lifecycle, audit search, audit-chain
  verification, and CSV export.
- System light/dark theme adaptation, accessible semantic colors, clear busy/error states,
  saved refresh preferences, and secure Controller URL validation.
- Native launcher that offers the browser recovery surface only if the desktop app is
  missing or exits during startup.
- Agent diagnostic tests plus installer checks for the native desktop and launcher files.

### Changed

- The installer and its shortcuts now launch the native app instead of a website.
- Read-only diagnostic operations have an explicit role permission and use typed,
  camel-case JSON contracts.
- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.2.0.

## [0.1.1] - 2026-09-13

### Changed

- Completed the Veltrix-Control rename across the dashboard, installer, Windows services,
  application data paths, release assets, solution, projects, namespaces, tests, and docs.
- Renamed the installer to `Veltrix-Control-Setup.exe` and the release bundle to
  `Veltrix-Control-Windows-x64.zip`.
- Updated the README download links to follow the current stable release automatically.

### Fixed

- Removed every remaining reference to the provisional product name from tracked content
  and repository paths.

## [0.1.0] - 2026-09-12

### Added

- Secure first-run Controller and premium live fleet dashboard.
- Expiring single-use node enrollment with ECDSA device identities.
- Signed heartbeats, replay protection, CPU/RAM/disk telemetry, and health scores.
- Guarded restart, shutdown, and managed-root file listing operations.
- Hash-chained audit history and integrity verification.
- Windows Agent service, safe node simulator, and role-selecting installer.
- Windows build workflow plus unit, integration, security, and end-to-end tests.

### Changed

- Expanded the roadmap into independently installable and upgrade-tested releases from
  fleet observability through production hardening.
- Updated GitHub Actions to maintained Node.js 24-based action versions.
- Added a tag-gated GitHub release workflow that rebuilds, tests, smoke-installs, verifies,
  and publishes the installer and checksum assets only from `main`.
- Adopted the MIT License and aligned public repository branding with Veltrix-Control.

### Fixed

- Load the Controller TLS certificate into the Windows service account key set so Schannel
  can complete node HTTPS handshakes.
- Added a real TLS handshake regression test and made dependency/JavaScript checks part of
  the release build.
- Allow the release workflow's first publication to distinguish an expected missing
  release from a fatal GitHub CLI error.
