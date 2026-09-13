# Changelog

All notable changes follow semantic versioning.

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
- Adopted the MIT License and aligned repository branding with the NexaGrid product name.

### Fixed

- Load the Controller TLS certificate into the Windows service account key set so Schannel
  can complete node HTTPS handshakes.
- Added a real TLS handshake regression test and made dependency/JavaScript checks part of
  the release build.
- Allow the release workflow's first publication to distinguish an expected missing
  release from a fatal GitHub CLI error.
