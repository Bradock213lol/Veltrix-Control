# Installer design

NexaGrid uses Inno Setup 6.7.3 for v0.1. It produces one genuine Windows PE executable
while allowing custom role/configuration pages and reliable service/upgrade/uninstall
steps. This is a better Phase 1 fit than MSIX because the product installs Windows
services and needs per-machine role configuration; a WiX bootstrapper remains an
option when enterprise MSI deployment becomes a release requirement.

## Roles

- **Controller:** Controller service, control surface, and simulator.
- **Managed Node:** Agent service and enrollment configuration.
- **Controller + Managed Node:** both internal components from the same installer.

The package is self-contained for Windows x64, so ordinary users do not install .NET.
The same `AppId` supports version detection, repair, and in-place upgrades. Existing
services are stopped before replacement, reconfigured, and restarted. Inno Setup's
transactional file installation provides rollback before the non-cancellable
post-install stage.

Controller and Agent state under `%ProgramData%\NexaGrid` is retained on uninstall to
avoid silently destroying identities, audit history, and fleet state. Administrators
may remove that directory manually only after securing any required backup.

For unattended deployments, pass `/ROLE=controller`, `/ROLE=agent`, or `/ROLE=both`.
Agent-only installs also accept `/CONTROLLERURL=`, `/FINGERPRINT=`, `/TOKEN=`, and
`/MANAGEDROOT=`. These can be combined with Inno Setup's standard `/VERYSILENT`,
`/SUPPRESSMSGBOXES`, and `/NORESTART` switches. Do not place enrollment tokens in
shared deployment logs.

## Build

Run `scripts/build.ps1`. It restores, builds with warnings as errors, runs all tests,
publishes self-contained win-x64 components, verifies PE headers, compiles the installer,
and writes SHA-256 checksums plus a release ZIP in `outputs/`.

Executable signing is intentionally not faked. A production publisher must add an
Authenticode signing step backed by a protected organization certificate.
