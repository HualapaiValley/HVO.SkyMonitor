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
