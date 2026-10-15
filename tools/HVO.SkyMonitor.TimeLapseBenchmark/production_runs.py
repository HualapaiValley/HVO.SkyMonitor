#!/usr/bin/env python3
"""Serial production-path trials; samples aggregate process-tree RSS and records child CPU/I/O.

Run inside the isolated Pi/local container, or directly on the already-qualified
NVIDIA host. Inputs are freshly advancing raw VirtualSky captures. Repeated
contention jobs are load only, never presented as a longer observing period.
"""
import argparse
import json
import os
from pathlib import Path
import resource
import signal
import subprocess
import time


def sample_group(group, peaks, io):
    rss = 0
    for entry in Path('/proc').iterdir():
        if not entry.name.isdigit():
            continue
        pid = int(entry.name)
        try:
            if os.getpgid(pid) != group:
                continue
            rss += int((entry / 'statm').read_text().split()[1]) * os.sysconf('SC_PAGE_SIZE')
            values = dict(line.split(':', 1) for line in (entry / 'io').read_text().splitlines())
            io[pid] = {key: int(values[key]) for key in ['rchar', 'wchar', 'read_bytes', 'write_bytes']}
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            continue
    peaks.append(rss)


def run(root, binary, output, name, arguments):
    report = output / name
    if report.exists():
        raise RuntimeError(f'Evidence already exists: {report}')
    log = output / (name + '.log')
    environment = dict(os.environ, DOTNET_PROCESSOR_COUNT='2')
    before = resource.getrusage(resource.RUSAGE_CHILDREN)
    started = time.monotonic()
    peaks, io = [], {}
    print(f'START {name}', flush=True)
    with log.open('w') as stream:
        process = subprocess.Popen([str(binary), *arguments], stdout=stream, stderr=subprocess.STDOUT,
                                   env=environment, start_new_session=True)
        try:
            while process.poll() is None:
                sample_group(process.pid, peaks, io)
                if time.monotonic() - started > 1800:
                    raise TimeoutError(name)
                time.sleep(.2)
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
            process.wait()
    after = resource.getrusage(resource.RUSAGE_CHILDREN)
    resources = {
        'name': name, 'command': [str(binary), *arguments], 'exitCode': process.returncode,
        'wallSecondsIncludingQualification': time.monotonic() - started,
        'userCpuSeconds': after.ru_utime - before.ru_utime,
        'systemCpuSeconds': after.ru_stime - before.ru_stime,
        'aggregatePeakRssBytes': max(peaks, default=0), 'sampleIntervalSeconds': .2,
        'sampledIoBytes': {key: sum(item[key] for item in io.values()) for key in ['rchar', 'wchar', 'read_bytes', 'write_bytes']},
        'ioCaveat': 'Last sampled per-process counters; very short-lived child I/O may finish between samples. rchar/wchar include cache traffic.',
        'inputRoot': str(root), 'cpuMax': Path('/sys/fs/cgroup/cpu.max').read_text().strip() if Path('/sys/fs/cgroup/cpu.max').exists() else None
    }
    (output / (name + '.resources.json')).write_text(json.dumps(resources, indent=2))
    if process.returncode:
        raise RuntimeError(f'{name} failed; see {log}')
    print(f'COMPLETE {name}: {resources["wallSecondsIncludingQualification"]:.3f}s, aggregate RSS {resources["aggregatePeakRssBytes"]}', flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('root', type=Path)
    parser.add_argument('binary', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--mode', choices=['edge', 'native', 'gpu', 'daytime', 'contention', 'contention-video', 'correction', 'day-contention'], required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    if args.mode in ['contention', 'contention-video', 'day-contention']:
        for video in ([False, True] if args.mode in ['contention', 'day-contention'] else [True]):
            name = 'foreground-video' if video else 'foreground-baseline'
            run(args.root, args.binary, args.output, name,
                ['production-contend', str(args.root / ('day3552' if args.mode == 'day-contention' else 'color3552')), str(args.output / name), str(video).lower()])
        return
    scenarios = [(case, 1280, 'Software', 180, 3) for case in ['mono3552', 'color3552', 'day3552', 'asi174-v2']]
    if args.mode == 'native':
        scenarios += [('mono3552', 3552, 'Software', 180, 3), ('color3552', 3552, 'Software', 180, 3)]
    if args.mode == 'gpu':
        scenarios = [('color3552', 3552, profile, 180, 3) for profile in ['Software', 'Nvidia']]
    if args.mode == 'daytime':
        scenarios = [('day3552', 1280, 'Software', 180, 3)]
    if args.mode == 'correction':
        scenarios = [(case, 1280, 'Software', 180, 3) for case in ['day3552', 'asi174-v2']]
    for case, dimension, profile, compression, stack in scenarios:
        name = f'{case}-{dimension}-{profile}-{compression}x-stack{stack}'
        run(args.root, args.binary, args.output, name, ['production', str(args.root / case), str(args.output / name),
            str(dimension), profile, str(compression), str(stack)])


if __name__ == '__main__':
    main()
