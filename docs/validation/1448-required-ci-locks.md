# Required CI lock investigation after PR 1690

## Evidence boundary

Run 42106379803 checked out `efe9743d7e6068aa8eea5622d0df04dcdaff9ba1`.
Its artifact provenance names that SHA; the Actions run head names
`350b7f9db7e26b1926ce08f40e7290de2ea4b948`. Both trees and green PR head
`1be2dcac93ead1fa66b4a561e29240c0201cb697` are byte-identical. There is no
additional main code delta to attribute these failures to.

The runner returned HTTP 500 from the same 2,825-byte invalidation SELECT,
SHA-256 `bf17f92335cd9967becd2f7789d82fa6fd7f8ed873191698b677781955eff1e7`,
after 30,001 ms. The request had not saved its tracked changes before that query.
Its diagnostic connection opened in 38 ms, but all three joined DMV observations
failed. Timer lag was about 1 ms and ThreadPool pending work was zero. Those
facts do not establish a blocker, plan defect, memory pressure or self-deadlock.

The combined durable-submission test passed acknowledgement and duplicate
submission, then failed in its execution probe acquiring the first source-object
fence. The lock timeout was 10 seconds. Coverage counts balance acquisitions and
releases; no release exception branch was hit. A retried artifact upload remained
in flight during the probe and is a strong candidate for the overlapping fence
owner. The SQL head blocker is not identified. This is an inference, not proven
causal attribution, and is not classified as a harmless flake.

## Aborted replay and cleanup

The first native replay stopped after about one minute when its initiating turn
ended. No completed TRX or target-test occurrence was produced. Its direct
observer preflight worked, but that interval cannot establish either root cause.
On resumption, the three orphaned containers were matched to the task label,
removed through the local Docker socket, and the private connection file deleted.
The uncommitted fixture naming/port overlay was reversed and the worktree checked
clean. A subsequent replay encountered a local port-binding collision and was
stopped; its cascading fixture failures are excluded. The temporary harness then
reserved unused allowed ports until the corresponding container start. These
harness changes are not part of the PR and changed no host networking settings.

## Completed local reproductions

The guarded native LogicHost Integration selection ran with normal fixture
workers, cumulative state and CI coverage settings: **593 passed, zero failed**,
14m47s. Runner recovery passed in 1.116 s, generation fencing in 15.938 s, and
proposal history in 3.049 s. Direct SQL observations retained request/session/
grant/lock evidence. Captured long application-lock waits correlate to named
contention acceptance tests, including
`Worker_FirstItemLockTimeoutDoesNotStarveSecondDueDeletion`; they are not the
runner incident. This replay did not reproduce the runner timeout.

The native combined selection then passed **24/24**, 3m29s. Durable submission
rows 0/250/12000 passed in 40.101/39.129/50.266 s respectively. The external
observer followed both the shared fixture and the transient row's SQL container.
No target failure recurred. These passes are counterevidence to an unconditional
failure, not proof of a cause or successful root-cause fix.

Both wrappers completed with exit zero, removed their owned containers and
private connection files, released the user port locks, and reversed the fixture
overlay. Investigation-owned pinned image cache is retained for the next probe;
no images were built or shared resources pruned. Evidence is retained under
`/tmp/t3-1448-locks-native/completed-logichost`, `completed-combined`, and their
lifecycle logs. The original CI evidence remains under
`/tmp/t3-1448-42106379803-integration`.

## Diagnostic correction for the next occurrence

The test-only observer is shared through TestInfrastructure so the combined test
can observe its own SQL instance from capture through execution. The original
joined/sorted DMV query is replaced by independent, bounded read-only sources:
permissions, requests (including connection IDs and wait resources), sessions,
memory grants, semaphores, SQL process/system memory, min/max memory
configuration, and application/waiting locks. No source joins another DMV.
Each source/cycle owns a fresh unpooled session, with separate two-second opening
and command/read bounds and automatic connection retry disabled. A failed source
cannot suppress the others or poison a later cycle. Output contains only numeric
IDs, timestamps, enum/reason values, lock resource identifiers, query hashes,
memory values and the two fixed memory-configuration names;
it excludes SQL text, parameter values, credentials and object payloads.

