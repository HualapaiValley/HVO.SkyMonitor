#!/usr/bin/env python3
"""Offline AFSPC-a dense oracle; never calls HVO or the network. See sampling-reference.md."""
import hashlib
import importlib.util
import json
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import jplephem
import numpy as np
import sgp4
import skyfield
from skyfield.sgp4lib import TEME_to_ITRF

ROOT = Path(__file__).resolve().parents[3]
DOC = ROOT / 'docs/validation/orbital-prediction-v1'
OUT = ROOT / 'tests/fixtures/orbital-prediction-v1/sampling'
UTC = timezone.utc


def digest(path):
    b = path.read_bytes()
    return dict(path=str(path.relative_to(ROOT)), bytes=len(b), sha256=hashlib.sha256(b).hexdigest().upper())


def main():
    assert (skyfield.__version__, sgp4.__version__, np.__version__, jplephem.__version__) == ('1.55', '2.27', '2.5.4', '2.24')
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location('coordinate_oracle', DOC / 'coordinate-reference-generator.py')
    oracle = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(oracle)
    oracle.MODERN = oracle.eop_rows(oracle.TIME / 'predicted-2026.finals2000A')
    oracle.HISTORICAL = oracle.eop_rows(oracle.TIME / 'final-2004.finals2000A')
    facts = json.loads((ROOT / 'docs/validation/orbital-reference-v1/gp-source-facts.json').read_text())
    plans = []
    # Frozen calibration/held-out split: before seeing the HVO sampler's outputs.
    # Nonzero EOP at a common UTC; sites include Seattle, HVO, antimeridian and poles.
    sites = [[47.6062, -122.3321, 50], [35.347, -113.878, 0], [0, 180, -1000], [90, 0, 10000]]
    for i, fact in enumerate(facts):
        plans.append((f'gp-{fact["NORAD_CAT_ID"]}-eop', fact, datetime(2026,10,20,13,3,32,287674,tzinfo=UTC), sites[i % 4], [5,20,100][i % 3], False, i % 2 == 1))
        epoch = datetime.fromisoformat(fact['EPOCH']).replace(tzinfo=UTC)
        for j, age in enumerate([-23.9, 0, 47.9, 167.9]):
            plans.append((f'gp-{fact["NORAD_CAT_ID"]}-age-{age}', fact, epoch + timedelta(hours=age), sites[(i+j) % 4], [0.3,5,20,100][j], True, (i+j) % 2 == 1))
    # Synthetic numerical stress elements, explicitly no asserted physical identity.
    stress = [(0.50001,.89999,0,0), (1.0027,0,0,0), (2,.5,63.4,0), (15.5,.0001,90,.00999), (15.5,.0001,180,-.00999), (1.1,.01,179.999,0)]
    for i, (n,e,inc,bstar) in enumerate(stress):
        fact = dict(facts[2], NORAD_CAT_ID=200001+i, OBJECT_NAME=f'SYNTHETIC NUMERICAL STRESS {i}', MEAN_MOTION=n, ECCENTRICITY=e, INCLINATION=inc, BSTAR=bstar, MEAN_MOTION_DOT=0, MEAN_MOTION_DDOT=0, MEAN_ANOMALY=0)
        epoch = datetime.fromisoformat(fact['EPOCH']).replace(tzinfo=UTC)
        plans.append((f'synthetic-{i}', fact, epoch, sites[i % 4], 20, True, i % 2 == 1))
    plans.append(('ISS-Seattle-fast', facts[2], datetime(2026,10,20,13,3,32,287674,tzinfo=UTC), sites[0], 100, False, True))
    plans.append(('ISS-HVO-near-zenith', facts[2], datetime(2026,10,20,17,58,34,82209,tzinfo=UTC), sites[1], 20, False, True))
    cases = []
    for name, fact, start, site, duration, zero, heldout in plans:
        sat = oracle.make_sat(fact)
        rows = []
        # All fit/validation nodes are multiples of 1/8. These 257 held-out phases
        # are deliberately offset and have a denominator coprime with 8.
        times = sorted(set([0.0, duration, duration/2] + [duration*(i+.371)/257 for i in range(257)]))
        for t in times:
            utc = start + timedelta(seconds=t)
            jd, fraction = oracle.split(utc)
            error,r,v = sat.sgp4(jd, fraction)
            if error:
                rows.append(dict(utc=utc.isoformat().replace('+00:00','Z'), error=error))
                continue
            if zero:
                p,q = TEME_to_ITRF(jd, np.array(r), np.array(v)*86400, 0, 0, fraction)
            else:
                p,q = oracle.fixed(r,v,utc)
            top = oracle.topocentric(p,q/86400,site)
            rows.append(dict(utc=utc.isoformat().replace('+00:00','Z'), error=0,
                             altitude=top['altitudeDegrees'], azimuth=top['azimuthDegrees'],
                             unitEnu=top['unitEnu'], rangeKilometers=top['rangeKilometers']))
        cases.append(dict(name=name, split='held-out' if heldout else 'calibration', facts=fact,
                          observer=site, startUtc=start.isoformat().replace('+00:00','Z'), durationSeconds=duration,
                          nominalZeroEop=zero, samples=rows))
    OUT.mkdir(parents=True, exist_ok=True)
    output = OUT / 'dense-directions.json'
    output.write_text(json.dumps(dict(schema='hvo-independent-sampling-oracle-v1', gravity='WGS72', mode='a',
        phases='257 off-node phases (i+0.371)/257 plus endpoints and midpoint; UTC microsecond quantization retained',
        cases=cases[:36]), separators=(',',':'), allow_nan=False)+'\n')
    extra = OUT / 'fast-directions.json'
    extra.write_text(json.dumps(dict(schema='hvo-independent-sampling-oracle-v1', gravity='WGS72', mode='a', cases=cases[36:]), separators=(',',':'), allow_nan=False)+'\n')
    manifest = dict(schema='hvo-independent-sampling-oracle-manifest-v1', sources=[digest(DOC/'sampling-reference-generator.py'),
        digest(DOC/'coordinate-reference-generator.py'), digest(ROOT/'docs/validation/orbital-reference-v1/gp-source-facts.json'),
        digest(oracle.TIME/'predicted-2026.finals2000A'),digest(oracle.TIME/'leap-seconds.list')], output=digest(output), supplementalOutput=digest(extra),
        software=dict(sgp4=sgp4.__version__,skyfield=skyfield.__version__,numpy=np.__version__,jplephem=jplephem.__version__),
        sourceLicenses='MIT oracle libraries: coordinate-PYTHON-SGP4-LICENSE.txt and coordinate-SKYFIELD-LICENSE.txt; HVO-authored generator; scientific facts credited by existing source manifests.',
        qualification='Dense independent numerical checks are empirical evidence, not an all-times or all-input interval proof. Synthetic stress cases have no physical object assertion.')
    (DOC/'sampling-source-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    print(len(cases),'cases including supplemental fast passes;',sum(len(c['samples']) for c in cases),'off-node/end/midpoint rows;',digest(output))


if __name__ == '__main__':
    main()
