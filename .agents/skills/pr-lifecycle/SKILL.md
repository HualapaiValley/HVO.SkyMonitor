---
name: pr-lifecycle
description: Guide coding threads through issue completeness and local review to a candidate handoff, and Kepler through formal review, PR readiness, reviewed-head publication, and merge-queue admission. Use for pre-PR readiness, PR review/correction, and Kepler finalization; exclude unrelated local-only work.
---

# Pull Request Lifecycle

This skill is the independently loaded PR-lifecycle entry point. The complete
operating procedure lives in `docs/planning/agent-execution.md`; this file keeps
the non-negotiable PR contracts, selection matrix, and guards needed before a
harness follows that procedure. `AGENTS.md` remains the repository-wide entry
point. User instructions take precedence; identify any conflict before changing
PR state. The Issue Completeness contract here and in `AGENTS.md` governs
pre-PR readiness even where an older walkthrough starts with draft creation.

## Ownership and State

Coding threads own implementation, original-requirement evidence, local tests,
independent local review, and corrections. They stop at the candidate/evidence/
review handoff and push only when instructed. They **never create PRs (even
drafts), mark ready, set `hvo/reviewed-head`, or enqueue**. This boundary is
unconditional: issue ownership, write access, or a fallback route does not
transfer PR authority. Kepler alone creates PRs, controls draft/readiness and
formal review, publishes the reviewed-head gate, and enqueues. Kepler also
operates authorized direct/auto-merge fallbacks and the PR ledger. All PR-state
commands later in this skill are Kepler-only; they are not coding instructions.
Fleet enrollment changes neither this boundary nor the review requirements.

### Required Review Tiers

| Tier | Scope | Required independent reviews on the same candidate SHA |
| --- | --- | --- |
| LOW | Docs/tests-only, non-gating CI configuration, dependency bump, or small isolated non-safety fix, without a HIGH concern | One Sol **CLEAN** |
| HIGH | Camera, roof, safety, interlock, durable-state, lifecycle, concurrency, recovery, security, merge-gating, data-migration, or cross-component work; when unsure, HIGH | Sol **and** Opus **CLEAN**, both on that candidate |

HIGH concerns override LOW categories. Apply the named-reviewer requirement at
each required local/formal stage; a local CLEAN does not waive formal review.
A/B/C/M tiers and fast/standard/deep profiles select validation and depth, not
substitute reviewers. Humans, bots, other models, interchangeable providers,
availability waivers, elapsed time, and correction caps cannot replace the
required Sol/Opus CLEAN reports. An unavailable required reviewer means BLOCKED.

Only High and Medium review findings block (Critical is treated as High).
Lows become follow-up issues through Kepler and do not force another correction
round. The coding handoff supplies the Low finding and follow-up text; Kepler
records its issue link. Incompleteness and missing required evidence always
block, are at least Medium, and are never Low or eligible for deferral. CLEAN
means complete scope and no unresolved High/Medium findings; it does not waive
required tests or CI.

The PR target determines the protected-CI profile. For `development/v1`, use
the two checks in [the v1 contract](../../../docs/development-v1.md):
Preflight and Build and Unit. The full `ci.yml` matrix is for main/release
qualification or explicit issue/operator acceptance. Do not manually dispatch it
for an ordinary v1 PR, even when the classifier selects `complete=true` or a
base-sync delta includes unrelated upstream changes. Record the profile beside
classifier evidence. Risk-tier local validation and independent review still
apply. This target-specific rule governs generic protected-CI references in this
skill; it does not waive an explicitly required integration/qualification result.

