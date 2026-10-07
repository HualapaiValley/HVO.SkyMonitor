#!/usr/bin/env python3
"""Decomposes #1170 S4 saturation allocation per trial, to locate where the drain's allocation is counted.

Usage: python3 -I docs/validation/issue-1170-s4-allocation.py <slot dir> <output.json>

<slot dir> holds the two measure packs of one interleaved S4 pair, exactly one A-<revision> and one B-<revision>
directory, each indexed as a passed saturation measure pack; trials are read through each index's runs, never by
globbing. Archive sidecars and any other entry are ignored, and a slot that does not hold exactly that pair is
rejected. In saturation a
sample's allocatedBytes is the process-wide GC.GetTotalAllocatedBytes delta over that frame's foreground window
while a concurrent drain processes earlier frames, so drain allocation counts only when it falls inside a window.
Each trial's whole-run total is split as

    whole = setup + warmup + inWindow + residual

where setup sums the recorded setup phases, warmup sums the serial unmeasured operations, inWindow sums the
measured foreground deltas, and residual is the rest: drain work after the last accept, gaps between windows and
the final output read. residualShare is residual / (inWindow + residual), the saturation phase.
"""
import glob
import json
import os
import sys


def trial(path):
    document = json.load(open(path, encoding="utf-8"))
    samples = document["samples"]
    whole = document["totals"]["allocatedBytes"]
    setup = sum(phase["allocatedBytes"] for phase in document["setup"].values()
                if isinstance(phase, dict) and "allocatedBytes" in phase)
    warmup = sum(sample["allocatedBytes"] for sample in samples if not sample["measured"])
    in_window = sum(sample["allocatedBytes"] for sample in samples if sample["measured"])
    measured = sum(1 for sample in samples if sample["measured"])
    phase = whole - setup - warmup
    return {"trial": document["trial"], "revision": document["revision"], "measuredOperations": measured,
            "wholeRunBytes": whole, "setupBytes": setup, "warmupBytes": warmup, "inWindowBytes": in_window,
            "residualBytes": phase - in_window, "residualShare": (phase - in_window) / phase,
            "wholeRunBytesPerOperation": whole / measured, "saturationPhaseBytesPerOperation": phase / measured,
            "gen0": document["totals"]["gen0"], "backlogPeak": document["saturation"]["backlogPeak"],
            "drainAfterLastAcceptMilliseconds": document["saturation"]["drainAfterLastAcceptMilliseconds"]}


def pack_problem(pack):
    path = os.path.join(pack, "index.json")
    if not os.path.isfile(path):
        return "no index.json"
    index = json.load(open(path, encoding="utf-8"))
    runs = index.get("runs") or []
    if index.get("mode") != "measure" or index.get("status") != "passed" or not runs:
        return f"not a passed measure pack (mode {index.get('mode')}, status {index.get('status')}, {len(runs)} runs)"
    if any(run.get("status") != "passed" or run.get("scenario") != "saturation" for run in runs):
        return "every indexed run must be a passed saturation run"
    if len({run.get("cell") for run in runs}) != 1:
        return "the pack measures more than one cell"
    return None


def spread(rows, key):
    values = [row[key] for row in rows]
    return {"minimum": min(values), "maximum": max(values)}


def main(argv):
    if len(argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    arms = {}
    # Each pack's archive sidecars (A-<revision>.tar.zst.sha256) sit beside it in the slot.
    packs = sorted(path for path in glob.glob(os.path.join(argv[1], "[AB]-*")) if os.path.isdir(path))
    if sorted(os.path.basename(pack)[0] for pack in packs) != ["A", "B"]:
        print(f"expected exactly one A-* and one B-* pack, found {[os.path.basename(pack) for pack in packs]}", file=sys.stderr)
        return 2
    for pack in packs:
        problem = pack_problem(pack)
        if problem:
            print(f"{os.path.basename(pack)}: {problem}", file=sys.stderr)
            return 2
        index = json.load(open(os.path.join(pack, "index.json"), encoding="utf-8"))
        rows = [trial(os.path.join(pack, "runs", run["name"], "evidence.json")) for run in index["runs"]]
        arms[os.path.basename(pack)] = {
            "trials": rows,
            **{key: spread(rows, key) for key in ("residualShare", "wholeRunBytesPerOperation",
                                                  "saturationPhaseBytesPerOperation", "inWindowBytes")}}
    cells = {json.load(open(os.path.join(pack, "index.json"), encoding="utf-8"))["runs"][0]["cell"] for pack in packs}
    if len(cells) != 1:
        print(f"the A and B packs measure different cells {sorted(cells)}", file=sys.stderr)
        return 2
    a, b = (arms[os.path.basename(pack)] for pack in packs)
    report = {"schema": "issue1170-s4-allocation-v1", "slot": argv[1], "arms": arms,
              "residualShareSeparated": a["residualShare"]["minimum"] > b["residualShare"]["maximum"],
              "wholeRunPerOperationDisjoint": b["wholeRunBytesPerOperation"]["maximum"] < a["wholeRunBytesPerOperation"]["minimum"],
              "saturationPhasePerOperationDisjoint":
                  b["saturationPhaseBytesPerOperation"]["maximum"] < a["saturationPhaseBytesPerOperation"]["minimum"]}
    json.dump(report, open(argv[2], "w", encoding="utf-8"), indent=2)
    for name, arm in arms.items():
        print(f"{name}: residualShare {arm['residualShare']['minimum']:.3f}-{arm['residualShare']['maximum']:.3f} "
              f"wholeRun/op {arm['wholeRunBytesPerOperation']['minimum'] / 1e6:.1f}-"
              f"{arm['wholeRunBytesPerOperation']['maximum'] / 1e6:.1f} MB "
              f"saturation/op {arm['saturationPhaseBytesPerOperation']['minimum'] / 1e6:.1f}-"
              f"{arm['saturationPhaseBytesPerOperation']['maximum'] / 1e6:.1f} MB")
    print(f"residualShareSeparated={report['residualShareSeparated']} "
          f"wholeRunPerOperationDisjoint={report['wholeRunPerOperationDisjoint']} "
          f"saturationPhasePerOperationDisjoint={report['saturationPhasePerOperationDisjoint']}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
