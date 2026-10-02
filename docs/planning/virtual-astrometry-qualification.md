# Virtual all-sky astrometry qualification

Epic [#1098](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1098)
is the bounded virtual milestone within celestial-fidelity epic
[#520](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/520), inheriting
`RM-001`. The operator authorized continuous implementation, validation,
independent review and merge into **development/v1** on 2026-10-01 UTC in the
[execution ledger](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1098#issuecomment-5922323508).
There is no authorization to promote to main, deploy, take another owner's
claim, or activate every remaining #520 child.

## Delivered foundation and remaining evidence

[#1088](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1088) /
[PR #1093](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1093)
delivered shared Astronomy/Imaging detection, blind acquisition, accepted-prior
warm refinement, immutable assessments and native pixel/sky/frame mappings.
Reuse those contracts and their tests; see
[shared astrometry](../astronomy/shared-astrometry-v1.md) and
[its validation](../astronomy/shared-astrometry-validation.md).
Their presence does not qualify the complete configured VirtualSky pixel path,
exposure-integrated production rendering, reusable optical calibration or
statistically validated uncertainty.
#1103 adds the v2 star measurer and residual diagnostics; see
[measured-star diagnostics](../astronomy/measured-star-diagnostics-v1.md).

The standalone #1084/#1085/#1087 experiments are prior evidence, not production
renderer implementations. #1102 records the remaining #1087 crop/bin
acceptance disposition. Closing historical experiments requires their own
acceptance and ownership reconciliation; it is not a prerequisite or an
automatic side effect of this milestone.

## Implementation ownership and order

Issue links are the live claim/evidence authority. Reinspect assignees, status
labels, claim comments and linked PRs before claiming any slice. Claim one
implementation issue through review, CI, merge and release before selecting the
next. Each implementation must declare its concrete acceptance tolerances,
supported profiles, rejection cases and resource budgets before final evidence.
This plan does not assert that an unmeasured profile is supported.

| Package | Implementation owner | Start and integration gates | Retained boundary |
| --- | --- | --- | --- |
| Scope activation and acceptance map | [#1100](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1100) | Operator authorization; documentation review and merge before deferred implementation starts | No runtime acceptance claim |
| V2: bounded HYG selection | [#1101](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1101) | #1100; approved existing immutable HYG 4.2 snapshot | #521 retains 4.4, schema, epoch/proper-motion, photometric semantics and package lifecycle |
| V1: actual camera/readout baseline | [#1102](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1102) | #1101; delivered solver; final exposure-correct qualification waits for #522 | Initial stationary-renderer evidence is explicitly provisional |
| V3: visibility and exposure motion | Existing [#522](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/522) | #1100; reuse approved existing catalog fields; coordinate bounded footprint contracts with #518 | Complete #522 acceptance, including SNR/visibility, not only trail rendering; no resolved-body work |
| V4: measured stars and diagnostics | [#1103](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1103), scoped under #526 | #1102 and #522 expected signal/trail contract | #526 retains annotation rollout, diagnostic presentation and any broader host reconstruction requirements |
| V5: virtual optical calibration | [#1104](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1104), coordinated with #523 | #1103 measurements and actual-pixel harness; controlled emulator optics | Explicit candidate review/accept/reject and retained virtual versions; no physical-rig writes |
| V6: uncertainty and conventions | [#1105](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1105), scoped under #523 | #1103 centroid precision and #1104 calibration; new #1095 clock contracts only after merge | Existing geometric/catalog baseline; new proper-motion semantics require #521, no parallel clock implementation |
| V7: integrated qualification and handoff | [#1106](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1106) | All V1–V6 integrated, including #522; pinned final test envelope | Final audit owns closure evidence, not a waiver of upstream requirements |
| Later physical qualification | [#1107](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1107), under #523 | Physical hardware, trusted references and separate scheduling authorization | Does not block virtual completion; never absorbs unfinished supported virtual work |

The required combined-host validation exposed an existing nondeterministic
transient fixture while qualifying #1101. [#1109](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1109)
is a separate test/evidence prerequisite before #1101 merges. It preserves the
combined coverage floors and does not activate additional transient product work.
The catalog issue retains its claim while that dependency is repaired.

The initial execution order is #1100 -> #1101 -> #1102 -> #522 -> #1103 ->
#1104 -> #1105 -> #1106. #522 may be prepared after #1100 where existing
contracts suffice; final adoption still requires its numerical and resource
qualification. The #521 catalog upgrade is not a prerequisite for experiments
using the already approved snapshot, as #522 already permits. It remains a
technical gate for any new epoch/proper-motion or photometric data contract.
Broader #523 adoption retains its #521/#526 dependencies and real/reference
acceptance. Partial virtual delivery cannot close those broader requirements.

#1055 retains projected-scene storage/deduplication ownership. #987/#1095/#1096
retain existing UI, clock and identity work. Their live claims are not
transferred by this plan. #141 and #523 retain deferred FITS/WCS work. No new
star catalog, search-hint service, meteor/orbit reconstruction, registered-stack
production recipe or unrelated presentation redesign is included.

## Epic acceptance-to-evidence map

These rows map the nine completion criteria in #1098 in their original order.
All start **pending**. Merged code plus review and measured evidence, not an
issue's creation or this table, establishes completion.

| Criterion | Owning issues | Required closure evidence |
| --- | --- | --- |
| 1. Existing-scope reconciliation | #1100; final audit #1106 | This map, bounded authorization in roadmap and affected ledgers, delivered-code references, dependencies and retained hardware gates |
| 2. Honest HYG selection/resource limits | #1101, #522; integrated #1106 | Exact snapshot/selection identities, complete-selection counts, cap boundary/failure tests, no silent rendering truncation, full-catalog resource measurements |
| 3. Final pixels, acquisition/refinement/mapping/readouts | #1102; final rerun #1106 | Ordinary configured VirtualSky captures, immutable input hashes, independent blind/warm and mapping checks, supported ROI/bin/mirror/CFA cases and explicit unsupported rejection, no truth leakage |
| 4. Production exposure behavior | #522; integrated #1106 | Independently calculated SNR/flux, exact logical UTC integration, short-exposure/temporal-refinement/trail/clipping checks, sensor ordering and bounded full-resolution costs |
| 5. Reusable virtual optical calibration | #1104; integrated #1106 | Recovery from nominal optics, known projection and bounded identifiable parameters, held-out stars/frames, inverse/conditioning checks, mismatch/ambiguity/support rejection, review/accept/reject/version retention |
| 6. Diagnostics and validated uncertainty | #1103, #1105; integrated #1106 | Spatial/brightness/time residuals and reason codes; independent interval/prediction coverage for every declared supported profile; model-bias checks and unavailable results for unsupported/underconstrained cases |
| 7. Reproducible compatible identities | #1101–#1105; final audit #1106 | Catalog/source/readout/configuration/calibration/algorithm/coordinate identities, deterministic reconstruction, incompatible reuse rejection, explicit geometric/epoch/time semantics |
| 8. Full-resolution resources, gates and review | Each implementation owner; #1106 | Before/after CPU, allocations, working set, latency, throughput and bounds; exact-range classifier output, required local/protected gates and independent immutable-range reviews |
| 9. Final envelope and hardware handoff | #1106, retained #1107/#523 | Supported-envelope report, limitations, exact later hardware evidence with durable issue ownership; no in-scope work moved to backlog for closure |

## Qualification rules

- Run the ordinary camera path with configured sensor dimensions, pixel pitch,
  optics, site, logical exposure interval, noise and supported readout. Decode
  final immutable linear pixels and reconstruct CFA through the shared path.
  Private centroids, IDs, true pose/distortion and projected-star masks may only
  score completed results; they must not enter detection, acquisition or fit.
- Predeclare calibration/training, tuning and final held-out partitions, sky
  times, independent noise seeds, source selection, numerical tolerances,
  interval coverage tests, operation counts and resource budgets. Preserve
  analytic or separately implemented reference checks in addition to shared
  round trips. An experiment may refine the design; changing final acceptance
  requires a documented scientific reason and fresh held-out evidence, never
  weakening tests to turn a failure into a pass.
- Calibrate several frames into one immutable native-sensor model; derive
  cropped/binned views. Start with known projection, bounded scale/center and
  fixed parity. Modest radial terms require corresponding emulator support,
  independent validation and an invertible supported aperture. A parameter
  lacking identifiability is explicitly unsupported; this cannot excuse
  failure of a parameter/profile declared supported for the final milestone.
- Distinguish centroid error, conditional frame fit, shared optical uncertainty
  and systematic mismatch. Width and heuristic confidence are not covariance
  or probabilities. A good fit cannot certify site or clock accuracy.
- Keep geometry/fitting in Astronomy, measurement in Imaging, concrete catalog
  persistence in Catalog.Sqlite and activation/persistence authority in hosts.
  Reuse native assessments rather than introducing competing scene records.
- Follow [performance validation](performance-validation.md),
  [agent execution](agent-execution.md) and the
  [PR lifecycle skill](../../.agents/skills/pr-lifecycle/SKILL.md). Tier C/M
  implementation requires the complete AGENTS.md local candidate gate, with
  separate Unit and Docker Integration results and CI-control guards. Record
  `scripts/ci:classify` output on each immutable review range alongside results.
  Tier A #1100 uses its classifier-selected documentation checks.
- Each draft PR must converge through independent read-only exact-range review,
  finalization locking, target synchronization and any required integration
  review before protected CI and merge. Continue only on the reviewed head with
  green required checks. Missing, skipped, failed or unavailable required
  validation is not success.

#1106 may close #1098 only after auditing all nine criteria against delivered
code and evidence. Physical success is not implied by simulation. #1107/#523
remain open for real raw exposures, independent external astrometry, physical
lens/sensor stability and real-profile application/rollback; unsupported virtual
parameters must name the specific hardware evidence still required.
