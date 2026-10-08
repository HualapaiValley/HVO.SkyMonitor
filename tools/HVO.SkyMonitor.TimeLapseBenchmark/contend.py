#!/usr/bin/env python3
"""Host-side orchestration for baseline and constrained-encoder foreground trials."""
import argparse
import json
from pathlib import Path
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument('--mount', required=True)
parser.add_argument('--architecture', choices=['x64', 'arm64'], required=True)
parser.add_argument('--host', choices=['local', 'pi'], required=True)
parser.add_argument('--logs', required=True)
args = parser.parse_args()
logs = Path(args.logs)
logs.mkdir(parents=True, exist_ok=True)
image = 'hvo/timelapse-benchmark:1130-f6f3f27f'
active = set()


def docker(*arguments):
    return subprocess.check_output(['docker', *arguments], text=True, timeout=400).strip()


def start(name, command, cpus=None):
    options = ['run', '-d', '--name', name, '--network', 'none', '--pids-limit', '128',
               '-v', args.mount + ':/data']
    if cpus:
        options += ['--cpus', str(cpus), '--cpu-shares', '128']
    docker(*options, image, *command)
    active.add(name)
    (logs / (name + '.inspect.json')).write_text(docker('inspect', name))


def finish(name):
    exit_code = docker('wait', name)
    (logs / (name + '.log')).write_text(docker('logs', name))
    docker('rm', name)
    active.remove(name)
    if exit_code != '0':
        raise RuntimeError(f'{name} failed with code {exit_code}; see retained log')


try:
    for cpus in [0, 1, 2]:
        label = 'baseline' if cpus == 0 else f'encode-{cpus}cpu'
        folder = f'/data/results/{args.host}-foreground/{label}'
        foreground = f'hvo-1130-{args.host}-foreground-{label}'
        encoder = f'hvo-1130-{args.host}-load-{label}'
        docker('run', '--rm', '-v', args.mount + ':/data', image, 'mkdir', '-p', folder)
        print(f'START {label} {time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}', flush=True)
        if cpus:
            start(encoder, ['python3', '/data/load.py', folder, '--threads', str(cpus)], cpus)
            time.sleep(2)
        start(foreground, ['dotnet', f'/data/bin/linux-{args.architecture}/HVO.SkyMonitor.TimeLapseBenchmark.dll',
                          'process', '/data/inputs/color-3552x3552-night', folder + '/foreground.json', '30', '5'])
        finish(foreground)
        if cpus:
            docker('exec', encoder, 'touch', folder + '/stop')
            finish(encoder)
        print(f'COMPLETE {label}', flush=True)
finally:
    for name in list(active):
        subprocess.run(['docker', 'stop', '-t', '10', name], stdout=subprocess.DEVNULL, check=False)
        with (logs / (name + '.aborted.log')).open('w') as log:
            subprocess.run(['docker', 'logs', name], stdout=log, stderr=log, check=False)
        subprocess.run(['docker', 'rm', name], stdout=subprocess.DEVNULL, check=False)
