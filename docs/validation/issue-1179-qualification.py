#!/usr/bin/env python3
"""Run and seal the two phases of the frozen #1179 VirtualSky study."""

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
MANIFEST = Path(__file__).with_name("issue-1179-qualification-manifest.json")
PROJECT = ROOT / "tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj"
TRX = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read(path):
    return json.loads(path.read_text())


def write(path, value):
    path.write_text(json.dumps(value, sort_keys=True, indent=2, allow_nan=False) + "\n")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def checksums(pack):
    return "".join(f"{digest(path)}  {path.relative_to(pack)}\n" for path in sorted(pack.rglob("*"))
                   if path.is_file() and path != pack / "SHA256SUMS")


def only_report(directory, name):
    matches = sorted(directory.rglob(name))
    require(len(matches) == 1, f"{directory}: expected one {name}, got {len(matches)}")
    return read(matches[0])


def run_cell(cell, phase, directory, revision, manifest):
    run = manifest[phase]
    family, seconds, profiles = cell["family"], cell["exposureSeconds"], cell["profiles"]
    realizations = run["profileRealizationsPerNight"]
    nights = len(run["nights"])
    frame_count = nights * profiles * realizations
    directory.mkdir()
    env = os.environ.copy()
    for name in ("HVO_ASTROMETRY_MEASURER", "HVO_LONG_EXPOSURE_VARIANT", "HVO_1179_FLOORS"):
        env.pop(name, None)
    env.update(HVO_PROJECTION_FAMILY=family, HVO_EXPOSURE_SECONDS=str(seconds),
               DOTNET_TieredCompilation="0", HVO_EVIDENCE_REVISION=revision)
    if phase == "held-out":
        env["HVO_1179_FLOORS"] = str(directory.parent.parent / "tuning" / directory.name / "floors.json")
    print(f"{phase} {family} {seconds}s", flush=True)
    with (directory / "test.log").open("w") as log:
        result = subprocess.run(["dotnet", "test", str(PROJECT), "-c", "Release", "--no-build", "--no-restore",
                                 "--filter", f"FullyQualifiedName~VirtualAstrometricUncertaintyQualificationTests.{run['method']}",
                                 "--results-directory", str(directory), "--logger", "trx;LogFileName=run.trx"],
                                cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, check=False)

    report = only_report(directory, "virtual-astrometric-uncertainty.json")
    lineage = only_report(directory, "long-exposure-qualification-lineage.json")
    tests = ET.parse(directory / "run.trx").findall("t:Results/t:UnitTestResult", TRX)
    failures = report["failures"]
    expected_outcome = "Failed" if failures else "Passed"
    require(len(tests) == 1 and tests[0].get("testName") == run["method"] and
            tests[0].get("outcome") == expected_outcome and result.returncode == int(bool(failures)),
            f"{directory}: TRX and report disagree")
    require(report["revision"] == revision and report["projectionFamily"] == family and
            report["exposureSeconds"] == seconds and report["estimator"] == manifest["schema"] and
            report["databaseSha256"].lower() == manifest["catalogDatabaseSha256"], f"{directory}: wrong workload identity")
    require(len(lineage) == frame_count + 7 * nights and len(report["sessions"]) == nights,
            f"{directory}: incomplete capture or session inventory")
    require({capture["startUtc"][:10] for capture in lineage.values()} == {night["utcDate"] for night in run["nights"]},
            f"{directory}: the capture nights differ from the manifest")
    for capture in lineage.values():
        require(all(re.fullmatch(r"[0-9a-fA-F]{64}", capture[field]) for field in ("rawSha256", "configurationSha256")),
                f"{directory}: invalid capture lineage")
        if capture["mapping"] == "Accepted":
            require(capture["productionUncertainty"] == "exposure-not-qualified", f"{directory}: release policy failed")

    rows = {row["profile"]: row for row in report["coverage"]}
    require(len(rows) <= profiles + 1 and rows["pooled"]["frames"] == len(report["frames"]) <= frame_count,
            f"{directory}: inconsistent frame coverage")
    fallback = []
    if phase == "tuning":
        floors = {}
        historical = report["tolerances"]["declaredFloorPixels"]
        profile_names = sorted({case.split("-", 1)[1].rsplit("-", 1)[0] for case in lineage if "-session-" not in case})
        for profile in profile_names:
            values = [frame["requiredFloorPixels"] for frame in report["frames"] if frame["profile"] == profile]
            if len(values) != nights * realizations or not all(isinstance(value, (int, float)) and
                    math.isfinite(value) and 0 <= value <= 10 for value in values):
                floors[profile] = historical[profile]
                fallback.append(profile)
                continue
            percentile = sorted(values)[math.ceil(.90 * len(values)) - 1]
            floors[profile] = math.ceil(1000 * percentile) / 1000
        require(len(floors) == profiles, f"{directory}: missing tuning profile")
        write(directory / "floors.json", floors)

    return {"cell": directory.name, "disposition": expected_outcome, "failures": failures, "coverage": report["coverage"],
            "captures": len(lineage), "acceptedMappings": len(report["frames"]),
            "floors": report["tolerances"]["declaredFloorPixels"], "tuningFallbackProfiles": fallback,
            "resources": report["frameResources"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("phase", choices=("tuning", "held-out"))
    parser.add_argument("pack", type=Path)
    args = parser.parse_args()
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    require(not subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=all"], cwd=ROOT),
            "qualification requires a clean, committed checkout")
    require(os.environ.get("HVO_ASTROMETRY_CATALOG_ROOT"), "set HVO_ASTROMETRY_CATALOG_ROOT to the verified install root")
    identity = {"revision": revision, "manifestSha256": digest(MANIFEST)}
    pack = args.pack.resolve()
    if args.phase == "tuning":
        pack.mkdir()
        write(pack / "identity.json", identity)
    else:
        require(read(pack / "identity.json") == identity, "held-out must use the tuning manifest and revision")
        tuning = pack / "tuning"
        require((tuning / "SHA256SUMS").read_text() == checksums(tuning), "tuning evidence changed after sealing")

    directory = pack / args.phase
    directory.mkdir()
    manifest = read(MANIFEST)
    rows = [run_cell(cell, args.phase, directory / f"{cell['family']}-{cell['exposureSeconds']}s", revision, manifest)
            for cell in manifest["cells"]]
    write(directory / "summary.json", rows)
    (directory / "SHA256SUMS").write_text(checksums(directory))
    print(f"{args.phase}: {len(rows)} complete processes, {sum(row['disposition'] == 'Passed' for row in rows)} passed", flush=True)


if __name__ == "__main__":
    main()
