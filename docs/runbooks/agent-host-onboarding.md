# Agent Host Onboarding

How to bring a developer or operator-directed agent session online against this
repository, and what to pre-stage on a machine before an agent runs there.

`AGENTS.md` is the policy. Section 4 of `docs/planning/agent-execution.md` is the
protocol. This runbook is the operational checklist that sits under both: the
concrete steps, in order, with the failures that actually happen when a step is
skipped.

The default **issue owner** is an authorized human or operator-directed agent
responsible for the full assigned issue lifecycle. No coordinator, enrollment,
`READY` signal, command receipt, or fleet heartbeat is required. Capacity is
assessed per shared resource (host, Docker, storage, external service), not a
global two-issue slot limit. The repository-wide finalization lock still
serializes final synchronization, protected CI, and merge.

**Coordinator** and **worker** below describe only optional legacy managed
sessions explicitly enrolled by the operator. Their `JOIN`, targeted-command,
slot, and heartbeat mechanics are not prerequisites for default issue ownership.

## 1. Pre-stage the machine

Do this before any agent session starts. None of it requires the agent.

- **Toolchain.** Install the SDK pinned in `global.json`. Confirm
  `dotnet --version` reports it rather than a preview or a newer band.
- **Shell floor and lock utility, on macOS.** The gate scripts need Bash 5 and
  `flock`, and macOS ships neither: its system Bash is 3.2 and there is no
  `flock` at all. Run `brew install bash` and `brew install flock`, then invoke
  the Homebrew Bash explicitly. Skipping this does not present as a missing tool
  when the gate runs, which is why it belongs here rather than being discovered.
  `scripts/ci:shell-syntax` refuses to run under 3.2 rather than reporting a pass
  it did not perform, and `scripts/test:pr-review-tools` exits 1 saying `flock is
  required for serialized review dispatch`, which reads as a guard failure on the
  change under test. `AGENTS.md` carries the reasoning for both.
- **Git identity.** Set `user.name` and `user.email`. Every commit an agent
  writes may be attributed to a shared operator account. Record a unique session
  identity in claims and review evidence; the GitHub author or assignee alone
  does not distinguish agents sharing that account.
- **GitHub CLI.** Install `gh` and authenticate it. Confirm with a read that
  costs nothing, such as listing open pull requests.
- **Docker.** Integration selections start real SQL Server, Redis, MinIO and
  Mailpit containers through Testcontainers. A machine without a working Docker
  daemon can run the unit selection but not the integration one, and must say so
  rather than reporting a partial run as a pass.
- **Architecture check.** An ARM64 machine cannot run the amd64 SQL Server image
  under emulation reliably. Record that limitation up front; that lane comes
  from protected CI on such a host, and a failure there is not evidence about
  the change.
- **Shared-service environment.** Copy `.env.template` to `.env` and fill it in.
  `scripts/with-env` and the infrastructure scripts read the SQL Server, Redis,
  MinIO and Mailpit endpoints and credentials from it, and they abort on an
  unbound variable rather than degrading, so a machine without `.env` looks ready
  until the first host or infrastructure command fails with no obvious cause. The
  values come from the operator; the file is intentionally ignored and is never
  committed.
- **Worktree root.** Choose a durable, host-owned path outside any container's
  writable layer. Concurrent work goes in separate worktrees and no two sessions
  ever share one.

## 2. Permissions, and why this is not optional

Verify GitHub permissions with the contributor's own account. Humans need no
agent permission configuration. Agents use their actual harness's scoped
permission controls; the Claude-specific configuration and fresh-session
experiment below apply only to Claude Code. Other harnesses must verify their
equivalent controls without creating a Claude configuration file.

The permission classifier sits above the tool, not inside it. A session with a
valid GitHub token will still be refused when it tries to merge a pull request
or close an issue, and the refusal looks identical whether the cause is the
classifier, the token, or the repository.

Pre-approve the commands the role actually needs, in the repository's
`.claude/settings.local.json`. This repository ignores that path, so the file
stays out of commits. Confirm it on the machine you are setting up rather than
assuming it: run `git check-ignore -v .claude/settings.local.json` and read
which file supplied the matching rule. A rule that comes from the operator's
global ignore rather than from the repository is true on that machine and false
on the next one, which is how this sentence was wrong when it was first
written.

```json
{
  "permissions": {
    "allow": [
      "Bash(gh:*)"
    ]
  }
}
```

Narrower rules such as `Bash(gh pr merge:*)` and `Bash(gh issue close:*)` work
the same way; the `:*` suffix is what extends a rule past the bare command to a
real invocation with arguments.

Do not take the form on trust, including from this runbook. The check has to be
able to fail, which rules out the obvious one: adding a rule and then watching a
command succeed proves nothing, because a broader rule may already cover it or
the same command may have been approved earlier in the session. What establishes
it is the same command refused before the rule existed and succeeding after it,
in a session started since the change.

