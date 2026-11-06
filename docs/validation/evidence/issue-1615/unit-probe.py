"""Disposable primitive comparison only; NOT an installer or crash-safe restore.

Run as an ordinary user inside a newly owned, mounted Btrfs image. No live hosts.
Usage: python3 unit-probe.py MOUNT PAYLOAD_DIRECTORY RESULT_JSON
"""
import hashlib
from contextlib import closing
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import time

root, payload, output = map(Path, sys.argv[1:])
assert root.is_dir() and not (root / 'unit').exists()


def run(*args):
    return subprocess.check_output(args, text=True).strip()


def sync():
    run('btrfs', 'filesystem', 'sync', str(root))


def sha(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def facts(unit):
    with closing(sqlite3.connect(unit / 'state.db')) as db:
        assert db.execute('PRAGMA integrity_check').fetchone() == ('ok',)
        assert db.execute('PRAGMA user_version').fetchone() == (12,)
        assert db.execute('SELECT count(*) FROM captures').fetchone() == (200000,)
        assert db.execute("SELECT count(*) FROM sqlite_master WHERE name='candidate'").fetchone() == (0,)
        rows = hashlib.sha256()
        for row in db.execute('SELECT id,path,sha256 FROM captures ORDER BY id'):
            rows.update(json.dumps(row, separators=(',', ':')).encode())
        refs = db.execute('SELECT DISTINCT path,sha256 FROM captures ORDER BY path').fetchall()
    inventory = {str(p.relative_to(unit)): sha(p) for p in sorted(unit.rglob('*'))
                 if p.is_file() and p.name not in ('state.db', 'state.db-wal', 'state.db-shm')}
    for path, digest in refs:
        assert inventory[path] == digest
    return {'rows_sha256': rows.hexdigest(), 'files_sha256': inventory}


def seconds(action):
    start = time.perf_counter()
    value = action()
    return round(time.perf_counter() - start, 6), value


def used():
    # Filesystem-wide allocated usage; includes metadata and both units.
    text = run('sudo', '-n', 'btrfs', 'filesystem', 'usage', '-b', str(root))
    return int(next(line.split(':', 1)[1].strip() for line in text.splitlines()
                    if line.strip().startswith('Used:')))


unit = root / 'unit'
neighbor = root / 'sibling'
run('btrfs', 'subvolume', 'create', str(unit))
run('btrfs', 'subvolume', 'create', str(neighbor))
(neighbor / 'unchanged').write_text('sibling-generation-7\n')
shutil.copytree(payload, unit / 'payload')
(unit / 'config').write_bytes(b'c' * 32768)
(unit / 'key').write_bytes(b'k' * 32768)
paths = [(str(p.relative_to(unit)), sha(p)) for p in sorted((unit / 'payload').rglob('*')) if p.is_file()]
with closing(sqlite3.connect(unit / 'state.db')) as db:
    db.execute('PRAGMA journal_mode=WAL')
    db.execute('PRAGMA synchronous=FULL')
    db.execute('CREATE TABLE captures(id INTEGER PRIMARY KEY,path TEXT NOT NULL,sha256 TEXT NOT NULL)')
    db.executemany('INSERT INTO captures VALUES(?,?,?)',
                   ((i, *paths[(i - 1) % len(paths)]) for i in range(1, 200001)))
    db.execute('PRAGMA user_version=12')
    db.execute('CREATE TABLE checkpoint_marker(value INTEGER NOT NULL)')
    db.execute('INSERT INTO checkpoint_marker VALUES(0)')
    db.commit()
sync()
expected = facts(unit)
neighbor_hash = sha(neighbor / 'unchanged')
fixture = {'rows': 200000, 'payload_files': len(paths),
           'payload_bytes': sum(p.stat().st_size for p in (unit / 'payload').rglob('*') if p.is_file()),
           'database_bytes': (unit / 'state.db').stat().st_size, 'mutable_file_bytes': 65536,
           'facts': expected, 'sibling_sha256': neighbor_hash}
results = []
for trial in range(1, 4):
    for mechanism in ('sqlite-copy-and-file-copy', 'btrfs-unit'):
        checkpoint = root / f'checkpoint-{trial}-{mechanism}'
        # Keep a committed WAL present when establishing either checkpoint.
        db = sqlite3.connect(unit / 'state.db')
        db.execute('PRAGMA journal_mode=WAL')
        db.execute('PRAGMA wal_autocheckpoint=0')
        db.execute('UPDATE checkpoint_marker SET value=value+1')
        db.commit()
        marker = db.execute('SELECT value FROM checkpoint_marker').fetchone()[0]
        wal_bytes = (unit / 'state.db-wal').stat().st_size
        assert wal_bytes > 0
        sync()
        before = used()

        def checkpoint_action():
            if mechanism == 'btrfs-unit':
                run('btrfs', 'subvolume', 'snapshot', '-r', str(unit), str(checkpoint))
            else:
                checkpoint.mkdir()
                target = sqlite3.connect(checkpoint / 'state.db')
                db.backup(target)
                target.close()
                for name in ('config', 'key'):
                    shutil.copyfile(unit / name, checkpoint / name)
                    with (checkpoint / name).open('rb') as stream:
                        os.fsync(stream.fileno())
                with (checkpoint / 'state.db').open('rb') as stream:
                    os.fsync(stream.fileno())
            sync()

        checkpoint_seconds, _ = seconds(checkpoint_action)
        after_checkpoint = used()
        db.close()

        def mutate():
            with closing(sqlite3.connect(unit / 'state.db')) as candidate:
                candidate.execute('UPDATE captures SET sha256=lower(hex(randomblob(32)))')
                candidate.commit()
                candidate.execute('DROP TABLE captures')
                candidate.execute('CREATE TABLE candidate(incompatible TEXT)')
                candidate.execute('UPDATE checkpoint_marker SET value=-1')
                candidate.commit()
            (unit / 'config').write_text('candidate-config')
            (unit / 'key').write_text('candidate-key')
            sync()

        mutation_seconds, _ = seconds(mutate)
        after_mutation = used()

        def restore():
            if mechanism == 'btrfs-unit':
                staged = root / 'replacement'
                run('btrfs', 'subvolume', 'snapshot', str(checkpoint), str(staged))
                # Deliberately only a microprobe: two renames require a durable
                # external selection journal before this could be product code.
                unit.rename(root / 'discarded')
                staged.rename(unit)
            else:
                for suffix in ('-wal', '-shm', '-journal'):
                    (unit / ('state.db' + suffix)).unlink(missing_ok=True)
                for name in ('state.db', 'config', 'key'):
                    shutil.copyfile(checkpoint / name, unit / (name + '.staged'))
                    with (unit / (name + '.staged')).open('rb') as stream:
                        os.fsync(stream.fileno())
                    (unit / (name + '.staged')).replace(unit / name)
            sync()

        restore_seconds, _ = seconds(restore)
        verification_seconds, observed = seconds(lambda: facts(unit))
        assert observed == expected
        with closing(sqlite3.connect(unit / 'state.db')) as restored:
            assert restored.execute('SELECT value FROM checkpoint_marker').fetchone() == (marker,)
        assert sha(neighbor / 'unchanged') == neighbor_hash
        results.append({'trial': trial, 'mechanism': mechanism, 'wal_bytes_at_checkpoint': wal_bytes,
                        'checkpoint_seconds': checkpoint_seconds, 'mutation_seconds': mutation_seconds,
                        'restore_seconds': restore_seconds, 'verification_seconds': verification_seconds,
                        'used_before_bytes': before, 'used_after_checkpoint_bytes': after_checkpoint,
                        'used_after_mutation_bytes': after_mutation,
                        'restored_facts_equal': True, 'sibling_equal': True,
                        'wal_marker_restored': marker})
        if mechanism == 'btrfs-unit':
            # This host permits unprivileged snapshot creation but refused
            # deletion of a readonly checkpoint, even with user_subvol_rm_allowed.
            run('sudo', '-n', 'btrfs', 'subvolume', 'delete', str(checkpoint))
            run('btrfs', 'subvolume', 'delete', str(root / 'discarded'))
        else:
            shutil.rmtree(checkpoint)
        sync()
output.write_text(json.dumps({'fixture': fixture, 'operations': results}, indent=2) + '\n')
