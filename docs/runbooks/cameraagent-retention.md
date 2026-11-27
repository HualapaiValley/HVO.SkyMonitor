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

## Environmental Local History

The authoritative targetless environmental journal is
`<raw-ingress-root>/.environment/environmental-observation-outbox.db`. Back up
the database with its WAL and SHM files while CameraAgent is stopped or through
a SQLite-consistent snapshot. Never edit or remove those files while the host is
running.

Schema v3 adds targetless observation history, source runtime and attempts,
capture associations, durable on-demand commands, and independent central
projections. Migration from v2 transactionally backfills every surviving
delivery row and is restart-idempotent. V2 rows already deleted after central
acknowledgement cannot be reconstructed, and migration never invents them. A
newer schema fails closed. The journal retains WAL, `synchronous=FULL`, busy
timeout, integrity, root containment, and symbolic-link defenses.

Local history has independent age, count, byte, and batch limits. Active
lineage, capture associations whose capture remains in `raw-ingress.db`, and
waiting or staged central projections pin their referenced observations under
normal and pressure retention. Once raw ingress releases a capture and its aged
association is pruned, the corresponding observation may become eligible.
Central acknowledgement removes only the delivery copy; it does not remove
local history. Standalone mode remains authoritative and creates no central
projection.

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

## Execution Evidence Export

The durable graph-execution evidence outbox is
`<raw-ingress-root>/evidence/execution-evidence-outbox.db`. It is a separate
database file from `raw-ingress.db` on purpose: an installer rollback to a
baseline image must still open every store that image knows about, and a
separate file is simply never opened. Back it up with its WAL and SHM files
while CameraAgent is stopped or through a SQLite-consistent snapshot, and never
edit those files while the host is running.

The store pins schema version 1 and compares its entire `sqlite_master`
definition against the canonical schema the first time the host opens it. A
drifted or newer store fails closed with `export.durable-state-unavailable`. The
check runs again on the next cycle, so archiving or repairing the file recovers
the lane without a host restart, and the first cycle after a failed start is the
one that retries it. Capture,
raw ingress, live processing, publication, artifact upload, and replay continue
unaffected, because nothing local reads this store.

Recovery steps for a stalled or degraded lane:

1. Read `execution-evidence-export-state` in the operations summary. Its
   `reasonCode` names the exact bound or fault: `export.transport-unconfigured`
   (standalone; nothing is enlisted and nothing accumulates),
   `export.backlog-saturated`, `export.storage-saturated`,
   `export.storage-pressure`, `export.source-pruned`,
   `export.projection-rejected`, `export.quarantined`,
   `export.negotiation-rejected`, `export.authentication-blocked`,
   `export.resync-requested`, `export.acknowledgement-pending`, or
   `export.cycle-failed`.
2. For a saturation or pressure state, free space or raise the relevant
   `CameraAgent:ExecutionEvidenceExport` bound. Enlistment resumes from the
   durable cursor; nothing already enlisted was discarded.
3. For `export.source-pruned`, source retention removed terminal executions the
   exporter had deferred and never sealed. The counter and the log entry record
   it; that evidence cannot be recovered and the operator decides whether to
   widen retention or the export bounds so it does not recur. For
   `export.projection-rejected`, an execution carried a durable value this
   contract version cannot express or sealed above `MaximumUnitBytes`; it is
   counted once per execution and the sweep continues past it.
4. For quarantined units, list them with
   `GET /api/v1/operations/outboxes/execution-evidence`, inspect one with
   `GET /api/v1/operations/outboxes/execution-evidence/{reference}` and its
   `/audit` page, then either
   `POST /api/v1/operations/outboxes/execution-evidence/replay` after the cause
   is corrected or
   `POST /api/v1/operations/outboxes/execution-evidence/abandon` to accept the
   loss explicitly.

Reads require operations-read authorization. Both mutations additionally require
owner operations-mutate authorization, antiforgery validation, an allow-listed
reason code, and an `Idempotency-Key`; the same key with different intent
returns `409`. References, cursors, and action tokens are protected and bounded,
so sequences, origin identities, payload hashes, and SQLite IDs never appear in
operator URLs.

Retention removes only acknowledged units, and only those older than
`AcknowledgementRetentionHours` or beyond `MaximumRetainedAcknowledgements`.
Pending, retrying, quarantined, and abandoned units are retained until the
receiver acknowledges them or an operator disposes of them, and the audit trail
and operator receipts outlive the units they describe so the disposition history
above stays readable. This lane pins no image file and holds no payload path, so
it never blocks the disk-pressure sweep and the sweep never deletes anything it
owns; disk pressure does pause new enlistment, which is reported as
`export.storage-pressure` and resumes from the durable cursor with nothing
lost.
