# #1041 bounded startup correction

Refs #1041. Correction handoff for implementation head
`cc0771f2999b13c1e5142db82f9aa7629e49cd36`, including the fresh normal merge of
origin/development/v1. The delivery commit adds evidence only; its exact SHA is
reported in the thread and its source/test/build trees match this validated head.
No PR/readiness/review verdict is claimed. Kepler owns Sol and Opus HIGH re-review.
Roy's t410u direction on Nov 11 at 7:03 PM PST supersedes the broad rerun plan:
finish the affected CameraAgent host Integration selection, retain earlier passes,
and skip other full/long suites, coverage sweeps and extra process probes.

The original [R1–R9 crosswalk](issue-1041-run3-high-corrections.md),
[Option A design/evidence](issue-1041-run2-ledger.md), and
[#1559](issue-1559-protected-read-attribution.md),
[#1591](issue-1591-schedule-validation-reuse.md) and
[#1618](issue-1618-retained-read-attribution.md) attribution reports remain
requirements. The original issue body and all 37 comments were refreshed read-only;
their hashes match the previous source snapshot. Roy's Nov 11 decisions, supplied
by Kepler as t388u/t389u, require recent-window startup and background history
qualification. They supersede the old full-inline-startup behavior. Roy's approved
out-of-band detection tradeoff is unchanged.

The first row-MAC reader is `87f7342ed1dcf37477ee88b14edd39e7cc84d06a`.
The final image minimum reader is the actual combined ledger/environmental reader
`53b5756ddbd26dc5ea623ecd34d34d2de2672f05`; this preserves both migration
checkpoint boundaries after the required fresh integration merge. Normal integration
merges `67349635cac270299eb86fb8e157d32fdb674573` and `2a75557c` incorporate
development tips `5f306ea41710f1936a456a59e65e52e4d9f630de` and `d0f56608`.
Normal merge `cc49c13bbf622b08971e5b44e75b4ab7bf532e9c` incorporates
development tip `72d0f66b0704a9656b312295443c19489dfdf067`. The latest fresh normal
merge, `53b5756ddbd26dc5ea623ecd34d34d2de2672f05`, incorporates
`c12d4d120c0e65d9fefa09c8fd1e4f0dbc5eae07` (environmental provenance and
schema-4 retention). The affected host Integration selection is recollected; broader reruns are
limited by Roy's later t410u direction. Changelog entries are preserved;
conflicting inventories combine both deltas, then actual discovery adds the
run-5 cases. All project inventories sum to Unit 16,252, Integration 1,338,
Manual 307, Soak 1, External 0, Hardware 1.

## Behavior and integrity

Startup validates fresh configuration/schedule/protected authority and a bounded
recent cohort. Only known date partitions intersecting the last 48 hours are
visited, at most three UTC dates; older and unknown directories are not traversed.
The indexed journal query checks recent raw identities/exposure times and sidecar
coverage. Limits are 400,000 filesystem entries, 20,000 journal rows, 4 GiB JSON
bytes and 8 MiB per JSON file. Reads allocate within those limits and reject growth
or malformed SQLite values. Missing required ledger authority keeps capture held.

Kestrel starts before the full background pass. Capture initialization can itself
invoke raw-ingress reconciliation through the schedule store, so it also runs
after ApplicationStarted. Pending capture dependencies report degraded health;
recent corrupt evidence reports unhealthy location readiness while the host and
authenticated recovery page remain available. A location edit arriving between
background qualification and capture initialization joins fresh reconciliation.

Every new process remains unqualified for lifecycle verification until a full
actual-byte/schema/provenance check finishes. Saved proofs never grant readiness
on their own. Typed proofs only avoid reparsing unchanged authenticated content
under the same protected-history/validator binding. Completed advisory parse
prefixes survive cancellation, including a validator refresh where typed proofs
cannot be used. Optional index writes remain best-effort.

One host coordinator owns initial, request and scheduled scrubs. HTTP cancellation
cannot cancel its work. An unchanged failed state latches recovery-required;
ordinary polls do not restart the full scan. Actual managed/config changes,
scheduled maintenance or a normal restart after authorized repair can retry.
A changed config snapshot receives at most three fresh attempts before refusal.
An unexpired qualified process retains its prior qualification during refresh;
observed drift invalidates immediately, and writes cannot extend scrub expiry.

Row MACs bind the protected nonce, key, digest, kind and generation. The random
per-ledger secret is protected in metadata. Expected rows are authenticated in a
bulk pass, stage inserts use prepared batches of 8,192 with FULL durability, and
the complete set commitment is checked before publication. Legacy protected rows
remain readable. Restore authenticates both formats and re-signs carried file
rows with the target secret, including legacy protected targets. Incomplete or
unverifiable writes/restores never qualify. Warm verification authenticates the
current database pathname, so an unlinked open SQLite inode cannot hide deletion.
Managed self-pruning recognizes its authenticated in-flight write; different
bytes still refuse deletion.

The only accepted timing relaxation is out-of-band tampering detected by the
next full scrub rather than every warm protected read. No identity, schema,
provenance, authentication, comparison, timeout or cancellation check is bypassed.
The recent reader uses one bounded opened stream; this does not claim a filesystem
snapshot against concurrent out-of-band replacement. The previously excluded
reopen race is not otherwise expanded by this correction.

## Startup measurements

Disposable synthetic production fan-out is 20 JSON files per frame: four image
sidecars plus eight metadata payload/sidecar pairs (12 sidecars), correct date/role
paths, one real v15 raw-journal row per frame, and eight-byte synthetic payloads.
The cadence is 20 seconds, so the newest 48 hours contain 8,640 captures regardless
of older retained volume. No acquisition or image-processing throughput is claimed.
Construction, input checksums and fixture deletion are outside startup timers.

All runs use SDK 10.0.402, one reported runtime processor and a 4 GiB managed-heap
limit under the host heavy lock. CPU affinity and physical storage/controller
caches are not constrained. Initial construction warms files; the larger full
campaign also incurred physical reads, recorded in its I/O counters. These are
local integration measurements, not Pi/production deployment qualification.

| Runtime / captures | JSON files | Initial health acceptance | Restart health acceptance | Qualification observations |
| --- | ---: | ---: | ---: | --- |
| Development baseline 60434ef8 / 1,000 | 20,000 | 5.03 s | 2.17 s | Native protected reads 0.62 / 0.58 s. |
| Development baseline 60434ef8 / 45,000 | 900,000 | 68.51 s | 21.76 s | Native protected reads 12.51 / 12.51 s. |
| Reviewed e7b37843 / 1,000 | 20,000 | 9.36 s | 5.47 s | Initial protection calls 41,004; restart unprotect calls 84,010. |
| Reviewed e7b37843 / 45,000 | 900,000 | Still unready at 181.13 s | Not attempted | Censored at the real installer health budget; 1,572,961 protection calls before cancellation. This is a lower bound, not a completed startup time. |
| Candidate full campaign / 45,000 | 900,000 | 8.36 s | 8.88 s | Qualified at 88.26 / 42.11 s from start; both native protected reads fit 120 s. |
| Final merged candidate 5deacf01 / 45,000 | 900,000 | 6.16 s | 5.81 s | Qualified at 76.17 / 33.18 s; native protected reads 69.94 / 26.34 s, both within the unchanged 120 s budget. |
| Candidate full campaign / 129,600 | 2,592,000 | 6.16 s | 11.38 s | Qualified at 371.37 / 245.70 s; early native reads refused at 120.001 / 120.001 s, background survived, and subsequent verification succeeded. |

Final startup measurements at `297b3bb7bed0a4e884c8112b5a12dcab88047454`
check the same 259,212 recent filesystem entries at both retained sizes:

| Captures / JSON | Initial health | Restart health | Recent JSON read per start |
| --- | ---: | ---: | ---: |
| 45,000 / 900,000 | 6.162 s | 4.170 s | 357,341,760 bytes |
| 129,600 / 2,592,000 | 6.209 s | 6.216 s | 357,384,960 bytes |

These final startup-only runs cancel background work after observing health and
pending refusal. Their restart is an unqualified resumption, not an established
qualified-ledger restart. The earlier full campaign separately covers both full
qualifications and restart. Subsequent integration merges change installer control receipts and environmental
provenance/retention; the measured startup/ledger/endpoint/client sources are
unchanged from this sample, as verified by exact path-tree comparisons.

Health acceptance above means the actual `OwnerBootstrapClient`, loaded from the
built CLI assembly, completed `/alive`, HTTP health and catalog-contract checks
against the synthetic approved-fixture catalog. The fixture reports degraded pending
capture/catalog dependencies; it does not manufacture full qualification.
The real Kestrel/client campaign performs both starts used by upgrade verification.
It is a protocol/health-wait campaign, not a Docker deployment of the full lifecycle
manager. Lifecycle mutation ordering and comparisons are covered separately.

The development baseline's endpoint/loader source and installer-generated fixture
configuration are unchanged at the merged development tip. Both old and new
configuration files have SHA-256
`bd531dcc2ec8e9ae2d128cb811762af5672b0d30a30b3e13a88a84bb8b75171f`.
The preserved reviewed-head binaries were checked against the baseline overlay.
The preliminary full candidate campaign preceded the small corruption/retry/
initialization race guards. Final startup counters, endpoint samples and the
45,000-capture full native campaign at `5deacf01b6e20a1caeae95c3e48e68792e815998`
are separately bound rather than relabeled as that earlier binary. The final
campaign uses the actual native client and two Kestrel starts; its health waits
and protected reads all meet their existing budgets above the reviewed image's
observed health-failure threshold.

In the final 45,000-capture campaign the process peak was 964,993,024 bytes;
initial/restart cumulative allocations were 28.05 / 16.65 GB, and physical writes
13.77 / 1.68 GB. Those are whole qualification costs, not endpoint costs.

The largest observed process peak in the preliminary full campaign was 1,784,971,264 bytes (about 1.66 GiB), including
the harness. Initial/restart allocations through qualification were 81.51 / 42.80
GB cumulatively; this is allocation traffic, not simultaneous live memory.
Full qualification remains linear background work. At the largest case it read
about 10.8 / 10.5 GB physically and wrote 51.6 / 4.9 GB, versus zero physical reads
in the smaller campaign. The larger B-tree/cache/I/O workload is disclosed;
constant-time whole-corpus scrubbing or flash-endurance qualification is not claimed.

The complete production hosted-service graph is checked separately from the
controlled scale fixture. Canonical supplemental Unit bootstrap exposed an inline
configuration wait in `CaptureDistributionService.StartAsync`: the deferred
initializer waited for ApplicationStarted while that consumer prevented it. The
new complete-host regression failed at its 10 s host-start limit on `5deacf01`.
A host adapter now defers admission, calibration recovery and distribution startup
until ApplicationStarted; all three can reconcile raw history. Their existing
initialization and stop/drain methods remain in charge of that work. An observed
failure marks capture readiness unhealthy, logs only the error type and leaves
recovery available. The complete-host regression now passes in 3 s, alongside
four adapter cancellation/drain/failure Unit cases. The full-graph test also passes after restarting with a torn recent Raw sidecar:
background recovery is required, `/alive` and authenticated `/system` remain 200,
and protected verification returns 503. The scale fixture excludes
these autonomous services; its measured recent/ledger/client paths are unchanged.

## Final real-endpoint scale measurements

The actual authenticated Kestrel endpoint uses installer-generated owner settings.
The final samples are bound to `297b3bb7bed0a4e884c8112b5a12dcab88047454`;
its measured endpoint/ledger/startup/client source is unchanged by the subsequent
normal integration merge. A fresh immutable development baseline at `60434ef8`
uses the same configuration and corpus construction. Its measured product source
is unchanged through the merged development tip. Historical issue medians are
not substituted for either measurement.

| Captures | Sidecars | Products / capture | Linear warm median | Qualified warm median | Linear evicted median | Qualified evicted median |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 100,001 | 100,001 | 1 | 3,104.19 ms | 40.01 ms | 5,878.48 ms | 25.31 ms |
| 300,000 | 300,000 | 1 | 10,056.04 ms | 19.87 ms | 30,307.96 ms | 21.69 ms |
| 100,000 | 300,000 | 3 | 8,506.73 ms | 19.02 ms | 27,614.34 ms | 20.85 ms |

Each cohort has three warm and three fixture-file-page-evicted reads. Eviction
uses fsync/POSIX_FADV_DONTNEED and mincore=0 on only disposable retained/main DB/
checkpoint files; transient SQLite SHM is excluded. Metadata/controller caches and
host-wide cold start are uncontrolled. Construction, qualification and eviction
are outside endpoint timers. CPU affinity is not constrained.

Every final first/warm/evicted sample has zero retained scan spans and parses,
with unchanged identity and retained checksums. The largest observed sample is
875.27 ms (the first 100,001-case evicted read); medians do not conceal that outlier.
Warm allocation medians are 7.93 / 7.54 / 7.54 MiB, versus linear 2,587 / 7,747 /
5,496 MiB of cumulative allocation. This demonstrates bounded qualified endpoint
work, not bounded whole-history background work. Ordinary/continuity samples also
have zero retained scans. Restart qualification parses zero entries and preserves
all 300,000 proofs, beyond the removed capacity cap. Initial/restart full
qualification took 14.41/8.61 s, 39.58/24.36 s and 32.41/18.82 s in the three
cohorts; those timers are explicitly excluded from qualified endpoint latency.
The three-product cohort is a fan-out subset; the startup
campaign above separately uses the full twelve-sidecar/twenty-JSON production shape.

## Reproducing the measurements

Use the pinned SDK, a fresh short private TMPDIR and `MSBUILDDISABLENODEREUSE=1`.
Build Release warning-clean with `-p:SourceRevisionId="$(git rev-parse HEAD)"`,
then generate synthetic owner settings with the
[fixture configuration generator](issue-1041-run2-configuration-generator.cs.txt)
and the cached catalog bundle. Supply that file through
`HVO_INSTALLATION_READ_OWNER_CONFIGURATION`; never use live settings or tokens.
The test fixtures generate their own credentials and confine their raw roots to
the disposable evidence directory.

```bash
export DOTNET_PROCESSOR_COUNT=1 DOTNET_GCHeapHardLimit=0x100000000
export HVO_ATTRIBUTION_REVISION="$(git rev-parse HEAD)"
export HVO_INSTALLATION_READ_OWNER_CONFIGURATION="$TMPDIR/owner-configuration.json"
export HVO_INSTALLATION_READ_EVIDENCE_DIR="$TMPDIR/scale"
HVO_INSTALLATION_READ_EXPECT_INDEX=1 flock -o /tmp/hvo-heavy-gate.lock \
  dotnet test tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests --no-build \
  --no-restore -c Release --filter FullyQualifiedName~RealEndpointRetainedIndexScaleWithEvictedCache
HVO_STARTUP_MODE=candidate HVO_STARTUP_ONLY=1 HVO_STARTUP_COUNTS=45000,129600 \
HVO_STARTUP_CLI_DIRECTORY="$PWD/src/HVO.SkyMonitor.Deployment.Cli/bin/Release/net10.0" \
  flock -o /tmp/hvo-heavy-gate.lock dotnet test \
  tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests --no-build --no-restore -c Release \
  --filter FullyQualifiedName~ProductionFanoutStartupAndNativeInstallerHealthCampaign
```

Use a fresh evidence directory with `HVO_STARTUP_ONLY=0` and
`HVO_STARTUP_COUNTS=45000` for the complete two-start qualification campaign.
The [sanitized measurements](issue-1041-run5-scale.json) retain revision, input
commitments, runtime/resource settings, all samples and cache-eviction counters.
The unchanged development baseline was built from its immutable source archive
with only the compatible measurement fixture/helpers added; product code was not
changed. The reviewed e7 comparison used its preserved original Common binary,
with binary hashes retained locally. Baseline construction is excluded from timers.

## Original requirements and correction evidence

| Finding | Regression test | Before the fix | After the fix |
| --- | --- | --- | --- |
| M-4 / M-A | `ColdOrdinaryStartupDoesNotWalkNonRecentRetainedHistory` | FAIL on unchanged e7 product source: cold ordinary load visits old retained evidence. | PASS in the affected CameraAgent selection; production-fan-out measurements above demonstrate a fixed recent cohort. |
| M-5 | `WarmVerificationRefusesDeletedLedgerEvenWhenItsSqliteHandleIsOpen` | FAIL on e7: the open SQLite handle hides deletion. | PASS: the current pathname is authenticated and verification refuses. |
| M-6 | `FocusRetentionCanPruneItsOwnNewlyPublishedOlderRecordWithoutDrift` | FAIL on e7: valid self-pruning loses qualification. | PASS: maximumRecords=1 retains the newer record and both warm/full verification succeed. The neighboring declared-byte mismatch test still refuses. |
| Full-host interaction found during validation | `CompleteHostedServiceGraphStartsBeforeDeferredCaptureConfiguration` | FAIL on 5deacf01 at the 10-second startup limit. | PASS after deferring the three capture initialization consumers; four adapter lifecycle/failure cases also pass. |

The sanitized gate ledger retains the before-test fingerprints and successful
full affected selections. These failures were captured before each product fix;
no passing result is inferred from the presence of a new assertion.

The [sanitized local evidence ledger](issue-1041-run5-local-gate.json) binds the
current build, focused corrections and host selection separately from retained
passes and intentionally stopped/skipped work. Retained evidence is not relabeled
as a new run on the delivery head.

| Requirement / source | Implemented behavior | Local behavioral evidence | Result |
| --- | --- | --- | --- |
| R1 / issue repeated work; M-4/M-A and Roy t388u | Bounded recent startup; complete history in background; qualified reads inspect only fresh identity and bounded authority. | Three required e7 regressions recorded before product edits; actual production-fan-out startup and native client campaign above; final-head 100,001/300,000 warm/evicted endpoint samples and identical recent entry counts. | PASS: startup/endpoint campaigns; affected host selection and retained suites as bound in the ledger. |
| R2 / integrity; M-5 | Authenticate current ledger pathname; full scrub rejects tamper/schema/provenance drift. Bad recent/quarantined evidence preserves host/recovery access. | Warm deleted/replaced ledger; recent and old tamper; invalid manifest/index; actual startup journal/quarantine/Raw probes with `/alive` and authenticated `/system`. | PASS: focused regressions and retained CameraAgent/Acceptance cases; host selection recollected. |
| R3 / fresh identity, concurrent edits | Fresh config/owner/protected state; no immutable config cache; bounded re-read on edit; capture initialization joins a late reconciliation. | Existing real endpoint edits/concurrent readers, config-race probe; new background retry and post-startup capture edit tests. | PASS: focused edit/concurrency cases and prior real config-race probe; extra probe rerun skipped per t410u. |
| R4 / #1042, lifecycle ordering | Same shared finite 120 s budget, timeout/caller-cancel distinction, auth/proxy/redirect protections and exact pre-mutation comparisons. Plans dry-run. | Actual 120 s refusal/surviving background campaign; affected CLI upgrade/rollback/backup mismatch/failure/cancel/plan and transport suites. | PASS: actual native campaign and retained CLI selection; full CLI rerun skipped per t410u. |
| R5 / failure, cancellation, optional state | Completed parse prefixes persist even when typed proofs are invalidated; optional cache writes cannot refuse unchanged authority. Committed memory precedes late cancellation. | Strengthened file/journal mid-scan cancellation records exactly one completed prefix; next process parses only the remaining distinct bytes; same-length tamper refuses. Existing disk-full/late-cancel/recovery cases. | PASS: focused cancellation/optional-state cases and retained affected suites; broad rerun skipped per t410u. |
| R6 / writers, invalidation, concurrency; M-6 | All writer hooks preserve expectations; declared self-pruning succeeds; unexplained bytes refuse; scrubs coalesce without premature daily invalidation. | Deterministic nested writer lock-order tests; focus maximumRecords=1 older self-pruning; negative declared-byte mismatch; periodic readiness and ordinary-load/coordinator cases. | PASS: writer/pruning/coordinator regressions and retained affected suites; broad rerun skipped per t410u. |
| R7 / durable crash, restore, rollback | New process never inherits readiness; authenticate pending/current/target sets and paired authority. Generic deleted-ledger restore refuses; active boundary recovery remains narrow. | Real legacy-target and rotated-current-key restore tests; previous failed-first-upgrade/failed-old-image rollback cases; prior SIGKILL/config/cancel probes retained. | PASS: real restore focus and retained lifecycle/SIGKILL cases; extra process probes skipped per t410u. |
| R8 / compatibility | Same qualified wire identity. MAC migration/paired restore needs matched updated installer; current read always executes on running agent. | Legacy/MAC and destination-key restore matrix; Docker reader floor; compatibility matrix below. | PASS: compatibility focus and current warning-clean Release; retained CLI evidence. |
| R9 / HIGH evidence, integration | Normal development merge, actual inventory discovery, exact classifier outputs, v1 target profile, short private TMPDIR, no node reuse, heavy lock. | Current warning-clean Release and category discovery; exact classifier/source bindings; earlier controls/format/coverage retained, with later broad reruns stopped/skipped per t410u. | PASS within t410u correction scope; broader reruns skipped explicitly. Kepler HIGH re-reviews follow push. |

## Validation scope and control corrections

Roy's t410u scope correction is authoritative: the full Unit, architecture/publish
and 39-case Acceptance evidence already passed at cc49c13bb and is retained.
The two merged still-work tests needed fixture expectations for upstream's
intentional withdrawal behavior; all four focused rows passed on 03987c5b8.
The final affected host Integration selection passed **61/61** on cc0771f29,
without another coverage sweep. The four focused still-work rows passed on
03987c5b8; the 19 compatibility/drain rows passed on cc0771f29. The current full-solution Release build at cc0771f29 passed with zero
warnings/errors; 19 focused compatibility/drain checks and category discovery
also passed. Current inventories sum to Unit 16,252 / Integration 1,338 / Manual
307 / Soak 1 / External 0 / Hardware 1.

The t410u message arrived during an unnecessary CameraAgent Unit rerun. Its
owned process group was stopped immediately; that attempt is CANCELLED, not a
pass. No remaining full CLI/Integration, five-component/canonical coverage, fresh
v1 Unit/floors, extra SIGKILL/config probes or format/long-control reruns were
started afterward. Earlier successful controls, format and coverage remain bound
to their original source snapshots; no final-head full-qualification claim is made.
The target profile remains development/v1 Preflight and Build and Unit; no
workflow, PR or GitHub state write was performed.

Control failures were corrected without weakening assertions: six synthetic timing
classifier log inputs were absent from Git under the general log ignore rule;
they are now tracked. The strict ARM64 image assertion now matches the new reader
floor. Both release and timing contract suites pass. Nineteen focused contract checks
also pass after updating the current-candidate boundary metadata (frozen SQL images
and previous-release labels are unchanged), the deferred-registration assertions
and an expiry-health mock that must declare recent evidence verified. The first
full CameraAgent Unit attempt reported 14 stale-input/assertion failures; its
other 8,224 cases passed, with one existing macOS-only skip on Linux. The corrected full selection passed before the latest merge; Roy subsequently
directed retaining that evidence rather than repeating full suites. Acceptance recovery contracts were updated for the deferred admission wrapper
and eventual readiness. Both existing operator repair/write-off flows preserve
paused admission, original evidence checksums and subsequent capture resumption.
Unsupported/drifted raw-journal SQLite layouts still refuse host startup; their
async cancellation wrapper is checked separately from the unchanged exact schema
validator refusal. No malformed schema is accepted. The coverage-policy harness
initially supplied a double-prefixed origin ref; its invocation uses the branch
name required by the script. Earlier startup-probe fixture failures were setup
errors (the strict journal prevented injecting the deliberately malformed value,
and the temporary fixture path missed its confinement prefix); the corrected
four startup/recovery cases pass without changing their refusal assertions.
An initial LogicHost Integration run had 597 passes and three attribution-guard
failures because older test-output copies carried the earlier commit stamp. The
rebuilt merged-head binaries pass all three focused source-attribution checks; the
complete selection is rerun with the guard unchanged.
Combined fixture provisioning now explicitly joins full qualification before
reading the active location. Its stale-assignment observer waits for first outbox
creation; strict SQL and all delivery/retention assertions remain unchanged. Two merged
automation cases now assert upstream's intended withdrawal of a disabled definition
as Skipped, with no start and an unchanged original occurrence/window. Global
runner disable still asserts queued/stalled. The merged control UI carries
cancellation separately from its outcome wording; the Integration assertion now
checks the structured actor/time/reason, while retaining paused admission and
explicit-resume requirements. These are test corrections, not product changes.

## Compatibility and operational limits

| Installer / running agent | Behavior |
| --- | --- |
| Older installer / updated qualified agent | Success fields/auth/comparisons are wire compatible. Older clients may fail immediately on pending 503 and require another invocation. Use the matching updated installer for MAC-state migration and paired restore. |
| Updated installer / pre-ledger old agent | Original finite 120 s budget; the old running server still scans all retained metadata. No server-only change accelerates that pre-upgrade read. |
| Updated installer / updated agent | Qualified reads are independent of retained volume. Pending retries spend the original finite budget. Recovery-required is terminal until authorized repair/requalification. |
| Updated installer / legacy or pre-MAC snapshot | Authenticate original paired state; carry file expectations under the destination proof format. New process fully verifies before lifecycle success. Generic absent authority never silently adopts. |

Pre-mutation verification always talks to the currently running image. A fixed
image cannot unblock an already-slow old runtime before that runtime answers;
the empty-instance adoption window remains a compatibility fact. At very large
histories, a newly started or restored candidate can remain genuinely unqualified
past 120 seconds even while health is available. A lifecycle command then refuses;
it must not be treated as success or bypassed. The final 45,000-capture two-start campaign
fits both existing budgets. The 129,600-capture case explicitly proves bounded
refusal and eventual safe qualification, not an unconditional successful upgrade
at any retained volume.

Capture admission remains held until safe full reconciliation publishes its
configuration. Host/health/recovery availability does not imply capture or
lifecycle qualification.

Full scrubs run after every process start, for dirty/changed state and scheduled
refresh before the 24-hour expiry. Unexpired refresh does not discard prior trust
before finding a fault. Long bulk-authentication/final-commit barriers can still
pause writers/ordinary reads; no sub-30-second continuity guarantee is made during
those phases. Five managed-publication samples per scale cohort write 86,016 bytes each after the
first 180,224-byte publication, including durable bookkeeping, for small fixture JSON.
Sustained capture/flash-wear qualification remains a follow-up.
The prior registration-lease race and retention continuation after one drifted
file remain Kepler's recorded Lows; no issues are filed by this thread. Combined
startup logs also show the existing graph-delivery loop briefly reporting an
uninitialized processing registry before deferred capture initialization completes.
Its retry preserves pending delivery; suppressing that startup diagnostic is an
observability follow-up, not evidence of qualified capture or completed delivery.

LogicHost follow-up (notes only): reuse recent indexed admission and host-owned
background coordination with its own persistence adapter. Unlimited retention
must not become an inline host/agent startup scan. LogicHost must also bound
agent discovery/admission so its registered CameraAgent count cannot become a
synchronous startup loop; qualify individual generations in background or on
demand with declared pending readiness. Segmented generation snapshots
can further reduce background barriers and memory without coupling the hosts or
moving domain/SQLite logic into Storage.FileSystem.

Owner disposition: implementation corrections M-4/M-A, M-5 and M-6 are resolved
with failing-before evidence and the declared validation scope. Kepler must obtain
Sol and Opus HIGH completeness/review verdicts on the pushed candidate. The
large-history pending refusal, local-only fixture scope, remaining Lows and
LogicHost follow-up above remain explicit limits. No PR or issue comment is made.
