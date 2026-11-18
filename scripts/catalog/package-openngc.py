#!/usr/bin/env python3
"""Package only the standalone database and notices pinned by the embedded registry."""

import hashlib
import json
from pathlib import Path
import shutil
import sys


def verify(path, evidence):
    if path.is_symlink() or not path.is_file():
        raise ValueError("Catalog input must be a regular file")
    data = path.read_bytes()
    if len(data) != evidence["length"] or hashlib.sha256(data).hexdigest() != evidence["sha256"]:
        raise ValueError("Catalog input differs from the approved specification")
    return data


def main():
    if len(sys.argv) != 3:
        raise ValueError("Expected DATABASE OUTPUT_DIRECTORY")
    repo = Path(__file__).resolve().parents[2]
    registry = json.loads((repo / "src/HVO.SkyMonitor.Catalog.Sqlite/ApprovedCatalogSpecifications.json").read_text())
    spec = next(item for item in registry["specifications"] if item["catalogId"] == "openngc-production")
    database = verify(Path(sys.argv[1]), spec["database"])
    license_data = verify(repo / "docs/catalog/openngc-license.md", spec["license"]["file"])
    attribution = verify(repo / "docs/catalog/openngc-attribution.md", spec["license"]["attribution"])
    output = Path(sys.argv[2])
    output.mkdir(mode=0o755)
    try:
        (output / spec["database"]["relativePath"]).write_bytes(database)
        (output / spec["license"]["file"]["relativePath"]).write_bytes(license_data)
        (output / spec["license"]["attribution"]["relativePath"]).write_bytes(attribution)
        manifest = {key: value for key, value in spec.items()
                    if key not in ("catalogId", "packageLineage", "packageVersionPrefix")}
        manifest["package"] = {"kind": "production", "version": spec["packageVersionPrefix"] + "1"}
        manifest["catalog"] = {"id": spec["catalogId"], **spec["catalog"]}
        (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
        for path in output.iterdir():
            path.chmod(0o444)
    except BaseException:
        shutil.rmtree(output)
        raise


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, StopIteration):
        sys.exit("OpenNGC packaging refused: input or output does not match the approved contract")
