# Long-exposure uncertainty: bounded withholding slice

Refs #1179. Target: `development/v1`; branch point `79a20dba`.

## Frozen scope and evidence plan

Declared 2026-11-13, before local regression evidence or estimator changes.
Original sources: [issue and discussion](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1179),
[predecessor](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1168), and the
[long-exposure envelope](../astronomy/virtual-astrometry-long-exposure-v1.md).
The operator explicitly permits the first coherent slice and reserves PR creation for Kepler.

The first slice enforces the existing consumer constraint in the shared owner:
an accepted mapping with an authoritative UTC exposure duration greater than one
second must withhold **all** covariance, including conditional and shared-calibration
components. The fixed reason is `exposure-not-qualified`. There is no configurable
override. Rejected assessments retain `assessment-not-accepted`; invalid inputs and
cancellation retain their existing behavior. Mapping acceptance and midpoint binding
do not change. Retained available evidence above the boundary must be rejected.

The initial candidate changed estimator/settings identity globally to v2. Review
found this invalidated qualified retained products. The corrected design preserves
the v1 numerical estimator, settings and Processing algorithm identities up to one
second, and binds `astrometric-exposure-release-v1` into the settings identity only
above that boundary. The result JSON shape stays v1. One-second results and
retained identities must be unchanged. Eligibility at shorter durations
does not qualify a new profile, gain, lens, measurer, or physical camera.

### Scientific evidence retained, not retuned

The independent held-out `final-350044f8` pack is the basis for withholding:
SHA256SUMS SHA-256 `ef50c44284f28eb38ea2193efece64350881a0a8d3d6f69d9a188f01d6c6d690`.
The tuning pack `tuning-f7e69d99` has SHA256SUMS SHA-256
`c8d9c647ff486f45e34df29b52e8268ad8c511b4034cc2f0d57b9554ac6b697d`.
The revision rerun `final-99301c2d` reproduced the three uncertainty-process failures.
Their reports and frozen floors remain historical evidence, with no edits or retuning.
The old raw packs are not present on this host. No new coverage qualification is claimed.

### Predeclared local checks

- Reproduce the release-policy defect before changing source, using accepted
  midpoint-centroid fits with fixed seed 812, catalog seed 934817, uniform centroid
  noise ±0.02 px and sigma `0.04 / sqrt(12)`. Test durations one second plus one
  100 ns tick, 20 s, and 60 s, with and without shared calibration. These are policy
  regressions, not a reproduction of the old sensor-pixel coverage statistics.
- Preserve exact numerical components at one second and one tick below it; capture
  baseline and candidate output with the same fixed inputs and compare byte for byte.
- Reject internally rehashed available evidence above the boundary; preserve exact
  withhold reason, native lineage, clock facts, input validation and cancellation.
- Exercise the ordinary Processing recipe and retained JSON at 20 s and 60 s.
- Run affected estimator, Monte Carlo core, catalog-selection and measured-astrometry
  tests only, under `flock /tmp/hvo-heavy-gate.lock`, with
  `MSBUILDDISABLENODEREUSE=1` and `TMPDIR=/tmp/hvo-1179`.
- Confirm whether changed sources are in the canonical line inventory. Regenerate
  only if required. Floors and policy remain unchanged; full coverage stays in CI
  under the operator's instruction.
- Obtain the HIGH tier's independent Sol and Opus local reviews on the candidate.

## Explicit non-goals and remaining work

The parent issue remains open. Statistical requalification needs a new frozen
tuning/independent held-out campaign before any exposure above one second can be
released. It is deferred; no variance inflation is fitted to evaluation data.
Renderer temporal capacity, mono cloud readout, ambiguity/acceptance rates,
long-trail metadata, deeper catalogs and other long-exposure projection families
are explicit non-goals of this slice. Existing refusal and fail-closed behavior
stays in force. Physical accuracy remains unclaimed.

No storage migration or CameraAgent production change is made. Retained restore,
replay, viewer and central reconstruction are affected consumers and require
compatibility evidence. Deployment and background-worker protocol changes remain
out of scope; existing fail-closed worker handling is assessed below.

## Local results

The first table records initial candidate `2723490d`; final correction results
are recorded below and supersede its candidate counts and identity design.

Environment: Ubuntu 24.04 x64, SDK 10.0.402, runtime 10.0.14, Release.
Sources/discussion were read and snapshotted at 2026-11-13T00:33:43Z.
Evidence is retained on this development host at
`/home/roys/review-evidence/1179-long-exposure-uncertainty`.

