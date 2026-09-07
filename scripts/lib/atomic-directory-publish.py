#!/usr/bin/env python3
"""Atomically install or exchange a staged directory on Linux."""

from __future__ import annotations

import argparse
import ctypes
import errno
import os
import shutil
import stat
import sys
import tempfile
from typing import NoReturn


AT_FDCWD = -100
RENAME_NOREPLACE = 1
RENAME_EXCHANGE = 2


def fail(message: str) -> NoReturn:
    raise RuntimeError(message)


def load_renameat2():
    libc = ctypes.CDLL(None, use_errno=True)
    try:
        renameat2 = libc.renameat2
    except AttributeError as error:
        raise RuntimeError("Linux libc does not expose renameat2") from error
    renameat2.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
    renameat2.restype = ctypes.c_int
    return renameat2


def rename(renameat2, source: str, destination: str, flags: int) -> None:
    if renameat2(
        AT_FDCWD,
        os.fsencode(source),
        AT_FDCWD,
        os.fsencode(destination),
        flags,
    ) == 0:
        return
    error = ctypes.get_errno()
    raise OSError(error, os.strerror(error), f"{source} -> {destination}")


def require_real_directory(path: str, description: str) -> None:
    try:
        mode = os.lstat(path).st_mode
    except FileNotFoundError as error:
        raise RuntimeError(f"{description} is missing: {path}") from error
    if not stat.S_ISDIR(mode):
        fail(f"{description} is not a real directory: {path}")


def sync_directory(path: str) -> None:
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def publish(staging: str, destination: str) -> str:
    staging = os.path.abspath(staging)
    destination = os.path.abspath(destination)
    staging_parent = os.path.dirname(staging)
    destination_parent = os.path.dirname(destination)
    if staging_parent != destination_parent:
        fail("staging and destination must be siblings for same-filesystem atomic publication")
    require_real_directory(staging, "staging directory")
    require_real_directory(staging_parent, "publication parent")

    renameat2 = load_renameat2()
    try:
        rename(renameat2, staging, destination, RENAME_NOREPLACE)
        mode = "installed"
    except OSError as error:
        if error.errno != errno.EEXIST:
            raise
        require_real_directory(destination, "published destination")
        rename(renameat2, staging, destination, RENAME_EXCHANGE)
        mode = "exchanged"
    sync_directory(staging_parent)
    return mode


def probe(parent: str) -> None:
    parent = os.path.abspath(parent)
    require_real_directory(parent, "publication parent")
    root = tempfile.mkdtemp(prefix=".atomic-directory-publish-probe-", dir=parent)
    left = os.path.join(root, "left")
    right = os.path.join(root, "right")
    initial = os.path.join(root, "initial")
    installed = os.path.join(root, "installed")
    os.mkdir(left)
    os.mkdir(right)
    os.mkdir(initial)
    left_inode = os.lstat(left).st_ino
    right_inode = os.lstat(right).st_ino
    initial_inode = os.lstat(initial).st_ino
    renameat2 = load_renameat2()
    try:
        rename(renameat2, left, right, RENAME_EXCHANGE)
        if os.lstat(left).st_ino != right_inode or os.lstat(right).st_ino != left_inode:
            fail("renameat2 exchange probe did not swap both directory identities")
        rename(renameat2, initial, installed, RENAME_NOREPLACE)
        if os.path.lexists(initial) or os.lstat(installed).st_ino != initial_inode:
            fail("renameat2 no-replace probe did not install the directory identity")
        sync_directory(root)
    finally:
        shutil.rmtree(root)
        sync_directory(parent)


def main() -> int:
    if not sys.platform.startswith("linux"):
        fail("atomic directory publication requires Linux renameat2")
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    publish_parser = subparsers.add_parser("publish")
    publish_parser.add_argument("staging")
    publish_parser.add_argument("destination")
    probe_parser = subparsers.add_parser("probe")
    probe_parser.add_argument("parent")
    arguments = parser.parse_args()
    if arguments.command == "probe":
        probe(arguments.parent)
        return 0
    print(publish(arguments.staging, arguments.destination))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError) as error:
        print(f"Atomic directory publication failed: {error}", file=sys.stderr)
        raise SystemExit(1) from error
