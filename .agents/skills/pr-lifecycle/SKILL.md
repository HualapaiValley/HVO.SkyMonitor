---
name: pr-lifecycle
description: Manage a pull request from draft creation through review, correction, final synchronization, protected CI, merge, and cleanup. Use whenever an agent opens, reviews, updates, finalizes, or merges a PR; do not use for local-only work that will not touch a PR.
---

# Pull Request Lifecycle

This skill is the independently loaded PR-lifecycle entry point. The complete
operating procedure lives in `docs/planning/agent-execution.md`; this file keeps
the non-negotiable PR contracts, selection matrix, and guards needed before a
harness follows that procedure. `AGENTS.md` remains the repository-wide entry
point. User instructions take precedence; identify any conflict before changing
PR state.

## Ownership and State

The implementing agent owns the complete lifecycle unless the operator pauses
it or reserves a decision:

```text
local implementation
  -> local candidate evidence
  -> draft PR
  -> review convergence or documented unavailability waiver
  -> final target-branch synchronization and review if the target advanced
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
   Select the candidate gate set by running `scripts/ci:classify` on the review
   range whenever it reports `complete=true`, not by reading the diff, and
   record which selector produced the set. Until the ready transition the
   classifier is the only selector that is not bounded by the diff, because the
   `changes` job is gated on `draft == false`. A ledger that lists gate results
   without naming the selector cannot distinguish a gate that passed from one
   that was never chosen. The gate includes the CI-control guards, which are
   Docker-free and are listed in `AGENTS.md`; run them again on the base-synced
   head before requesting the base-sync review.
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
Dispatch command ID and target participant ID:
Acceptance criteria:
Target base SHA: (the tip of the base branch read from the repository by
  branch name; this is the range's left endpoint, not the base the PR
  recorded when it was opened)
Target base ref: (the remote and branch used to transport that tip, verified
  to contain it; transport only, never the source of the endpoint)
PR-recorded base SHA: (baseRefOid; provenance only, never a range endpoint)
PR merge-base SHA:
Previous reviewed head SHA:
Previous reviewed head selection: (the request comment this tool emitted for
  the previous round, or a caller assertion marked as unverified; never the
  previous reviewer's prose)
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
Prior report selection: (the comment the dispatcher named as the previous
  round's report)
Prior findings and dispositions:
Prior-finding verification checklist (finding ID/link, expected disposition,
  and evidence location):
Evidence pack:
Expected output:
Start acknowledgement: acknowledge on this PR within 15 minutes and identify
  the execution route, provider, model, effort, and exact range.
```

Generate this block with `scripts/pr:review-request`; inspect it before launch.
Dispatch it with `scripts/pr:dispatch-review`, which must receive the durable
request comment ID, hold the host-local participant/session dispatch lock, post
a durable launch reservation, revalidate the current participant lease, and
only then resume the same previously joined CLI session and append STARTED
metadata. A concurrent dispatcher for that participant returns `DISPATCH_BUSY`;
retry it only after the active dispatcher records terminal state and releases
the lock. Bootstrap the session without review work, complete
`JOIN REQUEST` -> `JOIN ACK` -> `JOINED ACK`, and pass its full local resume ID
only to the launcher; the public participant identity retains the non-secret
short ID. The dispatcher must stay alive and wait for the resumed reviewer;
some harnesses reap detached descendants as soon as the parent command returns
even when `nohup` was used. A retry resumes from an already-posted request,
returns `ALREADY_CONSUMED` after STARTED, and requires explicit recovery if it
finds only a reservation; it never launches a second reviewer automatically.
The participant identity binds the session to one host; migrating it to another
host requires a new identity and join rather than reuse across host-local locks.
Do not reconstruct ranges or causal ordering in an ad hoc shell pipeline when
these scripts support the route.

