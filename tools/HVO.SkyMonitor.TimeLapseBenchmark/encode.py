#!/usr/bin/env python3
"""Bounded investigation harness. Every measured encode retains its exact command and media timing."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time


def digest(path):
    return hashlib.file_digest(open(path, 'rb'), 'sha256').hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n')


def timing_plan(dataset, output, repeat):
    facts = json.loads((dataset / 'sequence.json').read_text())
    lines = ['ffconcat version 1.0']
    mapping = []
    for cycle in range(repeat):
        for row in facts['rows']:
            image = (dataset / row['file']).resolve()
            if digest(image).upper() != row['jpegSha256']:
                raise ValueError(f'Input checksum mismatch: {image}')
            start = (cycle * facts['sourceSpanSeconds'] + row['sourceOffsetSeconds']) / 60
            duration = row['sourceDurationSeconds'] / 60
            mapping.append({'file': str(image), 'sourceIndex': row['index'], 'cycle': cycle,
                            'pts': start, 'duration': duration, 'exposureSeconds': row['exposureSeconds']})
    duration = facts['playbackDurationSeconds'] * repeat
    # Supply the final nominal 1/60-second packet explicitly. Without it FFmpeg's image input
    # ends the last VFR sample early even though concat's duration directive looks correct.
    last = mapping[-1]
    if last['duration'] > 1 / 60 + 1e-8:
        last['duration'] -= 1 / 60
        mapping.append({**last, 'pts': duration - 1 / 60, 'duration': 1 / 60, 'terminalHold': True})
    for row in mapping:
        if "'" in row['file'] or '\n' in row['file']:
            raise ValueError('This bounded harness requires ordinary paths without quotes/newlines.')
        lines.extend([f"file '{row['file']}'", 'option framerate 60', f"duration {row['duration']:.9f}"])
    plan = output.with_suffix('.ffconcat')
    plan.write_text('\n'.join(lines) + '\n')
    write_json(output.with_suffix('.mapping.json'), {'facts': facts, 'repeat': repeat, 'mapping': mapping,
                                                    'note': 'Repeated cycles are explicit fixture replay, not new observations.'})
    return facts, mapping, duration, plan


def run(args):
    dataset, output = Path(args.dataset).resolve(), Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        raise ValueError('Output already exists; use a new trial name.')
    facts, mapping, duration, plan = timing_plan(dataset, output, args.repeat)
    width, height = facts['width'], facts['height']
    filters = []
    if getattr(args, 'output_size', None):
        width, height = args.output_size
        if (width < 2 or height < 2 or width % 2 or height % 2 or
                width > facts['width'] or height > facts['height'] or
                width * facts['height'] != height * facts['width']):
            raise ValueError('Output size must be even, downsampled and preserve the exact source aspect ratio.')
        filters.append(f'scale={width}:{height}:flags=lanczos')
    if args.mode == 'legacy60':
        # Same one-sample-per-source-second timeline as HVOv6. Reuse file references
        # instead of physically copying JPEGs, so this is a conservative cost baseline.
        lines = ['ffconcat version 1.0']
        selected = 0
        for tick in range(round(duration * 60)):
            while selected + 1 < len(mapping) and mapping[selected + 1]['pts'] <= tick / 60 + 1e-8:
                selected += 1
            lines.append(f"file '{mapping[selected]['file']}'")
        plan.write_text('\n'.join(lines) + '\n')
    command = ['ffmpeg', '-hide_banner', '-loglevel', 'warning', '-nostdin', '-n',
               '-threads', str(args.threads), '-filter_threads', '1', '-filter_complex_threads', '1',
               *(['-r', '60'] if args.mode == 'legacy60' else []),
               '-f', 'concat', '-safe', '0', '-i', str(plan), '-map', '0:v:0', '-an',
               '-c:v', 'libx264', '-preset', args.preset, '-crf', '23', '-pix_fmt', 'yuv420p',
               '-threads', str(args.threads), '-x264-params', f'lookahead_threads=1:threads={args.threads}',
               '-movflags', '+faststart', '-video_track_timescale', '60000']
    if args.mode == 'vfr':
        command += ['-fps_mode', 'vfr', '-enc_time_base', '1:60', '-bf', '0']
    else:
        fps = 60 if args.mode == 'legacy60' else int(args.mode.removeprefix('cfr'))
        filters.append(f'fps=fps={fps}:round=near')
        command += ['-fps_mode', 'cfr', '-r', str(fps)]
    if filters:
        command += ['-vf', ','.join(filters)]
    command += ['-t', str(duration), str(output)]
    resource_file = output.with_suffix('.resources.json')
    if args.cold_sources:
        # Only private immutable benchmark inputs. Never flush the host's global cache.
        for file in sorted({row['file'] for row in mapping}):
            with open(file, 'rb') as source:
                os.fsync(source.fileno())
                os.posix_fadvise(source.fileno(), 0, 0, os.POSIX_FADV_DONTNEED)
    measured = ['/usr/bin/time', '-f', '{"wallSeconds":%e,"userSeconds":%U,"systemSeconds":%S,'
                '"maxRssKiB":%M,"inputBlocks":%I,"outputBlocks":%O,"voluntarySwitches":%w,'
                '"involuntarySwitches":%c,"exitCode":%x}', '-o', str(resource_file),
                'nice', '-n', '19', 'ionice', '-c', '3'] + command
    started = time.monotonic()
    with output.with_suffix('.log').open('w') as log:
        result = subprocess.run(measured, stdout=log, stderr=log, timeout=args.timeout)
    elapsed = time.monotonic() - started
    report = {'dataset': dataset.name, 'mode': args.mode, 'preset': args.preset, 'threads': args.threads,
              'repeat': args.repeat, 'plannedDuration': duration, 'sourceSeconds': duration * 60,
              'command': command, 'exitCode': result.returncode, 'elapsedSeconds': elapsed,
              'resources': json.loads(resource_file.read_text()), 'verified': False,
              'width': width, 'height': height, 'sourceWidth': facts['width'],
              'sourceHeight': facts['height'], 'output': str(output)}
    report['cachePolicy'] = 'task-source-only fsync + DONTNEED after checksum validation' if args.cold_sources else 'warm filesystem cache allowed'
    if result.returncode != 0:
        write_json(output.with_suffix('.result.json'), report)
        raise RuntimeError(f'Encode failed: {output}')
    report.update({'bytes': output.stat().st_size, 'sha256': digest(output)})
    if not args.skip_verify:
        probe_command = ['ffprobe', '-v', 'error', '-threads', '1', '-show_streams', '-show_format',
                         '-show_frames', '-show_entries',
                         'frame=best_effort_timestamp_time,pkt_duration_time:stream=codec_name,width,height,nb_frames,time_base,duration:format=duration',
                         '-of', 'json', str(output)]
        probe = json.loads(subprocess.check_output(probe_command, timeout=args.timeout))
        write_json(output.with_suffix('.probe.json'), probe)
        stream = probe['streams'][0]
        pts = [float(frame['best_effort_timestamp_time']) for frame in probe['frames']]
        actual_duration = float(probe['format']['duration'])
        if stream['codec_name'] != 'h264' or (stream['width'], stream['height']) != (width, height):
            raise RuntimeError('Wrong media format or dimensions.')
        if abs(actual_duration - duration) > .0011:
            raise RuntimeError(f'Duration mismatch: actual {actual_duration}, planned {duration}')
        if args.mode == 'vfr':
            expected = [row['pts'] for row in mapping]
        else:
            fps = 60 if args.mode == 'legacy60' else int(args.mode.removeprefix('cfr'))
            expected = [index / fps for index in range(round(duration * fps))]
        if len(pts) != len(expected) or any(abs(a - b) > .00002 for a, b in zip(pts, expected)):
            raise RuntimeError(f'PTS/count mismatch: {len(pts)} actual, {len(expected)} expected')
        subprocess.run(['ffmpeg', '-v', 'error', '-xerror', '-nostdin', '-threads', '1', '-i', str(output),
                        '-f', 'null', '-'], check=True, timeout=args.timeout, stdout=subprocess.DEVNULL,
                       stderr=subprocess.PIPE)
        report.update({'verified': True, 'decodedFrames': len(pts), 'actualDuration': actual_duration,
                       'lastFramePts': pts[-1], 'verification': 'exact PTS/count, codec/dimensions, complete hard-fail decode'})
    write_json(output.with_suffix('.result.json'), report)
    print(json.dumps({key: report[key] for key in ['dataset', 'mode', 'preset', 'threads', 'elapsedSeconds',
                                                 'resources', 'bytes', 'verified']}), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('dataset')
    parser.add_argument('output')
    parser.add_argument('--mode', choices=['vfr', 'cfr30', 'cfr60', 'legacy60'], required=True)
    parser.add_argument('--preset', choices=['fast', 'veryfast', 'ultrafast'], default='fast')
    parser.add_argument('--threads', type=int, choices=[1, 2, 4], default=1)
    parser.add_argument('--repeat', type=int, default=1)
    parser.add_argument('--timeout', type=int, default=600)
    parser.add_argument('--skip-verify', action='store_true')
    parser.add_argument('--cold-sources', action='store_true')
    parser.add_argument('--output-size', type=int, nargs=2, metavar=('WIDTH', 'HEIGHT'))
    run(parser.parse_args())
