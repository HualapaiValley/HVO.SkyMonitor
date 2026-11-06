# Deferred F7 catalog qualification (#1188)

This record owns the full catalog-only comparison deferred from #1186. The
foundation and curated annotation delivery do not establish this qualification.
No production optimization, catalog trust change, release, deployment, hardware
qualification or scientific acceptance is included.

## Qualification state

**VALID 1 for the pinned catalog-only software comparison under the declared
owner-approved VM-steal policy.** `F7-native-r7` completed all 119 ordered gates,
seven bootstrap invocations and 24 fresh trials in 375.437064 seconds. Paired
star outputs agree and the independently recomputed aggregate passes the
original startup/common-star/deep-sky regression rules, with no explanation
flags. Native waits, complete streams, resources, strict release, postseal and
final checks passed. Independent PR review and final integration remain separate
lifecycle gates; this is not production or scientific adoption acceptance.

Three earlier full campaigns across two hosts stopped at the original strict
quietness gates. They remain failed and immutable. The subsequent owner decision
changed only prospective steal-counter rejection on the selected original VM;
raw counters and all other criteria remain. The completed results below include
the added catalog's measured startup, memory, CPU and throughput costs rather
than claim that every metric is unchanged.

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

## Acceptance and prospective owner decision

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

The runqueue predicate, 2-GiB process peak limit, 8-GiB cohort allowance,
component and reserve limits, 119 ordered gates and finite phase bounds remain.
The original no-steal rule changes only through the owner decision below. Only
the selected host's shared heavy gate may be reserved.
Boundary samples do not prove continuous quietness or continuous peak
containment. Fresh processes do not imply cold operating-system storage caches.

