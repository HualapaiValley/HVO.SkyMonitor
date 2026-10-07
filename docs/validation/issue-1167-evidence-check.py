"""Checks that #1167 evidence packs are complete before issue-1167-evaluate.py's verdicts on them are relied on.

Usage (standard library only; run with python3 -I inside a clone that holds the measured revisions' objects):
  issue-1167-evidence-check.py deep <#1167 manifest> <final pack> <measured revision> <output json>
  issue-1167-evidence-check.py ab <#1167 manifest> <#1126 manifest> <A/B pack> <measured revision> <output json>

Added after review PR-1181-R0 (F1-F3). The frozen manifest and evaluator are unchanged and still decide; this check only
establishes that the evidence they decided on is the complete declared inventory. It was committed before it was run on
any measured pack. Its expected inventory comes only from committed sources: the frozen #1167 manifest, the #1126
manifest, the #1126/#1167 runner scripts and the harness sources at d3b78737, each quoted with its line below. Each
quoted declaration is then found again in the source the pack itself records (the harness blobs in a final index, the
scripts at the A and B revisions), so a pack is checked against the declarations of the revision that produced it.

"deep" requires the run set to be exactly the manifest matrix, every report to hash to its index entry, every case of the
declared family/view/partition/capture inventory exactly once with its declared view, capture and UTC, 30 finite
selection samples after 5 warm-ups whose nearest-rank p95 equals the reported p95 exactly, and index-bound evidence
equal to the harness formula on every perspective cold row. A run with no report is not incomplete evidence: the frozen
evaluator already makes every decision that depends on it undecidable (issue-1167-evaluate.py:144). It must be status
"error" and is listed under runsWithoutReport. "ab" requires every arm to hold exactly the #1126 manifest's run and
report inventory at its declared revision, every report to hash to its index entry, and both pixel-pair reports per
family. Exit 0 when the evidence is complete, 1 on any finding, and 2 on a usage or input error.
"""

import hashlib
import json
import math
import re
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
    (HARNESS, 102, "foreach (var fixtureProfile in VirtualAstrometryFixture.Profiles(seed))"),
    (HARNESS, 109, "for (var index = 0; index < 3; index++)"),
    (HARNESS, 111, "var utc = new DateTimeOffset(2026, month, day, 8, 0, 0, TimeSpan.Zero).AddSeconds(60 * index);"),
    (HARNESS, 117, "if (native.Layout.PixelFormat == CameraPixelFormat.BayerRggb16)"),
    (HARNESS, 122, 'var caseId = $"{month:D2}-{view.Name}-{index}";'),
    (HARNESS, 167, "var indexPrefix = family.Model == ProjectionModel.Perspective && prior is null"),
    (HARNESS, 231, "warmup = 5, measured = selectionMs.Count, p95Ms = Percentile95(selectionMs), samplesMs = selectionMs"),
    (HARNESS, 515, "long rows = Math.Min(training, 1501);"),
    (HARNESS, 516, "8 * rows * (rows - 1) / 2 + 24 * rows + 8L * training + 24L * training + 48"),
    (HARNESS, 519, "values.Order().ElementAt((int)Math.Ceiling(.95 * values.Count) - 1)"),
    (FIXTURE, 72, 'var mono = LoadRig("virtual-asi174.full.json") with'),
    (FIXTURE, 76, "CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned,"),
    (FIXTURE, 93, 'if (family.Model == ProjectionModel.EquidistantFisheye) profiles.Add(Profile("cfa-native", LoadRig("virtual-asi178mc.full.json")'),
    (FIXTURE, 344, 'new VirtualAstrometryProfile($"cfa-derived-phase-{x}{y}"'),
    (CFA_RIG, 21, '"pixelFormat": "BayerRggb16"'),
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
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def percentile95(values):
    """The harness's own nearest rank: values.Order().ElementAt((int)Math.Ceiling(.95 * values.Count) - 1), harness :519.

    It is compared exactly, with no tolerance: the harness's p95 is one element of the same list it serializes as
    samplesMs (:231), System.Text.Json writes each double in round-trip form, and 0.95 * n is the same IEEE product here.
    """
    return sorted(values)[math.ceil(0.95 * len(values)) - 1]


def index_bound(training):
    """IndexPrefixBound's bytes for a training-star count, harness :515-516."""
    rows = min(training, 1501)
    return 8 * rows * (rows - 1) // 2 + 24 * rows + 8 * training + 24 * training + 48


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


