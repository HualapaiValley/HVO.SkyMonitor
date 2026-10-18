#!/usr/bin/env python3
"""Tests for scripts/docs:terminal-screenshots, which refreshes docs/images/terminal (#1317, PR-1371-R1 F6).

The script runs in a scratch copy of the layout it needs, with a stand-in dotnet on PATH that saves, or does not save,
the screens a render test would. An empty or failed run must leave the committed screenshots as they are.

    python3 -m unittest discover -s tests/cli -p 'test_*.py'
"""

import os
import pathlib
import shutil
import stat
import subprocess
import tempfile
import unittest

REPOSITORY = pathlib.Path(__file__).resolve().parents[2]

FAKE_DOTNET = """#!/usr/bin/env bash
# Saves each name in FAKE_RENDERS as a screen in HVO_TERMINAL_RENDERS_DIR, then exits with FAKE_EXIT.
for name in ${FAKE_RENDERS:-}; do
  printf '\\033[1mscreen %s\\033[0m\\n' "$name" > "$HVO_TERMINAL_RENDERS_DIR/$name.ans"
done
exit "${FAKE_EXIT:-0}"
"""


class TerminalScreenshotsTests(unittest.TestCase):
    def setUp(self):
        self.root = pathlib.Path(tempfile.mkdtemp(prefix="hvo-screenshots-"))
        (self.root / "scripts").mkdir()
        (self.root / "tests" / "cli").mkdir(parents=True)
        shutil.copy2(REPOSITORY / "scripts" / "docs:terminal-screenshots", self.root / "scripts")
        shutil.copy2(REPOSITORY / "tests" / "cli" / "ansi-to-svg.py", self.root / "tests" / "cli")
        self.target = self.root / "docs" / "images" / "terminal"
        self.target.mkdir(parents=True)
        (self.target / "install-old.svg").write_text("<svg>old</svg>\n")
        bin_dir = self.root / "bin"
        bin_dir.mkdir()
        dotnet = bin_dir / "dotnet"
        dotnet.write_text(FAKE_DOTNET)
        dotnet.chmod(dotnet.stat().st_mode | stat.S_IXUSR)
        self.environment = dict(os.environ, PATH=f"{bin_dir}:{os.environ['PATH']}")

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def run_script(self, *arguments, renders="", exit_code=0):
        environment = dict(self.environment, FAKE_RENDERS=renders, FAKE_EXIT=str(exit_code))
        return subprocess.run(
            [str(self.root / "scripts" / "docs:terminal-screenshots"), *arguments],
            env=environment, capture_output=True, text=True, check=False)

    def screenshots(self):
        return sorted(path.name for path in self.target.iterdir())

    def test_a_run_that_saves_no_screens_leaves_the_screenshots(self):
        result = self.run_script()
        self.assertEqual(["install-old.svg"], self.screenshots())
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("saved no screens", result.stderr)

    def test_a_failed_test_run_leaves_the_screenshots(self):
        result = self.run_script(renders="install-80x24-1-camera", exit_code=1)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(["install-old.svg"], self.screenshots())

    def test_a_run_that_saves_screens_replaces_them(self):
        result = self.run_script(renders="install-80x24-1-camera ui-80x24-1-status")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["install-80x24-1-camera.svg", "ui-80x24-1-status.svg"], self.screenshots())
        self.assertIn('aria-label="hvo-sky-install"', (self.target / "install-80x24-1-camera.svg").read_text())
        self.assertIn('aria-label="hvo-sky ui"', (self.target / "ui-80x24-1-status.svg").read_text())
        self.assertEqual([], [path.name for path in self.target.parent.iterdir() if path.name != "terminal"],
                         "no staging folder is left beside the screenshots")

    def test_draw_draws_a_folder_in_place(self):
        folder = self.root / "renders"
        folder.mkdir()
        (folder / "install-a.ans").write_text("a\n")
        result = self.run_script("--draw", str(folder))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue((folder / "install-a.svg").is_file())
        self.assertEqual(["install-old.svg"], self.screenshots())

    def test_a_wrong_command_line_is_a_usage_error(self):
        self.assertEqual(2, self.run_script("--draw").returncode)
        self.assertEqual(2, self.run_script("--other", "x").returncode)


if __name__ == "__main__":
    unittest.main()
