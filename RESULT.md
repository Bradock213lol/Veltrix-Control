# NexaGrid v0.1.0 result report

## Status

Phase 1 — Secure fleet foundation is implemented as a usable, testable release
candidate. The expanded Phase 2–10 roadmap remains planned in `PHASES.md`; those
features are not represented as complete. Phase 1 is not a finished release until
the combined-role installer smoke test passes in GitHub Actions.

## Architecture

- ASP.NET Core Controller hosted as a Windows Service, with a browser control surface,
  REST endpoints, SignalR updates, SQLite migrations, and cookie-based RBAC.
- Lightweight Windows Agent service with DPAPI-protected ECDSA identity, certificate-
  pinned HTTPS transport, reconnect backoff, Windows telemetry, and guarded operations.
- Shared contracts and platform-neutral core security/domain libraries.
- Production-protocol Node Simulator for safe single-PC workflow testing.
- One self-contained Windows x64 Inno Setup executable for Controller, Managed Node,
  or combined installation.

## Completed features

- First-run Owner setup, session expiry, role permissions, CSRF header checks, and
  per-client rate limiting.
- Expiring, hashed, single-use, revocable enrollment codes and automatic secure local
  bootstrap for the combined installer role.
- Per-device ECDSA P-256 identities, signed messages, bounded clock skew, and persistent
  nonce replay rejection.
- Automatic inventory, CPU/RAM/disk/uptime telemetry, online/offline state, health score,
  live SignalR refresh, and reconnect behavior.
- Guarded restart/shutdown requests with explicit UI confirmation and a disabled-by-
  default local Agent power policy.
- Asynchronous directory listing confined to a configured managed root.
- Application-level hash-chained audit records and integrity verification.
- Polished responsive dark UI with setup, login, overview, device detail, remote files,
  audit history, loading, empty, error, and confirmation states.
- Self-contained Controller, Agent, and Simulator Windows executables; one installer;
  checksum manifest; release ZIP; Windows CI; installer lifecycle smoke test.

## Partially completed or deferred

- File management in v0.1 is read-only directory listing. Transfers, editing, rename,
  copy, move, delete, archive, and search belong to Phase 2.
- Process, service, terminal, software, update, network, and advanced storage management
  belong to Phase 2.
- Compute, game servers, Pterodactyl, Docker, backups, automation, and alerts belong to
  Phase 3.
- PostgreSQL, high availability, signed staged updates, enterprise identity, and large-
  fleet testing are v1.0 hardening work.

## Verification results

- Release build: **PASS**, zero compiler warnings and zero errors.
- Automated tests: **26/26 PASS** — 12 unit, 6 security, 7 integration, 1 end-to-end.
- Dependency vulnerability scan: **PASS** — no known vulnerable direct or transitive
  NuGet packages in the configured source at verification time.
- JavaScript syntax check: **PASS**.
- Controller runtime smoke test: **PASS** — launched from the Windows service working
  directory, returned health `healthy` and version `0.1.0`, produced a valid 64-character
  certificate fingerprint, and completed a real HTTPS handshake.
- Self-contained PE validation: **PASS** for Controller, Agent, and Simulator.
- Installer compile and PE validation: **PASS**.
- Installer lifecycle test: configured in Windows CI to install the combined role, verify
  both services, verify automatic Agent enrollment and online state, uninstall, and clean
  the disposable runner's test state.
- GitHub Actions: **PENDING RERUN** — the previous three runs exposed the Controller TLS
  key-storage defect; the local fix and regression test pass and await the next branch run.

## Release artifacts

- CI artifact name: `NexaGrid-Windows-x64`
- Installer: `NexaGridSetup.exe`
- Local installer size: 87,215,613 bytes
- Local installer SHA-256: recorded in `outputs/SHA256SUMS.txt`
- Release archive: `NexaGrid-Windows-x64.zip`

## Security considerations and known limitations

- The installer is not Authenticode-signed because no organization code-signing
  certificate was supplied. Signing is required before broad production distribution.
- The Controller uses a locally generated self-signed certificate for node transport.
  Administrators must verify its SHA-256 fingerprint out of band during enrollment.
- The local administration listener is loopback HTTP by default; remote node traffic uses
  certificate-pinned HTTPS. A production reverse proxy or enterprise certificate is
  recommended before exposing administration beyond the Controller host.
- SQLite is appropriate for the Phase 1 single-Controller deployment, not high availability.
- Audit chaining makes undetected in-database edits harder but is not an external WORM log.
- Power actions remain disabled on real Agents until an administrator changes local policy.

## Problems encountered and resolved

- The host had no system .NET SDK, so the exact .NET 10.0.401 SDK was isolated under the
  ignored development workspace and the build script selects it when needed.
- Test-host configuration was initially evaluated too early; configuration-dependent
  storage resolution was deferred and isolation tests were rerun.
- Browser QA found a CSS rule overriding the `hidden` attribute on authentication panels;
  a global hidden rule fixed the setup/login transition.
- Inno Setup caught unsupported flags, function signatures, and Pascal string-type issues;
  each was corrected and the complete installer was rebuilt successfully.
- Combined-role setup originally required a code that could not exist before first launch;
  it now creates a cryptographically random local one-time bootstrap code, imports it into
  the Controller, deletes the Controller copy, and enrolls the local Agent automatically.
- The first cloud installer smoke run showed that Windows services start with a system
  working directory; both hosts now resolve configuration and static content explicitly
  from their installed executable directory, with Agent failures also sent to Event Log.
- The next cloud smoke run showed that Windows Schannel rejects an ephemeral server private
  key even though Kestrel opens the HTTPS port. The certificate now loads into the service
  account's user key set, and a loopback TLS handshake regression test protects the fix.
