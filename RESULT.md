# Veltrix-Control v0.2.0 result report

## Status

Phase 2 is implemented as an app-first, independently installable release candidate.
The native Windows control center is the primary administration surface. The existing
web surface is retained as a recovery fallback and is offered automatically only when
the desktop app is missing or closes during startup.

Publication is tag-gated: the `v0.2.0` release is created only from `main` after the
Windows workflow rebuilds, retests, installs, repairs, verifies, and inspects the payload.

## Delivered workflows

- First-run Owner setup, sign-in/sign-out, saved Controller address, HTTPS validation,
  connection state, manual refresh, automatic refresh, and refresh intervals.
- Fleet cards for online nodes, CPU, memory, and attention; device search, status filter,
  column sorting, device profiles, copyable IDs, inventory, disks, health, and uptime.
- Read-only remote inventories for processes, services, installed software, and network
  adapters; bounded collection and searchable result tables.
- Managed-root navigation, folder traversal, reload and parent navigation.
- Enrollment-code lifetime selection, creation, automatic copy, replacement, and revoke.
- Guarded restart/shutdown queue, role checks, explicit confirmation, simulator protection,
  and the Agent's separate disabled-by-default local power policy.
- Audit load, search, hash-chain verification, and CSV export.
- System light/dark theme, semantic design tokens, resizable windows, keyboard shortcuts,
  accessible control names, busy/error status, and destructive-action warnings.
- Native launcher and Controller-role shortcuts; browser fallback prompt on app startup
  failure; no website is launched during the normal path.

## Architecture and packaging

- Native WPF desktop and launcher, ASP.NET Core Controller Windows Service, Windows Agent
  Service, SQLite persistence, shared contracts, and safe node simulator.
- One self-contained Windows x64 Inno Setup executable for Controller, Managed Node, or
  combined installation; stable installer identity supports repair and in-place upgrades.
- Typed diagnostic contracts and a dedicated `device.diagnostics` role permission.
- No arbitrary remote shell and no arbitrary operation name or executable path.

## Verification

- Release build: **PASS**, zero compiler warnings and zero errors.
- Automated tests: **32 PASS** across Agent diagnostics, unit, security, integration, and
  Controller-to-Agent end-to-end suites.
- New Agent collector tests execute process, service, software, network, and managed-root
  results and verify JSON compatibility.
- Dependency vulnerability scan and JavaScript syntax check: **PASS**.
- Self-contained PE validation: **PASS** for Controller, Agent, Simulator, Desktop, and
  Launcher.
- Installer compile and PE validation: **PASS**; desktop and launcher presence/PE checks
  are part of the isolated installer lifecycle test.
- Desktop startup smoke: **PASS**; the self-contained app remained live after connecting
  to the local Controller endpoint.
- Local installer lifecycle execution was intentionally skipped because this workstation
  already contains persistent `%ProgramData%\Veltrix-Control` data. The test refuses to
  overwrite an existing installation; the release workflow runs it on a clean runner.

## Release artifacts

- Tag: `v0.2.0`
- Installer: `Veltrix-Control-Setup.exe`
- Checksum manifest: `SHA256SUMS.txt`
- Archive: `Veltrix-Control-Windows-x64.zip`
- Version file and changelog are included in the release payload.

## Known limitations

- Binaries are not Authenticode-signed because no organization signing certificate was
  supplied; Windows may display an unknown-publisher warning.
- The Controller uses a generated certificate for Agent transport. Administrators must
  verify its SHA-256 fingerprint out of band during enrollment.
- SQLite targets a small single-Controller deployment, not high availability.
- Advanced sensor history, write-capable file transfers/editor, process/service mutation,
  software deployment, updates, backups, automation, compute, and game-server adapters
  remain planned phases and are not represented as complete.
