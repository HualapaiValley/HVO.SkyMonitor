#!/usr/bin/env python3
"""Verify encoded stellar displacement or source/encoded daytime brightness."""
import argparse
import json
import math
from pathlib import Path
import statistics
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('dataset')
parser.add_argument('video')
parser.add_argument('--brightness', action='store_true', help='Compare full-range RGB brightness even for a night sequence.')
args = parser.parse_args()
dataset, video = Path(args.dataset), Path(args.video)
facts = json.loads((dataset / 'sequence.json').read_text())
common = ['ffmpeg', '-v', 'error', '-nostdin', '-threads', '1', '-filter_threads', '1']
if facts['phase'] == 'day' or args.brightness:
    size = 64 * 64 * 3
    measurements = []
    for inputs in [['-f', 'concat', '-safe', '0', '-i', str(video.with_suffix('.ffconcat'))], ['-i', str(video)]]:
        raw = subprocess.check_output(common + inputs + ['-vf', 'scale=64:64:flags=area,format=rgb24',
            '-fps_mode', 'passthrough', '-f', 'rawvideo', '-'], timeout=180)
        assert len(raw) % size == 0
        means = []
        for offset in range(0, len(raw), size):
            values = [(.2126 * raw[offset + (y*64+x)*3] + .7152 * raw[offset + (y*64+x)*3+1] +
                       .0722 * raw[offset + (y*64+x)*3+2]) for y in range(64) for x in range(64)
                      if (x-31.5)**2 + (y-31.5)**2 < 27**2]
            means.append(statistics.mean(values))
        measurements.append({'means': means, 'minimum': min(means), 'maximum': max(means),
            'mean': statistics.mean(means), 'maximumAdjacentChange': max(abs(b-a) for a,b in zip(means, means[1:]))})
    report = {'dataset': dataset.name, 'video': str(video), 'method': 'Area-averaged full-range decoded RGB luminance inside the image circle; same conversion for JPEG and H264.',
              'source': measurements[0], 'encoded': measurements[1]}
else:
    width, height = facts['width'], facts['height']
    frame_size = width * height * 3
    last_index = len(facts['rows']) - 1
    raw = subprocess.check_output(common + ['-i', str(video), '-vf', f'select=eq(n\\,0)+eq(n\\,{last_index})',
        '-fps_mode', 'passthrough', '-frames:v', '2', '-pix_fmt', 'rgb24', '-f', 'rawvideo', '-'], timeout=180)
    assert len(raw) == frame_size * 2
    points = []
    for ordinal, row in enumerate([facts['rows'][0], facts['rows'][-1]]):
        stars = {}
        for star in row['starTracks']:
            cx, cy = int(star['expectedX']), int(star['expectedY'])
            def value(x,y):
                offset = ordinal * frame_size + (y*width+x)*3
                return sum(raw[offset:offset+3]) / 3
            background = statistics.median(value(x,y) for y in range(cy-7,cy+8) for x in range(cx-7,cx+8)
                                           if abs(x-cx)==7 or abs(y-cy)==7)
            weights = [(x+.5,y+.5,max(0,value(x,y)-background-8)) for y in range(cy-5,cy+6) for x in range(cx-5,cx+6)]
            total = sum(w for x,y,w in weights)
            if total:
                x, y = sum(x*w for x,y,w in weights)/total, sum(y*w for x,y,w in weights)/total
                error = math.hypot(x-star['expectedX'],y-star['expectedY'])
                if error < 2.5: stars[star['id']] = {'x':x,'y':y,'error':error,'name':star['displayName']}
        points.append(stars)
    tracks = [{'id':i,'name':points[0][i]['name'],
               'displacementPixels':math.hypot(points[1][i]['x']-points[0][i]['x'],points[1][i]['y']-points[0][i]['y']),
               'maximumErrorPixels':max(points[0][i]['error'],points[1][i]['error'])}
              for i in sorted(points[0].keys() & points[1].keys())]
    assert len(tracks) >= 5
    assert statistics.median(t['displacementPixels'] for t in tracks) > width / 100
    report = {'dataset':dataset.name,'video':str(video),'tracks':tracks,'trackedStars':len(tracks),
              'medianDisplacementPixels':statistics.median(t['displacementPixels'] for t in tracks),
              'maximumCentroidErrorPixels':max(t['maximumErrorPixels'] for t in tracks)}
video.with_suffix('.pixels.json').write_text(json.dumps(report,indent=2))
print(json.dumps({k:v for k,v in report.items() if k not in ['tracks','source','encoded']}))
if 'source' in report:
    for key in ['source','encoded']: print(key,json.dumps({k:v for k,v in report[key].items() if k!='means'}))
