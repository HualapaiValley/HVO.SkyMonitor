# Multi-Developer SDLC Assessment

Assessment date: 2026-09-16
Evidence base: `b7237a42e84460985e1dc2cd02fb0ec22edcff9f`

## Status

The operator approved rollout through #847 after this assessment. Developer-owned
claims and explicitly dispositioned technical dependencies are implemented in
AGENTS.md, the execution protocol, and the roadmap. This assessment remains a
dated rationale, not an override of those sources. CI optimization, artifact
reuse, and merge-queue proposals below require their own reviewed implementation;
this rollout removes no protected checks or release-evidence requirements.

## Diagnosis

The current process is optimized for one operator directing a coordinated agent
fleet. It couples ordinary contribution to coordinator availability, limits the
whole repository to two implementation slots, treats portfolio priority as a
technical start dependency, and holds a repository-wide finalization label
through review and CI. More developers would mostly create a longer queue.

The strengths to retain are isolated workspaces, explicit architecture ownership,
immutable review ranges, focused correction reviews, reproducible evidence,
fail-closed required checks, and independent validation before integration.

Long CI is not simply excessive integration testing. The available sample shows
runner contention, a dominant serial test assembly, repeated compilation, and
classification broadening. Each needs a different remedy. Local developer tests
cannot replace protected checks: Tier A/B currently relies on protected CI for
evidence it is explicitly not required to reproduce locally.

## Proposed Contribution Model

### Ownership And Claims

- Any authorized contributor may claim an approved, unowned, technically ready
  story. A coordinator may manage an opted-in fleet but is not required for
  every contributor.
- Use the issue assignee for the accountable GitHub owner. Record an agent's
  attributable session identity separately when applicable. Never share human
  credentials merely to conform to a coordination convention.
- Post a claim containing scope, branch/worktree, technical prerequisites,
  expected shared files/resources, and next checkpoint. Re-read current issue
  ownership before editing. Conflicting claims pause for explicit resolution.
- GitHub comments, assignees, and labels are not an atomic distributed lock.
  Automating exclusive claims requires a trusted serialized service; do not
  pretend a read followed by a label write provides that guarantee.
- One implementation owner per story and one editing owner per worktree.
  Reviewers and evidence helpers can participate without taking implementation
  ownership. Waiting for review/CI does not consume a global development slot.
- Limit expensive concurrency per host, Docker daemon, database, signing
  resource, or physical device. Ten developers do not imply ten simultaneous
  integration/performance campaigns on the same machine.
- An abandoned claim needs a handoff and explicit release/transfer. No automatic
  takeover based solely on elapsed time while a developer may be working offline.
- Automatic consumption of the next story is opt-in. Completing an assigned
  task does not enroll a human or agent in an indefinite roadmap loop.

### Issue Metadata

Use one source for each fact rather than multiplying overlapping tags:

| Fact | Proposed representation |
| --- | --- |
| Accountable owner | GitHub assignee plus agent attribution if needed |
| Lifecycle | Project Status: Triage, Ready, In Progress, Review, Integration, Done |
| Risk | One `risk:A`, `risk:B`, `risk:C`, or `risk:M` label |
| Ownership | One primary existing `component:*` label; explain consumers in scope |
| Portfolio | Existing `roadmap:RM-*` label and appropriate milestone |
| Priority | Project priority field; not a fake `blocked_by` edge |
| Technical dependency | GitHub relationship naming the prerequisite deliverable |
| Blocked state | Reason and next action in the issue; optional `status:blocked` label |

These are proposed fields/labels, not a claim that they exist or are enforced.
If Projects is unavailable, use documented lifecycle labels instead of keeping
two independent status systems. Milestones express outcomes, not developer locks.

### Story Readiness And Slicing

A Ready story has a bounded outcome, explicit exclusions, owner component,
acceptance and negative cases, technical prerequisites, validation tier, and
known contract/resource overlap. Mark missing decisions as discovery work.

Distinguish three gates:

1. Start: approved scope and stable inputs exist.
2. Integration: required implementations and compatibility tests are available.
3. Release: qualification, signed artifacts, and end-to-end acceptance pass.

Use `blocked_by` only for a named technical deliverable or required decision.
Priority-only holds belong in planning metadata. Narrow an epic dependency to
the necessary contract checkpoint when independently testable work is possible.
Do not split a transaction, canonical schema conversion, or security boundary
across independently mergeable PRs merely to increase the story count.

Prefer independently usable vertical changes. An internal contract/scaffold
slice is acceptable with executable conformance fixtures and no enabled partial
production behavior. Stacked branches need explicit coordination; their CI and
review evidence must be re-evaluated after dependencies merge.

## Proposed Review Model

