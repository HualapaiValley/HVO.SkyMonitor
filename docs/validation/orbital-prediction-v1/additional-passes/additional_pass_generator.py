#!/usr/bin/env python3
"""Offline independent historical pass oracles. No HVO import, HTTP or production use.

Predeclared: WGS72/AFSPC a; zero EOP; epoch-12h..epoch+48h; HVO observer;
thresholds0/10deg; independent bracket width<=0.001s; HVO agreement<=0.02s.
A Kepler perigee-aware seed<=30s plus a half-step/shifted qualification grid
and refined extrema qualify these two fixtures numerically, not globally.
"""
from pathlib import Path
from datetime import datetime,timedelta,timezone
from decimal import Decimal
import hashlib,importlib.util,json,math
import numpy as np
import skyfield,sgp4,jplephem
from sgp4.api import Satrec,WGS72
from skyfield.sgp4lib import TEME_to_ITRF

BASE=Path(__file__).resolve().parent
UTC=timezone.utc
MJD=datetime(1858,11,17,tzinfo=UTC)
EPOCH0=datetime(1949,12,31,tzinfo=UTC)
SITE=[35.347,-113.878,0]
BRACKET_SECONDS=.001
HVO_AGREEMENT_SECONDS=.02
THRESHOLDS=[0,10]
TLE_HASH='76727A72FC0D0A1AF115E03AF487C33213BEC8842AE6C32B5F0829454C8A45D7'
LEAP_HASH='DB5A895F16853B03BFC865E8D68F9FC8710EF1740E3400C701CD46A5BBBC3433'
ORACLE_HASH='47DEFF6FA0B236598EB6EBF99716C69E2A6A8E08256E4695C3F7048BED51F4D7'

def sha(b):return hashlib.sha256(b).hexdigest().upper()
def facts(path):
 b=path.read_bytes();return dict(path=str(path.relative_to(BASE)),bytes=len(b),sha256=sha(b))
def write(path,obj):path.write_text(json.dumps(obj,indent=2,allow_nan=False)+'\n')
def implied(text):return float(text[0]+'0.'+text[1:6]+'e'+text[6:])
def utcstr(utc):return utc.isoformat(timespec='microseconds').replace('+00:00','Z')
def split(utc):
 midnight=utc.replace(hour=0,minute=0,second=0,microsecond=0)
 return 2400000.5+(midnight-MJD).days,(utc-midnight).total_seconds()/86400

spec=importlib.util.spec_from_file_location('retained_coordinate_oracle',BASE/'coordinate-reference-generator.py')
o=importlib.util.module_from_spec(spec);spec.loader.exec_module(o)
assert (skyfield.__version__,sgp4.__version__,np.__version__,jplephem.__version__)==('1.55','2.27','2.5.4','2.24')
assert sha((BASE/'SGP4-VER.TLE').read_bytes())==TLE_HASH
assert sha((BASE/'leap-seconds.list').read_bytes())==LEAP_HASH
assert sha((BASE/'coordinate-reference-generator.py').read_bytes())==ORACLE_HASH
lines=(BASE/'SGP4-VER.TLE').read_text().splitlines()
# Validate actual retained leap facts, not coverage inferred from EOP or software defaults.
ntp_epoch=datetime(1900,1,1,tzinfo=UTC)
leap_rows=[];leap_expiry=None
for line in (BASE/'leap-seconds.list').read_text().splitlines():
 if line.startswith('#@'):
  leap_expiry=ntp_epoch+timedelta(seconds=int(line[2:].split()[0]))
 elif line.strip() and not line.lstrip().startswith('#'):
  fields=line.split('#')[0].split()
  leap_rows.append((ntp_epoch+timedelta(seconds=int(fields[0])),int(fields[1])))
assert leap_rows[0][0]==datetime(1972,1,1,tzinfo=UTC)
assert leap_expiry==datetime(2027,6,28,tzinfo=UTC)

