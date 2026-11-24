#!/usr/bin/env python3
"""Measure the exact view DDL and prune SQL on a disposable synthetic SQLite journal."""

import argparse
from contextlib import closing
import json
from pathlib import Path
import re
import sqlite3
import subprocess
import tempfile
from time import perf_counter, sleep


PROCESSING = "src/HVO.SkyMonitor.CameraAgent.Common/Capture/Processing"
PRUNE_FILE = f"{PROCESSING}/CaptureProcessingPersistence.CenteredViewRetention.cs"
BASELINE = "c241a2621b97811b2f9b0e15cf7f9eeb483cf2d0"
PREVIOUS_DISCOVERY = "7950c0f12b26c889e8c9b86f154a6e76e605c91c"
# Cumulative snapshot lengths from the layer-growth fixture, converted to individual row lengths.
# Overlays are represented by equally sized text here. This probe exercises SQLite, not image publication.
SNAPSHOT_BYTES = (593, 2624, 4657, 6690, 8723)


def sql(source, name):
    match = re.search(rf'{re.escape(name)} = """\n(.*?)\n\s*"""', source, re.DOTALL)
    if match is None:
        raise ValueError(f"Missing SQL constant {name}")
    return match[1]


def execute(connection, statements, parameters=None):
    value = None
    values = parameters or {}
    for statement in statements.split(";"):
        if statement.strip():
            cursor = connection.execute(statement, values)
            if cursor.description:
                value = cursor.fetchone()[0]
    return value


def seed(connection, bases, expired, schema, indexes, definition_bytes):
    connection.executescript(
        """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=FULL;
        PRAGMA foreign_keys=ON;
        PRAGMA busy_timeout=5000;
        CREATE TABLE processing_centered_windows(window_sha256 TEXT PRIMARY KEY);
        CREATE TABLE raw_captures(capture_id TEXT PRIMARY KEY, payload_relative_path TEXT, sidecar_relative_path TEXT,
            exposure_started_unix_ms INTEGER, retention_hold INTEGER);
        CREATE TABLE processing_outputs(output_identity_sha256 TEXT PRIMARY KEY, capture_id TEXT);
        """ + schema + indexes
    )
    manifest = "{}"
    definition = json.dumps({"preview": {"product": {"productIdentitySha256": "A" * 64}}}).ljust(definition_bytes)
    layers = ["x" * (size - len(manifest)) for size in SNAPSHOT_BYTES]
    connection.execute("BEGIN")
    for number in range(1, bases + 1):
        capture = f"{number:032x}"
        base = f"{number:064X}"
        reference = f"{capture[:8]}-{capture[8:12]}-{capture[12:16]}-{capture[16:20]}-{capture[20:32]}".upper()
        connection.execute("INSERT INTO raw_captures VALUES (?, ?, ?, ?, 0)",
                           (capture, f"{capture}.bin", f"{capture}.json", int(number > expired)))
        connection.execute("INSERT INTO processing_centered_view_bases VALUES (?, 'stream', 'camera', ?, ?, 0, NULL, 0, ?)",
                           (base, reference, number, definition))
        for position, snapshot in enumerate(layers):
            order = (number - 1) * len(layers) + position + 1
            revision = f"{order:064X}"
            connection.execute("INSERT INTO processing_centered_view_changes VALUES (?, 'stream', ?, ?, 0, ?)",
                               (order, base, revision, int(position == len(layers) - 1)))
            connection.execute("INSERT INTO processing_centered_view_revisions VALUES (?, ?, ?, 0, ?, ?)",
                               (base, revision, order, manifest, snapshot))
        connection.execute("INSERT INTO processing_centered_view_members VALUES (?, ?, ?)",
                           (capture, order - len(layers) + 1, base))
        if number > 1:
            previous_order = order - len(layers)
            connection.execute("UPDATE processing_centered_view_intervals SET ended_order = ?, ended_utc = 0 WHERE started_order = ?",
                               (order, previous_order))
        connection.execute("INSERT INTO processing_centered_view_intervals VALUES (?, 'stream', 'camera', ?, ?, 0, NULL, NULL)",
                           (order, base, revision))
    connection.commit()
    connection.execute("PRAGMA wal_checkpoint(TRUNCATE)")


def view_bytes(connection):
    return connection.execute("""
        SELECT sum(pgsize) FROM dbstat WHERE name IN (
            SELECT name FROM sqlite_schema WHERE tbl_name GLOB 'processing_centered_view_*'
        );
        """).fetchone()[0]


def read_pages(connection, query):
    after = ""
    while page := connection.execute(query, {"after": after, "cutoff": 0, "limit": 512}).fetchall():
        after = page[-1][0]
        yield page


def process_read_bytes():
    return int(Path("/proc/self/io").read_text().splitlines()[0].split()[1])


def measure_discovery(connection, label, source, current):
    connection.execute("PRAGMA shrink_memory")
    before = process_read_bytes()
    started = perf_counter()
    count = sum(sum(row[3] is not None and row[4] is not None and len(row[4]) == 64 and not row[6]
                    for row in page) if current else len(page)
                for page in read_pages(connection, sql(source, "ReadCenteredViewPruneCandidatesSql")))
    elapsed = perf_counter() - started
    return dict(discovery=label, candidates=count, read_bytes=process_read_bytes() - before,
                discovery_seconds=round(elapsed, 4))


