# Daily-summary maintenance follow-ups: L1-F1 and L1-F2

This is an explicitly authorized partial slice of [#1764](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1764).
The tracker and audit parent #1831 remain open. The original requirements are
the [two reviewer findings](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1764#issuecomment-6848053542)
and their [original reproduction](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1816#pullrequestreview-5888185082).
The original #1764 body and all nine pre-claim comments were read on 2026-11-13;
the [released prior claim](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1764#issuecomment-6858845286)
and [new scoped claim](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1764#issuecomment-6861707730)
establish ownership. The original [#1713 contract](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1713)
and [still-product runbook](../runbooks/cameraagent-still-products.md) govern
immutable publication, exact-period links and fail-closed verification.

## Scope and behavior

An `InvalidDataException` or `IOException` while listing a report date for
maintenance now leaves the completed still run's outcome intact and records an
explicit skipped-refresh note. It cannot produce a summary from a truncated
listing. Cancellation still propagates. A failure while refreshing an identified
retained summary still fails the occurrence with its existing retry instruction;
its note now retains publication, reuse and unchanged counts accumulated before
the failure, including any work reported by the failing evaluation.

Still-product counts and summary counts remain separate. Reopening the stores
preserves historical attempt details and immutable files. A retry verifies
completed summaries as unchanged, reuses a published output if its pointer did
not advance, and publishes only the remaining summary.

No persistent contract, scheduler, backend, candidate bound, coverage basis,
source selection, link-selection policy or corruption policy changes. L1/L7/L8
were already delivered. L2/L3 documented limits, L4/L5 time-lapse/event decisions,
N1 redesign and browser/focus/Tab follow-ups remain outside this slice. Existing
Kepler, WebSite, #1016/#1110, #1179 and #1242 ownership is unchanged.

## Requirement evidence

All tests below belong to `DailySummaryIntegrationTests`; the new cases live in
`DailySummaryIntegrationTests.Maintenance.cs`. They use synthetic 16×16 Mono8
frames, the real nightly SQLite/file store, real automation SQLite history,
`LocalAutomationRunnerService`, and the production nightly task adapter. The
test registry forwards to that adapter; only unrelated capture input and
selected source-restoration failures are controlled fixtures.

| Requirement/case | Behavioral evidence |
| --- | --- |
| F1: real bounded-list overflow, successful own work, honest history | `ListingOverflowKeepsSuccessfulStillOutcomeAndExplainsSkippedMaintenance`, keogram and star-trail rows: five hourly generations publish 1,030 real products without maintenance. A real day read throws the bounded-list exception. The sunrise-day runner then publishes eight products, records `Succeeded` attempt 1 and an explicit skipped-refresh note. |
| F1: restart, no wasted retry, immutable own output | The same two rows reopen both stores, sweep the runner, retain the succeeded attempt, reject retrying successful work, explicitly re-evaluate the retained occurrence without publication, and verify its final product and unchanged provenance. |
| F2: original two-summary partial failure and retry | `PartialRefreshHistoryRetainsCompletedCountsAcrossFailureAndRestart("recover")`: two original summaries become three after the second refresh fails. Attempt 1 records the first publication and retry instruction. A fresh runner/store retries through `RetryAsync`; attempt 2 publishes the remaining summary, reports one unchanged, and leaves four immutable summary products. Both current summaries link the actual still final. |
| F2: unchanged counts before another failure | The `"unchanged"` row adds a second failed attempt: the first summary is unchanged and the second still fails. Its history retains that count. Attempt 3 completes the remaining work. |
| F2: publication-before-pointer recovery and reuse counts | The `"reused"` row restores the previous window pointer through the real store API while retaining the published output, recreating the existing crash boundary. Attempt 2 reuses that output before a later failure and records one reuse; attempt 3 converges. No immutable product is rewritten. |
| Normal ordering, period/preset interactions | Existing `AutomationOrderRefreshesSummaryWithoutAnExplicitSummaryRetry` and `ChangedPresetSkipsHistoricalSummaryAndRefreshesOnlyExecutableSummary` cover both still kinds and order/preset combinations. |
| Failure/cancellation, restart and last-good preservation | Existing `InterruptedAutomaticRefreshRecoversOnUnchangedStillRetry` and `InterruptedRefreshPreservesPublishedSummaryAndRecoversAfterRestart` cover I/O failure and cancellation, then fresh-store recovery. No cancellation exception is converted to success. |
| Negative input and accepted fail-closed corruption | Existing `UnverifiedLinkedProductKeepsSummaryFailClosedAndRetriesWithContext`, `EmptyDayRecordsNotProducedAndCorruptRetryFailsWithoutSubstitution` and `AllExcludedDayRetainsFinalDiagnosticsWithoutProducingAnArtifact` retain failure/empty/excluded behavior. |
| Durable contract and outputs | New rows verify stored attempts, product counts, current pointers, linked identity, publication checksums and old provenance bytes. Existing schema-preservation/rollback tests remain in the focused suite. No migration is introduced. |

Fail-first on baseline `b961cd6a4dca75bba606cd4b66674983d3312320`: all five
new rows failed (zero passed/skipped), specifically at the expected outcome or
missing-count assertions. With the implementation, the complete focused summary
selection passed **24/24, zero failures/skips**. Commands:

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.IntegrationTests/HVO.SkyMonitor.CameraAgent.IntegrationTests.csproj --configuration Release --filter 'TestCategory=Integration&(FullyQualifiedName~ListingOverflowKeepsSuccessfulStillOutcomeAndExplainsSkippedMaintenance|FullyQualifiedName~PartialRefreshHistoryRetainsCompletedCountsAcrossFailureAndRestart)' -nodeReuse:false -p:UseSharedCompilation=false --logger trx
dotnet test tests/HVO.SkyMonitor.CameraAgent.IntegrationTests/HVO.SkyMonitor.CameraAgent.IntegrationTests.csproj --no-restore --configuration Release --filter 'TestCategory=Integration&FullyQualifiedName~DailySummaryIntegrationTests' -nodeReuse:false -p:UseSharedCompilation=false --logger trx
```

## Candidate validation and limits

Tier **C / HIGH / deep**, target `development/v1` at the baseline above. The
protected profile is **Development v1 / Preflight** plus **Build and Unit**.
The local candidate gate and canonical coverage are required independently;
classifier `complete=true` never authorizes a full remote workflow dispatch.
Exact candidate SHA, classifier output, commands, exit codes, TRX counts,
coverage inputs/results, reviewer requests/reports and source snapshots are
retained in `/home/roys/.t3/audits/issue-1764-130b2945/` on `home-dev-03` and
linked from the issue handoff. This document's focused results are not a claim
that the full candidate gate or independent review has passed.

The bounded correctness workload is serial, with 205 tiny source frames for
each overflow row and one source for each two-summary row. The focused suite
took 24 seconds on native Linux x64, SDK 10.0.402, Release. This is a reporting
correction, not a throughput optimization: no success-path query, image
algorithm, file synchronization, transaction, queue or retention bound changes.
The only added work is bounded note construction and exception isolation.
Large-frame throughput, hardware memory/thermal measurements and production
cadence are N/A because those paths are unchanged; no physical qualification is
claimed. The overflow limit remains 1,024 and is tested directly, not bypassed.

Runtime evidence is the actual persisted automation history and converged
immutable output. No metric, tracing or health contract changes; new metric
labels, spans and health transitions are N/A. A failed identified refresh
remains retryable, while an unlistable date cannot turn successful own work into
an exception failure. Operator detail is checked against its existing 512-byte
ASCII bound. No browser, network/host configuration or production operation is
part of this implementation.

Roy's superseding authorization permits the required independent Sol review via
`codex_cliproxy / gpt-6.1-sol / reasoningEffort=xhigh, serviceTier=priority` and
Opus via `claude_cliproxy / claude-opus-5-5 / effort=xhigh, fastMode=true`.
The disabled direct Claude route is excluded. Requested settings and catalog
availability are not execution evidence; actual model/effort, same-candidate
completeness and CLEAN verdicts must be recorded before Kepler readiness.
