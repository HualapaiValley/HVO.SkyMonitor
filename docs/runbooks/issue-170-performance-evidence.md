# Issue 170 Performance Evidence

This runbook records the historical issue #170 campaign. Issue #457 retired its
unreleased EF migration and backfill workload. Reproduce the reviewed historical
summary only from the cited revisions; current authority measurements start from
the canonical empty-database baseline and do not claim predecessor-schema
compatibility.

Run every command sequentially on one otherwise idle machine. The harness holds `/tmp/hvo-issue-170-performance.lock` for each test process and rejects concurrent evidence processes, but operators must also avoid concurrent builds, Testcontainers tests, and application workloads.

## Revisions

Set these full commit SHAs before collection:

```bash
export BASELINE_PRODUCTION=57098c0f04e88e2a1a85b3ac1b18a8b675fa6257
export BASELINE_HARNESS=<baseline-production-plus-harness-commit>
export CANDIDATE_PRODUCTION=<issue-170-production-commit>
export CANDIDATE_HARNESS=<candidate-production-plus-identical-comparable-harness-commit>
export SUMMARY_REVISION=<clean-descendant-summary-commit>
```

The baseline and candidate harness commits must differ from their production commits only in the harness files declared by `Issue170PerformanceEvidence`. The comparable harness files must be byte-identical. Both worktrees must be clean; use a named baseline evidence branch rather than an unreachable detached commit.

## Build

In each worktree, verify `HEAD` and build clean Release assemblies from that exact harness revision:

```bash
git status --short
git rev-parse HEAD
dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Release --no-incremental -warnaserror
```

Do not rebuild a worktree while trials from its current binaries are in progress.

## Trials

Run each command in a new process for trial values `1` through `5`. Run bootstrap and ingest in both worktrees. Run authority only in the candidate worktree.

```bash
DOTNET_gcServer=1 HVO_EVIDENCE_REVISION=<harness-commit> HVO_EVIDENCE_PRODUCTION_REVISION=<production-commit> HVO_EVIDENCE_TRIAL=<trial> dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --no-build --configuration Release --filter "FullyQualifiedName~DeviceBootstrapPerformanceTests.FreshRegistrationEnvelopeAndBootstrap_RecordsPerformanceEvidence"

DOTNET_gcServer=1 HVO_EVIDENCE_REVISION=<harness-commit> HVO_EVIDENCE_PRODUCTION_REVISION=<production-commit> HVO_EVIDENCE_TRIAL=<trial> dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --no-build --configuration Release --filter "FullyQualifiedName~LogicHostIngestPerformanceTests.NativeManifestV2Ingest_W1W2AndW4_RecordsPerformanceEvidence"

DOTNET_gcServer=1 HVO_EVIDENCE_REVISION=$CANDIDATE_HARNESS HVO_EVIDENCE_PRODUCTION_REVISION=$CANDIDATE_PRODUCTION HVO_EVIDENCE_TRIAL=<trial> dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --no-build --configuration Release --filter "FullyQualifiedName~DeploymentLocationAuthorityPerformanceTests.MigrationFleetReconciliationAndPaging_RecordPerformanceEvidence"
```

The authority filter above is the historical method name at the cited candidate
revision; it is intentionally not a current-tree test filter.

Each raw file records branch, clean state, harness and production commits, assembly hashes, process start/completion bounds, environment, workload, method, I/O, CPU, 100 ms sampled allocation-rate increments with boundary uncertainty, RSS, latency samples, throughput, backlog, and correctness. Copy the baseline `TestResults/issue-170/$BASELINE_HARNESS/` directory into the candidate worktree without changing its commit-scoped path.

## Summary

Run the summary from a clean descendant of the candidate harness revision after both five-trial sets are present. The descendant may change only allowlisted evidence/summary support and is recorded as `SummaryCommit`; the summary resolves every revision as a Git commit, verifies ancestry, rejects overlapping processes or harness drift, applies predeclared metric-specific budgets and N/A dispositions, and creates `docs/validation/issue-170-performance-summary.json`.

```bash
DOTNET_gcServer=1 HVO_EVIDENCE_REVISION=$SUMMARY_REVISION HVO_EVIDENCE_PRODUCTION_REVISION=$CANDIDATE_PRODUCTION HVO_EVIDENCE_TRIAL=1 HVO_EVIDENCE_CANDIDATE_REVISION=$CANDIDATE_HARNESS HVO_EVIDENCE_BASELINE_REVISION=$BASELINE_HARNESS HVO_EVIDENCE_CANDIDATE_PRODUCTION_REVISION=$CANDIDATE_PRODUCTION HVO_EVIDENCE_BASELINE_PRODUCTION_REVISION=$BASELINE_PRODUCTION dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --no-build --configuration Release --filter "FullyQualifiedName~Issue170PerformanceSummaryTests.FiveTrialEvidence_WritesDeterministicReviewedSummary"
```

Review every accepted regression and residual risk in the generated summary before committing it. Any unexplained budget failure blocks merge.
