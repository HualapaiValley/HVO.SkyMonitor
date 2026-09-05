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
      "AllowInsecureHttp": false
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
  instance is started through `setsid` so the launcher and everything it
  starts are terminated as one process group. The instance receives an
  allowlisted runtime environment (`PATH`, `HOME`, locale, `DOTNET_*`,
  temp and certificate paths; never connection strings, passwords, keys, or
  tokens) plus the container's `HVO_RUNNER_*` contract with the secret by
  file path only.
- `LocalProcess:IdleShutdown` is a safety net for a host that disappears:
  zero (default) leaves instances host-managed; a value is raised to outlive
  the host's scale-to-zero window, and warm-minimum instances never
  self-terminate, so an instance is retired and accounted by the host instead
  of exiting on its own as an orphan.
- Retirement drains first on every platform (a stop file the runner watches,
  plus `SIGTERM` to the process group on Unix) and forces the stop after
  `RetireGrace`. `MaxInstanceMinutesPerDay` also drains existing instances
  once the budget is spent.
- Backlog is counted with the claim's pool predicate (a reserved pool serves
  its pool and shared work; unpooled instances serve only unpooled
  observatories), and demand includes work already executing on the
  instances. A launched process is always recorded before it can claim: the
  durable intent precedes the launch, a failed launch closes it, and an
  instance the provider reports without a record is retired.
- A host restarted with `ElasticProviders` disabled retires instances a
  previous enabled host launched.

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
