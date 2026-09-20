# Veltrix-Control roadmap and gap analysis

This document is the honest status of the platform. Anything not listed under **Implemented**
is not implemented, regardless of how it may appear in earlier planning documents.

Last updated: v0.10.5.

## Implemented and verified

### Foundation and security
- One role-selecting installer (Controller, Managed Node, combined) with repair, upgrade,
  and full interactive uninstall.
- First-run Owner setup, recovery Administrator account, `--reset-owner` CLI recovery,
  four roles, secure cookie sessions, per-action permissions.
- Single-use expiring enrollment codes; per-device ECDSA P-256 identity stored under DPAPI;
  signed, time-bounded, replay-resistant agent transport over pinned TLS.
- Closed operation enum, typed validation on Controller and node, confirmations, operation
  IDs, node-local policy switches, protected process/service policy.
- Hash-chained audit log with integrity verification and CSV export; retention for
  operational data (audit records are never deleted).

### Monitoring and device management
- Hardware inventory, CPU / memory / disk / uptime telemetry, health scoring, live
  Overview and Devices views, search, filters, sorting, role-aware navigation.
- Processes: start `.exe`, stop, change priority. Services: start, stop, startup type.
- Power: restart, shutdown, schedule, logoff, sleep, hibernate, Wake-on-LAN.
- Audited PowerShell and CMD terminal sessions with streaming output and bounded buffers.

### Files and transfers
- Managed-root browsing plus create, rename, move, copy, delete, search, folder size, ZIP
  create/extract, per-file backup history and restore, diff view, line-numbered editor.
- Chunked uploads/downloads with progress, cancellation, sequential offsets, and SHA-256
  verification; hardened path policy (traversal, UNC/device paths, ADS, reparse points,
  zip-slip).

### Software, updates, protection
- Approved package registry (WinGet, checksum-pinned HTTPS MSI/EXE), multi-device
  deployments with per-device results, cancellation, restart handling.
- Windows Update scan and selective install with reboot reporting.
- Verified zip backups with retention, integrity verification, and safe restore.
- Alert engine (offline, CPU, memory, disk, agent version) with dedupe, auto-resolve, and
  acknowledgement; automation rules with triggers, conditions, cooldowns, allow-listed
  actions, and run history.

### Compute and workloads
- Priority compute job queue, retries, cancellation, bounded output, one job per node.
- Per-node Idle / Server / Compute / Gaming / Maintenance policies with CPU, memory, and
  disk reservations.
- Minecraft Java game servers: provisioning with published checksum verification, lifecycle,
  live console, crash restart, crash-loop alerts, backups through the backup module.

### Integrations and administration
- Optional Pterodactyl (nodes, servers, power actions) and Docker (containers, images,
  volumes, actions, logs) adapters with AES-GCM encrypted credentials.
- User administration with last-Owner protection, system diagnostics, agent-version policy.
- 156 automated tests plus installer lifecycle testing on Windows CI.

## Not implemented yet

### Platform and release engineering
- **PostgreSQL deployment option.** SQLite only; no second provider, no migration tooling
  between engines, no PostgreSQL-backed scale-out path.
- **Code signing.** Binaries and the installer are unsigned; Windows shows unknown-publisher
  warnings.
- **Signed self-update and staged rollout.** Agent/Controller upgrades are manual installer
  runs; there is no verified update feed, canary ring, health gate, or rollback automation.
- **High availability / disaster recovery.** Single Controller; no replication, failover, or
  documented recovery point objectives.
- **Capacity qualification.** No 10 / 100 / 1,000-node load runs, no soak test, no published
  performance budgets.
- **Localization.** UI strings are English-only and hard-coded; no resource extraction.

### Security and identity
- **Enterprise identity.** No Entra ID / OIDC / LDAP or SSO; local accounts only.
- **Session controls.** No MFA, no server-side session revocation list, no per-user API
  tokens for automation.
- **Secrets hardening.** Controller-local AES key and DPAPI are used; no HSM, key rotation,
  or certificate lifecycle automation.

### Device and telemetry depth
- **GPU inventory and GPU scheduling.** Requirements can be expressed but GPU jobs are
  refused with a clear message because nodes do not report GPU capability.
- **Hardware sensors.** No temperature, fan, SMART, battery, or power-state telemetry; disk
  health alerts depend on capacity only.
- **Per-core CPU detail and frequency.** Aggregate CPU percentage only.
- **Network throughput history.** Adapter inventory and counters at snapshot time; no
  bandwidth graphs or per-process network usage.
- **Historical charts.** Metrics are stored but the app shows current values and health, not
  time-series graphs.

### Device organization and operations
- **Device groups, tags, and favorites.** Flat device list with search/filter/sort only.
- **Bulk actions across devices.** Deployments are multi-device; power, files, and
  administration are one device at a time.
- **Device scheduling and maintenance windows.** No calendar for maintenance, no blackout
  periods for automation.
- **Scheduled software/update installation windows.** Updates install on demand only.
- **Process tree, affinity, and CPU controls beyond priority.**
- **Service dependencies, recovery configuration, and interactive service details.**
- **File permissions display, ownership changes, and ACL editing.** Paths are protected but
  ACLs are not shown.
- **File editor syntax highlighting.** Plain text with line numbers, find/replace, history,
  and diff — not a Monaco-class editor.
- **Multi-file search across the managed root from the editor.**

### Software, automation, and alerting
- **Chocolatey adapter.** WinGet, MSI, and EXE only.
- **Staged/canary deployment rings.** No automatic stop on canary failure, no percentage
  rollout.
- **External notifications.** No email, webhook, Teams, Slack, or SMS delivery for alerts.
- **Notification acknowledgement SLAs and escalation policies.**
- **Automation dry-run, simulation, and richer conditions** (time windows, tag/group
  conditions, multi-step actions).

### Backups
- **Encryption of backup archives**, incremental/deduplicated backups, offsite targets, and
  backup verification scheduling. Backups are local zips with checksums.

### Game servers
- **Adapters beyond Minecraft Java** (Bedrock, Terraria, Valheim, CS2). The adapter contract
  exists; only one adapter ships.
- **Player counts, scheduled restarts, and update automation** for game servers.
- **Port conflict detection and automatic port allocation.**

### Integrations
- **Docker Compose projects, per-container resource graphs, engine configuration.**
- **Pterodactyl client API / websocket console**, client keys, and scheduled-task
  management.
- **Circuit breakers and rate-limit budgets per integration** beyond request timeouts.

### Client surfaces
- **The browser fallback is feature-frozen at the v0.2 feature set.** New capabilities are
  desktop-only; the web page is a recovery surface, not a parallel UI.
- **macOS/Linux Controller builds.** The code is mostly portable but only Windows is built
  and tested.
- **Accessibility completeness.** Keyboard navigation is partial, screen-reader labels cover
  the primary flows, high-contrast themes are untested, and text does not scale with the
  system setting beyond DPI.
- **Automated UI tests.** There is no automated UI regression suite for the WPF app.

## Suggested next milestones

1. **Ship quality of life first:** Authenticode signing, device groups/tags, bulk power
   actions, historical charts, and a browser UI parity pass.
2. **Operations:** external alert notifications, maintenance windows, staged software rings,
   and update scheduling.
3. **Scale and durability:** PostgreSQL provider, backup encryption and offsite targets,
   1,000-node load qualification, HA design.
4. **Enterprise:** SSO/OIDC, MFA, session revocation, per-user API tokens, localization.
