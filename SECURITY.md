# Security policy

## Supported version

Security fixes are currently applied to the latest `0.1.x` release line.

## Reporting a vulnerability

Do not open a public issue containing exploit details, credentials, device identity
material, or private fleet information. Use GitHub's private vulnerability reporting
feature for the repository owner. Include the affected version, reproduction steps,
impact, and any proposed mitigation. Do not test against systems you do not own or
have explicit authorization to manage.

## Operating boundary

Veltrix-Control intentionally requires interactive, expiring enrollment and keeps its
Windows services visible. It does not include credential collection, keylogging,
security bypass, exploitation, stealth, or a custom kernel driver. Administrators
must use TLS, verify Controller certificate fingerprints during self-signed
deployments, restrict Controller network exposure, and protect backups of the
Controller database.
