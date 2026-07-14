# CI Pipeline Runbook

This runbook describes the required current-head checks in `.github/workflows/ci.yml` and their local equivalents.

## Required Checks

| Check | Enforced behavior |
| --- | --- |
| **Quality** | Pinned local tools, formatting, vulnerability audit, and exact reviewed deprecation allowlist. |
| **Build** | Warning-clean Debug and Release builds plus complete, disjoint behavioral category discovery. |
| **Unit Tests** | 560 Unit cases with an intentionally invalid Docker endpoint and per-project TRX/Cobertura paths. |
| **Integration Tests** | 121 SQLite, filesystem, SQL Server, Redis, MinIO, Mailpit, and host integration cases. |
| **Architecture & Publish** | Six repository graph/MSBuild/publish checks plus retained host publish manifests. |
| **Migrations** | Zero pending CameraAgent or LogicHost EF model changes; current and legacy migration convergence remains in Integration Tests. |
| **Coverage** | Exact source-path and branch merge of ten expected reports, checked-in aggregate non-regression, and risk-file floors. |
| **Required CI** | Current-head aggregate that fails when any required check fails, times out, is canceled, or is missing. |

Each test invocation owns a category/project-specific result directory and TRX name. Coverage rejects any report count other than the expected ten, preventing missing or overwritten evidence.

## Categories

The category audit requires every discovered case to belong to exactly one primary behavioral category. Current discovery is `Unit=560`, `Integration=121`, `Manual=11`, `Soak=1`, `External=0`, and `Hardware=0`.

`External` is implemented by the pinned, networkless Stellarium workflow rather than an empty MSTest check. The accelerated `Soak` case and real-duration soak are independently selectable in `.github/workflows/cameraagent-soak.yml`. No Hardware check is published until real device tests and a suitable runner exist.

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

Use a fresh result root for every collection. Before merging, require exactly one report from each of the ten category/project slots as shown in `.github/workflows/ci.yml`; never merge every historical GUID directory under a reused result root. Merge those ten explicit reports once with the pinned ReportGenerator tool, then enforce and publish that same canonical result:

```bash
reports=(TestResults/unit/*/*/coverage.cobertura.xml TestResults/integration/*/*/coverage.cobertura.xml TestResults/architecture/*/coverage.cobertura.xml)
[[ "${#reports[@]}" -eq 10 ]]
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

## Failure Triage

- Formatting or package failures name the command, package, version, project, and reviewed allowlist status.
- Category failures name uncategorized, multiply categorized, unknown, or count-drifted tests.
- Migration failures identify the host context with pending model changes.
- Coverage failures name exact source paths, covered/valid counts, observed rates, and required floors.
- Integration failures retain separate project/category TRX and Cobertura evidence for 30 days.
