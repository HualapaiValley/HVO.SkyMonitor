# CI Pipeline Runbook

This runbook describes the required current-head checks in `.github/workflows/ci.yml` and their local equivalents.

## Required Checks

| Check | Enforced behavior |
| --- | --- |
| **Change Classification** | Fail-closed selection of the full matrix for pushes and behavior-affecting pull requests or reduced mode for explicitly allowlisted documentation/developer-environment pull requests. |
| **Catalog Contracts** | Full-mode hosted build and smoke validation of the exact HYG v42 production catalog contracts, retained as a one-day workflow artifact. Skipped in classified reduced mode; its result is not currently aggregated by Required CI. |
| **Quality** | Workflow lint, syntax and documentation audits, lightweight environment/classification contracts, and Compose validation. Full mode also enforces formatting, package vulnerability/deprecation policy, and pinned .NET tools; manual dispatch additionally validates the historical Phase 14 acceptance inventory. Reduced mode does not restore or audit application packages it cannot affect. |
| **Deployment Contracts** | Deployment-relevant pull requests run the coordinator watchdog/failure contracts and current campaign-shape contracts, plus only the affected exhaustive catalog, split-host, or installer suite selected by the classifier. Main/release/manual runs execute every exhaustive suite. Otherwise its planned `skipped` result is required. |
| **Build** | Warning-clean Debug and Release builds plus complete, disjoint behavioral category discovery. Skipped only in classified reduced mode. |
| **Unit Tests** | 2604 Unit cases with an intentionally invalid Docker endpoint and per-project TRX/Cobertura paths. Skipped only in classified reduced mode. |
| **Integration Tests** | 582 Integration-category cases across SQLite, filesystem, SQL Server, Redis, S3-compatible object storage, Mailpit, forwarded-header, host integration, and the seven repository graph/provider-boundary/publish cases in Architecture & Publish. LogicHost coverage includes clean/current-layout initialization, idempotency, schema, locking, and permission behavior. Skipped only in classified reduced mode. |
| **Architecture & Publish** | Seven Integration-category repository graph/provider-boundary/MSBuild/publish cases, retained host publish manifests, and self-contained installer publishes plus SHA-256 manifests for Linux x64 and ARM64. |
| **Migrations** | Exactly one canonical initial migration source for CameraAgent Identity and LogicHost plus zero pending EF model changes; unreleased legacy-schema convergence is not supported. |
| **Coverage** | Exact source-path and branch merge of 22 expected reports, checked-in aggregate non-regression, and risk-file floors. The Coverlet 10.0.1 baseline is 84.3690% line and 66.3253% branch coverage. |
| **Required CI** | Current-head aggregate that fails when any expected check fails, times out, is canceled, is missing, or is unexpectedly skipped or run for the selected mode. |

Each test invocation owns a category/project-specific result directory and TRX name. Coverage rejects any report count other than the expected 22, preventing missing or overwritten evidence.

Change Classification, Catalog Contracts, Quality, and Required CI run on pinned
`ubuntu-24.04` hosted runners. Deployment Contracts, Build, Unit Tests,
Integration Tests, Architecture & Publish, Migrations, Coverage, and Coverage
Badges remain on the labeled self-hosted runners. This allocation keeps the long
deployment harness off hosted minutes. Quality performs .NET setup, restore,
formatting, and package audit only for full-mode changes.

## ARM64 Advisory

`.github/workflows/cameraagent-arm64.yml` supplies native Linux ARM64 evidence
on a dedicated runner selected by
`[self-hosted, linux, ARM64, hvo-skymonitor-arm64]`. It runs from protected
`main`, by daily schedule, or by default-branch `repository_dispatch`; it does
not execute pull-request or branch-selectable manual-dispatch code and is not
aggregated by `Required CI`.

The workflow builds the canonical production catalog contract on hosted x64,
then performs a native CameraAgent Release build, Docker-disabled Unit subset,
`linux-arm64` publish/ELF validation, native container build, catalog install,
and bounded VirtualSky smoke on ARM64. It retains checksums, image identity,
host resource/thermal samples, installation verification, capture evidence,
SQLite checks, logs, and cleanup state for 30 days. Provisioning, isolation,
updates, recovery, decommissioning, and promotion criteria are maintained in
[`cameraagent-arm64-ci.md`](cameraagent-arm64-ci.md).

## Categories

