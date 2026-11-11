# Protected installation verification (#1041)

> Superseded completeness verdict: independent Sol/Opus HIGH reviews at
> `c4069b8c` found unresolved linear I/O and startup/optional-cache regressions.
> The original owner completeness verdict is withdrawn. Roy subsequently approved
> the writer-maintained ledger and next-full-scrub out-of-band tamper boundary.
> See the [approved correction design](issue-1041-run2-design.md) and
> [current implementation/evidence report](issue-1041-run2-ledger.md).
> The measurements below remain historical observations, not proof the incident
> loop or capture-count-independent verification is solved.

## Sources and authority

Original issue body and all 13 discussion comments read 2026-11-10 UTC:
<https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1041>.
Roy's Nov 10 “Fix 1041” instruction (t350u, supplied by Kepler) authorizes
implementation beyond the older attribution-only restrictions. Supplied §12
incident figures are signed historical evidence, not measurements in this work.
The incident share is not mounted here. Baseline is freshly fetched
`d122742c7b619a887168d3f9e2b4f23e54ba6ee7` on `development/v1`.
Reuse [#1559](issue-1559-protected-read-attribution.md),
[#1591](issue-1591-schedule-validation-reuse.md), and
[#1618](issue-1618-retained-read-attribution.md); do not re-credit their changes.
Risk HIGH, validation Tier C, target protected profile development-v1.
Independent Sol/Opus reviews are reserved to Kepler by the assignment.

## Requirement/design map (recorded before product edits)

| ID/source | Planned behavior | Required local evidence |
| --- | --- | --- |
| R1 — current issue + Nov 10 scale goal | Replace repeated full parsing with a bounded content-verified summary, if current-tip measurements confirm parsing dominates. Continue reading current file/journal bytes; explicitly retain linear I/O rather than claim constant time. | Equivalent current-tip/candidate endpoint runs at 1k, 10k and 100,001 captures, fixed/solar; time, CPU, allocation, RSS, logical I/O; cold/unchanged/restart behavior. |
| R2 — fresh tamper/schema/corruption | SHA-256 of actual current bytes, not path/mtime/length. Only successful full contract and protected-history validation may produce a proof. Current protected history, marker and manual state remain validated every call. Invalid/unknown proof state rebuilds from evidence. | Same-length/timestamp replacement, malformed/unsupported manifests and journal, protected history corruption/schema, proof corruption/wrong build/history, successful repair. |
| R3 — installation identity/readiness | Actual file load, same-load schedule validation, effective location and current owner readiness on every call. No configuration/identity cache. | Existing real authenticated delayed/repeated/concurrent endpoint regression and actual config/manual edits. |
| R4 — pre-mutation lifecycle | Preserve installer ordering and all identity comparisons for upgrade/rollback/backup; no CLI timeout change, retry or bypass. | CLI timeout/caller cancellation, transport/auth/token sanitization and lifecycle refusal/no-mutation tests; disposable installer campaign. |
| R5 — cancellation/failure | Check cancellation during enumeration, file and journal processing; never persist failed/canceled proof additions. Original authority writes keep existing durability barriers. | Mid-file/journal cancellation, canceled gate wait, fault before summary publication, restart from incomplete write. |
| R6 — concurrency/invalidation | Existing store gate owns summary reads/builds/publication. Proof identity includes full protected-history semantics and exact validator binaries. Current bytes always checked; changed history discards prior proofs. Bound entry count and serialized size; overflow falls back to full parsing. | Concurrent calls, history/manual/config changes, append/replacement, capacity overflow and restart tests. |
| R7 — crash/recovery/durable state | Optional protected summary uses existing confined atomic/fsync publication. It contains only validated content digests tied to history/build identity. Missing/corrupt/incomplete summary never grants authority; rebuild. No journal schema, existing history or recovery-layout conversion. | Durable reload reuses proofs; missing/truncated/tampered/wrong-version summary rebuilds; injected write interruption and clean restart. |
| R8 — compatibility | Wire contract unchanged. Old installer + new agent benefits; new installer + old agent retains old cost/120 s cap. Pre-mutation verification always executes on the running agent. A server fix cannot unblock an already-slow old runtime. | Existing CLI/endpoint contracts, explicit compatibility matrix in final handoff. |
| R9 — evidence/guards | Exact-range classifier, warning-clean Debug/Release, canonical coverage, format, package audit, four guards, full positive Docker-disabled Unit and separate Integration; focused Acceptance/CLI and 100k campaign under heavy lock. | Commands, counts, results, artifacts and exact candidate binding. |

No new installer safeguard is selected: the shared finite 120-second budget and
sanitized distinction from caller cancellation already shipped in #1042.
No deployment, live service, token, production state or PR action is authorized.
The endpoint file-reopen race remains excluded unless implementation changes
its snapshot assumptions; this design adds no configuration memoization.

## Summary design to validate

A bounded set of SHA-256 content proofs avoids re-deserialization, not fresh
inspection. The key includes the complete history (including activation and
supersession times) and both validator assembly MVIDs, so a validator rebuild
or changed history forces full validation. Protect the binary summary with the
existing deployment-location protector; separate magic/version prevents type
confusion. Maximum 262,144 digests (8 MiB raw); no retained payloads or private
paths. Overflow still validates each manifest and simply cannot cache it.

Load a summary under the existing gate; scan current bytes under the same gate;
collect new proofs only for fully valid manifests with valid provenance. Publish
only after both scans, reconciliation and original durable state writes succeed.
No failed/canceled scan publishes additions. A summary publication I/O failure
refuses the request, preserving the existing durable publication barriers; the
next request/restart revalidates rather than treating an incomplete write as
proof. This is a stated availability cost of summary publication. One SQLite reader maintains its
existing statement snapshot. External writes after a file is read or after a
SQLite snapshot starts retain the existing read-boundary semantics; this is not
a cross-file atomic snapshot. Exact bytes at that boundary remain the authority.
The optional summary is advisory: deletion/corruption/version mismatch rebuilds,
never accepts current corrupt artifact evidence. Frame sidecars and raw journal
blobs require the artifact contract. Other JSON models sharing this root retain
their existing owners: site/control records, staging, structured metadata and
structured-product sidecars must not be mistaken for artifacts or cached as
artifact proofs. A root claiming `v2`, or both `descriptor` and
`relativeArtifactPath`, is also an artifact claim and must validate. Malformed
JSON is rejected; valid JSON for other contracts remains outside this store's
artifact validation scope.

This keeps the current filesystem/recovery primitives selected by #1607 and
[#1615](upgrade-recovery-cost-1615.md), with no DB migration, backend conversion,
checkpoint protocol, restore ordering, or new adoption requirement.

## Results

The current-tip baseline completed using the test-only harness at `c31afe4b`;
product sources are byte-identical to `d122742c`. The initial 100k attempt failed
in the fixture checksum helper, not endpoint validation; the bounded streaming
correction is retained in the harness history. The successful baseline uses
one affined CPU, a 384 MiB managed heap, native Linux x64 Release / .NET 10.0.14,
SDK 10.0.402, an invalid Docker endpoint and synthetic catalog/evidence only.

Each count has that many distinct captures, each represented by one file and
one journal manifest (therefore 200,002 manifest reads at 100,001 captures).
Construction uses current contract serialization, the real v15 journal schema,
foreign-key assignments and one bulk disposable transaction, with no image
payload I/O. This is a scan workload, not acquisition/durable-ingress throughput.
Each fixed/solar fixture has a first populated read, five settling reads and five
measured sequential reads; retain phase/JIT differences rather than pool them
or infer percentiles. Whole-process allocation includes the in-process host/client.

| Captures | Fixed baseline median, ms | Solar baseline median, ms |
| ---: | ---: | ---: |
| 1,000 | 242.64 | 89.30 |
| 10,000 | 856.59 | 398.35 |
| 100,001 | 3,393.07 | 4,249.77 |

At 100,001, median process allocations are 2,420.99 MiB fixed / 2,427.04 MiB
solar. These are this host's current software measurements; neither the historic
35,049 ms incident nor the earlier small-fixture ~180 ms medians are substituted.
Candidate comparative results and completeness evidence follow below.

### Source findings and scope decisions before product changes

The current-tip 1k/10k baseline is running on disposable state. At 100,001,
the fixture checksum helper (not the endpoint) exhausted the 384 MiB heap by
materializing the whole SQLite file. Replace that helper with a 64 KiB streaming
hash, preserving its exact checksum input; retain the failed run and rerun.

Two original-source integrity gaps need explicit treatment rather than claims
that the old code rejected every invalid state:

- `ParseManifest(...).Document?.Manifest` silently ignores parse/schema failures.
  Reject invalid frame sidecars/raw journal manifests and JSON claiming the
  artifact contract, including unsupported schema. Other owned JSON models in
  the root retain their original policy; they never produce parse proofs. This
  can refuse retained corrupt/quarantined JSON that the old scan ignored, while
  avoiding a false artifact requirement for site/control/structured models.
  Scan all journal manifests rather than a text LIKE filter that a
  malformed/re-encoded location property can evade. Keep valid artifacts without
  location under their existing no-provenance-comparison rule.
- Upgrade/rollback pre-mutation reads inspect owner state but compare the complete
  identity only after mutation; running backup has no pre-mutation protected read.
  Use the existing complete field comparator before image mutation, and before
  running backup's pause/stop. Keep stopped backups offline and plans dry-run.
  Add real scripted-transport mismatch/timeout/failure/cancellation tests for all
  three paths. Preserve the existing budget and transport implementation.

These changes are required by the assignment's explicit integrity/pre-mutation
acceptance. They are called out for Kepler's HIGH reviews. No existing durable
format is replaced, and no configuration snapshot/cache is introduced.


### Interaction correction before broad execution

The initial Debug gate passed on `978dedfc`; its Release gate was deliberately
terminated before Unit/Integration/scale execution after source inspection found
that requiring every JSON file to be an artifact would reject legitimate site,
lifecycle-control, staging, satellite and structured-product state. The correction
retains strict frame/journal/artifact-claim validation and the original ownership
of other JSON models. Eight additional Unit cases cover these interactions and
replacement of a real frame sidecar with `{}`. The stopped gate is retained and
is not claimed as a completed candidate gate. No unmodified long test suite was
repeated. The corrected candidate runs the full gate below.


### Actual process-crash evidence

The [standalone probe source](issue-1041-summary-crash-probe.cs.txt) and
[driver](issue-1041-summary-crash-probe.py.txt) compile against the candidate's
actual Release DLLs with the pinned SDK, in private TMPDIR state. The driver
requires a new disposable root marker and uses a separate synthetic Data
Protection purpose with keys under that fixture's `.location/keys`. It never
loads an installed application, token, configuration or data root.

The first process receives SIGKILL after the cache temporary file has been
flushed but before rename. A second process ignores that orphan and rebuilds
from current evidence (one full parse). A third process reuses the completed
protected summary (zero parses, one proof hit). A fourth corrupts the current
manifest and must reject it. The first local probe passed with expected exit
`-9`, then `0/0/0`; one orphan survived the kill and never became authority.
The stable-candidate gate ran this probe again, recording its candidate SHA,
compiled Common DLL hash and exact source hash in ignored results.

Reproduce after building Release from the candidate, with the host heavy lock:
`flock -o /tmp/hvo-heavy-gate.lock python3 docs/validation/issue-1041-summary-crash-probe.py.txt`.
The extended six-process version additionally rejects altered provenance with
unchanged file length/timestamp and proves cancellation while waiting on the
store gate leaves proofs unchanged and the next read usable. The final process
then rejects malformed JSON. Set a short private `TMPDIR` first. The `.txt` sources are explicit validation
artifacts, not new solution projects or production entry points.


### Candidate comparison

Runtime/test sources are pinned to `8879ad2ef6ee7236a872800b8a5a8adb304ae96e`.
The final evidence commit changes documentation only; its equivalence check and
exact-range selector are recorded in the local-gate ledger. No protection,
identity or retained bytes are substituted in either measurement cohort.

| Captures | Fixed baseline → candidate, ms | Solar baseline → candidate, ms |
| ---: | ---: | ---: |
| 1,000 | 242.64 → 41.66 | 89.30 → 44.00 |
| 10,000 | 856.59 → 208.21 | 398.35 → 133.97 |
| 100,001 | 3,393.07 → 1,149.73 | 4,249.77 → 1,063.88 |

These are five-sample repeated-read medians, not percentiles or confidence
intervals. At 100,001 the candidate ranges are 1,078.96–1,261.05 ms fixed and
1,053.69–1,116.85 ms solar. First populated reads, which cannot reuse retained
proofs, are 4,127.91 → 3,584.69 ms fixed and 4,173.51 → 2,419.95 ms solar.
Smaller first-read comparisons include regressions (10k fixed 1,426.78 →
1,626.96 ms and solar 600.18 → 691.44 ms); do not infer uniform cold improvement
from a process with sequential fixtures and changing JIT/GC phases.

At 100,001, process CPU medians fall from 3,388.65 → 1,146.30 ms fixed and
4,246.54 → 1,059.31 ms solar. Allocation falls from 2,420.99 → 635.69 MiB
fixed and 2,427.04 → 641.73 MiB solar (about 74%). Logical read bytes remain
approximately 654 → 657 MB, and read syscalls approximately 201k → 200.5k;
physical `read_bytes` is zero in these warm-filesystem samples. Each corpus has
485,982,652 file+journal manifest bytes. This optimizes parsing/validation CPU
and allocations, **not filesystem traversal or total input I/O**. Time and total
allocation still grow with retained count. The proof capacity/serialized state
is bounded, not the endpoint's worst-case runtime or I/O. Over-capacity digests
are validated normally without being remembered.

Semantic identity fields other than the configuration hash and disposable root
match between revisions, as do capture counts,
manifest bytes, ordered input hashes and deployment-location hashes. Each
fixture has its own absolute ingress/catalog paths in its configuration, so its
canonical configuration SHA-256 intentionally differs across revisions. Neither
the runtime nor the installer ignores that field; within each fixture every
repeated identity, including that hash, must equal its first response. Retained
pre/post hashes match within every fixture. At 100,001, the ordered manifest
SHA-256 is `83CF8577AEB83448A215395BC5869D5C238DB7706EAD43767C8911E9DA4E060C`.
The [summary](issue-1041-scale-summary.json) and
[raw numeric archive](issue-1041-scale-evidence.json.gz) retain all first/repeated
samples and input hashes; disposable root strings are redacted. Reproduce with
`LargeRetainedCorpusRecordsCurrentEndpointCost` in the Acceptance project,
`HVO_INSTALLATION_READ_EVIDENCE_DIR` pointing to disposable state,
`HVO_ATTRIBUTION_REVISION` set to the candidate, `DOTNET_PROCESSOR_COUNT=1`,
`DOTNET_GCHeapHardLimit=0x18000000`, one affined CPU and the heavy lock. The
local-gate ledger records the exact invocation. Baseline product equivalence is
recorded in the [environment record](issue-1041-baseline-environment.json).

The primary cohort's RSS **increases**: 335.99 → 372.59 MiB fixed and
400.54 → 494.46 MiB solar at 100,001 (candidate solar maximum 504.59 MiB).
A 384 MiB managed-heap limit is not a process RSS cap. Supplemental GC evidence
below separates live managed state from transient/committed memory; it does not
replace or improve these primary observations.



### Working-set investigation

The supplemental [candidate GC samples](issue-1041-memory-scale-evidence.json.gz),
[candidate exit snapshots](issue-1041-memory-exit-results.json) and
[baseline exit snapshots](issue-1041-baseline-memory-exit-results.json) preserve
a separate diagnostic cohort. The baseline is rebuilt from a disposable archive
of `c31afe4b` (product-equivalent to fetched `d122742c`) with zero warnings/errors.
Both run the same six fixtures, CPU affinity, heap limit and BCL-only
[startup hook](issue-1041-memory-exit-hook.cs.txt). The
[candidate driver](issue-1041-memory-diagnostic.py.txt) and
[baseline driver](issue-1041-baseline-memory.py.txt) contain exact commands.
Forced gen2 collections occur **only at process exit**, after all timed work and
fixture disposal; they never run in product code or primary measurements.

| Test-host process, MiB | Baseline before → after exit GC | Candidate before → after exit GC |
| --- | ---: | ---: |
| Estimated managed bytes (`GC.GetTotalMemory`) | 139.92 → 107.71 | 207.94 → 120.97 |
| GC committed bytes | 165.80 → 161.51 | 268.22 → 259.85 |
| Last-GC fragmented bytes | 14.81 → 18.15 | 9.86 → 46.34 |
| RSS | 399.84 → 395.53 | 508.04 → 499.90 |

The extra post-collection live estimate is 13.26 MiB; additional GC commitment
is 98.34 MiB, closely tracking the 104.37 MiB additional RSS. Fragmentation alone
is 28.19 MiB higher; most of the extra commitment is free/fragmented GC memory,
not additional live corpus objects. Source inspection accounts for the new
memory pressure: each scan has two bounded value-key hash sets, plaintext and
protected summary buffers, and resize arrays on the large-object heap. At
100,001 entries, two .NET hash-set bucket/entry arrays at capacity 156,437 occupy
about 13.13 MiB before small object headers, consistent with the live-estimate
delta. This is a footprint calculation, not a per-type heap/root attribution;
no memory dump is taken. The old host/test harness already retains about
108 MiB managed state after collection.

Candidate gen0/gen1 counts fall (3,105/796 versus baseline 8,408/2,266), while
gen2 counts rise (16 versus 6) before the diagnostic collections, consistent
with less short-lived parse churn and new large-array pressure. Per-read
candidate samples also show GC commitment/fragmentation carried from the 100k
fixed fixture into the small solar fixtures. The 384 MiB managed-heap cap is
satisfied throughout; it does not constrain native/JIT/GC mappings or RSS.

The measured RSS increase is an explicit CPU/allocation-versus-working-set
tradeoff with bounded proof structures. It is not an RSS optimization or proof
of an arbitrary-duration memory plateau. Retaining fewer proofs or reusing
buffers could reduce that pressure, but no speculative GC/pooling product
change is introduced. Physical-host memory/capture-load qualification remains
a stated adoption limit. These supplemental exit observations explain the
primary regression; they do not replace the primary times/RSS values.

### Installer/agent compatibility and deployment boundary

| Installer | Running agent | Result |
| --- | --- | --- |
| Existing installer | New agent | Same endpoint/authentication/response fields; repeated content validation can reuse proofs. Existing installer comparison behavior remains its own. |
| New installer | Existing agent | Same wire contract and finite 120-second budget; complete pre-mutation comparisons run, but the old endpoint still has the old cost and can time out. |
| New installer | New agent | Fresh current-byte validation plus proof reuse, and complete identity comparisons before upgrade/rollback or running-backup mutation. |

Pre-mutation verification always executes on the **currently running agent**.
A server-only fix cannot unblock pre-upgrade verification of an already-slow
old runtime. The newly empty instance is an operational opportunity for the
first fixed image, not a correctness dependency; no deployment is performed by
this work. No timeout increase, infinite retry, bypass or installer identity reset
is introduced. Backup of a stopped instance keeps the existing offline policy;
plans remain dry-run.

### Integrity and remaining limits

No integrity weakening is selected. Current file/journal bytes, full contract
and provenance validation on every proof miss, protected history/marker/manual
state, actual configuration/schedule, owner readiness, tokens, redirect/proxy
refusal, sanitized timeout/cancellation distinctions and field comparisons
remain authoritative. Stricter admission rejects corrupt/unsupported frame or
journal manifests that the older code silently ignored; previously tolerated
bad retained state can therefore refuse verification until repaired through its
own authorized recovery process. Other JSON contract owners are preserved.

The existing gate serializes store operations; concurrent endpoint callers
observe fresh per-load configuration and do not share partial proof additions.
The original excluded configuration-file reopen race is unchanged. External
edits after a file read or the SQLite statement snapshot remain outside that
read's boundary; no cross-file atomic snapshot is claimed. A validator/runtime
build change or history edit invalidates the entire proof set. First reads,
restores without summaries, newly acquired manifests and over-capacity entries
still pay full parsing cost. Missing/corrupt summaries rebuild; failed summary
publication refuses the request and retries on the next read rather than
accepting partial proofs. The summary adds bounded write/storage and transient
memory cost. There is no journal schema migration or restore-format conversion.

This host's constrained software evidence at 100,001 captures is well below the
existing 120-second deadline, including the first populated read. It does not
qualify Raspberry Pi storage, disk-cold latency, simultaneous capture throughput,
or arbitrary corpus sizes. Unchanged metadata I/O can still dominate on slow
storage; physical-host performance qualification and production adoption remain
Kepler/Roy's subsequent decisions.


### Requirement → behavior → local evidence

All runtime/test evidence below is bound to `8879ad2e`; the evidence-only
commit preserves those source trees. The original design map above remains the
record of intent, rather than replacing original requirements with generated
acceptance criteria.

| Requirement | Implemented behavior | Expected → observed local evidence | Owner status |
| --- | --- | --- | --- |
| R1 — current-tip profile and 100k scale | Persisted, authenticated, capacity-limited content proofs; actual bytes always hashed. | Same current-tip/candidate corpora at 1k/10k/100,001; repeated 100k fixed 3,393.07 → 1,149.73 ms, solar 4,249.77 → 1,063.88 ms; CPU/allocation and input/output hashes recorded. RSS tradeoff analyzed separately. | PROVED for stated software cohort; linear I/O and physical qualification limits explicit. |
| R2 — fresh tamper/schema/corruption | Changed bytes cannot hit an old proof; history/marker/manual/configuration validated afresh; invalid frame/journal/artifact claims refused. | `AdvisorySummaryDamageRebuildsAndCannotHideFreshCorruption` (6 faults), `WarmSummaryRejectsInvalidCurrentManifestInEitherSource` (6 cases), frame replacement/auxiliary JSON cases; authenticated endpoint rejects current file/journal/config/history corruption and recovers after repair. Extended process probe covers unchanged length/timestamp. | PROVED. |
| R3 — current identity/readiness and real edits | No configuration cache; actual load/owner readiness each request; full existing installer comparator used before mutation. | `RepeatedDelayedAndConcurrentReadsObserveActualEditsAndRejectInvalidState`: denied auth never reaches loader; temporary-password readiness; delayed config edit, changed schedule/hash, four concurrent reads, actual manual-location change, invalid config/history and repaired current identity. | PROVED; original file-reopen race unchanged. |
| R4 — lifecycle ordering, finite budget, transport | Upgrade/rollback compare before image mutation; running backup compares before pause/stop/archive mutation; stopped backup remains offline; plans return first. | `ProtectedVerificationRefusesBeforeLifecycleMutation` (15 cases): mismatch, deadline, caller cancellation, HTTP failure and plan for each path. Existing owner-client (29) and resume deadline/cancel (2) cases pass. Disposable campaign exercises successful lifecycle paths. | PROVED by focused and canonical selections; campaign result recorded below. |
| R5 — cancellation/failure isolation | Cancellation during traversal/waits/publication; failed scans cannot publish additions or cached identity. | Mid-file and mid-journal cancellation; canceled/failed summary publication; gate-wait cancellation; next read/restart revalidates and succeeds. Existing timeout/caller-cancellation and sanitized diagnostic checks remain. | PROVED. |
| R6 — concurrency/invalidation/capacity | Gate serializes each local summary; full history/build identity; observed current-byte set replaces previous proofs only after success. | `ChangedHistoryInvalidatesProofsAndConcurrentReadsKeepCurrentValidation`; repeated/restart/append/replacement/deletion cases; actual config/manual edit endpoint test; bounded 262,144-entry format and overflow miss; wrong identity rebuild. | PROVED. |
| R7 — durable state/crash/recovery | Existing confined atomic/fsync publication; missing/invalid optional proof state rebuilds; original protected authority and DB schema unchanged. | Actual SIGKILL after flush/before rename; second process parses, third process reuses persisted proof; later tamper rejected. Missing/truncated/tampered/schema/identity/oversize summary and publication fault cases pass. | PROVED. |
| R8 — compatibility | Endpoint wire fields/auth unchanged; no timeout increase/retry/bypass; verification always calls current running agent. | Existing client/endpoint contracts and lifecycle tests; explicit matrix and old-runtime limitation above. No installed system is contacted. | PROVED for contract/software behavior; deployment N/A by authorization. |
| R9 — HIGH/Tier C evidence and canonical gates | Classifier selects exact affected plan; complete local gate with positive Docker-disabled Unit and separate isolated Integration. | Final command ledger, counts, coverage, guards, builds, publish/migration and installer evidence below. | PROVED; all 56 selected canonical gate commands pass. |

Normal, negative, failure, cancellation, recovery, durable-state and interaction
cases are represented by the rows above, rather than inferred from green builds.
Hardware capture/roof/safety actuation, network rollout, live installation-token
use, production service health and physical-disk throughput are N/A to this
explicitly disposable server/installer implementation assignment. No hardware or
production result is asserted. Installer compatibility is contract-tested;
production adoption requires a later authorized operation.


### Canonical local gate

The exact-range selector is
`scripts/ci:classify pull_request d122742c7b619a887168d3f9e2b4f23e54ba6ee7 8879ad2ef6ee7236a872800b8a5a8adb304ae96e`:
`mode=full complete=false deployment=true deployment_installer=true cameraagent=true combined=true delivery=true`;
all other lane flags false. The protected target profile is **development-v1**,
whose required checks are Preflight and Build and Unit. No workflow is dispatched;
these local Tier C checks do not assert protected-CI or production qualification.

The [gate ledger](issue-1041-local-gate.json) records exact commands, attempts,
durations, source-tree hashes, canonical TRX counts/checksums and artifact hashes.
All builds/tests use the private short TMPDIR, SDK 10.0.402, Release runtime
10.0.14 and the host heavy lock for suites/campaigns/benchmarks. Unit and
measurement/probe jobs use an invalid Docker endpoint. Integration and the
installer campaign explicitly use the verified local home-dev-03 daemon with
disposable fixtures; external daemon/S3 settings are unset.

Tool/solution restore, warning-clean Debug and Release solution builds (zero
warnings/errors), full `dotnet format --verify-no-changes`, package vulnerability
audit, all four CI-control guards, operations-doc audit and full category audit
pass. The category inventory is Unit 15,743 / Integration 1,193 / Manual 305.
The canonical positive Unit selections execute 15,740 passes and three existing
not-executed cases: the unavailable separate HYG 4.4 cross-lineage prerequisite,
an OSX-only case on Linux, and a provider whose ambiguous-delete state is
unreachable. No issue-specific test is skipped or counted as a pass. Integration
passes all 1,186 canonical cases, including all 27 standalone Acceptance cases;
the separate architecture/publish selection passes seven more, covering all
1,193 discovered Integration cases.

Selected canonical component coverage gates pass:

| Component | Line | Branch |
| --- | ---: | ---: |
| cameraagent | 90.5096% | 77.3416% |
| combined | 34.4249% | 19.9941% |
| delivery | 92.4801% | 78.6718% |

CameraAgent/replay-runner and installer component publish checks, architecture
publish checks, CameraAgent canonical migration/model checks and coverage
non-regression policy pass. The 58-case protected-location class includes 27
new cases, with the existing authenticated real-edit endpoint test, 29 existing
owner-client cases, two resume timeout/cancel cases and 15 new lifecycle refusal/
plan cases also covered by the canonical selections. Standalone process/crash
and memory diagnostics are supplemental evidence with sources linked above.

Validation setup failures are retained as failed attempts, followed by focused
successful retries: the category wrapper initially assumed its standalone tool
was built by the solution; the Acceptance browser prerequisite was absent; and
the coverage-policy wrapper passed a remote-prefixed branch where the script
adds `origin/` itself. The extended probe initially failed compilation because a
span receiver crossed an await; reading into a local array fixed that probe-only
assertion. No runtime probe ran in that failed attempt. These are not reported
as product passes, and unchanged successful long selections were reused.

The complete `scripts/test:deployment-installer` campaign passes on disposable
local state, including installer layout/help/wizard dry-run checks, state
preflight, routine swap upgrades, snapshot-taking migration upgrades, real-store
rollback, reset/reinstall rollback, backup/restore, preserved owner settings and
same-email owner restoration. Its production catalog input is the available
HYG v4.2 bundle, never an installed CameraAgent root. No live upgrade is run.
All 56 selected canonical commands have a final successful result (59 attempts;
the three setup/invocation failures and their corrections are retained).

Owner completeness verdict: **RESOLVED for the assigned implementation and
local-evidence scope**, with no remaining required software behavior/evidence
gap. The scope uses the assignment's measured-under-120-second alternative;
it does not claim capture-count-independent I/O or physical qualification.
Required independent **Sol and Opus local HIGH reviews with completeness
verdicts remain pending with Kepler**, as explicitly reserved by the assignment.
The issue stays open; this is the candidate/evidence handoff for those reviews.
Final handoff records the pushed head and verifies that this evidence-only
commit preserves `src`, `tests`, `scripts`, `.github` and toolchain/package inputs
from the tested immutable runtime candidate. Protected CI and PR lifecycle are
subsequent Kepler work.
