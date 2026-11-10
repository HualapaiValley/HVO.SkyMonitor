# Protected installation verification (#1041)

## Sources and authority

Original issue body and all 13 discussion comments read 2026-11-10 UTC:
<https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1041>.
Roy's Nov 10 “Fix 1041” instruction (t350u, supplied by Kepler) authorizes
implementation beyond the older attribution-only restrictions. Supplied §12
incident figures are signed historical evidence, not measurements in this work.
The incident share is not mounted here. Baseline is freshly fetched
`d122742c7b619a887168d3f9e2b4f23e54ba6ee7` on `development/v1`.
Reuse [#1559](issue-1559-protected-read-attribution.md),
[#1591](issue-1591-schedule-validation-reuse.md), and
[#1618](issue-1618-retained-read-attribution.md); do not re-credit their changes.
Risk HIGH, validation Tier C, target protected profile development-v1.
Independent Sol/Opus reviews are reserved to Kepler by the assignment.

## Requirement/design map (recorded before product edits)

| ID/source | Planned behavior | Required local evidence |
| --- | --- | --- |
| R1 — current issue + Nov 10 scale goal | Replace repeated full parsing with a bounded content-verified summary, if current-tip measurements confirm parsing dominates. Continue reading current file/journal bytes; explicitly retain linear I/O rather than claim constant time. | Equivalent current-tip/candidate endpoint runs at 1k, 10k and 100,001 captures, fixed/solar; time, CPU, allocation, RSS, logical I/O; cold/unchanged/restart behavior. |
| R2 — fresh tamper/schema/corruption | SHA-256 of actual current bytes, not path/mtime/length. Only successful full contract and protected-history validation may produce a proof. Current protected history, marker and manual state remain validated every call. Invalid/unknown proof state rebuilds from evidence. | Same-length/timestamp replacement, malformed/unsupported manifests and journal, protected history corruption/schema, proof corruption/wrong build/history, successful repair. |
| R3 — installation identity/readiness | Actual file load, same-load schedule validation, effective location and current owner readiness on every call. No configuration/identity cache. | Existing real authenticated delayed/repeated/concurrent endpoint regression and actual config/manual edits. |
| R4 — pre-mutation lifecycle | Preserve installer ordering and all identity comparisons for upgrade/rollback/backup; no CLI timeout change, retry or bypass. | CLI timeout/caller cancellation, transport/auth/token sanitization and lifecycle refusal/no-mutation tests; disposable installer campaign. |
| R5 — cancellation/failure | Check cancellation during enumeration, file and journal processing; never persist failed/canceled proof additions. Original authority writes keep existing durability barriers. | Mid-file/journal cancellation, canceled gate wait, fault before summary publication, restart from incomplete write. |
| R6 — concurrency/invalidation | Existing store gate owns summary reads/builds/publication. Proof identity includes full protected-history semantics and exact validator binaries. Current bytes always checked; changed history discards prior proofs. Bound entry count and serialized size; overflow falls back to full parsing. | Concurrent calls, history/manual/config changes, append/replacement, capacity overflow and restart tests. |
| R7 — crash/recovery/durable state | Optional protected summary uses existing confined atomic/fsync publication. It contains only validated content digests tied to history/build identity. Missing/corrupt/incomplete summary never grants authority; rebuild. No journal schema, existing history or recovery-layout conversion. | Durable reload reuses proofs; missing/truncated/tampered/wrong-version summary rebuilds; injected write interruption and clean restart. |
| R8 — compatibility | Wire contract unchanged. Old installer + new agent benefits; new installer + old agent retains old cost/120 s cap. Pre-mutation verification always executes on the running agent. A server fix cannot unblock an already-slow old runtime. | Existing CLI/endpoint contracts, explicit compatibility matrix in final handoff. |
| R9 — evidence/guards | Exact-range classifier, warning-clean Debug/Release, canonical coverage, format, package audit, four guards, full positive Docker-disabled Unit and separate Integration; focused Acceptance/CLI and 100k campaign under heavy lock. | Commands, counts, results, artifacts and exact candidate binding. |

No new installer safeguard is selected: the shared finite 120-second budget and
sanitized distinction from caller cancellation already shipped in #1042.
No deployment, live service, token, production state or PR action is authorized.
The endpoint file-reopen race remains excluded unless implementation changes
its snapshot assumptions; this design adds no configuration memoization.

## Summary design to validate

A bounded set of SHA-256 content proofs avoids re-deserialization, not fresh
inspection. The key includes the complete history (including activation and
supersession times) and both validator assembly MVIDs, so a validator rebuild
or changed history forces full validation. Protect the binary summary with the
existing deployment-location protector; separate magic/version prevents type
confusion. Maximum 262,144 digests (8 MiB raw); no retained payloads or private
paths. Overflow still validates each manifest and simply cannot cache it.

Load a summary under the existing gate; scan current bytes under the same gate;
collect new proofs only for fully valid manifests with valid provenance. Publish
only after both scans, reconciliation and original durable state writes succeed.
No failed/canceled scan publishes additions. A summary publication I/O failure
refuses the request, preserving the existing durable publication barriers; the
next request/restart revalidates rather than treating an incomplete write as
proof. This is a stated availability cost of summary publication. One SQLite reader maintains its
existing statement snapshot. External writes after a file is read or after a
SQLite snapshot starts retain the existing read-boundary semantics; this is not
a cross-file atomic snapshot. Exact bytes at that boundary remain the authority.
The optional summary is advisory: deletion/corruption/version mismatch rebuilds,
never accepts current corrupt evidence. Existing unknown/non-manifest JSON
handling needs explicit review before deciding whether to tighten rejection.

This keeps the current filesystem/recovery primitives selected by #1607 and
[#1615](upgrade-recovery-cost-1615.md), with no DB migration, backend conversion,
checkpoint protocol, restore ordering, or new adoption requirement.

## Results

The current-tip baseline completed using the test-only harness at `c31afe4b`;
product sources are byte-identical to `d122742c`. The initial 100k attempt failed
in the fixture checksum helper, not endpoint validation; the bounded streaming
correction is retained in the harness history. The successful baseline uses
one affined CPU, a 384 MiB managed heap, native Linux x64 Release / .NET 10.0.14,
SDK 10.0.402, an invalid Docker endpoint and synthetic catalog/evidence only.

Each count has that many distinct captures, each represented by one file and
one journal manifest (therefore 200,002 manifest reads at 100,001 captures).
Construction uses current contract serialization, the real v15 journal schema,
foreign-key assignments and one bulk disposable transaction, with no image
payload I/O. This is a scan workload, not acquisition/durable-ingress throughput.
Each fixed/solar fixture has a first populated read, five settling reads and five
measured sequential reads; retain phase/JIT differences rather than pool them
or infer percentiles. Whole-process allocation includes the in-process host/client.

| Captures | Fixed baseline median, ms | Solar baseline median, ms |
| ---: | ---: | ---: |
| 1,000 | 242.64 | 89.30 |
| 10,000 | 856.59 | 398.35 |
| 100,001 | 3,393.07 | 4,249.77 |

At 100,001, median process allocations are 2,420.99 MiB fixed / 2,427.04 MiB
solar. These are this host's current software measurements; neither the historic
35,049 ms incident nor the earlier small-fixture ~180 ms medians are substituted.
Candidate comparative results and final completeness evidence follow below.

### Source findings and scope decisions before product changes

The current-tip 1k/10k baseline is running on disposable state. At 100,001,
the fixture checksum helper (not the endpoint) exhausted the 384 MiB heap by
materializing the whole SQLite file. Replace that helper with a 64 KiB streaming
hash, preserving its exact checksum input; retain the failed run and rerun.

Two original-source integrity gaps need explicit treatment rather than claims
that the old code rejected every invalid state:

- `ParseManifest(...).Document?.Manifest` silently ignores parse/schema failures.
  Tighten this scan to reject invalid retained manifest JSON, including unsupported
  schema. This can refuse an installation containing retained corrupt/quarantined
  JSON that the old location scan ignored; it is stricter admission, not weaker
  validation. Scan all journal manifests rather than a text LIKE filter that a
  malformed/re-encoded location property can evade. Keep valid manifests without
  location under their existing no-provenance-comparison rule.
- Upgrade/rollback pre-mutation reads inspect owner state but compare the complete
  identity only after mutation; running backup has no pre-mutation protected read.
  Use the existing complete field comparator before image mutation, and before
  running backup's pause/stop. Keep stopped backups offline and plans dry-run.
  Add real scripted-transport mismatch/timeout/failure/cancellation tests for all
  three paths. Preserve the existing budget and transport implementation.

These changes are required by the assignment's explicit integrity/pre-mutation
acceptance. They are called out for Kepler's HIGH reviews. No existing durable
format is replaced, and no configuration snapshot/cache is introduced.
