# CameraAgent Silo catalog and durable records

Status: implementation of [#1967](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1967) under the
[Silo storage and cutover contract](../planning/cameraagent-silo-cutover.md) (#1965). It covers the contract's
#1967 slice: sections 4.2–4.7, 5.2, 5.3 and 6.1–6.3. The code is in
`src/HVO.SkyMonitor.CameraAgent.Common/Silo/`. No host, installer or pipeline uses it yet. #1966 binds the stock Silo
store, and #1968 wires publication into the centered pipeline. Contract section numbers below refer to that contract.

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
A database is checked from its header bytes before it is opened for write. Its schema must then match the canonical
definition exactly, and a database bound to another instance, store or bucket is refused.

The storage binding identity is the SHA-256 of `hvo.cameraagent.silo.storage-binding/1` and the length-prefixed
instance ID, store ID and bucket (4.2). #1966 writes the instance marker and must use the same derivation.

## Object namespace and headers

Payloads are content-addressed under `p/full`, `p/display` and `p/meta` (4.3). Every object is created once, with
`If-None-Match: *` and a supplied SHA-256 checksum, and carries the stable headers `hvo-sha256`, `hvo-binding` and,
for metadata objects, `hvo-format`. These headers identify an object and help diagnose it. They never prove its bytes
(F13). Physical proof is always an exact-version GET with an independently computed length and SHA-256.

## Physical split and backup inventory

The State directory holds two databases with different recovery properties (4.6). A rebuild replaces only the index.

| State | Rebuildable from Silo | Needs the instance backup | Why |
| --- | --- | --- | --- |
| `catalog-index.db`: publications, members, dependencies, layer bindings, roles, locators, copies, remaps, expiry intents and outcomes, survivor reservations, live intervals, pins, policies, floors, generations, anchors, cursor, authority | Yes, within the authenticated journal prefix | No. Backing it up only shortens recovery. | Derived from the journal, the manifests and exact-version reads |
| `publication-state.db`: journal writer head and its one pending segment, staged groups and their members' qualified locators, orphan candidates | No | Yes | Staged work and the pending commit are not yet in the journal. The head authenticates the journal tail after catalog loss. |
| `spool/`: exact bytes of the pending segment and of staged manifests | No | Yes, while `publication-state.db` references them | They are needed to replay an uncertain write with identical bytes |
| Payload spool (#1968), admission journal and sequences, processing executions and pins, schedules, rig and calibration library, restore and replay leases | No | Yes | Contract 4.6. The capture-sequence floor is also journaled in `generation-open` records. |
| Identity, Data Protection keys, provisioning, settings, automations, site profile, deployment location, orbital and space-weather stores, time data | No | Yes | Contract 4.1 and 8.5. None of it is published to Silo. |
| Restore cache and FFmpeg workspace | Not needed | No | Bounded scratch |

Silo `meta/`, `m/`, `j/` and `c/` are covered in full by the sealed backup (#1972). Archive replication (#1969)
covers them and the selected `p/` classes (4.5).

## Publication

Publication moves through these states, recorded in `publication-state.db` (5.3):

1. `staged`: the draft is fixed. Staging the same draft again is idempotent. A different draft for the same revision,
   or an artifact revision staged or published with another digest, is a conflict and stages nothing.
2. `payloads-qualified` (P1): each payload is created from its exact spooled bytes, read back and qualified. A version
   lost before readback is replayed, and only the new locator is named.
3. `manifest-qualified` (P2): the manifest names only qualified locators. Its exact bytes are spooled before the first
   PUT.
4. `recorded` (P3): the `publish` segment commits. In the same transaction the group moves to `recorded` at that
   position, so a crash leaves both or neither.
5. `visible` (P4): the catalog indexes the original P3 position. Only then are the spooled bytes released.

`abandoned` is reachable only before P3. It hands every version the group created to retention as an orphan
candidate. A create-only conflict compares SHA-256: equal bytes mean an earlier write succeeded, and different bytes
stop that group, or the writer, without replacing anything. A store whose profile is not persistence-qualified is
never named by a committed record. Publication stays staged, and nothing is written to the store.

## Journal and authority

- **Ordering.** Records are ordered by `(g, seq)`. Sequences have no gaps within a generation. Each segment names its
  predecessor's position and SHA-256. One writer, with one pending segment, makes sequence reuse impossible.
- **Authentication.** A segment is applied only when the next segment names its exact SHA-256, or when it is the tail.
  An altered segment that still parses therefore ends the prefix rather than entering the catalog. When the writer's
  head survives in `publication-state.db`, a rebuild also checks the tail against it and exposes a lost tail.
- **Generations.** The first segment of every generation opens it. A later generation must name the end of the prefix
  it continues, and its witnesses must prove it through `ISiloGenerationWitness`, which #1972 supplies. Without that
  proof the prefix ends at the boundary.
- **Trusted anchors.** `SiloCheckpointWriter` writes a checkpoint of every applied record at the cursor. The checkpoint
  becomes a trusted anchor only when all of these hold:
  - an `anchor` record committed after it names the checkpoint's exact version and SHA-256;
  - its bytes verify;
  - `ISiloAnchorTrust` (#1972) confirms the seal evidence.

  Replay starts at the newest trusted anchor and falls back to the previous one. Otherwise it starts at generation 1.
  `SiloJournalRetention.RemovableThrough` permits removing segments only behind the previous trusted anchor.
- **Reporting.** The catalog reports these separately (4.5, 5.4):
  - the authenticated prefix end and why it ended (`tail`, `gap`, `invalid`, `chain-mismatch`,
    `unproven-generation`, `source-unavailable`, `possibly-incomplete-tail`);
  - the complete-publication frontier, which is the first publication whose manifest did not verify;
  - whether an authority-unknown interval follows;
  - the publications named past the prefix, as authority-unproven.

  A rebuild from a journal replica that may lag (`SourceMayBeIncomplete`) always reports an unknown interval.

## Eligibility and dispositions

A named locator is eligible until its exact copy is evicted. A `copy` or `remap` is eligible only when it was recorded
after the role's P3, inside the prefix, and is bound to the same identity, revision, length and SHA-256. A remap must
also be persistence-qualified (4.2). Matching keys or bytes alone never qualify.

Role dispositions come from journaled state and exact-version probes, local copies first:

- `available` at a named source;
- `unverified`, or `temporarily-unavailable` while a source cannot be checked;
- `missing` only when every eligible source was checked and no unknown interval could hold a later copy;
- `policy-expired` only under a settled role-scoped intent;
- `conflict`.

Copy-scoped eviction never makes a role `policy-expired`. A role expiry settles only when all of these hold:

- the intent covered the complete authoritative copy set;
- every target is `deleted` or `already-absent` under that intent;
- no copy remains outside the intent.

Otherwise the role availability record is kept and rejected, with its reason.

P3 recovery at startup (`SiloCatalog.CatchUpAsync`) requires the manifest and every required member to verify before
P4. A timeout leaves the revision closed as `temporarily-unavailable`, and a later success brings the same P3
revision to P4. A rebuild verifies each manifest and leaves member roles `unverified` for the lazy phase
(`VerifyRolesAsync`). An audit (`includeAvailable`) re-reads available roles, so a deleted or damaged version behind a
valid catalog is found (#1059 negative control).

Exact reopen (`ReopenAsync`) requires the manifest, every required member and every required reopen dependency (6.1).
For centered views, `SiloCenteredViews` keeps every layer binding exactly. It marks evidence as a required reopen
dependency only for `Ready` and `Partial` layers. Pointers to the first, latest and last live revision come from the
publish order and `live-issue` records (6.3).

## Seams for downstream issues

| Issue | Seam |
| --- | --- |
| #1966 | Implement `ISiloObjectStore` on the pinned stock Silo. Set `PersistenceQualified` only for the qualified profile. Write the instance marker with the storage binding identity above. |
| #1968 | `ISiloPayloadSpool` reopens and releases payload bytes. `SiloPublisher` stages `SiloGroupDraft` values and resumes on startup. `SiloCenteredViews` builds view sections and reopen dependencies. Host and DI wiring belong here too. |
| #1969, #1970 | Append `copy`, `reserve-survivor`, `expiry-intent`, `expiry-disposition` and `availability` records through `SiloJournalWriter`. `SiloPublicationState.ListStagedLocatorsAsync` lists versions that staged work still needs. |
| #1971 | `ReopenAsync`, `VerifyRolesAsync` and the role and locator reads |
| #1972 | `ISiloGenerationWitness`, `ISiloAnchorTrust`, `SiloJournalWriter.OpenGenerationAsync`, `SiloCheckpointWriter`, and `SiloRebuildOptions` (`KnownHead`, `SourceMayBeIncomplete`) |

## Known limits

- A checkpoint carries every applied record, so its size grows with history. #1974 measures whether compaction of
  settled records is needed against the RTO budget.
- A rebuild reads the journal from one source. Merging journals from the backup and the archive after total loss
  belongs to #1972.
- Deleting journal segments, issuing deletes and accounting for reclamation belong to #1970. This code only records
  and applies their outcomes.