def harness_declarations(index, revision, findings):
    sources = {s.get("path"): s.get("blob") for s in index.get("harnessSources", [])}
    if set(sources) != HARNESS_SOURCES or len(index.get("harnessSources", [])) != len(HARNESS_SOURCES):
        findings.append(f"index harness sources {sorted(sources)} are not the four declared harness sources")
    for path, blob in sources.items():
        if path in HARNESS_SOURCES and git("rev-parse", f"{revision}:{path}").strip() != blob:
            findings.append(f"index blob for {path} is not the blob at {revision}")
    texts = {path: git("cat-file", "blob", blob) for path, blob in sources.items()
             if path in (HARNESS, FIXTURE) and isinstance(blob, str) and re.fullmatch(r"[0-9a-f]{40}", blob)}
    texts[CFA_RIG] = git("show", f"{revision}:{CFA_RIG}")
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
    profiles = PROFILE_PATTERN.findall(fixture)
    if profiles != [*MONO_VIEWS, CFA_VIEWS[0]]:
        findings.append(f"fixture profiles {profiles} are not the declared profiles")
    rows.append({"source": FIXTURE, "lineAtD3b78737": "41-46", "families": families})
    rows.append({"source": FIXTURE, "lineAtD3b78737": "85-90, 94", "profiles": profiles})
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


def check_rows(name, family, report, findings):
    cases = expected_cases(family)
    rows = report.get("reports") if isinstance(report.get("reports"), list) else []
    ids = [r.get("caseId") for r in rows]
    duplicates = sorted({i for i in ids if ids.count(i) > 1})
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
    cold_prefix = 0
    for row in rows:
        case = cases.get(row.get("caseId"))
        if case is not None:
            view, capture, utc = case
            try:
                row_utc = datetime.fromisoformat(str(row.get("utc")))
            except ValueError:
                row_utc = None
            if row.get("name") != view or row.get("index") != capture or row_utc != utc:
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
        fields = (prefix.get("trainingStars"), prefix.get("admittedIndexStars"), prefix.get("boundBytes")) if isinstance(prefix, dict) else ()
        if len(fields) != 3 or not all(integer(v) for v in fields) or fields[0] < 0:
            findings.append(f"{name}: {row.get('caseId')} index-bound evidence is malformed: {prefix!r}")
        elif fields[2] != index_bound(fields[0]):
            findings.append(f"{name}: {row.get('caseId')} boundBytes {fields[2]} is not the formula's {index_bound(fields[0])}")
        elif row.get("index") == 0:
            cold_prefix += 1
    return {"rows": len(rows), "declaredCases": len(cases), "perspectiveColdRowsWithBound": cold_prefix if perspective else None}


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
        runs.append({"run": name, "status": run.get("status"), **check_rows(name, family, report, findings),
                     "selection": check_selection(name, report, findings)})
    if files_under(pack) != present:
        findings.append(f"report files differ from the indexed reports: {sorted(files_under(pack) ^ present)[:10]}")
    return finish(output, "deep", findings, {"manifestSha256": sha256(manifest_path), "revision": revision,
                                             "indexSha256": sha256(pack / "index.json"), "runsWithoutReport": without,
                                             "declarations": declarations, "runs": runs})


def ab(manifest_path, manifest_1126_path, pack, revision, output):
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
    arms = []
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
                    recorded = load(path).get("revision")
                    if recorded is not None and recorded != revisions[role]:
                        findings.append(f"{arm}/{name}: report revision {recorded}, declared {revisions[role]}")
        if files_under(root) != present:
            findings.append(f"{arm}: report files differ from the indexed reports: {sorted(files_under(root) ^ present)[:10]}")
        arms.append({"arm": arm, "revision": index.get("revision"), "runs": len(names), "declaredRuns": len(expected),
                     "reports": len(present)})
    pairs = []
    for family in families:
        for arm, role in (("b2", "b"), ("a2", "a")):
            path = pack / arm / family / "virtual-astrometry-pixels.json"
            recorded = load(path).get("revision") if path.is_file() else None
            if recorded != revisions[role]:
                findings.append(f"{arm}/{family}: pixel report {'revision ' + str(recorded) if path.is_file() else 'missing'}, declared {revisions[role]}")
            pairs.append({"arm": arm, "family": family, "present": path.is_file(), "revision": recorded})
    return finish(output, "ab", findings, {"manifestSha256": sha256(manifest_path), "manifest1126Sha256": sha256(manifest_1126_path),
                                           "revisions": revisions, "declarations": declarations, "arms": arms, "pixelPairs": pairs})


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
