# Processing Recipes v1

`HVO.SkyMonitor.Processing` is the host-neutral execution boundary for canonical
image recipes. CameraAgent and LogicHost supply immutable inputs and explicit
context, invoke `IProcessingRecipeExecutor`, and retain ownership of scheduling,
persistence, transport, retries, and durable state.

## Identity

A recipe identity covers this canonical JSON envelope:

- schema `hvo-processing-recipe-v1`;
- recipe name and semantic version;
- implementation version;
- effective options with defaults materialized;
- explicit input kind and role, plus variant and producing recipe identity for
  exact recipe-result selectors;
- annotation provenance identity when annotation geometry is required.
- canonical named auxiliary selectors and JSON-context identities when supplied.

Object properties are sorted by `CaptureContractJson`; array and source order are
significant. The output identity additionally covers the target role, target
variant, recipe identity, and ordered immediate source artifact IDs. The target
variant does not change recipe identity, but does change output identity.
`CreateRequestedIdentity` binds normalized configuration and the input selector
without runtime annotation geometry. Annotation execution adds its provenance
identity, so a catalog request identity is intentionally distinct from the
identity of each produced annotated artifact.

## Inputs And Outcomes

`ProcessingInputSelector` distinguishes raw, calibrated, combined, and exact
recipe-result inputs. Missing combined input never falls back to raw. A
recipe-result selector requires role, variant, and full recipe identity, allowing
multiple products with the same role to coexist without first-match behavior.

Outcomes are `Produced`, `Skipped`, `RetryableFailure`, or `TerminalFailure` and
carry stable reason codes and fields. Cancellation throws
`OperationCanceledException`; it does not produce or commit a partial product.
Exception messages are not protocol reason codes.
Malformed artifact metadata or annotation geometry is rejected before identity binding.
Named auxiliary inputs are unique case-insensitively. Canonical JSON context rejects
duplicate properties and non-canonical byte representations; auxiliary order does not
change identity because names are sorted before binding. Requests without auxiliary
inputs bind an explicit empty auxiliary-input array in the canonical v2 envelope;
`null` and an empty collection therefore produce the same identity.
Unexpected recipe exceptions are terminal; retryable outcomes must be returned explicitly by
the recipe.

Input payload memory is borrowed, immutable for the duration of execution, and
never retained by a stateless recipe. Product memory is owned by the returned
product. Public Processing APIs contain no ASP.NET, EF Core, SQL, MinIO,
filesystem, logging, stream, or disposable Skia types.

## Built-In Recipes

| Recipe | Kind | Inputs | Output | Complexity and maximum live full-frame buffers |
| --- | --- | --- | --- | --- |
| `linear-normalization` | Transform | Raw | Packed calibrated linear frame | O(pixels), input plus one output |
| `reference-calibration` | Transform | Raw plus named bias, dark, flat, defect, and profile inputs | Packed calibrated linear frame | O(pixels), five borrowed frames plus one output |
| `encoded-preview` | Transform | Raw or calibrated | JPEG by default; packed compatibility projection | O(pixels), Mono16 input/histogram/display/output or CFA input/mosaic/RGB/output; JPEG may add one RGBA/native codec copy |
| `annotation` | Transform | Raw, calibrated, combined, or exact preview result | JPEG by default; packed compatibility projection | O(pixels + rendered geometry), input/display/mask/output; hosts supply projected geometry |
| `mixed-gain-normalized-mean` | Window, opt-in | Frozen ordered raw inputs, per-gain family masters and normalization plan | Packed combined frame, bounded diagnostic JSON and two UInt8 support rasters | O(pixels x sources), bounded row bands/distinct master tuples plus combined/count/mask outputs (4 bytes per photosite total) |
| `rolling-mean` | Window | Explicit ordered compatible linear sources | Packed combined linear frame | O(pixels x sources), sources plus UInt64 accumulator and one output; no source is retained |
| `image-quality` | Analyzer | Any supported unpacked image | Canonical JSON integer statistics | O(samples), no full-frame output |
| `no-op-analyzer` | Analyzer | Any explicit input | Canonical JSON acknowledgement | O(1), no full-frame allocation |
| `cloud-assessment` | Analyzer | Linear current image, named compatible clear reference, and optional environmental snapshot | `Metadata/cloud-assessment-v1` canonical JSON | O(pixels + tiles), two borrowed inputs plus O(tile count) accumulators |
| `weather-cloud-overlay` | Transform | Preview, named assessment, and named environmental snapshot | Packed or JPEG annotated preview | O(pixels + tiles), borrowed inputs plus one display/output copy |

