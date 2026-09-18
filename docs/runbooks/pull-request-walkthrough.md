# Pull Request Walkthrough

The exact sequence for taking an issue from claim to merge on `development/v1`,
with the commands, using PR #909 (the September dependency consolidation) as the
worked example throughout. [development-v1-review.md](development-v1-review.md)
is the rulebook; this is the procedure. Both apply to every repository that has
adopted the model in [repository-setup.md](repository-setup.md).

The shape:

```text
claim -> branch -> implement -> local gates -> draft PR
   -> review body on the branch -> bot posts it -> findings as threads
   -> corrections -> VERIFIED_* (bot) -> operator resolves -> ready
   -> required checks -> merge -> close issue -> (nightly) promote
```

Two roles run this, and on a single-operator repository they are two sessions
of the same person or agent: the **implementer** and the **independent
reviewer**. The reviewer never edits the branch; the implementer never posts
`VERIFIED_*`.

## 1. Claim

Before editing anything, look for an existing claim: assignee,
`workflow:in-progress`, a claim comment, a linked open PR. An existing claim
means take a different issue.

```bash
gh issue edit 908 --add-assignee @me --add-label workflow:in-progress --add-label review:standard
gh issue comment 908 --body 'CLAIM
Owner: <harness>:<provider>:<host>:<session>
Target: development/v1
Branch: feature/908-dependency-consolidation
Scope: <one line>
Next checkpoint: <what will be true at the next report>'
```

Pick the review level now and label it: `review:mechanical` (docs, links,
renames), `review:standard` (ordinary code), `review:deep` (CI control,
security, durability, concurrency, deployment, or anything cross-boundary). Add
`risk:*` labels for the deep triggers. Re-read the issue after claiming.

## 2. Branch and implement

```bash
git switch development/v1 && git pull --ff-only
git switch -c feature/908-dependency-consolidation
```

