#!/usr/bin/env python3
"""Matched native-host software/NVENC trial, with timing and reference-quality evidence."""
import argparse
import json
from pathlib import Path
import subprocess
import time

from encode import digest, timing_plan, write_json


def run(args):
    source, output = args.source.resolve(), args.output.resolve()
    if output.exists():
        raise ValueError('Use a new trial name.')
    output.parent.mkdir(parents=True, exist_ok=True)
    facts, mapping, duration, plan = timing_plan(source, output, 1)
    command = ['ffmpeg', '-hide_banner', '-loglevel', 'warning', '-nostdin', '-n',
               '-threads', '1', '-filter_threads', '1', '-filter_complex_threads', '1',
               '-f', 'concat', '-safe', '0', '-i', str(plan), '-map', '0:v:0', '-an']
    if args.encoder == 'libx264':
        command += ['-c:v', 'libx264', '-preset', 'veryfast', '-crf', '23',
                    '-threads', '1', '-x264-params', 'lookahead_threads=1:threads=1']
    else:
        command += ['-c:v', 'h264_nvenc', '-preset', args.preset, '-tune', 'hq', '-gpu', '0']
        command += (['-rc', 'vbr', '-cq', str(args.quality), '-b:v', '0'] if args.rate_control == 'cq'
                    else ['-rc', 'constqp', '-qp', str(args.quality)])
    command += ['-pix_fmt', 'yuv420p', '-bf', '0', '-g', '250', '-fps_mode', 'vfr',
                '-enc_time_base', '1:60', '-movflags', '+faststart', '-video_track_timescale', '60000',
                '-t', str(duration), str(output)]
    resource_file = output.with_suffix('.resources.json')
    measured = ['/usr/bin/time', '-f', '{"wallSeconds":%e,"userSeconds":%U,"systemSeconds":%S,'
                '"maxRssKiB":%M,"inputBlocks":%I,"outputBlocks":%O,"exitCode":%x}',
                '-o', str(resource_file), 'nice', '-n', '19', 'ionice', '-c', '3', *command]
    with output.with_suffix('.gpu.csv').open('w') as gpu, output.with_suffix('.log').open('w') as log:
        monitor = subprocess.Popen(['nvidia-smi', '--query-gpu=timestamp,name,utilization.gpu,utilization.encoder,utilization.decoder,memory.used,temperature.gpu',
                                    '--format=csv,noheader,nounits', '--loop-ms=200'], stdout=gpu, stderr=log)
        try:
            began = time.monotonic()
            result = subprocess.run(measured, stdout=log, stderr=log, timeout=600)
            elapsed = time.monotonic() - began
        finally:
            monitor.terminate()
            monitor.wait(timeout=10)
    report = {'encoder': args.encoder, 'preset': 'veryfast' if args.encoder == 'libx264' else args.preset,
              'source': str(source), 'sourceManifestSha256': digest(source/'sequence.json'), 'command': command,
              'elapsedSeconds': elapsed, 'resources': json.loads(resource_file.read_text()),
              'exitCode': result.returncode, 'verified': False, 'quality': 23 if args.encoder == 'libx264' else args.quality,
              'rateControl': 'crf' if args.encoder == 'libx264' else args.rate_control,
              'scope': 'Software JPEG decode; matched VFR H264 encode with x264 CRF or NVENC VBR-CQ/constant-QP. These quality settings are not equivalent. Verification and quality assessment excluded from encode timing.'}
    if result.returncode:
        write_json(output.with_suffix('.result.json'), report)
        raise RuntimeError('Encoder failed; see retained log.')
    probe = json.loads(subprocess.check_output(['ffprobe', '-v', 'error', '-threads', '1', '-show_frames',
        '-show_streams', '-show_format', '-show_entries',
        'frame=best_effort_timestamp_time,pkt_duration_time:stream=codec_name,width,height,nb_frames,duration:format=duration',
        '-of', 'json', str(output)], timeout=300))
    write_json(output.with_suffix('.probe.json'), probe)
    pts = [float(row['best_effort_timestamp_time']) for row in probe['frames']]
    stream = probe['streams'][0]
    assert stream['codec_name'] == 'h264'
    assert (stream['width'], stream['height']) == (facts['width'], facts['height'])
    assert len(pts) == len(mapping) and all(abs(a-b['pts']) <= .00002 for a,b in zip(pts,mapping))
    assert abs(float(probe['format']['duration']) - duration) <= .0011
    subprocess.run(['ffmpeg', '-v', 'error', '-xerror', '-nostdin', '-threads', '1', '-i', str(output),
                    '-fps_mode', 'passthrough', '-f', 'null', '-'], check=True, timeout=300)
    count = len(facts['rows'])
    filters = (f'[0:v]trim=end_frame={count},settb=1/1,setpts=N,format=yuv420p[e];'
               f'[1:v]trim=end_frame={count},settb=1/1,setpts=N,scale=in_range=full:out_range=tv,format=yuv420p,split[rp][rs];'
               f'[e][rp]psnr=stats_file={output.with_suffix(".psnr.txt")}[p];'
               f'[p][rs]ssim=stats_file={output.with_suffix(".ssim.txt")}[q]')
    quality_command = ['ffmpeg', '-hide_banner', '-nostdin', '-threads', '1', '-i', str(output),
        '-threads', '1', '-f', 'concat', '-safe', '0', '-i', str(plan), '-filter_complex_threads', '1',
        '-filter_complex', filters, '-map', '[q]', '-frames:v', str(count), '-f', 'null', '-']
    with output.with_suffix('.quality.log').open('w') as log:
        subprocess.run(quality_command, check=True, stdout=log, stderr=log, timeout=600)
    quality_lines = output.with_suffix('.quality.log').read_text().splitlines()
    quality = [line for line in quality_lines if 'PSNR y:' in line or 'SSIM Y:' in line]
    assert len(quality) == 2
    report.update({'verified': True, 'bytes': output.stat().st_size, 'sha256': digest(output),
                   'frames': len(pts), 'duration': float(probe['format']['duration']),
                   'qualityCommand': quality_command, 'qualitySummary': quality,
                   'qualityFrameCount': count, 'qualityBoundary': 'Uniform per-source-frame comparison, excluding terminal duplicate. JPEG full-range converted to limited-range YUV420; video unchanged. Global PSNR/SSIM do not replace visual star/overlay inspection.'})
    write_json(output.with_suffix('.result.json'), report)
    print(json.dumps(report), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--encoder', choices=['libx264', 'h264_nvenc'], required=True)
    parser.add_argument('--preset', choices=['p4', 'p6'], default='p4')
    parser.add_argument('--quality', type=int, choices=range(1, 52), default=23)
    parser.add_argument('--rate-control', choices=['cq', 'qp'], default='cq')
    run(parser.parse_args())
