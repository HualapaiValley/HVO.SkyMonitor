# Issue 538 validation record

This work imports immutable CameraAgent execution evidence into LogicHost. The target is `development/v1`. These results provide integration evidence; they do not promote the product to `main` or establish production qualification.

## Scope and compatibility

Owner: `codex:account2:538:1b2916d1`, branch `feature/538-logichost-evidence-import`. The initial target baseline was `0f5213925907bfcac6d7238ffa2de03d1181804f`; the preliminary integrated target is `bce9bac0cb7b8fca9aac19f2a5fbf7dbe9c4ef8b`.

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

## Preliminary upstream integration

Draft PR#1249 opened after the passing829 documentation head. The target then advanced to `bce9bac0cb7b8fca9aac19f2a5fbf7dbe9c4ef8b` (merged#1235). It was incorporated before initial review as `a02c0a0b49f47fca3af25aec06cdd72191c60bdc`; this is implementation integration, not the guarded final synchronization/ready transition.

The lane store retains upstream raw-evidence invalidation/recovery and the approved standard-only graph candidate projection/join. Astronomy schema assertions are semantically equivalent to the upstream assertions, so the upstream files were retained. Inventory conflicts preserve both independent deltas: CameraAgent Unit3530/Integration248/Manual65; declared solution totals6697/803/224. Fresh discovery and a coherent selected gate are still required. The delta classifier selects all component lanes and deployment shards/installer (catalog=false). The pre-integration evidence remains archived and is not claimed as validation of this new capture boundary.

The diagnostic ingress wrapper must forward the newly introduced operational-init API to the actual ingress. This test-only compatibility change and the approved measurement mode are isolated in the combined project. No broader CameraAgent product change is proposed. Independent Deep initial review and protected CI have not started; PR remains draft.

## Correctness evidence

The original eight lane-ownership regressions failed on the pre-fix implementation and passed with standard-only ownership. They use real ingress and compiled graphs. Existing graph/distribution tests and the complete Unit matrix passed afterward.

The combined harness uses an isolated real source, manually enabled production exporter, actual CameraAgent HTTP transport and delegating TestServer faults. It covers more than256 units,64-unit/256-fact caps, restart/old-origin bytes, lost post-commit responses, corrupted/stale feedback, cross-origin refusal, object-late reconciliation and live evidence denial. Admission pauses before accepted executions reach terminal state; stopping the service is not used to cancel accepted work. Object-late recovery requires the latest Available observation for every reference.

Live capture, raw ingress, artifact upload and the smallest real central EncodedPreview graph continue during HTTP503 evidence denial and receiver-specific SQL failure. A whole shared SQL Server outage is not used to claim normal SQL ingest remains available. Real payload sizes/checksums, exact identities and source→upload→job→output lineage are checked.

Metrics, spans and sanitized pressure/conflict/unavailable logs are asserted. Database and object-store health passed. The direct-seeded fixture's aggregate health is degraded by documented deployment/catalog prerequisites; it is not reported Healthy.

## Approved cold-start and steady-state acceptance

The maintainer explicitly approved retaining30 cold-start operations before the unchanged four5warmup+30measured interference phases. The opt-in command is `HVO_ISSUE_538_STEADY_STATE=1` on the existing Manual method; default and diagnostic140-selection paths remain unchanged. The [mode specification](../../tests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests/Infrastructure/ExecutionEvidenceSteadyStateAcceptance.md) records the separate namespace, exact counts, full terminal/upload/job/export barrier, natural-operation retention and unchanged limits. Diagnostic/trace mixing is refused. No extra adaptive acquisition or outlier removal is used.

Implementation `5f654206` plus the one-line explicit cancellation-token correction `3997925408d2d88f1228f787e4f2ad2a700c1e0f` passed focused Release with zero warnings/errors, scoped full format and9/9 combined correctness cases with zero skips. The initial CS7036 compile failure is retained separately. The passing binaries embed39979254;839runtime/config/fixture/catalog files were reverified before and after both correctness and measurement. Parent `80f4c1c0a2611d37a5415cecb5e2f18eb7d31c01` has exactly equal src/tests/root build configuration; only documentation differs.

ONE approved Manual invocation on clean39979254 passed1/1 and produced **10Pass,0Inconclusive,0Regression**. The sole lock acquired11:06:34.587342UTC after0.0302386s and released11:11:03.301626UTC. Cold capture p951438.1485ms is separately retained and excluded from interference comparisons. All170declared selections plus21natural operations are retained:191consecutive capture/commit sequences,899295232raw bytes,191distinct capture/artifact/job/output identities, exact SHA and output-lineage checks. Observed raw pending peaked at3, held bytes14131239, central pending0; all failure/loss/pressure/quarantine/abandonment counters stayed zero. Final convergence:191raw captures,382artifact acknowledgements, zero held/pending raw uploads or central jobs.

