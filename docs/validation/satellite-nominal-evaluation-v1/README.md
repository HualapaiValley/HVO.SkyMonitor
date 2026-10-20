# Nominal offline satellite detection experiments (#530 preparation)

Source: `583cc485d70007e2ca97672b0165bcb30af03cdb`; tool assembly SHA256
`132C146D7EC6A05819454898E7B2F8D3499F9A36599BD28C57655EE69FC24CBE`.
The portable evidence commit is a documentation-only extension of that source.

This is offline research tooling using configured 640 × 640 virtual cameras.
Production association, accepted measured calibration, held-out accuracy,
physical sensitivity and the complete five-second publication deadline remain
open under #530/#1395 and their actual dependencies. Existing published scenes
are exploratory/tuning groups; changing a seed or dropping a frame cannot turn
these into independent held-out samples.

## Actual raw replay

The unchanged VirtualSkyCameraModule/sensor captures from #529 were reused byte
for byte. No provider fetch, orbital regeneration, detector threshold tuning or
new image injection occurred. Seven unique five-frame inputs represent eight
published scene/control references: the ISS and synthetic-fade control are the
same source sequence and are counted once. Each input was evaluated in its own
process twice; the output bytes matched exactly. Separate processes compared
all returned components with the supplied canonical tracks. Five exposure-specific
prediction packages per sequence preserve the existing eight-product command
limit; paired Starlink packages each contain both alternatives.

Every target retained its original source sequence number. The first two
outcomes are actual `Missing` results with null extraction, followed by three
actual `Produced` results. Empty detections on a produced background are distinct
from unavailable context. All file-read allowlists, raw hashes, canonical hashes,
component counts and alternative counts were checked. There were 35 unique
targets and 49 CLI invocations including deterministic repeats and comparisons.

| Exploratory sequence | Components in frames 3, 4, 5 | Public input key |
| --- | --- | --- |
| ISS control | 0, 0, 0 | `fa1a95d6e3738bfb984f` |
| ISS injected | 1, 1, 1 | `f401c68e4904c45e1901` |
| Two Starlinks control | 0, 0, 0 | `ee22be3c5e42449b3e0d` |
| Two Starlinks injected | 2, 2, 2 | `cb0d235eb6e5d8b2c2dc` |
| Historical Vanguard 1 control | 0, 0, 0 | `8cab2244a73a3736dccd` |
| Historical Vanguard 1 injected | 1, 1, 1 | `bab84089a2e6edd9071e` |
| Synthetic fade injected | 1, 0, 0 | `686ef62ab251556d6776` |

Counts are observations of these explicit optical stimuli, not recall or false
match estimates. Brightness/fade profiles remain private simulation inputs, not
orbital-element properties. The comparator calculates research residuals; it has
no match threshold, winner, satellite classifier or automatic rejection. A single
integrated streak has unordered endpoints and unavailable direction/angular speed.
The complete candidate/prediction sets and all geometry/coverage qualifications
remain in the reports.

## Independent capture geometry and location

The [public geometry files](geometry/seattle.json) were reconstructed from the
retained public rig settings and captured readout descriptor using the existing
RigProjectionContextFactory, AstrometricCalibration and canonical hashing APIs.
The exporter did not read predicted tracks, object truth, module options, seed
or brightness. Rig and observer fields were also checked against the original
render inputs before producing the neutral public facts.

Seattle, HVO and Sydney share the same camera geometry hash in these fixtures;
location is a separate fact. The new raw envelope therefore carries an explicit
nullable observer. Unknown or differing latitude, east-positive longitude or
elevation withholds comparison residuals while retaining the component and
hypothesis, even when time and camera hashes match. Invalid coordinates fail
before opening raw files. Heights and geometry remain nominal configured facts,
with no accepted measured uncertainty or calibrated confidence.

## Separate fault and context checks

Ten additional CLI invocations verified seven bounded cases using retained raw
bytes:

- Dropping source frame 2 retains IDs 1, 3, 4, 5. Outcomes are Missing, Missing,
  Incompatible (sequence gap), Produced; history recovers with two consecutive
  predecessors.
- Restarting at source frame 3 retains IDs 3, 4, 5 and genuine Missing, Missing,
  Produced startup.
- Unknown observer, a deliberately mismatched observer and unknown geometry
  retain candidates/hypotheses but withhold numerical residuals with reasons.
- An empty prediction package preserves the measured streak and explicit absent
  coverage. It does not infer a meteor or an empty sky.
