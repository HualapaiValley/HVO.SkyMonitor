# Issue 538 validation record

This work imports immutable CameraAgent execution evidence into LogicHost. The target is `development/v1`. These results provide integration evidence; they do not promote the product to `main` or establish production qualification.

## Scope and compatibility

Owner: `codex:account2:538:1b2916d1`, branch `feature/538-logichost-evidence-import`. The target baseline is `0f5213925907bfcac6d7238ffa2de03d1181804f`.

The receiver requires an explicit binding created by the authenticated observatory owner. Local origin IDs remain local; optional central IDs can be null. Canonical bytes and exact evidence IDs, sequence numbers and hashes are acknowledged only after SQL commit. Duplicate receipts are idempotent; conflicting identities are audited. Unknown dependencies remain retryable. Imported evidence does not mutate central graphs/jobs, fleet manifest acknowledgement or edge authority.

The maintainer approved one CameraAgent exception: only standard-lane candidates project and join live graph executions. This fixes completed executions being reopened by secondary upload claims. No automatic repair of historical durable rows is included. The [terminal-barrier report](issue-538-combined-terminal-barrier.md) retains the original proof and scope.

The LogicHost initial migration is unreleased and was regenerated with its designer/snapshot. A fresh/current-layout database is required; no upgrade from a released legacy schema is claimed. All 66 existing triggers remain; six evidence immutability triggers are added.

## Complete local baseline

Immutable head `97662c323fe72ff0d20efb671c70738a98b7ffdb` completed its Tier M gate. Selector:

```text
scripts/ci:classify pull_request 0f5213925907bfcac6d7238ffa2de03d1181804f 97662c323fe72ff0d20efb671c70738a98b7ffdb
mode=full complete=true
shared=true cameraagent=true logichost=true combined=true delivery=true
deployment=true deployment_catalog=true deployment_shards=true deployment_installer=true
```

Protected CI remains **Development v1 / Preflight** and **Development v1 / Build and Unit**. No full `ci.yml` run was dispatched from this classification.

| Gate | Result |
| --- | --- |
| Tool restore, restore, package audit | Pass |
| Full Debug and Release builds | Pass, zero warnings/errors |
| Full format and four CI-control guards | Pass |
| Exhaustive category discovery | Unit=6680 / Integration=793 / Manual=223 / Soak=1 / External=0 / Hardware=1 |
| Complete Unit selection, invalid Docker endpoint | 6678 passed, zero failed, two expected NotExecuted |
| Complete Integration selection | 793 passed, zero failed/skipped, including 493 LogicHost and18 combined |
| Canonical migrations and pending models, both hosts | Pass |
| Component coverage, all five lanes | Pass |
| Canonical23-slot coverage merge/enforcement | 87.3234% line /72.0876% branch |
| Fixed shared VisibleScene.cs coverage floor | 98.04% line (550/561),90.46% branch (237/262) |
| Catalog lifecycle and focused deployment contracts | Pass |
| All ten deployment shards, maximum parallelism2 | Pass |
| Real disposable offline installer | Pass |
| Installer/replay/processing runners, x64 and ARM64 | Pass; ARM architecture, x64 capabilities and SHA manifests retained |

The expected Unit NotExecuted cases are the macOS-only runtime-access rejection and an unreachable filesystem-provider ambiguous-delete state. Counts are derived from actual UnitTestResult outcomes, not solely TRX Counters.notExecuted.

A publish-only resume corrected an external runner omission: the offline installer contract had left only x64 restore assets before the ARM64 publish. Explicit per-RID restore resolved NETSDK1047. The original failed publish and the successful same-head resume are separate evidence; no successful test suite was repeated. Resume acquired 10:08:16.500695 UTC after481.34 seconds and released 10:08:40.934183 UTC.

The operations-document audit then identified stale category totals in the CI runbook. Documentation was corrected to the discovered6680/793/223 totals; the audit passed. No test category was changed by that correction.

## Current receiver resync correction

Post-baseline inspection found feedback could describe more missing sequence positions than the advertised64-unit receiver limit. Normal exporter batches clamp negotiation, but its shared resync helper uses the global256 ceiling. A valid local256 setting can therefore replay an oversized request. The correction constrains the aggregate inclusive span of returned missing ranges to 64 and reports omitted positions with MissingRangesTruncated. It changes only LogicHost feedback production, not the shared protocol or CameraAgent.

Existing tests are extended without adding categories/cases. Core coverage includes exact64,65 and long.MaxValue gaps and multiple ranges. The combined reproducer seeds a complete real canonical prefix plus two already-enlisted availability rows, leaves every local row pending, and creates32+266 missing positions. It checks the total replay span, actual restart/resync HTTP batches, exact old-origin ACKs/canonical SQL bytes and final watermark/gap convergence.

RED head `c1c69ad564a23bea71bbd6671f3d365c18dc9e72` produced exactly the two expected assertion failures: receiver range end65 versus64 and actual generated replay span256 versus64. Build and scoped format passed before either failure; no fixture, identity, admission or acknowledgement failure preceded the new bounds.

