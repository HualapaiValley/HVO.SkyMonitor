# Deep HYG catalog selection — bounded virtual astrometry (v1)

Issue [#1167](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1167), under epic
[#520](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/520), measured complete HYG selections deeper than
magnitude 5 on the virtual projection families of [#1126](virtual-astrometry-qualification-v2.md), under the named,
immutable selection profile `hyg-deep-selection-v1`.

**Status: 5.5 fails. Nothing is advertised.** The method was frozen at `d3b78737` and measured there (see
[Results](#results)). The issue's
acceptance minimum, a complete selection above 2,500 entries qualified at magnitude 5.5, is not met, so #1167 stays open
and carries the gap (see [Administration](#administration)). No configuration can select a deep profile; the contract
is stated in [Shared astrometry: deep selection profile](shared-astrometry-v1.md#deep-selection-profile). The name
`hyg-deep-selection-v1` is burned: it records what was measured and not qualified, and any later attempt uses a new
name.

The variants, matrix, partitions, gates and decision rule below were declared in the
[#1167 manifest](../validation/issue-1167-qualification-manifest.json) before any tuning, held-out, A/B or
magnitude-7 measurement, and are unchanged.

This is a virtual qualification only. It makes no claim about physical accuracy or detection sensitivity, and no
profile here is a physical lens certification.

## Catalog selection

Counts are complete selections of the installed `hyg-v42-production` snapshot (HYG 4.2, 119,625 rows, database
SHA-256 `B51D18B7…0B9E2`). A request is complete only when every row at or brighter than the requested magnitude fits
inside the request bound.

| Requested magnitude | Rows | Fits the bound of |
| --- | --- | --- |
| ≤ 5.0 | 1,637 | legacy 2,500 |
| ≤ 5.37 | 2,483 | legacy 2,500 (the faintest complete legacy request) |
| ≤ 5.5 | 2,865 | 4,096 or 8,192 |
| ≤ 6.0 | 5,070 | 8,192 |
| ≤ 6.5 | 8,920 | none (exceeds the 8,192 ceiling) |
| ≤ 7.0 | 15,598 | none; the solver default, evaluated only |

Under a 4,096 bound the faintest complete request is 5.79 (4,059 rows); under 8,192 it is 6.42 (8,142 rows). The
profile's qualified maximum, not the bound, limits what a configuration may request.

### Legacy null-profile band

Settings without a profile keep the 2,500-entry bound, exactly as before #1167. That bound admits a complete request
up to magnitude 5.37. Only magnitude 5 is qualified (#1098/#1126); a request above 5.0 up to 5.37 is admitted
unqualified and is outside this qualification's evidence. #1167 does not change it, because doing so would change
behavior for configurations that did not opt in.

## Frozen method

Each run is one process per projection family and variant, at one committed clean revision built in Release with
`DOTNET_TieredCompilation=0`, sequentially on one otherwise idle host holding the epic's heavy lock for the whole
measured sequence. Detections come from the #1102/#1126 measurer (`StellarDetector` v1 through
`VirtualAstrometryFixture.Measure`), so the `m50` baseline runs the #1126 pixel settings identity on this issue's
seed block.

### Variants

| Variant | Render (magnitude, results) | Solve magnitude | Profile | Pointing | Baseline |
| --- | --- | --- | --- | --- | --- |
| `m50` | 5.0, 2,000 | 5.0 | none | family views | — |
| `m55` | 5.5, 32,768 | 5.5 | `hyg-deep-selection-v1` | family views | `m50` |
| `m60` | 6.0, 32,768 | 6.0 | `hyg-deep-selection-v1` | family views | `m50` |
| `d55` | 6.5, 32,768 | 5.5 | `hyg-deep-selection-v1` | family views | `m50` |
| `d60` | 6.5, 32,768 | 6.0 | `hyg-deep-selection-v1` | family views | `m50` |
| `x50` | 5.0, 2,000 | 5.0 | none | dense | — |
| `x55` | 6.5, 32,768 | 5.5 | `hyg-deep-selection-v1` | dense | `x50` |
| `x60` | 6.5, 32,768 | 6.0 | `hyg-deep-selection-v1` | dense | `x50` |

`m` variants render exactly the solved depth. `d` and `x` variants render to 6.5, so the image holds stars fainter than
the catalog selection. `x` variants point the rectilinear 6 mm field at the densest sky on a 1° grid whose corners stay
at least 10° up: 52°/260° on 10 February, 52°/235° on 15 January, 56°/70° on 15 May and 66°/35° on 15 September, at
08:00Z, holding 643–777 stars to magnitude 6.

The four fisheye families run `m50`, `m55`, `m60`, `d55` and `d60`; rectilinear 6 mm runs all eight. Rectilinear 8 mm
runs all eight for the record and never gates.

### Partitions

Tuning is 10 February 2026, seed 116710, and is never decision evidence. Held-out is 15 January, 15 May and
15 September 2026, seeds 116711, 116715 and 116719. Each view captures at 08:00:00Z plus 0, 60 and 120 s: index 0
is blind (cold), and 1 and 2 are warm from the previous accepted assessment of the same view.

### Gates

Held-out runs only; envelope families gate.

| Gate | Depths | Rule |
| --- | --- | --- |
| G0 | 5.5, 6.0 | One report per gated run and baseline at the measured revision, matching the manifest's schema, partition, family, variant, catalog and complete selection count, with partitions × views × 3 rows. Otherwise undecidable. |
| G1 | 5.5, 6.0 | Same case ids as the baseline; no failing case the baseline does not also fail. |
| G2 | 5.5, 6.0 | False associations ≤ 1% of associations in every accepted case. A false association is one whose catalog star's truth pixel is more than 1.5 px from its detection. |
| G3 | 5.5, 6.0 | Source probe matches; no probe, re-capture or re-solve failure, in the variant and its baseline. |
| G4 | 5.5, 6.0 | Selection p95 ≤ 20 ms at 5.5 and ≤ 40 ms at 6.0 (30 reads after 5 warm-up). |
| G5 | 5.5, 6.0 | Cold p95 ≤ 2× the baseline's; every cold solve ≤ 15 s and every warm solve ≤ 500 ms. |
| G6 | 5.5 | Mean cold allocation ≤ 2× the baseline's. |
| G7 | 6.0 | Peak working set ≤ the baseline's + 128 MiB. |
| G8 | 5.5, 6.0 | Every perspective cold index prefix ≤ 16 MiB. |

A depth qualifies when every gate for it holds for every envelope family and every variant of that depth; 6.0
qualifies only if 5.5 does.

| Outcome | Profile bound | Qualified maximum |
| --- | --- | --- |
| 5.5 and 6.0 qualify | 8,192 | 6.0 |
| Only 5.5 qualifies | 4,096 | 5.5 |
| 5.5 fails | nothing advertised; one follow-up under #520 | — |

The bound is in no identity, so changing it to 4,096 leaves every measured 5.5 selection and assessment unchanged.

#### Rulings fixed before the decision

The #520 coordinator made two rulings on 7 October 2026 after the held-out run had started and before any evaluator
output existed. Neither changes a gate or a threshold.

- **Infrastructure errors (07:20:16Z).** A gate is evaluable only when every report it reads exists. G1, G5, G6, G7
  and the baseline half of G3 read the variant's and the baseline's report; G2, G4, G8 and the variant half of G3 read
  only the variant's.
  - Any evaluable 5.5 failure on an envelope family decides "5.5 fails".
  - An evaluable failure only at 6.0 rules out "5.5 and 6.0 qualify" but does not decide "5.5 fails".
  - Otherwise, with a gated report missing, the decision is undecidable (G0). Undecidable ships the "5.5 fails" code
    shape, so nothing is advertised, and it is recorded as undecidable, not as a failure.
  - Tuning and rectilinear 8 mm never gate. A harness exit code or test result is not an outcome.
  - A rerun needs separate approval, a harness fix committed first, and the complete held-out matrix at that one new
    revision. Results from two revisions are never combined.
- **Code shape and administration (07:09:49Z).** If nothing qualifies, nothing is advertised:
  - no profile is declared, and no public member can select one;
  - the measured profile stays internal so that its harness remains runnable;
  - its name is burned.

#### Administration

The coordinator's 07:09:49Z ruling replaces the manifest's administrative line, "one follow-up under #520 records the
evidence". No follow-up issue is opened. #1167 stays open as `workflow:blocked`, holds the frozen evidence and carries
the residual gap. The operator decides between a second strategy and closing it. This is an administrative change,
not a decision-rule change; the manifest file is frozen and unedited.

The rule applies as written to the whole envelope. A depth that fails on any subset of cases, such as 2×-binned
captures only, is not advertised for the remainder. A claim conditional on binning or any other post-hoc subset is a
new hypothesis for a later attempt under a new profile name. The #520 coordinator recorded this before any held-out
measurement.

## Results

The measured sequence ran on hvo-dev-03 on 7 October 2026: tuning 03:58:50Z–04:29:45Z, magnitude-5 A/B
04:29:45Z–06:54:56Z, held-out 06:54:56Z–08:10:15Z, magnitude 7 08:10:15Z–08:17:58Z and the evaluator until
08:18:01Z.

### Decision

The evaluator's `decision.json` (SHA-256 `ea02077b…1582247`) for revision `d3b78737` and manifest `ed3b4d03…3c77`
reports that neither 5.5 nor 6.0 qualifies. Its `outcome` field is null because two gated runs wrote no report (see
the [detector finding](#finding-detector-candidate-budget-at-a-65-render)). Under the 07:20:16Z ruling, the
evaluable G1 failures at 5.5 on every envelope family decide "5.5 fails". The ruling's application is recorded in
`decision-ruling.json` (SHA-256 `00ebaf40…0e6bb89`):

- Every row with both reports was evaluable on every gate.
- No present report had a G0 problem.
- Only equidistant `d55` and `d60` are undecidable.

The outcome rests on G1, which compares case outcomes and does not depend on time.

### Held-out gates

Each run's case ids start with the held-out date (`01`, `05`, `09`), then name the capture view and its index
(0, 1, 2). Each family has four unbinned views and two 2×-binned views (`mono-bin2`, `mono-roi-bin2`), and
equidistant adds the #1126 CFA views. The gate columns are the evaluator's. The failing counts are per binning class.

| Family | Variant | Failing, unbinned | Failing, 2×-binned | Cold p95 ms (baseline) | Selection p95 ms | Cold allocation ÷ baseline | Peak working set Δ MiB | Failed gates |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| equidistant | `m55` | 0 of 72 | 15 of 18 | 4,596 (2,737) | 3.02 | 1.60 | +39 | G1 |
| equisolid | `m55` | 0 of 36 | 14 of 18 | 4,084 (2,922) | 2.98 | 1.57 | +20 | G1 |
| equisolid | `d55` | 0 of 36 | 6 of 18 | 5,139 (2,922) | 3.09 | 1.65 | +24 | G1 |
| stereographic | `m55` | 0 of 36 | 18 of 18 | 6,468 (4,176) | 3.02 | 1.59 | +54 | G1 |
| stereographic | `d55` | 0 of 36 | 5 of 18 | 6,059 (4,176) | 2.88 | 1.58 | +66 | G1 |
| orthographic | `m55` | 0 of 36 | 14 of 18 | 3,718 (2,282) | 3.10 | 1.62 | +30 | G1 |
| orthographic | `d55` | 0 of 36 | 18 of 18 | 3,524 (2,282) | 2.78 | 1.59 | +21 | G1 |
| rectilinear | `m55` | 0 of 36 | 2 of 18 | 2,983 (2,464) | 2.90 | 1.11 | -12 | G1 |
| rectilinear | `d55` | 0 of 36 | 6 of 18 | 2,924 (2,464) | 3.11 | 1.10 | -48 | G1 |
| rectilinear | `x55` | 0 of 36 | 7 of 18 | 3,143 (2,418) | 3.06 | 1.11 | +109 | G1 |
| rectilinear-8mm (not gating) | `m55` | 0 of 36 | 0 of 18 | 2,387 (2,063) | 2.90 | 1.05 | -53 | none |
| rectilinear-8mm (not gating) | `d55` | 0 of 36 | 0 of 18 | 3,319 (2,063) | 2.75 | 1.06 | -88 | none |
| rectilinear-8mm (not gating) | `x55` | 0 of 36 | 6 of 18 | 3,423 (2,928) | 4.68 | 1.06 | +128 | G1 |
| equidistant | `m60` | 0 of 72 | 16 of 18 | 7,382 (2,737) | 5.22 | 2.67 | +48 | G1+G5 |
| equisolid | `m60` | 0 of 36 | 17 of 18 | 8,639 (2,922) | 4.73 | 2.68 | +25 | G1+G5 |
| equisolid | `d60` | 0 of 36 | 9 of 18 | 8,652 (2,922) | 4.72 | 2.72 | -11 | G1+G5 |
| stereographic | `m60` | 0 of 36 | 14 of 18 | 10,929 (4,176) | 5.08 | 2.50 | +79 | G1+G5 |
| stereographic | `d60` | 0 of 36 | 4 of 18 | 10,657 (4,176) | 8.77 | 2.60 | +43 | G1+G5 |
| orthographic | `m60` | 0 of 36 | 18 of 18 | 5,820 (2,282) | 8.57 | 2.66 | +45 | G1+G5 |
| orthographic | `d60` | 0 of 36 | 18 of 18 | 6,463 (2,282) | 5.34 | 2.62 | +76 | G1+G5 |
| rectilinear | `m60` | 0 of 36 | 18 of 18 | 3,989 (2,464) | 4.61 | 1.28 | +7 | G1 |
| rectilinear | `d60` | 0 of 36 | 18 of 18 | 3,972 (2,464) | 4.76 | 1.28 | -52 | G1 |
| rectilinear | `x60` | 0 of 36 | 18 of 18 | 4,136 (2,418) | 8.37 | 1.29 | +131 | G1+G7 |
| rectilinear-8mm (not gating) | `m60` | 0 of 36 | 18 of 18 | 3,051 (2,063) | 8.34 | 1.17 | -2 | G1 |
| rectilinear-8mm (not gating) | `d60` | 0 of 36 | 18 of 18 | 4,358 (2,063) | 7.94 | 1.17 | -73 | G1+G5 |
| rectilinear-8mm (not gating) | `x60` | 0 of 36 | 18 of 18 | 4,106 (2,928) | 6.93 | 1.17 | +39 | G1 |

The baselines fail nothing on the envelope families. The rectilinear 8 mm `m50` baseline fails 3 of its 36 unbinned
cases.

Per gate:

- **G1** fails on every evaluable envelope row at both depths.
- **G2 and G3** have no finding anywhere.
- **G4** passes: selection p95 is at most 3.11 ms at 5.5 and 8.77 ms at 6.0.
- **G5** fails at 6.0 on every fisheye row. Cold p95 is 2.55–2.96× the baseline's; rectilinear is 1.61–1.71×. No
  single solve exceeds the 15 s or 500 ms limits.
- **G6** passes at 5.5, with cold allocation 1.10–1.65× the baseline's.
- **G7** fails only on rectilinear `x60`, at +131 MiB against the 128 MiB limit.
- **G8** passes: the largest perspective cold index prefix is 5.2 MB at 5.5 and 9.1 MB at 6.0.

### 2×-binned captures

Every new failure is a 2×-binned capture. On the gating rows:

| Depth | Unbinned failing | Unbinned failing, baselines | 2×-binned failing | 2×-binned failing, baselines |
| --- | --- | --- | --- | --- |
| 5.5 (10 rows) | 0 of 396 | 0 of 396 | 105 of 180 | 0 of 180 |
| 6.0 (10 rows) | 0 of 396 | 0 of 396 | 150 of 180 | 0 of 180 |

On the non-gating rectilinear 8 mm rows at 5.5, `x55` fails 6 of its 18 binned cases and `m55` and `d55` fail none.
At 6.0, all three fail 18 of 18. Equidistant `d55` and `d60` have no report and are not counted.

This is an observation, recorded at the #520 coordinator's direction (08:22:26Z): no subset is qualified or claimed;
the pre-committed binning carve-out guard applies; the outcome is 3 as written.

### Candidate isolation grid

The manifest's grid rule, quoted: "Keep the grid when the median estimatedPairwiseShare is >= 0.05, or any row's
pairwiseMs x distinctCandidates is >= 1500 ms." The evaluator applies it as an OR. Over the 192 cold rows of the
envelope families' 6.0 variants:

- The median estimated pairwise share is 0.0683.
- The largest estimated pairwise time is 1,682 ms.
- The median grid time is 0.91 ms; the median pairwise time is 38.2 ms.

The rule says keep, on both criteria; the maximum alone would keep it. Fit 05 governs instead: under the #520
coordinator's 08:42:10Z attribution rule the grid was reverted (see
[Optical-calibration fit 05](#optical-calibration-fit-05)). `IsolatedIds` was tried and reverted, and no performance
claim is made for it. The pairwise times above are among those measured under possible neighbour load (below).

What the measured evidence records about the two rules selecting the same isolated set:

- The deep harness at `d3b78737` ran both rules on 512 cold rows (378 held-out, 134 tuning; every family, the 6.0
  variants `m60`, `d60` and `x60`). Each row's input was the truth-projected above-horizon selection, from 181 to 2,496
  projected stars and 48 to 1,745 isolated. The harness fails a case on any set difference. No row failed.
- The magnitude-5 A/B below compares solver v2's pairwise rule (A) with the grid (B) through complete solves. Their
  outputs are identical.
- The pairwise-oracle unit tests passed. They cover seeded skies of up to 8,192 stars and the boundary, coincident,
  non-finite and extreme-coordinate cases.

None of these compares the per-candidate projections that `Evaluate` computes in a 5.5 or 6.0 solve, and the harness
rows hold no 5.5 selection.

The merge-head harness no longer times isolation. Against a later report the evaluator's grid section finds no rows.

### Measurement-host disclosure

From 06:58:22Z to about 07:00:34Z on 7 October 2026, an evidence copy (`rsync`, nice 19, idle I/O class, about 121 MB)
ran on the measurement host while the gated held-out run `final-equidistant-m55` was active. It was stopped and was
the only other load on the host. The #520 coordinator ruled that the run is evaluated as measured without a rerun:

- The overlap can only inflate wall-clock time, so a G4 or G5 pass on that run stands.
- A G4 or G5 failure that alone decides the outcome goes back to the coordinator before it is applied.
- G6 and G7 are process-local and stand.

**Possible neighbour load.** hvo-dev-03 and hvo-dev-02 are probably guests on one physical host: both are 8-vCPU KVM
guests on an Intel Core Ultra 9 285H. During the measured sequence, #1168's work ran on hvo-dev-02:

- continuity, 05:22Z–06:05Z;
- tuning, 06:07Z–06:28Z;
- its final pack, 06:30Z–07:59Z;
- gate tests.

Outcome 3 rests on G1, which does not depend on time. Every timing measured in that window should be read as possibly
inflated by neighbour load. That includes:

- the G4 and G5 timings;
- the grid's pairwise times;
- the A/B timing pairs;
- the optical-calibration fit-05 times.

The first fit-05 diagnostic's second B run (08:24:10Z–08:26:45Z) overlapped #1168's gate tests. The attribution
session below held both hosts' heavy locks.

### Finding: detector candidate budget at a 6.5 render

`StellarDetector` v1 stops at 4,096 candidate peaks (`StellarDetector.MaximumSupportedCandidates`) and throws
`InvalidOperationException: The stellar detection candidate budget was exceeded`, returning no partial result. The
frozen measurer passes no smaller budget. When an image rendered to magnitude 6.5 holds more peaks than that, the run
aborts before any solve and writes no report:

```text
StellarDetector.Detect                                   src/HVO.SkyMonitor.Imaging/StellarDetector.cs:99
VirtualAstrometryFixture.Measure                         tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualAstrometryFixture.cs:225
VirtualDeepAstrometryQualificationTests (case loop)      tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualDeepAstrometryQualificationTests.cs:126, 109
VirtualDeepAstrometryQualificationTests (magnitude 7)    tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualDeepAstrometryQualificationTests.cs:293, 289
```

Three runs at `d3b78737` hit the budget, all on the equidistant family:

| Run | Partition | Render (magnitude, results) | Solve magnitude | Exit | Report |
| --- | --- | --- | --- | --- | --- |
| `final-equidistant-d55` | held-out, gating | 6.5, 32,768 | 5.5 | 1 | none |
| `final-equidistant-d60` | held-out, gating | 6.5, 32,768 | 6.0 | 1 | none |
| `mag7-equidistant` | magnitude 7, never gating | 7.0, 32,768 | 7.0 | 1 | none |

Every other run completed under the budget, including the tuning equidistant `d55` and `d60` runs on 10 February
and every other family's 6.5 and 7.0 renders. The two held-out runs make equidistant `d55` and `d60` undecidable
under G0; they decide nothing either way.

This is a limit of the frozen measurer at that render depth, not a solver result. It is recorded here and not fixed
under #1167, and no issue is opened for it (coordinator ruling 07:20:16Z).

### Magnitude-5 A/B

`SolverVersion` stays `spherical-triangle-astrometry-v2`, so #1167 must not change any magnitude-5 output. The
unchanged #1126 qualification runs at the base `0639e27d` (A) and at the measured head (B), final and tuning, over
every frozen #1126 case. Every `*IdentitySha256` value and every association array must be equal. Timing is compared
as adjacent A1/B1 and B2/A2 pairs of held-out pixel runs per family; B/A above 1.10 in both is a resolvable
regression that blocks merge until explained.

One behavior differs only in time. The old code could spend its cold budget building catalog rows that a later check
then rejected. The new code reaches that rejection sooner. No deterministic output changes.

**Result: pass.** Over 45 reports (the A and B final and tuning reports of every frozen #1126 case), all 17,405
`*IdentitySha256` values and all 467 association arrays are identical between `0639e27d` and `d3b78737`. The 8 other
differing leaves are all derived from timing:

- 6 are `operationsPerSecond` throughput rates.
- 2 are the failure messages of two #1126 timing assertions that failed at B only. Both still wrote their reports.
  - A StellarDetector v2 measurement took 56 ms against 34–36 ms for its other samples. #1167 does not change Imaging.
  - An optical-calibration session fit took 20,275 ms against its 19,541 ms limit. At A the same row took 16,768 ms;
    the other rows are flat or faster at B. See [Optical-calibration fit 05](#optical-calibration-fit-05).

Neither assertion is part of the frozen A/B rule. The #520 coordinator ruled the detector spike disclosed only.

| Family | Cold solves | A1 ms | B1 ms | B2 ms | A2 ms | B1/A1 | B2/A2 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| equidistant | 30 | 2,490 | 2,166 | 2,135 | 2,141 | 0.870 | 0.997 |
| equisolid | 18 | 2,327 | 2,242 | 2,290 | 2,223 | 0.963 | 1.030 |
| stereographic | 18 | 2,333 | 2,400 | 2,326 | 2,356 | 1.029 | 0.987 |
| orthographic | 18 | 1,887 | 1,840 | 1,770 | 1,837 | 0.975 | 0.964 |
| rectilinear | 18 | 1,794 | 1,899 | 1,849 | 1,780 | 1.059 | 1.039 |

Medians of held-out cold solves. No family exceeds 1.10 in both pairs. Rectilinear is the only family slower in both,
by 5.9% and 3.9%.

#### Default-path changes

These changes run for every configuration, including those without a profile:

- `IndexPrefix` stores triangular jagged rows, one allocation per admitted ray, instead of a flat n² array. It stops
  as soon as an index the caller must reject is certain. Only the perspective path builds an index prefix.
- The `catalog-selection-unsupported` guards in `AstrometricSolver` and `OpticalCalibrationSession` are O(1)
  comparisons made before any numerical work.

The measured revision `d3b78737` also replaced the isolation step in `Evaluate` with a linear-time grid
(`IsolatedIds`). That grid has been reverted, so the merge head's `Evaluate` is textually identical to `0639e27d` (see
[Optical-calibration fit 05](#optical-calibration-fit-05)).

The A/B pass above covers both changes. It measured `d3b78737`, which still had the grid. The table compares
allocation and working set, A (`0639e27d`) against B (`d3b78737`), from the #1126 optical-calibration and
astrometric-uncertainty reports of runs A1 and B1:

| Partition | Family | Session fits alloc MiB A → B | Omitted-k1 fits alloc MiB A → B | Cold solve alloc (mean) MiB A → B | Calibration peak WS MiB A → B | Uncertainty frame alloc (mean) MiB A → B | Uncertainty peak WS MiB A → B |
| --- | --- | --- | --- | --- | --- | --- | --- |
| final | equidistant | 49,635 → 49,672 (1.001) | 35,547 → 35,557 (1.000) | 2,314 → 2,316 (1.001) | 484 → 526 (1.086) | 182 → 183 (1.005) | 1,375 → 1,373 (0.999) |
| final | equisolid | 50,081 → 50,127 (1.001) | 35,724 → 35,734 (1.000) | 2,341 → 2,343 (1.001) | 488 → 487 (0.998) | 141 → 142 (1.007) | 430 → 462 (1.074) |
| final | stereographic | 52,148 → 52,174 (1.001) | 36,852 → 36,856 (1.000) | 2,446 → 2,451 (1.002) | 513 → 476 (0.928) | 153 → 154 (1.005) | 473 → 453 (0.957) |
| final | orthographic | 31,556 → 31,652 (1.003) | 22,573 → 22,614 (1.002) | 1,491 → 1,500 (1.006) | 512 → 492 (0.961) | 111 → 112 (1.010) | 450 → 482 (1.071) |
| final | rectilinear | 37,338 → 37,299 (0.999) | 26,611 → 26,577 (0.999) | 1,723 → 1,722 (0.999) | 1,210 → 1,225 (1.012) | 114 → 115 (1.001) | 1,193 → 1,249 (1.047) |
| tuning | equidistant | 17,043 → 17,078 (1.002) | 10,545 → 10,545 (1.000) | 3,181 → 3,183 (1.001) | 484 → 482 (0.997) | 349 → 350 (1.004) | 1,303 → 1,359 (1.043) |
| tuning | equisolid | 16,635 → 16,680 (1.003) | 10,240 → 10,240 (1.000) | 2,986 → 2,992 (1.002) | 483 → 498 (1.033) | 300 → 302 (1.006) | 427 → 454 (1.062) |
| tuning | stereographic | 18,102 → 18,114 (1.001) | 10,888 → 10,888 (1.000) | 3,052 → 3,059 (1.002) | 490 → 503 (1.026) | 355 → 355 (1.002) | 453 → 436 (0.965) |
| tuning | orthographic | 10,094 → 10,122 (1.003) | 6,226 → 6,226 (1.000) | 1,941 → 1,943 (1.001) | 495 → 494 (0.998) | 224 → 227 (1.011) | 443 → 448 (1.011) |
| tuning | rectilinear | 12,345 → 12,330 (0.999) | 7,069 → 7,058 (0.998) | 1,740 → 1,738 (0.999) | 1,171 → 1,162 (0.992) | 250 → 250 (1.000) | 991 → 996 (1.005) |
| | **min B/A** | 0.999 | 0.998 | 0.999 | 0.928 | 1.000 | 0.957 |
| | **max B/A** | 1.003 | 1.002 | 1.006 | 1.086 | 1.011 | 1.074 |

Allocation moves by at most 1.1%. Peak working set is the process's cumulative peak at the end of the report, so it
moves with garbage-collection timing; it varies from 7.2% lower to 8.6% higher, in both directions.

#### Optical-calibration fit 05

In the A/B's B1 run, the held-out stereographic optical-calibration session fit for row 05 took 20,275 ms. Its #1126
limit is 19,541 ms. At A the same row took 16,768 ms.

**Diagnostic: reproduced.** The #520 coordinator registered the rule at 06:57:49Z, before any data: "reproduced if any
B row 05 > 19541 ms, or row-05 B/A > 1.10 in at least 2 of the 3 adjacent pairs". Six alternating runs of that fit ran
in one session on hvo-dev-03 under its heavy lock, from 08:21:39Z to 08:35:51Z. Every run exited 0 with no failures.

| Run | Arm | Start (UTC) | Row 01 wall/CPU ms | Row 05 wall/CPU ms | Row 09 wall/CPU ms |
| --- | --- | --- | --- | --- | --- |
| 1 | A | 08:21:39 | 20,373/20,467 | 16,891/16,974 | 22,062/22,284 |
| 2 | B | 08:24:10 | 25,910/26,081 | 19,690/19,842 | 19,574/19,702 |
| 3 | A | 08:26:45 | 21,742/21,886 | 16,815/16,898 | 17,802/17,897 |
| 4 | B | 08:29:04 | 22,138/22,252 | 17,326/17,414 | 17,128/17,230 |
| 5 | A | 08:31:20 | 21,842/21,952 | 17,495/17,580 | 16,455/16,539 |
| 6 | B | 08:33:37 | 22,069/22,188 | 17,333/17,420 | 16,093/16,194 |

Row-05 B/A in the adjacent pairs is 1.166, 1.030 and 0.991. Run 2's 19,690 ms exceeds the limit, so the rule reports
reproduced. Run 2 (08:24:10Z–08:26:45Z) overlapped #1168's gate tests on hvo-dev-02, and that session held only
hvo-dev-03's lock.

**Attribution: B did not recur.** The coordinator registered the rule at 08:42:10Z, again before any data.

- Arms:
  - A = `0639e27d`
  - B = `d3b78737`
  - G = `bfae8d7a`, which is `d3b78737` with only `Evaluate`'s isolation lines restored to the `0639e27d` text.
- Design: three Latin-square rounds, A B G | B G A | G A B, on hvo-dev-03 under its heavy lock. A holder process held
  hvo-dev-02's heavy lock from 08:57:43Z to 09:17:09Z.
- R(X) is true when any X row-05 trial exceeds L = 19,541 ms, or X/A in the same round exceeds 1.10 in at least 2 of the
  3 rounds.
- If R(G) is false, the grid is reverted.

| Run | Round | Arm | Window (UTC) | Row 01 wall/CPU ms | Row 05 wall/CPU ms | Row 09 wall/CPU ms | Row 05 alloc MB |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | A | 08:58:04–09:00:07 | 19,744/19,824 | 15,492/15,564 | 15,289/15,361 | 16,841 |
| 2 | 1 | B | 09:00:08–09:02:13 | 20,521/20,624 | 16,054/16,115 | 15,469/15,518 | 16,849 |
| 3 | 1 | G | 09:02:14–09:04:20 | 20,444/20,519 | 16,016/16,111 | 15,639/15,704 | 16,841 |
| 4 | 2 | B | 09:04:21–09:06:28 | 20,476/20,567 | 15,997/16,051 | 15,772/15,837 | 16,849 |
| 5 | 2 | G | 09:06:28–09:08:35 | 20,471/20,557 | 15,972/16,044 | 15,747/15,822 | 16,841 |
| 6 | 2 | A | 09:08:35–09:10:43 | 20,778/20,853 | 16,178/16,237 | 15,802/15,863 | 16,841 |
| 7 | 3 | G | 09:10:44–09:12:50 | 19,894/19,964 | 16,030/16,087 | 15,943/16,030 | 16,841 |
| 8 | 3 | A | 09:12:51–09:14:57 | 20,518/20,594 | 15,928/15,980 | 15,784/15,851 | 16,841 |
| 9 | 3 | B | 09:14:58–09:17:05 | 20,502/20,583 | 15,944/16,007 | 15,784/15,878 | 16,849 |

Every run exited 0 with no failures, the same settings identity and the holder's lock held at its start and end.

- No row-05 trial exceeds L.
- Row-05 B/A by round: 1.036, 0.989, 1.001.
- Row-05 G/A by round: 1.034, 0.987, 1.006.

R(B) and R(G) are both false, so the grid is reverted. B did not recur in a quiet session; reverted under the
07:09:49Z outcome-3 rule. The slowdown is not attributed to the grid or to any other change. G's runs are the
post-revert no-regression confirmation against A. G's source differs from the merge head's only by the unused
`IsolatedIds` method and by the selection-profile visibility and `ReadAsync` changes made after `d3b78737`.

### Magnitude 7

The complete 15,598-row selection at the solver default is evaluated through an internal overload that admits it,
with the default search and 15 s cold budget, every family and view, 2026-01-15T08:00Z, seed 116771 and render
7.0/32,768. It is recorded and never advertised.

**Result: no view is accepted on any family.**

| Family | Views | Accepted | Timed out (15 s) | Rejected | Slowest completed solve ms | Peak working set MiB |
| --- | --- | --- | --- | --- | --- | --- |
| equidistant | — | — | — | — | — | — (detector budget, no report) |
| equisolid | 6 | 0 | 6 | 0 | — | 372 |
| stereographic | 6 | 0 | 6 | 0 | — | 375 |
| orthographic | 6 | 0 | 6 | 0 | — | 395 |
| rectilinear | 6 | 0 | 0 | 6 | 10,949 | 1,206 |
| rectilinear 8 mm | 6 | 0 | 0 | 6 | 6,604 | 1,157 |

The fisheye solves allocated up to 12.1 GiB each before the 15 s budget stopped them. The rectilinear solves finished
inside the budget with a 6,256-star index. Every rejection was "Independent verification, residual, count, or
coverage gates failed". No false association was recorded.

## Evidence

The evidence is bound to the measured revision `d3b78737`. Its harness sources and scripts, as frozen at
2026-10-07T03:58:45Z (`freeze.json`), are these git blobs:

| Path | Blob at `d3b78737` |
| --- | --- |
| `docs/validation/issue-1167-qualification-manifest.json` | `cc6a96e0dca8f978b1f319efcce198c68bdb33e1` |
| `docs/validation/issue-1167-qualification.sh` | `0663ff0263dbca4e64c54dadc4cad52e2c784bbf` |
| `docs/validation/issue-1167-ab.sh` | `c87d1a32a8b61ff2b21247084cc054a0cf227da1` |
| `docs/validation/issue-1167-evaluate.py` | `f41a084c78fdda0280e6a5cac56b2bcbfc40d6df` |
| `docs/validation/issue-1167-measure.sh` | `27f8eb2682edcc65415d13f99a46d678f0c255b7` |
| `docs/validation/issue-1126-qualification.sh` | `c79d311769cda82b94f4eda25ffec714222144c0` |
| `docs/validation/issue-1126-qualification-manifest.json` | `4babc9a331ba5583b45d514ee27c3177b7162af6` |
| `tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualAstrometryFixture.cs` | `1dfbe2e12f85b3db0b387413aec87ddd6579ba96` |
| `tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualAstrometryQualificationTests.cs` | `66d41934777052b64d9c19e3a6abed2b2fd10ecd` |
| `tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualAstrometryReference.cs` | `b41ebf0307c997447c6ee8a65c98c338b0f1760b` |
| `tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualDeepAstrometryQualificationTests.cs` | `5803658a81333e1d782f3df59acf6563bf1328f8` |

Since `d3b78737` only the deep harness has changed, and neither change alters what it measures:

| Revision | Harness blob | Change |
| --- | --- | --- |
| `d3b78737` (measured) | `5803658a81333e1d782f3df59acf6563bf1328f8` | — |
| `2151f8c5` | `69cf273e16ee6436b871493e7724fa7bc021ea31` | Sets the profile through the internal `WithCatalogSelectionProfile`; two hunks |
| isolation revert | `42aa5b1125927cf477e112bda404aca49e2dfc15` | Deletes the isolation timing (`IsolationTiming`, the `isolation` report field); deletions only |

Every other path in the table above is byte-identical at the merge head. The evidence pack holds both diffs under
`equivalence/`.

The evidence pack is kept outside the repository under the #520 evidence root, at `evidence/1167/d3b78737/`, with a
`SHA256SUMS` manifest. It holds the tuning, held-out, magnitude-7 and A/B reports and logs, `decision.json`,
`decision-ruling.json`, `freeze.json`, `steps.json` and the fit-05 diagnostic and attribution sessions.

### Evidence completeness check

Review PR-1181-R0 found three ways the frozen evaluator can pass incomplete evidence (F1–F3):

- G0 takes the expected views from the report itself.
- The A/B comparison lists only the report files that are present.
- G4 accepts a bare p95, and G8 skips a missing `indexPrefix`.

The frozen manifest and evaluator stay unchanged, and they still decide. A separate script,
`docs/validation/issue-1167-evidence-check.py` (SHA-256
`aa1524c202059829fd17605906d45860091f68cbe9ef8805fc07e9b45c812040`), checks that the evidence they decided on is
complete.

Its expected inventory comes only from committed sources, and the script quotes each source line at `d3b78737`:

- the frozen #1167 manifest;
- the #1126 manifest;
- the #1126 and #1167 runner scripts;
- the harness sources.

The script then finds each quoted declaration again in the sources the pack itself records. For a final pack these are
the harness blobs in its index. For an A/B pack they are the scripts at the A and B revisions.

**`deep` mode.** A final pack is complete when all of the following hold:

- its runs are exactly the manifest matrix;
- every report hashes to its index entry;
- every family, view, held-out date and capture appears exactly once, with its declared view, capture and UTC;
- `selection` holds 30 finite samples after 5 warm-ups, and their nearest-rank p95, computed with the harness's own
  definition, equals `p95Ms` exactly;
- every perspective cold row carries an `indexPrefix` whose `boundBytes` equals the harness formula.

A run with no report must be an `error` run. It is listed rather than counted as incomplete, because the frozen
evaluator already makes every decision that depends on it undecidable.

**`ab` mode.** An A/B pack is complete when all of the following hold:

- each arm holds exactly the #1126 run and report inventory at its declared revision;
- every report hashes to its index entry;
- the B2 and A2 pixel reports are present for every family.

**Declaration order.**

1. The check was committed and pushed before it was run on any measured pack. Before that it was tested only on
   synthetic packs built from its declared constants.
2. It was then run on the `d3b78737` final and A/B packs.
3. Finally, the reviewer's F1–F3 triggers were applied to scratch copies of those packs, outside the repository.

The commit, the results and the trigger runs are recorded below once they exist.

**Disclosure.** Before the check was written, while verifying the r0 findings, the #1167 owner inspected the `d3b78737`
final reports: their view names, a recomputed p95, and whether `indexPrefix` was present and matched the bound formula.
The check's constants do not come from that inspection. They come only from the committed sources it quotes.
