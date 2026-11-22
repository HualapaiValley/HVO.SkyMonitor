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
| Group manifest | `hvo.cameraagent.silo.group-manifest/1` | Silo `m/<yyyy>/<mm>/<dd>/<groupId>/<revisionId>/<sha256>.json` |
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

A centered view manifest embeds its typed base and revision documents, and those documents are the view. Staging and
every manifest readback derive the projection from them again: stream, reference capture, order, preview and layer
bindings. They also derive the exact reopen set:

- the preview is required;
- a stack's closed window is required;
- the science is required when it is displayed, which is the producer's fact;
- each evidence binding is required when any layer using it is `Ready` or `Partial`;
- `Ready`, `Partial` and `AssessedEmpty` layers must bind their evidence, observation time and order (F17).

A manifest whose projection or reopen entries disagree with its embedded documents is refused. Nothing the view needs
can therefore be dropped while its embedded revision ID stays the same.

`SiloCenteredViews.Section(CenteredView, previousRevisionId)` builds the section from the typed view. It accepts only a
view the store itself writes under that predecessor, uses the store's own document serialization and stream identity,
and recomputes the base and revision IDs rather than trusting them.

## Object namespace and headers

Payloads are content-addressed under `p/full`, `p/display` and `p/meta` (4.3). Each exact manifest has its own key
beneath its logical revision, fixed by the manifest's SHA-256. A retry of the same bytes therefore always targets the
same key, and a manifest rebuilt with other bytes never contends for the key of the one it supersedes. Every object is
created once, with `If-None-Match: *` and a supplied SHA-256 checksum. Each carries the stable headers `hvo-sha256` and `hvo-binding`,
and metadata objects also carry `hvo-format`. These headers identify an object and help diagnose it. They never prove
its bytes (F13). Physical proof is always an exact-version GET with an independently computed length and SHA-256.

## Physical split and backup inventory

The State directory holds two databases with different recovery properties (4.6). A rebuild replaces only the index.

| State | Rebuildable from Silo | Needs the instance backup | Why |
| --- | --- | --- | --- |
| `catalog-index.db`: publications, members, dependencies, layer bindings, roles, locators, copies, remaps, expiry intents and outcomes, survivor reservations, live intervals, pins, policies, floors, generations, anchors, cursor, authority, verification cursors | Yes, within the authenticated journal prefix | No. Backing it up only shortens recovery. | Derived from the journal, the manifests and exact-version reads |
| `publication-state.db`: journal writer head, its one pending segment with its write attempts, every committed operation key with the fingerprint of its records, staged groups with their members' named locators and unresolved writes, orphan candidates | No | Yes | Staged work and the pending commit are not yet in the journal. The committed head authenticates the journal tail and exposes a lost one. |
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
2. `payloads-qualified` (P1): each payload is created from its exact spooled bytes, read back and qualified. Before
   each PUT the key is marked unresolved, and storing the version it produced resolves it.
3. `manifest-qualified` (P2): every named payload version is read back again first, and a lost one is recreated from
   its spooled bytes. The manifest then names only verified versions. Its exact bytes are spooled before the first PUT.
4. `recorded` (P3): a pending commit of this revision is reconciled first. Each create-only write reports one
   result for its whole history, and that result only rises: `created` once an attempt was acknowledged, `possibly
   applied` once an attempt found bytes at the key or ended in a way that could have landed, and `provably not
   applied` only while every attempt was definitely refused. Reads are not writes. A GET that misses or a HEAD that
   times out never lowers the result, so it can never clear an earlier possible application.
   - A segment that may have been written is replayed with its exact spooled bytes. That covers a timeout after it
     could have landed, a crash that left a write in flight, and any earlier attempt that landed or found bytes. The
     replay finds the acknowledged write and commits it once, and nothing it names changes afterwards. If other bytes
     hold that sequence, the writer stays fenced on its pending bytes.
   - A segment that provably never reached the store, because it was never sent or the store definitely refused every
     attempt, is withdrawn. The group is then requalified from its staged inputs before its first actual commit, which
     takes the same uncommitted sequence. An absent key is never taken as proof that nothing was written.

   Then every named version, manifest included, is read back again, never trusting a proof from P1 or P2. A lost
   manifest is recreated from its exact spooled bytes under the same digest key. A lost payload is recreated from its spooled bytes, and one transaction re-names the member and supersedes the
   manifest built from the old locator:
   - the superseded manifest's exact never-committed locator is handed to retention as an orphan candidate. An
     uncertain write of it is resolved through its digest key, which no committed record can name; if nothing is
     there yet, the key itself is handed over;
   - the group returns to P2, which writes a new manifest from the current locators under its own digest key.

   The logical revision never changes, the superseded version is never overwritten or deleted, and nothing waits for
   retention. The `publish` segment commits under the operation key `publish:<sha256>`, where the digest covers the
   length-prefixed group and the revision, so every valid identity has a bounded, stable key. In the same transaction
   the group moves to `recorded` at that position. P3 recovery checks that the committed manifest locator is the key
   its UTC anchor, revision and digest fix.