Default observation preflights immediately, then samples after 5/8/8-second
intervals. The combined test samples at one-second intervals, at most 300 cycles
and five minutes overall. Queries have fixed row caps; a capped snapshot is not
proof that no other session or lock exists. Observations are passive: subject
faults/cancellation/success remain authoritative, and existing liveness, SQL and
object-lock budgets and assertions are unchanged. No production query, lock
policy, worker or runner configuration is changed.

A controlled live probe held a row lock on one disposable SQL instance and
verified that the collector named the exact blocking session while all nine
independent sources succeeded. It also preserved the original subject exception.
This validates the collector, not the CI root cause. The probe exited zero and
removed its container/tmpfs state, private files and port lock. Evidence:
`/tmp/t3-1448-42158642535-integration/memory-probe-observations.log`,
`memory-probe-lifecycle.log` and `memory-probe-build.log` in that same directory.
These `/tmp` evidence paths are host-local, not durable shared artifacts.

Historical runner timeout and underlying artifact/fence stall acceptance remain
**OPEN**. The next reviewed diagnostic qualification must identify a blocker or
runtime condition before a production correction is justified. A green rerun
alone does not resolve that acceptance.


## Review corrections and embedded-collector check

Opus F1 restores `schedulingLagMs`: the due time is recorded before the sampling
interval delay, with zero lag on preflight. SQL query duration is not charged to
timer scheduling lag. F2 pairs the controlled live row data with its final
correction-build/lifecycle logs and discloses their host-local retention. F3
adds an `is_observer` flag without disclosing program names, filters background
requests, and trims padded lock descriptions; observations still use no DMV join.

The three native durable-submission rows ran with the embedded correction built
at `84626928`, under the uncommitted fixture naming/port overlay. All passed:
rows 0/250/12000 took 45.855/37.673/48.608 seconds. Their stdout sizes were
277208/227275/249628 bytes, including ordinary test logs. They retained
165/145/145 successful source observations respectively and no observer errors;
scheduling-lag fields were present. This is measured local diagnostic overhead
and output volume, not a production performance bound or proof of the CI cause.
The wrapper exited zero, cleaned owned containers/private files and user locks,
and restored the overlay; the worktree was clean afterward. Evidence and TRX are
retained in `/tmp/t3-1448-locks-native/embedded-collector`.


## Qualification 42158642535: reconciled failure evidence

Quality rejected whitespace in the shared library's connection-string builder.
The earlier project-scoped formatter did not verify that referenced project's
source. The initializer is now formatted explicitly and checked through the
solution-level formatter on the actual shared-library path. No format exception
or suppression is introduced.

The new direct observer succeeded during the runner failure. At samples 2/3/4,
session 51 was suspended on `RESOURCE_SEMAPHORE` with zero blocking session,
waiting about 4.98/13.04/21.06 seconds. It requested 49,920 KB of execution memory
(required minimum 2,176 KB); no grant had been awarded. Several other application
sessions also awaited grants, including 1,024 KB requests. The grants snapshots
contained only waiting requests and no granted rows. Session observations mark
these as non-observer sessions. The observer sources returned in milliseconds;
.NET timer lag remained near zero with no queued ThreadPool work. The same
runtime lines report approximately 7.30 GB managed memory and 3.1 GB resident
working set (the prior failed run reported about 7.27 GB managed memory).
Earlier fence samples report 6.0–6.15 GB managed memory. These figures make
test-process memory a candidate for host pressure, not an established cause.