JPEG bytes are deterministic for the repository-pinned Skia/runtime environment.
Cross-platform conformance compares decoded pixels and provenance unless the
codec environment is identical. Packed products are used by the current
CameraAgent compatibility projection so existing local UI/storage contracts do
not become lossy; durable variant-aware processing replaces that projection in
phase 5.

## Rolling Compatibility

Rolling mean uses the newest explicit sources selected by frame count and
optional integration-time and age limits. It emits during warm-up. All selected
sources must match dimensions, stride, pixel format, byte order, sample/container
depth, packing, CFA, black/white levels, and capture-time rig/orientation,
calibration, mask, sensor, setpoint-regime, and processing-profile identities.
Capture manifest v2 carries orientation inside the aggregate rig profile identity,
so the `Rig` and `Orientation` axes both contain that profile hash until a separately
versioned orientation profile is introduced. An orientation change still changes both
axes and resets the window.
The arithmetic is a linear integer mean using UInt64 accumulation. Registration,
sigma clipping, dark subtraction, and motion compensation are not implicit.

## Mixed-Gain Normalized Mean

CameraAgent's `RollingCombination` step opts in with `options.recipe` set to
`mixed-gain-normalized-mean`; the default remains `rolling-mean`. It requires raw
inputs, a durable execution, an explicitly activated calibration family and an
available virtual gain response. A minimal configured step is:

```json
{
  "id": "night-stack",
  "type": "RollingCombination",
  "dependsOn": ["$raw"],
  "options": {
    "recipe": "mixed-gain-normalized-mean",
    "windowSize": 3,
    "windowKind": "Trailing",
    "outputVariant": "mixed-gain"
  }
}
```

The plan freezes every candidate's gain, exposure, descriptor and payload hashes,
selection or exclusion, exact bias/dark/flat/defect masters and hashes, response,
fixed-point coefficients and arithmetic policy. Raw inputs are calibrated at their
own gain in signed electron space, then averaged with equal weights per valid
photosite. Bias and dark are subtracted before normalization; RGGB lanes stay at
their original photosites. There is no raw, nearest-gain or guessed fallback.

The UInt16 output is **reference gain 0, ×16**: one output code is 1/16 of a
reference-gain stored code. It is neither a native camera code nor an electron
count. Signed signals survive until the final mean, which rounds ties to even
and clips to [0, 65535]. A stack admits only coefficients within validated response
coverage with `max(k)/min(k) <= 4`; exposure, offset, temperature setpoint, layout,
rig/orientation, sensor, mask, location, processing profile and calibration family
remain compatibility boundaries. A trailing window stops at a bound; a centered
window must retain every required position after exclusions.

Stored-light saturation is tested before correction. Saturated, defective and
invalid-flat samples contribute neither signal nor weight. A photosite without
any valid sample is encoded as zero, **meaning no measurement**, and is counted
separately from output clipping. Zero output alone does not establish zero flux.
The companion `mixed-gain-diagnostics-v1` JSON metadata product records per-input
saturated and other invalid sample counts, product clipped-low/clipped-high and
no-coverage counts, and the plan identity plus combined output identity/checksum.
Counts aggregate all row bands and refer to original photosites. The metadata
product and the support maps cannot enter a rolling pixel window: they have
`Metadata` role, whereas rolling image inputs require Raw, Calibrated or Combined.
Two retained raster companions preserve exact per-photosite support:

- `valid-counts-u8-row-major-v1`: one UInt8 contributor count (0–128) per original
  photosite, in row-major order with stride equal to width.
- `no-coverage-u8-row-major-v1`: one UInt8 flag per original photosite, 1 when
  contributor count is zero, otherwise 0. This is a byte raster, not a bitset.

