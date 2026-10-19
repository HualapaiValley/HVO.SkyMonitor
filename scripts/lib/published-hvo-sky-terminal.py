#!/usr/bin/env python3
"""Run a published hvo-sky as an operator would, on a terminal and without one (#1317).

hvo-sky is published without Terminal.Gui's syntax highlighting: TextMateSharp, its grammars and Onigwrap's native
Oniguruma (Directory.Build.targets, as for the installer). This shows the single file carries none of them and its
terminal interface still works: --version, `hvo-sky ui` without a terminal, which refuses with exit 2, and the
interface on a pseudo-terminal at 80 x 24 and 120 x 32 with no credentials, where it opens on System, not signed in,
and F10 leaves it (exit 0). No CameraAgent is needed and nothing is saved.

    python3 scripts/lib/published-hvo-sky-terminal.py PUBLISHED-HVO-SKY

scripts/release:assets runs it on the client it publishes for the machine it runs on.
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
CARRIED = ("Terminal.Gui.dll", "Wcwidth.dll", "System.CommandLine.dll")
ESCAPES = re.compile(r"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b[()][A-Za-z0-9]|\x1b[=>78]")
F10 = b"\x1b[21~"
TIMEOUT_SECONDS = 60


def fail(message: str) -> NoReturn:
    print(f"published-hvo-sky-terminal: {message}", file=sys.stderr)
    sys.exit(1)


def check_bundle(client: str) -> None:
    # A single-file bundle names each file it holds in its manifest.
    with open(client, "rb") as stream:
        content = stream.read()
    for name in CARRIED:
        if name.encode() not in content:
            fail(f"hvo-sky does not hold {name}, so its manifest cannot be read this way")
    held = [name for name in LEFT_OUT if name.encode() in content]
    if held:
        fail(f"hvo-sky holds {', '.join(held)}, which it leaves out")
    print(f"ok: hvo-sky holds {', '.join(CARRIED)} and none of {', '.join(LEFT_OUT)}")


def check_version(client: str, environment: dict[str, str]) -> None:
    result = subprocess.run([client, "--version"], env=environment, stdin=subprocess.DEVNULL, capture_output=True,
                            text=True, timeout=TIMEOUT_SECONDS, check=False)
    if result.returncode != 0 or "+" not in result.stdout:
        fail(f"--version exited {result.returncode}: {result.stdout[:200]}{result.stderr[:200]}")
    print(f"ok: --version prints {result.stdout.strip()} (exit 0)")


def check_no_terminal(client: str, environment: dict[str, str]) -> None:
    result = subprocess.run([client, "ui"], env=environment, stdin=subprocess.DEVNULL, capture_output=True,
                            text=True, timeout=TIMEOUT_SECONDS, check=False)
    if result.returncode != 2 or "needs a terminal" not in result.stderr:
        fail(f"without a terminal, hvo-sky ui exited {result.returncode}: {result.stderr[:300]}")
    print("ok: without a terminal hvo-sky ui opens nothing and says why (exit 2)")


def check_ui(client: str, environment: dict[str, str], columns: int, rows: int, config: str) -> None:
    child, terminal = pty.fork()
    if child == 0:
        os.execve(client, [client, "ui"], environment)
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
            if not shown and all(text in screen for text in ("HVO SKYMONITOR", "[NOT SIGNED IN]", "CAMERAAGENT", "F10 Quit")):
                shown = True
                time.sleep(0.5)
                os.write(terminal, F10)
        if not shown:
            os.kill(child, signal.SIGKILL)
            os.waitpid(child, 0)
            fail(f"the interface's System page did not appear at {columns} x {rows}")
        _, status = os.waitpid(child, 0)
    finally:
        os.close(terminal)
    code = os.waitstatus_to_exitcode(status)
    if code != 0:
        fail(f"F10 on the interface at {columns} x {rows} exited {code}")
    if os.path.exists(os.path.join(config, "hvo-sky")):
        fail("the interface saved something without being asked to")
    print(f"ok: the interface opens on System, not signed in, at {columns} x {rows}, and F10 leaves it (exit 0)")


def main() -> None:
    if len(sys.argv) != 2:
        print("Usage: published-hvo-sky-terminal.py PUBLISHED-HVO-SKY", file=sys.stderr)
        sys.exit(2)
    client = os.path.abspath(sys.argv[1])
    if not os.access(client, os.X_OK):
        fail(f"{client} is not an executable file")
    check_bundle(client)
    home = tempfile.mkdtemp(prefix="hvo-published-sky-")
    try:
        config = os.path.join(home, ".config")
        drop = ("NO_COLOR", "HVO_TERMINAL", "HVO_SKY_URL", "HVO_SKY_API_KEY")
        environment = {key: value for key, value in os.environ.items() if key not in drop}
        environment.update(HOME=home, XDG_CONFIG_HOME=config, DOTNET_BUNDLE_EXTRACT_BASE_DIR=os.path.join(home, "bundle"),
                           TERM="xterm-256color", COLORTERM="truecolor", LANG="C.UTF-8", LC_ALL="C.UTF-8")
        check_version(client, environment)
        check_no_terminal(client, environment)
        check_ui(client, environment, 80, 24, config)
        check_ui(client, environment, 120, 32, config)
    finally:
        shutil.rmtree(home, ignore_errors=True)


if __name__ == "__main__":
    main()
