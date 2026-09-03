# Central Transient Acceptance Evidence

Issue #116 closes the Central transient validation path and its final Hybrid
submission boundary. The runtime remains opt-in through `CentralTransient:Mode`;
the default is `Off`. This evidence does not claim a CameraAgent HTTP drain
worker, physical sensitivity, or a UI.

## Acceptance Boundaries

The executable fault inventory is
[`central-transient-fault-matrix.json`](central-transient-fault-matrix.json).
Each row names its deterministic control, durable invariant, exact MSTest filter,
and Docker requirement. The Docker-free unit gate validates that every required
row remains present and executable evidence is named:

```bash
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
dotnet test tests/HVO.SkyMonitor.LogicHost.Tests/HVO.SkyMonitor.LogicHost.Tests.csproj \
  --no-build --configuration Release \
  --filter "FullyQualifiedName~CentralTransientAcceptanceManifestTests"
```

The matrix covers authenticated canonical Hybrid handoff, exact and concurrent
retry convergence, conflicting identity quarantine, unavailable/corrupt/timeout
source evidence, CameraAgent acknowledgement hold release and reopen, CameraAgent
isolation from a failed Central artifact transport,
delayed and out-of-order `N-2..N+2` arrival, deadlines,
incompatibility, missing and corrupt evidence, duplicate scheduling, lease and
max-attempt adoption, the SQL-commit/generic-completion crash boundary, atomic
rollback, MinIO failure modes, no-candidate and limitation outcomes,
post-commit invalidation, immutable version history, and retrospective
version/starvation behavior.

Run the Docker-backed implementation rows with:

```bash
dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --no-build --configuration Release \
  --filter "FullyQualifiedName~CentralDerivativeWindowIntegrationTests|FullyQualifiedName~CentralTransientEventPersistenceIntegrationTests|FullyQualifiedName~DerivativeJobIntegrationTests|FullyQualifiedName~ArtifactRetrievalTests"

dotnet test tests/HVO.SkyMonitor.CameraAgent.IntegrationTests/HVO.SkyMonitor.CameraAgent.IntegrationTests.csproj \
  --no-build --configuration Release \
  --filter "FullyQualifiedName~VirtualSkyPipelineTests.CentralTransportOutageDoesNotBlockAcquisitionOrLoseLocalTransientProvenance"

dotnet test tests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests.csproj \
  --no-build --configuration Release \
  --filter "FullyQualifiedName~HybridTransientSubmissionTests"
```

## Hybrid Boundary

`POST /api/device/transient-candidates` requires the existing `ArtifactIngest`
system bearer policy plus `X-HVO-Device-Id` and `X-HVO-Device-Key`. The request
body is the unmodified canonical `TransientCandidateSubmissionEnvelopeV1`, and
`Idempotency-Key` must equal its submission identity. The candidate Agent ID
must exactly equal the authenticated registration ID. LogicHost verifies the
causal `N-2,N-1,N` evidence under that authenticated device/Agent identity,
resolves and verifies `N+1,N+2` from Central state, freezes the resulting exact
five-input window, and commits acceptance atomically. If an available, verified
future frame has crossed to a different rig or processing profile, LogicHost
instead persists and returns a terminal `Retired` settlement; CameraAgent stores
that acknowledgement atomically and releases the impossible window's source
holds. Missing future uploads remain retryable. Validation retains the
full canonical centered extraction receipt for audit, but only the unique
centered match for the submitted candidate is promoted, assessed, and persisted;
all unrelated candidate identity slots become unused.

Hybrid mode exposes the shared CameraAgent edge extraction recipe but does not
schedule transient jobs during ordinary artifact ingest. Exact retries return a
durable duplicate acknowledgement with the original receipt timestamp.
Candidate/Event identity conflicts and rejected source evidence are retained in
an additive hash-only audit table; credentials, payload bytes, and storage paths
are excluded. CameraAgent remains responsible for local durable handoff under
issue #63; this change intentionally adds no hosted HTTP drain worker.

## Performance Method

Performance evidence is split into two isolated x64 Release processes. This
prevents SQL Server, MinIO, and Testcontainers activity from contaminating the
detector's process-wide CPU, allocation, LOH, and RSS counters.

The host-neutral detector component measures W2-shaped synthetic source conversion,
centered temporal background construction, extraction, promotion, and
deterministic assessment. It performs five warmups and 30 measurements in each
of five independent trials. It is not a VirtualSky render, production capture,
or physical sensitivity claim:

```bash
dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj \
  --configuration Release --arch x64 \
  --filter "FullyQualifiedName~TransientCandidateExtractionPerformanceTests.Issue116W2CandidateExtractionRecordsAcceptanceEvidence"
```

The Central component measures these production boundaries:

