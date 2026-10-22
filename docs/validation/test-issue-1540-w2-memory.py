"""Checks that the W2 guard rejects regressions and incomplete acceptance evidence."""

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ANALYZER = Path(__file__).with_name("issue-1540-w2-memory.py")
MIB = 1024 * 1024


class MemoryGuardTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.pack = Path(self.temporary.name)
        self.index = {
            "schema": "issue1540-w2-memory-pack-v1", "mode": "measure", "pairs": 5,
            "order": ["base", "candidate"], "runs": [],
            "arms": [{"label": label, "revision": character * 40}
                     for label, character in (("base", "a"), ("candidate", "b"))],
        }
        for pair in range(1, 6):
            for arm in self.index["arms"]:
                label, revision = arm["label"], arm["revision"]
                name = f"p{pair}-{label}"
                self.index["runs"].append({
                    "name": name, "label": label, "revision": revision, "pair": pair,
                    "exitCode": 0, "passedTests": 1,
                })
                status = {"VmHWM": 1000 * MIB, "RssAnon": 900 * MIB, "RssFile": 100 * MIB, "Threads": 12}
                data = {
                    "revision": revision, "binaryRevision": f"0.9.0+{revision}", "label": label, "trial": pair,
                    "runtimeProfile": "production-defaults", "workload": "w2-night", "failures": [],
                    "collectorAttached": False, "protocol": {"warmup": 5, "measured": 30}, "setup": {},
                    "samples": [{"ordinal": n, "measured": n >= 5, "status": status} for n in range(35)],
                    "memory": {"peakWorkingSetBytes": 1000 * MIB, "status": status,
                               "lohSizeAfterBytes": 500 * MIB, "pohSizeAfterBytes": MIB, "gcPauseMilliseconds": 10},
                    "summary": {"allocatedBytesPerOperation": {"median": 700 * MIB},
                                "serviceMilliseconds": {"median": 100}},
                    "totals": {"allocatedBytes": 25000 * MIB, "gen0": 20, "gen1": 10, "gen2": 5},
                }
                path = self.pack / "runs" / name / "data.json"
                path.parent.mkdir(parents=True)
                path.write_text(json.dumps(data))

    def change(self, name, action):
        path = self.pack / "runs" / name / "data.json"
        data = json.loads(path.read_text())
        action(data)
        path.write_text(json.dumps(data))

    def run_guard(self):
        (self.pack / "index.json").write_text(json.dumps(self.index))
        return subprocess.run([sys.executable, "-I", str(ANALYZER), "base", "candidate", str(self.pack)],
                              capture_output=True, text=True, check=False)

    def test_noise_passes(self):
        for pair in range(1, 6):
            self.change(f"p{pair}-candidate", lambda d: d["memory"].update(peakWorkingSetBytes=1040 * MIB))
        result = self.run_guard()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(json.loads(result.stdout)["peakRssRule"]["regression"])

    def test_historical_size_regression_fails_even_with_equal_maximum(self):
        # A favorable maximum must never mask the +13.56% median that originally blocked F3.
        for pair in range(1, 6):
            self.change(f"p{pair}-candidate", lambda d: d["memory"].update(peakWorkingSetBytes=1136 * MIB))
        for label in ("base", "candidate"):
            self.change(f"p5-{label}", lambda d: d["memory"].update(peakWorkingSetBytes=1200 * MIB))
        result = self.run_guard()
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertTrue(json.loads(result.stdout)["peakRssRule"]["regression"])

    def test_missing_trial_fails(self):
        self.index["runs"].pop()
        self.assertNotEqual(0, self.run_guard().returncode)

    def test_duplicate_trial_fails(self):
        self.index["runs"][-1] = self.index["runs"][0]
        self.assertNotEqual(0, self.run_guard().returncode)

    def test_failed_trial_fails(self):
        self.index["runs"][0]["exitCode"] = 1
        self.assertNotEqual(0, self.run_guard().returncode)

    def test_wrong_binary_fails(self):
        self.change("p1-candidate", lambda d: d.update(binaryRevision="0.9.0+wrong"))
        self.assertNotEqual(0, self.run_guard().returncode)

    def test_truncated_operations_fail(self):
        self.change("p1-candidate", lambda d: d["samples"].pop())
        self.assertNotEqual(0, self.run_guard().returncode)

    def test_collector_in_measurement_fails(self):
        self.change("p1-candidate", lambda d: d.update(collectorAttached=True))
        self.assertNotEqual(0, self.run_guard().returncode)


if __name__ == "__main__":
    unittest.main()
