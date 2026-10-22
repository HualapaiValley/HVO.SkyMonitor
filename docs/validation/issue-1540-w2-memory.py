"""Summarizes issue-1540-w2-memory.sh packs: per-arm process-memory distributions and the #1170 peak-RSS rule.

Usage: python3 -I issue-1540-w2-memory.py <baseline label> <candidate label> <pack dir> [<pack dir> ...]

Every run in every pack must have passed; the script refuses a pack with a failed or missing trial rather than
summarizing a subset. The peak-RSS rule is #1170's: a regression is a candidate median peak working set above the
baseline median by more than max(5 %, 32 MiB). Paired deltas use the pair number, so B-C and C-B orders both count.
"""

import json
import statistics
import sys
from pathlib import Path

MIB = 1024 * 1024


def load(packs, labels):
    runs = []
    for pack in packs:
        index = json.loads((Path(pack) / "index.json").read_text())
        if index["schema"] != "issue1540-w2-memory-pack-v1" or set(index["order"]) != set(labels):
            sys.exit(f"{pack}: wrong pack schema or arms")
        if len(index["runs"]) != 2 * index["pairs"]:
            sys.exit(f"{pack}: missing or extra trials")
        revisions = {arm["label"]: arm["revision"] for arm in index["arms"]}
        seen = set()
        for run in index["runs"]:
            key = (run["pair"], run["label"])
            if key in seen or key[0] not in range(1, index["pairs"] + 1) or key[1] not in labels:
                sys.exit(f"{pack}: duplicate or invalid trial {key}")
            seen.add(key)
            if run["exitCode"] != 0 or run["passedTests"] != 1:
                sys.exit(f"{pack}: {run['name']} did not pass")
            data = json.loads((Path(pack) / "runs" / run["name"] / "data.json").read_text())
            if data["runtimeProfile"] != "production-defaults" or data["workload"] != "w2-night":
                sys.exit(f"{pack}: {run['name']} is not a production-default w2-night trial")
            if data["revision"] != run["revision"] or data["label"] != run["label"] or data["trial"] != run["pair"]:
                sys.exit(f"{pack}: {run['name']} does not match its recorded revision or trial")
            if run["revision"] != revisions[run["label"]] or run["revision"] not in data["binaryRevision"]:
                sys.exit(f"{pack}: {run['name']} does not match the prepared binary revision")
            if data["failures"] or data["protocol"]["warmup"] != 5 or data["protocol"]["measured"] != 30:
                sys.exit(f"{pack}: {run['name']} failed or used a different protocol")
            if len(data["samples"]) != 35 or sum(sample["measured"] for sample in data["samples"]) != 30:
                sys.exit(f"{pack}: {run['name']} has incomplete operations")
            if data["collectorAttached"] != (index["mode"] == "attribute"):
                sys.exit(f"{pack}: {run['name']} has the wrong collector state")
            runs.append({"pack": pack, "mode": index["mode"], "run": run, "data": data})
    return runs


def metrics(data):
    samples = data["samples"]
    measured = [sample for sample in samples if sample["measured"]]
    final = data["memory"]["status"]
    peak = final["VmHWM"]
    # VmHWM is monotonic, so the first sample at the final value is the operation that set the peak; -1 means the
    # peak was already reached during setup, before the first operation completed.
    peak_ordinal = next((sample["ordinal"] for sample in samples if sample["status"]["VmHWM"] >= peak), -2)
    if samples and samples[0]["status"]["VmHWM"] >= peak:
        peak_ordinal = -1
    setup_allocated = sum(phase.get("allocatedBytes", 0) for phase in data["setup"].values() if isinstance(phase, dict))
    totals = data["totals"]
    return {
        "peakWorkingSetBytes": data["memory"]["peakWorkingSetBytes"],
        "vmHwmBytes": peak,
        "finalRssAnonBytes": final["RssAnon"],
        "finalRssFileBytes": final["RssFile"],
        "firstMeasuredRssAnonBytes": measured[0]["status"]["RssAnon"],
        "lastMeasuredRssAnonBytes": measured[-1]["status"]["RssAnon"],
        "measuredRssAnonGrowthBytes": measured[-1]["status"]["RssAnon"] - measured[0]["status"]["RssAnon"],
        "firstSampleVmHwmBytes": samples[0]["status"]["VmHWM"],
        "peakOrdinal": peak_ordinal,
        "lohSizeAfterBytes": data["memory"]["lohSizeAfterBytes"],
        "pohSizeAfterBytes": data["memory"]["pohSizeAfterBytes"],
        "gcPauseMilliseconds": data["memory"]["gcPauseMilliseconds"],
        "allocatedBytesPerOperationMedian": data["summary"]["allocatedBytesPerOperation"]["median"],
        "setupAllocatedBytes": setup_allocated,
        "totalAllocatedBytes": totals["allocatedBytes"],
        "gen0": totals["gen0"],
        "gen1": totals["gen1"],
        "gen2": totals["gen2"],
        "threads": final["Threads"],
        "serviceMillisecondsMedian": data["summary"]["serviceMilliseconds"]["median"],
    }


