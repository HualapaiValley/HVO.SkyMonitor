# CameraAgent Silo catalog and durable records

Status: implementation of [#1967](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1967) under the
[Silo storage and cutover contract](../planning/cameraagent-silo-cutover.md) (#1965). It covers the contract's
#1967 slice: sections 4.2–4.7, 5.2, 5.3 and 6.1–6.3. The code is in
`src/HVO.SkyMonitor.CameraAgent.Common/Silo/`. No host, installer or pipeline uses it yet. #1968 implements the
production store adapter and wires publication into the centered pipeline. Contract section numbers below refer to that
contract.

## Format identities

Every new-layout format has an explicit identity (4.7). Readers accept added fields within an identity. They refuse
any other identity read-only, with a `SiloFormatRefusedException` that names the object, the format found and the
format supported. A refused object, bucket or database is left byte for byte unchanged. A rebuild that meets an
unsupported object aborts before it replaces the catalog. The tests check each of these by digest.

| Object | Identity | Where |
| --- | --- | --- |
| Group manifest | `hvo.cameraagent.silo.group-manifest/1` | Silo `m/<yyyy>/<mm>/<dd>/<groupId>/<revisionId>.json` |
| Journal segment | `hvo.cameraagent.silo.journal-segment/1` | Silo `j/g<g:D6>/<seq:D12>.json` |
| Journal checkpoint | `hvo.cameraagent.silo.journal-checkpoint/1` | Silo `c/g<g:D6>/<seq:D12>.json` |
| Catalog index database | `hvo.cameraagent.silo.catalog-index/1`, SQLite `application_id` `0x48534349`, `user_version` 1 | State `catalog-index.db` |
| Publication state database | `hvo.cameraagent.silo.publication-state/1`, `application_id` `0x48535053`, `user_version` 1 | State `publication-state.db` |
| Centered view documents inside a view manifest | `cameraagent-centered-view-base-v1`, `cameraagent-centered-view-revision-v1` | Embedded verbatim |

`tests/fixtures/silo-catalog-v1/` pins the v1 JSON formats byte for byte. A change to them is a new format version.

A database is judged before anything writes to it:

- Its header is read first.
- When its WAL holds committed pages, a copy of the main file and WAL is validated instead of the original.
- Its schema must match the canonical definition exactly.
- A database bound to another instance, store or bucket is refused.

The storage binding identity is the SHA-256 of `hvo.cameraagent.silo.storage-binding/1` and the length-prefixed
instance ID, store ID and bucket (4.2). #1966 writes the instance marker and must use the same derivation.

### Identity

An artifact revision is bound to its length, its SHA-256 and the digest of its producer and sidecar facts: role,
retention class, media type, content and output identity, producer and recipe (4.2). Changed facts under the same
revision are a conflict at staging, in the publish record (`factsSha256`) and in the index.

A centered view manifest carries its stream, reference capture and preview. Validation derives its exact reopen set
from the view itself:

- the preview is required;
- each evidence binding is required when any layer using it is `Ready` or `Partial`;
- `Ready`, `Partial` and `AssessedEmpty` layers must bind their evidence, observation time and order (F17).

`SiloCenteredViews.Section(CenteredView, previousRevisionId)` builds the section from the typed view. It uses the
store's own document serialization and stream identity, and recomputes the revision ID rather than trusting it.

## Object namespace and headers

Payloads are content-addressed under `p/full`, `p/display` and `p/meta` (4.3). Every object is created once, with
`If-None-Match: *` and a supplied SHA-256 checksum. Each carries the stable headers `hvo-sha256` and `hvo-binding`,
and metadata objects also carry `hvo-format`. These headers identify an object and help diagnose it. They never prove
its bytes (F13). Physical proof is always an exact-version GET with an independently computed length and SHA-256.

## Physical split and backup inventory

The State directory holds two databases with different recovery properties (4.6). A rebuild replaces only the index.

| State | Rebuildable from Silo | Needs the instance backup | Why |
| --- | --- | --- | --- |
| `catalog-index.db`: publications, members, dependencies, layer bindings, roles, locators, copies, remaps, expiry intents and outcomes, survivor reservations, live intervals, pins, policies, floors, generations, anchors, cursor, authority, verification cursors | Yes, within the authenticated journal prefix | No. Backing it up only shortens recovery. | Derived from the journal, the manifests and exact-version reads |
| `publication-state.db`: journal writer head, its one pending segment and every committed operation key, staged groups with their members' named locators and PUT attempts, orphan candidates | No | Yes | Staged work and the pending commit are not yet in the journal. The committed head authenticates the journal tail and exposes a lost one. |
| `spool/`: exact bytes of the pending segment and of staged manifests | No | Yes, while `publication-state.db` references them | They replay an uncertain write with identical bytes |
| Payload spool (#1968), admission journal and sequences, processing executions and pins, schedules, rig and calibration library, restore and replay leases | No | Yes | Contract 4.6. The capture-sequence floor is also journaled in `generation-open` records. |
| Identity, Data Protection keys, provisioning, settings, automations, site profile, deployment location, orbital and space-weather stores, time data | No | Yes | Contract 4.1 and 8.5. None of it is published to Silo. |
| Restore cache and FFmpeg workspace | Not needed | No | Bounded scratch |

Silo `meta/`, `m/`, `j/` and `c/` are covered in full by the sealed backup (#1972). Archive replication (#1969)
covers them and the selected `p/` classes (4.5).

## Publication

Publication moves through these states, recorded in `publication-state.db` (5.3):

1. `staged`: the draft is fixed. Staging the same draft again is idempotent. A different draft for the same revision,
   or an artifact revision staged or published with other bytes or facts, is a conflict and stages nothing.
2. `payloads-qualified` (P1): each payload is created from its exact spooled bytes, read back and qualified. The
   attempt is recorded before the PUT.
3. `manifest-qualified` (P2): every named payload version is read back again first, and a lost one is recreated from
   its spooled bytes. The manifest then names only verified versions. Its exact bytes are spooled before the first PUT.
4. `recorded` (P3): every named version, manifest included, is read back once more. A lost manifest is recreated from
   its exact spooled bytes. A lost payload supersedes the manifest:
   - the manifest is rebuilt and its old version handed to retention as an orphan candidate;
   - while that old version still holds the create-only key, publication blocks with
     `silo.manifest-supersession-blocked`. Nothing is committed or replaced, and the spool is kept.

   The `publish` segment commits under the operation key `publish:<group>:<revision>`. In the same transaction the
   group moves to `recorded` at that position.
5. `visible` (P4): the writer's own first P4 runs P3 recovery for that revision within the same operation. Only when
   it verifies does the group become visible and release its spooled bytes. An interrupted release is finished on resume.

`abandoned` is reachable only before P3. It hands every version the group created, or may have created through an
uncertain write (recorded with an empty version ID), to retention as an orphan candidate. A store whose profile is not
persistence-qualified is never named by a committed record.

## Journal and authority

- **Ordering and idempotence.** Records are ordered by `(g, seq)`. Sequences have no gaps within a generation, and each
  segment names its predecessor's position and SHA-256. One writer holds one pending segment, and every append carries
  an operation key. A retry of the same key returns the original segment, whether it is still pending or committed but
  unacknowledged. A generation opening advances the writer generation in the same transaction that makes it pending,
  so a crash leaves nothing or an opening that the same call completes.
- **Authentication.** A segment is applied only when the next segment names its exact SHA-256. With the writer's
  evidence it must instead match the writer's committed head, past which nothing is read as authority. Without that
  evidence, as after total loss, it is accepted as the tail. An altered segment that still parses therefore ends the
  prefix rather than entering the catalog.
- **Writer evidence.** Startup catch-up and a rebuild with surviving State both take `SiloWriterEvidence`: the
  committed head, and the P3s still awaiting their first P4. The authority-unknown interval clears only when the
  committed head is read again with its SHA-256. A P3 awaiting its first P4 is recovered under P3 rules, never as
  settled history.
- **Generations.** The first segment of every generation opens it. A later generation must name the end of the prefix
  it continues. `ISiloGenerationWitness`, supplied by #1972, must prove that exact opening. Without that proof the
  prefix ends at the boundary.
- **Trusted anchors.** `SiloCheckpointWriter` writes a checkpoint of every applied record at the cursor. It refuses any
  checkpoint larger than the readers' bound, so it never produces one a rebuild cannot read. Selection starts from the
  predecessor hash named by the first segment after a checkpoint. It then needs an `anchor` record, trusted through
  `ISiloAnchorTrust` (#1972), in the authenticated chain. Finally it reads the anchor's exact version, bounded by its
  length, which a newer version at the same key cannot hide. An unreadable, oversized, absent or unproven candidate is
  skipped and an older one tried, then generation 1. `SiloJournalRetention.RemovableThrough` permits removing segments
  only behind the previous trusted anchor.
- **Reporting.** The catalog reports these separately (4.5, 5.4):
  - the authenticated prefix end and why it ended;
  - the complete-publication frontier;
  - whether an authority-unknown interval follows;
  - the publications named past the prefix, as authority-unproven, refreshed on every catch-up.

## Catalog file replacement

A rebuild validates the live catalog, including committed WAL pages, on a copy before it touches anything. It then
builds `catalog-index.db.building` and renames it to `.replacement` only when complete and synced. Next it retires the
live catalog by rename, main file first and then its WAL, so committed pages never leave their database. Finally it
installs the replacement and deletes the retired files. `SiloCatalogFiles.Recover` runs before the catalog is opened or
rebuilt. It finishes or discards each intermediate state deterministically, so no residue survives.

## Eligibility and dispositions

A named locator is eligible until its exact copy is evicted. A `copy` or `remap` is eligible only when all of these hold:

- it was recorded after the role's P3, inside the prefix;
- it is bound to the same identity, revision, length and SHA-256;
- for a remap, it is persistence-qualified (4.2).

Matching keys or bytes alone never qualify. A later valid copy or qualified remap of the same exact version promotes an
earlier ineligible record. A newly eligible or restored copy requeues any revision closed for want of that role, so
ordinary catch-up reaches what a rebuild serves.

Role dispositions are decided in this order:

- `policy-expired`, only under a settled role-scoped intent;
- `conflict`;
- `available` at a copy verified now;
- `temporarily-unavailable` while a copy cannot be checked;
- `unverified` while a copy is unprobed;
- then `unverified` if an authority-unknown interval exists, otherwise `missing`.

An earlier `missing` observation never outranks a timeout or an unknown interval.

Role expiry needs all of these:

- affirmative last-copy authority on a role-scoped intent;
- that intent covering every authoritative copy, including copies found unexpectedly missing;
- `deleted` or `already-absent` for each of them under that intent.

Copy-scoped eviction never makes a role `policy-expired`.

Reopen (`ReopenAsync`, or `ReopenViewAsync` for an exact link that must also name the stream and reference capture)
only reads. It never changes a revision's publication state. Within that operation it verifies the manifest, every
required member and every required reopen dependency by exact read. It falls back from the local copy to eligible
replicas, and returns the locator that verified. Lazy verification and audit (`VerifyRolesAsync`, `includeAvailable`)
resume after durable cursors and wrap around. A role that keeps timing out, or a healthy early role, never starves the
rest. A deleted or damaged version behind a valid catalog is found (#1059 negative control).

## Seams for downstream issues

| Issue | Seam |
| --- | --- |
| #1966 | Stock Silo runtime and its persistence-qualified profile (`SiloStoreDescriptor.PersistenceQualified`), the instance marker with the storage binding identity above, and the bucket and fence bootstrap |
| #1968 | The production `ISiloObjectStore` adapter on that runtime. `ISiloPayloadSpool`, whose release must be idempotent. `SiloPublisher` staging and resume. `SiloCenteredViews` from typed views. Host and DI wiring. |
| #1969, #1970 | Append `copy`, `reserve-survivor`, `expiry-intent`, `expiry-disposition` and `availability` records through `SiloJournalWriter` with operation keys. `SiloPublicationState.ListStagedLocatorsAsync` lists versions that staged work still names. |
| #1971 | `ReopenAsync`, `ReopenViewAsync`, `VerifyRolesAsync` and the role and locator reads |
| #1972 | `ISiloGenerationWitness`, `ISiloAnchorTrust`, `SiloJournalWriter.OpenGenerationAsync`, `SiloCheckpointWriter`, and `SiloRebuildOptions` (`Writer`, `SourceMayBeIncomplete`, `MaximumCheckpointBytes`) |

## Known limits

- A checkpoint carries every applied record, so its size grows with history until the 64 MiB reader bound refuses new
  checkpoints. Correctness holds beyond that, because replay continues from the last readable anchor or generation 1.
  Compaction of settled records is a later measured decision (#1974).
- A rebuild reads the journal from one source. Merging journals from the backup and the archive after total loss
  belongs to #1972.
- A payload lost before P3, while its superseded manifest still holds the create-only key, blocks that group until
  retention removes the orphaned manifest version (#1970). A manifest supersession key would need a §4.3 revision.
- Deleting journal segments, issuing deletes and accounting for reclamation belong to #1970. This code only records
  and applies their outcomes.
