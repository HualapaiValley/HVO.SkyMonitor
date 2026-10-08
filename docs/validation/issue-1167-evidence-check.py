"""Checks that #1167 evidence packs are complete before issue-1167-evaluate.py's verdicts on them are relied on.

Usage (standard library only; run with python3 -I inside a clone that holds the measured revisions' objects):
  issue-1167-evidence-check.py deep <#1167 manifest> <final pack> <measured revision> <output json>
  issue-1167-evidence-check.py ab <#1167 manifest> <#1126 manifest> <A/B pack> <measured revision> <output json>

Added after review PR-1181-R0 (F1-F3) and corrected after PR-1181-R1, PR-1181-R2 and PR-1181-R3. The
frozen manifest and evaluator are unchanged and still decide; this check only establishes that the evidence they decided
on is the complete declared inventory with the content its declared writers produce. Each version was committed before
it was run on any measured or synthetic pack. Its expected inventory and content rules come only from committed sources:
the frozen #1167 manifest and evaluator, the #1126 manifest, the #1126/#1167 runner scripts, and the harness, writer and
producer sources, each quoted with its line below. Each quoted declaration is then found again in the source the pack
itself records (the harness blobs and the measured revision in a final index, the sources at the A and B revisions), so
a pack is checked against the declarations of the revision that produced it. Nothing in the inventory or the content
rules is derived from evidence.

"deep" requires the run set to be exactly the manifest matrix, every report to hash to its index entry, every case of the
declared family/view/partition/capture inventory exactly once with its declared view, capture and UTC, 30 finite
selection samples after 5 warm-ups whose nearest-rank p95 equals the reported p95 exactly, and index-bound evidence
equal to the harness formula on every perspective cold row, with its admitted count equal to the row's own
metrics.indexStars. Every row's solve time must be a finite nonnegative double (a Stopwatch reading, INT :32-33), the
report's coldSolves and coldP95Ms the count and nearest-rank p95 of its capture-0 rows' solve times (harness :199,
:243-244), and nothing in the report a non-finite double (below). A run with no report is not incomplete evidence: the
frozen evaluator already makes every decision that depends on it undecidable (issue-1167-evaluate.py:144). It must be
status "error" and is listed under runsWithoutReport. "ab" requires every arm to hold exactly the #1126 manifest's run
and report inventory at its declared revision, every report to hash to its index entry, and both pixel-pair reports per
family; every one of those reports must record its arm's revision and hold the content its declared writer produces
(Report below). Exit 0 when the evidence is complete, 1 on any finding, and 2 on a usage or input error.

Reference scores (PR-1181-R2). The pixels and optical writers score a mapped solve with VirtualAstrometryReference.Score
(PIX :72-73, OPT :179-185). Each score must hold that scorer's members (REF :245-256) and withheld rows (REF :223-229).
Its precision must equal the share of all its association rows, repeats included, whose catalogId is expectedNearestId
(REF :215). With no rows the scorer computes 0/0 = NaN: the pixels writer cannot serialize that, so a pixels score with
no rows is a finding; the optical writer writes "NaN" with its REF :238 line. Separately, the 12/4 association minimum
(CORE B:419) counts each detection index once. A score's withheld count, missing count and p95 nullness must be those
of its withheld rows. Each REF :238-244 line is decided from the score's serialized values: it must appear exactly once
when its condition holds and not at all when it does not. A line is matched by its "{id}: <fixed text>" prefix only. A
REF line for an id the writer did not score is a finding. Association rows preserve SOL A:88/B:97's nondecreasing
StringComparer.Ordinal catalog-ID order (UTF-16 code units), without a secondary tie-breaker. Hypotheses are a
nonnegative Int32 solver counter (CORE A:103/B:119, A:142/B:158; AC A:156/B:207), not a Stopwatch reading.
In a "runs" pixels report, A or B, the exact statistics.median capture-0 aggregation must be finite and positive;
both B1/A1 and B2/A2 ratios must also be finite (issue-1167-evaluate.py:89-91, :103-109). Finite samples alone do not
guarantee either property, and an alternative overflow-resistant median would mask the frozen evaluator's behavior.

Non-finite doubles (PR-1181-R2). The pixels and resources writers and the deep harness serialize with JsonOptions (FIX
:62), which cannot write NaN or an infinity: System.Text.Json throws instead (framework behaviour, not verified in this
repository). A bare NaN, Infinity or -Infinity, a number beyond the double range such as 1e309 (which Python reads as
an infinity), an integer-form token outside the double range, or a string equal to "NaN", "Infinity" or "-Infinity"
anywhere in such a report is a finding. The string
rule is a superset of numeric positions: it is not derived from which members are strings. The optical, uncertainty
and measured writers serialize with EvidenceJsonOptions (OPT :33-35, UNC :54-56, MEAS :32-34), which writes those
values as the three strings, so there the rule does not apply; only the optical score's doubles are checked (score()).

Not checked. Failure lines are checked only where a rule names them: lines for ids outside the declared inventory
and lines no rule names (for example the PIX :70 time-budget line) are not. REF :242 is not checked in an optical
withheld score: its solve (OPT :176) is not serialized beside the score. An optical withheld frame without a mapping
carries no score (OPT :178), so it has nothing to check but its failure line. The withheld RMS and p95 values are not
recomputed from the withheld rows, only their nullness and the REF :240 line they decide; the expected and
measuredMapping pixels are not checked; and no "{...:R}" text is compared. In deep mode a row's score (harness :144)
and failure lines are not checked. Twelve key sets are hand lists transcribed from writers not quoted whole: TOP_KEYS,
PIXEL_ROW_KEYS, GEOMETRY_KEYS, OPTICAL_REPORT_KEYS, FIT_KEYS, ERROR_KEYS, MEASURED_REPORT_KEYS, V1_KEYS, V2_KEYS,
MEASURED_DIAGNOSTIC_KEYS, MEASURED_SCORE_KEYS and RESOURCE_ROW_KEYS. Every other key set is derived from a quoted
declaration by members().
"""

import hashlib
import json
import math
import re
import statistics
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

HARNESS = "tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualDeepAstrometryQualificationTests.cs"
FIXTURE = "tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualAstrometryFixture.cs"
HARNESS_SOURCES = {HARNESS, FIXTURE, "tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualAstrometryQualificationTests.cs",
                   "tests/HVO.SkyMonitor.CameraAgent.Tests/VirtualAstrometryReference.cs"}  # issue-1167-qualification.sh:66
CFA_RIG = "src/HVO.SkyMonitor.CameraAgent/virtual-asi178mc.full.json"
RUNNER_1126 = "docs/validation/issue-1126-qualification.sh"
AB_RUNNER = "docs/validation/issue-1167-ab.sh"
SOLVER = "src/HVO.SkyMonitor.Astronomy/Astrometry/AstrometricSolver.cs"
INTERNALS = "src/HVO.SkyMonitor.Astronomy/Astrometry/AstrometricSolverInternals.cs"

# The expected inventory. Line numbers are at d3b78737.
FAMILY_MODELS = {  # VirtualAstrometryFixture.cs:41-46
    "equidistant": "EquidistantFisheye", "equisolid": "EquisolidFisheye", "stereographic": "StereographicFisheye",
    "orthographic": "OrthographicFisheye", "rectilinear": "Perspective", "rectilinear-8mm": "Perspective"}
MONO_VIEWS = ("mono-native", "mono-roi", "mono-bin2", "mono-roi-bin2", "mono-mirror", "mono-roll")  # fixture :85-90
CFA_VIEWS = ("cfa-native", "cfa-derived-phase-10", "cfa-derived-phase-01", "cfa-derived-phase-11")  # fixture :94, :344
HELD_OUT = ((1, 15), (5, 15), (9, 15))  # harness :49; #1167 manifest :64
CAPTURES = 3  # harness :109
WARMUP, MEASURED = 5, 30  # harness :84, :88, :231; #1167 manifest :103 (G4)

# Each declaration is found, with any run of whitespace matching any other, in the recorded source.
DECLARATIONS = (
    (HARNESS, 49, "HeldOutPartitions = [(1, 15, 116711), (5, 15, 116715), (9, 15, 116719)];"),
    (HARNESS, 50, "DerivedCfaPhases = [(1, 0), (0, 1), (1, 1)];"),
    (HARNESS, 84, "for (var sample = -5; sample < 30; sample++)"),
    (HARNESS, 88, "if (sample >= 0) selectionMs.Add("),
    (HARNESS, 95, "var mapCases = new List<VirtualAstrometryQualificationTests.MapCase>(); var cold = new List<double>();"),
    (HARNESS, 102, "foreach (var fixtureProfile in VirtualAstrometryFixture.Profiles(seed))"),
    (HARNESS, 109, "for (var index = 0; index < 3; index++)"),
    (HARNESS, 111, "var utc = new DateTimeOffset(2026, month, day, 8, 0, 0, TimeSpan.Zero).AddSeconds(60 * index);"),
    (HARNESS, 117, "if (native.Layout.PixelFormat == CameraPixelFormat.BayerRggb16)"),
    (HARNESS, 122, 'var caseId = $"{month:D2}-{view.Name}-{index}";'),
    (HARNESS, "167-168", "var indexPrefix = family.Model == ProjectionModel.Perspective && prior is null"
                         " ? IndexPrefixBound(catalog, frame.MidpointUtc, solved.Metrics.IndexStars) : null;"),
    (HARNESS, "189-190", "solved.Assessment, solved.Metrics,"),
    (HARNESS, "197-199", "failures = caseFailures }); if (index == 0) cold.Add(solved.Metrics.ElapsedMilliseconds);"),
    (HARNESS, 231, "warmup = 5, measured = selectionMs.Count, p95Ms = Percentile95(selectionMs), samplesMs = selectionMs"),
    (HARNESS, 243, "coldSolves = cold.Count,"),
    (HARNESS, 244, "coldP95Ms = cold.Count == 0 ? (double?)null : Percentile95(cold),"),
    (HARNESS, "251-253", "failingCases, failures }, VirtualAstrometryFixture.JsonOptions)).ConfigureAwait(false);"),
    (HARNESS, 504, "private sealed record IndexPrefixEvidence(int TrainingStars, int AdmittedIndexStars, long BoundBytes);"),
    (HARNESS, 510, "private static IndexPrefixEvidence IndexPrefixBound(AstrometricCatalogData catalog, DateTimeOffset midpointUtc, int admitted)"),
    (HARNESS, 515, "long rows = Math.Min(training, 1501);"),
    (HARNESS, 516, "return new(training, admitted, 8 * rows * (rows - 1) / 2 + 24 * rows + 8L * training + 24L * training + 48);"),
    (HARNESS, 519, "values.Order().ElementAt((int)Math.Ceiling(.95 * values.Count) - 1)"),
    (FIXTURE, 62, "internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, "
                  "Converters = { new JsonStringEnumConverter() } };"),
    (FIXTURE, 72, 'var mono = LoadRig("virtual-asi174.full.json") with'),
    (FIXTURE, 76, "CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned,"),
    (FIXTURE, 93, 'if (family.Model == ProjectionModel.EquidistantFisheye) profiles.Add(Profile("cfa-native", LoadRig("virtual-asi178mc.full.json")'),
    (FIXTURE, 344, 'new VirtualAstrometryProfile($"cfa-derived-phase-{x}{y}"'),
    (CFA_RIG, 21, '"pixelFormat": "BayerRggb16"'),
    # Every solve time a deep row records (harness :189-190, :199): a Stopwatch reading through every metrics construction.
    (INTERNALS, 32, "private readonly Stopwatch watch = Stopwatch.StartNew();"),
    (INTERNALS, 33, "internal double ElapsedMilliseconds => watch.Elapsed.TotalMilliseconds;"),
    (SOLVER, 45, "var control = new AstrometricWorkControl(previous is null ? options.ColdBudgetMilliseconds : options.WarmBudgetMilliseconds, "
                 "cancellationToken);"),
    (SOLVER, 47, "Create(frame, calibration, catalog, options, mode, status, code, reason, null, null, [], new(control.ElapsedMilliseconds, 0, 0, "
                 "0, 0, 0, status == AstrometricAssessmentStatus.BudgetExceeded), previous?.IdentitySha256);"),
    (SOLVER, 86, "null, null, [], new(control.ElapsedMilliseconds, core.CatalogIndexStars, core.CatalogTriangles, core.ImageTriangles, "
                 "core.Hypotheses, core.DistinctCandidates, true),"),
    (SOLVER, 102, "new(control.ElapsedMilliseconds, core.CatalogIndexStars, core.CatalogTriangles, core.ImageTriangles, core.Hypotheses, "
                  "core.DistinctCandidates, limit), previous?.IdentitySha256);"),
)
FAMILY_PATTERN = re.compile(r'new\("([a-z0-9-]+)", ProjectionModel\.(\w+),')  # fixture :41-46
PROFILE_PATTERN = re.compile(r'\bProfile\("([a-z0-9-]+)"')  # fixture :85-90, :94
AB_DECLARATIONS = (  # (source, revision role, line at d3b78737, declaration)
    (RUNNER_1126, "a", 25, 'case "$mode" in tuning) section=tuning ;; final) section=runs ;;'),
    (RUNNER_1126, "a", 39, "mapfile -t families < <(jq -r '.families.values[]' \"$manifest\")"),
    (RUNNER_1126, "a", 71, "name=$id-$value;"),
    (RUNNER_1126, "a", 95, 'target="$out/reports/$name/$(basename "$pattern")"'),
    (RUNNER_1126, "b", 25, 'case "$mode" in tuning) section=tuning ;; final) section=runs ;;'),
    (RUNNER_1126, "b", 39, "mapfile -t families < <(jq -r '.families.values[]' \"$manifest\")"),
    (RUNNER_1126, "b", 71, "name=$id-$value;"),
    (RUNNER_1126, "b", 95, 'target="$out/reports/$name/$(basename "$pattern")"'),
    (AB_RUNNER, "b", 37, '(cd "$tree" && docs/validation/issue-1126-qualification.sh "$mode" "$target")'),
    (AB_RUNNER, "b", 47, 'revision=$(git -C "$tree" rev-parse HEAD)'),
    (AB_RUNNER, "b", 50, 'env DOTNET_TieredCompilation=0 HVO_EVIDENCE_REVISION="$revision" HVO_PROJECTION_FAMILY="$family"'),
    (AB_RUNNER, "b", 52, '--filter "FullyQualifiedName~VirtualAstrometryQualificationTests.ActualPixelsBlindWarmReadouts"'),
    (AB_RUNNER, "b", 57, 'cp "${matches[0]}" "$target/virtual-astrometry-pixels.json"'),
    (AB_RUNNER, "b", 64, 'qualify "$a" final "$out/a1-final"'),
    (AB_RUNNER, "b", 65, 'qualify "$a" tuning "$out/a1-tuning"'),
    (AB_RUNNER, "b", 66, 'qualify "$b" final "$out/b1-final"'),
    (AB_RUNNER, "b", 67, 'qualify "$b" tuning "$out/b1-tuning"'),
    (AB_RUNNER, "b", 68, "mapfile -t families < <(jq -r '.families.values[]' \"$b/docs/validation/issue-1126-qualification-manifest.json\")"),
    (AB_RUNNER, "b", 70, 'pixels "$b" "$family" "$out/b2/$family"'),
    (AB_RUNNER, "b", 71, 'pixels "$a" "$family" "$out/a2/$family"'),
)
ARMS = (("a1-final", "runs", "a"), ("a1-tuning", "tuning", "a"), ("b1-final", "runs", "b"), ("b1-tuning", "tuning", "b"))

# Report content (PR-1181-R1). Sources by key; every content rule below cites a declaration in CONTENT_DECLARATIONS, which
# "ab" finds at both the A revision (0639e27d) and the B revision (d3b78737), with its line at each.
T, S = "tests/HVO.SkyMonitor.CameraAgent.Tests/", "src/HVO.SkyMonitor.Astronomy/Astrometry/"
SOURCES = {
    "PIX": T + "VirtualAstrometryQualificationTests.cs", "REF": T + "VirtualAstrometryReference.cs",
    "OPT": T + "VirtualOpticalCalibrationQualificationTests.cs", "UNC": T + "VirtualAstrometricUncertaintyQualificationTests.cs",
    "MEAS": T + "VirtualMeasuredStarQualificationTests.cs", "RES": T + "VirtualAstrometryResourceTests.cs", "FIX": FIXTURE,
    "AC": S + "AstrometryContracts.cs", "SOL": S + "AstrometricSolver.cs", "CORE": S + "AstrometricSolverCore.cs",
    "SESS": S + "OpticalCalibrationSession.cs", "OC": S + "OpticalCalibrationContracts.cs", "UNCS": S + "AstrometricUncertainty.cs",
    "DIAG": S + "AstrometricResidualDiagnostics.cs", "MAP": S + "AstrometricMapping.cs",
    "SSM": "src/HVO.SkyMonitor.Imaging/StellarSourceMeasurer.cs", "CCJ": "src/HVO.SkyMonitor.AgentCore/CaptureContractJson.cs",
    "CAT": "src/HVO.SkyMonitor.Astronomy/Catalog.cs", "R1126": RUNNER_1126, "CFA": CFA_RIG, "INT": INTERNALS}