A correction or base-sync request also requires `--previous-command-id`, the
dispatch command ID of the round whose reviewed head this round starts from.
The tool recovers that head from the request it emitted for that round, found by
its command ID or pinned with `--previous-request-comment`. It never reads a
range out of a reviewer's report. For a round dispatched before the tool
recorded a request, assert the head with `--previous-head`; the emitted request
then records it as caller-asserted and unverified, and a reviewer must treat it
as an input to check rather than as an attested range. If neither is available
the tool fails and names what was missing, which is correct: a correction review
whose left endpoint is a guess reviews the wrong commits and says nothing about
it.

Such a request also requires `--previous-report-comment`, the comment ID of the
previous round's report. The dispatcher supplies it; the tool never searches for
it. The head can be selected because the tool wrote the comment carrying it,
while the report is the reviewer's own comment and carries no field the tool
controls, so every rule for recognising one is a proxy that this repository's
ledgers defeat. Replayed against merged pull requests, matching on verdict
wording found no review at all on #806 and selected a base-sync note over the
real report on #798, and matching on the command ID anywhere in the body selects
dispatch ledgers, start acknowledgements, and later comments that merely cite
the round. The tool checks that the named comment echoes the round it is being
used for, as a whole command ID rather than as a substring, and refuses a
comment that belongs to another one.

It also refuses a named report that carries both `Exact review range:` and
`Dispatch command ID and target participant ID:`. Those are the two fields the
tool writes into every request it emits, so a comment carrying both is its own
output rather than a reviewer's report, and the round's dispatch ledger entry
quotes the request and therefore echoes the round by construction. The tool
cannot recognise a report, but it can recognise what it wrote, which is the same
premise the head half rests on used in the direction it actually holds.

Every comment the caller names is fetched through
`repos/{owner}/{repo}/issues/comments/{id}`, which is repository-wide rather
than per-issue, so a comment on another pull request fetches cleanly through it.
The tool reads `issue_url` alongside the body and refuses any comment that is
not on the pull request under review. Without that check, the claim that the
tool wrote the comment carrying the previous reviewed head was an assumption
about authorship rather than something checked.

`scripts/pr:dispatch-review` quotes the request into a fenced block opened with
a fence longer than any fence in the request itself, and the reader closes only
on one at least that long. Requests interpolate caller-supplied test and
evidence files, which routinely carry pasted command output inside fenced
blocks; a fixed three-backtick opener is closed by the first of those on
read-back, and the request comes back truncated while still carrying enough
fields to look complete.

An empty `Prior findings and dispositions:` is not an attestation that the
previous round was clean. When the tool could not extract a checklist it says
so in that field and points at the full report in the evidence pack; verify
findings against the report itself, not against the summary.

If a resumed CLI reports that its nested read-only sandbox cannot execute, mark
the attempt `INCOMPLETE` and use an explicitly enrolled collaboration-agent or
other provider route. Do not bypass the sandbox merely to make the review run;
any intentionally unsandboxed route requires separately verified external
isolation and an explicit ledger record.

## Review Execution and Agent Selection

The canonical exact-range route is a coordinator-launched local review agent,
or an equivalent runner that can inspect the requested immutable Git range.
The dispatch configuration, not prose in a PR mention, binds the model and
reasoning effort. A provider-side PR bot may provide an additional full audit of
the current PR head, but it is not initial, correction, or base-sync convergence
evidence unless its execution route demonstrably enforces the requested range
and its report attests that range. Never rerun a bot merely to repair a
range-less report when a local exact-range reviewer is available.

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

Tier A/B receives one initial exact-range review and only finding-driven
correction rereviews. `standard` is the default for Tier A and ordinary Tier B;
concurrency, durability, security, CI-control, or cross-boundary Tier B risk is
`deep`. Tier C/M remains `deep` unless its issue-specific evidence requires a
stronger supported effort. Finalization does not create a review round when the
fresh target SHA is unchanged as described below.

