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

Object properties are sorted by `CaptureContractJson`; array and source order are
significant. The output identity additionally covers the target role, target
variant, recipe identity, and ordered immediate source artifact IDs. The target
variant does not change recipe identity, but does change output identity.

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
| `encoded-preview` | Transform | Raw or calibrated | JPEG by default; packed compatibility projection | O(pixels), Mono16 input/histogram/display/output or CFA input/mosaic/RGB/output; JPEG may add one RGBA/native codec copy |
| `annotation` | Transform | Raw, calibrated, combined, or exact preview result | JPEG by default; packed compatibility projection | O(pixels + rendered geometry), input/display/mask/output; hosts supply projected geometry |
| `rolling-mean` | Window | Explicit ordered compatible linear sources | Packed combined linear frame | O(pixels x sources), sources plus UInt64 accumulator and one output; no source is retained |
| `image-quality` | Analyzer | Any supported unpacked image | Canonical JSON integer statistics | O(samples), no full-frame output |
| `no-op-analyzer` | Analyzer | Any explicit input | Canonical JSON acknowledgement | O(1), no full-frame allocation |

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
The arithmetic is a linear integer mean using UInt64 accumulation. Registration,
sigma clipping, dark subtraction, and motion compensation are not implicit.

## Host Adapters

CameraAgent maps canonical products back into its legacy role-keyed
`FrameArtifactSet` only as a compatibility view and retains full products and
outcomes on `CaptureProcessingContext`. LogicHost validates a reconstruction
descriptor and checksum before invoking the same executor, including ordered
multi-source windows. Both adapters derive compatibility from equivalent
capture-time profile versions, processing-profile hashes, effective exposure,
gain/offset, and temperature setpoint rather than measured temperature. Neither
adapter owns a durable graph or worker lifecycle in this phase.
