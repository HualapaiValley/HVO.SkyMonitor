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

The estimator settings identity will change to v2, while the result JSON shape stays
v1. One-second numerical results must be unchanged. Eligibility at shorter durations
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

Durable-state migration, storage, deployment, background workers and runtime
telemetry are N/A: this is a synchronous, host-neutral numerical/evidence policy.

## Local results

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
| Issue acceptance: record exposure-gated release decision in shared owner with identity/version change | v2 estimator settings identity; fixed one-second gate; every covariance component absent above it | Proved |
| Issue acceptance: preserve qualified one-second baseline | Exact numerical component comparison and existing Monte Carlo core tests; no changes to numerical refit or declared floors | Proved for the bounded change; historical sensor-pixel baseline retained |
| Issue consumer constraint: no unsupported covariance release | Estimator, retained validation and cross-covariance regressions; Processing JSON/publication tests | Proved |
| Issue acceptance: tuning before independent held-out evaluation | No new qualification or tuning; original independent held-out failures remain the reason to withhold | Preserved; new requalification deferred |
| Issue acceptance: explicit envelope/non-goals and no physical claim | Updated long-exposure envelope and this non-goal ledger | Proved for this slice |
| Original broader statistical/renderer/readout ambitions | Still tracked by the open parent; no broader acceptance claim | Deferred under the operator's first-slice authorization |

Normal and boundary release, unsupported exposure, malformed input, cancelled
execution, missing contributor and retained evidence interactions have local
tests. Restart/recovery and durable-state cases are N/A because no persistence
or host protocol changes. The resource test records output parity and bounded
native/recipe workloads; it is a sanity check, not a new comparative performance
qualification. The new gate is constant-time after existing input validation and
avoids the unsupported refit entirely. Sol/Opus exact-candidate review reports
and the classifier output belong to the external handoff pack.
