---
name: pr-lifecycle
description: Manage a pull request from draft creation through review, correction, final synchronization, protected CI, merge, and cleanup. Use whenever an agent opens, reviews, updates, finalizes, or merges a PR; do not use for local-only work that will not touch a PR.
---

# Pull Request Lifecycle

This skill is the canonical detailed PR procedure for this repository. It
supplements the mandatory invariants in `AGENTS.md` and the validation and
handoff rules in `docs/planning/agent-execution.md`. User instructions take
precedence; identify any conflict before changing PR state.

## Ownership and State

The implementing agent owns the complete lifecycle unless the operator pauses
it or reserves a decision:

```text
local implementation
  -> local candidate evidence
  -> draft PR
  -> review convergence or documented unavailability waiver
  -> final target-branch synchronization and integration review
  -> ready for review
  -> classifier-selected protected CI
  -> merge, synchronize main, and clean up
```

Keep one implementation issue per branch and PR unless dependencies explicitly
define a stacked series. Preserve append-only issue and PR ledgers, immutable
reviewed ranges, and unrelated worktree changes. Never amend or force-push a
reviewed head without explicit operator approval.

## Local Work and Draft PR

1. Inspect the worktree, branch, target branch, issue, dependencies, and related
   PRs. Start from the current target branch.
2. Use the risk-tiered validation ladder: focused inner-loop tests, then one
   tier-appropriate candidate gate before the first push. Record the commands
   and the commit or worktree fingerprint they validate.
3. Commit only intended files, push the issue branch, and open a draft PR.
4. Keep the PR draft while acquiring review, correcting findings, and
   synchronizing the target branch. Draft pushes must not run protected CI.
5. Include scope, exclusions, compatibility, tests, output and performance
   evidence, observability review, and residual risks in the PR.

## Review Request Contract

Every review request must state:

```text
Review mode: initial | correction | base-sync
Issue and PR:
Acceptance criteria:
Target base SHA:
Previous reviewed head SHA:
Current head SHA:
Exact review range:
Correction rereview count: <N>/3
Required capability lens:
Requested capability profile: fast | standard | deep
Primary provider:
Fallback provider:
Execution route: local agent | equivalent exact-range runner | optional PR bot
Requested model and reasoning effort:
Actual provider, model, and reasoning effort:
Tests and failure modes to evaluate:
Local evidence:
Prior findings and dispositions:
Prior-finding verification checklist (finding ID/link, expected disposition,
  and evidence location):
Expected output:
Start acknowledgement: acknowledge on this PR within 15 minutes and identify
  the execution route, provider, model, effort, and exact range.
```

## Review Execution and Agent Selection

The canonical exact-range route is a coordinator-launched local review agent,
or an equivalent runner that can inspect the requested immutable Git range.
The dispatch configuration, not prose in a PR mention, binds the model and
reasoning effort. A provider-side PR bot may provide an additional full audit of
the current PR head, but it is not correction or base-sync convergence evidence
unless its execution route demonstrably enforces the requested range and its
report attests that range. Never rerun a bot merely to repair a range-less
report when a local exact-range reviewer is available.

Choose the capability profile before choosing a provider or model:

| Profile | Use when | Model capability | Reasoning effort |
| --- | --- | --- | --- |
| `fast` | Mechanical inventories or a small, isolated Tier A correction with no security, data, concurrency, migration, or CI-control risk | Fast, narrow coding model | `medium`; `low` only for deterministic inventory with no review judgment |
| `standard` | Ordinary Tier A/B implementation, full initial review, bounded test/configuration work, or a localized correction | Balanced general coding model | `medium` for implementation; `high` for review |
| `deep` | Tier C/M, security/auth/secrets, data-loss, concurrency, migrations, architecture or cross-host boundaries, CI semantics, material-correctness findings, conflict resolution, or ambiguous failure analysis | Strongest available coding/reasoning model | `high`; increase to `xhigh` or the supported equivalent only when complexity warrants it |

Review work is adversarial and must not use low effort. A narrow correction may
step down only when both the changed surface and every carried finding are
non-critical. A security, data-loss, acceptance, failing-CI, or
material-correctness finding keeps the review at `deep` until verified resolved.
A base-sync review with no merge-created changes may use `standard`; conflicts
or newly interacting boundaries require `deep`.

At dispatch, map the profile to a model identifier that the current harness
actually exposes. For the current Codex family, use `gpt-5.6-sol` for `deep`,
`gpt-5.6-terra` for `standard`, and `gpt-5.6-luna` for `fast`, or their documented
successors. Pin the reasoning effort separately. If a provider such as the
current Copilot reviewer offers one provider-managed model and no effort
control, record those fields as `provider-managed`; use it only when that fixed
capability satisfies the selected profile, and never claim that a requested
model was enforced. Prefer the other provider when the required profile cannot
be selected or verified.

