# Issue 538 combined acceptance: completed lane work leaves executions active

The original combined smoke exposed an existing CameraAgent execution-lifecycle defect. Acquisition, actual raw
upload, central preview processing and three additional live captures complete under a denied evidence sink.
After normal admission pauses, the durable execution barrier never empties despite completed processing work.
The terminal predicate and enabled lanes remain unchanged. The maintainer subsequently authorized the narrow
CameraAgent correction within issue 538. Its focused regression evidence and passing combined acceptance are
recorded below; performance acceptance remains pending.

## Reproduction and retained evidence

The Release test assembly was built with SDK 10.0.401 and `-warnaserror`: zero warnings and errors. The diagnostic
run used clean source `0511d814807fd14f2b1d0236b9342d5e739163f3`, including the core receiver through
`09b150a09f393f693ac0e5e769286ee2bbd3d77b`. Its empty dirty fingerprint is
`E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`.

One `flock -o -w 600 /tmp/hvo-520-heavy.lock` acquisition started at 2026-10-15T06:37:21Z and acquired immediately.
It ran only this command (paths shortened here to the project directory):

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests.csproj \
  --no-build --configuration Release \
  --filter 'FullyQualifiedName~ExecutionEvidenceCombinedTests.DeniedEvidenceSinkCannotStopLiveCaptureArtifactUploadOrCentralPreview' \
  --logger 'trx;LogFileName=smoke.trx' \
  --results-directory tests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests/TestResults/issue-538/diagnostic-0511d814/smoke
```

Outcome: **0 passed, 1 failed**, approximately 76 seconds, with cancellation in the 60-second terminal wait after
`PauseAsync` returned `Paused`. The command exited, released the heavy lock, and disposed its SQL/Redis/Mailpit
containers. Full9 and manual performance did not run. The earlier SQLite-path failure and corrected smoke failure
remain separate and unmodified.

Ignored evidence paths relative to the worktree:

- Command, timestamps, source fingerprint and exit status:
  `tests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests/TestResults/issue-538/diagnostic-0511d814/status.json`.
- Log and TRX: the same directory's `smoke/test.log` and `smoke/smoke.trx`.
- Online SQLite backup taken **before isolated host cleanup**:
  `tests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests/bin/Release/net10.0/TestResults/issue-538/diagnostics/20261015T0638379501335Z-be9932b4aed8439eac7bde86a860a566/raw-ingress.db`.
- Offline read-only extraction: `durable-snapshot.json` beside that backup. Live JSON serialization failed on a
  non-finite telemetry number; the backup was already complete. The diagnostic correction permits named non-finite
  numbers and writes durable SQL rows before optional runtime telemetry. That correction has not been Docker-run.
- Checksum-verified copies of the journal and extracted JSON are retained outside build output under
  `tests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests/TestResults/issue-538/diagnostic-0511d814/durable/`.

The backup is 1,970,176 bytes, SQLite integrity check `ok`, SHA-256
`29181E993B160B3944282288385AC2BA908D28B1F7185318F6420149D46981CD`.
No identity, provisioning or credential runtime files were copied.

## Durable result

| Record | Observed state before cleanup |
| --- | --- |
| Capture control | `paused`, version 2 |
| Raw captures | 8 `committed` |
| Standard lane | 8 `completed`, zero unfinished |
| Upload lane | 8 `completed`, zero unfinished |
| Live executions | 8 `Running`, every `completed_unix_ms` null, every `attempt_count` 3 |
| Execution nodes | All 72 `Completed` |
| Node attempts | All 72 `Completed` |

For example capture sequence 1, capture `6cb233e518a28dc9bae68de06c06c83c`, execution
`a4f3ebee9d0da18cf611e2145609bad3`, is `Running` after its standard work completed at
1792046254576 ms and its upload work completed at 1792046255838 ms. Every other retained capture exhibits the same
contradiction. The snapshot contains the exact IDs, linked lane rows and node attempt timestamps.

This is completed work with an incorrect execution state, not evidence of an in-flight pipeline still needing
more time. `ReadOldestActiveExecutionKeyAsync` correctly considers `Pending` and `Running` live/replay rows active.
The registered capture service is `HVO.SkyMonitor.CameraAgent.Common.Capture.CameraCaptureService`, from the Common
assembly; the harness resolves that actual hosted type, with no host alias substitute.

## Source explanation and authorized correction

In `SqliteCaptureLaneStore.ReadCandidateAsync`, the live execution join is enabled for every lane when the
execution schema exists. In `ClaimAsync`, the block beginning at the `candidate.ExecutionId` guard marks that
execution `Running`, clears completion and increments attempts for **any** lane. However, the handler's
`context.Execution` is populated only for the `standard` lane. `CompleteAsync` updates execution completion only
when that context exists. An upload claim after standard completion therefore reopens a completed execution and
its eventual successful completion leaves it active. A retry/reclaim repeats the state mutation. The retained
rows and exact source control flow agree.

The authorized patch exposes execution identity only on standard-lane candidates. In `ReadCandidateAsync`, both
existing SQL fragments now derive from:

```csharp
var includeExecution = _hasExecutionSchema &&
    string.Equals(lane.Name, "standard", StringComparison.Ordinal);