CONTENT_DECLARATIONS = (  # (source, line at A, line at B, declaration)
    # Serialization conventions
    ("FIX", "62", "62", "internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, "
                        "Converters = { new JsonStringEnumConverter() } };"),
    ("FIX", "53", "53", "var name = Environment.GetEnvironmentVariable(FamilyVariable);"),
    ("FIX", "55", "55", ": Families.SingleOrDefault(f => f.Name == name) ??"),
    ("R1126", "34", "34", "revision=$(git rev-parse HEAD)"),
    ("R1126", "38", "38", 'variable=$(jq -r .families.variable "$manifest")'),
    ("R1126", "72", "72", 'extra=("$variable=$value")'),
    ("R1126", "75", "75", 'env DOTNET_TieredCompilation=0 HVO_EVIDENCE_REVISION="$revision" "${extra[@]}"'),
    # Profile and view inventory
    ("FIX", "72", "72", 'var mono = LoadRig("virtual-asi174.full.json") with'),
    ("FIX", "76", "76", "CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned,"),
    ("FIX", "93", "93", 'if (family.Model == ProjectionModel.EquidistantFisheye) profiles.Add(Profile("cfa-native", '
                        'LoadRig("virtual-asi178mc.full.json")'),
    ("FIX", "344", "344", 'new VirtualAstrometryProfile($"cfa-derived-phase-{x}{y}"'),
    ("CFA", "21,63", "21,63", '"pixelFormat": "BayerRggb16"'),
    # Pixels writer
    ("PIX", "21", "21", "public Task TuningStationaryPixels() => RunAsync(tuning: true);"),
    ("PIX", "24", "24", "public Task ActualPixelsBlindWarmReadouts() => RunAsync(tuning: false);"),
    ("PIX", "31", "31", "if (!tuning) RequireRevision();"),
    ("PIX", "34", "34", "var partitions = tuning ? new[] { (Month: 2, Day: 10, Seed: 110220) } : [(Month: 1, Day: 15, Seed: 110201), (Month: 5, "
                        "Day: 15, Seed: 110205), (Month: 9, Day: 15, Seed: 110209)];"),
    ("PIX", "36", "36", 'var partition = tuning ? "tuning-not-final" : "held-out-stationary-baseline";'),
    ("PIX", "42", "42", "foreach (var profile in VirtualAstrometryFixture.Profiles(seed))"),
    ("PIX", "48", "48", "for (var index = 0; index < 3; index++)"),
    ("PIX", "50", "50", "var utc = new DateTimeOffset(2026, month, day, 8, 0, 0, TimeSpan.Zero).AddSeconds(60 * index);"),
    ("PIX", "56", "56", "if (native.Layout.PixelFormat == CameraPixelFormat.BayerRggb16) foreach (var (x, y) in new[] { (1, 0), (0, 1), (1, 1) })"),
    ("PIX", "61", "61", 'var caseId = $"{month:D2}-{view.Name}-{index}";'),
    ("PIX", "66", "66", "var solved = VirtualAstrometryFixture.Solve(input, nominal, catalog, measured, prior);"),
    ("PIX", "67", "67", "var expectedMode = index == 0 ? AstrometricSolveMode.Blind : AstrometricSolveMode.Warm;"),
    ("PIX", "68", "68", 'if (solved.Assessment.Mode != expectedMode) failures.Add($"{caseId}: expected {expectedMode} solve");'),
    ("PIX", "72", "72", "var score = solved.Assessment.HasMeasuredMapping ? VirtualAstrometryReference.Score(view.Config.Rig,"),
    ("PIX", "73", "73", "sceneUtc, nominal, catalog, [.. measured.Detections.Select(d => new AstrometricDetection(d.Index, d.Pixel, d.Flux))], "
                        "solved, failures, caseId) : null;"),
    ("PIX", "74", "74", 'if (!solved.Assessment.HasMeasuredMapping) failures.Add($"{caseId}: {solved.Assessment.Reason}");'),
    ("PIX", "78", "78", "mapCases.Add(new(caseId, view.Config.Rig, sceneUtc, nominal, solved.Assessment));"),
    ("PIX", "90", "90", "caseId, view.Name, index, utc,"),
    ("PIX", "100", "100", "solved.Assessment, solved.Metrics, score,"),
    ("PIX", "108", "108", "nominal = new { nominal.IdentitySha256, nominal.CalibrationVersion, nominal.ReadoutIdentitySha256, nominal.Projection },"),
    ("PIX", "122", "122", 'schema = "virtual-astrometry-pixels-v2", partition, projectionFamily = VirtualAstrometryFixture.Family.Name, revision = '
                          'Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),'),
    ("PIX", "128", "128", "catalog.IdentitySha256, catalog.SelectionIdentitySha256,"),
    ("PIX", "134", "134", "reports, grids = mappings.Grids, sourceGeometry = mappings.SourceGeometry,"),
    ("PIX", "137-140", "137-140", "sourceCoverage = mappings.SourceCoverage, mappingCoverage = mappings.MappingCoverage, failures }, "
                                  "VirtualAstrometryFixture.JsonOptions)).ConfigureAwait(false);"),
    ("PIX", "206", "206", "foreach (var source in cases)"),
    ("PIX", "211", "211", "foreach (var fy in new[] { .05, .2, .5, .8, .95 }) foreach (var fx in new[] { .05, .2, .5, .8, .95 })"),
    ("PIX", "246", "246", "geometry.Add(new { source = source.Id, source.Assessment.Mode, source.Assessment.IdentitySha256,"),
    # Reference scorer
    ("REF", "201", "201", "var associationRows = solved.Associations.Select(association =>"),
    ("REF", "206-213", "206-213", "return new { association.CatalogId, association.DetectionIndex, association.Verification, expectedNearestId = "
                                  "nearest.Star.Id, residualPixels = Distance(measured.Pixel, actual.Pixel) };"),
    ("REF", "215", "215", "var precision = associationRows.Count(row => row.CatalogId == row.expectedNearestId) / (double)associationRows.Length;"),
    ("REF", "223-229", "223-229", "return new { item.Star.Id, expected = item.Pixel, measuredMapping = mapped, residualPixels = mapped is null ? "
                                  "(double?)null : Distance(mapped.Value, item.Pixel) };"),
    ("REF", "231", "231", "var missing = withheld.Count(row => row.residualPixels is null);"),
    ("REF", "232", "232", "var sorted = withheld.Where(row => row.residualPixels is not null).Select(row => row.residualPixels!.Value).Order()"
                          ".ToArray();"),
    ("REF", "234", "234", "var p95 = sorted.Length == 0 ? (double?)null : sorted[(int)Math.Ceiling(.95 * sorted.Length) - 1];"),
    ("REF", "238", "238", 'if (precision != 1) failures.Add($"{caseId}: association precision {precision:R}");'),
    ("REF", "239", "239", "if (withheld.Length < 4 || missing > 0 || rms > .5 || p95 is null or > .75)"),
    ("REF", "240", "240", 'failures.Add($"{caseId}: independent withheld mapping'),
    ("REF", "241", "241", 'if (poseError > .06 || focalError > .001) failures.Add($"{caseId}: pose'),
    ("REF", "242", "242", 'if (solved.Assessment.Mode == AstrometricSolveMode.Warm && solved.Metrics.Hypotheses != 0) failures.Add($"{caseId}: '
                          'warm search ran hypotheses");'),
    ("REF", "243", "243", "if (solved.Associations.Select(a => a.DetectionIndex).Distinct().Count() != solved.Associations.Count)"),
    ("REF", "244", "244", 'failures.Add($"{caseId}: non-unique measured associations");'),
    ("REF", "245-256", "245-256", "return new { precision, poseErrorDegrees = poseError, focalRelativeError = focalError, withheldCount = "
                                  "withheld.Length, missingMappings = missing, withheldRmsPixels = rms, withheldP95Pixels = p95, associationRows, "
                                  "withheld };"),
    ("CAT", "4", "4", "public sealed record CelestialCatalogObject( string Id,"),
    # Optical writer
    ("OPT", "33", "33", "private static readonly JsonSerializerOptions EvidenceJsonOptions = new(VirtualAstrometryFixture.JsonOptions)"),
    ("OPT", "35", "35", "NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,"),
    ("OPT", "40", "40", 'private static readonly (string Readout, double Hours)[] FitPlan = [("native", 5), ("roi", 6.5), ("native", 8), ("bin2", '
                        '9)];'),
    ("OPT", "41", "41", 'private static readonly (string Readout, double Hours)[] ValidationPlan = [("native", 7.25), ("roi", 7.25), ("roi-bin2", '
                        '9.5)];'),
    ("OPT", "48", "48", "public Task TuningOpticalCalibration() => RunAsync(tuning: true);"),
    ("OPT", "51", "51", "public Task HeldOutOpticalCalibration() => RunAsync(tuning: false);"),
    ("OPT", "61", "61", "? new[] { (Month: 2, Day: 10, Seed: 110420,"),
    ("OPT", "62", "62", ": [(Month: 1, Day: 15, Seed: 110401,"),
    ("OPT", "63", "63", "(Month: 5, Day: 15, Seed: 110405,"),
    ("OPT", "64", "64", "(Month: 9, Day: 15, Seed: 110409,"),
    ("OPT", "65", "65", 'var partition = tuning ? "optical-calibration-tuning-not-final" : "optical-calibration-held-out";'),
    ("OPT", "70", "70", 'var caseId = $"{month:D2}";'),
    ("OPT", "104", "104", "return (new OpticalCalibrationFrame(VirtualAstrometryFixture.FrameContextV2(input), descriptor, "
                          "CaptureContractJson.ComputeCanonicalJsonSha256(descriptor), VirtualAstrometryFixture.Detections(measured)),"),
    ("OPT", "116", "116", "foreach (var (readout, hours) in ValidationPlan)"),
    ("OPT", "121", "121", "var validation = withheld.Select(w => w.Frame).ToArray();"),
    ("OPT", "145", "145", 'failures.Add($"{caseId}: calibration {result.Status} {result.ReasonCode}: {string.Join(",", result.Rejections)}");'),
    ("OPT", "172", "172", "foreach (var (readout, frame, pixels, sceneUtc) in withheld)"),
    ("OPT", "176", "176", "var solved = VirtualAstrometryFixture.SolveV2(pixels, calibration, catalog, measured);"),
    ("OPT", "177", "177", 'var scoreId = $"{caseId}-{readout}-withheld";'),
    ("OPT", "178", "178", 'if (!solved.Assessment.HasMeasuredMapping) { failures.Add($"{scoreId}: {solved.Assessment.Reason}"); continue; }'),
    ("OPT", "179-185", "179-185", "withheldScores.Add(new { readout, frame.Frame.CaptureId, score = VirtualAstrometryReference.Score("
                                  "WithReadout(truthRig, readout), sceneUtc, calibration, catalog, VirtualAstrometryFixture.Detections(measured), "
                                  "solved, failures, scoreId) });"),
    ("OPT", "200", "200", "kind = fitKind, resources = fitResources, singleFrameColdSolveBaseline = baseline, result.Status, result.ReasonCode, "
                          "result.Rejections,"),
    ("OPT", "203", "203", "result.Status, result.ReasonCode, result.Rejections, result.IdentitySha256, result.CalibratedNative, result.Parameters, "
                          "result.Frames, result.Validations,"),
    ("OPT", "214", "214", "errors, withheldScores,"),
    ("OPT", "230", "230", 'schema = "virtual-optical-calibration-v1", partition, projectionFamily = VirtualAstrometryFixture.Family.Name, revision '
                          '= Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),'),
    ("OPT", "238", "238", "catalog.IdentitySha256, catalog.SelectionIdentitySha256,"),
    ("OPT", "257", "257", "reports, failures }, EvidenceJsonOptions)"),
    # Optical session and contracts
    ("SESS", "87", "87", 'works.Count > 0 ? works.Select(FrameSummary) : frames.Select(f => new OpticalCalibrationFrameResult(f.Frame.CaptureId, '
                         'f.ReadoutIdentitySha256, "not-attempted", 0, null, null, null, 0, 0, null, null)),'),
    ("SESS", "112", "114", "foreach (var frame in frames) { control.Check(); var work = Prepare(frame, catalog, "
                           "solverOptions.MaximumCatalogMagnitude); works.Add(work);"),
    ("SESS", "164", "166", "foreach (var frame in validationFrames)"),
    ("SESS", "172", "174", "validations.Add(new(frame.Frame.CaptureId, a.Status, a.ReasonCode, a.Parameters?.FocalScale, "
                           "a.Quality?.FittingRmsPixels, a.Quality?.VerificationRmsPixels, a.IdentitySha256));"),
    ("SESS", "180", "182", "if (validationRejections.Count > 0)"),
    ("SESS", "184", "186", 'return Result(OpticalCalibrationStatus.Accepted, "accepted",'),
    ("SESS", "226", "228", "AstrometricIdentity.RequireSha256(frame.ReadoutIdentitySha256);"),
    ("SESS", "617", "619", "return new(work.Input.Frame.CaptureId, work.Input.ReadoutIdentitySha256, work.Status,"),
    ("OC", "95", "95", "public sealed record OpticalCalibrationValidation(Guid CaptureId, AstrometricAssessmentStatus Status, string ReasonCode, "
                       "double? FocalScale, double? FittingRmsPixels, double? VerificationRmsPixels, string AssessmentIdentitySha256);"),
    ("OC", "173", "173", "IdentitySha256 = SharedCovariance is null ? AstrometricIdentity.Hash(core) : AstrometricIdentity.Hash(new { core, "
                         "SharedCovariance });"),
    ("OC", "201", "201", "public bool IsAccepted => Status == OpticalCalibrationStatus.Accepted && CalibratedNative is not null;"),
    # Uncertainty writer and estimator
    ("UNC", "54", "54", "private static readonly JsonSerializerOptions EvidenceJsonOptions = new(VirtualAstrometryFixture.JsonOptions)"),
    ("UNC", "56", "56", "NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,"),
    ("UNC", "62", "62", "private sealed record Realization(string Profile, int Index, string Status, string ReasonCode, double[]? Error, double[]? "
                        "Covariance, double? Mahalanobis, double? ChiSquare, int DegreesOfFreedom, double? HeldOutChiSquare, int "
                        "HeldOutDegreesOfFreedom, double? RequiredFloorPixels, VirtualMeasuredStarQualificationTests.ResourceSample Frame, "
                        "VirtualMeasuredStarQualificationTests.ResourceSample Estimator, string? IdentitySha256);"),
    ("UNC", "67", "67", "public Task TuningAstrometricUncertainty() => RunAsync(tuning: true);"),
    ("UNC", "70", "70", "public Task HeldOutAstrometricUncertainty() => RunAsync(tuning: false);"),
    ("UNC", "79", "79", "var partitions = tuning ? new[] { (Month: 2, Day: 10, Seed: 110520) } : [(Month: 1, Day: 15, Seed: 110501), (Month: 5, "
                        "Day: 15, Seed: 110505), (Month: 9, Day: 15, Seed: 110509)];"),
    ("UNC", "81", "81", "var realizations = tuning ? 8 : 20;"),
    ("UNC", "82", "82", 'var partition = tuning ? "uncertainty-tuning-not-final" : "uncertainty-held-out";'),
    ("UNC", "86", "86", "foreach (var name in VirtualAstrometryFixture.Profiles(seed).Select(p => p.Name)) frames.AddRange(await "
                        "ProfileAsync(snapshot, catalog, month, day, seed, name, realizations, failures).ConfigureAwait(false)); "
                        "sessions.Add(await SessionChainAsync(snapshot, catalog, month, day, seed, failures).ConfigureAwait(false));"),
    ("UNC", "87", "87", "frames.AddRange(await ProfileAsync("),
    ("UNC", "134", "134", 'schema = "virtual-astrometric-uncertainty-v1", projectionFamily = VirtualAstrometryFixture.Family.Name, partition, '
                          'revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),'),
    ("UNC", "142", "142", "catalog.IdentitySha256, catalog.SelectionIdentitySha256,"),
    ("UNC", "158", "158", "realizationsPerProfile = realizations,"),
    ("UNC", "169", "169", "frames, sessions, failures }, EvidenceJsonOptions)"),
    ("UNC", "186", "186", "for (var index = 0; index < count; index++)"),
    ("UNC", "197", "197", 'var caseId = $"{month:D2}-{name}-{index}";'),
    ("UNC", "203", "203", 'if (!solved.Assessment.HasMeasuredMapping) { failures.Add($"{caseId}: solve not accepted: {solved.Assessment.Reason}"); '
                          'continue; }'),
    ("UNC", "224", "224", "results.Add(new(name, index, uncertainty.Status, uncertainty.ReasonCode, error, covariance, distance, v.ChiSquare, "
                          "v.DegreesOfFreedom, v.HeldOutChiSquare, v.HeldOutDegreesOfFreedom, required, frame, estimator, "
                          "uncertainty.IdentitySha256));"),
    ("UNC", "254", "254", 'var caseId = $"{month:D2}-session";'),
    ("UNC", "273", "273", 'foreach (var (readout, hours) in new[] { ("native", 7.25), ("roi", 7.25), ("roi-bin2", 9.5) })'),
    ("UNC", "280", "280", 'if (!result.IsAccepted || result.SharedCovariance is null) { failures.Add($"{caseId}: calibration {result.Status} '
                          '{result.ReasonCode}"); return new { caseId, result.Status, result.ReasonCode }; }'),
    ("UNC", "295", "295", 'if (!solved.Assessment.HasMeasuredMapping) { failures.Add($"{caseId}-{readout}: {solved.Assessment.Reason}"); continue; '
                          '}'),
    ("UNC", "297", "297", 'pixels.Layout.Readout, shared, Budget($"mono-{readout}"));'),
    ("UNC", "298", "298", 'if (uncertainty.Total.Status != AstrometricUncertaintyEstimator.Available) { failures.Add($"{caseId}-{readout}: total '
                          '{uncertainty.Total.Status} {uncertainty.ReasonCode} {uncertainty.Total.ReasonCode}"); rows.Add(new { readout, '
                          'uncertainty.Status, uncertainty.ReasonCode, totalReason = uncertainty.Total.ReasonCode, uncertainty.Validity }); '
                          'continue; }'),
    ("UNC", "309", "309", 'if (distance > SessionEllipsoidLimit) failures.Add($"{caseId}-{readout}: total Mahalanobis {distance:F2} exceeds '
                          '{SessionEllipsoidLimit}");'),
    ("UNC", "310", "310", "rows.Add(new { readout, frame.Frame.CaptureId, uncertainty.IdentitySha256, error, totalMahalanobis = distance, "
                          "conditionalOnlyMahalanobis = conditionalDistance, uncertainty.Total, uncertainty.ConditionalFit, "
                          "uncertainty.SharedCalibration, uncertainty.Validity });"),
    ("UNC", "325", "325", "return new { caseId, result.IdentitySha256, sharedCalibrationIdentity = shared.IdentitySha256, shared.Covariance, rows, "
                          "crossCovarianceFirstPair = cross };"),
    ("UNCS", "93", "93", 'IdentitySha256 = AstrometricIdentity.Hash(new { schema = "astrometric-calibration-covariance-v1", SourceIdentitySha256, '
                         'CalibratedNative, Covariance });'),
    ("UNCS", "169", "169", 'public const string Available = "available";'),
    ("UNCS", "130", "130", "public sealed record AstrometricSharedCalibrationComponent(string Status, string ReasonCode, string? "
                           "CalibrationCovarianceIdentitySha256,"),
    ("UNCS", "244", "244", "return value with { IdentitySha256 = AstrometricIdentity.Hash(value) };"),
    ("UNCS", "251", "251", "new(Withheld, code, sharedCalibration?.IdentitySha256,"),
    ("UNCS", "303", "303", 'shared = new(Withheld, "shared-calibration-covariance-not-supplied", null, [], null,'),
    ("UNCS", "306", "306", "shared = new(Withheld, calibrationFailure, sharedCalibration.IdentitySha256,"),
    ("UNCS", "309", "309", "shared = new(Available, Available, sharedCalibration.IdentitySha256,"),
    ("UNCS", "313", "313", "if (shared.Status != Available) total = new(Withheld, shared.ReasonCode, null, null, null, null);"),
    # Measured-stars writer
    ("MEAS", "32", "32", "private static readonly JsonSerializerOptions EvidenceJsonOptions = new(VirtualAstrometryFixture.JsonOptions)"),
    ("MEAS", "34", "34", "NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,"),
    ("MEAS", "41", "41", "public Task TuningMeasuredStars() => RunAsync(tuning: true);"),
    ("MEAS", "44", "44", "public Task HeldOutMeasuredStars() => RunAsync(tuning: false);"),
    ("MEAS", "54", "54", "var partitions = tuning ? new[] { (Month: 2, Day: 10, Seed: 110220) } : [(Month: 1, Day: 15, Seed: 110201), (Month: 5, "
                         "Day: 15, Seed: 110205), (Month: 9, Day: 15, Seed: 110209)];"),
    ("MEAS", "56", "56", 'var partition = tuning ? "measured-stars-tuning-not-final" : "measured-stars-held-out";'),
    ("MEAS", "60", "60", "foreach (var profile in VirtualAstrometryFixture.Profiles(seed))"),
    ("MEAS", "65", "65", "var utc = new DateTimeOffset(2026, month, day, 8, 0, 0, TimeSpan.Zero);"),
    ("MEAS", "70", "70", 'var caseId = $"{month:D2}-{profile.Name}";'),
    ("MEAS", "75", "75", "var v1Solved = VirtualAstrometryFixture.Solve(input, nominal, catalog, v1);"),
    ("MEAS", "78", "78", "var v2Solved = VirtualAstrometryFixture.SolveV2(input, nominal, catalog, v2);"),
    ("MEAS", "81", "81", "var baseline = Score(truth, v1.Detections, v1Solved); var candidate = Score(truth, v2.Detections, v2Solved);"),
    ("MEAS", "108", "108", "caseId, profile.Name, utc,"),
    ("MEAS", "121", "121", "baseline, v1Solved.Assessment.Status, v1Solved.Assessment.ReasonCode, associations = v1Solved.Associations.Count"),
    ("MEAS", "129", "129", "settingsIdentitySha256 = "
                           "StellarSourceMeasurer.SettingsIdentity(VirtualAstrometryFixture.MeasurementOptions(input.Layout)),"),
    ("MEAS", "140", "140", "candidate, v2Solved.Assessment.Status, v2Solved.Assessment.ReasonCode, associations = v2Solved.Associations.Count"),
    ("MEAS", "145", "145", "diagnostics = new { diagnostics.IdentitySha256,"),
    ("MEAS", "176", "176", 'schema = "virtual-measured-stars-v1", partition, projectionFamily = VirtualAstrometryFixture.Family.Name, revision = '
                           'Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),'),
    ("MEAS", "184", "184", "catalog.IdentitySha256, catalog.SelectionIdentitySha256,"),
    ("MEAS", "197", "197", "reports, windowPressure = pressure, failures"),
    ("MEAS", "254", "254", "internal sealed record MeasuredStarScore(int EligibleStars, int Recovered, double Recall, int Associations,"),
    ("MEAS", "298", "298", "return new(eligible.Length, recovered, eligible.Length == 0 ? 0 : recovered / (double)eligible.Length, "
                           "solved.Associations.Count,"),
    # Resources writer
    ("RES", "22", "22", 'var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION");'),
    ("RES", "41", "41", 'var natives = VirtualAstrometryFixture.Profiles(110220).Where(profile => profile.Name is "mono-native" or '
                        '"cfa-native").ToArray();'),
    ("RES", "105", "105", "profile.Name, configHash = CaptureContractJson.ComputeCanonicalJsonSha256(profile.Config), nominal.IdentitySha256,"),
    ("RES", "126", "126", 'schema = "virtual-astrometry-resources-v2", revision, projectionFamily = VirtualAstrometryFixture.Family.Name,'),
    ("RES", "143", "143", "reports, rawSamples, failures }, VirtualAstrometryFixture.JsonOptions)"),
    # Fixture identity producers
    ("FIX", "163", "163", "return new(RigProjectionContextFactory.Create(nominalRig), nominalRig.Optics.CalibrationVersion, "
                          "CaptureContractJson.ComputeCanonicalJsonSha256(readout.Layout));"),
    ("FIX", "274", "274", "DetectionSettingsIdentitySha256 = StellarSourceMeasurer.SettingsIdentity(MeasurementOptions(input.Layout))"),
    ("FIX", "281", "281", "return prior is null ? AstrometricSolver.Solve(FrameContextV2(input), nominal, catalog, detections, SolverOptions) : "
                          "AstrometricSolver.Refine(FrameContextV2(input), nominal, catalog, detections, prior, SolverOptions);"),
    ("FIX", "319", "319", "CaptureContractJson.ComputeCanonicalJsonSha256(Observer), Observer, input.StartUtc, input.StartUtc + input.Exposure,"),
    ("FIX", "320", "320", "StellarDetector.AlgorithmVersion, CaptureContractJson.ComputeCanonicalJsonSha256(DetectionOptions(input.Layout)));"),
    ("FIX", "353", "353", "return prior is null ? AstrometricSolver.Solve(FrameContext(input), nominal, catalog, detections, SolverOptions) : "
                          "AstrometricSolver.Refine(FrameContext(input), nominal, catalog, detections, prior, SolverOptions);"),
    ("CCJ", "56", "56", "return Convert.ToHexString(SHA256.HashData(canonical));"),
    ("SSM", "239", "239", "public static string SettingsIdentity(StellarMeasurementOptions options)"),
    ("SSM", "242", "242", "return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new"),
    ("DIAG", "134", "134", "var value = new AstrometricResidualDiagnostics(AstrometricResidualDiagnostics.CurrentSchemaVersion, string.Empty, "
                           "assessment.IdentitySha256,"),
    ("DIAG", "142", "142", "return value with { IdentitySha256 = AstrometricIdentity.Hash(value) };"),
    # Contracts
    ("AC", "31", "31", "ReadoutIdentitySha256 = AstrometricIdentity.RequireSha256(readoutIdentitySha256);"),
    ("AC", "32", "32", 'IdentitySha256 = AstrometricIdentity.Hash(new { schema = "astrometric-calibration-v1", projection, calibrationVersion, '
                       'ReadoutIdentitySha256 });'),
    ("AC", "52", "52", "AstrometricIdentity.RequireSha256(DetectionSettingsIdentitySha256);"),
    ("AC", "56", "56", "AstrometricIdentity.RequireSha256(SourceDescriptorSha256); AstrometricIdentity.RequireSha256(SourcePayloadSha256); "
                       "AstrometricIdentity.RequireSha256(ObserverIdentitySha256);"),
    ("AC", "100", "106", 'IdentitySha256 = provenance is null ? AstrometricIdentity.Hash(new { metadata, coordinateModel }) : '
                         'AstrometricIdentity.Hash(new { schema = "astrometric-catalog-v2", metadata, coordinateModel, provenance });'),
    ("AC", "103", "109", 'SelectionIdentitySha256 = AstrometricIdentity.Hash(new { schema = "astrometric-catalog-selection-v2", Stars, '
                         'IsCompleteForRequestedMagnitude, CompletenessMagnitudeLimit });'),
    ("AC", "130", "173", "[JsonIgnore] public string IdentitySha256 => AstrometricIdentity.Hash(this with { });"),
    ("AC", "145", "196", "public enum AstrometricAssessmentStatus { Accepted, Rejected, Unavailable, BudgetExceeded }"),
    ("AC", "146", "197", "public enum AstrometricSolveMode { Blind, Warm }"),
    ("AC", "155", "206", "public sealed record AstrometricAssociation(string CatalogId, int DetectionIndex, double ResidualPixels, bool "
                         "Verification);"),
    ("AC", "165", "216", "AstrometricFitParameters? Parameters, AstrometricFitQuality? Quality, string? AssociationIdentitySha256, string? "
                         "PreviousAssessmentIdentitySha256)"),
    ("AC", "170", "221", "public bool HasMeasuredMapping => Status == AstrometricAssessmentStatus.Accepted && Parameters is not null;"),
    ("AC", "188", "239", "return Convert.ToHexStringLower(Convert.FromHexString(value));"),
    ("AC", "190", "241", "internal static string Hash<T>(T value) => "
                         "Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));"),
    # Solver
    ("SOL", "44", "44", "var mode = previous is null ? AstrometricSolveMode.Blind : AstrometricSolveMode.Warm;"),
    ("SOL", "45", "45", "var control = new AstrometricWorkControl(previous is null ? options.ColdBudgetMilliseconds : "
                        "options.WarmBudgetMilliseconds, cancellationToken);"),
    ("SOL", "46", "46", "Failure(AstrometricAssessmentStatus status, string code, string reason) =>"),
    ("SOL", "47", "47", "Create(frame, calibration, catalog, options, mode, status, code, reason, null, null, [], new(control.ElapsedMilliseconds, "
                        "0, 0, 0, 0, 0, status == AstrometricAssessmentStatus.BudgetExceeded), previous?.IdentitySha256);"),
    ("SOL", "88", "97", "var associations = core.Associations.Select(a => new AstrometricAssociation(a.CatalogId, a.DetectionIndex, "
                        "a.ResidualPixels, a.Verification)).OrderBy(a => a.CatalogId, StringComparer.Ordinal).ToArray();"),
    ("SOL", "93", "102", "new(control.ElapsedMilliseconds, core.CatalogIndexStars, core.CatalogTriangles, core.ImageTriangles, core.Hypotheses, "
                         "core.DistinctCandidates, limit), previous?.IdentitySha256);"),
    ("SOL", "90", "99", "var status = core.Accepted ? AstrometricAssessmentStatus.Accepted : limit ? AstrometricAssessmentStatus.BudgetExceeded : "
                        "AstrometricAssessmentStatus.Rejected;"),
    ("SOL", "106", "115", "calibration.IdentitySha256, catalog.IdentitySha256, catalog.SelectionIdentitySha256, "
                          "AstrometricConventions.SolverVersion, options.IdentitySha256,"),
    ("SOL", "109", "118", "parameters, quality, associations.Length > 0 ? AstrometricIdentity.Hash(associations) : null, previousIdentity);"),
    ("SOL", "110", "119", "assessment = assessment with { IdentitySha256 = AstrometricIdentity.Hash(assessment) };"),
    ("SOL", "111", "120", "return new(assessment, associations, metrics);"),
    ("INT", "32", "32", "private readonly Stopwatch watch = Stopwatch.StartNew();"),
    ("INT", "33", "33", "internal double ElapsedMilliseconds => watch.Elapsed.TotalMilliseconds;"),
    # Solver core: every non-accepted result carries no associations; accepted ones are fit matches then held matches
    ("CORE", "104", "120", 'CoreResult Reject(string reason, CoreQuality? q = null, int candidates = 0) => new(false, "rejected", reason, null, '
                           'null, q, [],'),
    ("CORE", "103", "119", "var grid = new CoreDetectionGrid(detections); var hypotheses = 0; var triangleCount = 0; var imageCount = 0; "
                           "var exhausted = false;"),
    ("CORE", "142", "158", "if (++hypotheses > o.MaximumHypotheses) { exhausted = true; goto SearchFinished; }"),
    ("CORE", "171", "187", 'return new(false, "acquired", "Refined acquisition candidates returned without verification or quality gates", null, '
                           'null, null, [],'),
    ("CORE", "175", "191", 'var accepted = evaluated.Where(c => c.Quality.Status == "accepted").ToArray();'),
    ("CORE", "181", "197", "best.Candidate.Matches.Select(m => new CoreAssociation(m.Star.Catalog.Id, m.CoreDetection.Index, m.Distance, "
                           "false)).Concat(best.Held.Select(m => new CoreAssociation(m.Star.Catalog.Id, m.CoreDetection.Index, m.Distance, "
                           "true))).ToArray(),"),
    ("CORE", "195", "211", 'CoreResult Rejected(string reason, CoreQuality? quality = null, bool timeout = false) => new(false, "rejected", '
                           'reason, null, null, quality, [],'),
    ("CORE", "210", "226", 'if (evaluation.Quality.Status != "accepted") return Rejected('),
    ("CORE", "212", "228", "var associations = evaluation.Candidate.Matches.Select(m => new CoreAssociation(m.Star.Catalog.Id, "
                           "m.CoreDetection.Index, m.Distance, false)).Concat(evaluation.Held.Select(m => new CoreAssociation(m.Star.Catalog.Id, "
                           "m.CoreDetection.Index, m.Distance, true))).ToArray();"),
    ("CORE", "101,198", "117,214", "var training = stars.Where(s => !IsVerification(s.Catalog.Id)).ToList(); var verification = stars.Where(s => "
                                   "IsVerification(s.Catalog.Id)).ToList();"),
    ("CORE", "267", "288", "var finalFit = c.Matches.Where(m => eligible.Contains(m.Star.Catalog.Id)).ToList(); var used = finalFit.Select(m => "
                           "m.CoreDetection.Index).ToHashSet(); var held = Match(verification.Where(s => "
                           "eligible.Contains(s.Catalog.Id)).ToList(), new CoreDetectionGrid(detections.Where(d => !used.Contains(d.Index))), "
                           "camera, c.Rotation, 1.5);"),
    ("CORE", "271", "292", "return (c with { Matches = finalFit }, held, Quality(finalFit, held, expected, configuration));"),
    ("CORE", "364", "419", 'if (fit.Count < 12) reasons.Add("Fewer than12 fitting stars"); if (held.Count < 4) reasons.Add("Fewer than4 withheld '
                           'stars");'),
    ("CORE", "368", "423", 'reasons.Count == 0 ? "accepted" : "rejected",'),
    # Echo sites and early-return forms of the B-only producers
    ("OPT", "43", "43", "private sealed record Truth(double FocalScale, double PrincipalOffsetX, double PrincipalOffsetY, double K1, "
                        "RigOrientation Pose);"),
    ("OPT", "138", "138", 'if (truth.K1 == 0 && !omitted.IsAccepted) failures.Add($"{caseId}: undistorted truth rejected without k1: '
                          '{string.Join(",", omitted.Rejections)}");'),
    ("OPT", "142", "142", "object? errors = null; var withheldScores = new List<object>(); if (!result.IsAccepted)"),
    ("OPT", "193", "193", "caseId, truth, truthNative, nominalNative, measurements,"),
    ("OPT", "211", "211", "result.Diagnostics, result.Metrics"),
    ("OPT", "216", "216", "omittedDistortion = new { resources = omittedResources, omitted.Status, omitted.ReasonCode, omitted.Rejections, "
                          "omitted.Diagnostics }"),
    ("MEAS", "84", "84", 'if (!v2Solved.Assessment.HasMeasuredMapping) failures.Add($"{caseId}: v2 {v2Solved.Assessment.Reason}");'),
    ("SESS", "80", "80", "var skyRotation = SkyRotationDegrees(frames);"),
    ("SESS", "82", "82", "OpticalCalibrationResult Result(OpticalCalibrationStatus status, string code, string reason, IReadOnlyList<string> "
                         "rejections, Shared? shared = null, double?[]? errors = null, ProjectionContext? calibrated = null, Diagnostics? "
                         "diagnostics = null, IEnumerable<OpticalCalibrationValidation>? validations = null) =>"),
    ("SESS", "85", "85", "new(status, code, reason, rejections, nominalNative, calibrated, Parameters(nominalNative, options, shared ?? shared0, "
                         "errors, shared is not null),"),
    ("SESS", "88", "88", 'validations ?? [], (diagnostics ?? new Diagnostics()).ToContract(skyRotation, works.Count(w => w.Status == "fitted")),'),
    ("SESS", "94", "94", 'return Result(OpticalCalibrationStatus.Unavailable, "catalog-incomplete",'),
    ("SESS", "440", "442", "private sealed class Diagnostics"),
    ("SESS", "444", "446", "public int FittingStars { get; set; }"),
    ("SESS", "445", "447", "public int VerificationStars { get; set; }"),
    ("SESS", "446", "448", "public double? FittingRms { get; set; }"),
    ("SESS", "447", "449", "public double? VerificationRms { get; set; }"),
    ("SESS", "448", "450", "public int OccupiedRadialBins { get; set; }"),
    ("SESS", "449", "451", "public int OccupiedAzimuthBins { get; set; }"),
    ("SESS", "450", "452", "public double? ConditionNumber { get; set; }"),
    ("SESS", "451", "453", "public int Iterations { get; set; }"),
    ("SESS", "452", "454", "public bool Converged { get; set; }"),
    ("SESS", "453", "455", "public List<OpticalCalibrationResidualBin> Bins { get; } = [];"),
    ("SESS", "455", "457", "public OpticalCalibrationDiagnostics ToContract(double skyRotation, int fittedFrames) => new(skyRotation, "
                           "fittedFrames, FittingStars, VerificationStars, FittingRms, VerificationRms, OccupiedRadialBins, OccupiedAzimuthBins, "
                           "ConditionNumber, Iterations, Converged, Bins.AsReadOnly());"),
    ("SESS", "598", "600", "private static IEnumerable<OpticalCalibrationParameter> Parameters(ProjectionContext nominal, "
                           "OpticalCalibrationOptions o, Shared shared, double?[]? errors, bool fitted) { var bounds = Bounds(nominal, o); for "
                           "(var i = 0; i < 4; i++)"),
    ("SESS", "607", "609", "var atBound = fitted && bounds.Fitted[i] &&"),
    ("SESS", "610", "612", "Value(shared[i]), Value(bounds.Min[i]), Value(bounds.Max[i]), errors?[i], atBound);"),
    ("OC", "79", "79", "public enum OpticalCalibrationStatus { Accepted, Rejected, Unavailable, BudgetExceeded }"),
    ("OC", "82", "82", "public sealed record OpticalCalibrationParameter(string Name, string Unit, bool Fitted, double Nominal, double Value, "
                       "double Minimum, double Maximum, double? StandardError, bool AtBound);"),
    ("OC", "86", "86", "public sealed record OpticalCalibrationFrameResult(Guid CaptureId, string ReadoutIdentitySha256, string Status, int "
                       "AcquisitionCandidates, double? BoresightAltitudeDegrees, double? BoresightAzimuthDegrees, double? RollDegrees, int "
                       "FittingStars, int VerificationStars, double? FittingRmsPixels, double? VerificationRmsPixels);"),
    ("OC", "99", "99", "public sealed record OpticalCalibrationDiagnostics(double SkyRotationDegrees, int FittedFrames, int FittingStars, int "
                       "VerificationStars, double? FittingRmsPixels, double? VerificationRmsPixels, int OccupiedRadialBins, int "
                       "OccupiedAzimuthBins, double? ConditionNumber, int Iterations, bool Converged, IReadOnlyList<OpticalCalibrationResidualBin> "
                       "ResidualBins);"),
    ("AC", "156", "207", "public sealed record AstrometricExecutionMetrics(double ElapsedMilliseconds, int IndexStars, int IndexTriangles, int "
                         "ImageTriangles, int Hypotheses, int DistinctCandidates, bool BudgetExhausted);"),
)
# The producers B adds. Each is found at B and not at A; any other A/B difference in a declared writer is a finding above.
B_ONLY_DECLARATIONS = (  # (source, line at B, declaration)
    ("SOL", "50", 'if (catalog.Stars.Count > options.CatalogEntryBound) return Failure(AstrometricAssessmentStatus.Unavailable, '
                  '"catalog-selection-unsupported", $"Catalog selection exceeds the {options.CatalogEntryBound}-entry bound of '
                  'the requested selection profile; no fit was attempted.");'),
    ("SOL", "84-87", 'return Create(frame, calibration, catalog, options, mode, AstrometricAssessmentStatus.BudgetExceeded, '
                     '"resource-limit", $"Accepted evidence exceeds the {AstrometricEvidenceJson.MaximumEvidenceStars}-star '
                     'assessment bound; no proposed mapping replaces last-good evidence.", null, null, [], '
                     'new(control.ElapsedMilliseconds, core.CatalogIndexStars, core.CatalogTriangles, core.ImageTriangles, '
                     'core.Hypotheses, core.DistinctCandidates, true), previous?.IdentitySha256);'),
    ("SESS", "95-96", 'if (catalog.Stars.Count > solverOptions.CatalogEntryBound) return Result(OpticalCalibrationStatus.Unavailable, '
                      '"catalog-selection-unsupported", $"Catalog selection exceeds the {solverOptions.CatalogEntryBound}-entry '
                      'bound of the requested selection profile; no fit was attempted.", ["catalog-selection-unsupported"]);'),
    ("MAP", "77", "internal const int MaximumEvidenceStars = 2500;"),
)

