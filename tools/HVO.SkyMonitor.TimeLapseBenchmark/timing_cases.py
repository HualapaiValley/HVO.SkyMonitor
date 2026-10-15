#!/usr/bin/env python3
"""Controlled timestamp cases using real VirtualSky pixels, explicitly retimed for this test."""
import bisect
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import subprocess

root = Path('/data/results/timing-cases')
root.mkdir(parents=True, exist_ok=False)
source = Path('/data/inputs/color-1280x1280-day')
facts = json.loads((source / 'sequence.json').read_text())
# 1,200 observations in 60 source seconds, 1/5 ms exposure, 50 ms capture cadence.
# The 24 genuine source images are reused; this is a timing case, not a new sky capture.
starts_us = list(range(0, 60_000_000, 50_000))
start = datetime(2026, 10, 12, 19, tzinfo=timezone.utc)
selections = {}
for fps in [30, 60]:
    indices = [bisect.bisect_right(starts_us, tick * 60_000_000 // fps) - 1 for tick in range(fps)]
    assert indices == list(range(0, 1200, 1200 // fps))
    selections[str(fps)] = indices
    dataset = root / f'day-fast-grid{fps}'
    dataset.mkdir()
    rows = []
    for tick, selected in enumerate(indices):
        original = facts['rows'][selected % len(facts['rows'])]
        file = f'{tick:04}.jpg'
        (dataset / file).symlink_to(source / original['file'])
        rows.append({**original, 'index': tick, 'file': file,
                     'originalTemporalIndex': selected,
                     'sourceUtc': (start + timedelta(microseconds=starts_us[selected])).isoformat(),
                     'sourceOffsetSeconds': tick * 60 / fps,
                     'sourceDurationSeconds': 60 / fps,
                     'exposureSeconds': .001 if (selected // 20) % 2 == 0 else .005})
    (dataset / 'sequence.json').write_text(json.dumps({**facts, 'start': start.isoformat(),
        'end': (start + timedelta(seconds=60)).isoformat(), 'sourceSpanSeconds': 60,
        'playbackDurationSeconds': 1, 'count': fps, 'rows': rows,
        'fixture': 'Retimed copies of actual VirtualSky pixels; 1,200 timestamps selected on a fixed time grid.',
        'selectionRule': 'Latest capture at or before each output tick; no future frame and no exposure weighting.'}, indent=2))
    subprocess.run(['python3', '/data/encode.py', str(dataset), str(root / f'day-fast-{fps}.mp4'),
                    '--mode', f'cfr{fps}'], check=True)

# An exposure change must not change grid selection. A genuine missing span is kept
# as an explicit missing entry instead of silently extending the last image to fill it.
observations = [(0, 5), (5, 10), (20, 25)]
mapping = [next((index for index, (first, last) in enumerate(observations) if first <= tick < last), None)
           for tick in range(25)]
assert mapping[10:20] == [None] * 10 and mapping[20:] == [2] * 5
(root / 'cases.json').write_text(json.dumps({
    'fastCaptureCount': 1200, 'sourceSpanSeconds': 60, 'exposuresSeconds': [.001, .005],
    'captureCadenceSeconds': .05, 'selectedSourceIndices': selections,
    'missingIntervalSourceSeconds': [10, 20], 'gapMapping': mapping,
    'gapDisposition': 'Selection only. User-visible gap rendering policy remains for requirements discussion.',
    'verified': True
}, indent=2))
