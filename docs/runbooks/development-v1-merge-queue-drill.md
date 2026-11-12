# Development v1 merge queue drill appendix

**Live drill: KNOWN LIMIT / HELD. Runner capacity: KNOWN LIMIT (Atlas).**

Roy must be watching; a repository administrator operates the later settings writes.
Kepler coordinates the freeze and the observer checks the readbacks.
The committed [inputs and restore payloads](development-v1-merge-queue-drill/)
and [offline verifier](development-v1-merge-queue-drill/verify.py) make this plan
reviewable from any checkout. No local owner handoff is required.

Full cutover and rollback drill plan — supplied for later supervised execution only

Refs #1747. No live write command below was executed by the implementation owner.

Preconditions and people:
- Kepler coordinates the window. Roy watches while a repository administrator operates the settings commands; a second observer verifies the snapshots and readbacks. Atlas coordinates capacity separately; no capacity work is needed to run this settings-only drill.
- Kepler freezes all queue admission, direct merges and auto-merge activity, and obtains sole ownership of the settings window. The queue must be empty and no open PR may have auto-merge enabled. If either condition fails, stop; this plan does not dequeue, disable another PR's auto-merge, submit a PR or merge anything.
- Have the evidence directory and rollback payloads on disk before the first write. Confirm the active account has repository-administration permission without printing credentials. Keep the freeze until the final checks pass.
- The captured policy is already queue-active. The deliberate drill delta is temporarily removing ONLY the merge_queue rule while retaining the active ruleset, every other rule, and classic branch protection; cutover then restores the original queue rule. No required check is removed or relaxed. This small design choice exercises the return from protected fallback without admitting work.
- This is a settings-control drill. It does not dispatch workflows or prove queue execution/runner throughput with a new PR. Those remain existing CI evidence and later normal Kepler activity.

BEFORE snapshot: 2026-11-12T17:07:11.397455+00:00. The repository ruleset list returned 2 entries (<100); only ruleset 26838822 targets a branch. The other ruleset targets release tags and is outside this drill. The effective development/v1 rule read identified the same branch ruleset. A historical read-only GraphQL query at 2026-11-12T16:18:59.463021+00:00 observed an empty queue; it is not current precondition evidence. Repeat that check during the supervised window. Only read-only GETs were used for this correction.

Classic branch protection, exact response JSON from GET /repos/HualapaiValley/HVO.SkyMonitor/branches/development%2Fv1/protection:

```json
{
  "url": "https://api.github.com/repos/HualapaiValley/HVO.SkyMonitor/branches/development/v1/protection",
  "required_status_checks": {
    "url": "https://api.github.com/repos/HualapaiValley/HVO.SkyMonitor/branches/development/v1/protection/required_status_checks",
    "strict": false,
    "contexts": [
      "Development v1 / Preflight",
      "Development v1 / Build and Unit",
      "hvo/reviewed-head"
    ],
    "contexts_url": "https://api.github.com/repos/HualapaiValley/HVO.SkyMonitor/branches/development/v1/protection/required_status_checks/contexts",
    "checks": [
      {
        "context": "Development v1 / Preflight",
        "app_id": 15368
      },
      {
        "context": "Development v1 / Build and Unit",
        "app_id": 15368
      },
      {
        "context": "hvo/reviewed-head",
        "app_id": null
      }
    ]
  },
  "required_pull_request_reviews": {
    "url": "https://api.github.com/repos/HualapaiValley/HVO.SkyMonitor/branches/development/v1/protection/required_pull_request_reviews",
    "dismiss_stale_reviews": false,
    "require_code_owner_reviews": false,
    "require_last_push_approval": false,
    "required_approving_review_count": 0
  },
  "required_signatures": {
    "url": "https://api.github.com/repos/HualapaiValley/HVO.SkyMonitor/branches/development/v1/protection/required_signatures",
    "enabled": false
  },
  "enforce_admins": {
    "url": "https://api.github.com/repos/HualapaiValley/HVO.SkyMonitor/branches/development/v1/protection/enforce_admins",
    "enabled": true
  },
  "required_linear_history": {
    "enabled": false
  },
  "allow_force_pushes": {
    "enabled": false
  },
  "allow_deletions": {
    "enabled": false
  },
  "block_creations": {
    "enabled": false
  },
  "required_conversation_resolution": {
    "enabled": true
  },
  "lock_branch": {
    "enabled": false
  },
  "allow_fork_syncing": {
    "enabled": false
  }
}
```

