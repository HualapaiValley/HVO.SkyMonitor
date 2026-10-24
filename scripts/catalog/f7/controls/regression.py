"""Focused private fake-module regressions. Never invokes selected scripts natively."""

import argparse
from contextlib import contextmanager
import copy
import importlib.util
import json
import os
from pathlib import Path
import random
import resource
from unittest.mock import patch

from evidence_codec import (decode_payload, encode_payload, join_evidence,
                            read_record, sha, RECORD_CAP, TOTAL_CAP)

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("f7_private_controls", HERE / "qualification-v8.py")
controls = importlib.util.module_from_spec(spec)
spec.loader.exec_module(controls)


def check(condition, detail):
    if not condition:
        raise AssertionError(detail)


def reject(action, expected=None):
    try:
        action()
    except (ValueError, OSError, controls.OracleReject) as error:
        if expected is not None:
            check(isinstance(error, controls.OracleReject) and error.predicate == expected,
                  "wrong typed rejection: " + repr(error))
        return
    raise AssertionError("invalid evidence accepted")


@contextmanager
def changed(**values):
    originals = {key: getattr(controls, key) for key in values}
    try:
        for key, value in values.items():
            setattr(controls, key, value)
        yield
    finally:
        for key, value in originals.items():
            setattr(controls, key, value)


def json_bytes(value):
    return (json.dumps(value, sort_keys=True, allow_nan=False) + "\n").encode()


def save(path, value):
    with path.open("xb") as stream:
        stream.write(json_bytes(value))


def verify_all90(root):
    inventory = json.loads((HERE / "CURRENT90-SOURCE-LITERALS.json").read_bytes())
    check(inventory["cases"] == controls.V7_CASES and inventory["count"] == 90
          and len({c["id"] for c in controls.V7_CASES}) == 90, "exact current90 case/source inventory")
    check(len(controls.SOURCES) == 5, "exact five whole selected modules")
    check(controls.incremental_run(root) == 0, "full current90 run")
    expected_files = {"completed-summary.json"}
    full_bytes = 0
    for index, case in enumerate(controls.V7_CASES):
        completed_path = root / ("%03d.completed.json" % index)
        completed = json.loads(read_record(completed_path))
        check(completed["case"] == case["id"] and completed["matched"] is True
              and completed["status"] == "COMPLETED", "completed exact case")
        binding = completed["evidenceIndex"]
        index_path = root / binding["name"]
        manifest = json.loads(read_record(index_path, binding))
        raw = join_evidence(index_path)
        # Reexecute the same private selected whole modules and compare the
        # complete serialization, not just counts or a subset of trace keys.
        expected = json_bytes(controls.v7_case(case)["observed"])
        check(raw == expected and sha(raw) == manifest["payloadSha256"], "exact full snapshot reconstruction")
        full_bytes += len(raw)
        expected_files.update((completed_path.name, index_path.name))
        expected_files.update(chunk["name"] for chunk in manifest["chunks"])
    check({p.name for p in root.iterdir()} == expected_files, "no omitted or extra outputs")
    check(json.loads((root / "completed-summary.json").read_bytes())["count"] == 90, "summary count")
    # The argv arrays are intentionally empty fake metadata. The complete
    # ordered gate IDs still pass through the actual selected finalizer.
    case = controls.V7_CASES[0]
    state = controls.exercise(case)
    controls.strict_oracle(case, state)
    result = json.loads(state["rt"].fs.nodes["/f/executor/result.json"]["raw"])
    gates = json.loads((HERE.parent / "source/commands.json").read_bytes())["orderedSteps"]
    check(len(gates) == 119 and len({g["id"] for g in gates}) == 119
          and [g["id"] for g in result["gates"]] == [g["id"] for g in gates]
          and all(g["status"] == "PASS" and g["selectedArgv"] == [] for g in result["gates"]),
          "all119 exact fake gate interactions retained")
    return {"count": 90, "reconstructedSnapshotBytes": full_bytes,
            "caseLogicalBytes": sum(p.stat().st_size for p in root.iterdir()),
            "caseAllocatedBytesIncludingDirectory": allocated(root)}


