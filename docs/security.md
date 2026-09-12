# Security model

## Trust establishment

Devices never discover or enroll themselves. An Owner/Administrator creates a
cryptographically random code with a 1–60 minute expiry. The code is stored only as a
SHA-256 digest and consumed atomically. A node generates an ECDSA P-256 key locally,
submits only its public key, and protects the private key with machine-scope Windows
DPAPI. Simulators are visibly labelled and keep only ephemeral keys.

## Ongoing authentication

Every Agent message carries its device ID, Unix time, 128-bit random nonce, base64
payload, and ECDSA/SHA-256 signature over a canonical representation. The Controller:

1. loads the enrolled public key;
2. rejects timestamps outside a two-minute window;
3. verifies the signature;
4. atomically records the nonce and rejects replay;
5. validates the decoded payload.

Production node traffic uses TLS. The Controller creates a DPAPI-protected self-signed
certificate on first start; agents pin its verified thumbprint unless a normally
trusted certificate is deployed. Loopback HTTP is intended only for local UI and
explicit development mode.

## Administrators and operations

PBKDF2-SHA-256 uses a random salt and 210,000 iterations. Sessions are HTTP-only,
SameSite strict, and expire after eight hours with sliding renewal. Rate limiting,
security response headers, a custom-header CSRF defense, length/range validation, and
role permissions protect the API.

Power requests need `device.power`, explicit UI/API confirmation, and the Agent's
separate `AllowPowerActions` local setting (off by default). File requests need
`device.files`; canonical path resolution confines them to `ManagedRoot`. The Agent
executes a closed enum rather than arbitrary command text.

## Audit integrity

Sensitive events append a SHA-256 chain containing the previous hash plus normalized
event fields. This detects database-level edits, deletions, or reordering but is not a
substitute for external append-only/WORM storage. Export/signing is planned for v1.0.

## Known Phase 1 limits

- The self-signed Controller certificate must be verified out of band before pinning.
- SQLite is not encrypted by the application; rely on Windows volume protection and
  restrictive ACLs, and protect database backups.
- Only the bootstrap Owner user can currently be created through the UI; full user
  administration arrives with Phase 2.
- Executable and installer code signing requires an organization-owned certificate
  and is not performed by this repository.
