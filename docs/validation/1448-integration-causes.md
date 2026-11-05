# #1448 recurring Integration failures

Base: `d5bf7873446f26785c49eb65bc8046f0069f1535` on `development/v1`.
Failure: [qualification 41956783961](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/runs/41956783961),
Integration job 127013642565. Earlier comparison: qualification 41945710353 on
11a8a5ae. This document separates confirmed causes from unfinished investigation.

## Proposal history: confirmed ordered-prefetch over-read

The retained runtime plan from the newer failure has SHA-256
`cfe252b33b8722b306f099497afa2351fc5f931eebda618ff3675c0e868aab5d`.
It records:

| Operator | Runtime evidence |
| --- | --- |
| TOP | 51 rows |
| Nested Loops | `WithOrderedPrefetch=1`, 51 rows |
| Existing `ProposalId_RecordedAtUtc_Id` index | Ordered backward seek, 58 rows read and returned |
| Clustered payload lookup | 51 executions / 51 rows |

There is no Sort or residual predicate. Ordered-prefetch consumption ahead of the
payload lookups explains this 58-versus-51 assertion failure. The earlier
144-versus-51 failure did not retain a plan, so its precise mechanism is not
independently established. The HTTP response itself was already bounded.

The correction materializes only the latest 51 covered index keys before a
separate payload query. `EF.Parameter` keeps the ID collection in one JSON
parameter rather than expanding 51 SQL parameters. Payloads are reassembled in
returned key order, preserving SQL GUID ordering; truncation comes from the key
count. The existing reauthorization runs after both commands. Immutable facts
are not refilled from a newer page during hydration.

No assertion or application timeout is relaxed. The original exact 51 pagination
index-entry assertion now also rejects lookup and Sort operators. A separate
runtime-plan assertion requires exactly 51 payload rows examined through primary
key seeks. Append and revocation tests cover both command gaps. A nonempty read
now executes four SELECTs instead of three; empty and denial contracts remain.

### Measured cost

A standalone probe compiled the exact base service body under a renamed class
and compared it with the candidate against the same private SQL database and
faithful 10,000 retained / 10,000 unrelated lifecycle-fact workload. Both returned
the same bounded contract. After five warm-ups per implementation, blocks ran in
before/after/after/before order, 100 operations each.

| Per operation, observed mean | Base | Candidate |
| --- | ---: | ---: |
| Product SELECTs | 3 | 4 |
| Sequential elapsed time | 18.465 ms | 27.655 ms |
| SQL logical reads | 186.57 | 177 |
| SQL worker CPU | 373.475 microseconds | 538.885 microseconds |
| Process-wide allocated bytes | 473,891 | 619,632 |

The benchmark disables connection pooling, so each EF command also performs a
physical encrypted connection/login. The measured time and allocation delta
includes that extra connection and cannot isolate the dictionary/EF cost or
predict the production pooled-connection delta. This is a bounded-work tradeoff,
not a speedup claim. Client CPU was also recorded,
but the first block includes tiering/JIT activity and is not a stable production
CPU estimate. This is a local comparative smoke measurement, not an SLA,
percentile claim or production qualification. The local SQL image was pinned,
limited to two CPUs / 3 GiB, with its SQL memory limit at 2 GiB; recurring host
workers were suppressed only for this comparison. Raw blocks, source and binary
hashes remain in the local evidence pack.

A fresh run using the current test DLL passed the exact 51-key / 51-payload
assertions, concurrent appends and both revocation gaps. An older standalone
probe had a stale copied test DLL; it is excluded from candidate-assertion
coverage.

## Runner failure: location confirmed, cause still open

Both failures log the identical 2,825-byte SELECT, SHA-256
`bf17f92335cd9967becd2f7789d82fa6fd7f8ed873191698b677781955eff1e7`.
Each produces SQL error -2 after 30,001 ms in
`ArtifactIngestService.InvalidateDependentsAsync`, called by
`MarkInputUnavailableAsync` while handling `object.missing` on reclaimed attempt
2. The query includes attempts, input requirements and inputs. Neither artifact
establishes why it timed out; three observer timeouts contain no SQL wait data.

Controlled local checks have **not** established a query-shape cause:

- The real runner test passed in a fresh environment and later in an
  accumulated-state native MSTest run.
- In a private migrated database with 5,000 unrelated retained jobs, the exact
  captured statement returned two rows in 24 ms cold / 2 ms warm. Holding an
  exclusive lock on an unrelated job row still allowed it to finish in 2 ms.
- An indexed-ID/split-loading prototype also completed. This does not prove it
  fixes the CI incident, and no such production rewrite is included.

The observer is repaired to connect before invoking the operation, separately
report connection and command failures, and avoid SQL-text retrieval. Samples
include active requests and open-transaction sessions, connection IDs (matching
SqlClient error IDs), blockers, waits, CPU/reads, query/plan hashes and memory
grants. Runtime counters and timer scheduling lag distinguish observer scheduling
delay from server waits. SQL, parameters, credentials and host/login names are
not logged. Existing SQL deadlines remain 30 seconds; the fence-entry deadline
starts before observer setup so it is not extended.

