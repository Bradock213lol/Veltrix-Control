# Veltrix-Control v0.9.0 result report

## Status

Phase 9 is implemented: existing Pterodactyl and Docker environments can be observed and
controlled through optional, isolated integrations. Phases 1-8 remain intact.

## Delivered workflows

- Pterodactyl Panel integration: health check, node and server listing, and confirmed
  power actions through the official application API.
- Docker integration: engine health, containers, images, volumes, container lifecycle
  actions, and bounded container logs over npipe or HTTPS endpoints.
- AES-GCM credential protection with a Controller-local key; credentials are never exposed
  in API responses and are deleted with the integration.
- A separate `Veltrix-Control.Integrations` module so integration outages cannot degrade
  core workflows.
- Desktop Integrations workspace with creation, health, resources, actions, logs, and
  deletion.

## Verification

- Release build: **PASS**, zero compiler warnings and zero errors.
- Automated tests: **134 PASS** across unit, security, Agent, integration, and
  Controller-to-Agent end-to-end suites.
- New coverage: credential round trip and tamper rejection, key reuse, redaction in
  responses, health reporting for unavailable endpoints, endpoint and kind validation,
  permission denial, and confirmation requirements.

## Release artifacts

- Tag: `v0.9.0`
- Installer: `Veltrix-Control-Setup.exe`
- Checksum manifest: `SHA256SUMS.txt`
- Archive: `Veltrix-Control-Windows-x64.zip`
- Version file and changelog are included in the release payload.

## Known limitations

- Pterodactyl console logs require the client API and websocket access; the application
  API does not expose them, and the integration says so explicitly.
- Docker remote endpoints require npipe or HTTPS; plaintext TCP is limited to loopback.
- Binaries are not Authenticode-signed because no organization signing certificate was
  supplied; Windows may display an unknown-publisher warning.
- Production hardening (PostgreSQL option, signed self-update, enterprise identity) is
  the remaining milestone.