A controlled execution of the exact retained 2,825-byte invalidation SQL in a
private migrated database reproduced query hash `3E4E7159F917CC27`, matching
session 51 in the failing CI observations. The XML and DMV hash agree. This
establishes at least 21 of the 30 seconds spent waiting for an execution-memory
grant, with no row-lock blocker in those samples. The final approximately nine
seconds are unobserved; execution cost during that interval is not ruled out.
The exception originated in the subject, not observer draining. The empty controlled database needed no execution grant; it validates the
hash mapping, not the CI memory requirement or starvation cause. Evidence is in
`/tmp/t3-1448-42158642535-integration/hash-lifecycle.log`,
`invalidation-hash-proof.xml`, `requests-summary.json`, and `grants-summary.json`.
The memory pressure's source (SQL target/budget, host pressure, or resource-pool
state) was not captured and remains unresolved. Query and lock-policy changes
would be speculative at this point.

The other failure is in the later eight-overlapping-submission scenario, not the
single-entry observer wrapper: one request held its five fences, while the
snapshot saw zero of the expected seven waiting application-lock sessions.
The run-level log records future-capture SELECT timeouts for overlapping
requests before the assertions complete. Those requests cannot reach the fence
queue while still validating future inputs. The overlapping scenario does not
run the SQL observer; prior observer scopes are drained and disposed before it.
This is an unmet concurrency precondition under upstream SQL stalls, not proof
that the five-fence invariant changed. Its timeout's exact wait type was not
captured, so attributing it specifically to the later runner's semaphore state
would overstate the evidence. Its original assertions and deadlines stay intact.

The correction adds independent numeric semaphore, SQL-process memory, system
memory, and min/max SQL-memory configuration observations to explain the grant
queue on the next occurrence. SQL Server on Linux exposes memory figures through
its platform abstraction; these views cannot establish actual runner-host free
memory or swapping. The local probe reported 2,000 MB total system memory and
4,096 MB process memory under a 1,024 MB SQL budget and 3 GB container limit.
Semaphore/configuration observations describe SQL grant capacity; attributing
pressure to the host still requires independent host-memory evidence.
These sources do not join other DMVs, change SQL
configuration or allocate grants for an application query. Row limits, fresh
sessions, two-second opening/read limits, five-minute overall cap and passive
subject-outcome handling remain unchanged. Tests verify every catalog source
runs in every cycle despite another source failing. The live deliberate-blocker
check with all nine sources passed and cleaned its container/private files/lock;
its observations and lifecycle/build logs are retained together in the directory
above. Earlier live-r1 logs are historical and their mutable row-output path was
superseded by this archived correction evidence.

Neither integration failure warrants raising timeouts or weakening assertions.
The prior incident's allocation origin is still OPEN; this run identifies the
runner's specific memory-grant wait and explains why the fence test did not
observe the intended queue. Next qualification must retain the memory-state
sources before an evidence-backed capacity or query correction is justified.

Correction validation with all nine observation sources: the focused native
combined rows 0/250/12000 passed in 45.681/36.820/47.738 seconds, with
288/261/252 successful source reads, zero observer errors, and stdout sizes
315257/298908/290751 bytes. The wrapper exited zero, removed owned containers,
private files and locks, and restored the uncommitted fixture overlay. These
local rows validate the collector's execution and output volume, not the CI
pressure's origin. Retained TRX/logs are under `embedded-nine` in the failed-run
evidence directory.

A separate offline factory-lifetime probe confirmed that a shared
WebApplicationFactory retains disposed derived factory objects. However, a
follow-up with actual started hosts retained all six disposed service providers
both with the shared-derived approach and with independently created factories
at the observation point. That experiment does not establish a remedy for the
observed SQL pressure. No factory-lifetime rewrite, SQL resource configuration,
GC budget, lane scheduling or runner capacity change is included on that basis.
Its logs are retained as `factory-probe.log` and `started-factory-lifecycle.log`
in the evidence directory; it is not presented as a proven CI cause or fix.
