#!/usr/bin/env bash
set -euo pipefail
benchmark_mount=$1
benchmark_host=$2
benchmark_image=hvo/timelapse-benchmark:1130-f6f3f27f

run_limited() {
    local cpu_count=$1
    shift
    docker run --rm --name "hvo-1130-${benchmark_host}-followup" --network none \
        --cpus "$cpu_count" --cpu-shares 128 --pids-limit 128 \
        -v "$benchmark_mount:/data" "$benchmark_image" "$@"
}

run_limited 1 python3 /data/sweep.py "/data/results/${benchmark_host}-repeat-1280" \
    --datasets color-1280x1280-night --modes cfr30 vfr --trials 5
run_limited 1 python3 /data/sweep.py "/data/results/${benchmark_host}-repeat-full" \
    --datasets color-3552x3552-night --modes vfr --trials 5
run_limited 1 python3 /data/sweep.py "/data/results/${benchmark_host}-veryfast-1cpu" \
    --datasets color-3552x3552-night --modes cfr30 vfr --preset veryfast
run_limited 2 python3 /data/sweep.py "/data/results/${benchmark_host}-veryfast-2cpu" \
    --datasets color-3552x3552-night --modes cfr30 vfr --preset veryfast --threads 2
run_limited 1 python3 /data/sweep.py "/data/results/${benchmark_host}-hour" \
    --datasets color-1280x1280-night --modes vfr --repeat 12
run_limited 1 python3 /data/stitch.py \
    "/data/results/${benchmark_host}-hour/color-1280x1280-night-vfr-fast-t1-r12-1.mp4" \
    "/data/results/${benchmark_host}-daily/daily.mp4" --count 24
echo FOLLOWUPS_COMPLETE