- The PR owner requests review; no global coordinator is required.
- A qualified independent human or agent can review. Human model/effort fields
  are not applicable. Agent provider/model/effort must be truthfully recorded,
  not inferred from aliases or prose requests.
- Initial review covers the complete immutable PR range. Corrections cover the
  delta and concrete interactions, with each carried finding dispositioned.
- Ordinary A/B changes use one independent review. Promote security, durability,
  concurrency, migration, CI-control, and material cross-boundary work to deeper
  capability. A second reviewer is justified by risk, not the number of prior
  rounds or a blanket two-provider requirement.
- A reviewer can verify a bounded non-behavioral correction in the existing
  thread against a new SHA; a new agent session is not itself evidence of quality.
- Keep the existing bounded non-blocking follow-up policy. Security, data-loss,
  failing-CI, acceptance, and material-correctness findings remain merge blockers.
- When corrections repeatedly fail, reconsider the design and reproducer rather
  than continuing an unbounded series of small patches and nominal deep reviews.
- Base advancement requires an integration assessment. Conflict-free unrelated
  changes need recorded overlap analysis and affected tests; independent
  base-sync review is reserved for conflicts or material interactions. A clean
  textual merge alone does not establish independence.
- Review unavailability is not approval. Escalate to an authorized maintainer;
  an agent should not self-authorize a waiver that bypasses required review.

The existing resumed-session dispatcher remains useful for managed fleets. Add
human and one-shot review routes without weakening its lease, duplicate-dispatch,
sandbox, or ambiguous-launch protections. Fleet enrollment applies to consuming
fleet commands and editing leased slots, not to ordinary authorized development.

## Integration And Release

Independent branch development and review should proceed concurrently. PR CI
can also proceed concurrently once the implemented integration controls support
it. Prefer a protected merge queue that validates the actual integration
candidate and retains classifier and required-check semantics.

Before enabling a queue, verify repository plan/features, rulesets, permissions,
`merge_group` event support, base/head attribution, classification, and required
checks. They have not been verified in this assessment. The current workflow
does not implement merge-group validation. A queue must not be declared enabled
by documentation alone.

Until a replacement is implemented and tested, retain the current serialized
finalization fallback. Do not shorten the lock while accepting stale-base CI.
Eventually serialize integration per target branch, not all development or all
branches. Keep signing/release promotion separately authorized.

#535 still requires an immutable candidate and real campaign evidence. Other
developers can prepare work without changing that candidate. Releasing an older
candidate while main advances requires an explicitly approved release-branch
and evidence model; this proposal does not silently relax the current-head rule.

## Measured CI

The last ten completed PR runs at assessment time contained six intentional
draft-block failures, one reduced success, and three complete-matrix successes.
No component-only execution was sampled. Do not derive a p95, general failure
rate, or promised savings from this sample.

| Run / PR | Plan | Workflow elapsed | Sum of non-skipped job duration |
| --- | --- | --- | --- |
| 35148019772 / #846 | Reduced | 2m47s | 2m38s |
| 35141755039 / #845 | Complete + deployment | 19m07s | 62m07s |
| 35134229355 / #826 | Complete + deployment | 32m21s | 75m06s |
| 35130267380 / #827 | Complete | 20m06s | 55m11s |

Run URLs use `https://github.com/RoySalisbury/HVO.SkyMonitor/actions/runs/<id>`.
Elapsed is creation to final update. Summed job duration includes overlap and
is not CPU time or billed minutes. The six draft runs took 6-11 seconds each and
did not execute the expensive matrix.

Key observations:

- Integration took 16-17 minutes. LogicHost's 407 cases accounted for roughly
  72-75% of the Integration execution step and controlled two full-run paths.
- #826 Deployment waited 8m28s after its catalog prerequisite; Unit waited
  14m16s. The preceding main-push run 35132397626 occupied the same runners.
- Complete plans build Release four times, plus two migration project builds
  and publishing. Three additional solution restore/build steps consumed
  6m01s-9m14s per sampled run, but artifact reuse could delay the critical path
  if consumers must wait for a slower producer.
- Formatting took approximately eight minutes, but was not the terminal
  bottleneck in these full runs. Removing it would not imply eight-minute
  latency savings.
- Deployment already has ten shards with up to eight workers. In #826 the
  shard phase took 16m44s and `existing-down` finished nearly four minutes after
  the other last shard. More workers alone will not eliminate that long tail.
- `mode=full` differs from `complete=true`. Even component plans retain a common
  Build/Quality/Architecture/migration floor. Component tests still include
  affected integration boundaries.
- #827's central category-count file forced complete selection for mostly
  CameraAgent tests. #826's workflow edit selected exhaustive deployment work.
  #845's package train warranted the full matrix; its release workflow edit
  selected catalog/installer contracts, not every deployment shard.

