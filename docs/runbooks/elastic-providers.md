# Elastic provider adapters (#430)

Optional elastic runner capacity behind the
[`elastic-provider-v1`](../contracts/elastic-provider-v1.md) boundary. The
delivered adapter is `local-process`, which launches self-hosted runner
processes on the host on demand. Absent and disabled by default.

## Configuration

```json
{
  "ElasticProviders": {
    "Enabled": true,
    "Provider": "LocalProcess",
    "MaxInstances": 2,
    "MinWarmInstances": 0,
    "ScaleToZeroAfter": "00:05:00",
    "ExpectedColdStart": "00:00:20",
    "QueueDeadline": "00:10:00",
    "MaxInstanceMinutesPerDay": 0,
    "MaxConcurrencyPerInstance": 1,
    "Pool": null,
    "Labels": [],
    "SampleInterval": "00:00:15",
    "RetireGrace": "00:00:30",
    "RegistrationTimeout": "00:02:00",
    "LocalProcess": {
      "Executable": "/opt/hvo/processing-runner/HVO.SkyMonitor.ProcessingRunner",
      "Arguments": [],
      "WorkingDirectory": "/opt/hvo/processing-runner",
      "LogicHostUrl": "https://logichost.example.org/",
      "ClientId": "system-processing-runner",
      "ClientSecretFile": "/run/secrets/hvo-processing-runner",
      "IdleShutdown": "00:00:00",
      "AllowInsecureHttp": false,
      "RequireProcessGroupIsolation": true
    }
  }
}
```

- Work reaches elastic instances only when a recipe is placed on runners
  (`ProcessingRunners:Placement`); the autoscaler counts that backlog.
- `Pool` joins instances to an entitlement pool as reserved runners.
- `MaxInstanceMinutesPerDay` is the global cost/resource limit; instance
  minutes are accounted from `CentralElasticRunnerInstances`.
- The runner executable can be the published binary, `dotnet` with the
  runner dll as the first argument, or a launcher script; on Unix the
  instance is started through `setsid` (found on `PATH` or the usual system
  locations) so the launcher and everything it starts are terminated as one
  process group, and provisioning is refused when `setsid` is unavailable
  unless `RequireProcessGroupIsolation` is false for an executable that is
  the runner itself. The instance receives an
  allowlisted runtime environment (`PATH`, `HOME`, locale, `DOTNET_*`,
  temp and certificate paths; never connection strings, passwords, keys, or
  tokens) plus the container's `HVO_RUNNER_*` contract with the secret by
  file path only.
- `LocalProcess:IdleShutdown` is a safety net for a host that disappears:
  zero (default) leaves instances host-managed; a value is raised to outlive
  the host's scale-to-zero window (the runner's idle clock starts after
  registration), instances provisioned for the warm minimum never
  self-terminate, and excess capacity keeps the timeout, so an instance is
  retired and accounted by the host instead of exiting on its own as an
  orphan.
- Retirement drains first on every platform (a stop file the runner watches,
  plus `SIGTERM` to the process group on Unix), waits for the whole process
  group, and forces the stop after `RetireGrace`; when several instances
  retire at once every drain request goes out before any is awaited.
  `MaxInstanceMinutesPerDay` also drains existing and registering instances
  once the budget is spent.
- Backlog is counted with the claim's pool predicate (a reserved pool serves
  its pool and shared work; unpooled instances serve only unpooled
  observatories), and demand includes work already executing on the
  instances. A launched process is always recorded before it can claim: the
  durable intent precedes the launch, a failed launch closes it, and an
  instance the provider reports without a record is retired.