5. `visible` (P4): the writer's own first P4 runs P3 recovery for that revision, reading every required member again
   after the commit. Only when it verifies does the group become visible and release its spooled bytes. An
   interrupted release is finished on resume.

`abandoned` is reachable only before P3. It hands every version the group created, or may have created, to retention
as an orphan candidate. Each recorded version is handed over exactly. A write that may have landed a version not yet
named, whether the first write, a recreated payload or a recreated manifest, is resolved through its key: the version
it holds now is handed over exactly, and otherwise the key itself, with an empty version ID. A store whose profile is
not persistence-qualified is never named by a committed record.

## Journal and authority

- **Ordering and idempotence.** Records are ordered by `(g, seq)`. Sequences have no gaps within a generation, and each
  segment names its predecessor's position and SHA-256. One writer holds one pending segment. Every append carries an
  operation key bound to the canonical SHA-256 of its records. A retry with the same records returns the original
  segment, whether it is still pending or committed but unacknowledged. A retry under that key with any other records
  is `silo.operation-conflict`. That covers a changed live interval and an opening with changed floors. Every write
  attempt of the pending segment is recorded as in flight before it is sent, and its outcome after. A generation
  opening advances the writer generation in the same transaction that makes it pending, so a crash leaves nothing or an
  opening that the same call completes.
- **Authentication.** A segment is applied only when the next segment names its exact SHA-256. With the writer's
  evidence it must instead match the writer's committed head, past which nothing is read as authority. A writer that
  committed nothing proves the journal empty, so even a genesis that landed but never committed is not adopted.
  Without the writer's evidence, as after total loss, the last segment that chains back is accepted as the tail. An
  altered segment that still parses therefore ends the prefix rather than entering the catalog.
- **Writer evidence.** Startup catch-up and a rebuild with surviving State both take `SiloWriterEvidence`: the
  committed head, and the P3s still awaiting their first P4. The authority-unknown interval clears only when the
  committed head is read again with its SHA-256. A P3 awaiting its first P4 is recovered under P3 rules, never as
  settled history.
- **Generations.** The first segment of every generation opens it. A later generation must name the end of the prefix
  it continues. `ISiloGenerationWitness`, supplied by #1972, must prove that exact opening by the SHA-256 of the
  segment that carries it. A witness of another opening of the same generation and prefix, differing in any field,
  proves nothing. Without that proof the prefix ends at the boundary.
