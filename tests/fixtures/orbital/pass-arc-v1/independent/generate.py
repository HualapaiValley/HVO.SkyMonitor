#!/usr/bin/env python3
"""Independent, offline actual-EOP pass arcs. No HVO import, HTTP, tests or builds.

All UTC values are integer 100ns ticks, not datetime-rounded oracle epochs.
Generator inputs are retained approved bytes packaged beside this script.
"""
from pathlib import Path
from datetime import datetime, timedelta, timezone
from decimal import Decimal
import bisect,hashlib,importlib.util,json,math,sys
import numpy as np
import sgp4,skyfield,jplephem
from sgp4.api import Satrec,WGS72
from skyfield.sgp4lib import TEME_to_ITRF

BASE=Path(__file__).resolve().parent
INPUT=BASE/'inputs'
UTC=timezone.utc
TICKS=10_000_000
DAY=86400*TICKS
UNIX=datetime(1970,1,1,tzinfo=UTC)
MJD_UNIX=40587
EPOCH0=datetime(1949,12,31,tzinfo=UTC)
BRACKET=1000 # .0001 seconds, below fixed .001s independent gate
VERSIONS=dict(sgp4=sgp4.__version__,skyfield=skyfield.__version__,numpy=np.__version__,jplephem=jplephem.__version__)
assert VERSIONS==dict(sgp4='2.27',skyfield='1.55',numpy='2.5.4',jplephem='2.24')

def sha(b):return hashlib.sha256(b).hexdigest().upper()
def write(path,value):path.write_text(json.dumps(value,indent=2,allow_nan=False)+'\n')
def facts(path):
 b=path.read_bytes();return dict(path=str(path.relative_to(BASE)),bytes=len(b),sha256=sha(b))
def ticks(s):
 value=s.replace('Z','+00:00')
 daypart,clock=value.split('T');clock=clock.split('+')[0]
 hh,mm,ss=clock.split(':');seconds=Decimal(ss)
 date=datetime.fromisoformat(daypart).replace(tzinfo=UTC)
 return (date-UNIX).days*DAY+(int(hh)*3600+int(mm)*60)*TICKS+int(seconds*TICKS)
def stamp(t):
 days,sub=divmod(t,DAY);secs,frac=divmod(sub,TICKS)
 return (UNIX+timedelta(days=days,seconds=secs)).strftime('%Y-%m-%dT%H:%M:%S')+f'.{frac:07d}Z'
def dt_to_ticks(t):return (t-UNIX).days*DAY+(t.hour*3600+t.minute*60+t.second)*TICKS+t.microsecond*10

def implied(text):return float(text[0]+'0.'+text[1:6]+'e'+text[6:])
def from_tle(catalog):
 lines=(INPUT/'SGP4-VER.TLE').read_text().splitlines()
 i=next(i for i,l in enumerate(lines) if l.startswith('1 '+catalog))
 a,b=lines[i:i+2]
 yy=int(a[18:20]);year=2000+yy if yy<57 else 1900+yy
 epoch=dt_to_ticks(datetime(year,1,1,tzinfo=UTC))+int((Decimal(a[20:32])-1)*DAY)
 f=dict(OBJECT_NAME='VANGUARD 1 historical verification' if catalog=='00005' else 'MOLNIYA 2-14 historical verification',
  EPOCH=stamp(epoch).removesuffix('Z'),NORAD_CAT_ID=int(catalog),MEAN_MOTION=float(b[52:63]),ECCENTRICITY=float('0.'+b[26:33]),
  INCLINATION=float(b[8:16]),RA_OF_ASC_NODE=float(b[17:25]),ARG_OF_PERICENTER=float(b[34:42]),
  MEAN_ANOMALY=float(b[43:51]),BSTAR=implied(a[53:61]),MEAN_MOTION_DOT=float(a[33:43]),
  MEAN_MOTION_DDOT=implied(a[44:52]),EPHEMERIS_TYPE=int(a[62]),CLASSIFICATION_TYPE=a[7])
 return f,dict(line1=a,line2=b,rawFile='inputs/SGP4-VER.TLE',rawSha256=sha((INPUT/'SGP4-VER.TLE').read_bytes()))

