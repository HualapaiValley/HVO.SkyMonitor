#!/usr/bin/env python3
"""Create independently timed downloads without changing encoded image packets."""
import argparse
from fractions import Fraction
import hashlib
import json
from pathlib import Path
import subprocess


def probe(path):
    return json.loads(subprocess.check_output([
        'ffprobe', '-v', 'error', '-show_packets', '-show_streams', '-show_format',
        '-show_data_hash', 'sha256', '-show_entries',
        'packet=pts,dts,duration,data_hash:stream=codec_name,width,height,time_base,nb_frames:format=duration',
        '-of', 'json', str(path)], timeout=120))


def retime(source, output, factor):
    if output.exists():
        raise ValueError('Use a fresh output filename.')
    output.parent.mkdir(parents=True, exist_ok=True)
    before = probe(source)
    assert len(before['streams']) == 1, 'Video-only input required.'
    # This timebase exactly represents every original 1/60000-second tick at 2x, 3x and 5x.
    bsf = (f'setts=pts=PTS*TB/TB_OUT/{factor}:dts=DTS*TB/TB_OUT/{factor}:'
           f'duration=DURATION*TB/TB_OUT/{factor}:time_base=1/1800000')
    command = ['ffmpeg', '-hide_banner', '-loglevel', 'warning', '-nostdin', '-n',
               '-i', str(source), '-map', '0:v:0', '-an', '-c:v', 'copy', '-bsf:v', bsf,
               '-map_metadata', '-1', '-movflags', '+faststart',
               '-video_track_timescale', '1800000', str(output)]
    subprocess.run(command, check=True, timeout=120)
    after = probe(output)
    old_stream, new_stream = before['streams'][0], after['streams'][0]
    for field in ['codec_name', 'width', 'height', 'nb_frames']:
        assert old_stream[field] == new_stream[field], field
    old_tb, new_tb = Fraction(old_stream['time_base']), Fraction(new_stream['time_base'])
    assert len(before['packets']) == len(after['packets'])
    maximum_error = Fraction(0)
    for old, new in zip(before['packets'], after['packets']):
        assert old['data_hash'] == new['data_hash'], 'Encoded pixels changed.'
        for field in ['pts', 'dts', 'duration']:
            error = abs(int(old[field]) * old_tb / factor - int(new[field]) * new_tb)
            maximum_error = max(maximum_error, error)
            assert error <= new_tb, (field, error)
    expected_duration = float(before['format']['duration']) / factor
    assert abs(float(after['format']['duration']) - expected_duration) < .0011
    subprocess.run(['ffmpeg', '-v', 'error', '-xerror', '-nostdin', '-threads', '1',
                    '-i', str(output), '-map', '0:v:0', '-fps_mode', 'passthrough',
                    '-f', 'null', '-'], check=True, timeout=300, stdout=subprocess.DEVNULL)
    def digest(path):
        with path.open('rb') as stream:
            return hashlib.file_digest(stream, 'sha256').hexdigest()
    result = {'source': str(source), 'sourceSha256': digest(source), 'output': str(output),
              'outputSha256': digest(output), 'factor': factor, 'sourceCompression': 60,
              'compression': 60 * factor, 'duration': float(after['format']['duration']),
              'bytes': output.stat().st_size, 'packets': len(after['packets']),
              'maximumTimingErrorSeconds': float(maximum_error), 'command': command,
              'verified': True, 'verification': 'Every compressed packet hash unchanged; PTS, DTS and duration divided by factor; complete hard-fail decode.'}
    output.with_suffix('.retime.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--factor', type=int, choices=[2, 3, 5], required=True)
    args = parser.parse_args()
    retime(args.source, args.output, args.factor)
