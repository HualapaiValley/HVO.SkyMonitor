#!/usr/bin/env python3

import argparse
import array
import hashlib
import json
from pathlib import Path
import sys


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Report little-endian RAW16 container-code and low-nibble occupancy."
    )
    parser.add_argument("raw_file", type=Path)
    args = parser.parse_args()

    byte_count = args.raw_file.stat().st_size
    if byte_count % 2 != 0:
        parser.error("RAW16 input length must be divisible by two")

    digest = hashlib.sha256()
    residues = [0] * 16
    distinct_codes = set()
    sample_count = 0
    with args.raw_file.open("rb") as source:
        while True:
            chunk = source.read(1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
            samples = array.array("H")
            samples.frombytes(chunk)
            if sys.byteorder != "little":
                samples.byteswap()
            sample_count += len(samples)
            for sample in samples:
                residues[sample & 0xF] += 1
                distinct_codes.add(sample)

    print(json.dumps({
        "file": str(args.raw_file),
        "sha256": digest.hexdigest(),
        "byteCount": byte_count,
        "sampleCount": sample_count,
        "distinctContainerCodes": len(distinct_codes),
        "lowNibbleCounts": residues
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
