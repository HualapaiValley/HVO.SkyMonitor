# Issue 290 Candidate Evidence

## Boundary

Issue #290 adds an explicit completion boundary between local Identity owner
reconciliation and standalone acceptance-host readiness. Production startup
completes the singleton boundary only after the SQLite migration, configured
owner metadata/password reconciliation, stale-owner demotion, and file
permission enforcement all succeed. Initialization failures fault the boundary
and still fail host startup.

The standalone Kestrel fixture awaits that boundary before exposing the host.
It does not poll authorization, retry HTTP requests, sleep, weaken the owner
policy, or add any central-service dependency.

## Correctness Evidence

- Unit coverage proves the boundary remains incomplete before publication,
  completes exactly through the success signal, propagates initialization
  failure, and preserves caller cancellation without reporting readiness.
- The exact formerly intermittent standalone acceptance test immediately signs
  in the configured owner and requires HTTP success from
  `/api/v1/operations/environmental/sources` after fixture startup.
- Existing authorization coverage continues to require the configured persisted
  site owner and returns HTTP 403 for authenticated non-owners.
- The same readiness boundary is applied on standalone fixture restart because
  each restart creates a new host and singleton initialization state.

## Tier B Disposition

This is an isolated test-host lifecycle synchronization correction. It changes
no durable schema, owner authorization rule, public API, environmental workflow,
or normal executable startup ordering. Focused unit, repeated exact acceptance,
the full CameraAgent acceptance Integration selection, affected builds, format,
and protected CI provide the candidate evidence.

## Validation

Candidate source: `origin/main@12411e27bb296203ae6e93d9267625e5adbea4d1`
plus the uncommitted issue #290 diff.

- Pinned tool and solution restore: passed.
- Warning-clean Debug affected builds and Release solution build: passed.
- Formatting verification: passed.
- Package audit: passed with no vulnerabilities and 12 exact deprecated
  package occurrences reviewed.
- Category audit: passed with Unit 1,725, Integration 524, Manual 73, Soak 1,
  External 0, and Hardware 1.
- Identity initialization unit tests: 3/3 passed.
- Formerly intermittent exact standalone test: passed once during the inner
  loop, five consecutive focused runs, and once after restart authorization was
  added.
- Full CameraAgent Acceptance Integration selection: 4/4 passed.
- Owner authorization integration selection: 5/5 passed, preserving configured
  owner success and authenticated non-owner HTTP 403 behavior.
- Complete CameraAgent Unit shard with an invalid Docker endpoint: 1,026/1,026
  passed.
- Complete CameraAgent host Integration shard: 18/18 passed.
- Independent review found no success-path or security defect. Its startup
  failure wiring gap was corrected by moving completion/failure publication
  into the tested `RunAsync` boundary; documented category totals were also
  synchronized.
