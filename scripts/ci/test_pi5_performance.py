import io
import json
import subprocess
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch

from pi5_performance import compare, evaluate, load_baseline, main, measure, read_trx, ticks


TEST_ID = "01234567-89ab-cdef-0123-456789abcdef"
TEST_RESULT = f'<UnitTestResult testId="{TEST_ID}" outcome="Passed" duration="00:00:00.2500000" />'
TEST_DEFINITION = f'<UnitTest id="{TEST_ID}"><TestMethod className="Suite.Measurements" name="Measured" /></UnitTest>'


def trx_document(results, definitions=TEST_DEFINITION):
    return f"""
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>{results}</Results>
  <TestDefinitions>{definitions}</TestDefinitions>
</TestRun>
"""


class RegressionTests(unittest.TestCase):
    def test_strict_twenty_percent_threshold(self):
        names = ("faster", "equal", "at limit", "over limit")
        measured = dict(zip(names, [91, 100, 120, 121], strict=True))
        rows = compare(measured, {name: [100] for name in names})
        self.assertEqual([rows[name]["status"] for name in names],
                         ["accepted", "accepted", "accepted", "regressed"])
        self.assertEqual(rows["over limit"]["changePercent"], 21)

    def test_rolling_median_uses_only_prior_observations(self):
        self.assertEqual(compare({"case": 122}, {"case": [101, 102]})["case"]["baselineTicks"], 101.5)
        self.assertEqual(compare({"case": 122}, {"case": [101, 102]})["case"]["status"], "regressed")
        self.assertEqual(compare({"case": 120}, {"case": [100, 110, 100, 500, 90]})["case"]["status"],
                         "accepted")

    def test_cold_start_rollover_and_regression_preserve_accepted_history(self):
        with tempfile.TemporaryDirectory() as directory:
            baseline = Path(directory, "cache", "baseline.json")
            report = Path(directory, "evidence", "comparison.json")
            self.assertEqual(evaluate({"first": 100}, baseline, report), 0)
            self.assertEqual(load_baseline(baseline), {"first": [100]})
            self.assertEqual(json.loads(report.read_text())["cases"]["first"]["status"], "seeded")

            for value in (101, 102, 103, 104, 105):
                self.assertEqual(evaluate({"first": value, "added": 200}, baseline, report), 0)
            self.assertEqual(load_baseline(baseline), {"first": [101, 102, 103, 104, 105], "added": [200] * 5})

            prior = baseline.read_bytes()
            self.assertEqual(evaluate({"first": 124, "added": 198}, baseline, report), 1)
            self.assertEqual(baseline.read_bytes(), prior)
            self.assertEqual(json.loads(report.read_text())["cases"]["first"]["baselineTicks"], 103)

            self.assertEqual(evaluate({"first": 99}, baseline, report), 0)
            self.assertEqual(load_baseline(baseline), {"first": [102, 103, 104, 105, 99]})

    def test_corrupt_history_is_an_error(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory, "baseline.json")
            for content in ('{"version":0}', '{"version":1,"history":{"case":[0]}}',
                            '{"version":1,"history":{"case":[true]}}', '{"version":1,"history":{}}'):
                with self.subTest(content=content):
                    path.write_text(content)
                    with self.assertRaises(ValueError):
                        load_baseline(path)

    def test_report_records_the_cache_restore_outcome_and_matched_key(self):
        cache_key = "skymonitor-development-v1-pi5-performance-v1-123-1"
        with tempfile.TemporaryDirectory() as directory:
            baseline = Path(directory, "baseline.json")
            report = Path(directory, "comparison.json")
            for outcome, key in (("", ""), ("skipped", ""), ("success", ""), ("success", cache_key)):
                with self.subTest(outcome=outcome, key=key), patch.dict("os.environ", {
                    "PI5_CACHE_MATCHED_KEY": key, "PI5_CACHE_RESTORE_OUTCOME": outcome,
                }):
                    self.assertEqual(evaluate({"case": 100}, baseline, report), 0)
                    self.assertEqual(json.loads(report.read_text())["baselineCache"], {
                        "matchedKey": key or None, "restoreOutcome": outcome or None,
                    })