On 2026-10-24, the [owner approved a prospective VM steal
exception](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1188#issuecomment-6386492204)
for both arms on the original claim host. New runs must retain and disclose raw
before/after steal counters and deltas, but increases alone no longer reject a
run. Other quietness and acceptance limits remain. This does not establish a
causal absence of steal effects or change any prior failed allocation. Fresh
host, toolchain and resource bindings, fail-first policy controls, a new measured
FIT and a distinct full allocation were completed before accepting the full
comparison.

The fresh `FIT-native-r7` completed all 34 selected gates plus seven bootstrap
invocations and three representative trials in 153.061216 seconds. Native typed
wait/exit, complete EOF, no signals, strict release, resource, postseal and final
checks passed. Paired steal deltas were 0, 1 and 6 ticks; all raw counters remain
retained. The 30 policy controls include a real failure against the old observer,
plus passing cases for the exception and preserved runqueue, reset, malformed
data, host and authority rejection.

The owner independently verified all source, executor and outer manifest rows
and recomputed the unchanged capacity model from all 41 fresh observations.
Its selected scenario projects 1,117.694955 seconds, 361,021,440 evidence bytes
and 4,086,118,400 workspace bytes within the original caps. The adverse stress
scenario exceeds the evidence cap at 527,298,560 bytes. These are sensitivity
scenarios, with actual full-run checks still mandatory. The frozen
`OWNER-FIT-DISPOSITION.json` SHA-256 is
`f2a2a53d406ec1722ae97658ddde99b7e9e24568c8398a890f4304d1a5d62581`.
It authorizes preparation of a distinct full packet; it grants no full
allocation or comparative qualification.

## Completed comparison and measured envelope

The [machine-readable results](catalog-f7-1188-results.json) retain each group's
six-trial median/range, CPU and verification costs, allocations, working set,
I/O, throughput, all 90 pinned deep-sky measurements and all 24 raw paired steal
deltas. Both comparisons use the exact sources, packages and query workload
above. The runtime was Ubuntu 24.04.5 x64, SDK 10.0.402 / .NET 10.0.13,
12 processors, workstation GC, concurrency one and initial backlog zero, with
local filesystem I/O. No queued or concurrent workload was qualified.

Startup summaries below each use six fresh processes. They include authenticated
validation, eager loading and index construction. The composition comparison's
higher cost is retained; no single causal foundation effect is inferred from
the source comparison's different dependency versions.

| Comparison / arm | Startup median [range], ms | CPU median, ms | Allocated median, MiB | Startup peak working set median, MiB | Logical reads median, bytes | Read calls median |
| --- | --- | --- | --- | --- | --- | --- |
| Source / baseline stars | 778.182 [716.744–1271.086] | 829.933 | 89.964 | 174.018 | 32,371,714.0 | 4025 |
| Source / candidate stars | 764.834 [738.456–917.308] | 828.599 | 90.329 | 176.371 | 32,371,706.5 | 275 |
| Composition / candidate stars | 795.526 [759.275–915.710] | 855.534 | 90.326 | 176.742 | 32,371,706.5 | 275 |
| Composition / candidate composed | 1200.665 [1124.085–1474.172] | 1308.965 | 137.507 | 210.900 | 46,540,419.0 | 376 |

Startup storage-read counters were zero in all 24 trials. They do not establish
cold-cache I/O performance: the application cache was empty while the operating
system page cache was uncontrolled. Storage bytes, logical bytes and read-call
counts remain distinct.

Query values below are medians across six per-process summaries, not pooled
operation percentiles. Every process used five warmups and 100 measured
operations; output verification is outside the operation timer and its CPU is
reported separately in the JSON.

| Comparison / arm | Query | Median latency, ms | p95 latency, ms | Throughput, operations/s | Process CPU/operation, ms |
| --- | --- | --- | --- | --- | --- |
| Source / baseline stars | All sky | 0.05210 | 0.06035 | 17,491.3 | 0.06057 |
| Source / baseline stars | Bounded cap | 0.41780 | 0.47005 | 2,351.3 | 0.42929 |
| Source / candidate stars | All sky | 0.05405 | 0.06195 | 16,829.9 | 0.06423 |
| Source / candidate stars | Bounded cap | 0.41020 | 0.43485 | 2,398.7 | 0.42050 |
| Composition / candidate stars | All sky | 0.05560 | 0.06640 | 16,180.8 | 0.06576 |
| Composition / candidate stars | Bounded cap | 0.40815 | 0.44210 | 2,412.6 | 0.42011 |
| Composition / candidate composed | All sky | 0.05805 | 0.10005 | 6,778.8 | 0.33940 |
| Composition / candidate composed | Bounded cap | 0.40710 | 0.45055 | 2,332.7 | 0.57676 |

The composed arm has lower observed all-sky throughput and higher process CPU
than the stars-only composition arm. Throughput is computed from the sum of all
100 operation times, so tail operations affect it even when the reported p95
remains below one millisecond. The six composed all-sky maxima were
3.5366–10.3908 ms; the stars-only maxima were 0.4125–1.2878 ms. The retained data
do not identify GC or VM steal as a proven cause, and no CPU-ratio or unchanged
throughput claim follows from the latency gate passing. These costs are part of
this measured envelope. All-sky allocation medians were 142,856 bytes/operation
in both composition arms.

All six composed trials validated the 50-pointing, 20-degree region sweep and
all 15 nonempty pinned deep-sky queries against equivalent within-candidate
linear outputs. Across the 90 pinned measurements, the largest indexed-minus-
linear p95 difference was +0.4118 ms (`brightness-Visual-false` in
`composition-t10`: 0.5300 versus 0.1182 ms), below the original +1-ms criterion.
No stars-only deep-sky operation or HYG timing ratio is invented.

The largest terminal GNU-time process-tree RSS was 259,289,088 bytes, below the
2-GiB limit. This later peak includes retained linear deep-sky baselines and is
separate from startup memory. Final accounting charged 2,999,582,720 workspace
bytes, including 227,024,896 evidence bytes; every original component/reserve
check passed. The four postseal files used 57,344/65,536 bytes. All 126 gate and
bootstrap resource boundaries passed. Boundary observations do not certify
continuous peaks or continuous quietness.

The 24 paired samples retained 181 total steal ticks across 20 trials, with a
maximum of 56 ticks in one trial and maximum observed runqueue two against
12 processors. Increases were accepted only under the exact owner-bound rule;
malformed/reset/host/authority/runqueue failures remain rejecting. The original
zero-steal method was not satisfied or retrospectively reinterpreted.

Parent verification joined the actual tool return with complete typed native
waits, EOF, no signals, strict inner/outer release, postseal/root/afterfinal
receipts, all 7,317 prepared-input rows, 1,415 executor entries and twelve outer
entries. A separate pure-reader recomputation reproduced the complete 24-trial
aggregate exactly, including all output/TRX/GNU-time checks and no unexplained
regression flags. No benchmark or partial-prefix aggregate was rerun.

Canonical evidence remains at
`/home/roys/review-evidence/1188/jobs/F7-native-r7`. Its admitted ruling SHA-256
is `5c80963b31467d643fcc92dbc677d05f8dba4a9d29af3b9a202df2ff7e3141bb`;
`OWNER-F7-DISPOSITION.json` SHA-256 is
`22a155bfe0c9957d165f4140ddc89b6aeb45c64d7070af1db21270433f64d181`.
The native provisional VALID0 markers remain immutable. The owner's acceptance
record joins the completed return and evidence and grants the scoped VALID1
above. This result does not qualify cold-cache startup, different packages or
queries, parallel/backlogged hosts, other hardware, releases or live deployment.

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
The initial 90 controls and 119 fake gate interactions passed. Their snapshots contain
10,334,069 reconstructed bytes; deterministic lossless compression keeps the
complete native observer output at 852,193 logical bytes and 1,740,800 allocated
bytes, within the unchanged output limits. The actual adapter exits zero with
raw wait status zero in 1.61 seconds, sends no signals, and observes an empty
released cohort. Typed terminal, multi-chunk reconstruction, corruption and
failure-preservation regressions pass. This establishes the private control
model and its observed adapter, without a native performance or crash-survival
claim.

Independent initial PR review subsequently reproduced F1: the afterseal oracle
accepted Boolean/integer substitutions in wait status, flags and usage because
Python equality treated them as equal. New `afterseal_terminal_regressions`
failed against that reviewed oracle before correction. Exact terminal schemas,
types and agreement with independently recorded fake wait observations now
reject all 60 mutations across raw-only, receipt-only and matching changes to
both artifacts. The affected suite passes all 90 original controls and 119
ordered fake gates, ten whole-execute terminal rejections, the 60 afterseal
rejections, three-chunk reconstruction and four failure-preservation cases.
Its 10,346,059 reconstructed snapshot bytes retain complete observations;
the first correction's regression output totals 2,361,813 logical bytes with a
65,536-byte maximum record, within the unchanged 8 MiB aggregate and per-record
caps. This corrects
the private oracle; no selected executor or native measurement code changed.
The review independently verified the real r7 typed receipts and found no
malformed native wait. Original native packets and all failures remain unchanged;
F1 requires no repeat performance campaign. Correction review remains required.

The first correction review found F1 only partially corrected: the available
printed terminal was still unchecked when raw-terminal serialization failed.
New fail-first tests reproduce that path with all five original substitutions.
Twenty printed-only schema/type mutations and three known/unknown wait checks
now cover it. The selected final printed terminal is validated independently of
raw-file presence; an observed wait cannot be erased as UNKNOWN, and an
unobserved wait cannot be asserted as known. The named serialization error,
missing raw file, journal fallback and retained failure remain unchanged.
All 23 new checks and the complete affected private suite pass within the same
caps, including all 90 original controls and 119 ordered fake gate interactions.
Native receipts, selected executor code, measurements and limits are untouched.

The second correction review found an earlier printed terminal still bypassed
validation in the existing two-receipt parent-close failure. New fail-first
`afterseal_all_printed_terminal_regressions` and
`afterseal_printed_terminal_corpus_regressions` reproduce the original five
substitutions on that first receipt against the reviewed oracle. The corrected
oracle validates each available printed terminal, including terminals without
the outer receipt marker, and compares each against the raw terminal when
present. All 56 two-receipt checks and 183 substitutions across 39 terminals in
the 48 existing afterseal cases pass. The provisional first result, actual exit
1, close diagnostic, raw artifacts and independent wait observations remain
unchanged. The complete affected suite also passes within the original caps:
10,346,059 reconstructed snapshot bytes, 2,361,812 logical output bytes and a
65,536-byte maximum record. This private oracle correction changes no selected
executor, native packet, numerical acceptance criterion or measurement.

Both 37-file campaign source sets match their recorded manifests. The complete
original 621/894-entry packets and some dated serialized inventory bindings
remain unavailable. Current 66/90 source cases and all 128 distinct historical
case IDs were recovered; derived inventories do not recreate missing original
hashes. New packet seals and original historical seals must stay distinct.

## Local validation and retained execution history

After merging target `6a1e13bd1c8942ec55c2ce7ba7d1e8b5ded505d3`, the
complete Tier C candidate gate passed on
`34bd74d985a94db88e32b12c39b2de2721cfa67e` at 2026-10-24 18:08:50 UTC.
Its selector, `scripts/ci:classify pull_request 6a1e13bd 34bd74d9`, returned
`mode=full`, `complete=true`, all component lanes and
catalog/deployment/shards selected, with installer unselected. Both restores,
warning-clean Debug and Release builds, format, package audit and all four
CI-control guards passed. Docker-disabled Unit passed **14,188 cases with 124
selected skips**; Integration passed **973 cases**. These totals come from all
21 projects' TRX outcomes, checked against category inventories, and exclude
stdout discovery-only skips.

All 23 canonical coverage slots were joined through their TRX attachments.
The first merge again failed the ReleaseTool line floor at 40.65% because
the complete Unit gate intentionally omits the production catalog prerequisite.
That failed report is retained. A separate focused Distribution Unit invocation
with the exact pinned stars package passed **116/116** cases. Replacing only
its coverage slot, with the other 22 inputs and package files unchanged,
passed enforcement at **88.9240% line / 75.1947% branch**. ReleaseTool passed
at **89.03% line / 62.50% branch**. The supplemental invocation does not replace
the complete Unit totals or skips. The composite mapping SHA-256 is
`252fb40007218633e143e4879ca834cadaab10172c3ea37de81e5267bad4893a`;
merged report SHA-256 is
`30f9ca57b24f4f6f90eec88137ceebce8cb405861e3caf5be07b35b1e639c9ac`.
Later edits to this validation record do not change the tested executable tree.
Final target synchronization and its affected validation remain separate
lifecycle requirements.

The earlier Tier C candidate gate passed on
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

The owner then authorized moving both comparison arms to another host using its
normal heavy gate. A separate Linux x64 host passed fresh source/toolchain,
capacity and read-only admission checks. Both arms retained SDK 10.0.402 and
the original source/package/query identities. Its SDK compiler/MSBuild/config
bytes match the original host; its different launcher and native file metadata
are explicitly pinned. Original quietness and all numerical caps remain intact.

The fresh `FIT-alt-r6` completed all 34 selected gates, seven bootstrap envelopes
and three representative trials. Its native runner reported operational closure
after 146.91 seconds; the separately recorded SSH transport returned zero after
148.11 seconds. Native raw wait status zero, complete EOF, no signals, all six
quiet boundaries, both strict release checks, the 57,344-byte postseal receipt,
final root accounting and terminal reserve passed. Independent verification
covered all 5,886 prepared inputs, 714 executor entries and twelve outer entries.
The 1,109 selected native proof files matched their original hashes; an additional
retrieval manifest records that selection. SSH client usage is transport evidence
and is not native process resource usage.

Full preparation also found four inherited seed Git reads that omitted
`--no-optional-locks` in `allocation-copy.stdin.py`. Its actual extracted status
argv returned clean while refreshing a stale Git index. The corrected argv
preserved all metadata while returning clean and detecting a dirty tracked edit.
All four seed reads now disable optional writes; fresh destination clone/fetch
writes remain enabled. The native full guardian, standalone read-only precheck
and projection module remain byte-identical to prior source. Historical sealed
packets remain unchanged. The prepared report's virtual baseline count of 3,135
is a prose typo: its unchanged sealed inventory contains 3,132 tracked files.

The separately sealed `F7-alt-r6` occupied 339,546,112 allocated bytes, including
60,227,584 bytes of complete selected read-only fitting and transport proof.
Independent recomputation matched all three scenarios from the unchanged model.
The selected scenario projects 1,000.75 seconds, 323,072,000 evidence bytes and
3,883,913,216 workspace bytes, within the original caps. The adverse scenario's
463,163,392 evidence bytes remain over the limit. These scenarios grant no
quietness or worst-case guarantee; no fitting setup, build, cache or trial credit
carries into the full campaign.

The actual `F7-alt-r6` native runner reported failure after 133.08 seconds;
transport returned 1 after 134.12 seconds. `quiet-after-source-t01` rejected steal
207,617 to 207,618. Its totals are 23 passed, one failed and 95 unrun gates.
No aggregate ran. Native raw wait status 256, exit 1, complete EOF, no signals,
both strict releases, the 57,344-byte postseal receipt and final resource checks
passed. All 7,027 prepared inputs, 586 executor entries and twelve outer entries
independently verified afterward. This consumed allocation remains failed and
unchanged, with zero comparison credit.

Further read-only discovery found the checked CI runners below the original
12-GiB available-RAM floor, an edge host outside the required architecture/RAM/
SDK closure, another development host outside the current OOM admission, and
unreachable desktop candidates through direct and proxy routes. Another eligible
development host observed 21 steal ticks in a passive minute. These observations
do not prove every future window must fail. They establish no currently verified
alternative for completing the unchanged campaign. This was the retained
blocker before the prospective owner decision and fresh r7 comparison above;
no other-host schedule change was used to resolve it.

Durable native evidence is retained on each original execution host under
`/home/roys/review-evidence/1188/jobs/`. Each canonical job contains its admitted ruling,
sealed prepared source/input manifests, `native/executor/result.json`, raw
records, outer wait/release/postseal receipts and owner disposition. Source
manifest identities are:

| Job | Whole prepared-source manifest SHA-256 | Outcome |
| --- | --- | --- |
| `FIT-native-r4` | `66e5d066491ec58d616c55469c2fb1312423ef437f19cd23e254074f7cadb507` | Operational fit passed; three trials, no comparison credit |
| `F7-native-r4` | `e146bc0132b487737e394a213426173161754417ceab7af523375d9e22925a66` | Quietness stop after trial 2 |
| `F7-native-r5` | `2e0a361b909c6a1b83479ec4d1e7d5630f5c7b653acb0e9d58c4eef577551dd5` | Quietness stop after trial 1 |
| `FIT-alt-r6` | `a9fad3185366d1a95eb6dd6796a4628fa45f0203499de6796c40cd8865cdc734` | Alternate-host operational fit passed; no comparison credit |
| `F7-alt-r6` | `6e4b1dbad6fa3cfac97d7831b1a2bb01e29557f9fd3c6b260b38e8bda43a95a7` | Alternate-host quietness stop after trial 1 |
| `FIT-native-r7` | `166c0b8e2887484eabe9aaeeff0b17ee82a80443e4457276bc93fb18f7f67fbe` | Fresh prospective-method operational FIT passed |
| `F7-native-r7` | `573a7d227863e4ea2f255b7d260f2ce42e8bc99c22cc572cc1506ded2fc586db` | Full119+7/24 passed under declared prospective method |

The r6 canonical jobs remain on the alternate host. The local sealed archive
`/home/roys/review-evidence/1188/alternate-host-followup-20261024T0730Z`
contains selected proof under `parent-records/FIT-alt-r6-native-proof/` and
`parent-records/F7-alt-r6-failed-native-proof/`, with their retrieval manifests
and exclusions. Its 2,420-entry manifest SHA-256 is
`dabe51f39b2b0a6b8bb4a4496b7bf15d2d813253a1918e0fc41cd224e4db390a`.
Those local selections omit compiled helpers, catalogs, caches/worktrees and
binary-containing record archives; they are not complete original remote
packets.

The owning issue's append-only ledger records the recovered sources, exact
per-file provenance, fail-first receipts and subsequent control runs. Retain all
failed attempts. The accepted r7 comparison joins current controls, complete
bounded output reconstruction, actual terminal/resource/release receipts, a
fresh measured fitting assessment and exact source/package bindings. Its
measured envelope and limits are recorded above.

The merge profile is `development-v1`: Preflight plus Build and Unit after
independent review. Tier C local candidate gates and the separate F7 acceptance
remain required. A development integration pass does not establish production
qualification.