def describe(values):
    ordered = sorted(values)
    median = statistics.median(ordered)
    return {
        "n": len(ordered),
        "median": median,
        "minimum": ordered[0],
        "maximum": ordered[-1],
        "spread": ordered[-1] - ordered[0],
        "spreadPercentOfMedian": 100 * (ordered[-1] - ordered[0]) / median if median else None,
        "values": ordered,
    }


def main():
    if len(sys.argv) < 4:
        sys.exit(__doc__)
    baseline, candidate, packs = sys.argv[1], sys.argv[2], sys.argv[3:]
    runs = load(packs, (baseline, candidate))
    modes = {run["mode"] for run in runs}
    if len(modes) != 1:
        sys.exit(f"packs mix modes {sorted(modes)}; summarize measure and attribute packs separately")
    by_arm = {baseline: [], candidate: []}
    pairs = {}
    for run in runs:
        label = run["run"]["label"]
        if label not in by_arm:
            sys.exit(f"unexpected arm {label}")
        values = metrics(run["data"])
        by_arm[label].append(values)
        pairs.setdefault((run["pack"], run["run"]["pair"]), {})[label] = values
    if modes == {"measure"} and any(len(rows) < 5 for rows in by_arm.values()):
        sys.exit("measurement acceptance needs at least five trials per arm")
    for label in by_arm:
        revisions = {run["data"]["revision"] for run in runs if run["run"]["label"] == label}
        if len(revisions) != 1:
            sys.exit(f"arm {label} mixes revisions")
    keys = list(by_arm[baseline][0])
    arms = {label: {key: describe([values[key] for values in rows]) for key in keys} for label, rows in by_arm.items()}
    paired = {}
    complete = [pair for pair in pairs.values() if baseline in pair and candidate in pair]
    for key in keys:
        deltas = [pair[candidate][key] - pair[baseline][key] for pair in complete]
        paired[key] = describe(deltas) if deltas else None
    base_peak = arms[baseline]["peakWorkingSetBytes"]["median"]
    delta = arms[candidate]["peakWorkingSetBytes"]["median"] - base_peak
    threshold = max(0.05 * base_peak, 32 * MIB)
    result = {
        "schema": "issue1540-w2-memory-summary-v1",
        "mode": modes.pop(),
        "packs": packs,
        "baseline": baseline,
        "candidate": candidate,
        "peakRssRule": {
            "baselineMedianBytes": base_peak,
            "candidateMedianBytes": arms[candidate]["peakWorkingSetBytes"]["median"],
            "deltaBytes": delta,
            "deltaPercent": 100 * delta / base_peak,
            "thresholdBytes": threshold,
            "regression": delta > threshold,
        },
        "arms": arms,
        "pairedCandidateMinusBaseline": paired,
    }
    json.dump(result, sys.stdout, indent=2)
    print()
    print(f"peak RSS: baseline median {base_peak / MIB:.1f} MiB, candidate median "
          f"{arms[candidate]['peakWorkingSetBytes']['median'] / MIB:.1f} MiB, delta {delta / MIB:+.1f} MiB "
          f"({100 * delta / base_peak:+.2f} %), threshold {threshold / MIB:.1f} MiB, "
          f"regression={delta > threshold}", file=sys.stderr)
    return int(result["mode"] == "measure" and delta > threshold)


if __name__ == "__main__":
    sys.exit(main())
