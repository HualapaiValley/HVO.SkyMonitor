#!/usr/bin/env python3
"""Splits a #1170 dotnet-sampled-thread-time trace into blocked and running time.

Usage: python3 -I docs/validation/issue-1170-oncpu.py <trace.speedscope.json> <output.json> [top N]

dotnet-trace's EventPipe sample profiler samples every managed thread whether it runs or waits, so its
topN report is dominated by idle thread-pool and test-host threads. This walks the evented Speedscope
export per thread. Each interval ends in the profiler's CPU_TIME (managed code running) or
UNMANAGED_CODE_TIME (native: a wait, an I/O syscall or native work) pseudo-frame; native intervals are
classified by the deepest real frame (wait, I/O, or native running). GC suspension and unresolved native
frames (?!?, which perf would resolve but is unavailable here) are totalled separately. Frames are ranked by running time
only: exclusive by deepest real frame, inclusive once per distinct frame on the stack. The classification
is a heuristic over frame names and is recorded in the output alongside its patterns.
"""
import json
import re
import sys
from collections import Counter

WAIT = re.compile(r"(Monitor\.Wait|WaitHandle\.Wait|LowLevelLifoSemaphore\.Wait|Thread\.Sleep|Interop\+Sys\.Poll"
                  r"|WaitForSignal|WaitNative|SpinWait|EventPipe|Interop\+Sys\.Read\(class Microsoft\.Win32\.SafeHandles\.SafePipeHandle)")
# Threads parked for a GC suspension, and the finalizer/Gen2 callbacks that run behind it, sample as running
# managed code; they are counted separately so allocation pressure is visible without inflating hot frames.
GC = re.compile(r"(Thread\.<PollGC>|GC\.RunFinalizers|SharedArrayPool`1.*\.Trim\(|InitializeTlsBucketsAndTrimming)")
UNRESOLVED = "?!?"
PSEUDO = {"CPU_TIME", "UNMANAGED_CODE_TIME"}
IO = re.compile(r"Interop\+Sys\.(FSync|PWrite|PRead|Write|Read|Open|Stat|LStat|FStat|Unlink|Rename|FTruncate|FAllocate|MkDir|Close)\b")


def main(argv):
    if len(argv) not in (3, 4):
        print(__doc__, file=sys.stderr)
        return 2
    top = int(argv[3]) if len(argv) == 4 else 60
    with open(argv[1], encoding="utf-8") as stream:
        document = json.load(stream)
    frames = [frame["name"] for frame in document["shared"]["frames"]]
    totals = Counter()
    exclusive = Counter()
    inclusive = Counter()
    io_leaf = Counter()
    for profile in document["profiles"]:
        stack = []
        last = None
        for event in profile.get("events", []):
            at = event["at"]
            if stack and last is not None and at > last:
                span = at - last
                marker = frames[stack[-1]]
                real = [index for index in stack if frames[index] not in PSEUDO]
                leaf = frames[real[-1]] if real else marker
                if GC.search(leaf):
                    totals["gcSuspensionMilliseconds"] += span
                elif leaf == UNRESOLVED:
                    totals["unresolvedNativeMilliseconds"] += span
                elif marker == "UNMANAGED_CODE_TIME" and WAIT.search(leaf):
                    totals["waitMilliseconds"] += span
                elif marker == "UNMANAGED_CODE_TIME" and IO.search(leaf):
                    totals["ioMilliseconds"] += span
                    io_leaf[leaf] += span
                else:
                    key = "managedRunningMilliseconds" if marker == "CPU_TIME" else "nativeOrUnclassifiedMilliseconds"
                    totals[key] += span
                    totals["runningMilliseconds"] += span
                    exclusive[leaf] += span
                    for name in {frames[index] for index in real}:
                        inclusive[name] += span
            last = at
            if event["type"] == "O":
                stack.append(event["frame"])
            elif stack:
                stack.pop()
    running = totals["runningMilliseconds"] or 1.0

    def ranked(counter):
        return [{"frame": name, "milliseconds": round(value, 3), "runningShare": round(value / running, 5)}
                for name, value in counter.most_common(top)]

    result = {
        "schema": "issue1170-oncpu-v1",
        "source": argv[1],
        "classification": {"wait": WAIT.pattern, "io": IO.pattern, "gcSuspension": GC.pattern, "unresolvedNative": UNRESOLVED},
        "threads": len(document["profiles"]),
        "totals": {key: round(value, 3) for key, value in totals.items()},
        "exclusive": ranked(exclusive),
        "inclusive": ranked(inclusive),
        "ioLeaves": [{"frame": name, "milliseconds": round(value, 3)} for name, value in io_leaf.most_common(top)],
    }
    with open(argv[2], "w", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
