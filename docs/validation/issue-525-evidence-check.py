"""Validate #525 v2 smoke/measurement evidence, including mandatory non-vacuous pinned query rows."""
import argparse
import copy
import hashlib
import json
import math
from pathlib import Path

PINS_SHA256 = 'acb6f7600850307aac42a5b5fe021885f4b26c4507f1f4dfed6f333e806c30f6'
FRAME_BYTES = 3552 * 3552 * 3
PEAK_LIMIT = 2 * 1024 ** 3


def require(condition, message):
    if not condition:
        raise ValueError(message)


def number(value, name):
    require(isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and value >= 0,
            name + ' must be a finite nonnegative number')


def measurement(value, route=None, checksum=None):
    require(isinstance(value, dict), 'measurement missing')
    if route is not None:
        require(value['route'] == route, 'measurement route differs')
    require(value['warmups'] == 5 and value['repetitions'] >= 30 and value['distinctResults'] > 0,
            'insufficient repetitions or empty results')
    for key in ['firstOperationMilliseconds', 'medianMilliseconds', 'p95Milliseconds', 'maximumMilliseconds',
                'cpuMillisecondsPerOperation', 'verificationCpuMillisecondsPerOperation', 'allocatedBytesPerOperation',
                'operationsPerSecond']:
        number(value[key], key)
    require(value['medianMilliseconds'] <= value['p95Milliseconds'] <= value['maximumMilliseconds'],
            'latency percentile order differs')
    for key in ['memoryBefore', 'memoryAfter']:
        memory = value[key]
        for counter in ['workingSetBytes', 'peakWorkingSetBytes', 'managedBytes']:
            number(memory[counter], key + '.' + counter)
        require(0 < memory['workingSetBytes'] <= memory['peakWorkingSetBytes'] <= PEAK_LIMIT,
                'process memory exceeds the declared bound or is absent')
    require(len(value['resultChecksumSha256']) == 64, 'result checksum absent')
    if checksum is not None:
        require(value['resultChecksumSha256'] == checksum, 'measurement output differs from pinned output')


