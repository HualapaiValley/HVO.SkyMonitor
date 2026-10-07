"""Evaluates #1167 evidence packs against the frozen decision in issue-1167-qualification-manifest.json.

Usage (standard library only; run with python3 -I):
  issue-1167-evaluate.py ab <manifest> <A/B pack> <output json>
  issue-1167-evaluate.py deep <manifest> <final pack> <output json> [<tuning pack>] [<mag7 pack>]

"ab" compares every identity and association of the A1 and B1 #1126 reports and the adjacent-pair cold timings; it
exits 1 on an output difference or a resolvable regression. "deep" applies gates G0-G8 to the held-out pack, derives
the outcome and the Evaluate-grid decision, and summarizes tuning and magnitude 7, which never gate; it exits 1 when
the decision is undecidable. Exit 2 is a usage or input error.
"""

import hashlib
import json
import math
import re
import statistics
import sys
from pathlib import Path

TIMING = re.compile(r"(ms|milliseconds|seconds|utc|elapsed.*|cpu.*|bytes|bytesperpixel|workingset.*|revision|host|payloadfile)$", re.IGNORECASE)
PROBES = ("fail-closed probe", "re-capture changed", "re-solve changed", "SolveAsync selected")


def load(path):
    with open(path, encoding="utf-8") as stream:
        return json.load(stream)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def leaves(value, path=""):
    if isinstance(value, dict):
        for key, child in value.items():
            yield from leaves(child, f"{path}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            yield from leaves(child, f"{path}[{index}]")
    else:
        yield path, value


def outputs(value, path=""):
    """Identity leaves and association arrays: the bytes the A/B requires to be unchanged."""
    if isinstance(value, dict):
        for key, child in value.items():
            here = f"{path}.{key}"
            if key.lower().endswith("identitysha256") and not isinstance(child, (dict, list)):
                yield here, child
            elif key in ("associations", "associationRows") and isinstance(child, list):
                yield here, canonical(child)
            else:
                yield from outputs(child, here)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            yield from outputs(child, f"{path}[{index}]")


def compare_reports(a_root, b_root):
    a_files = {p.relative_to(a_root).as_posix() for p in a_root.glob("reports/*/*.json")}
    b_files = {p.relative_to(b_root).as_posix() for p in b_root.glob("reports/*/*.json")}
    rows = []
    for name in sorted(a_files | b_files):
        if name not in a_files or name not in b_files:
            rows.append({"report": name, "present": {"a": name in a_files, "b": name in b_files}, "identical": False})
            continue
        a, b = load(a_root / name), load(b_root / name)
        a_out, b_out = dict(outputs(a)), dict(outputs(b))
        changed = sorted(k for k in a_out.keys() & b_out.keys() if a_out[k] != b_out[k])
        missing = sorted(a_out.keys() ^ b_out.keys())
        a_leaves, b_leaves = dict(leaves(a)), dict(leaves(b))
        other = sorted(k for k in a_leaves.keys() | b_leaves.keys()
                       if k not in a_out and a_leaves.get(k) != b_leaves.get(k) and not TIMING.search(k.rsplit(".", 1)[-1].split("[")[0]))
        rows.append({
            "report": name,
            "identityLeaves": sum(not k.endswith(("associations", "associationRows")) for k in a_out),
            "associationArrays": sum(k.endswith(("associations", "associationRows")) for k in a_out),
            "changed": changed[:50],
            "pathSetDifference": missing[:50],
            "identical": not changed and not missing,
            "informationalNonTimingDifferences": len(other),
            "informationalExamples": other[:20],
        })
    return rows


def cold_median(report):
    values = [r["metrics"]["elapsedMilliseconds"] for r in report["reports"] if r["index"] == 0]
    return statistics.median(values), len(values)


def ab(manifest, pack, output):
    limit = 1.10
    identity = []
    for arm in ("final", "tuning"):
        for row in compare_reports(pack / f"a1-{arm}", pack / f"b1-{arm}"):
            identity.append({"arm": arm, **row})
    families = load(pack / "ab-index.json")["families"]
    performance = []
    for family in families:
        a1, n1 = cold_median(load(pack / "a1-final" / "reports" / f"actual-pixels-blind-warm-readouts-{family}" / "virtual-astrometry-pixels.json"))
        b1, _ = cold_median(load(pack / "b1-final" / "reports" / f"actual-pixels-blind-warm-readouts-{family}" / "virtual-astrometry-pixels.json"))
        b2, _ = cold_median(load(pack / "b2" / family / "virtual-astrometry-pixels.json"))
        a2, _ = cold_median(load(pack / "a2" / family / "virtual-astrometry-pixels.json"))
        first, second = b1 / a1, b2 / a2
        performance.append({"family": family, "coldSolves": n1, "a1Ms": a1, "b1Ms": b1, "b2Ms": b2, "a2Ms": a2,
                            "ratioA1B1": first, "ratioB2A2": second, "resolvableRegression": first > limit and second > limit})
    identical = all(r["identical"] for r in identity) and len(identity) > 0
    regression = any(p["resolvableRegression"] for p in performance)
    result = {
        "schema": "virtual-deep-astrometry-ab-decision-v1",
        "manifestSha256": hashlib.sha256(manifest.read_bytes()).hexdigest(),
        "identity": {"identical": identical, "reports": len(identity), "rows": identity},
        "performance": {"rule": f"B/A > {limit} in both adjacent pairs", "families": performance, "resolvableRegression": regression},
        "verdict": "pass" if identical and not regression else "output-change" if not identical else "resolvable-regression",
    }
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"identity {'identical' if identical else 'CHANGED'} over {len(identity)} reports; "
          f"regression {'YES' if regression else 'none'}; verdict {result['verdict']}")
    return 0 if result["verdict"] == "pass" else 1


