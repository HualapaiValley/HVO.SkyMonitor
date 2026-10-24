# Deferred F7 catalog qualification (#1188)

This record owns the full catalog-only comparison deferred from #1186. The
foundation and curated annotation delivery do not establish this qualification.
No production optimization, catalog trust change, release, deployment, hardware
qualification or scientific acceptance is included.

## Qualification state

**VALID 0.** Both current full campaigns stopped at their quietness gates, after
trial 2 and trial 1 respectively. No complete comparison, performance verdict or
supported operating envelope has been established. The separate operational
fitting run passed.

The original execution11 remains incomplete: ten source trials had quiet checks
and individual validators return zero, but the finite work deadline stopped the
24-trial comparison at 61 passed gates and 58 not run. A separate late receipt
used 90,112 allocated bytes against its 65,536-byte allowance. The original
nonzero exits, failure records and incomplete comparison remain unchanged.
The foundation's 416 functional passes are separate evidence.

## Pinned comparison

The source baseline is `764233766a2d42da823241954469f48b086a7e06`.
The current catalog candidate is
`f57dfc1b2ddf0a646f1455a15b4e4eaac4e23c29` on `development/v1`.
The two Foundation measurement files and `issue-525-query-pins.json` are
byte-identical to historical candidate
`b60fa1d430f8f8183d95caae0dceaa6ccc8af236`:

| Input | SHA-256 |
| --- | --- |
| `CatalogPerformanceTests.Foundation.cs` | `8a9620d89f5f91deb8774bdee6f3d46f9166d6943a5bd2a79ba1a494a44c5802` |
| `CatalogPerformanceTests.FoundationDso.cs` | `a9c1a29784ae7e87b184d3aed70a1adeb8d3ad35fa1eb426a94cd71acf31c0be` |
| `issue-525-query-pins.json` | `acb6f7600850307aac42a5b5fe021885f4b26c4507f1f4dfed6f333e806c30f6` |

Both arms use the repository's pinned .NET SDK 10.0.402. The baseline needs a
declared SDK configuration overlay in addition to the historical test-only
overlay; its catalog implementation stays at the original baseline. Source,
test-overlay, dependency, runtime and binary identities must be recorded
separately. The source comparison includes the actual package versions at each
revision and does not isolate a causal effect of the foundation alone.

The immutable catalog inputs remain:

| Arm | Package | Database SHA-256 |
| --- | --- | --- |
| Stars | `hyg-v4.4-p4-s3-r1` | `2cc06890de1d4168f1eb91ca4ef5cde1c4923f275a52a1aedfec002df42a3321` |
| Composed | `hyg-v4.4-openngc-v20260501-p5-s4-r1` | `eea1181ffae1dca2935aeed2c7790305b28045061f38ca6dda5eaf19694a8f1f` |

The source comparison runs baseline stars against candidate stars; the
composition comparison runs candidate stars against candidate composed. Each
comparison uses three consecutive ABBA blocks, twelve fresh processes, and six
adjacent A/B pairs. All 24 trials run in one serial session. Query measurements
retain five warmups and 100 operations, the 50-pointing region sweep, and all 15
nonempty pinned deep-sky queries. The linear deep-sky baselines run on the same
candidate; no deep-sky operation is invented for a stars-only catalog.

## Unchanged acceptance

The existing rules require explanation for startup exceeding both twice its
paired baseline and an additional 100 ms; common star query median or p95
exceeding both 10% and an additional 1 ms; and any deep-sky query p95 exceeding
its equivalent linear baseline p95 by more than 1 ms. Unexplained material
regression blocks qualification. Incomplete prefixes yield no regression verdict.

Startup includes package authentication, validation, eager row loading and
immutable index construction. CPU, allocations, working set, storage reads,
logical reads and read calls stay distinct. Query verification remains outside
the measured operation. CPU-counter zeros mean below resolution. Startup has
six values per arm and reports median and range, without a startup p95 claim.

The original strict no-steal and runqueue predicates, 2-GiB process peak limit,
8-GiB cohort allowance, component and reserve limits, 119 ordered gates and
finite phase bounds remain. Only this host's shared heavy gate may be reserved.
Boundary samples do not prove continuous quietness or continuous peak
containment. Fresh processes do not imply cold operating-system storage caches.

## Recovered harness and failures

The complete reviewed v6 control source was recovered with its original
81,173-byte length and SHA-256
`2aeed76da244d8ce59dbacebdcf87e17705c93c55793c4e5ab14cdadee061ed5`.
Its complete independent report was recovered with SHA-256
`6d3901bfcf00ba75ddf38d4d6fb823f7c60eb0713885aba5e5abe6231e75cd66`.
The recorded requested reviewer was Astra/xhigh; actual historical model and
effort remain unknown. This recovery supplies no current PR convergence credit.

Both reported blockers were reproduced before correction:

- **F7-V6-IR-1:** 122 extracted files produce at least 610 mandatory filesystem
  events and a 91,086-byte trace. The actual complete v6 run rejects both its
  first normal evidence and failed evidence at the 65,536-byte record limit.