On a machine with no history, manufacture the refusal rather than wait for one.
In a fresh session, before adding the rule, run the exact command and let the
prompt appear. That is the before measurement, and it costs one denial. Add the
rule, start a new session, and run it again. No prompt means the rule fired, and
a prompt at that step still fails the check. Where one denial per rule is more
than a rule is worth, the weaker fallback is to read the settings file and
confirm that no other allow rule matches the command, which makes a later
success attributable without proving it. Record a rule as unverified only when
neither is available, not as the ordinary outcome of following this section.

Two consequences worth stating, because both cost real time before they were
understood:

- **A per-call approval is not a rule.** Approving a blocked call lets that call
  through and changes nothing for the next one.
- **A classifier denial is usually fleet-wide.** It is a rule about a class of
  command, not a property of one session. When a worker reports a denial, assume
  every session will hit it and escalate once, rather than routing the action to
  a second session to find out.

Routing a denied action to another session is prohibited regardless. A peer
cannot grant escalation, and a peer's approval is never the operator's. When the
operator himself instructs the action in the session he is talking to, that is an
operator decision and is legitimate; tell the blocked session what you did.

**The operator cannot see a prompt raised inside another session.** He is
attached to one terminal. Never resolve a block by telling him to answer it
elsewhere. Put the decision in front of him where he already is, complete enough
to decide without seeing the original prompt, and act on his answer yourself.

## 3. Claim and own the assigned issue

- **Claim before implementation.** Read existing issue claims, assignees, labels,
  and related PRs. An authorized human or operator-directed agent assigns the
  issue to themselves if possible, applies `workflow:in-progress`, and posts a
  claim comment with unique person/agent identity, branch/worktree, scope, and
  next checkpoint. Agents use `<harness>:<provider>:<host>:<session-short-id>`
  without credentials or full tokens. An assignee alone is insufficient when
  agents share an account. If permissions prevent the claim, report the blocker.
- **Re-read for conflict.** GitHub writes are not an atomic claim operation.
  Re-read comments, assignees, and labels after posting. Stop on a competing
  claim and resolve ownership explicitly before proceeding. Silence or age
  never authorizes automatic takeover.
- **Retain ownership.** Keep the claim through implementation, review corrections,
  and CI. Release explicitly with a resumable handoff on completion or transfer;
  remove `workflow:in-progress` when releasing, without overwriting a competing
  owner's state. A paused owner records whether the claim is retained or released.
- **Own review and completion.** The PR owner requests independent human or
  one-shot agent review bound to immutable base/head SHAs, with reviewer identity,
  findings, and dispositions recorded in the PR ledger. Agent reviews record
  requested capability and actual provider/model/effort; human reviews use N/A
  for model/effort. Follow the PR lifecycle skill's validation, draft convergence,
  bounded corrections, finalization lock, base-sync review or unchanged-base
  proof, and green current-head required CI rules. Complete authorized merge,
  verify issue closure, synchronize local `main`, and clean up merged branches/
  worktrees without deleting unrelated work. If merge is reserved or unauthorized,
  report that boundary and leave a handoff. Do not take another issue unless the
  user authorized that scope.
- **Report directly.** Send milestones and blockers to the operator and retain
  UTC evidence on the issue/PR, with interim notes during long gates and reviews.
  Default ownership does not depend on a coordinator relay or fleet heartbeat.

### Optional legacy managed-session enrollment

Only explicitly enrolled legacy managed sessions follow this subsection and
section 4's coordinator setup. Existing resumed fleet tools retain all identity,
enrollment, and targeted-command checks; do not bypass them to obtain a review.

- **Mint the participant identity** as `<harness>:<provider>:<host>:<session-short-id>`.
  Propose a short id from your own session's scratchpad directory name, which
  on some harnesses is a session identifier and on others is not. Do not copy
  one from another session, and never derive it from a live process, socket
  path, or connection handle. Never put a credential or a complete token in it.
  **The proposal is not the identity.** The `JOIN ACK` the coordinator returns
  is authoritative, and the identity you sign comments with and answer targeted
  commands under is the one in that `ACK`.

  More than one derivation is live on this fleet and no single rule explains all
  of them, so say which input you used. Two are measured: one participant's
  suffix is the first eight hex of the SHA-256 of its API session identifier,
  and another's is the plain first segment of its scratchpad directory name.
  Neither reproduces the other, hashing the first participant's directory name
  reproduces nothing, and suffix lengths are not even uniform across the fleet.
  A session that mints a suffix, states no input, and skips the acknowledgement
  can end up enrolled under an identity that targeted commands never reach.
- **Send `JOIN REQUEST`** to the coordinator on the owning roadmap epic, which
  is issue #513 at the time of writing. `AGENTS.md` correctly tells you to use
  the active initiative's epic and cites #89 only as retained virtual-first
  history, but it does not name the active one, so a new session cannot get
  there from the policy alone. If #513 is closed when you read this, ask the
  operator which epic is live rather than guessing from the issue list. The
  coordinator's participant identity is not published anywhere and is not
  derivable: you learn it from the `JOIN ACK` and from the footnote it signs its
  own comments with. The session stays `UNREGISTERED/WAIT` until the coordinator
  returns a participant-bound `JOIN ACK` and the session returns `JOINED ACK`.
  Reading
  `AGENTS.md` is not enrolment.
