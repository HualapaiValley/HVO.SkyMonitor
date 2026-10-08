#!/usr/bin/env bash
set -euo pipefail
benchmark_mount=$1
benchmark_host=$2
benchmark_image=hvo/timelapse-benchmark:1130-f6f3f27f

run_profile() {
    local dataset=$1 preset=$2 cpus=$3
    docker run --rm --name "hvo-1130-${benchmark_host}-motion-run" --network none \
        --cpus "$cpus" --cpu-shares 128 --pids-limit 128 -v "$benchmark_mount:/data" \
        "$benchmark_image" python3 /data/encode.py \
        "/data/motion-inputs/$dataset" \
        "/data/results/${benchmark_host}-motion/$dataset-$preset-${cpus}cpu.mp4" \
        --mode vfr --preset "$preset" --threads "$cpus"
}

run_profile color-1280x1280-motion-hour fast 1
run_profile mono-3552x3552-motion-hour veryfast 1
run_profile color-3552x3552-motion-hour veryfast 1
run_profile color-3552x3552-day-stable veryfast 1
run_profile color-3552x3552-motion-hour fast 1
run_profile color-3552x3552-motion-hour veryfast 2
echo MOTION_RUNS_COMPLETE
