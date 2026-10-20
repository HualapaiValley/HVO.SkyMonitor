#!/usr/bin/env python3
"""Offline oracle only; never imports HVO. See coordinate-reference.md and source manifest."""
import argparse
import hashlib
import json
import math
from datetime import datetime, timedelta, timezone
from pathlib import Path

import jplephem
import numpy as np
import sgp4
import skyfield
from sgp4.api import Satrec, WGS72
from skyfield.api import wgs84
from skyfield.sgp4lib import TEME_to_ITRF
from skyfield.timelib import julian_date

ROOT = Path(__file__).resolve().parents[3]
PACK = ROOT / 'tests/fixtures/orbital-prediction-v1'
TIME = PACK / 'time'
COORD = PACK / 'coordinates'
UTC = timezone.utc
MJD = datetime(1858, 11, 17, tzinfo=UTC)
EPOCH0 = datetime(1949, 12, 31, tzinfo=UTC)
FULL_HASH = 'BFA2066AAB0400B075357F573AF6081E0068A518993609EEE0C5CA991BAB0845'


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def write(path, obj):
    path.write_text(json.dumps(obj, indent=2, allow_nan=False) + '\n')


def split(utc):
    midnight = utc.replace(hour=0, minute=0, second=0, microsecond=0)
    return 2400000.5 + (midnight - MJD).days, (utc - midnight).total_seconds() / 86400


def eop_rows(path):
    rows = []
    for line in path.read_text().splitlines():
        jd = float(line[7:15])
        if line[134:165].strip():
            rows.append((jd, float(line[154:165]), float(line[134:144]), float(line[144:154])))
        elif line[16] == line[57] == 'P':
            rows.append((jd, float(line[58:68]), float(line[18:27]), float(line[37:46])))
    return rows


def eop(utc):
    jd, fraction = split(utc)
    mjd = jd - 2400000.5 + fraction
    rows = HISTORICAL if mjd < 55000 else MODERN
    for a, b in zip(rows, rows[1:]):
        if a[0] <= mjd <= b[0]:
            t = ((utc - (MJD + timedelta(days=a[0]))).total_seconds() / 86400)
            return [a[i] + t * (b[i] - a[i]) for i in range(1, 4)]
    raise ValueError('EOP outside retained brackets')


def fixed(r, v, utc):
    dut1, xp, yp = eop(utc)
    jd, fraction = split(utc)
    return TEME_to_ITRF(jd, np.array(r), np.array(v) * 86400,
                        math.radians(xp / 3600), math.radians(yp / 3600), fraction + dut1 / 86400)


def observer(site):
    return wgs84.latlon(site[0], site[1], site[2])


def topocentric(r, v, site):
    obs = observer(site)
    relative = obs._R_latlon @ (r - obs.itrs_xyz.km)  # Skyfield axes: north, east, up
    velocity = obs._R_latlon @ v
    distance = np.linalg.norm(relative)
    unit = relative / distance
    rr = float(np.dot(unit, velocity))
    angular = float(np.linalg.norm(np.cross(unit, velocity)) / distance * 180 / math.pi)
    return dict(observerKilometers=obs.itrs_xyz.km.tolist(),
                unitEnu=unit[[1, 0, 2]].tolist(), rangeKilometers=float(distance),
                rangeRateKilometersPerSecond=rr, angularSpeedDegreesPerSecond=angular,
                altitudeDegrees=math.degrees(math.atan2(relative[2], np.hypot(*relative[:2]))),
                azimuthDegrees=math.degrees(math.atan2(relative[1], relative[0])) % 360)


def make_sat(f):
    epoch = (datetime.fromisoformat(f['EPOCH']).replace(tzinfo=UTC) - EPOCH0).total_seconds() / 86400
    sat = Satrec()
    sat.sgp4init(WGS72, 'a', int(f['NORAD_CAT_ID']), epoch, f['BSTAR'],
                 f['MEAN_MOTION_DOT'] * math.pi / 1036800,
                 f['MEAN_MOTION_DDOT'] * math.pi / 1492992000,
                 f['ECCENTRICITY'], math.radians(f['ARG_OF_PERICENTER']),
                 math.radians(f['INCLINATION']), math.radians(f['MEAN_ANOMALY']),
                 f['MEAN_MOTION'] * math.pi / 720, math.radians(f['RA_OF_ASC_NODE']))
    assert sat.operationmode == 'a'
    return sat


