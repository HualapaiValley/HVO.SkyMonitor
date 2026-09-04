# Processing Entitlements and Fair Scheduling

LogicHost can enforce per-observatory allocations and weighted fair scheduling
for its central derivative jobs (#429). The policy is host configuration under
`ProcessingEntitlements`, disabled by default; a standalone installation needs
no plan, license server, or metering service.

## Configuration

```json
{
  "ProcessingEntitlements": {
    "Enabled": true,
    "DefaultActiveJobs": 2,
    "DefaultActiveJobsPerCamera": 0,
    "DefaultWeight": 1.0,
    "DefaultPriority": 0,
    "StarvationAge": "00:10:00",
    "AdmissionPendingLimit": 500,
    "BacklogDegradedAfter": "00:10:00",
    "Observatories": {
      "6f1c5c2e-...": { "ActiveJobs": 50, "Weight": 4, "Priority": -1, "Pool": "blue",
                         "ResourceClassActiveJobs": { "encoding": 8 } }
    },
    "ResourceClasses": {
      "encoding": { "ActiveJobs": 16, "ActiveInputBytes": 2147483648 },
      "structured-analysis": { "ActiveJobs": 8 }
    },
    "RecipeResourceClasses": { "rolling-mean": "image" }
  }
}
```

- `ActiveJobs` limits unexpired leases per observatory (0 = unlimited);
  `ActiveJobsPerCamera` limits per device. Window-waiting jobs are outside
  these quotas; their pinned bytes stay in the window metrics.
- `Weight` and `Priority` order claimable work: starving jobs (older than
  `StarvationAge`) first, then priority (lower first), then weighted fair
  share `(active + 1) / weight`, then availability order.
- Resource classes follow the layered-product contract: `structured-analysis`
  (quality, cloud assessment, analyzers, transient runtime), `presentation`
  (annotation, projected scene, weather overlay), `composition` (rolling mean,
  normalization, calibration), `encoding` (previews, JPEG), and `image` for
  anything unmapped. Global class budgets bound active jobs and summed active
  input bytes; per-observatory class limits bound one observatory inside a
  class. Class budgets never block work of another class.
- `Pool` binds an observatory to runners labelled `pool:<name>` (dedicated) or
  `pool:<name>` plus `pool-mode:reserved` (its pool first, then shared work).
  The in-process worker and unpooled runners serve only unpooled observatories.

## Enforcement

The claim query computes the active-lease counts once, filters and orders
under the policy without taking row locks (rows another claimer is updating
are skipped), and returns a short ranked batch of candidates. The claimer
locks the best candidate that is still claimable, takes a transaction-scoped
application lock per observatory (and per resource class when that class has
a budget), re-checks the counts, and only then leases, so concurrent claims
from any number of workers or runners cannot exceed an entitlement (log event
2220 and metric `skymonitor.central.fairness.throttled` by reason
`observatory`, `camera`, `class`, `class-bytes`, or `observatory-class`). An
application-lock wait that exceeds ten seconds is retried like any other
claim collision. A claim that needs more than eight iterations logs warning
event 2221 with the iteration count, elapsed time, and retry reasons
(`batch-exhausted`, `lease-update`, `expired-attempt`, `adopted-outcome`,
`terminal-update`, lock timeouts, throttled observatories); sustained 2221
warnings mean the database host is oversubscribed or claimers far outnumber
claimable work. Lease expiry, failure, completion, and cancellation release
capacity through the existing lease engine.

## Usage records

Every terminal attempt writes one `CentralProcessingUsageRecords` row
(observatory, camera, job, attempt, recipe, resource class, worker, outcome,
lease start, end, input/output bytes, recipe duration) in the same transaction
as the attempt's terminal update. Aggregate by observatory or camera for
billing-ready reporting; no payment provider is involved.

## Signals and backpressure

Meter `HVO.SkyMonitor.LogicHost.ProcessingFairness` exposes per-observatory
queue depth by status, oldest pending age, active leases, entitlement,
saturation, throttled claims, completions, and usage bytes
(`docs/validation/central-fairness-runtime-signals.json`). Raw ingest is never
refused and derivative scheduling continues under overload; the
`processing-entitlements` health check degrades when an observatory's pending
work exceeds `AdmissionPendingLimit` or when a saturated observatory carries
backlog older than `BacklogDegradedAfter`. That is the admission behavior:
bounded by entitlements, visible, never lossy.

## Capacity guidance

Use `ProcessingFairnessPerformanceEvidenceTests` (Manual) to measure the
installed mix: it drains deterministic 5-, 10-, and 100-camera arrival streams
and reports Jain's fairness index, claim and queue latency, throughput, CPU,
memory, and recovery after lease loss. Size worker and runner concurrency from
the sum of entitlements you intend to honor concurrently and the measured
recipe durations of the placed mix, not from the camera count alone.