class Case:
 def __init__(self,catalog,regime):
  index=next(i for i,line in enumerate(lines) if line.startswith('1 '+catalog))
  self.line1,self.line2=lines[index:index+2]
  a,b=self.line1,self.line2
  decoded=Satrec.twoline2rv(a,b,WGS72)
  yy=int(a[18:20]);year=2000+yy if yy<57 else 1900+yy
  day=Decimal(a[20:32]);self.epoch=datetime(year,1,1,tzinfo=UTC)+timedelta(microseconds=int((day-1)*Decimal(86400000000)))
  f=self.elements=dict(meanMotionRevolutionsPerDay=float(b[52:63]),eccentricity=float('0.'+b[26:33]),
   inclinationDegrees=float(b[8:16]),ascendingNodeDegrees=float(b[17:25]),argumentOfPericenterDegrees=float(b[34:42]),
   meanAnomalyDegrees=float(b[43:51]),bStarInverseEarthRadii=implied(a[53:61]),meanMotionDot=float(a[33:43]),
   meanMotionDdot=implied(a[44:52]),ephemerisType=int(a[62]),classification=a[7],elementSetNumber=None,revolutionNumber=None)
  ndot=f['meanMotionDot']*2*math.pi/1440**2;nddot=f['meanMotionDdot']*2*math.pi/1440**3
  assert math.isclose(decoded.ndot,ndot,rel_tol=2e-15,abs_tol=1e-30)
  assert math.isclose(decoded.nddot,nddot,rel_tol=2e-15,abs_tol=1e-35)
  assert decoded.operationmode=='i'  # Explicitly document and reject silently retaining API default mode.
  sat=Satrec()
  epochDays=float(Decimal((datetime(year,1,1,tzinfo=UTC)-EPOCH0).days)+day-1)
  sat.sgp4init(WGS72,'a',int(catalog),epochDays,f['bStarInverseEarthRadii'],ndot,nddot,f['eccentricity'],
   math.radians(f['argumentOfPericenterDegrees']),math.radians(f['inclinationDegrees']),math.radians(f['meanAnomalyDegrees']),
   f['meanMotionRevolutionsPerDay']*2*math.pi/1440,math.radians(f['ascendingNodeDegrees']))
  assert sat.operationmode=='a' and sat.error==0
  self.sat=sat;self.catalog=catalog;self.regime=regime;self.cache={};self.evaluations=0
  n=f['meanMotionRevolutionsPerDay']*2*math.pi/86400;e=f['eccentricity']
  perigee=(398600.8/(n*n))**(1/3)*(1-e)-6378.135
  self.profile=dict(nominalKeplerPerigeeKilometers=perigee,
   supported=f['meanMotionRevolutionsPerDay']>=.5 and f['meanMotionRevolutionsPerDay']<=18 and e<=.9 and abs(f['bStarInverseEarthRadii'])<=.01 and perigee>=100)
  assert self.profile['supported']
  keplerMaxRate=n*math.sqrt(1+e)/(1-e)**1.5
  self.step=math.floor(min(30,math.radians(2)/keplerMaxRate)*1000)/1000
  self.seedFacts=dict(keplerPerigeeAngularRateRadiansPerSecond=keplerMaxRate,maxKeplerPhaseStepDegrees=2,
    primaryStepSeconds=self.step,qualificationStepSeconds=self.step/2,qualificationPhaseSeconds=self.step/4,
    caveat='This nominal Kepler rate selects a practical seed for these retained records; it is not a rigorous SGP4/topocentric rate or whole-cell cap.')
  self.start=self.epoch-timedelta(hours=12);self.end=self.epoch+timedelta(hours=48)
  self.duration=(self.end-self.start).total_seconds()
  assert leap_rows[0][0]<=self.start<=self.epoch<=self.end<leap_expiry
  assert not any(self.start<utc<=self.end for utc,offset in leap_rows[1:])
  self.decoding=dict(twoline2rvDefaultMode=decoded.operationmode,reinitializedMode=sat.operationmode,gravity='WGS72',
   meanMotionDotRawGp=f['meanMotionDot'],meanMotionDdotRawGp=f['meanMotionDdot'],
   ndotRadiansPerMinuteSquared=ndot,nddotRadiansPerMinuteCubed=nddot,
   epochDaysSince1949December31=epochDays,
   derivativeConvention='gp-tle-field-values-rev-per-day-powers-v1; no extra factors2/6',
   propagationTime='sgp4_tsince with exact UTC elapsed difference from decimal TLE epoch, including negative minutes; coordinate rotation gets independently split UTC JD.')

 def at(self,seconds):
  # Float seconds only select a Python microsecond-representable UTC, never a rounded full JD.
  utc=self.start+timedelta(seconds=float(seconds))
  key=utc
  if key in self.cache:return self.cache[key]
  minutes=(utc-self.epoch).total_seconds()/60
  error,r,v=self.sat.sgp4_tsince(minutes)
  if error:raise RuntimeError(f'{self.catalog} SGP4 error{error} at {utcstr(utc)} (tsince{minutes})')
  jd,fraction=split(utc)
  p,q=TEME_to_ITRF(jd,np.array(r),np.array(v)*86400,0,0,fraction)
  top=o.topocentric(p,q/86400,SITE)
  assert top['rangeKilometers']>=50
  self.evaluations+=1
  self.cache[key]=(utc,top)
  return utc,top

 def alt(self,t):return self.at(t)[1]['altitudeDegrees']
 def probe(self,t):
  utc,p=self.at(t)
  return dict(secondsSinceWindowStart=t,utc=utcstr(utc),altitudeDegrees=p['altitudeDegrees'],azimuthDegrees=p['azimuthDegrees'],
   rangeKilometers=p['rangeKilometers'],rangeRateKilometersPerSecond=p['rangeRateKilometersPerSecond'])
 def refine_extremum(self,lo,hi,is_max):
  while hi-lo>BRACKET_SECONDS:
   a=(2*lo+hi)/3;b=(lo+2*hi)/3
   if (self.alt(a)<self.alt(b))==is_max:lo=a
   else:hi=b
  middle=(lo+hi)/2
  return dict(kind='maximum' if is_max else 'minimum',bracketSeconds=[lo,hi],bracketWidthSeconds=hi-lo,
   bracketUtc=[utcstr(self.at(lo)[0]),utcstr(self.at(hi)[0])],bracketAltitudeDegrees=[self.alt(lo),self.alt(hi)],
   secondsSinceWindowStart=middle,utc=utcstr(self.at(middle)[0]),altitudeDegrees=self.alt(middle))
 def crossing(self,lo,hi,threshold):
  initial_lo,initial_hi=lo,hi;a=self.alt(lo)-threshold
  assert (a>=0)!=(self.alt(hi)>=threshold)
  while hi-lo>BRACKET_SECONDS:
   mid=(lo+hi)/2;b=self.alt(mid)-threshold
   if (a>=0)==(b>=0):lo,a=mid,b
   else:hi=mid
  middle=(lo+hi)/2
  return dict(kind='rise' if self.alt(hi)>self.alt(lo) else 'set',thresholdDegrees=threshold,
   seedBracketSeconds=[initial_lo,initial_hi],bracketSeconds=[lo,hi],bracketWidthSeconds=hi-lo,
   bracketUtc=[utcstr(self.at(lo)[0]),utcstr(self.at(hi)[0])],bracketAltitudeDegrees=[self.alt(lo),self.alt(hi)],
   secondsSinceWindowStart=middle,utc=utcstr(self.at(middle)[0]),altitudeDegrees=self.alt(middle))
 def scan(self,step,phase):
  grid=sorted(set([0.,self.duration]+list(np.arange(phase,self.duration,step,dtype=float))))
  values=[self.alt(t) for t in grid]
  extrema=[]
  for i in range(1,len(grid)-1):
   if values[i]>values[i-1] and values[i]>=values[i+1]:extrema.append(self.refine_extremum(grid[i-1],grid[i+1],True))
   elif values[i]<values[i-1] and values[i]<=values[i+1]:extrema.append(self.refine_extremum(grid[i-1],grid[i+1],False))
  # Add discovered extrema before threshold sign tests, so an actual refined interior peak
  # above a threshold is retained even if both bounding seed endpoints are below it.
  augmented=sorted(set(grid+[p['secondsSinceWindowStart'] for p in extrema]))
  by_threshold=[]
  for threshold in THRESHOLDS:
   roots=[]
   for a,b in zip(augmented,augmented[1:]):
    if (self.alt(a)>=threshold)!=(self.alt(b)>=threshold):roots.append(self.crossing(a,b,threshold))
   peaks=[p for p in extrema if p['kind']=='maximum' and p['altitudeDegrees']>=threshold]
   by_threshold.append(dict(thresholdDegrees=threshold,startAboveThreshold=values[0]>=threshold,endAboveThreshold=values[-1]>=threshold,crossings=roots,peaks=peaks))
  return dict(seedStepSeconds=step,seedPhaseSeconds=phase,seedPointCount=len(grid),extrema=extrema,thresholds=by_threshold)

 def make_passes(self,threshold):
  roots=threshold['crossings'];passes=[];rise=None;open_start=0. if threshold['startAboveThreshold'] else None
  def close(end,set_event):
   interior=[p for p in threshold['peaks'] if open_start<=p['secondsSinceWindowStart']<=end]
   candidates=interior+[dict(kind='window-boundary',**self.probe(open_start)),dict(kind='window-boundary',**self.probe(end))]
   peak=max(candidates,key=lambda p:p['altitudeDegrees'])
   passes.append(dict(startSeconds=open_start,endSeconds=end,startUtc=utcstr(self.at(open_start)[0]),endUtc=utcstr(self.at(end)[0]),
    startsAtWindowBoundary=rise is None,endsAtWindowBoundary=set_event is None,rise=rise,set=set_event,peak=peak))
  for root in roots:
   if root['kind']=='rise':
    assert open_start is None;rise=root;open_start=root['secondsSinceWindowStart']
   else:
    assert open_start is not None;close(root['secondsSinceWindowStart'],root);open_start=None;rise=None
  if open_start is not None:close(self.duration,None)
  return passes

 def output(self):
  primary=self.scan(self.step,0)
  qualification=self.scan(self.step/2,self.step/4)
  deltas=[]
  for a,b in zip(primary['thresholds'],qualification['thresholds']):
   assert len(a['crossings'])==len(b['crossings'])
   assert len(a['peaks'])==len(b['peaks'])
   crossing_deltas=[];peak_deltas=[]
   for x,y in zip(a['crossings'],b['crossings']):
    assert x['kind']==y['kind'];delta=abs(x['secondsSinceWindowStart']-y['secondsSinceWindowStart']);assert delta<.003
    crossing_deltas.append(delta)
   for x,y in zip(a['peaks'],b['peaks']):
    delta=abs(x['secondsSinceWindowStart']-y['secondsSinceWindowStart']);assert delta<HVO_AGREEMENT_SECONDS
    peak_deltas.append(delta)
   a['passes']=self.make_passes(a)
   deltas.append(dict(thresholdDegrees=a['thresholdDegrees'],crossingCount=len(a['crossings']),peakCount=len(a['peaks']),passCount=len(a['passes']),
    maximumCrossingTimestampDifferenceSeconds=max(crossing_deltas,default=0),maximumPeakTimestampDifferenceSeconds=max(peak_deltas,default=0)))
  # Confirm negative epoch offsets are actually propagated successfully, not just advertised.
  negative_minutes=(self.start-self.epoch).total_seconds()/60
  assert negative_minutes==-720
  self.at(0);self.at(self.duration)
  return dict(schema='additional-orbital-pass-reference-v1',catalogNumber=self.catalog,objectIdentity=dict(authority='norad',catalogNumber=str(int(self.catalog))),regime=self.regime,
   sourceTle=dict(line1=self.line1,line2=self.line2,sourceSha256=TLE_HASH),epochUtc=utcstr(self.epoch),elements=self.elements,numericalSupportProfile=self.profile,initializer=self.decoding,
   observer=dict(latitudeDegrees=SITE[0],longitudeDegrees=SITE[1],elevationMeters=SITE[2],heightDatum='wgs84-ellipsoidal-assumed'),
   timeModel=dict(nominalZeroEarthOrientation=True,dut1Seconds=0,polarMotionXArcseconds=0,polarMotionYArcseconds=0,
    conversionVersion='teme-gmst82-ut1-pm-no-sp-nominal-rate-v1',ut1Convention='UT1=UTC; explicit degraded diagnostic',refraction='none',
    leapSecondsSha256=LEAP_HASH,leapCoverageStartUtc='1972-01-01T00:00:00Z',leapCoverageEndExclusiveUtc='2027-06-28T00:00:00Z',leapCrossing=False),
   windowStartUtc=utcstr(self.start),windowEndUtc=utcstr(self.end),windowSeconds=self.duration,
   negativeEpochOffsetMinutes=negative_minutes,negativeEpochOffsetPropagationSucceeded=True,
   independentBracketMaximumSeconds=BRACKET_SECONDS,rootEngineAgreementMaximumSeconds=HVO_AGREEMENT_SECONDS,
   endpoints=dict(start=self.probe(0),end=self.probe(self.duration)),seed=self.seedFacts,
   extrema=primary['extrema'],thresholds=primary['thresholds'],qualification=dict(method='Second half-step phase-shifted independent numerical scan; same declared solver and immutable inputs.',comparisons=deltas),
   finiteSuccessfulStateEvaluations=self.evaluations,
   limitations=['These are historical reference numerical/diagnostic scenarios, not current orbit or physical accuracy claims.',
    'Nominal zero EOP is explicitly degraded; no final/predicted EOP, physical observer/clock precision, illumination or brightness claim.',
    'Seed selection, refined extrema and agreement under a finer shifted scan qualify these two fixtures numerically; this is not a rigorous global completeness proof.',
    'Crossing brackets are sign-changing numerical intervals; extremum brackets use local unimodality found from the stated seed, not interval-certified global extrema.',
    'The existing source oracles and tolerances are untouched. Root still must compare its actual engine products with these added fixed expectations.'])

