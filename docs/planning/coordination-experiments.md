# Coordination Experiments

This is the living evidence and decision log for roadmap coordination
mechanisms. The mandatory behavior remains in `AGENTS.md` and
`agent-execution.md`; this file records why that behavior exists, whether it is
earning its cost, and which bounded variation to try next.

## Goals and boundaries

The channel should let independent coordinators keep multiple issue lanes active
without colliding on worktrees, dependency bases, Docker resources, reviews, or
the repository-wide finalization lock. It must also keep the operator informed
without making either model reread an ever-growing coordination epic.

Use the channel as a control plane, not a substitute for durable history:

- Two fixed epic comments contain only mutable current status. Each coordinator
  owns and writes one slot.
- Issue, PR, and epic ledger comments retain claims, grants, findings,
  milestones, blockers, and handoffs as append-only evidence.
- Milestones are event-driven and detailed once. Routine five-minute liveness is
  compact, sequenced, acknowledged, and delta-oriented.
- The operator heartbeat remains independently visible. A repository comment
  that is not relayed to the main conversation does not satisfy it.

## Measurement contract

Record these fields for every bounded trial:

| Measure | Definition |
| --- | --- |
| Delivery | Scheduled due time, body timestamp, API `updated_at`, and lateness |
| Response | Peer acknowledgement sequence and API-to-API response latency |
| Context cost | Slot bytes read and written, metadata polls, changed-body fetches, durable comments, estimated transcript tokens, and coordinator service time per active hour |
| Reliability | On-time, late, missed, duplicated, and out-of-order wakes plus repair latency |
| Safety | Worktree, base, branch, Docker, review, and finalization-lock collisions or prevented collisions |
| Throughput | Concurrent implementation issues, idle time caused by coordination, and rework avoided |
| Human cost | Approximate coordinator time spent servicing cadence or resolving ambiguous records |

For routine slot polling, retain the exact comment ID and last `updated_at`.
Read the body only after the timestamp changes. Advance a cursor for newer
durable comments and read a full body only by exact comment ID. This controls
model input; merely storing all history on GitHub does not.

For these measurements, a scheduled wake is one due time declared by the
previous accepted slot while the channel is active. Delivery time is the API
`updated_at`, and delivery is on time when that value is within 30 seconds
before or after the declared due time. The denominator is every scheduled wake
in the trial, excluding the immediate baseline that follows an intentional
active-set restart. Measure recovery from the first coordinator detection of a
late wake until the next fresh peer update. Prefer harness token usage; when it
is unavailable, record UTF-8 bytes divided by four as an explicitly approximate
token estimate.

## Experiment log

### E-001: buffered timer output

- **Hypothesis:** a background loop that prints status every five minutes is a
  sufficient heartbeat.
- **Result:** rejected. Output remained buffered until the coordinator manually
  polled, so operator updates stopped even though the timer itself ran.
- **Decision:** a monitor must actively wake or message the coordinator. Require
  an immediate baseline after start/restart and direct polling after a late
  signal until the repaired path proves itself.
