# #1041 authority-path inspection correction

Refs #1041. SOL-M3 is the remaining original missing-authority defect identified
by [formal Sol review 5873458838](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1803#pullrequestreview-5873458838)
at `6af47032daee7c5c3ed2257bf97950a6644d16c0`. The complete review was read before
implementation. This corrects R2/R7 path inspection; the authenticated-history
guard and genuine legacy compatibility from [SOL-M2](issue-1041-sol-m2-correction.md)
remain intact. Kepler owns PR metadata and subsequent formal reviews.

## Behavior

Authority lookup now uses the existing Linux `statx` wrapper, which returns
absence only for ENOENT and throws for lookup failures such as EACCES or ENOTDIR.
Every existing component from the filesystem root is checked: ancestors must be
real directories, and authority files must be regular files. Symlinks and other
unexpected node types refuse. Missing components are accepted only after their
existing ancestors have passed inspection.

Historical snapshot verification checks the ledger, checkpoint and current
history through this inspection. Restore inventory validation checks all current
protected authority files and the ledger before returning a plan or replacing
stores. The ownership helper also validates its expected directory rather than
treating a non-directory or failed lookup as absence. Diagnostics contain a
constant refusal message, without the inspected path.

The guard performs metadata reads and authenticates readable history with the
existing keys. It generates no key, history, ledger or qualification proof and
does not repair permissions or remove evidence. The existing stopped-instance
contract and previously excluded reopen race are unchanged.

## Fail-first and passing-after proof

The real-agent, real-Data-Protection historical snapshot matrix now contains 18
independent plan/direct-restore cases. Direct restore uses a legitimate earlier
plan. Each negative asserts refusal, preserved database rows, preserved protected
history and identical current state-file fingerprints. Permission fixtures prove
a real kernel access denial and restore only their own mode in `finally`; they
also assert the product did not change that mode.

| Cases | Unchanged 6af47032 product source | Corrected focused run |
| --- | --- | --- |
| Directory at history: plan and direct restore | 2 expected failures; planning accepted and restore rewound 2 rows to 1 | 2 passed; refused without mutation |
| Regular file at `.location`: plan and direct restore | 2 expected failures with the same unsafe outcomes | 2 passed; refused without mutation |
| No search permission: plan and direct restore | Plan expected failure; restore already refused later, so its unchanged-state control passed | 2 passed; both refuse |
| Original readable sealed-history cases and genuine unsealed legacy controls | 4 passed | 4 passed |
| Tampered history, foreign key ring, missing key ring and authenticated non-object JSON, each plan/direct restore | 8 passed | 8 passed |
| **Total** | **13 passed / 5 expected failures / 0 skipped** | **18 passed / 0 failed / 0 skipped** |

The source binding confirms `git diff --quiet HEAD -- src` before the fail-first
build. All fixtures are disposable; keys generated to construct a foreign-key
negative belong only to that fixture, before the product operation and the state
fingerprint. No product operation may generate replacement keys or history.

Actual changed-project discovery found CameraAgent Unit=8,398 (+14). Inventories
and runbook totals are re-summed and checked by the complete category audit.
Final immutable-head commands, TRX counters and gate results are retained in
ignored `TestResults/issue-1041-sol-m3/` and the owner thread handoff.

## Affected validation and limits

Per t410u, execution is restricted to the historical-path matrix, real paired
database/archive/failed-first-upgrade restore controls, Deployment.Cli snapshot
and protected-file tests, and the M1 dirty-endpoint test. Necessary affected
Release builds are warning-clean. Category discovery, exact-range classifiers,
operations/control/diff checks and both documentation secret-scan gates supply
the remaining local evidence. No broad behavioral suite, coverage sweep,
performance campaign or hosted workflow is dispatched.

SDK 10.0.402, a short private mode-0700 TMPDIR, isolated HOME/XDG, disabled MSBuild
server/node reuse/shared compilation, an absent Docker socket and
`/tmp/hvo-heavy-gate.lock` are used for builds/tests.

All other requirements in the original R1–R9 crosswalk retain their prior
behavior: current identity/configuration, authentication, transport and comparison
semantics, pre-mutation ordering, bounded qualified reads, the shared finite
120-second budget, and Roy's Option A scrub timing. Only legacy-path admission
and restore path inspection change here; eligibility is not broadened.

30 days is the shipped default; longer settings are allowed. Recorded disposal,
first-qualification budget, old-complete-backup diagnostic wording, recovery
Retry-After guidance and the existing changelog blank line are unchanged. No PR
body/status/review/queue operation, deployment or live/hardware access occurs.
