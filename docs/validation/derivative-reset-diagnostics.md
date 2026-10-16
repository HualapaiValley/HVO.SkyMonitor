# Derivative reset diagnostics (#1274)

This test-only route observes one selected reset in the entitlement/runner integration
classes. It leaves the UPDATE, 30-second command timeout, assertions, test order,
and hosted workers intact. It does not resolve or waive qualification run
38837361197. Blocking, accumulated-state plan cost and physical-host pressure remain
hypotheses until a witness identifies them.

Set `HVO_RESET_DIAGNOSTICS_DIRECTORY` to a fresh owner-only directory and
`HVO_RESET_DIAGNOSTICS_SELECT=runner:1` to observe the first runner reset. The other
site is `entitlement`; the positive occurrence counts invocations at that site in
one process, including cleanup. Without the directory variable, no SQL observer,
resource collection, counter or file is created. A mismatched selector produces no
record: verify the output exists before claiming diagnostic success.

The collector holds the reset connection open to identify its SQL session and uses
a separate unpooled observer. Its read-only target count uses READUNCOMMITTED and
is labeled accordingly, not an atomic cardinality promise. Observer commands have
two-second timeouts and an overall 90-second cancellation budget, independent of
the unchanged reset timeout. Samples begin at least one second apart (delay after
completion, no catch-up), at most 90 times. Each includes up to 64 user sessions,
128 host PIDs and bounded proc/cgroup counters. Full sets at these caps may be
truncated; absence then cannot exclude a blocker or competing workload. Read/CPU/
write counters and login/transaction/request timestamps support temporal correlation.
Session IDs plus login times distinguish reuse. Host PIDs identify processes, not
the responsible managed worker or stack. A running worker is not causal evidence.

The SQL fixture's exact container supplies cgroup CPU/throttling, memory/swap,
I/O and memory events. Host proc counters include CPU, memory/swap, disk I/O times
and process CPU/RSS without argv or user names. Cumulative counters need two samples
for rates; a short reset may provide only a snapshot. UTC timestamps relate all
observations. Missing/unsupported counters or observer failures are recorded as
closed error kinds/SQL numbers, never exception messages. Diagnostics do not replace
the original reset exception.

Plans are **cached estimated structural projections, never actual runtime plans**.
At most two recent reset-shaped statements and two graph-identity-trigger statements
are considered, each source XML limited to 1 MiB and projected to 512 operators.
The output retains numeric estimates, nesting depth and allowlisted operator kinds;
SQL, literals, objects, parameters and unknown attributes are dropped. Raw XML is
not persisted. Source SHA-256, command SHA-256 (SQL Server UTF-16 bytes), query and
plan hashes bind the projections. Correlate session hashes and last execution time
before attributing a cached candidate to the selected invocation; cached estimates
alone do not prove actual row counts, locks or CPU cost. See Microsoft's
[request DMV](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-exec-requests-transact-sql)
and [cached plan DMV](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-exec-query-plan-transact-sql).

Output is one `derivative-reset.json`, at most 8 MiB, published atomically with
owner-only permissions through the existing P5 evidence writer. Existing files and
symlinked directories are rejected. Oversize/failing publication leaves no partial
published file. The collector never exports connection strings, principal names,
SQL text or application payloads. Original integration TRX/logs are separate private
artifacts and must not be described as sanitized collector output.

## Finite evidence procedure

Use the pinned SDK/image and `tests/coverage.runsettings`, only disposable local
SQL and an exclusive host resource window. Freeze source and retain HEAD/tree,
instrumentation diff, transitive source hashes, DLL/PDB hashes, image digest,
SDK/runtime, command/filter, category discovery, TRX and resource evidence.
Current-PR validation and the old baseline witness have separate provenance.

For baseline 27413fe5a3e1c3892f7de828951191cc5c55191c, create a separate disposable
worktree and commit only the exact diagnostic overlay; retain its diff against that
baseline. Never run a current-target DLL and label it baseline.

1. In one test invocation select the complete `ProcessingEntitlementIntegrationTests`
   class, `ProcessingGraphRevisionReadTests` class and
   `ProcessingRunnerProtocolIntegrationTests.Runner_ClaimsFetchesExecutesAndCompletesARunnerPlacedJob`.
   Check the TRX chronology against the recorded order (9 + 3 + 1 cases).
2. Only if that misses accumulated state, run the instrumented full LogicHost
   Integration assembly once. Stop after a useful witness; no further replay or
   qualification workflow rerun. Maximum **two diagnostic executions total**.

A non-reproduction is a valid bounded result and does not resolve the original
failure. Before recommending any correction, identify the observed blocker owner
and lifetime or the actual plan/resource evidence. A product correction needs a
separately bounded follow-up. No attribution to #849, #587, #1005 or #1270 follows
from these diagnostics alone. #1275 owns the independent toolkit cleanup issue.

## Validation evidence

Recorded execution results and immutable provenance are appended here after the
bounded windows. Focused Unit cases cover sampling bounds, cancellation, closed
plan/resource projections and the output cap. The PR uses Tier B affected gates,
all four CI-control guards and classifier selection on the exact immutable range;
protected profile is development/v1 Preflight + Build and Unit only.

### 2026-10-16 baseline attempt 1

