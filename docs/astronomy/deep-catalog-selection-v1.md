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

The record for each of these claims:

- **Host.** `freeze.json` records `host.name` `hvo-dev-03`. So do the `host` fields of the 85 reports that carry one:
  34 held-out, 36 tuning, 5 magnitude-7 and 10 A/B resource reports.
- **Times.** They come from `steps.json`.
- **Lock.** `freeze.json` declared before the run that `/tmp/hvo-520-heavy.lock` was held by `flock -o` for the whole
  measured run. No `/proc/locks` line from the run is retained. That the lock was held is the #1167 owner's account.

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

**Records.** Some of this section rests on no retained record:

- The `rsync` overlap and its times are the #1167 owner's account.
- So is the shared physical host, which is an inference. The A/B arms' `index.json` host blocks record the CPU model
  and 8 logical processors but no host name, and nothing records the hypervisor.
- #1168's times are as its owner's ledger records them.
- The attribution session's locks are recorded as described under [Attribution](#optical-calibration-fit-05).

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

| Family | Cold solves | A1 ms | B1 ms | B2 ms\* | A2 ms\* | B1/A1 | B2/A2\* |
| --- | --- | --- | --- | --- | --- | --- | --- |
| equidistant | 30 | 2,490 | 2,166 | 2,135 | 2,141 | 0.870 | 0.997 |
| equisolid | 18 | 2,327 | 2,242 | 2,290 | 2,223 | 0.963 | 1.030 |
| stereographic | 18 | 2,333 | 2,400 | 2,326 | 2,356 | 1.029 | 0.987 |
| orthographic | 18 | 1,887 | 1,840 | 1,770 | 1,837 | 0.975 | 0.964 |
| rectilinear | 18 | 1,794 | 1,899 | 1,849 | 1,780 | 1.059 | 1.039 |

