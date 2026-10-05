#!/usr/bin/env python3
"""Measure stream-copy assembly and verify every packet, boundary, and final decode."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument('source')
parser.add_argument('output')
parser.add_argument('--count', type=int, default=24)
parser.add_argument('--segments', nargs='+', help='Distinct compatible segments; overrides count.')
args = parser.parse_args()
source, output = Path(args.source).resolve(), Path(args.output).resolve()
output.parent.mkdir(parents=True, exist_ok=True)
if output.exists():
    raise ValueError('Use a fresh output.')
def probe_media(path):
    return json.loads(subprocess.check_output([
    'ffprobe', '-v', 'error', '-show_streams', '-show_packets', '-show_data_hash', 'sha256',
    '-show_entries', 'packet=pts_time,dts_time,duration_time,data_hash:stream=duration,nb_frames,codec_name,width,height',
    '-of', 'json', str(path)]))
sources = [Path(p).resolve() for p in args.segments] if args.segments else [source] * args.count
probes = [probe_media(p) for p in sources]
source_probe = probes[0]
duration = float(source_probe['streams'][0]['duration'])
plan = output.with_suffix('.ffconcat')
plan.write_text('ffconcat version 1.0\n' + ''.join(f"file '{p}'\n" for p in sources))
command = ['ffmpeg', '-v', 'warning', '-nostdin', '-n', '-f', 'concat', '-safe', '0', '-auto_convert', '0', '-i', str(plan),
           '-map', '0:v:0', '-c', 'copy', '-movflags', '+faststart', '-video_track_timescale', '60000', str(output)]
resource_file = output.with_suffix('.resources.json')
started = time.monotonic()
with output.with_suffix('.log').open('w') as log:
    subprocess.run(['/usr/bin/time', '-f', '{"wallSeconds":%e,"userSeconds":%U,"systemSeconds":%S,"maxRssKiB":%M,"inputBlocks":%I,"outputBlocks":%O}',
                    '-o', str(resource_file), 'nice', '-n', '19', 'ionice', '-c', '3', *command],
                   stdout=log, stderr=log, check=True, timeout=120)
elapsed = time.monotonic() - started
probe = json.loads(subprocess.check_output([
    'ffprobe', '-v', 'error', '-show_streams', '-show_packets', '-show_data_hash', 'sha256',
    '-show_entries', 'packet=pts_time,dts_time,duration_time,data_hash:stream=duration,nb_frames,codec_name,width,height',
    '-of', 'json', str(output)], timeout=120))
expected = []
offset = 0.0
for part in probes:
    for packet in part['packets']:
        expected.append({**packet, 'pts_time':float(packet['pts_time'])+offset,
                         'dts_time':float(packet['dts_time'])+offset})
    offset += float(part['streams'][0]['duration'])
assert len(probe['packets']) == len(expected)
assert abs(float(probe['streams'][0]['duration']) - offset) < .0011
for index, (actual, original) in enumerate(zip(probe['packets'], expected)):
    for clock in ['pts_time', 'dts_time']:
        assert abs(float(actual[clock]) - float(original[clock])) < .00002
    assert actual['data_hash'] == original['data_hash']
subprocess.run(['ffmpeg', '-v', 'error', '-xerror', '-nostdin', '-threads', '1', '-i', str(output),
                '-f', 'null', '-'], check=True, timeout=600, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
report = {'sources': [str(p) for p in sources], 'sourceDuration': duration,
          'repeatedFixtureSegments': 0 if args.segments else args.count,
          'note': 'Distinct segments from continuous observations.' if args.segments else 'Repeated qualified segment tests assembly cost and timestamp boundaries, not independent observing hours.',
          'command': command, 'elapsedSeconds': elapsed, 'resources': json.loads(resource_file.read_text()),
          'bytes': output.stat().st_size, 'sha256': hashlib.file_digest(output.open('rb'), 'sha256').hexdigest(),
          'duration': float(probe['streams'][0]['duration']), 'packetCount': len(expected),
          'verified': 'Every encoded packet payload preserved, PTS/DTS continuous, exact duration, complete hard-fail decode'}
output.with_suffix('.result.json').write_text(json.dumps(report, indent=2))
print(json.dumps(report), flush=True)