- **Trusted anchors.** `SiloCheckpointWriter` writes a checkpoint of every applied record at the cursor. It refuses any
  checkpoint larger than the readers' bound, so it never produces one a rebuild cannot read. Selection starts from the
  predecessor hash named by the first segment after a checkpoint. It then needs an `anchor` record, trusted through
  `ISiloAnchorTrust` (#1972), in the authenticated chain. Finally it reads the anchor's exact version, bounded by its
  length, which a newer version at the same key cannot hide. A replica is read only through the exact version a verified
  receipt names there (`ISiloAnchorTrust.ReceiptedCopy`), never through whatever its key holds now. The same checkpoint
  may be anchored more than once, each anchor with its own exact version or receipt. Every trusted anchor of it is
  tried in chain order before selection falls back. A receipt that names other bytes, another key or another store
  never qualifies its anchor. Only when no anchor of a checkpoint qualifies is an older one tried, then generation 1.
  `SiloJournalRetention.RemovableThrough` permits removing segments only behind the previous trusted anchor.
- **Reporting.** The catalog reports these separately (4.5, 5.4):
  - the authenticated prefix end and why it ended;
  - the complete-publication frontier;
  - whether an authority-unknown interval follows;
  - the publications named past the prefix, as authority-unproven. A complete scan replaces them. A scan that could
    not read everything only adds to them, so a known gap keeps its names while the journal times out. Only work
    beyond the authenticated cursor is unproven. Applying a segment removes what it holds from the set in the same
    transaction, so the prefix and the unproven set stay disjoint in every committed state. Reading the committed head
    again clears them.

  The prefix end, the unproven names and every role disposition the authority-unknown interval implies are recorded in
  one transaction, so a crash leaves all of them or none.

## Catalog file replacement

A rebuild validates the live catalog, including committed WAL pages, on a copy before it touches anything. It then
builds `catalog-index.db.building` and renames it to `.replacement` only when complete and synced. Next it retires the
live catalog by rename, main file first and then its WAL, so committed pages never leave their database. Finally it
installs the replacement and deletes the retired files.

`SiloCatalogFiles.RecoverAsync` runs before the catalog is opened or rebuilt. It first proves, on copies, that every
catalog it could keep, install or put back belongs to this instance and has a supported format and schema. That covers
the live catalog, the replacement and the retired catalog, each with its own WAL. A replacement it would install, or a
retired catalog it would put back, must also be a complete catalog. An empty or unversioned file there is unproven. A
foreign, newer, other-binding or unproven catalog is refused, and every file, including the populated catalog's main
file and WAL and any scratch, is left byte for byte unchanged.

Only then does recovery finish or discard each intermediate state and remove inspection copies a crash left behind.
It removes exactly the shape this code emits: `<file>.inspect-<32 hex>`, optionally followed by a SQLite sidecar
suffix, where `<file>` is the catalog, its replacement or its retired copy. A name an operator gives under the same
prefix, such as `catalog-index.db.backup.inspect-notes`, is never removed.

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
- `deleted` or `already-absent` for each of them under that intent;
- the same proof under that intent for every copy that entered the authoritative set after it. A copy enters on its
  first eligible record, on promotion by a later valid receipt, and on restoration after eviction. Each such copy
  blocks settlement until it is proven absent under that intent, even when a different operation has since removed
  it. A new intent must cover it.

Copy-scoped eviction never makes a role `policy-expired`.

Reopen (`ReopenAsync`, or `ReopenViewAsync` for an exact link that must also name the stream and reference capture)
only reads. It never changes a revision's publication state. Within that operation it verifies the manifest, every
required member and every required reopen dependency by exact read. A verification is reused only for the same exact
version, length and SHA-256. A manifest, or another member read through the same version, never stands in for a
different role, so a receipt that names the wrong version cannot make a revision reopenable. It falls back from the local copy to eligible
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
| #1972 | `ISiloGenerationWitness` (bound to the opening segment's SHA-256), `ISiloAnchorTrust` (trust and the receipted replica version of a checkpoint), `SiloJournalWriter.OpenGenerationAsync`, `SiloCheckpointWriter`, and `SiloRebuildOptions` (`Writer`, `SourceMayBeIncomplete`, `MaximumCheckpointBytes`) |

## Known limits

- A checkpoint carries every applied record, so its size grows with history until the 64 MiB reader bound refuses new
  checkpoints. Correctness holds beyond that, because replay continues from the last readable anchor or generation 1.
  Compaction of settled records is a later measured decision (#1974).
- A rebuild reads the journal from one source. Merging journals from the backup and the archive after total loss
  belongs to #1972.
- Deleting journal segments, issuing deletes and accounting for reclamation belong to #1970. This code only records
  and applies their outcomes, and hands superseded and abandoned versions to retention as orphan candidates.
- Proof never crosses a phase, so each required payload is read three times per publication: its P1 readback, again
  before P3, and again at the first P4. #1968 may replace a readback only with an independently qualified equivalent,
  and its performance evidence must include this cost.
- A payload lost after its P3 may have been written is never substituted. The revision
  stays closed with its P3 recovery outcome and keeps its spool until an eligible copy or qualified remap restores the
  member (#1969, #1971).
