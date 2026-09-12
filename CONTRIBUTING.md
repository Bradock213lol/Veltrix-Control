# Contributing

Create changes from `develop` or a focused `feature/*` branch. Keep commits small and
descriptive, add tests for behavior changes, and run `./scripts/build.ps1` before
opening a pull request. Pull requests must explain security impact, migration impact,
and manual verification performed.

Never commit secrets, certificates, enrollment codes, production databases, or real
device telemetry. Do not weaken or delete a test to make a change pass. Features that
execute on a node require a named permission, an allow-listed operation contract,
an audit event, understandable errors, and a locally enforceable safety boundary.

Formatting and analyzer warnings are treated as build failures.