| Phase | Capture-to-ingress p95 ms | Raw ingress p95 ms | Upload p95 ms | Central p95 ms | Operations/s |
| --- | ---: | ---: | ---: | ---: | ---: |
| cold30, separate | 1438.1485 | 22.7414 | 252.5829 | 149.4889 | 1.13440 |
| idle-before | 475.8985 | 22.0794 | 166.9980 | 103.9701 | 1.64930 |
| active | 524.6250 | 25.3893 | 224.0339 | 109.5299 | 1.54017 |
| denied | 498.3396 | 20.1606 | 227.3220 | 114.2943 | 1.51465 |
| idle-after | 467.0845 | 22.8549 | 207.1146 | 112.6240 | 1.62586 |

The unchanged bracket-drift fractions are1.8870%capture,3.5123%ingress,24.0222%upload,8.3235%central and1.4422%throughput. Upload variation is close to the25%inconclusive bound; this remains one bracketed trial, not independent-trial confidence or a cold-start improvement. All ten comparisons satisfy the original25%material threshold. The production reconciler remains enabled: idle-before due checks447→319, later stages1000/truncated. Cold-prefix convergence and its added imported history are retained explicitly.

M remeasured10000observations+3real source units in108610.3145ms,92.07229units/s;157submissions, maximum64units/one in-flight, submission p95573.5470ms;102keyset pages p951.2577ms. Final ACK-through10003, zero pending/retry/quarantine/conflict/projection/pruning loss; canonical set SHA256713BFE7FC08DDE962BBD2742379BD6915DFD6182041368B75966EA85F36916DD. C1/4/8 each retained20warmup+200measured logical requests: p9512.6986/31.2923/84.3582ms,136.7576/171.2612/152.7346logical requests/s; physical requests200/290/494 and pressure retries0/90/294. All converged.

Current-source resource observations (cold-selected includes isolated-fixture startup; cold-drain is separate):

| Phase | Process CPU s | Allocated MiB | Peak sampled RSS MiB | Physical write MiB | SQL commands all/evidence | Separate SQL CPU s | SQL cgroup memory end MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| cold-selected | 54.807 | 4460.08 | 1089.59 | 1142.863 | 8235/18 | 2.837 | 984.21 |
| cold-drain | 3.337 | 668.54 | 894.72 | 101.270 | 1714/1121 | 0.520 | 994.47 |
| idle-before | 27.990 | 4049.85 | 1036.65 | 1112.871 | 9158/968 | 2.401 | 1008.21 |
| active | 37.333 | 6197.72 | 1099.17 | 1322.047 | 31925/23693 | 7.351 | 1045.61 |
| denied | 31.158 | 4621.38 | 1391.00 | 1471.715 | 9183/968 | 3.008 | 1059.41 |
| idle-after | 27.473 | 4261.85 | 1187.04 | 1188.227 | 8947/725 | 3.285 | 1077.70 |
| M | 74.038 | 15674.30 | 1254.49 | 2551.715 | 167759/165742 | 41.431 | 1117.66 |
| C1 | 1.673 | 313.92 | 1319.73 | 23.203 | 3467/3433 | 0.777 | 1118.66 |
| C4 | 1.466 | 322.38 | 1292.86 | 22.934 | 3226/3200 | 0.799 | 1120.71 |
| C8 | 1.699 | 380.03 | 1296.49 | 22.996 | 3453/3425 | 0.917 | 1122.01 |

Active metadata work has observable CPU/allocation/SQL costs; no zero-overhead claim is made. The ten predeclared operation-latency/throughput comparisons pass, with finite backlog and complete correctness. Process counters aggregate the harness and both in-process hosts; SQL cgroup counters are separate. Full bookends, physical reads, logical I/O and cgroup block-I/O remain in the JSON. Process RSS is sampled. SQL memory.current/memory.peak are cgroup memory, including non-RSS charges; the peak is lifetime, not a SQL-process RSS or allocator peak. Final acceptance JSON:592509bytes, SHA256c1a6d3e0d1e9f0ce026a2535b8b7bb50234a69e2a86ae644818f36d8540473cf. Independent Deep review and the coherent selected post-integration gate remain required.

## Historical fixed-manifest and diagnostic results

The approved default manifest is E538-I first on a fresh fixture, then E538-M and E538-C. I uses actual W1 1936×1216 Mono16 frames (4,708,352 bytes), real ingress/upload and central EncodedPreview processing. Its idle-before/active/denied/idle-after phases each retain five warmups and thirty measured successful capture/upload/job tuples. Three-minute deadlines, bounded raw/central backlogs, zero correctness loss and the original thresholds remain enforced. Idle bracket drift above 25% produces Inconclusive, not a wider tolerance.

