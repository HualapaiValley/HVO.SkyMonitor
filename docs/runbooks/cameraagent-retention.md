# CameraAgent Retention and Recovery

CameraAgent retains capture payloads and their local evidence under each configured storage root.
The raw capture journal, processing records, and transient evidence determine which payloads remain held.

## Planned Storage Policy Changes

Before changing Storage in the local capture profile, pause acquisition and finish
in-flight standard processing. Drain pending standard work too if those captures
must complete under the old policy, even for a retention-only change in the same
directory. A profile activation does not migrate already bound or running graphs.


Configured-basic refreshes from the new profile automatically. If a Named graph
is active, reconcile and activate a matching graph before resuming capture. See
[the graph Storage workflow](capture-node-options.md) for refreshed sources,
processing skips and the limits of explicit replay recovery.

## Safety Invariant

Raw ingress evidence remains held by `raw-ingress.db` until all required durable
lane acknowledgements are recorded and no retained transient reference still
depends on it.

Retention fails closed for a storage root before deleting anything when:

- a pending path is absolute or escapes the configured root;
- a pending payload is missing;
- cancellation is requested while pending manifests are scanned.

Retention, derived-product reconciliation and projected-scene stage reconciliation
delete nothing while capture admission is not running. An installer upgrade or
rollback pauses admission durably before it stops the previous image and resumes
it only after the swap is decided, so a candidate image cannot delete frames,
archives or derived products the previous image still needs. An operator pause
holds them too; nothing new is captured while paused. Only the deletions wait:
retention still measures capacity and publishes disk pressure, and derived-product
reconciliation still refreshes its evidence inventory. Each skipped pass logs
`<maintenance> held while capture admission is <state>; nothing was deleted` at
Information, and the next pass after capture resumes runs normally.

The pause is a barrier, not a flag each pass happens to read. A pass enters capture
admission before it deletes anything, and the pause is acknowledged only after every
admitted pass has finished, including one still waiting for its storage lock; no new
pass is admitted until capture resumes.

The same barrier covers every other deletion a restored database row could depend on:
- A projected-scene stage is removed after its processing node commits only once capture opens. That removal waits in
  memory, so a restart forgets it, and stage ownership does not depend on it: a stage whose projected-scene node is
  durably complete belongs to no capture, and stage reconciliation removes it once capture runs. A snapshot restore
  rewinds the node with the journal, so a stage the restored node still needs stays owned.
- A frame sidecar is upgraded in place only once capture opens; until then the node retries.
- Raw-ingress evidence is quarantined when the journal opens only once capture opens. Until then raw ingress reports
  Degraded (`reconciliation-held`), and the quarantine runs as soon as capture resumes.
- Stage cleanup when the journal opens, and the calibration bundle quarantine at host start, wait the same way.

