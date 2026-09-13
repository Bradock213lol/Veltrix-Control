# Controller–Agent protocol v0.2

The protocol is JSON over HTTPS. Native-app administration and the recovery web surface
use cookie sessions; Agent endpoints use device signatures and never accept UI credentials.

## Enrollment

`POST /api/agent/enroll` accepts the one-time code, device name, hardware inventory,
and base64 DER SubjectPublicKeyInfo for an ECDSA P-256 key. Success returns a UUID and
enrollment time. The private key never leaves the node.

## Signed envelope

Heartbeats and results use:

```json
{
  "deviceId": "uuid",
  "unixTimeSeconds": 0,
  "nonce": "32 uppercase hex characters",
  "payload": "base64 encoded JSON",
  "signature": "base64 ECDSA/SHA-256 signature"
}
```

The canonical signed bytes are UTF-8:

```text
deviceId-lowercase-d\nunixTimeSeconds\nnonce\npayload
```

`POST /api/agent/heartbeat` records inventory/telemetry and returns queued operations
plus the next recommended interval. Claims transition operations atomically from
Queued to Running. `POST /api/agent/operation-result` accepts only terminal states and
transitions the matching device's Running operation.

The protocol rejects unknown devices, stale clocks, invalid keys/signatures, replayed
nonces, malformed payloads, implausible metrics, oversized results, and results for
another node's operation.
