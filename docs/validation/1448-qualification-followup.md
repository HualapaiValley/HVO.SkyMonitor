# #1448 qualification follow-up

PR #1689 remains a draft targeting `development/v1`. Full qualification is
separately authorized by Kepler; a draft's skipped Build and Unit check is not
qualification evidence.

## Run 41945710353, head 11a8a5ae

Unit, Deployment Contracts, Quality, Build, architecture/publish, both migration
jobs, catalog contracts and coverage policy passed. Integration failed: the
LogicHost assembly completed with 589 passed and three failed cases. The other
Integration lane passed. Aggregate coverage was skipped because Integration
failed. The previous entitlement/runner fixture reset timeouts did not recur.

- `GenerationCheck_HoldsObjectFencesWithoutSqlTransactionAndRejectsChangedSource`
  timed out at concurrency 1 before entering the generation callback. Earlier
  stale-source and object-replacement checks passed. Its wait previously masked
  early HTTP completion or request faults as a barrier timeout. Offline probes
  reproduce that diagnostic defect; an early HTTP 425 is a reproducer, not the
  established response from CI.
- `Http_FaithfulLargeHistoryReadsIndexedBoundedRowsAndConcurrentLifecycleAppend`
  returned exactly 51 rows through the intended fact index seek, but its
  `ActualRowsRead` was 144 rather than 51. The assertion preceded plan logging,
  so the failed-context plan was lost. Ordered key-lookup prefetch is a plausible
  explanation, not a demonstrated cause. No larger physical-read bound is
  justified by this artifact alone.
- `Runner_CancellationExpiryAndStalenessRecoverSafely` returned HTTP 500 while
  reporting `object.missing` on reclaimed attempt 2. TRX run-level stdout records
  SQL error -2 after 30,001 ms in `ArtifactIngestService.InvalidateDependentsAsync`
  at the query including attempts, input requirements and inputs. The query has
  a sibling-collection multiplication risk, but the known job itself has only
  two attempts, one requirement and one input. No runtime plan, wait or blocker
  evidence establishes why it timed out.

## Evidence correction and next decision

The fence wait now races callback entry against request completion and preserves
request faults/cancellation or reports early HTTP status. Timeouts include entry,
verification and request-state counts. Cleanup has a 30-second drain bound and
retains disposal continuations. Docker-free regressions cover rejection, faults,
cancellation, timeout evidence and successful entry at concurrency 1, 4 and 8.

The proposal test logs its replay SQL, returned-row count and raw runtime plan
before checking the physical-read bound. Both affected long waits receive at
most three read-only SQL session samples, each bounded to two seconds, containing
session IDs, transaction counts, waits, blockers, CPU/read counters and command
hashes. These samples contain no SQL text, parameter values or credentials.
They do not reactivate the historical #1274 reset diagnostic collector.

The application's query behavior, SQL timeout, fence timeout and physical-read
assertions remain unchanged. This is an evidence repair, not a claim that all
three underlying failures are fixed or harmless flakes. A reviewed qualification
rerun must either pass the existing gates or provide the missing evidence for a
specific correction. Do not infer a production trigger defect, host-admin need,
or a safe replacement row threshold from elapsed time alone.
