#!/usr/bin/env python3
"""Predeclared process-paired analysis; no within-process pseudo-replication."""
import json, math, statistics, sys
from pathlib import Path
root=Path(sys.argv[1]); out=Path(sys.argv[2]); pairs=[]
metrics=['milliseconds','cpuMilliseconds','allocatedBytes']
for trial in range(1,6):
    runs={k:json.loads((root/f'trial{trial}-{k}.json').read_text()) for k in ('baseline','candidate')}
    assert all(e['stageProbeEnabled'] for e in runs.values())
    means={}; distributions={}; stages={}
    for kind,e in runs.items():
        samples=[s for s in e['samples'] if s['measured']];assert len(samples)==30
        means[kind]={m:statistics.mean(s[m] for s in samples) for m in metrics}
        distributions[kind]={m:{'median':statistics.median(s[m] for s in samples),'p95':sorted(s[m] for s in samples)[28],'max':max(s[m] for s in samples)} for m in metrics}
        names={v['name'] for s in samples for v in s['stages']};assert len(names)==8 and 'node:unknown' not in names
        stages[kind]={n:{m:statistics.mean(v[m] for s in samples for v in s['stages'] if v['name']==n) for m in metrics} for n in sorted(names)}
        for n in names:
            stages[kind][n]['io']={m:statistics.mean(v['io'][m] for s in samples for v in s['stages'] if v['name']==n) for m in ('rchar','wchar','syscr','syscw','read_bytes','write_bytes')}
    pairs.append({'trial':trial,'means':means,'distributions':distributions,'stages':stages})
def interval(values):
    mean=statistics.mean(values);half=2.7764451051977987*statistics.stdev(values)/math.sqrt(5)
    return {'mean':mean,'lower95':mean-half,'upper95':mean+half,'standardDeviation':statistics.stdev(values),'nIndependentProcessPairs':5}
def compare(getter):
    differences=[getter(p,'candidate')-getter(p,'baseline') for p in pairs]
    logs=[math.log(getter(p,'candidate')/getter(p,'baseline')) for p in pairs]
    iv=interval(logs)
    return {'pairedDifference':interval(differences),'pairedGeometricRatio':{k:math.exp(iv[k]) for k in ('mean','lower95','upper95')}}
summary={m:compare(lambda p,k,m=m:p['means'][k][m]) for m in metrics}
distribution_summary={stat:compare(lambda p,k,stat=stat:p['distributions'][k]['milliseconds'][stat]) for stat in ('median','p95','max')}
stage_summary={n:{m:compare(lambda p,k,n=n,m=m:p['stages'][k][n][m]) for m in metrics} for n in pairs[0]['stages']['baseline']}
tails=[]
for trial in range(1,6):
    for kind in ('baseline','candidate'):
        e=json.loads((root/f'trial{trial}-{kind}.json').read_text());samples=[s for s in e['samples'] if s['measured']]
        for s in sorted(samples,key=lambda s:s['milliseconds'],reverse=True)[:3]:
            previous=e['samples'][s['ordinal']-1]
            tails.append({'trial':trial,'kind':kind,'ordinal':s['ordinal'],'milliseconds':s['milliseconds'],'cpuMilliseconds':s['cpuMilliseconds'],'gcPauseMilliseconds':s['gc']['pauseMilliseconds']-previous['gc']['pauseMilliseconds'],'stages':s['stages']})
result={'method':'Five independent paired process trials, alternating order. Student t intervals on paired differences and log ratios, df4; process means are units. Node times overlap lane-inclusive and must not be summed twice. Small-N normality assumption and shared-host temporal variability limit inference; inclusion of1 is not equivalence. Probe has symmetric included overhead.','pairs':pairs,'total':summary,'descriptiveWallDistribution':distribution_summary,'stages':stage_summary,'slowestThreePerRun':tails}
assert not out.exists();out.write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps({'total':summary,'stageMilliseconds':{n:v['milliseconds'] for n,v in stage_summary.items()}},indent=2))
