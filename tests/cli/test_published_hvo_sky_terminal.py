"""The published terminal probe bounds every recorded child, even one ignoring F10."""
import importlib.util
import pathlib
import signal
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "published_sky", pathlib.Path(__file__).resolve().parents[2] / "scripts/lib/published-hvo-sky-terminal.py")
PROBE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PROBE)


class PublishedSkyTerminalTests(unittest.TestCase):
    def run_probe(self, shown=True, exits=False, closes=False):
        killed = []
        waits = []
        ticks = iter(n / 10 for n in range(1000))

        def waitpid(child, flags):
            self.assertEqual(12345, child)
            waits.append(flags)
            if killed:
                return child, signal.SIGKILL
            if exits and (flags == 0 or len(waits) > 1):
                return child, 0
            if flags == 0:
                raise AssertionError("blocking wait before the child exited or was killed")
            return 0, 0

        screen = b"HVO SKYMONITOR [NOT SIGNED IN] CAMERAAGENT F10 Quit" if shown else b"starting"
        with patch.object(PROBE.pty, "fork", return_value=(12345, 42)), \
                patch.object(PROBE.fcntl, "ioctl"), \
                patch.object(PROBE.select, "select", return_value=([42], [], [])), \
                patch.object(PROBE.os, "read", side_effect=[screen, b""] if closes else None, return_value=screen), \
                patch.object(PROBE.os, "write"), patch.object(PROBE.os, "close"), \
                patch.object(PROBE.os, "kill", side_effect=lambda child, sig: killed.append((child, sig))), \
                patch.object(PROBE.os, "waitpid", side_effect=waitpid), \
                patch.object(PROBE.os.path, "exists", return_value=False), \
                patch.object(PROBE.time, "monotonic", side_effect=lambda: next(ticks)), \
                patch.object(PROBE.time, "sleep"), patch.object(PROBE, "TIMEOUT_SECONDS", 1):
            if exits:
                PROBE.check_ui("fake", {}, 80, 24, "config")
            else:
                with self.assertRaises(SystemExit) as error:
                    PROBE.check_ui("fake", {}, 80, 24, "config")
                self.assertEqual(1, error.exception.code)
        return killed, waits

    def test_f10_ignored_is_killed_and_reaped_after_deadline(self):
        killed, waits = self.run_probe()
        self.assertEqual([(12345, signal.SIGKILL)], killed)
        self.assertEqual(0, waits[-1])
        self.assertTrue(all(flags == PROBE.os.WNOHANG for flags in waits[:-1]))

    def test_no_page_is_killed_and_reaped(self):
        self.assertEqual([(12345, signal.SIGKILL)], self.run_probe(shown=False)[0])

    def test_closed_terminal_does_not_allow_an_unbounded_wait(self):
        self.assertEqual([(12345, signal.SIGKILL)], self.run_probe(closes=True)[0])

    def test_normal_exit_is_reaped_without_a_kill(self):
        self.assertEqual([], self.run_probe(exits=True)[0])


if __name__ == "__main__":
    unittest.main()
