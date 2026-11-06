# Existing fixed-transfer preview editor (#1625)

This Tier B delivery targets `development/v1` and extends the existing #1567
panels for Preview, CalibratedPreview and CombinedPreview. It binds only
BlackLevel, WhiteLevel and Gamma within an already present FixedTransfer object.
The shared recipe remains `fixed-preview-v1`, semantic version 1.0.0. The
[operator procedure](../runbooks/preview-jpeg-options.md) remains draft, preview,
save, validate, activate and rollback.

## Regression evidence

- `ProcessingNodeOptionsTests.FixedTransfer.cs`: all aliases; native bounds,
  finite JSON numbers, gamma limits, effective defaults and strict black < white;
  absent/null/non-object parents and duplicate bindings rejected; nested unknown
  members, numeric spelling, source formatting and default absence preserved.
- `CaptureScalarOptionsEditorTests.FixedTransfer.cs`: all three panels, attached
  labels/help, decimal keyboard hints, edits restored to no-op, invalid edits
  invalidating an already previewed draft, immutable source and explicit activation.
- `ProcessingGraphOperationsTests.Preview.cs`: unchanged graph/plan identities,
  server rejection of invalid nested values, immutable draft, activation denied
  before validation, live output unchanged before activation, changed output
  after activation and byte-identical output after rollback. The fixed fixture
  uses native levels 0–4 for its four retained sample values, avoiding clipping.
- `HostRecipeContractConformanceTests.PreviewOptions.cs`: each scalar edited
  independently for raw/calibrated/combined roles, changed payload/recipe/output
  identities, preserved layout and source lineage, exact original reconstruction,
  unchanged input bytes and equivalent CameraAgent/LogicHost adapter outputs.
  The cases also compile into the isolated LogicHost test assembly.

On 2026-10-24, the T3 browser inspected the rendered bUnit Preview fixture at
1280×900 and 390×844. All three controls had accessible labels and help,
`inputmode="decimal"`, and fitted inside the narrow viewport. Tab moved from
black level to white level to gamma. Desktop/mobile screenshots are retained in
T3 browser artifacts; HTML fixtures attach to test results. This checks static
markup and keyboard traversal, without production CSS or live Blazor handlers;
bUnit and service tests cover edit/save and activation behavior.

## Validation boundary

Local commands and immutable candidate SHAs are recorded in the PR ledger.
`scripts/ci:classify` selects the affected lanes. Protected CI uses
`profile=development-v1` (Preflight and Build and Unit); no manual full
qualification is requested. Runtime image processing and telemetry are unchanged,
so this editor change introduces no full-frame algorithm or hot-path benchmark.
The result is integration evidence, not production or physical-device qualification.
