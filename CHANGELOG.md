# Changelog

All notable changes follow semantic versioning.

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
