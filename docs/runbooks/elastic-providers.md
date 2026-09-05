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
  minimum count every replica's live instances. Each sample heartbeats the
  host's rows; rows whose owner has not reconciled them for six sample
  intervals (at least five minutes) are abandoned as `owner-lost` and stop
  accruing minutes; the registry then denies any re-registration of that
  runner id (`runner.registration-denied`), so a runner process that
  outlived its host drains and exits on its own instead of reviving its
  registration. Deployment-wide scaling decisions are serialized under a
  database application lock and the intents are committed before the
  processes launch, so two replicas cannot both fill the same shortfall;
  in-flight work on every replica's instances counts toward demand. `SampleInterval` and `RetireGrace` are validated even
  while disabled because that cleanup consumes them.
- The entitlement bound is each backlogged observatory's remaining headroom
  (its limit minus its unexpired leases from any worker) plus the work
  already executing on the instances. Scale-down retires excess instances
  before instances kept for the warm minimum, and only instances idle beyond
  `ScaleToZeroAfter` are idle-retirement candidates. The warm designation
  follows the live count of warm instances: a lost warm instance is replaced
  by a warm one even while excess capacity runs. A retirement interrupted by
  host shutdown leaves the instance tracked and its stop file in place so the
  runner still drains and the next host reconciles it.

## Behavior

Each sample the host reconciles recorded instances with the provider and the
runner registry (orphans, registration timeouts, measured cold starts, busy
tracking), measures provider-eligible backlog and the entitlement bound,
decides with the scaling policy, and provisions or retires. A cold start is
taken only when it can still serve the oldest backlog within `QueueDeadline`;
otherwise the work stays local and the rejection is counted. Idle instances
above the warm minimum are retired after `ScaleToZeroAfter`; retirement asks
the runner to drain and forces it after `RetireGrace`. Instances recorded by a
previous host process are re-adopted when their process is still alive.

## Signals

Meter `HVO.SkyMonitor.LogicHost.ElasticProviders`: instances by state,
backlog, instance minutes today, provisions, retirements by reason, orphans
cleaned, rejected placements by reason, cold-start histogram
(`docs/validation/central-elastic-runtime-signals.json`). Health check
`elastic-providers` is healthy when disabled, degraded when the daily limit
retains backlog, when startup cannot meet the deadline past the deadline,
when orphans were cleaned in the last sample, or when no sample has
completed within three intervals of startup, and unhealthy after three
consecutive sampling failures (for example an executable that cannot start).
Log events 2230-2237.

## Operations

- Zero provider calls in local-only mode are proven by the integration suite;
  enabling the feature never changes CameraAgent acquisition or live
  processing.
- Evidence: `ElasticProviderPerformanceEvidenceTests` (Manual) records cold
  and warm start, drain throughput with 1, 2, and 4 instances, scale-to-zero
  timing, and host CPU/memory to `TestResults/elastic-providers/<revision>/`.
- Provider exit: disable the section; instances scale to zero and no job state
  lives in the provider.
