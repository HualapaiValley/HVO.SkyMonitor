# Development v1 merge queue operations

Refs #1747. Read-only settings snapshot taken 2026-11-12 UTC from
`GET /repos/HualapaiValley/HVO.SkyMonitor/rulesets/26838822`.
This document records policy; changing settings remains an operator action.

The complete [ruleset snapshot](development-v1-merge-queue-drill/ruleset-26838822.before.json)
includes `bypass_actors: []` and every other returned field. The
[classic branch-protection snapshot](development-v1-merge-queue-drill/branch-protection.before.json)
is retained alongside it. See the [exact command appendix](development-v1-merge-queue-drill.md)
for the empty-queue admission/merge freeze, committed restore payloads,
readback stop conditions, and reproducible offline restoration evidence.

## Cutover checklist

- Record the existing ruleset and required checks before changing settings.
- Require Development v1 / Preflight, Development v1 / Build and Unit and
  hvo/reviewed-head on development/v1; queue mode is ALLGREEN with MERGE.
- Keep hvo/reviewed-head set to **any source** (no pinned integration id):
  the operator publishes a commit status on PR heads and Actions publishes the
  same context on temporary queue commits. Pinning either producer excludes
  the other. A future external App requires a separate reviewed migration.
- Verify all required checks on one reviewed candidate and its queue group;
  record the operator's observed settings and resulting SHAs in Kepler's ledger.

## Publish review approval

Kepler records complete issue evidence and the required independent CLEAN
reviews on the same reviewed head (Sol and Opus for HIGH work). The review
record includes the full reviewed head SHA and the merge base used for the
reviewed diff. After current-head CI passes and conversations are resolved,
run from a trusted checkout of `development/v1` with `gh` authenticated as
`RoySalisbury`:

```bash
./scripts/ci:queue-reviewed-head stamp "$pr_number" "$reviewed_head" \
  --reviewed-base "$reviewed_merge_base"
```

The publisher confirms the open PR and its target, fetches the protected ref
and the PR head, and checks the reviewed merge base before posting
`hvo/reviewed-head=success` on the reviewed commit. Record the published SHA
and the fingerprint in the PR ledger. A legacy status remains valid for its
exact SHA and can be stamped with a fingerprint while that reviewed head is
still current.

Before stamping a newly reviewed revision, Kepler must revoke every active
approval for that PR on another head. Use the PR ledger to identify the prior
reviewed SHAs and every head that received their carry, including legacy
approvals and SHAs outside the current PR history. Serialize these writes for
the PR. Dequeue any entry that relied on the prior approval and require a new
group check before re-entry.

Repeat `--supersede` for each of those SHAs when publishing the new stamp.

```bash
./scripts/ci:queue-reviewed-head stamp "$pr_number" "$reviewed_head" \
  --reviewed-base "$reviewed_merge_base" \
  --supersede "$prior_reviewed_head" --supersede "$prior_carried_head"
```

GitHub commit statuses are indexed by SHA, so Kepler supplies the full list
from the ledger. The publisher checks the authorized status on each supplied
SHA and validates versioned approvals against this PR's fingerprint. It
posts `failure` with `Superseded by <new_sha>` before publishing the new
success. If a revocation fails, reconcile the latest statuses and repeat the
command with the same list. The publisher skips prior statuses already marked
`failure` or `error` by the authorized publisher.

If the PR head advances solely through clean merges from `development/v1`,
revalidate current-head checks, conversations and any integration evidence.
Use the original reviewed SHA from the active versioned status.

```bash
./scripts/ci:queue-reviewed-head stamp "$pr_number" "$reviewed_head" --carry
```

The publisher verifies the original status and the full merge-only history.
It then publishes the verified approval reference on the current head. The
queue verifier requires that exact-head status and also checks that the
original approval is still valid. Record both SHAs in the ledger. Enqueue with
`expectedHeadOid` set to the current head. If review must be revoked after a
group gate has passed, dequeue the PR and require a new group check before
re-entry. A new PR edit, a conflict resolution or an inconclusive verification
returns to the ordinary local validation and formal review loop. The
[verification contract](../development-v1.md#diff-bound-review-approval)
describes the proof and legacy behavior.

## Rollback checklist

The supervised drill uses the numbered appendix and requires an empty queue.
This checklist describes ordinary fallback operations.

- Kepler records the reason and current queue entries, then the operator stops
  new admission and dequeues pending groups before disabling the queue rule.
- Preserve all three required checks, including hvo/reviewed-head with any
  source. Preserve the exact-head completeness, independent reviews, passing
  target-branch CI and conversation requirements.
- Use the authorized direct/auto-merge fallback in the pr-lifecycle skill,
  including its finalization lock; only Kepler performs fallback merges.
- Re-enable the queue only after the cutover checks pass and record a fresh
  settings snapshot. Never remove review requirements to recover availability.

## Approved operational follow-up

Roy authorized the live cutover/rollback drill in t421s2656 (2026-11-12,
8:14 AM PST). Kepler owns its later supervised execution. This implementation
branch supplies the [exact command plan and restoration evidence](development-v1-merge-queue-drill.md)
in the repository; it changes no live settings. The not-yet-executed drill is
**KNOWN LIMIT / HELD** until the later supervised window, with Roy watching
and a repository administrator operating the settings writes. Refs #1747.

Atlas delivered the initial 8-vCPU build pair (`gh-runner-05` and
`gh-runner-06`) on 2026-11-12 at 9:04–9:05 AM PST. Build and Unit selects
`[self-hosted, linux, x64, hvo-skymonitor-build]`; see the
[runner capacity runbook](ci-runner-capacity.md#build-and-unit-allocation)
for the current organization runner inventory carrying that label. Capacity
is ready and no longer a known limit. Timeout budgets and test selection stay
unchanged. The live drill remains **KNOWN LIMIT / HELD**.