- A one-segment comparison budget retains both measured components and both
  Starlink alternatives, reports partial work and preserves withheld residuals.

Public-fact fault variants are deliberately incomplete/mismatched comparison
inputs, not additional independent captures or accuracy samples. No status field
or golden expectation was edited to manufacture partial orbital products; the
focused tests obtain their partial prefix from the actual bounded engine.

## Reproduction and provenance

The [tool guide](../../../tools/HVO.SkyMonitor.VirtualSkyPoc/NOMINAL-EVALUATION.md)
defines the strict raw/prediction schemas, runnable explicit-raw export seam,
commands, fixed options, bounds, publication semantics and limitations.

1. Reproduce or obtain the exact retained raw sequences using #529's
   [camera reproduction instructions](../orbital-prediction-v1/virtual-camera.md),
   its public settings, optical profiles, canonical tracks and verified HYG catalog.
   Keep generation truth outside the public input roots. If regenerated bytes
   differ, retain the failure and investigate; do not repin expected hashes.
2. Select an [input manifest](inputs/fa1a95d6e3738bfb984f.json), copy it to a new
   neutral public root as `input.json`, and copy each explicit raw payload as
   `raw/<SHA256>.raw`. The committed hashes bind every target. Original readout
   remains in the separately pinned public geometry; the supported detector
   layout describes the raw raster after readout and omits layout.readout.
3. Run `nominal-evaluate PUBLIC_ROOT NEW_REPORT.json`. Its only input reads are
   input.json and the declared raw files; no camera/simulator metadata, diagnostic
   image, orbital file or private expected identity enters detection.
4. Prepare `predictions.json` with exact canonical bytes from
   `tests/fixtures/orbital-prediction-v1/virtual-camera/tracks/`, including all
   supplied alternatives for the exposure. Run the separate `nominal-compare`
   process. Per-exposure packages are bounded; a package's selected population
   never proves global absence or unique identity.

[Replay summary](replay-summary.json) contains all sequence hashes, statuses,
component counts and residual sets. [Reports](reports/iss-comparison.json) retain
exact machine-readable examples for ISS, paired Starlinks, control, fade and the
historical epoch. [Checksums](checksums.json) bind all 34 portable data/image/report
files; README and the checksum index are excluded. Raw payloads are not duplicated
in this repository; their full hashes and reproducible original generation are
retained. Durable host workers, capture publication and live acquisition were not
exercised by these commands.

Images are original fixed-display raw derivatives with separate diagnostic
geometry. Stellar rendering inherits the [HYG attribution](samples/ATTRIBUTION-HYG.md)
and [CC BY-SA 4.0 notice](samples/LICENSE-HYG.md). Existing source/data licenses
and numerical reference pins from #529 remain unchanged.

## Validation boundary

The implementation child's exact commit `6996694e` passed 48 focused cases
(24 new and 24 unchanged). Root added eight observer cases; the complete
Processing Unit selection passed 342/342 with warnings as errors and an invalid
Docker endpoint. Actual discovery found 32 new cases and updated the project
inventory to 342 and total Unit inventory to 9124. Scoped format and all five
lightweight controls passed. The classifier selects every component lane because
tools paths are outside its component boundaries. Root conservatively runs one
full Release build and complete Docker-disabled Unit selection for those lanes;
final results are recorded in the PR/issue ledger. No Debug/Integration/coverage
campaign or full-qualification workflow is claimed for this Tier B tool-only slice.
Required development/v1 Preflight and Build-and-Unit remain separate final gates.


The full local Release build passed. The original full Unit command selected
9124 cases: 9004 passed, one existing terminal-colour test failed, and 119 cases
retained their existing qualification skips. On the same source and binary, the
single failure reproduced with inherited TERM=dumb, empty COLORTERM and
NO_COLOR=1, then passed with TERM=xterm-256color, COLORTERM=truecolor and NO_COLOR
unset. The complete 1751-case affected CLI selection then passed (1635 passed,
116 existing skips), with an exactly identical test-ID set. Combining that
explicitly environment-corrected replay with the other 20 original assemblies
provides 9005 passes and 119 skips. The original full command remains exit 1;
it is not relabeled as a new green native run. See
[candidate Unit evidence](candidate-unit-evidence.json) for original/replay hashes,
environments and source boundaries. No test/assertion or production colour code
was changed. Required protected v1 CI remains a separate current-head gate.

The ambient terminal-test isolation follow-up is [#1434](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1434), unassigned; its implementation is separate from this offline satellite slice.