- **Durable change:** issue
  [#660](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/660) and PR
  [#661](https://github.com/RoySalisbury/HVO.SkyMonitor/pull/661).

### E-002: two fixed mutable epic slots

- **Hypothesis:** one coordinator-owned and one peer-owned rolling comment can
  carry current state without unbounded epic growth.
- **Trial:** RM-017 epic
  [#513](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/513), coordinator
  [slot](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/513#issuecomment-5556958035),
  and Claude
  [slot](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/513#issuecomment-5556958190).
- **Observed value:** #619 and #649 ran concurrently from explicit merged bases
  in separate worktrees. The channel kept #659 paused, withheld #649 Docker
  authority until its non-Docker gate was green, granted one task-scoped
  campaign while #663 independently held the finalization lock, and conveyed
  the new main SHA after #663 merged. No shared-resource or worktree collision
  occurred during the observed window.
- **Cost:** exact five-minute delivery required coordinator attention, and early
  prose-heavy records repeated stable SHAs, scope, and lock rules.
- **Decision:** retain the two-slot control plane, separate mutable liveness from
  append-only decisions, and reduce routine payloads.

### E-003: long work and scheduled wakes

- **Observation:** one Claude wake due at `2026-09-06T05:32:49Z` was suppressed
  while the harness tracked a background task. The next status arrived about
  5 minutes 10 seconds after the missed due time and about 3 minutes 32 seconds
  after the coordinator requested repair.
- **Repair:** detach long work from the harness operation that suppresses wakes,
  then poll the detached job on each scheduled tick. Shut down unrelated idle
  build servers before reporting live resource inventory.
- **Decision:** a missed wake is a signaling gap, not a work-failure conclusion.
  Withhold new shared-resource authority while peer state is stale, but do not
  terminate healthy work without evidence.

### E-004: compact `C1` record

- **Hypothesis:** a complete routine peer record can stay at or below 500 UTF-8
  bytes without losing sequence, task, next step, blocker, resource owner,
  request, or due-time semantics.
- **Agreement:** coordinator proposed `C1`; Claude explicitly accepted it.
- **Samples:** the first coordinator/reply pair was 395/452 bytes with 77 seconds
  API-to-API response latency. The second pair was 343/444 bytes with 85 seconds
  latency. A later 371-byte coordinator request received a 582-byte reply in
  112 seconds because the reply retained a redundant legacy header.
- **Raw evidence:** the exact slot IDs, due/body/API timestamps, complete bodies,
  byte counts, acknowledgements, and latency calculations for five pairs are in
  the append-only
  [#664 sample ledger](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/664#issuecomment-5557392531).
- **Outcome:** required operational meaning survived, but the whole-comment byte
  cap must include headers and all stable boilerplate.
- **Next variation:** `C1.1` omits the legacy header and repeated stable facts,
  counts the complete comment body, and keeps the 500-byte target.
- **C1.1 result:** Claude explicitly accepted the proposal. Its first complete
  reply was 329 bytes with 103 seconds API-to-API response latency and retained
  the required task, next-step, blocker, resource-owner, acknowledgement, and
  due-time fields.
- **Decision:** use `C1.1` for the continuing bounded trial. Keep `C1` as the
  fallback until the decision thresholds below are evaluated.

## Candidate improvements

Try one bounded change at a time and record it before changing the default:

1. Automate slot construction, whole-body byte validation, timestamping, and
   sequence acknowledgement so servicing exact windows does not interrupt
   implementation work.
2. Prefer a harness-native push or mailbox signal when both coordinators can
   receive it; keep the epic slots as durable cross-provider fallback.
3. Keep immediate milestone delivery and five-minute liveness, but omit stable
   fields after acknowledgement and reference their durable comment IDs.
4. Use a short one-line operator heartbeat for unchanged items while retaining
   finished/current/next/blocker semantics. Do not duplicate a milestone that
   was already relayed immediately.
5. Add a validator that rejects over-budget routine records or missing sequence,
   acknowledgement, blocker, resource owner, and next-due fields before update.
6. Compare the five-minute fixed cadence with an event-driven channel plus the
   same five-minute liveness floor. Do not reduce operator visibility during the
   comparison.

## Provisional decision thresholds

Evaluate after at least 20 scheduled wakes and three implementation-issue
handoffs. Keep the channel when it has zero unreported gaps, zero ambiguous
resource grants, zero coordination-caused collisions, and at least 95% of
scheduled deliveries are on time by the definition above. Separately require
every late or missed wake to be reported at the next monitor wake and followed
by a fresh peer update within five minutes of detection. Every routine body in
the format under evaluation, produced after that format's explicit acceptance,
must remain at or below 500 bytes. Also require demonstrable parallel-lane or
avoided-rework value while targeting less than two minutes of coordinator
attention and fewer than 5,000 estimated transcript tokens per active hour.

Simplify or retire the mechanism if stale state causes unsafe authority, missed
wakes repeatedly exceed one cadence, the payload cannot remain bounded, or the
measured coordination cost exceeds the concurrent-work or avoided-rework
benefit. A retirement decision must preserve append-only issue and PR ledgers
and the mandatory operator-visible heartbeat.
