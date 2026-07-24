# Smoke-Test Environment Runbook

This runbook defines the configuration boundary for reproducible platform smoke
tests. The complete split-host deployment and bootstrap automation remains owned
by issue #151; these commands only generate and verify inputs and do not start,
stop, reset, or mutate application or shared-service resources.

## Environment Contract

`.env.smoketest.template` is the checked-in, secret-free schema. The generated
`.env.smoketest` is ignored, must remain owner-only, and contains one smoke run's
complete operator inputs:

- control/Docker host names, addresses, public URLs, ports, and host-path mapping;
- SQL Server, Redis, MinIO, SMTP, and OpenTelemetry endpoints and credentials;
- LogicHost and CameraAgent owner/client identity inputs;
- desired Observatory name, east-positive longitude, latitude, elevation, and
  timezone;
- CameraAgent friendly name, configuration file, expected dimensions, exposure,
  and cadence;
- approved production catalog identity and control-host installation;
- run ID, state/evidence paths, bootstrap mode, and data policy.

Device ID, verification code, registration/public IDs, envelope, and device
secret are application-generated state. They must never be added to
`.env.smoketest`; future #151 automation will obtain them through the supported
bootstrap APIs/UI and retain them under the owner-protected
`SMOKETEST_RUN_STATE_PATH`.

## Initialize

Create or refresh the ignored file from `.env` and the template:

```bash
./scripts/smoke:env init
```

Existing `.env.smoketest` operator values win, so rerunning initialization
preserves an established run. The canonical Compose/SQL/Redis/MinIO namespaces
are re-derived in isolated mode. The development `.env` runtime root is ignored
so a smoke cannot silently reuse ordinary application state. Missing smoke owner
credentials are generated without being printed. Shared-service and CameraAgent
credentials are never invented because they must match provisioned services.

Non-secret host-specific values can be supplied without opening the secret file:

```bash
./scripts/smoke:env init \
  SMOKETEST_DOCKER_HOST_NAME=hvo-dev-01 \
  SMOKETEST_DOCKER_HOST_ADDRESS=192.168.1.9 \
  SMOKETEST_LOGIC_HOST_PUBLIC_URL=http://192.168.1.9:5174 \
  SMOKETEST_CAMERA_AGENT_PUBLIC_URL=http://192.168.1.9:5130
```

Only `SMOKETEST_*` and `HVO_RUNTIME_DATA_ROOT` overrides are accepted on the
command line. Credentials must come from the owner-only baseline or generated
target, never process arguments.

Review non-secret values locally, then set host-specific public URLs and the
host-side repository/runtime paths. Never paste the file or its secret values
into an issue, PR, log, or evidence bundle.

## Validate And Preflight

Static validation is safe before services are reachable:

```bash
./scripts/smoke:env validate
```

Preflight adds read-only Docker, DNS/TCP endpoint, production catalog manifest,
database-file, and checksum checks:

```bash
./scripts/smoke:env preflight
```

Both commands clear ambient contract variables, fail closed, and print variable
names or resource classes, never secret values. Validation also checks that the
selected CameraAgent JSON has the declared dimensions, cadence mode/interval,
and night exposure. Preflight does not contact application login/bootstrap
endpoints because the applications may not have started yet.

## Data Policy

`SMOKETEST_DATA_POLICY` is one of:

- `preserve`: retain existing application and shared-service history;
- `isolated`: use run-scoped resources created by the future #151 orchestrator;
- `reset-shared`: permit a future explicit shared-data reset.

`reset-shared` is rejected unless `SMOKETEST_CONFIRM_SHARED_DATA_RESET` exactly
matches `SMOKETEST_RUN_ID`. The current environment command only validates this
intent; it does not delete data. A reset implementation must enumerate the exact
SQL database, Redis namespace, MinIO buckets, application state, and Mailpit data
before deletion and remain scoped to HVO.SkyMonitor resources.

`preserve` and `reset-shared` accept only database `SkyMonitor`, Redis prefix
`skymonitor:`, and buckets `skymonitor-artifacts` and
`skymonitor-diagnostics`. This prevents a future reset consumer from accepting
an unrelated resource merely because the confirmation token matched.

For `isolated`, initialization derives run-scoped SQL database, Redis prefix,
MinIO artifact/diagnostic bucket, and Compose project names from
`SMOKETEST_RUN_ID`; validation rejects an isolated contract if any namespace no
longer exactly matches its canonical run-owned name.

## Contract Tests

```bash
./scripts/test:smoke-env
```

The tests use temporary files and mocked connectivity. They verify owner-only
permissions, generated run-scoped credentials, required fields, coordinate and
reset guards, exclusion of generated bootstrap state, production catalog
identity/checksum checks, and output redaction.

## Full-Resolution Standalone CameraAgent

Issue #171 has a separate opt-in smoke that does not start LogicHost or shared
services. It loads `cameraagent.standalone-production-smoke.json`, rejects all
central HTTP traffic, and requires an installed Production HYG 4.2 catalog. The
checked-in `appsettings.StandaloneProductionSmoke.json` supplies the standalone
host boundary when running the CameraAgent directly with
`DOTNET_ENVIRONMENT=StandaloneProductionSmoke`. The checked-in deployment pair
uses `/var/lib/hvo/data/agent` for both raw ingress and derivative storage and
`/var/lib/hvo/data/catalog` for the catalog. If an operator changes the storage
location, both `CameraAgent__RawIngressRoot` and the deployment copy's
`LocalStorage.options.storageRoot` must resolve to the same directory.

Direct-host startup also requires an owner password and writable identity,
provisioning, and Data Protection state. Keep the password out of source and
provide it through environment configuration, user secrets, or the host's
secret manager. Identity and provisioning paths are configurable as shown
below; Data Protection keys are written to `DataProtection-Keys` below the
CameraAgent content root, which must therefore be writable and persistent. A
representative direct launch is:

```bash
DOTNET_ENVIRONMENT=StandaloneProductionSmoke \
LocalIdentity__AdminPassword='OperatorSecret!171' \
LocalIdentity__DatabasePath=/var/lib/hvo/data/agent/identity/cameraagent_identity.db \
DeviceProvisioning__StateDirectory=/var/lib/hvo/data/agent/provisioning \
  dotnet run --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj
```

Build and install the pinned catalog only when the approved package is not
already present:

```bash
./scripts/catalog/build-hyg-v42.sh --fetch \
  --install-root /var/lib/hvo/data/catalog \
  /var/lib/hvo/catalog-build
```

Run the smoke against the installation root, not a bundle or SQLite file:

```bash
HVO_CATALOG_PERF_ROOT=/var/lib/hvo/data/catalog \
  ./scripts/test:cameraagent-standalone-171
```

The gate fails closed on package kind, version, manifest/schema/preprocessing
versions, database SHA-256/length, and 119,625-row identity. It retains a
sanitized full-frame annotated JPEG and JSON manifest for each of five
independent, approximately one-minute trials below
`TestResults/issue-171/production-smoke`, including the
fixed `2026-01-15T08:00:00Z` astronomy scene epoch and run clock anchor,
selected catalog row IDs, deployment location, recipes/lineage, actual
module-start five-second cadence, CPU, RSS/LOH, allocations, retained/download I/O, throughput, latency,
queue/backlog/drain, restart, and central-traffic evidence. The UTC anchor is
fixed once at the start of each run and then advances with real elapsed time so
all five trials render the same sky while durable lease and retention clocks
remain on operational wall time. The runner also executes the 5-warmup/30-sample
W1 and W2 reference-calibration gates and the representative durable local-graph
gate into the same result root.
