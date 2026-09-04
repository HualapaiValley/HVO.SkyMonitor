# Processing Runner v1

`processing-runner-v1` is the provider-neutral protocol between LogicHost and
self-hosted processing runners (`src/HVO.SkyMonitor.ProcessingRunner`). It moves
optional central recipe execution out of the LogicHost process onto separately
deployable, capability-labelled runner services on the same LAN or machine.
LogicHost remains the only authority for durable job state; a runner executes
the shared recipe kernel (`HVO.SkyMonitor.Processing`) and nothing else. The
contracts live in `src/HVO.SkyMonitor.ProcessingRunner.Contracts`.

## Boundary

- LogicHost owns leases, attempts, retries, cancellation, deadlines, output
  validation, object storage writes, artifact rows, lineage, and downstream
  scheduling. The runner protocol reuses the existing derivative lease engine;
  a runner lease is an ordinary `CentralDerivativeJob` lease whose owner is the
  runner id.
- The pre-execution checks (frozen graph plan integrity, requested recipe
  identity, canonical input integrity, pending output recovery, annotation
  freezing) and the post-execution validation/publication are one shared code
  path (`ICentralDerivativeExecutionPipeline`) used by both the in-process slot
  and the runner endpoints, so in-process and runner outputs are equivalent by
  construction and stale or duplicate completions are rejected by the same lease
  filters.
- Job classes are explicit: `central-recipe` is the only class claimable
  through LogicHost, `cameraagent-archived-replay` is reserved for the
  CameraAgent adoption of this protocol, and `cameraagent-live` can never be
  claimed (the endpoint rejects it with `runner.job-class-not-claimable`).
- Placement is host configuration (`ProcessingRunners` options): a recipe is
  either `InProcess` (default) or `Runner`. The in-process worker never claims
  a runner-placed recipe and a runner never claims an in-process recipe, so a
  missing, cold, or saturated runner creates backlog rather than a silent
  fallback. Transient runtime recipes are never runner-capable.

## Authentication and authorization

- Runners authenticate with a confidential OAuth client (client credentials)
  carrying `api.runner` and `api.artifacts.read`. The `ProcessingRunner` policy
  requires a system account with `api.runner`.
- A runner id (`^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$`) is bound at registration
  to the credential subject that registered it; other subjects receive
  `runner.registration-not-owned`.
- Inputs are read only through the existing job-scoped artifact retrieval
  (`GET /api/v1.0/devices/{device}/artifacts/{artifact}/content`) with
  `X-HVO-Job-Id`, `X-HVO-Lease-Token`, and `X-HVO-Runner-Id`; LogicHost checks
  that the job is leased to that runner id with that token, that the runner id
  is owned by the caller, and that the artifact is one of the job's inputs.
- Products are uploaded to LogicHost, which verifies and writes object storage
  itself. Runners never hold SQL, object-store, identity, or filesystem
  credentials.

## Endpoints

All bodies use the protocol serializer: camel case, string enums, unmapped
members rejected, 4 MiB metadata limit. Every failure body is
`{ reasonCode, message, retryAfterMilliseconds? }`.

| Call | Purpose | Notable responses |
| --- | --- | --- |
| `PUT api/v1.0/processing-runners/{runnerId}` | Register or re-register capabilities. Returns heartbeat/lease/renewal/backoff intervals and the recipes LogicHost resolved as eligible. | 400 invalid id/capabilities, 403 not owned, 503 runners disabled |
| `GET api/v1.0/processing-runners/{runnerId}` | Non-mutating status (effective status by heartbeat age, warm state, eligible recipes, active leases). Used by liveness probes; never refreshes the heartbeat. | 404 not registered |
| `POST .../heartbeat` | Warm state, free slots, active job ids. Returns job ids whose cancellation was requested, job ids whose lease is no longer held, and the eligible recipes re-resolved from current placement. | 404 not registered, 410 retired |
| `POST .../claims` | Claim the next eligible `central-recipe` job. Returns the claim or 204. Refused (410 `runner.registration-stale`) when the heartbeat is older than `StaleAfter`; returns 204 when the runner already holds `maxConcurrency` active leases; jobs whose inputs exceed the runner transfer limit are excluded before leasing. | 403 job class not claimable, 410 retired or stale |
| `POST .../jobs/{jobId}/lease` | Renew the lease. | 409 stale, 410 canceled |
| `POST .../jobs/{jobId}/completion` | Multipart: `outcome` JSON part plus `payload-{n}` binary parts. Publishes through the shared pipeline. | 400 invalid, 409 stale, 413 too large |
| `POST .../jobs/{jobId}/failure` | Retryable or terminal failure; `object.*` reasons with an artifact id mark that input unavailable. | 409 stale |
| `DELETE .../{runnerId}` | Retire the registration. | |