The current providers are Copilot and Codex. Provider is independent of
capability profile: select for required capability first, availability second.
Record requested profile, requested model/effort, execution route, and actual
provider/model/effort in the append-only ledger. A missing actual value is
`unknown`, not an inferred alias.

Review modes have fixed ranges:

- **Initial:** review the complete merge-base-to-head PR diff.
- **Correction:** review only the previous-reviewed-head-to-current-head delta
  and the effects that delta has on surrounding code. Verify every prior
  finding individually as `verified corrected`, `verified deferred` with a
  linked issue and rationale, or `unresolved`. A prior finding that is neither
  fixed nor explicitly deferred must be reported as unresolved. Do not reopen
  unchanged code without concrete evidence of a new interaction caused by the
  delta.
- **Base-sync:** review conflict resolutions and interactions introduced by the
  target-branch merge, then recalculate the complete PR diff against the new
  base without rereviewing unchanged upstream code.

A completed correction rereview is acceptable only when its report identifies
the exact requested range and returns the disposition of every item in the
prior-finding checklist. A generic whole-PR approval or a clean result that
does not provide that evidence is an incomplete response, not convergence.
Ask the same provider to correct the report within the active acquisition
window; if it cannot, use the fallback/unavailability path without incrementing
the correction-round count or changing the head.

## Bounded Review Acquisition

1. Launch the primary local review session with the exact head, range,
   capability profile, model, and effort. Require it to send the coordinator a
   structured `STARTED` message before substantive review and post the same
   acknowledgement on the PR when it can. The coordinator immediately relays
   the start to the main conversation and starts a fifteen-minute acquisition
   timer when dispatch is visible.
2. A written PR acknowledgement is preferred. If the provider cannot post one,
   accept a local harness start message or session state that identifies the
   exact head/range and actual model/effort. For an optional provider-side bot,
   accept a provider-generated in-progress check, status, timeline event, or
   visible review activity after the exact-head dispatch and before any head
   change. The request or mention itself proves dispatch, not start.
   For Copilot, the provider-generated `copilot_work_started` PR timeline event
   is the known start acknowledgement. For Codex, the connector-generated
   `codex-pull-request-review-summary` comment qualifies when its table reports
   `Running`, a start time, and the exact commit. A Codex `mentioned` timeline
   event is invocation only. For another provider, record the first observed
   provider-generated comment, status, timeline event, or in-progress review
   that demonstrates work began; do not invent or assume a signal before it is
   observed. Reactions and unrelated automation do not qualify.
3. If the primary rejects the request or dispatch fails, request the fallback
   immediately. Otherwise, when fifteen minutes pass without a valid start,
   append the timeout to the ledger and send the unchanged request to the
   fallback.
4. Give the fallback fifteen minutes under the same rules. If it also does not
   start, append `Review waived - providers unavailable` with both provider
   names, request links and times, deadlines, exact SHA/range, and residual
   risk. The waiver completes acquisition for that exact head and mode; it is
   not a passed review and does not waive local evidence or protected CI.
5. Once a review starts, require agent-to-coordinator and PR-ledger activity at
   every milestone and at least every thirty minutes. The coordinator relays
   milestones immediately and continues its independent five-minute
   main-conversation heartbeat. After thirty minutes without agent activity,
   request status. If there is no response for another fifteen minutes, treat
   the provider as unavailable and use the same fallback or waiver path.
6. When fallback begins, stop waiting for the primary and withdraw its request
   when supported. Any substantive finding received before merge must still be
   dispositioned; an actionable late finding reopens convergence.
7. Any head change cancels stale requests and invalidates completed review or
   waiver evidence for the changed range.

## Corrections and Rereview Cap

Disposition each finding as corrected, evidenced non-actionable, explicitly
deferred as non-blocking to a linked issue, or an unresolved merge blocker.
Batch coherent corrections, run the reproducer and affected local gates, push
without rewriting reviewed history, and request correction review. The
rereviewer must return an item-by-item disposition for all findings carried
into the round. Any finding omitted from that response, or neither verified
fixed nor verified deferred, remains unresolved and must be flagged in the
ledger; it prevents convergence.

Count correction rereviews across the whole PR. Initial and base-sync reviews
do not consume this cap, and switching providers for the same range remains one
round. A clean correction rereview still counts.