Queue/required-check ruleset, exact response JSON from GET /repos/HualapaiValley/HVO.SkyMonitor/rulesets/26838822:

```json
{
  "id": 26838822,
  "name": "development/v1 merge queue",
  "target": "branch",
  "source_type": "Repository",
  "source": "HualapaiValley/HVO.SkyMonitor",
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
  ],
  "node_id": "RRS_lACqUmVwb3NpdG9yec5BRzpczgGZhyY",
  "created_at": "2026-11-09T01:44:56.421-07:00",
  "updated_at": "2026-11-09T01:44:56.556-07:00",
  "bypass_actors": [],
  "current_user_can_bypass": "never",
  "_links": {
    "self": {
      "href": "https://api.github.com/repos/HualapaiValley/HVO.SkyMonitor/rulesets/26838822"
    },
    "html": {
      "href": "https://github.com/HualapaiValley/HVO.SkyMonitor/rules/26838822"
    }
  }
}
```

Preparation commands (local file work and read-only API operations):

```bash
set -euo pipefail
repo_root=$(git rev-parse --show-toplevel)
operator_gh_config="${GH_CONFIG_DIR:-${XDG_CONFIG_HOME:-$HOME/.config}/gh}"
export TMPDIR=$(mktemp -d /tmp/w1747.XXXX)
chmod 700 "$TMPDIR"
mkdir -p "$TMPDIR/home" "$TMPDIR/xdg"
export HOME="$TMPDIR/home" XDG_CONFIG_HOME="$TMPDIR/xdg"
export GH_CONFIG_DIR="$operator_gh_config"
repo=HualapaiValley/HVO.SkyMonitor
branch_api="repos/$repo/branches/development%2Fv1/protection"
rule_api="repos/$repo/rulesets/26838822"
evidence="$repo_root/docs/runbooks/development-v1-merge-queue-drill"
cp "$evidence/branch-protection.before.json" "$TMPDIR/protection.before.json"
cp "$evidence/ruleset-26838822.before.json" "$TMPDIR/ruleset.before.json"
cd "$TMPDIR"

canon_rule() {
  jq -S '{name,target,enforcement,conditions,rules,bypass_actors}' "$1"
}
canon_protection() {
  jq -S 'walk(if type == "object" then
    del(.url,.contexts_url) | with_entries(select(.value != null))
    else . end)' "$1"
}
canon_rule ruleset.before.json > ruleset.restore.json
jq '.rules |= map(select(.type != "merge_queue"))' \
  ruleset.restore.json > ruleset.fallback.json
jq '{
  required_status_checks: {
    strict: .required_status_checks.strict,
    checks: [.required_status_checks.checks[] |
      {context,app_id:(if .app_id == null then -1 else .app_id end)}]
  },
  enforce_admins: .enforce_admins.enabled,
  required_pull_request_reviews: (.required_pull_request_reviews | del(.url)),
  restrictions: (.restrictions // null),
  required_linear_history: .required_linear_history.enabled,
  allow_force_pushes: .allow_force_pushes.enabled,
  allow_deletions: .allow_deletions.enabled,
  block_creations: .block_creations.enabled,
  required_conversation_resolution: .required_conversation_resolution.enabled,
  lock_branch: .lock_branch.enabled,
  allow_fork_syncing: .allow_fork_syncing.enabled
}' protection.before.json > protection.restore.json
cmp "$evidence/ruleset.restore.json" ruleset.restore.json
cmp "$evidence/ruleset.fallback.json" ruleset.fallback.json
cmp "$evidence/protection.restore.json" protection.restore.json
canon_protection protection.before.json > protection.expected.json
queue_query='query { repository(owner:"HualapaiValley",name:"HVO.SkyMonitor") { mergeQueue(branch:"development/v1") { entries(first:100) { nodes { id pullRequest { number } } pageInfo { hasNextPage } } } } }'

read_policy() {
  gh api --method GET "$branch_api" > protection.current.json
  gh api --method GET "$rule_api" > ruleset.current.json
}
assert_original_policy() {
  canon_protection protection.current.json > protection.actual.json
  cmp protection.expected.json protection.actual.json
  canon_rule ruleset.current.json > ruleset.actual.json
  cmp ruleset.restore.json ruleset.actual.json
}
assert_empty_queue() {
  # GraphQL uses an HTTP POST for this read-only query; it has no mutation.
  gh api graphql -f query="$queue_query" > queue.current.json
  jq -e '(.errors == null) and (.data.repository.mergeQueue != null) and
    (.data.repository.mergeQueue.entries.pageInfo.hasNextPage == false) and
    (.data.repository.mergeQueue.entries.nodes | length == 0)' queue.current.json
}
```