Review correction Sol-P2: a broken observer connection must not fault its next
sample and replace the subject's exception during cleanup. Sampling never reuses a disconnected connection (including a disconnect racing command execution).
Production sampling retries a fresh connection at the next bounded sample point;
there are at most three sample points, each with separate two-second opening and command bounds.
Two additional Unit cases verify retry count and cancellation. Passive
cleanup observes sampler/cancellation/reporting failures without changing either
a successful subject outcome or its original fault/cancellation. Reports expose
only exception types, not exception messages. Five Docker-free regressions cover
closed-connection observation followed by subject fault/cancellation, unexpected
sampler faults, subject success and a broken diagnostic writer. At correction `85917130`, the Unit
inventory was 14,857; the affected mixed project had 36 passing Unit cases.
A live private-SQL probe against the pre-retry build at `63d56026` terminated only
the owned observer session, with SqlClient automatic reconnect disabled for that
experiment. That build stopped sampling, and subsequent subject fault and
cancellation were preserved. Its cleanup is unchanged in the retry-capable head;
this probe does not verify the new successful reconnect-and-sample path. The
normal connection retry configuration was not changed.

The next reviewed qualification must capture an occurrence with this evidence
before attributing it to blocking, memory grants, compilation, application
scheduling or host pressure. The runner cause is not declared fixed or a harmless
flake. No runner administration is justified by the current evidence.

## Local validation boundaries

The complete Docker-disabled Unit gate passed 14,726 cases with 124 skipped and
zero failed (14,850 inventory at the initial candidate). Debug/Release solution builds, formatting, package
and control guards passed; affected corrections receive focused checks.

A native MSTest Integration replay used a temporary, uncommitted fixture overlay
only for resource names, port-slot allocation and Redis tmpfs. It ran with CI's
coverage settings and normal recurring workers: 590 passed, three provenance
checks failed because restoring the fixture overlay made source timestamps newer
than the test DLL. Those failures are retained and are not reported as a green
qualification. Both requested tests passed; the proposal runtime plans retained
exactly 51 key and 51 payload rows examined. The overlay, source restoration and
binary hashes are archived separately. An earlier reflection-based replay with
host-lifetime failures is excluded from qualification evidence.

All dependencies used the local Unix Docker socket, `t3-skym-` names, user locks
and ports 48250–48299. Ryuk was disabled; owned containers and private connection
files were removed. Task-owned image cache is retained only until investigation
ends, then removed if unshared. No application image was built, no production
host or runner was administered, and no promotion, deployment, merge or release
was performed.

Correction evidence: the actual application payload plan, isolated with a per-test SQL comment,
was compiled for a two-fact proposal and reused for the faithful 10,000-fact workload.
It retained primary-key equality seeks and returned 51 rows. The redundant SQL
proposal predicate is removed; hydration still checks proposal identity in memory.
Captured replay parameters preserve provider types, sizes, precision and scale.
The focused run passed exact 51-key/51-payload work, append and both authorization gaps.

## Qualification 42078567936: artifact cancellation settlement defect

The run on `71c22a07` failed the combined CameraAgent/LogicHost suite: 23/24
passed, with `CameraAgentDurableSubmissionAcknowledgesAndRetriesAgainstOneCentralJob (0)`
exhausting its unchanged five-minute total budget. All 593 LogicHost Integration
cases passed, including the proposal read (4.747 s) and runner recovery (1.118 s).
The proposal retained its exact 51-key/51-payload checks and small-first cached
primary-key plan. The runner observer opened in 38 ms; the operation completed
before a sample was due. This is not a recurrence or explanation of the older
30-second runner SQL timeout.

The failed test's stdout records a detected candidate at 18:01:44 UTC. Artifact
POSTs continued, but no candidate-submission POST appears. At 18:03:41 UTC an
artifact POST returned HTTP 499 after 100050.6895 ms; the next warning is
`Artifact outbox quarantined durable work at attempt 1 because http-499`.
`ArtifactUploadClient.IsRetryable` included 408, 425, 429 and 5xx, but excluded
499, causing an interrupted request to become terminal durable work.
`TransientCandidateDeliveryService` requires every context artifact to be
acknowledged before sending a fresh submission. Quarantine cannot satisfy that
gate. This is a confirmed classification defect and a plausible cause of this
test's absent submission. The retained log has no artifact identity for the
quarantine and no candidate/outbox snapshot, so it does not prove that this
particular quarantined row was a candidate source. Nor does it establish why the
artifact HTTP handler took 100 seconds; no SQL timeout from that request was
retained. The repeated scene-pending messages are normal raw/processing lane
coordination and do not establish the cause.