spec=importlib.util.spec_from_file_location('retained_coordinate_oracle',BASE/'coordinate-reference-generator.py')
o=importlib.util.module_from_spec(spec);spec.loader.exec_module(o)
assert sha((BASE/'coordinate-reference-generator.py').read_bytes())=='47DEFF6FA0B236598EB6EBF99716C69E2A6A8E08256E4695C3F7048BED51F4D7'
assert sha((INPUT/'SGP4-VER.TLE').read_bytes())=='76727A72FC0D0A1AF115E03AF487C33213BEC8842AE6C32B5F0829454C8A45D7'
assert sha((INPUT/'leap-seconds.list').read_bytes())=='DB5A895F16853B03BFC865E8D68F9FC8710EF1740E3400C701CD46A5BBBC3433'
leaps=[];expiry=None
for line in (INPUT/'leap-seconds.list').read_text().splitlines():
 if line.startswith('#@'):expiry=ticks('1900-01-01T00:00:00Z')+int(line[2:].split()[0])*TICKS
 elif line.strip() and not line.lstrip().startswith('#'):
  a,b=line.split('#')[0].split()[:2];leaps.append((ticks('1900-01-01T00:00:00Z')+int(a)*TICKS,int(b)))
assert expiry is not None

class Case:
 def __init__(self,spec):
  self.spec=spec;self.f=spec['facts'];self.epoch=ticks(self.f['EPOCH']);self.site=spec['observer'];self.cache={};self.calls=0
  self.rows=[]
  for l in (BASE/spec['eopFile']).read_text().splitlines():
   date=(int(float(l[7:15]))-MJD_UNIX)*DAY
   if l[134:165].strip():row=(date,float(l[154:165]),float(l[134:144]),float(l[144:154]),'Final')
   elif l[16]==l[57]=='P':row=(date,float(l[58:68]),float(l[18:27]),float(l[37:46]),'Predicted')
   else:raise ValueError('Unsupported retained EOP row')
   self.rows.append(row)
  self.dates=[r[0] for r in self.rows]
  assert all(b[0]-a[0]==DAY for a,b in zip(self.rows,self.rows[1:]))
  assert set(r[-1] for r in self.rows)=={spec['eopStatus']}
  self.sat=Satrec();f=self.f
  epoch0=dt_to_ticks(EPOCH0)
  epochdays=float(Decimal(self.epoch-epoch0)/Decimal(DAY))
  self.sat.sgp4init(WGS72,'a',f['NORAD_CAT_ID'],epochdays,f['BSTAR'],f['MEAN_MOTION_DOT']*2*math.pi/1440**2,
   f['MEAN_MOTION_DDOT']*2*math.pi/1440**3,f['ECCENTRICITY'],math.radians(f['ARG_OF_PERICENTER']),
   math.radians(f['INCLINATION']),math.radians(f['MEAN_ANOMALY']),f['MEAN_MOTION']*2*math.pi/1440,
   math.radians(f['RA_OF_ASC_NODE']))
  assert self.sat.operationmode=='a' and self.sat.error==0
  self.window=(ticks(spec['windowStartUtc']),ticks(spec['windowEndUtc']))
  lo,hi=min(self.epoch,self.window[0]),max(self.epoch,self.window[1])
  assert leaps[0][0]<=lo<=hi<expiry
  assert not any(lo<t<=hi for t,v in leaps)
  assert self.dates[0]<=self.window[0]<=self.window[1]<=self.dates[-1]
  n=f['MEAN_MOTION']*2*math.pi/86400
  perigee=(398600.8/(n*n))**(1/3)*(1-f['ECCENTRICITY'])-6378.135
  assert .5<=f['MEAN_MOTION']<=18 and f['ECCENTRICITY']<=.9 and abs(f['BSTAR'])<=.01 and perigee>=100
  self.perigee=perigee
 def state(self,t):
  if t in self.cache:return self.cache[t]
  i=bisect.bisect_right(self.dates,t)-1
  if t==self.rows[i][0]:dut1,xp,yp=self.rows[i][1:4]
  else:
   a,b=self.rows[i:i+2];assert b[0]-a[0]==DAY
   phase=(t-a[0])/DAY;dut1,xp,yp=[a[j]+phase*(b[j]-a[j]) for j in range(1,4)]
  error,r,v=self.sat.sgp4_tsince((t-self.epoch)/(60*TICKS));assert error==0,(error,stamp(t))
  day,sub=divmod(t,DAY)
  jd=2440587.5+day;fraction=sub/DAY
  p,q=TEME_to_ITRF(jd,np.array(r),np.array(v)*86400,math.radians(xp/3600),math.radians(yp/3600),fraction+dut1/86400)
  z=o.topocentric(p,q/86400,self.site)
  assert z['rangeKilometers']>=50 and abs(sum(x*x for x in z['unitEnu'])-1)<1e-12
  result=dict(utc=stamp(t),error=0,altitude=z['altitudeDegrees'],azimuth=z['azimuthDegrees'],unitEnu=z['unitEnu'],
   rangeKilometers=z['rangeKilometers'],rangeRateKilometersPerSecond=z['rangeRateKilometersPerSecond'],
   angularSpeedDegreesPerSecond=z['angularSpeedDegreesPerSecond'])
  self.calls+=1;self.cache[t]=result;return result
 def alt(self,t):return self.state(t)['altitude']
 def root(self,lo,hi):
  a=self.alt(lo);assert (a>=0)!=(self.alt(hi)>=0)
  original=(lo,hi)
  while hi-lo>BRACKET:
   mid=(lo+hi)//2;b=self.alt(mid)
   if (a>=0)==(b>=0):lo,a=mid,b
   else:hi=mid
  t=(lo+hi)//2
  return dict(utc=stamp(t),kind='rise' if self.alt(hi)>self.alt(lo) else 'set',
   bracketUtc=[stamp(lo),stamp(hi)],bracketWidthSeconds=(hi-lo)/TICKS,
   bracketAltitudeDegrees=[self.alt(lo),self.alt(hi)],seedBracketUtc=[stamp(x) for x in original])
 def peak(self,lo,hi):
  while hi-lo>BRACKET:
   a=(2*lo+hi)//3;b=(lo+2*hi)//3
   if self.alt(a)<self.alt(b):lo=a
   else:hi=b
  t=(lo+hi)//2
  return dict(utc=stamp(t),altitude=self.alt(t),bracketUtc=[stamp(lo),stamp(hi)],bracketWidthSeconds=(hi-lo)/TICKS)
 def mesh(self,lo,hi,step,phase=0):
  values=[lo,*range(lo+phase if phase else lo+step,hi,step),hi]
  return sorted(set(values))
 def scan(self,step,phase=0):
  lo,hi=self.window;grid=self.mesh(lo,hi,step,phase)
  crossings=[self.root(a,b) for a,b in zip(grid,grid[1:]) if (self.alt(a)>=0)!=(self.alt(b)>=0)]
  hint=ticks(self.spec['peakHintUtc'])
  pair=next((a,b) for a,b in zip(crossings,crossings[1:]) if a['kind']=='rise' and b['kind']=='set' and ticks(a['utc'])<=hint<=ticks(b['utc']))
  start,end=[ticks(x['utc']) for x in pair]
  mesh=self.mesh(start,end,step,phase)
  maxima=[self.peak(mesh[i-1],mesh[i+1]) for i in range(1,len(mesh)-1)
   if self.alt(mesh[i])>self.alt(mesh[i-1]) and self.alt(mesh[i])>=self.alt(mesh[i+1])]
  assert maxima
  peak=max(maxima,key=lambda x:(x['altitude'],-ticks(x['utc'])))
  # Global peak among every observed local maximum and endpoints, not ternary search over the whole pass.
  assert peak['altitude']>=max(self.alt(start),self.alt(end))
  return pair,peak,maxima
 def run(self):
  step=int(Decimal(str(self.spec['scanStepSeconds']))*TICKS)
  pair,peak,maxima=self.scan(step)
  qp,qpeak,qmaxima=self.scan(step//2,step//4)
  assert all(abs(ticks(a['utc'])-ticks(b['utc']))/TICKS<=.001 for a,b in zip(pair,qp))
  # Flat extrema can vary by a small timestamp despite numerically identical height; keep original20ms gate.
  assert abs(ticks(peak['utc'])-ticks(qpeak['utc']))/TICKS<=.02
  start,end=ticks(pair[0]['utc']),ticks(pair[1]['utc']);pt=ticks(peak['utc'])
  stride=int(Decimal(self.spec['sampleStrideSeconds'])*TICKS);phase=int(Decimal('0.1234567')*TICKS)
  times=sorted(set([start,pt,end,*range(start+phase,end,stride)]))
  samples=[self.state(t) for t in times]
  anchors=dict(start=self.state(start),peak=self.state(pt),end=self.state(end))
  d=self.spec.copy();d.pop('peakHintUtc');d.pop('scanStepSeconds');d.pop('sampleStrideSeconds')
  d.update(schema='independent-actual-eop-pass-arc-case-v1',gravity='WGS72',mode='a',nominalZeroEop=False,
   coordinateConvention='teme-gmst82-ut1-pm-no-sp-nominal-rate-v1',
   eopSha256=sha((BASE/d['eopFile']).read_bytes()),eopCoverage=dict(startUtc=stamp(self.dates[0]),endUtc=stamp(self.dates[-1])),
   leapFile='inputs/leap-seconds.list',leapSha256=sha((INPUT/'leap-seconds.list').read_bytes()),
   leapCoverage=dict(startUtc=stamp(leaps[0][0]),endExclusiveUtc=stamp(expiry)),
   observerHeightDatum='wgs84-ellipsoidal-assumed',refraction='none',illumination='Unknown',physicalOrbitUncertainty=None,
   requestedArcTargetDegrees=.001,independentDirectionAllowanceDegrees=1e-6,exactUtcAnchorAllowanceDegrees=1e-8,
   passBoundaryAgreementSeconds=.02,independentBracketMaximumSeconds=.001,
   passInfo=dict(startUtc=stamp(start),endUtc=stamp(end),peakUtc=stamp(pt),maximumElevationDegrees=peak['altitude'],startWindowClipped=False,endWindowClipped=False),
   anchors=anchors,samples=samples,
   sampling=dict(phaseSeconds='0.1234567',strideSeconds=str(Decimal(stride)/TICKS),stateCount=len(samples),utcPrecisionSeconds='0.0000001',
    boundaryAndPeakUtcIncluded=True,interpolation='none; every expected state is independently evaluated at its own exact UTC'),
   independentSearch=dict(primaryScanStepSeconds=step/TICKS,qualificationScanStepSeconds=step/(2*TICKS),qualificationPhaseSeconds=step/(4*TICKS),
    rise=pair[0],set=pair[1],peak=peak,localMaxima=maxima,qualificationRise=qp[0],qualificationSet=qp[1],qualificationPeak=qpeak,
    peakTimeDifferenceSeconds=abs(ticks(peak['utc'])-ticks(qpeak['utc']))/TICKS,
    caveat='Bounded two-phase fixture scan plus separately refined all observed extrema; not a generic all-orbit completeness proof.'),
   initializer=dict(operationMode='a',gravity='WGS72',epochUtc=stamp(self.epoch),epochDaysSince1949December31=float(Decimal(self.epoch-dt_to_ticks(EPOCH0))/DAY),
    propagation='sgp4_tsince from exact integer100ns query/decimal element epoch difference, not subtraction of rounded full Julian dates',
    derivatives='raw GP/TLE DOT/DDOT multiplied by2pi/1440^2 and2pi/1440^3; no extra factors2/6'),
   nominalKeplerPerigeeKilometers=self.perigee,finiteStateEvaluations=self.calls)
  d['pass']=d.pop('passInfo')
  return d


def main():
 if len(sys.argv)>1:
  manifest=json.loads((BASE/'artifact-manifest.json').read_text())
  for f in manifest['files']:
   b=(BASE/f['path']).read_bytes();assert len(b)==f['bytes'] and sha(b)==f['sha256'],f['path']
  print('Verified',len(manifest['files']),'retained artifact file hashes');return
 gp=json.loads((INPUT/'gp-source-facts.json').read_text());iss=next(x for x in gp if x['NORAD_CAT_ID']==25544)
 vang,vtle=from_tle('00005');mol,mtle=from_tle('08195')
 specs=[dict(name='ISS-Seattle-predicted-EOP-full-pass',facts=iss,observer=[47.6062,-122.3321,50],
  windowStartUtc='2026-10-20T12:57:00Z',windowEndUtc='2026-10-20T13:11:00Z',peakHintUtc='2026-10-20T13:04:21Z',
  thresholdDegrees=0,eopFile='inputs/predicted-2026.finals2000A',eopStatus='Predicted',scanStepSeconds=5,sampleStrideSeconds='.137'),
 dict(name='Vanguard1-Sydney-final-EOP-full-pass',facts=vang,sourceTle=vtle,observer=[-33.8688,151.2093,50],
  windowStartUtc='2000-06-28T11:40:00Z',windowEndUtc='2000-06-28T12:40:00Z',peakHintUtc='2000-06-28T12:08:33Z',
  thresholdDegrees=0,eopFile='inputs/final-2000.finals2000A',eopStatus='Final',scanStepSeconds=10,sampleStrideSeconds='.237'),
 dict(name='Molniya08195-HVO-final-EOP-full-pass',facts=mol,sourceTle=mtle,observer=[35.347,-113.878,0],
  windowStartUtc='2006-06-25T00:40:00Z',windowEndUtc='2006-06-25T03:10:00Z',peakHintUtc='2006-06-25T01:53:51Z',
  thresholdDegrees=0,eopFile='inputs/final-2006.finals2000A',eopStatus='Final',scanStepSeconds=10,sampleStrideSeconds='.731')]
 cases=[]
 for s in specs:
  c=Case(s).run();cases.append(c)
  print(json.dumps(dict(name=c['name'],passInfo=c['pass'],eopStatus=c['eopStatus'],samples=len(c['samples']),evaluations=c['finiteStateEvaluations'])),flush=True)
 total=sum(len(c['samples']) for c in cases);assert total<=30000,total
 data=dict(schema='independent-actual-eop-pass-arc-pack-v1',softwareVersions=VERSIONS,maximumOutputStates=30000,totalOutputStates=total,cases=cases)
 write(BASE/'cases.json',data)
 actual=[]
 for package,paths in [('sgp4',['api.py','wrapper.py','vallado_cpp.abi3.so']),('skyfield',['sgp4lib.py','toposlib.py','timelib.py','functions.py'])]:
  module=importlib.import_module(package);directory=Path(module.__file__).parent
  for name in paths:
   path=directory/name
   if path.exists():actual.append(dict(package=package,path=name,bytes=path.stat().st_size,sha256=sha(path.read_bytes())))
 write(BASE/'software-hashes.json',dict(versions=VERSIONS,python=sys.version,usedFiles=actual,
  referenceSourceManifest='inputs/coordinate-source-manifest-original.json',licenses='notices/'))
 print('TOTAL',total,flush=True)

if __name__=='__main__':main()
