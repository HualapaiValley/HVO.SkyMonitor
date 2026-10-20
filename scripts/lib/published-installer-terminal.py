#!/usr/bin/env python3
"""Run a published hvo-sky-install as an operator would, on a terminal and without one (#1317).

The installer is published without Terminal.Gui's syntax highlighting: TextMateSharp, its grammars and Onigwrap's native
Oniguruma (Directory.Build.targets). This shows the single file carries none of them and still works: --help, a run
without a terminal that refuses with exit 2, and the wizard's first page on a pseudo-terminal at 80 x 24 and 120 x 32,
left with Esc (exit 130, nothing saved).

    python3 scripts/lib/published-installer-terminal.py PUBLISHED-INSTALLER

scripts/test:deployment-installer runs it on the installer it publishes.
"""

from __future__ import annotations

import fcntl
import os
import pty
import re
import select
import shutil
import signal
import struct
import subprocess
import sys
import tempfile
import termios
import time
from typing import NoReturn

LEFT_OUT = ("TextMateSharp.dll", "TextMateSharp.Grammars.dll", "Onigwrap.dll", "libonigwrap.so")
CARRIED = ("Terminal.Gui.dll", "Wcwidth.dll")
ESCAPES = re.compile(r"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b[()][A-Za-z0-9]|\x1b[=>78]")
TIMEOUT_SECONDS = 60


def fail(message: str) -> NoReturn:
    print(f"published-installer-terminal: {message}", file=sys.stderr)
    sys.exit(1)


def check_bundle(installer: str) -> None:
    # A single-file bundle names each file it holds in its manifest.
    with open(installer, "rb") as stream:
        content = stream.read()
    for name in CARRIED:
        if name.encode() not in content:
            fail(f"the installer does not hold {name}, so its manifest cannot be read this way")
    held = [name for name in LEFT_OUT if name.encode() in content]
    if held:
        fail(f"the installer holds {', '.join(held)}, which it leaves out")
    print(f"ok: the installer holds {', '.join(CARRIED)} and none of {', '.join(LEFT_OUT)}")


def check_help(installer: str, environment: dict[str, str]) -> None:
    result = subprocess.run([installer, "--help"], env=environment, stdin=subprocess.DEVNULL, capture_output=True,
                            text=True, timeout=TIMEOUT_SECONDS, check=False)
    if result.returncode != 0 or not result.stdout.startswith("Usage: hvo-sky-install"):
        fail(f"--help exited {result.returncode}: {result.stdout[:200]}{result.stderr[:200]}")
    print("ok: --help prints the usage (exit 0)")


def check_no_terminal(installer: str, environment: dict[str, str]) -> None:
    result = subprocess.run([installer, "cameraagent", "install"], env=environment, stdin=subprocess.DEVNULL,
                            capture_output=True, text=True, timeout=TIMEOUT_SECONDS, check=False)
    if result.returncode != 2 or "--friendly-name is required." not in result.stderr:
        fail(f"without a terminal, cameraagent install exited {result.returncode}: {result.stderr[:300]}")
    print("ok: without a terminal nothing is asked and the missing value is named (exit 2)")


def check_wizard(installer: str, environment: dict[str, str], columns: int, rows: int, config: str) -> None:
    child, terminal = pty.fork()
    if child == 0:
        os.execve(installer, [installer, "cameraagent", "install"], environment)
    try:
        fcntl.ioctl(terminal, termios.TIOCSWINSZ, struct.pack("HHHH", rows, columns, 0, 0))
        output = bytearray()
        deadline = time.monotonic() + TIMEOUT_SECONDS
        shown = False
        while time.monotonic() < deadline:
            ready, _, _ = select.select([terminal], [], [], 0.2)
            if not ready:
                continue
            try:
                chunk = os.read(terminal, 65536)
            except OSError:
                break
            if not chunk:
                break
            output += chunk
            screen = ESCAPES.sub("", output.decode("utf-8", "replace"))
            if not shown and all(text in screen for text in ("HVO SKYMONITOR INSTALLER", "CAMERA", "STEP 1 OF")):
                shown = True
                time.sleep(0.5)
                os.write(terminal, b"\x1b")
        if not shown:
            os.kill(child, signal.SIGKILL)
            os.waitpid(child, 0)
            fail(f"the wizard's first page did not appear at {columns} x {rows}")
        _, status = os.waitpid(child, 0)
    finally:
        os.close(terminal)
    code = os.waitstatus_to_exitcode(status)
    screen = ESCAPES.sub("", output.decode("utf-8", "replace"))
    if code != 130 or "Cancelled" not in screen:
        fail(f"Esc on the wizard's first page at {columns} x {rows} exited {code}")
    if os.path.exists(os.path.join(config, "hvo-sky")):
        fail("Esc left saved answers behind")
    print(f"ok: the wizard's first page shows at {columns} x {rows}, and Esc leaves it with nothing saved (exit 130)")


def main() -> None:
    if len(sys.argv) != 2:
        print("Usage: published-installer-terminal.py PUBLISHED-INSTALLER", file=sys.stderr)
        sys.exit(2)
    installer = os.path.abspath(sys.argv[1])
    if not os.access(installer, os.X_OK):
        fail(f"{installer} is not an executable file")
    check_bundle(installer)
    home = tempfile.mkdtemp(prefix="hvo-published-installer-")
    try:
        config = os.path.join(home, ".config")
        environment = {key: value for key, value in os.environ.items() if key not in ("NO_COLOR", "HVO_TERMINAL")}
        environment.update(HOME=home, XDG_CONFIG_HOME=config, DOTNET_BUNDLE_EXTRACT_BASE_DIR=os.path.join(home, "bundle"),
                           TERM="xterm-256color", COLORTERM="truecolor", LANG="C.UTF-8", LC_ALL="C.UTF-8")
        check_help(installer, environment)
        check_no_terminal(installer, environment)
        check_wizard(installer, environment, 80, 24, config)
        check_wizard(installer, environment, 120, 32, config)
    finally:
        shutil.rmtree(home, ignore_errors=True)


if __name__ == "__main__":
    main()
