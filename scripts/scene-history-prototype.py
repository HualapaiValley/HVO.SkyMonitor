#!/usr/bin/env python3
"""Disposable CameraAgent history layout experiment; never opens an installation DB."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import random
import sqlite3
import statistics
import tempfile
import time


def bytes_on_disk(root):
    return sum(path.stat().st_size for path in root.rglob("*") if path.is_file())


def measure(fn, iterations=100):
    samples = []
    for _ in range(iterations):
        start = time.perf_counter_ns()
        fn()
        samples.append((time.perf_counter_ns() - start) / 1e6)
    samples.sort()
    return {"median_ms": round(statistics.median(samples), 4),
            "p95_ms": round(samples[int(.95 * (len(samples) - 1))], 4)}


def run(root, width, height, count, payload_count, payload_bytes, seed):
    root.mkdir(parents=True, exist_ok=False)
    baseline = root / "baseline"
    candidate = root / "candidate"
    baseline.mkdir()
    candidate.mkdir()
    (candidate / "hours").mkdir()
    (candidate / "products").mkdir()
    db_old = sqlite3.connect(baseline / "history.db")
    db_new = sqlite3.connect(candidate / "mutable.db")
    for db in (db_old, db_new):
        db.execute("PRAGMA journal_mode=WAL")
        db.execute("PRAGMA synchronous=FULL")
    db_old.executescript("""CREATE TABLE captures (sequence INTEGER PRIMARY KEY, hour INTEGER NOT NULL,
        raw_json TEXT NOT NULL, derivative_json TEXT NOT NULL, status TEXT NOT NULL);
        CREATE INDEX capture_hour ON captures(hour, sequence);""")
    db_new.executescript("""CREATE TABLE capture_index (sequence INTEGER PRIMARY KEY, hour INTEGER NOT NULL,
        offset INTEGER NOT NULL, length INTEGER NOT NULL, raw_sha256 TEXT NOT NULL, status TEXT NOT NULL);
        CREATE INDEX capture_hour ON capture_index(hour, sequence);""")
    # Scene-sized manifest geometry, not a claim about a particular camera's star density.
    geometry = "x" * 256_000
    random_source = random.Random(seed)
    start = time.perf_counter()
    product_total = 0
    for hour in range((count + 359) // 360):
        with (candidate / "hours" / f"{hour:05d}.jsonl").open("wb") as stream:
            for sequence in range(hour * 360, min((hour + 1) * 360, count)):
                identity = hashlib.sha256(f"{seed}:{sequence}".encode()).hexdigest()
                raw = json.dumps({"sequence": sequence, "width": width, "height": height,
                                  "scene": {"identity": identity, "objects": geometry}}, separators=(",", ":"))
                compact = json.dumps({"sequence": sequence, "scene": {"identity": identity}}, separators=(",", ":"))
                # Four pixel-derivative descriptors in the observed journal; retain raw geometry.
                old_derivatives = json.dumps([raw] * 4, separators=(",", ":"))
                new_derivatives = [compact] * 4
                db_old.execute("INSERT INTO captures VALUES (?, ?, ?, ?, ?)",
                               (sequence, hour, raw, old_derivatives, "available"))
                fact = {"sequence": sequence, "hour": hour, "capture_sha256": hashlib.sha256(raw.encode()).hexdigest(),
                        "raw": raw, "derivatives": new_derivatives, "products": []}
                if sequence < payload_count:
                    # Deterministic incompressible stress bytes represent an opaque large overlay/product.
                    data = random_source.randbytes(payload_bytes)
                    digest = hashlib.sha256(data).hexdigest()
                    relative = f"products/{sequence:08d}.bin"
                    (candidate / relative).write_bytes(data)
                    fact["products"].append({"path": relative, "length": len(data), "sha256": digest})
                    product_total += len(data)
                line = (json.dumps(fact, separators=(",", ":")) + "\n").encode()
                offset = stream.tell()
                stream.write(line)
                db_new.execute("INSERT INTO capture_index VALUES (?, ?, ?, ?, ?, ?)",
                               (sequence, hour, offset, len(line), fact["capture_sha256"], "available"))
        # The facts file is published only after all its rows are written and synced.
        # This prototype does not implement crash-safe DB/file commit coordination.
        with (candidate / "hours" / f"{hour:05d}.jsonl").open("rb") as stream:
            os.fsync(stream.fileno())
    db_old.commit()
    db_new.commit()
    write_seconds = time.perf_counter() - start

    def lookup_new(sequence):
        hour, offset, length, digest, status = db_new.execute(
            "SELECT hour, offset, length, raw_sha256, status FROM capture_index WHERE sequence=?", (sequence,)).fetchone()
        with (candidate / "hours" / f"{hour:05d}.jsonl").open("rb") as stream:
            stream.seek(offset)
            fact = json.loads(stream.read(length))
        assert fact["sequence"] == sequence and status in ("available", "retained")
        assert hashlib.sha256(fact["raw"].encode()).hexdigest() == digest
        return fact

    rng = random.Random(seed)
    selections = [rng.randrange(count) for _ in range(100)]
    index = 0

    def next_sequence():
        nonlocal index
        value = selections[index % len(selections)]
        index += 1
        return value

    old_lookup = measure(lambda: db_old.execute("SELECT raw_json FROM captures WHERE sequence=?",
                                                 (next_sequence(),)).fetchone())
    new_lookup = measure(lambda: lookup_new(next_sequence()))
    old_page = measure(lambda: db_old.execute("SELECT sequence FROM captures WHERE sequence>? ORDER BY sequence LIMIT 50",
                                               (count // 2,)).fetchall())
    new_page = measure(lambda: db_new.execute("SELECT sequence FROM capture_index WHERE sequence>? ORDER BY sequence LIMIT 50",
                                               (count // 2,)).fetchall())
    assert db_old.execute("SELECT sequence FROM captures WHERE sequence>? ORDER BY sequence LIMIT 50",
                          (count // 2,)).fetchall() == db_new.execute(
                              "SELECT sequence FROM capture_index WHERE sequence>? ORDER BY sequence LIMIT 50",
                              (count // 2,)).fetchall()
    db_new.execute("UPDATE capture_index SET status='retained' WHERE sequence=?", (0,))
    db_new.commit()
    assert db_new.execute("SELECT status FROM capture_index WHERE sequence=0").fetchone()[0] == "retained"
    assert lookup_new(1)["sequence"] == 1
    if payload_count:
        first = lookup_new(0)["products"][0]
        data = (candidate / first["path"]).read_bytes()
        assert len(data) == first["length"] and hashlib.sha256(data).hexdigest() == first["sha256"]
    db_old.close()
    db_new.close()
    return {"dimensions": [width, height], "captures": count, "payload_samples": payload_count,
            "payload_bytes_each": payload_bytes, "payload_bytes_total": product_total,
            "baseline_db_bytes": (baseline / "history.db").stat().st_size,
            "candidate_mutable_db_bytes": (candidate / "mutable.db").stat().st_size,
            "candidate_facts_bytes": bytes_on_disk(candidate / "hours"),
            "candidate_total_excluding_products": bytes_on_disk(candidate) - product_total,
            "write_seconds": round(write_seconds, 3), "baseline_lookup": old_lookup,
            "candidate_verified_lookup": new_lookup, "baseline_page": old_page, "candidate_page": new_page,
            "checks": "50-row keyset equality, late mutable status, raw SHA-256, product SHA-256"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, help="New disposable output directory (must not exist)")
    parser.add_argument("--captures", type=int, default=360)
    parser.add_argument("--payload-samples", type=int, default=30)
    parser.add_argument("--payload-mib", type=int, default=2)
    parser.add_argument("--seed", type=int, default=1055)
    args = parser.parse_args()
    if args.captures < 1 or not 0 <= args.payload_samples <= args.captures or args.payload_mib < 0:
        parser.error("invalid capture count, payload sample count, or payload size")
    root = args.root or Path(tempfile.mkdtemp(prefix="scene-1055-")) / "runs"
    if root.exists():
        parser.error("output root must not exist")
    root.mkdir(parents=True)
    results = [run(root / label, width, height, args.captures, args.payload_samples,
                   args.payload_mib * 1024 * 1024, args.seed)
               for label, width, height in (("w1", 1936, 1216), ("w6", 3552, 3552))]
    print(json.dumps({"root": str(root), "seed": args.seed, "results": results}, indent=2))


if __name__ == "__main__":
    main()