def typed_terminal_regressions():
    target = dict(next(c for c in controls.NEW_V6_CASES if c["failure"] == "late-open"))
    base = controls.exercise(target)
    controls.strict_oracle(target, base)
    fs = base["rt"].fs
    wait_path = "/f/executor.postseal.actual-wait.json"
    identity_path = "/f/executor.postseal.child-before-work.json"
    mutations = [
        (wait_path, lambda v: v.update(wait4usage=None), "EXEC_USAGE"),
        (wait_path, lambda v: v.update(wait4usage=[]), "EXEC_USAGE"),
        (wait_path, lambda v: v["wait4usage"].update(ru_utime="0.125"), "EXEC_USAGE"),
        (wait_path, lambda v: v.update(WIFEXITED=1), "EXEC_WAIT_STATUS"),
        (wait_path, lambda v: v.update(WIFSIGNALED=0), "EXEC_WAIT_STATUS"),
        (wait_path, lambda v: v.update(WTERMSIG=0), "EXEC_WAIT_STATUS"),
        (wait_path, lambda v: v.update(rawWaitStatus=False), "EXEC_WAIT_STATUS"),
        (identity_path, lambda v: v.update(actual={}), "EXEC_IDENTITY"),
        (identity_path, lambda v: v["actual"].update(uid=True), "EXEC_IDENTITY"),
        (identity_path, lambda v: v.update(noNamespaceMapping=1), "EXEC_IDENTITY"),
    ]
    for path, mutate, predicate in mutations:
        original = dict(fs.nodes[path])
        value = json.loads(original["raw"])
        mutate(value)
        fs.json(path, value)
        reject(lambda: controls.strict_oracle(target, base), predicate)
        fs.nodes[path] = original
    return len(mutations)


def afterseal_terminal_regressions():
    target = next(c for c in controls.V7_CASES
                  if c["id"] == "v5-normal-complete-owned-barrier-and-retention")
    state = controls.exercise(target)
    controls.strict_oracle(target, state)
    fs, rt = state["rt"].fs, state["rt"]
    path = "/f/executor.postseal.check-terminal.json"
    original_node, original_output = dict(fs.nodes[path]), list(rt.output)
    mutations = [
        ("raw-status-bool", lambda v: v["actualWait"].update(rawWaitStatus=False), "CHECK_WAIT_STATUS"),
        ("exited-int", lambda v: v["actualWait"].update(WIFEXITED=1), "CHECK_WAIT_STATUS"),
        ("signaled-int", lambda v: v["actualWait"].update(WIFSIGNALED=0), "CHECK_WAIT_STATUS"),
        ("return-code-bool", lambda v: v.update(actualReturnCode=False), "CHECK_WAIT_STATUS"),
        ("usage-bool", lambda v: v["actualWait"]["wait4usage"].update(ru_utime=False), "CHECK_USAGE"),
        ("nested-return-code-bool", lambda v: v["actualWait"].update(actualReturnCode=False), "CHECK_WAIT_STATUS"),
        ("exit-status-bool", lambda v: v["actualWait"].update(WEXITSTATUS=False), "CHECK_WAIT_STATUS"),
        ("wrong-pid", lambda v: v["actualWait"].update(pid=rt.pid + 1), "CHECK_WAIT_STATUS"),
        ("signal-zero", lambda v: v["actualWait"].update(WTERMSIG=0), "CHECK_WAIT_STATUS"),
        ("usage-wrong-value", lambda v: v["actualWait"]["wait4usage"].update(ru_maxrss=1), "CHECK_USAGE"),
        ("usage-missing-field", lambda v: v["actualWait"]["wait4usage"].pop("ru_stime"), "CHECK_USAGE"),
        ("usage-extra-field", lambda v: v["actualWait"]["wait4usage"].update(extra=0), "CHECK_USAGE"),
        ("wait-provenance-int", lambda v: v["actualWait"].update(rawWait4NotOwnRSS=1), "CHECK_WAIT_SHAPE"),
        ("wait-extra-field", lambda v: v["actualWait"].update(extra=0), "CHECK_WAIT_SHAPE"),
        ("wait-missing-field", lambda v: v["actualWait"].pop("WEXITSTATUS"), "CHECK_WAIT_SHAPE"),
        ("terminal-missing-field", lambda v: v.pop("actualReturnCode"), "CHECK_TERMINAL_SHAPE"),
        ("eof-int", lambda v: v["eof"].update(stderr=1), "CHECK_TERMINAL_SHAPE"),
        ("capture-int", lambda v: v.update(captureComplete=1), "CHECK_TERMINAL_SHAPE"),
        ("no-retry-int", lambda v: v.update(noRetry=1), "CHECK_TERMINAL_SHAPE"),
        ("timeout-float", lambda v: v.update(timeoutSeconds=30.0), "CHECK_TERMINAL_SHAPE"),
    ]
    failures = []
    for name, mutate, predicate in mutations:
        # Exercise raw-only, receipt-only and mutually agreeing substitutions.
        # Equality between two identically malformed artifacts is no authority.
        for channel in ("raw", "receipt", "both"):
            value = json.loads(original_node["raw"])
            mutate(value)
            if channel in ("raw", "both"):
                fs.json(path, value)
            if channel in ("receipt", "both"):
                output = json.loads(original_output[-1])
                output["checkerTerminal"] = value
                rt.output[-1] = json.dumps(output)
            try:
                controls.strict_oracle(target, state)
            except controls.OracleReject as error:
                if error.predicate != predicate:
                    failures.append([name, channel, "wrong predicate", error.predicate])
            except Exception as error:
                failures.append([name, channel, "untyped error", type(error).__name__])
            else:
                failures.append([name, channel, "malformed terminal accepted"])
            finally:
                fs.nodes[path], rt.output[:] = dict(original_node), original_output
    check(not failures, "afterseal terminal regressions: " + json.dumps(failures))
    controls.strict_oracle(target, state)
    return len(mutations) * 3