At dispatch, map the profile to a model identifier that the current harness
actually exposes. Current Codex documentation maps demanding work to `gpt-5.6`,
balanced read-heavy work to `gpt-5.6-terra`, and narrow repeatable work to
`gpt-5.6-luna`; use those identifiers or documented successors only where the
launcher advertises them. A harness may expose a different provider-specific
identifier such as `gpt-5.6-sol`; use it only when that exact identifier appears
in the launcher's available-model list. Pin the reasoning effort separately. If
a provider such as the current Copilot reviewer offers one provider-managed
model and no effort control, record those fields as `provider-managed`; use it
only when that fixed capability satisfies the selected profile, and never claim
that a requested model was enforced. Prefer the other provider when the
required profile cannot be selected or verified.

The current providers are Copilot and Codex. Provider is independent of
capability profile: select for required capability first, availability second.
Record requested profile, requested model/effort, execution route, and actual
provider/model/effort in the append-only ledger. A missing actual value is
`unknown`, not an inferred alias.

For a narrow test-only or docs-only Tier A/B correction with no carried
material finding, the least-cost currently advertised standard-capable route
may use `gpt-5.6-luna` at high effort. Ordinary code corrections use
`gpt-5.6-terra` at high effort when that identifier is available. Security,
durability, data-loss, unresolved acceptance, or material-correctness findings
remain `deep` on the strongest supported route. Never substitute an identifier
the launcher does not advertise.

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

Before a correction launch, build a bounded evidence pack containing the exact
delta, changed-symbol call sites, previous round's full report, complete
carried-finding checklist, relevant tests/results, and applicable instruction
excerpts. Limit unrelated preloaded history, documentation, and repeated green
gates, but never restrict code reachable from the delta; the reviewer may read
every caller or interaction needed. Record elapsed time, actual reviewer token
usage when available, diff files/lines, mode/profile, and findings for each
round. Budget exhaustion returns `INCOMPLETE` with remaining work and escalates;
it can never imply `CLEAN`. Compare at least three post-change correction rounds
before changing the default model mapping, including safety outcomes rather
than token cost alone.

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

