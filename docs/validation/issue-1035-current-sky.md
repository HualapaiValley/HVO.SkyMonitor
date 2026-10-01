# Current Sky fidelity — issue #1035

This is the active local evidence record for the remaining work under epic #1110.
The issue remains open. Operator page acceptance, full candidate validation,
independent review and release are not complete.

## Starting point and preserved candidate

The current dashboard starts from `development/v1` at `de18e41c`, including the
merged #1046 prototype port and the subsequent shared shell/stage policy changes.
The earlier `origin/feature/1035-current-sky` candidate is preserved at
`5a216fffc131372f6893e261326c4a2953a25da6`; its ten unique commits are 105 target
commits behind this starting point. Its earlier reviews do not qualify this new
branch or resolve its recorded default-resource concerns.

Sixteen shared rendering, consumer and regression-test files were carried
forward. Each was unchanged between the older candidate's merge base and the
current target. They include the corrected semantic coverage, bounded admission
and cancellation fixes, immutable v1 compatibility, and RGB output layout updates
in both existing hosts. The newer page port, deployment/resource defaults and
older candidate's historical evidence were not replaced.

## Baseline and focused evidence

Native authenticated baseline images at 1440×900, 390×844 and 320×844 are recorded
under `/home/roys/.cache/hvo/1035/`, together with matching prototype screenshots,
DOM geometry, layer availability and exact image URLs. The baseline runtime's
source tree `c4239571` is identical to merged `de18e41c`. Capture #543 is paused and
retained. It has flattened annotation, rather than an interactive retained layer
manifest; those two states are recorded separately. No prototype science values
were substituted. All three widths had no overflow or browser errors.

The first local rendering checks passed 21 Imaging compositor cases and 21
Processing payload/product cases. After the UI changes, 88 focused Current Sky,
capture-fact, authorization and operator-service cases passed with warnings as
errors. Regression cases cover four terminal optional layer outcomes arriving
during full screen, immediate authorization denial, rejected browser full screen,
and central configuration without an invented acknowledgement.

Fresh per-project Unit discovery is Imaging 240, Processing 210 and CameraAgent
2,904. The corresponding category inventories and solution Unit total 4,958 are
updated. These counts are discovery evidence, not a full-suite pass.

## Work still required

- Complete the capture-bound rig, cadence, lineage span and bottom-card facts.
- Exercise actual newly retained layered products and saved raster output in the
  native browser; compare desktop, phone and native overlay bounds and styling.
- Verify full-screen selection/default/save/Escape/focus/resizing, failure,
  authorization and disposal behavior in a real browser.
- Measure matched before/after renderer and host costs under declared limits;
  do not infer safe default-resource adoption from the earlier candidate's tests.
- Run classifier-selected Tier C candidate gates and independent exact-range
  review, then obtain the separate operator page review required by #1035 before
  proceeding through other pages.

Private preview state and evidence are outside installed instances. CameraAgent
previews bind `192.168.2.45`, never localhost. No installed instance, resource
default or protected `/mnt/hvo-1058` evidence was changed.