def codec_regressions(root):
    root.mkdir(mode=0o700)
    rng = random.Random(1188)
    padding = "".join(rng.choice("abcdefghijklmnopqrstuvwxyz0123456789") for _ in range(240000))
    first_case = controls.V7_CASES[0]
    original_snapshot = controls.snapshot

    def extended_snapshot(state):
        return dict(original_snapshot(state), codecRegressionProbe=padding)

    output = root / "multichunk"
    with changed(V7_CASES=[first_case], snapshot=extended_snapshot):
        check(controls.incremental_run(output) == 0, "multi-chunk publication")
        expected = json_bytes(controls.v7_case(first_case)["observed"])
    index_path = output / "000.evidence.index.json"
    index = json.loads(index_path.read_bytes())
    check(index["chunkCount"] >= 3 and join_evidence(index_path) == expected,
          "complete multi-chunk roundtrip")
    packed, meta = encode_payload(expected)
    check(encode_payload(expected) == (packed, meta), "deterministic encoding")
    for key in ("payloadBytes", "encodedBytes", "payloadSha256", "encodedSha256"):
        wrong = dict(meta)
        wrong[key] = meta[key] + 1 if type(meta[key]) is int else "0" * 64
        reject(lambda: decode_payload(packed, wrong))
    for broken in (packed[:-1], packed + b"trailing", packed + packed):
        wrong = dict(meta, encodedBytes=len(broken), encodedSha256=sha(broken))
        reject(lambda: decode_payload(broken, wrong))
    reject(lambda: decode_payload(packed, dict(meta, payloadBytes=16)))
    reject(lambda: decode_payload(packed, dict(meta, payloadBytes=True)))

    # Each damaged copy is retained independently; the passing evidence is
    # never edited. Rebind only the identities changed by copying the files.
    for mutation in ("chunk-bytes", "chunk-missing", "order", "offset", "identity", "raw-hash", "encoded-hash"):
        damaged = root / mutation
        damaged.mkdir(mode=0o700)
        manifest = copy.deepcopy(index)
        info = damaged.stat()
        manifest.update(directory=str(damaged), directoryIdentity=[info.st_uid, info.st_dev, info.st_ino, info.st_mode])
        for chunk in manifest["chunks"]:
            path = damaged / chunk["name"]
            path.write_bytes((output / chunk["name"]).read_bytes())
            info = path.stat()
            chunk.update(uid=info.st_uid, dev=info.st_dev, ino=info.st_ino, mode=info.st_mode,
                         nlink=info.st_nlink, allocatedBytes=info.st_blocks * 512)
        chunk = manifest["chunks"][0]
        if mutation == "chunk-bytes":
            path = damaged / chunk["name"]
            body = path.read_bytes()
            path.write_bytes(bytes([body[0] ^ 1]) + body[1:])
        elif mutation == "chunk-missing":
            (damaged / chunk["name"]).unlink()
        elif mutation == "order":
            manifest["chunks"].reverse()
        elif mutation == "offset":
            chunk["offset"] += 1
        elif mutation == "identity":
            chunk["ino"] += 1
        elif mutation == "raw-hash":
            manifest["payloadSha256"] = "0" * 64
        else:
            manifest["encodedSha256"] = "0" * 64
        corrupted_index = damaged / index_path.name
        save(corrupted_index, manifest)
        reject(lambda: join_evidence(corrupted_index))
    return index["chunkCount"]