V1's Build and Unit check enforces the measurable canonical per-file floors
using the same baseline and evaluator as the local coverage gate; Preflight
rejects baseline weakening. Follow the [coverage scope and reproduction](../../../docs/runbooks/ci-pipeline.md#development-v1-per-file-coverage).
Missing enforced-file data is a failure. Catalog-dependent ReleaseTool coverage,
full-suite aggregate and additional/higher component floors remain local/full
qualification. Preserve documented per-PR coverage exceptions, including #1739;
do not turn them into permanent waivers or invent a new-file percentage gate.

```text
coding implementation -> original-issue evidence at candidate SHA
  -> required tier-specific local CLEAN reviews -> coding thread STOP/handoff
Kepler creates draft -> required formal CLEAN reviews
  -> Kepler marks ready -> target-branch protected CI
  -> Kepler revalidates current head, completeness, reviews, CI, conversations
  -> Kepler publishes reviewed-head -> GraphQL enqueuePullRequest
  -> queue integration CI/merge -> Kepler completion and cleanup
```

Keep one implementation issue per branch and PR unless dependencies explicitly
define a stacked series. Preserve append-only issue and PR ledgers, immutable
reviewed ranges, and unrelated worktree changes. Never amend or force-push a
reviewed head without explicit operator approval.

## Coding Work and Candidate Handoff

1. Inspect the worktree, branch, target branch, issue, dependencies, and related
   PRs. Record the target ref and branch point. Read the original issue including
   its discussion and acceptance criteria, not just the planned implementation.
2. Use the risk-tiered validation ladder: focused inner-loop tests, then one
   tier-appropriate candidate gate before the first push. Record the commands
   and the commit or worktree fingerprint they validate.
   Select the candidate gate set by running `scripts/ci:classify` on the review
   range whenever it classifies the range successfully, not by reading the diff,
   and record which selector produced the set. `complete=false` is authoritative
   for component-scoped changes; it does not permit manual gate selection. Until
   the ready transition the
   classifier is the only selector that is not bounded by the diff, because the
   `changes` job is gated on `draft == false`. A ledger that lists gate results
   without naming the selector cannot distinguish a gate that passed from one
   that was never chosen. The gate includes the CI-control guards, which are
   Docker-free and are listed in `AGENTS.md`; run them again on the base-synced
   head before requesting the base-sync review.
3. Commit only intended files and bind completeness evidence to that candidate
   SHA. Prove every in-scope requirement before independent local review, then
   obtain the required tier's CLEAN reports on the candidate.
4. Hand the candidate, original-source mapping, check results, review reports,
   and any follow-up text to Kepler, then stop. Push only when instructed.
   Coding threads never create a PR/draft, mark ready, publish reviewed-head,
   or enqueue. Reaching handoff means completed, tested, independently vetted
   scope; formal review and CI must not carry implementation-completeness work.
5. If Kepler returns formal findings, resume IMPLEMENTATION, test and obtain
   required local reviews, then hand back the new candidate. Kepler keeps or
   returns the existing PR to draft and obtains the required formal reviews.
6. Include scope, exclusions, compatibility, tests, output/performance evidence,
   observability review, and residual risks in the handoff; Kepler writes the PR.

## Issue Completeness and Independent Local Review

The owner must read the original GitHub issue body, all discussion, acceptance
criteria, linked governing specifications, and approved scope decisions before
claiming done. Record source links and the read/snapshot time so the reviewer
can independently inspect the same requirements. Roy's latest instructions win
conflicts; cite the clarification rather than silently narrowing scope.

Before independent local review, prove each in-scope requirement through
implemented behavior and local evidence at the candidate SHA. Deriving tests
or acceptance criteria only from what the implementation happens to do is
insufficient. Evaluate normal, negative, failure, cancellation, recovery,
durable-state, and interaction cases, with explicit N/A reasons for any case
that does not apply. For docs-only work,
inspect the required policy decisions and cross-document consistency; for
mechanical work, verify the claimed invariant. Neither needs invented runtime
tests. Preserve assigned HIGH/LOW risk tiers and existing tier-specific local
gates and review depth; completeness is required at either risk level.

Any missing in-scope behavior or required evidence returns the owner to
**IMPLEMENTATION** before review or PR creation. A partial slice may proceed
only when its scope is explicitly authorized and independently complete. Name
the remaining parent requirements and their tracking; keep the parent issue
**OPEN**, and omit closing keywords for it until its entire scope is satisfied.
An owner cannot silently turn an incomplete issue into a completed slice.

The independent local reviewer must read the original sources and inspect the
implementation and evidence, checking the mapping for omissions independently.
Return a separate completeness verdict, supporting evidence, and uncovered
requirements alongside code findings. A code-only CLEAN result, green CI, or a
coverage percentage is insufficient. A focused interaction review can preserve
previously established full completeness evidence through a delta; alone it
cannot establish full issue resolution.

Blocking incompleteness returns **CHANGES REQUESTED** and the owner resumes
implementation. Do not create a PR or advance CI/queue state. Keep an existing
PR draft through Kepler, who returns it to draft and withdraws queue
advancement when needed. The required loop is:

```text
reject -> IMPLEMENTATION -> local tests/evidence at new candidate SHA
       -> independent local review -> Kepler PR lifecycle
```

Missing requirements or evidence are acceptance blockers regardless of a Low
finding label and cannot be deferred through the correction cap. Classify each
finding using the distinctions below; a classification never excuses unmet
acceptance:

| Class | Required distinction and evidence |
| --- | --- |
| Existing bug | Original required behavior versus observed behavior; whether it predates the fix and whether it is in scope. |
| Test gap | Behavior may work, but a required case lacks evidence; do not equate a missing test with a proven runtime bug or a pass. |
| Fix-introduced regression | Before/after behavior showing the change broke a requirement or interaction. |
| Mechanical-only | The preserved invariant and direct checks establishing no behavior change; the label alone is insufficient. |

When hardware or state is unavailable, record what could and could not be
validated, the missing prerequisite, and the exact remaining check for
Kepler/Roy. Do not assert a pass. Required acceptance stays blocked until the
evidence exists or Roy explicitly changes scope. Preserve accepted coverage
exceptions; completeness does not reopen them or invent new coverage gates.

### Owner Evidence Template

Record original issue/discussion/specification links and source snapshot/read
time, approved scope and exclusions, branch, target base SHA, candidate SHA,
assigned HIGH/LOW risk and validation tier, classifier output, and gate results.
Use one row per in-scope requirement; distinguish expected and observed outcomes:

| Requirement ID and original source | Required outcome/cases | Implemented behavior/location | Local command or check, expected -> observed, evidence at candidate SHA | Status / gap |
| --- | --- | --- | --- | --- |
| R1 — issue AC/discussion link | Normal; negative; failure; cancellation; recovery; durable-state; interactions (N/A reasons where inapplicable) | Path/symbol or policy location | Command/check; result; artifact/log; SHA | PROVED / MISSING / BLOCKED |

Owner verdict: `RESOLVED | IMPLEMENTATION REQUIRED | BLOCKED` for the declared
scope. List remaining parent requirements and tracking, limitations, and the
next action. Only `RESOLVED` with no acceptance gaps is ready for independent
local review; only the required tier-specific CLEAN reports make it ready for
Kepler handoff.

### Reviewer Evidence Template

Record request ID, independent reviewer identity, actual provider/model/effort,
exact base..candidate range, original sources personally read, and evidence
inspected or reproduced. For every requirement independently evaluate normal,
negative, failure, cancellation, recovery, durable-state, and interaction cases;
record evidence or an explicit N/A reason for each inapplicable case:

| Requirement/source | Expected versus observed behavior and evidence at candidate SHA | Verdict | Uncovered requirement/evidence, finding class, required action |
| --- | --- | --- | --- |
| R1 — original AC/discussion link | Per-case checks/results, including cancellation and durable-state or N/A reasons; implementation location | COMPLETE / INCOMPLETE / BLOCKED | None, or concrete gap and owner action |

Report `Completeness: COMPLETE | INCOMPLETE | BLOCKED`, `Review: CLEAN | CHANGES
REQUESTED | BLOCKED`, uncovered requirements (`none` only if verified), prior
finding dispositions where applicable, limitations, and the exact next action.
Any blocking incompleteness requires `CHANGES REQUESTED` and implementation;
missing required environmental evidence must not be reported as COMPLETE or
CLEAN. A code-only CLEAN without the requirement-level completeness verdict is
insufficient. Return every prior High/Medium finding individually verified
corrected/non-actionable or unresolved; only Lows may have follow-up disposition.

These verdicts are agent/Kepler policy. GitHub may reject native
`request_changes` submitted by the PR author; record the actual limitation and
the process verdict without fabricating a native review or approval. Existing
branch protection and review statuses do not natively prove issue completeness.
PR/readiness/reviewed-head/queue writes belong exclusively to Kepler.

## Review Request Contract

Every required Sol/Opus review request must state the following information,
adapted to its execution route. Independent one-shot reviews need no fleet
enrollment. Human/bot reviews supplement, but never replace, required reviews.
For pre-PR local review, PR fields are `N/A` and requests, acknowledgements,
and reports live in the local evidence pack. References below to posting on a
PR apply only after an authorized PR exists.

```text
Review mode: initial | correction | base-sync
Review stage: pre-PR local | formal PR
Issue and PR: (PR N/A before creation)
Review request ID and reviewer identity:
Dispatch command ID and target participant ID: (enrolled resumed-session route only)
Acceptance criteria:
Original issue body, discussion, governing sources and read/snapshot time:
Approved scope, exclusions, and remaining parent requirements:
Candidate completeness mapping and prior local independent review report:
  (report N/A for the first local review)
Target base SHA: (the observed tip of the base branch read by branch name;
  provenance and mergeability context, not a latest-base submission gate or
  the initial range's left endpoint, which is the computed merge base;
  tip movement alone does not invalidate unchanged-head review evidence)
Target base ref: (the remote and branch used to transport that tip, verified
  to contain it; transport only, never the source of the endpoint)
PR-recorded base SHA: (baseRefOid; provenance only, never a range endpoint)
PR merge-base SHA:
Previous reviewed head SHA:
Previous reviewed head selection: (the previous round's durable request link,
  or a caller assertion marked as unverified; never the
  previous reviewer's prose)
Current head SHA:
Exact review range:
Correction rereview count: <N> (legacy three-round checkpoint; never a waiver)
Required risk tier: LOW | HIGH
Required reviewer set and same-candidate report links:
Required capability lens:
Requested capability profile: fast | standard | deep
Primary required Sol/Opus reviewer/provider:
Fallback execution route: (same required reviewer family only)
Execution route: independent one-shot agent | enrolled resumed-session agent | equivalent exact-range runner
Requested Sol/Opus model and reasoning effort:
Actual provider, model, and reasoning effort: (all required values verified)
Supplemental human/bot identity: (optional; does not satisfy required reviews)
Tests/cases to evaluate: (normal, negative, failure, cancellation, recovery,
  durable-state, interactions; explicit N/A reasons where inapplicable)
Local evidence:
Prior report selection: (explicit link to the previous round's full report)
Prior findings and dispositions:
Prior-finding verification checklist (finding ID/link, expected disposition,
  and evidence location):
Evidence pack:
Expected output: (reviewer identity, request link/ID, exact reviewed range,
  completeness verdict with evidence and uncovered requirements, review verdict,
  findings, and individual carried-finding dispositions)
Start acknowledgement: within 15 minutes for required agents; identify the
  reviewer identity, execution route, exact range, and actual provider/model/effort.
```

### Independent Reports and Supplemental Human Review

Required independent Sol/Opus executions can use a one-shot agent, an enrolled
resumed session, or an equivalent exact-range runner with verified actual
model/effort. The route does not change the named-reviewer requirement. Humans
and provider-side bots can add evidence but cannot supply the required CLEAN.

The coding owner records requests and reports in the local evidence pack;
Kepler records formal PR requests, acknowledgements, and attributed full reports.
Use durable request IDs and actual reviewer identity, without inventing fleet
IDs or native GitHub approvals. The report must retain the reviewer's actual
result, evidence, immutable endpoints, and requirement-level completeness.

For correction/base-sync rounds, link the prior request and full report and
verify the previous reviewed head against the request/history. Carry every
prior finding into the checklist. An asserted endpoint remains unverified until
checked; never infer it from reviewer prose or an empty summary. Required
Sol/Opus reports must name the same candidate, even when the review range is a
bounded delta. A reviewed old head cannot stand in for the new candidate.

### Enrolled Resumed-Session Script Route

For this route, generate the request with `scripts/pr:review-request` and inspect
it before launch. Its existing field names supply the equivalent contract above;
the dispatch command/participant binding identifies the request and reviewer.
Do not change generated fields to imply unsupported script modes.
The existing `scripts/pr:dispatch-review` supports enrolled
resumed Codex or Claude CLI sessions only; it has no human or unenrolled one-shot
mode. Enrollment and the dispatch-specific fields and guards below are required
only when using this route, not for all reviewers or PR owners.

Dispatch with `scripts/pr:dispatch-review`, which must receive the durable
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

### Sandbox Requirements

Agent reviews use a read-only sandbox regardless of enrollment or launch route.
If a CLI reports that its read-only sandbox cannot execute, mark the attempt
`INCOMPLETE` and use another supported independent execution of the same
required Sol/Opus reviewer. Supplemental human review cannot replace it.
Do not bypass the sandbox merely to make the review run; any intentionally
unsandboxed route requires separately verified external
isolation and an explicit ledger record.

## Review Execution and Agent Selection

The implementer cannot review their own candidate to convergence. A required
Sol/Opus review is a separate independent execution that can inspect the exact
immutable range and original requirements. Record actual provider, model,
reasoning effort, request ID, range, and candidate SHA. A PR mention, model
alias, native approval, or range-less bot summary does not establish that the
required reviewer ran. The launcher must actually expose the selected Sol/Opus
model; missing availability blocks instead of authorizing a different model.

Choose review depth separately from required reviewer count:

| Profile | Use | Depth |
| --- | --- | --- |
| fast | Supplemental mechanical inventory only | Never replaces a required Sol/Opus review |
| standard | LOW, bounded implementation/docs/tests and isolated non-safety fixes | Full initial review; normally high reasoning effort for review |
| deep | HIGH or a material High/Medium correctness finding | Strong available Sol/Opus reasoning; high or stronger effort as warranted |

Roy may explicitly prescribe a docs-only local Sol review at LOW reasoning
effort, as distinct from the LOW risk tier. That exception does not reduce HIGH
review counts/depth or permit executable-gate work under a docs-only scope.
A/B/C/M validation tiers retain their local gate obligations. An isolated
correction or base-sync can bound the review range, not remove a required
reviewer. Any changed candidate needs LOW's Sol or HIGH's Sol and Opus CLEAN on
that same SHA; unchanged-head age or target-tip movement does not require rerun.

Review modes normally have these ranges; an explicitly requested full new
diff review covers the entire merge-base-to-candidate diff instead:

- **Initial:** review the complete merge-base-to-head candidate/PR diff and
  independently assess issue completeness against the original sources.
- **Correction:** review only the previous-reviewed-head-to-current-head delta
  and the effects that delta has on surrounding code. Retain the initial full
  completeness mapping and assess any affected requirements; a focused delta
  review alone is not proof of full issue resolution. Verify every prior
  finding individually as `verified corrected`, `verified deferred` with a
  linked Low follow-up issue and rationale, or `unresolved`. High/Medium findings
  cannot be deferred. A prior finding that is neither
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
Ask the same reviewer to correct the report within the active acquisition
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

For each required Sol/Opus review, record the exact candidate/range, selected
depth, actual model/effort, and STARTED acknowledgement. Before PR creation the
coding thread retains that record locally; Kepler records formal PR activity.
Require a valid start within fifteen minutes, then report unavailability and
retry only an execution route for the same required reviewer family. A started
review gets milestone reports and an interim update at least every thirty
minutes; follow the harness's completion/wait instructions.

If a required reviewer cannot run, record BLOCKED and a resumable handoff to
Kepler. No availability waiver, timeout, provider switch to a different model,
or supplemental human/bot review supplies CLEAN or allows advancement. For HIGH,
both Sol and Opus must still return CLEAN on the same candidate. Stop stale
requests on a changed head and obtain the required reports for that new SHA;
never copy the old verdict. Any substantive late High/Medium finding reopens
implementation. Elapsed time is never approval.

## Corrections, Severity, and the Legacy Rereview Cap

High and Medium findings require implementation or evidenced non-actionability,
then affected local tests and independent verification. Missing in-scope
behavior/evidence is at least Medium and never Low. Return to IMPLEMENTATION;
Kepler keeps/returns the PR to draft and blocks CI/queue advancement.

Lows become follow-up issues through Kepler, without another correction round
solely for the Low. Supply source evidence, expected/observed behavior, residual
risk, and follow-up acceptance in the handoff; Kepler records the issue link.
Only Low findings may receive `verified deferred` disposition. A deferred item
that violates current acceptance or fails required CI is a High/Medium blocker,
not an eligible Low.

Each rereview must name the exact range/candidate, independently assess affected
requirements, and individually verify every carried finding. An omitted finding
remains unresolved. LOW still requires Sol CLEAN; HIGH still requires Sol and
Opus CLEAN on the same candidate at each required local/formal stage.

The legacy three-correction-round budget is an escalation/checkpoint limit,
not a convergence rule. Escalate remaining blockers to Kepler and continue the
implementation/test/review loop; never deem the third head approved, defer a
High/Medium, waive a required reviewer, or advance while incomplete. Low
follow-ups do not need to wait for that budget.

Convergence requires complete original-source evidence, local and required
formal CLEAN reports from the named tier reviewers on the CURRENT PR HEAD SHA,
verified disposition of every High/Medium finding, and Low follow-up tracking.
Kepler separately validates required CI and conversations before the gate/queue.

## Merge-Queue Head Identity

For the operated merge queue, eligibility is evaluated on the **CURRENT PR HEAD
SHA**, not the destination-branch tip or synthetic merge-group SHA. A PR being
behind the target tip, or its age, alone does not invalidate unchanged-head
reviews, require automatic base-only synchronization or re-testing against
latest `development/v1`, or block submission/resubmission. Roy deferred the
stricter latest-base pre-submit policy: monitor merge queue and CI first; adopt
stricter policy only if evidence shows a need and Roy authorizes it. This rule
governs any synchronization language elsewhere in the lifecycle procedure.

Actual CONFLICTING merges and real product conflicts still require resolution.
New commits on the PR require applicable local evidence and independent review,
including base-sync review when integration changes the head. Queue CI validates
the combined group separately. This does not authorize queue cutover, change
queue gates, or substitute group CI for completeness or required reviews.

## Kepler's Operated Merge-Queue Route

This is the operated `development/v1` route. These steps belong to Kepler alone;
coding threads stop at handoff. Older pending-rollout/default-auto-merge prose
does not replace this operated route or authorize a coding thread to finalize.

1. Kepler creates/maintains the draft from a complete, locally vetted candidate,
   obtains the required formal Sol/Opus reviews for its tier, and records the
   disposition of all findings. Any incompleteness or High/Medium returns to
   IMPLEMENTATION.
2. After formal convergence Kepler marks ready and waits for the target profile's
   required CI on that exact head. Draft-skipped results do not count. For v1,
   use Preflight and Build and Unit, not an unsolicited full qualification run.
3. Immediately before publishing the gate, Kepler re-reads the **CURRENT PR HEAD
   SHA** and verifies complete original-requirement evidence, required local and
   formal CLEAN reports (LOW: Sol; HIGH: Sol and Opus, all on that head), passing
   required CI, resolved conversations, and no actual merge/product conflict.
   A changed head returns to the appropriate implementation/test/review loop;
   target-tip movement or age alone does not invalidate unchanged-head reviews.
4. Kepler alone sets `hvo/reviewed-head=success` on exactly the revalidated head.
   This is an operator commit status, not proof that GitHub natively assessed
   issue completeness. Never copy it from another candidate or queue commit.
5. Kepler revalidates the head and enqueues with GraphQL `enqueuePullRequest`,
   binding `expectedHeadOid` to that head. An example mutation for Kepler is:

   ```graphql
   mutation EnqueueReviewed($pr: ID!, $head: GitObjectID!) {
     enqueuePullRequest(input: {pullRequestId: $pr, expectedHeadOid: $head}) {
       mergeQueueEntry { id }
     }
   }
   ```

6. Kepler verifies the returned queue entry and monitors queue integration CI
   and merge. A conflict, failed required check, changed head, or blocking
   finding goes back to the owner through Kepler; queue/group CI cannot replace
   completeness or reviews. Preserve existing conflict resolutions.

Under the operated queue, **`gh pr merge --merge` warns and does NOT enqueue**.
It is not the queue-admission command; use the GraphQL mutation. Do not silently
switch to direct/auto-merge when enqueueing is unavailable or fails. The current
PR head owns eligibility; queue CI evaluates the combined group. The stricter
latest-base pre-submit/resubmit policy remains deferred as stated above.

## Kepler Direct/Auto-Merge Fallback

This is a separate route, used only by Kepler when the queue is unavailable or
an operator explicitly selects the fallback. It is not the default queue path.
Coding threads still cannot create PRs, mark ready, set the gate, enqueue, or
perform fallback finalization. `main`/release promotion remains operator-approved.

Kepler revalidates the same current-head completeness, required local/formal
Sol/Opus CLEAN reports, required checks, conversations, and conflict conditions
as the queue route. Kepler controls readiness and, where required, sets
`hvo/reviewed-head` on exactly that head. A direct merge uses
`gh pr merge <n> --merge --match-head-commit <sha>`; an explicitly selected
fallback auto-merge uses `gh pr merge <n> --auto --merge --match-head-commit <sha>`.
These commands are fallback merge operations, not queue admission.

Any configured direct-merge freshness restriction governs merge execution;
waiting for integration does not impose a latest-base submission requirement.
Kepler disables auto-merge and returns the PR to draft before authorizing a
correction push; auto-merge does not disable itself on a write-access push.
Any new head, including an integration merge, needs applicable local evidence
and the required tier reviewers CLEAN on that SHA. An unchanged-diff/base proof
may help review but cannot carry forward old CLEAN reports or the gate.

## Finalization Lock and Target-Branch Synchronization

This is a Kepler-only fallback procedure, not queue admission or coding-thread
authority. Kepler uses it when the fallback cannot use auto-merge or the operator
reserves merge. Kepler uses `workflow:finalizing` on the PR as the repository-wide
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
2. Verify the current PR head still matches its reviewed SHA and inspect
   structural mergeability and any reported product conflict. Resolve actual
   CONFLICTING merges or real product conflicts; target-tip movement or age
   alone requires no base merge, re-testing, or new review. If resolution needs
   a target merge, preserve existing conflict resolutions and reviewed history;
   do not rebase or force-push it.
3. After conflict resolution or another authorized integration changes the
   head, run affected local gates and obtain a base-sync review. Use `standard`
   when there was no conflict,
   shared-file/contract overlap, or material changed interaction; use `deep` for
   any of those conditions. Run the CI-control guards on the merged head before
   requesting that review, and do not expect the review to substitute for them:
   a base-sync review verifies that the merge preserved both sides' behaviour,
   which it can do correctly while a classifier lane that each side satisfied
   separately is violated by their union. PR #740 is the case; its base-sync
   reviewer said so itself rather than letting a CLEAN verdict look wider than
   it was.
4. Re-read the current PR head and confirm it is mergeable and reviewed. A
   changed head requires applicable evidence and review; an advanced target
   alone does not require synchronization or re-testing.
   At this draft pre-ready gate, use the provider's structural mergeability
   result: for GitHub, require `mergeable: true`, retry a bounded `null` or
   unknown result, and stop on `false` or conflicts. Do not require
   `mergeable_state: clean` here. The intentionally failing draft `Required CI`
   placeholder keeps policy state `blocked` until the ready transition starts
   protected CI. `behind` alone does not invalidate head reviews or require
   base-only synchronization; actual merge or product conflicts still block.
5. Kepler alone marks the PR ready only now. This starts the target branch's
   authoritative protected CI profile: Development v1 checks for v1 PRs,
   classifier-selected full CI for main/release PRs.
6. Kepler holds the lock through CI and merge. Kepler alone publishes the
   reviewed-head gate after revalidating the exact candidate, completeness, all
   required tier reviews, fresh required checks, and resolved conversations as
   in the fallback route. The coding thread never publishes it. The operator's
   merge decision is unchanged: publishing the gate does not merge anything.

If actual conflicts or a product correction require a changed PR head during
CI, return the PR to draft, release the lock, resolve, test, and review the new
head before re-entering finalization. Cancel checks tied to a superseded head
when possible. Target-tip movement alone does not cancel head reviews or
require this loop; queue CI handles combined integration. A diagnosed
infrastructure-only, timeout, cancellation, or flaky failure on the same SHA may
retain it for one bounded rerun. Releasing the lock means removing the
`workflow:finalizing` label and recording the reason in the ledger.

## Protected CI, Merge, and Cleanup

PR-state actions in this section belong only to Kepler. Coding owners supply
corrections and local evidence, then stop at handoff.

- Investigate every non-green result. Never suppress a symptom, weaken a test,
  or accept a canceled, missing, timed-out, or stale check as success.
- A product correction returns the PR to draft, runs affected local gates, and
  receives correction review before another ready transition. Do not repeat
  unchanged successful local gates.
- On a target that requires it (`development/v1`), Kepler publishes
  `hvo/reviewed-head` on exactly that head before merging, whichever route is
  used. Never publish it on a head that review did not converge on, or before
  fresh required checks have passed.
- Kepler merges only when the current head has all required tier-specific
  CLEAN reports, the PR has no unresolved merge or product conflicts, applicable
  required head/queue checks are green, and actionable conversations are resolved.
- After merge, confirm closure only for fully resolved issues. For a partial
  slice, record what shipped and keep its incomplete parent issue open. Update
  the owning roadmap epic when applicable, and synchronize local `main`.
  Clean up the PR's disposable
  resources: those created only for this PR and used by nothing else. That
  means:
  - its worktrees and local branch, and the remote branch if it remains;
  - its scratch directories and review checkouts;
  - containers, images, volumes and networks created only for its own tests;
  - test databases and processes it left running.

  Never remove any of these, even if this PR created or started them:
  - shared infrastructure, such as the shared-services stack and its volumes
    (for example `skymonitor-redis-data`);
  - another PR's or session's worktrees;
  - deployed Pi or runtime state;
  - release, validation or evidence artifacts kept under their retention
    policies.

  When ownership is unclear, leave the resource and say so on the issue. Keep a
  disposable resource only when a recorded follow-up needs it, and say what and
  why on the issue. Then release the finalization lock if this PR held it, and
  report completion.
  Start or select another issue only with explicit operator authorization,
  including an existing explicit multi-issue assignment or an `agent:ready`
  queue label; completing this PR is not that authorization.

## Required Ledger Entries

Kepler appends PR-ledger entries; coding threads retain local evidence and
report to Kepler without PR-state writes. Append rather than replace entries
for review dispatch, acknowledgement,
provider timeout or switch, completed review, finding disposition, correction
range, escalation and Low follow-up issue, unavailable-review blockers,
finalization lock, base synchronization,
ready transition, CI terminal result, merge, and cleanup. Every entry includes a
UTC timestamp, relevant SHAs, what finished, what is running, the next step, and
any blocker.
Before PR creation, retain the original-source requirement mapping, candidate
evidence, explicit owner completeness verdict, and independent local report in
the local evidence pack. Carry them into the authorized Kepler/PR handoff.