- Instance rows record the launching host; each LogicHost replica reconciles
  and retires only the instances it launched (including when it restarts
  with `ElasticProviders` disabled), while `MaxInstances` and the warm
  minimum count every replica's live instances. Each sample persists the
  owner heartbeat on the host's rows before reconciliation and renews it on
  its own connection while the sample runs (so a drain that blocks for the
  whole `RetireGrace` never looks like a lost owner; a failed renewal is
  retried every tick, event 2245, and a sample whose heartbeat stays
  unrenewed for half the owner-stale window is abandoned); rows whose owner has
  not reconciled them for six sample intervals (at least five minutes) are
  abandoned as `owner-lost`, counted in `orphansCleanedLastSample`, and stop
  accruing minutes; the registry then denies any re-registration of that
  runner id (`runner.registration-denied`), so a runner process that
  outlived its host drains and exits on its own instead of reviving its
  registration. Deployment-wide scaling decisions are serialized under a
  database application lock and the intents are committed before the
  processes launch, so two replicas cannot both fill the same shortfall;
  in-flight work on every replica's instances counts toward demand;
  retirements are reserved as `Stopping` under the same lock, and a warm
  instance lost while the pool is at `MaxInstances` is replaced by retiring
  one excess instance that has no work in flight first; when every excess
  instance is busy the replacement waits for one to go idle, because the warm
  designation never force-terminates active work. Busy tracking uses the
  runner's unexpired leases as well as its heartbeat-reported slots, every
  retirement is reserved under the runner's claim lock after a fresh lease
  check, every close is written under that lock, and the claim path refuses
  new work (inside the same lock) to an instance in any state but `Starting`
  or `Running`. Intents that could not be
  launched (a failed launch, host shutdown mid-batch, or a process that
  started but whose record could not be saved and is retired again) are
  closed immediately as `launch-aborted`. Abandonment closes the instance row
  and retires the registration in one transaction under a per-runner
  database lock that registration also takes, so a re-registration racing
  the abandonment is denied instead of reviving the retired registration; an
  abandoned runner's heartbeat is refused as well as its re-registration. `SampleInterval` and `RetireGrace` are validated even
  while disabled because that cleanup consumes them.
- Existing registrations cover queued work only through their currently free
  slots. Free slots are matched to queued jobs by exact recipe and transfer
  limit using deterministic maximum-cardinality matching; occupied slots are
  reserved from heartbeat and unexpired-lease state. The allocator refuses
  more than 4096 jobs, 4096 free slots, 100 million compatibility/edge visits,
  or five seconds of matching work rather than silently truncating demand.
- Remaining active-job entitlement is applied independently to each unmatched
  job's observatory. Matched identities remain covered by existing capacity;
  only provisionable unmatched identities size new capacity and their oldest
  age controls the cold-start deadline. An exhausted observatory cannot consume
  another observatory's headroom, and old matched work cannot reject a cold
  start for younger unmatched work. Scale-down retires excess instances
  before instances kept for the warm minimum, and only instances idle beyond
  `ScaleToZeroAfter` are idle-retirement candidates. The warm designation
  follows the live count of warm instances: a lost warm instance is replaced
  by a warm one even while excess capacity runs. A retirement interrupted by
  host shutdown leaves the instance tracked and its stop file in place so the
  runner still drains and the next host reconciles it; a retirement reserved
  as `Stopping` whose drain request never went out is re-adopted by the next
  host process and completed as `retirement-resumed` (event 2241) rather than
  orphaned while its process keeps running.

## Behavior

