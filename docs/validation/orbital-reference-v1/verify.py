"""Verify the pinned research reference pack offline; not production propagation."""

import argparse
import hashlib
import importlib.metadata
import json
import math
from pathlib import Path


def _finite_float(text):
    value = float(text)
    if not math.isfinite(value):
        raise ValueError("JSON numbers must be finite")
    return value


def _reject_constant(text):
    raise ValueError(f"Nonstandard JSON constant: {text}")


def _load_json(path):
    return json.loads(path.read_text(), parse_float=_finite_float, parse_constant=_reject_constant)


def _finite_vector(value):
    if not isinstance(value, list) or len(value) != 3 or any(
        not isinstance(component, (int, float)) or isinstance(component, bool) or not math.isfinite(component)
        for component in value
    ):
        raise ValueError("Reference vectors must have three finite numeric components")
    return value


def verify(root):
    manifest = _load_json(root / "manifest.json")
    for entry in manifest["files"]:
        path = root / entry["path"]
        if path.parent != root or not path.is_file():
            raise ValueError("Fixture path must be a direct retained file")
        data = path.read_bytes()
        if len(data) != entry["bytes"] or hashlib.sha256(data).hexdigest() != entry["sha256"]:
            raise ValueError(f"Fixture checksum mismatch: {path.name}")

    if importlib.metadata.version("sgp4") != "2.27":
        raise ValueError("Use the pinned research reference sgp4==2.27")
    from sgp4.earth_gravity import wgs72
    from sgp4.io import twoline2rv
    from sgp4.propagation import sgp4

    fixture = _load_json(root / "vallado-afspc-vectors.json")
    if fixture["gravity"] != "WGS72" or fixture["operationMode"] != "a":
        raise ValueError("Reference model mismatch")
    count = failures = 0
    methods = set()
    maximum_position = maximum_velocity = 0.0
    for case in fixture["cases"]:
        satellite = twoline2rv(case["tle"]["line1"], case["tle"]["line2"], wgs72, opsmode="a")
        methods.add(satellite.method)
        for sample in case["samples"]:
            expected_position = _finite_vector(sample["positionKm"])
            expected_velocity = _finite_vector(sample["velocityKmPerSecond"])
            position, velocity = sgp4(satellite, sample["minutesSinceEpoch"])
            count += 1
            if satellite.error != sample["expectedError"]:
                raise ValueError(f"Unexpected propagation status: {case['catalogNumber']}")
            if satellite.error:
                failures += 1
                continue
            if not all(math.isfinite(value) for value in (*position, *velocity)):
                raise ValueError("A successful state must be finite")
            position_error = math.dist(position, expected_position)
            velocity_error = math.dist(velocity, expected_velocity)
            if not math.isfinite(position_error) or not math.isfinite(velocity_error):
                raise ValueError("Reference residuals must be finite")
            maximum_position = max(maximum_position, position_error)
            maximum_velocity = max(maximum_velocity, velocity_error)
            if position_error > 1e-6 or velocity_error > 1e-8:
                raise ValueError(f"Reference tolerance exceeded: {case['catalogNumber']}")
    expected = manifest["referenceArchive"]
    if count != expected["sampleCount"] or len(fixture["cases"]) != expected["caseCount"]:
        raise ValueError("Reference inventory mismatch")
    if methods != {"n", "d"}:
        raise ValueError("Both near-Earth and deep-space branches must be exercised")
    records = _load_json(root / "gp-source-facts.json")
    if len(records) != 6 or len({row["NORAD_CAT_ID"] for row in records}) != 6:
        raise ValueError("Source-fact inventory mismatch")
    return {
        "schemaVersion": "orbital-research-verification-v1",
        "networkRequests": 0,
        "referencePackage": "sgp4==2.27",
        "gravity": "WGS72",
        "operationMode": "a",
        "cases": len(fixture["cases"]),
        "samples": count,
        "expectedFailures": failures,
        "maximumPositionResidualKm": maximum_position,
        "maximumVelocityResidualKmPerSecond": maximum_velocity,
        "productionDotNetCandidateExecuted": False,
        "physicalAccuracyQualified": False,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args()
    result = verify(Path(__file__).resolve().parent)
    serialized = json.dumps(result, indent=2, allow_nan=False) + "\n"
    if arguments.output:
        if arguments.output.exists():
            raise ValueError("Retained reports are immutable; choose a new output path")
        arguments.output.write_text(serialized)
    print(serialized, end="")


if __name__ == "__main__":
    main()