Authoritative host: home-dev-02, local Docker daemon home-dev-02. This differs
from failed qualification runner03; it cannot reproduce that physical host's
unrecorded pressure. SDK 10.0.401; SQL image
`mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04@sha256:ba4c8329f48fb8f02e1416be6a930ebfd71268caee78aa985f3af4315e457c89`.
Baseline parent `27413fe5a3e1c3892f7de828951191cc5c55191c`, immutable diagnostic
commit `bd35b8de23da14b6d9b1ae11539250a9e606cd10`, tree
`b8796c55aa172b8147b3462143b9fc0ea3369a68`. The four-file overlay SHA-256 is
`9342a4f3e7cdfea076d6ad54b5823e50f7e3b5ad934612128740d61a5d212ae5`.

The first diagnostic invocation ran 03:30:23–03:31:01 UTC, using its own freshly
built Release DLLs/PDBs and the unchanged coverage settings. All 13 tests passed;
TRX chronology matches all nine entitlement cases, three intervening graph-read
cases, then the first runner method. Tracked-source and binary hashes passed before
and after execution. The finite resource lock was independently verified free
at terminal rc 0. The preceding baseline build was warning-clean.

Selected `runner:1`: session84, 132 dirty-read target rows, command
03:30:57.0368538–03:30:57.0535759 UTC (16.7221 ms), succeeded. The 38,020-byte
collector output contains one sample and three cached plan projections with zero
collector errors. Sample timestamp03:30:57.0346662 UTC precedes command start;
it contains eight sessions, no observed blocking chain, container cgroup counters,
host CPU/memory/swap/disk counters and a PID census. This snapshot does **not**
prove the absence of a transient blocker during the command or provide CPU/I/O
rates. Reset cached query/plan hashes: `00DBA9E0FF2B5A5B` / `4C101D25A4C56298`;
last execution03:30:57.040 UTC. Two graph-identity-trigger statement projections
are also retained. No actual plan or causal blocker was observed.

Private evidence root on home-dev-02:
`/home/roys/.cache/hvo-issue-completion-20261015/1274/baseline/attempt1/`.
It retains the exact filter, SDK/image/source/binary stamps, build and test logs,
TRX, ordered result projection, coverage, container census, and sanitized collector
JSON. This is a **non-reproduction**, not resolution of qualification38837361197.
The single permitted whole-assembly accumulation attempt follows separately.

### 2026-10-16 baseline attempt 2 and disposition

The final allowed invocation ran03:32:43–03:42:32 UTC on the same immutable
baseline overlay and verified owned binaries. All512 Integration cases passed
(test duration9m38s). Source/binary hashes again passed after execution; terminal
rc0, independent lock-release proof,440846172160B free. Both allowed diagnostic
executions are consumed. No qualification workflow was dispatched.

Selected `runner:1`: session76/process2658776,2,189 dirty-read target rows,
03:41:21.0133899–03:41:22.181952 UTC (1.1685621 seconds), succeeded. The62,442-byte
collector JSON contains three samples and three cached projections, zero errors.
At03:41:22.0642504 UTC the reset session was executing the graph-identity trigger:
request CPU1,058ms,8,717,192 logical reads,0 physical reads,1,472 writes,
query hash`46642951171632A0`, plan hash`7D25E18F90001DF1`, command hash matching
the trigger batch. Its one open transaction began03:41:21.013 UTC; at the final
03:41:23.1094186 sample it had no active request or open transaction. No sampled
session had a positive blocking session ID. There is **no observed blocker owner**
to identify and no basis for recommending a blocking correction.

Across the three samples, SQL container CPU rose1,222,754 microseconds, memory
stayed near2.37GB, swap remained0, and throttling/OOM counters remained0. Container
read bytes did not rise; write bytes rose13,824. Host swap-in/out counters did not
rise; available RAM stayed about8.3million KiB. Host/process/disk observations are
retained, but they do not observe the hypervisor or establish the failed runner's
physical pressure. The cached reset query hash was unchanged from attempt1 while
its plan hash became`118B5636F2180C0C`. This is descriptive, not a controlled
performance comparison: accumulated data, compilation and sample windows differ.
The retained trigger projections are cached batch projections; the two latest
statement hashes do not include the sampled expensive statement, so they must not
be called its actual runtime plan.

Observation: accumulated state coincided with much more work in a successful
reset, and the collector can distinguish that work from an observed blocking
chain. Inference limit: neither run reproduced the original30-second timeout;
no cause or production correction is established. Minimum next action is to
retain this evidence and, under separate bounded authorization, obtain the same
witness on the affected qualification profile or investigate the observed trigger
cost. Do not change SQL, timeouts, worker topology or runner labels on these results.

Attempt2 private artifacts are under the adjacent `baseline/attempt2/` directory;
`baseline/provenance.json` binds both attempts and the instrumentation patch.
The actual SQL container image digest was independently recorded while live.

A reporting-only candidate correction follows these immutable runs: session and
request counters now have distinct SQL aliases and duplicate output keys fail
closed instead of overwriting. In the retained baseline JSON, `cpu_time`, `reads`,
`writes`, and `logical_reads` mean **request** counters; they are null once a
request ends. Session lifetime CPU/I/O totals were lost to that name collision,
so no such totals are claimed above. This alias-only delta is built and reviewed
as current-PR validation, not relabeled as another baseline execution.
