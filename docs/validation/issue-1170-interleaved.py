#!/usr/bin/env python3
"""Compares #1170 baseline and after evidence measured as interleaved per-cell pairs, and checks output identity.

Usage:
  python3 -I docs/validation/issue-1170-interleaved.py pairs <interleaved root> <output.json> <expected cell ids>
  python3 -I docs/validation/issue-1170-interleaved.py s1 <baseline s1 dir> <after s1 dir> <output.json>

Timings on the measurement host drift by about 6% between sessions an hour apart (the #520 addendum 004 control),
so a baseline pack and an after pack measured in separate sessions are not comparable. Instead every measure cell
is run as an adjacent pair: <root>/<NN>-<cell>/A-<revision> (baseline arm) and B-<revision> (after arm). Each arm
is an unchanged docs/validation/issue-1170-pipeline.sh measure invocation restricted to that cell. Odd cells ran
A then B, even cells B then A.

pairs, per cell, compares each arm against its own adjacent pair only:
- Coverage: the root must hold at least one measure slot, every slot exactly one A-* and one B-* pack measuring
  the same single manifest cell, and each arm the manifest's trial count (1..trials) under the manifest it was
  measured with; no cell may appear twice. The required comma-separated <expected cell ids> (manifest cell ids)
  must equal the set of measured cells exactly, so a missing slot is a gap rather than a smaller comparison. Any gap is incomplete. Slots whose packs are both mode s1 are
  listed and left to the s1 mode.
- Timings (trial medians of foreground, processing and service; cold first-operation service; S4 drain): B
  regressed when its fastest trial is slower than A's slowest, improved when its slowest trial beats A's fastest.
  Otherwise the change is not resolvable at this host's noise.
- Allocated bytes per operation and peak RSS keep the manifest rule (above 1%; above max(5%, 32 MiB)).
- Outputs: every trial's per-sample rawSha256, rawBytes and measured flag, every pipeline node's presence, status, outcome and
  output count (outputless storage and telemetry nodes included), and every output's payloadSha256, payloadBytes,
  recipeIdentitySha256 and outputIdentitySha256. A value that differs between A's own trials is run-variant for
  that one record and field only, and only RUN_VARIANT_ALLOWED fields may be; B must equal A on every other value.
  Any difference, and any other run-variant value, is an output difference.

s1 compares two docs/validation/issue-1106-qualification.sh packs leaf by leaf, ignoring a narrow,
recorded set of timing, resource and provenance keys. Every other difference is an output difference.

Exit status is 0 when nothing regressed and outputs are identical, otherwise 1.
"""
import glob
import hashlib
import json
import os
import re
import subprocess
import sys

METRICS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "issue-1170-metrics.sh")
OUTPUT_FIELDS = ("payloadSha256", "payloadBytes", "recipeIdentitySha256", "outputIdentitySha256")
# Identity hashes that can bind run provenance may vary between a revision's own trials; payload bytes, raw
# samples, statuses and outcomes may not, so a run-variant value of any other field is a difference.
RUN_VARIANT_ALLOWED = frozenset(("recipeIdentitySha256", "outputIdentitySha256"))
MANIFEST = os.path.join(os.path.dirname(os.path.abspath(__file__)), "issue-1170-pipeline-manifest.json")
# Deliberately narrow: statistical words such as median, mean, min, max or rate also name astrometric outputs,
# so they never mark a key as timing; a timing field they name surfaces as a difference for review.
S1_TIMING = re.compile(
    r"(milliseconds?$|millis$|ms$|seconds$|elapsed|duration|utc$|timestamp|startedat|finishedat|"
    r"allocatedbytes|allocated$|gc(count|collections)|gen[012]collections|peakrss|rssbytes|workingset|"
    r"jit(time|milliseconds)|cputime|cpumilliseconds|stopwatch|ticks$|^revision$|informationalversion|"
    r"^hostname$|processid|^operationspersecond$|^processedframespersecond$|^initialprocesspeakbytes$|^binaryrevision$)",
    re.IGNORECASE)


def load(path):
    with open(path, encoding="utf-8") as stream:
        return json.load(stream)


def summarize(pack):
    result = subprocess.run(["bash", METRICS, pack], check=True, capture_output=True, text=True)
    return json.loads(result.stdout)


def bounds(spread):
    if not spread or spread.get("median") is None:
        return None
    return {"median": spread["median"], "minimum": spread["minimum"], "maximum": spread["maximum"]}


