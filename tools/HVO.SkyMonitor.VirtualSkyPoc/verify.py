#!/usr/bin/env python3
"""Independent numeric checks of retained raw samples, real sequence pixels, gaps and trail selection."""
import argparse
from datetime import datetime, timedelta
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image


def utc(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("day", type=Path)
    parser.add_argument("samples", type=Path)
    parser.add_argument("--details", type=Path)
    args = parser.parse_args()
    samples = json.loads((args.samples / "samples.json").read_text())
    yy, xx = np.mgrid[:640, :640]
    active = (xx + .5 - 320) ** 2 + (yy + .5 - 320) ** 2 <= (640 * .49) ** 2
    for sample in samples:
        raw = (args.samples / (sample["name"] + ".raw")).read_bytes()
        assert hashlib.sha256(raw).hexdigest().upper() == sample["rawSha256"]
        values = np.frombuffer(raw, dtype="<u2").reshape(640, 640)[active]
        assert len(values) == sample["activeSampleCount"]
        assert abs(float(values.mean()) - sample["rawMean"]) < 1e-9
        assert abs(float(np.mean(values >= 4095)) - sample["saturationFraction"]) < 1e-12
        assert values.max() <= 4095
        rgb = np.array(Image.open(args.samples / (sample["name"] + ".png")).convert("RGB"))
        assert hashlib.sha256(rgb.tobytes()).hexdigest().upper() == sample["displayPixelsSha256"]
    panorama_checks = 0
    for name in ["day-policy", "sunset-policy", "night-policy"]:
        map_file = args.samples / (name + "-panorama.json")
        if not map_file.exists():
            continue
        mapping = json.loads(map_file.read_text())
        projection = mapping["projection"]
        assert projection["horizontalFlip"] is True
        assert projection["model"] == "EquidistantFisheye"
        panorama = np.array(Image.open(args.samples / (name + "-panorama.png")).convert("RGB"))
        source = np.array(Image.open(args.samples / (name + ".png")).convert("RGB"), dtype=float)
        assert hashlib.sha256(source.astype(np.uint8).tobytes()).hexdigest().upper() == mapping["sourceDisplayPixelsSha256"]
        assert hashlib.sha256(panorama.tobytes()).hexdigest().upper() == mapping["panoramaPixelsSha256"]
        for px in [0, 180, 359, 540, 719, 900, 1079, 1260, 1439]:
            for py in [1, 60, 180, 300]:
                azimuth = np.deg2rad(360*(px+.5)/1440)
                radius = projection["focalLengthXPixels"] * np.deg2rad(90*(py+.5)/360)
                x = projection["principalPointX"] - radius*np.sin(azimuth) - .5
                y = projection["principalPointY"] - radius*np.cos(azimuth) - .5
                xi, yi = int(np.floor(x)), int(np.floor(y)); dx, dy = x-xi, y-yi
                expected = source[yi,xi]*(1-dx)*(1-dy)+source[yi,xi+1]*dx*(1-dy)+source[yi+1,xi]*(1-dx)*dy+source[yi+1,xi+1]*dx*dy
                assert np.max(np.abs(expected-panorama[py,px])) <= 1
                panorama_checks += 1
    detail_count = 0
    if args.details:
        for detail in json.loads((args.details / "details.json").read_text()):
            e = detail["source"]
            raw = (args.details / (detail["name"] + ".raw")).read_bytes()
            assert hashlib.sha256(raw).hexdigest().upper() == e["rawSha256"]
            values = np.frombuffer(raw, dtype="<u2")
            assert values.max() <= 4095 and abs(float(values.mean())-e["rawMean"]) < 1e-9
            pixels = np.array(Image.open(args.details / (detail["name"] + ".png")).convert("RGB"))
            assert hashlib.sha256(pixels.tobytes()).hexdigest().upper() == e["displayPixelsSha256"]
            assert e["configuration"]["rig"]["optics"]["horizontalFlip"] is True
            assert 0 < e["exposureSeconds"] <= 1
            detail_count += 1
    records = json.loads((args.day / "sequence.json").read_text())
    coverage = json.loads((args.day / "coverage.json").read_text())
    start, end = utc(coverage["start"]), utc(coverage["end"])
    counts = {}
    composite = np.zeros((640, 640, 3), dtype=np.uint8)
    trail_count = 0
    integral = 0
    for index, row in enumerate(records):
        assert row["slot"] == index and utc(row["utc"]) == start + timedelta(minutes=index)
        assert start <= utc(row["utc"]) < end
        counts[row["missing"] or "captured"] = counts.get(row["missing"] or "captured", 0) + 1
        pixels = np.array(Image.open(args.day / "sequence" / f"{index:04d}.png").convert("RGB"))
        if row["evidence"] is None:
            assert not pixels.any() and not row["dark"]
            continue
        evidence = row["evidence"]
        assert utc(evidence["sourceUtc"]) == utc(row["utc"])
        assert evidence["timestampUtc"] == evidence["scene"]["virtualExposure"]["requestedStartUtc"]
        assert utc(evidence["sourceUtc"]) == utc(evidence["scene"]["virtualExposure"]["celestialStartUtc"])
        assert hashlib.sha256(pixels.tobytes()).hexdigest().upper() == evidence["displayPixelsSha256"]
        eligible = (utc(coverage["darkStart"]) <= utc(row["utc"]) and
                    utc(row["utc"]) + timedelta(seconds=evidence["exposureSeconds"]) <= utc(coverage["darkEnd"]) and
                    float(evidence["extra"]["stellarSolarAltitudeDegrees"]) <= -18)
        assert row["dark"] == eligible
        if eligible:
            composite = np.maximum(composite, pixels)
            trail_count += 1
            integral += evidence["exposureSeconds"]
    assert counts == {"leading-unscheduled": 15, "captured": 1381, "deliberate-30-minute-outage": 30, "trailing-capture-missing": 15}
    assert trail_count == coverage["darkSourceCount"] and integral == coverage["integratedSeconds"]
    assert np.array_equal(composite, np.array(Image.open(args.day / "star-trail.png")).astype(np.uint8)[:, :, :3])
    keogram = np.array(Image.open(args.day / "keogram-pixels.png")).astype(np.uint8)[:, :, :3]
    assert keogram.shape == (361, 1441, 3)
    path = json.loads((args.day / "sample-path.json").read_text())
    for slot in [15, 100, 600, 900, 1400]:
        if records[slot]["evidence"] is None:
            continue
        source = np.array(Image.open(args.day / "sequence" / f"{slot:04d}.png").convert("RGB"), dtype=float)
        for y, point in enumerate(path):
            if point is None:
                assert not keogram[y, slot].any()
                continue
            x0, y0 = point["x"] - .5, point["y"] - .5
            xi, yi = int(np.floor(x0)), int(np.floor(y0))
            dx, dy = x0 - xi, y0 - yi
            if 0 <= xi < 639 and 0 <= yi < 639:
                expected = (source[yi, xi] * (1-dx)*(1-dy) + source[yi, xi+1]*dx*(1-dy) +
                            source[yi+1, xi]*(1-dx)*dy + source[yi+1, xi+1]*dx*dy)
                assert np.max(np.abs(expected - keogram[y, slot])) <= 1
    result = {"rawSamplesVerified": len(samples), "panoramaIndependentPixelChecks": panorama_checks, "narrowFieldRawCapturesVerified": detail_count, "sourceSlotsVerified": len(records), "coverageStates": counts,
              "trailSourcesVerified": trail_count, "integratedSecondsVerified": integral,
              "keogramIndependentBilinearColumnChecks": 5,
              "checks": "raw hashes/ADU means/clipping, fixed display pixel hashes, separate clocks, exact minute source grid, full-period dimensions, dark eligibility, streaming maximum composite, independently sampled strips"}
    (args.day / "verification.json").write_text(json.dumps(result, indent=2))
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
