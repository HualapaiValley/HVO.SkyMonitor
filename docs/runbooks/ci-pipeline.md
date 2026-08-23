# CI Pipeline Runbook

This runbook describes the required current-head checks in `.github/workflows/ci.yml` and their local equivalents.

## Required Checks

| Check | Enforced behavior |
| --- | --- |
| **Change Classification** | Fail-closed selection of the full matrix for pushes and behavior-affecting pull requests or reduced mode for explicitly allowlisted documentation/developer-environment pull requests. |
| **Quality** | Workflow lint, syntax and documentation audits, lightweight environment/classification contracts, and Compose validation. Full mode also enforces formatting, package vulnerability/deprecation policy, the active acceptance inventory contract, and pinned .NET tools; reduced mode does not restore or audit application packages it cannot affect. |
| **Deployment Contracts** | One self-hosted job running the deployment coordinator and current campaign contracts, product-layout migration contracts, catalog lifecycle, and nine isolated split-host shards with at most eight local child processes. Always runs for main/release pushes and deployment-relevant pull requests; otherwise its planned `skipped` result is required. |
| **Build** | Warning-clean Debug and Release builds plus complete, disjoint behavioral category discovery. Skipped only in classified reduced mode. |
| **Unit Tests** | 1923 Unit cases with an intentionally invalid Docker endpoint and per-project TRX/Cobertura paths. Skipped only in classified reduced mode. |
| **Integration Tests** | 563 Integration-category cases across SQLite, filesystem, SQL Server, Redis, MinIO, Mailpit, forwarded-header, host integration, and the six repository graph/publish cases in Architecture & Publish. Skipped only in classified reduced mode. |
| **Architecture & Publish** | Six Integration-category repository graph/MSBuild/publish cases plus retained host publish manifests. |
| **Migrations** | Zero pending CameraAgent or LogicHost EF model changes; current and legacy migration convergence remains in Integration Tests. |
| **Coverage** | Exact source-path and branch merge of twelve expected reports, checked-in aggregate non-regression, and risk-file floors. |
| **Required CI** | Current-head aggregate that fails when any expected check fails, times out, is canceled, is missing, or is unexpectedly skipped or run for the selected mode. |

Each test invocation owns a category/project-specific result directory and TRX name. Coverage rejects any report count other than the expected twelve, preventing missing or overwritten evidence.

Change Classification, Catalog Contracts, Quality, and Required CI run on pinned
`ubuntu-24.04` hosted runners. Deployment Contracts, Build, Unit Tests,
Integration Tests, Architecture & Publish, Migrations, Coverage, and Coverage
Badges remain on the labeled self-hosted runners. This allocation keeps the long
deployment harness off hosted minutes. Quality performs .NET setup, restore,
formatting, and package audit only for full-mode changes.

## Categories

The category audit requires every discovered case to belong to exactly one primary behavioral category. Current discovery is `Unit=1923`, `Integration=563`, `Manual=76`, `Soak=1`, `External=0`, and `Hardware=1`.

`External` is implemented by the pinned, networkless Stellarium workflow rather than an empty MSTest check. The accelerated `Soak` case and real-duration soak are independently selectable in `.github/workflows/cameraagent-soak.yml`. The Hardware case remains separately selectable and is not published as a CI check until a suitable device runner exists.

## Local Validation

```bash
dotnet tool restore
dotnet restore HVO.SkyMonitor.v9.slnx
dotnet build HVO.SkyMonitor.v9.slnx --no-restore --no-incremental --configuration Debug -warnaserror
dotnet build HVO.SkyMonitor.v9.slnx --no-restore --no-incremental --configuration Release -warnaserror
dotnet format HVO.SkyMonitor.v9.slnx --no-restore --verify-no-changes
./scripts/package:audit
dotnet run --project scripts/test-categories/HVO.SkyMonitor.TestCategoryAudit.csproj --configuration Release
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter "TestCategory=Unit"
dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter "TestCategory=Integration"
```

Use the exact per-project commands in `.github/workflows/ci.yml` when producing coverage evidence; solution-level TRX names are not collision-proof.

The deployment harness defaults to the original serial all-contract mode for
local validation:

```bash
./scripts/test:deploy-environment
```

Its closed shard inventory is `preflight`, `prepare-images`,
`existing-catalog-up`, `bootstrap-authority`, `bootstrap-credentials`, `smoke`,
`measure`, `existing-down`, and `deploy-services`. Inspect it with
`./scripts/test:deploy-environment --list-shards`, or run one isolated shard with
`./scripts/test:deploy-environment --shard NAME`. Deployment-relevant CI uses
`./scripts/test:deploy-environment --parallel`; each child creates an independent
temporary fixture. The coordinator defaults to the smaller of the shard count,
`nproc`, and eight local child processes. `DEPLOY_TEST_MAX_PARALLEL=1` through
`8` can lower that bound. Every child runs in its own session. Fail-fast and
signal cleanup send TERM to the complete process group, wait a bounded grace
interval, escalate surviving groups to KILL, and reap stopped leaders. Queued
shards are marked as not started and the complete failed-shard log is printed.