def validate(document, pins, revision=None, trial=None):
    require(document['schema'] == 'hvo-catalog-performance-v2', 'old or unknown evidence schema')
    if revision is not None:
        require(document['revision'] == revision, 'revision differs')
    if trial is not None:
        require(document['trial'] == trial, 'trial differs')
    result = document['results']
    arm = result['arm']
    require(arm in ['stars-only', 'composed'], 'unknown arm')
    require(document['workload'] == 'dso-' + arm, 'workload differs from arm')
    require(result['measurementContract'] == 'hvo-issue525-measurement-v2', 'CPU/query contract missing')
    require(result['cpu']['counter'] == 'Process.TotalProcessorTime', 'CPU counter missing')
    require(int(result['cpu']['ticksPerSecond']) > 0, 'CPU granularity evidence missing')
    startup = result['startup']
    require(startup['process'] == 'fresh', 'startup not fresh-process')
    for counter in ['latencyMilliseconds', 'cpuMilliseconds', 'allocatedBytes', 'workingSetBytes',
                    'peakWorkingSetBytes', 'managedHeapBytes', 'databaseLengthBytes', 'packageBytes', 'sqliteRowsLoaded']:
        number(startup[counter], 'startup.' + counter)
    require(0 < startup['workingSetBytes'] <= startup['peakWorkingSetBytes'] <= PEAK_LIMIT, 'startup memory bound')
    for counter in ['storageReadBytes', 'logicalReadBytes', 'readSystemCalls']:
        require(startup[counter] is not None, 'Linux startup I/O counter missing')
        number(startup[counter], counter)
    queries = result['queries']
    measurement(queries['starAllSky'])
    measurement(queries['starBoundedCap'])
    buffers = result['fullResolutionBuffers']
    require(buffers['borrowedBaseCount'] == buffers['ownedOutputCountPerOperation'] == 1, 'full-frame ownership differs')
    require(buffers['borrowedBaseBytes'] == buffers['ownedOutputBytes'] == FRAME_BYTES, 'full-resolution output differs')
    require(buffers['maximumNativeTileBytes'] == 4 * 1024 ** 2 and buffers['fullFrameLayerBuffers'] == 0,
            'native raster bound differs')
    require(buffers['baseUnchanged'] is True and len(buffers['borrowedBaseChecksumSha256']) == 64, 'immutable base proof missing')
    require(buffers['processPeakLimitBytes'] == PEAK_LIMIT, 'process bound differs')
    require(result['geometry']['widthPixels'] == result['geometry']['heightPixels'] == 3552, 'W6 dimensions differ')
    scenes = result['scenes']
    require([scene['instant'] for scene in scenes] == ['m31-high', 'm45-high', 'm42-high'], 'canonical scenes missing')
    for scene in scenes:
        measurement(scene['starScene'])
        stages = [scene['projection']]
        if arm == 'composed':
            stages.append(scene['stress'])
        for stage in stages:
            measurement(stage['projectedScene'])
            require(0 < stage['sceneBytes'] <= 4 * 1024 ** 2, 'scene payload bound')
            require(len(stage['sceneIdentitySha256']) == 64, 'scene identity absent')
            if arm == 'composed':
                measurement(stage['deepSkyScene'])
                measurement(stage['presentation']['layer'])
                measurement(stage['presentation']['composite'])
                require(stage['deepSky']['objects'] > 0, 'empty deep-sky scene')
                require(stage['presentation']['payloadBytes'] > 0, 'empty layer payload')
                bounds = stage['presentation']['layerBounds']
                require(0 < bounds['primitives'] <= bounds['maximumPrimitives'] == 4096, 'layer primitive bound')
                require(0 <= bounds['geometryWork'] <= bounds['maximumGeometryWork'] == 4 * 1024 ** 2,
                        'layer geometry bound')
    dso = queries['deepSkyWorkloads']
    if arm == 'stars-only':
        require(dso is None and queries['deepSkyRegion'] is None, 'stars-only arm invents DSO workloads')
        return
    require(result['databaseSha256'].lower() == pins['databaseSha256'].lower(), 'composed database differs')
    measurement(result['baseComposite'])
    region = queries['deepSkyRegion']
    measurement(region['measurement'])
    require(region['pointings'] == 50 and region['totalMatches'] > 0, 'spatial sweep missing')
    require(isinstance(dso, dict) and dso['pinsSha256'].lower() == PINS_SHA256, 'pinned DSO workload inventory missing')
    require(dso['baselineSetup']['aliasRows'] > 0 and dso['baselineSetup']['outlineRows'] > 0, 'baseline setup absent')
    expected = {pin['name']: pin for pin in pins['workloads']}
    rows = dso['workloads']
    require(len(rows) == len(expected) == 15, 'required DSO workload count differs')
    require({row['criteria']['name'] for row in rows} == set(expected), 'required DSO workload names differ')
    require({row['criteria']['kind'] for row in rows} == {'spatial', 'type', 'brightness', 'alias', 'outline'},
            'required query family missing')
    for row in rows:
        criteria = row['criteria']
        pin = expected[criteria['name']]
        for key, value in pin.items():
            require(criteria[key] == value, 'query pin differs: ' + criteria['name'] + '.' + key)
        require(criteria['expectedMatchCount'] > 0 and criteria['expectedIds'], 'vacuous query result')
        if criteria['kind'] == 'outline':
            require(criteria['expectedPointCount'] > 0, 'vacuous outline')
        digest = hashlib.sha256((':' + criteria['expectedContentSha256']).encode()).hexdigest().upper()
        measurement(row['measurement'], criteria['name'], digest)
        baseline = row['baseline']
        require(baseline['kind'] == 'within-candidate-linear-snapshot-scan', 'baseline semantics missing')
        require(baseline['ratioToHyg'] is None and baseline['equivalentPinnedOutput'] is True, 'incomparable baseline ratio')
        measurement(baseline['measurement'], criteria['name'] + '-linear-baseline', digest)