var executionColumns = includeExecution
    ? /* existing execution columns */
    : /* existing NULL columns */;
var executionJoin = includeExecution
    ? /* existing live-execution join */
    : string.Empty;
```

Product commit `739e9d6167f3717045086b7bc0de75840d117a4c` applies this guard. It keeps standard graph execution authority
on the standard lane, including candidate-based failure paths; guarding only the claim-start block would leave
the nonstandard `MarkUnprocessableAsync` failure path able to mutate graph state. This correction prevents invalid
transitions on subsequent lane operations. It does not repair already affected persisted executions or rewrite
their history. Those rows remain diagnosable by the execution/node/lane comparisons above; no automatic recovery
or schema change is included in the approved exception.

Regression commit `b76f1a9e` adds eight Unit cases in the owning CameraAgent test project, using actual raw ingress,
the production-compiled configured preview graph and durable lane operations. Upload and an enabled generic
secondary lane are each tested before and after standard completion:

1. Commit a real raw capture under a production-compiled configured graph. Complete its standard processing, then
   claim, release/retry, reclaim and complete upload. Assert the execution's terminal status, completion time,
   attempt count, node attempts and output publication remain unchanged throughout nonstandard work, and the
   evidence source reports no active execution after standard completion.
2. Reverse the ordering: claim secondary work while standard remains pending. Assert it does not start, complete or
   fail the graph execution; standard alone performs its `Pending -> Running -> Completed` transitions.
   Missing-payload cases additionally exercise candidate-based quarantine/abandon handling
   without changing the graph execution; restoring the payload allows pending standard work to complete.
3. Keep a standard execution active through actual processing. Assert the barrier remains non-null until its
   real terminal transition. This prevents a workaround that merely weakens active-work detection.

All eight cases failed on clean regression commit `b76f1a9e` against the original product, at the graph-state
assertions. All eight passed after the guard on clean commit `739e9d6167f3717045086b7bc0de75840d117a4c`; the Release
build used `-warnaserror`. Logs, source ledgers and TRX files are retained separately under
`TestResults/issue538-edge-fix/red/` and `TestResults/issue538-edge-fix/green/` in the integration worktree. The
original failing combined evidence above remains unmodified.

## Combined acceptance after the correction

On clean combined commit `0029117718616d362bf67103ff3676a2a496f806`, the Release build passed with zero warnings
and errors, the unchanged denied-sink smoke passed 1/1 in 16 seconds, and the full issue-specific combined
selection passed 9/9 with zero skips in 33 seconds. A single bounded lock covered build, smoke and full9; it
released at 2026-10-15T06:59:09Z. Enabled lanes and the accepted-work terminal barrier were unchanged.

The candidate's `src` tree `c03bc86586f4340dbbf92672d8630ed08ec48b73` and `tests` tree
`e569195b6ae34f0d0af59262c94766d9445949ed` exactly match parent fix commit `739e9d6167f3717045086b7bc0de75840d117a4c`.
The combined worktree retains source/config equality proof, runtime binary/fixture/catalog checksums, log, TRX
and stage status under `tests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests/TestResults/issue-538/corrective-00291177/`.

The first performance invocation on the same candidate failed before completing its first phase: it attempted
full-tick equality between a telemetry timestamp and its deliberately millisecond-normalized raw descriptor
timestamp. `RawCaptureDescriptorFactory.NormalizeEvidence` truncates this persisted timestamp; lane telemetry
preserves the original submission ticks. This is a measurement join defect, not a product regression result.
That failed run and checksummed analysis remain under `TestResults/issue-538/perf-00291177/` in the combined project.

Test-only commit `21e0dd0401b1bb42c838be42636716d790aba0e4` corrects the unique join to exact Unix milliseconds,
asserts the full telemetry timestamp is within `[0, 1 ms)` after the descriptor timestamp, and uses full precision
for measured-admission exclusion. Both original timestamps remain in the output. Focused Release build and
scoped format verification passed; workload counts, graphs, phase deadlines and regression thresholds are
unchanged. Its parent cherry-pick is `349d81c5405db3a72fe50f45412a0864f8f8dbbd`. A fresh exclusive measurement is
required before performance acceptance; the failed attempt is not a passing baseline.

## Relationship to current work

[Issue 1175](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1175) includes lifecycle drain gaps in items 6
and 7, but neither describes nonstandard claims reopening live graph executions. Its item 4 concerns audited
release of evidence-invalid quarantine. [PR 1235](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1235) explicitly
implements only item 4; its description and file scope were read without mutation. File overlap in the lane store
does not establish that it fixes this defect, and the observed rows are completed rather than quarantined.

The maintainer explicitly approved including this narrow fix and its regression tests in issue 538. The
[scope decision](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/538#issuecomment-6186238503) records the
exception to the original CameraAgent exclusion. The issue body now includes that exception. Broader edge
features, schema changes and automatic historical-state repair remain excluded. Combined correctness has passed;
performance and the remaining lifecycle gates are still pending.