1. Acquire the lock only after draft review converges and no ordinary
   product correction is expected. Confirm no open PR has
   `workflow:finalizing` by direct object read of each open PR, not by a
   filtered or search-indexed list. A filtered read proves presence, never
   absence: a result naming a holder is authoritative, but an empty result
   cannot distinguish no holder from an index that has not caught up, and it
   returns a well-formed `200` in both cases rather than an error. Do not
   read `incomplete_results` as a freshness signal; it is `false` in both,
   and a label name that never existed returns the same empty result
   permanently, so a typo in `workflow:finalizing` reads as a free lock.
   Enumerate open PRs with `gh pr list --state open`, which reads primary
   data, then read each `issues/<n>` object, and treat only that set as the
   lock state. Do not add `--label workflow:finalizing`: that one flag
   routes the same command through search, and nothing at the call site
   announces it. The same rule applies to every recheck of sole ownership.
   That enumeration is the read in this procedure that can truncate silently.
   It is unfiltered, so its page boundary is set by how many pull requests
   are open rather than by anything about the lock, and the default page is
   thirty. Every list read in this step therefore carries an explicit page
   size and a check of the returned count against it, on the same call and
   not as a separate step: the result is trustworthy only when it returns
   strictly fewer items than the size requested, because a count equal to the
   size may be complete or may be truncated and nothing in the response
   distinguishes the two. Naming an endpoint or a limit without the check
   moves the boundary rather than removing it, and leaves the next reader a
   call that looks blessed. Measured here: `gh pr list --state open` at a
   limit of five returns five and at seven returns seven, both suspect; at
   eight it returns seven, complete. This matters because the lock is stated
   here as a negative over that enumeration — confirm that no open pull
   request carries the label — and confirming a negative is exactly where a
   silent page boundary reports a free lock while one is held. A short page
   under-reports holders; it never invents one. That direction is what makes
   this a defect rather than a tidiness point, because the failure is a
   double claim on a repository-wide mutex, and it arrives from ordinary
   repository growth rather than from any agent doing anything wrong. The
   same enumeration backs the sole-ownership confirmation at both ends of the
   stabilization interval, so a truncated read there can confirm a sole
   ownership that does not exist. Where a filtered read is used at all, it
   must be a server-side label filter over primary data —
   `repos/{owner}/{repo}/issues` with `state=open`,
   `labels=workflow:finalizing`, an explicit `per_page`, and the same
   strictly-fewer-than check — and never `gh pr list --label`, which routes
   through search as stated above. That endpoint returns issues and pull
   requests together, so read the `pull_request` field to tell them apart,
   and note that it carries the same default of thirty: unfiltered against
   this repository it returns exactly thirty of eighty-six open items. The
   filtered form is not itself likely to truncate, since the single-holder
   invariant means one row and any page size above one passes. Carry the
   check regardless. It survives the clause being copied with the filter
   dropped, which is the form that does truncate, and it costs one
   comparison. When reading fields off those objects, do not print a boolean
   through jq's alternative operator. `.draft // "missing"` yields the
   fallback for a present `false`, so a non-draft lock holder reads as an
   object with no draft field. Use
   `if has("draft") then .draft else "missing" end`, or compare the value.
   Direct object reads fix the staleness half of that failure and leave the
   spelling half untouched: a mistyped literal produces the same empty
   result in the direct-read form as in the filtered form, though not for
   the same reason, and it produces it permanently, because a label that was
   never created never acquires members. So before an empty result is
   believed to mean a free lock, confirm the predicate matches something
   known: either assert `workflow:finalizing` is present in the repository's
   label set, or observe the same comparison naming a known holder. The
   control and the comparison must resolve one definition of the label name,
   which in shell means a single variable read by both rather than the
   string typed twice. A control that checks its own separate copy passes
   while the comparison stays misspelled, which is worse than no control,
   because it converts an unexamined assumption into a checked one that was
   never checked. A third mechanism returns that same empty result, and
   neither of the first two implies it. A read that is unfiltered, unsearched
   and well formed still stops at the API's default page size, so its array
   is complete in form and short in fact; a label sorting past that boundary
   reads exactly like a label that was never created. This control is a list
   read like any other in this step, so it carries the same explicit page
   size and the same strictly-fewer-than check, or it pages to exhaustion.
   Wrongly reporting the label absent fails safe: it makes an agent distrust
   an empty lock read and decline to acquire, a spurious block rather than a
   double claim. The second form of the control escapes this mechanism only
   in the narrow sense that it reads the enumeration rather than a separate
   name list. The enumeration carries the same default and is the half that
   fails toward a double claim, so it is governed above; a margin is not an
   immunity. If a holder exists, do not apply the label. Otherwise,
   apply it to this PR, then confirm sole ownership by the same direct
   object reads at both ends of a minimum thirty-second stabilization
   interval. If concurrent claims appear, the lowest PR number retains the
   label and every other claimant removes it and waits. Recheck sole
   ownership immediately before final synchronization, the ready transition,
   every CI rerun, and merge; losing ownership aborts the guarded
   transition. With `gh api`, use an explicit `--method GET` when passing
   query fields, or put the encoded query in the URL; otherwise `-f` fields
   default to a POST and can accidentally target issue creation instead of
   performing a read.
2. Fetch the target branch and compare its exact SHA with the target-base SHA
   already covered by the converged review. When they are equal, append `base
   unchanged at <sha>; no merge and no base-sync review required` and proceed
   without changing the head. When the target advanced at all, merge it into the
   topic branch; do not rebase or force-push reviewed history.
3. After an advancing-target merge, resolve conflicts, run affected local gates,
   and obtain a base-sync review. Use `standard` when there was no conflict,
   shared-file/contract overlap, or material changed interaction; use `deep` for
   any of those conditions. Run the CI-control guards on the merged head before
   requesting that review, and do not expect the review to substitute for them:
   a base-sync review verifies that the merge preserved both sides' behaviour,
   which it can do correctly while a classifier lane that each side satisfied
   separately is violated by their union. PR #740 is the case; its base-sync
   reviewer said so itself rather than letting a CLEAN verdict look wider than
   it was.
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