def state(sat, utc):
    jd, fraction = split(utc)
    error, r, v = sat.sgp4(jd, fraction)
    assert error == 0, error
    return r, v


def event_pack(sat, site, name):
    start = datetime(2026, 10, 19, 12, tzinfo=UTC)
    duration = 60 * 3600
    def altitude(seconds):
        utc = start + timedelta(seconds=float(seconds))
        r, v = state(sat, utc)
        p, q = fixed(r, v, utc)
        return topocentric(p, q / 86400, site)['altitudeDegrees']
    def root(lo, hi, threshold):
        a = altitude(lo) - threshold
        while hi - lo > .0005:
            mid = (lo + hi) / 2
            b = altitude(mid) - threshold
            if (a >= 0) == (b >= 0):
                lo, a = mid, b
            else:
                hi = mid
        return dict(kind='rise' if altitude(hi) > altitude(lo) else 'set', bracketSeconds=[lo, hi], bracketAltitudeDegrees=[altitude(lo), altitude(hi)],
                    seconds=(lo + hi) / 2, utc=(start + timedelta(seconds=(lo + hi) / 2)).isoformat())
    def peak(lo, hi):
        while hi - lo > .0005:
            a, b = (2 * lo + hi) / 3, (lo + 2 * hi) / 3
            if altitude(a) < altitude(b): lo = a
            else: hi = b
        t = (lo + hi) / 2
        return dict(bracketSeconds=[lo, hi], seconds=t,
                    utc=(start + timedelta(seconds=t)).isoformat(), altitudeDegrees=altitude(t))
    step = 60 if name.startswith('LES') else 30
    samples = [(t, altitude(t)) for t in range(0, duration + 1, step)]
    crossings = [root(a[0], b[0], 0) for a, b in zip(samples, samples[1:]) if (a[1] >= 0) != (b[1] >= 0)]
    peaks = [peak(samples[i-1][0], samples[i+1][0]) for i in range(1, len(samples)-1)
             if samples[i][1] > samples[i-1][1] and samples[i][1] >= samples[i+1][1] and samples[i][1] > 0]
    result = dict(name=name, observer=site, windowStartUtc=start.isoformat(), windowEndUtc=(start+timedelta(seconds=duration)).isoformat(),
                  thresholdDegrees=0, scanStepSeconds=step, startAltitudeDegrees=samples[0][1], endAltitudeDegrees=samples[-1][1],
                  crossings=crossings, peaks=peaks)
    if peaks and not name.startswith('LES'):
        chosen = max(peaks, key=lambda p:p['altitudeDegrees'])
        threshold = chosen['altitudeDegrees'] - .00001
        t = chosen['seconds']
        result['nearTangent'] = dict(thresholdDegrees=threshold, peak=chosen,
              rise=root(t-10, t, threshold), set=root(t, t+10, threshold),
              qualification='Synthetic threshold near a retained orbit maximum; separate altitude/bracket evidence, no uniform timestamp claim.')
    return result