def self_test(document, pins):
    validate(document, pins)
    changes = [
        ('old-schema', lambda data: data.update(schema='hvo-catalog-performance-v1')),
        ('missing-verification-CPU', lambda data: data['results']['queries']['starAllSky'].pop('verificationCpuMillisecondsPerOperation')),
        ('excess-process-peak', lambda data: data['results']['queries']['starAllSky']['memoryAfter'].update(peakWorkingSetBytes=PEAK_LIMIT + 1)),
        ('wrong-frame-bytes', lambda data: data['results']['fullResolutionBuffers'].update(ownedOutputBytes=0)),
    ]
    if document['results']['arm'] == 'composed':
        changes.extend([
            ('missing-inventory', lambda data: data['results']['queries'].update(deepSkyWorkloads=None)),
            ('missing-row', lambda data: data['results']['queries']['deepSkyWorkloads']['workloads'].pop()),
            ('empty-pin', lambda data: data['results']['queries']['deepSkyWorkloads']['workloads'][0]['criteria'].update(expectedIds=[])),
            ('changed-checksum', lambda data: data['results']['queries']['deepSkyWorkloads']['workloads'][0]['measurement'].update(resultChecksumSha256='0' * 64)),
            ('incomparable-ratio', lambda data: data['results']['queries']['deepSkyWorkloads']['workloads'][0]['baseline'].update(ratioToHyg=1)),
        ])
    for name, change in changes:
        mutated = copy.deepcopy(document)
        change(mutated)
        try:
            validate(mutated, pins)
        except (ValueError, KeyError, TypeError):
            print('REJECTED ' + name)
        else:
            raise ValueError('false pass for ' + name)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('evidence', type=Path)
    parser.add_argument('--pins', type=Path, required=True)
    parser.add_argument('--revision')
    parser.add_argument('--trial')
    parser.add_argument('--self-test', action='store_true')
    parser.add_argument('--campaign', choices=['smoke', 'measure'])
    args = parser.parse_args()
    pins_bytes = args.pins.read_bytes()
    require(hashlib.sha256(pins_bytes).hexdigest() == PINS_SHA256, 'pins file hash differs')
    pins = json.loads(pins_bytes)
    if args.campaign:
        documents = [json.loads(path.read_bytes()) for path in args.evidence.glob('catalog-dso-*.json')]
        documents.sort(key=lambda data: data['trial'])
        expected_arms = ['stars-only', 'composed'] if args.campaign == 'smoke' else ['stars-only', 'composed', 'composed', 'stars-only'] * 3
        require([data['results']['arm'] for data in documents] == expected_arms, 'campaign arm order or count differs')
        reference = {}
        for index, document in enumerate(documents, start=1):
            validate(document, pins, args.revision, f'{args.campaign}-t{index:02d}')
            result = document['results']
            # Star queries and star scenes are comparable across the two packages whose star tables are identical.
            values = {name: result['queries'][name]['resultChecksumSha256'] for name in ['starAllSky', 'starBoundedCap']}
            values.update({scene['instant']: scene['starScene']['resultChecksumSha256'] for scene in result['scenes']})
            for key, value in values.items():
                require(reference.setdefault(key, value) == value, 'paired star output differs: ' + key)
            for scene in result['scenes']:
                for stage in ['projection', 'stress'] if result['arm'] == 'composed' else ['projection']:
                    key = result['arm'] + '.' + scene['instant'] + '.' + stage
                    identity = scene[stage]['sceneIdentitySha256']
                    require(reference.setdefault(key, identity) == identity, 'within-arm scene identity differs: ' + key)
        print(f'PASS campaign {args.campaign} {len(documents)} trials; paired star outputs and within-arm identities agree')
        return
    document = json.loads(args.evidence.read_bytes())
    validate(document, pins, args.revision, args.trial)
    if args.self_test:
        self_test(document, pins)
    print('PASS ' + document['workload'] + ' ' + document['trial'])


if __name__ == '__main__':
    main()
