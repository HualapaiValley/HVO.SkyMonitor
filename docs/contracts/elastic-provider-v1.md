# elastic-provider-v1

Provider-neutral boundaries for elastic processing capacity (#430, `RM-005`).
The host stays the scheduler and the authority for job state; a provider
adapter only starts, lists, and retires runner instances. Instances join the
host through [`processing-runner-v1`](processing-runner-v1.md) like any
self-hosted runner, so graphs, jobs, leases, identity, and lineage are
unchanged whether work runs in process, on a self-hosted runner, or on an
elastically provisioned one.

## Provider decision

The delivered adapter is the bounded local proof adapter `local-process`:
self-hosted `HVO.SkyMonitor.ProcessingRunner` processes launched on the host
on demand. It exercises every boundary obligation (scale-to-zero, maximum
instances, warm minimum, capability labels, entitlement and daily-limit
propagation, cancellation, orphan cleanup, cold-start placement, job-scoped
artifact access, provenance, accounting) without a cloud SDK. Azure, AWS, or
another provider is a later `IElasticRunnerProvider` implementation behind
the same boundary, chosen only after a measured workload fit against the
local evidence in `TestResults/elastic-providers/`. Container-job services are
evaluated before function runtimes for full-resolution, multi-input,
long-running, native, or GPU recipes.

## Boundary

- `IElasticRunnerProvider`: `Name`, `Capabilities` (provider, process
  architecture, runtime image, scale-to-zero support, labels),
  `EstimateStartup()`, `DescribeInstance(maxConcurrency, labels)` (the
  runner capabilities an instance provisioned with those settings registers,
  so the host counts only backlog such an instance could claim),
  `ProvisionAsync(request)`, `RetireAsync(instance, grace)`, `ListAsync()`. Provisioning is idempotent per instance id (a retried
  request returns the instance already launched); retire asks the instance to
  drain on every platform (the runner watches a stop file the host creates,
  and Unix adds `SIGTERM` to the instance's process group) and forces it
  after the grace period.
- `ElasticRunnerProvisionRequest`: instance id, runner id, eligible job
  classes, labels, per-instance concurrency, optional pool. Requests naming
  `CameraAgentLive` are refused by `ElasticWorkloadClass` before any provider
  call; eligible classes are `CentralRecipe` and `CameraAgentArchivedReplay`
  (the archived-replay transport remains the `RM-017` contract).
- `IElasticArtifactAccessAdapter`: describes how instances read inputs and
  write outputs. The local implementation is lease-scoped HTTP against the
  LogicHost artifact endpoints with the runner, job, and lease headers; it
  never distributes central storage credentials. A provider adapter may
  describe job-scoped signed access instead, never a shared credential.
- Provenance: every instance advertises `provider:<name>` and
  `elastic-instance:<id>` labels in its registration; the host records
  provider, architecture, runtime image, lifecycle timestamps, measured cold
  start, and stop reason in `CentralElasticRunnerInstances`. Usage records
  carry the instance's runner id, so cost and lineage attribution need no
  further schema.

## Placement, limits, and cleanup

The autoscaler samples provider-eligible runner-placed backlog (recipes
filtered through the capabilities a provisioned instance registers; pending,
retryable, and expired-lease work the claim would reclaim) and decides
with a pure policy (`ElasticScalingPolicy`): desired instances follow
backlog plus in-flight work, sized against the concurrency the running
instances actually registered and the configured per-instance value for new
ones, bounded by the
remaining entitlement headroom of the backlogged observatories (#429),
`MaxInstances` counted across every LogicHost replica,
`MinWarmInstances`, and `MaxInstanceMinutesPerDay`. A cold start is taken
only when `startup estimate + oldest backlog age <= QueueDeadline`; otherwise
the work is retained locally as backlog (rejected placements are counted and
logged). Idle instances above the warm minimum are retired after
`ScaleToZeroAfter`. Instances whose process is gone are marked orphaned and
their registration retired; instances whose registration went stale or never
arrived within `RegistrationTimeout` are retired. Instances recorded by a
previous host process are re-adopted when still alive, and a retirement
reserved by a previous host process is completed rather than orphaned.

## Local-first guarantees

`ElasticProviders` is absent and disabled by default. A disabled host never
resolves or calls a provider adapter (`LocalOnlyModeNeverCallsAProvider`),
CameraAgent acquisition and live processing are untouched by any provider
outcome, and provider outage, cold capacity, or quota exhaustion only leaves
bounded, visible backlog (`elastic-providers` health check).

## Data residency, encryption, credentials, outage, and exit

The local adapter keeps data on the host and inherits the host's transport
security (loopback or the configured HTTPS LogicHost URL); the runner client
secret is read from a file path only. A provider adapter must document
residency and encryption for the instance environment, rotate the runner
client secret through the same file contract, degrade to local backlog on
outage, and exit by scaling to zero: no job state lives in the provider.