cases=[]
for catalog,regime in [('08195','Molniya12h-resonant-high-eccentricity'),('28623','GTO-deep-space-high-eccentricity-low-perigee')]:
 case=Case(catalog,regime);result=case.output();write(BASE/(catalog+'-passes.json'),result);cases.append(result)
summary=dict(schema='additional-orbital-pass-reference-summary-v1',gravity='WGS72',operationMode='a',nominalZeroEopDiagnostic=True,
 independentBracketMaximumSeconds=BRACKET_SECONDS,rootEngineAgreementMaximumSeconds=HVO_AGREEMENT_SECONDS,
 cases=[dict(catalogNumber=c['catalogNumber'],epochUtc=c['epochUtc'],numericalSupportProfile=c['numericalSupportProfile'],
 seed=c['seed'],endpoints=c['endpoints'],negativeEpochOffsetPropagationSucceeded=c['negativeEpochOffsetPropagationSucceeded'],comparisons=c['qualification']['comparisons'],finiteSuccessfulStateEvaluations=c['finiteSuccessfulStateEvaluations']) for c in cases],
 softwareVersions=dict(sgp4=sgp4.__version__,skyfield=skyfield.__version__,numpy=np.__version__,jplephem=jplephem.__version__),
 generator=facts(Path(__file__)),inputs=[facts(BASE/n) for n in ('SGP4-VER.TLE','leap-seconds.list','coordinate-reference-generator.py')])
write(BASE/'summary.json',summary)
print(json.dumps(summary,indent=2))
