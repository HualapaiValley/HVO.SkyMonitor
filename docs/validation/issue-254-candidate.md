# Issue 254 SQL Session Attribution Evidence

## Scope

Issue #254 gives LogicHost runtime, dedicated object-lock, and controlled
database-initialization SQL sessions purpose-owned application names. It also
sanitizes malformed connection configuration without retaining parser
exceptions or secrets. No pool, timeout, retry, isolation, Query Store, or
Extended Events policy was invented.

## Source And Method

- Production baseline: `300d1b5940fd2241cc32a3f2e35cd27e89ff03ca`.
- Frozen harness-only baseline head: `bfe0e5780087ee4c7989885a7a80f70f7df54904`.
- Candidate evidence head: `eae51159b24972c935e4403a1bac57d275104ffb`.
- Harness SHA-256: `DB46735CAFE583A5941F8BF5D1ABD8300817D5930B6ECCF64CD62412118ACEEE`.
- Environment fingerprint SHA-256: `4AE96994E082A29E94CBD4293D1F8A94794CD803AF28BC131D79998C1A4719A1`.
- Five separate Release/server-GC processes per phase measured W4 retention
  release after 20 warmups and across 200 operations at concurrency 1/4/8.
- Separate concurrency 1/4/8 cells applied 0/250/2,000 ms object DELETE delays.
- Each trial checked exact payload length/SHA-256 before deletion, final SQL and
  MinIO convergence, caller-visible outcomes, SQL transaction outcomes, and
  sanitized evidence manifests.

The DMV sampler records active/sleeping sessions, requests, transactions,
session application locks, transaction-log bytes, blocked requests, and lock
occupancy without SQL text, parameters, session IDs, lock resources, login
names, connection strings, credentials, artifact IDs, or object keys. Non-atomic
DMV state and occupancy values are retained as diagnostics rather than assigned
an invented regression direction. Blocked requests and stable-window
application-lock sessions with open transactions remain correctness gates.

## Results

The automatic comparison result was `passed-no-material-regression` with zero
material regressions. Its baseline summary SHA-256 is
`036E417CFF3B7FB779B71EE323FDB63C7C6F0B21D0A2A5ECFC65CB7A9F25A44C` and
after summary SHA-256 is
`30E5616AD5FB7FCB4949626E93188EEDE79F7471D58F6BC426A6B9E443693255`.

| Concurrency | Median ops/s | Median p50 ms | Median p95 ms | Attributed sessions | Blocked requests |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 16.79 | 35.25 | 113.13 | 3 | 0 |
| 4 | 91.53 | 38.32 | 114.62 | 9 | 0 |
| 8 | 146.30 | 47.53 | 101.80 | 17 | 0 |

At the 2,000 ms delay, median p50 was 2,106.81/2,067.31/2,098.39 ms for
concurrency 1/4/8. Every trial observed all expected session application locks
in the stable delay window, zero lock sessions with open transactions, zero
blocked requests, and zero caller-visible failures.

The effective policy remained unchanged across phases: pooling enabled, minimum
pool size 0, maximum pool size 100, connect timeout 15 seconds, EF command
timeout 30 seconds, and connection lifetime 0. Those provider defaults are
retained because the measured workload did not justify tighter values.

## Review Disposition

Independent review found and corrected two evidence-model defects before final
evidence: transition samples could race pooled-session reuse, and non-atomic DMV
occupancy counts had no defensible regression direction. Every harness correction
was applied identically to a clean harness-only baseline worktree and the
candidate, then all ten trials were replaced. One attempted baseline trial was
rejected before evidence output when its median sampling interval exceeded the
frozen quality bound; the unchanged trial was rerun successfully.