The correction classifies HTTP 499 as retryable for both upload and status
preflight, preserving the existing status-first recovery of an upload that may
already have committed centrally. Regression coverage verifies both routes and
recovery of the exact acknowledgement without a second payload transmission.
Timeouts, liveness assertions and durable quarantine for other permanent 4xx
responses remain unchanged. Failure-state rendering now includes artifact
outbox and transient-delivery snapshots, so a future timeout records the
acknowledgement gate and its backlog rather than only capture progress.

Evidence: run/job 42078567936/127425334123, `integration-test-evidence` artifact
13442355528; local logs and parsed TRX retained under
`/tmp/t3-1448-42078567936-integration`. Coverage was skipped because Integration
failed; Required CI correctly rejected the run. Other qualification jobs passed.
Runner cause acceptance remains open; a green rerun alone will not establish it.

The artifact correction adds two Unit cases: CameraAgent inventory 7,515,
repository Unit inventory 14,859; Integration counts remain unchanged.

## Qualification 42089316458: execution evidence and first-failure preservation

On `b4b4bd56`, the LogicHost selection failed two of 593 cases. The proposal
failure is specifically an empty post-request `sys.dm_exec_query_stats` lookup,
not an observed bad payload plan or `compiledForSmall=False`. Its three HTTP
reads succeeded and the four-command/response checks passed before the lookup.
The cache is a mutable observation surface; query completion does not guarantee
that a plan remains available for a subsequent DMV query. The retained CI
artifact does not distinguish eviction, invalidation or lookup filtering as the
reason this particular lookup was empty.

The test now captures the actual marked application executions with a uniquely
named, fixture-only Extended Events session. Its bounded ring buffer retains
plans independently of later cache residency; the session is dropped afterward.
No `SET STATISTICS` is added to application commands. Auto-statistics STATMAN
plans can inherit the marker, so selection additionally requires the application's
JSON parameter. The test requires exactly three application payload plans and
exact 2/51/51 physical primary-key reads for tiny/warm/measured operations,
including equality seeks. The existing exact 51-key assertion, payload replay,
ordering, truncation, append and authorization assertions remain in place.
The cache-presence assertion is replaced explicitly: it was a diagnostic
assumption, not the physical-work acceptance requirement. Actual execution work
is checked more directly. Capture overhead is test-only and its HTTP duration
is not a production performance estimate.

The fence case reports a 30-second timeout from `EvidenceTaskCleanup.AwaitAsync`
in a `finally` block. That cleanup can replace a preceding 15-second entry timeout
or assertion. Run-level stdout records authentication and all three submitted
artifact lookups at 19:17:16 UTC, but no entry counter, successful session snapshot
or competing-writer evidence. It cannot establish whether the first generation
callback was reached, or the original exception before cleanup. The underlying
stall is not classified as a harmless flake or a confirmed product fence bug.

The single-request test now uses the existing request-versus-entry probe and
phase-specific SQL observer, starting its original 15-second entry deadline
before observer setup. Generation/verification counters and request states are
reported on failure. Cleanup occurs in a catch path that rethrows the original
failure; cancellation callback faults and late response cleanup are observed
without replacing it. Two Docker-free regressions cover bounded cleanup with
late response disposal, and failing cancellation callbacks plus request faults.
The successful path still requires all five fences, zero SQL transactions,
competing-writer success, changed-source rejection and no accepted job.
No timeout or fence assertion is relaxed.

The target merge was attempted after fetching `development/v1`: already up to
date at `d5bf7873`; no rebase or force push. Historical runner SQL timeout and
artifact HTTP stall acceptance remain open unless new diagnostics establish a
cause. Evidence for this run is retained under
`/tmp/t3-1448-42089316458-integration`.

Correction verification: a local owned SQL run captured all three real payload
plans with `compiledForSmall=True`, exact 2/51/51 primary-key reads and exact
2/51/51 root returned rows. Pagination, append and both authorization gaps passed.
A separate controlled probe evicted only its marked statement's plan: the DMV
entry disappeared while all three executed plans remained available in the
capture target. This demonstrates the old retention assumption's failure mode;
it does not establish that CI's missing row was specifically evicted.
Auto-statistics produced one additional internal STATMAN plan during the first
local attempt; filtering on the actual application parameter correctly separated
it from the three payload statements. The initial four-event count failure is
retained and is not presented as a pass.

The real single-request fence test also passed locally through its complete
provenance/evidence path after the driver and dependencies were placed under the
ignored repository TestResults directory. Earlier standalone-driver path and
missing-dependency failures are excluded. The source tree was dirty and this is
focused diagnostic evidence, not green qualification or a reproduced CI stall.
All owned containers/private connection files were cleaned. Evidence is retained
in `/tmp/t3-1448-proposal-benchmark/xe-passing.log`, `xe-lifecycle.log`,
`fence-passing.log` and `fence-repo-lifecycle.log`.

The two new cleanup Unit cases bring the affected project to 38 Unit cases and
the repository to 14,861 Unit cases; Integration inventory remains 1,005.