def timing_verdict(a, b):
    a, b = bounds(a), bounds(b)
    if a is None or b is None:
        return None
    verdict = ("regressed" if b["minimum"] > a["maximum"] else
               "improved" if b["maximum"] < a["minimum"] else "no resolvable change")
    change = None if a["median"] == 0 else (b["median"] - a["median"]) / a["median"]
    return {"armA": a, "armB": b, "medianChange": change, "verdict": verdict}


def output_records(pack):
    trials = []
    for path in sorted(glob.glob(os.path.join(pack, "runs", "*", "evidence.json"))):
        document = load(path)
        # A sample's "status" is its /proc/self/status memory snapshot, a resource reading, so it is not compared.
        records = {("raw", index): {field: sample.get(field) for field in ("rawSha256", "rawBytes", "measured")}
                   for index, sample in enumerate(document["samples"])}
        for index, node in enumerate(document["outputs"]):
            # Every node gets its own record, so an outputless storage or telemetry node's status is compared too.
            records[("node", index, node["node"])] = {"status": node.get("status"), "outcome": node.get("outcome"),
                                                      "outputCount": len(node["outputs"])}
            for position, output in enumerate(node["outputs"]):
                key = ("output", index, node["node"], position, output.get("role"), output.get("variant"))
                records[key] = {field: output.get(field) for field in OUTPUT_FIELDS}
        trials.append((os.path.basename(os.path.dirname(path)), records))
    return trials


def output_identity(a_pack, b_pack):
    a, b = output_records(a_pack), output_records(b_pack)
    if not a or not b:
        return {"verdict": "difference", "reason": "an arm has no trial evidence"}
    layouts = {frozenset(records) for _, records in a} | {frozenset(records) for _, records in b}
    if len(layouts) != 1:
        return {"verdict": "difference", "reason": "sample/output layout differs between trials or arms"}
    reference = a[0][1]
    node = lambda key: key[2] if key[0] != "raw" else "raw"
    # Run-variance is per record and field: one varying output never excuses the same field of another record.
    variant = {(key, field) for key, values in reference.items() for field, value in values.items()
               if any(records[key][field] != value for _, records in a[1:])}
    differences = [{"trial": "A trials", "key": [str(part) for part in key], "field": field,
                    "difference": "run-variant field not in RUN_VARIANT_ALLOWED"}
                   for key, field in sorted(variant, key=str) if field not in RUN_VARIANT_ALLOWED]
    compared = 0
    for key, values in reference.items():
        for field, value in values.items():
            if (key, field) in variant:
                continue
            compared += 1
            differences += [{"trial": name, "key": [str(part) for part in key], "field": field,
                             "armA": value, "armB": records[key][field]}
                            for name, records in b if records[key][field] != value]
    excluded = {}
    for key, field in variant:
        excluded[f"{node(key)}.{field}"] = excluded.get(f"{node(key)}.{field}", 0) + 1
    return {"verdict": "identical" if not differences else "difference", "comparedValues": compared,
            "runVariantFields": sorted(excluded), "runVariantValues": dict(sorted(excluded.items())),
            "differenceCount": len(differences), "differences": differences[:100]}


def trial_numbers(pack):
    return sorted(run.get("trial") for run in load(os.path.join(pack, "index.json"))["runs"])


def manifest_cells(pack):
    return sorted({run.get("cell") for run in load(os.path.join(pack, "index.json"))["runs"]}, key=str)


def coverage_gap(pack, manifest_sha, trials):
    index = load(os.path.join(pack, "index.json"))
    if index.get("mode") != "measure":
        return f"{os.path.basename(pack)} is a {index.get('mode')} pack, not measure"
    if index.get("manifestSha256") != manifest_sha:
        return f"{os.path.basename(pack)} was measured with manifest {index.get('manifestSha256')}, not {manifest_sha}"
    if len(manifest_cells(pack)) != 1:
        return f"{os.path.basename(pack)} measures cells {manifest_cells(pack)}, not exactly one"
    if trial_numbers(pack) != list(range(1, trials + 1)):
        return f"{os.path.basename(pack)} has trials {trial_numbers(pack)}, not 1..{trials}"
    return None