Correction `8631d558bd5ffe0731e6bc93a55ce8716d1e031f` passed the exact core reproducer and all 18 combined Integration cases. Its delta classifier (`97662c32..8631d558`) selected full/complete=false, cameraagent/logichost/combined=true and shared/delivery/deployment=false. The complete selected affected gate passed:4347 Unit cases plus the same two expected NotExecuted, all 791 selected Integration cases (including 493 LogicHost), changed-host Debug and solution Release builds with zero warnings/errors, scoped full format, all four CI-control guards, v1 control and documentation audit, architecture/publish and all three selected component coverage gates. Canonical23-slot coverage enforcement passed at 87.3710%line/72.1015%branch. Shared/delivery reports and unselected deployment/migration/package gates explicitly reuse the unchanged976 baseline; they were not rerun or misattributed to a fresh whole-matrix execution.

The one affected-gate host lock was acquired 10:15:43.708971 UTC after 0.022 seconds and released 10:42:12.052167 UTC. Original RED and GREEN logs/TRX, exact source/binary provenance, selector and reused-report mapping remain separate. Test/category counts are unchanged by the regression extensions.

## Correctness evidence

The original eight lane-ownership regressions failed on the pre-fix implementation and passed with standard-only ownership. They use real ingress and compiled graphs. Existing graph/distribution tests and the complete Unit matrix passed afterward.

The combined harness uses an isolated real source, manually enabled production exporter, actual CameraAgent HTTP transport and delegating TestServer faults. It covers more than256 units,64-unit/256-fact caps, restart/old-origin bytes, lost post-commit responses, corrupted/stale feedback, cross-origin refusal, object-late reconciliation and live evidence denial. Admission pauses before accepted executions reach terminal state; stopping the service is not used to cancel accepted work. Object-late recovery requires the latest Available observation for every reference.

Live capture, raw ingress, artifact upload and the smallest real central EncodedPreview graph continue during HTTP503 evidence denial and receiver-specific SQL failure. A whole shared SQL Server outage is not used to claim normal SQL ingest remains available. Real payload sizes/checksums, exact identities and source→upload→job→output lineage are checked.

Metrics, spans and sanitized pressure/conflict/unavailable logs are asserted. Database and object-store health passed. The direct-seeded fixture's aggregate health is degraded by documented deployment/catalog prerequisites; it is not reported Healthy.

## Performance acceptance remains unresolved

The approved default manifest is E538-I first on a fresh fixture, then E538-M and E538-C. I uses actual W1 1936×1216 Mono16 frames (4,708,352 bytes), real ingress/upload and central EncodedPreview processing. Its idle-before/active/denied/idle-after phases each retain five warmups and thirty measured successful capture/upload/job tuples. Three-minute deadlines, bounded raw/central backlogs, zero correctness loss and the original thresholds remain enforced. Idle bracket drift above 25% produces Inconclusive, not a wider tolerance.

The uninstrumented clean 21e0dd0401b1bb42c838be42636716d790aba0e4 trial completed I/M/C with 8 Pass,2capture Inconclusive-idle-drift and0 Regression. Capture p95 idle-before1209.4418 ms versus idle-after478.6294 ms drifted152.689%; overall Investigate is not acceptance. All 120 measured tuples had exact sizes/checksums and output lineage; no failure/overflow/loss. Final convergence was160 raw captures/320 artifact acknowledgements and zero held/pending/jobs.

M drained10003 units in106954.8 ms (93.49744 units/s),157 submissions, maximum 64 units and one in-flight request.102keyset pages had p95 1.3949 ms. No pending/retry/quarantine/projection errors remained. C1/4/8 each retained 20 warmups+200 measured logical requests: p95 12.9455/65.3994/187.819 ms;135.282/160.163/163.957logical requests/s; physical requests/retries200/0,296/96,471/271; all converged. This is an initial absolute importer baseline and one bracketed trial. The new feedback correction changes a measured importer path, so these M/C results are historical rather than current-source acceptance.

Diagnostic-only cb392a61 retained 140 producers, all eight existing producer stage values,2299runtime samples and all 120 operation joins, without missing/dropped/evicted records. All five idle-before warmups were already slow. Measured rendering then transitioned from1321–1348 ms through 742 ms to420–456 ms. Rendering localization and overlapping process-wide compilation are established; a causal JIT/method/tier explanation is not. GC pause durations alone did not account for the difference. Diagnostic overhead prevents acceptance use.

Diagnostic-only f9273212 retained the same 140 selections, all 120 valid operation tuples, no correctness/overflow failure and complete final convergence. Its actual capture p95 brackets1498.0823 ms versus484.4697 ms drifted209.2211%. The finalized EventPipe trace reports907710enumerated events and zero EventsLost, matching all 140 identity markers and full source/nonce/PID/command provenance. The first decoder hit its512 MiB output cap, retaining only the early21.087 seconds. Full-span method attribution is unavailable from that partial output. Actual147890 AllocationTick events were observed; the earlier contrary methodology sentence is corrected. Sampled thread-stack occupancy is not on-CPU time. Unresolved/depth-truncated stacks and process-wide counter limits remain explicit.