- **Take a slot lease** if the role needs one. Slots are mutable epic comments
  with a sequence number; read the last sequence before writing and acknowledge
  the other slot's latest.
- **Adopt the attribution footnote.** Every issue comment, pull request comment,
  issue body and pull request body ends with a footnote naming the writing
  session, because GitHub attributes all of it to the operator's account.

## 4. Optional legacy coordinator setup

- **Arm the heartbeat, and confirm the cadence with the operator.** Two
  different intervals live in the protocol and they are easy to collapse into
  one. Section 4 of `docs/planning/agent-execution.md` specifies a five-minute
  operator-visible heartbeat, and separately gives fifteen minutes to the
  cross-provider slot writer for its state line. The fifteen-minute figure is
  scoped to that writer and is not a licence to relay to the operator every
  fifteen minutes; `AGENTS.md` has the five-minute heartbeat relay every wake to
  the operator, carrying the literal `still running, no change` when that is
  what applies. This fleet has also run a fifteen-minute fallback tick, on the
  operator's judgement that push messaging carries the events and the tick only
  catches what push misses. Which
  applies is the operator's call, so ask rather than infer, and say in your first
  relay which one you armed.
- **Prove the job exists on every tick**, because it has silently disappeared
  mid-session before and a heartbeat that stopped looks exactly like a fleet
  with nothing to report. List the harness's scheduled jobs as the first action
  of each tick, before anything else; in Claude Code that is the `CronList`
  tool. If the job is gone, recreate it and say so in that tick's relay.
- **Verify the slot write path once.** A malformed write can leave a two-byte
  body and destroy the sequence counter with no error. Read the slot back after
  the first write.
- **Keep any per-participant queue within operator-authorized scope.** Completion
  does not authorize automatically assigning or taking another issue.

## 5. Validation and finalization for every owner

- **Never trust a filtered read for the lock.** The finalization lock is the
  `workflow:finalizing` label on an open pull request. The label search endpoint
  lags and returns empty seconds after a write, which is indistinguishable from
  no holder. Read the pull request objects directly. A filtered enumeration is
  trustworthy only when the returned count is strictly less than the requested
  limit, and even then not immediately after a write. Hold the repository-wide
  lock from final synchronization through any required base-sync review,
  protected CI, and merge. If the target advances, return to draft and release
  the lock before synchronizing and reviewing again under the lifecycle rules.
- **Clear the label from a merged pull request.** A merged holder does not hold
  the lock, but leaving the label on makes every later read ambiguous.

- **Confirm host quiet before any timed or gated run.** Read the load average and
  name any foreign process. A failure produced under contention is unattributable
  in either direction and has to be discarded. A pass under worse contention than
  the disputed run is still admissible; only a failure is not. That asymmetry is
  only usable afterwards if you recorded the contention at the time, so capture
  the load average and processor count with every timed run and put them in the
  evidence. Without them, a later pass cannot be shown to be the worse case and
  the argument is unavailable.
- **Never reuse build output across a commit boundary.** Evidence guards bind an
  assembly's stamped commit to the checked-out head. Whether the compiled
  behaviour changed is irrelevant; evidence claiming to be about a commit must
  have been produced from it.
- **Report per-assembly outcomes, never the summary line.** A run that executes
  nothing prints a clean summary. A test command exits zero when its filter
  matches nothing.
- **Verify before transcribing.** Never measure a commit that exists only in
  another machine's working tree. Ask for it to be pushed, fetch the object, and
  publish the blob hash of the file measured. A run against transcribed text is
  evidence about the transcription.

## 6. The failure class this fleet keeps hitting

Nearly every defect found in a recent batch was one shape: **a channel
structurally incapable of saying it does not know, so absence renders as a
definite answer.** A gate exiting 127 read as a refusal. A test filter matching
nothing exiting zero. A lagging label filter read as no lock holder. A clean
auto-merge read as agreement when it had silently pinned a stale number. An SVG
class name that is never a string, always truthy, and stringifies to a fixed
token.

Every one was caught by a second measurement disagreeing with the first. None
was caught by looking harder at the first. Build the second measurement in.

## 7. Bringing the session down

Leave the resumable handoff the execution protocol requires: what is done, what
is running, the exact next action, and what would invalidate it. Release any
lock. Post the state to the owning issue or pull request, not only to the
operator, because a session's own transcript does not survive it. Explicitly
record claim retention or release with a handoff; shutdown alone does not release
ownership, and a stale claim must not be taken over automatically. On completion,
release the claim and remove `workflow:in-progress`. Do not start another issue
without user authorization.