Numbered cutover steps:

1. Establish the people/freeze above, then re-read the complete ruleset index and policy. Do not silently replace the recorded baseline if anything drifted. Stop for Kepler/Roy to replan instead.

```bash
gh api --method GET "repos/$repo" > repository.current.json
jq -e ".permissions.admin == true" repository.current.json
gh api --method GET "repos/$repo/rulesets?includes_parents=true&per_page=100" > rulesets.current.json
jq -e 'length < 100' rulesets.current.json
jq -S 'map({id,target,source,enforcement}) | sort_by(.id)' \
  "$evidence/rulesets-index.before.json" > index.expected.json
jq -S 'map({id,target,source,enforcement}) | sort_by(.id)' \
  rulesets.current.json > index.actual.json
cmp index.expected.json index.actual.json
read_policy
assert_original_policy
jq -e '.enforcement == "active" and .bypass_actors == [] and
  .conditions.ref_name.include == ["refs/heads/development/v1"] and
  .conditions.ref_name.exclude == [] and
  ([.rules[] | select(.type == "merge_queue")] | length == 1) and
  ([.rules[] | select(.type == "required_status_checks") |
    .parameters.required_status_checks[]] | sort_by(.context)) ==
  ([{"context":"Development v1 / Preflight","integration_id":15368},
    {"context":"Development v1 / Build and Unit","integration_id":15368},
    {"context":"hvo/reviewed-head"}] | sort_by(.context))' ruleset.current.json
```

2. Check that the queue is empty and no open PR has auto-merge enabled. The unfiltered PR enumeration must be shorter than its explicit limit. If not, stop and use exhaustive pagination before making any assertion.

```bash
assert_empty_queue
gh pr list -R "$repo" --state open --limit 1000 \
  --json number,autoMergeRequest > open-prs.current.json
jq -e 'length < 1000 and all(.[]; .autoMergeRequest == null)' open-prs.current.json
```

3. Operator-only future write: temporarily remove only the queue rule. Keep the admission/merge freeze. Immediately check the full fallback payload and unchanged classic protection. On any error, go directly to rollback.

```bash
gh api --method PUT "$rule_api" --input ruleset.fallback.json > fallback.write-response.json
read_policy
canon_rule ruleset.current.json > ruleset.actual.json
cmp ruleset.fallback.json ruleset.actual.json
canon_protection protection.current.json > protection.actual.json
cmp protection.expected.json protection.actual.json
jq -e '.enforcement == "active" and
  ([.rules[] | select(.type == "merge_queue")] | length == 0) and
  ([.rules[] | select(.type == "required_status_checks")] | length == 1)' ruleset.current.json
```

4. Operator-only future cutover write: restore the captured queue-active policy. Validate both layers and the empty queue immediately. No PR admission or workflow dispatch is part of this plan.

```bash
gh api --method PUT "$rule_api" --input ruleset.restore.json > cutover.write-response.json
read_policy
assert_original_policy
assert_empty_queue
```

5. The operator and observer check the exact restored queue values: MERGE, ALLGREEN, max entries to build 3, min/max entries to merge 1/3, wait 2 minutes, response timeout 45 minutes. Check the three required contexts in both protection layers, GitHub Actions app 15368 on the two CI contexts, and any source for hvo/reviewed-head. Retain the GET responses, command outputs and UTC times in Kepler's ledger. With those checks green, the temporary state is gone and the settings portion succeeded. Release the freeze only when Kepler explicitly concludes the supervised window. A new-PR end-to-end run is outside this drill.