| Selection | Pass | Fail | Skip |
| --- | ---: | ---: | ---: |
| Pre-change estimator regressions and existing estimator tests | 24 | 9 expected | 0 |
| Candidate `AstrometricUncertaintyEstimatorTests` | 33 | 0 | 0 |
| Candidate `AstrometricUncertaintyCoverageTests` | 23 | 0 | 0 |
| Candidate `DeepCatalogSelectionTests` | 23 | 0 | 0 |
| Candidate `MeasuredAstrometryRecipeTests` | 21 | 0 | 0 |
| Candidate `MeasuredAstrometryIntegrityTests` | 37 | 0 | 0 |
| Candidate `MeasuredAstrometryResourceTests` (Manual) | 1 | 0 | 0 |
| **Candidate total** | **138** | **0** | **0** |

The nine expected failures demonstrate release of unsupported long-exposure
covariance and acceptance of rehashed available long-exposure evidence. The
initial two Processing attempts failed the fixture's source-lineage gate
(integration still declared one second); they are discarded fixture diagnostics,
not fail-first product evidence. Corrected integration metadata reaches the
ordinary recipe and publication contract at 20 s and 60 s; both pass with a
measured mapping and explicit covariance withholding.

The exact baseline/candidate numerical-component JSON SHA-256 is
`f83322fbd08be32a6d6971e3ba25156e385bb759e247ddf63ae9afaaf6b1a1e1`.
Both one-second boundary rows match it byte for byte. This is a deterministic
component comparison, not a rerun of the original full-resolution qualification.

Commands (all dotnet test invocations wrapped in the heavy lock with the declared
environment; candidate builds also use `--disable-build-servers
-p:UseSharedCompilation=false -nodeReuse:false -warnaserror`):

```bash
dotnet test tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj \
  --configuration Release --no-restore \
  --filter 'FullyQualifiedName~AstrometricUncertaintyEstimatorTests|FullyQualifiedName~AstrometricUncertaintyCoverageTests|FullyQualifiedName~DeepCatalogSelectionTests'
dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj \
  --configuration Release --no-restore --filter 'FullyQualifiedName~MeasuredAstrometry'
```

