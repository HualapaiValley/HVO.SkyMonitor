import json
import tempfile
import unittest
from pathlib import Path

from pi5_performance import compare, evaluate, load_baseline, read_trx, ticks


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


class TrxTests(unittest.TestCase):
    def test_durations_in_ticks(self):
        self.assertEqual(ticks("00:00:01.0023456"), 10_023_456)
        self.assertEqual(ticks("1.02:03:04.5"), 937_845_000_000)
        for duration in ("", "00:00:00", "00:60:00.1", "00:00:00.01234567"):
            with self.subTest(duration=duration), self.assertRaises(ValueError):
                ticks(duration)

    def test_requires_complete_passing_results_and_stable_case_identity(self):
        test_id = "01234567-89ab-cdef-0123-456789abcdef"
        result = f'<UnitTestResult testId="{test_id}" outcome="Passed" duration="00:00:00.2500000" />'
        template = f"""
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>{{results}}</Results>
  <TestDefinitions>
    <UnitTest id="{test_id}"><TestMethod className="Suite.Measurements" name="Measured" /></UnitTest>
  </TestDefinitions>
</TestRun>
"""
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory, "results.trx")
            path.write_text(template.format(results=result))
            self.assertEqual(read_trx(path, "Project", 1), {f"Project/Suite.Measurements.Measured [{test_id}]": 2_500_000})
            with self.assertRaises(ValueError):
                read_trx(path, "Project", 2)
            path.write_text(template.format(results=result.replace("Passed", "NotExecuted")))
            with self.assertRaises(ValueError):
                read_trx(path, "Project", 1)
            path.write_text(template.format(results=result * 2))
            with self.assertRaises(ValueError):
                read_trx(path, "Project", 2)


if __name__ == "__main__":
    unittest.main()
