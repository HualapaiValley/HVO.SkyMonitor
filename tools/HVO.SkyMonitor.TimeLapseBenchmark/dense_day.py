#!/usr/bin/env python3
"""Encoding stress case: replay actual millisecond-exposure previews at one new source per second."""
from datetime import datetime, timedelta
import json
from pathlib import Path
import subprocess
import sys

root = Path(sys.argv[1])
root.mkdir(parents=True, exist_ok=False)
source = Path('/data/inputs/color-1280x1280-day')
facts = json.loads((source / 'sequence.json').read_text())
start = datetime.fromisoformat(facts['start'])
dataset = root / 'dense-day'
dataset.mkdir()
rows = []
for index in range(60):
    original = facts['rows'][index % len(facts['rows'])]
    name = f'{index:04}.jpg'
    (dataset / name).symlink_to(source / original['file'])
    rows.append({**original, 'index': index, 'file': name,
                 'originalSourceUtc': original['sourceUtc'],
                 'sourceUtc': (start + timedelta(seconds=index)).isoformat(),
                 'sourceOffsetSeconds': index, 'sourceDurationSeconds': 1})
(dataset / 'sequence.json').write_text(json.dumps({**facts, 'rows': rows, 'count': len(rows),
    'end': (start + timedelta(seconds=60)).isoformat(), 'sourceSpanSeconds': 60,
    'playbackDurationSeconds': 1,
    'fixture': '24 actual VirtualSky daytime JPEGs replayed as 60 changing observations at 1 Hz. Explicitly retimed encoding load, not a newly captured minute.'}, indent=2))
for mode in ['vfr', 'cfr30', 'cfr60']:
    subprocess.run(['python3', '/data/encode.py', str(dataset), str(root / (mode + '.mp4')),
                    '--mode', mode], check=True)
