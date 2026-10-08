"""Record fixed #525 query results directly from the approved read-only composed SQLite snapshot.

This is a pin recipe, not a benchmark. It never rewrites an existing output. Spatial membership is an independent
full scan over catalogue directions and maximum extent/outline reach; outline checksums encode exact IEEE-754 bits.
"""
import hashlib
import json
import math
import sqlite3
import struct
import sys
from pathlib import Path

APPROVED_SHA256 = 'eea1181ffae1dca2935aeed2c7790305b28045061f38ca6dda5eaf19694a8f1f'


def checksum(text):
    return hashlib.sha256(text.encode()).hexdigest().upper()


def direction(ra, dec):
    ra, dec = math.radians(ra * 15), math.radians(dec)
    return math.cos(dec) * math.cos(ra), math.cos(dec) * math.sin(ra), math.sin(dec)


def separation(a, b):
    return math.degrees(math.acos(max(-1, min(1, sum(x * y for x, y in zip(a, b))))))


def main():
    if len(sys.argv) != 3:
        sys.exit('usage: issue-525-query-pins.py APPROVED_DATABASE NEW_OUTPUT_JSON')
    source, output = map(Path, sys.argv[1:])
    data = source.read_bytes()
    if hashlib.sha256(data).hexdigest() != APPROVED_SHA256:
        sys.exit('database is not the approved composed snapshot')
    connection = sqlite3.connect(f'file:{source.resolve()}?mode=ro', uri=True)
    connection.row_factory = sqlite3.Row
    objects = list(connection.execute('SELECT * FROM deep_sky_objects ORDER BY id COLLATE BINARY'))
    points = list(connection.execute('SELECT * FROM deep_sky_outline_points '
                                     'ORDER BY object_id COLLATE BINARY, level, ring, sequence'))
    directions = {row['id']: direction(row['right_ascension_hours'], row['declination_degrees']) for row in objects}
    reach = {row['id']: (row['major_axis_arcminutes'] or 0) / 120 for row in objects}
    for point in points:
        oid = point['object_id']
        reach[oid] = max(reach[oid], separation(directions[oid],
                         direction(point['right_ascension_degrees'] / 15, point['declination_degrees'])))
    workloads = []

    def query(name, kind, criteria, matches):
        ids = [row['id'] for row in matches[:64]]
        assert ids and len(matches) > 0
        workloads.append(dict(name=name, kind=kind, maximumResults=64, **criteria,
                              expectedMatchCount=len(matches), expectedIds=ids,
                              expectedContentSha256=checksum(str(len(matches)) + '\n' + '\n'.join(ids))))

    for oid in ['NGC0224', 'Mel022', 'NGC1976']:
        row = next(row for row in objects if row['id'] == oid)
        ra, dec = row['right_ascension_hours'], row['declination_degrees']
        centre = direction(ra, dec)
        query('spatial-' + oid, 'spatial', dict(rightAscensionHours=ra, declinationDegrees=dec, radiusDegrees=20),
              [row for row in objects if separation(centre, directions[row['id']]) <= 20 + reach[row['id']] + 1e-9])
    for types in [['G'], ['OCl'], ['PN']]:
        query('type-' + '-'.join(types), 'type', dict(objectTypes=types),
              [row for row in objects if row['object_type'] in types])
    for band, column, limit in [('Visual', 'v_magnitude', 8), ('Blue', 'b_magnitude', 10)]:
        for unknown in [False, True]:
            query(f'brightness-{band}-{str(unknown).lower()}', 'brightness',
                  dict(band=band, maximumMagnitude=limit, includeUnknown=unknown),
                  [row for row in objects if (row[column] <= limit if row[column] is not None else unknown)])
    for alias in ['M31', 'M45', 'M102']:
        rows = list(connection.execute('SELECT alias, object_id, kind FROM deep_sky_aliases '
                                       'WHERE alias = ? COLLATE NOCASE ORDER BY object_id COLLATE BINARY, alias COLLATE BINARY',
                                       (alias,)))
        assert rows
        workloads.append(dict(name='alias-' + alias, kind='alias', alias=alias, expectedMatchCount=len(rows),
                              expectedIds=[row['object_id'] for row in rows],
                              expectedContentSha256=checksum('\n'.join('\t'.join(row) for row in rows))))
    for oid in ['Mel022', 'NGC1976']:
        rows = [row for row in points if row['object_id'] == oid]
        assert rows
        text = '\n'.join(f"{oid}:{row['level']}:{row['ring']}:{row['sequence']}:"
                         f"{struct.pack('>d', row['right_ascension_degrees']).hex().upper()}:"
                         f"{struct.pack('>d', row['declination_degrees']).hex().upper()}" for row in rows)
        workloads.append(dict(name='outline-' + oid, kind='outline', objectId=oid,
                              expectedMatchCount=len({row['level'] for row in rows}), expectedIds=[oid],
                              expectedPointCount=len(rows), expectedContentSha256=checksum(text)))
    result = dict(schema='hvo-issue525-query-pins-v1', databaseSha256=APPROVED_SHA256,
                  sourceCommit='36cb178a0f69dba8bfc03a99c10512831edf1c6b',
                  checksumEncoding='query: count\\nIDs; alias: alias\\tID\\tkind; outline: ID:level:ring:point:RA-bits:Dec-bits; join LF without trailing LF',
                  workloads=workloads)
    with output.open('x') as file:
        json.dump(result, file, indent=2)
        file.write('\n')
    print(f'{len(workloads)} nonempty pinned workloads; {hashlib.sha256(output.read_bytes()).hexdigest()}')


if __name__ == '__main__':
    main()