def measure(connection, baseline, previous, candidate):
    durations = []

    def transaction(statement):
        connection.execute("BEGIN IMMEDIATE")
        started = perf_counter()
        execute(connection, statement)
        connection.commit()
        durations.append(perf_counter() - started)

    started = perf_counter()
    if baseline:
        connection.execute("CREATE TEMP TABLE centered_view_expiration_candidates (base_id TEXT PRIMARY KEY) STRICT")
        read = sql(previous, "candidates.CommandText")
        insert = "INSERT INTO temp.centered_view_expiration_candidates VALUES (?)"
    else:
        execute(connection, sql(candidate, "CenteredViewPruneSetupSql"))
        read = sql(candidate, "ReadCenteredViewPruneCandidatesSql")
        insert = "INSERT INTO temp.centered_view_expiration_candidates VALUES (?, ?, ?, ?, ?, ?)"
        pause = int(re.search(r"CenteredViewPrunePause = TimeSpan.FromMilliseconds\((\d+)\)", candidate)[1]) / 1000
    for page in read_pages(connection, read):
        if baseline:
            rows = [(row[0],) for row in page]
        else:
            rows = [(row[0], *row[3:6], *row[1:3]) for row in page
                    if row[3] is not None and row[4] is not None and len(row[4]) == 64 and not row[6]]
        connection.executemany(insert, rows)
        if not baseline and len(page) == 512:
            sleep(pause)
    if baseline:
        transaction(sql(previous, "PruneCenteredViewJournalSql"))
    else:
        load = sql(candidate, "LoadCenteredViewPruneBatchSql")
        bound = int(re.search(r"CenteredViewPruneBatchSize = (\d+)", candidate)[1])
        for recomputed, name in ((None, "DetachExpiredCenteredIntervalsSql"),
                                 (1, "DeleteExpiredCenteredBasesSql"), (0, "DeleteExpiredCenteredBasesSql")):
            statement = sql(candidate, name)
            after = ""
            while last := execute(connection, load, {"after": after, "limit": bound, "recomputed": recomputed}):
                after = last
                transaction(statement)
                sleep(pause)
    return perf_counter() - started, durations


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bases", type=int, required=True)
    parser.add_argument("--expired", type=int, required=True)
    parser.add_argument("--definition-bytes", type=int, default=7446)
    parser.add_argument("--discovery-only", action="store_true",
                        help="compare candidate query read bytes on Linux without pruning")
    parser.add_argument("--mode", choices=("before", "after", "both"), default="both")
    args = parser.parse_args()
    if not 0 <= args.expired < args.bases:
        parser.error("require 0 <= expired < bases so one interval stays live")
    if args.definition_bytes < 128:
        parser.error("require at least 128 bytes for the preview identity")
    root = Path(__file__).resolve().parents[4]
    schema_source = (root / PROCESSING / "SqliteCaptureProcessingStore.CenteredViews.cs").read_text()
    schema = sql(schema_source, "CenteredViewSchemaSql") + sql(schema_source, "CenteredViewRuntimeSchemaSql")
    indexes = sql((root / PROCESSING / "CenteredViewRetentionSchema.cs").read_text(), "IndexSql")
    candidate = (root / PRUNE_FILE).read_text()
    previous = subprocess.check_output(["git", "show", f"{BASELINE}:{PRUNE_FILE}"], cwd=root, text=True)
    if args.discovery_only:
        prior = subprocess.check_output(["git", "show", f"{PREVIOUS_DISCOVERY}:{PRUNE_FILE}"], cwd=root, text=True)
        with tempfile.TemporaryDirectory(prefix="centered-retention-") as working:
            with closing(sqlite3.connect(Path(working) / "journal.db", isolation_level=None)) as connection:
                seed(connection, args.bases, args.expired, schema, indexes, args.definition_bytes)
                for label, source, current in (("previous", prior, False), ("current", candidate, True)):
                    result = measure_discovery(connection, label, source, current)
                    assert result["candidates"] == args.expired
                    print(json.dumps(result), flush=True)
        return
    for mode in ("before", "after") if args.mode == "both" else (args.mode,):
        with tempfile.TemporaryDirectory(prefix="centered-retention-") as working:
            with closing(sqlite3.connect(Path(working) / "journal.db", isolation_level=None)) as connection:
                seed(connection, args.bases, args.expired, schema, indexes if mode == "after" else "", args.definition_bytes)
                before = view_bytes(connection)
                total, durations = measure(connection, mode == "before", previous, candidate)
                assert connection.execute("SELECT count(*) FROM processing_centered_view_bases").fetchone()[0] == args.bases - args.expired
                assert not connection.execute("PRAGMA foreign_key_check").fetchall()
                freed = connection.execute("PRAGMA freelist_count").fetchone()[0]
                print(json.dumps(dict(sqlite=sqlite3.sqlite_version, mode=mode, bases=args.bases, expired=args.expired,
                    definition_bytes=args.definition_bytes,
                    max_lock_seconds=round(max(durations, default=0), 4), total_lock_seconds=round(sum(durations), 4),
                    sweep_seconds=round(total, 4), transactions=len(durations), view_bytes_before=before,
                    view_bytes_after=view_bytes(connection), free_pages_after=freed)), flush=True)


if __name__ == "__main__":
    main()
