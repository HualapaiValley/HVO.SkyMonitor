# Development v1 merge queue operations

Refs #1747. Read-only settings snapshot taken 2026-11-12 UTC from
`GET /repos/HualapaiValley/HVO.SkyMonitor/rulesets/26838822`.
This document records policy; changing settings remains an operator action.

```json
{
  "id": 26838822,
  "name": "development/v1 merge queue",
  "target": "branch",
  "enforcement": "active",
  "conditions": {
    "ref_name": {
      "exclude": [],
      "include": [
        "refs/heads/development/v1"
      ]
    }
  },
  "rules": [
    {
      "type": "deletion"
    },
    {
      "type": "non_fast_forward"
    },
    {
      "type": "pull_request",
      "parameters": {
        "required_approving_review_count": 0,
        "dismiss_stale_reviews_on_push": false,
        "required_reviewers": [],
        "require_code_owner_review": false,
        "dismissal_restriction": {
          "enabled": false,
          "allowed_actors": []
        },
        "require_last_push_approval": false,
        "required_review_thread_resolution": true,
        "require_extra_approval_for_unattributed_changes": true,
        "allowed_merge_methods": [
          "merge"
        ]
      }
    },
    {
      "type": "required_status_checks",
      "parameters": {
        "strict_required_status_checks_policy": false,
        "do_not_enforce_on_create": false,
        "required_status_checks": [
          {
            "context": "Development v1 / Preflight",
            "integration_id": 15368
          },
          {
            "context": "Development v1 / Build and Unit",
            "integration_id": 15368
          },
          {
            "context": "hvo/reviewed-head"
          }
        ]
      }
    },
    {
      "type": "merge_queue",
      "parameters": {
        "merge_method": "MERGE",
        "max_entries_to_build": 3,
        "min_entries_to_merge": 1,
        "max_entries_to_merge": 3,
        "min_entries_to_merge_wait_minutes": 2,
        "grouping_strategy": "ALLGREEN",
        "check_response_timeout_minutes": 45
      }
    }
  ]
}
```

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
branch supplies the exact command plan and fresh read-only protection/ruleset
snapshots in the local owner handoff; it changes no live settings. The
not-yet-executed drill is a known operational limit, with no further Roy decision
needed for the approved activity. Refs #1747.

Roy also selected additional or relocated self-hosted runner capacity. Atlas
must report the infrastructure plan, available capacity and labels before any
workflow selector changes. That dependency is a known limit awaiting Atlas;
this branch retains existing runner labels, timeout budgets and test selection.
