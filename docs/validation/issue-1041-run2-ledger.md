# #1041 correction: writer-maintained retained integrity

Refs #1041. This report supersedes the withdrawn completeness verdict at
`c4069b8c`. Roy approved Option A on Nov 10, 2026 at 14:04 PST / 22:04 UTC
(widget t356s2122, relayed by Kepler): out-of-band tampering moves from the next
protected read to the next full scrub. No other guarantee is relaxed.

Status: implementation/evidence in progress. Required HIGH re-reviews belong to
Kepler; this report is not a Sol/Opus approval or permission to create a PR.

## Sources and scope

The owner read the original issue and all 13 original comments before initial
implementation, and the subsequent correction/approval discussion during run 2.
The original requirements are retained in #1041, including the shipped #1042
finite shared 120-second deadline, transport/authentication protections and
pre-mutation comparisons. The reports for #1559, #1591 and #1618 remain the
attribution sources; their old 35,049 ms/small-fixture results are not current
measurements. The prior full-byte cache did not solve the linear-I/O problem.
The design was pushed before product edits in `1b1bbf1f`, linked from
[issuecomment-6779992529](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1041#issuecomment-6779992529).

Work is isolated on home-dev-03, branch
`kepler/1041-bounded-installation-verification`, originally branched from fetched
`development/v1` `d122742c7b619a887168d3f9e2b4f23e54ba6ee7`.
No production/beta request, token, deployment, live data/container, CLIProxy,
network configuration, PR, review request or merge operation is part of this work.

## What is verified, and when

Each qualified protected request freshly reads/parses/validates configuration
and its 366-day schedule, protected location history/marker/manual state,
installation fields, owner readiness and a small authenticated integrity
checkpoint. It does not enumerate, read, hash or parse retained history, or open
the retained ledger database. A one-second limit applies to each local gate wait.
Unqualified/missing/expired/busy state returns sanitized HTTP 503 with
`Retry-After: 1`; there is no successful identity response or installer bypass.
New clients retry only HTTP 503 carrying the explicit `scrub-pending` header.
The same original finite 120-second CTS covers every attempt, retry delay and
final response body; it is never restarted. Other unsuccessful responses fail
immediately. Exhaustion/caller cancellation retain the #1042 distinction and
sanitized diagnostics; all field comparisons still precede mutation.

The checkpoint is bound to a new process epoch, validator/Common/AgentCore/runtime
identity and current protected-history identity. It includes an authenticated
mutation generation, entry count and set commitment. A new process never inherits
readiness from disk. Endpoint work is independent of retained capture/product
count; current configuration/history size still determines its own validation cost.

A full scrub authenticates the expected-entry set and reads/hash-validates every
current raw-root JSON file and raw journal manifest, including schema and location
provenance. Unexpected additions, deletions and byte changes are drift, even if
the replacement is valid JSON with valid provenance. Per-entry authenticated
proofs plus a protected SHA-512 additive set commitment detect row changes,
deletion and replay. Pending mutation intents have their own authenticated set commitment. A fresh
readonly pathname check prevents an old open SQLite inode from hiding a replaced
durable database during qualification/publication. This addresses a new ledger
path-replacement interaction, in addition to the configuration snapshot race
called out below. The nonce, count, generation and commitments are themselves
protected; the database alone cannot certify a generation.

Full scrubs run during normal startup/configuration load, after unqualified reads
request coalesced revalidation, and every 24 hours. The fast path independently
expires qualification after 24 hours; managed writes cannot extend it. A dirty
read starts at most one host-owned refresh, with a 30-second failed-attempt
cooldown. Caller cancellation does not cancel/restart that refresh; host shutdown
cancels it. No incomplete scrub qualifies a checkpoint. Full scrubs are linear
work and can temporarily refuse protected reads. Per-entry barriers allow managed
writers between observations; finalization revalidates changed/missing keys and
publishes authority/checkpoint under the barrier. Initial commitment checks and
final database replacement/commitment computation also hold the barrier.

**Accepted integrity tradeoff:** unmanaged/out-of-band edits after qualification
may precede a successful protected read until a full scrub detects them. Watchers,
mtime and length are not treated as byte-integrity proofs. Schema/provenance is
checked at managed publication and full scrub; current configuration/protected
identity remain fresh on every protected read. Payload-byte verification was not
part of the original protected metadata scan and is not newly claimed here.

The configuration loader now returns the canonical hash of its exact parsed JSON
snapshot and checks the file again before location-authority publication. This
explicitly touches the previously excluded configuration-file reopen race: an
asynchronous scrub must not freeze stale input as immutable installer identity.
It does not claim a transaction with arbitrary external editors after that check.

## Durable writes and recovery

`retained-integrity.v1.db` is required integrity state, unlike the disposable
`retained-validation.v2.db` parse index. Managed writes persist an authenticated
key/prefix and declared digest (or source mapping/deletion) before changing data.
They validate affected current bytes afterwards, update expected entries and
metadata in a FULL-synchronous SQLite transaction, then atomically/fsync publish
the protected checkpoint. Fast reads wait or refuse while that barrier is held.
Failed/unknown results leave integrity unqualified. A registration barrier drains managed writes that began before an integrity
owner existed, so initial qualification cannot miss their eventual publication.
Nested publication scopes preserve that barrier across async calls. A managed
writer cannot
launder unexplained pre-existing drift by replacing/deleting it. Quarantine can
preserve damaged bytes, but never qualifies those bytes as valid evidence.

A crash before/after data publication leaves authenticated pending intent;
restart performs full current-byte/schema/provenance verification and can accept
only unchanged evidence or the declared valid outcome. Torn results refuse
verification. Offline restore/rollback starts a fresh epoch and requires a full
scrub, including paired ledger/evidence comparison. Incomplete restores and old
writers that changed evidence without this ledger are refused as drift. Restoring
a complete paired backup can requalify; no cached checkpoint is sufficient.
A damaged/unreadable authoritative ledger cannot grant trust and is not silently
reset. Preserve it and the retained evidence for recovery; restore a known-good
paired state or repair against expected bytes through an explicitly authorized
recovery procedure. Do not delete evidence or the ledger to force adoption.

Startup tolerates torn/schema-invalid sidecars in quarantine and frames so the
real reconciler and authenticated recovery UI remain available. A startup scrub
that detects drift retains the original expected set and keeps protected reads
unqualified. Valid contradictory location provenance/protected authority retain
their existing startup refusal. Invalid quarantine evidence is preserved and can
still refuse lifecycle verification; moving it does not erase the finding.

The advisory parse index has no whole-corpus capacity cap. Small transactions
persist only fully validated exact-content/type/history/build proofs, even when
a full read is canceled or fails. Auxiliary JSON is classified once and cannot
satisfy a required frame contract. Index I/O/ENOSPC is best-effort and cannot
roll back durable location authority or leave in-memory location at an old
version. Cleanup is bounded; optional damage is a cache miss/rebuild.

## Managed-writer inventory

| Path | Ledger integration | Scope |
| --- | --- | --- |
| Raw ingress publication and capture journal commit/recover | Declared sidecar bytes before atomic publication; declared capture key/manifest before journal transaction | New raw evidence; schema migration dirties/requires full scrub |
| FileSystemFrameStorageService, CaptureProcessingPersistence, CameraAgentFileSystem | Prepared publication guards; tracked moves/deletes | Raw/preview/derived artifacts and sidecars |
| RawIngressReconciler, DerivedProductReconciler, CalibrationLibraryReconciler | Guarded moves/copies/deletes and tree quarantine | Evidence preserved; invalid evidence never qualifies |
| RetentionBackgroundService, ProjectedSceneRetentionGuard, time-lapse recovery | Guarded per-file or affected-prefix deletions/moves | Expected tombstones prevent missing evidence being silently adopted |
| CalibrationArtifactPublisher, ManualFocusSessionStore | Guarded atomic JSON publication | Manifest/auxiliary JSON inside the registered raw root |
| CaptureSatelliteWorkStore, ProjectedSceneStagingStore, NightlyProductStore | Prepared/guarded publication and cleanup | Auxiliary JSON classified under existing owner contracts |
| SiteProfileStore, OperatorSettingsFile, CameraAgentControlStore | Guarded writes/atomic publication | Fresh owner settings; no cached installer identity |
| OrbitalElementsStore, ManagedTimeDataStore, environmental outbox | Guarded direct atomic publication/deletion | No-op for integrity outside the raw root or non-JSON data |
| Installer backup/restore/rollback | Existing offline lifecycle operations; process epoch invalidates all restored readiness | Paired full verification after restart; no live restore bypass |

Shared host-neutral filesystem ownership remains in Storage.FileSystem; CameraAgent
owns the ledger/writer policy. Binary payloads, JSONL, temporary files and unrelated
SQLite row-state updates were outside the original metadata scan. Prepared JSON
publication is guarded at its final tracked destination. Device-secret/identity
writers remain under their existing separate identity roots. Linux only.

## Production multiplicity

The installer-generated configuration is produced locally by the BCL-only
[configuration generator](issue-1041-run2-configuration-generator.cs.txt); the
fixture rewrites only its two storage roots and seeds a completed synthetic
owner. The default graph declares four ScenePresentation outputs, two
EnvironmentPresentation outputs, one ProjectedScene and one OverlayManifest:
eight structured metadata products when those stages complete. Each has a JSON
payload **and** a `.manifest.json` sidecar (`CaptureProcessingPersistence`). Raw,
RollingCombination, CombinedPreview and PresentationMaterializer add image/binary
artifacts and their sidecars; optional cloud products add further evidence.
Thus the complete successful graph can produce about twelve distinct manifests
and twenty retained JSON files per capture before optional products. The former
262,144-manifest proof cap could therefore be reached near 21,845 such captures,
not 262,144 captures. Outcomes/optional stages/retention affect the actual ratio;
this is a source-verified graph count, not a production capture measurement.

The scale cohorts count manifests, raw-journal rows and captures separately.
The three-product cohort is a conservative synthetic Raw/Preview/AnnotatedPreview
fan-out (100,000 captures, 300,000 sidecars, 100,000 journal rows), not a claim to
reproduce all default graph outputs. Auxiliary JSON correctness is covered by
seven owner-model/layout cases, including a structured metadata payload beside
its manifest. The endpoint takes the same fixed path regardless of either kind.

## Requirement → behavior → evidence

| Requirement/source | Behavior | Local test/evidence | Result |
| --- | --- | --- | --- |
| R1: current costs and 100k+ bounded endpoint | Fixed checkpoint plus fresh current identity, no retained scan; exact product/capture counts | Real endpoint scale, warm and fixture-only evicted at 100,001/300,000 sidecars | Pending |
| R2: tamper/schema/corruption and retained provenance | Approved scrub boundary; managed validation; authenticated expected set; strict qualification | OutOfBandBoundary, LedgerTamper, WarmIndexRejectsInvalid, startup/quarantine tests | Pending final head |
| R3: authoritative identity/readiness and concurrent edits | Exact config snapshot/freshness callback; history/manual/owner state fresh; no fast-path authority changes | QualifiedProtectedRead, StaleBackgroundConfiguration, actual delayed/config/manual/authenticated endpoint edits | Pending final head |
| R4: installer budget/auth/comparisons/pre-mutation | Existing #1042 shared 120s finite policy and transport protections; only explicit pending-scrub responses retry within that budget | Existing owner client/resume tests; upgrade/rollback/backup mismatch/timeout/cancel/plan cases; installer campaign | Pending |
| R5: cancellation/failure/progress | Independent coalesced scrub; canceled full scan saves individually valid proofs only | DirtyEndpoint, MidScanCancellation, optional-index fault tests | Pending final head |
| R6: invalidation/concurrency/writers | Exact expected-entry updates, dirty refusal, changed-generation merge and finite waits | ManagedPublishCopyMoveRetention, concurrent scrub/writer, daily deadline, raw journal integration | Pending final head |
| R7: crash/restore/rollback | New epoch always unqualified; pending declared results verified; no reset | Actual SIGKILL before/after/torn write; paired restore/rollback and incomplete-restore tests | Pending |
| R8: compatibility/adoption | Wire fields/auth unchanged; currently running agent always answers | Matrix below; existing endpoint/CLI contracts | Pending gates |
| R9: HIGH/Tier C validation | Classifier-selected affected gates, canonical coverage and four CI guards | Exact range/classifier and command/TRX ledger | Pending |

## Compatibility

| Installer | Running agent | Behavior |
| --- | --- | --- |
| Old | New | Same success wire contract and comparisons; qualified reads benefit; 503 requires later retry after scrub |
| New | Old | Same finite client deadline/comparisons; old full-scan endpoint can still exceed 120 seconds |
| New | New | Bounded qualified reads; explicit pending responses retry within the original aggregate 120s budget; no identity or mutation until requalified |

Pre-mutation verification always executes on the currently running agent. A
server-only fix cannot unblock pre-upgrade verification of an already-slow old
runtime. The fresh empty instance is an adoption window, not a correctness
assumption. There is no timeout increase, unbounded retry, identity bypass or reset.
`--plan` still returns before the protected pre-mutation read and remains dry-run.
An old agent writing a corpus after rollback cannot maintain a ledger it does not
understand: a subsequent new agent detects that drift and refuses qualification.
Development policy permits such upgrade incompatibility; it is disclosed rather
than silently trusting old-writer changes.

## Measurements and candidate gates

Cache-evicted means file-scoped `fsync` + `POSIX_FADV_DONTNEED`, followed by
`mincore == 0` for each retained data/main-journal/checkpoint file. Construction
closes fixture SQLite pools and checkpoints/truncates the idle raw journal WAL;
transient SQLite SHM coordination pages are excluded. Residual data pages get
at most five short file-scoped retries, then fail the experiment. No global
`drop_caches`, live service, image payload or production storage is involved.
Directory/controller caches and full-machine cold start are not controlled.
Three preliminary baseline attempts failed this precondition: one repeated an
older harness after a concurrent formatter writeback; the diagnostic attempt
identified 240 pinned `.db-shm` coordination pages. These attempts contribute no
baseline performance result. The corrected fixture boundary is applied to both
baseline and candidate. Full qualification, durable-ledger restart qualification and five prepared/durable
auxiliary publications are recorded separately from endpoint timing. Restart
must requalify in a fresh store epoch and reuse all existing artifact proofs;
only the newly published auxiliary state is parsed. The extra restart phase
is candidate-only and follows every timed endpoint/publication sample.

Pending final measurements and immutable candidate. Historical run-1/reviewer
numbers are not represented as results for this implementation. Raw local logs
and disposable fixtures stay ignored under `TestResults/issue-1041-run2`.
