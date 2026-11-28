# Centered acquisition follow-ups

[Issue #1950](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1950)
tracks the findings from the [original review of PR #1949](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1949#issuecomment-7063543058).
Verification started from `development/v1` at
`781adfb41a8e9a44e24bbfc410c73981bbd25160`. The candidate is based on
`6e9b7810b2f7efa10dbdc83e8b66a851fc0d7a4a`, which includes the writable
journal restart check from #2005 and the centered-view retention indexes from
#2000. The growth comparison below used the original base.

## Schedule expansion retention (F9)

The journal keeps the 64 most recently inserted unpinned expansions. It also
keeps the most recently inserted expansion of each supported algorithm for the
active and pending revisions, up to four more rows. Startup applies the rule
to existing journals. A new expansion and any retirement commit together.
Foreign keys remove each retired expansion's interval and unavailable-window
rows in the same transaction. Freed pages can be reused by SQLite.

An active runtime snapshot holds the preview and admission expansion it uses
to evaluate the next grant. On restart or a profile switch, the runtime reads
a matching retained expansion and verifies its checksum, or expands the
selected revision for the current location and date. Schedule revisions,
activation receipts and recorded admissions are durable. Replay reads the
original capture's frozen admission bounds, revision and expansion digests
from the raw manifest.

`CenteredScheduleRetiresOldExpansionsAndReopensAdmissionAndReplay` advances
the clock across 50 local days and four centered revisions in an isolated
journal. The schedule uses one fixed morning window each day of the week. Each
materialized day and revision has a one-day preview and a nine-day admission
expansion.

| Checkpoint | Base expansions / intervals | Bounded expansions / intervals | Base / bounded live journal bytes |
| --- | ---: | ---: | ---: |
| Day 10, after activation | 24 / 120 | 24 / 120 | 1,269,760 / 1,269,760 |
| Day 25, after activation | 56 / 280 | 56 / 280 | 1,339,392 / 1,339,392 |
| Day 40 | 86 / 430 | 66 / 330 | 1,413,120 / 1,376,256 |
| Day 50, after activation | 108 / 540 | 68 / 340 | 1,466,368 / 1,392,640 |

The bounded journal reopened with 66 expansions and 330 intervals. The page
count includes the shared journal's other tables and indexes and excludes
freelist pages.

The regression checks admission with a retained revision after restart,
verifies the retained expansion checksum across dates and profile switches,
and replays the oldest capture after its expansion has retired.

## Archive authority (F7)

The disposable archive tests reuse the released-writer specimen approved in
[#1919](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1919#issuecomment-7056710877).
They exercise a SQLite exclusive lock, a redirected journal, wrong file types
at the journal path and its parent, and a centered copy moved with its receipt
to a different root.
The operator's [archive reporting and recovery procedure](../runbooks/deployment-installer.md#local-archive-publication-and-root-availability)
describes the resulting visibility and warning. These reader checks complement
the [#1059 artifact history fault evidence](artifact-history-1059-fault-evidence.md).
[#1059](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1059) owns the
broader physical evidence and installer-style restore campaign.

## Conditional observations

**F4.** `SqliteCaptureLaneStore.ClaimAsync` returns no claim when the ordered
Standard candidate has a centered manifest and lacks the live execution,
context bytes, or context checksum. The row can remain at the head for later
claims. Tracing normal admission and recovery on `development/v1` reached
no writer of this malformed state. Centered admission requires a frozen
binding, and the journal commits the lane work, context and live execution
together. Recovery of a centered manifest requires the corresponding
execution and context and records them atomically with the recovered work.
Carry this observation into malformed-journal handling if a reachable case
is reproduced. That work needs a test and an explicit preserve or quarantine
decision.

**F10.** An unbounded always-open centered grant can use `DecisionUtc` as its
effective start. The centered descriptor carries that precision while raw
timing rounds shutter start down to milliseconds. A shutter in the same
millisecond could then compare earlier than the grant. Verified refusal of
the always-open centered profile at file load and stage. A future decision
to support it needs a same-millisecond shutter regression that preserves
existing time evidence.
