#!/usr/bin/env python3
"""Recompute #1117 raw/display probes and historical base-pixel diagnostics.

Python standard library only. Reads retained inputs and writes a separate output
directory; never rewrites capture evidence. This is a bounded qualification
diagnostic, not a detector or an independent measured-star association.
"""

import argparse
import hashlib
import json
import math
from pathlib import Path
import statistics
import struct
import zlib


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, allow_nan=False) + '\n')


def chunk(kind, payload):
    return struct.pack('>I', len(payload)) + kind + payload + struct.pack(
        '>I', zlib.crc32(kind + payload))


def encode_png(width, height, pixels):
    require(len(pixels) == width * height, 'Mono8 output length mismatch')
    rows = b''.join(b'\0' + pixels[y * width:(y + 1) * width]
                    for y in range(height))
    return (b'\x89PNG\r\n\x1a\n' +
            chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 0, 0, 0, 0)) +
            chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b''))


def decode_png(png, expected_width, expected_height):
    """Decode retained 8-bit grayscale/RGB/RGBA PNGs and verify their pixels."""
    require(png[:8] == b'\x89PNG\r\n\x1a\n', 'Invalid PNG signature')
    offset, compressed, header = 8, bytearray(), None
    while offset < len(png):
        length = struct.unpack_from('>I', png, offset)[0]
        kind = png[offset + 4:offset + 8]
        payload = png[offset + 8:offset + 8 + length]
        crc = struct.unpack_from('>I', png, offset + 8 + length)[0]
        require(zlib.crc32(kind + payload) == crc, 'Invalid PNG chunk CRC')
        offset += length + 12
        if kind == b'IHDR':
            header = struct.unpack('>IIBBBBB', payload)
        elif kind == b'IDAT':
            compressed.extend(payload)
        elif kind == b'IEND':
            break
    require(header is not None, 'Missing PNG header')
    width, height, depth, color, compression, filtering, interlace = header
    require((width, height) == (expected_width, expected_height), 'PNG dimensions mismatch')
    require(depth == 8 and color in (0, 2, 6) and
            (compression, filtering, interlace) == (0, 0, 0), 'Unsupported PNG layout')
    channels = {0: 1, 2: 3, 6: 4}[color]
    stride = width * channels
    data = zlib.decompress(compressed)
    require(len(data) == (stride + 1) * height, 'PNG payload length mismatch')
    result, previous = bytearray(), bytearray(stride)
    for y in range(height):
        start = y * (stride + 1)
        method = data[start]
        require(method in range(5), 'Unsupported PNG filter')
        row = bytearray(data[start + 1:start + 1 + stride])
        for i in range(stride):
            left = row[i - channels] if i >= channels else 0
            up = previous[i]
            diagonal = previous[i - channels] if i >= channels else 0
            prediction = left + up - diagonal
            distances = (abs(prediction - left), abs(prediction - up), abs(prediction - diagonal))
            paeth = (left, up, diagonal)[distances.index(min(distances))]
            row[i] = (row[i] + (0, left, up, (left + up) // 2, paeth)[method]) % 256
        if channels == 1:
            result.extend(row)
        else:
            for i in range(0, stride, channels):
                require(row[i] == row[i + 1] == row[i + 2], 'PNG is not grayscale')
                require(channels == 3 or row[i + 3] == 255, 'PNG contains nonopaque pixels')
                result.append(row[i])
        previous = row
    return bytes(result)


def sample_star(pixels, width, height, stride, x, y):
    core, annulus = [], []
    for py in range(max(0, math.floor(y - 9)), min(height, math.ceil(y + 9) + 1)):
        for px in range(max(0, math.floor(x - 9)), min(width, math.ceil(x + 9) + 1)):
            distance2 = (px + 0.5 - x) ** 2 + (py + 0.5 - y) ** 2
            value = pixels[py * stride + px]
            if distance2 <= 9:
                core.append((px + 0.5, py + 0.5, value))
            elif 36 <= distance2 <= 81:
                annulus.append(value)
    require(core and annulus, 'Star sample has insufficient on-frame support')
    background = statistics.median(annulus)
    peak = max(value for _, _, value in core)
    total = sum(max(0, value - background) for _, _, value in core)
    centroid = None
    if total > 0:
        cx = sum(px * max(0, value - background) for px, _, value in core) / total
        cy = sum(py * max(0, value - background) for _, py, value in core) / total
        centroid = math.hypot(cx - x, cy - y)
    return {'peak': peak, 'background': background, 'contrast': peak - background,
            'centroidDistance': centroid}


def measure_case(evidence, output, name):
    report = json.loads((evidence / f'{name}.json').read_text())
    layout = report['layout']
    width, height, stride = layout['width'], layout['height'], layout['strideBytes']
    require(layout['pixelFormat'] == 'Mono16' and layout['byteOrder'] == 'LittleEndian',
            'Unsupported raw probe layout')
    raw = (evidence / f'{name}.raw').read_bytes()
    display = (evidence / f'{name}.mono8').read_bytes()
    require(len(raw) == layout['byteLength'] == stride * height, 'Raw payload length mismatch')
    require(sha(raw) == report['rawSha256'], 'Raw checksum mismatch')
    require(len(display) == width * height and sha(display) == report['displaySha256'],
            'Display checksum mismatch')
    source_png = (evidence / f'{name}.png').read_bytes()
    require(decode_png(source_png, width, height) == display,
            'Retained PNG does not encode the recorded Mono8 display pixels')
    generated_png = encode_png(width, height, display)
    require(decode_png(generated_png, width, height) == display, 'PNG round-trip mismatch')
    (output / f'{name}.png').write_bytes(generated_png)
    pixels = struct.unpack('<' + 'H' * (len(raw) // 2), raw)
    stars = sorted(report['stars'], key=lambda star: star['magnitude'])
    measurements = []
    for star in stars:
        x, y = star['pixelX'], star['pixelY']
        local = sample_star(pixels, width, height, stride // 2, x, y)
        displayed = sample_star(display, width, height, width, x, y)
        measurements.append({'id': star['id'], 'name': star['displayName'],
                             'magnitude': star['magnitude'], 'x': x, 'y': y,
                             'rawPeak': local['peak'], 'rawBackground': local['background'],
                             'rawContrast': local['contrast'], 'displayPeak': displayed['peak'],
                             'centroidDistance': local['centroidDistance']})
    bright = measurements[:15]
    close = [{'left': left['id'], 'right': right['id']}
             for i, left in enumerate(bright) for right in bright[i + 1:]
             if math.hypot(left['x'] - right['x'], left['y'] - right['y']) < 1]
    return {'case': name, 'starCount': len(stars), 'rawSha256': sha(raw),
            'displaySha256': sha(display), 'sourcePngSha256': sha(source_png),
            'encodedPngSha256': sha(generated_png), 'pngDecodedDisplaySha256': sha(display),
            'positiveContrast': sum(item['rawContrast'] > 0 for item in measurements),
            'bright15MaxCentroidError': max((item['centroidDistance'] for item in bright
                                            if item['centroidDistance'] is not None), default=None),
            'brightCatalogEntriesSharingSubpixelSpot': close,
            'brightest': bright, 'allStars': measurements}


def measure_historical(historical):
    detail = json.loads((historical / 'capture42-detail.json').read_text())
    view = json.loads((historical / 'capture42-layer-view.json').read_text())
    artifact = next(item for item in detail['artifacts'] if item['artifactId'] == view['baseArtifactId'])
    pixels = (historical / 'capture42-base.pixels').read_bytes()
    require(sha(pixels) == artifact['checksumSha256'] and len(pixels) == artifact['byteLength'],
            'Historical base identity mismatch')
    scene_artifact = next(item for item in detail['artifacts'] if item['variant'] == 'projected-scene-v1')
    scene_bytes = (historical / f"capture42-artifact-{scene_artifact['artifactId']}.json").read_bytes()
    require(sha(scene_bytes) == scene_artifact['checksumSha256'], 'Historical scene identity mismatch')
    scene = json.loads(scene_bytes)
    width, height = view['widthPixels'], view['heightPixels']
    require(len(pixels) == width * height, 'Historical base is not tightly packed Mono8')
    stars = sorted((item for item in scene['objects'] if item['kind'] == 'Star'),
                   key=lambda star: star['magnitude'])
    measurements = []
    for star in stars[:20]:
        x, y = star['pixel']['x'], star['pixel']['y']
        sample = sample_star(pixels, width, height, width, x, y)
        measurements.append({'id': star['id'], 'name': star['displayName'],
                             'magnitude': star['magnitude'], 'pixel': star['pixel'],
                             'peak': sample['peak'], 'localMedian': sample['background']})
    return {'captureSequence': detail['captureSequence'], 'captureId': detail['captureId'],
            'baseArtifactId': artifact['artifactId'], 'baseSha256': sha(pixels),
            'sceneSha256': sha(scene_bytes), 'effectiveUtc': scene['effectiveUtc'],
            'catalog': scene['catalog'], 'starCount': len(stars), 'brightest': measurements}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence-dir', type=Path, required=True)
    parser.add_argument('--historical-dir', type=Path, required=True)
    parser.add_argument('--output-dir', type=Path, required=True)
    args = parser.parse_args()
    require(args.output_dir.resolve() not in (args.evidence_dir.resolve(), args.historical_dir.resolve()),
            'Output must be separate from immutable evidence inputs')
    args.output_dir.mkdir(parents=True, exist_ok=True)
    results = [measure_case(args.evidence_dir, args.output_dir, name)
               for name in ('day-1s', 'night-1s', 'night-20s')]
    write_json(args.output_dir / 'pixel-measurements.json', results)
    write_json(args.output_dir / 'historical-pixels.json', measure_historical(args.historical_dir))
    for result in results:
        print(result['case'], 'catalog entries:', result['starCount'],
              'positive contrast:', result['positiveContrast'],
              'bright15 maximum centroid error:', result['bright15MaxCentroidError'])


if __name__ == '__main__':
    main()
