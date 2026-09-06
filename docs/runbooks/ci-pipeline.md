# CI Pipeline Runbook

This runbook describes the required current-head checks in `.github/workflows/ci.yml` and their local equivalents.

## Required Checks

| Check | Enforced behavior |
| --- | --- |
| **Change Classification** | Fail-closed selection of the full matrix for pushes and behavior-affecting pull requests or reduced mode for explicitly allowlisted documentation/developer-environment pull requests. |
| **Catalog Contracts** | Full-mode hosted build and smoke validation of the exact HYG v42 production catalog contracts, retained as a one-day workflow artifact. Skipped in classified reduced mode. |
| **Quality** | Workflow lint, syntax and documentation audits, lightweight environment/classification contracts, and Compose validation. Full mode also enforces formatting, package vulnerability/deprecation policy, and pinned .NET tools; manual dispatch additionally validates the historical Phase 14 acceptance inventory. Reduced mode does not restore or audit application packages it cannot affect. |
| **Deployment Contracts** | Deployment-relevant pull requests run the coordinator watchdog/failure contracts and current campaign-shape contracts, plus only the affected exhaustive catalog, split-host, or installer suite selected by the classifier. Main/release/manual runs execute every exhaustive suite. Otherwise its planned `skipped` result is required. |
| **Build** | Warning-clean solution Debug and Release builds plus complete, disjoint behavioral category discovery. Never component-scoped, so no component plan can hide a warning or a category-count drift. Skipped only in classified reduced mode. |
| **Unit Tests** | 3309 Unit cases with an intentionally invalid Docker endpoint and per-project TRX/Cobertura paths. Runs only for the complete solution plan; component plans run the same per-project commands inside their selected lanes. |
| **Integration Tests** | 659 Integration-category cases across SQLite, filesystem, SQL Server, Redis, S3-compatible object storage, Mailpit, forwarded-header, host integration, and the seven repository graph/provider-boundary/publish cases in Architecture & Publish. LogicHost coverage includes clean/current-layout initialization, idempotency, schema, locking, and permission behavior. Runs only for the complete solution plan; component plans run the same per-project commands inside their selected lanes. |
| **Architecture & Publish** | Both category selections of the architecture project: the six Unit-category boundary cases, including the host `Dockerfile` and fault-matrix discovery contracts, and the seven Integration-category repository graph/provider-boundary/MSBuild/publish cases; plus retained host publish manifests and self-contained installer publishes with SHA-256 manifests for Linux x64 and ARM64. Never component-scoped, so no component plan can skip the architecture or host-publish boundary. |
| **CameraAgent Migrations** | Exactly one canonical initial migration source for CameraAgent Identity plus zero pending CameraAgent EF model changes, built from the CameraAgent project root. Runs for every full-mode head. |
| **LogicHost Migrations** | Exactly one canonical initial migration source for LogicHost plus zero pending LogicHost EF model changes, built from the LogicHost project root. Runs for every full-mode head. Unreleased legacy-schema convergence is not supported by either host. |
| **Coverage Policy** | Schema validation of every aggregate and component coverage baseline plus rejection of any pull request that lowers a baseline rate, raises its tolerance, or drops a baseline file floor relative to the merge target. Runs for every full-mode head. |
| **Shared Libraries** | Component lane. Project-root restore/build of the shared test and runner project roots, the shared Unit and Integration selections, a `linux-x64` ProcessingRunner publish with a checksum manifest, and the shared component coverage baseline over 9 slots. |
| **CameraAgent Component** | Component lane. Project-root restore/build of the CameraAgent host, runner, and test project roots, the CameraAgent Unit and Integration selections, CameraAgent and `linux-x64` ReplayRunner publishes with checksum manifests and an opposite-host assembly check, and the CameraAgent component coverage baseline over 5 slots. |
| **LogicHost Component** | Component lane. Project-root restore/build of the LogicHost host and test project roots, the LogicHost Unit and Integration selections, the LogicHost publish with checksum manifest and an opposite-host assembly check, and the LogicHost component coverage baseline over 2 slots. |
| **Combined Protocol & Integration** | Component lane. Project-root restore/build and execution of the only suites allowed to compose both hosts, covering registration, identity, artifact transfer, fleet contracts, and other cross-host behavior, plus the combined component coverage baseline over 2 slots. |
| **Delivery Component** | Component lane. Project-root restore/build of the deployment CLI project root and the deployment CLI/distribution test roots, which also build the deployment contracts and release-tool projects they reference, against the exact production catalog contract; a `linux-x64` installer publish with a checksum manifest; and the delivery component coverage baseline over 2 slots. |
| **Coverage** | Aggregate rollup for the complete solution plan: exact source-path and branch merge of 22 expected reports, checked-in aggregate non-regression, and risk-file floors. The Coverlet 10.0.1 baseline is 84.3690% line and 66.3253% branch coverage. Component plans enforce their own baselines inside their lanes instead. |
| **Required CI** | One current-head aggregate whose expected job results are derived from the validated classifier plan. It fails when any expected check fails, times out, is canceled, is missing, is unexpectedly skipped, or is unexpectedly run, and it rejects the plan itself when the plan is incomplete, self-inconsistent, or invalid for the event. |