After the third correction rereview, if eligible newly reported non-blocking
findings remain and would otherwise require a fourth round, do not request that
round and do not change the reviewed head to correct them. Before finalization:

1. Create one issue titled
   `Follow-up: deferred review findings from PR #<number>`.
2. Record the originating PR and issue, target-base and reviewed-head SHAs,
   provider/model, source-comment links, reproduction, expected and actual
   behavior, severity, affected paths, attempted corrections and reviewed
   ranges, required tests and acceptance criteria, residual risk, and owning
   roadmap epic when applicable.
3. Append a `Review cap reached` ledger entry linking the issue and disposition
   each carried finding as `deferred due to three-rereview cap`.
4. Treat the third reviewed head as converged and continue to finalization.

If the third rereview is clean or no eligible finding remains, do not create an
empty follow-up issue.

Security, data-loss, acceptance, failing-CI, and material-correctness defects
are never deferrable. Correct them, rerun affected evidence, and request a
targeted cap-exception review. Record the exception, and do not run CI or merge
until the blocker is resolved. A deferred finding that makes CI fail becomes a
current-PR blocker.

Review converges only when every requested review has completed or has an exact
head/range unavailability waiver, every finding is dispositioned, required
correction rereviews are complete, no actionable thread remains, and the
current head equals the latest reviewed or waived head.

## Finalization Lock and Target-Branch Synchronization

Use the `workflow:finalizing` label on an open PR as the repository-wide
finalization lock. Draft PRs may continue implementation and review while
another PR holds it, but no other PR may perform final synchronization, run
protected CI, or merge.

1. Acquire the lock only after draft review converges and no ordinary product
   correction is expected. Use live issue/PR API data, not search-indexed list
   results, to confirm no open PR has `workflow:finalizing`. If a holder exists,
   do not apply the label. Otherwise, apply it to this PR, then use live queries
   to confirm sole ownership at both ends of a minimum thirty-second
   stabilization interval. If concurrent claims appear, the lowest PR number
   retains the label and every other claimant removes it and waits. Recheck sole
   ownership immediately before final synchronization, the ready transition,
   every CI rerun, and merge; losing ownership aborts the guarded transition.
   With `gh api`, use an explicit `--method GET` when passing query fields, or
   put the encoded query in the URL; otherwise `-f` fields default to a POST and
   can accidentally target issue creation instead of performing a read.
2. Fetch the target branch and merge it into the topic branch. Do not rebase or
   force-push reviewed history.
3. Resolve conflicts, run affected local gates, and obtain a base-sync review.
4. Fetch again and prove the target base and PR head are current, mergeable, and
   reviewed. If either moved, repeat synchronization or review as applicable.
   At this draft pre-ready gate, use the provider's structural mergeability
   result: for GitHub, require `mergeable: true`, retry a bounded `null` or
   unknown result, and stop on `false` or conflicts. Do not require
   `mergeable_state: clean` here because the intentionally failing draft
   `Required CI` placeholder keeps policy state `blocked` until the ready
   transition starts protected CI.
5. Mark the PR ready only now. This transition starts the authoritative
   classifier-selected protected CI plan.
6. Hold the lock through CI and merge.

If the target branch advances during CI, cancel the stale run when possible,
return the PR to draft, release the lock, synchronize and review again, then
re-enter finalization. A product correction also releases the lock. A diagnosed
infrastructure-only, timeout, cancellation, or flaky failure on the same SHA may
retain it for one bounded rerun. Releasing the lock means removing the
`workflow:finalizing` label and recording the reason in the ledger.

## Protected CI, Merge, and Cleanup

- Investigate every non-green result. Never suppress a symptom, weaken a test,
  or accept a canceled, missing, timed-out, or stale check as success.
- A product correction returns the PR to draft, runs affected local gates, and
  receives correction review before another ready transition. Do not repeat
  unchanged successful local gates.
- Merge only when the current head equals the reviewed or precisely waived head,
  the target branch is still current, the PR is mergeable, required checks are
  green for that exact head, and actionable conversations are resolved.
- After merge, confirm issue closure, update the owning roadmap epic when
  applicable, synchronize local `main`, remove merged branches/worktrees when
  safe, release the finalization lock, and continue according to `AGENTS.md`.

## Required Ledger Entries

Append rather than replace entries for review dispatch, acknowledgement,
provider timeout or switch, completed review, finding disposition, correction
range, cap and follow-up issue, waiver, finalization lock, base synchronization,
ready transition, CI terminal result, merge, and cleanup. Every entry includes a
UTC timestamp, relevant SHAs, what finished, what is running, the next step, and
any blocker.