- **F7-V6-IR-2:** the setup-error oracle returns `matched: true` for an empty
  preservation wait object. The counterexample fixture follows the caught
  setup-error path; it does not claim a real child wait.

The recovered v7 draft uses complete ordered trace chunks and typed terminal
agreement. Its first actual execution published fifteen completed cases, then
stopped on `NAMED_PHASE` in `v5-fallback-EIO-existing-partial-no-zero`.
The failure and complete failed trace were retained. Subsequent fail-first
verification found inspection faults occurring before their named partial write,
incorrect errno exception classes, aggregate raw traces exceeding 8 MiB, and
failure progress referring to a previous case. Each red remains preserved.

The [current private controls](../../scripts/catalog/f7/README.md) correct those
cases without changing the selected executor logic or scientific predicates.
All 90 controls and 119 fake gate interactions pass. Complete snapshots contain
10,334,069 reconstructed bytes; deterministic lossless compression keeps the
complete native observer output at 852,193 logical bytes and 1,740,800 allocated
bytes, within the unchanged output limits. The actual adapter exits zero with
raw wait status zero in 1.61 seconds, sends no signals, and observes an empty
released cohort. Typed terminal, multi-chunk reconstruction, corruption and
failure-preservation regressions pass. This establishes the private control
model and its observed adapter, without a native performance or crash-survival
claim.

Both 37-file campaign source sets match their recorded manifests. The complete
original 621/894-entry packets and some dated serialized inventory bindings
remain unavailable. Current 66/90 source cases and all 128 distinct historical
case IDs were recovered; derived inventories do not recreate missing original
hashes. New packet seals and original historical seals must stay distinct.

## Evidence and next gate

The Tier C candidate gate passed on
`dab7422b7d2a2db9bd7ffec1f085a78891072918`. Its selector was
`scripts/ci:classify pull_request f57dfc1b dab7422b`, returning `mode=full`,
`complete=true`, all component lanes and catalog/deployment/shards selected,
with installer unselected. Tool/solution restore, warning-clean Debug and
Release builds, format, package audit and the four CI-control guards passed.
The complete Docker-disabled Unit selection passed 13,842 cases with 124
selected skips; Integration passed all 957 cases. Discovery-only skip messages
are excluded from these totals.

The initial supplemental canonical coverage merge failed the ReleaseTool line
floor because Unit ran without the production catalog prerequisite. Preserve
that failure. A focused Distribution Unit selection with the exact retained
approved stars package passed all 116 cases. A new 23-slot coverage mapping
replaced only its Distribution slot; the other 22 input hashes and all package
files remained unchanged. Enforcement passed at 88.8078% line and 75.1101%
branch coverage, with ReleaseTool at 89.03% line and 62.50% branch. The original
Unit totals/skips and the supplemental selection remain separate.

The first native operational fitting attempt, `FIT-native-r1`, exited 1 after
147.64 seconds. Its 34 selected gates and three representative trials passed,
but an embedded external allocation scan hit its explicit 10-second bound.
The function default is 15 seconds; neither bound is raised. The later complete
outer scan passed. Actual raw wait status 256, exit 1, complete stream EOF and
no signals were recorded. Independent outer release found the recorded cohort
empty; the original inner release was not run and postseal agreement remained
red. The four postseal files used 57,344 allocated bytes within their 65,536-byte
cap. No failed receipt is upgraded by the later observations.

The complete final sample charged 119,365,632 evidence bytes against
402,653,184. The larger projected evidence figure was a conservative scenario,
not an observed cap breach. The initial layout also nested the entire fitting
workspace beneath the proposed later campaign packet. A new job may select a
separate namespace under the original finite-root accounting contract: every
active input/output descendant remains charged, historical failed jobs stay
intact, and the later campaign imports complete sealed read-only fitting proof
without carrying its builds, caches, setup or trial credit. This changes path
bindings without changing scientific predicates or numeric limits.

Before correction, deterministic function controls reproduced the costly
embedded path representation. The proposed correction uses the original v7
support function's immutable prefixes for each fresh scan. Across 39 cases and
nine functions, normalized results, entry digests and ordered descriptor calls
agree; ten additional namespace controls preserve complete active-root charging.
Scanner predicates, deadlines, errors and traversal are unchanged. These are
function controls; the separate successful native fitting and finalization
evidence is recorded below.

The separately sealed r2 packet passed all 6,142 source-manifest checks before
admission, then its entrypoint rejected a changed seed Git index before creating
the native namespace. The owner's admission precheck had run `git status`
without disabling optional writes, allowing its stat-cache refresh to replace
the sealed index. No .NET or native fitting gate started. Preserve this failed
attempt; subsequent seed checks must disable optional Git writes and verify
that the complete sealed input remains unchanged after the checks.

R3 passed the read-only seed checks and reached native fitting. It stopped at
`quiet-after-source-t02`: the raw steal counter changed from 540,388 to 540,389.
Its 27 passed, one failed and six unrun gates remain an incomplete attempt.
The corrected external scans, inner and outer strict release, actual wait/EOF,
postseal agreement and complete root accounting passed. The four postseal files
used 57,344 allocated bytes. These mechanism results do not upgrade the failed
quietness predicate or supply qualification credit.