KINDS = {"virtual-astrometry-pixels.json": "pixels", "virtual-optical-calibration.json": "optical",
         "virtual-astrometric-uncertainty.json": "uncertainty", "virtual-measured-stars.json": "measured",
         "virtual-astrometry-resources.json": "resources"}
SCHEMAS = {"pixels": "virtual-astrometry-pixels-v2", "optical": "virtual-optical-calibration-v1",  # PIX :122, OPT :230
           "uncertainty": "virtual-astrometric-uncertainty-v1", "measured": "virtual-measured-stars-v1",  # UNC :134, MEAS :176
           "resources": "virtual-astrometry-resources-v2"}  # RES :126
PARTITIONS = {"pixels": {"runs": "held-out-stationary-baseline", "tuning": "tuning-not-final"},  # PIX :36
              "optical": {"runs": "optical-calibration-held-out", "tuning": "optical-calibration-tuning-not-final"},  # OPT :65
              "uncertainty": {"runs": "uncertainty-held-out", "tuning": "uncertainty-tuning-not-final"},  # UNC :82
              "measured": {"runs": "measured-stars-held-out", "tuning": "measured-stars-tuning-not-final"}}  # MEAS :56
MONTHS = {"runs": ((1, 15), (5, 15), (9, 15)), "tuning": ((2, 10),)}  # PIX :34, OPT :61-64, UNC :79, MEAS :54
REALIZATIONS = {"runs": 20, "tuning": 8}  # UNC :81
READOUTS = ("native", "roi", "roi-bin2")  # OPT :41, UNC :273
FIT_FRAMES, PARAMETERS, GRID_POINTS = 4, 4, 5 * 5  # OPT :40; SESS B:600; PIX :211
STATUSES = ("Accepted", "Rejected", "Unavailable", "BudgetExceeded")  # AC B:196; OC :79
REFERENCE_FAILURES = ("association precision ", "independent withheld mapping ", "pose ", "warm search ran hypotheses",
                      "non-unique measured associations")  # REF :238-244, after "{id}: "