One issue per branch. Commit messages are sentences describing the change, not
the issue number ("Consolidate the September dependency updates and move to
.NET SDK 10.0.401"). Do not commit anything under `data/`, `App_Data/`, or
other runtime state.

## 3. Local gates before the draft

The tier decides how much. Every PR runs at least:

```bash
dotnet restore HVO.SkyMonitor.v9.slnx
dotnet build HVO.SkyMonitor.v9.slnx --no-restore -c Release -warnaserror
dotnet format HVO.SkyMonitor.v9.slnx --no-restore --verify-no-changes
git diff --check
```

Plus the focused tests for what changed. A PR that touches the shared
dependency graph, CI control, or a shell library additionally runs the gates
that graph feeds: the category audit, `scripts/test:ci-classification`,
`scripts/ci:shell-syntax`, `scripts/docs:audit-operations`, and, when
behaviour could move, the Integration selection locally. Record what you ran
and the numbers in the PR body; "tests pass" without counts is not evidence.

## 4. Open the draft

```bash
git push -u origin feature/908-dependency-consolidation
gh pr create --draft --base development/v1 --title '<sentence>' --body '<see template>'
```

The body follows `.github/PULL_REQUEST_TEMPLATE/development-v1.md`: the issue,
what changed and why, exclusions, local validation with numbers, the review
level and the lens the reviewer should apply. Drafts run hosted Preflight only;
the self-hosted Build and Unit job waits until the PR is ready, so review
happens before the expensive check, not after.

## 5. Review

The reviewer works from the exact range, verifies claims against the
repository and external sources rather than the PR text, and writes the review
body **as a file on the PR branch**:

```bash
head=$(git rev-parse HEAD)
cat > .agentcontrol/reviews/PR-909-R0-${head:0:8}.md <<EOF
REVIEW PR-909-R0-${head:0:8}
Level: Standard
Mode: Initial
Range: development/v1..$head (complete PR diff)
Reviewer: <identity>, posted as hvo-agentcontrol[bot]
Provider/model/effort: <actual values>

Verified against the repository and external sources, not the PR text:
- <one line per verified claim, stating how it was verified>

Findings: <count, or none>
Verdict: APPROVE | CHANGES_REQUIRED | BLOCKED — CONVERGED at $head
EOF
git add .agentcontrol/reviews && git commit -m 'Record the R0 review body for AgentControl to post' && git push
```

Then the bot posts it:

```bash
gh workflow run agentcontrol.yml --ref development/v1 \
  -f operation=post-review -f pr=909 -f body_file=.agentcontrol/reviews/PR-909-R0-${head:0:8}.md
```

The comment appears authored by `hvo-agentcontrol[bot]` with a trailer naming
who dispatched it, the head, and the run. The review file must then be removed
from the branch (`git rm`) before the PR is marked ready; the posted comment is
the durable record.

The first line of the file must be `REVIEW PR-<n>-R<k>-<head8>` or the workflow
refuses it. `R0` is the initial review; `R1`, `R2`, `R3` are correction reviews
and the level caps them (Mechanical 1, Standard 2, Deep 3).

## 6. Findings

Each finding is one resolvable review thread on a line of the diff, with a
stable ID and a severity:

```bash
gh api repos/HualapaiValley/HVO.SkyMonitor/pulls/909/comments \
  -f commit_id=$head -f path=<file> -F line=<n> -f side=RIGHT \
  -f body='F1 (Low) — <one-sentence summary>.

<what was observed, how it was observed, why it matters>

Required: <what would close it>'
```

Severity sets what happens: Critical and High always block and are never
deferred; Medium blocks unless the issue owner records a deferral to a linked
follow-up issue; Low and nits are at the reviewer's discretion but still get a
thread so the disposition is recorded.

The implementer replies in the thread with exactly one of `CORRECTED at
<head8>`, `DEFERRED to #<issue>`, `NON_ACTIONABLE because <reason>`, or
`SUPERSEDED by <what>`, pushes the correction, and the reviewer re-checks the
delta only (not the whole PR) and replies `VERIFIED_CORRECTED at <head8>`,
`VERIFIED_DEFERRED`, or `VERIFIED_NON_ACTIONABLE`. A correction review body
(`R1`) is posted via the bot the same way as `R0`, covering the delta and
stating each finding's disposition.

Then the **operator** resolves the thread. This is deliberately not a bot
operation: GitHub refuses `resolveReviewThread` to App installation tokens.

```bash
tid=$(gh api graphql -f query='query($o:String!,$r:String!,$n:Int!){repository(owner:$o,name:$r){pullRequest(number:$n){reviewThreads(first:50){nodes{id comments(first:1){nodes{databaseId}}}}}}}' \
  -f o=HualapaiValley -f r=HVO.SkyMonitor -F n=909 --jq ".data.repository.pullRequest.reviewThreads.nodes[]|select(.comments.nodes[0].databaseId==<F1 comment id>)|.id")
gh api graphql -f query='mutation($t:ID!){resolveReviewThread(input:{threadId:$t}){thread{isResolved}}}' -f t=$tid
```

A thread with no `VERIFIED_*` reply is unresolved, and branch protection will
not let the PR merge with it open.

## 7. Ready, checks, merge

Only after the latest review verdict is `APPROVE`/`CONVERGED` on the current
head, every finding has a verified disposition, every thread is resolved, and
the review file is off the branch:

```bash
gh pr edit 909 --add-label review:converged
gh pr ready 909
gh pr checks 909 --watch
gh pr merge 909 --squash --delete-branch
```

Squash is right for feature branches into `development/v1`: one commit per
PR, the PR number in the subject. (Promotion into `main` is the opposite: a
merge commit, so those squashed SHAs survive.) Pushing after the review
converged invalidates it; the reviewer must re-verify the new head.

Do not post a converged verdict before the required checks have run. This was
done once, on PR #907, and Preflight then failed twice on diagnostics the review
had dismissed. The verdict is a claim about the head; the checks are the
evidence for it.

## 8. Close the issue

```bash
gh issue comment 908 --body 'COMPLETE
PR: #909
Merged into development/v1 as <sha>
Review: PR-909-R0 (Standard, posted as hvo-agentcontrol[bot], <findings>) -> PR-909-R1 CONVERGED
CI: Preflight <t>, Build and Unit <t>
<what was delivered, with the numbers>
Not promoted to main.'
gh issue edit 908 --remove-label workflow:in-progress
gh issue close 908
```

"Not promoted to main" is literal: the nightly promotion carries it, and the
promotion PR lists the issue number. If a follow-up was discovered and not
taken, open it and link it here rather than leaving it in the comment.

## 9. What the nightly does with it

At 02:00 America/Phoenix, if `development/v1` is ahead of `main` and its head's
push run is green, `promote-main.yml` opens (or refreshes) one PR from
`development/v1` to `main` as the bot, listing every commit and the issues they
reference. `main`'s full pipeline runs on it. The operator merges it with a
merge commit. That is the only way `main` moves.

## Worked example: PR #909

| Step | What happened |
| --- | --- |
| Claim | #908 assigned, `workflow:in-progress`, `review:standard` |
| Implement | 9 Dependabot bumps, SDK 10.0.401 in 14 places, 6 further packages; 2 implementation commits (7 on the PR once review-body and correction commits are counted) |
| Local gates | restore clean, both builds warning-clean, format clean, category audit unchanged, Unit 3496/3496, Integration 676/676 across six assemblies |
| Draft | body listed every version, the CA2025 re-probe, and the Redis release-notes summary |
| R0 | verified digests against MCR, action SHAs against tag objects, Redis API surface against the source; no findings on the diff; **posted by the bot** |
| F1 (Low) | the review body was still on the branch; corrected by removing it |
| R1 | delta only; F1 `VERIFIED_CORRECTED`; **posted by the bot**; operator resolved the thread |
| Ready | Preflight 9s, Build and Unit 7m18s |
| Merge | squash, `3932273d` |
| Promote | carried to `main` by #911 the same night, merge commit `d999c172` |
