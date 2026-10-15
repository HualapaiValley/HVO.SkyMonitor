#!/usr/bin/env python3
"""Require moving star centroids in source pixels, not just differing noise/checksums."""
import json
import math
from pathlib import Path
import statistics
import sys

root = Path(sys.argv[1])
reports = []
for path in sorted(root.glob('*/sequence.json')):
    facts = json.loads(path.read_text())
    rows = facts['rows']
    assert len(rows) == 180
    assert len({r['jpegSha256'] for r in rows}) == 180
    assert [r['sourceOffsetSeconds'] for r in rows] == list(range(0, 3600, 20))
    if facts['phase'] != 'night':
        reports.append({'dataset': path.parent.name, 'uniqueImages': 180,
                        'exposureRangeSeconds': [min(r['exposureSeconds'] for r in rows), max(r['exposureSeconds'] for r in rows)],
                        'note': 'Daylight source; brightness verification is separate from stellar motion.'})
        continue
    first = {s['id']: s for s in rows[0]['starTracks']}
    last = {s['id']: s for s in rows[-1]['starTracks']}
    tracks = []
    for identifier in first.keys() & last.keys():
        a, b = first[identifier], last[identifier]
        if min(a['measured']['contrast'], b['measured']['contrast']) < 20:
            continue
        if a['measured']['x'] is None or b['measured']['x'] is None:
            continue
        measured = math.hypot(a['measured']['x']-b['measured']['x'], a['measured']['y']-b['measured']['y'])
        expected = math.hypot(a['expectedX']-b['expectedX'], a['expectedY']-b['expectedY'])
        error = max(math.hypot(s['measured']['x']-s['expectedX'], s['measured']['y']-s['expectedY']) for s in [a,b])
        if error > 2.5:
            continue
        tracks.append({'id': identifier, 'name': a['displayName'], 'measuredDisplacementPixels': measured,
                       'expectedDisplacementPixels': expected, 'maximumCentroidErrorPixels': error,
                       'first': a, 'last': b})
    assert len(tracks) >= 5, f'Insufficient source-pixel motion evidence: {path}'
    assert statistics.median(t['measuredDisplacementPixels'] for t in tracks) > facts['width'] / 100
    reports.append({'dataset': path.parent.name, 'uniqueImages': 180, 'tracksVerified': len(tracks),
                    'medianDisplacementPixels': statistics.median(t['measuredDisplacementPixels'] for t in tracks),
                    'maximumCentroidErrorPixels': max(t['maximumCentroidErrorPixels'] for t in tracks), 'tracks': tracks})
output = root / 'motion-verification.json'
output.write_text(json.dumps(reports, indent=2))
for report in reports:
    print(json.dumps({k:v for k,v in report.items() if k != 'tracks'}))
