#!/usr/bin/env python3
"""Bounded serial encoder load; finish the current file when the foreground test ends."""
import argparse
import json
from pathlib import Path
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument('output')
parser.add_argument('--threads', type=int, required=True)
args = parser.parse_args()
root = Path(args.output)
root.mkdir(parents=True, exist_ok=True)
started = time.monotonic()
index = 0
while not (root / 'stop').exists() and time.monotonic() - started < 240:
    index += 1
    subprocess.run(['python3', '/data/encode.py', '/data/inputs/color-3552x3552-night',
                    str(root / f'load-{index:03}.mp4'), '--mode', 'vfr', '--threads', str(args.threads),
                    '--skip-verify'], check=True)
(root / 'load-summary.json').write_text(json.dumps({
    'completedEncodes': index, 'elapsedSeconds': time.monotonic() - started,
    'cpuMax': Path('/sys/fs/cgroup/cpu.max').read_text().strip(),
    'cpuStat': Path('/sys/fs/cgroup/cpu.stat').read_text(),
    'note': 'Continuous serial replay of the qualified full-resolution fixture. Verification is excluded from load.'
}, indent=2))