A separate function-only full-success filename fixture retains 1,415 modeled
manifest rows, including all original 119 gates and 24 trials. Its successful
checksum stdout is 110,411 bytes and its lossless encoded field is 8,276 bytes;
the four-file scenarios allocate 61,440 bytes within the unchanged 65,536-byte
limit. The fixture matches both observed fitting inventories before expansion.
No deterministic output-size defect was reproduced. Its size is conditional on
the modeled ordinary-success filenames and observed runtime metadata; future
diagnostics, identities and allocations still require the original actual gate.

R4 completed all 34 selected fitting gates, seven bootstrap envelopes and three
representative trials in one fresh window. The actual tool exited zero after
135.20 seconds. Raw wait status zero, complete EOF, no signals, both strict
release checks, all original postseal checks and complete final root accounting
passed. The four designated files used 57,344 allocated bytes. Independent
checks verified the outer, executor and prepared manifests after completion.
This is operational fitting evidence with zero F7 comparison credit.

The source-derived twice-duration/1.25-byte scenario projects 947.28 seconds
and 359,129,088 evidence bytes against the unchanged 3,880-second whole-window
and 402,653,184-byte evidence limits. Its other component, workspace, native and
sampled RAM scenarios also fit. The four-times-duration/twice-byte stress case
projects an evidence excess and remains recorded. These are explicit sensitivity
scenarios, not confidence or worst-case bounds. Full aggregate cost is still
source-inferred. A fresh full packet must bind the complete read-only fitting
proof, measure its actual input allocation and preserve every actual stop gate
before the original 119-gate/24-trial campaign can be admitted.

The first full campaign, `F7-native-r4`, used a new source-bound once-only
allocation, fresh normal restore/build/setup and the original 119 gates plus
seven bootstrap envelopes and 24-trial selection. Its prepared inputs occupied
313,155,584 allocated bytes. With complete charged read-only fitting proof,
the unchanged model recomputed the selected scenario at 1,030.69 seconds,
359,129,088 evidence bytes and 3,920,769,024 workspace bytes. The adverse stress
scenario and all original actual stop gates remained in force.

The actual full runner exited 1 after 117.24 seconds. It stopped at
`quiet-after-source-t02`: steal changed from 540,594 to 540,595. Its gate totals
are 27 passed, one failed and 91 not run. No aggregate ran, and this incomplete
prefix provides no comparative or regression verdict. Actual wait status 256,
exit 1, complete EOF and no signals were recorded. Inner and outer strict
release, the 57,344-byte four-file receipt and complete root accounting passed.
The owner independently verified all 6,513 prepared-input rows, executor and
outer manifests after completion. The failed allocation is consumed and retained
unchanged; any later attempt requires a separate namespace and fresh admission.

The separate `F7-native-r5` attempt used only namespace and source-hash binding
changes, independently checked across all 30 active Python files. All 6,600
prepared-input rows verified before and after fresh admission; the prepared job
occupied 320,913,408 allocated bytes. Its actual runner exited 1 after 117.00
seconds at `quiet-after-source-t01`: steal changed from 540,653 to 540,654.
The result is 23 passed, one failed and 95 unrun gates, with no aggregate or
comparison credit. Actual typed wait/EOF, both strict release checks, postseal
agreement and final root accounting passed. The prepared, executor and outer
manifests independently verified after completion. This second consumed full
allocation remains unchanged; repeated quietness failures do not justify
relaxing the original predicate.

Durable native evidence is retained under
`/home/roys/review-evidence/1188/jobs/`. Each job contains its admitted ruling,
sealed prepared source/input manifests, `native/executor/result.json`, raw
records, outer wait/release/postseal receipts and owner disposition. Source
manifest identities are:

| Job | Whole prepared-source manifest SHA-256 | Outcome |
| --- | --- | --- |
| `FIT-native-r4` | `66e5d066491ec58d616c55469c2fb1312423ef437f19cd23e254074f7cadb507` | Operational fit passed; three trials, no comparison credit |
| `F7-native-r4` | `e146bc0132b487737e394a213426173161754417ceab7af523375d9e22925a66` | Quietness stop after trial 2 |
| `F7-native-r5` | `2e0a361b909c6a1b83479ec4d1e7d5630f5c7b653acb0e9d58c4eef577551dd5` | Quietness stop after trial 1 |

The owning issue's append-only ledger records the recovered sources, exact
per-file provenance, fail-first receipts and subsequent control runs. Retain all
failed attempts. Before any valid comparison, require completed current controls,
complete bounded output reconstruction, actual terminal/resource/release
receipts, a conservative fitting-window assessment and fresh source/package
bindings. The eventual results must state their measured envelope and limits.

The merge profile is `development-v1`: Preflight plus Build and Unit after
independent review. Tier C local candidate gates and the separate F7 acceptance
remain required. A development integration pass does not establish production
qualification.
