#!/usr/bin/env python3
"""Fault-injected disposable hourly facts publication; not a production storage API."""

import hashlib
import json
import os
from pathlib import Path
import sqlite3
import tempfile


def sync_dir(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def facts(hour):
    return [{"hour": hour, "sequence": hour * 360 + i, "raw_sha256": hashlib.sha256(
        f"raw:{hour}:{i}".encode()).hexdigest()} for i in range(3)]


def initialize(root):
    root.mkdir()
    (root / "hours").mkdir()
    db = sqlite3.connect(root / "mutable.db")
    db.execute("PRAGMA journal_mode=WAL")
    db.execute("PRAGMA synchronous=FULL")
    db.executescript("""CREATE TABLE hours (hour INTEGER PRIMARY KEY, sha256 TEXT NOT NULL);
        CREATE TABLE capture_index (sequence INTEGER PRIMARY KEY, hour INTEGER NOT NULL,
        offset INTEGER NOT NULL, length INTEGER NOT NULL, status TEXT NOT NULL);""")
    db.close()


def scan(path, hour):
    data = path.read_bytes()
    rows = []
    offset = 0
    for line in data.splitlines(keepends=True):
        assert line.endswith(b"\n"), "torn record"
        fact = json.loads(line)
        assert fact["hour"] == hour and fact["sequence"] == hour * 360 + len(rows)
        assert len(fact["raw_sha256"]) == 64
        rows.append((fact["sequence"], hour, offset, len(line)))
        offset += len(line)
    assert rows and offset == len(data)
    return hashlib.sha256(data).hexdigest(), rows


def publish(root, hour, fail=None):
    hours = root / "hours"
    staged = hours / f"{hour:05d}.pending"
    sealed = hours / f"{hour:05d}.jsonl"
    assert not staged.exists() and not sealed.exists()
    with staged.open("wb") as stream:
        for fact in facts(hour):
            stream.write((json.dumps(fact, separators=(",", ":")) + "\n").encode())
        stream.flush()
        os.fsync(stream.fileno())
    sync_dir(hours)
    if fail == "staged":
        return
    os.replace(staged, sealed)
    sync_dir(hours)
    if fail == "renamed":
        return
    digest, rows = scan(sealed, hour)
    db = sqlite3.connect(root / "mutable.db")
    try:
        with db:
            db.execute("INSERT INTO hours VALUES (?, ?)", (hour, digest))
            db.executemany("INSERT INTO capture_index VALUES (?, ?, ?, ?, 'available')", rows)
    finally:
        db.close()


def recover(root):
    hours = root / "hours"
    db = sqlite3.connect(root / "mutable.db")
    discarded = 0
    indexed = 0
    try:
        for staged in sorted(hours.glob("*.pending")):
            staged.unlink()
            discarded += 1
        if discarded:
            sync_dir(hours)
        for sealed in sorted(hours.glob("*.jsonl")):
            hour = int(sealed.stem)
            digest, rows = scan(sealed, hour)
            previous = db.execute("SELECT sha256 FROM hours WHERE hour=?", (hour,)).fetchone()
            if previous:
                assert previous[0] == digest, "indexed immutable hour was changed"
                existing = db.execute("SELECT sequence, hour, offset, length FROM capture_index WHERE hour=? ORDER BY sequence",
                                      (hour,)).fetchall()
                assert existing == rows, "indexed offsets were changed"
                continue
            with db:
                db.execute("INSERT INTO hours VALUES (?, ?)", (hour, digest))
                db.executemany("INSERT INTO capture_index VALUES (?, ?, ?, ?, 'available')", rows)
            indexed += 1
        assert all((hours / f"{hour:05d}.jsonl").exists() and
                   scan(hours / f"{hour:05d}.jsonl", hour)[0] == digest
                   for hour, digest in db.execute("SELECT hour, sha256 FROM hours"))
        return {"discarded_unsealed": discarded, "indexed_sealed": indexed,
                "rows": db.execute("SELECT count(*) FROM capture_index").fetchone()[0]}
    finally:
        db.close()


def main():
    results = {}
    with tempfile.TemporaryDirectory(prefix="scene-1055-recovery-") as temporary:
        for fault in ("staged", "renamed", None):
            label = fault or "committed"
            root = Path(temporary) / label
            initialize(root)
            publish(root, 0, fault)
            first = recover(root)
            second = recover(root)
            db = sqlite3.connect(root / "mutable.db")
            try:
                assert first["rows"] == (0 if fault == "staged" else 3)
                assert second["indexed_sealed"] == 0 and second["discarded_unsealed"] == 0
                if fault != "staged":
                    db.execute("UPDATE capture_index SET status='retained' WHERE sequence=0")
                    db.commit()
                    assert recover(root)["rows"] == 3
                    assert db.execute("SELECT status FROM capture_index WHERE sequence=0").fetchone()[0] == "retained"
            finally:
                db.close()
            results[label] = {"first_recovery": first, "idempotent_recovery": second}
        root = Path(temporary) / "corrupt"
        initialize(root)
        publish(root, 0)
        sealed = root / "hours" / "00000.jsonl"
        with sealed.open("ab") as stream:
            stream.write(b"corrupt")
        try:
            recover(root)
        except (AssertionError, json.JSONDecodeError):
            results["corrupt"] = "detected, not silently indexed"
        else:
            raise AssertionError("mutated immutable facts accepted")
    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