| Workload | Exact method | Recorded evidence |
| --- | --- | --- |
| W2 | ASI178-shaped 3096x2080 RGGB16 synthetic residual, 12,879,360 bytes; 5 warmups plus 30 positive jobs | Production Central claim/executor, five verified MinIO inputs, exact object-reader operation/payload bytes, positive candidate/event/assessment persistence, generic completion, median/p95, CPU, allocation, LOH/RSS, candidate rate, checksums, ordered lineage, and post-commit adoption preserving one event version |
| W2 I/O control | Rolling-mean over the same dimensions and bytes | Separately labeled request/byte/output-checksum control only; it is not transient evidence |
| W3M | 10,000 metadata-only retrospective transient candidates | Production bounded batch schedules/resolves 100 jobs, 3,200 identity slots, 600 requirements, 500 artifact inputs, 100 canonical inputs, and 100 claims; SQL statements/rows/logical reads/query and plan hashes are recorded |
| W4 | Concurrency 1/4/8 | Barrier-released first-create contention at each level, then 20 warmups plus 200 steady duplicate schedules; records latency, operations/s, deadlocks/collisions, and convergence to one job/five inputs/32 slots |
| Recovery | Positive W2 post-commit replay plus five generic lease/retention trials | Exact transient event-version adoption and generic median/min/max recovery/backlog/pin evidence are reported separately |

The transient executor currently leaves `CentralDerivativeJobAttempt.InputBytes` at
zero. The evidence reports that value unchanged as a residual accounting gap and
separately records exact selected-input bytes plus successful checksum-verification
and payload-read counts/bytes at the production `ICentralArtifactObjectReader`
boundary. It does not infer or backfill the attempt counter.

Run the Central component only when this worktree has exclusive Docker capacity:

```bash
dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --configuration Release --arch x64 \
  --filter "FullyQualifiedName~CentralDerivativeWindowPerformanceTests.Issue116CentralTransientW2W3MW4_RecordsAcceptanceEvidence"
```

Generated evidence is ignored and revision-scoped:

| Artifact | Contents |
| --- | --- |
| `TestResults/issue-116/<revision>/transient-detector-w2.json` | W2 candidate rate, phase latency, CPU, allocation/LOH/RSS, live buffers, throughput, identities, and source checksums |
| `TestResults/issue-116/<revision>/central-w2-w3m-w4.json` | Central SQL and MinIO object-reader operation/byte accounting, W2/W3M/W4 measurements, queue convergence, recovery, checksums, and lineage |

Both files record candidate revision, branch, and dirty disposition. A dirty run
uses `local-dirty`; release evidence must be regenerated from the clean candidate
head. The path is covered by the repository-wide `TestResults/` ignore rule.

## Runtime Signals

[`central-transient-runtime-signals.json`](central-transient-runtime-signals.json)
is the machine-readable runtime signal manifest. It pins event IDs and fields,
metric names/units/bounded labels, span boundaries, health transitions,
collection commands, privacy exclusions, and retained evidence paths.

The production worker correlation chain asserted by integration coverage is:

`central-derivative.window.resolve` -> `central-derivative.execute` ->
`verify`/`load` -> `detect`/`converge`/`persist` -> generic completion.

The window-resolution span precedes the worker consumer span rather than being
its child. The transient executor bypasses the generic recover/internal-execute
path; `verify`, `load`, `detect`, `converge`, and `persist` are asserted as
connected descendants of the consumer execution span. The collector also asserts actual event IDs, metric names and bounded tag
sets, healthy-to-degraded health behavior, and absence of checksums, storage
references, device IDs, credentials, and opaque event IDs from signal values.
Hybrid submission records bounded `transient-submit` operation outcomes:
`accepted`, `duplicate`, `retired`, and `rejected`.

## CameraAgent Isolation Scope

`CentralTransportOutageDoesNotBlockAcquisitionOrLoseLocalTransientProvenance`
uses the real two-host fixture and production CameraAgent capture, raw ingress,
SQLite/filesystem storage, local processing, and outbox drain. Only the outbound
artifact HTTP handler is replaced with a deterministic transport failure. The
test proves acquisition and local outputs continue while durable uploads remain
pending/retry and raw manifests retain transient-scenario provenance.

Central-only mode does not run a local provisional event classifier. This test
therefore does not claim offline provisional classification, only preservation
of the local evidence required for later Central processing.

Metrics use bounded recipe/status/outcome/classification dimensions. Job IDs,
candidate IDs, event IDs, object paths, payloads, lease tokens, credentials, and
user identity are not metric labels. Opaque `JobId` is retained in logs and
traces for SQL correlation.

The `central-derivative-worker` health check must transition to degraded for a
recent dependency or renewal failure, overdue window, or old backlog, and to
unhealthy for stale heartbeat or an inconsistent runnable window. Normal
waiting work alone is not a failure.

## Validation Gates

After both performance components are generated from a clean candidate, run the
standard Debug/Release warning-as-error builds, format verification, package
audit, Docker-free Unit selection, Docker-backed Integration selection, and the
pending-model/migration gates from [`ci-pipeline.md`](../runbooks/ci-pipeline.md).
Issue #457 retires upgrade and rollback coverage for the unreleased transient EF
migration chain. Current validation retains the canonical transient schema,
constraints, indexes, triggers, and outcome/recipe identities from a clean
database; it does not construct or roll back through obsolete EF layouts.
