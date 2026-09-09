# Agent Host Onboarding

How to bring a new coordinating or implementing agent session online against this
repository, and what to pre-stage on a machine before an agent runs there.

`AGENTS.md` is the policy. Section 4 of `docs/planning/agent-execution.md` is the
protocol. This runbook is the operational checklist that sits under both: the
concrete steps, in order, with the failures that actually happen when a step is
skipped.

Two roles are described. **Coordinator** is the single session that owns the
queue, the finalization lock, command issuance, and operator reporting.
**Worker** is any enrolled implementing or measuring session. A machine can host
several worker sessions; only one coordinator exists at a time.

## 1. Pre-stage the machine

Do this before any agent session starts. None of it requires the agent.

- **Toolchain.** Install the SDK pinned in `global.json`. Confirm
  `dotnet --version` reports it rather than a preview or a newer band.
- **Git identity.** Set `user.name` and `user.email`. Every commit an agent
  writes is attributed to the operator account, so the `Claude-Session` commit
  trailer is what identifies the authoring session. The GitHub author field
  never does.
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

## 3. Enrol the session

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

## 4. Coordinator-only setup

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
- **Never trust a filtered read for the lock.** The finalization lock is the
  `workflow:finalizing` label on an open pull request. The label search endpoint
  lags and returns empty seconds after a write, which is indistinguishable from
  no holder. Read the pull request objects directly. A filtered enumeration is
  trustworthy only when the returned count is strictly less than the requested
  limit, and even then not immediately after a write.
- **Clear the label from a merged pull request.** A merged holder does not hold
  the lock, but leaving the label on makes every later read ambiguous.
- **Keep a per-participant queue.** Nobody idles. A worker with nothing to do is
  a coordination failure, not a worker failure.

## 5. Worker-only setup

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
operator, because a session's own transcript does not survive it.
