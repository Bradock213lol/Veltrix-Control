# Compute roadmap

Compute scheduling is Phase 3. NexaGrid will schedule independent distributed jobs;
it will not claim to merge ordinary computers into one transparent CPU/GPU.

The planned scheduler scores eligible nodes by declared policy, available logical
processors, memory, disk, GPU requirements, current load, and health. Reservations are
leases with expiry and explicit lifecycle states. Workload profiles (Gaming, Server,
Compute, Maintenance, Idle) constrain eligibility before scoring. Jobs will have
priorities, bounded retries, cancellation, resource limits, logs, and audit events.

No scheduler code ships in v0.1; this document records the contract direction without
claiming implementation.
