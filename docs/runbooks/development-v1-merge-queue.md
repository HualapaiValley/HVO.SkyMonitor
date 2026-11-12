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

Roy also selected additional or relocated self-hosted runner capacity. Atlas
must report the infrastructure plan, available capacity and labels before any
workflow selector changes. That dependency is **KNOWN LIMIT (Atlas)**;
this branch retains existing runner labels, timeout budgets and test selection.