REFERENCE_LINES = dict(zip((238, 240, 241, 242, 244), REFERENCE_FAILURES))  # a text ending in a space is a prefix
NAMED = {"NaN": math.nan, "Infinity": math.inf, "-Infinity": -math.inf}  # AllowNamedFloatingPointLiterals (OPT :35)
JSON_OPTIONS_KINDS = ("pixels", "resources")  # PIX :137-140, RES :143-146; the other writers use EvidenceJsonOptions
NON_FINITE = ("a non-finite double or its named literal; this writer's JsonOptions (FIX :62) writes neither: System.Text.Json "
              "throws instead (framework behaviour, not verified in this repository)")


def keys(text):
    return frozenset(text.split())


def camel(name):
    """JsonNamingPolicy.CamelCase, which JsonSerializerDefaults.Web selects (FIX :62). System.Text.Json framework behaviour,
    transcribed from its FixCasing and not verified in this repository: a name not starting upper case is kept; otherwise
    leading upper-case characters are lowered, stopping before the last one that is followed by a non-upper character."""
    if not name or not name[0].isupper():
        return name
    chars = list(name)
    for i, char in enumerate(chars):
        if i == 1 and not char.isupper():
            break
        if i > 0 and i + 1 < len(chars) and not chars[i + 1].isupper():
            if chars[i + 1] == " ":
                chars[i] = char.lower()
            break
        chars[i] = char.lower()
    return "".join(chars)


def members(declaration, opener=None):
    """The serialized names of a quoted declaration's members: the anonymous object after opener, or, with no opener, a
    positional record's parameters. A record's declaration must end its parameter list with ");": it has no body, so no
    other property. Each member is split at a top-level comma; an anonymous member is "name = value" or a member access
    whose last identifier is its name. Raises ValueError on a malformed, unterminated or duplicated member list."""
    text = " ".join(declaration.split())
    start = text.index(opener) + len(opener) if opener else text.index("(", text.index(" record ")) + 1
    depth, part, parts, position = 0, "", [], start
    while True:
        if position >= len(text):
            raise ValueError(f"unterminated member list: {declaration}")
        char = text[position]
        if text.startswith("=>", position):
            part, position = part + "=>", position + 2
            continue
        if char in ")]}>" and depth == 0:
            parts.append(part)
            break
        if char == "," and depth == 0:
            parts.append(part)
            part, position = "", position + 1
            continue
        depth += 1 if char in "([{<" else -1 if char in ")]}>" else 0
        part, position = part + char, position + 1
    if opener and char != "}" or not opener and text[position:] != ");":
        raise ValueError(f"member list does not close as its declaration kind: {declaration}")
    names = []
    for part in parts:
        named = re.match(r"\s*(\w+)\s*=(?![=>])", part) if opener else None
        if named:
            names.append(named.group(1))
        elif opener and re.fullmatch(r"\s*[\w.]+\s*", part):
            names.append(re.findall(r"\w+", part)[-1])
        elif not opener and re.fullmatch(r"\s*[\w.<>\[\]?, ]+\s+\w+\s*", part):
            names.append(re.findall(r"\w+", part)[-1])
        else:
            raise ValueError(f"member {part.strip()!r} is not a declared member: {declaration}")
    names = [camel(name) for name in names]
    if len(names) != len(set(names)):
        raise ValueError(f"duplicated member names {names}: {declaration}")
    return frozenset(names)


def quoted(key, line):
    """The one quoted declaration of SOURCES[key] at line, its line at B; the harness record from DECLARATIONS."""
    found = [text for k, _, at_b, text in CONTENT_DECLARATIONS if (k, at_b) == (key, line)]
    found += [text for path, at, text in DECLARATIONS if (path, str(at)) == (key, line)]
    if len(found) != 1:
        raise ValueError(f"{len(found)} quoted declarations at {key}:{line}")
    return found[0]


TOP_KEYS = {
    "pixels": keys("schema partition projectionFamily revision databaseSha256 rowCount identitySha256 selectionIdentitySha256 "
                   "logicalExposurePolicy detectorValidity mappingInteriorMarginPixels finalExposureQualification reports grids "
                   "sourceGeometry sourceCoverage mappingCoverage failures"),
    "optical": keys("schema partition projectionFamily revision tieredCompilation runtime processorCount databaseSha256 "
                    "identitySha256 selectionIdentitySha256 fitter tolerances fitPlan validationPlan reports failures"),
    "uncertainty": keys("schema projectionFamily partition revision tieredCompilation runtime processorCount databaseSha256 "
                        "identitySha256 selectionIdentitySha256 estimator tolerances realizationsPerProfile coverage "
                        "frameResources frames sessions failures"),
    "measured": keys("schema partition projectionFamily revision tieredCompilation runtime processorCount databaseSha256 "
                     "identitySha256 selectionIdentitySha256 tolerances reports windowPressure failures"),
    "resources": keys("schema revision projectionFamily binaryRevision route databaseSha256 warmup measured concurrency "
                      "tieredCompilation runtime host os processors backlog workingSetMeaning initialProcessPeakBytes reports "
                      "rawSamples failures")}
PIXEL_ROW_KEYS = keys("caseId name index utc captureMs measurementMs detected candidateCount background noiseSigma assessment "
                      "metrics score reportedAcquisition sceneUtc layout parentPayloadSha256 configHash nominal detections payloadFile")
METRIC_KEYS = members(quoted("AC", "207"))  # AC B:207
METRIC_COUNTS = ("indexStars", "indexTriangles", "imageTriangles", "hypotheses", "distinctCandidates")  # SOL :47
NOMINAL_KEYS = members(quoted("PIX", "108"), "nominal = new {")  # PIX :108
SCORE_KEYS = members(quoted("REF", "245-256"), "return new {")  # REF :245-256
ASSOCIATION_ROW_KEYS = members(quoted("REF", "206-213"), "return new {")  # REF :206-213
WITHHELD_ROW_KEYS = members(quoted("REF", "223-229"), "return new {")  # REF :223-229
GEOMETRY_KEYS = keys("source mode identitySha256 pixel expectedRayAtMidpoint actualSky rayErrorDegrees "
                     "expectedScaleXArcsecondsPerPixel expectedScaleYArcsecondsPerPixel actualScaleXArcsecondsPerPixel "
                     "actualScaleYArcsecondsPerPixel scaleXRelativeError scaleYRelativeError")  # PIX :246
OPTICAL_REPORT_KEYS = keys("caseId truth truthNative nominalNative measurements fit errors withheldScores omittedDistortion")
FIT_KEYS = keys("kind resources singleFrameColdSolveBaseline status reasonCode rejections identitySha256 calibratedNative "
                "parameters frames validations diagnostics metrics")  # OPT :200-211
OMITTED_KEYS = members(quoted("OPT", "216"), "omittedDistortion = new {")  # OPT :216
ERROR_KEYS = keys("focalError principalError k1Error mappingError")
WITHHELD_KEYS = members(quoted("OPT", "179-185"), "withheldScores.Add(new {")  # OPT :179-185
TRUTH_KEYS = members(quoted("OPT", "43"))  # OPT :43
FRAME_RESULT_KEYS = members(quoted("OC", "86"))  # OC :86
VALIDATION_KEYS = members(quoted("OC", "95"))  # OC :95
PARAMETER_KEYS = members(quoted("OC", "82"))  # OC :82
DIAGNOSTIC_KEYS = members(quoted("OC", "99"))  # OC :99
REALIZATION_KEYS = members(quoted("UNC", "62"))  # UNC :62
SESSION_SHORT_KEYS = members(quoted("UNC", "280"), "return new {")  # UNC :280
SESSION_KEYS = members(quoted("UNC", "325"), "return new {")  # UNC :325
ROW_UNAVAILABLE_KEYS = members(quoted("UNC", "298"), "rows.Add(new {")  # UNC :298
ROW_KEYS = members(quoted("UNC", "310"), "rows.Add(new {")  # UNC :310
MEASURED_REPORT_KEYS = keys("caseId name utc sceneUtc parentPayloadSha256 eligibleTruthStars v1 v2 diagnostics")
V1_KEYS = keys("algorithm measurementMs resources detected candidateCount baseline status reasonCode associations")  # MEAS :121
V2_KEYS = keys("algorithm settingsIdentitySha256 measurementMs resources allocatedBytesPerPixel measurementStatus detected "
               "candidateCount exclusionCounts missedEligibleReasonCounts medianBackground medianNoiseSigma candidate status "
               "reasonCode associations")  # MEAS :129-140
MEASURED_DIAGNOSTIC_KEYS = keys("identitySha256 fittingRmsPixels verificationRmsPixels medianNormalizedResidualSquared "
                                "covarianceStatus conditioning occupancy parameterBoundHits unmatchedPredictionReasonCounts "
                                "unassociatedDetectionReasonCounts bins")  # MEAS :145
MEASURED_SCORE_KEYS = keys("eligibleStars recovered recall associations falseAssociations centroidRmsPixels "
                           "centroidP95Pixels")  # MEAS :254
RESOURCE_ROW_KEYS = keys("name configHash identitySha256 captureMedianMs captureP95Ms detectionMedianMs detectionP95Ms "
                         "medianAllocatedBytes operationsPerSecond samples")  # RES :105
PREFIX_KEYS = members(quoted(HARNESS, "504"))  # harness :504

# Every serialized identity: its producer's form and the observable condition, if any, under which it is null.
# L: AstrometricIdentity.Hash/RequireSha256 and SettingsIdentity, Convert.ToHexStringLower (AC B:239, B:241; SSM :242).
# U: CaptureContractJson.ComputeCanonicalJsonSha256, Convert.ToHexString (CCJ :56), serialized unnormalized (U1-U3).
# An identity path not listed is a finding: it has no quoted producer (this includes ReadoutIdentitySha256, N5).
FORMS = {"L": re.compile(r"[0-9a-f]{64}"), "U": re.compile(r"[0-9A-F]{64}")}
NULL_RULES = {
    "N1": lambda parent: parent.get("mode") == "Blind",  # SOL :44, :47, B:118: previous?.IdentitySha256
    "N2": lambda parent: parent.get("status") != "Accepted",  # SOL B:118; CORE: every non-accepted result carries []
    "N3": lambda parent: parent.get("reasonCode") == "shared-calibration-covariance-not-supplied"}  # UNCS :303, :306, :309
