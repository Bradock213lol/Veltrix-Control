# Changelog

All notable changes follow semantic versioning.

## [0.10.8] - 2026-09-19

### Added

- Device tags and favorites: add and remove tags per device, star devices, search by tag,
  and a Tags column in the device directory. Tag changes are permission-checked
  (Administrator) and audited; favorites are available to any signed-in role.
- Alert notifications: alerts can be delivered to an HTTPS webhook with an
  `X-Veltrix-Signature` HMAC-SHA256 header when a secret is configured. Settings include a
  Save and Send test action; the secret is encrypted with the Controller-local AES key.
- Chocolatey as a package source for software deployment, alongside WinGet, MSI, and EXE,
  in the agent, Controller validation, and the package registration window.

### Changed

- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.10.8.

## [0.10.7] - 2026-09-19 - 2026-09-19

### Added

- The delivered Carbon interface now runs 1:1 in the product. It is served by the
  Controller at `/carbon/` and hosted in a WebView2 **Console** window inside the native
  app, with the signed-in session injected so the kit's own HTML, CSS, JavaScript, icons,
  motion, and layout are used unchanged.
- A live bridge (`assets/veltrix.js`) replaces the kit's sample adapter with real data:
  devices and telemetry refresh from the API, enrollment codes are created for real, the
  activity list is built from the audit trail, and the workspace name, user, initials,
  build, controller endpoint, and device count come from the session. The kit still opens
  standalone with its bundled sample content when the bridge is absent.
- Device rows hide the sample IP annotation when the platform has no verified IP source.

### Changed

- The launcher's browser fallback opens the Carbon console instead of the legacy page.

### Changed

- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.10.7.

## [0.10.6] - 2026-09-19 - 2026-09-19

### Changed

- Adopted the delivered Carbon interface language across the desktop app: graphite
  surfaces, mint accent, fine borders, flat backgrounds (no gradients or decorative glow),
  and the compact 210 px navigation column with a 54 px top bar and 29 px status bar.
- New shell: VELTRIX monogram and wordmark, workspace card, preference section, account
  card with initials and icon sign-out, build chip, breadcrumb, global device search with
  a Ctrl+F hint, controller status pill, and a footer showing the real Controller address,
  fleet online count, and TLS state.
- Device tables now show a status dot with label and a health bar with `N / 100`; memory
  keeps `used / total` plus free RAM.
- Primary buttons use flat mint with dark text and a restrained low-opacity glow; control
  corners are 6 px and panels 10 px, matching the kit.
- Content transitions use the kit's 250 ms opacity + 5 px lift and remain disabled when
  Windows animations are off. The Windows light appearance no longer swaps the palette;
  Carbon is a single graphite console theme.
- The Inno Setup installer now renders the Carbon theme: graphite wizard pages, mint page
  names and Next button, and role selection presented as cards with a mint selection edge.
  All installation, service, firewall, repair, upgrade, and uninstall behavior is unchanged.

### Changed

- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.10.6.

## [0.10.5] - 2026-09-19

### Added

- Repository documentation: a rewritten README with a feature-status table, a keyboard
  shortcut reference, and an explicit not-implemented section, plus `docs/roadmap.md`
  with a complete implemented / not-implemented gap analysis.
- In-app **Settings → Keyboard shortcuts** card documenting every accelerator.
- `Ctrl+1 … Ctrl+9` navigate the nine workspace sections; `Ctrl+R` refreshes; the file
  editor gained `Ctrl+S` save and `Ctrl+F` find/replace.

### Changed

- Visual layer refined in an instrument-console direction: aurora background glow, violet
  gradient accent and primary buttons, gradient brand mark, 12 px panel radius, and thin
  utilization bars under the Overview metrics so values and proportions read together.
- The browser fallback is now documented as a recovery surface frozen at the v0.2 feature
  set, not a parallel UI.

### Changed

- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.10.5.

## [0.10.4] - 2026-09-19

### Changed

- Memory telemetry now always reads as used / total with free capacity: Overview shows
  e.g. `6.3 GB / 32 GB` with `25.7 GB free across online nodes`, the device directory has
  Memory used / total and Free RAM columns, and the device profile shows the same values
  with a detail caption.
- Metric typography uses tabular figures for stable alignment; labels, captions, and
  secondary text follow one hierarchy, and secondary text contrast was raised to meet
  accessibility targets (secondary ~8:1, subtle ~4.9:1 on surfaces).
- Device profile redesigned: metric cards carry captions (health band, logical processors,
  memory detail, uptime), and the storage table now shows used / total, free, and used
  percent per drive.
- Navigation is role-aware: sections the signed-in role cannot use are disabled with an
  explanation tooltip instead of failing later.
- One primary action per toolbar (Create backup, Register package, Deploy selected,
  New rule, New server, New integration) to clarify hierarchy.
- Motion: a 150 ms content fade on navigation and a pulsing refresh indicator, both
  automatically disabled when Windows animations are turned off.

### Fixed

- Health now reads as `82/100` in tables and offline devices report "Offline" instead of a
  bare zero.
- Empty states added for the Overview, Devices, Files, Alerts, and Audit grids so blank
  tables explain themselves.

### Changed

- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.10.4.

## [0.10.3] - 2026-09-19

### Changed

- Complete desktop retheme with original control templates replacing default WPF chrome
  that clashed with the product theme: buttons (hover/pressed/disabled states), text and
  password fields (hover and focus rings), combo boxes with themed drop-downs, checkboxes,
  tabs with underline selection, data grids (headers, row hover, selection), slim
  scrollbars, list boxes, progress bars, tooltips, and focus visuals.
- Native window title bars now match the active light or dark theme on Windows 10/11.
- Sidebar navigation now uses icons with active-state highlighting; secondary actions use
  a link style; the product version is shown in the sidebar.
- Consistent typography, spacing, and muted-label hierarchy across every screen.

### Fixed

- The Windows Update "Install" column is now editable; the grid's read-only mode had
  blocked selecting updates.

### Changed

- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.10.3.

## [0.10.2] - 2026-09-19

### Added

- Recovery Administrator account: after first-run setup the Controller automatically
  creates an `admin` account (password `admin!` by default) so a forgotten Owner password
  can never lock anyone out. It is created only when missing, is configurable under
  `Controller:RecoveryAccount*`, and should be changed or deleted once Owner access is
  verified. The Controller logs a warning when it is created.

### Fixed

- The desktop window background now follows the dark/light theme on every window instead
  of leaving a large white area around the content.
- Interactive uninstall now removes every product file: services, firewall rule, program
  files, the database with user accounts, certificates, node identities, transfers, and
  desktop settings. Silent uninstall (used by upgrades and repair) still preserves data.

### Changed

- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.10.2.

## [0.10.1] - 2026-09-19

### Fixed

- First-run setup is now detected reliably: the sign-in screen waits for the Controller
  service to start, switches to a dedicated "Create your control plane" form with password
  confirmation and guidance when no accounts exist, and automatically offers setup when a
  sign-in attempt finds no accounts.
- Login validation now returns specific messages ("Enter your username." / "Enter your
  password.") instead of a generic failure.
- Added an account recovery path: `--reset-owner <username>` with `VELTRIX_OWNER_PASSWORD`
  resets a password from an elevated prompt on the Controller machine without touching any
  other data, and records an audit event.

### Added

- Getting-started checklist on the Overview tab until the first device is enrolled.
- Setup/status, login validation, and first-run flow tests.

### Changed

- Bumped all product, health, simulator, test, installer, and web-fallback versions to 0.10.1.

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
