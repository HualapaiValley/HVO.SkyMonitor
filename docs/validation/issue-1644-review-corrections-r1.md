# Issue #1644 review corrections, round 1

This is local correction evidence for PR #1662, targeting **development-v1**
(Preflight and Build and Unit). It does not claim production qualification.
The parent owns the single push, independent correction/base-sync review,
review ledger, PR description and finalization.

The previous reviewed head is `d80d0dd6dde9dc98ca2d6c73f94dc127964733c2`.
Target `edb63ef898b14a8da369dc0b116ac6873a7b059c` was merged as
`692ddfaa00a46105167bdb7cec1fdfb0c579b9f1`. Both finding locations were unchanged
by that merge. The corrected production source tree is
`795fa4a2e54b80860a1651f595aae84268d18bee` (`git rev-parse <candidate>:src`).
[Source and base-interaction provenance](evidence/issue-1644/review-corrections-r1/source-provenance.json)
binds the original production hashes, test patch and new source bytes.

## F1: one science exposure per mapping

The added `stack` row of
`RetainedAstrometry_ViewerPublishesTypedDiagnosticsAndFrozenExportWithoutChangingBase`
uses the real 512-square Mono16 workload, valid consecutive captures 60/61 with
one-second exposures two seconds apart, production `RollingMean(2)`, Packed
`EncodedPreview` and the production derivative descriptor factory. Sequence 61
satisfies the existing capture cadence; no cadence or solver budget was relaxed.

Before the fix, the preview's two-second integration was assigned a compatible
base for the latest capture's one-second fit. After the fix, the publisher
returns the original manifest, no compatible base and no measured layers.
The accepted single-exposure case still publishes diagnostics, round-trips
coordinates and saves/replays the exact frozen selection. Existing component
stage tests verify that an accepted raw mapping without compatible-base evidence
cannot enable the processed-image calculator.

`HasSingleExposureAstrometricMappingAsync` requires an unambiguous complete science chain,
matching capture, exposure endpoints and effective integration at every
recognized derivative, within eight source edges. Reference-calibration masters
cannot establish the science mapping. Alternate/duplicate ancestry, missing or
unavailable parents, mismatched integration/timing, unknown producers, excess
depth and cycles fail closed. The generic trail predicate is unchanged.
`AstrometricSourceMappingTests` covers 17 bounded cases.

## F2: same-capture completion order

`OlderStandaloneAstrometryCannotReplaceNewerSameCaptureLayerEvidence` starts a
pending standalone read after missing layers, applies a newer accepted layer
snapshot with a compatible base, then completes the older read. It uses the
existing checksum-pinned retained accepted solution, never a fresh UI fit.

Before the fix, late success disables the calculator; late failure and timeout
replace Accepted with Unavailable. All three preserve the newer accepted snapshot
after the fix. An independent astrometry read revision invalidates stale success
and error paths when evidence is applied or cleared. Authorization denial is
handled before revision/capture checks and still clears sensitive content.
The fourth completion-order case verifies that older-read denial remains
unconditional. No sleeps or increased wait budgets establish the ordering.

## Red-before-green evidence

The registered root is
`/tmp/hvo-skymonitor-1644-review-corrections-r1.aXOPER`.
[Exact commands, exits, log hashes and TRX hashes](evidence/issue-1644/review-corrections-r1/gate-results.json)
retain all attempts. Production source hashes remained identical to the merged
pre-fix head through both finding reproductions.

| Selection | Pre-fix result | Corrected result |
| --- | --- | --- |
| F1 viewer rows (`red-f1/red-f1.trx`) | Stack assertion fails; five existing rows pass | All six rows pass |
| F2 completion rows (`red-reproducers/red-reproducers.trx`) | Accepted/failure/timeout assertions fail; denial passes | All four pass |
| Focused corrected selection (`green-reproducers-fixed-import/green-reproducers.trx`) | Not used as baseline | 27 passed, zero failed/skipped |

Initial harness attempts encountered completion scheduling, journal sequence and
capture-cadence errors; one supplemental-test compile lacked a namespace import.
The first browser launch also failed before rendering because the long scratch
label exceeded the Unix-domain socket path limit. Its five failures and copied
hosts remain at `/tmp/hvo-skymonitor-1644-review-corrections-browser-r1.3IBsgM`;
a shorter registered root fixes the prerequisite without source changes.
Those attempts and the initial whitespace failure remain retained, and are not
counted as successful finding reproductions or passing gates.

## Validation scope

`scripts/ci:classify pull_request d80d0dd6 692ddfaa` selected `mode=full`,
`complete=false`, shared/CameraAgent/LogicHost/combined/delivery true, and
all deployment, release-script and secret-scan-contract flags false.
The final immutable `d80d0dd6..<candidate>` review delta and
`692ddfaa..<candidate>` correction selectors are retained as
`classifier-review-candidate.txt` and `classifier-correction-candidate.txt` in the
registered root after the single correction commit. The selector does not
authorize a full workflow dispatch for this target.

The affected selection includes warning-clean solution Debug/Release builds,
format verification, package audit, the four CI-control guards, operations/docs
count audit, focused canonical category discovery, complete CameraAgent Unit and
storage Integration with coverage, shared Processing/Imaging Unit, LogicHost and
combined contract Unit, and central materialization Integration for all five
retained version combinations including scene style v5.

The incoming scene-style change affects Processing, Imaging and both hosts.
The source comparison records unchanged project trees and unchanged SDK,
package, solution and coverage inputs. Existing default/legacy behavior remains
covered while new scene-style validation runs against the combined source.
Unchanged Delivery, numerical producer benchmarks, publish/migration and other
unaffected original green slots retain their previous evidence; the complete
21-project selection was not repeated.

