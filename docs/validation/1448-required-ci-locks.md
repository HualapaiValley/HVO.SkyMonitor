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
memory grants, and application/waiting locks. No source joins another DMV.
Each source/cycle owns a fresh unpooled session, with separate two-second opening
and command/read bounds and automatic connection retry disabled. A failed source
cannot suppress the others or poison a later cycle. Output contains only numeric
IDs, timestamps, enum/reason values, lock resource identifiers and query hashes;
it excludes SQL text, parameter values, credentials and object payloads.

Default observation preflights immediately, then samples after 5/8/8-second
intervals. The combined test samples at one-second intervals, at most 300 cycles
and five minutes overall. Queries have fixed row caps; a capped snapshot is not
proof that no other session or lock exists. Observations are passive: subject
faults/cancellation/success remain authoritative, and existing liveness, SQL and
object-lock budgets and assertions are unchanged. No production query, lock
policy, worker or runner configuration is changed.

A controlled live probe held a row lock on one disposable SQL instance and
verified that the collector named the exact blocking session while all five
independent sources succeeded. It also preserved the original subject exception.
This validates the collector, not the CI root cause. The probe exited zero and
removed its container/tmpfs state, private files and port lock. Evidence:
`/tmp/t3-1448-invalidation-probe/direct-observer-live.log` and
`/tmp/t3-1448-locks-native/live-probe-lifecycle.log`.

Historical runner timeout and underlying artifact/fence stall acceptance remain
**OPEN**. The next reviewed diagnostic qualification must identify a blocker or
runtime condition before a production correction is justified. A green rerun
alone does not resolve that acceptance.