\* Recorded, inputs unarchived. These pair-2 values appear only in the sealed `ab-decision.json`. The B2 and A2 pixel
reports they were computed from are not in the evidence pack (see
[Evidence completeness check](#evidence-completeness-check)).

Medians of held-out cold solves. The verdict rests on the archived pair 1. Every family's B1/A1 is at most 1.10; the
largest is rectilinear's 1.0586. The both-pairs rule therefore cannot fire, whatever pair 2 holds. Rectilinear is the
only family slower in both pairs: by 5.9% in pair 1, and by 3.9% in pair 2 (recorded, inputs unarchived).

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

The six runs' TRX directory names (`_hvo-dev-03_…`) record the host. The session's lock, and that it held no
hvo-dev-02 lock, are the #1167 owner's account; no `/proc/locks` line from it is retained.

**Attribution: B did not recur.** The coordinator registered the rule at 08:42:10Z, again before any data.

- Arms:
  - A = `0639e27d`
  - B = `d3b78737`
  - G = `bfae8d7a`, which is `d3b78737` with only `Evaluate`'s isolation lines restored to the `0639e27d` text.
- Design: three Latin-square rounds, A B G | B G A | G A B, on hvo-dev-03 under its heavy lock. A holder process held
  hvo-dev-02's heavy lock from 08:57:43Z to 09:17:09Z.
- Records:
  - The nine runs' TRX directory names (`_hvo-dev-03_…`) record the host.
  - hvo-dev-03's own lock is the #1167 owner's account.
  - The holder's times are its `acquired.utc` and `released.utc` in `attr-fit05/hvo-dev-02-holder/`.
  - `attr-fit05/session/attribution.json` records the holder's `/proc/locks` line (`procLocksLine`) at each run's
    start and end: 18 lines.
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

Every run exited 0 with no failures and the same settings identity. The holder's lock was held at each run's start and
end, according to its `procLocksLine` records.

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
inside the budget and were all rejected. Every rejection was "Independent verification, residual, count, or coverage
gates failed". No false association was recorded.

Their recorded `indexStars` maximum of 6,256 is the number of eligible training stars, not the size of an index. The
report takes `indexStars` from `CatalogIndexStars` (`VirtualDeepAstrometryQualificationTests.cs:340`). On a
rejection, the solver fills that field with `training.Count`, as `AstrometricSolverCore.cs:120` shows:

```csharp
CoreResult Reject(string reason, CoreQuality? q = null, int candidates = 0) => new(false, "rejected", reason, null, null, q, [], training.Count, ...);
```

The index the solver actually builds is capped at 1,500 stars (`MaximumIndexStars`, `:72`, enforced at `:132`). Its
size is not recorded on this path.

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

Some A/B files are unarchived:

- **The B2 and A2 pixel reports.** Their values are recorded only in the sealed `ab-decision.json`.
- **Every per-run `test.log` and `run.trx` of the four #1126 arms.** Each arm's `index.json` still records every TRX
  file's SHA-256, total, executed, passed and failed counts. Neither the evaluator nor the evidence check reads the
  files themselves. The check reads only the counts the index records.

[Evidence completeness check](#evidence-completeness-check) records the cause and the disposition.

### Evidence completeness check

Review PR-1181-R0 found three ways the frozen evaluator can pass incomplete evidence (F1–F3):

- G0 takes the expected views from the report itself.
- The A/B comparison lists only the report files that are present.
- G4 accepts a bare p95, and G8 skips a missing `indexPrefix`.

The frozen manifest and evaluator stay unchanged, and they still decide. A separate script,
`docs/validation/issue-1167-evidence-check.py`, checks that the evidence they decided on is complete. It has two
versions:

- `3cb05cf9` (SHA-256 `aa1524c202059829fd17605906d45860091f68cbe9ef8805fc07e9b45c812040`), described from here to
  [Correction after PR-1181-R1](#correction-after-pr-1181-r1);
- `a8f09a0a` (SHA-256 `4da7f452d4a05fe4ec0efc351444bf7b23e15274f411ebc3b18b941eee471a8d`), which also checks each A/B
  report's revision and content and is described in that subsection.

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

The check was committed as `3cb05cf9` and pushed at 2026-10-07T14:47:06Z. It ran on the measured packs from
14:47:12Z to 14:47:16Z.

**Results.**

| Mode | Exit | Result |
| --- | --- | --- |
| `deep` | 0 | Complete, 0 findings. 34 reported runs hold all 1,944 declared case rows and 288 bounded perspective cold rows. All 34 p95 values are exact. All 19 quoted declarations were found. |
| `ab` | 1 | Incomplete, 10 findings: the B2 and A2 pixel report is missing for each of the 5 families. The four arms are complete: 25, 20, 25 and 20 runs; every report hashes to its index; all 17 quoted declarations were found. |

In `deep` mode, `final-equidistant-d55` and `final-equidistant-d60` are listed as `error` runs with no report. This is
consistent with the standing undecidable-G0 ruling (see [Decision](#decision)).

The check was not changed to pass the `ab` pack. Its result stands as recorded.

**Disposition of the `ab` result.** Pair-2 (B2/A2) pixel-pair inputs were not archived. Their values are recorded
only in the sealed `ab-decision.json`. The verdict rests on archived pair 1, in which every family is ≤ 1.10 (max
rectilinear 1.0586), so the both-pairs rule cannot fire whatever pair 2 holds.

**Pair 1 recomputed.** A script recomputed every family's A1 and B1 cold medians and B1/A1 ratio from the archived
`a1-final` and `b1-final` reports. It loads the frozen evaluator's own `load` and `cold_median` from the `d3b78737`
blob, verified against SHA-256 `aa028b40b6595b1583ce4c52072d7a0b7a2f2ed11a4df54118ec8d185d068ff8` and run unmodified.
It reads each input only after checking it against the pack's `SHA256SUMS`.

All 15 values are exactly equal to `ab-decision.json`. The largest ratio is rectilinear's 1.0586199732301054, and none
exceeds 1.10. The script and its output are in `evidence/1167/3cb05cf9/pair1-recompute/` (`SHA256SUMS` sha256
`91b57453a97fec331d3bc2f00895ea0041070ce0fc2da2b1a4cd7d8b992c7d02`).

**Cause.** The evidence transfer (`transfer-record/copy-evidence.sh`, `SHA256SUMS` sha256
`f65f9d8e57c8d5b029cbb1a9c81740e8ac3fc9f608241d8db779ca8cd3f85adb`) was a whitelist. It had no rule for `ab/a2/`,
`ab/b2/` or the four A/B arms' `runs/` files, so those files were never copied; its file list, `pack-files.txt`,
names none of them. The empty `a2/` and `b2/` directory trees most likely remain from an earlier interrupted copy,
which is not archived.

Future measured packs are copied as a whole tree, not through a whitelist. A copy counts as archived only after it
verifies complete against a source-side `find -type f | sha256sum` manifest and the evidence check passes on the
archived copy.

**Trigger runs.** The reviewer's triggers were applied to scratch symlink-tree copies of the posted packs in `/tmp`,
outside the repository, at HEAD `3cb05cf9`. They ran from 2026-10-07T14:56:53Z to 14:58:36Z.

`trigger.py` rewrites each edited report as a new file and re-indexes its SHA-256 and size. A stale hash therefore
never reveals a trigger.

Neither real pack lets the frozen evaluator reach a clean pass, so "the evaluator passes" is shown as "the evaluator's
outcome is unchanged from its own baseline":

- The `deep` baseline is undecidable (exit 1). Its scratch `decision.json` is byte-identical to the archived one.
- `ab()` raises `FileNotFoundError` at the first pair-2 read. The A/B triggers are therefore also run through the
  frozen `compare_reports`, exactly as `ab()` calls it.

| Trigger | Change | Frozen evaluator | Evidence check |
| --- | --- | --- | --- |
| None, `deep` | — | Exit 1, undecidable | Exit 0, complete |
| F1 | `mono-bin2` rows, and their failing cases and failures, removed from the 5 equisolid runs | Exit 1, same undecidable list, no G0 finding. Equisolid d55 turns from failing G1 to passing (6 failing cases to 0); m55 falls from 14 to 6, m60 from 17 to 8, d60 from 9 to 3 | Exit 1, 10 findings: each run has "45 case rows, declared 54" and "9 declared cases missing" |
| F3a | `selection` set to 0 warm-ups, 0 samples, p95 0 in all 34 runs | Exit 1, unchanged; every `selectionP95Ms` is 0 and no G4 finding | Exit 1, 102 findings |
| F3b | `indexPrefix` set to null on the cold rows of the 16 rectilinear runs | Exit 1, unchanged; `maxIndexPrefixBytes` becomes null and no G8 finding | Exit 1, 288 findings: each row "has no index-bound evidence" |
| None, `ab` | — | `ab()` raises at pair 2; `compare_reports` identical over 45 reports | Exit 1, the 10 pair-2 findings |
| F2 | `optical-calibration-held-out-stereographic` report deleted from `a1-final` and `b1-final` | `ab()` raises at pair 2; `compare_reports` identical over 44 reports | Exit 1, 14 findings: the 10 above, plus "is missing" and "report files differ from the indexed reports" for each arm |
| F2, unindexed | The same report directory deleted, and its run removed from each arm's index | `ab()` raises at pair 2; `compare_reports` identical over 44 reports | Exit 1, 12 findings: the 10 above, plus "runs are not the #1126 inventory: missing [...]" for each arm |

The posted `d3b78737` pack verified 428 of 428 entries afterwards, with no file newer than the run's start. The tools,
outputs and decisions are in `evidence/1167/3cb05cf9/discrimination/` (`SHA256SUMS` sha256
`488c5cea40f3036ebca6e31072db2e707c3db119a4337c7f66b03e264dfc56ae`).

**F2 on the full `ab()` (synthetic).** This run is **SYNTHETIC: discrimination only, not performance evidence.** It
shows that F2 reaches `ab()`'s verdict, and not only `compare_reports`.

In each scratch copy, every empty pair-2 family directory was given one symlink to that family's pair-1 report: B2 to
`b1-final`, A2 to `a1-final`. Each pair-2 ratio therefore equals its pair-1 ratio. The run took place from
2026-10-07T15:03:36Z to 15:04:53Z.

| Trigger | Frozen `ab()` | Evidence check |
| --- | --- | --- |
| None | Exit 0: "identity identical over 45 reports; regression none; verdict pass" | Exit 0, complete |
| F2 | Exit 0: "identity identical over 44 reports; regression none; verdict pass" | Exit 1, 4 findings: "is missing" and "report files differ from the indexed reports" for each arm |
| F2, unindexed | Exit 0: "identity identical over 44 reports; regression none; verdict pass" | Exit 1, 2 findings: "runs are not the #1126 inventory: missing [...]" for each arm |

The posted pack again verified 428 of 428 entries, with no file newer than the start. The run is in
`evidence/1167/3cb05cf9/discrimination-synthetic/` (`SHA256SUMS` sha256
`a0f5a702a59f952b4125df5560e31026a3228981cfe80839dfca865dec2d3719`).

**Disclosure.** Before the check was written, while verifying the r0 findings, the #1167 owner inspected the `d3b78737`
final reports: their view names, a recomputed p95, and whether `indexPrefix` was present and matched the bound formula.
The check's constants do not come from that inspection. They come only from the committed sources it quotes.

#### Correction after PR-1181-R1

Review PR-1181-R1 covered `d812c139`. It verified F1, F3 and F4 and found F2 only partly fixed, through three new
findings:

- **R1-F1 (High).** An A/B arm report whose `revision` was absent or null was exempt from the revision comparison.
- **R1-F2 (High).** A report that was present and matched its index entry passed whatever it held. The frozen evaluator
  compares only identity leaves and association lists. So nulls, `{}` or empty lists written alike in both arms
  compare identical and pass both the evaluator and the check.
- **R1-F3 (Low).** `indexPrefix.admittedIndexStars` was not checked.

The corrected check is `a8f09a0a` (SHA-256 `4da7f452d4a05fe4ec0efc351444bf7b23e15274f411ebc3b18b941eee471a8d`). Like
the first version, it takes every rule from committed source and quotes each source line. A declaration that differs
between A and B is either one of the declared B-only producers below or a finding.

**Revision (R1-F1).** Every A/B report must serialize `revision`, and the value must equal the revision its arm
declares: A for `a1-*` and `a2`, B for `b1-*` and `b2`. An absent or null revision is a finding. The report's
top-level keys must be exactly its writer's. Its `schema`, `projectionFamily`, `partition` and, for uncertainty
reports, `realizationsPerProfile` must be the declared values. Three new A/B declarations quote the A/B runner's
revision capture, environment and test filter (`issue-1167-ab.sh` :47, :50, :52 at B). They bind the B2 and A2 pixel
reports to their revision. The check now quotes 20 A/B declarations, up from 17.

**Content (R1-F2).** Every arm report and every B2/A2 pixel report is checked against its declared writer:

- **Declarations.** The check quotes 218 content declarations, each with its line at A and at B, and must find every
  one at both revisions. They cover:
  - serialization conventions;
  - the profile and view inventory;
  - the pixels, reference-scorer, optical, uncertainty, measured-stars and resources writers;
  - the optical session, its contracts and the uncertainty estimator;
  - the fixture's identity producers;
  - the solver and solver core;
  - the echo sites and early-return forms. These include `OpticalCalibrationSession`'s `Diagnostics` class, which is
    identical at A :440 and B :442.
- **Inventory.** Case ids, views, captures, UTCs, readouts and realizations appear in writer order.
- **Keys.** Every object the writer serializes has exactly the writer's keys.
- **Echoes.** Every unaccepted or missing item has its writer's failure line (PIX :68, :74; OPT :145, :178; UNC :203,
  :282, :295, :298; MEAS :84).

**Identities.** Every key ending in `identitySha256` must sit at a path whose producer is quoted. An identity at any
other path is a finding. Every declared identity must be serialized wherever its writer writes it; the writer
serializes nulls (FIX :62). Each identity has one of two forms:

- **L**, `[0-9a-f]{64}`. `AstrometricIdentity` and `SettingsIdentity` produce it through `Convert.ToHexStringLower`
  (AC B:239, B:241; SSM :242). Every identity not listed under U is L.
- **U**, `[0-9A-F]{64}`. `CaptureContractJson.ComputeCanonicalJsonSha256` produces it through `Convert.ToHexString`
  (CCJ :56), and it is serialized as produced:
  - U1: pixels `assessment.frame.observerIdentitySha256` (FIX :319);
  - U2: pixels `assessment.frame.detectionSettingsIdentitySha256` (FIX :320);
  - U3: optical `fit.frames[].readoutIdentitySha256` (OPT :104; SESS :87, B:619).

The uncertainty session's `sharedCalibrationIdentity` does not end in `identitySha256`. It is checked as L by name
(UNCS :93).

An identity may be null only under its rule:

| Rule | Identity | Null if and only if |
| --- | --- | --- |
| N1 | pixels `assessment.previousAssessmentIdentitySha256` | `mode` is `Blind` (SOL :44, :47, B:118) |
| N2 | pixels `assessment.associationIdentitySha256` | `status` is not `Accepted` (SOL B:118; every non-accepted core result carries `[]`) |
| N3 | uncertainty `rows[].sharedCalibration.calibrationCovarianceIdentitySha256` | `reasonCode` is `shared-calibration-covariance-not-supplied` (UNCS :303, :306, :309) |
| N4 | uncertainty `frames[].identitySha256` | never (UNC :224; UNCS :244) |
| N5 | the uncertainty estimate's nullable `ReadoutIdentitySha256` (UNCS :150) | no rule: the writer does not serialize it, so it needs no allowance |

**N3 disclosure.** UNCS :251 also yields a null covariance identity under another reason code, but only when
`sharedCalibration` is null. The session path never passes null, because UNC :280 returns before it. N3 therefore
holds vacuously for that branch.

**Case of the U identities.** U1–U3 are upper case, and every other identity is lower case. Normalizing them would be
a product change outside #1167. The #520 coordinator ruled that it goes to the #520 backlog. The check accepts each
identity only in its producer's form.

**Associations.** Every serialized association list or count must sit at a path with a quoted writer:

- **Lists.** A pixels `score.associationRows` list or an optical `withheldScores[].score.associationRows` list must be
  non-empty. An empty list is a finding. Every row has the REF :208 shape. A list has at least 12 fitting and 4
  withheld rows, the solver's acceptance minimum (CORE B:419, `if (fit.Count < 12)` and `if (held.Count < 4)`).
- **Measured counts.** `v1.associations` must equal `v1.baseline.associations`, and `v2.associations` must equal
  `v2.candidate.associations`. A count is 0 if and only if its solve is not `Accepted`. An accepted count is at least
  16, the same minimum, 12 plus 4.

**B-only producers.** `d3b78737` adds three producers that `0639e27d` cannot write, and one value declaration. Each
must be found at B and absent at A:

| Declaration | Status, reason code | Text |
| --- | --- | --- |
| SOL B:50 | `Unavailable`, `catalog-selection-unsupported` | "Catalog selection exceeds the {bound}-entry bound of the requested selection profile; no fit was attempted." |
| SOL B:84–87 | `BudgetExceeded`, `resource-limit`, budget exhausted | "Accepted evidence exceeds the {MaximumEvidenceStars}-star assessment bound; no proposed mapping replaces last-good evidence." |
| SESS B:95–96 | optical session `Unavailable`, `catalog-selection-unsupported`, rejections `["catalog-selection-unsupported"]` | SOL B:50's text |
| MAP B:77 | — | `internal const int MaximumEvidenceStars = 2500;`, a value declaration, not a producer |

The check derives the code, both texts, the 2,500 bound and the 12/4 minimum from these quoted literals and CORE
B:419 only.

Any other A/B difference would be a finding. The over-bound narrow index needs no allowance either. `IndexPrefix`
returns `maximumStars + 1` entries, so a B-arm over-bound narrow index holds 1,501. The core `Reject` serializes
`training.Count`, and `triangleCount` is 0 there, so no serialized value differs.

**Marker rule.** A marker is any string that contains `catalog-selection-unsupported`, `Catalog selection exceeds the `
or `Accepted evidence exceeds the `:

- **A arm.** Any marker is a finding.
- **B arm.** A marker is allowed only in its producer's exact shape, echoed only at its writer sites. Any other marker
  is a finding.

The shapes are:

- **Pixels, SOL B:50.** `status` `Unavailable`. `reason` is the B:50 text with an integer bound. `score`,
  `parameters`, `quality` and `associationIdentitySha256` are null. The metrics have `budgetExhausted` false and every
  count 0. The row has its PIX :74 echo.
- **Pixels, SOL B:84–87.** `status` `BudgetExceeded` and `reasonCode` `resource-limit`. `reason` is exactly the B:85
  text with 2,500. The same members are null, and the metrics have `budgetExhausted` true. The row has its PIX :74
  echo.
- **Measured stars, SOL B:50.** A v1 or v2 solve with this code is `Unavailable` with 0 associations. A v2 solve also
  needs its MEAS :84 echo.
- **Measured stars, SOL B:84–87.** This solve is recognized only through v2's MEAS :84 echo. With that echo, it must
  be `BudgetExceeded` and `resource-limit` with 0 associations. The measured-stars report serializes a solve's status,
  reason code and association count, but not its reason (MEAS :121, :129–140). Without the echo, a SOL B:84–87 solve is
  indistinguishable from the solver's ordinary resource-limit path (SOL A:91), which A also writes.
- **Optical, SESS B:95–96 fit.** The fit is `Result` (SESS :82–88) returned before any work, with every OC :82, :86
  and :99 member at its early value. The members are:
  - `status` `Unavailable` and `rejections` `["catalog-selection-unsupported"]`;
  - `calibratedNative` null and `validations` `[]`;
  - four `frames`, each `not-attempted`, with `acquisitionCandidates`, `fittingStars` and `verificationStars` 0, and
    with `boresightAltitudeDegrees`, `boresightAzimuthDegrees`, `rollDegrees`, `fittingRmsPixels` and
    `verificationRmsPixels` null;
  - four `parameters`, each with `standardError` null and `atBound` false;
  - `diagnostics` with a numeric `skyRotationDegrees`, with `fittedFrames`, `fittingStars`, `verificationStars`,
    `occupiedRadialBins`, `occupiedAzimuthBins` and `iterations` 0, with `fittingRmsPixels`, `verificationRmsPixels`
    and `conditionNumber` null, with `converged` false and with `residualBins` `[]`;
  - in the report, `withheldScores` `[]`, `errors` null and the OPT :145 echo.

  The optical report does not serialize covariance, so no covariance member is checked.
- **Optical, `omittedDistortion`.** A marker here is allowed only beside a SESS B:95–96 fit. It is `Unavailable`,
  with the same rejections and early diagnostics, and it has its OPT :139 echo when the truth `k1` is 0.
- **Uncertainty.** A SESS B:95–96 session is the short form (UNC :280) with status `Unavailable` and its UNC :282
  echo. The solver echoes at UNC :203 and :295 and OPT :178 are allowed when the echoed remainder is a SOL B:50 or
  B:85 text.

**Writer sites.** The writer sites are the lines that echo a solver or session reason, reason code or rejection into
`failures`. This command finds them:

```
git grep -n -E 'failures\.Add\(\$"[^"]*\{[^}]*(Reason|ReasonCode|Rejections)\b' <rev> -- \
  tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualAstrometryQualificationTests.cs \
  tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualOpticalCalibrationQualificationTests.cs \
  tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualAstrometricUncertaintyQualificationTests.cs \
  tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualMeasuredStarQualificationTests.cs
```

It returns 11 lines at both A and B:

- **The 8 writer sites:** PIX :74, OPT :139, :145 and :178, UNC :203, :282 and :295, and MEAS :84.
- **3 lines that cannot carry a marker, so a marker there is a finding:**
  - UNC :99 writes an accepted frame's withheld and estimator reason codes.
  - OPT :162 writes validation lines. Only an attempted fit has validations, and a SESS B:95–96 fit has `[]`.
  - UNC :300 writes the uncertainty total's reason codes.

**Index bound (R1-F3).** The `deep` declarations rise from 19 to 22. The check now quotes the full `indexPrefix`
assignment, which passes `solved.Metrics.IndexStars` (harness :167–168), and the solved row's
`solved.Assessment, solved.Metrics,` (:189–190). It also quotes the `IndexPrefixEvidence` record (:504),
`IndexPrefixBound(…, int admitted)` (:510) and its whole `return` (:516). An `indexPrefix` must hold exactly
`trainingStars`, `admittedIndexStars` and `boundBytes` as nonnegative integers. `admittedIndexStars` must equal the
row's own `metrics.indexStars`. The check fails closed: a missing or non-integer `indexStars` is a finding.

**Declaration order.**

1. `a8f09a0a` was committed at 2026-10-07T20:35:03Z and pushed at 20:35:04Z, according to the branch's
   remote-tracking reflog.
2. Before that, it ran only on the hand-made synthetic pack and its probes.
3. Its first run on a measured pack was the condition-5 run at 20:41:17Z. An earlier attempt at 20:40:42Z evaluated
   no pack (see the disclosures below).

**Self-test (synthetic).** Run at `a8f09a0a` from 20:43:03Z:

- The hand-made good pack passes in `deep` and `ab` with 0 findings.
- 105 probes each apply one defect to a copy of it, and all 105 exit 1.
- In the source probes, the A/B baseline gives 0 findings. Each of the four declaration defects gives findings:
  - B-only text present at A: 4;
  - B-only text absent at B: 4;
  - a declaration missing at both revisions: 2;
  - a declaration missing at A only: 1.

**Rewrap equivalence.** The long lines were wrapped mechanically before the commit. The wrap tool rebuilt the
committed file byte for byte from the pre-wrap check. Lines over the limit fell from 73 to 4. All 68 module constants
were loaded from both files and compared:

- `NULL_RULES` is the only one unequal by value. It holds lambdas, which never compare equal across two loads, and
  its source text is identical.
- The 218 `CONTENT_DECLARATIONS` rows are equal.

**Posted packs.** The packs are read only, and Python ran under `nice` and `ionice` with no lock.

| Pack | `a8f09a0a` check | `d812c139` check |
| --- | --- | --- |
| `final/` (`deep`) | Exit 0, complete, 0 findings. 34 runs hold 1,944 case rows and 288 bounded perspective cold rows. All 22 quoted declarations were found. `final-equidistant-d55` and `-d60` are listed as `error` runs. | Exit 0, complete, 0 findings |
| `ab/` | Exit 1, 10 findings, exactly the 10 pair-2 "pixel report missing" findings, none unexpected. Arms of 25, 20, 25 and 20 runs. All 20 A/B declarations were found. All 218 content declarations were found at A and at B. All four B-only declarations were found at B and absent at A. 90 reports were content-checked, 45 per revision: 20 pixels, 20 optical, 20 uncertainty, 20 measured-stars and 10 resources. None drew a finding, and no B-only producer appears in them. | Exit 1, the same 10 findings |

The archived pair-1 reports draw no content finding, so the `ab` disposition above stands unchanged: pair 2 is
unarchived and `ab` stays incomplete for pair 2.

**Triggers (synthetic).** **SYNTHETIC: discrimination only, not performance evidence.** Each case applies one change
to a scratch symlink-tree copy of a posted pack. `trigger2.py` rewrites each edited file as a new file and re-indexes
its hash and size. The `ab` copies give pair 2 the pair-1 reports, as in the F2 synthetic run above. Each case runs
the frozen evaluator, the `a8f09a0a` check and the `d812c139` check.

| Case | Change | Frozen evaluator | `a8f09a0a` | `d812c139` |
| --- | --- | --- | --- | --- |
| `baseline-ab` | — | Pass, identity identical over 45 reports | Exit 0, 0 findings | Exit 0, 0 findings |
| `baseline-deep` | — | Exit 1, the posted undecidable decision | Exit 0, 0 findings | Exit 0, 0 findings |
| `r1f1-b2-revision-null` | B2 equidistant `revision` null | Pass | 1 | Exit 1 |
| `r1f1-a2-revision-absent` | A2 stereographic `revision` removed | Pass | 2 | Exit 1 |
| `r1f1-arm-revision-null-both` | measured equisolid `revision` null in both arms | Pass | 2 | Pass |
| `r1f1-b1-revision-a` | a B1 tuning optical report given A's revision | Pass | 1 | Exit 1 |
| `r1f2-empty-association-rows-both` | a pixels row's `associationRows` set to `[]` in both arms | Pass | 4 | Pass |
| `r1f2-short-association-rows-both` | 11 fitting rows | Pass | 4 | Pass |
| `r1f2-association-row-shape-both` | `residualPixels` `"NaN"` | Pass | 4 | Pass |
| `r1f2-untraced-identity-both` | an identity added at `v1.settingsIdentitySha256`, an untraced measured-stars path | Pass | 4 | Pass |
| `symmetric-nulling` | pixels `nominal.readoutIdentitySha256` null in both arms | Pass | 4 | Pass |
| `zero-measured-count-both` | an accepted measured-stars count set to 0 | Pass | 2 | Pass |
| `drop-withheld-score-and-failure-both` | an optical withheld score and its OPT :178 echo removed | Pass | 2 | Pass |
| `drop-pixel-score-and-failure-both` | a pixels score and its echo removed | Pass | 4 | Pass |
| `lowercase-u1-both` | U1 written in lower case | Pass | 4 | Pass |
| `uppercase-lowercase-produced-both` | an L identity written in upper case | Pass | 8 | Pass |
| `n1-warm-null-both` | a warm row's previous-assessment identity null | Pass | 4 | Pass |
| `n2-accepted-null-both` | an accepted row's association identity null | Pass | 4 | Pass |
| `a-arm-row-code` | the SOL B:50 code on an accepted A pixels row | Pass | 2 | Pass |
| `a-arm-failure-text` | a UNC :203 failure with the SOL B:50 text added to an A uncertainty report | Pass | 1 | Pass |
| `a-arm-optical-fit-code` | an A optical fit given the SESS B:95–96 status, code, rejections and echo | Pass | 4 | Pass |
| `a-arm-uncertainty-session-code` | a SESS B:95–96 session echo added to an A uncertainty report's failures | Pass | 1 | Pass |
| `b84-signature-mapped` | a mapped B pixels row given the SOL B:84–87 status, code, text and echo | Pass | 12 | Pass |
| `b84-text-altered` | a B pixels row in the SOL B:84–87 shape with the bound in its text and echo changed to 2501 | Exit 1, identity changed, output-change | 4 | Pass |
| `b-fit-code-validations` | a B optical fit given the SESS B:95–96 code, keeping its validations and calibration | Pass | 5 | Pass |
| `b-fit-code-calibrated` | a B optical fit with every SESS B:95–96 early member except a non-null calibration | Exit 1, identity changed, output-change | 4 | Pass |
| `b-omitted-beside-ordinary-fit` | the SESS B:95–96 code in `omittedDistortion` beside an ordinary fit | Pass | 4 | Pass |
| `b-marker-outside-site` | the SOL B:50 text in a resources failure | Pass | 1 | Pass |
| `b84-measured-echo-without-shape` | a MEAS :84 B:85 echo added for an accepted v2 solve | Pass | 2 | Pass |
| `r1f3-admitted-negative` | `admittedIndexStars` and `metrics.indexStars` both −1 on `final-rectilinear-m50` | The posted undecidable decision | 1 | Pass |
| `r1f3-admitted-mismatch` | `admittedIndexStars` 285 against `metrics.indexStars` 284 | The posted undecidable decision | 1 | Pass |
| `r1f3-indexstars-null` | `metrics.indexStars` null | The posted undecidable decision | 1 | Pass |

The `a8f09a0a` column gives finding counts, and every one of those 30 trigger runs exits 1. The `d812c139` check
fails 3 of the 30, all on a revision. The frozen evaluator passes 25 of the 27 `ab` triggers. It sees only the two
that change a compared leaf in the B arm alone.

**Archive.** The self-test, the rewrap record and every condition-5 output are kept at `evidence/1167/a8f09a0a/`
(`SHA256SUMS` sha256 `3baf98db8c917b21eec89dee78b44ba74d2b431932bab7ed6ec0e9dccdd94c0c`). The archive does not keep
the 766 MiB of rewritten scratch JSON. Instead, each synthetic case keeps a manifest of every file it wrote, with
path, bytes and SHA-256, and its symlink list. Every case was rebuilt from scratch, and all 32 reproduced their
manifests and link lists exactly.

**Disclosures.**

- **First attempt.** The condition-5 driver first ran copies of both checks from `/tmp`, outside any clone. The
  checks run `git` from their own directory, so all four check runs exited 2 and wrote nothing. The driver then
  printed a misleading `STOP: the posted packs drew a new finding` and exited 3. No pack was evaluated. The record is
  in `condition-5/attempt1-rc2/`. The second attempt runs each check in place inside a clone.
- **Lock-line header.** The run logs carry a host and lock header. The first four of them piped `grep` into `sed`, so
  their `heavy-lock line: none` could never print. In those logs, the absence of any `heavy-lock line:` means `grep`
  matched no `/proc/locks` line for the lock's inode. Later headers print `none` explicitly.