CATALOG_IDENTITIES = {"$.identitySha256": ("L", None), "$.selectionIdentitySha256": ("L", None)}  # AC B:106, B:109
IDENTITY_PATHS = {
    "pixels": {**CATALOG_IDENTITIES,  # PIX :128
               "$.reports[].assessment.identitySha256": ("L", None),  # SOL B:119
               "$.reports[].assessment.calibrationIdentitySha256": ("L", None),  # SOL B:115; AC :32
               "$.reports[].assessment.catalogIdentitySha256": ("L", None),  # SOL B:115
               "$.reports[].assessment.catalogSelectionIdentitySha256": ("L", None),  # SOL B:115
               "$.reports[].assessment.settingsIdentitySha256": ("L", None),  # SOL B:115; AC B:173
               "$.reports[].assessment.associationIdentitySha256": ("L", "N2"),  # SOL B:118
               "$.reports[].assessment.previousAssessmentIdentitySha256": ("L", "N1"),  # SOL :44, B:118
               "$.reports[].assessment.frame.observerIdentitySha256": ("U", None),  # U1: FIX :319
               "$.reports[].assessment.frame.detectionSettingsIdentitySha256": ("U", None),  # U2: FIX :320
               "$.reports[].nominal.identitySha256": ("L", None),  # PIX :108; AC :32
               "$.reports[].nominal.readoutIdentitySha256": ("L", None),  # PIX :108; AC :31; FIX :163
               "$.sourceGeometry[].identitySha256": ("L", None)},  # PIX :246
    "optical": {**CATALOG_IDENTITIES,  # OPT :238
                "$.reports[].fit.identitySha256": ("L", None),  # OPT :203; OC :173
                "$.reports[].fit.frames[].readoutIdentitySha256": ("U", None),  # U3: OPT :104; SESS :87, B:619
                "$.reports[].fit.validations[].assessmentIdentitySha256": ("L", None)},  # SESS B:174; OC :95
    "uncertainty": {**CATALOG_IDENTITIES,  # UNC :142
                    "$.frames[].identitySha256": ("L", None),  # N4: UNC :224; UNCS :244
                    "$.sessions[].identitySha256": ("L", None),  # UNC :325; OC :173
                    "$.sessions[].rows[].identitySha256": ("L", None),  # UNC :310; UNCS :244
                    "$.sessions[].rows[].sharedCalibration.calibrationCovarianceIdentitySha256": ("L", "N3")},  # UNCS :93, :130
    "measured": {**CATALOG_IDENTITIES,  # MEAS :184
                 "$.reports[].diagnostics.identitySha256": ("L", None),  # MEAS :145; DIAG :142
                 "$.reports[].v2.settingsIdentitySha256": ("L", None)},  # MEAS :129; SSM :242
    "resources": {"$.reports[].identitySha256": ("L", None)}}  # RES :105; AC :32
ASSOCIATION_PATHS = {  # every serialized association list or count; any other is a finding
    "pixels": {"$.reports[].score.associationRows"},  # PIX :72; REF :201, :254
    "optical": {"$.reports[].withheldScores[].score.associationRows"},  # OPT :179; REF :254
    "uncertainty": set(), "resources": set(),
    "measured": {"$.reports[].v1.associations", "$.reports[].v1.baseline.associations",  # MEAS :121, :298
                 "$.reports[].v2.associations", "$.reports[].v2.candidate.associations"}}  # MEAS :140, :298


class InputError(Exception):
    pass


def load(path):
    try:
        with open(path, encoding="utf-8") as stream:
            return json.load(stream)
    except (OSError, ValueError) as error:
        raise InputError(f"cannot read {path}: {error}") from error


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def git(*args):
    repo = Path(__file__).resolve().parent
    result = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise InputError(f"git {' '.join(args)}: {result.stderr.strip()}")
    return result.stdout


def declared(text, declaration):
    return re.search(re.escape(declaration).replace(r"\ ", r"\s+"), text) is not None


def integer(value):
    return isinstance(value, int) and not isinstance(value, bool)


def number(value):
    # Comparing an arbitrary-size JSON integer with the double bound avoids math.isfinite/float's OverflowError.
    return (isinstance(value, (int, float)) and not isinstance(value, bool)
            and -sys.float_info.max <= value <= sys.float_info.max)


def ordinal(value):
    """StringComparer.Ordinal compares UTF-16 code units, including unpaired surrogates, not Unicode code points."""
    return value.encode("utf-16-be", errors="surrogatepass")


def same(left, right):
    """Equal and of the same JSON type: a string revision is never equal to a number, nor 0 to false."""
    return type(left) is type(right) and left == right


def utc_of(value):
    try:
        return datetime.fromisoformat(value) if isinstance(value, str) else None
    except ValueError:
        return None


def percentile95(values):
    """The harness's own nearest rank: values.Order().ElementAt((int)Math.Ceiling(.95 * values.Count) - 1), harness :519.

    It is compared exactly, with no tolerance: the harness's p95 is one element of the same list it serializes as
    samplesMs (:231), System.Text.Json writes each double in round-trip form, and 0.95 * n is the same IEEE product here.
    """
    return sorted(values)[math.ceil(0.95 * len(values)) - 1]


def non_finite(data):
    """Each path holding a non-finite double (a bare NaN or infinity, or a number beyond the double range such as 1e309,
    which json reads as an infinity, or an integer-form token outside the double range) or a string equal to a named
    literal, with its count. Applied only to reports
    written with JsonOptions (FIX :62); the string rule is not limited to members the writer declares as doubles."""
    found, stack = {}, [(data, "$")] if isinstance(data, (dict, list)) else []
    while stack:
        node, path = stack.pop()
        for key, value in node.items() if isinstance(node, dict) else enumerate(node):
            child = f"{path}.{key}" if isinstance(node, dict) else f"{path}[]"
            if (isinstance(value, (int, float)) and not isinstance(value, bool) and not number(value)
                    or isinstance(value, str) and value in NAMED):
                found[child] = found.get(child, 0) + 1
            elif isinstance(value, (dict, list)):
                stack.append((value, child))
    return dict(sorted(found.items()))


def index_bound(training):
    """IndexPrefixBound's bytes for a training-star count, harness :515-516."""
    rows = min(training, 1501)
    return 8 * rows * (rows - 1) // 2 + 24 * rows + 8 * training + 24 * training + 48


def profiles(family):
    return MONO_VIEWS + (CFA_VIEWS[:1] if FAMILY_MODELS[family] == "EquidistantFisheye" else ())  # fixture :85-90, :93


def derive():
    """The B-only producers' code, messages and bounds, the association minimum and the reference scorer's thresholds
    (REF :239, :241), from their quoted literals only."""
    texts = {(key, line): text for key, line, text in B_ONLY_DECLARATIONS}
    core = next((text for key, _, line, text in CONTENT_DECLARATIONS if (key, line) == ("CORE", "419")), "")

    def message(text):
        return re.fullmatch(r"([^{}]*)\{[^{}]*\}([^{}]*)", re.search(r'\$"([^"]*)"', text).group(1)).groups()
    try:
        code = re.search(r'AstrometricAssessmentStatus\.Unavailable, "([^"]+)"', texts["SOL", "50"]).group(1)
        before, after = message(texts["SOL", "50"])
        status85, code85 = re.search(r'AstrometricAssessmentStatus\.(\w+), "([^"]+)", \$"', texts["SOL", "84-87"]).groups()
        before85, after85 = message(texts["SOL", "84-87"])
        stars = int(re.search(r"= (\d+);", texts["MAP", "77"]).group(1))
        session = {"code": re.search(r'OpticalCalibrationStatus\.Unavailable, "([^"]+)"', texts["SESS", "95-96"]).group(1),
                   "parts": list(message(texts["SESS", "95-96"])),
                   "rejections": re.findall(r'\["([^"]+)"\]', texts["SESS", "95-96"])}
        fit, held = (int(re.search(rf"if \({name}\.Count < (\d+)\)", core).group(1)) for name in ("fit", "held"))
        withheld, rms, p95 = re.search(r"withheld\.Length < (\d+) \|\| missing > 0 \|\| rms > ([\d.]+) \|\| p95 is null or > ([\d.]+)\)",
                                       quoted("REF", "239")).groups()
        pose, focal = re.search(r"poseError > ([\d.]+) \|\| focalError > ([\d.]+)\)", quoted("REF", "241")).groups()
    except (AttributeError, KeyError, ValueError) as error:
        raise InputError(f"cannot derive the producers from their declarations: {error}") from error
    return {"code": code, "b50": re.escape(before) + r"(\d+)" + re.escape(after), "solverParts": [before, after],
            "b85": f"{before85}{stars}{after85}", "b85Status": status85, "b85Code": code85, "maximumEvidenceStars": stars,
            "markers": [code, before, before85], "session": session, "minimumFitting": fit, "minimumWithheld": held,
            "referenceWithheld": int(withheld), "referenceRms": float(rms), "referenceP95": float(p95),
            "referencePose": float(pose), "referenceFocal": float(focal)}


