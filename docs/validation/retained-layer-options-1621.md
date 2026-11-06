# Existing cloud/environment layer scalar editors (#1621)

Tier B configuration slice; target/protected profile `development-v1`.
The descriptors pin CloudPresentationLayer `cloud-presentation-v5-original-colours`
and EnvironmentPresentationLayer `metadata-corner-presentation-v6-original-colours`.
Only DrawLabels/LineThickness and Scale/Inset/LineSpacing are described. No
producer, recipe, rendering identity, provider or stored graph is migrated.

## Acceptance evidence

- `ProcessingNodeOptionsTests.RetainedLayers`: exact versions, case-insensitive
  aliases, strict scalar types/bounds/duplicates, shared server validation,
  opaque-member rejection, protected source bytes and unchanged graph plan
  identities on CameraAgent and LogicHost.
- `CaptureScalarOptionsEditorTests.RetainedLayers`: named deep links resolve the
  active graph, accessible inputs, unchanged/restored edits, disabled-node and
  protected JSON preservation, missing/unsupported aliases and invalid edits
  invalidating a preview. The shared denied-access test includes both aliases.
- `ProcessingGraphOperationsTests.RetainedLayerOptions`: actual registered
  producer versions and defaults/bounds, missing dimensions rejected without
  invented defaults, unchanged effective plan identity, draft/validate/explicit
  activate/rollback, immutable source revision and server rejection of invalid
  scalars.
- `PresentationMetadataFactsBuilderTests.LayerOptions`: edited form JSON reaches
  both real capture producer steps. Each of the five scalars changes its
  deterministic layer payload; restoring the old options reproduces its bytes
  and output identity. Environment facts retain identical identities and stale
  humidity/missing pressure status. Cloud labels retain the assessment identity
  and missing precipitation status, with the other layer unaffected by an edit.
  Existing missing-reference cloud and retained-association tests remain in the
  affected CameraAgent suite.

The UI edits retain raw JSON tokens. The existing revision store canonicalizes
JSON on initial persistence; the lifecycle test compares the stored source
revision before/after editing, rather than claiming to change that behavior.

## Browser evidence

The [cloud panel](cloud-layer-options-1621.png) and
[environment panel](environment-layer-options-1621.png) are actual rendered bUnit
markup with shipped Bootstrap, theme and scoped CSS, served from disposable
local state. They are component/service fixtures; real coordinator and producer
tests supply execution evidence. Desktop (1653 px) and phone (390×844) checks
found no horizontal overflow or duplicate IDs; every control has a label and
help association. Tab moves from cloud labels to thickness and from environment
scale to inset. No console warning/error was observed. Mobile images and HTML
are retained with the local issue evidence.

T3 preview navigation/evaluation worked, but snapshot capture failed twice with
`Preview automation snapshot timed out after 15000ms.` Chrome supplied the
screenshots and responsive/keyboard checks. No installed host or hardware was
used.

## Local validation

SDK 10.0.402; heavy commands serialized with
`flock -o /tmp/hvo-heavy-gate.lock timeout …`, node reuse/shared compilation off.
Unit runs unset `HVO_PRODUCTION_CATALOG_BUNDLE`, use the invalid Docker endpoint
and `TMPDIR=/private/tmp` (macOS `/tmp` is a symlink, rejected by existing path
confinement fixtures).

- Warning-clean affected Release builds and focused CameraAgent layer cases:
  23 passed.
- Shared Processing Unit: 952 passed, 3 orbital golden mismatches. All three
  reproduce byte-for-byte at untouched base `d4493297` in an isolated checkout
  (`ApprovedTracksRemainByteExactAcrossIndependentExecutors`, ISS and two
  Starlink fixtures). The initial `/tmp` path-only failures pass with the
  physical temporary directory.
- Bash 5.3/GNU coreutils: all four CI-control guards passed (shell syntax,
  classification, coordination, PR review tools).
- `dotnet tool restore` passed. Focused format, classifier and final affected
  suite evidence are recorded in the PR ledger on the candidate head.

Only the two required development-v1 checks run after review convergence.
This is integration evidence; no main/release qualification or deployment is
requested. #1386 stays open for the remaining editor families and final
acceptance.