The category audit requires every discovered case to belong to exactly one primary behavioral category. Current discovery is `Unit=2604`, `Integration=582`, `Manual=87`, `Soak=1`, `External=0`, and `Hardware=1`.

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

The exhaustive deployment harness runs on main/release/manual CI and on pull
requests that change split-host inputs. It defaults to the original serial
all-contract mode for local validation:

```bash
./scripts/test:deploy-environment
```

Its closed shard inventory is `preflight`, `prepare-images`, `partial-prepare`,
`existing-catalog-up`, `bootstrap-authority`, `bootstrap-credentials`, `smoke`,
`measure`, `existing-down`, and `deploy-services`. Inspect it with
`./scripts/test:deploy-environment --list-shards`, or run one isolated shard with
`./scripts/test:deploy-environment --shard NAME`. Main/release/manual CI and
affected pull requests use `./scripts/test:deploy-environment --parallel`; each
child creates an independent temporary fixture. The coordinator defaults to the
smaller of the shard count,
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

Manual dispatch retains the historical Phase 14 acceptance inventory contract:

```bash
./scripts/test:phase14-acceptance
```

Deployment-relevant pull requests retain the lightweight coordinator and two
current deployment orchestration contracts:

```bash
./scripts/test:deployment-logichost-outage-contract
./scripts/test:deployment-normal-flow-contract
```

The historical Phase 14 component/source importers are reproducibility tools,
not current protected gates. Run them only when changing or reproducing that
closed evidence campaign.

Run the path-classification and aggregate-protection contract tests when changing CI orchestration or the reduced-mode allowlist:

```bash
bash ./scripts/test:ci-classification
```

Use a fresh result root for every collection. Before merging, require exactly one report from each of the 22 category/project slots as shown in `.github/workflows/ci.yml`; never merge every historical GUID directory under a reused result root. Merge those 22 explicit reports once with the pinned ReportGenerator tool, then enforce and publish that same canonical result:

```bash
patterns=(
  'TestResults/unit/astronomy/*/coverage.cobertura.xml'
  'TestResults/unit/imaging/*/coverage.cobertura.xml'
  'TestResults/unit/processing/*/coverage.cobertura.xml'
  'TestResults/unit/catalog-sqlite/*/coverage.cobertura.xml'
  'TestResults/unit/deployment-cli/*/coverage.cobertura.xml'
  'TestResults/unit/deployment-distribution/*/coverage.cobertura.xml'
  'TestResults/unit/agent-core/*/coverage.cobertura.xml'
  'TestResults/unit/common/*/coverage.cobertura.xml'
  'TestResults/unit/fleet-contracts/*/coverage.cobertura.xml'
  'TestResults/unit/test-support/*/coverage.cobertura.xml'
  'TestResults/unit/architecture/*/coverage.cobertura.xml'
  'TestResults/unit/cameraagent/*/coverage.cobertura.xml'
  'TestResults/unit/cameraagent-acceptance/*/coverage.cobertura.xml'
  'TestResults/unit/logichost/*/coverage.cobertura.xml'
  'TestResults/unit/cameraagent-logichost/*/coverage.cobertura.xml'
  'TestResults/integration/cameraagent-storage/*/coverage.cobertura.xml'
  'TestResults/integration/cameraagent-standalone/*/coverage.cobertura.xml'
  'TestResults/integration/astronomy/*/coverage.cobertura.xml'
  'TestResults/integration/logichost/*/coverage.cobertura.xml'
  'TestResults/integration/cameraagent/*/coverage.cobertura.xml'
  'TestResults/integration/cameraagent-logichost/*/coverage.cobertura.xml'
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

The Migrations job first requires exactly one non-designer timestamped migration
source file in each EF context. A model change replaces that canonical migration
and snapshot; adding a second migration fails CI during the pre-release period.

## Protection And Review

Protect `main` with the stable `Required CI` check and require branches to be
current before merge. GitHub enforces current-head CI and conversation
resolution; independent review is an additional repository-process requirement,
not an approving-review branch-protection rule. The initial review covers the
full PR diff. Later reviews cover only the correction delta from the previous
reviewed head and verify prior findings. Use normal GitHub review, `@codex review`,
or independent local review; do not wait indefinitely when a service or billing
condition makes one path unavailable.

Open implementation PRs as drafts and keep them draft while review corrections
converge. Draft PR events skip the expensive CI plan and publish an intentionally
failing `Required CI`, so branch protection remains fail-closed; returning a PR
to draft also cancels its superseded in-progress run. Marking the final reviewed
head ready triggers the classifier-selected CI plan, and a successful
`Required CI` on that exact head satisfies the gate. Before every planned
post-ready head change, including CI corrections, base synchronization, and
conflict resolution, return the PR to draft. Review that delta before marking it
ready again. A diagnosed infrastructure failure may rerun the same unchanged
SHA. Stale, canceled, timed-out, failed, skipped, or absent checks do not satisfy
`Required CI`.

The aggregate is fail-closed for classification inputs and job results, but a workflow running from a pull request cannot be an independent trust boundary against an author who maliciously rewrites that workflow or its CI helper scripts. Independent review of `.github/workflows/**` and `scripts/ci:*` remains part of this repository's solo-maintainer protection model. Repositories accepting untrusted workflow changes require a separately trusted required workflow or mandatory reviewer policy.

## Pull Request Selection

The workflow runs the classifier-selected CI plan only for non-draft pull
requests targeting `main` or `release/**`. Draft events run only the fail-closed
`Required CI` result. The
workflow responds to the `ready_for_review` transition so a reviewed draft
receives current-head CI. A lightweight classifier uses the
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
- `scripts/opencode:enable`, `scripts/opencode:disable`, `scripts/opencode:connect`, `scripts/opencode:remote-connect`, and `scripts/test:opencode`

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
request runs the lightweight deployment gate plus the affected exhaustive suite
selected by `deployment_catalog`, `deployment_shards`, or
`deployment_installer`. Main, `release/**`, and manual runs select all three. The
closed deployment path map is:

- `.github/workflows/ci.yml`, `.dockerignore`, `.env.template`, `docker-compose.apps.yml`, and `global.json`
- `scripts/ci:classify`, `scripts/ci:require`, and `scripts/test:ci-classification`
- `scripts/deploy:environment` and `scripts/deploy/**`
- `scripts/test:deploy-environment`, `scripts/test:deploy-environment-cli`, `scripts/test:deployment-installer`, `scripts/test:deployment-logichost-outage-contract`, and `scripts/test:deployment-normal-flow-contract`
- `scripts/catalog:*`, `scripts/catalog/**`, and `scripts/infra:operation-lock`
- `deploy/**`
- `tests/fixtures/catalog/hyg-v42-bright-stars.sqlite`
- `src/HVO.SkyMonitor.CameraAgent/Dockerfile` and `src/HVO.SkyMonitor.LogicHost/Dockerfile`
- `src/HVO.SkyMonitor.Deployment.Cli/**`, `src/HVO.SkyMonitor.Deployment.Contracts/**`, `src/HVO.SkyMonitor.Deployment.Distribution/**`, `tests/HVO.SkyMonitor.Deployment.*.Tests/**`, and `tools/HVO.SkyMonitor.Deployment.ReleaseTool/**`
- `.github/workflows/release.yml` and `scripts/install-hvo-skymonitor.sh`
- `src/HVO.SkyMonitor.CameraAgent/cameraagent.sample.json`
- `src/HVO.SkyMonitor.CameraAgent/virtual-asi174.full.json` and `src/HVO.SkyMonitor.CameraAgent/virtual-asi178mc.full.json`

The classifier emits `mode`, `deployment`, and the three exhaustive-suite outputs
with reasons in the step summary. Missing commits, failed or malformed diffs,
and empty change sets fail closed to every suite. Deletions, renames, type
changes, and missing or non-regular entries use the affected path to select a
suite while still forcing `mode=full` and `deployment=true`. `Required CI` requires Deployment
Contracts to be `success` exactly when deployment is `true`, and `skipped`
exactly when it is `false`; failed, canceled, missing, or mismatched results are
rejected in either plan.

Pushes to `main` or `release/**` and manual dispatches use the full matrix and
exhaustive deployment contracts. The existing full/reduced scope for Build, Unit
Tests, Integration Tests, Architecture & Publish, Migrations, and Coverage is
unchanged; this work does not implement broader subsystem targeting.

## Failure Triage

- Formatting or package failures name the command, package, version, project, and reviewed allowlist status.
- Category failures name uncategorized, multiply categorized, unknown, or count-drifted tests.
- Migration failures identify the host context with pending model changes.
- Coverage failures name exact source paths, covered/valid counts, observed rates, and required floors.
- Integration failures retain separate project/category TRX and Cobertura evidence for 30 days.
- Deployment failures identify the failed shard in coordinator output; inspect the retained shard log and status marker before rerunning only that shard locally.
