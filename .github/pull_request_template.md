## Summary

- Closes #
- Roadmap initiative (`RM-###`) or maintenance rationale:
- Owning roadmap epic:
- Plain-language outcome and why this issue is next:
- Practical benefit and what this unlocks:

## Scope

- Implemented:
- Excluded or deferred:
- Compatibility/migration impact:

## Issue Ownership

- Authorized human or operator-directed agent's unique identity:
- Issue claim comment, branch/worktree, scope, and next checkpoint:
- GitHub assignee (if possible) and `workflow:in-progress` label:
- [ ] Claim comments, labels, and assignees were re-read after claiming; writes
      are not atomic, and any competing claim stopped work pending resolution.
- [ ] An assignee alone was not treated as sufficient for agents sharing an account.
- [ ] The owner retains the claim through review and CI, with explicit release
      and handoff on completion or transfer; no stale-claim automatic takeover.
- [ ] Shared-resource capacity was checked as applicable, without global
      two-issue slots or a required coordinator assignment.

## Validation

- [ ] Risk tier (`A`, `B`, `C`, or `M`) and rationale are recorded below.
- [ ] Focused and tier-appropriate affected tests pass.
- [ ] Tier `C`/`M` complete local candidate gate passed, or `N/A` is justified.
- [ ] Corrections reran focused and affected gates without repeating unrelated
      long suites.
- [ ] Required Debug/Release builds and solution tests pass for the selected tier.
- [ ] Coverage output was reviewed; the current gate was not lowered and no
      uncorrected regression remains.
- [ ] Migrations, architecture, publish, format, package, fault, soak, external,
      or browser gates required by the issue pass.
- Commands and results:

## Output Evidence

- Checksums/numerical invariants:
- Provenance/recipe/profile/lineage:
- Durable filesystem/SQLite/SQL/MinIO state:

## Performance

- Applicability and any `N/A` rationale:
- Baseline/candidate revisions and dirty-state disposition:
- Exact command, workload/config/fixture/seed/count/rate/outage parameters:
- Environment, tools/counters, warm-up, repetitions, sampling, and raw-result location:
- Baseline and candidate I/O, CPU, allocations/working set, throughput, latency,
  and backlog evidence:
- Noise/tolerance method, regression disposition, and residual risk:
- Complexity justification: simpler alternative, measured/correctness benefit,
  and added ownership/invalidation/migration/recovery burden:

## Runtime Review

- Logs/errors/warnings:
- Metrics/cardinality:
- Traces and health transitions:
- Expected signal manifest, collection/assertion command, and retained artifact path:
- Secret, payload, path, or personal-data leakage review:

## Review And Merge Gate

- Initial merge-base SHA:
- Initial reviewed head SHA:
- Latest reviewed head SHA:
- Final target-base SHA:
- Independent reviewer identity and primary/fallback route (human, independent one-shot agent, enrolled resumed-session agent, or equivalent exact-range runner):
- Required review depth/capability profile (human or agent):
- Review execution route (human, independent one-shot agent, enrolled resumed-session agent, equivalent exact-range runner, or optional PR bot):
- Requested/actual provider, model, and reasoning effort (human review: `N/A`):
- Correction rereview count (`0`-`3`, or documented blocking exception):
- Finalization-lock owner and acquisition time:

| Mode/round | Reviewed range | Route and provider/model/effort | Requested/start/completed UTC | Finding disposition and evidence |
| --- | --- | --- | --- | --- |
| Initial | `<merge-base>..<head>` |  |  |  |

For every correction row, name the exact delta and record each prior finding as
`verified corrected`, `verified deferred` with its linked issue, or `unresolved`.
A generic whole-PR approval is not correction-rereview evidence.

- [ ] Initial review covered the complete PR diff through the initial reviewed head.
- [ ] The PR owner requested independent human, one-shot agent, enrolled
      resumed-session agent, or equivalent exact-range runner convergence
      reviews bound to immutable base/head SHAs, with reviewer identity and
      findings recorded; agent reviews recorded actual provider/model/effort;
      an optional provider-side current-head audit was not used as a substitute.
- [ ] Every request recorded reviewer identity and immutable SHA/range; applicable
      agent routes recorded the fifteen-minute acknowledgement deadline and
      fallback or waiver outcome. Human model/effort fields are `N/A`.
- [ ] Human review used a recorded agreed availability window, not agent timers
      or a timed waiver; if unavailable, another qualified reviewer was requested.
- [ ] Every finding is corrected, evidenced non-actionable, agreed deferred to a
      linked issue, or identified as an unresolved merge blocker.
- [ ] Every correction rereview identified its exact previous-head-to-current-head
      delta, reviewed only that delta and its concrete interactions, and
      dispositioned every prior finding individually.
- [ ] Any prior finding neither verified corrected nor explicitly deferred to a
      linked issue was flagged unresolved and prevented convergence; unchanged
      code was not reopened without concrete interaction evidence.
- [ ] No more than three correction rereviews were requested unless a documented
      non-deferrable blocker required a targeted cap exception.
- [ ] If a fourth correction rereview would otherwise be needed, one consolidated
      follow-up issue contains every eligible non-blocking finding and is linked
      here:
- [ ] Every review round was appended to the ledger without replacing earlier
      reviewed ranges.
- [ ] The owner reported review start, milestones, and blockers directly to the
      operator and retained interim evidence during long reviews and gates.
- [ ] `JOIN`, command receipts, slot leases, and coordinator heartbeat mechanics
      were scoped only to optional explicitly enrolled legacy managed sessions,
      not required for default ownership or independent review. Any resumed
      fleet tool retained its identity and command guards.
- [ ] Sandbox and permission boundaries were preserved; denied actions were not
      routed through another session.
- [ ] Correction commits are pushed without force-push or unrequested amend.
- [ ] The PR remained draft during review correction and was marked ready only
      after review convergence, final target-branch synchronization, and
      required base-sync review or recorded unchanged-base proof.
- [ ] Every planned post-ready head change returned the PR to draft before the
      change and received bounded delta review.
- [ ] The repository-wide finalization lock was held from final synchronization
      through protected CI and merge; no outstanding target-branch merge remains.
- [ ] Classifier-selected protected CI ran on the final reviewed current head.
- [ ] Every required current-head check is green; canceled, timed-out, flaky,
      missing, or stale pre-correction checks are not accepted.
- [ ] Review conversations are resolved only after correction evidence and
      correction-delta review exist.
- [ ] For roadmap work, the owning epic will be updated after merge with
      validation, performance, and the exact next action.
- [ ] The owner will complete authorized merge, confirm issue closure,
      synchronize local `main`, and safely clean up merged branches/worktrees,
      preserving unrelated changes; reserved or unauthorized merge is a reported
      boundary, not permission to bypass it.
- [ ] Completion or transfer will explicitly release the claim with a handoff
      and remove `workflow:in-progress`; a stop will record retention or release.
- [ ] No further issue will be selected or claimed without user authorization.