Each sample the host reconciles recorded instances with the provider and the
runner registry (orphans, registration timeouts, measured cold starts, busy
tracking), matches provider-eligible backlog to free registered slots, filters
the unmatched identities by scoped entitlement headroom, decides with the
scaling policy, and provisions or retires. A cold start is taken only when it
can still serve the oldest provisionable unmatched job within `QueueDeadline`;
otherwise the work stays local and the rejection is counted. Idle instances
above the warm minimum are retired after `ScaleToZeroAfter`; retirement asks
the runner to drain and forces it after `RetireGrace`, which the child also
receives as its own shutdown grace; the stop time recorded for instance
minutes is the time the drain actually completed. Backlog counts only
runner-placed recipes a provisioned instance could claim (the configured
executable is probed with `--capabilities`, event 2243, accepted only on a
zero exit; after a failed probe, event 2244, nothing is provisioned or
idle-retired, not even the warm minimum, the decision reads
`instance-capabilities-unknown` (event 2246, health degraded) and the probe
is retried every five minutes; recipes whose
requirements the instance cannot satisfy are excluded and logged once as
event 2242) and includes expired leases the claim would reclaim. Backlog is counted with the claim's own readiness query (recipe filter,
the probed runner's transfer limit, input and graph-execution readiness),
so no instance is provisioned for work no runner could claim. Registered free
slots are maximum-matched to jobs they can claim by exact recipe and transfer
limit, with constrained jobs, age, stable job id, and stable runner id defining
the deterministic secondary order. Occupied slots cover no queued work. An
instance that can claim nothing covers nothing, and when such instances fill `MaxInstances`
one is retired as `incompatible-replacement` so the next sample can
provision one that can. Executable jobs no registration can take within its
slots are an uncovered shortfall that provisions new instances within the
entitlement bound; terminal cleanup no instance can claim needs exactly one
instance, exempt from the deadline; when the limit is full of instances
serving other work the decision reads `instance-limit` and the work is
reported, not served by retiring a useful instance, while a fleet above a
lowered limit retires its idle excess first. Expired
leases whose attempts are exhausted are terminal cleanup the claim exempts
from pool and entitlement bounds, so they are counted apart from executable
backlog and only ensure one instance exists, while the published backlog
(health, gauge, rejection log) and its age include them. Idle scale-down and warm
replacement never retire registered capacity the demand still needs (the
replacement's configured size counts), concurrent drains stamp each
instance's own stop time, and a retirement that fails beside a successful
one still closes the successful rows before the failure is raised. Instances
recorded by a previous host process are re-adopted when their process is
still alive, reserved retirements included, which are then completed.
`LocalProcess:LogicHostUrl` must be http or https, and http only for loopback
or with `AllowInsecureHttp`; enabling `ElasticProviders` requires
`ProcessingRunners:Enabled=true`, both checked at startup.

## Signals

Meter `HVO.SkyMonitor.LogicHost.ElasticProviders`: instances by state,
backlog, instance minutes today, provisions, retirements by reason, orphans
cleaned, rejected placements by reason, successful provisions by reason,
cold-start histogram, and allocation histograms for job count, free-slot count,
edge visits, and elapsed milliseconds. Allocation metrics carry only `provider`
and bounded `phase` (`sample` or `locked`) labels; they never carry job, runner,
recipe, or observatory identities
(`docs/validation/central-elastic-runtime-signals.json`). Health check
`elastic-providers` is healthy when disabled, degraded when the daily limit
retains backlog, when startup cannot meet the deadline past the deadline,
when orphans were cleaned in the last sample, or when no sample has
completed within three intervals of startup, and unhealthy after three
consecutive sampling failures (for example an executable that cannot start).
Log events 2230-2246.

## Operations

- Zero provider calls in local-only mode are proven by the integration suite;
  enabling the feature never changes CameraAgent acquisition or live
  processing.
- Evidence: `ElasticProviderPerformanceEvidenceTests` (Manual) records cold
  and warm start, drain throughput with 1, 2, and 4 instances, scale-to-zero
  timing, and host CPU/memory to `TestResults/elastic-providers/<revision>/`.
- `HeterogeneousFleetCounterexamplesConvergeTogether` runs the constrained /
  flexible recipe, occupied-slot, scoped-entitlement, and uncovered-deadline
  scenarios in one production-path sequence. The representative allocator test
  records deterministic cardinalities, edge visits, elapsed time, process CPU,
  and thread allocations for a 64-job, 16-registration heterogeneous fleet.
- Diagnose retained work with the backlog gauge and snapshot `lastDecision`.
  `instance-limit` means useful active instances fill the limit;
  `incompatible-replacement` is a retirement reason that makes room for a
  compatible template; `entitlement-bound` means unmatched work exists but only
  the per-observatory provisionable subset can launch; cleanup-driven launches
  remain `backlog` because cleanup is entitlement-exempt.
- Provider exit: disable the section; instances scale to zero and no job state
  lives in the provider.