class TrxTests(unittest.TestCase):
    def test_durations_in_ticks(self):
        self.assertEqual(ticks("00:00:01.0023456"), 10_023_456)
        self.assertEqual(ticks("1.02:03:04.5"), 937_845_000_000)
        for duration in ("", "00:00:00", "00:60:00.1", "00:00:00.01234567"):
            with self.subTest(duration=duration), self.assertRaises(ValueError):
                ticks(duration)

    def test_requires_complete_passing_results_and_stable_case_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory, "results.trx")
            path.write_text(trx_document(TEST_RESULT))
            self.assertEqual(read_trx(path, "Project", 1), {f"Project/Suite.Measurements.Measured [{TEST_ID}]": 2_500_000})
            with self.assertRaises(ValueError):
                read_trx(path, "Project", 2)
            path.write_text(trx_document(TEST_RESULT.replace("Passed", "NotExecuted")))
            with self.assertRaises(ValueError):
                read_trx(path, "Project", 1)
            path.write_text(trx_document(TEST_RESULT * 2))
            with self.assertRaises(ValueError):
                read_trx(path, "Project", 2)

    def test_missing_and_empty_trx_ids_raise_reportable_errors(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory, "results.trx")
            for test_id in (None, "", " "):
                with self.subTest(test_id=test_id):
                    result_id = "" if test_id is None else f' testId="{test_id}"'
                    definition_id = "" if test_id is None else f' id="{test_id}"'
                    result = TEST_RESULT.replace(f' testId="{TEST_ID}"', result_id)
                    definition = TEST_DEFINITION.replace(f' id="{TEST_ID}"', definition_id)
                    path.write_text(trx_document(result, definition))
                    with self.assertRaisesRegex(ValueError, "malformed test definition id"):
                        read_trx(path, "Project", 1)
                    path.write_text(trx_document(result))
                    with self.assertRaisesRegex(ValueError, "missing a valid test definition"):
                        read_trx(path, "Project", 1)


class TrialTests(unittest.TestCase):
    def test_failed_trial_reports_its_case_to_stdout_log_and_artifact(self):
        with tempfile.TemporaryDirectory() as directory:
            scratch = Path(directory, "raw")
            baseline = Path(directory, "baseline.json")
            report = Path(directory, "comparison.json")
            project = Path(directory, "Project.csproj")
            trx = scratch / project.stem / "sample-1.trx"
            baseline.write_text('{"version":1,"history":{"case":[100]}}')
            before = baseline.read_bytes()

            def run_test(command, **_):
                result = TEST_RESULT.replace('outcome="Passed"', 'outcome="Failed" testName="Raw display text"')
                trx.write_text(trx_document(result))
                return subprocess.CompletedProcess(command, 1)

            stdout = io.StringIO()
            with (patch("pi5_performance.performance_projects", return_value=[(project, 1)]),
                  patch("pi5_performance.subprocess.run", side_effect=run_test),
                  patch.dict("os.environ", {"PI5_CACHE_MATCHED_KEY": "prior-key", "PI5_CACHE_RESTORE_OUTCOME": "success"}),
                  redirect_stdout(stdout)):
                self.assertEqual(main([str(scratch), str(baseline), str(report), "--no-build"]), 1)

            artifact = json.loads(report.read_text())
            for evidence in (stdout.getvalue(), trx.with_suffix(".log").read_text(), artifact["error"]):
                self.assertIn("Project Performance trial 1 failed (exit 1)", evidence)
                self.assertIn(f"Suite.Measurements.Measured (testId={TEST_ID}, outcome=Failed)", evidence)
                self.assertNotIn("Raw display text", evidence)
            self.assertEqual(artifact["baselineCache"], {"matchedKey": "prior-key", "restoreOutcome": "success"})
            self.assertEqual(baseline.read_bytes(), before)

    def test_even_trial_counts_are_rejected_before_they_can_write_a_baseline(self):
        with tempfile.TemporaryDirectory() as directory:
            scratch = Path(directory, "raw")
            for count in (0, 2):
                with self.subTest(count=count), patch("pi5_performance.SAMPLES_PER_RUN", count):
                    with self.assertRaisesRegex(ValueError, "positive odd integer"):
                        measure(scratch, no_build=True)
            self.assertFalse(scratch.exists())


if __name__ == "__main__":
    unittest.main()
