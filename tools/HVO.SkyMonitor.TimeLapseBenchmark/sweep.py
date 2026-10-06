#!/usr/bin/env python3
"""Run a serial screening matrix under the enclosing container's CPU cap."""
import argparse
import json
from pathlib import Path
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument('output')
parser.add_argument('--datasets', nargs='*')
parser.add_argument('--modes', nargs='+', default=['legacy60', 'cfr60', 'cfr30', 'vfr'])
parser.add_argument('--trials', type=int, default=1)
parser.add_argument('--threads', type=int, default=1)
parser.add_argument('--preset', default='fast')
parser.add_argument('--repeat', type=int, default=1)
args = parser.parse_args()
root = Path(args.output)
root.mkdir(parents=True, exist_ok=True)
metadata = {'startedUtc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
            'cpuMax': Path('/sys/fs/cgroup/cpu.max').read_text().strip(),
            'ffmpeg': subprocess.check_output(['ffmpeg', '-version'], text=True),
            'arguments': vars(args), 'cachePolicy': 'warm filesystem cache; container startup and verification excluded from encode time',
            'vfrProfile': 'B frames disabled after a rejected MP4 sample exposed incorrect stream duration with reordered variable-duration packets',
            'outputBoundary': 'encoding and output close to OS filesystem cache, not durable CameraAgent publication'}
(root / 'environment.json').write_text(json.dumps(metadata, indent=2))
datasets = args.datasets or sorted(path.parent.name for path in Path('/data/inputs').glob('*/sequence.json'))
for trial in range(args.trials):
    for dataset in datasets:
        for mode in args.modes:
            output = root / f'{dataset}-{mode}-{args.preset}-t{args.threads}-r{args.repeat}-{trial + 1}.mp4'
            command = ['python3', '/data/encode.py', f'/data/inputs/{dataset}', str(output), '--mode', mode,
                       '--threads', str(args.threads), '--preset', args.preset, '--repeat', str(args.repeat)]
            print(f'START {output.name}', flush=True)
            subprocess.run(command, check=True)
print('SWEEP_COMPLETE', flush=True)
