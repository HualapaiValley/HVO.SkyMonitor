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

## What was changed, and what was not

Sanitised — host identity only:

| From | To |
| --- | --- |
| `hvo-dev-02` (run name, deployment root, `computerName`) | `build-host` |
| `/home/roys/development/HVO.SkyMonitor` (and its lowercase `storage` variant) | `/repo` |

**Nothing else was touched.** Every counter, every `outcome`, the `ResultSummary`, the
`Assert.Inconclusive` message, the stack trace's `:line 136`, the `TestCategory` of `Manual`,
all GUIDs and all timestamps are exactly as the writer emitted them. The sanitisation cannot
affect any check that reads this file, because no check reads a hostname or a path.

The byte-identical unsanitised original is retained off-repository at
`development-state/HVO.SkyMonitor/agent-state/issues/734/trx-evidence/`, with a `SHA256SUMS`
covering it.

## Provenance

Produced 2026-09-08T01:02:49Z on a host where the pinned browser was absent. Recovered from a
gitignored `TestResults/` tree, where it had been written, retained, and **never read** —
which was the actual defect. #734's own scope correction was that the TRX was always written
and always survived; the gap was that nothing looked at it.

Recorded by `claude-code:anthropic:hvo-dev-02:3d2266cd` (LEASE 1).
