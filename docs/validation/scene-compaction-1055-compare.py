#!/usr/bin/env python3
"""Compare two identical #1055 producer results; never edits evidence or capture state."""
import argparse, json, statistics, sqlite3
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('baseline');p.add_argument('candidate');p.add_argument('--baseline-root',required=True);p.add_argument('--candidate-root',required=True);p.add_argument('--output',required=True);a=p.parse_args()
b=json.loads(Path(a.baseline).read_text());c=json.loads(Path(a.candidate).read_text())
assert b['mode']==c['mode']=='capture'
assert b['harnessSha256']==c['harnessSha256']
assert b['catalogSha256']==c['catalogSha256']
assert b['processorCount']==c['processorCount']
assert [s['rawSha256'] for s in b['samples']]==[s['rawSha256'] for s in c['samples']]
def frames(e):
    out={}
    for f in e['outputs']['frames']:
        key=(f['captureSequence'],f['role'],f['path'].split('/')[0])
        assert key not in out
        out[key]=f['sha256']
    return out
assert frames(b)==frames(c), 'raw/derivative pixel output changed'
def scenes(e):
    out={}
    for s in e['outputs']['scenes']:
        key=s['captureSequence'];value=(s['geometrySha256'],s['layerRasterSha256'])
        if key in out: assert out[key]==value
        out[key]=value
    return out
assert scenes(b)==scenes(c), 'projected geometry or rendered presentation pixels changed'
assert len(c['outputs']['scenes'])==len(c['samples']), 'candidate must retain one geometry product per capture'
assert all(f['inlineObjects']==f['inlineSegments']==0 for f in c['outputs']['frames'])
assert c['outbox'] and all(r['inlineGeometryItems']==0 for r in c['outbox'])
# Stage keys are random 256-bit capture leases in VirtualSkyCameraModule (not reproducibility facts).
# Each run already validates its durable scene source against the immutable raw descriptor.
def scene_facts(root):
    uri=Path(root,'canonical','journal','raw-ingress.db').resolve().as_uri()+'?mode=ro'
    with sqlite3.connect(uri,uri=True) as db:
        rows=db.execute('SELECT capture_sequence, manifest_json FROM raw_captures ORDER BY capture_sequence').fetchall()
    return {n:{k:v for k,v in json.loads(raw)['scene'].items() if k not in ('objects','segments','projectedSceneSchemaVersion','projectedSceneStageKey')} for n,raw in rows}
assert scene_facts(a.baseline_root)==scene_facts(a.candidate_root), 'reproducibility facts changed'
def geometry_items(value):
    if isinstance(value,list): return sum(geometry_items(v) for v in value)
    if not isinstance(value,dict): return 0
    return sum(len(v) if k in ('objects','segments') and isinstance(v,list) else geometry_items(v) for k,v in value.items())
def journal_geometry(root, expected):
    uri=Path(root,'canonical','journal','raw-ingress.db').resolve().as_uri()+'?mode=ro'
    with sqlite3.connect(uri,uri=True) as db:
        assert db.execute('SELECT COUNT(*) FROM processing_outputs').fetchone()[0]==expected['products'], 'compare before destructive retention preparation'
        return sum(geometry_items(json.loads(row[0])) for row in db.execute(
            'SELECT manifest_json FROM raw_captures UNION ALL SELECT descriptor_json FROM processing_outputs'))
baseline_journal_geometry=journal_geometry(a.baseline_root,b['journal'])
candidate_journal_geometry=journal_geometry(a.candidate_root,c['journal'])
assert candidate_journal_geometry==0, 'candidate journal still embeds geometry'
def metrics(e):
    samples=[s for s in e['samples'] if s['measured']]
    result={}
    for k in ('milliseconds','cpuMilliseconds','allocatedBytes','workingSetBytes','peakWorkingSetBytes'):
        values=sorted(s[k] for s in samples)
        result[k]={'median':statistics.median(values),'p95':values[min(len(values)-1,int(len(values)*.95))],'max':max(values)}
    result['measuredIo']={k:sum(s['io'][k] for s in samples) for k in samples[0]['io']}
    result['journal']=e['journal'];result['retainedScenePayloads']=len(e['outputs']['scenes'])
    result['retainedSceneBytes']=sum(s['bytes'] for s in e['outputs']['scenes'])
    result['frameManifestBytes']=sum(f['manifestBytes'] for f in e['outputs']['frames'])
    result['outboxManifestBytes']=sum(r['manifestBytes'] for r in e['outbox'])
    result['physicalFilesBytes']=sum(f['bytes'] for f in e['files'])
    result['bytesPerCapture']=result['physicalFilesBytes']/len(e['samples'])
    return result
output=Path(a.output);assert not output.exists()
output.write_text(json.dumps({'schemaVersion':'issue1055-comparison-v1','baselineRevision':b['revision'],'candidateRevision':c['revision'],'harnessSha256':c['harnessSha256'],'equivalence':'PASS','journalInlineGeometryItems':{'baseline':baseline_journal_geometry,'candidate':candidate_journal_geometry},'baseline':metrics(b),'candidate':metrics(c)},indent=2)+'\n')
print(output)