Every coordinator invocation creates a collision-safe directory beneath
`TestResults/deployment-contracts/`. GitHub directories include run and attempt
identity; local directories use a local identity, UTC timestamp, coordinator
PID, and random suffix. Concurrent invocations never clear or share these
directories. Each contains per-shard `.log`, `.status`, and leader PID evidence,
and the Deployment Contracts artifact retains every invocation directory even
when the job fails.

Run the lightweight closed-CLI and injected coordinator-failure contracts
without executing the nine full shards:

```bash
./scripts/test:deploy-environment-cli
```

Full Quality retains the acceptance inventory contract, while the deployment
gate owns the two supported campaign orchestration contracts:

```bash
./scripts/test:phase14-acceptance
./scripts/test:phase14-campaign
./scripts/test:phase14-normal-campaign
```

The historical Phase 14 component/source importers are reproducibility tools,
not current protected gates. Run them only when changing or reproducing that
closed evidence campaign.

Run the path-classification and aggregate-protection contract tests when changing CI orchestration or the reduced-mode allowlist:

```bash
bash ./scripts/test:ci-classification
```

Use a fresh result root for every collection. Before merging, require exactly one report from each of the twelve category/project slots as shown in `.github/workflows/ci.yml`; never merge every historical GUID directory under a reused result root. Merge those twelve explicit reports once with the pinned ReportGenerator tool, then enforce and publish that same canonical result:

```bash
patterns=(
  'TestResults/unit/astronomy/*/coverage.cobertura.xml'
  'TestResults/unit/imaging/*/coverage.cobertura.xml'
  'TestResults/unit/processing/*/coverage.cobertura.xml'
  'TestResults/unit/catalog-sqlite/*/coverage.cobertura.xml'
  'TestResults/unit/cameraagent/*/coverage.cobertura.xml'
  'TestResults/unit/cameraagent-acceptance/*/coverage.cobertura.xml'
  'TestResults/unit/logichost/*/coverage.cobertura.xml'
  'TestResults/integration/cameraagent-storage/*/coverage.cobertura.xml'
  'TestResults/integration/cameraagent-standalone/*/coverage.cobertura.xml'
  'TestResults/integration/logichost/*/coverage.cobertura.xml'
  'TestResults/integration/cameraagent-host/*/coverage.cobertura.xml'
  'TestResults/architecture/*/coverage.cobertura.xml'
)
reports=()
for pattern in "${patterns[@]}"; do
  mapfile -t matches < <(compgen -G "$pattern" || true)
  if [[ "${#matches[@]}" -ne 1 ]]; then
    printf 'Expected one coverage report for %s, found %s\n' "$pattern" "${#matches[@]}" >&2
    exit 1
  fi
  reports+=("${matches[0]}")
done
dotnet reportgenerator "-reports:$(IFS=';'; echo "${reports[*]}")" -targetdir:coverage-report -reporttypes:"Html;TextSummary;MarkdownSummaryGithub;Badges;Cobertura"
./scripts/coverage:enforce --merged coverage-report/Cobertura.xml
```

Pending-model checks use the pinned `dotnet-ef` tool:

```bash
dotnet ef migrations has-pending-model-changes --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj --startup-project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj --context HVO.SkyMonitor.CameraAgent.Data.ApplicationDbContext --configuration Release --no-build
ConnectionStrings__skymonitordb="Server=127.0.0.1,1433;Database=ModelCheck;User Id=sa;Password=Model_check1!;TrustServerCertificate=True" dotnet ef migrations has-pending-model-changes --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj --startup-project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj --context HVO.SkyMonitor.LogicHost.Data.ApplicationDbContext --configuration Release --no-build
```

## Protection And Review

Protect `main` with the stable `Required CI` check and require branches to be current before merge. The project uses an independent PR review plus corrected-head reruns and resolved review threads; it does not impose a self-approval rule that a single-author workflow cannot satisfy. Stale, canceled, timed-out, failed, or absent checks do not satisfy `Required CI`.

The aggregate is fail-closed for classification inputs and job results, but a workflow running from a pull request cannot be an independent trust boundary against an author who maliciously rewrites that workflow or its CI helper scripts. Independent review of `.github/workflows/**` and `scripts/ci:*` remains part of this repository's solo-maintainer protection model. Repositories accepting untrusted workflow changes require a separately trusted required workflow or mandatory reviewer policy.

## Pull Request Selection

The workflow triggers only for pull requests targeting `main` or `release/**`,
including the `release/deploy-331` strategy. A lightweight classifier uses the
pull request's base and head commits and selects reduced mode only when every
changed path is an added or modified member of this allowlist:

- `docs/**`, except the production bundle inputs `docs/catalog/hyg-v42-attribution.md` and `docs/catalog/hyg-v42-license.md`
- `.devcontainer/**`
- `.vscode/**`
- `.github/prompts/**`
- `README.md`, `AGENTS.md`, and `THIRD-PARTY-NOTICES.md`
- `.github/copilot-instructions.md` and `.github/pull_request_template.md`
- `tools/asi-capture/README.md`
- one-level `src/*/README.md` and `tests/*/README.md`
- `tests/fixtures/catalog/SOURCE.md` and `tests/fixtures/stellarium/SIMBAD_ENDPOINTS.md`
- `scripts/opencode:enable`, `scripts/opencode:disable`, `scripts/opencode:connect`, `scripts/opencode:prepare-rebuild`, `scripts/opencode:remote-connect`, and `scripts/test:opencode`

Reduced mode still runs lightweight **Quality** and **Required CI**. Quality
does not set up .NET, restore/format the solution, build catalog artifacts, or
run the package audit for paths excluded from application/package behavior. It intentionally skips
Build, Unit Tests, Integration Tests, Architecture & Publish, Migrations, and
Coverage. Documentation-only reduced pull requests also skip Deployment
Contracts. `Required CI` accepts those skipped results only when classification
and Quality succeeded and the deployment plan is explicitly `false`.

Deployment selection is independent from full/reduced mode. A full-mode pull
request with ordinary application or test changes runs the existing full build
and test matrix but skips Deployment Contracts. A deployment-relevant pull
request runs the sharded deployment gate. The closed deployment path map is:

- `.github/workflows/ci.yml`, `.dockerignore`, `.env.template`, `docker-compose.apps.yml`, and `global.json`
- `scripts/ci:classify`, `scripts/ci:require`, and `scripts/test:ci-classification`
- `scripts/deploy:environment`, `scripts/deploy:migrate-product-layout`, and `scripts/deploy/**`
- `scripts/test:deploy-environment`, `scripts/test:deploy-environment-cli`, `scripts/test:product-layout`, `scripts/test:phase14-campaign`, and `scripts/test:phase14-normal-campaign`
- `scripts/catalog:*`, `scripts/catalog/**`, and `scripts/infra:operation-lock`
- `deploy/**`
- `tests/fixtures/catalog/hyg-v42-bright-stars.sqlite`
- `src/HVO.SkyMonitor.CameraAgent/Dockerfile` and `src/HVO.SkyMonitor.LogicHost/Dockerfile`
- `src/HVO.SkyMonitor.CameraAgent/cameraagent.sample.json`
- `src/HVO.SkyMonitor.CameraAgent/virtual-asi174.full.json` and `src/HVO.SkyMonitor.CameraAgent/virtual-asi178mc.full.json`

The classifier emits both `mode` and `deployment` with reasons in the step
summary. Missing commits, failed or malformed diffs, empty change sets,
deletions, renames, type changes, and missing or non-regular entries fail closed
to `mode=full` and `deployment=true`. `Required CI` requires Deployment
Contracts to be `success` exactly when deployment is `true`, and `skipped`
exactly when it is `false`; failed, canceled, missing, or mismatched results are
rejected in either plan.

Pushes to `main` or `release/**` always use the full matrix and deployment
contracts. The existing full/reduced scope for Build, Unit Tests, Integration
Tests, Architecture & Publish, Migrations, and Coverage is unchanged; this work
does not implement broader subsystem targeting.

## Failure Triage

- Formatting or package failures name the command, package, version, project, and reviewed allowlist status.
- Category failures name uncategorized, multiply categorized, unknown, or count-drifted tests.
- Migration failures identify the host context with pending model changes.
- Coverage failures name exact source paths, covered/valid counts, observed rates, and required floors.
- Integration failures retain separate project/category TRX and Cobertura evidence for 30 days.
- Deployment failures identify the failed shard in coordinator output; inspect the retained shard log and status marker before rerunning only that shard locally.

## Deployment Timing Evidence

Run `32669782600` is the cleanup baseline: Quality took 12:23, including about
4:05 for the closed Phase 14 component/source importer path. Issue #444 removes
that historical work from protected CI, retains the fast acceptance inventory
contract in full Quality, and moves the current campaign contracts to Deployment Contracts. Reduced mode also
avoids full-only .NET restore/format/package work.

Earlier pre-change Quality jobs took approximately 24-25 minutes, with the
deployment suite accounting for approximately 19-20 minutes. The first
four-shard implementation measured 22:51 serial and 19:38 parallel; its
`existing-services` shard took 19:38 and therefore did not satisfy the target.
The refined nine-shard local candidate measured 22:48 serial and a 10:08 median
parallel time on the same 8-core, 31-GiB host, a 55.6% wall-time reduction.
Three complete parallel runs passed in 10:07.64, 10:07.82, and 10:11.06 with
no leaked child or listener process. User plus system
CPU increased from 1,424.44 to 1,513.80 seconds (6.3%) because independently
reproducible lifecycle setup is repeated; maximum reported resident set stayed
within measurement noise at approximately 202 MiB. Record the current-head
hosted Quality, Deployment Contracts, and total Required CI wall time here after
protected CI executes. Pull-request deployment relevance reduces unnecessary
runner use but is not included in the 55.6% execution-time comparison.