Each test invocation owns a category/project-specific result directory and TRX name. Coverage rejects any report count other than the expected 22, preventing missing or overwritten evidence.

Change Classification, Catalog Contracts, Quality, Coverage Policy, and Required
CI run on pinned `ubuntu-24.04` hosted runners. Deployment Contracts, Build, Unit
Tests, Integration Tests, Architecture & Publish, both migration checks, every
component lane, Coverage, and Coverage Badges remain on the labeled self-hosted
runners. This allocation keeps the long
deployment harness off hosted minutes. Quality performs .NET setup, restore,
formatting, and package audit only for full-mode changes.

## Coverage Badge Publication

The main-branch aggregate Coverage job publishes its line and branch percentages
to the public
[`HVO.SkyMonitor coverage badges`](https://gist.github.com/RoySalisbury/aec5c0f8e0741d964da6859ec4466740)
Gist. The README renders `coverage-line.json` and `coverage-branch.json` through
the Shields endpoint. The Gist must contain only public aggregate metrics; never
publish paths, credentials, test output, or other repository data there.

Badge publication uses two repository-level GitHub Actions settings:

| Setting | Kind | Value and ownership |
| --- | --- | --- |
| `GIST_TOKEN` | Secret | Dedicated token owned by the Gist account. Use either a fine-grained token with only the `Gists: read and write` user permission or an OAuth token whose only scope is `gist`; record its expiry or rotation due date. |
| `COVERAGE_GIST_ID` | Variable | `aec5c0f8e0741d964da6859ec4466740`; this identifier is public and is not a credential. |

The owner recovery copy of `GIST_TOKEN` is
`HVO-SkyMonitor--GitHub--GistToken` in the RBAC-enabled `hvo-central-kv` Azure
Key Vault. The Gist ID is mirrored there as
`HVO-SkyMonitor--GitHub--CoverageGistId`. GitHub Actions does not read either
value directly from Key Vault: the badge job intentionally has no Azure login,
repository permission, or access to the release-signing identity. Use the Azure
and GitHub settings portals to copy the current Key Vault secret version into
the repository Actions secret without placing it in a command argument, shell
history, log, or issue comment. Install the public identifier with:

```bash
gh variable set COVERAGE_GIST_ID \
  --body aec5c0f8e0741d964da6859ec4466740 \
  --repo RoySalisbury/HVO.SkyMonitor
```

The publication step is best-effort because `Required CI` is the protected
code-quality gate. Missing configuration emits a warning and summary. When
configured, the job sends one atomic Gist API request for both files. Connection,
transfer, and retry limits bound that request to less than the job's five-minute
backstop; a request failure is tolerated and followed by a diagnostic warning
and summary. The token is supplied to `curl` through standard input rather than
the process argument list on the shared runner, and the invocation disables
ambient curl configuration before reading that credential. Treat any badge-job
warning or failed publication step as stale-badge evidence and repair it before
relying on the displayed percentages. A runner or workflow cancellation can
still cancel the overall run and is an infrastructure failure, not a
badge-publication result.

Rotate the token make-before-break: create the replacement with the same narrow
permission or scope, create a new Key Vault secret version, update the GitHub
Actions secret from that version, rerun the badge job, verify both Gist files
changed, and only then revoke the old token. Verify the rendered endpoints and
the underlying JSON without exposing the token:

```bash
curl --fail --silent --show-error \
  https://gist.githubusercontent.com/RoySalisbury/aec5c0f8e0741d964da6859ec4466740/raw/coverage-line.json |
  jq --exit-status '.schemaVersion == 1 and (.message | endswith("%"))'
curl --fail --silent --show-error \
  https://gist.githubusercontent.com/RoySalisbury/aec5c0f8e0741d964da6859ec4466740/raw/coverage-branch.json |
  jq --exit-status '.schemaVersion == 1 and (.message | endswith("%"))'
```

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

The category audit requires every discovered case to belong to exactly one primary behavioral category. Current discovery is `Unit=3309`, `Integration=659`, `Manual=102`, `Soak=1`, `External=0`, and `Hardware=1`.

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

Use the exact per-project commands in `.github/workflows/ci.yml` when producing
coverage evidence; solution-level TRX names are not collision-proof. Run the
per-lane equivalents in [Component Selection](#component-selection) when a
change is component-scoped.

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

Run the path-classification, component-plan, and aggregate-protection contract
tests when changing CI orchestration, the component map, the seam rule, or the
reduced-mode allowlist. They cover representative shared-only, CameraAgent-only,
LogicHost-only, combined, delivery, documentation-only, and CI-change diffs,
`Required CI` failure aggregation and cancellation, and the complete fallback
matrix:

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

Each split migration job first requires exactly one non-designer timestamped
migration source file in its own EF context, through
`./scripts/ci:canonical-migration <cameraagent|logichost>`. A model change replaces that canonical migration
and snapshot; adding a second migration fails CI during the pre-release period.

## Protection And Review

Protect `main` with the stable `Required CI` check and require branches to be
current before merge. GitHub enforces current-head CI and conversation
resolution; independent review is an additional repository-process requirement,
not an approving-review branch-protection rule. The complete review-provider,
correction-cap, finalization-lock, and merge procedure is defined by
`.agents/skills/pr-lifecycle/SKILL.md`.

Open implementation PRs as drafts and keep them draft while review corrections
converge and the target branch is finally synchronized and integration-reviewed.
Draft PR events skip the expensive CI plan and publish an intentionally failing
`Required CI`, so branch protection remains fail-closed; returning a PR to draft
also cancels its superseded in-progress run. Only the PR holding the
repository-wide finalization lock may perform final synchronization, transition
to ready, run protected CI, and merge. Marking that final reviewed head ready
triggers the classifier-selected CI plan, and a successful `Required CI` on that
exact head and current target base satisfies the gate. A diagnosed
infrastructure failure may rerun the same unchanged SHA. Stale, canceled,
timed-out, failed, skipped, or absent checks do not satisfy `Required CI`.

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
- `.agents/skills/*/SKILL.md`; supporting scripts and other resources fail
  closed to the complete matrix
- `.devcontainer/**`
- `.vscode/**`
- `.github/prompts/**`
- `README.md`, `AGENTS.md`, `CLAUDE.md`, and `THIRD-PARTY-NOTICES.md`
- `.github/copilot-instructions.md` and `.github/pull_request_template.md`
- `tools/asi-capture/README.md`
- one-level `src/*/README.md` and `tests/*/README.md`
- `tests/fixtures/catalog/SOURCE.md` and `tests/fixtures/stellarium/SIMBAD_ENDPOINTS.md`
- `scripts/test:devcontainer`
Reduced mode still runs lightweight **Quality** and **Required CI**. Quality
resolves the devcontainer definition after unsetting GitHub-token and Git-identity
variables and suppresses the resolved output because future configuration may
contain secrets. It does not set up .NET, restore/format the solution, build
catalog artifacts, or run the package audit for paths excluded from
application/package behavior. It
intentionally skips Catalog Contracts, Build, Unit Tests, Integration Tests,
Architecture & Publish, Coverage Policy, both migration checks, every component
lane, and Coverage. Reduced mode is the only plan in which the never-component-scoped
gates do not run, because no executable contract is affected. Documentation-only reduced pull requests also skip
Deployment Contracts. `Required CI` accepts those skipped results only when
classification and Quality succeeded, the deployment plan is explicitly `false`,
and the plan selects neither the complete matrix nor any component lane.

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
rejected in either plan. The same derivation applies to every other gate: a job
that is `success` when the plan expected `skipped` is rejected exactly like a
job that is `skipped` when the plan expected `success`, so neither a stale plan
nor an edited job condition can turn an unselected lane into a silent pass.

Pushes to `main` or `release/**` and manual dispatches always use the complete
solution matrix and exhaustive deployment contracts.

## Component Selection

Full mode is refined by six further classifier outputs. `complete` selects the
solution-wide Unit Tests, Integration Tests, and aggregate Coverage jobs;
`shared`, `cameraagent`, `logichost`, `combined`, and `delivery` select the
component lanes. A lane runs only when `complete` is `false`, so the complete
matrix and the lanes never duplicate the same evidence, and `Required CI`
derives each job's expected result from that plan rather than from a hard-coded
list.

Six gates are deliberately never component-scoped and run for every full-mode
head: **Catalog Contracts** (the exact production catalog bundle both the Unit
job and the Delivery lane consume), **Build** (warning-clean solution builds and
the behavioral category audit), **Architecture & Publish** (repository graph,
provider boundary, host publish evidence, and both category selections of the
architecture project), **Coverage Policy** (baseline non-regression), and both
**CameraAgent Migrations** and **LogicHost Migrations**. The migration gate is
split per host so each failure is attributable to one EF context, but neither
half is component-selected: a delivery-only or documentation-adjacent plan still
runs both. **Quality** already runs in every mode and keeps the package
vulnerability/deprecation audit on every full-mode head.

The closed component map is keyed on the exact project directory segment, so
sibling names such as `HVO.SkyMonitor.CameraAgent.Tests` and
`HVO.SkyMonitor.CameraAgent.LogicHost.Tests` can never match by prefix:

| Component | Owned paths |
| --- | --- |
| `shared` | `src/` and `tests/` project directories for `AgentCore`, `Astronomy`, `Imaging`, `Processing`, `ProcessingRunner`, `ProcessingRunner.Contracts`, `Catalog.Sqlite`, `Common`, `Fleet.Contracts`, and `TestSupport` |
| `cameraagent` | `src/HVO.SkyMonitor.CameraAgent`, `CameraAgent.Common`, `CameraAgent.Modules.Zwo`, `CameraAgent.Replay`, `CameraAgent.ReplayRunner`, and `tests/HVO.SkyMonitor.CameraAgent.{Tests,AcceptanceTests,IntegrationTests}` |
| `logichost` | `src/HVO.SkyMonitor.LogicHost` and `tests/HVO.SkyMonitor.LogicHost.{Tests,IntegrationTests,TestInfrastructure}` |
| `combined` | `tests/HVO.SkyMonitor.CameraAgent.LogicHost.{Tests,IntegrationTests}` |
| `delivery` | `src/HVO.SkyMonitor.Deployment.*`, `tools/HVO.SkyMonitor.Deployment.ReleaseTool`, and `tests/HVO.SkyMonitor.Deployment.*.Tests` |

Deployment inputs that are not project directories — `deploy/**`,
`docker-compose.apps.yml`, `.env.template`, `scripts/deploy*`,
`scripts/catalog*`, `scripts/install-hvo-skymonitor.sh`,
`scripts/infra:operation-lock`, `scripts/test:deploy*`,
`scripts/test:deployment-*`, the HYG v42 attribution and license inputs, and
`tests/fixtures/catalog/hyg-v42-bright-stars.sqlite` — are deliberately **not**
mapped to the delivery component. Test code reads several of them from the
repository root at run time, which no project file records: for example
`SqlOperationsIssue255Tests` and `DatabaseInitializationAcceptanceTests` read and
assert `deploy/sql/*.sql`, and seven test projects across every component load
the catalog fixture. Narrowing them would silently drop the suites that assert
them, so they keep their independent deployment-contract selection and otherwise
select the complete matrix, exactly as before component scoping.

The classification rules applied to that map are:

- A shared contract change selects `shared` plus every consuming lane:
  `cameraagent`, `logichost`, `combined`, and `delivery`. Delivery is included
  because `HVO.SkyMonitor.Deployment.Cli` and the release tool consume
  `AgentCore` and `Catalog.Sqlite`.
- A CameraAgent change selects `cameraagent`, and adds `combined` when the path
  can change an exported protocol or integration seam.
- A LogicHost change selects `logichost`, and adds `combined` under the same
  seam rule.
- A combined fixture change selects `combined` plus both host lanes.
- A delivery change selects `delivery`; deployment-contract selection stays
  independent and still runs the Deployment Contracts gate.
- CI, coverage, package, architecture, classifier, toolchain, and solution
  inputs select the complete matrix, and so does any path outside every
  component boundary. **The default is the complete matrix**, so a new
  top-level directory or a new project fails closed rather than silently
  running a narrow plan.
- Documentation-only pull requests stay in reduced mode only through the
  existing allowlist, and only while no allowlisted path is compiled, embedded,
  or copied into a project. Seven allowlisted paths are included by project
  files today — `THIRD-PARTY-NOTICES.md` and six `docs/validation/*.json`
  signal manifests — and each leaves reduced mode for the complete matrix. The
  remaining 234 allowlisted paths still classify reduced.

Seam selection is an inverse allowlist. A host path is treated as an exported
protocol or integration seam unless it is explicitly host-private, so a new host
directory fails closed into combined coverage. The host-private set is:

- `src/HVO.SkyMonitor.CameraAgent/{Components,Properties,wwwroot}/**`, except `Components/Account/**`, which wires the identity endpoints the combined suite exercises, and `Properties/AssemblyInfo.cs`, whose `InternalsVisibleTo` grants are what let the combined suites compile
- `src/HVO.SkyMonitor.CameraAgent.Common/{Background,Diagnostics,Frames,Gallery,Imaging,Logging,Properties,Reflection,Scheduling,SkyMap,Storage}/**`, except `Properties/AssemblyInfo.cs`, whose `InternalsVisibleTo` grant is what lets `CameraAgent.LogicHost.Tests` compile
- `src/HVO.SkyMonitor.CameraAgent.{Modules.Zwo,Replay,ReplayRunner}/**`
- `src/HVO.SkyMonitor.LogicHost/{Components,Properties,wwwroot}/**`, with the same `Components/Account/**` and `Properties/AssemblyInfo.cs` exceptions
- every `tests/**` path except `tests/HVO.SkyMonitor.LogicHost.TestInfrastructure/**`, which is the non-test fixture project both the LogicHost and combined suites compose

Everything else inside a host project, including its project root files,
`Configuration`, `Controllers`, `Endpoints`, `Http`, `Authentication`,
`Authorization`, `Security`, `Services`, `Data`, `Hosting`, `Infrastructure`,
`Middleware`, `Models`, `Capture`, `Environmental`, `Transients`, `Modules`,
`Fleet`, `Upload`, `Deployment`, `DeploymentLocation`, `Operations`, `Options`,
`RawIngress`, `Telemetry`, and `DependencyInjection`, adds the combined lane.

The host-private set is not merely asserted. `scripts/test:ci-classification`
extracts every `HVO.SkyMonitor.CameraAgent.Common.*` and
`HVO.SkyMonitor.LogicHost.*` reference from the two combined test projects —
plain, `global`, `static`, aliased, and fully qualified alike — and fails when
any namespace those suites reference maps to a host-private directory, so the
list cannot drift away from what the combined lane actually guards. The check
covers the `HVO.SkyMonitor.CameraAgent`, `HVO.SkyMonitor.CameraAgent.Common`, and
`HVO.SkyMonitor.LogicHost` namespace roots; a host-private file that contributes
to a combined behavior through some other namespace, such as an ASP.NET routing
extension, still needs a deliberate carve-out like `Components/Account/**`. A
second cross-check covers the assembly-visibility graph: any file granting
`InternalsVisibleTo` to a combined suite must select the combined lane, which is
why `Properties/AssemblyInfo.cs` is carved out of the host-private set.

Directory ownership is not the only way a source reaches a project. A file that
another project compiles, embeds, or copies through a relative MSBuild item —
`<Compile Include>`, `<None Include>` with `CopyToOutputDirectory`, and the rest —
also selects the including project's component, wherever in the repository it
lives. `tests/HVO.SkyMonitor.CameraAgent.IntegrationTests/VirtualSkyPipelineTests.cs`
therefore selects the combined lane because the combined integration project
compiles it, and `THIRD-PARTY-NOTICES.md` and `docs/validation/*.json` leave
reduced mode because shipping and test projects copy them.

Link derivation only ever **widens** a plan. A path outside every project
directory still selects the complete matrix, because test code also reads
repository-root files at run time in ways no project file records. The
classifier derives links from the checked-out project files rather than a list,
and skips project references, which the component map already models, and
wildcard includes, whose directories the contract test proves already select the
complete matrix. `scripts/test:ci-classification` copies the real project files
into its fixture repository and asserts that every resolvable link selects at
least what a file in the including project selects.

### Exact Affected-Gate Selection

Every full-mode row below additionally runs the six never-component-scoped
gates: Catalog Contracts, Build, Architecture & Publish, Coverage Policy,
CameraAgent Migrations, and LogicHost Migrations. Quality runs in every mode,
including reduced. The table records only what varies.

| Change | Complete | Lanes | Deployment Contracts |
| --- | --- | --- | --- |
| Allowlisted documentation only | no | none | no — and every full-mode gate above is skipped too; only Quality and Required CI run |
| Shared library or shared test | no | shared, cameraagent, logichost, combined, delivery | no |
| CameraAgent UI, imaging, `Modules.Zwo`, storage, gallery, scheduling, or replay code | no | cameraagent | no |
| CameraAgent seam (`Capture`, `Environmental`, `Transients`, `Modules`, `Fleet`, `Upload`, controllers, identity, configuration) | no | cameraagent, combined | no |
| LogicHost UI code | no | logichost | no |
| LogicHost seam (controllers, services, data, infrastructure) | no | logichost, combined | no |
| `tests/HVO.SkyMonitor.LogicHost.TestInfrastructure/**` | no | logichost, combined | no |
| Combined fixture or combined suite | no | cameraagent, logichost, combined | no |
| `src/HVO.SkyMonitor.Deployment.*`, the release tool, or their tests | no | delivery | yes |
| `deploy/**`, `scripts/deploy*`, `scripts/catalog*`, or the other non-project deployment inputs | yes | every lane claimed by the complete matrix | yes |
| `THIRD-PARTY-NOTICES.md` or a `docs/validation/*.json` a project copies | yes | every lane claimed by the complete matrix | no |
| `docs/catalog/hyg-v42-attribution.md` or `hyg-v42-license.md` | no | delivery | no |
| CameraAgent or LogicHost `Dockerfile` or host configuration sample | no | that host, combined | yes |
| CI, classifier, coverage, package, toolchain, solution, or architecture-test input | yes | every lane claimed by the complete matrix | yes for the deployment-contract inputs; the rest run every other gate |
| `tests/fixtures/**`, including the shared HYG v42 catalog fixture | yes | every lane claimed by the complete matrix | yes for the HYG v42 fixture |
| Deleted, renamed, type-changed, missing, empty, or unclassifiable path | yes | every lane claimed by the complete matrix | yes |
| Push to `main`/`release/**` or manual dispatch | yes | every lane claimed by the complete matrix | yes |

The complete matrix additionally runs Unit Tests, Integration Tests, and the
aggregate Coverage rollup, which every component plan skips in favour of its
lanes.

Inspect the exact plan for any two commits without pushing:

```bash
./scripts/ci:classify pull_request <base-sha> <head-sha>
```

Reproduce one lane locally with the same scripts CI runs:

```bash
dotnet tool restore
./scripts/ci:component-build cameraagent
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --no-build --configuration Release --filter "TestCategory=Unit&TestCategory!=Integration&TestCategory!=Manual&TestCategory!=Soak&TestCategory!=External&TestCategory!=Hardware" --settings tests/coverage.runsettings --collect:"XPlat Code Coverage" --results-directory TestResults/unit/cameraagent --logger "trx;LogFileName=unit-cameraagent.trx"
./scripts/ci:component-publish cameraagent
./scripts/coverage:component cameraagent
```

Use the exact per-project `dotnet test` invocations in `.github/workflows/ci.yml`
for the remaining slots of the selected lane; `scripts/coverage:component
--list-slots <component>` prints the exact result directories a lane must
produce, and the lane fails when a slot is missing, duplicated, or borrowed from
an unselected lane. The split migration checks and the coverage policy have
direct local equivalents:

```bash
./scripts/ci:canonical-migration cameraagent
./scripts/ci:canonical-migration logichost
./scripts/coverage:policy pull_request main
```

Component baselines live beside the aggregate baseline as
`scripts/coverage/baseline.<component>.json`. Each one carries its own aggregate
rate over the files its lane observes plus its own critical-file floors, so a
regression in one component cannot be masked by another component's coverage.

A lane merges fewer reports than the aggregate rollup, so its measured rates are
its own. To keep component gating from relaxing risk coverage, `Coverage Policy`
requires that every file floor in `scripts/coverage/baseline.json` is matched or
exceeded by the component baseline that a change to that file selects. A
component floor may sit below the aggregate only when the lane provably cannot
reach it, and only when the entry records a `belowAggregateReason`. One entry
does today: `HVO.SkyMonitor.AgentCore/ArtifactManifestV2.cs` reaches 0.90 branch
coverage only in the union of every report, so the shared lane records 0.85 with
that reason while the aggregate rollup keeps enforcing 0.90 on `main`.

`TestResults/unit/processing-runner` is deliberately not a coverage slot: the
aggregate rollup has never counted it, and changing the aggregate slot set is
outside component gating. The Shared Libraries lane still runs that suite as a
gate; only its coverage report is discarded, exactly as the Unit job does today.

## Failure Triage

- Formatting or package failures name the command, package, version, project, and reviewed allowlist status.
- Category failures name uncategorized, multiply categorized, unknown, or count-drifted tests.
- Migration failures identify the host context with pending model changes.
- Coverage failures name exact source paths, covered/valid counts, observed rates, and required floors.
- Integration failures retain separate project/category TRX and Cobertura evidence for 30 days.
- Deployment failures identify the failed shard in coordinator output; inspect the retained shard log and status marker before rerunning only that shard locally.
