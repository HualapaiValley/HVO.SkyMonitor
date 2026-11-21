# Historical AgentControl review bodies

The `hvo-agentcontrol[bot]` reviewer role and its `post-review` publication route
are retired. Kepler publishes reviews without head-mutating review commits.

Formal review begins after draft checks pass on the exact PR head. Kepler
publishes reviewer output verbatim under the operator account, links the reviewer
sessions, and relays verification records before Ready. Follow the
[PR lifecycle skill](../../.agents/skills/pr-lifecycle/SKILL.md)
and the [common policy](../../docs/development/policy/common.md#authority-and-precedence).

The AgentControl App still opens or refreshes draft PRs for
[nightly promotion to `main`](../../docs/development-v1.md#promotion-to-main).

## Historical publication format

Review bodies were committed here on the PR branch as
`PR-<number>-R<round>-<head8>.md` and began with the line
`REVIEW PR-<number>-R<round>-<head8>`. The manually dispatched `post-review`
operation read the review body from the PR head and posted it as
`hvo-agentcontrol[bot]`. The comment recorded the dispatcher, PR head SHA, and
workflow run URL. See the [historical workflow](../../.github/workflows/agentcontrol.yml).
