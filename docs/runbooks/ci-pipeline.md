# CI Pipeline Runbook

This runbook describes the required current-head checks in `.github/workflows/ci.yml` and their local equivalents.

## Required Checks

| Check | Enforced behavior |
| --- | --- |
| **Change Classification** | Fail-closed selection of the full matrix for pushes and behavior-affecting pull requests or reduced mode for explicitly allowlisted documentation/developer-environment pull requests. |
| **Quality** | Pinned local tools, formatting, vulnerability audit, and exact reviewed deprecation allowlist. |
| **Build** | Warning-clean Debug and Release builds plus complete, disjoint behavioral category discovery. Skipped only in classified reduced mode. |
| **Unit Tests** | 1731 Unit cases with an intentionally invalid Docker endpoint and per-project TRX/Cobertura paths. Skipped only in classified reduced mode. |
| **Integration Tests** | 524 SQLite, filesystem, SQL Server, Redis, MinIO, Mailpit, forwarded-header, and host integration cases; the remaining six Integration-category cases run in Architecture & Publish. Skipped only in classified reduced mode. |
| **Architecture & Publish** | Six Integration-category repository graph/MSBuild/publish cases plus retained host publish manifests. |
| **Migrations** | Zero pending CameraAgent or LogicHost EF model changes; current and legacy migration convergence remains in Integration Tests. |
| **Coverage** | Exact source-path and branch merge of twelve expected reports, checked-in aggregate non-regression, and risk-file floors. |
| **Required CI** | Current-head aggregate that fails when any expected check fails, times out, is canceled, is missing, or is unexpectedly skipped or run for the selected mode. |

Each test invocation owns a category/project-specific result directory and TRX name. Coverage rejects any report count other than the expected twelve, preventing missing or overwritten evidence.

## Categories

The category audit requires every discovered case to belong to exactly one primary behavioral category. Current discovery is `Unit=1731`, `Integration=528`, `Manual=75`, `Soak=1`, `External=0`, and `Hardware=1`.

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

## Reduced Pull Request Mode

The workflow always triggers for pull requests. A lightweight classifier uses the pull request's base and head commits and selects reduced mode only when every changed path is an added or modified member of this allowlist:

- `docs/**`, except the production bundle inputs `docs/catalog/hyg-v42-attribution.md` and `docs/catalog/hyg-v42-license.md`
- `.devcontainer/**`
- `.vscode/**`
- `.github/prompts/**`
- `README.md`, `AGENTS.md`, and `THIRD-PARTY-NOTICES.md`
- `.github/copilot-instructions.md` and `.github/pull_request_template.md`
- `deploy/hvo-docker/README.md` and `tools/asi-capture/README.md`
- one-level `src/*/README.md` and `tests/*/README.md`
- `tests/fixtures/catalog/SOURCE.md` and `tests/fixtures/stellarium/SIMBAD_ENDPOINTS.md`
- `scripts/opencode:enable`, `scripts/opencode:disable`, `scripts/opencode:connect`, `scripts/opencode:prepare-rebuild`, `scripts/opencode:remote-connect`, and `scripts/test:opencode`

Reduced mode still runs **Quality** and **Required CI**. It intentionally skips Build, Unit Tests, Integration Tests, Architecture & Publish, Migrations, and Coverage. `Required CI` accepts those skipped results only when classification succeeded in reduced pull-request mode. This preserves the stable protected check while avoiding approximately 25 of the 30.4 aggregate runner-minutes observed in baseline run `29673206708`.

Pushes to `main` or `release/**` always use the full matrix. Deletions, renames, type changes, symlinks or other non-regular entries, empty diffs, unavailable commits, failed diffs, workflow/build/package/runtime/deployment changes, general scripts, product code, tests, migrations, production catalog bundle inputs, `.env.template`, and every unknown path also use the full matrix. Add or modify any non-allowlisted path to force full CI when extra evidence is desired.

## Failure Triage

- Formatting or package failures name the command, package, version, project, and reviewed allowlist status.
- Category failures name uncategorized, multiply categorized, unknown, or count-drifted tests.
- Migration failures identify the host context with pending model changes.
- Coverage failures name exact source paths, covered/valid counts, observed rates, and required floors.
- Integration failures retain separate project/category TRX and Cobertura evidence for 30 days.