def failure_regressions(root):
    root.mkdir(mode=0o700)
    cases = controls.V7_CASES[:2]
    original_exercise = controls.exercise

    def missing_trigger(case):
        state = original_exercise(case)
        if case["id"] == cases[1]["id"]:
            state["rt"].events = []
        return state

    failed = root / "oracle-failure"
    with changed(V7_CASES=cases, exercise=missing_trigger):
        check(controls.incremental_run(failed) == 1, "oracle failure must stay red")
    record = json.loads((failed / "001.failed.json").read_bytes())
    check(record["evidenceProgress"] == {}, "current failure must not cite previous case")
    detail = json.loads(join_evidence(failed / "001.failed-evidence.index.json"))
    expected = controls.snapshot(missing_trigger(cases[1]))
    check(all(detail[key] == value for key, value in expected.items()), "full failed snapshot retained")
    check(detail["adapterPrimaryError"]["binding"]["predicate"] == "NAMED_TRIGGER"
          and not (failed / "001.completed.json").exists()
          and not (failed / "completed-summary.json").exists(), "no false completion after oracle failure")

    for fault in ("partial-write", "file-fsync", "directory-fsync"):
        failed = root / fault
        observed = []
        real_write, real_fsync = os.write, os.fsync

        def write(fd, body):
            path = os.readlink("/proc/self/fd/" + str(fd))
            if fault == "partial-write" and not observed and path.startswith(str(failed)) and ".chunk-" in path:
                observed.append(path)
                real_write(fd, body[:17])
                raise OSError(28, "regression: partial output chunk")
            return real_write(fd, body)

        def fsync(fd):
            path = os.readlink("/proc/self/fd/" + str(fd))
            if not observed and ((fault == "file-fsync" and path.startswith(str(failed)) and ".chunk-" in path)
                                 or (fault == "directory-fsync" and path == str(failed))):
                observed.append(path)
                raise OSError(5, "regression: output sync failure")
            return real_fsync(fd)

        with changed(V7_CASES=cases[:1]), patch("os.write", write), patch("os.fsync", fsync):
            check(controls.incremental_run(failed) == 1, "publication failure must stay red")
        check(len(observed) == 1, "exact output fault reached once")
        detail = json.loads(join_evidence(failed / "000.failed-evidence.index.json"))
        expected_state = original_exercise(cases[0])
        controls.strict_oracle(cases[0], expected_state)
        expected = controls.snapshot(expected_state)
        check(all(detail[key] == value for key, value in expected.items()), "full failed publication snapshot retained")
        publications = detail["adapterPublications"]
        partial = next(p for p in publications if p["status"] == "UNKNOWN_PARTIAL_OR_ABSENT")
        retained = failed / partial["name"]
        check(retained.stat().st_size == (17 if fault == "partial-write" else partial["expectedBytes"]),
              "original partial/full unsynced chunk retained")
        stop = json.loads((failed / "FIRST-STOP.json").read_bytes())
        check(stop["reservedLogicalBytes"] >= sum(p["expectedBytes"] for p in publications)
              and stop["reservedBlockScreenBytes"] >= retained.stat().st_blocks * 512
              and stop["partialNamesMustBeChargedByActualRootFinal"] is True,
              "fallible publication and partials remain charged")
        check(not (failed / "000.completed.json").exists()
              and not (failed / "completed-summary.json").exists(), "no completion after publication fault")
    return 4


def allocated(root):
    return sum(p.lstat().st_blocks * 512 for p in [root, *root.rglob("*")])


def main(output_root):
    resource.setrlimit(resource.RLIMIT_AS, (268435456, 268435456))
    resource.setrlimit(resource.RLIMIT_FSIZE, (TOTAL_CAP, TOTAL_CAP))
    root = output_root.resolve() / "regression"
    root.mkdir(mode=0o700)
    summary = verify_all90(root / "all90")
    summary["extraTypedTerminalRejections"] = typed_terminal_regressions()
    summary["aftersealTypedTerminalRejections"] = afterseal_terminal_regressions()
    summary["roundtripChunkCount"] = codec_regressions(root / "codec")
    summary["failurePreservationCases"] = failure_regressions(root / "failures")
    files = [p for p in root.rglob("*") if p.is_file()]
    summary.update(totalLogicalBytes=sum(p.stat().st_size for p in files),
                   totalAllocatedBytesIncludingDirectories=allocated(root),
                   maximumRecordBytes=max(p.stat().st_size for p in files), fakeOnly=True)
    check(summary["totalLogicalBytes"] + len(json_bytes(summary)) <= TOTAL_CAP
          and summary["maximumRecordBytes"] <= RECORD_CAP,
          "unchanged aggregate/record output caps")
    save(root / "regression-summary.json", summary)
    print(json.dumps(summary, sort_keys=True), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-root", type=Path, required=True)
    main(parser.parse_args().output_root)