def pairs(root, output, expected):
    cells, failed = [], False
    with open(MANIFEST, "rb") as stream:
        manifest_sha = hashlib.sha256(stream.read()).hexdigest()
    trials = load(MANIFEST)["trials"]
    slots, s1_slots = [], []
    for slot in sorted(entry for entry in os.listdir(root) if os.path.isdir(os.path.join(root, entry))):
        packs = sorted(name for name in os.listdir(os.path.join(root, slot))
                       if name[:2] in ("A-", "B-") and os.path.isfile(os.path.join(root, slot, name, "index.json")))
        if packs and all(load(os.path.join(root, slot, name, "index.json")).get("mode") == "s1" for name in packs):
            s1_slots.append(slot)
            continue
        slots.append(slot)
        # Exactly one pack per arm: a second A-* pack must not silently replace the first.
        if sorted(name[0] for name in packs) != ["A", "B"]:
            cells.append({"slot": slot, "verdict": "incomplete", "arms": packs})
            failed = True
            continue
        arms = {name[0]: os.path.join(root, slot, name) for name in packs}
        gaps = [gap for gap in (coverage_gap(arms[arm], manifest_sha, trials) for arm in "AB") if gap]
        if not gaps and manifest_cells(arms["A"]) != manifest_cells(arms["B"]):
            gaps.append(f"arms measure different cells {manifest_cells(arms['A'])} and {manifest_cells(arms['B'])}")
        if gaps:
            cells.append({"slot": slot, "verdict": "incomplete", "reasons": gaps})
            failed = True
            continue
        a, b = summarize(arms["A"]), summarize(arms["B"])
        order = sorted(arms, key=lambda arm: load(os.path.join(arms[arm], "index.json"))["runs"][0]["startedUtc"])
        if a["failedRuns"] or b["failedRuns"] or len(a["cells"]) != 1 or a["cells"].keys() != b["cells"].keys():
            cells.append({"slot": slot, "verdict": "incomplete", "failedRuns": a["failedRuns"] + b["failedRuns"]})
            failed = True
            continue
        (cell, ca), = a["cells"].items()
        cb = b["cells"][cell]
        timing = {name: timing_verdict(ca.get(key) if path is None else (ca.get(path) or {}).get(key),
                                       cb.get(key) if path is None else (cb.get(path) or {}).get(key))
                  for name, path, key in (("foregroundMs", None, "foregroundMs"), ("processingMs", None, "processingMs"),
                                          ("serviceMs", None, "serviceMs"), ("coldFirstOperationServiceMs", "cold", "firstOperationServiceMs"),
                                          ("drainAfterLastAcceptMs", "saturation", "drainAfterLastAcceptMs"))}
        timing = {name: value for name, value in timing.items() if value is not None}
        alloc_a, alloc_b = ca["allocatedBytesPerOperation"]["median"], cb["allocatedBytesPerOperation"]["median"]
        rss_a, rss_b = ca["peakRssBytes"]["median"], cb["peakRssBytes"]["median"]
        resources = {
            "allocatedBytesPerOperation": {"armA": alloc_a, "armB": alloc_b, "change": (alloc_b - alloc_a) / alloc_a,
                                           "regressed": (alloc_b - alloc_a) / alloc_a > 0.01},
            "peakRssBytes": {"armA": rss_a, "armB": rss_b, "change": (rss_b - rss_a) / rss_a,
                             "regressed": rss_b - rss_a > max(0.05 * rss_a, 32 * 1024 * 1024)}}
        outputs = output_identity(arms["A"], arms["B"])
        regressed = any(value["verdict"] == "regressed" for value in timing.values()) or \
            any(value["regressed"] for value in resources.values())
        failed |= regressed or outputs["verdict"] != "identical"
        cells.append({"slot": slot, "cell": cell, "manifestCell": manifest_cells(arms["A"])[0], "order": "".join(order), "armA": a["revision"], "armB": b["revision"],
                      "armAProductSourceUnchangedFromBase": a["productSourceUnchangedFromBase"],
                      "timing": timing, "resources": resources, "regressed": regressed, "outputs": outputs})
    measured = [cell["manifestCell"] for cell in cells if "cell" in cell]
    coverage = []
    if not slots:
        coverage.append("the root holds no cell slots")
    coverage += [f"cell {cell} is measured in more than one slot" for cell in sorted(set(measured)) if measured.count(cell) > 1]
    wanted = {cell for cell in expected.split(",") if cell}
    if not wanted:
        coverage.append("no expected cells were given")
    coverage += [f"expected cell {cell} is not measured" for cell in sorted(wanted - set(measured))]
    coverage += [f"cell {cell} is not expected" for cell in sorted(set(measured) - wanted)]
    failed |= bool(coverage)
    report = {"schema": "issue1170-interleaved-comparison-v2", "root": root, "manifestSha256": manifest_sha,
              "manifestTrials": trials, "expectedCells": expected, "coverageGaps": coverage, "s1Slots": s1_slots,
              "cells": cells,
              "regressed": any(cell.get("regressed") for cell in cells),
              "outputsIdentical": bool(cells) and all(cell.get("outputs", {}).get("verdict") == "identical" for cell in cells),
              "complete": not coverage and all("cell" in cell for cell in cells)}
    with open(output, "w", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, default=str)
    for cell in cells:
        timing = " ".join(f"{name}={value['verdict']}({value['medianChange']:+.1%})" for name, value in cell.get("timing", {}).items())
        print(f"{cell['slot']} order={cell.get('order')} {timing} "
              f"alloc={cell.get('resources', {}).get('allocatedBytesPerOperation', {}).get('change', float('nan')):+.2%} "
              f"outputs={cell.get('outputs', {}).get('verdict', cell.get('verdict'))}")
    for gap in coverage:
        print(f"coverage: {gap}")
    print(f"regressed={report['regressed']} outputsIdentical={report['outputsIdentical']} complete={report['complete']}")
    return 1 if failed else 0