Numbered rollback steps (valid from either write stage):

1. Keep the admission/merge freeze and the operator/observer present. Preserve the failed-stage responses. Do not remove a required check, publish a substitute status, change a timeout or bypass a gate to obtain green results.

2. Re-read classic protection. This drill never intentionally changes it. If it differs, the present administrator uses the exact captured protection payload below; an unexpected concurrent administrator change must first be reconciled with that administrator under the sole-owner window. The -1 app ID in the request explicitly restores any-source semantics for the null app ID in the snapshot. Signature policy is a separate API resource and is restored only if its boolean differs.

```bash
gh api --method GET "$branch_api" > protection.current.json
canon_protection protection.current.json > protection.actual.json
if ! cmp -s protection.expected.json protection.actual.json; then
  gh api --method PUT "$branch_api" --input protection.restore.json > protection.rollback-response.json
fi
gh api --method GET "$branch_api" > protection.current.json
signature_before=$(jq -r '.required_signatures.enabled' protection.before.json)
signature_now=$(jq -r '.required_signatures.enabled' protection.current.json)
if [[ "$signature_now" != "$signature_before" ]]; then
  if [[ "$signature_before" == true ]]; then
    gh api --method POST "$branch_api/required_signatures" > signatures.rollback-response.json
  else
    gh api --method DELETE "$branch_api/required_signatures"
  fi
fi
```

3. Restore the complete captured ruleset, including bypass actors, conditions, all non-queue rules and the queue parameters. The BEFORE state is queue-active, so successful rollback returns to active ALLGREEN/MERGE rather than leaving the queue disabled.

```bash
gh api --method PUT "$rule_api" --input ruleset.restore.json > ruleset.rollback-response.json
read_policy
assert_original_policy
assert_empty_queue
```

4. Operator and observer retain the final JSON/readback comparison and UTC time, confirm all three contexts and sources, and let Kepler end the freeze. If any API rejects a payload, a context/source differs, the queue is absent or nonempty unexpectedly, or comparison fails, keep the freeze and stop for Roy/Kepler. Do not discard unknown rule fields to make a request pass; replan from the captured responses. Live PUT acceptance has not been exercised by this thread.

Success criteria and limits:
- The fallback stage changes exactly one rule; classic protection and all required checks remain intact throughout the frozen window.
- Cutover and rollback comparisons reproduce the full mutable policy in the BEFORE snapshots, including any-source hvo/reviewed-head and the exact queue parameters. Server metadata such as updated_at can change and is not a policy value.
- No PR, status, workflow, device, runner or merge operation is performed by the drill commands. No live setting has been changed in this implementation thread.
- Runner capacity awaits Atlas's plan/labels. The live drill awaits Kepler's supervised window. Both are known limits, not additional implementation work or new issues.

## Committed offline restoration evidence

Run from any checkout using Python 3, Bash and jq:

    python3 docs/runbooks/development-v1-merge-queue-drill/verify.py

The verifier runs all seven Bash blocks with a private fake `gh` first on PATH.
Every API response and simulated write stays in private temporary files. It
checks the committed payloads against the plan's generators, restores every
captured mutable ruleset field (including `bypass_actors: []`), and checks classic
protection and signature policy. No live API command is executed by the verifier.

| Offline behavior case | Expected outcome | Recorded result |
| --- | --- | --- |
| Full cutover and rollback | Three simulated ruleset PUTs; exact baseline restored | PASS |
| Occupied queue | Stop before any write | PASS |
| Auto-merge enabled | Stop before any write | PASS |
| Ruleset enumeration reaches its limit | Stop before any write | PASS |
| Unexpected required-check context | Stop before any write | PASS |
| Bad readback plus protection/signature drift | Stop; rollback restores ruleset, classic protection and signature state | PASS |

Seven extracted Bash blocks also pass `bash -n`. Machine-readable outcomes and
input hashes are in [results.json](development-v1-merge-queue-drill/results.json);
the captured verifier output is in
[verification.txt](development-v1-merge-queue-drill/verification.txt).
These results prove offline behavior only. Live payload acceptance and the
supervised drill remain **KNOWN LIMIT / HELD**, with Roy watching and an admin
operating the writes. Capacity delivery remains **KNOWN LIMIT (Atlas)**.
