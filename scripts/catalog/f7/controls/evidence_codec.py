"""Bounded, deterministic, lossless snapshot storage for private fake controls.

The hash/length of the complete JSON bytes is distinct from the encoded stream
and each ordered chunk. This format makes no filesystem crash-survival claim.
"""

import gzip
import hashlib
import json
import os
from pathlib import Path
import stat
import zlib

RECORD_CAP = 65536
TOTAL_CAP = 8388608
SCHEMA = "lossless-ordered-gzip-json-chunks-v1"
CODEC = "gzip-mtime0-v1"


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def require(condition, detail):
    if not condition:
        raise ValueError(detail)


def encode_payload(raw):
    require(type(raw) is bytes and 0 < len(raw) <= TOTAL_CAP, "raw payload bound")
    packed = gzip.compress(raw, compresslevel=9, mtime=0)
    meta = {"codec": CODEC, "payloadBytes": len(raw), "payloadSha256": sha(raw),
            "encodedBytes": len(packed), "encodedSha256": sha(packed)}
    require(decode_payload(packed, meta) == raw, "exact encoding roundtrip")
    return packed, meta


def decode_payload(packed, meta):
    for key in ("payloadBytes", "encodedBytes"):
        require(type(meta[key]) is int and 0 < meta[key] <= TOTAL_CAP, key)
    require(meta["codec"] == CODEC and type(packed) is bytes, "codec")
    require(len(packed) == meta["encodedBytes"] and sha(packed) == meta["encodedSha256"],
            "encoded length/hash")
    require(packed[:3] == b"\x1f\x8b\x08" and packed[4:8] == b"\0" * 4,
            "deterministic gzip header")
    decoder = zlib.decompressobj(16 + zlib.MAX_WBITS)
    raw = decoder.decompress(packed, meta["payloadBytes"] + 1)
    require(decoder.eof and not decoder.unused_data and not decoder.unconsumed_tail,
            "truncated/extra/overflow gzip")
    require(len(raw) == meta["payloadBytes"] and sha(raw) == meta["payloadSha256"],
            "full payload length/hash")
    raw.decode("utf-8", errors="strict")
    return raw


def file_identity(info):
    require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid() and info.st_nlink == 1,
            "regular owned single-link record required")
    return (info.st_uid, info.st_dev, info.st_ino, info.st_mode, info.st_nlink)


def read_record(path, binding=None):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        first = os.fstat(fd)
        identity = file_identity(first)
        require(0 < first.st_size <= RECORD_CAP, "record bound")
        raw = bytearray()
        while len(raw) <= RECORD_CAP:
            chunk = os.read(fd, RECORD_CAP + 1 - len(raw))
            if not chunk:
                break
            raw.extend(chunk)
        last = os.fstat(fd)
        require(identity == file_identity(last) == file_identity(path.lstat())
                and first.st_size == last.st_size == len(raw), "record changed")
        raw = bytes(raw)
        if binding is not None:
            require(identity == tuple(binding[k] for k in ("uid", "dev", "ino", "mode", "nlink")),
                    "record identity mismatch")
            require(type(binding["bytes"]) is int and len(raw) == binding["bytes"]
                    and sha(raw) == binding["sha256"], "record length/hash mismatch")
            require(last.st_blocks * 512 == binding["allocatedBytes"], "record allocation mismatch")
        return raw
    finally:
        os.close(fd)


def join_evidence(index_path):
    """Verify every chunk identity/order/hash, then reconstruct all JSON bytes."""
    index_path = Path(index_path)
    index = json.loads(read_record(index_path))
    require(index["schema"] == SCHEMA, "index schema")
    directory = index_path.parent
    info = directory.lstat()
    require(stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid(), "index directory type/owner")
    require(str(directory) == index["directory"] and directory.is_absolute()
            and directory.resolve() == directory, "index directory path")
    require([info.st_uid, info.st_dev, info.st_ino, info.st_mode] == index["directoryIdentity"],
            "index directory identity")
    count = index["chunkCount"]
    require(type(count) is int and 0 < count <= TOTAL_CAP // RECORD_CAP
            and count == len(index["chunks"]) and index["chunkBytesMaximum"] == RECORD_CAP,
            "chunk count/bound")
    require(type(index["caseIndex"]) is int and index["caseIndex"] >= 0
            and index["label"] in ("evidence", "failed-evidence"), "case/label")
    prefix = "%03d.%s" % (index["caseIndex"], index["label"])
    require(index_path.name == prefix + ".index.json", "index name")
    packed = bytearray()
    for ordinal, chunk in enumerate(index["chunks"]):
        require(type(chunk["ordinal"]) is int and chunk["ordinal"] == ordinal
                and type(chunk["offset"]) is int and chunk["offset"] == len(packed), "chunk order/offset")
        require(chunk["name"] == prefix + ".chunk-%03d.raw" % ordinal, "chunk name")
        raw = read_record(directory / chunk["name"], chunk)
        require(ordinal == count - 1 or len(raw) == RECORD_CAP, "chunk partition")
        require(len(packed) + len(raw) <= TOTAL_CAP, "encoded aggregate bound")
        packed.extend(raw)
    return decode_payload(bytes(packed), index)