def leaves(value, path=()):
    if isinstance(value, dict):
        for key, child in value.items():
            yield from leaves(child, path + (str(key),))
    elif isinstance(value, list):
        for index, child in enumerate(value):
            yield from leaves(child, path + (f"[{index}]",))
    else:
        yield path, value


def s1(base, after, output):
    bi, ai = load(os.path.join(base, "index.json")), load(os.path.join(after, "index.json"))
    bruns, aruns = {run["name"]: run for run in bi["runs"]}, {run["name"]: run for run in ai["runs"]}
    differences, ignored, identical = [], {}, []
    for name in sorted(set(bruns) | set(aruns)):
        if name not in bruns or name not in aruns:
            differences.append({"run": name, "difference": "run present on one side only"})
            continue
        b, a = bruns[name], aruns[name]
        differences += [{"run": name, "field": field, "baseline": b.get(field), "after": a.get(field)}
                        for field in ("id", "status", "exitCode", "expectedPassed", "workload") if b.get(field) != a.get(field)]
        breports, areports = {r["report"]: r for r in b.get("reports", [])}, {r["report"]: r for r in a.get("reports", [])}
        for report in sorted(set(breports) | set(areports)):
            if report not in breports or report not in areports:
                differences.append({"run": name, "report": report, "difference": "report present on one side only"})
                continue
            if breports[report]["sha256"] == areports[report]["sha256"]:
                identical.append(f"{name}/{report}")
                continue
            file = report.rsplit("/", 1)[-1]
            bl = dict(leaves(load(os.path.join(base, "reports", name, file))))
            al = dict(leaves(load(os.path.join(after, "reports", name, file))))
            for path in sorted(set(bl) | set(al)):
                if bl.get(path, "<absent>") == al.get(path, "<absent>"):
                    continue
                if path in bl and path in al and any(S1_TIMING.search(part) for part in path if not part.startswith("[")):
                    generic = re.sub(r"\[\d+\]", "[]", "/".join(path))
                    ignored[generic] = ignored.get(generic, 0) + 1
                else:
                    differences.append({"run": name, "report": report, "path": "/".join(path),
                                        "baseline": bl.get(path, "<absent>"), "after": al.get(path, "<absent>")})
    passed = bi["status"] == ai["status"] == "passed"
    report = {"schema": "issue1170-s1-output-identity-v1", "baselineRevision": bi["revision"], "afterRevision": ai["revision"],
              "manifestSha256": {"baseline": bi["manifestSha256"], "after": ai["manifestSha256"]},
              "status": {"baseline": bi["status"], "after": ai["status"]}, "ignoredKeyPattern": S1_TIMING.pattern,
              "byteIdenticalReports": identical, "ignoredTimingDifferences": dict(sorted(ignored.items())),
              "differenceCount": len(differences), "differences": differences[:500],
              "verdict": "identical" if passed and not differences else "difference"}
    with open(output, "w", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, default=str)
    print(f"verdict={report['verdict']} differences={len(differences)} byteIdenticalReports={len(identical)} "
          f"ignoredTimingKeys={len(ignored)}")
    return 0 if report["verdict"] == "identical" else 1


def main(argv):
    if len(argv) == 5 and argv[1] == "pairs":
        return pairs(argv[2], argv[3], argv[4])
    if len(argv) == 5 and argv[1] == "s1":
        return s1(argv[2], argv[3], argv[4])
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