Compact offline recovery of the SAME trace completed after the affected gate released capacity. One focused external reader build passed 0 warnings/errors, then exactly one 9.892second decode retained907710/907710 enumerated events with zero filtered/unexported/unvisited events and reported loss 0. Full source/mode/flag/command/nonce/PID and all140 selections matched. Reader peak sampled RSS was534134784 bytes. The unchanged512 MiB/3M-event/600second/4 GiB-sampled-RSS/2 GiB-file bounds held. Original bytes were reverified unchanged; lock released 10:42:46.622050 UTC. Attribution still returns Partial/exit2 because of 533 truncated sample stacks,11618 unresolved frames and 12 opaque payload events (raw bytes retained); full event export does not establish a causal JIT or GC finding. Visibility SHA2561EEB59F9C44D1B512CD39F01CD74340C0C0A9F770F3609D7C9209693A9200807. No recollection, outlier deletion, extra warmup, relaxed threshold or acceptance waiver is implied. A separately named30-cold-operation prefix followed by the unchanged four steady-state phases is a maintainer decision still pending; no changed-count run is authorized.


Historical uninstrumented resource baseline, derived directly from the checksummed final JSON:

| Phase | Process CPU s | Allocated MiB | Peak sampled RSS MiB | Physical write MiB | SQL commands all/evidence | Separate SQL CPU s | SQL RSS end MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| idle-before | 39.000 | 3988.53 | 919.83 | 1119.641 | 8158/4 | 2.066 | 1335.54 |
| active | 37.835 | 6131.73 | 1052.18 | 1310.961 | 32667/24437 | 7.181 | 1369.86 |
| denied | 30.886 | 4584.27 | 1111.07 | 1455.047 | 9125/970 | 2.810 | 1387.04 |
| idle-after | 27.579 | 4275.42 | 1247.00 | 1161.859 | 9164/966 | 2.879 | 1401.49 |
| M | 71.478 | 15587.22 | 1302.53 | 2431.270 | 167586/165610 | 40.844 | 1439.33 |
| C1 | 1.907 | 304.14 | 1161.64 | 21.660 | 3234/3200 | 0.714 | 1440.43 |
| C4 | 1.525 | 323.61 | 1143.14 | 21.586 | 3227/3200 | 0.859 | 1450.56 |
| C8 | 1.652 | 366.17 | 1152.96 | 21.594 | 3228/3200 | 0.808 | 1452.12 |

The JSON also retains RSS bookends, physical reads, logical I/O characters and separate SQL cgroup block-I/O deltas/lifetime memory peak. These values are not independent trials or current-source performance acceptance.

Process CPU/allocation/RSS/I/O aggregates the harness and both in-process hosts; SQL cgroup counters are separate. Sampled RSS is not an exact allocator peak, SQL memory.peak is lifetime, and logical I/O characters are not physical network bytes. The production reconciler remains enabled after sender deactivation; idle-after retains that due-check workload.

## Retention and remaining gates

Checksummed raw evidence is retained under ignored TestResults and copied outside worktrees before cleanup:

- /home/roys/review-evidence/issue538/candidate-97662c323fe72ff0d20efb671c70738a98b7ffdb:199 files,865876615 bytes; manifest SHA256 defe3e3c3bbbac47a6b959b5543b47505d7b6979e6d1df58b3b57f2b0fd58144.
- /home/roys/review-evidence/issue538/candidate-8631d558bd5ffe0731e6bc93a55ce8716d1e031f:98files,717313289bytes; manifest SHA256874584107984a0d143bae44bc719a4658c9b1e0b2f7abd53b20aeb2a971c224c.
- /home/roys/review-evidence/issue538/eventpipe-f9273212-full:48 files,1030387375 bytes; manifest SHA25676ed324d28370549de9c3ff27abe1c21ff187991c4c27d94bddaf57bac1eff48.
- Original barrier backup SHA25629181E993B160B3944282288385AC2BA908D28B1F7185318F6420149D46981CD.
- Completed uninstrumented performance JSON SHA2562B8036105D913B724448016E8D9AD3AC3551557190BE3636E1F6CC59DCC7E902.
- Finalized raw trace SHA256F51D706FECC6997ABB50EA9E7138E89D4721E9E60CF097D37A18E1EF54151842.

Remaining: resolved performance acceptance, independent exact-range Deep review, final target synchronization under the finalization reservation, current-head protected v1 checks, merge and cleanup. No review convergence or merge is claimed here.

Record prepared after affected validation at 2026-10-15T10:43:41.686671+00:00. Source/test/build inputs remained pinned throughout execution; this document adds no runtime change.
