"""Run the inventory-owned Performance selection and compare its TRX durations."""

import argparse
import json
import re
import statistics
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path


SAMPLES_PER_RUN = 3
HISTORY_SIZE = 5
NS = {"trx": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
DURATION = re.compile(r"(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?")
REPO_ROOT = Path(__file__).resolve().parents[2]


def ticks(duration):
    match = DURATION.fullmatch(duration)
    if not match:
        raise ValueError("Invalid TRX duration")
    days, hours, minutes, seconds, fraction = match.groups(default="0")
    if int(hours) >= 24 or int(minutes) >= 60 or int(seconds) >= 60:
        raise ValueError("Invalid TRX duration")
    whole_seconds = ((int(days) * 24 + int(hours)) * 60 + int(minutes)) * 60 + int(seconds)
    value = whole_seconds * 10_000_000 + int(fraction.ljust(7, "0"))
    if value <= 0:
        raise ValueError("TRX duration must be positive")
    return value


def read_trx(path, project, expected_count):
    document = ET.parse(path)
    results = document.findall("trx:Results/trx:UnitTestResult", NS)
    if len(results) != expected_count or any(result.get("outcome") != "Passed" for result in results):
        raise ValueError(f"{project} did not pass all {expected_count} expected Performance cases")

    methods = {}
    for definition in document.findall("trx:TestDefinitions/trx:UnitTest", NS):
        method = definition.find("trx:TestMethod", NS)
        if method is not None:
            name = f'{method.get("className", "")}.{method.get("name", "")}'
            if not re.fullmatch(r"[\w.+]+", name, flags=re.ASCII):
                raise ValueError(f"{project} TRX contains a malformed test name")
            methods[definition.get("id")] = name

    measurements = {}
    for result in results:
        test_id = result.get("testId")
        if test_id not in methods or not re.fullmatch(r"[0-9a-fA-F-]{36}", test_id):
            raise ValueError(f"{project} TRX result is missing a test definition")
        # VSTest distinguishes data rows in testId. Class and method names give
        # the reader an identifier without copying arbitrary TRX display text.
        case = f"{project}/{methods[test_id]} [{test_id}]"
        if case in measurements:
            raise ValueError(f"{project} TRX contains duplicate results")
        measurements[case] = ticks(result.get("duration", ""))
    return measurements


def performance_projects(root):
    for project in sorted((root / "tests").glob("*/*.csproj")):
        if "LogicHost" in project.stem:
            continue
        inventory = json.loads(project.with_name("test-categories.json").read_text())
        count = inventory.get("Performance") if isinstance(inventory, dict) else None
        if type(count) is not int or count < 0:
            raise ValueError(f"{project.stem} has an invalid Performance inventory")
        if count:
            yield project, count


def measure(scratch, no_build):
    projects = list(performance_projects(REPO_ROOT))
    if not projects:
        raise ValueError("No projects declare Performance cases")
    scratch.mkdir(mode=0o700, parents=True)
    measurements = {}
    for project, count in projects:
        output = scratch / project.stem
        output.mkdir()
        trials = []
        for trial in range(SAMPLES_PER_RUN):
            trx = output / f"sample-{trial + 1}.trx"
            command = [
                "dotnet", "test", str(project), "--configuration", "Release",
                "--filter", "TestCategory=Performance", "--results-directory", str(output),
                "--logger", f"trx;LogFileName={trx.name}",
            ]
            if no_build or trial > 0:
                command.extend(["--no-build", "--no-restore"])
            with trx.with_suffix(".log").open("w") as log:
                result = subprocess.run(command, cwd=REPO_ROOT, stdout=log, stderr=subprocess.STDOUT, check=False)
            if result.returncode:
                raise ValueError(f"{project.stem} Performance trial {trial + 1} failed (exit {result.returncode})")
            if not trx.is_file():
                raise ValueError(f"{project.stem} Performance trial {trial + 1} produced no TRX")
            trials.append(read_trx(trx, project.stem, count))
        if any(trial.keys() != trials[0].keys() for trial in trials[1:]):
            raise ValueError(f"{project.stem} discovered different Performance cases between trials")
        measurements.update({
            case: statistics.median(trial[case] for trial in trials) for case in trials[0]
        })
    return measurements


def load_baseline(path):
    if not path.exists():
        return {}
    document = json.loads(path.read_text())
    if not isinstance(document, dict) or document.get("version") != 1:
        raise ValueError("Invalid Pi baseline version")
    history = document.get("history")
    if not isinstance(history, dict) or not history:
        raise ValueError("Pi baseline must contain timing history")
    for samples in history.values():
        if not isinstance(samples, list) or not 1 <= len(samples) <= HISTORY_SIZE or any(
            type(sample) is not int or sample <= 0 for sample in samples
        ):
            raise ValueError("Invalid Pi baseline samples")
    return history


def compare(measurements, history):
    rows = {}
    for case, measured in measurements.items():
        previous = history.get(case)
        reference = statistics.median(previous) if previous else None
        # 6/5 is exact, including when the median falls between two whole ticks.
        regressed = reference is not None and 5 * measured > 6 * reference
        rows[case] = {
            "measuredTicks": measured,
            "baselineTicks": reference,
            "changePercent": round(100 * (measured - reference) / reference, 2) if reference else None,
            "status": "seeded" if reference is None else "regressed" if regressed else "accepted",
        }
    return rows


def write_report(path, **data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"version": 1, **data}, indent=2) + "\n")


def evaluate(measurements, baseline_path, report_path):
    if not measurements:
        raise ValueError("No Performance measurements to compare")
    history = load_baseline(baseline_path)
    comparison = compare(measurements, history)
    regressions = [case for case, row in comparison.items() if row["status"] == "regressed"]
    write_report(report_path, cases=comparison)
    if regressions:
        for case in regressions:
            print(f"Regression: {case} is {comparison[case]['changePercent']:.2f}% above its Pi baseline")
        return 1

    updated = {
        case: (history.get(case, []) + [value])[-HISTORY_SIZE:] for case, value in measurements.items()
    }
    baseline_path.parent.mkdir(parents=True, exist_ok=True)
    replacement = baseline_path.with_suffix(".next")
    replacement.write_text(json.dumps({"version": 1, "history": updated}, indent=2) + "\n")
    replacement.replace(baseline_path)
    seeded = sum(row["status"] == "seeded" for row in comparison.values())
    print(f"Pi performance: {len(comparison)} cases passed, {seeded} seeded")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("scratch", type=Path, help="New private directory for raw TRX and dotnet output")
    parser.add_argument("baseline", type=Path)
    parser.add_argument("report", type=Path)
    parser.add_argument("--no-build", action="store_true", help="Use prebuilt Release tests")
    args = parser.parse_args()
    try:
        return evaluate(measure(args.scratch, args.no_build), args.baseline, args.report)
    except (OSError, ValueError, ET.ParseError) as error:
        # I/O and parse exceptions can embed absolute paths or raw test output.
        if isinstance(error, (OSError, ET.ParseError)):
            message = f"Could not read or write timing evidence ({type(error).__name__})"
        else:
            message = str(error)
        print(f"Pi performance: {message}")
        try:
            write_report(args.report, error=message)
        except OSError:
            print("Pi performance: could not record failure evidence")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
