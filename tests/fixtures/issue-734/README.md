# A real skipped TRX that was reported as a successful run

`skipped-run-reported-as-success.trx` is a **genuine artefact from a real campaign run**, not
a constructed fixture. It is the run that issue #734 was opened about: the canonical W6
campaign reported `Test Run Successful` on a host with no pinned Playwright Chromium, having
opened no page at all.

It is kept because **no synthetic fixture can prove this shape was ever real.** Reproducing
another requires a campaign to skip while reporting success, which is exactly the condition
the surrounding fixes now prevent.

## Why it is the hard case

```xml
<ResultSummary outcome="Completed">
<Counters total="1" executed="0" passed="0" failed="0" ...
          inconclusive="0" ... notExecuted="0" ... />
```

The run executed nothing, and **every counter a validator would naturally reach for is
zero**: `failed`, `inconclusive`, `notExecuted`, `error`, `aborted`. `ResultSummary` outcome
is `Completed`, and `dotnet test` exited **0**.

Only two things distinguish it from a genuine pass:

- the `executed` and `passed` counters, both `0`
- the per-result `outcome="NotExecuted"`

Any check that reads `notExecuted` or `inconclusive` to detect a skip **passes this file**.
That is the trap, and it is why this artefact is worth keeping.

## Which writer produced it, so staleness is detectable

This artefact is evidence about **one writer's behaviour**, not about the TRX format in
general, so what produced it has to be recorded or the fixture cannot be re-evaluated when the
toolchain moves.

| | |
| --- | --- |
| Adapter | `executor://mstestadapter/v4` — carried in the artefact itself, at `TestMethod/@adapterTypeName` |
| Schema | `http://microsoft.com/schemas/VisualStudio/TeamTest/2010` |
| MSTest | 4.3.3 (`Directory.Packages.props`) |
| SDK | 10.0.400 (`global.json`) |
| Produced | 2026-09-08T01:02:49Z |

**If the adapter or MSTest major version changes, re-derive rather than assume.** The
counter-zeroing behaviour this file pins is a property of that writer; a later writer could
populate `notExecuted` and this fixture would then document history rather than current
behaviour. It would still be worth keeping — it would just be pinning a different claim.

## What was changed, and what was not

Sanitised — host identity only:

| From | To |
| --- | --- |
| `hvo-dev-02` (run name, deployment root, `computerName`) | `build-host` |
| `/home/roys/development/HVO.SkyMonitor` (and its lowercase `storage` variant) | `/repo` |

**Nothing else was touched.** Every counter, every `outcome`, the `ResultSummary`, the
`Assert.Inconclusive` message, the stack trace's `:line 136`, the `TestCategory` of `Manual`,
all GUIDs and all timestamps are exactly as the writer emitted them. **No check that reads this file may read a hostname or a path.** That is a
constraint on consumers, not a prediction about them: the sanitisation is safe only for as long
as it holds, and a future consumer asserting on `computerName` or on a checkout path would be
asserting on values this file no longer carries honestly. Such a consumer is wrong to add, not
a reason to unsanitise the fixture.

The byte-identical unsanitised original is retained off-repository at
`development-state/HVO.SkyMonitor/agent-state/issues/734/trx-evidence/`, with a `SHA256SUMS`
covering it.

## What makes it load-bearing, and what does not yet

**Nothing in the repository reads this file today.** It is committed ahead of its consumer,
and that is deliberate rather than an oversight.

Its consumer is the retention gate under **#768**. That issue's first item — the unanchored
counters read — was resolved by #767, so what remains of #768 is the gate that keeps what the
measurements established, and this artefact is that gate's most valuable input. A gate written
first would have to be tested against something, and the only honest something is a real
artefact of the failure it exists to catch.

Until that gate exists, this file is documentation with a checksum rather than an enforced
constraint. **If #768 is closed without a consumer for it, this fixture should be re-justified
or removed** rather than left as a file nothing reads.

## Provenance

Produced 2026-09-08T01:02:49Z on a host where the pinned browser was absent. Recovered from a
gitignored `TestResults/` tree, where it had been written, retained, and **never read** —
which was the actual defect. #734's own scope correction was that the TRX was always written
and always survived; the gap was that nothing looked at it.

Recorded by `claude-code:anthropic:hvo-dev-02:3d2266cd` (LEASE 1).
