#!/usr/bin/env python3
"""Recompute #1540's four diagnostic trials from retained decoded traces and smaps.

This reports timestamp-matched GC mappings and an unassigned anonymous upper bound,
not an exact split of the unsampled process peak or of individual native allocators.
The pack must contain attribute-before and derived-before. Raw nettrace files and
all decoder source remain in the evidence pack; this script consumes their exports.
"""
import argparse
import collections
import csv
import datetime as dt
import json
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("pack", type=Path)
parser.add_argument("--output", required=True, type=Path, help="new JSON output file; existing evidence is never overwritten")
args = parser.parse_args()
root = args.pack.resolve(strict=True)
result = []

def epoch(s):
    return dt.datetime.fromisoformat(s.replace('Z', '+00:00')).timestamp()

def number(s):
    return int(s.replace(',', ''), 16 if s.startswith('0x') else 10)

for raw in sorted((root / 'attribute-before/runs').iterdir()):
    derived = root / 'derived-before' / raw.name
    summary = json.loads((derived / 'gc.nettrace.summary.json').read_text())
    assert summary['eventsLost'] == 0
    assert summary['allocationTicks'] == summary['allocationTicksWithStack'] > 0
    counter_values = collections.defaultdict(list)
    for row in csv.DictReader((raw / 'counters.csv').open()):
        counter_values[row['Counter Name']].append(float(row['Mean/Increment']))
    counters = {name: {'minimum': min(v), 'maximum': max(v), 'last': v[-1]}
                for name, v in counter_values.items() if 'gc.' in name or 'memory.working_set' in name}
    proc = []
    for line in (raw / 'proc-samples.txt').read_text().splitlines():
        fields = dict((k, int(v) * 1024) for k,v in re.findall(r'([A-Za-z_]+):=(\d+)', line))
        if 'VmRSS' in fields:
            proc.append({'time': float(line.split()[0]), **fields})
    segment_events = []
    heap_stats = []
    gen_history = collections.defaultdict(list)
    for line in (derived / 'gc.nettrace.events.jsonl').open():
        event = json.loads(line)
        name = event['EventName']
        if name in ('GC/CreateSegment', 'GC/FreeSegment', 'GC/HeapStats'):
            x = ET.fromstring(event['xml'])
            a = x.attrib
            t = epoch(event['utc'])
            if name == 'GC/CreateSegment':
                segment_events.append((t, 'add', number(a['Address']), number(a['Size'])))
            elif name == 'GC/FreeSegment':
                segment_events.append((t, 'remove', number(a['Address']), 0))
            else:
                heap_stats.append({'time': t, 'depth': number(a['Depth']),
                                   'generations': [number(a[f'GenerationSize{n}']) for n in range(5)]})
        if name == 'GC/PerHeapHistory':
            # TraceEvent renders a self-closing header before its child GenData entries. Read the full payload,
            # including all generation values; the original nettrace remains the authority.
            x = ET.fromstring('<root>' + event['xml'].replace('/> <GenData', '> <GenData', 1) + '</root>')
            for gen in x.findall('.//GenData'):
                a = gen.attrib
                gen_history[a['Name']].append({k:number(a[k]) for k in
                    ('SizeBefore','SizeAfter','ObjSpaceBefore','ObjSizeAfter','Fragmentation','NewAllocation')})
    assert gen_history['GenLargeObj'] and gen_history['Gen2']
    snapshots = []
    for path in sorted((raw / 'smaps').glob('*.smaps*')):
        if path.name.startswith('final'):
            # final.smaps has no filename timestamp, so its exact segment-lifetime classification is omitted.
            continue
        timestamp = float(path.name.split('.smaps')[0])
        text = subprocess.check_output(['zstd','-q','-dc',str(path)],text=True) if path.suffix == '.zst' else path.read_text()
        segments = {}
        for t, action, address, size in segment_events:
            if t > timestamp: break
            if action == 'add': segments[address] = size
            else: segments.pop(address, None)
        ranges = sorted((a & ~4095, (a + s + 4095) & ~4095) for a,s in segments.items())
        maps = []
        for line in text.splitlines():
            m = re.match(r'^([0-9a-f]+)-([0-9a-f]+)\s+(\S+)\s+\S+\s+\S+\s+\S+\s*(.*)$', line)
            if m:
                maps.append({'start':int(m[1],16),'end':int(m[2],16),'name':m[4].strip(),'rss':0})
            elif line.startswith('Rss:'):
                maps[-1]['rss'] = int(line.split()[1])*1024
        sums = collections.Counter()
        for mapping in maps:
            a,b = mapping['start'],mapping['end']
            if mapping['name'] and not mapping['name'].startswith('['):
                category = 'fileOrMemfdRss'
            else:
                overlap = [(max(a,c),min(b,d)) for c,d in ranges if c < b and d > a]
                cursor=a
                for c,d in overlap:
                    if c>cursor: break
                    cursor=max(cursor,d)
                category = 'gcSegmentRss' if cursor>=b else ('partialGcSegmentRss' if overlap else 'otherAnonymousRssUpperBound')
            sums[category] += mapping['rss']
        snapshots.append({'time':timestamp,**sums})
    heap_text = (derived / 'retained-heap.txt').read_text()
    retained = int(re.search(r'([\d,]+)\s+GC Heap bytes',heap_text)[1].replace(',',''))
    result.append({'name':raw.name,'counterMetrics':counters,'kernelSamplePeak':max(proc,key=lambda r:r['VmRSS']),
                   'kernelSampleLast':proc[-1], 'retainedHeapBytes':retained,
                   'gcHistory':{name:{key:{'minimum':min(v[key] for v in rows),'maximum':max(v[key] for v in rows)}
                                     for key in rows[0]} for name,rows in gen_history.items()},
                   'lastNaturalHeapStats':heap_stats[-1], 'segmentSnapshots':snapshots,
                   'traceEventsLost':summary['eventsLost'],'gcHistoryEvents':len(gen_history['Gen2']),
                   'allocationTicks':summary['allocationTicks'],'allocationTicksWithStacks':summary['allocationTicksWithStack']})
with args.output.open("x") as output:
    output.write(json.dumps(result, indent=2) + "\n")
for row in result:
    print(row['name'],'retainedMiB',round(row['retainedHeapBytes']/1048576,3),
          'committedMaxMiB',round(row['counterMetrics']['dotnet.gc.last_collection.memory.committed_size (By)']['maximum']/1048576,2))
    for field in ('gcSegmentRss','partialGcSegmentRss','otherAnonymousRssUpperBound','fileOrMemfdRss'):
        print(field,round(max((s.get(field,0) for s in row['segmentSnapshots']), default=0)/1048576,2))