A first initialization reads the durable admission state from the journal, because admission initialization waits for
it; a later one takes a lease without initializing admission. See
[Checkpoint and image recovery](deployment-installer.md#checkpoint-and-image-recovery).

The completion log records the storage root, deleted file count, and number of
protected pending artifacts. A repeated `Retention sweep failed` event requires
operator investigation. Preserve the affected capture evidence.

## Disk Pressure

Each retention sweep measures available capacity for every configured storage
root. Pressure begins below `DiskPressureThresholdPercent` (default 10%) and
remains active until capacity reaches `DiskPressureRecoveryPercent` (default
15%). While active, eligible history uses the smaller of its configured
retention and `DiskPressureRetentionDays` (default one day).

The current observing day and locally held payloads, sidecars, and index entries
remain protected. Probe or hold validation failures delete nothing for that
root. `/health` reports pressure as degraded and capacity-probe failure as
unhealthy; transition logs record entry and recovery. This policy does not pause
capture or discard accepted frames.

## Centered-view journal

The raw-root retention sweep prunes a centered view after the reference capture
is eligible under the effective UTC-day cutoff, its raw payload and sidecar are
absent, and its preview has left the processing store. The cutoff day is
included. A retained raw file, raw hold, or preview keeps the view in the journal.

An open live interval, retry-pending window, or saved selection keeps the whole
base and all of its revisions. Saved selections have no automatic expiry and
do not extend the retention policy for artifact bytes. Surviving recomputations
keep their original base. An expired base needed to close a retained interval
keeps its change and revision records. Its own expired cross-base intervals can
be detached. The archive reads the last live revision from the ordered live
changes, so detaching an interval preserves the last-live archive target.

The sweep reads at most 512 base IDs at a time and commits at most 128 bases
per SQLite write transaction. Between full read pages and after each write
batch, it releases both lifecycle locks and pauses briefly so capture and other
journal writers can proceed. It rechecks raw files and durable holds before
pruning each batch. A paused or interrupted pass resumes by rechecking what
remains, and completed publication checkpoints stay settled. Event 2547 reports
how many bases were deleted. Journal failures are reported separately from file
expiration as event 2546. Event 2548 counts expired bases skipped because their
definitions lack a readable preview identity and identifies the first such base.
Exact links to successfully pruned revisions return HTTP 404.

After pruning bases, the same sweep checks measured-trail work by source
exposure. It removes consumed records when the source has reached the UTC-day
cutoff, its raw files and holds are gone, no retained view uses the exposure,
and any qualified linear products and centered previews have left the
processing store. An exposure with pending or deferred trail work keeps its
records until the worker settles them. A retained stack keeps the trail records
of all its contributors, even after a contributor's single view expires.

The sweep pages through 128 trail work records at a time and commits at most 128
deletes per transaction, releasing the lifecycle locks between batches. This
also clears eligible records left by earlier sweeps. Event 2549 reports the
number removed. The original transient stage receipts persist, so a retry or
restart cannot enqueue the same settled event again.

SQLite reuses free journal pages on later writes. This sweep does not vacuum
the journal or return the free pages to the filesystem. The shared journal
adopts the retention indexes on startup. Use the paired pre-upgrade checkpoint
when rolling back to a reader without those indexes in its schema allowlist.
The [journal measurements](../validation/centered-view-retention-2000.md) record
snapshot growth and steady-state and backlog sweep costs.

## Restart Browsing

The daily `<storage-root>/index/frames_yyyy-MM-dd.jsonl` append is the browsing
visibility commit point. A fresh CameraAgent storage service reconstructs a
date/role listing from that index and requires the matching payload and JSON
sidecar to exist and agree on artifact identity, role, timestamp, dimensions,
and pixel format. Malformed, duplicate, or incomplete entries are skipped and
logged rather than exposed as valid artifacts or blocking the rest of the day.
Every caller supplies a complete `ReconstructionDescriptor` and stores an
`ArtifactManifestV2` sidecar containing the same validated descriptor used for
transport. Browsing reads only that canonical form. A malformed, unversioned,
or unsupported sidecar is skipped and never downgraded to another parser.
Before publishing a v2 sidecar, storage verifies every overlapping frame and
artifact fact, writes the payload, then streams SHA-256 from the published file.
An ordinary v2 save failure removes only that invocation's temporary files.
Already-published immutable payloads and sidecars remain for idempotent retry and
reconciliation; a failed compatibility-index append does not discard evidence.
Browsing also requires the sidecar path and byte length to match the selected
payload; checksum verification remains mandatory when bytes are reconstructed
or transferred rather than forcing a full-frame scan for every gallery listing.
Raw ingress reconciles residual files and repairs this projection at startup;
the JSONL index remains compatibility browsing state rather than durable truth.
Before appending, the writer terminates any torn final line so a later valid
commit remains independently browseable. Listings also inspect adjacent daily
indexes for bounded restart recovery.

Listings use persisted UTC capture time, then artifact ID as a stable tie-break,
and apply the caller's result limit. Files committed before an interrupted index
append remain hidden until a future reconciliation operation; do not manually
add index lines without validating the sidecar and payload pair.

During graceful host shutdown, acquisition cancellation happens first. The
processing channel then completes and drains every accepted frame before the
camera module is disposed. A `Capture processing channel drained` event confirms
that completion-driven shutdown path finished. If the host shutdown deadline
expires, the drain is canceled, the module is still disposed, and a `Capture
processing channel drain aborted` warning records that accepted work may remain
unfinished.

Capture failures use deterministic exponential retry delays configured by the
rig pipeline (`captureFailureInitialDelay`, default 250 ms, and
`captureFailureMaximumDelay`, default 30 seconds). A successful capture resets the sequence; shutdown
cancellation interrupts either capture or backoff immediately. Structured logs
record the failure count/delay and the subsequent recovery transition.

## Raw Ingress Recovery

`CameraAgent:RawIngressRoot` is mandatory and must identify persistent local
storage. The authoritative journal is
`<raw-ingress-root>/journal/raw-ingress.db`; do not edit it or delete its WAL/SHM
files while CameraAgent is running. `/health` reports `raw-ingress` as healthy
only after schema verification, integrity checking, reconciliation, and a
passive checkpoint complete.

Schema inspection uses a read-only SQLite transaction over the main database and
its committed WAL, with a bounded page cache and memory mapping disabled. It does
not copy the journal into temporary storage. All schema queries in an inspection
share the same committed image; contention remains a transient startup failure,
while canonical-schema drift and integrity failures still fail closed.

Raw reconciliation, projected-scene stage ownership, and recovered live-execution
binding visit one retained manifest at a time. Reconciliation releases each SQLite reader before checking
payload files or repairing state, so payload scanning does not pin a WAL reader
for the whole history. Compact path and stage-key sets still scale with capture
count; manifest bytes and decoded scene objects do not accumulate with history.
Startup compares each committed capture's sidecar with its journaled manifest
and checks that the payload exists with its committed length. It reads no
payload bytes except those of captures processing reported, described below, so
its I/O does not scale with retained payload bytes. Evidence a crash left
without a committed journal row is still checksummed in full before it is
recovered. Processing, replay, window inputs, transients, time-lapses, downloads
verify a committed payload's checksum before using it.
When processing finds committed evidence it cannot use, its
lane work ends as `evidence-unavailable`, and every reconciliation from then on,
the next startup's included, checksums that capture's payload and quarantines a
mismatch, as startup did before. Resuming capture after a start under the
installer's pause runs only the maintenance that start held, not a second
reconciliation.

The protected deployment-location history, the protected manual-coordinate record
`manual-deployment-location.v1.protected` that carries the governing local seed
when an owner has entered coordinates, and the dedicated stable-application Data
Protection key ring are under `<raw-ingress-root>/.location/`; the non-secret
initialization marker is `<raw-ingress-root>/.deployment-location.v1.identity`.
Back up and restore those paths with the raw-ingress evidence. Loss of the protected history or its key ring makes
capture-time location versions unavailable; the marker and retained manifests
make CameraAgent fail closed rather than assigning current coordinates or
silently creating a replacement version 1.

The versioned local automation store is
`<raw-ingress-root>/.automation/local-automations.db`. It is deliberately a
separate SQLite database rather than tables inside the raw ingress journal: the
journal pins an exact schema version and object count and refuses a database it
does not recognize, so a forward-only change there would break the installer's
rollback-to-baseline contract. A baseline image never opens this file, so a
rollback simply leaves it in place and a roll-forward finds the same definitions
and run history. Back it up with the raw-ingress evidence. Its retention is
bounded by the contract itself: at most 32 definitions, 50 retained revisions and
200 terminal runs per live definition plus at most 32 queued and one running attempt,
a further 200 retained revisions and 200
retained runs in total for definitions that have been removed (pruned on every
removal, because per-definition retention only runs from a live definition's own
write paths and removal frees its slot), and a seven-day idempotency ledger whose
retained window is the replay window. Deleting the file loses only the
local automation definitions and their run journal; capture, calibration, and
location evidence are unaffected, and the store is recreated empty on the next
start. See [the local automation runbook](cameraagent-local-automation.md).

Configured synthetic calibration references are immutable payload/manifest pairs
under `<raw-ingress-root>/calibration/synthetic/`. They are explicit processing
retention holds and must be backed up with the raw evidence while their profile
is configured. Each directory includes a canonical
`calibration-library-bundle.json` envelope, and
`reference-calibration-profile.json` is the final bundle commit marker. Removing
or replacing either file or any committed reference makes calibration fail closed
before a partial derivative can be published; restore the exact evidence rather
than deleting the rest of the bundle.

On restart, complete valid manifest-v2 pairs are recovered exactly once, stale
temporary files are recorded and removed, compatibility indexes are repaired,
and malformed or conflicting evidence moves beneath `quarantine/`. A committed
row with missing evidence makes ingress unhealthy and requires restoring the
exact payload/sidecar or an explicit operator disposition. Never clear the
journal merely to make health green: committed rows are retention holds and the
only durable record of unfinished raw work.

Disk exhaustion, failed capacity probes, inaccessible storage, SQLite lock
timeout, integrity failure, and unsupported newer schemas stop further capture
acceptance. Restore capacity or access, preserve all evidence, then restart the
agent and confirm `raw-ingress` health, pending count/bytes, oldest age, and
quarantine totals before resuming normal operation.

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

## Soak Validation

The normal test suite runs a reduced-resolution VirtualSky day from 289
five-minute captures and verifies exact raw/derivative counts, committed
payload/metadata pairs, bounded scene caching, and no temporary files. Channel
tests separately prove capacity blocking and accepted/dequeued equality.

An actual overnight or 24-hour run is optional and never gates pull requests.
Dispatch `CameraAgent Real-Duration Soak` on a runner labeled
`self-hosted`, `linux`, and `skymonitor-soak`, or run
`./scripts/run:cameraagent-soak 24h`. The workflow retains compact logs and a
JSON inventory; it does not upload the complete image tree.

## Derived-product reconciliation lock budget

Derived-product reconciliation releases the storage lifecycle lock between individual
products, temporary-file deletions and durable lifecycle operations. Reclaimed orphan
records use a fixed batch of 16. Directory enumeration, payload reads, checksums and
manifest validation run outside the lock. Crash recovery also compares duplicate source
and destination copies outside the lock, revalidates their identities, then resumes all
moves and database transitions as one locked operation. The configured reconciliation page size
controls progress per pass, not the number of products held under one lock.

The diagnostic wait budget is **250 ms**. A caller queued behind reconciliation
waits for its current metadata/publication unit, rather than all retained products.
This is an operating budget, not a real-time filesystem guarantee or a cancellation
timeout: a slow filesystem, directory sync, SQLite transaction, or another lock owner
can exceed it. Durable mutations finish safely. The next pre-release Pi smoke test
must verify capture storage and Current Sky during a 512-product pass.

After unlocked validation, reconciliation rechecks the canonical paths, file lengths,
last-write and creation timestamps, and committed database identity/state under the
lock. A changed or removed product is deferred to a subsequent pass; stale validation
never authorizes a quarantine, availability update or deletion. Publication, quarantine
moves, retention checks and artifact handle pinning still use the same per-root lock.
Frozen-output pin admission rejects outputs with a delete operation in `planned` or
`moved` phase inside its existing SQLite transaction. Recovery can therefore release
the lock for duplicate-copy comparison without accepting a replay input it will delete.
`database-completed` and `files-deleted` only clean up tombstones and do not block
a valid output recreated with the same identity.
The existing outcome counters count inspected evidence and completed decisions;
a deferred observation contributes no available, missing or quarantine decision.

The existing `HVO.SkyMonitor.CameraAgent.ProcessingGraph` meter exposes
`camera_agent.storage.lifecycle.wait.duration` in seconds, tagged only by the bounded
`caller` values `capture-storage`, `artifact-read`, `reconciliation`, `gallery`,
`retention`, `trail-composition` and `replay`. Acquisitions add
`storage.lifecycle.lock.acquired` events to the current diagnostic activity with
`caller` and `wait.duration`. Debug event 8450 records each completed or canceled wait. Warning
8451 is emitted only when the measured wait exceeds 250 ms. No path or artifact
identity is a metric label or diagnostic field. Canceled waits contribute wait timing and a `storage.lifecycle.lock.cancelled` event without claiming acquisition.
