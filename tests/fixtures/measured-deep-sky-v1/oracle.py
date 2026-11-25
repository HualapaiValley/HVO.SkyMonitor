"""Independent analytic sensor exposures for #1926. No product code or catalogs."""
import hashlib
import json
import math
import pathlib
import struct
import sys

WIDTH = HEIGHT = 96
SHIFTS = ((-2, 1), (1, -2), (0, 0), (2, 1), (-1, -1))
CHANNELS = ((18, 1000), (18, 1200), (18, 900))
TRUTHS = (("training", 42.5, 45.5, 193), ("held-out", 62.5, 32.5, 907))
CASES = ("diffuse", "blank", "psf", "blend", "trail", "seam")


def exposures(case, cx, cy, seed):
    # Twelve independent discrete uniform samples approximate Gaussian read+sky noise.
    # The integer LCG and round-to-nearest code simplify independent reproduction.
    state = seed
    for dx, dy in SHIFTS:
        for amplitude, sky in CHANNELS:
            values = []
            for y in range(HEIGHT):
                for x in range(WIDTH):
                    noise = -1530
                    for _ in range(12):
                        state = (1664525 * state + 1013904223) & 0xffffffff
                        noise += state >> 24
                    u, v = x + .5 - dx - cx, y + .5 - dy - cy
                    signal = 0
                    if case == "diffuse":
                        signal = amplitude * math.exp(-.5 * ((u / 3.4)**2 + (v / 2.8)**2))
                    elif case == "psf":
                        signal = 350 * math.exp(-.5 * (u*u + v*v) / .95**2)
                    elif case == "blend":
                        signal = sum(300 * math.exp(-.5 * ((u - t)**2 + (v - t/2)**2) / .95**2)
                                     for t in (-2.8, 2.8))
                    elif case == "trail":
                        signal = 110 * math.exp(-.5 * ((u / 13)**2 + (v / 1.05)**2))
                    elif case == "seam":
                        signal = 85 if u > 0 else 0
                    values.append(round(sky + noise / 9 + signal))
            yield struct.pack("<" + "H" * len(values), *values)


def main(destination):
    destination.mkdir(parents=True, exist_ok=True)
    result = {}
    for split, x, y, seed in TRUTHS:
        for case in CASES:
            key = f"{split}-{case}"
            payload = b"".join(exposures(case, x, y, seed))
            (destination / (key + ".bin")).write_bytes(payload)
            result[key] = {"sha256": hashlib.sha256(payload).hexdigest(), "centroid": [x, y]}
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main(pathlib.Path(sys.argv[1]))
