# Cloud assessment configuration evidence (#1565)

Date: 2026-10-23. Tier B, `profile=development-v1`, SDK 10.0.402.
Base: `dffab19529c55769e25fbd162fe06b248386eed0` (merged #1564).
Bounded partial delivery, Refs #1386; no #1004 physical qualification.

## Contract and retained inputs

`CloudAssessmentOptionDescriptors` pins `CloudAssessment` at
`comparative-tiled-transmission-v1`. The existing generic panel binds only
enablement, output variant, grid columns/rows, transmission threshold, reference
signal, samples per tile, saturated fraction and mask inclusion. The factory test
compares every descriptor default against `CloudAssessmentProcessingStepOptions`
and every edited value against the real factory's runtime-effective options.
The recipe remains `cloud-assessment` / `1.0.0` /
`comparative-tiled-transmission-v1`.

`ProcessingNodeOptionsTests.CloudAssessment.cs` covers exact version resolution,
strict JSON types, both ends of every numeric range, duplicates, output-route
limits, absent defaults, byte-exact untouched JSON and unchanged saves. Attempts
to edit the opaque reference member through the codec are rejected. Unknown and
out-of-scope members survive a supported scalar edit.

The both-host `CloudAssessmentConfigurationTests` use deterministic retained
64×64 Mono16 current and clear-reference descriptors/payloads. The current frame
has transmission 0.9 on the left and 0.5 on the right, with symmetric saturated
samples excluded from regression. Baseline coverage is 500000 millionths; a
950000 threshold produces 1000000. Columns, rows, signal, sample support,
saturation and mask edits are exercised separately. Excessive signal/support
produces null coverage; a zero saturation tolerance produces contamination.
The full 4096-tile allocation remains within the existing 1 MiB payload bound;
one-sample tiles cannot invent a regression estimate. Oversized grids and grids
that do not fit the input retain their existing failure reasons on both hosts.

Every produced case compares bytes, checksums, recipe/product/content identities,
algorithm identities and ordered source lineage between CameraAgent and
LogicHost. Absent and incompatible reference cases retain insufficient evidence,
null coverage and no mask. Missing precipitation stays missing. Restoring the
original options reproduces the original bytes and product identity; current and
reference checksums remain unchanged. No fixture truth is passed to the algorithm.

## Editor and lifecycle

bUnit covers all nine labelled controls, changes, denied access, invalid edits
after a valid preview, unchanged saves and edits restored to absent defaults.
The reference path is absent from panel text and inputs. Enablement uses the
existing explicit-v2 node field; saving creates no activation command.

The disposable real coordinator tests cover absent and configured-but-unavailable
references. They verify runtime defaults/options, reject unsuitable bit depth and
out-of-range server input, require validation before activation, preserve the old
active revision while a draft exists, activate the edited metadata route and roll
back to the retained revision. Published assessment bytes are read and verified.
The draft codec preserves source formatting; persistence keeps its pre-existing
canonical representation. The frozen persisted source is compared before and
after editing, and opaque reference provisioning remains unchanged.

## Browser and accessibility

The [retained screenshot](cloud-assessment-1565.png) shows the actual bUnit-rendered
`CloudAssessmentPanelEditsScalarsRetainsOpaqueReferenceAndExportsAccessibleFixture`
page with shipped Bootstrap, theme and scoped CSS. This is a component/service
fixture; coordinator and host-adapter tests exercise persistence and execution.

T3 browser inspection at 1280×1000 and Chrome inspection at 390×844 found no
horizontal overflow. The named **Cloud assessment** region contains all nine
controls, each associated with its label and existing help target. No duplicate
IDs or reference path text appeared. Keyboard Tab advances from assessment
enablement to the output variant. Save starts disabled. Chrome reported no
warning/error console entries. T3 screenshot capture timed out twice, so the
screenshot was retained through Chrome; the HTML export and phone audit/image
are also retained in local issue evidence.

## Validation boundary

The candidate gate selector is `scripts/ci:classify pull_request <base> HEAD` on
the committed range. Its output and exact candidate gate results belong with the
issue/PR evidence. Tier B uses affected builds/format, Processing Unit tests,
CameraAgent editor/service/graph tests, both-host conformance, LogicHost graph
tests, architecture tests and all four CI-control guards. Protected CI remains
the development-v1 profile; full qualification is not selected by this document.

Heavy gates use `flock -o /tmp/hvo-heavy-gate.lock timeout ...`; builds/tests disable
node reuse and shared compilation. Unit runs unset `HVO_PRODUCTION_CATALOG_BUNDLE`
and use the invalid Docker endpoint. No production algorithm, storage protocol,
worker or telemetry changes; runtime signals remain the existing graph outcomes,
assessment reasons and publication evidence. No hot path or canonical benchmark
change is involved, and no physical profile is activated or qualified.
