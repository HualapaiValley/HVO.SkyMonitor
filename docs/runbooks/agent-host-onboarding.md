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
- **Worktree root.** Choose a durable, host-owned path outside any container's
  writable layer. Concurrent work goes in separate worktrees and no two sessions
  ever share one.

## 2. Permissions, and why this is not optional

The permission classifier sits above the tool, not inside it. A session with a
valid GitHub token will still be refused when it tries to merge a pull request
or close an issue, and the refusal looks identical whether the cause is the
classifier, the token, or the repository.

Pre-approve the commands the role actually needs, in the repository's
`.claude/settings.local.json`, which is git-ignored:

```json
{
  "permissions": {
    "allow": [
      "Bash(gh *)"
    ]
  }
}
```

Narrower rules such as `Bash(gh pr merge:*)` and `Bash(gh issue close:*)` work
the same way. The `:*` suffix matters: without it the rule matches only the bare
command and never fires on a real invocation.

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
  The short id is the session's own directory name, not a value copied from
  another session and not derived from a process, socket path, or connection
  handle. Never put a credential or a complete token in it.
- **Send `JOIN REQUEST`** to the coordinator on the owning roadmap epic. The
  session stays `UNREGISTERED/WAIT` until the coordinator returns a
  participant-bound `JOIN ACK` and the session returns `JOINED ACK`. Reading
  `AGENTS.md` is not enrolment.
- **Take a slot lease** if the role needs one. Slots are mutable epic comments
  with a sequence number; read the last sequence before writing and acknowledge
  the other slot's latest.
- **Adopt the attribution footnote.** Every issue comment, pull request comment,
  issue body and pull request body ends with a footnote naming the writing
  session, because GitHub attributes all of it to the operator's account.

## 4. Coordinator-only setup

- **Arm the heartbeat.** One recurring job, currently fifteen minutes. It is a
  safety net, not the transport: workers message directly, so events arrive by
  push. Do not poll what a worker has promised to report. Check the job still
  exists on every tick, because it has silently disappeared mid-session before.
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
  the disputed run is still admissible; only a failure is not.
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
