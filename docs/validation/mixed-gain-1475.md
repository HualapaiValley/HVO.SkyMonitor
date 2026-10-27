# Mixed-gain phase-2 acceptance (#1475, #1515)

Scope: `development/v1`, virtual evidence only. The
[approved design](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1475#issuecomment-6316607166)
and [owner decisions](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1475#issuecomment-6321750720)
remain the acceptance source. The following audit includes merged slices A–G and
final H+I evidence; it does not qualify physical cameras or a production release.

- [x] **Response/policy (A):** versioned simulation truth, deterministic E12
  coefficients, validated coverage, exact 4:1 boundary, unsupported/physical
  sources unavailable. [GainNormalizationContractTests](../../tests/HVO.SkyMonitor.Processing.Tests/GainNormalizationContractTests.cs),
  especially `VirtualAsi174MmUsesTheModelThatRenderedTheFrame`,
  `CoefficientBoundComparesCoefficientsAndAcceptsGainZero` and
  `ResolveReportsAStableUnavailableReasonWithoutValidResponseData`.
  PR1495-R1 F2: exact coefficient fixture uses gain 110, whose scaled coefficient
  is 2226522515698.918517… (rounds to 2226522515699); gain 121 remains a rejecting
  ratio-bound case. This avoids its near-half rounding tie.
- [x] **Gain-specific calibration (B/C):** immutable family, explicit activation,
  exact applicability, each input's own masters, no guessed substitution, held
  source/master evidence and durable activation. [Family selection tests](../../tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/Calibration/SqliteCalibrationLibraryStoreTests.FamilySelection.cs),
  [family evidence tests](../../tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/Calibration/SqliteCalibrationLibraryStoreTests.FamilyEvidence.cs),
  [family store tests](../../tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/Calibration/SqliteCalibrationFamilyStoreTests.cs).
- [x] **Signed arithmetic and support (D/H+I):** bias once, exposure-scaled dark,
  unity flat, signed electron-space reduction, equal valid-sample weights,
  pre-correction saturation, defects/invalid flats, final ties-to-even rounding,
  ×16 output, low/high clipping and no-coverage semantics.
  [Linear16GainNormalizedMeanTests](../../tests/HVO.SkyMonitor.Imaging.Tests/Linear16GainNormalizedMeanTests.cs)
  includes hand arithmetic and `Compute_MarksAPhotositeWithoutValidSamplesAsNoCoverageRatherThanZeroFlux`.
  [MixedGainNormalizedMeanRecipeTests](../../tests/HVO.SkyMonitor.Processing.Tests/MixedGainNormalizedMeanRecipeTests.cs)
  verifies exact retained UInt8 valid counts/no-coverage maps, distinguishes a
  measured zero from no measurement, checks maps and aggregate counts across row
  bands against the full kernel, and checks byte/geometry bounds above 4 MiB.
- [x] **Atomic frozen plans (E):** canonical plans/hash validation, actual gain,
  descriptor/payload hashes, coefficients, master exposures/IDs/hashes, exclusions,
  arithmetic versions, lease checks, retry/cancellation and pin lifetimes.
  [NormalizationPlanContractTests](../../tests/HVO.SkyMonitor.Processing.Tests/NormalizationPlanContractTests.cs)
  and [execution plan tests](../../tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/Processing/ProcessingGraphOperationsTests.NormalizationPlans.cs).
- [x] **Opt-in compatibility (F/F2):** only the named raw-backed recipe crosses a
  gain boundary; old raw/rolling behavior retains gain. Other compatibility axes,
  age/integration bounds, primary/minimum/required positions remain enforced.
  [MixedGainWindowGateTests](../../tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/Processing/MixedGainWindowGateTests.cs),
  [recipe tests](../../tests/HVO.SkyMonitor.Processing.Tests/MixedGainNormalizedMeanRecipeTests.cs)
  and [end-to-end tests](../../tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/Processing/MixedGainRollingWindowEndToEndTests.cs).
  Real normalized products feed downstream rolling means; Metadata diagnostic
  and support products do not enter those windows.
  `HeldCombinedKeepsOnlyItsExactCompanionsUntilTheHoldIsReleased` proves payload-
  and sidecar-only holds preserve the diagnostic/count/mask companions byte for
  byte, leave other windows/metadata eligible, and release every companion after
  the hold ends.
- [x] **Archived replay (G/H+I):** trailing and centered original plans reproduce
  combined **and support-map bytes** after changed family/master/configuration and
  service restart. Missing/corrupt frozen evidence fails explicitly; ordinary
  identical-gain replay remains supported.
  [ArchivedReplayReproducesOriginalBytesAfterFamilyMastersAndConfigurationChange](../../tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/Processing/MixedGainRollingWindowEndToEndTests.ArchivedReplay.cs).
- [x] **Real automatic gain evidence (H):**
  `AutomaticNightGainStepsKeepTheFullTrailingStackDepth` in the
  [end-to-end fixture](../../tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/Processing/MixedGainRollingWindowEndToEndTests.cs)
  renders six 64×48 VirtualSky night lights at fixed 32 ms, fixed UTC
  `2026-02-01T03:00:00Z`, model seed 1524. Rendered mean/declared-white measurements
  feed the real `ExposureController`; its gains are 82, 88, 94, 100, 100, 100.
  Four gain-specific reference acquisitions form the active family. The real
  ingress/standard-lane worker retains depths 1, 2, 3, 3, 3, 3 and frozen
  `virtual-simulation-truth` response evidence. No manual gain list drives
  this test's captures.
- [x] **Operator evidence/docs (I):** execution details project hash-verified
  frozen exclusions and bounded diagnostic JSON; saturation and other invalid
  sample counts remain distinct from frame exclusions and output clipping.
  Missing/corrupt diagnostics report unavailable counts. [UI tests](../../tests/HVO.SkyMonitor.CameraAgent.Tests/Components/ProcessingExecutionPagesTests.cs),
  [sanitized projection tests](../../tests/HVO.SkyMonitor.CameraAgent.Tests/Services/CameraAgentProcessingGraphUiServiceTests.cs)
  and `MissingOrCorruptDiagnosticEvidenceNeverAppearsAsZeroCounts` in the end-to-end
  fixture cover these paths. [Processing](../contracts/processing-recipes-v1.md#mixed-gain-normalized-mean)
  and [calibration](../contracts/reference-calibration-v1.md#mixed-gain-calibration-families)
  contracts describe enablement, bounds, support encodings and trust limits.
  The single operator feature entry in [CHANGELOG](../../CHANGELOG.md) replaces
  the slice-A groundwork entry (PR1495-R1 F1).

Support resource contract: one UInt8 count and one UInt8 no-coverage flag per
original photosite add exactly `2 × width × height` payload bytes and result
memory per stack, plus small JSON/sidecars. Temporary kernel/master planes remain
banded. A 4096² frame uses 16 MiB per map through existing raster persistence;
structured metadata limits are unchanged. Execution details read at most 32 KiB
of JSON and descriptor facts, never the map payloads. These are bounded resource
facts, not a full-resolution cadence or physical SNR qualification.

Local command logs, TRX/coverage, timings and the exact candidate fingerprint are
recorded in the issue's implementation evidence ledger. The parent owns independent
deep review and final Development v1 checks; those are separate from this local audit.