def main():
    assert (skyfield.__version__, sgp4.__version__, np.__version__, jplephem.__version__) == ('1.55', '2.27', '2.5.4', '2.24')
    parser = argparse.ArgumentParser()
    parser.add_argument('--retained-finals', type=Path)
    parser.add_argument('--verify', action='store_true', help='Verify retained file hashes without regenerating expectations')
    args = parser.parse_args()
    if args.verify:
        manifest = json.loads((ROOT / 'docs/validation/orbital-prediction-v1/coordinate-source-manifest.json').read_text())
        for item in manifest['files']:
            data = (ROOT / item['path']).read_bytes()
            assert len(data) == item['bytes'] and sha(data) == item['sha256'], item['path']
        print('Verified', len(manifest['files']), 'coordinate/time artifacts; no expectations regenerated')
        return
    if args.retained_finals:
        data = args.retained_finals.read_bytes()
        assert sha(data) == FULL_HASH
        lines = data.splitlines(keepends=True)
        for name, lower, upper in [('final-2004', 53100, 53103), ('leap-2016', 57752, 57756), ('predicted-2026', 61331, 61338)]:
            subset = b''.join(l.rstrip(b' \r\n') + b'\n' for l in lines if lower <= float(l[7:15]) <= upper)
            (TIME / (name + '.finals2000A')).write_bytes(subset)
    global HISTORICAL, MODERN
    HISTORICAL = eop_rows(TIME / 'final-2004.finals2000A')
    MODERN = eop_rows(TIME / 'predicted-2026.finals2000A')
    appendix_jd = julian_date(2004, 4, 6, 7, 51, 28.386) - .439961 / 86400
    cases = []
    r0, v0 = [5094.18016210, 6127.64465950, 6380.34453270], [-4.746131487, .785818041, 5.531931288]
    p, q = TEME_to_ITRF(appendix_jd, np.array(r0), np.array(v0)*86400,
                        math.radians(-.140682 / 3600), math.radians(.333309 / 3600))
    cases.append(dict(name='appendix-c-literal', ut1Day=float(appendix_jd), ut1Fraction=0,
                     xp=-.140682, yp=.333309, temePosition=r0, temeVelocity=v0,
                     fixedPosition=p.tolist(), fixedVelocity=(q/86400).tolist()))
    gp = json.loads((ROOT / 'docs/validation/orbital-reference-v1/gp-source-facts.json').read_text())
    sites = [[47.6062,-122.3321,50], [35.347,-113.878,0], [0,0,0], [90,180,1000], [-90,-180,-250], [0,180,2000]]
    for i, fact in enumerate(gp):
        utc = datetime(2026, 10, 20, 13, 4, 21, 123456, tzinfo=UTC)
        r, v = state(make_sat(fact), utc)
        p, q = fixed(r, v, utc)
        dut1, xp, yp = eop(utc)
        day, fraction = split(utc)
        cases.append(dict(name='mode-a-' + str(fact['NORAD_CAT_ID']), utc=utc.isoformat(), ut1Day=day, ut1Fraction=fraction+dut1/86400,
                         dut1=dut1, xp=xp, yp=yp, temePosition=list(r), temeVelocity=list(v),
                         fixedPosition=p.tolist(), fixedVelocity=(q/86400).tolist(),
                         observer=sites[i], topocentric=topocentric(p, q/86400, sites[i])))
    # Pure observer cases built in Skyfield NEU, including wrap and the exactly vertical vector.
    observer_cases=[]
    for i, (name, site, enu, velocity) in enumerate([
        ('equator-east', [0,0,0], [1000,0,0], [-1,2,3]),
        ('north-pole', [90,180,1000], [1,2,1000], [.1,.2,.3]),
        ('south-pole', [-90,-180,-250], [-1,2,1000], [.1,-.2,.3]),
        ('antimeridian-east', [12,180,2000], [2,3,1000], [.5,.4,-.2]),
        ('antimeridian-west', [12,-180,-500], [2,3,1000], [.5,.4,-.2]),
        ('wrap-before', [0,0,0], [-.00001,1000,0], [7,0,0]),
        ('wrap-after', [0,0,0], [.00001,1000,0], [7,0,0]),
        ('zenith', [0,0,0], [0,0,500], [7,0,1]),
        ('near-zenith', [35.347,-113.878,0], [.00001,0,500], [7,0,1]),
    ]):
        obs=observer(site)
        p=obs.itrs_xyz.km + obs._R_latlon.T @ np.array(enu)[[1,0,2]]
        q=obs._R_latlon.T @ np.array(velocity)[[1,0,2]]
        observer_cases.append(dict(name=name, observer=site, fixedPosition=p.tolist(), fixedVelocity=q.tolist(), topocentric=topocentric(p,q,site)))
    write(COORD / 'reference-states.json', dict(convention='teme-gmst82-ut1-pm-no-sp-nominal-rate-v1', states=cases, observerStates=observer_cases))
    iss=make_sat(next(f for f in gp if f['NORAD_CAT_ID']==25544))
    les=make_sat(next(f for f in gp if f['NORAD_CAT_ID']==2866))
    write(COORD / 'pass-references.json', dict(operationMode='a', gravity='WGS72', solarAssertions='none',
          caveat='Pinned independent event pack for later root qualification; scan is not a general completeness proof.',
          cases=[event_pack(iss, sites[0], 'ISS-Seattle'), event_pack(iss, sites[1], 'ISS-HVO'), event_pack(les, sites[1], 'LES-5-HVO')]))
    sources=[]
    base=Path(skyfield.__file__).parent
    for name in ['tests/test_earth_satellites.py','tests/test_satellite_events.py','sgp4lib.py','toposlib.py','timelib.py']:
        b=(base/name).read_bytes()
        sources.append(dict(package='Skyfield', version='1.55', path=name, bytes=len(b), sha256=sha(b), license='MIT', source='https://github.com/skyfielders/python-skyfield/tree/1.55/skyfield/' + name))
    sgp4_base=Path(sgp4.__file__).parent
    for name in ['api.py', 'wrapper.py', 'omm.py']:
        b=(sgp4_base/name).read_bytes()
        sources.append(dict(package='python-sgp4', version='2.27', path=name, bytes=len(b), sha256=sha(b), license='MIT',
                            source='https://github.com/brandon-rhodes/python-sgp4/tree/092d206d6e6556a3cbbbe86f6ba4293c1f4ec646/sgp4/' + name))
    manifest=dict(schema='coordinate-time-reference-v1', generator='coordinate-reference-generator.py',
        versions=dict(skyfield='1.55',sgp4='2.27',numpy='2.5.4',jplephem='2.24'),
        conventions=dict(rotation='GMST82 analytic derivative per UT1 second, nominal UTC rate, no DUT1-slope/LOD correction',
                         polar='Rx(yp) Ry(xp), zero polar derivative, TIO s-prime zero',observer='WGS84 geodetic, east-positive, ellipsoidal height',refraction='none'),
        eopSource=dict(url='https://maia.usno.navy.mil/ser7/finals2000A.all',sha256=FULL_HASH,bytes=3769212,retrievedUtc='2026-10-19T17:40:05.574120Z',
                       transformation='Select lines by inclusive MJD bounds, trim trailing ASCII spaces and normalize line endings to LF; preserve all numeric columns. B final, A P predicted; no I-only final promotion.',
                       terms='IERS scientific EOP values distributed by USNO; attribution retained. No bespoke license grant in raw input; institutional redistribution approval is not inferred.'),
        leapSource=dict(url='https://hpiers.obspm.fr/iers/bul/bulc/ntp/leap-seconds.list',retrievedUtc='2026-10-19T22:11:00Z',retrievalPrecision='minute',
                        terms='Raw file explicitly declares public domain.', coverageStartUtc='1972-01-01T00:00:00Z',expiresUtc='2027-06-28T00:00:00Z',
                        approval='Authoritative bounded fixture; no production source/clock/physical accuracy approval or updater introduced.'),
        gpSource=dict(path='docs/validation/orbital-reference-v1/gp-source-facts.json', sha256=sha((ROOT / 'docs/validation/orbital-reference-v1/gp-source-facts.json').read_bytes())),
        softwareSources=sources, files=[])
    for path in sorted([*TIME.iterdir(),*COORD.iterdir(), Path(__file__).resolve(), *Path(__file__).resolve().parent.glob('coordinate-*-LICENSE.txt')]):
        if path.is_file():
            b=path.read_bytes();manifest['files'].append(dict(path=str(path.relative_to(ROOT)),bytes=len(b),sha256=sha(b)))
    write(ROOT / 'docs/validation/orbital-prediction-v1/coordinate-source-manifest.json',manifest)

if __name__ == '__main__':
    main()
