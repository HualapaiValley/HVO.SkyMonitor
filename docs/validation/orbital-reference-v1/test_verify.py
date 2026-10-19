"""Failure regressions for the offline research verifier; no filesystem writes."""

import hashlib
import json
import unittest
from pathlib import Path
from unittest.mock import patch

import verify as verifier


class ReferenceVerifierTests(unittest.TestCase):
    def test_valid_pack(self):
        result = verifier.verify(Path(__file__).resolve().parent)
        self.assertEqual((result["samples"], result["expectedFailures"]), (641, 1))

    def check_repinned_mutation_rejected(self, field, value, overflow_literal=False):
        root = Path(__file__).resolve().parent
        blobs = {path.name: path.read_bytes() for path in root.iterdir() if path.is_file()}
        name = "vallado-afspc-vectors.json"
        fixture = json.loads(blobs[name])
        for case in fixture["cases"]:
            for sample in case["samples"]:
                if sample["expectedError"] == 0:
                    sample[field] = [value, value, value]
        raw = json.dumps(fixture)
        if overflow_literal:
            raw = raw.replace('"overflow"', "1e999")
        blobs[name] = raw.encode()
        manifest = json.loads(blobs["manifest.json"])
        entry = next(item for item in manifest["files"] if item["path"] == name)
        entry.update(bytes=len(blobs[name]), sha256=hashlib.sha256(blobs[name]).hexdigest())
        blobs["manifest.json"] = json.dumps(manifest).encode()
        with patch.object(Path, "read_text", lambda path, *a, **k: blobs[path.name].decode()), \
             patch.object(Path, "read_bytes", lambda path: blobs[path.name]), \
             patch.object(Path, "is_file", lambda path: path.name in blobs):
            with self.assertRaises(ValueError):
                verifier.verify(root)

    def test_repinned_nonfinite_expected_positions_and_velocities_are_rejected(self):
        for field in ("positionKm", "velocityKmPerSecond"):
            for value in (float("nan"), float("inf"), float("-inf")):
                with self.subTest(field=field, value=value):
                    self.check_repinned_mutation_rejected(field, value)

    def test_json_numeric_overflow_is_rejected(self):
        for field in ("positionKm", "velocityKmPerSecond"):
            with self.subTest(field=field):
                self.check_repinned_mutation_rejected(field, "overflow", overflow_literal=True)

    def test_finite_expected_values_cannot_overflow_the_residual(self):
        for field in ("positionKm", "velocityKmPerSecond"):
            with self.subTest(field=field):
                self.check_repinned_mutation_rejected(field, 1.7976931348623157e308)

    def test_materialized_vectors_reject_nonfinite_values(self):
        for value in (float("nan"), float("inf"), float("-inf"), True, "0", None):
            with self.subTest(value=value):
                with self.assertRaises(ValueError):
                    verifier._finite_vector([value, 0.0, 0.0])


if __name__ == "__main__":
    unittest.main()