class Report:
    """One writer report against its declared writer: header, every identity and association, inventory and producers.

    Findings are grouped by rule and abstract path, so a defect repeated on every row is one finding with a count.
    """

    def __init__(self, label, data, kind, family, section, role, derived):
        self.label, self.data, self.kind, self.family, self.section, self.role, self.d = (
            label, data, kind, family, section, role, derived)
        self.findings, self.markers, self.echoes, self.observations = {}, {}, [], {}
        self.cold_median = None
        failures = data.get("failures") if isinstance(data, dict) else None
        valid = isinstance(failures, list) and all(isinstance(line, str) for line in failures)
        self.failures = failures if valid else []

    def add(self, group, message=None):
        self.findings.setdefault(group, [message or group, 0])[1] += 1

    def observe(self, name):
        self.observations[name] = self.observations.get(name, 0) + 1

    def messages(self):
        return [f"{self.label}: {message}" + (f" ({count} occurrences)" if count > 1 else "")
                for message, count in self.findings.values()]

    def exact(self, value, expected, where, case=""):
        if not isinstance(value, dict):
            self.add(f"object {where}", f"{where} {case}: {type(value).__name__} where the writer serializes an object")
            return False
        if set(value) != expected:
            self.add(f"keys {where}", f"{where} {case}: keys are not the writer's; missing {sorted(expected - set(value))}, "
                                      f"undeclared {sorted(set(value) - expected)}")
            return False
        return True

    def failure(self, text=None, prefix=None, pattern=None, exclude=()):
        """The index of the first failure line that is text, or starts with prefix but not prefix + exclude, or is pattern."""
        for index, line in enumerate(self.failures):
            if (text is not None and line == text
                    or prefix is not None and line.startswith(prefix) and not line[len(prefix):].startswith(exclude)
                    or pattern is not None and re.fullmatch(pattern, line)):
                return index
        return None

    def consume(self, container, key):
        return self.markers.pop((id(container), key), None) is not None

    def check(self, revision):
        if not isinstance(self.data, dict):
            self.add("not an object", f"report is {type(self.data).__name__}, not an object")
            return self
        self.scan()
        if self.kind in JSON_OPTIONS_KINDS:
            for path, count in non_finite(self.data).items():
                self.findings[f"non-finite {path}"] = [f"{path}: {NON_FINITE}", count]
        try:
            self.header(revision)
            self.presence()
            getattr(self, self.kind)()
        except (KeyError, TypeError, AttributeError, ValueError, IndexError) as error:
            self.add("malformed", f"malformed content stopped the inventory: {type(error).__name__}: {error}")
        try:
            self.producers()
        except (KeyError, TypeError, AttributeError, ValueError, IndexError) as error:
            self.add("malformed producers", f"malformed content stopped the producer check: {type(error).__name__}: {error}")
        return self

    def header(self, revision):
        data = self.data
        self.exact(data, TOP_KEYS[self.kind], "$")
        declared_values = [("schema", SCHEMAS[self.kind]), ("projectionFamily", self.family), ("revision", revision)]
        if self.kind in PARTITIONS:
            declared_values.append(("partition", PARTITIONS[self.kind][self.section]))
        if self.kind == "uncertainty":
            declared_values.append(("realizationsPerProfile", REALIZATIONS[self.section]))
        for key, value in declared_values:
            if not same(data.get(key), value):
                self.add(f"header {key}", f"$.{key} {data.get(key)!r}, declared {value!r}")
        if self.failures is not data.get("failures"):
            self.add("failures", "$.failures is not a list of strings")
        for key in ("frames", "sessions") if self.kind == "uncertainty" else ("reports",):
            if not isinstance(data.get(key), list):
                self.add(f"list {key}", f"$.{key} is not a list")

    def scan(self):
        """Every identity and association in the report, wherever it is, and every string carrying a producer marker."""
        markers = self.d["markers"]
        stack = [(self.data, "$")]
        while stack:
            node, path = stack.pop()
            items = node.items() if isinstance(node, dict) else enumerate(node)
            for key, value in items:
                child = f"{path}.{key}" if isinstance(node, dict) else f"{path}[]"
                if isinstance(node, dict):
                    if any(marker in key for marker in markers):
                        self.add(f"marker key {child}", f"{child}: a key carries a producer marker")
                    if key.lower().endswith("identitysha256"):
                        self.identity(node, key, child)
                    if key.lower() in ("associations", "associationrows"):
                        self.association(node, key, child)
                if isinstance(value, str):
                    if any(marker in value for marker in markers):
                        self.markers[id(node), key] = (child, value)
                elif isinstance(value, (dict, list)):
                    stack.append((value, child))

    def identity(self, parent, key, path):
        rule, value = IDENTITY_PATHS[self.kind].get(path), parent[key]
        if rule is None:
            self.add(f"untraced {path}", f"{path}: identity path has no quoted producer")
            return
        form, null = rule
        if null is not None and NULL_RULES[null](parent):
            if value is not None:
                self.add(f"{null} {path}", f"{path}: {value!r} where {null} requires null")
        elif not (isinstance(value, str) and FORMS[form].fullmatch(value)):
            condition = f" and the row does not meet {null}" if null else ""
            self.add(f"form {path}", f"{path}: {value!r} is not the producer's {form} form{condition}")

    def association(self, parent, key, path):
        value = parent[key]
        if path not in ASSOCIATION_PATHS[self.kind]:
            self.add(f"untraced {path}", f"{path}: association path has no quoted writer")
        elif key == "associationRows":
            if not isinstance(value, list) or not value:
                self.add(f"empty {path}", f"{path}: {value!r} is not a non-empty association list")
                return
            if not all(isinstance(row, dict) and set(row) == ASSOCIATION_ROW_KEYS and isinstance(row["catalogId"], str)
                       and integer(row["detectionIndex"]) and isinstance(row["verification"], bool)
                       and isinstance(row["expectedNearestId"], str) and number(row["residualPixels"]) for row in value):
                self.add(f"shape {path}", f"{path}: an association row is not the writer's shape (REF :208)")
                return
            ids = [ordinal(row["catalogId"]) for row in value]
            if any(left > right for left, right in zip(ids, ids[1:])):
                self.add(f"order {path}", f"{path}: catalogId rows are not in nondecreasing StringComparer.Ordinal order "
                                         "(SOL A:88/B:97; REF :201-208); equal IDs have no additional ordering rule")
            # The minimum counts each detection once: a repeated index adds no star (and draws REF :244, score()).
            fitting = {row["detectionIndex"] for row in value if not row["verification"]}
            withheld = {row["detectionIndex"] for row in value if row["verification"]} - fitting
            if len(fitting) < self.d["minimumFitting"] or len(withheld) < self.d["minimumWithheld"]:
                once = "" if len(fitting) + len(withheld) == len(value) else f" counted once per detection index, of {len(value)} rows"
                self.add(f"count {path}", f"{path}: {len(fitting)} fitting and {len(withheld)} withheld rows{once}; an "
                                          f"accepted fit has at least {self.d['minimumFitting']} and {self.d['minimumWithheld']} (CORE B:419)")
        elif not integer(value) or value < 0:
            self.add(f"count {path}", f"{path}: {value!r} is not a nonnegative association count")

    def value(self, item):
        """A serialized double as this writer can write it: a finite number, or in an optical report one of the named
        literals EvidenceJsonOptions writes (OPT :33-35); None for anything else."""
        if number(item):
            return float(item)
        return NAMED[item] if self.kind == "optical" and isinstance(item, str) and item in NAMED else None

    def score(self, score, where, scored, warm=None):
        """REF :201-256 for the score of scored: its precision over every association row, its withheld rows and counts,
        and exactly the REF :238-244 failure lines its serialized values decide (a line is matched by its "{id}: <text>"
        prefix only). warm is REF :242's condition, or None where it is not serialized beside the score (OPT :176)."""
        if not self.exact(score, SCORE_KEYS, where, scored):
            return
        d, holds, rows = self.d, {}, score["associationRows"]
        if isinstance(rows, list) and all(isinstance(row, dict) and isinstance(row.get("catalogId"), str)
                                          and isinstance(row.get("expectedNearestId"), str) for row in rows):
            precision = self.value(score["precision"])
            if not rows and self.kind == "pixels":
                self.add(f"precision {where}", f"{where} {scored}: no association rows, so the writer's precision is 0/0 = NaN "
                                               "(REF :215), which its JsonOptions cannot write (FIX :62)")
            elif not rows and not same(score["precision"], "NaN"):
                self.add(f"precision {where}", f"{where} {scored}: precision {score['precision']!r} with no association rows; "
                                               'the writer computes 0/0 = NaN (REF :215) and writes "NaN" (OPT :33-35)')
            elif not rows:
                holds[238] = True
            else:
                want = sum(row["catalogId"] == row["expectedNearestId"] for row in rows) / len(rows)
                if precision is None or precision != want:
                    self.add(f"precision {where}", f"{where} {scored}: precision {score['precision']!r} is not {want!r}, the share of "
                                                   f"its {len(rows)} association rows whose catalogId is expectedNearestId (REF :215)")
                else:
                    holds[238] = want != 1
        if isinstance(rows, list) and all(isinstance(row, dict) and integer(row.get("detectionIndex")) for row in rows):
            indexes = [row["detectionIndex"] for row in rows]
            holds[244] = len(set(indexes)) != len(indexes)
        withheld = score["withheld"]
        if not isinstance(withheld, list):
            self.add(f"withheld {where}", f"{where} {scored}: withheld {withheld!r} is not a list (REF :217-230)")
        else:
            rows_valid, nulls = True, 0
            for row in withheld:
                if not self.exact(row, WITHHELD_ROW_KEYS, f"{where}.withheld[]", scored):
                    rows_valid = False
                    continue
                residual = row["residualPixels"]
                if (not isinstance(row["id"], str) or (residual is None) != (row["measuredMapping"] is None)
                        or residual is not None and self.value(residual) is None):
                    self.add(f"withheld row {where}", f"{where}.withheld[] {scored}: id {row['id']!r}, residualPixels {residual!r} "
                                                      "is not null exactly when measuredMapping is, or not a double (REF :223-229)")
                    rows_valid = False
                nulls += residual is None
            count, missing, p95 = score["withheldCount"], score["missingMappings"], score["withheldP95Pixels"]
            rms, p95_value = self.value(score["withheldRmsPixels"]), None if p95 is None else self.value(p95)
            if not rows_valid:
                pass
            elif (not same(count, len(withheld)) or not same(missing, nulls) or rms is None
                  or (p95 is None) != (nulls == len(withheld)) or p95 is not None and p95_value is None):
                self.add(f"withheld counts {where}", f"{where} {scored}: withheldCount {count!r}, missingMappings {missing!r}, "
                                                     f"withheldRmsPixels {score['withheldRmsPixels']!r}, withheldP95Pixels {p95!r} are not "
                                                     f"those of its {len(withheld)} withheld rows, {nulls} without a mapping (REF :231-234)")
            else:
                holds[240] = (len(withheld) < d["referenceWithheld"] or nulls > 0 or rms > d["referenceRms"]
                              or p95 is None or p95_value > d["referenceP95"])
        pose, focal = self.value(score["poseErrorDegrees"]), self.value(score["focalRelativeError"])
        if pose is None or focal is None:
            self.add(f"pose {where}", f"{where} {scored}: poseErrorDegrees {score['poseErrorDegrees']!r} or focalRelativeError "
                                      f"{score['focalRelativeError']!r} is not a double this writer writes (REF :235-237)")
        else:
            holds[241] = pose > d["referencePose"] or focal > d["referenceFocal"]
        if warm is not None:
            holds[242] = warm
        for line, condition in sorted(holds.items()):
            count = sum(self.reference(failure, scored, REFERENCE_LINES[line]) for failure in self.failures)
            if count != int(condition):
                self.add(f"REF:{line} {where}", f"{scored}: {count} REF :{line} failure lines where its condition "
                                                f"{'holds' if condition else 'does not hold'}; the writer adds {int(condition)}")

    @staticmethod
    def reference(failure, scored, text):
        prefix = f"{scored}: {text}"
        return failure.startswith(prefix) if text.endswith(" ") else failure == prefix

    def orphans(self, scored, skip=None):
        """REF :238-244 lines for an id with no score: the writer adds them only inside Score (REF :193-244)."""
        for index, failure in enumerate(self.failures):
            if index != skip and any(self.reference(failure, scored, text) for text in REFERENCE_FAILURES):
                self.add("REF orphan", f"{scored}: REF failure line {failure[:100]!r} for an id the writer did not score (REF :193-244)")

    def presence(self):
        """Every declared identity is serialized wherever its writer writes it (a null is serialized, FIX :62)."""
        for path in IDENTITY_PATHS[self.kind]:
            parts, nodes = path[2:].replace("[]", ".[]").split("."), [self.data]
            for part in parts[:-1]:
                children = []
                for node in nodes:
                    if part == "[]" and isinstance(node, list):
                        children.extend(node)
                    elif part != "[]" and isinstance(node, dict) and part in node:
                        children.append(node[part])
                    else:
                        self.add(f"absent {path}", f"{path}: no {part} where the writer serializes one")
                if part == "[]" and self.kind == "uncertainty":  # UNC :280 and :298 forms carry no identity
                    children = [c for c in children if not (isinstance(c, dict) and (set(c) == SESSION_SHORT_KEYS or "totalReason" in c))]
                nodes = children
            if any(not isinstance(node, dict) or parts[-1] not in node for node in nodes):
                self.add(f"absent {path}", f"{path}: not serialized where the writer serializes it")

    def pixels(self):
        """PIX :34-134 and :206-246."""
        rows, expected = self.data["reports"], []
        for month, day in MONTHS[self.section]:
            for profile in profiles(self.family):
                for index in range(CAPTURES):
                    utc = datetime(2026, month, day, 8, tzinfo=timezone.utc) + timedelta(seconds=60 * index)
                    for view in (profile, *(CFA_VIEWS[1:] if profile == CFA_VIEWS[0] else ())):
                        expected.append((f"{month:02d}-{view}-{index}", view, index, utc))
        ids = [row.get("caseId") if isinstance(row, dict) else None for row in rows]
        if ids != [case for case, *_ in expected]:
            self.add("inventory", f"$.reports: {len(rows)} case rows are not the writer's {len(expected)} cases in order")
        mapped, cold, cold_valid = [], [], True
        for row, (case, view, index, utc) in zip(rows, expected):
            if not self.exact(row, PIXEL_ROW_KEYS, "$.reports[]", case) or row["caseId"] != case:
                continue
            if not same(row["name"], view) or not same(row["index"], index) or utc_of(row["utc"]) != utc:
                self.add("row identity", f"{case}: view {row['name']!r}, capture {row['index']!r}, utc {row['utc']!r}")
            metrics = row["metrics"] if self.exact(row["metrics"], METRIC_KEYS, "$.reports[].metrics", case) else None
            if metrics is not None and (not number(metrics["elapsedMilliseconds"]) or metrics["elapsedMilliseconds"] < 0
                                        or not integer(metrics["hypotheses"]) or not 0 <= metrics["hypotheses"] <= 2 ** 31 - 1):
                self.add("metrics", f"{case}: elapsedMilliseconds {metrics['elapsedMilliseconds']!r} is not a finite nonnegative "
                                    f"Stopwatch reading (INT :32-33; SOL :45, :47, B:86, B:102), or hypotheses "
                                    f"{metrics['hypotheses']!r} is not a nonnegative Int32 solver counter "
                                    "(CORE A:103/B:119, A:142/B:158; AC A:156/B:207)")
                metrics = None
            if same(row["index"], 0):
                cold_valid = cold_valid and metrics is not None
                cold += [metrics["elapsedMilliseconds"]] if metrics is not None else []
            self.exact(row["nominal"], NOMINAL_KEYS, "$.reports[].nominal", case)
            assessment, mode = row["assessment"], "Blind" if index == 0 else "Warm"  # PIX :67
            if not isinstance(assessment, dict):
                self.add("assessment", f"{case}: assessment is not an object")
                continue
            if assessment.get("mode") not in ("Blind", "Warm") or assessment.get("status") not in STATUSES:
                self.add("mode/status", f"{case}: mode {assessment.get('mode')!r}, status {assessment.get('status')!r}")
            elif assessment["mode"] != mode and self.failure(text=f"{case}: expected {mode} solve") is None:
                self.add("PIX:68", f"{case}: {assessment['mode']} solve without the PIX :68 failure")
            if assessment.get("status") == "Accepted" and assessment.get("parameters") is not None:  # AC B:221
                mapped.append((row, assessment))
                warm = None if metrics is None or assessment["mode"] not in ("Blind", "Warm") else (
                    assessment["mode"] == "Warm" and metrics["hypotheses"] != 0)  # REF :242
                self.score(row["score"], "$.reports[].score", case, warm)
                continue
            echo = self.failure(text=f"{case}: {assessment.get('reason')}")
            if row["score"] is not None or not isinstance(assessment.get("reason"), str) or echo is None:
                self.add("PIX:74", f"{case}: unmapped row with a score or without its PIX :74 failure")
            self.orphans(case, echo)
        # Match the frozen Python aggregation exactly; its addition of two finite middle samples can overflow.
        if self.section == "runs" and cold_valid and cold:
            median = statistics.median(cold)
            if not number(median) or median <= 0:
                self.add("cold median", f"$.reports: the median capture-0 solve time {median!r} of {len(cold)} rows "
                                        "is not finite and positive (issue-1167-evaluate.py:89-91, :107)")
            else:
                self.cold_median = median
        geometry = self.data["sourceGeometry"]
        if not isinstance(geometry, list) or len(geometry) != GRID_POINTS * len(mapped):
            self.add("geometry", f"$.sourceGeometry: {len(geometry) if isinstance(geometry, list) else geometry!r} entries, "
                                 f"declared {GRID_POINTS} per mapped case ({len(mapped)})")
        else:
            for position, entry in enumerate(geometry):
                row, assessment = mapped[position // GRID_POINTS]
                if self.exact(entry, GEOMETRY_KEYS, "$.sourceGeometry[]", row["caseId"]) and not (
                        same(entry["source"], row["caseId"]) and same(entry["mode"], assessment.get("mode"))
                        and same(entry["identitySha256"], assessment.get("identitySha256"))):
                    self.add("geometry source", f"$.sourceGeometry[] {position}: not its mapped case {row['caseId']}")
        for key in ("grids", "sourceCoverage", "mappingCoverage"):
            if not isinstance(self.data[key], list):
                self.add(f"list {key}", f"$.{key} is not a list")

    def optical(self):
        """OPT :61-216; SESS :82-88; OC :82-99, :201."""
        reports = self.data["reports"]
        expected = [f"{month:02d}" for month, _ in MONTHS[self.section]]
        if [r.get("caseId") if isinstance(r, dict) else None for r in reports] != expected:
            self.add("inventory", f"$.reports: case ids are not the writer's {expected}")
        for position, report in enumerate(reports):
            case = report.get("caseId") if isinstance(report, dict) else None
            if not self.exact(report, OPTICAL_REPORT_KEYS, "$.reports[]", case):
                continue
            fit, truth = report["fit"], report["truth"]
            if self.exact(truth, TRUTH_KEYS, "$.reports[].truth", case) and not number(truth["k1"]):
                self.add("truth k1", f"{case}: truth k1 {truth['k1']!r}")
            self.exact(report["omittedDistortion"], OMITTED_KEYS, "$.reports[].omittedDistortion", case)
            if not self.exact(fit, FIT_KEYS, "$.reports[].fit", case):
                continue
            if not same(fit["kind"], "cold" if position == 0 else "session"):  # OPT :125
                self.add("fit kind", f"{case}: fit kind {fit['kind']!r}")
            status, rejections, frames, validations = fit["status"], fit["rejections"], fit["frames"], fit["validations"]
            if status not in STATUSES or not isinstance(rejections, list) or not all(isinstance(r, str) for r in rejections):
                self.add("fit status", f"{case}: fit status {status!r}, rejections {rejections!r}")
                continue
            accepted = status == "Accepted" and fit["calibratedNative"] is not None  # OC :201
            if not isinstance(frames, list) or not 1 <= len(frames) <= FIT_FRAMES or accepted and len(frames) != FIT_FRAMES:
                self.add("fit frames", f"{case}: {len(frames) if isinstance(frames, list) else frames!r} fit frames")
            else:
                for frame in frames:
                    self.exact(frame, FRAME_RESULT_KEYS, "$.reports[].fit.frames[]", case)
            limit = len(READOUTS)
            if not isinstance(validations, list) or len(validations) > limit or accepted and len(validations) != limit:
                self.add("fit validations", f"{case}: {len(validations) if isinstance(validations, list) else validations!r} validations")
            else:
                for validation in validations:
                    self.exact(validation, VALIDATION_KEYS, "$.reports[].fit.validations[]", case)
            withheld = report["withheldScores"]
            if not accepted:
                line = f"{case}: calibration {status} {fit['reasonCode']}: {','.join(rejections)}"  # OPT :145
                echo = self.failure(text=line)
                if withheld != [] or report["errors"] is not None or echo is None:
                    self.add("OPT:145", f"{case}: unaccepted fit with withheld scores or errors, or without its OPT :145 failure")
                else:
                    self.echoes.append(("OPT:145", echo, line, case))
                for readout in READOUTS:
                    self.orphans(f"{case}-{readout}-withheld")
                continue
            self.exact(report["errors"], ERROR_KEYS, "$.reports[].errors", case)
            if not isinstance(withheld, list):
                self.add("withheld", f"{case}: withheldScores is not a list")
                continue
            pointer = 0
            for readout in READOUTS:  # OPT :172-179
                entry = withheld[pointer] if pointer < len(withheld) else None
                if isinstance(entry, dict) and entry.get("readout") == readout:
                    pointer += 1
                    if self.exact(entry, WITHHELD_KEYS, "$.reports[].withheldScores[]", case):
                        self.score(entry["score"], "$.reports[].withheldScores[].score", f"{case}-{readout}-withheld")
                    continue
                prefix = f"{case}-{readout}-withheld: "
                echo = self.failure(prefix=prefix, exclude=REFERENCE_FAILURES)
                if echo is None:
                    self.add("OPT:178", f"{case}-{readout}: no withheld score and no OPT :178 failure")
                else:
                    self.echoes.append(("OPT:178", echo, self.failures[echo][len(prefix):], case))
                self.orphans(prefix[:-2])
            if pointer != len(withheld):
                self.add("withheld order", f"{case}: withheld scores are not the writer's readouts in order")

    def uncertainty(self):
        """UNC :79-325."""
        frames, pointer = self.data["frames"], 0
        for month, _ in MONTHS[self.section]:
            for profile in profiles(self.family):
                for index in range(REALIZATIONS[self.section]):
                    frame = frames[pointer] if pointer < len(frames) else None
                    if isinstance(frame, dict) and frame.get("profile") == profile and same(frame.get("index"), index):
                        pointer += 1
                        self.exact(frame, REALIZATION_KEYS, "$.frames[]", f"{month:02d}-{profile}-{index}")
                        continue
                    prefix = f"{month:02d}-{profile}-{index}: solve not accepted: "  # UNC :197, :203
                    echo = self.failure(prefix=prefix)
                    if echo is None:
                        self.add("UNC:203", f"{prefix[:-21]}: no frame and no UNC :203 failure")
                    else:
                        self.echoes.append(("UNC:203", echo, self.failures[echo][len(prefix):], prefix[:-21]))
        if pointer != len(frames):
            self.add("frames", f"$.frames: {len(frames) - pointer} frames are not the writer's inventory in order")
        sessions = self.data["sessions"]
        expected = [f"{month:02d}-session" for month, _ in MONTHS[self.section]]  # UNC :254
        if [s.get("caseId") if isinstance(s, dict) else None for s in sessions] != expected:
            self.add("sessions", f"$.sessions: case ids are not the writer's {expected}")
        for session in sessions:
            if not isinstance(session, dict):
                continue
            case = session.get("caseId")
            if set(session) == SESSION_SHORT_KEYS:
                line = f"{case}: calibration {session['status']} {session['reasonCode']}"  # UNC :280-283
                echo = self.failure(text=line)
                if echo is None:
                    self.add("UNC:282", f"{case}: short session without its UNC :282 failure")
                else:
                    self.echoes.append(("UNC:282", echo, line, case))
                continue
            if not self.exact(session, SESSION_KEYS, "$.sessions[]", case):
                continue
            if not (isinstance(session["sharedCalibrationIdentity"], str) and FORMS["L"].fullmatch(session["sharedCalibrationIdentity"])):
                self.add("shared identity", f"{case}: sharedCalibrationIdentity {session['sharedCalibrationIdentity']!r} is not L (UNCS :93)")
            rows, pointer, full = session["rows"], 0, 0
            for readout in READOUTS:
                row = rows[pointer] if pointer < len(rows) else None
                if isinstance(row, dict) and row.get("readout") == readout:
                    pointer += 1
                    if "totalReason" in row:
                        if self.exact(row, ROW_UNAVAILABLE_KEYS, "$.sessions[].rows[]", case):
                            pattern = (re.escape(f"{case}-{readout}: total ") + r"\S+ " + re.escape(f"{row['reasonCode']} {row['totalReason']}"))
                            if self.failure(pattern=pattern) is None:
                                self.add("UNC:298", f"{case}-{readout}: unavailable row without its UNC :298 failure")
                    elif self.exact(row, ROW_KEYS, "$.sessions[].rows[]", case):
                        full += 1
                        if not isinstance(row["sharedCalibration"], dict):
                            self.add("shared calibration", f"{case}-{readout}: sharedCalibration is not an object")
                    continue
                prefix = f"{case}-{readout}: "
                echo = self.failure(prefix=prefix, exclude=("total ",))
                if echo is None:
                    self.add("UNC:295", f"{case}-{readout}: no row and no UNC :295 failure")
                else:
                    self.echoes.append(("UNC:295", echo, self.failures[echo][len(prefix):], case))
            if pointer != len(rows):
                self.add("rows", f"{case}: rows are not the writer's readouts in order")
            cross = session["crossCovarianceFirstPair"]
            if (full >= 2) != isinstance(cross, list) or full < 2 and cross is not None:
                self.add("cross covariance", f"{case}: crossCovarianceFirstPair {type(cross).__name__} with {full} estimated rows")

    def measured(self):
        """MEAS :54-145, :254-298."""
        reports = self.data["reports"]
        expected = [(f"{month:02d}-{profile}", profile, datetime(2026, month, day, 8, tzinfo=timezone.utc))
                    for month, day in MONTHS[self.section] for profile in profiles(self.family)]
        if [r.get("caseId") if isinstance(r, dict) else None for r in reports] != [case for case, *_ in expected]:
            self.add("inventory", f"$.reports: {len(reports)} reports are not the writer's {len(expected)} cases in order")
        least = self.d["minimumFitting"] + self.d["minimumWithheld"]
        for report, (case, profile, utc) in zip(reports, expected):
            if not self.exact(report, MEASURED_REPORT_KEYS, "$.reports[]", case) or report["caseId"] != case:
                continue
            if not same(report["name"], profile) or utc_of(report["utc"]) != utc:
                self.add("row identity", f"{case}: name {report['name']!r}, utc {report['utc']!r}")
            self.exact(report["diagnostics"], MEASURED_DIAGNOSTIC_KEYS, "$.reports[].diagnostics", case)
            for arm, arm_keys, score_key in (("v1", V1_KEYS, "baseline"), ("v2", V2_KEYS, "candidate")):
                solve = report[arm]
                if not self.exact(solve, arm_keys, f"$.reports[].{arm}", case):
                    continue
                status, count = solve["status"], solve["associations"]
                if status not in STATUSES:
                    self.add(f"{arm} status", f"{case}: {arm} status {status!r}")
                if not self.exact(solve[score_key], MEASURED_SCORE_KEYS, f"$.reports[].{arm}.{score_key}", case):
                    continue
                if not integer(count) or not same(count, solve[score_key]["associations"]):
                    self.add(f"{arm} count", f"{case}: {arm} associations {count!r} is not its score's")
                elif (status == "Accepted") != (count > 0) or status == "Accepted" and count < least:
                    self.add(f"{arm} count status", f"{case}: {arm} {status} with {count} associations")
            if isinstance(report["v2"], dict) and report["v2"].get("status") != "Accepted" and self.failure(prefix=f"{case}: v2 ") is None:
                self.add("MEAS:84", f"{case}: v2 {report['v2'].get('status')} without its MEAS :84 failure")

    def resources(self):
        """RES :41, :105."""
        reports = self.data["reports"]
        expected = ["mono-native", *(["cfa-native"] if FAMILY_MODELS[self.family] == "EquidistantFisheye" else [])]
        if [r.get("name") if isinstance(r, dict) else None for r in reports] != expected:
            self.add("inventory", f"$.reports: names are not the writer's {expected}")
        for report in reports:
            self.exact(report, RESOURCE_ROW_KEYS, "$.reports[]")

    def producers(self):
        """B-only producer markers: never in an A-arm report; in a B-arm report only in the producer's exact shape and
        echoed only at the declared writer sites (PIX :74, OPT :139/:145/:178, UNC :203/:282/:295, MEAS :84)."""
        if self.role == "a":
            for path, value in list(self.markers.values()):
                self.add(f"A marker {path}", f"{path}: A-arm content carries a B-only producer marker: {value[:100]!r}")
            self.markers.clear()
            return
        getattr(self, f"{self.kind}_producers", lambda: None)()
        d = self.d
        for site, echo, remainder, case in self.echoes:
            if site in ("OPT:178", "UNC:203", "UNC:295") and (re.fullmatch(d["b50"], remainder) or remainder == d["b85"]):
                if self.consume(self.failures, echo):
                    self.observe(f"{site} echo")
        for path, value in list(self.markers.values()):
            self.add(f"B marker {path}", f"{path}: a producer marker outside the producers' shapes and writer sites: {value[:100]!r}")

    def early_metrics(self, metrics, exhausted):
        return (isinstance(metrics, dict) and set(metrics) == METRIC_KEYS and same(metrics["budgetExhausted"], exhausted)
                and (exhausted or all(same(metrics[k], 0) for k in METRIC_COUNTS)))

    def pixels_producers(self):
        """SOL B:50 (Failure, SOL :46-47) and SOL B:84-87, with their PIX :74 echo."""
        d = self.d
        for row in self.data["reports"]:
            assessment = row.get("assessment") if isinstance(row, dict) else None
            if not isinstance(assessment, dict):
                continue
            case, reason = row.get("caseId"), assessment.get("reason")
            if assessment.get("reasonCode") == d["code"]:
                site, exhausted, status = "SOL B:50", False, "Unavailable"
                shaped = isinstance(reason, str) and re.fullmatch(d["b50"], reason) is not None
            elif reason == d["b85"]:
                site, exhausted, status, shaped = "SOL B:84-87", True, d["b85Status"], assessment.get("reasonCode") == d["b85Code"]
            else:
                continue
            echo = self.failure(text=f"{case}: {reason}")
            if (shaped and assessment.get("status") == status and row.get("score") is None and echo is not None
                    and all(assessment.get(k) is None for k in ("parameters", "quality", "associationIdentitySha256"))
                    and self.early_metrics(row.get("metrics"), exhausted)):
                self.consume(assessment, "reasonCode")
                self.consume(assessment, "reason")
                self.consume(self.failures, echo)
                self.observe(site)
            else:
                self.add(f"{site} shape", f"{case}: a {site} row is not the producer's shape or has no PIX :74 echo")

    def measured_producers(self):
        """SOL B:50 and B:84-87 in either solve, echoed for v2 only at MEAS :84."""
        d = self.d
        for report in self.data["reports"]:
            if not isinstance(report, dict):
                continue
            case = report.get("caseId")
            for arm in ("v1", "v2"):
                solve = report.get(arm)
                if not isinstance(solve, dict) or solve.get("reasonCode") != d["code"]:
                    continue
                echo = self.failure(pattern=re.escape(f"{case}: v2 ") + d["b50"]) if arm == "v2" else None
                if solve.get("status") == "Unavailable" and same(solve.get("associations"), 0) and (arm == "v1" or echo is not None):
                    self.consume(solve, "reasonCode")
                    if echo is not None:
                        self.consume(self.failures, echo)
                    self.observe(f"SOL B:50 {arm}")
                else:
                    self.add("SOL B:50 shape", f"{case}: a {arm} SOL B:50 solve is not the producer's shape or has no MEAS :84 echo")
            echo, solve = self.failure(text=f"{case}: v2 {d['b85']}"), report.get("v2")
            if echo is None:
                continue
            if (isinstance(solve, dict) and solve.get("status") == d["b85Status"] and solve.get("reasonCode") == d["b85Code"]
                    and same(solve.get("associations"), 0)):
                self.consume(self.failures, echo)
                self.observe("SOL B:84-87 v2")
            else:
                self.add("SOL B:84-87 shape", f"{case}: a MEAS :84 SOL B:84-87 echo without the producer's v2 shape")

    def early_fit(self, fit, code):
        """SESS B:95-96 through Result (SESS :82-88) before any work: OC :82, :86, :99 members at their early values."""
        frames, parameters = fit.get("frames"), fit.get("parameters")
        return (fit.get("status") == "Unavailable" and fit.get("rejections") == [code] and fit.get("calibratedNative") is None
                and fit.get("validations") == [] and isinstance(frames, list) and len(frames) == FIT_FRAMES
                and all(isinstance(f, dict) and set(f) == FRAME_RESULT_KEYS and f["status"] == "not-attempted"
                        and same(f["acquisitionCandidates"], 0) and same(f["fittingStars"], 0) and same(f["verificationStars"], 0)
                        and all(f[k] is None for k in ("boresightAltitudeDegrees", "boresightAzimuthDegrees", "rollDegrees",
                                                      "fittingRmsPixels", "verificationRmsPixels")) for f in frames)
                and isinstance(parameters, list) and len(parameters) == PARAMETERS
                and all(isinstance(p, dict) and set(p) == PARAMETER_KEYS and p["standardError"] is None
                        and same(p["atBound"], False) for p in parameters)
                and self.early_diagnostics(fit.get("diagnostics")))

    @staticmethod
    def early_diagnostics(diagnostics):
        return (isinstance(diagnostics, dict) and set(diagnostics) == DIAGNOSTIC_KEYS and number(diagnostics["skyRotationDegrees"])
                and all(same(diagnostics[k], 0) for k in ("fittedFrames", "fittingStars", "verificationStars",
                                                          "occupiedRadialBins", "occupiedAzimuthBins", "iterations"))
                and all(diagnostics[k] is None for k in ("fittingRmsPixels", "verificationRmsPixels", "conditionNumber"))
                and same(diagnostics["converged"], False) and diagnostics["residualBins"] == [])

    def optical_producers(self):
        """SESS B:95-96 in the fit (OPT :145 echo) and, beside such a fit only, in omittedDistortion (OPT :139 echo)."""
        code = self.d["code"]
        for report in self.data["reports"]:
            if not isinstance(report, dict):
                continue
            case, fit, omitted, truth = report.get("caseId"), report.get("fit"), report.get("omittedDistortion"), report.get("truth")
            early = False
            if isinstance(fit, dict) and fit.get("reasonCode") == code:
                echo = self.failure(text=f"{case}: calibration Unavailable {code}: {code}")
                if (self.early_fit(fit, code) and report.get("withheldScores") == [] and report.get("errors") is None
                        and echo is not None):
                    early = True
                    self.consume(fit, "reasonCode")
                    self.consume(fit["rejections"], 0)
                    self.consume(self.failures, echo)
                    self.observe("SESS B:95-96 fit")
                else:
                    self.add("SESS B:95-96 fit shape", f"{case}: a SESS B:95-96 fit is not the producer's shape or has no OPT :145 echo")
            if not isinstance(omitted, dict) or omitted.get("reasonCode") != code:
                continue
            undistorted = isinstance(truth, dict) and number(truth.get("k1")) and truth["k1"] == 0
            echo = self.failure(text=f"{case}: undistorted truth rejected without k1: {code}") if undistorted else None
            if (early and omitted.get("status") == "Unavailable" and omitted.get("rejections") == [code]
                    and self.early_diagnostics(omitted.get("diagnostics")) and (echo is not None or not undistorted)):
                self.consume(omitted, "reasonCode")
                self.consume(omitted["rejections"], 0)
                if echo is not None:
                    self.consume(self.failures, echo)
                self.observe("SESS B:95-96 omittedDistortion")
            else:
                self.add("SESS B:95-96 omitted shape", f"{case}: a SESS B:95-96 omittedDistortion is not the producer's shape, "
                                                       "is not beside a SESS B:95-96 fit, or has no OPT :139 echo")

    def uncertainty_producers(self):
        """SESS B:95-96 as a short session (UNC :280-283 echo); solver echoes at UNC :203 and :295 are taken in producers()."""
        code = self.d["code"]
        for session in self.data["sessions"]:
            if not isinstance(session, dict) or set(session) != SESSION_SHORT_KEYS or session.get("reasonCode") != code:
                continue
            echo = self.failure(text=f"{session.get('caseId')}: calibration Unavailable {code}")
            if session.get("status") == "Unavailable" and echo is not None:
                self.consume(session, "reasonCode")
                self.consume(self.failures, echo)
                self.observe("SESS B:95-96 session")
            else:
                self.add("SESS B:95-96 session shape", f"{session.get('caseId')}: a SESS B:95-96 session is not the producer's shape")


def check_content(label, path, family, section, role, revision, derived, findings):
    """R1-F1/F2: a report's recorded revision and its content against its declared writer."""
    kind = KINDS.get(path.name)
    if kind is None:
        findings.append(f"{label}: {path.name} has no declared writer")
        return {"report": label, "kind": None}
    try:
        data = load(path)
    except InputError as error:
        findings.append(f"{label}: unreadable report: {error}")
        return {"report": label, "kind": kind, "readable": False}
    report = Report(label, data, kind, family, section, role, derived).check(revision)
    findings.extend(report.messages())
    return {"report": label, "kind": kind, "revision": data.get("revision") if isinstance(data, dict) else None,
            "findings": len(report.findings), "producers": report.observations,
            **({"coldMedianMs": report.cold_median} if kind == "pixels" and section == "runs" else {})}


def source_declarations(revisions, families, derived, findings):
    """Every content declaration at both revisions; the B-only producers at B and not at A."""
    cache = {}

    def text(role, key):
        if (role, key) not in cache:
            try:
                cache[role, key] = git("show", f"{revisions[role]}:{SOURCES[key]}")
            except InputError:
                cache[role, key] = None
        return cache[role, key]
    content = []
    for key, line_a, line_b, declaration in CONTENT_DECLARATIONS:
        found = {role: text(role, key) is not None and declared(text(role, key), declaration) for role in ("a", "b")}
        content.append({"source": SOURCES[key], "lineAtA": line_a, "lineAtB": line_b, "declaration": declaration,
                        "foundAtA": found["a"], "foundAtB": found["b"]})
        for role in ("a", "b"):
            if not found[role]:
                findings.append(f"declaration {SOURCES[key]}:{line_a if role == 'a' else line_b} not found at {revisions[role]}: {declaration}")
    b_only = []
    for key, line, declaration in B_ONLY_DECLARATIONS:
        at_a = text("a", key) is None or declared(text("a", key), declaration)
        at_b = text("b", key) is not None and declared(text("b", key), declaration)
        b_only.append({"source": SOURCES[key], "lineAtB": line, "declaration": declaration,
                       "foundAtA": at_a if text("a", key) is not None else None, "foundAtB": at_b})
        if at_a or not at_b:
            findings.append(f"B-only declaration {SOURCES[key]}:{line} is not present at {revisions['b']} and absent at {revisions['a']}: {declaration}")
    for role in ("a", "b"):
        fixture = text(role, "FIX") or ""
        found_families, found_profiles = dict(FAMILY_PATTERN.findall(fixture)), PROFILE_PATTERN.findall(fixture)
        if found_families != FAMILY_MODELS or found_profiles != [*MONO_VIEWS, CFA_VIEWS[0]]:
            findings.append(f"fixture families {found_families} or profiles {found_profiles} at {revisions[role]} are not the declared ones")
    if not set(families) <= FAMILY_MODELS.keys():
        findings.append(f"#1126 families {sorted(set(families) - FAMILY_MODELS.keys())} have no declared inventory")
    session = derived["session"]
    if session["code"] != derived["code"] or session["parts"] != derived["solverParts"] or session["rejections"] != [derived["code"]]:
        findings.append(f"SESS B:95-96 {session} does not declare SOL B:50's code and message")
    return content, b_only


def harness_declarations(index, revision, findings):
    sources = {s.get("path"): s.get("blob") for s in index.get("harnessSources", [])}
    if set(sources) != HARNESS_SOURCES or len(index.get("harnessSources", [])) != len(HARNESS_SOURCES):
        findings.append(f"index harness sources {sorted(sources)} are not the four declared harness sources")
    for path, blob in sources.items():
        if path in HARNESS_SOURCES and git("rev-parse", f"{revision}:{path}").strip() != blob:
            findings.append(f"index blob for {path} is not the blob at {revision}")
    texts = {path: git("cat-file", "blob", blob) for path, blob in sources.items()
             if path in (HARNESS, FIXTURE) and isinstance(blob, str) and re.fullmatch(r"[0-9a-f]{40}", blob)}
    for path in (CFA_RIG, SOLVER, INTERNALS):
        texts[path] = git("show", f"{revision}:{path}")
    rows = []
    for path, line, text in DECLARATIONS:
        found = path in texts and declared(texts[path], text)
        rows.append({"source": path, "lineAtD3b78737": line, "declaration": text, "found": found})
        if not found:
            findings.append(f"declaration {path}:{line} not found at the evaluated revision: {text}")
    fixture = texts.get(FIXTURE, "")
    families = dict(FAMILY_PATTERN.findall(fixture))
    if families != FAMILY_MODELS:
        findings.append(f"fixture families {families} are not the declared families")
    profiles_found = PROFILE_PATTERN.findall(fixture)
    if profiles_found != [*MONO_VIEWS, CFA_VIEWS[0]]:
        findings.append(f"fixture profiles {profiles_found} are not the declared profiles")
    rows.append({"source": FIXTURE, "lineAtD3b78737": "41-46", "families": families})
    rows.append({"source": FIXTURE, "lineAtD3b78737": "85-90, 94", "profiles": profiles_found})
    return rows


def expected_cases(family):
    views = MONO_VIEWS + (CFA_VIEWS if FAMILY_MODELS[family] == "EquidistantFisheye" else ())
    cases = {}
    for month, day in HELD_OUT:
        for view in views:
            for capture in range(CAPTURES):
                utc = datetime(2026, month, day, 8, tzinfo=timezone.utc) + timedelta(seconds=60 * capture)
                cases[f"{month:02d}-{view}-{capture}"] = (view, capture, utc)
    return cases


def check_rows(name, family, report, derived, findings):
    cases = expected_cases(family)
    rows = report.get("reports") if isinstance(report.get("reports"), list) else []
    ids = [r.get("caseId") if isinstance(r, dict) else None for r in rows]
    duplicates = sorted({i for i in ids if ids.count(i) > 1}, key=str)
    missing, extra = sorted(cases.keys() - set(ids)), sorted(set(ids) - cases.keys(), key=str)
    if len(rows) != len(cases):
        findings.append(f"{name}: {len(rows)} case rows, declared {len(cases)}")
    if duplicates:
        findings.append(f"{name}: duplicate case ids {duplicates[:10]}")
    if missing:
        findings.append(f"{name}: {len(missing)} declared cases missing, e.g. {missing[:6]}")
    if extra:
        findings.append(f"{name}: {len(extra)} undeclared cases, e.g. {extra[:6]}")
    perspective = FAMILY_MODELS[family] == "Perspective"
    cold_prefix, codes = 0, {derived["code"]: 0, derived["b85Code"]: 0, "evidence-bound reason": 0}
    cold, cold_rows, invalid = [], 0, 0
    for row in rows:
        if not isinstance(row, dict):
            findings.append(f"{name}: a case row is not an object")
            continue
        metrics = row.get("metrics")
        elapsed = metrics.get("elapsedMilliseconds") if isinstance(metrics, dict) else None
        valid = number(elapsed) and elapsed >= 0
        invalid += not valid
        if same(row.get("index"), 0):  # harness :199 adds every capture-0 row's solve time, after its reports.Add
            cold_rows += 1
            cold += [elapsed] if valid else []
        assessment = row.get("assessment") if isinstance(row.get("assessment"), dict) else {}
        if assessment.get("reasonCode") in codes:
            codes[assessment["reasonCode"]] += 1
        if assessment.get("reason") == derived["b85"]:
            codes["evidence-bound reason"] += 1
        case = cases.get(row.get("caseId"))
        if case is not None:
            view, capture, utc = case
            if row.get("name") != view or row.get("index") != capture or utc_of(row.get("utc")) != utc:
                findings.append(f"{name}: {row.get('caseId')} has view {row.get('name')!r}, capture {row.get('index')!r}, "
                                f"utc {row.get('utc')!r}; declared {view}, {capture}, {utc.isoformat()}")
        prefix = row.get("indexPrefix")
        if not perspective:
            if prefix is not None:
                findings.append(f"{name}: {row.get('caseId')} has index-bound evidence in a non-perspective family")
            continue
        if prefix is None:
            if row.get("index") == 0:
                findings.append(f"{name}: perspective cold row {row.get('caseId')} has no index-bound evidence")
            continue
        # R1-F3: harness :167-168 records the row's own admitted count, metrics.indexStars (:189-190), as admittedIndexStars.
        if (not isinstance(prefix, dict) or set(prefix) != PREFIX_KEYS or not all(integer(prefix[k]) for k in PREFIX_KEYS)
                or prefix["trainingStars"] < 0 or prefix["admittedIndexStars"] < 0):
            findings.append(f"{name}: {row.get('caseId')} index-bound evidence is malformed: {prefix!r}")
            continue
        metrics = row.get("metrics")
        stars = metrics.get("indexStars") if isinstance(metrics, dict) else None
        if not integer(stars) or stars != prefix["admittedIndexStars"]:
            findings.append(f"{name}: {row.get('caseId')} admittedIndexStars {prefix['admittedIndexStars']} is not metrics.indexStars {stars!r}")
        elif prefix["boundBytes"] != index_bound(prefix["trainingStars"]):
            findings.append(f"{name}: {row.get('caseId')} boundBytes {prefix['boundBytes']} is not the formula's {index_bound(prefix['trainingStars'])}")
        elif row.get("index") == 0:
            cold_prefix += 1
    if invalid:
        findings.append(f"{name}: {invalid} rows' metrics.elapsedMilliseconds is not a finite nonnegative Stopwatch reading "
                        "(INT :32-33; SOL :45, :47, B:86, B:102)")
    recomputed = None if cold_rows == 0 or len(cold) != cold_rows else percentile95(cold)
    if not (integer(report.get("coldSolves")) and report["coldSolves"] == cold_rows):
        findings.append(f"{name}: coldSolves {report.get('coldSolves')!r} is not its {cold_rows} capture-0 rows (harness :199, :243)")
    if cold_rows == 0 and report.get("coldP95Ms", 0) is not None:
        findings.append(f"{name}: coldP95Ms {report.get('coldP95Ms')!r} with no capture-0 rows; the harness writes null (:244)")
    elif recomputed is not None and not (number(report.get("coldP95Ms")) and report["coldP95Ms"] == recomputed):
        findings.append(f"{name}: coldP95Ms {report.get('coldP95Ms')!r} is not the nearest-rank p95 {recomputed!r} of its capture-0 "
                        "rows' solve times (harness :199, :244, :519)")
    return {"rows": len(rows), "declaredCases": len(cases), "perspectiveColdRowsWithBound": cold_prefix if perspective else None,
            "observedReasonCodes": codes, "coldSolves": cold_rows, "recomputedColdP95Ms": recomputed}


def check_selection(name, report, findings):
    selection = report.get("selection") if isinstance(report.get("selection"), dict) else {}
    samples = selection.get("samplesMs")
    summary = {"warmup": selection.get("warmup"), "measured": selection.get("measured"), "p95Ms": selection.get("p95Ms"),
               "recomputedP95Ms": None}
    if not (integer(selection.get("warmup")) and selection["warmup"] == WARMUP):
        findings.append(f"{name}: selection warmup {selection.get('warmup')!r}, declared {WARMUP}")
    if not (integer(selection.get("measured")) and selection["measured"] == MEASURED):
        findings.append(f"{name}: selection measured {selection.get('measured')!r}, declared {MEASURED}")
    if not isinstance(samples, list) or len(samples) != MEASURED or not all(number(v) for v in samples):
        count = len(samples) if isinstance(samples, list) else None
        findings.append(f"{name}: selection samplesMs has {count} values, declared {MEASURED} finite measurements; p95 cannot be recomputed")
        return summary
    summary["recomputedP95Ms"] = percentile95(samples)
    if not number(selection.get("p95Ms")) or selection["p95Ms"] != summary["recomputedP95Ms"]:
        findings.append(f"{name}: selection p95Ms {selection.get('p95Ms')!r} is not the samples' nearest-rank p95 {summary['recomputedP95Ms']!r}")
    return summary


def deep(manifest_path, pack, revision, output):
    derived = derive()
    manifest, index = load(manifest_path), load(pack / "index.json")
    findings = []
    if index.get("schema") != "virtual-deep-astrometry-qualification-index-v1" or index.get("mode") != "final" or index.get("final") is not True:
        findings.append(f"index is not a final #1167 index (schema {index.get('schema')}, mode {index.get('mode')})")
    if index.get("revision") != revision:
        findings.append(f"index revision {index.get('revision')} is not {revision}")
    if index.get("manifestSha256") != sha256(manifest_path):
        findings.append("index does not name this manifest")
    dates = [p.get("date") for p in manifest["partitions"]["heldOut"]]
    if dates != [f"2026-{m:02d}-{d:02d}" for m, d in HELD_OUT]:
        findings.append(f"manifest held-out dates {dates} are not the declared partitions")
    expected = {f"final-{e['family']}-{v}": (e["family"], v) for e in manifest["matrix"] for v in e["variants"]}
    if index.get("pairs") != [f"{f}:{v}" for f, v in expected.values()]:
        findings.append("index pairs are not the manifest matrix")
    unknown = sorted({f for f, _ in expected.values()} - FAMILY_MODELS.keys())
    if unknown:
        raise InputError(f"manifest families {unknown} have no declared inventory")
    declarations = harness_declarations(index, revision, findings)
    names = [r.get("name") for r in index.get("runs", [])]
    if len(names) != len(set(names)) or set(names) != set(expected):
        findings.append(f"index runs are not exactly the matrix: missing {sorted(set(expected) - set(names))}, "
                        f"undeclared {sorted(set(names) - set(expected), key=str)}, duplicated {len(names) - len(set(names))}")
    report_name = manifest["runs"]["final"]["report"]
    runs, without, present = [], [], set()
    for run in index.get("runs", []):
        name = run.get("name")
        if name not in expected:
            continue
        family, variant = expected[name]
        if run.get("family") != family or run.get("variant") != variant:
            findings.append(f"{name}: index family/variant {run.get('family')}/{run.get('variant')}")
        entries = run.get("reports") if isinstance(run.get("reports"), list) else None
        if not entries:
            without.append({"run": name, "status": run.get("status")})
            if run.get("status") != "error" or entries != []:
                findings.append(f"{name}: has no report but is not an error run with an empty report list")
            continue
        trx = run.get("trx") or {}
        if len(entries) != 1 or trx.get("total") != "1" or trx.get("executed") != "1" or run.get("status") not in ("passed", "failed"):
            findings.append(f"{name}: {len(entries)} reports, trx total {trx.get('total')!r} executed {trx.get('executed')!r}, status {run.get('status')!r}")
        present.add(check_report_entry(pack, name, entries[0], report_name, findings))
        path = pack / "reports" / name / report_name
        if not path.is_file():
            continue
        report = load(path)
        for key, value in (("schema", "virtual-deep-astrometry-v1"), ("partition", "held-out"), ("projectionFamily", family), ("revision", revision)):
            if report.get(key) != value:
                findings.append(f"{name}: report {key} {report.get(key)!r}, declared {value!r}")
        if (report.get("variant") or {}).get("name") != variant:
            findings.append(f"{name}: report variant {(report.get('variant') or {}).get('name')!r}")
        for where, count in non_finite(report).items():  # harness :251-253 writes with JsonOptions
            findings.append(f"{name}: {where}: {NON_FINITE}" + (f" ({count} occurrences)" if count > 1 else ""))
        runs.append({"run": name, "status": run.get("status"), **check_rows(name, family, report, derived, findings),
                     "selection": check_selection(name, report, findings)})
    if files_under(pack) != present:
        findings.append(f"report files differ from the indexed reports: {sorted(files_under(pack) ^ present)[:10]}")
    return finish(output, "deep", findings, {"manifestSha256": sha256(manifest_path), "revision": revision,
                                             "indexSha256": sha256(pack / "index.json"), "runsWithoutReport": without,
                                             "derived": derived, "declarations": declarations, "runs": runs})


def files_under(root):
    return {p.relative_to(root).as_posix() for p in (root / "reports").rglob("*") if p.is_file()} if (root / "reports").is_dir() else set()


def check_report_entry(root, name, entry, pattern, findings, label=None):
    """A report entry must name its pattern and hash to the file the runner copied."""
    target, label = root / "reports" / name / Path(pattern).name, label or name
    if entry.get("report") != pattern:
        findings.append(f"{label}: index report {entry.get('report')!r} is not {pattern!r}")
    elif not target.is_file():
        findings.append(f"{label}: {target.relative_to(root).as_posix()} is missing")
    elif entry.get("sha256") != sha256(target) or entry.get("bytes") != target.stat().st_size:
        findings.append(f"{label}: {target.relative_to(root).as_posix()} does not hash to its index entry")
    return target.relative_to(root).as_posix()


def ab(manifest_path, manifest_1126_path, pack, revision, output):
    derived = derive()
    manifest, inventory = load(manifest_path), load(manifest_1126_path)
    ab_index = load(pack / "ab-index.json")
    revisions = {"a": manifest["base"]["revision"], "b": revision}
    findings = []
    families = inventory["families"]["values"]
    for key, value in (("schema", "virtual-deep-astrometry-ab-index-v1"), ("a", revisions["a"]), ("b", revisions["b"]),
                       ("manifestSha256", sha256(manifest_path)), ("families", families)):
        if ab_index.get(key) != value:
            findings.append(f"ab-index {key} {ab_index.get(key)!r}, declared {value!r}")
    declarations = []
    for path, role, line, text in AB_DECLARATIONS:
        found = declared(git("show", f"{revisions[role]}:{path}"), text)
        declarations.append({"source": path, "revision": revisions[role], "lineAtD3b78737": line, "declaration": text, "found": found})
        if not found:
            findings.append(f"declaration {path}:{line} not found at {revisions[role]}: {text}")
    content, b_only = source_declarations(revisions, families, derived, findings)
    arms, contents = [], []
    for arm, section, role in ARMS:
        root = pack / arm
        index = load(root / "index.json")
        for key, value in (("schema", "virtual-astrometry-projection-family-qualification-index-v1"),
                           ("mode", "final" if section == "runs" else "tuning"), ("revision", revisions[role]),
                           ("manifestSha256", sha256(manifest_1126_path)), ("families", families)):
            if index.get(key) != value:
                findings.append(f"{arm}: index {key} {index.get(key)!r}, declared {value!r}")
        expected = {f"{run['id']}-{family}": (run, family) for run in inventory[section] for family in families}
        names = [r.get("name") for r in index.get("runs", [])]
        if len(names) != len(set(names)) or set(names) != set(expected):
            findings.append(f"{arm}: runs are not the #1126 inventory: missing {sorted(set(expected) - set(names))}, "
                            f"undeclared {sorted(set(names) - set(expected), key=str)}, duplicated {len(names) - len(set(names))}")
        present = set()
        for run in index.get("runs", []):
            name = run.get("name")
            if name not in expected:
                continue
            declared_run, family = expected[name]
            trx, want = run.get("trx") or {}, str(declared_run["expectedPassed"])
            if run.get("id") != declared_run["id"] or run.get("family") != family or trx.get("total") != want or trx.get("executed") != want:
                findings.append(f"{arm}/{name}: id {run.get('id')!r}, family {run.get('family')!r}, trx total {trx.get('total')!r} executed {trx.get('executed')!r}")
            entries = run.get("reports") if isinstance(run.get("reports"), list) else []
            if len(entries) != len(declared_run["reports"]):
                findings.append(f"{arm}/{name}: {len(entries)} indexed reports, declared {len(declared_run['reports'])}")
            for entry, pattern in zip(entries, declared_run["reports"]):
                present.add(check_report_entry(root, name, entry, pattern, findings, f"{arm}/{name}"))
                path = root / "reports" / name / Path(pattern).name
                if path.is_file():
                    contents.append(check_content(f"{arm}/{name}", path, family, section, role, revisions[role], derived, findings))
        if files_under(root) != present:
            findings.append(f"{arm}: report files differ from the indexed reports: {sorted(files_under(root) ^ present)[:10]}")
        arms.append({"arm": arm, "revision": index.get("revision"), "runs": len(names), "declaredRuns": len(expected),
                     "reports": len(present)})
    pairs = []
    for family in families:
        for arm, role in (("b2", "b"), ("a2", "a")):
            path = pack / arm / family / "virtual-astrometry-pixels.json"
            if path.is_file():
                contents.append(check_content(f"{arm}/{family}", path, family, "runs", role, revisions[role], derived, findings))
            else:
                findings.append(f"{arm}/{family}: pixel report missing, declared at {revisions[role]}")
            pairs.append({"arm": arm, "family": family, "present": path.is_file()})
    # The frozen evaluator divides B1/A1 and B2/A2. Positive finite medians can still produce an infinite ratio.
    medians = {item["report"]: item.get("coldMedianMs") for item in contents if "coldMedianMs" in item}
    timing_pairs = []
    for family in families:
        for name, numerator, denominator in (
                ("B1/A1", f"b1-final/actual-pixels-blind-warm-readouts-{family}",
                 f"a1-final/actual-pixels-blind-warm-readouts-{family}"),
                ("B2/A2", f"b2/{family}", f"a2/{family}")):
            b, a = medians.get(numerator), medians.get(denominator)
            ratio = None
            # Missing or invalid medians already have report/inventory findings; do not mask those with a division.
            if a is not None and b is not None:
                ratio = b / a
                if not number(ratio):
                    findings.append(f"{family} {name}: ratio {ratio!r} of finite positive medians {b!r}/{a!r} is not finite "
                                    "(issue-1167-evaluate.py:103-109)")
                    ratio = None
            timing_pairs.append({"family": family, "pair": name, "aMedianMs": a, "bMedianMs": b, "ratio": ratio})
    return finish(output, "ab", findings, {"manifestSha256": sha256(manifest_path), "manifest1126Sha256": sha256(manifest_1126_path),
                                           "revisions": revisions, "derived": derived, "declarations": declarations,
                                           "contentDeclarations": content, "bOnlyDeclarations": b_only, "arms": arms,
                                           "pixelPairs": pairs, "timingPairs": timing_pairs, "reports": contents})


def finish(output, mode, findings, details):
    result = {"schema": "virtual-deep-astrometry-evidence-check-v1", "issue": 1167, "mode": mode,
              "checkSha256": sha256(Path(__file__).resolve()), "complete": not findings, "findings": findings, **details}
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"{mode}: {'complete' if not findings else 'INCOMPLETE'}; {len(findings)} findings")
    for finding in findings[:40]:
        print(f"  {finding}")
    return 0 if not findings else 1


def main(argv):
    try:
        if len(argv) == 6 and argv[1] == "deep":
            return deep(Path(argv[2]), Path(argv[3]), argv[4], Path(argv[5]))
        if len(argv) == 7 and argv[1] == "ab":
            return ab(Path(argv[2]), Path(argv[3]), Path(argv[4]), argv[5], Path(argv[6]))
    except InputError as error:
        print(error, file=sys.stderr)
        return 2
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
