# #1041 correction after b3423622

Refs #1041. Implementation and validation in progress. Kepler supplied Sol
7393d8b2 (COMPLETE, Lows only) and Opus 27a3f0ec (COMPLETE, CHANGES REQUESTED,
three Medium findings). No local CLEAN claim is made. Kepler owns re-review and
PR creation; this coding thread makes no GitHub writes except its authorized
normal branch push.

The original [R1–R9 crosswalk](issue-1041-run3-high-corrections.md), original issue
and discussion, Roy's Option A approval and #1559/#1591/#1618 attribution remain
governing. The only approved retained-file detection tradeoff remains out-of-band
tampering at the next full scrub. Current identity/readiness, managed mutation
proofs, schema/location checks, pre-mutation comparisons and #1042's shared finite
120-second verification budget remain required.

| Finding | Behavior | Baseline proof / planned candidate evidence |
| --- | --- | --- |
| M-1 | Ordinary configuration loads wait cancellably for the store gate and managed-write barrier. Protected verification retains its finite local waits and refusal semantics. | Both >1s contention cases fail on unchanged b3423622; fixed focused cases and caller-cancellation cases pass. Real continuity HTTP regression and affected suites pending final candidate. |
| M-2 | Unchanged ordinary loads validate fresh protected state without certifying a dirty ledger. They request the host-owned coalesced scrub. Cooldown starts at completion, including a failed scrub longer than 30s; a cold startup is not immediately repeated. Startup/actual identity changes still fully reconcile. | Four b3423622 cases repeat scans for invalid JSON, drift, missing and damaged ledger. Fixed cases do zero inline scans, still refuse protected verification; host queue/cooldown regression passes. Final gates pending. |
| M-3 | Only an active rollback operation may recover its own authenticated S1 ledger after verifying its paired pre-ledger S0 and known protected-file transition. Generic missing-ledger restore still refuses. Current unchecked bytes never become expected proofs. New runtime must fully scrub. | Committed-upgrade/failed-old-image cases fail on b3423622. Fixed recovery, interrupted recovery/resume and unexpected-protected-edit refusal cases pass in CLI focus. Final gates pending. |
| L-b/L-c/L-d | Test sealed/non-object/unauthenticated/missing-key legacy refusal; document protected-history rewind; bounded config snapshot re-read on concurrent edit. | Negative legacy cases pass; bounded retry tests and final gate pending. L-a registration lease race and L4 retention continuation remain Kepler follow-ups. |

M-3 restores the stopped S1 expectations, not a newly adopted corpus. Protected
files must hash to their recorded S1 or S0 values, allowing interrupted paired
writes but refusing unrelated edits. Copy stages on the state filesystem,
verifies the immutable snapshot checksum, removes stale SQLite recovery files,
and syncs the rename. A crash before completion remains an active operation;
resume restores S1 before restarting it. Real retained changes remain subject to
full scrub; no restored checkpoint grants cross-process qualification.

Normal configuration availability does not mean installer readiness. The ordinary
path still freshly parses config, protected history/marker/manual state and checks
the current snapshot. A dirty ledger keeps installation verification refused.
Busy ordinary reads can wait as the baseline did, with caller cancellation;
the finite protected-read deadline is not raised or reused as an ordinary-load
exception. Continuously changing config has a bounded three-attempt re-read and
cannot become immutable installer identity.

Baseline regressions were built warning-clean against b3423622 with no product
source diff: six CameraAgent cases and two lifecycle cases failed as expected.
Their TRX/source hashes and commands are retained in local run-4 evidence.
Initial test setup compile errors were fixed before those behavioral runs.

Final merge, classifier-selected gates, exact SHA, counts and original A/C mapping
will be recorded here after validation. No claim of completion is made by this
draft. No live instance, production token/data, home-docker, network change,
CLIProxy restart, PR or deployment is authorized or performed.
