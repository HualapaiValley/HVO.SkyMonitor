#!/usr/bin/env python3
"""Decomposes #1170 S4 saturation allocation per trial, to locate where the drain's allocation is counted.

Usage: python3 -I docs/validation/issue-1170-s4-allocation.py <slot dir> <output.json>

<slot dir> holds the two measure packs of one interleaved S4 pair, A-<revision> and B-<revision>. In saturation a
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


def spread(rows, key):
    values = [row[key] for row in rows]
    return {"minimum": min(values), "maximum": max(values)}


def main(argv):
    if len(argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    arms = {}
    for pack in sorted(glob.glob(os.path.join(argv[1], "[AB]-*"))):
        rows = [trial(path) for path in sorted(glob.glob(os.path.join(pack, "runs", "*", "evidence.json")))]
        arms[os.path.basename(pack)] = {
            "trials": rows,
            **{key: spread(rows, key) for key in ("residualShare", "wholeRunBytesPerOperation",
                                                  "saturationPhaseBytesPerOperation", "inWindowBytes")}}
    if len(arms) != 2:
        print("expected exactly one A-* and one B-* pack", file=sys.stderr)
        return 2
    a, b = (arms[name] for name in sorted(arms))
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
