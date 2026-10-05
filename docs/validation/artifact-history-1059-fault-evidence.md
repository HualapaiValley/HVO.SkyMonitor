# Disposable artifact-fact fault evidence (#1059)

This is the operator-approved evidence slice of #1059, not completion of that
issue. The 2026-10-05 scope decision keeps authoritative storage changes and
#1044 installer recovery outside this PR. The issue remains open for those
prerequisites. No installed instance, schedule, physical camera, or retained
`/mnt/hvo-1058` data participates.

## Reproduction and fixture

Run the pinned SDK 10.0.401 in Release:

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj \
  --configuration Release --filter 'FullyQualifiedName~CameraAgentArtifactFactsFaultTests' \
  --logger 'trx;LogFileName=artifact-facts-faults.trx'
```

The Manual tests use the existing real-schema `GalleryPerformanceFixture` and
non-authoritative `HourlyArtifactFactsPrototype`. The primary matrix seeds two
captures and eight scene-bearing output descriptors in a private disposable
`hvo-gallery-performance-*` temporary directory. Each case constructs an
uninterrupted reference projection from the **same** source journal. Neither
layout becomes a production writer. Fact files are individually immutable JSON
files grouped into hourly directories; they are not a compact sealed hourly
segment and have no segment-offset contract.

A parent launches the built acceptance assembly through `dotnet vstest`, selecting
only `ProjectorCrashWorker`. The worker signals the exact reached boundary with
its PID and resource counters and waits. The parent kills the whole child process
tree; it does not simulate interruption by throwing an exception. A 45-second
rendezvous deadline and 90-second worker watchdog bound failed orchestration.
The worker is Inconclusive when discovered outside that parent protocol.

## Checked boundaries

| Boundary | Assertion after process death and reprojection |
| --- | --- |
| Staged write, file fsync, pre-rename | Pending bytes do not become a ready generation; retry produces exact reference facts and removes pending files. |
| Rename before directory sync, after directory sync | Reconciliation adopts the exact already-published fact without duplicate names or changed bytes. |
| Raw lookup commit, before and after output lookup commit | Partial SQLite state remains unready, then converges to two captures and eight outputs with verified descriptor identities and ordered source links. |
| Before and after ready commit | Only the completed generation can pass snapshot validation; both interruptions converge and a second pass retains identical facts. |
| Missing, truncated, changed raw and product facts | Snapshot validation and projection fail closed; the damaged evidence is left unchanged. Restoring the exact original test bytes permits verification. |
| Concurrent hold, deletion and availability commits | A second SQLite writer commits during the source read snapshot; projection detects the changed source and remains unready. Retry records the hold, four removal tombstones and four missing outputs without rewriting facts. |

Every kill case compares sorted relative paths and SHA-256 of **all** JSON facts
against the uninterrupted reference, verifies the ready SQLite index, identity and
ordered lineage, and compares a length-framed logical digest of source manifests,
output descriptors and ordered source rows before/after. The second projection
checks idempotence. The corruption cases preserve evidence and fail closed; they
do not implement a quarantine mover.

## Measurement contract

This is a functional fault campaign with small fixtures, not a comparative
performance qualification. Before values for a new fault harness are N/A. One
independent disposable fixture per boundary records recovery wall time and row
visits, worker CPU and RSS at the stopped boundary, kernel process-lifetime peak
RSS, parent process RSS before/after recovery, and retained file lengths at the
boundary and before/after recovery. Parent peak RSS is cumulative across its test
process, not attributable to one recovery. File-length observations are not peak
allocated disk blocks or inode metadata; fsync calls are exercised but not counted.
No p95, performance improvement, installer capture pause, OS-cold behavior or
power-loss durability claim follows from these observations. Reproduction output
retains the exact measurements in the TRX.

## Explicitly unqualified acceptance

Two unchanged-recovery fixtures (3 and 12 captures) count one scan visit for
every retained capture and compare all facts. They document the current
full-history traversal. This is a pinned limitation, **not** satisfaction of
#1059's bounded-history recovery gate. A qualified future recovery protocol needs
an authoritative durable generation/cursor and a bounded interrupted-work set,
then scale-independent recovery tests; this offline prototype lacks that contract.

A physical-product negative control deletes an existing referenced payload,
records its length and SHA-256, and demonstrates that fact/index verification
still succeeds. That verifier authenticates metadata facts, not physical capture
or product recovery. The fixture omits most physical artifacts. Real capture
acknowledgement, physical-product publication/corruption, replay pin lifecycle,
and concurrent physical retention must be qualified against the eventual
production protocol, with exact bytes and multi-source provenance.

The merged `InstanceBackupManager.CreateAsync` still invokes `tar --create --gzip`
on the complete `config` and `state` trees. It therefore includes artifact files
and does not implement the selected mutable-state-only upgrade backup. #1044
records a separate unqualified snapshot candidate and a failing precommit
payload-preservation case. This PR neither adopts that candidate nor runs an
installer on an installed instance. A modeled index copy is not an installer
restore drill and is intentionally not presented as one.

Before full #1059 acceptance, #1044 must supply a qualified precommit boundary
that preserves every preexisting artifact referenced by the restored database
without putting artifacts into upgrade backups. Then run actual disposable
installer failure/restore with physical artifacts, candidate-created orphan
allowance, checksums, repeated crash recovery, pause and peak disk/RSS evidence.
Combine that with the authoritative bounded publication protocol and genuine
capture-acknowledgement/replay/retention tests. #1059 stays open and blocked on
those named outcomes after this evidence slice merges.

## Initial focused run (2026-10-05 UTC)

At source base `499df22901b10c201f76408615753d7b95d00015` plus the five
files in this evidence slice, the Release build passed with zero warnings under
SDK 10.0.401. The Linux 6.8.0-142 x86-64 host exposes eight Intel Core Ultra 9
285H CPUs; `/tmp` is on the host root filesystem (ext4, not a cold-cache
or dedicated-device run). The test process ran sequentially; the host is shared.

The first focused selection returned 19 passes, one expected worker skip, and
one fixture failure: `Expired` is not an allowed authoritative availability
state. Correcting that synthetic mutation to `Missing` passed the affected
concurrency test separately (1/1). Both original reports are retained; no crash
case or recovery threshold was weakened. Actual Manual discovery is 79 cases,
up from 58; repository total is 188 before other concurrent branch changes.

All ten process-kill cases produced 10 identical fact files after recovery,
with two capture rows and eight output rows, and no remaining pending files.
Recovery wall time ranged from 27.85 to 227.08 ms across the different boundaries;
these are ten different failure cases, not repeated-trial percentiles. Worker
process RSS at the boundary ranged from 146,567,168 to 160,595,968 bytes. Retained
shadow file lengths after recovery were 3,459,346 bytes in each case. The full-scan
negative controls visited 3/3 and 12/12 retained captures (48.40 and 172.55 ms,
respectively, single warm runs). Those results establish neither a production
pause budget nor bounded history recovery.

The logical source digest in every crash case was
`FA72FBEE9C5B0A45E322A7E4B85F33CDE506FB6E0E607B213FBBB092C5995A97`.
Initial TRX SHA-256:
`f72dba1198d4de5f72d6a0ba9db1fcf971e002873df8cd1c93095f5f093041b3`;
affected correction TRX SHA-256:
`c3517bfc03a481444a223b24435c46016985394fbcbf74dfd1d722cfb5cf9d13`.
These files include the original failed fixture attempt and per-boundary stdout;
they are retained outside the source worktree, not committed artifact payloads.

## Complete local candidate gate

On immutable candidate `2db485443e33d4ce407c146bd46ae49528f9c6a4`, SDK
10.0.401 tool/solution restore, warning-clean Debug and Release solution builds,
solution format, package audit and all four CI-control guards passed. Positive
Unit selection with an invalid Docker endpoint returned 5,778 passes and seven
existing skips; sequential Integration returned **685 passes**, zero failures.
The final fault selection passed 20 cases with one expected worker skip. The
23 workflow-selected coverage reports merged through ReportGenerator 5.5.11
passed the unchanged policy at 86.6953% line and 71.6347% branch coverage.
CameraAgent component coverage also passed (85.3177% line, 67.2278% branch), and
its host/replay-runner publish gate passed. Architecture's 18 Unit and seven
Integration cases passed within the complete selections.

The full run exposed an inherited inventory error: LogicHost's Integration
assembly discovered and passed 418 cases while its inventory said 417. Complete
unfiltered and six-category discovery, parsed by stable case IDs from VSTest
completion messages, proves a complete disjoint partition of 460 cases:
Unit=4, Integration=418, Manual=38, and zero other categories. The extra case
is not a repeated execution or category overlap. This PR corrects that inventory
and the repository Integration total to 685. These metadata-only corrections
preserve the completed suite evidence; the classifier, control guards and docs
checks run again on the corrected head. Concurrent target changes require an
additive count reconciliation during final synchronization.

The protected profile is Development v1 Preflight and Build and Unit. These
local results do not authorize full `ci.yml` dispatch, installed upgrades,
production hourly storage adoption or closing #1059.