## Implementation Plan

Do not bundle these changes into one SDLC-and-CI mega-PR. The first two stories
can proceed independently; subsequent stories depend on their specific inputs,
not on closure of an unrelated product milestone.

| Story | Scope and acceptance | Real dependency |
| --- | --- | --- |
| Contributor policy | Independent claims, optional fleet mode, ownership/status vocabulary, resource-scoped capacity; synchronize all instruction entry points and templates; demonstrate two contributors need no coordinator | Maintainer approval of policy |
| CI plan/timing ledger | Record immutable range, classifier reasons/flags, expected gates, ready/start/end times, run attempt; reproduce the sample without rerunning suites; distinguish drafts | None |
| Review routes | Human, one-shot, and enrolled resumed routes with equivalent range/findings evidence; preserve existing dispatch security tests | Contributor/review policy |
| Dependency cleanup and slicing | Disposition scheduling-only edges, preserve qualification/release gates, create small acceptance-complete children; verify live issue graph and roadmap agree | Approved readiness policy and owning maintainer decisions |
| Runner capacity | Measure daemon/host headroom; protect ready-PR capacity from main-push contention without starving either or dropping gates | Timing ledger and runner inventory |
| Release artifact reuse pilot | One consumer; bind bytes to checkout/SDK/configuration/attempt; preserve coverage and runtime files; reject mismatches; measure transfer and critical path | Timing ledger and artifact measurements |
| LogicHost integration optimization | Profile fixture lifetime and slow cases, then bounded isolation-preserving partitions; identical inventory and coverage slots | Timing ledger and Docker capacity |
| Deployment tail optimization | Measure/split independent `existing-down` scenarios and redundant fixture setup; retain watchdogs, cleanup, scenario inventory and logs | Timing ledger and topology capacity |
| Category inventory ownership | Per-project counts or equivalent ownership design; keep global discovery; engine/schema/unknown-project changes fail closed | Ownership design plus classifier/aggregator tests |
| Integration queue | Verify hosting support, implement merge-group validation and protected queue; prove stale candidate rejection and concurrent PR safety | Trusted ruleset/event design and measured capacity |

#792 should be discovery-first: skipping post-merge Quality is only one possible
optimization, and must be justified against merge-skew detection. It does not
replace the broader timing/capacity analysis. Keep #788's rejected removal of
protected PR evidence closed.

## Parallel Backlog Candidates

These are proposed candidates after dependency disposition, not new claims:

- #535: candidate/environment admission, real campaign execution, and independent
  aggregation can have separate owners; they contribute to one completion gate.
- #632: isolate the intermittent provider-lifetime defect with a reproducer.
  #613's allocation-model discovery can proceed separately, but overlapping
  allocator implementation needs coordination.
- #584: split provider contract/conformance, atomic identity/schema conversion,
  and selection/health integration. #592 needs a settled primitive API checkpoint,
  not necessarily completion of every #584 implementation detail.
- Preserve #585 provider completion -> #586 topology qualification -> #506
  supported adoption. Qualification preparation can start before implementation
  completes; qualification execution cannot.
- #538: delivered exporter contracts permit receiver/persistence discovery and
  bounded import implementation independently of CameraAgent campaign execution,
  subject to explicit removal of the current scheduling-only hold.
- #539: separate API audit, authorized shell, image/archive UI, and imported
  execution views. Only the imported-evidence views inherently need #538.
- #540: packaging/lifecycle design can precede storage qualification; production
  publication still requires #499. #541 final combined acceptance needs actual
  released artifacts and completed integration behavior.
- #803 is already small. #782 is an error-level enforcement tranche. Split #795
  into host-specific adoption under one diagnostic/privacy contract. Coordinate
  their evidence-producer changes with any active #535 campaign.

For seven to ten developers, allocate discovery, implementation, review, and
qualification preparation as well as coding. Do not manufacture ten code owners
for one tightly coupled allocator or migration.

## Policy Files To Reconcile

The contributor-policy story must update AGENTS.md, agent-execution.md,
agent-prompts.md, the PR lifecycle skill, agent-host-onboarding.md, the roadmap,
project-plan.md sections 23/24, ci-pipeline.md protection guidance,
`.github/pull_request_template.md`, and the implement-project-phase prompt.
Keep CLAUDE.md and Copilot entry points as canonical-policy pointers.

Tool changes require matching tests in `test:pr-review-tools`,
`test:coordination-guard`, and `test:ci-classification`. Preserve managed-fleet
guards rather than making an unregistered command consumer silently valid.

Before any future merge-policy relaxation, audit actual GitHub rulesets and
permissions. The current documented absence of required approving review and
PR-controlled workflows are not an independent security boundary for untrusted
contributors. Attribution and a coordination label cannot replace that boundary.
