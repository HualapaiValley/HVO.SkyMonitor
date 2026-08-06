# Issue 253 Candidate Evidence

## Boundary

Issue #253 restores current-head central derivative fault and recovery evidence.
Production staging reconciliation already advances one durable partition per
cycle across 47 legacy and derivative partitions. The stale harness invoked one
arbitrary cycle and expected a captured derivative staging object to disappear.
The corrected fault fixture records the real staging key, places the durable
checkpoint at its owning partition, invokes one production reconciliation
cycle, and asserts the object and SQL recovery state before and after restart.

Truthful queue sources now include timing, control, rig/profile, layout, recipe,
provenance, checksum, and an uploaded immutable MinIO object. The expected four
canonical jobs include current rolling-window scheduling.

Eight simultaneous distinct completion transactions deterministically exposed
a SQL Server 1205 under `Serializable`. Completion now uses `ReadCommitted`.
Per-object application locks, conditional job lease and attempt updates,
row-version checks, and unique constraints continue to enforce completion
invariants. No blanket retry or weakened state assertion was added.

## Recovery Correctness

- Both `staging/<hex>` and `staging/derivatives/<hex>` cleanup cases
  retain objects inside the grace period, target the owning durable partition,
  and remove only expired objects.
- The canonical fault campaign passed intent-commit, staging-write,
  canonical-publication, completion-pre-commit, and completion-post-commit
  boundaries. Every trial reached zero durable backlog and passed output,
  checksum, processing identity, attempt, and immediate-lineage validation.
- The staging-write and canonical-publication trials each observed the captured
  staging object before restart and removed it in one targeted production
  reconciliation cycle.
- Eight barrier-synchronized distinct completions converged without SQL
  deadlock, duplicate output, stale attempt, or incomplete job state.
- P5's initial-drain monitor now performs a jobs-only existence query. The old
  polling snapshot unnecessarily joined source artifacts for byte totals,
  creating an `Artifacts -> Jobs` read order opposite the completion writer's
  `Jobs -> Artifacts` order. Final backlog and correctness checks retain the
  complete joined snapshot.

## Performance Evidence

Candidate source: `origin/main@fb6262d05ca2bae79276151459e203a8a062d031`
plus the issue #253 diff. The canonical evidence was recorded from the dirty
candidate because the reviewed summary is committed with the implementation.

- Environment: .NET 10.0.0, Ubuntu 24.04.3 LTS, x64, 8 logical processors,
  server GC, 16,768,606,208 bytes available memory, SQL Server and MinIO through
  Testcontainers, Release configuration.
- Command: `DOTNET_gcServer=1 HVO_EVIDENCE_REVISION=issue-253-candidate-final-2 dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --no-build --configuration Release --filter "FullyQualifiedName~LogicHostDerivativeWorkerPerformanceTests.CentralDerivativeWorker_CanonicalWorkloads_RecordPerformanceEvidence"`.
- Method: five P4 and five P5 trials per concurrency; 10 ms process RSS
  sampling; process CPU and managed allocations; durable SQL backlog and
  attempt state; SQL command and MinIO request counters; output streaming and
  SHA-256 validation. Forced pre-measurement GC now compacts the large-object
  heap so W1/W2 allocations from prior phases do not contaminate later RSS
  plateau trials.
- P1 W2 median/p95 latency was 8,367/12,783 ms for encoded preview,
  7,073/11,827 ms for annotation, and 1,833/2,825 ms for image quality.
  Throughput was 3.07, 2.92, and 12.68 operations/s respectively.
- P2 W3M drained 10,000 jobs at 33.78 operations/s for C1 and 194.11
  operations/s for C8. Final backlog was zero. Measured queue behavior made
  112,651 and 114,453 SQL commands and zero MinIO requests; immutable source
  publication occurred before the measured claim/skip boundary.
- P3 W4/W1 completed all 220 jobs at each concurrency. Throughput was 9.28,
  32.21, and 46.90 operations/s at C1/C4/C8; no completion deadlock occurred.
- P4 recovery median/minimum/maximum was 8,428/8,422/9,516 ms. Median throughput
  was 11.86 jobs/s and every final backlog was zero.
- P5 processed 39 W2 jobs per trial. C1 recovery median/minimum/maximum was
  60,135/60,110/60,212 ms with median drain rate 0.6485 jobs/s. C4 was
  60,114/60,110/60,116 ms and 0.6488 jobs/s. C1 RSS growth was 0, 0,
  25,698,304, 1,736,704, and 1,642,496 bytes against a 26,807,296-byte limit.
  C4 growth ranged from 1,318,912 to 3,801,088 bytes against 65,445,376 bytes.
- Filesystem and SQLite I/O are not applicable to this SQL Server/MinIO path.
  Authoritative SQL wire bytes and container CPU/RSS are unavailable from the
  fixture and are not estimated. The in-process worker/testhost resource scope
  is explicit in the generated evidence.

The first corrected full run reached P5 but exceeded the C1 RSS envelope by
3,019,776 bytes. Five fresh-process canonical-duration C1 trials all passed the
unchanged envelope and showed falling GC committed bytes. Two prior combined
smokes repeatedly failed before LOH compaction; all ten combined smoke trials
passed afterward. This classified the excursion as prior-phase harness
contamination rather than a product regression. A later replacement run exposed
a SQL 1205 only in the observational joined polling query; the jobs-only monitor
removed that lock-order conflict. The final unchanged candidate campaign then
passed in 28 minutes 52 seconds.

## Validation

- Debug and Release solution builds with warnings as errors: passed.
- Formatting verification: passed.
- Package audit: passed with no vulnerabilities and 12 exact deprecated package
  occurrences reviewed.
- Category audit: passed with Unit 1,725, Integration 526, Manual 74, Soak 1,
  External 0, and Hardware 1.
- Focused reconciliation and concurrent completion integration tests: 3/3
  passed.
- Complete Unit gate with an invalid Docker endpoint: 1,725/1,725 passed.
- Complete Integration execution passed 353/353 central, 145/145 CameraAgent
  storage, 18/18 CameraAgent host, and 6/6 architecture cases. One of four
  standalone acceptance cases encountered the known owner-surface 403 from
  #290; its exact replacement passed 1/1 and the complete standalone project
  replacement passed 4/4.
- Architecture/publish validation: 6/6 passed and retained both host outputs.
- CameraAgent and LogicHost pending-model checks: no changes.
- Five fresh-process P5 C1 diagnostics: 5/5 passed under the unchanged RSS
  envelope.
- Corrected canonical W1/W2/W3M/W4/fault campaign: passed.
- Independent review found no remaining actionable findings after queue-object,
  category-count, and server-GC evidence corrections.
