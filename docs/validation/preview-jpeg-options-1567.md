# Preview and JPEG scalar settings (#1567)

This Tier B configuration slice adds shared Processing descriptors for all three
existing preview aliases at `encoded-preview-v1` and `fixed-preview-v1`, and
`JpegEncoding` at `packed-jpeg-v1`. All recipes remain semantic version 1.0.0.
No image algorithm, persistence protocol, product identity rule or mode changes.
The [operator guide](../runbooks/preview-jpeg-options.md) lists defaults, bounds
and the explicit draft/validate/activate/rollback sequence. Refs #1386, which
remains open.

## Contract and lifecycle

Shared codec tests cover exact versions, alias casing, defaults, numeric bounds,
finite numbers, strict JSON types, duplicate scalar members, percentile ordering,
unknown fields, explicit/absent defaults and lossless JSON edits. Fixed-transfer
panels contain only enablement, artifact recipe label and output route. They
preserve nested levels/gamma, unknown nested fields and inactive percentiles.
JPEG quality edits preserve absent, explicit-null and numeric MaximumDimension
without adding resize controls. Preview steps do not offer ineffective JPEG
quality controls because they produce packed output.

Factory tests compare descriptors with actual registered versions and runtime
defaults for every preview alias in both modes. Unchanged drafts preserve source
JSON and effective plan hashes. Real disposable coordinator execution covers
preview and JPEG edits, rejection of activation before validation, continued use
of the old graph until explicit activation, production under the new revision,
and rollback to the retained revision.

The deterministic 8×8 Mono16 host conformance fixture executes through both host
adapters and the isolated central test project. Edited percentile/asinh settings
change the packed-preview checksum from `DA8BED81…1286FE0` to
`635BA047…8C299D6`. Restoring retained options reproduces the old bytes and output
identity. Changing JPEG quality from 90 to 35 changes checksums at both native
8×8 and bounded 4×4 output sizes; reconstruction reproduces each old JPEG exactly.
Both hosts agree on bytes, output identities, lineage and product contracts.
Changing a fixed-transfer artifact label leaves the shared fixed-transfer product
unchanged. Raw input bytes remain unchanged throughout. Full hashes are emitted
in the conformance test's TRX output.

## Browser and accessibility

The [retained screenshot](preview-options-1567.png) shows actual bUnit-rendered
component markup with the shipped Bootstrap, dark theme and scoped CSS. The
fixture does not run a live host; coordinator and recipe tests supply execution
evidence. The original HTML and phone screenshot remain in registered local
issue evidence.

T3 browser inspection at 1280×800 and 390×844 found no horizontal overflow or
duplicate IDs. All six percentile-mode controls have associated labels and help.
Keyboard Tab moves from Enable preview to Artifact recipe label. Save starts
disabled. At phone width, the fixed panel exposes only three effective fields;
the JPEG panel exposes output variant and quality (default 80). bUnit additionally
covers denial, invalid input after a valid preview, unchanged saves and restoration
of absent defaults, and proves saves issue no activation command.

## Validation scope

Selector: `scripts/ci:classify pull_request
5c2f0f9860660578b00f225aadb45e0244b13432 HEAD` reports `mode=full`,
`complete=false`, `shared/cameraagent/logichost/combined/delivery=true`;
deployment and its sublanes, secret-scan-contract and release-scripts are false.
Target profile is development-v1 Preflight + Build and Unit. Local gates use
Tier B affected project/boundary checks; no full qualification workflow is
requested. The PR ledger records the final immutable head and results.

Heavy builds/tests use the shared flock with bounded timeouts, node reuse and
shared compilation disabled, no production catalog bundle and an invalid Docker
endpoint. Disposable coordinator state is deleted by the fixtures. Existing
execution outcomes and reason codes remain the runtime signals; telemetry,
worker behavior and measured hot paths are unchanged, so no canonical performance
run is required. This evidence does not qualify physical hardware or promotion.

Candidate validation at source head `5354d22f` passed the Release solution build
with zero warnings/errors. Unit executions: Processing 905; affected CameraAgent
379; combined host conformance 35; LogicHost graph/conformance 183; architecture
32; Deployment CLI 2,656; distribution 115. All 4,305 executed cases passed.
The delivery selections additionally reported 122 catalog-dependent inconclusive
cases because the required production catalog bundle is deliberately unset for
Unit validation. They are not counted as executed evidence; no installer or
catalog qualification is claimed. All four CI-control guards and the operations
documentation audit passed. Final formatting/discovery results are in the PR
ledger. Documentation-only result updates preserve this source evidence.

Changed-source formatting passed. Positive Unit discovery verified Processing
905, CameraAgent 7,189, combined-host 82 and LogicHost 959 cases. This also
reconciles inherited inventory drift in the first three projects (+17/+23/+5
beyond this slice's new cases), without removing tests or weakening selection.
The runbook total matches the per-project inventories. Unchanged binaries and
saved discovery diagnostics provide the verification after these count-only
corrections; no long suite is repeated.

The independent review's F1 service-boundary regression was reproduced by six
new cases before correction: all three fixed-transfer aliases with quoted
inactive AsinhStrength or duplicate inactive percentile members. The form and
runtime binder accepted these retained values, while preview/save selected the
percentile descriptor and rejected them. The service now resolves with the
actual step options, matching the form's fixed-transfer descriptor. The
regressions preserve every untouched byte and exercise authorized preview and
draft creation; no transfer mode changes.