The [original candidate record](issue-1644-candidate.md) and all original reports
remain preserved. Its catalog-absent ReleaseTool limitation, **63/155 lines and
31/88 branches**, remains disclosed. No floor was weakened and no full
qualification workflow was dispatched. Fresh affected raw coverage reports are
retained separately for the parent's canonical composition; a partial central
Integration report does not replace the original complete LogicHost slot.

## Affected gate results

Both solution builds pass with **zero warnings/errors**. Format verification,
package audit, all four CI-control guards and the operations inventory audit
pass. Scoped canonical discovery verifies CameraAgent, Processing, combined
contracts and LogicHost Integration: Unit=8767, Integration=913, Manual=169.
Declared whole-repository totals are Unit=14581, Integration=1000, Manual=294.

| Covered selection | Passed | Expected skips |
| --- | ---: | ---: |
| CameraAgent Unit | 7496 | 1 macOS-only prerequisite |
| CameraAgent storage Integration | 325 | 0 |
| Processing Unit | 1157 | 0 |
| Imaging Unit | 493 | 0 |
| LogicHost Unit | 984 | 1 provider-unreachable state |
| Combined contract Unit | 94 | 0 |
| Central materialization Integration (v1/v2/mixed/v3/v5) | 5 | 0 |

[Fresh covered reports](evidence/issue-1644/review-corrections-r1/replacement-coverage.json)
name every report, hash, command/selection and replacement scope. The central
five-case report is supplemental interaction evidence. All test-exclusive
central containers were removed by their fixtures; shared services were retained.

## Fresh browser and resource evidence

The unchanged five-case Manual selection passes **5/5**, with no skipped cases,
on canonical immutable source
`/tmp/hvo-skymonitor-1644-canonical-viewer.hLz32w` and fresh registered output
`/tmp/hvo-skymonitor-1644-r1b.YYd6HM`. All original/prepared file hashes survive
reads, rendering and export. The frozen residual-only raster has exactly the
same source IDs and SHA-256 as the original run:
`D2E38F8E7DD5FA461EFDF9288F4A1C4001375B9AD689DCAFFE65514FB3DA49E7`.
The generator and unchanged numerical benchmark were not rerun.

[Raw reads](evidence/issue-1644/review-corrections-r1/read-resources.json),
[browser samples](evidence/issue-1644/review-corrections-r1/accepted-browser.json)
and [maxima](evidence/issue-1644/review-corrections-r1/resource-maxima.json)
retain one first instrumented call plus five warm calls per operation, at
concurrency one after login. These are small virtual-workload regression bounds,
not cold-cache measurements or production percentiles.

| Warm operation | Max wall ms | Max host CPU ms | Max allocation MiB | Max positive RSS MiB | Max logical reads MiB | Opens / declared bytes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| base-composition | 6.17 | 6.67 | 0.27 | 0.03 | 0.47 | 2 / 262,819 |
| retained-mapping-diagnostics | 128.37 | 148.38 | 28.82 | 13.96 | 2.38 | 5 / 949,966 |
| measured-composition | 113.52 | 147.28 | 31.50 | 0.32 | 3.33 | 10 / 1,253,655 |
| authenticated-astrometry | 104.59 | 115.71 | 29.03 | 0.11 | 2.41 | not instrumented at HTTP boundary |
| authenticated-presentation | 120.19 | 167.12 | 31.89 | 0.39 | 3.45 | not instrumented at HTTP boundary |

Every warm call satisfies the unchanged <500ms wall/CPU, <100MiB allocation,
<128MiB positive RSS, <64MiB logical/physical reads, ≤16 opens/16MiB declared
artifact bytes and <1MiB response ceilings. Retained reconstruction stays at
five authenticated opens/949,966 bytes, below this fixture's 4MiB bound.
Maximum response/SVG is 27,982 bytes. Physical read bytes are zero;
logical storage work remains measured.
Host metrics include test/host infrastructure and exclude Chromium/driver.
Process I/O also includes persistence reads outside artifact-service counters.

| Route | Interactive image ms | Off / residual screenshot ms | Renderer process CPU ms | Toggle/calculator task CPU ms |
| --- | ---: | ---: | ---: | ---: |
| Current Sky | 599.85 | 198.69 / 312.84 | 2620 | 247.44 |
| Archive | 428.65 | 321.38 / 210.55 | 2460 | 330.78 |

Whole-route CPU uses monotonic renderer `ProcessTime`; it never crosses a reset
of `TaskDuration`. The unchanged limits are <10s to interactive image, <5s per
screenshot and per CPU sequence, and <64MiB sampled JS heap. Both inverse
coordinate systems still return 280/240 within `1e-7` pixels; independent layer
toggles, authenticated selected export, 401/403, keyboard order and 390px fit pass.
The [current view](evidence/issue-1644/review-corrections-r1/current-residual-calculator.png),
[calculator detail](evidence/issue-1644/review-corrections-r1/calculator-card.png)
and [narrow view](evidence/issue-1644/review-corrections-r1/narrow.png) were inspected.
All page-error counts are zero. The partial fixture still produces 22 image HTTP
415 events for its unavailable thumbnail; the verified processed image, controls
and export pass. The retained metadata-only event traces contain
no authenticated replay snapshots, cookies, raw console bodies or request bodies.
The original 30 packaged hashes remain unchanged; only the invalidated browser
path has a new evidence subdirectory.