Focused static discovery (all six categories and uncategorized/overlap checks)
observed Astronomy Unit=815/Integration=2/Manual=5 and Processing
Unit=1323/Manual=19, with all other categories zero. The owning inventories were
updated from those observations, and the runbook total was recomputed from all
inventories. Whitespace verification on the six changed C# files, `git diff
--check`, the four CI-control guards, and `scripts/docs:audit-operations` pass.

Neither changed source is in `scripts/coverage/development-v1-lines.json` or the
canonical baseline's enforced-file set. No inventory regeneration is triggered;
floors and `policy.json` are unchanged. Full coverage and target-branch protected
CI remain for Kepler/CI, as explicitly directed by the operator.

### Requirement mapping for the authorized slice

| Source requirement | Implemented evidence | Disposition |
| --- | --- | --- |
| Issue acceptance: record exposure-gated release decision in shared owner with identity/version change | Versioned exposure policy bound into settings identity only above one second; fixed gate; every covariance component absent above it | Proved |
| Issue acceptance: preserve qualified one-second baseline | Exact numerical component comparison and existing Monte Carlo core tests; no changes to numerical refit or declared floors | Proved for the bounded change; historical sensor-pixel baseline retained |
| Issue consumer constraint: no unsupported covariance release | Estimator, retained validation and cross-covariance regressions; Processing JSON/publication tests | Proved |
| Issue acceptance: tuning before independent held-out evaluation | No new qualification or tuning; original independent held-out failures remain the reason to withhold | Preserved; new requalification deferred |
| Issue acceptance: explicit envelope/non-goals and no physical claim | Updated long-exposure envelope and this non-goal ledger | Proved for this slice |
| Original broader statistical/renderer/readout ambitions | Still tracked by the open parent; no broader acceptance claim | Deferred under the operator's first-slice authorization |

Normal and boundary release, unsupported exposure, malformed input, cancelled
execution, missing contributor and retained evidence interactions have local
tests. Retained restart/replay/read compatibility is assessed below; no persistent
layout or host protocol changes. The resource test records output parity and bounded
native/recipe workloads; it is a sanity check, not a new comparative performance
qualification. The new gate is constant-time after existing input validation and
avoids the unsupported refit entirely. Sol/Opus exact-candidate review reports
and the classifier output belong to the external handoff pack.

## Independent-review correction (F1, F2)

Opus identified a retained-product interaction in initial candidate `2723490d`:
a global v2 settings/algorithm identity invalidates historical one-second products
at viewer, restore, replay and central verification. It also breaks the existing
Manual retained-resource test's canonical solution hash pin. That candidate is
superseded; its test counts above remain historical evidence.

The correction preserves original identities for exposures up to one second,
including rejected assessments and withheld centroid covariance. The new policy
identity is scoped to unsupported longer exposures. Input-bound validation and
Processing context matching compute the same scoped identity. The existing
Manual solution hash pin remains unchanged and must pass. No automatic rewrite,
refit, prepared-file deletion or new storage protocol is introduced. Historical
available long-exposure evidence remains rejected, as the original envelope
requires; retained runbook guidance states the limitation.

Correction acceptance, declared before runs: repeat the affected Astronomy and
Processing selection with the two new identity/restore regressions; run existing
retained-astrometry restart/read/export/replay cases and the Manual resource pin;
inspect optional-node exception disposition; preserve coverage floors, policy
and original one-second numerical baseline hash. None of the three edited source
files is pinned in the canonical inventory.

Worker disposition was traced through `MeasuredAstrometryCaptureProcessingStep`,
`CaptureAstrometryPersistence` and `FrameProcessingWorker`: an invalid prepared
result raises through the normal exception path. The production graph makes
astrometry optional; live work degrades the node without retrying its lane, and
replay uses its configured `ReplayMaximumAttempts` before optional exhaustion.
Qualified one-second restore/replay has unchanged payload and identity. The
existing optional-node regressions are included in the focused correction run;
no worker or storage implementation changes are made.

Low notes L2/L3 are addressed by exposure-eligible naming and exact tick semantics.
L4 is documented: future requalification must freeze its evaluation path as well
as tuning before evidence; this slice adds no public bypass. L1 (generic recipe/
algorithm identity coupling) remains proposed follow-up text for Kepler; this owner
has filed no GitHub issue.

### Correction validation results

| Final selection | Pass | Fail | Skip |
| --- | ---: | ---: | ---: |
| Astronomy: estimator (34), Monte Carlo (23), catalog selection (23) | 80 | 0 | 0 |
| Processing: recipe (22), integrity (37), Manual resources (1) | 60 | 0 | 0 |
| CameraAgent retained astrometry: graph, restart/read/export/replay, viewer and Manual resource pin | 30 | 0 | 0 |
| Existing optional-worker terminal/degraded/exhausted disposition | 4 | 0 | 0 |
| **Final affected total** | **174** | **0** | **0** |

The original Manual retained solution pin passes unchanged:
`959067FF97697681F5118ED6543F39C40EEACEC4CA43999D083A75316B6B80A9`.
This proves the qualified one-second canonical solution identity remains the
pre-change identity. Existing restore/replay/export/read tests and the new
Processing restore regression pass without refitting. Both numerical boundary
JSON rows still match the original SHA-256
`f83322fbd08be32a6d6971e3ba25156e385bb759e247ddf63ae9afaaf6b1a1e1`.
The warning-as-error build caught a redundant constant assertion in the new test;
it was removed and the final Astronomy build/run exits 0. No production logic
was changed to suppress a diagnostic.

Additional focused commands, with the declared short TMPDIR, node-reuse setting
and heavy lock (no Docker or live service is used):

```bash
HVO_ISSUE1643_EVIDENCE_ROOT=/tmp/hvo-1179/evidence/correction/retained-resource \
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release --filter 'FullyQualifiedName~RetainedAstrometry_'
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release --no-build --no-restore \
  --filter 'FullyQualifiedName~OptionalTerminalNode_DoesNotBlockIndependentRequiredNode|FullyQualifiedName~OptionalRetryableNode_'
```

Focused static discovery observes Astronomy Unit=816/Integration=2/Manual=5 and
Processing Unit=1324/Manual=19, all other categories zero. Owning inventories and
runbook totals are updated from this discovery. Final whitespace verification on
all seven changed C# files and the four CI-control guards pass. The full coverage
gate remains in CI by the operator's instruction; floors, policy and pinned
inventory are unchanged. Detailed TRX, logs and exact-candidate Sol/Opus correction
reports are retained in the external evidence directory.
