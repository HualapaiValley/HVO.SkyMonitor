# #1041 round-4 correction design

Refs #1041. Kepler's consolidated r4 findings at 9fc7d1eb8 govern this correction;
Roy's Option A, t388u/t389u and affected-only t410u validation scope remain in force.
No PR, issue comment, reviewer-branch edit, deployment or live runtime access.

| Finding | Planned behavior | Required regression/evidence |
| --- | --- | --- |
| F1 | A failed bounded recent check revokes incremental eligibility. Initial qualification without a completed recent check also uses full bytes. Only a completed full qualification, or a successful fresh recent check, can restore recent readiness. Never clear a detected tamper merely because older metadata still matches. | Same-length/time-preserving recent file and same-length recent journal tampering: cold check refuses, ordinary background scrub cannot qualify, protected verification refuses. Both fail before the fix. |
| F2 | Remove only the disposable staged archive's clean checkpoint, durably, before publishing its authenticated ledger. History, ledger, keys and captured evidence remain intact. Missing checkpoint forces full qualification even if file metadata is preserved. DB-only restore keeps its existing generation advance. | Complete archive, preserved file timestamps and v15 journal: ordinary startup/ScrubCurrent must read both manifests. Same-length file and journal tampering refuses; missing keys remain refused. Tests use VerifyInstallationAsync, not a forced FullVerify as the trust guard. |
| Docs | Correct the earlier unconditional restore/refusal claims and distinguish historical evidence from these defects. | Run-5/run-6 reports/design/ledger annotations and current operator runbook; full R1–R9 mapping retained. |
| Sol Low | Count parse proofs separately from file/journal metadata hints. | Three-sidecar small fixture shows 3 parse proofs and 9 total rows without the large manual campaign. |
| Opus Lows | Keep the explicit semantic validator-version maintenance rule visible; disclose manual bump risk and O(N) row authentication/copy/rewrite on restart. | Evidence limitations; no unrequested format-guard redesign or large-scale burn-in. |

Use a fresh short private TMPDIR, SDK 10.0.402, disabled node reuse and the heavy
lock. Capture failing-before tests before product edits, then run classifier-
selected affected evidence, warning-clean Release, category audit, operations
audit and applicable lightweight guards. Fresh normal development merge before
push; Kepler launches the next independent HIGH reviews afterward.
