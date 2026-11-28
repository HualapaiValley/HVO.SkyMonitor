# CameraAgent retention and recovery

CameraAgent stores captures and products in the installed local Silo bucket.
An optional Silo archive can hold independently verified copies. The CameraAgent
SQLite catalog records searchable history and availability. The Silo journal
records publication and expiry against exact object versions.

## Capture and product retention

Use the [Silo retention runbook](cameraagent-silo-runtime.md#local-tier-retention)
to inspect and set age and pressure policy, review a dry run, and manage pins.
Silo honours required processing, calibration, restore, replay and other
durable holds before removing an eligible version. An archive copy must be
verified and protected through local eviction unless the policy authorizes
last-copy expiry.

Admission pauses when Silo cannot make progress within its physical space
budget. Retention can resume pressure-paused admission after it observes
sufficient available space. Installer and operator pause controls still apply.

## Startup and recovery

Configure the installed local binding under `CameraAgent:Silo`. Add
`CameraAgent:SiloArchive` when the site has a verified archive binding.
`CameraAgent:RawIngressRoot` identifies the writable application state
directory. It holds SQLite state and fsynced staging for admissions that have
yet to settle in Silo. Preserve the state and bucket together while recovering
acknowledged captures. Follow the
[Silo runtime runbook](cameraagent-silo-runtime.md) to evaluate the writer
binding, catalog readiness and committed history.

Install this release into fresh state with empty history. CameraAgent refuses
the former filesystem storage settings at startup.

The retired filesystem store used per-directory retention, daily file indexes,
and central delivery acknowledgements to release artifact outbox holds. Silo
retention evaluates configured roles and exact versions from the catalog and
uses the Silo hold and pin records. Existing filesystem captures and their
outbox journals are outside this release's recovery scope.

## Environmental Local History

The authoritative targetless environmental journal is
`<raw-ingress-root>/.environment/environmental-observations.db`. Back up
the database with its WAL and SHM files while CameraAgent is stopped or through
a SQLite-consistent snapshot. Never edit or remove those files while the host is
running.

The journal records observation history, source runtime and attempts, capture associations,
and durable on-demand commands. WAL, full synchronization, integrity checks, and path
confinement protect the store.

Retention applies age, count, byte, and batch limits. Active lineage and associations
for retained raw captures pin their observations. Once a capture is released and its
aged association is pruned, the observation can become eligible for removal.

Owner-authenticated environmental operations are:

- `GET /api/v1/operations/environmental/sources`
- `GET /api/v1/operations/environmental/attempts`
- `GET /api/v1/operations/environmental/history`
- `GET /api/v1/operations/environmental/history/{reference}`
- `GET /api/v1/operations/environmental/captures/{captureId}/associations`
- `POST /api/v1/operations/environmental/sources/{sourceId}/acquisitions`

Reads require operations-read authorization. The mutation additionally requires
owner operations-mutate authorization, antiforgery validation, actor identity,
and an `Idempotency-Key`. References and cursors are protected and bounded;
SQLite IDs, storage roots, hashes, actor IDs, credentials, and operation keys
must not appear in operator URLs or public health output. A full queue or active
same-source command returns `429`; same-key/different-payload conflict returns
`409`.
