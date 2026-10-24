# Issue #1643 retained astrometry evidence

Implementation base: `839e75d4c761f8e467bfdb1e22803f8e7b2f8c57`, target
`development/v1`. Protected profile: **Development v1 / Preflight** and
**Development v1 / Build and Unit**. The parent owns classifier selection,
the complete Tier C gate, independent deep review, protected CI and PR lifecycle.
This implementation ledger does not claim those pending gates or production
qualification. No defaults, installed rig, shared infrastructure or EF schema
were changed. [Adoption/API/#1644 handoff](../runbooks/retained-astrometry.md).

The generated #1642 software reference supplies actual 512 x 512 Mono16 pixels,
560 seeded catalog rows, a one-second exposure and a 0.04-degree rendered roll
offset. The durable fixture binds a fresh raw descriptor, runs the real scene,
measurement and astrometry recipes, then restores and reads their immutable
products after reopening the SQLite/file stores. Accepted, rejected,
unavailable and execution-budget results are retained. Five payload-tampering
cases (raw, scene, measured associations, context, solution) withhold the read.
Frozen replay reuses the original result despite different current catalog
completeness and a near-zero current budget. Structured manifests and payloads
independently reconstruct the identical accepted solution bytes and artifact ID.
Interrupted publication tests fail after files are published and before node
commit, then forbid further recipe execution during retry.

The central input-reader tests use the existing object-reader/lease boundary,
ingested immutable structured descriptors and no central-producer evidence row.
Runner tests serialize a real claim, preserve measured producer descriptors,
reconstruct its request and obtain identical accepted solution/output identity.
The actual ingest payload dispatcher accepts both native media types, validates
the canonical/native evidence and exact raw/capture/producer/ordered-source
anchors, and rejects altered descriptor, source and payload bytes in eight
focused cases. Remaining cross-artifact semantic validation occurs when the
frozen request is reconstructed. Producer descriptor, context, raw descriptor, scene and time tampering fail
closed. The shared #1642 integrity/prior selection remains the numerical owner.

## Resource comparison

The [original committed comparison](issue-1643-retained-resources-before.json)
is preserved byte-for-byte: enabled warm-process producer allocation was
1,305,640,048 bytes, **failing** the declared 209,715,200-byte ceiling (6.2258x).
The correction first enforced all four existing comparison ceilings in
`RetainedAstrometry_ResourceEvidenceMeasuresBaselineAnalyzerPersistenceAndRead`.
It failed again at 1,305,676,392 bytes before numerical code changed. Both the
failed test/TRX and its complete JSON remain in the registered correction scratch.
An intermediate equivalent change reduced this to 401,989,608 bytes and still
failed; that comparison is also retained, not described as passing.

[Corrected machine-readable evidence](issue-1643-retained-resources.json) uses
SDK 10.0.402, .NET 10.0.13, Linux x64, 12 logical processors, Release and
concurrency one. The identical #1642 seeded 512-square Mono16 raw, 560 catalog
rows, one-second exposure, recipe/solver settings, live stride and durable
SQLite/file pipeline are used. No prior is supplied: every enabled operation
still performs the same **cold numerical solve**. Both arms share one test
process; index 0 of each arm is tagged `cold`, followed by five measured
warm-process operations. Fixture generation/admission precede timing. The baseline still executes raw, scene and
measured associations without astrometry. Sample p95 is the largest of five.

| Phase | Baseline median / sample p95 ms | Enabled median / sample p95 ms | Baseline / enabled CPU median ms | Baseline / enabled allocated bytes |
| --- | ---: | ---: | ---: | ---: |
| Producer plus persistence | 203.80 / 226.88 | 825.44 / 839.27 | 236.98 / 985.32 | 27,925,240 / 203,884,072 |
| Authenticated read | 0.260 / 0.313 | 26.20 / 43.08 | 0.572 / 38.25 | 55,544 / 22,169,104 |

The four enforced warm-process median ceilings now pass: producer allocation
**203,884,072 bytes (194.44 MiB) <= 200 MiB**, producer latency <= 5,000 ms,
read allocation <= 100 MiB and read latency <= 500 ms. The harness also requires
an accepted assessment and the original semantic solution identity on every
enabled iteration. It writes the JSON before asserting resource ceilings.
No ceiling, solver option, search bound, numerical threshold or work concurrency
was changed; an unavailable assessment cannot satisfy this gate.

Thread-allocation attribution on the same shared numerical fixture measured
199,647,472 bytes for the original triangle index, 983,794,320 for search and
28,805,632 for refinement. After stable scalar triangle ordering and nearest-two
selection, the remaining search still allocated about 240 MB; repeated ranking
RMS alone allocated 161,930,480 bytes over 302,049 calls. Most index buckets
held one triangle (139,155 of 190,111). The shared numerical owner now reuses the
identical RMS for each immutable candidate, inserts into the same bounded stable
ranking, avoids a capture closure per visited triangle and starts triangle lists
at one slot. Stable ordering, tie handling, assignments, floating arithmetic,
quality gates, verification partition, identities, finite work bounds and
cancellation checkpoints are preserved. No alternate solver or fitted cache
replaces the existing algorithm. Three direct equivalence tests compare scalar
ordering/nearest selection and incremental ranking with their former stable
LINQ operations, including ties and neighboring floating-point values.

Median sampled producer RSS is 268,156,928 bytes baseline and 352,489,472 enabled;
median producer RSS deltas are 3,092,480 and -606,208 bytes. The enabled process
peak counter reaches 391,028,736 bytes. Median producer logical read/write bytes
are 1,688,805/811,578 baseline and 1,874,753/1,050,517 enabled; storage writes are
831,488 and 1,085,440 bytes. Enabled read adds 2,473,673 logical bytes and no
writes or physical read bytes at the median. Durable roots remain 2,067,940 and
2,306,879 bytes. Counters are process-wide phase deltas; RSS is sampled working
set, not an isolated live-heap peak. These results do not claim lower peak RSS.

Structural/cooperative bounds remain: 2,500 catalog rows, 2 MiB context,
128 KiB solution, 16M raw samples, the 20-second default/60-second maximum recipe
budget, and all native solver bounds. Reads admit one request, at most five
artifacts, 32 MiB raw and 44 MiB total. The aggregate test lowers an internal
budget to exercise accounting; runtime limits are unchanged. Measurements do
not qualify W1/W2/W6, ARM/Pi, physical data, sustained acquisition interference,
full resolution or a production catalog.

The compact accepted solution remains 18,306 bytes with semantic SHA-256
`959067FF97697681F5118ED6543F39C40EEACEC4CA43999D083A75316B6B80A9`.
Catalog rows, scene and calibration remain separate referenced inputs. Numerical
residuals, membership and honest covariance/clock withhold reasons remain the
shared recipe's evidence. The same accepted assessment retains 82 fitting
inliers and 24 verification stars, with fitting RMS
0.006889433368410621 pixels and verification RMS 0.008375508244848805 pixels.
Centroid covariance remains withheld (`centroid-covariance-missing`), and quality
is explicitly not a calibrated probability. Raw/descriptor, producer/input and output identities
are compared across the retained before/after durable trees.

The admitted raw capture is `00000000-0000-0000-0000-000000000001`, raw artifact
`00000000-0000-0000-0000-000000000002`, descriptor identity
`756C70F6FC8AB950805A06F48A768696E42115A4AB012DC3933FC2BEA7C98951`, and raw payload
identity `37EF395A697A4ABA0D0FB4868443C3B04323FDDB77739C657DDF34600D0E610D`.
The unchanged astrometry producer identity is
`E4A523192A75CC766A8853207EB7B059838BE4BA47A23C6CB1B5D50461114B6E`;
measured producer identity is
`989F6E4B14A79B9FB5C682E9D63E9C3BA11DA50B1300717087E3D8F67DEB1AF8`.
The retained solution artifact is `30bbbdfc-f758-8417-b9ed-500c92d5769b`.
All 96 raw/descriptor/product/manifest files across twelve roots compare
byte-for-byte with the failed-before run; SQLite runtime files are excluded
from that byte identity comparison.

## Reproduction and local evidence

Scratch roots created with `hvo scratch HVO.SkyMonitor 1643 implementation` and
`hvo scratch HVO.SkyMonitor 1643 resources` retain logs, TRX, the resource JSON and
the twelve durable resource trees. This run used
`/tmp/hvo-skymonitor-1643-implementation.zdv0vO` and
`/tmp/hvo-skymonitor-1643-resources.AZjLkx`. Every heavy build/test was serialized
with `flock -o /tmp/hvo-heavy-gate.lock timeout ...`; builds use
`-nodeReuse:false -p:UseSharedCompilation=false -warnaserror`. Tests unset
`HVO_PRODUCTION_CATALOG_BUNDLE` and use
`DOCKER_HOST=unix:///tmp/hvo-no-docker.sock`.

Build the CameraAgent, LogicHost and ProcessingRunner test project roots in
Release. Focused selectors are:

- CameraAgent: `FullyQualifiedName~RetainedAstrometry&TestCategory!=Manual`.
- LogicHost: `FullyQualifiedName~CentralDerivativeJobInputReaderTests` and
  `FullyQualifiedName~AstrometryIngestValidationTests`.
- ProcessingRunner: `TestCategory=Unit`.
- Processing: `FullyQualifiedName~MeasuredAstrometry&TestCategory=Unit`.
- Resource method: `FullyQualifiedName~RetainedAstrometry_ResourceEvidence`,
  with `HVO_ISSUE1643_EVIDENCE_ROOT` set to a fresh `hvo scratch` directory.

After the Release build, the exact resource invocation is:

```bash
resource_dir="$(hvo scratch HVO.SkyMonitor 1643 resources)"
flock -o /tmp/hvo-heavy-gate.lock timeout 1200 env \
  -u HVO_PRODUCTION_CATALOG_BUNDLE \
  DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  HVO_ISSUE1643_EVIDENCE_ROOT="$resource_dir" \
  dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release --no-build --no-restore \
  -nodeReuse:false -p:UseSharedCompilation=false \
  --filter 'FullyQualifiedName~RetainedAstrometry_ResourceEvidence'
```

Leave TMPDIR/TMP/TEMP unchanged. Central regressions use the real Docker
endpoint; the Manual resource harness uses only local SQLite/filesystem state.

Per-project inventories add 6 CameraAgent Unit, 11 CameraAgent Integration,
1 CameraAgent Manual, 11 LogicHost Unit and 6 ProcessingRunner Unit cases.
Repository totals become Unit=14272, Integration=984, Manual=288; the parent
candidate gate performs complete discovery. Corrections made during the inner
loop and their failed preliminary logs remain in scratch; final gate outcomes
are reported with the implementation handoff.

Final affected evidence: 17 new capture/graph cases pass (including both
interrupted commits); the wider CameraAgent selection passed 66 cases and its
one stale alias-count assertion passed separately after correction. The real
Kestrel authorization check is included. Shared measured-astrometry Unit passed
55/55; central input reader passed 13/13 and ingest validation 8/8; runner Unit
passed 35/35, with the six affected runner cases rechecked on the final shared
implementation (6/6). The resource method passed 1/1. Affected Release project
builds passed with zero warnings/errors, formatting completed successfully, and
the operations documentation audit passed.

## Owner candidate and target synchronization

The complete Tier C local block ran on `af0909175956ca00a2caacaee22aedf272fa8556`.
Selector `scripts/ci:classify pull_request 839e75d4 af090917` returned
`mode=full complete=false`, all five component lanes selected, and deployment,
catalog, shards, installer, secret-scan-contract and release-script flags false.
The target protected profile remains Development v1; no full workflow was dispatched.

Tool/package restore, warning-clean Debug/Release builds, format verification,
package audit, all four guards, documentation audit, category discovery, both
canonical migration and pending-model checks passed. Real-Docker Integration
passed 984/984, including architecture and publish. Four real publish outputs
have retained checksum manifests. The first Deployment CLI Unit run had 169
environment-induced failures because the evidence harness supplied an insecure,
overlong TMPDIR. With TMPDIR/TMP/TEMP unset, the identical project selection
passed 2,656 cases with zero failures and 121 explicit prerequisite skips.
Original failures and replacement TRX/coverage remain retained.

Target `70eecaefd27a8e1b5c284a40dfe74ac15bad3851` was merged as
`e84dd22e553b99f89ff953ce2a010b1935064462`. Conflicts were confined to inventory
counts and their runbook totals; both branches' deltas were added. The initial
PR and synchronization classifiers select the same five lanes. Fresh synced
Debug/Release, format, discovery, all four guards, documentation and pinned
coverage-policy checks pass. The review-tools guard's temporary-wrapper race
failed once and passed with a stable wrapper; both receipts remain retained.
Fresh full CameraAgent, LogicHost, Processing and combined Unit suites, plus
23 focused capture/runner astrometry cases, cover changed shared options and
their interactions. Unchanged Integration/publish/migration and other Unit
evidence is preserved rather than repeated. Composed results are **14,201 Unit
passes, zero failures and 124 skips** against discovery of 14,325 Unit, 984
Integration and 288 Manual cases. Of the Unit skips, 122 need the absent
production catalog bundle; the others are platform/provider conditions.

The refreshed canonical merge uses exactly 23 slots: 18 preserved reports plus
the five affected replacement Unit reports. Aggregate is 88.7919% line /
75.0002% branch. Shared, CameraAgent, LogicHost and combined component floors
pass. **Canonical and Delivery coverage remain failed** solely at unchanged
`tools/HVO.SkyMonitor.Deployment.ReleaseTool/Program.cs`: 63/155 lines and
31/88 branches, matching the catalog-absent prerequisite result. Its source,
all six baselines and coverage tools are byte-identical across original base,
synced target and candidate. InstallationContracts recovered to 286/286 lines.
No floor was weakened. Mixed preserved/fresh coverage retains older line
mappings for unchanged reports; it is not a fresh whole-head qualification.

Complete commands, logs, UTC timing, TRX, hashes and coverage provenance are
retained in `/tmp/hvo-skymonitor-1643-candidate.J7xjLk/handoff.txt`,
`/tmp/hvo-skymonitor-1643-sync.LeOJ4c/handoff.txt`, and the Deployment replacement
under `/tmp/hvo-skymonitor-1643-owner.Y8zsUy/deployment-rerun/`. These registered
scratch roots are validation evidence to preserve, not runtime state. The
following documentation-only ledger update does not invalidate executable gates.
Independent review and fresh target-profile CI remain separate merge gates.

## Initial-review corrections

Reviewed head `41639131da9c36f4ca6f8ec14d33dae160107edf` was merged normally
with freshly fetched target `6a1e13bd1c8942ec55c2ce7ba7d1e8b5ded505d3` as
`9c0ccbb382c05140dd5b4013d27f00860fec0008`. Only CHANGELOG, the CameraAgent
inventory and runbook totals conflicted; both issue entries and count deltas
were retained. No reviewed history was rewritten. The target profile remains
Development v1; full `ci.yml` was not dispatched.

All four blocking findings have executable failed-before evidence in
`/tmp/hvo-skymonitor-1643-corrections.CRiHbJ/logs/` and matching named TRX files:

- F1: `NativeAstrometrySourceFactsPassReconciliation(false/true)` failed before
  the native partial-fact correction. It now checks every resolved role and
  supplied producer, rejects missing measured-producer facts, and accepts native
  solution readout hashes normalized to lowercase. Other structured products
  retain their existing completeness requirements.
- F2: `OnlyAstrometryPriorRawCanResolveAcrossFrames` failed before the narrow
  ordinal-3 context exception. Real-Docker
  `RetainedAstrometry_NativeIngestReconcilesAndReadsExactProducts` then passed
  without a prior and rejected a foreign-device prior, while the same-device
  earlier capture remained `PendingReference / lineage.source-not-found`.
  After correction all three cases pass; context/solution source rows come from
  actual ingest and reconciliation, reach Complete when authorized, and read
  back exact payloads through the owner-authenticated central endpoint. The test
  only clears retry timestamps to make pending work due; it never assigns
  resolution IDs or object/reconstruction-state outcomes.
- F3: five rows of
  `RetainedAstrometry_InvalidReadEvidenceReturnsUnavailableAndCancellationPropagates`
  failed with InvalidDataException before its catch-filter correction; the
  cancellation row passed. All six now pass. Individual raw/solution limits,
  aggregate accounting and raw/product envelope mismatches return Unavailable
  with no solution; caller cancellation propagates.
- F4: the enforced Manual harness failed first, then passed under its unchanged
  ceilings and original accepted identity. The original comparison and each
  intermediate failure remain separate evidence. No owner exception is assumed.

Correction inventories are Unit=14341, Integration=993 and Manual=288.
Final classifier receipts, candidate builds/guards/discovery/affected suites,
coverage provenance and any residual blockers are recorded in the correction
scratch handoff. Pending gates are not claimed passed here. The unchanged
ReleaseTool prerequisite coverage failure documented above remains separate
from corrected runtime/resource acceptance; no coverage floor was changed.

The broader central selection exposed a fixture interaction after the new native
capture cases: `MultipartIngestV2_PersistsReconstructableFactsAndBindsHistoricalRig`
claimed an unrelated `annotation` job while expecting `image-quality`. The same
failure reproduced with only the three new cases plus that existing method
(`central-queue-isolation-before`, 3 passes / 1 failure). Cleanup now terminalizes
only background jobs belonging to registrations created by the new fixture,
after its assertions, including the foreign-prior registration. No artifact
resolution or reconstruction result is assigned by cleanup. The four-case
replacement passes 4/4; affected Debug/Release builds remain warning-clean.
The original 150-case failure and both minimal reproducer runs are preserved.

## Final correction candidate evidence

Executable candidate `a7bbf22fde38ea438ae8135474643fc5bd014942`, plus the
fixture-only queue cleanup above, has these completed local results:

- Both `scripts/ci:classify` ranges (reviewed `41639131` to candidate, and target
  `6a1e13bd` to candidate) select `mode=full complete=false`, shared,
  CameraAgent, LogicHost, combined and delivery; deployment/catalog/shards/
  installer/secret-scan-contract/release-script flags are false. The protected
  target profile is Development v1, not full qualification.
- Whole-solution Debug and Release builds pass with zero warnings/errors.
  Whole-solution formatting verification, all four CI-control guards,
  operations-doc audit, complete category discovery and coverage-policy checks
  pass. The later test-only cleanup has its own warning-clean Debug/Release
  project builds and formatting verification.
- Seven full affected Unit projects pass: Astronomy 798, Catalog.Sqlite 296,
  Processing 1,044, CameraAgent 7,379 plus one platform skip, LogicHost 979 plus
  one provider skip, ProcessingRunner 35, and combined 89. Fresh evidence is
  **10,620 passes, zero failures, two skips**. With the retained unaffected
  selections, composed Unit evidence is **14,217 passes, zero failures and
  124 skips**, matching discovery of 14,341. The 122 other skips require the
  absent production catalog. This is composed evidence, not a fresh whole-head
  solution Unit run.
- All 17 retained-capture Integration cases pass with coverage, including the
  six reader regressions. The corrected real-Docker central ingestion,
  retrieval, derivative/runner and reconstruction selection passes **150/150**.
  Its initial 149/150 result and the minimal 3/4 -> 4/4 queue-isolation evidence
  remain retained; the successful 150/150 rerun replaces that failed gate.
- The enforced Manual resource harness passes on the built candidate with the
  final JSON above: 203,884,072 producer bytes, 825.44 ms producer median,
  22,169,104 read bytes and 26.20 ms read median. Exact raw/descriptor,
  producer/options, product bytes, sidecars and output identities are unchanged.

Canonical coverage uses the same closed 23 slots: six fresh canonical full-Unit
reports and 17 preserved restricted reports, with fresh affected Integration
coverage merged into the corresponding CameraAgent/LogicHost slots. The runner
report remains outside the canonical rollup, as the existing policy requires.
No stale shifted line mappings are retained for changed code. Preserved coverage
is restricted to byte-identical files, plus the byte-identical, same-line prefix
of `CentralArtifactReconciliationService.cs` before `ResolveReferencesAsync`
(line 2051). Old coverage from that changed method onward is discarded; changed
regions use current reports. Prefix SHA, source heads, removed classes, source
mappings, raw report hashes and merge commands are recorded per slot in
`canonical-reports.json`. The first whole-file-discard experiment, which lost
unchanged recovery-method coverage, is preserved separately.

The conservative composed aggregate is **88.8573% line / 75.0995% branch**.
Shared, CameraAgent, LogicHost and combined floors pass. **Canonical and Delivery
remain failed** solely at unchanged ReleaseTool coverage: 63/155 lines and
31/88 branches. The source, package/SDK pins and all six coverage baselines are
byte-identical to reviewed head and target. No catalog was supplied and no floor
was weakened. This remains a merge blocker, not a green qualification result.

Unchanged prior package/tool restore, package audit, other Integration,
architecture/publish and migration evidence remains retained in the earlier
candidate/sync/owner roots. Publishing and migration behavior did not change;
those outputs were not regenerated for this correction. Complete local commands,
TRX, failed-before/passed-after logs, resource roots, attribution source,
identity proofs, source-mapping policy and current results are in
`/tmp/hvo-skymonitor-1643-corrections.CRiHbJ/handoff.txt` and `summary.json`.
The parent owns the single push, independent deep correction review, fresh
protected CI and remaining lifecycle. This editor performed no push, GitHub
mutation, claim/release, review dispatch or finalization.