def percentile95(values):
    ordered = sorted(values)
    return ordered[math.ceil(0.95 * len(ordered)) - 1]


def deep(manifest_path, final, output, tuning=None, mag7=None):
    manifest = load(manifest_path)
    index = load(final / "index.json")
    variants = {v["name"]: v for v in manifest["variants"]["values"]}
    envelope = manifest["families"]["envelope"]
    counts = {float(k): v for k, v in manifest["catalog"]["completeSelectionRows"].items()}
    rows_per_run = 3 * len(manifest["partitions"]["heldOut"])
    sha = manifest["catalog"]["databaseSha256"].upper()
    reports, undecidable = {}, []
    if index["mode"] != "final" or index["status"] != "measured":
        undecidable.append(f"final index mode {index['mode']} status {index['status']}")
    for run in index["runs"]:
        path = final / "reports" / run["name"] / manifest["runs"]["final"]["report"]
        if not path.is_file():
            undecidable.append(f"{run['name']}: no report")
            continue
        report = load(path)
        variant = variants[run["variant"]]
        problems = []
        if report.get("schema") != "virtual-deep-astrometry-v1": problems.append("schema")
        if report.get("partition") != "held-out": problems.append("partition")
        if report.get("projectionFamily") != run["family"]: problems.append("family")
        if report.get("variant", {}).get("name") != run["variant"]: problems.append("variant")
        if report.get("revision") != index["revision"]: problems.append("revision")
        if str(report.get("databaseSha256", "")).upper() != sha: problems.append("catalog")
        if report.get("selection", {}).get("stars") != counts[float(variant["solveMagnitude"])]: problems.append("selection count")
        views = {r["name"] for r in report["reports"]}
        if len(report["reports"]) != rows_per_run * len(views): problems.append("case rows")
        if problems:
            undecidable.append(f"{run['name']}: G0 {', '.join(problems)}")
        reports[(run["family"], run["variant"])] = report

    def cold(report):
        return [r for r in report["reports"] if r["index"] == 0]

    def gates(family, name, report, baseline, depth):
        found = []
        ids, base_ids = {r["caseId"] for r in report["reports"]}, {r["caseId"] for r in baseline["reports"]}
        new = sorted(set(report["failingCases"]) - set(baseline["failingCases"]))
        if ids != base_ids: found.append(("G1", "case ids differ from the baseline"))
        if new: found.append(("G1", f"new failing cases {new}"))
        for r in report["reports"]:
            # A scored case is an accepted measured mapping.
            if r["score"] is not None and r["falseAssociations"] > 0.01 * r["associationCount"]:
                found.append(("G2", f"{r['caseId']}: {r['falseAssociations']} of {r['associationCount']} false"))
        for which, rep in (("variant", report), ("baseline", baseline)):
            if not (rep.get("sourceProbe") or {}).get("matches"): found.append(("G3", f"{which} source probe does not match"))
            for failure in rep["failures"]:
                if any(p in failure for p in PROBES): found.append(("G3", f"{which}: {failure}"))
        limit = 20 if depth == "5.5" else 40
        if report["selection"]["p95Ms"] > limit: found.append(("G4", f"selection p95 {report['selection']['p95Ms']:.2f} ms > {limit}"))
        if report["coldP95Ms"] > 2 * baseline["coldP95Ms"]:
            found.append(("G5", f"cold p95 {report['coldP95Ms']:.0f} ms > 2 x {baseline['coldP95Ms']:.0f}"))
        # The limit follows the solve actually run: a warm-index capture without a warm prior is solved blind (cold).
        for r in report["reports"]:
            limit_ms = 500 if (r.get("assessment") or {}).get("mode") == "Warm" else 15000
            if r["metrics"]["elapsedMilliseconds"] > limit_ms: found.append(("G5", f"{r['caseId']} {r['metrics']['elapsedMilliseconds']:.0f} ms"))
        allocation = statistics.mean(r["solveAllocatedBytes"] for r in cold(report))
        base_allocation = statistics.mean(r["solveAllocatedBytes"] for r in cold(baseline))
        if depth == "5.5" and allocation > 2 * base_allocation:
            found.append(("G6", f"mean cold allocation {allocation:.0f} > 2 x {base_allocation:.0f}"))
        delta = report["process"]["peakWorkingSetBytes"] - baseline["process"]["peakWorkingSetBytes"]
        if depth == "6" and delta > 128 * 1024 * 1024: found.append(("G7", f"peak working set +{delta} bytes"))
        for r in cold(report):
            if r.get("indexPrefix") and r["indexPrefix"]["boundBytes"] > 16 * 1024 * 1024:
                found.append(("G8", f"{r['caseId']} IndexPrefix {r['indexPrefix']['boundBytes']} bytes"))
        return found, {"coldP95Ms": report["coldP95Ms"], "baselineColdP95Ms": baseline["coldP95Ms"],
                       "selectionP95Ms": report["selection"]["p95Ms"], "meanColdAllocatedBytes": allocation,
                       "baselineMeanColdAllocatedBytes": base_allocation, "peakWorkingSetDeltaBytes": delta,
                       "failingCases": len(report["failingCases"]), "baselineFailingCases": len(baseline["failingCases"]),
                       "ambiguous": sum(r["assessment"]["reasonCode"] == "ambiguous" for r in report["reports"]),
                       "maxIndexPrefixBytes": max((r["indexPrefix"]["boundBytes"] for r in cold(report) if r.get("indexPrefix")), default=None)}

    evaluated, qualifies = [], {}
    for depth, names in manifest["decision"]["variantSets"].items():
        ok = True
        for entry in manifest["matrix"]:
            family = entry["family"]
            for name in (n for n in entry["variants"] if n in names):
                key, base_key = (family, name), (family, variants[name]["baseline"])
                if key not in reports or base_key not in reports:
                    undecidable.append(f"{family}/{name}: missing run or baseline")
                    continue
                found, numbers = gates(family, name, reports[key], reports[base_key], depth)
                gating = family in envelope
                ok &= not (gating and found)
                evaluated.append({"family": family, "variant": name, "depth": depth, "gating": gating,
                                  "passed": not found, "findings": [{"gate": g, "detail": d} for g, d in found], **numbers})
        qualifies[depth] = ok
    qualifies["6"] = qualifies["6"] and qualifies["5.5"]
    outcomes = manifest["decision"]["outcomes"]
    outcome = None if undecidable else outcomes[0] if qualifies["6"] else outcomes[1] if qualifies["5.5"] else outcomes[2]

    grid_rows = [r["isolation"] | {"family": f, "variant": v, "caseId": r["caseId"]}
                 for (f, v), rep in reports.items() if f in envelope and v in ("m60", "d60", "x60")
                 for r in cold(rep) if r.get("isolation")]
    shares = [r["estimatedPairwiseShare"] for r in grid_rows if r["estimatedPairwiseShare"] is not None]
    worst = max((r["pairwiseMs"] * r["distinctCandidates"] for r in grid_rows), default=0)
    median_share = statistics.median(shares) if shares else None
    keep = bool(shares) and (median_share >= 0.05 or worst >= 1500)
    grid = {"rows": len(grid_rows), "medianEstimatedPairwiseShare": median_share, "maximumEstimatedPairwiseMs": worst,
            "medianGridMs": statistics.median(r["gridMs"] for r in grid_rows) if grid_rows else None,
            "medianPairwiseMs": statistics.median(r["pairwiseMs"] for r in grid_rows) if grid_rows else None,
            "decision": "keep" if keep else "revert"}

    result = {
        "schema": "virtual-deep-astrometry-decision-v1",
        "manifestSha256": hashlib.sha256(manifest_path.read_bytes()).hexdigest(),
        "revision": index["revision"],
        "undecidable": undecidable,
        "qualifies": qualifies,
        "outcome": outcome,
        "evaluated": evaluated,
        "evaluateGrid": grid,
    }
    if tuning is not None:
        tuning_index = load(tuning / "index.json")
        summary = []
        for run in tuning_index["runs"]:
            path = tuning / "reports" / run["name"] / manifest["runs"]["tuning"]["report"]
            rep = load(path) if path.is_file() else None
            summary.append({"run": run["name"], "status": run["status"],
                            "failingCases": rep and len(rep["failingCases"]), "coldP95Ms": rep and rep["coldP95Ms"]})
        result["tuning"] = {"gating": False, "status": tuning_index["status"], "runs": summary}
    if mag7 is not None:
        mag7_index = load(mag7 / "index.json")
        summary = []
        for run in mag7_index["runs"]:
            path = mag7 / "reports" / run["name"] / manifest["runs"]["mag7"]["report"]
            if not path.is_file():
                summary.append({"family": run["family"], "status": run["status"]})
                continue
            rows = load(path)["reports"]
            elapsed = [r["elapsedMs"] for r in rows if r["elapsedMs"] is not None]
            summary.append({
                "family": run["family"], "views": len(rows), "accepted": sum(bool(r["accepted"]) for r in rows),
                "timedOut": sum(r["timedOut"] for r in rows), "exceedsEvidenceBound": sum(r["exceedsEvidenceBound"] for r in rows),
                "reasons": sorted({str(r["reason"]) for r in rows if not r["accepted"]}),
                "maximumElapsedMs": max(elapsed, default=None), "maximumAllocatedBytes": max(r["solveAllocatedBytes"] for r in rows),
                "maximumIndexStars": max((r["indexStars"] or 0) for r in rows),
                "maximumPoseErrorDegrees": max((r["poseErrorDegrees"] for r in rows if r["poseErrorDegrees"] is not None), default=None),
                "falseAssociations": sum(r["falseAssociations"] for r in rows),
                "peakWorkingSetBytes": max(r["processPeakWorkingSetBytes"] for r in rows)})
        result["magnitude7"] = {"gating": False, "advertised": False, "status": mag7_index["status"], "families": summary}
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    label = "undecidable" if undecidable else f"{outcome['maximumEntries']}/{outcome['qualifiedMaximumMagnitude']}"
    print(f"qualifies 5.5={qualifies['5.5']} 6.0={qualifies['6']}; outcome {label}; evaluate grid {grid['decision']}")
    for item in undecidable:
        print(f"  undecidable: {item}")
    return 1 if undecidable else 0


def main(argv):
    if len(argv) >= 5 and argv[1] == "ab":
        return ab(Path(argv[2]), Path(argv[3]), Path(argv[4]))
    if 5 <= len(argv) <= 7 and argv[1] == "deep":
        optional = [Path(p) if p != "-" else None for p in argv[5:]] + [None, None]
        return deep(Path(argv[2]), Path(argv[3]), Path(argv[4]), optional[0], optional[1])
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