The uninstrumented clean 21e0dd0401b1bb42c838be42636716d790aba0e4 trial completed I/M/C with 8 Pass,2capture Inconclusive-idle-drift and0 Regression. Capture p95 idle-before1209.4418 ms versus idle-after478.6294 ms drifted152.689%; overall Investigate is not acceptance. All 120 measured tuples had exact sizes/checksums and output lineage; no failure/overflow/loss. Final convergence was160 raw captures/320 artifact acknowledgements and zero held/pending/jobs.

M drained10003 units in106954.8 ms (93.49744 units/s),157 submissions, maximum 64 units and one in-flight request.102keyset pages had p95 1.3949 ms. No pending/retry/quarantine/projection errors remained. C1/4/8 each retained 20 warmups+200 measured logical requests: p95 12.9455/65.3994/187.819 ms;135.282/160.163/163.957logical requests/s; physical requests/retries200/0,296/96,471/271; all converged. This is an initial absolute importer baseline and one bracketed trial. The new feedback correction changes a measured importer path, so these M/C results are historical rather than current-source acceptance.

Diagnostic-only cb392a61 retained 140 producers, all eight existing producer stage values,2299runtime samples and all 120 operation joins, without missing/dropped/evicted records. All five idle-before warmups were already slow. Measured rendering then transitioned from1321–1348 ms through 742 ms to420–456 ms. Rendering localization and overlapping process-wide compilation are established; a causal JIT/method/tier explanation is not. GC pause durations alone did not account for the difference. Diagnostic overhead prevents acceptance use.

Diagnostic-only f9273212 retained the same 140 selections, all 120 valid operation tuples, no correctness/overflow failure and complete final convergence. Its actual capture p95 brackets1498.0823 ms versus484.4697 ms drifted209.2211%. The finalized EventPipe trace reports907710enumerated events and zero EventsLost, matching all 140 identity markers and full source/nonce/PID/command provenance. The first decoder hit its512 MiB output cap, retaining only the early21.087 seconds. Full-span method attribution is unavailable from that partial output. Actual147890 AllocationTick events were observed; the earlier contrary methodology sentence is corrected. Sampled thread-stack occupancy is not on-CPU time. Unresolved/depth-truncated stacks and process-wide counter limits remain explicit.

Compact offline recovery of the SAME trace completed after the affected gate released capacity. One focused external reader build passed 0 warnings/errors, then exactly one 9.892second decode retained907710/907710 enumerated events with zero filtered/unexported/unvisited events and reported loss 0. Full source/mode/flag/command/nonce/PID and all140 selections matched. Reader peak sampled RSS was534134784 bytes. The unchanged512 MiB/3M-event/600second/4 GiB-sampled-RSS/2 GiB-file bounds held. Original bytes were reverified unchanged; lock released 10:42:46.622050 UTC. Attribution still returns Partial/exit2 because of 533 truncated sample stacks,11618 unresolved frames and 12 opaque payload events (raw bytes retained); full event export does not establish a causal JIT or GC finding. Visibility SHA2561EEB59F9C44D1B512CD39F01CD74340C0C0A9F770F3609D7C9209693A9200807. No recollection, outlier deletion, extra warmup, relaxed threshold or acceptance waiver is implied. The maintainer subsequently approved the separately named opt-in cold-start/steady-state split: retain30 cold-start operations, then the unchanged four5warmup+30measured phases (170 declared selections). Default and diagnostic140-selection behavior remains unchanged. At that checkpoint implementation, validation and current-source acceptance measurement were pending. The approved mode and subsequent passing run are recorded above; original Inconclusive trials and thresholds remain intact. Approval is recorded on PR#1249 in comment6190309075.


Historical uninstrumented resource baseline, derived directly from the checksummed final JSON:

| Phase | Process CPU s | Allocated MiB | Peak sampled RSS MiB | Physical write MiB | SQL commands all/evidence | Separate SQL CPU s | SQL cgroup memory end MiB |
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
- /home/roys/review-evidence/issue538/eventpipe-f9273212-compact-recovery-r1:126files,578170087bytes; manifest SHA2563d97a6a18a03c87ee2c24831033db880fbfc49b51fbc5685d6faaf0cd3fe2c9f.
- Original barrier backup SHA25629181E993B160B3944282288385AC2BA908D28B1F7185318F6420149D46981CD.
- Completed uninstrumented performance JSON SHA2562B8036105D913B724448016E8D9AD3AC3551557190BE3636E1F6CC59DCC7E902.
- Finalized raw trace SHA256F51D706FECC6997ABB50EA9E7138E89D4721E9E60CF097D37A18E1EF54151842.

Remaining: the coherent classifier-selected post-integration local gate, independent exact-range Deep review, final target synchronization under the finalization reservation, current-head protected v1 checks, merge and cleanup. No review convergence or merge is claimed here. Later gate/review/CI results are appended to the PR#1249 ledger without rewriting this immutable measurement record.

Record prepared after affected validation at 2026-10-15T10:43:41.686671+00:00. Source/test/build inputs remained pinned throughout execution; this document adds no runtime change.
