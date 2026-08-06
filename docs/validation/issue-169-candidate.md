# Issue 169 Candidate Evidence

## Boundary

Issue #169 bounds central artifact reconciliation scheduling contention to three
total attempts. Every retry creates a fresh service scope, reloads durable SQL
state, and executes the idempotent production scheduler. A successful pass after
contention records convergence; three marked conflicts record exhaustion and
retain the existing durable verification requeue and Error path.

## Correctness Workload

- SQL Server and MinIO integration fixture, Release configuration.
- One raw artifact initially blocked by a missing rig profile, then made
  eligible for reconciliation and the complete required derivative recipe set.
- Deterministic marked scheduler conflicts: stale row-version advancement for
  unresolved attempts, and a final competing production-scheduler commit before
  the second conflict in the durable-winner case.
- Converged case: two consecutive conflicts, three distinct DbContext scopes,
  exact unique required request identities, Available/Complete artifact, and no
  pending verification or Error log.
- Exhausted case: three consecutive conflicts, three distinct DbContext scopes,
  no derivative jobs, durable pending verification, one bounded Error, and no
  further attempt.

## Tier C Resource Disposition

- I/O: each injected retry adds one bounded SQL artifact update and one fresh
  reconciliation read; no object-store payload transfer is added after the
  first verification.
- CPU: no new algorithm or unbounded scan; retry work is capped at two retries.
- Memory: one service scope and DbContext are live per attempt and disposed
  before the next; queue and artifact cardinality are unchanged.
- Latency/throughput: the contention path can perform one additional scheduler
  attempt. This is intentional bounded recovery work and does not affect the
  no-conflict path.
- Backlog: successful third-attempt convergence avoids a false verification
  requeue. Exhaustion preserves the existing durable retry marker and delay.
- Result: deterministic acceptance proves both successful repeated contention
  and genuine bounded exhaustion. No comparative performance harness applies
  because this fixes an exceptional recovery path without changing the normal
  scheduling hot path.

## Validation

Candidate source: `origin/main@1c273974f0bf1c948ca413dc03e8ea1b840f1a2d`
plus the uncommitted issue #169 diff.

- `dotnet tool restore` and `dotnet restore`: passed.
- Debug and Release solution builds with `-warnaserror`: passed with zero
  warnings and errors.
- `dotnet format ... --verify-no-changes`: passed after the complete restore.
- `./scripts/package:audit`: passed; no vulnerabilities and 12 exact
  deprecated package occurrences reviewed.
- Test-category audit: passed with Unit 1,722, Integration 524, Manual 73,
  Soak 1, External 0, and Hardware 1.
- Reconciliation-focused Integration selection: 10/10 passed.
- Complete Unit selection with an invalid Docker endpoint: 1,722/1,722 passed.
- Complete Integration selection: 524/524 passed, comprising CameraAgent
  storage 145, standalone 4, LogicHost 351, LogicHost unit-project integration
  6, and CameraAgent host 18.
- Architecture boundary selection: 6/6 passed.
- CameraAgent and LogicHost pending-model checks: passed.
- Canonical twelve-report coverage gate: 84.3089% line (73,933/87,693) and
  69.7324% branch (28,243/40,502); all aggregate and risk-file floors passed.
- Independent review found one completed-metric overcount and one
  concurrent-winner test gap. Both were corrected; the focused and complete
  affected gates above validate the corrected boundary. Final independent code
  review found no correctness issues; one evidence-ordering wording correction
  was applied without changing executable behavior.