Both use a Mono8 layout carrying the original width/height, no CFA, and exactly
`width * height` bytes. Their media type is `application/vnd.hvo.mixed-gain-support`;
the encoding version is an explicit `support-encoding` algorithm. Each map is at
most 1,073,741,823 bytes (the combined UInt16 output must also fit a checked Int32
byte array); actual supported capture geometry and storage constraints still
apply. They use the existing reconstructable raster publication path, so the
4 MiB structured-metadata upload limit is unchanged. The companion JSON uses
CameraAgent's local metadata manifest; this feature does not add central structured
metadata ingestion or automatic upload policies. Extra result memory is two
bytes per photosite, plus bounded band buffers and the combined UInt16 result.
The small JSON companion binds both map output identities, checksums, byte lengths
and geometry to the plan and combined image. All four products retain exact
ordered raw/master lineage. Variants include a hash of the configured output
variant so multiple stack nodes have distinct companion variants. Execution
detail reads at most 32 KiB of diagnostic JSON and never loads support rasters.
A retention hold on the Combined payload or its sidecar also retains its exact
diagnostic/count/mask companions. Other windows and unrelated metadata do not
inherit the hold; companions expire normally after it is released.
A zero image code with count 1/mask 0 is a measured zero; count 0/mask 1 means no
measurement. Both maps are reproduced byte for byte in archived replay.
Execution detail's Stage summary
shows these counts and frozen frame exclusions (including missing masters,
coefficient ratio and identity mismatches); failures before a plan is frozen
remain visible as the recorded stage reason. Missing or corrupt diagnostic
payloads show unavailable counts, never fabricated zeros. Older executions can
have a plan without a diagnostic companion.

Archived replay uses the original plan and retained payloads, never today's
family, masters, configuration or gain controller. Combined bytes are reproduced
exactly; new diagnostics are additional evidence. See
[Reference Calibration v1](reference-calibration-v1.md#mixed-gain-calibration-families)
and [the phase-2 acceptance audit](../validation/mixed-gain-1475.md).

## Reference Calibration

`reference-calibration-profile-v1` binds exact immutable reference artifact IDs,
payload checksums, exposure/gain/temperature applicability, effective interval,
layout, flat normalization, source, and profile version. The recipe accepts only
little-endian Mono16 or RGGB16 and requires the named `bias-reference`,
`dark-reference`, `flat-reference`, `defect-reference`, and
`calibration-profile` auxiliaries. Ordered output lineage is raw, bias, dark,
flat, then defect. See [Reference Calibration v1](reference-calibration-v1.md).

## Cloud Assessment

Cloud assessment compares Mono16 or RGGB16 current/reference samples at identical
photosites. A fixed-point per-tile regression slope removes an additive background;
RGGB lanes are accumulated separately and never demosaiced. Coverage is weighted by
accepted valid-sky samples rather than unweighted tile count. The optional mask has one
row-major, least-significant-bit-first bit per tile.

Missing or incompatible clear reference/calibration, insufficient support, excessive
saturation, precipitation, or daylight never produces a percentage. Environmental
missing/stale state remains explicit. The algorithm receives no simulator scenario
provenance, expected coverage, fixture labels, or truth masks. See
`cloud-assessment-v1.md` for the compound product contract.

## Host Adapters

CameraAgent maps canonical products back into its legacy role-keyed
`FrameArtifactSet` only as a compatibility view and retains full products and
outcomes on `CaptureProcessingContext`. LogicHost validates a reconstruction
descriptor and checksum before invoking the same executor, including ordered
multi-source windows. Both adapters derive compatibility from equivalent
capture-time profile versions, processing-profile hashes, effective exposure,
gain/offset, and temperature setpoint rather than measured temperature. Neither
adapter owns a durable graph or worker lifecycle in this phase.

CameraAgent persists layoutless metadata with a separate versioned local product
manifest under `derived/`; it does not fabricate a `CameraFrame`. LogicHost processing
inputs retain durable binding names so configured reference artifacts reach the same
auxiliary-input identity envelope. Central automatic cloud scheduling requires a
configured durable reference designation and frozen environmental context.
