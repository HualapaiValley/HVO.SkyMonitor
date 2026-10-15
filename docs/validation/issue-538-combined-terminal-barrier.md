# Issue 538 combined acceptance: completed lane work leaves executions active

The combined smoke is blocked by an existing CameraAgent execution-lifecycle defect. Acquisition, actual raw
upload, central preview processing and three additional live captures complete under a denied evidence sink.
After normal admission pauses, the durable execution barrier never empties despite completed processing work.
The terminal predicate, enabled lanes and product sources have not been changed to bypass this result.

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

## Source explanation and proposed prerequisite

In `SqliteCaptureLaneStore.ReadCandidateAsync`, the live execution join is enabled for every lane when the
execution schema exists. In `ClaimAsync`, the block beginning at the `candidate.ExecutionId` guard marks that
execution `Running`, clears completion and increments attempts for **any** lane. However, the handler's
`context.Execution` is populated only for the `standard` lane. `CompleteAsync` updates execution completion only
when that context exists. An upload claim after standard completion therefore reopens a completed execution and
its eventual successful completion leaves it active. A retry/reclaim repeats the state mutation. The retained
rows and exact source control flow agree.

The narrow suggested patch, for an explicitly authorized CameraAgent owner to review, is to expose execution
identity only on standard-lane candidates. In `ReadCandidateAsync`, derive the two existing SQL fragments from:

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

This is a proposed change, not an applied or validated product patch. It keeps standard graph execution authority
on the standard lane, including candidate-based failure paths. Review all candidate execution mutations when
implementing it; guarding only the claim-start block leaves the nonstandard `MarkUnprocessableAsync` failure path
able to mutate graph state. A fix for new work must also explicitly decide how already affected persisted rows
are diagnosed or recovered; this test does not rewrite them.

Suggested focused regression tests in the owning CameraAgent test project (not added by this delegate):

1. Commit a real raw capture under a production-compiled configured graph. Complete its standard processing, then
   claim, release/retry, reclaim and complete upload. Assert the execution's terminal status, completion time,
   attempt count, node attempts and output publication remain unchanged throughout nonstandard work, and the
   evidence source reports no active execution after standard completion.
2. Reverse the ordering: claim upload while standard remains pending. Assert upload does not start, complete or
   fail the graph execution; standard alone performs its `Pending -> Running -> Completed` transitions. Repeat
   with an enabled environment-association lane where practical.
3. Keep a standard execution active through actual processing. Assert the barrier remains non-null until its
   real terminal transition. This prevents a workaround that merely weakens active-work detection.
4. Re-run this combined denied-sink smoke unchanged, then the nine combined cases. Only after a fixed coherent
   candidate passes should E538-I/M/C measurement begin, with its approved graph/phase manifest unchanged.

## Relationship to current work

[Issue 1175](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1175) includes lifecycle drain gaps in items 6
and 7, but neither describes nonstandard claims reopening live graph executions. Its item 4 concerns audited
release of evidence-invalid quarantine. [PR 1235](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1235) explicitly
implements only item 4; its description and file scope were read without mutation. File overlap in the lane store
does not establish that it fixes this defect, and the observed rows are completed rather than quarantined.

Issue 538 explicitly excludes CameraAgent product implementation. A narrow prerequisite assignment or another
authorized owner is required for the product correction; this delegate has changed only its combined test project
and this evidence summary. There is no passing full9 result or performance result to report yet.
