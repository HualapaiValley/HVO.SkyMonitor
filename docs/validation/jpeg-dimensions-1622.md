# Optional JPEG dimensions (#1622)

The `JpegEncoding` panel binds the existing `packed-jpeg-v1` implementation,
semantic recipe 1.0.0. Its optional maximum dimension accepts null or an integer
from 1 to 16384 pixels. The encoder and resize/transfer algorithms are unchanged.

The shared nullable whole-number value plus decoded `IsExplicit` distinguishes
absent, explicit null and a present number. No edits, or an edit restored to its
original effective value, preserves source bytes. Clearing an existing numeric
limit replaces only that member with null. Clearing an already absent limit
leaves it absent. Unknown members, casing, numeric spelling, quality, output
variant and nested transfer options keep their original bytes.

## Regression evidence

- `ProcessingNodeOptionsTests.JpegDimensions`: presence/null/value and byte-exact
  no-op edits; null and both numeric bounds; wrong types, nonfinite/exponent and
  fractional tokens, overflow, out-of-range numbers, case-insensitive duplicates,
  set/clear edits and descriptor-default validation.
- `CaptureScalarOptionsEditorTests.JpegDimensions`: accessible labels/status,
  numeric keyboard hint, explicit clear button, no-op saves, edited drafts and
  rejection of invalid input after a previously valid preview.
- `CameraAgentProcessingGraphUiServiceTests`: raw invalid JPEG options fail
  preview and save with identical shared reason codes before compilation/storage,
  including disabled nodes; denied operators cannot preview or save.
- `PreviewAndJpegDraftRequiresValidationActivationAndRollback`: both set and clear
  through the real CameraAgent revision service, unchanged plan identity, runtime
  descriptor defaults, invalid dimensions rejected, activation before validation
  denied, continued original capture until activation, output dimensions and
  SHA-256 checks, unchanged retained options and rollback reproducing original
  JPEG bytes.
- `NullableDimensionEditsChangeOnlyJpegSizeAndReconstructRetainedOutput`: the same
  deterministic 8x8 Mono16/packed-preview input exercises both host recipe adapters
  with absent/null/present source settings, a 2-pixel limit, clear and a larger
  limit that never enlarges the image. Edited outputs preserve lineage, change
  recipe/output identities when dimensions change, and old options reconstruct
  the original JPEG bytes and output identity.

## Browser evidence

The [mobile screenshot](jpeg-dimensions-1622-mobile.png) uses actual bUnit-rendered
markup with the application's Bootstrap, dark theme and compiled scoped styles.
Chrome inspection at 390x844 reported document width 390 (no horizontal overflow),
a labelled numeric-input field and a 44-pixel-high original-dimensions button.
Tab from the dimension input focuses that button. These are frozen fixture
layout/keyboard checks; bUnit exercises the actual set/clear and save callbacks.
T3 preview snapshots timed out twice, so Chrome supplied the browser evidence.

## Validation profile and limits

Tier B; target profile `development-v1`. The range selector is
`scripts/ci:classify pull_request 097136be49de0d39caef111bbca085f52ef321c1 HEAD`:
`mode=full complete=false shared=true cameraagent=true logichost=true combined=true
delivery=true deployment=false secret_scan_contract=false release_scripts=false`.
Affected Release Unit projects and focused contract/UI/service checks provide
local evidence; Preflight and Build and Unit are the protected merge gates.
There is no requested full-qualification workflow, hardware exercise, deployment
or comparative performance claim. No new worker signals, persistence protocol,
image-buffer ownership or automatic rendering identity are introduced.

The local broad Unit run on macOS also exposes unrelated baseline limitations.
An initial default temporary directory traversed a macOS symlink; using
`TMPDIR=/private/tmp` makes the unchanged base's 40 nominal-evaluation tests pass.
The three byte-pinned orbital prediction cases still fail on untouched base
`097136be`, with the same byte mismatch positions as the candidate. They are
outside the edited option path. Final counts and gate outcomes are recorded in
the PR ledger; the Linux protected profile remains mandatory.