## Capabilities and matching

A runner advertises protocol version, OS, architectures, runtime identifier,
framework, processor count, memory, resource class, GPU availability, latency
class, free-form labels, the exact built-in recipe versions it executes,
concurrency, transfer limit, warm-up stages (runtime/JIT, native libraries,
catalog, calibration, models, GPU) and warm state (`Cold`, `Warming`, `Warm`,
`Degraded`). LogicHost resolves the eligible recipes as the runner-placed
recipes whose advertised semantic and implementation versions equal the host's
built-in definitions and whose configured requirement (resource class, latency
class, GPU, process architecture, labels) the runner satisfies. A version
mismatch is never matched.

## Capacity and staleness

LogicHost enforces the advertised concurrency: claims for one runner id are
serialized under a per-runner lock and refused once the runner holds
`maxConcurrency` unexpired leases, so two processes sharing an id or a client
issuing overlapping claims cannot hold unbounded work. Heartbeat loss past
`StaleAfter` is enforced on the claim path itself (not only when health is
polled); the runner recovers by heartbeating. Eligibility is re-resolved on
every heartbeat, so a placement change reaches a registered runner without
re-registration, while leases already granted stay valid to completion.

## Claim

A claim carries the lease credential (`jobId`, `leaseToken`,
`leaseExpiresUtc`, `renewalInterval`, attempt counters), the complete execution
request (recipe, canonical options, input selector, payload-less input
metadata with content paths, lengths, and SHA-256, output variant, frozen
annotation input, canonical auxiliary inputs, primary artifact id), the
requested/expected recipe identities, and correlation (graph execution and
node, trace context). Payload bytes never travel in the claim. The runner
fetches every input under the lease, verifies length and SHA-256, rebuilds the
identical `ProcessingExecutionRequest`, executes it, and uploads products with
their payload SHA-256, content checksum, output identity, recipe identity,
algorithms, ordered source artifact ids, layout, kind, and schema version.

LogicHost rebuilds the recipe identity from its own built-in definition and the
declared options (versions, canonical options, options hash, identity,
operation kind) and re-derives the output identity from role, variant, recipe
identity, and ordered sources before anything durable is keyed by them;
mismatches and non-built-in recipes are rejected
(`runner.recipe-identity-mismatch`, `runner.output-identity-mismatch`). Every
product is also bound to the execution identity LogicHost derives from the
frozen lease (recipe, normalized options, selector, frozen annotation, and
auxiliary inputs), so a different but internally consistent recipe can never be
published under the leased job.
Completion is authorized by the lease alone: eligibility may shrink through a
placement change while a lease granted under the previous placement is still
executing, and that work still publishes. An annotation job whose frozen
provenance yields no annotation is skipped authoritatively by LogicHost on both
paths once its inputs were read or fetched.

## Recovery

- Execution is at-least-once. A crashed or disconnected runner lets the lease
  expire; the job is reclaimed by another runner (attempt + 1) and the expired
  lease can no longer complete or fail it. Publication is idempotent through the
  existing output writer, so a duplicate attempt adopts the pending output.
- Heartbeat loss past `ProcessingRunners:StaleAfter` marks the runner `Stale`
  (no claims); the next heartbeat recovers it. Retired runners must register
  again.
- Cancellation reaches a runner through heartbeat (`cancelRequestedJobIds`) and
  refused renewal (410); the runner stops execution and never completes.
- Runner-placed backlog is visible on the `processing-runners` health check
  (degraded when recipes are placed but no runner is active, or when the oldest
  placed job exceeds `BacklogDegradedAfter`).

## Limits

Runner id 128 characters, display name 256, 32 labels of 64 characters,
64 recipes, 256 inputs, 32 auxiliary inputs, 64 products, concurrency 1-32,
transfers up to 100 MiB per claim or completion (matching the LogicHost object
PUT limit), lease 1 s to 1 h, 64 reported active jobs per heartbeat.

## Relationship to the CameraAgent local replay runner

The envelope shapes (artifact and product metadata, warm-up stages, recipe
capability) mirror `local-replay-runner-v1` so evidence is comparable. The
local replay runner keeps its owner-only Unix-socket transport for archived
replay; CameraAgent adoption of this networked claim protocol for archived
replay is an RM-017 contract and is not part of LogicHost.
