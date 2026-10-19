#!/usr/bin/env python3
"""Generate and check the NOAA SWPC v1 space-weather fixture pack (issue #1398).

The pack is test data. This script reads only files inside its own directory,
plus an OVATION original that ``write`` receives explicitly. Run it with an
isolated interpreter so nothing is imported from the working directory:

    python3 -I tests/fixtures/space-weather/swpc-v1/fixtures.py check
    python3 -I tests/fixtures/space-weather/swpc-v1/fixtures.py write \
        [--ovation-original <path to the captured ovation_aurora_latest.json>]

``check`` verifies every manifest hash, regenerates every scenario and compares
it byte for byte, and replays every scenario through the reference model below,
comparing the model with the hand-authored expectations. ``write`` regenerates
the derived files and the manifest. It rebuilds the OVATION subset only when the
original is supplied and matches the recorded hash.

The reference model is a specification aid, not production code. The policy it
encodes is described in docs/contracts/space-weather-facts-v1.md.
"""

from __future__ import annotations

import argparse
import base64
import copy
import datetime as dt
import hashlib
import json
import math
import re
import sys
from pathlib import Path
from zoneinfo import ZoneInfo

PACK = Path(__file__).resolve().parent
CAPTURE = "capture-20261019T1857Z"
SCENARIO_SCHEMA = "hvo-space-weather-swpc-scenario-v1"
MANIFEST_SCHEMA = "hvo-space-weather-swpc-fixtures-v1"
TEXT_CAPTURE_SCHEMA = "hvo-space-weather-swpc-text-capture-v1"
OVATION_SUBSET_SCHEMA = "hvo-space-weather-swpc-ovation-subset-v1"
OVATION_SUBSET = "ovation-aurora-site-sector.json"
OVATION_ORIGINAL_SHA256 = "3B4CE243EFE8B6B44EAF3692EAFD0517BA12A6FBDB3357A41AF45505BF7C760C"
OVATION_ORIGINAL_BYTES = 918460
OVATION_SECTOR = {"lonMin": 236, "lonMax": 256, "latMin": -5, "latMax": 90}
THREE_DAY_ORIGINAL = CAPTURE + "/3-day-forecast.original.json"
THREE_DAY_NORMALIZED = CAPTURE + "/3-day-forecast.txt"
RETRIEVED_BY = "claude-code:anthropic:home-dev-01:d221666e"

UTC = dt.timezone.utc
MONTHS = {m: i for i, m in enumerate(
    ("Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"), 1)}

# Fixture site: the configured CameraAgent observatory location.
HVO_SITE = {
    "siteId": "hvo",
    "location": {"latitudeDeg": 35.5599378, "longitudeDeg": -113.9119818},
    "timeZoneId": "America/Phoenix",
}
NO_LOCATION_SITE = {"siteId": "unlocated", "location": None, "timeZoneId": "America/Phoenix"}

# Selected/proposed policy values (see the contract document).
POLICY = {
    "dipolePole": {"latitudeDeg": 80.8, "longitudeDeg": -72.6},
    "products": {
        "alerts": {"path": "/products/alerts.json", "baseSeconds": 120, "maxBytes": 1048576,
                   "media": "application/json", "outdatedSeconds": 3600},
        "scales": {"path": "/products/noaa-scales.json", "baseSeconds": 300, "maxBytes": 65536,
                   "media": "application/json", "outdatedSeconds": 10800},
        "kp-forecast": {"path": "/products/noaa-planetary-k-index-forecast.json", "baseSeconds": 900,
                        "maxBytes": 262144, "media": "application/json", "outdatedSeconds": 21600},
        "3-day-forecast": {"path": "/text/3-day-forecast.txt", "baseSeconds": 1800, "maxBytes": 65536,
                           "media": "text/plain", "outdatedSeconds": 93600},
        "ovation": {"path": "/json/ovation_aurora_latest.json", "baseSeconds": 600, "maxBytes": 4194304,
                    "media": "application/json", "outdatedSeconds": 3600},
    },
    "freshMultiplier": 3,
    "minimumAttemptSpacingSeconds": 60,
    "transientBackoffCapSeconds": 1800,
    "retryAfterCapSeconds": 21600,
    "rateLimitedDefaultSeconds": 1800,
    "rateLimitedHaltCount": 3,
    "rejectedAttentionCount": 3,
    "maxRecords": 2000,
    "maxMessageBytes": 16384,
    "recentSeconds": 86400,
    "newGraceSeconds": 300,
    "activeCapSeconds": 345600,
    "retentionSeconds": 3024000,
}
TYPICAL_LATITUDE = {"K4": 65, "G1": 60, "G2": 55, "G3": 50, "G4": 45, "G5": 40}


class ScenarioError(Exception):
    pass


# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------

def ts(value):
    if value is None:
        return None
    return dt.datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=UTC)


def fmt(value):
    return None if value is None else value.strftime("%Y-%m-%dT%H:%M:%SZ")


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def normalize_text(text: str) -> str:
    """CRLF to LF, trim trailing blanks per line, drop trailing blank lines, end with LF."""
    lines = [line.rstrip(" \t\r") for line in text.replace("\r\n", "\n").split("\n")]
    while lines and lines[-1] == "":
        lines.pop()
    return "".join(line + "\n" for line in lines)


def encode_alerts(records) -> str:
    """Reproduce the byte layout of products/alerts.json, including its escaped '/'."""
    return json.dumps(records, separators=(",", ":"), ensure_ascii=True).replace("/", "\\/")


def dump_json(obj) -> bytes:
    return (json.dumps(obj, indent=2, ensure_ascii=True) + "\n").encode("ascii")


def parse_swpc_time(text):
    m = re.fullmatch(r"(\d{4}) ([A-Z][a-z]{2}) (\d{2}) (\d{2})(\d{2}) UTC", text.strip())
    if not m or m.group(2) not in MONTHS:
        return None
    try:
        return dt.datetime(int(m.group(1)), MONTHS[m.group(2)], int(m.group(3)),
                           int(m.group(4)), int(m.group(5)), tzinfo=UTC)
    except ValueError:
        return None


def geomagnetic_latitude(lat, lon):
    pole = POLICY["dipolePole"]
    pl, pn = math.radians(pole["latitudeDeg"]), math.radians(pole["longitudeDeg"])
    f, l = math.radians(lat), math.radians(lon)
    value = math.sin(f) * math.sin(pl) + math.cos(f) * math.cos(pl) * math.cos(l - pn)
    return round(math.degrees(math.asin(value)), 1)


# ---------------------------------------------------------------------------
# alerts.json message parsing
# ---------------------------------------------------------------------------

ISSUE_DATETIME_RE = re.compile(r"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}")
HEADLINE_RE = re.compile(r"(?:(CANCEL|EXTENDED|CONTINUED) )?(WATCH|WARNING|ALERT|SUMMARY): (.+)")
WATCH_DAY_RE = re.compile(r"([A-Z][a-z]{2}) (\d{2})\s*:\s+(G[1-5]|None)\b")


class Message:
    def __init__(self, record, text):
        self.record = record
        self.product_id = record["product_id"]
        self.issue_datetime = record["issue_datetime"]
        self.text = text
        self.sha256 = sha256(text.encode("utf-8"))
        lines = text.split("\n")
        self.code = re.fullmatch(r"Space Weather Message Code: ([A-Z0-9]{4,8})", lines[0]).group(1)
        self.serial = int(re.fullmatch(r"Serial Number: (\d{1,6})", lines[1]).group(1))
        self.issue = parse_swpc_time(re.fullmatch(r"Issue Time: (.+)", lines[2]).group(1))
        self.identity = f"{self.code}/{self.serial}/{fmt(self.issue)}"
        self.headline = next((line for line in lines[3:] if line.strip()), "")
        m = HEADLINE_RE.fullmatch(self.headline.strip())
        self.modifier = m.group(1) if m else None
        self.kind = m.group(2) if m else None
        self.corrected = bool(re.search(r"(?m)^CORRECTED$", text))
        self.link = None
        for label, kind in (("Extension to Serial Number", "extension"),
                            ("Continuation of Serial Number", "continuation"),
                            ("Cancel Serial Number", "cancel")):
            value = self.field(label)
            if value and re.fullmatch(r"\d{1,6}", value):
                self.link = (kind, int(value))
        self.family = family_of(self.code, self.kind)
        self.poleward = None
        m = re.search(r"poleward of (\d{1,2}) degrees Geomagnetic Latitude", text)
        if m:
            self.poleward = int(m.group(1))
        self.level = level_of(self)
        self.days = watch_days(self) if self.family == "watch" else []

    def field(self, label):
        m = re.search(r"(?m)^" + re.escape(label) + r": ?(.*)$", self.text)
        return m.group(1).strip() if m else None

    def time_field(self, label):
        value = self.field(label)
        return parse_swpc_time(value) if value else None


def family_of(code, kind):
    if kind is None:
        return "unclassified"
    if code.startswith("WATA") and kind == "WATCH":
        return "watch"
    if code.startswith("WAR") and kind == "WARNING":
        return "warning"
    if re.fullmatch(r"ALTK0[4-9]", code) and kind == "ALERT":
        return "k-alert"
    if code.startswith("ALTX") and kind == "ALERT":
        return "xray-alert"
    if code in ("ALTTP2", "ALTTP4") and kind == "ALERT":
        return "radio-sweep"
    if (code == "ALTEF3" or code.startswith("ALTPX") or code == "ALTPC0") and kind == "ALERT":
        return "flux-alert"
    if code.startswith("SUM") and kind == "SUMMARY":
        return "summary"
    return "unclassified"


def level_of(msg):
    m = re.fullmatch(r"(?:WARK|ALTK)0([4-9])", msg.code)
    if m:
        k = int(m.group(1))
        return "K4" if k == 4 else f"G{k - 4}"
    if msg.family == "watch":
        m = re.search(r"Category (G[1-5])", msg.headline)
        return m.group(1) if m else None
    m = re.search(r"(?im)^noaa scale: ([GRS][1-5])\b", msg.text)
    return m.group(1).upper() if m else None


def level_rank(level):
    if level is None or level == "K4":
        return 0
    return int(level[1])


def infer_year(month, reference):
    """The year of a month printed without one: the year that puts it within six months of the reference."""
    year = reference.year
    delta = month - reference.month
    if delta < -6:
        year += 1
    elif delta > 6:
        year -= 1
    return year


def watch_days(msg):
    result = []
    for line in msg.text.split("\n"):
        for mon, day, level in WATCH_DAY_RE.findall(line):
            if mon not in MONTHS:
                continue
            result.append((dt.date(infer_year(MONTHS[mon], msg.issue), MONTHS[mon], int(day)), level))
    return result


def parse_record(record, index):
    """Return (Message, None) or (None, rejection reason)."""
    if not isinstance(record, dict):
        return None, "record-schema"
    for key in ("product_id", "issue_datetime", "message"):
        if not isinstance(record.get(key), str):
            return None, "record-schema"
        try:
            record[key].encode("utf-8")
        except UnicodeEncodeError:
            # An unpaired surrogate escape such as \ud800 decodes to a string that has no UTF-8 form.
            return None, "record-schema"
    if not re.fullmatch(r"[A-Z0-9]{2,8}", record["product_id"]):
        return None, "record-schema"
    if not ISSUE_DATETIME_RE.fullmatch(record["issue_datetime"]):
        return None, "issue-datetime"
    if len(record["message"].encode("utf-8")) > POLICY["maxMessageBytes"]:
        return None, "record-oversize"
    text = normalize_text(record["message"])
    lines = text.split("\n")[:-1]
    if (len(lines) < 4
            or not re.fullmatch(r"Space Weather Message Code: [A-Z0-9]{4,8}", lines[0])
            or not re.fullmatch(r"Serial Number: \d{1,6}", lines[1])
            or not re.fullmatch(r"Issue Time: \d{4} [A-Z][a-z]{2} \d{2} \d{4} UTC", lines[2])
            or parse_swpc_time(lines[2][len("Issue Time: "):]) is None):
        return None, "message-header"
    return Message(record, text), None


# ---------------------------------------------------------------------------
# Notice groups and lifecycle
# ---------------------------------------------------------------------------

class Group:
    def __init__(self, gid, nodes, versions, orphan):
        self.gid = gid
        self.nodes = nodes
        self.versions = versions
        self.orphan = orphan
        noncancel = [v for v in versions if v.modifier != "CANCEL"]
        self.noncancel = noncancel
        self.cancels = [v for v in versions if v.modifier == "CANCEL"]
        lead = noncancel[0] if noncancel else versions[0]
        self.family = lead.family if noncancel else ("watch" if lead.code.startswith("WATA") else
                                                     family_of(lead.code, lead.kind))
        self.root = noncancel[0] if noncancel else None
        self.latest_nc = noncancel[-1] if noncancel else None
        self.latest = versions[-1]
        self.level = self.latest_nc.level if self.latest_nc else None
        self.poleward = self.latest_nc.poleward if self.latest_nc else None


def version_key(entry):
    msg, seq = entry
    return (msg.issue, msg.issue_datetime, seq)


def build_groups(store):
    """store: identity -> list of (Message, seq). Returns gid -> Group."""
    parent = {}

    def find(node):
        parent.setdefault(node, node)
        while parent[node] != node:
            parent[node] = parent[parent[node]]
            node = parent[node]
        return node

    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[max(ra, rb)] = min(ra, rb)

    known = set()
    for versions in store.values():
        for msg, _ in versions:
            node = (msg.code, msg.serial)
            known.add(node)
            find(node)
            if msg.link:
                union(node, (msg.code, msg.link[1]))
    members = {}
    for versions in store.values():
        for entry in versions:
            members.setdefault(find((entry[0].code, entry[0].serial)), []).append(entry)
    groups = {}
    for root, entries in members.items():
        nodes = {n for n in parent if find(n) == root}
        entries.sort(key=version_key)
        gid = f"{root[0]}/{root[1]}"
        orphan = any(n not in known for n in nodes)
        groups[gid] = Group(gid, nodes, [m for m, _ in entries], orphan)
    return groups


def watch_issue_times(groups):
    return sorted((g.root.issue, gid) for gid, g in groups.items() if g.family == "watch" and g.root)


def window(group, groups):
    """Return (from, until, endReason, recentUntil, fallback)."""
    fam = group.family
    nc = group.latest_nc
    fallback = False
    if nc is None:
        start = group.latest.issue
        return start, start, "cancelled", start + dt.timedelta(seconds=POLICY["recentSeconds"]), False
    last_issue = nc.issue
    if fam == "summary":
        start = group.root.issue
        return start, start, None, start + dt.timedelta(seconds=POLICY["recentSeconds"]), False
    start = until = None
    if fam == "watch":
        start = group.root.issue
        days = [d for d, level in nc.days if level != "None"]
        if nc.days:
            until = (dt.datetime.combine(max(days), dt.time(), UTC) + dt.timedelta(days=1)) if days else start
    elif fam == "warning":
        start = group.root.time_field("Valid From")
        until = nc.time_field("Now Valid Until") or nc.time_field("Valid To")
    elif fam == "k-alert":
        start = nc.time_field("Threshold Reached")
        period = nc.field("Synoptic Period")
        m = re.fullmatch(r"(\d{2})(\d{2})-(\d{2})(\d{2})", period or "")
        if start and m:
            end_minutes = int(m.group(3)) * 60 + int(m.group(4))
            until = dt.datetime.combine(start.date(), dt.time(), UTC) + dt.timedelta(minutes=end_minutes)
            if until <= start:
                until = None
    elif fam == "xray-alert":
        start = nc.time_field("Threshold Reached")
        until = start + dt.timedelta(hours=3) if start else None
    elif fam == "radio-sweep":
        start = nc.time_field("Begin Time")
        until = start + dt.timedelta(hours=1) if start else None
    elif fam == "flux-alert":
        start = group.root.time_field("Threshold Reached") or group.root.time_field("Begin Time")
        until = last_issue + dt.timedelta(hours=30)
    elif fam == "unclassified":
        start = group.root.issue
        until = start + dt.timedelta(hours=6)
    if start is None or until is None:
        fallback = True
        start = group.root.issue
        until = start + dt.timedelta(hours=6)
    reason = "expired"
    if fam == "watch":
        later = [t for t, gid in watch_issue_times(groups) if gid != group.gid and t > group.root.issue]
        if later and later[0] < until:
            until, reason = later[0], "superseded"
    if group.cancels:
        cancel_at = min(c.issue for c in group.cancels)
        if cancel_at < until:
            until, reason = cancel_at, "cancelled"
    cap = last_issue + dt.timedelta(seconds=POLICY["activeCapSeconds"])
    if until > cap:
        until, reason = cap, "capped"
    return start, until, reason, until + dt.timedelta(seconds=POLICY["recentSeconds"]), fallback


def notice_state(group, groups, t):
    start, until, reason, recent_until, _ = window(group, groups)
    if group.family == "summary" and group.latest_nc is not None:
        return ("recent" if t < recent_until else "history"), start, until, None
    if t >= until:
        return ("recent" if t < recent_until else "history"), start, until, reason
    if t < start:
        return "pending", start, until, None
    return "active", start, until, None


def relevance(group, site):
    geomagnetic = group.family in ("watch", "k-alert") or (
        group.family == "warning" and re.fullmatch(r"WARK0[4-9]", group.latest.code))
    if group.family == "unclassified":
        return "Unknown"
    if not geomagnetic:
        return "NoLocalObservingImpact"
    if site["location"] is None:
        return "Unknown"
    required = group.poleward or TYPICAL_LATITUDE.get(group.level)
    if required is None:
        return "Unknown"
    phi = abs(geomagnetic_latitude(site["location"]["latitudeDeg"], site["location"]["longitudeDeg"]))
    diff = round(required - phi, 1)
    if diff <= 0:
        return "InImpactArea"
    if diff <= 5:
        return "PossibleNearHorizon"
    if diff <= 10:
        return "Unlikely"
    return "NotExpected"


# ---------------------------------------------------------------------------
# Other products
# ---------------------------------------------------------------------------

def parse_scales(obj):
    if not isinstance(obj, dict) or not {"-1", "0", "1", "2", "3"} <= set(obj):
        return None, "schema"
    facts = {}
    for key in ("-1", "0", "1", "2", "3"):
        day = obj[key]
        try:
            stamp = dt.datetime.strptime(day["DateStamp"] + " " + day["TimeStamp"], "%Y-%m-%d %H:%M:%S")
            entry = {"stampUtc": fmt(stamp.replace(tzinfo=UTC)),
                     "rScale": day["R"]["Scale"], "rMinorProbPct": day["R"]["MinorProb"],
                     "rMajorProbPct": day["R"]["MajorProb"], "sScale": day["S"]["Scale"],
                     "sProbPct": day["S"]["Prob"], "gScale": day["G"]["Scale"]}
        except (KeyError, TypeError, ValueError):
            return None, "schema"
        facts[{"-1": "observed24hMax", "0": "current"}.get(key, "predictedDay" + key)] = entry
    return facts, None


def parse_kp_forecast(obj):
    if not isinstance(obj, list) or not obj:
        return None, "schema" if not isinstance(obj, list) else "suspect-empty"
    bins = {}
    for row in obj:
        try:
            start = dt.datetime.strptime(row["time_tag"], "%Y-%m-%dT%H:%M:%S").replace(tzinfo=UTC)
            kp = row["kp"]
            if not isinstance(kp, str) or not re.fullmatch(r"\d\.\d{2}", kp):
                return None, "schema"
            bins[fmt(start)] = {"kp": kp, "label": row["observed"], "noaaScale": row["noaa_scale"]}
        except (KeyError, TypeError, ValueError):
            return None, "schema"
    return {"rowCount": len(obj), "firstBinStartUtc": min(bins), "lastBinStartUtc": max(bins),
            "bins": bins}, None


THREE_DAY_COLUMNS_RE = re.compile(r" *([A-Z][a-z]{2}) (\d{2}) +([A-Z][a-z]{2}) (\d{2}) +([A-Z][a-z]{2}) (\d{2})")
THREE_DAY_KP_ROW_RE = re.compile(r"(\d{2})-\d{2}UT +(.+)")
THREE_DAY_KP_CELLS_RE = re.compile(r"(\d\.\d{2})(?: \((G[1-5])\))? +(\d\.\d{2})(?: \((G[1-5])\))?"
                                   r" +(\d\.\d{2})(?: \((G[1-5])\))?")
THREE_DAY_PROB_ROW_RE = re.compile(r"(\S.*?) {2,}(\d{1,3})% +(\d{1,3})% +(\d{1,3})%")


def three_day_table(lines, title_re, issued):
    """Return (column dates, row lines) for the table whose title matches, or None.

    The column header is the first non-blank line after the title and must name three days. Each column's
    year comes from the :Issued: stamp, never from the year printed in the title. Rows run to the next
    blank line.
    """
    index = next((i for i, line in enumerate(lines) if re.fullmatch(title_re, line)), None)
    if index is None:
        return None
    index += 1
    while index < len(lines) and not lines[index].strip():
        index += 1
    m = THREE_DAY_COLUMNS_RE.fullmatch(lines[index]) if index < len(lines) else None
    if not m:
        return None
    dates = []
    for mon, day in zip(m.groups()[0::2], m.groups()[1::2]):
        if mon not in MONTHS:
            return None
        try:
            dates.append(dt.date(infer_year(MONTHS[mon], issued), MONTHS[mon], int(day)))
        except ValueError:
            return None
    rows = []
    for line in lines[index + 1:]:
        if not line.strip():
            break
        rows.append(line)
    return dates, rows


def parse_three_day(text):
    m = re.search(r"(?m)^:Issued: (.+)$", text)
    issued = parse_swpc_time(m.group(1)) if m else None
    if not text.startswith(":Product: 3-Day Forecast\n") or issued is None:
        return None, "schema"
    lines = text.split("\n")
    kp = three_day_table(lines, r"NOAA Kp index breakdown \S.*", issued)
    radiation = three_day_table(lines, r"Solar Radiation Storm Forecast for \S.*", issued)
    blackout = three_day_table(lines, r"Radio Blackout Forecast for \S.*", issued)
    if kp is None or radiation is None or blackout is None:
        return None, "schema"
    dates, rows = kp
    bins = {}
    starts = []
    for row in rows:
        rm = THREE_DAY_KP_ROW_RE.fullmatch(row)
        cm = THREE_DAY_KP_CELLS_RE.fullmatch(rm.group(2)) if rm else None
        if not cm:
            return None, "schema"
        cells = list(zip(cm.groups()[0::2], cm.groups()[1::2]))
        starts.append(int(rm.group(1)))
        for day, (value, scale) in zip(dates, cells):
            start = dt.datetime.combine(day, dt.time(), UTC) + dt.timedelta(hours=int(rm.group(1)))
            bins[fmt(start)] = {"kp": value, "noaaScale": scale or None}
    if starts != list(range(0, 24, 3)):
        return None, "schema"
    probabilities = {}
    for key, (prob_dates, prob_rows) in (("radiationProbabilities", radiation),
                                         ("radioBlackoutProbabilities", blackout)):
        table = {}
        for row in prob_rows:
            pm = THREE_DAY_PROB_ROW_RE.fullmatch(row)
            if not pm:
                return None, "schema"
            table[pm.group(1)] = {d.isoformat(): v for d, v in zip(prob_dates, pm.groups()[1:])}
        if not table:
            return None, "schema"
        probabilities[key] = table
    return dict({"issuedUtc": fmt(issued), "kpBreakdownBins": len(bins), "kpBreakdown": bins},
                **probabilities), None


def ovation_band(grid, site):
    """The site's band, or None (unknown) when the site has no location."""
    if site["location"] is None:
        return None
    lat, lon = site["location"]["latitudeDeg"], site["location"]["longitudeDeg"]
    centre_lon = round(lon % 360.0) % 360
    base = round(abs(lat))
    sign = 1 if lat >= 0 else -1
    lats = [sign * la for la in range(base, base + 10)]
    lons = [(centre_lon + d) % 360 for d in range(-10, 11)]
    cells = [grid[(lo, la)] for lo in lons for la in lats if (lo, la) in grid]
    return {"latitudes": [min(lats), max(lats)], "longitudes": [lons[0], lons[-1]], "cells": len(cells),
            "expectedCells": 210, "maxValue": max(cells) if cells else None}


def parse_ovation(obj, sites):
    grid = {(c[0], c[1]): c[2] for c in obj["coordinates"]}
    artifact = sum(1 for (lo, la), v in grid.items() if -2 <= la <= 0 and v > 0)
    observed = ts(obj["Observation Time"])
    forecast = ts(obj["Forecast Time"])
    return {"observationTimeUtc": fmt(observed), "forecastTimeUtc": fmt(forecast),
            "leadMinutes": int((forecast - observed).total_seconds() // 60),
            "siteBands": {site["siteId"]: ovation_band(grid, site) for site in sites},
            "equatorialNonzeroCellsIgnored": artifact}


# ---------------------------------------------------------------------------
# Reference model
# ---------------------------------------------------------------------------

class ProductState:
    def __init__(self):
        self.lkg = None            # {"sha256", "stamp", "facts"}
        self.etag = None
        self.last_modified = None
        self.last_success = None
        self.last_attempt = None
        self.next_attempt = None
        self.retry_not_before = None   # Retry-After of a halting 429, applied when the halt is resumed
        self.in_backoff = False
        self.transient = 0
        self.rate_limited = 0
        self.rejected = 0
        self.halt = None
        self.last_outcome = None
        self.record_drift_stamp = None  # newest in-file stamp of a drifted body while an episode is open


class Model:
    def __init__(self, scenario):
        self.sites = scenario["sites"]
        self.products = {p: ProductState() for p in POLICY["products"]}
        self.provider_halt = None
        self.store = {}            # identity -> [(Message, seq)]
        self.seq = 0
        self.raised = {}           # node -> rank
        self.acks = {}             # node -> {"operator", "atUtc"}
        self.orphans = set()

    # -- notices ----------------------------------------------------------
    def groups(self):
        return build_groups(self.store)

    def group_raised(self, group):
        ranks = [self.raised[n] for n in group.nodes if n in self.raised]
        return max(ranks) if ranks else None

    def group_ack(self, group):
        acks = [self.acks[n] for n in sorted(group.nodes) if n in self.acks]
        return acks[-1] if acks else None

    def purge(self, t):
        groups = self.groups()
        for group in groups.values():
            _, until, _, _, _ = window(group, groups)
            if t >= until + dt.timedelta(seconds=POLICY["retentionSeconds"]):
                for msg in group.versions:
                    self.store.pop(msg.identity, None)
                for node in group.nodes:
                    self.raised.pop(node, None)
                    self.acks.pop(node, None)
                self.orphans.discard(group.gid)

    def notice_view(self, t):
        groups = self.groups()
        alerts_fresh = self.freshness("alerts", t) == "fresh"
        view = {}
        for gid, group in sorted(groups.items()):
            state, start, until, reason = notice_state(group, groups, t)
            entry = {
                "family": group.family, "level": group.level, "state": state,
                "fromUtc": fmt(start), "untilUtc": fmt(until), "endReason": reason,
                "mayBeOutOfDate": state in ("pending", "active") and not alerts_fresh,
                "latest": group.latest.identity, "versions": len(group.versions),
                "corrected": any(v.corrected for v in group.versions),
                "orphan": group.orphan, "acknowledged": self.group_ack(group),
                "relevance": {s["siteId"]: relevance(group, s) for s in self.sites},
            }
            if group.family == "watch" and group.latest_nc:
                entry["days"] = [{"dateUtc": d.isoformat(), "level": level} for d, level in group.latest_nc.days]
            site = self.sites[0]
            zone = ZoneInfo(site["timeZoneId"])
            entry["local"] = {"fromLocal": start.astimezone(zone).isoformat(),
                              "untilLocal": until.astimezone(zone).isoformat()}
            view[gid] = entry
        return view

    def ingest_alerts(self, records, t, diagnostics, stale=False):
        """Merge a body's records. A stale body adds new identities but never a new version of a stored one."""
        touched = set()
        added = []
        seen_in_body = {}
        for index, record in enumerate(records):
            msg, reason = parse_record(record, index)
            if msg is None:
                diagnostics.append(f"record-rejected:{reason}:{index}")
                continue
            if record["issue_datetime"][:16] != msg.issue.strftime("%Y-%m-%d %H:%M"):
                diagnostics.append(f"issue-time-mismatch:{msg.identity}")
            prior = seen_in_body.get(msg.identity)
            if prior is not None and msg.sha256 in prior:
                if f"duplicate-record-in-body:{msg.identity}" not in diagnostics:
                    diagnostics.append(f"duplicate-record-in-body:{msg.identity}")
                continue
            seen_in_body.setdefault(msg.identity, set()).add(msg.sha256)
            versions = self.store.setdefault(msg.identity, [])
            if any(v.sha256 == msg.sha256 for v, _ in versions):
                continue
            if versions and stale:
                diagnostics.append(f"stale-revision-ignored:{msg.identity}")
                continue
            if versions:
                diagnostics.append(f"source-revision:{msg.identity}")
            self.seq += 1
            versions.append((msg, self.seq))
            touched.add((msg.code, msg.serial))
            added.append(msg)
        present = {(m.code, m.serial) for vs in self.store.values() for m, _ in vs}
        for msg in added:
            if msg.link and msg.link[1] != msg.serial and (msg.code, msg.link[1]) not in present:
                diagnostics.append(f"orphan-link:{msg.identity}")
        for gid, group in self.groups().items():
            if group.orphan:
                self.orphans.add(gid)
            elif gid in self.orphans:
                self.orphans.discard(gid)
                diagnostics.append(f"orphan-resolved:{gid}")
        return touched

    def attention_for(self, touched, t):
        events = []
        groups = self.groups()
        grace = dt.timedelta(seconds=POLICY["newGraceSeconds"])
        # Root issue-time order, so a watch replacement sees a predecessor that raised earlier in this merge.
        candidates = [(gid, group) for gid, group in groups.items()
                      if group.nodes & touched and group.family != "summary" and group.root is not None]
        for gid, group in sorted(candidates, key=lambda item: (item[1].root.issue, item[0])):
            state, _, _, _ = notice_state(group, groups, t)
            rank = level_rank(group.level)
            raised = self.group_raised(group)
            node = min(group.nodes)
            if raised is None:
                just_ended = state == "recent" and t < group.latest_nc.issue + grace
                if state not in ("pending", "active") and not just_ended:
                    continue
                reason = "new"
                if group.family == "watch":
                    before = group.root.issue - dt.timedelta(seconds=1)
                    preds = [self.group_raised(g) for g in groups.values()
                             if g.family == "watch" and g.gid != gid and g.root
                             and g.root.issue < group.root.issue
                             and notice_state(g, groups, before)[0] == "active"]
                    preds = [r for r in preds if r is not None]
                    if preds and rank <= max(preds):
                        self.raised[node] = rank
                        continue
                    if preds:
                        reason = "escalated"
                self.raised[node] = rank
                events.append({"group": gid, "reason": reason, "level": group.level})
            elif rank > raised:
                self.raised[node] = rank
                events.append({"group": gid, "reason": "escalated", "level": group.level})
        return sorted(events, key=lambda event: event["group"])

    # -- fetch ------------------------------------------------------------
    def freshness(self, name, t):
        st = self.products[name]
        cfg = POLICY["products"][name]
        if st.last_success is None:
            return "none"
        age = (t - st.last_success).total_seconds()
        if age < cfg["baseSeconds"] * POLICY["freshMultiplier"]:
            return "fresh"
        if age < cfg["outdatedSeconds"]:
            return "stale"
        return "outdated"

    def product_view(self, name, t):
        st = self.products[name]
        halt = self.provider_halt or st.halt
        view = {
            "lastOutcome": st.last_outcome, "freshness": self.freshness(name, t),
            "lkgSha256": st.lkg["sha256"] if st.lkg else None,
            "lastSuccessUtc": fmt(st.last_success),
            "nextAttemptNotBeforeUtc": fmt(st.next_attempt),
            "retryNotBeforeUtc": fmt(st.retry_not_before),
            "consecutiveTransientFailures": st.transient,
            "consecutiveRateLimited": st.rate_limited,
            "consecutiveRejected": st.rejected,
            "recordDriftOpen": st.record_drift_stamp is not None,
            "recordDriftStampUtc": st.record_drift_stamp,
            "halt": dict(halt) if halt else None,
            "validators": {"etag": st.etag, "lastModified": st.last_modified},
            "facts": copy.deepcopy(st.lkg["facts"]) if st.lkg else None,
        }
        if name == "kp-forecast" and st.lkg:
            # The bin's relation to now is computed when read; the source label is kept verbatim in facts.
            relations = {}
            for start_text in st.lkg["facts"]["bins"]:
                start = ts(start_text)
                end = start + dt.timedelta(hours=3)
                relations[start_text] = "past" if end <= t else ("current" if start <= t else "future")
            view["kpBinRelations"] = relations
        return view

    def body_bytes(self, response):
        if "body" in response:
            return response["body"].encode("utf-8")
        if "bodyBase64" in response:
            return base64.b64decode(response["bodyBase64"], validate=True)
        if "bodyFile" in response:
            path = (PACK / response["bodyFile"]).resolve()
            if PACK not in path.parents:
                raise ScenarioError("bodyFile escapes the pack")
            return path.read_bytes()
        synth = response.get("bodySynthesis")
        if synth is None:
            return b""
        if synth["kind"] == "padded-empty-array":
            return b"[" + b" " * (synth["totalBytes"] - 2) + b"]"
        if synth["kind"] == "repeated-record":
            return encode_alerts([synth["record"]] * synth["count"]).encode("ascii")
        raise ScenarioError(f"unknown bodySynthesis kind {synth['kind']}")

    def validate(self, name, response, t, diagnostics):
        """Return ("ok", snapshot, records) or ("rejected", reason, None)."""
        cfg = POLICY["products"][name]
        data = self.body_bytes(response)
        if len(data) > cfg["maxBytes"]:
            return "rejected", "oversize", None
        media = (response.get("contentType") or "").split(";")[0].strip().lower()
        if media != cfg["media"]:
            return "rejected", "content-type", None
        try:
            text = data.decode("utf-8")
        except UnicodeDecodeError:
            return "rejected", "encoding", None
        if media == "text/plain":
            normalized = normalize_text(text)
            facts, reason = parse_three_day(normalized)
            if facts is None:
                return "rejected", reason, None
            return "ok", {"sha256": sha256(normalized.encode("utf-8")), "stamp": facts["issuedUtc"],
                          "facts": facts}, None
        try:
            obj = json.loads(text, parse_float=lambda s: s)
        except ValueError:
            return "rejected", "malformed-json", None
        digest = sha256(data)
        if name == "alerts":
            if not isinstance(obj, list):
                return "rejected", "schema", None
            if not obj:
                return "rejected", "suspect-empty", None
            if len(obj) > POLICY["maxRecords"]:
                return "rejected", "too-many-records", None
            parsed = [parse_record(r, i)[0] for i, r in enumerate(obj)]
            accepted = [m for m in parsed if m is not None]
            if not accepted:
                return "rejected", "no-valid-records", None
            stamp = fmt(max(m.issue for m in accepted))
            return "ok", {"sha256": digest, "stamp": stamp, "facts": {"records": len(obj),
                                                                         "acceptedRecords": len(accepted)}}, obj
        if name == "scales":
            facts, reason = parse_scales(obj)
            stamp = facts["current"]["stampUtc"] if facts else None
        elif name == "kp-forecast":
            facts, reason = parse_kp_forecast(obj)
            stamp = max((b for b, v in facts["bins"].items() if v["label"] == "observed"), default=None) \
                if facts else None
        else:
            try:
                facts, reason = parse_ovation(obj, self.sites), None
            except (KeyError, TypeError, ValueError, IndexError):
                facts, reason = None, "schema"
            stamp = facts["observationTimeUtc"] if facts else None
        if facts is None:
            return "rejected", reason, None
        return "ok", {"sha256": digest, "stamp": stamp, "facts": facts}, None

    def succeed(self, st, cfg, t, outcome, fresh=True):
        st.last_outcome = outcome
        if fresh:
            st.last_success = t
        st.transient = st.rate_limited = st.rejected = 0
        st.in_backoff = False
        st.next_attempt = t + dt.timedelta(seconds=cfg["baseSeconds"])

    def fetch(self, step):
        name = step["product"]
        t = ts(step["atUtc"])
        trigger = step.get("trigger", "scheduled")
        st = self.products[name]
        cfg = POLICY["products"][name]
        result = {"refused": None, "request": None, "outcome": None, "attention": [], "diagnostics": []}
        if self.provider_halt or st.halt:
            result["refused"] = "halted"
            return result
        if trigger == "scheduled":
            if st.next_attempt and t < st.next_attempt:
                raise ScenarioError(f"step {step['step']}: scheduled before {fmt(st.next_attempt)}")
        else:
            if st.in_backoff and t < st.next_attempt:
                result["refused"] = "backoff"
                return result
            spacing = dt.timedelta(seconds=POLICY["minimumAttemptSpacingSeconds"])
            if st.last_attempt and t < st.last_attempt + spacing:
                result["refused"] = "min-interval"
                return result
        if "response" not in step:
            raise ScenarioError(f"step {step['step']}: the request was not refused but the step has no response")
        result["request"] = {"ifNoneMatch": st.etag, "ifModifiedSince": st.last_modified}
        st.last_attempt = t
        # An attempt ends the backoff the previous outcome set; only a transient failure or a 429 starts one.
        st.in_backoff = False
        response = step["response"]
        status = response.get("status")
        diagnostics = result["diagnostics"]
        if response.get("outcome") in ("timeout", "network-error") or (status is not None and status >= 500) \
                or (status == 304 and st.etag is None and st.last_modified is None) \
                or (status is not None and status < 300 and status != 200):
            st.transient += 1
            delay = min(POLICY["transientBackoffCapSeconds"],
                        max(POLICY["minimumAttemptSpacingSeconds"], cfg["baseSeconds"] * 2 ** st.transient))
            st.next_attempt = t + dt.timedelta(seconds=delay)
            st.in_backoff = True
            st.last_outcome = result["outcome"] = "transient-failure"
        elif status == 304:
            self.succeed(st, cfg, t, "not-modified")
            result["outcome"] = "not-modified"
        elif status == 429:
            st.rate_limited += 1
            delay = POLICY["rateLimitedDefaultSeconds"]
            retry = response.get("retryAfter")
            if retry and re.fullmatch(r"\d{1,9}", retry):
                delay = int(retry)
            elif retry:
                try:
                    when = dt.datetime.strptime(retry, "%a, %d %b %Y %H:%M:%S GMT").replace(tzinfo=UTC)
                    delay = int((when - t).total_seconds())
                except ValueError:
                    pass
            delay = min(POLICY["retryAfterCapSeconds"], max(POLICY["minimumAttemptSpacingSeconds"], delay))
            st.in_backoff = True
            if st.rate_limited >= POLICY["rateLimitedHaltCount"]:
                self.provider_halt = {"scope": "provider", "reason": "rate-limited", "sinceUtc": fmt(t)}
                st.next_attempt = None
                st.retry_not_before = t + dt.timedelta(seconds=delay)
                st.last_outcome = result["outcome"] = "halted"
            else:
                st.next_attempt = t + dt.timedelta(seconds=delay)
                st.last_outcome = result["outcome"] = "rate-limited"
        elif status == 403:
            self.provider_halt = {"scope": "provider", "reason": "forbidden", "sinceUtc": fmt(t)}
            st.next_attempt = None
            st.last_outcome = result["outcome"] = "halted"
        elif status is not None and 300 <= status < 500:
            reason = {404: "not-found", 410: "gone"}.get(status,
                                                          "endpoint-moved" if status < 400 else "client-error")
            st.halt = {"scope": "product", "reason": reason, "sinceUtc": fmt(t)}
            st.next_attempt = None
            st.last_outcome = result["outcome"] = "halted"
        else:
            verdict, detail, records = self.validate(name, response, t, diagnostics)
            if verdict == "rejected":
                diagnostics.append(f"body-rejected:{detail}")
                st.rejected += 1
                st.transient = st.rate_limited = 0
                st.in_backoff = False
                st.next_attempt = t + dt.timedelta(seconds=cfg["baseSeconds"])
                st.last_outcome = result["outcome"] = "rejected"
                if st.rejected == POLICY["rejectedAttentionCount"]:
                    result["attention"].append({"group": f"provider/{name}/format-drift", "reason": "new",
                                                "level": None})
            else:
                # The outcome is decided before any record is merged, so a stale body cannot revise a stored
                # record.
                snapshot = detail
                if st.lkg and snapshot["sha256"] == st.lkg["sha256"]:
                    outcome = "unchanged"
                elif st.lkg and snapshot["stamp"] is not None and st.lkg["stamp"] is not None \
                        and snapshot["stamp"] < st.lkg["stamp"]:
                    outcome = "stale-snapshot"
                    diagnostics.append("stale-snapshot")
                else:
                    outcome = "updated"
                touched = set()
                if records is not None and outcome != "unchanged":
                    touched = self.ingest_alerts(records, t, diagnostics, stale=outcome == "stale-snapshot")
                if outcome == "updated":
                    st.lkg = snapshot
                if outcome != "stale-snapshot":
                    st.etag = response.get("etag")
                    st.last_modified = response.get("lastModified")
                self.succeed(st, cfg, t, outcome, fresh=outcome != "stale-snapshot")
                result["outcome"] = outcome
                if name == "alerts":
                    result["attention"] = self.attention_for(touched, t)
                    if outcome == "updated":
                        facts = snapshot["facts"]
                        drift = st.record_drift_stamp
                        if facts["records"] > facts["acceptedRecords"]:
                            if drift is None:
                                result["attention"].append({"group": "provider/alerts/record-drift",
                                                            "reason": "new", "level": None})
                            if drift is None or snapshot["stamp"] > drift:
                                st.record_drift_stamp = snapshot["stamp"]
                        elif drift is not None and snapshot["stamp"] > drift:
                            # Only a clean body newer than every drifted body closes the episode, so an edge
                            # alternating between equal-stamp bodies cannot close and reopen it.
                            st.record_drift_stamp = None
        result["diagnostics"] = sorted(diagnostics)
        return result

    def run_step(self, step):
        kind = step["kind"]
        t = ts(step["atUtc"]) if "atUtc" in step else None
        if t is not None:
            self.purge(t)
        observed = {}
        if kind == "fetch":
            observed.update(self.fetch(step))
            observed["product"] = self.product_view(step["product"], t)
        elif kind == "acknowledge":
            groups = self.groups()
            if step["group"] not in groups:
                raise ScenarioError(f"step {step['step']}: unknown group {step['group']}")
            self.acks[min(groups[step["group"]].nodes)] = {"operator": step["operator"], "atUtc": step["atUtc"]}
        elif kind == "resume":
            if step["scope"] == "provider":
                self.provider_halt = None
                targets = list(self.products)
            else:
                self.products[step["product"]].halt = None
                targets = [step["product"]]
            # Resuming lifts the halt only. Counters, backoff and Retry-After stand, and minimum spacing holds.
            spacing = dt.timedelta(seconds=POLICY["minimumAttemptSpacingSeconds"])
            for name in targets:
                st = self.products[name]
                if st.halt is None and self.provider_halt is None:
                    candidates = [t, st.next_attempt, st.retry_not_before,
                                  st.last_attempt + spacing if st.last_attempt else None]
                    st.next_attempt = max(c for c in candidates if c is not None)
                    st.retry_not_before = None
        elif kind == "restart":
            self.restore(json.loads(json.dumps(self.durable_state())))
        elif kind != "evaluate":
            raise ScenarioError(f"unknown step kind {kind}")
        if t is not None:
            observed["notices"] = self.notice_view(t)
            observed["products"] = {p: self.product_view(p, t) for p in self.products}
        return observed

    # -- durability -------------------------------------------------------
    PRODUCT_FIELDS = ("lkg", "etag", "last_modified", "in_backoff", "transient", "rate_limited", "rejected",
                      "halt", "last_outcome", "record_drift_stamp")
    PRODUCT_TIMES = ("last_success", "last_attempt", "next_attempt", "retry_not_before")

    def durable_state(self):
        """Everything a restart keeps. A restart step discards every other field."""
        def node(n):
            return f"{n[0]}/{n[1]}"
        return {
            "providerHalt": self.provider_halt,
            "seq": self.seq,
            "store": {identity: [{"record": m.record, "seq": seq} for m, seq in versions]
                      for identity, versions in self.store.items()},
            "raised": {node(n): rank for n, rank in self.raised.items()},
            "acks": {node(n): ack for n, ack in self.acks.items()},
            "orphans": sorted(self.orphans),
            "products": {name: dict({f: getattr(st, f) for f in self.PRODUCT_FIELDS},
                                    **{f: fmt(getattr(st, f)) for f in self.PRODUCT_TIMES})
                         for name, st in self.products.items()},
        }

    def restore(self, state):
        def node(text):
            code, serial = text.split("/")
            return code, int(serial)
        self.provider_halt = state["providerHalt"]
        self.seq = state["seq"]
        self.store = {identity: [(parse_record(v["record"], 0)[0], v["seq"]) for v in versions]
                      for identity, versions in state["store"].items()}
        self.raised = {node(n): rank for n, rank in state["raised"].items()}
        self.acks = {node(n): ack for n, ack in state["acks"].items()}
        self.orphans = set(state["orphans"])
        self.products = {}
        for name, fields in state["products"].items():
            st = ProductState()
            for f in self.PRODUCT_FIELDS:
                setattr(st, f, fields[f])
            for f in self.PRODUCT_TIMES:
                setattr(st, f, ts(fields[f]))
            self.products[name] = st


def subset_mismatches(expected, observed, path):
    problems = []
    if isinstance(expected, dict) and isinstance(observed, dict):
        for key, value in expected.items():
            if key not in observed:
                problems.append(f"{path}.{key}: missing")
            else:
                problems.extend(subset_mismatches(value, observed[key], f"{path}.{key}"))
    elif expected != observed:
        problems.append(f"{path}: expected {json.dumps(expected)} observed {json.dumps(observed)}")
    return problems


def replay(scenario):
    model = Model(scenario)
    problems = []
    for step in scenario["steps"]:
        where = f"{scenario['scenarioId']} step {step['step']}"
        try:
            observed = model.run_step(step)
        except ScenarioError as error:
            problems.append(f"{scenario['scenarioId']}: {error}")
            break
        expect = step.get("expect", {})
        for key, value in expect.items():
            if key == "notices":
                if set(value) != set(observed["notices"]):
                    problems.append(f"{where}.notices: groups expected {sorted(value)} "
                                    f"observed {sorted(observed['notices'])}")
                problems.extend(subset_mismatches(value, observed["notices"], f"{where}.notices"))
            elif key in ("attention", "diagnostics"):
                if value != observed.get(key):
                    problems.append(f"{where}.{key}: expected {json.dumps(value)} "
                                    f"observed {json.dumps(observed.get(key))}")
            elif key in observed:
                problems.extend(subset_mismatches(value, observed[key], f"{where}.{key}"))
            else:
                problems.append(f"{where}.{key}: not observable for this step kind")
    return problems


# ---------------------------------------------------------------------------
# Scenario construction
# ---------------------------------------------------------------------------

class Capture:
    def __init__(self):
        self.alerts_text = (PACK / CAPTURE / "alerts.json").read_text("ascii")
        self.alerts = json.loads(self.alerts_text)
        self.by_identity = {}
        for record in self.alerts:
            msg = Message(record, normalize_text(record["message"]))
            self.by_identity[msg.identity] = record

    def rec(self, identity):
        return copy.deepcopy(self.by_identity[identity])


def message_text(code, serial, issue, lines):
    """Build an SWPC-shaped message: CRLF header lines, LF body lines."""
    when = ts(issue)
    stamp = f"{when.year} {when.strftime('%b')} {when.day:02d} {when:%H%M} UTC"
    header = (f"Space Weather Message Code: {code}\r\nSerial Number: {serial}\r\n"
              f"Issue Time: {stamp}\r\n\r\n")
    return header + "\n".join(lines)


def synthetic(product_id, issue_datetime, code, serial, lines):
    issue = issue_datetime[:10] + "T" + issue_datetime[11:16] + ":00Z"
    return {"product_id": product_id, "issue_datetime": issue_datetime,
            "message": message_text(code, serial, issue, lines)}


def newest_first(records):
    return sorted(records, key=lambda r: r["issue_datetime"], reverse=True)


def alerts_ok(records, etag, last_modified, order=True):
    body = encode_alerts(newest_first(records) if order else records)
    return {"status": 200, "contentType": "application/json", "etag": etag,
            "lastModified": last_modified, "body": body}


def http_date(value):
    return ts(value).strftime("%a, %d %b %Y %H:%M:%S GMT")


class Builder:
    def __init__(self, sid, title, covers, sites=None, synthetic_records=False, notes=None):
        self.doc = {
            "schemaVersion": SCENARIO_SCHEMA,
            "scenarioId": sid,
            "title": title,
            "covers": covers,
            "containsSyntheticRecords": synthetic_records,
            "notes": notes or [],
            "sites": sites or [HVO_SITE],
            "configuration": {"jitterFraction": 0, "policy": "docs/contracts/space-weather-facts-v1.md"},
            "steps": [],
        }

    def step(self, kind, expect=None, **fields):
        entry = {"step": len(self.doc["steps"]) + 1, "kind": kind}
        entry.update(fields)
        if expect is not None:
            entry["expect"] = expect
        self.doc["steps"].append(entry)
        return self

    def fetch(self, at, response, expect, product="alerts", trigger="scheduled"):
        """A None response marks a step whose request the model must refuse."""
        if response is None:
            return self.step("fetch", expect, atUtc=at, product=product, trigger=trigger)
        return self.step("fetch", expect, atUtc=at, product=product, trigger=trigger, response=response)

    def build(self):
        return self.doc


def scenarios(cap: Capture):
    out = []
    hvo = "hvo"

    # s01 ------------------------------------------------------------------
    w1131 = cap.rec("WATA20/1131/2026-10-18T17:24:00Z")
    b = Builder("s01-duplicate-refetch",
                "Identical body under a churned ETag, then a body that repeats one record",
                ["duplicate", "etag-churn", "duplicate-record-in-body"])
    first = alerts_ok([w1131], 'W/"fixture-s01-a"', "Sun, 18 Oct 2026 17:29:41 GMT")
    b.fetch("2026-10-18T17:30:00Z", first, {
        "request": {"ifNoneMatch": None, "ifModifiedSince": None},
        "outcome": "updated",
        "attention": [{"group": "WATA20/1131", "reason": "new", "level": "G1"}],
        "diagnostics": [],
        "notices": {"WATA20/1131": {"family": "watch", "level": "G1", "state": "active",
                                    "fromUtc": "2026-10-18T17:24:00Z", "untilUtc": "2026-10-21T00:00:00Z",
                                    "latest": "WATA20/1131/2026-10-18T17:24:00Z", "versions": 1,
                                    "relevance": {hvo: "NotExpected"},
                                    "days": [{"dateUtc": "2026-10-19", "level": "G1"},
                                             {"dateUtc": "2026-10-20", "level": "G1"},
                                             {"dateUtc": "2026-10-21", "level": "None"}]}},
        "product": {"freshness": "fresh", "nextAttemptNotBeforeUtc": "2026-10-18T17:32:00Z"},
    })
    churned = dict(first, etag='W/"fixture-s01-b"', lastModified="Sun, 18 Oct 2026 17:31:40 GMT")
    b.fetch("2026-10-18T17:32:00Z", churned, {
        "request": {"ifNoneMatch": 'W/"fixture-s01-a"', "ifModifiedSince": "Sun, 18 Oct 2026 17:29:41 GMT"},
        "outcome": "unchanged", "attention": [], "diagnostics": [],
        "notices": {"WATA20/1131": {"versions": 1, "state": "active"}},
        "product": {"lkgSha256": sha256(first["body"].encode()),
                    "validators": {"etag": 'W/"fixture-s01-b"', "lastModified": "Sun, 18 Oct 2026 17:31:40 GMT"}},
    })
    doubled = alerts_ok([w1131, w1131], 'W/"fixture-s01-c"', "Sun, 18 Oct 2026 17:33:40 GMT")
    b.fetch("2026-10-18T17:34:00Z", doubled, {
        "outcome": "updated", "attention": [],
        "diagnostics": ["duplicate-record-in-body:WATA20/1131/2026-10-18T17:24:00Z"],
        "notices": {"WATA20/1131": {"versions": 1}},
        "product": {"lkgSha256": sha256(doubled["body"].encode()),
                    "facts": {"records": 2, "acceptedRecords": 2}},
    })
    out.append(b.build())

    # s02 ------------------------------------------------------------------
    x543 = cap.rec("ALTXMF/543/2026-10-10T15:09:00Z")
    revised = copy.deepcopy(x543)
    revised["message"] = revised["message"].replace("Comment: \r\n", "Comment: Fixture in-place revision.\r\n", 1)
    if revised["message"] == x543["message"]:
        raise ScenarioError("s02 revision did not change the message")
    b = Builder("s02-same-identity-revised-content",
                "The same code, serial and issue minute served later with different text",
                ["revised", "source-revision"], synthetic_records=True,
                notes=["Step 2 alters the Comment line of a real record to model an in-place source revision. "
                       "No such revision was observed in the capture."])
    b.fetch("2026-10-10T15:10:00Z", alerts_ok([x543], 'W/"fixture-s02-a"', "Sat, 10 Oct 2026 15:09:50 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "ALTXMF/543", "reason": "new", "level": "R2"}],
        "notices": {"ALTXMF/543": {"family": "xray-alert", "level": "R2", "state": "active",
                                   "fromUtc": "2026-10-10T15:07:00Z", "untilUtc": "2026-10-10T18:07:00Z",
                                   "relevance": {hvo: "NoLocalObservingImpact"}}},
    })
    b.fetch("2026-10-10T15:12:00Z", alerts_ok([revised], 'W/"fixture-s02-b"', "Sat, 10 Oct 2026 15:11:50 GMT"), {
        "outcome": "updated", "attention": [],
        "diagnostics": ["source-revision:ALTXMF/543/2026-10-10T15:09:00Z"],
        "notices": {"ALTXMF/543": {"versions": 2, "state": "active",
                                   "latest": "ALTXMF/543/2026-10-10T15:09:00Z"}},
    })
    out.append(b.build())

    # s03 ------------------------------------------------------------------
    tp2_a = cap.rec("ALTTP2/1531/2026-10-08T16:43:00Z")
    tp2_b = cap.rec("ALTTP2/1531/2026-10-08T16:46:00Z")
    b = Builder("s03-correction-reissue",
                "A CORRECTED reissue reuses the serial with a later issue time and moves the begin time",
                ["revised", "correction", "expired"])
    b.fetch("2026-10-08T16:44:00Z", alerts_ok([tp2_a], 'W/"fixture-s03-a"', "Thu, 08 Oct 2026 16:43:55 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "ALTTP2/1531", "reason": "new", "level": None}],
        "notices": {"ALTTP2/1531": {"family": "radio-sweep", "state": "active",
                                    "fromUtc": "2026-10-08T16:42:00Z", "untilUtc": "2026-10-08T17:42:00Z",
                                    "versions": 1, "corrected": False}},
    })
    b.fetch("2026-10-08T16:47:00Z",
            alerts_ok([tp2_b, tp2_a], 'W/"fixture-s03-b"', "Thu, 08 Oct 2026 16:46:55 GMT"), {
        "outcome": "updated", "attention": [], "diagnostics": [],
        "notices": {"ALTTP2/1531": {"state": "recent", "endReason": "expired",
                                    "fromUtc": "2026-10-08T15:42:00Z", "untilUtc": "2026-10-08T16:42:00Z",
                                    "latest": "ALTTP2/1531/2026-10-08T16:46:00Z", "versions": 2,
                                    "corrected": True}},
    })
    b.step("evaluate", {"notices": {"ALTTP2/1531": {"state": "history"}}}, atUtc="2026-10-09T16:42:00Z")
    out.append(b.build())

    # s04 ------------------------------------------------------------------
    ef = [cap.rec(i) for i in ("ALTEF3/3751/2026-10-05T18:47:00Z", "ALTEF3/3752/2026-10-06T05:07:00Z",
                               "ALTEF3/3752/2026-10-07T05:04:00Z", "ALTEF3/3752/2026-10-08T05:01:00Z")]
    b = Builder("s04-continuation-self-reference",
                "CONTINUED ALERT chain whose later reissues continue their own serial",
                ["continuation", "self-reference", "first-run-backlog", "expired"])
    b.fetch("2026-10-08T05:05:00Z", alerts_ok(ef, 'W/"fixture-s04-a"', "Thu, 08 Oct 2026 05:04:40 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "ALTEF3/3751", "reason": "new", "level": None}],
        "diagnostics": [],
        "notices": {"ALTEF3/3751": {"family": "flux-alert", "level": None, "state": "active",
                                    "fromUtc": "2026-10-05T18:30:00Z", "untilUtc": "2026-10-09T11:01:00Z",
                                    "latest": "ALTEF3/3752/2026-10-08T05:01:00Z", "versions": 4,
                                    "corrected": True, "orphan": False,
                                    "relevance": {hvo: "NoLocalObservingImpact"}}},
    })
    b.step("evaluate", {"notices": {"ALTEF3/3751": {"state": "active"}}}, atUtc="2026-10-09T11:00:59Z")
    b.step("evaluate", {"notices": {"ALTEF3/3751": {"state": "recent", "endReason": "expired"}}},
           atUtc="2026-10-09T11:01:00Z")
    out.append(b.build())

    # s05 ------------------------------------------------------------------
    k = {s: cap.rec(i) for s, i in ((5429, "WARK04/5429/2026-10-09T01:29:00Z"),
                                    (5430, "WARK04/5430/2026-10-10T05:38:00Z"),
                                    (5431, "WARK04/5431/2026-10-10T14:40:00Z"),
                                    (5432, "WARK04/5432/2026-10-11T04:57:00Z"))}
    b = Builder("s05-extension-chain-expiry",
                "EXTENDED WARNING chain keeps Valid From and moves the end; half-open expiry",
                ["extension", "expired", "half-open-validity"])
    b.fetch("2026-10-09T01:30:00Z", alerts_ok([k[5429]], 'W/"fixture-s05-a"', "Fri, 09 Oct 2026 01:29:55 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "WARK04/5429", "reason": "new", "level": "K4"}],
        "notices": {"WARK04/5429": {"family": "warning", "level": "K4", "state": "active",
                                    "fromUtc": "2026-10-09T01:28:00Z", "untilUtc": "2026-10-10T06:00:00Z",
                                    "relevance": {hvo: "NotExpected"}}},
    })
    b.fetch("2026-10-10T05:40:00Z",
            alerts_ok([k[5430], k[5429]], 'W/"fixture-s05-b"', "Sat, 10 Oct 2026 05:39:55 GMT"), {
        "outcome": "updated", "attention": [],
        "notices": {"WARK04/5429": {"untilUtc": "2026-10-10T15:00:00Z", "versions": 2,
                                    "latest": "WARK04/5430/2026-10-10T05:38:00Z"}},
    })
    b.step("evaluate", {"notices": {"WARK04/5429": {"state": "active"}}}, atUtc="2026-10-10T06:00:00Z")
    b.fetch("2026-10-11T04:58:00Z", alerts_ok(list(k.values()), 'W/"fixture-s05-c"',
                                              "Sun, 11 Oct 2026 04:57:55 GMT"), {
        "outcome": "updated", "attention": [],
        "notices": {"WARK04/5429": {"untilUtc": "2026-10-11T12:00:00Z", "versions": 4,
                                    "fromUtc": "2026-10-09T01:28:00Z",
                                    "latest": "WARK04/5432/2026-10-11T04:57:00Z"}},
    })
    b.step("evaluate", {"notices": {"WARK04/5429": {"state": "active", "mayBeOutOfDate": True}},
                        "products": {"alerts": {"freshness": "outdated"}}}, atUtc="2026-10-11T11:59:59Z")
    b.step("evaluate", {"notices": {"WARK04/5429": {"state": "recent", "endReason": "expired",
                                                    "mayBeOutOfDate": False}}},
           atUtc="2026-10-11T12:00:00Z")
    out.append(b.build())

    # s06 ------------------------------------------------------------------
    b = Builder("s06-extension-before-root",
                "An extension seen before its root warning is kept as an orphan and joined later",
                ["out-of-order", "orphan-link"])
    b.fetch("2026-10-10T05:40:00Z", alerts_ok([k[5430]], 'W/"fixture-s06-a"', "Sat, 10 Oct 2026 05:39:55 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "WARK04/5429", "reason": "new", "level": "K4"}],
        "diagnostics": ["orphan-link:WARK04/5430/2026-10-10T05:38:00Z"],
        "notices": {"WARK04/5429": {"orphan": True, "versions": 1, "state": "active",
                                    "fromUtc": "2026-10-09T01:28:00Z", "untilUtc": "2026-10-10T15:00:00Z"}},
    })
    b.fetch("2026-10-10T05:42:00Z",
            alerts_ok([k[5430], k[5429]], 'W/"fixture-s06-b"', "Sat, 10 Oct 2026 05:41:55 GMT"), {
        "outcome": "updated", "attention": [], "diagnostics": ["orphan-resolved:WARK04/5429"],
        "notices": {"WARK04/5429": {"orphan": False, "versions": 2,
                                    "latest": "WARK04/5430/2026-10-10T05:38:00Z",
                                    "untilUtc": "2026-10-10T15:00:00Z"}},
    })
    out.append(b.build())

    # s07 ------------------------------------------------------------------
    w = {s: cap.rec(i) for s, i in ((284, "WATA30/284/2026-10-09T08:12:00Z"),
                                    (285, "WATA30/285/2026-10-09T16:36:00Z"),
                                    (286, "WATA30/286/2026-10-11T17:02:00Z"))}
    b = Builder("s07-watch-supersession-and-cancel",
                "A same-level replacement watch, then a CANCEL WATCH that lists only remaining days",
                ["retracted", "cancel", "watch-supersession"])
    b.fetch("2026-10-09T08:15:00Z", alerts_ok([w[284]], 'W/"fixture-s07-a"', "Fri, 09 Oct 2026 08:14:50 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "WATA30/284", "reason": "new", "level": "G2"}],
        "notices": {"WATA30/284": {"state": "active", "fromUtc": "2026-10-09T08:12:00Z",
                                   "untilUtc": "2026-10-12T00:00:00Z", "relevance": {hvo: "NotExpected"}}},
    })
    b.fetch("2026-10-09T16:40:00Z",
            alerts_ok([w[285], w[284]], 'W/"fixture-s07-b"', "Fri, 09 Oct 2026 16:39:50 GMT"), {
        "outcome": "updated", "attention": [],
        "notices": {"WATA30/284": {"state": "recent", "endReason": "superseded",
                                   "untilUtc": "2026-10-09T16:36:00Z"},
                    "WATA30/285": {"state": "active", "fromUtc": "2026-10-09T16:36:00Z",
                                   "untilUtc": "2026-10-12T00:00:00Z"}},
    })
    b.step("evaluate", {"notices": {"WATA30/284": {"state": "history"}, "WATA30/285": {"state": "active"}}},
           atUtc="2026-10-11T16:00:00Z")
    b.fetch("2026-10-11T17:05:00Z",
            alerts_ok([w[286], w[285], w[284]], 'W/"fixture-s07-c"', "Sun, 11 Oct 2026 17:04:50 GMT"), {
        "outcome": "updated", "attention": [], "diagnostics": [],
        "notices": {"WATA30/284": {"state": "history"},
                    "WATA30/285": {"state": "recent", "endReason": "cancelled",
                                   "untilUtc": "2026-10-11T17:02:00Z",
                                   "latest": "WATA30/286/2026-10-11T17:02:00Z", "versions": 2}},
    })
    out.append(b.build())

    # s08 ------------------------------------------------------------------
    w1130 = cap.rec("WATA20/1130/2026-10-17T20:14:00Z")
    escalation = synthetic("A30F", "2026-10-18 20:00:12.000", "WATA30", 900, [
        "WATCH: Geomagnetic Storm Category G2 Predicted ",
        "Highest Storm Level Predicted by Day:",
        "Oct 19:  G1 (Minor)   Oct 20:  G2 (Moderate)   Oct 21:  None (Below G1)   ",
        "THIS SUPERSEDES ANY/ALL PRIOR WATCHES IN EFFECT",
        "Comment: Synthetic fixture escalation; not an SWPC product.",
    ])
    same_merge_g3 = synthetic("A50F", "2026-10-19 17:00:08.000", "WATA50", 100, [
        "WATCH: Geomagnetic Storm Category G3 Predicted ",
        "Highest Storm Level Predicted by Day:",
        "Oct 19:  G3 (Strong)   Oct 20:  G2 (Moderate)   Oct 21:  G1 (Minor)   ",
        "THIS SUPERSEDES ANY/ALL PRIOR WATCHES IN EFFECT",
        "Comment: Synthetic fixture for a same-merge replacement; not an SWPC product.",
    ])
    same_merge_g2 = synthetic("A30F", "2026-10-19 17:03:11.000", "WATA30", 901, [
        "WATCH: Geomagnetic Storm Category G2 Predicted ",
        "Highest Storm Level Predicted by Day:",
        "Oct 19:  G1 (Minor)   Oct 20:  G2 (Moderate)   Oct 21:  None (Below G1)   ",
        "THIS SUPERSEDES ANY/ALL PRIOR WATCHES IN EFFECT",
        "Comment: Synthetic fixture for a same-merge replacement; not an SWPC product.",
    ])
    b = Builder("s08-watch-replacement-and-escalation",
                "A same-level watch replaces silently; a higher-level watch escalates; watches first seen "
                "together are evaluated in root issue-time order",
                ["watch-supersession", "escalation", "new-grace"], synthetic_records=True,
                notes=["WATA30 serial 900 is synthetic and models a G2 watch superseding a G1 watch.",
                       "WATA50 serial 100 and WATA30 serial 901 are synthetic. Both are first seen in step 4: the "
                       "G3 watch was issued at 17:00 and replaced by the G2 watch at 17:03. The G3 watch is "
                       "evaluated first, escalates within the new-notice grace, and the G2 replacement is then "
                       "silent because an earlier watch in the same merge raised a higher rank."])
    b.fetch("2026-10-17T20:20:00Z", alerts_ok([w1130], 'W/"fixture-s08-a"', "Sat, 17 Oct 2026 20:19:50 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "WATA20/1130", "reason": "new", "level": "G1"}],
        "notices": {"WATA20/1130": {"state": "active", "untilUtc": "2026-10-21T00:00:00Z"}},
    })
    b.fetch("2026-10-18T17:30:00Z",
            alerts_ok([w1131, w1130], 'W/"fixture-s08-b"', "Sun, 18 Oct 2026 17:29:50 GMT"), {
        "outcome": "updated", "attention": [],
        "notices": {"WATA20/1130": {"state": "recent", "endReason": "superseded",
                                    "untilUtc": "2026-10-18T17:24:00Z"},
                    "WATA20/1131": {"state": "active"}},
    })
    b.fetch("2026-10-18T20:02:00Z",
            alerts_ok([escalation, w1131, w1130], 'W/"fixture-s08-c"', "Sun, 18 Oct 2026 20:01:50 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "WATA30/900", "reason": "escalated", "level": "G2"}],
        "notices": {"WATA20/1130": {"state": "recent"},
                    "WATA20/1131": {"state": "recent", "endReason": "superseded",
                                    "untilUtc": "2026-10-18T20:00:00Z"},
                    "WATA30/900": {"state": "active", "level": "G2", "untilUtc": "2026-10-21T00:00:00Z",
                                   "relevance": {hvo: "NotExpected"}}},
    })
    b.fetch("2026-10-19T17:04:00Z",
            alerts_ok([same_merge_g2, same_merge_g3, escalation, w1131, w1130], 'W/"fixture-s08-d"',
                      "Mon, 19 Oct 2026 17:03:50 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "WATA50/100", "reason": "escalated", "level": "G3"}],
        "notices": {"WATA20/1130": {"state": "recent"},
                    "WATA20/1131": {"state": "recent"},
                    "WATA30/900": {"state": "recent", "endReason": "superseded",
                                   "untilUtc": "2026-10-19T17:00:00Z"},
                    "WATA50/100": {"state": "recent", "endReason": "superseded", "level": "G3",
                                   "untilUtc": "2026-10-19T17:03:00Z"},
                    "WATA30/901": {"state": "active", "level": "G2", "untilUtc": "2026-10-21T00:00:00Z"}},
    })
    out.append(b.build())

    # s09 ------------------------------------------------------------------
    k2270 = cap.rec("WARK05/2270/2026-10-10T14:42:00Z")
    cancel = synthetic("K05W", "2026-10-10 16:00:31.000", "WARK05", 2271, [
        "CANCEL WARNING: Geomagnetic K-index of 5 expected ",
        "Cancel Serial Number: 2270",
        "Original Issue Time: 2026 Oct 10 1442 UTC",
        "Comment: Synthetic fixture cancellation; not an SWPC product.",
    ])
    absent_cancel = synthetic("K05W", "2026-10-10 16:03:20.000", "WARK05", 2272, [
        "CANCEL WARNING: Geomagnetic K-index of 5 expected ",
        "Cancel Serial Number: 2265",
        "Original Issue Time: 2026 Oct 10 1200 UTC",
        "Comment: Synthetic fixture cancellation of a warning that was never stored; not an SWPC product.",
    ])
    b = Builder("s09-synthetic-cancel-warning",
                "A CANCEL WARNING ends its target at the cancel issue time; a cancel of an absent target never alerts",
                ["retracted", "cancel", "orphan-link"], synthetic_records=True,
                notes=["No CANCEL WARNING was captured. Serials 2271 and 2272 are synthetic, shaped like the "
                       "captured CANCEL WATCH (WATA30 286) and the documented SWPC rules.",
                       "Serial 2272 cancels serial 2265, which this scenario never stores and the capture "
                       "does not contain."])
    b.fetch("2026-10-10T14:45:00Z", alerts_ok([k2270], 'W/"fixture-s09-a"', "Sat, 10 Oct 2026 14:44:50 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "WARK05/2270", "reason": "new", "level": "G1"}],
        "notices": {"WARK05/2270": {"state": "active", "fromUtc": "2026-10-10T14:40:00Z",
                                    "untilUtc": "2026-10-11T03:00:00Z", "relevance": {hvo: "NotExpected"}}},
    })
    b.fetch("2026-10-10T16:02:00Z",
            alerts_ok([cancel, k2270], 'W/"fixture-s09-b"', "Sat, 10 Oct 2026 16:01:50 GMT"), {
        "outcome": "updated", "attention": [],
        "notices": {"WARK05/2270": {"state": "recent", "endReason": "cancelled",
                                    "untilUtc": "2026-10-10T16:00:00Z", "versions": 2,
                                    "latest": "WARK05/2271/2026-10-10T16:00:00Z"}},
    })
    b.fetch("2026-10-10T16:04:00Z",
            alerts_ok([absent_cancel, cancel, k2270], 'W/"fixture-s09-c"', "Sat, 10 Oct 2026 16:03:50 GMT"), {
        "outcome": "updated", "attention": [],
        "diagnostics": ["orphan-link:WARK05/2272/2026-10-10T16:03:00Z"],
        "notices": {"WARK05/2265": {"family": "warning", "level": None, "state": "recent",
                                    "endReason": "cancelled", "orphan": True, "versions": 1,
                                    "fromUtc": "2026-10-10T16:03:00Z", "untilUtc": "2026-10-10T16:03:00Z",
                                    "latest": "WARK05/2272/2026-10-10T16:03:00Z"},
                    "WARK05/2270": {"state": "recent", "versions": 2}},
    })
    out.append(b.build())

    # s10 ------------------------------------------------------------------
    a2691 = cap.rec("ALTK04/2691/2026-10-16T19:39:00Z")
    w5436 = cap.rec("WARK04/5436/2026-10-16T19:38:00Z")
    s930 = cap.rec("SUM10R/930/2026-10-19T09:09:00Z")
    extension = synthetic("K04W", "2026-10-16 19:53:40.000", "WARK04", 5437, [
        "EXTENDED WARNING: Geomagnetic K-index of 4 expected",
        "Extension to Serial Number: 5436",
        "Valid From: 2026 Oct 16 1938 UTC",
        "Now Valid Until: 2026 Oct 17 0900 UTC",
        "Warning Condition: Persistence",
        "Comment: Synthetic fixture extension; not an SWPC product.",
    ])
    b = Builder("s10-restart-ack-rolling-window",
                "Restart does not re-alert; acknowledgement persists across a later extension; aged-out records "
                "stay; retention purges, a summary 35 days after its issue time",
                ["restart", "acknowledgement", "extension", "rolling-window", "retention"], synthetic_records=True,
                notes=["WARK04 serial 5437 is synthetic. It extends the real WARK04 5436, which the capture never "
                       "extended."])
    body = alerts_ok([a2691, w5436], 'W/"fixture-s10-a"', "Fri, 16 Oct 2026 19:44:50 GMT")
    b.fetch("2026-10-16T19:45:00Z", body, {
        "outcome": "updated",
        "attention": [{"group": "ALTK04/2691", "reason": "new", "level": "K4"},
                      {"group": "WARK04/5436", "reason": "new", "level": "K4"}],
        "notices": {"ALTK04/2691": {"family": "k-alert", "state": "active", "fromUtc": "2026-10-16T19:40:00Z",
                                    "untilUtc": "2026-10-16T21:00:00Z"},
                    "WARK04/5436": {"state": "active", "untilUtc": "2026-10-17T03:00:00Z"}},
    })
    b.step("acknowledge", None, atUtc="2026-10-16T19:50:00Z", group="WARK04/5436", operator="fixture-operator")
    b.step("restart")
    b.fetch("2026-10-16T19:52:00Z", dict(body, etag='W/"fixture-s10-b"'), {
        "outcome": "unchanged", "attention": [],
        "notices": {"ALTK04/2691": {"acknowledged": None},
                    "WARK04/5436": {"acknowledged": {"operator": "fixture-operator",
                                                     "atUtc": "2026-10-16T19:50:00Z"}}},
    })
    b.fetch("2026-10-16T19:54:00Z", alerts_ok([extension, a2691, w5436], 'W/"fixture-s10-ext"',
                                              "Fri, 16 Oct 2026 19:53:50 GMT"), {
        "outcome": "updated", "attention": [], "diagnostics": [],
        "notices": {"ALTK04/2691": {"acknowledged": None},
                    "WARK04/5436": {"state": "active", "untilUtc": "2026-10-17T09:00:00Z", "versions": 2,
                                    "latest": "WARK04/5437/2026-10-16T19:53:00Z",
                                    "acknowledged": {"operator": "fixture-operator",
                                                     "atUtc": "2026-10-16T19:50:00Z"}}},
    })
    b.fetch("2026-11-13T19:45:00Z", alerts_ok([s930], 'W/"fixture-s10-c"', "Fri, 13 Nov 2026 19:44:50 GMT"), {
        "outcome": "updated", "attention": [],
        "notices": {"ALTK04/2691": {"state": "history"},
                    "WARK04/5436": {"state": "history",
                                    "acknowledged": {"operator": "fixture-operator",
                                                     "atUtc": "2026-10-16T19:50:00Z"}},
                    "SUM10R/930": {"family": "summary", "state": "history", "endReason": None}},
    })
    b.step("evaluate", {"notices": {"WARK04/5436": {"state": "history"}, "SUM10R/930": {"state": "history"}}},
           atUtc="2026-11-20T21:00:00Z")
    b.step("evaluate", {"notices": {"SUM10R/930": {"state": "history"}}}, atUtc="2026-11-21T09:00:00Z")
    # A summary's retention is anchored at its root issue time (2026-10-19T09:09Z).
    b.step("evaluate", {"notices": {"SUM10R/930": {"state": "history", "endReason": None}}},
           atUtc="2026-11-23T09:08:59Z")
    b.step("evaluate", {"notices": {}}, atUtc="2026-11-23T09:09:00Z")
    out.append(b.build())

    # s11 ------------------------------------------------------------------
    tp4 = cap.rec("ALTTP4/719/2026-10-08T16:44:00Z")
    current = alerts_ok([tp2_b, tp4, tp2_a], 'W/"fixture-s11-a"', "Thu, 08 Oct 2026 16:46:55 GMT")
    tp4_older = copy.deepcopy(tp4)
    tp4_older["message"] = tp4_older["message"].replace("Comment: Type IV recorded by Sagamore Hill RSTN site",
                                                        "Comment: Fixture older text.", 1)
    if tp4_older["message"] == tp4["message"]:
        raise ScenarioError("s11 older text did not change the message")
    b = Builder("s11-stale-cdn-out-of-order",
                "A body with an older in-file stamp neither revises stored records nor replaces last-known-good",
                ["out-of-order", "stale-snapshot", "first-run-backlog", "new-grace"], synthetic_records=True,
                notes=["Step 2's ALTTP4 719 is the real record with its Comment line altered, modelling an older "
                       "text served by a stale cache. No such text was observed in the capture.",
                       "ALTTP2 1531 has already ended when first seen at 16:47, but its latest version was "
                       "issued at 16:46, inside the new-notice grace window, so it alerts."])
    b.fetch("2026-10-08T16:47:00Z", current, {
        "outcome": "updated",
        "attention": [{"group": "ALTTP2/1531", "reason": "new", "level": None},
                      {"group": "ALTTP4/719", "reason": "new", "level": None}],
        "notices": {"ALTTP2/1531": {"state": "recent", "versions": 2,
                                    "latest": "ALTTP2/1531/2026-10-08T16:46:00Z"},
                    "ALTTP4/719": {"state": "active", "fromUtc": "2026-10-08T16:12:00Z",
                                   "untilUtc": "2026-10-08T17:12:00Z"}},
    })
    stale = alerts_ok([tp4_older, tp2_a], 'W/"fixture-s11-old"', "Thu, 08 Oct 2026 16:44:55 GMT")
    b.fetch("2026-10-08T16:49:00Z", stale, {
        "outcome": "stale-snapshot", "attention": [],
        "diagnostics": ["stale-revision-ignored:ALTTP4/719/2026-10-08T16:44:00Z", "stale-snapshot"],
        "notices": {"ALTTP2/1531": {"versions": 2, "latest": "ALTTP2/1531/2026-10-08T16:46:00Z",
                                    "fromUtc": "2026-10-08T15:42:00Z"},
                    "ALTTP4/719": {"state": "active", "versions": 1}},
        "product": {"lkgSha256": sha256(current["body"].encode()), "lastSuccessUtc": "2026-10-08T16:47:00Z",
                    "validators": {"etag": 'W/"fixture-s11-a"'}},
    })
    b.fetch("2026-10-08T16:51:00Z", current, {
        "request": {"ifNoneMatch": 'W/"fixture-s11-a"'},
        "outcome": "unchanged", "attention": [], "diagnostics": [],
        "product": {"lastSuccessUtc": "2026-10-08T16:51:00Z"},
    })
    out.append(b.build())

    # s12 ------------------------------------------------------------------
    good = alerts_ok([a2691], 'W/"fixture-s12-a"', "Fri, 16 Oct 2026 19:44:50 GMT")
    good_sha = sha256(good["body"].encode())
    no_message = {"product_id": "K04W", "issue_datetime": "2026-10-16 19:38:53.387"}
    iso_datetime = dict(w5436, issue_datetime="2026-10-16T19:38:53Z")
    no_serial = dict(w5436, message=w5436["message"].replace("Serial Number: 5436\r\n", "", 1))
    unknown = synthetic("ZZZA", "2026-10-16 19:50:10.000", "ALTZZZ", 1, [
        "ALERT: Synthetic fixture product with an unknown code ",
        "Comment: Synthetic; not an SWPC product.",
    ])
    mismatch = dict(cap.rec("ALTEF3/3754/2026-10-16T14:19:00Z"), issue_datetime="2026-10-16 14:25:49.850")
    oversize_record = dict(w5436, message=w5436["message"] + "\r\nComment: " + "x" * POLICY["maxMessageBytes"])
    header_only = {"product_id": "K04A", "issue_datetime": "2026-10-16 19:51:02.000",
                   "message": "Space Weather Message Code: ALTK04\r\nSerial Number: 2692\r\n"
                              "Issue Time: 2026 Oct 16 1951 UTC\r\n"}
    long_serial = dict(a2691, message=a2691["message"].replace("Serial Number: 2691\r\n",
                                                               "Serial Number: 1234567\r\n", 1))
    spaced_issue = dict(a2691, message=a2691["message"].replace("Issue Time: 2026 Oct 16 1939 UTC",
                                                                "Issue Time:  2026 Oct 16 1939 UTC", 1))
    surrogate = dict(a2691, message=a2691["message"] + "\ud800")
    if no_serial["message"] == w5436["message"] or long_serial["message"] == a2691["message"] \
            or spaced_issue["message"] == a2691["message"]:
        raise ScenarioError("s12 record damage did not change the message")
    mixed = alerts_ok([unknown, w5436, no_message, iso_datetime, no_serial, mismatch, oversize_record,
                       header_only, long_serial, spaced_issue, surrogate], 'W/"fixture-s12-mixed"',
                      "Fri, 16 Oct 2026 19:52:50 GMT", order=False)
    non_utf8 = b'[{"product_id":"K04A","issue_datetime":"2026-10-16 19:39:45.267","message":"\xff\xfe"}]'
    b = Builder("s12-malformed-bodies",
                "Rejected bodies keep last-known-good; record-level faults reject only the record and raise "
                "record drift once",
                ["malformed", "quarantine", "format-drift", "record-drift", "unknown-code"], synthetic_records=True,
                notes=["Every body after step 1 is synthetic or a damaged copy of a real body.",
                       "Step 5's ALTEF3 3754 is the real record with its issue_datetime moved six minutes later; "
                       "its other damaged records are copies of WARK04 5436 and ALTK04 2691, plus a synthetic "
                       "header-only ALTK04 2692. Index 9 doubles the space after 'Issue Time:', and index 10 "
                       "ends its message with the unpaired surrogate escape \\ud800."])
    b.fetch("2026-10-16T19:45:00Z", good, {"outcome": "updated", "product": {"lkgSha256": good_sha}})
    b.fetch("2026-10-16T19:47:00Z", dict(good, body=good["body"][:len(good["body"]) // 2], etag='W/"fixture-s12-trunc"'), {
        "outcome": "rejected", "diagnostics": ["body-rejected:malformed-json"], "attention": [],
        "product": {"lkgSha256": good_sha, "consecutiveRejected": 1,
                    "nextAttemptNotBeforeUtc": "2026-10-16T19:49:00Z",
                    "validators": {"etag": 'W/"fixture-s12-a"'}},
    })
    b.fetch("2026-10-16T19:49:00Z", dict(good, body=encode_alerts(a2691)), {
        "outcome": "rejected", "diagnostics": ["body-rejected:schema"], "attention": [],
        "product": {"consecutiveRejected": 2},
    })
    html = {"status": 200, "contentType": "text/html; charset=UTF-8", "etag": None, "lastModified": None,
            "body": "<html><body>Service temporarily unavailable</body></html>"}
    b.fetch("2026-10-16T19:51:00Z", html, {
        "outcome": "rejected", "diagnostics": ["body-rejected:content-type"],
        "attention": [{"group": "provider/alerts/format-drift", "reason": "new", "level": None}],
        "product": {"consecutiveRejected": 3, "lkgSha256": good_sha, "freshness": "stale"},
    })
    all_active = {"ALTEF3/3754": {"state": "active"}, "ALTK04/2691": {"state": "active"},
                  "ALTZZZ/1": {"state": "active"}, "WARK04/5436": {"state": "active"}}
    b.fetch("2026-10-16T19:53:00Z", mixed, {
        "outcome": "updated",
        "diagnostics": ["issue-time-mismatch:ALTEF3/3754/2026-10-16T14:19:00Z",
                        "record-rejected:issue-datetime:3", "record-rejected:message-header:4",
                        "record-rejected:message-header:7", "record-rejected:message-header:8",
                        "record-rejected:message-header:9", "record-rejected:record-oversize:6",
                        "record-rejected:record-schema:10", "record-rejected:record-schema:2"],
        "attention": [{"group": "ALTEF3/3754", "reason": "new", "level": None},
                      {"group": "ALTZZZ/1", "reason": "new", "level": None},
                      {"group": "WARK04/5436", "reason": "new", "level": "K4"},
                      {"group": "provider/alerts/record-drift", "reason": "new", "level": None}],
        "notices": dict(all_active, **{
            "ALTZZZ/1": {"family": "unclassified", "state": "active",
                         "fromUtc": "2026-10-16T19:50:00Z", "untilUtc": "2026-10-17T01:50:00Z",
                         "relevance": {hvo: "Unknown"}}}),
        "product": {"consecutiveRejected": 0, "lkgSha256": sha256(mixed["body"].encode()),
                    "recordDriftOpen": True, "recordDriftStampUtc": "2026-10-16T19:50:00Z",
                    "facts": {"records": 11, "acceptedRecords": 3}},
    })
    b.fetch("2026-10-16T19:55:00Z", dict(good, body="[]"), {
        "outcome": "rejected", "diagnostics": ["body-rejected:suspect-empty"], "attention": [],
        "notices": all_active,
        "product": {"consecutiveRejected": 1, "recordDriftOpen": True},
    })
    oversize = {"status": 200, "contentType": "application/json", "etag": None, "lastModified": None,
                "bodySynthesis": {"kind": "padded-empty-array", "totalBytes": 1048577}}
    b.fetch("2026-10-16T19:57:00Z", oversize, {
        "outcome": "rejected", "diagnostics": ["body-rejected:oversize"], "product": {"consecutiveRejected": 2},
    })
    oversize_step = len(b.doc["steps"])
    b.fetch("2026-10-16T19:59:00Z", {"status": 200, "contentType": "application/json", "etag": None,
                                     "lastModified": None,
                                     "bodyBase64": base64.b64encode(non_utf8).decode("ascii")}, {
        "outcome": "rejected", "diagnostics": ["body-rejected:encoding"],
        "attention": [{"group": "provider/alerts/format-drift", "reason": "new", "level": None}],
        "product": {"consecutiveRejected": 3},
    })
    b.fetch("2026-10-16T20:01:00Z", dict(good, body=encode_alerts([no_message, header_only])), {
        "outcome": "rejected", "diagnostics": ["body-rejected:no-valid-records"], "attention": [],
        "product": {"consecutiveRejected": 4},
    })
    small = {"product_id": "ZZZA", "issue_datetime": "2026-10-16 20:00:00.000", "message": "Synthetic"}
    many = {"status": 200, "contentType": "application/json", "etag": None, "lastModified": None,
            "bodySynthesis": {"kind": "repeated-record", "record": small, "count": POLICY["maxRecords"] + 1}}
    b.fetch("2026-10-16T20:03:00Z", many, {
        "outcome": "rejected", "diagnostics": ["body-rejected:too-many-records"], "attention": [],
        "notices": all_active,
        "product": {"consecutiveRejected": 5, "recordDriftOpen": True, "lkgSha256": sha256(mixed["body"].encode())},
    })
    many_step = len(b.doc["steps"])
    b.doc["notes"] += [f"Step {oversize_step} body is described, not embedded: a valid JSON empty array padded "
                       "with spaces to 1 MiB + 1 byte.",
                       f"Step {many_step} body is described, not embedded: 2,001 copies of one small record, "
                       "within the byte limit."]
    out.append(b.build())

    # s13 ------------------------------------------------------------------
    ok = alerts_ok([s930], '"fixture-s13-a"', "Mon, 19 Oct 2026 18:59:30 GMT")
    sha_ok = sha256(ok["body"].encode())
    b = Builder("s13-outage-backoff-halts",
                "Timeout and 5xx back off, 304 and 429 follow SWPC policy, 403 and moves halt",
                ["outage", "backoff", "rate-limit", "halt", "manual-refresh", "not-modified"])
    b.fetch("2026-10-19T19:00:00Z", ok, {
        "outcome": "updated", "product": {"nextAttemptNotBeforeUtc": "2026-10-19T19:02:00Z"}})
    b.fetch("2026-10-19T19:02:00Z", {"outcome": "timeout"}, {
        "outcome": "transient-failure",
        "product": {"consecutiveTransientFailures": 1, "nextAttemptNotBeforeUtc": "2026-10-19T19:06:00Z",
                    "freshness": "fresh", "lkgSha256": sha_ok}})
    b.fetch("2026-10-19T19:06:00Z", {"status": 503}, {
        "outcome": "transient-failure",
        "product": {"consecutiveTransientFailures": 2, "nextAttemptNotBeforeUtc": "2026-10-19T19:14:00Z",
                    "freshness": "stale"}})
    b.fetch("2026-10-19T19:10:00Z", None, {"refused": "backoff"}, trigger="manual")
    b.fetch("2026-10-19T19:14:00Z", {"status": 502}, {
        "outcome": "transient-failure",
        "product": {"consecutiveTransientFailures": 3, "nextAttemptNotBeforeUtc": "2026-10-19T19:30:00Z"}})
    b.fetch("2026-10-19T19:30:00Z", {"status": 304, "etag": '"fixture-s13-a"'}, {
        "request": {"ifNoneMatch": '"fixture-s13-a"', "ifModifiedSince": "Mon, 19 Oct 2026 18:59:30 GMT"},
        "outcome": "not-modified",
        "product": {"consecutiveTransientFailures": 0, "freshness": "fresh",
                    "nextAttemptNotBeforeUtc": "2026-10-19T19:32:00Z", "lastSuccessUtc": "2026-10-19T19:30:00Z"}})
    b.fetch("2026-10-19T19:30:30Z", None, {"refused": "min-interval"}, trigger="manual")
    b.fetch("2026-10-19T19:31:00Z", dict(ok, etag='"fixture-s13-b"'), {
        "outcome": "unchanged", "product": {"nextAttemptNotBeforeUtc": "2026-10-19T19:33:00Z"}},
        trigger="manual")
    b.fetch("2026-10-19T19:33:00Z", {"status": 429, "retryAfter": "30"}, {
        "outcome": "rate-limited",
        "product": {"consecutiveRateLimited": 1, "nextAttemptNotBeforeUtc": "2026-10-19T19:34:00Z"}})
    b.fetch("2026-10-19T19:34:00Z", dict(ok, etag='"fixture-s13-c"'), {
        "outcome": "unchanged", "product": {"consecutiveRateLimited": 0}})
    b.fetch("2026-10-19T19:36:00Z", {"status": 429, "retryAfter": "Mon, 19 Oct 2026 19:56:00 GMT"}, {
        "outcome": "rate-limited",
        "product": {"consecutiveRateLimited": 1, "nextAttemptNotBeforeUtc": "2026-10-19T19:56:00Z"}})
    b.fetch("2026-10-19T19:56:00Z", {"status": 429}, {
        "outcome": "rate-limited",
        "product": {"consecutiveRateLimited": 2, "nextAttemptNotBeforeUtc": "2026-10-19T20:26:00Z"}})
    b.fetch("2026-10-19T20:26:00Z", {"status": 429, "retryAfter": "600"}, {
        "outcome": "halted",
        "product": {"halt": {"scope": "provider", "reason": "rate-limited", "sinceUtc": "2026-10-19T20:26:00Z"},
                    "nextAttemptNotBeforeUtc": None, "retryNotBeforeUtc": "2026-10-19T20:36:00Z",
                    "consecutiveRateLimited": 3}})
    b.fetch("2026-10-19T20:30:00Z", None, {"refused": "halted"}, product="scales", trigger="manual")
    b.step("restart")
    b.step("evaluate", {"products": {"alerts": {"halt": {"scope": "provider", "reason": "rate-limited"},
                                                "lkgSha256": sha_ok, "retryNotBeforeUtc": "2026-10-19T20:36:00Z"},
                                     "scales": {"halt": {"scope": "provider", "reason": "rate-limited"}}}},
           atUtc="2026-10-19T20:34:50Z")
    b.step("resume", {"products": {"alerts": {"halt": None, "nextAttemptNotBeforeUtc": "2026-10-19T20:36:00Z",
                                              "retryNotBeforeUtc": None, "consecutiveRateLimited": 3}}},
           atUtc="2026-10-19T20:35:00Z", scope="provider", operator="fixture-operator")
    b.fetch("2026-10-19T20:35:30Z", None, {"refused": "backoff"}, trigger="manual")
    b.fetch("2026-10-19T20:36:00Z", {"status": 403}, {
        "outcome": "halted",
        "product": {"halt": {"scope": "provider", "reason": "forbidden", "sinceUtc": "2026-10-19T20:36:00Z"}}})
    b.fetch("2026-10-19T20:36:30Z", None, {"refused": "halted"}, trigger="manual")
    b.step("resume", None, atUtc="2026-10-19T20:50:00Z", scope="provider", operator="fixture-operator")
    b.fetch("2026-10-19T20:50:00Z", {"status": 301}, {
        "outcome": "halted",
        "product": {"halt": {"scope": "product", "reason": "endpoint-moved", "sinceUtc": "2026-10-19T20:50:00Z"}}})
    b.fetch("2026-10-19T20:51:00Z", {"status": 200, "contentType": "application/json", "etag": None,
                                     "lastModified": None,
                                     "bodyFile": CAPTURE + "/noaa-scales.json"}, {
        "outcome": "updated", "product": {"halt": None}}, product="scales")
    b.step("resume", None, atUtc="2026-10-19T21:00:00Z", scope="product", product="alerts",
           operator="fixture-operator")
    b.fetch("2026-10-19T21:00:00Z", {"status": 404}, {
        "outcome": "halted",
        "product": {"halt": {"scope": "product", "reason": "not-found", "sinceUtc": "2026-10-19T21:00:00Z"},
                    "lkgSha256": sha_ok}})
    out.append(b.build())

    # s14 ------------------------------------------------------------------
    rollover = synthetic("A20F", "2026-12-31 21:00:05.000", "WATA20", 1200, [
        "WATCH: Geomagnetic Storm Category G1 Predicted ",
        "Highest Storm Level Predicted by Day:",
        "Jan 01:  G1 (Minor)   Jan 02:  G1 (Minor)   Jan 03:  None (Below G1)   ",
        "THIS SUPERSEDES ANY/ALL PRIOR WATCHES IN EFFECT",
        "Comment: Synthetic fixture for year rollover; not an SWPC product.",
    ])
    rollover_forecast = "\n".join([
        ":Product: 3-Day Forecast",
        ":Issued: 2026 Dec 31 1230 UTC",
        "# Synthetic fixture for year rollover; not an SWPC product.",
        "#",
        "A. NOAA Geomagnetic Activity Observation and Forecast",
        "",
        "NOAA Kp index breakdown Dec 31-Jan 02 2027",
        "",
        "             Dec 31       Jan 01       Jan 02",
        "00-03UT       1.33         2.00         3.67",
        "03-06UT       1.00         2.33         4.00",
        "06-09UT       1.00         2.67         3.67",
        "09-12UT       1.33         3.00         3.33",
        "12-15UT       1.67         3.33         3.00",
        "15-18UT       2.00         3.67         3.00",
        "18-21UT       2.00         4.33         2.67",
        "21-00UT       2.33         5.00 (G1)    2.33",
        "",
        "B. NOAA Solar Radiation Activity Observation and Forecast",
        "",
        "Solar Radiation Storm Forecast for Dec 31-Jan 02 2027",
        "",
        "              Dec 31  Jan 01  Jan 02",
        "S1 or greater    1%      1%      1%",
        "",
        "C. NOAA Radio Blackout Activity and Forecast",
        "",
        "Radio Blackout Forecast for Dec 31-Jan 02 2027",
        "",
        "              Dec 31        Jan 01        Jan 02",
        "R1-R2           15%           15%           10%",
        "R3 or greater    1%            1%            1%",
        "",
    ])
    b = Builder("s14-year-rollover-and-local-day",
                "Watch days and 3-day forecast columns without a year cross 31 December; UTC days shown in site "
                "time",
                ["timezone-boundary", "year-rollover"], synthetic_records=True,
                notes=["The site zone is America/Phoenix (UTC-07:00, no daylight saving).",
                       "WATA20 serial 1200 and the 3-day forecast issued 2026 Dec 31 1230 UTC are synthetic. "
                       "The forecast's column years come from its :Issued: stamp, not from the year printed in "
                       "its table titles."])
    b.fetch("2026-12-31T21:02:00Z", alerts_ok([rollover], 'W/"fixture-s14-a"', "Thu, 31 Dec 2026 21:01:50 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "WATA20/1200", "reason": "new", "level": "G1"}],
        "notices": {"WATA20/1200": {"state": "active", "fromUtc": "2026-12-31T21:00:00Z",
                                    "untilUtc": "2027-01-03T00:00:00Z",
                                    "days": [{"dateUtc": "2027-01-01", "level": "G1"},
                                             {"dateUtc": "2027-01-02", "level": "G1"},
                                             {"dateUtc": "2027-01-03", "level": "None"}],
                                    "local": {"fromLocal": "2026-12-31T14:00:00-07:00",
                                              "untilLocal": "2027-01-02T17:00:00-07:00"}}},
    })
    b.fetch("2026-12-31T21:03:00Z", {"status": 200, "contentType": "text/plain", "etag": '"fixture-s14-3day"',
                                     "lastModified": "Thu, 31 Dec 2026 12:31:00 GMT",
                                     "body": rollover_forecast}, {
        "outcome": "updated",
        "product": {"facts": {
            "issuedUtc": "2026-12-31T12:30:00Z", "kpBreakdownBins": 24,
            "kpBreakdown": {"2026-12-31T00:00:00Z": {"kp": "1.33", "noaaScale": None},
                            "2027-01-01T00:00:00Z": {"kp": "2.00", "noaaScale": None},
                            "2027-01-01T21:00:00Z": {"kp": "5.00", "noaaScale": "G1"},
                            "2027-01-02T21:00:00Z": {"kp": "2.33", "noaaScale": None}},
            "radiationProbabilities": {"S1 or greater": {"2026-12-31": "1", "2027-01-01": "1",
                                                         "2027-01-02": "1"}},
            "radioBlackoutProbabilities": {"R1-R2": {"2026-12-31": "15", "2027-01-01": "15", "2027-01-02": "10"},
                                           "R3 or greater": {"2026-12-31": "1", "2027-01-01": "1",
                                                             "2027-01-02": "1"}}}}},
        product="3-day-forecast")
    b.step("evaluate", {"notices": {"WATA20/1200": {"state": "active"}}}, atUtc="2027-01-02T23:59:59Z")
    b.step("evaluate", {"notices": {"WATA20/1200": {"state": "recent", "endReason": "expired"}}},
           atUtc="2027-01-03T00:00:00Z")
    out.append(b.build())

    # s15 ------------------------------------------------------------------
    ef3754 = cap.rec("ALTEF3/3754/2026-10-16T14:19:00Z")
    k153 = cap.rec("WARK07/153/2026-10-10T17:42:00Z")
    b = Builder("s15-missing-scale-and-location",
                "Messages without a scale or impact line, null predicted scales, and a site without a location",
                ["missing-scale", "missing-location", "location-relevance", "first-run-backlog"],
                sites=[HVO_SITE, NO_LOCATION_SITE])
    b.fetch("2026-10-16T14:25:00Z", alerts_ok([ef3754, k153], 'W/"fixture-s15-a"',
                                              "Fri, 16 Oct 2026 14:24:50 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "ALTEF3/3754", "reason": "new", "level": None}],
        "notices": {"ALTEF3/3754": {"level": None, "state": "active",
                                    "relevance": {hvo: "NoLocalObservingImpact",
                                                  "unlocated": "NoLocalObservingImpact"}},
                    "WARK07/153": {"level": "G3", "state": "history",
                                   "relevance": {hvo: "Unlikely", "unlocated": "Unknown"}}},
    })
    b.fetch("2026-10-19T18:57:03Z", {"status": 200, "contentType": "application/json",
                                     "etag": 'W/"44e-65e360fa970f3"',
                                     "lastModified": "Mon, 19 Oct 2026 18:55:44 GMT",
                                     "bodyFile": CAPTURE + "/noaa-scales.json"}, {
        "outcome": "updated",
        "product": {"facts": {
            "current": {"stampUtc": "2026-10-19T18:55:00Z", "rScale": "0", "sScale": "0", "gScale": "0"},
            "observed24hMax": {"stampUtc": "2026-10-18T18:55:00Z", "rScale": "1"},
            "predictedDay1": {"stampUtc": "2026-10-19T18:55:00Z", "rScale": None, "sScale": None,
                              "rMinorProbPct": "25", "rMajorProbPct": "5", "sProbPct": "5", "gScale": "1"},
            "predictedDay3": {"stampUtc": "2026-10-21T00:00:00Z", "gScale": "0"}}}},
        product="scales")
    out.append(b.build())

    # s16 ------------------------------------------------------------------
    b = Builder("s16-kp-estimated-future-bin",
                "Kp bins keep the source label verbatim; the time relation is computed from the bin start and end "
                "when read",
                ["kp-forecast", "label-versus-time"],
                notes=["At 18:57:03 the 21:00 bin is labelled estimated by the source but is still in the future.",
                       "The relation is a read-time view, not a stored fact; step 2 reads the same snapshot later."])
    b.fetch("2026-10-19T18:57:03Z", {"status": 200, "contentType": "application/json",
                                     "etag": 'W/"1af9-65e361333c7ac"',
                                     "lastModified": "Mon, 19 Oct 2026 18:56:43 GMT",
                                     "bodyFile": CAPTURE + "/noaa-planetary-k-index-forecast.json"}, {
        "outcome": "updated",
        "product": {"facts": {
            "rowCount": 81, "firstBinStartUtc": "2026-10-12T00:00:00Z", "lastBinStartUtc": "2026-10-22T00:00:00Z",
            "bins": {"2026-10-19T15:00:00Z": {"kp": "0.33", "label": "observed", "noaaScale": None},
                     "2026-10-19T18:00:00Z": {"kp": "0.67", "label": "estimated", "noaaScale": None},
                     "2026-10-19T21:00:00Z": {"kp": "5.00", "label": "estimated", "noaaScale": "G1"},
                     "2026-10-20T03:00:00Z": {"kp": "4.67", "label": "predicted", "noaaScale": "G1"}}},
            "kpBinRelations": {"2026-10-19T15:00:00Z": "past", "2026-10-19T18:00:00Z": "current",
                               "2026-10-19T21:00:00Z": "future", "2026-10-20T03:00:00Z": "future"}}},
        product="kp-forecast")
    b.step("evaluate", {"products": {"kp-forecast": {
        "kpBinRelations": {"2026-10-19T18:00:00Z": "past", "2026-10-19T21:00:00Z": "current",
                           "2026-10-20T03:00:00Z": "future"},
        "facts": {"bins": {"2026-10-19T21:00:00Z": {"label": "estimated"}}}}}}, atUtc="2026-10-19T21:30:00Z")
    out.append(b.build())

    # s17 ------------------------------------------------------------------
    original = json.loads((PACK / THREE_DAY_ORIGINAL).read_text("ascii"))
    normalized_sha = sha256(normalize_text(original["text"]).encode("utf-8"))
    renamed = original["text"].replace("NOAA Kp index breakdown", "NOAA Kp Index Forecast Breakdown", 1)
    no_radiation_rows = re.sub(r"(?m)^S1 or greater.*\n", "", original["text"], count=1)
    if renamed == original["text"] or no_radiation_rows == original["text"]:
        raise ScenarioError("s17 damage did not change the text")
    b = Builder("s17-three-day-issue-versus-last-modified",
                "The text product's :Issued: stamp is the issue time; HTTP Last-Modified churns; a renamed table "
                "or a probability table with no rows rejects the body",
                ["text-normalization", "issue-time", "etag-churn", "malformed"], synthetic_records=True,
                notes=["The body is the exact original, including trailing spaces; the snapshot hash is over "
                       "the normalized text.",
                       "Step 2's validators are those of the 19:00:23Z re-request, which returned an identical "
                       "body.",
                       "Step 3's body is the original with its Kp table title renamed; it is synthetic.",
                       "Step 4's body is the original with its 'S1 or greater' row removed, so the solar "
                       "radiation table has a title and a column header but no rows; it is synthetic."])
    text_response = {"status": 200, "contentType": "text/plain", "etag": '"7fe-65e360f9121ca-gzip"',
                     "lastModified": "Mon, 19 Oct 2026 18:55:42 GMT", "body": original["text"]}
    b.fetch("2026-10-19T18:57:03Z", text_response, {
        "outcome": "updated",
        "product": {"lkgSha256": normalized_sha,
                    "facts": {"issuedUtc": "2026-10-19T12:30:00Z", "kpBreakdownBins": 24,
                              "kpBreakdown": {"2026-10-20T03:00:00Z": {"kp": "4.67", "noaaScale": "G1"},
                                              "2026-10-19T21:00:00Z": {"kp": "4.33", "noaaScale": None}},
                              "radiationProbabilities": {"S1 or greater": {"2026-10-19": "5", "2026-10-20": "5",
                                                                           "2026-10-21": "5"}},
                              "radioBlackoutProbabilities": {
                                  "R1-R2": {"2026-10-19": "30", "2026-10-20": "30", "2026-10-21": "30"},
                                  "R3 or greater": {"2026-10-19": "10", "2026-10-20": "10",
                                                    "2026-10-21": "10"}}}}},
        product="3-day-forecast")
    b.fetch("2026-10-19T19:27:03Z", dict(text_response, etag='"7fe-65e361ddb4bcb-gzip"',
                                         lastModified="Mon, 19 Oct 2026 18:59:42 GMT"), {
        "request": {"ifNoneMatch": '"7fe-65e360f9121ca-gzip"', "ifModifiedSince": "Mon, 19 Oct 2026 18:55:42 GMT"},
        "outcome": "unchanged", "product": {"lkgSha256": normalized_sha,
                                            "facts": {"issuedUtc": "2026-10-19T12:30:00Z"}}},
        product="3-day-forecast")
    b.fetch("2026-10-19T19:57:03Z", dict(text_response, etag='"fixture-s17-renamed"', body=renamed), {
        "outcome": "rejected", "diagnostics": ["body-rejected:schema"],
        "product": {"lkgSha256": normalized_sha, "consecutiveRejected": 1, "freshness": "fresh",
                    "lastSuccessUtc": "2026-10-19T19:27:03Z",
                    "validators": {"etag": '"7fe-65e361ddb4bcb-gzip"'},
                    "facts": {"issuedUtc": "2026-10-19T12:30:00Z"}}},
        product="3-day-forecast")
    b.fetch("2026-10-19T20:27:03Z", dict(text_response, etag='"fixture-s17-no-rows"', body=no_radiation_rows), {
        "outcome": "rejected", "diagnostics": ["body-rejected:schema"],
        "product": {"lkgSha256": normalized_sha, "consecutiveRejected": 2,
                    "lastSuccessUtc": "2026-10-19T19:27:03Z",
                    "facts": {"issuedUtc": "2026-10-19T12:30:00Z"}}},
        product="3-day-forecast")
    out.append(b.build())

    # s18 ------------------------------------------------------------------
    b = Builder("s18-ovation-site-band",
                "OVATION site band for HVO; the equatorial artifact is outside the band",
                ["ovation", "location-relevance", "missing-location", "data-only"],
                sites=[HVO_SITE, NO_LOCATION_SITE],
                notes=["Data only. OVATION acquisition is unresolved (U3) and is not part of the first tranche.",
                       "The body is the committed sector subset, not the full 918,460-byte product.",
                       "A site without a location has no band; its value is null (unknown), never zero."])
    b.fetch("2026-10-19T18:57:04Z", {"status": 200, "contentType": "application/json",
                                     "etag": 'W/"e03bc-65e360ec17552"',
                                     "lastModified": "Mon, 19 Oct 2026 18:55:29 GMT",
                                     "bodyFile": OVATION_SUBSET}, {
        "outcome": "updated",
        "product": {"facts": {"observationTimeUtc": "2026-10-19T18:47:00Z",
                              "forecastTimeUtc": "2026-10-19T20:01:00Z", "leadMinutes": 74,
                              "siteBands": {"hvo": {"latitudes": [36, 45], "longitudes": [236, 256],
                                                    "cells": 210, "expectedCells": 210, "maxValue": 0},
                                            "unlocated": None},
                              "equatorialNonzeroCellsIgnored": 42}}},
        product="ovation")
    out.append(b.build())
    # s19 ------------------------------------------------------------------
    calm = alerts_ok([s930], '"fixture-s19-a"', "Mon, 19 Oct 2026 18:59:30 GMT")
    b = Builder("s19-early-resume-spacing",
                "An early resume lifts a halt only: spacing, backoff, Retry-After and the 429 count stand",
                ["halt", "resume", "rate-limit", "manual-refresh", "backoff"])
    b.fetch("2026-10-19T19:00:00Z", calm, {
        "outcome": "updated", "product": {"nextAttemptNotBeforeUtc": "2026-10-19T19:02:00Z"}})
    b.fetch("2026-10-19T19:00:30Z", {"status": 429, "retryAfter": "600"}, {
        "outcome": "rate-limited",
        "product": {"consecutiveRateLimited": 1, "nextAttemptNotBeforeUtc": "2026-10-19T19:10:30Z"}},
        product="scales")
    b.fetch("2026-10-19T19:02:00Z", {"status": 403}, {
        "outcome": "halted",
        "product": {"halt": {"scope": "provider", "reason": "forbidden", "sinceUtc": "2026-10-19T19:02:00Z"},
                    "nextAttemptNotBeforeUtc": None}})
    b.step("resume", {"products": {
        "alerts": {"halt": None, "nextAttemptNotBeforeUtc": "2026-10-19T19:03:00Z"},
        "scales": {"halt": None, "nextAttemptNotBeforeUtc": "2026-10-19T19:10:30Z", "consecutiveRateLimited": 1}}},
        atUtc="2026-10-19T19:02:05Z", scope="provider", operator="fixture-operator")
    b.fetch("2026-10-19T19:02:10Z", None, {"refused": "min-interval"}, trigger="manual")
    b.fetch("2026-10-19T19:02:10Z", None, {"refused": "backoff"}, product="scales", trigger="manual")
    b.fetch("2026-10-19T19:03:00Z", {"status": 429, "retryAfter": "120"}, {
        "outcome": "rate-limited",
        "product": {"consecutiveRateLimited": 1, "nextAttemptNotBeforeUtc": "2026-10-19T19:05:00Z"}})
    b.fetch("2026-10-19T19:05:00Z", {"status": 429}, {
        "outcome": "rate-limited",
        "product": {"consecutiveRateLimited": 2, "nextAttemptNotBeforeUtc": "2026-10-19T19:35:00Z"}})
    b.fetch("2026-10-19T19:35:00Z", {"status": 429, "retryAfter": "900"}, {
        "outcome": "halted",
        "product": {"halt": {"scope": "provider", "reason": "rate-limited", "sinceUtc": "2026-10-19T19:35:00Z"},
                    "nextAttemptNotBeforeUtc": None, "retryNotBeforeUtc": "2026-10-19T19:50:00Z",
                    "consecutiveRateLimited": 3}})
    b.step("resume", {"products": {
        "alerts": {"halt": None, "nextAttemptNotBeforeUtc": "2026-10-19T19:50:00Z", "retryNotBeforeUtc": None,
                   "consecutiveRateLimited": 3},
        "scales": {"halt": None, "nextAttemptNotBeforeUtc": "2026-10-19T19:35:05Z", "consecutiveRateLimited": 1}}},
        atUtc="2026-10-19T19:35:05Z", scope="provider", operator="fixture-operator")
    b.fetch("2026-10-19T19:40:00Z", None, {"refused": "backoff"}, trigger="manual")
    b.fetch("2026-10-19T19:50:00Z", {"status": 429}, {
        "outcome": "halted",
        "product": {"halt": {"scope": "provider", "reason": "rate-limited", "sinceUtc": "2026-10-19T19:50:00Z"},
                    "retryNotBeforeUtc": "2026-10-19T20:20:00Z", "consecutiveRateLimited": 4}})
    b.step("resume", {"products": {"alerts": {"halt": None, "nextAttemptNotBeforeUtc": "2026-10-19T20:30:00Z",
                                              "retryNotBeforeUtc": None}}},
           atUtc="2026-10-19T20:30:00Z", scope="provider", operator="fixture-operator")
    b.fetch("2026-10-19T20:30:00Z", dict(calm, etag='"fixture-s19-b"'), {
        "request": {"ifNoneMatch": '"fixture-s19-a"'},
        "outcome": "unchanged",
        "product": {"consecutiveRateLimited": 0, "consecutiveTransientFailures": 0, "halt": None,
                    "freshness": "fresh", "nextAttemptNotBeforeUtc": "2026-10-19T20:32:00Z"}})
    out.append(b.build())

    # s20 ------------------------------------------------------------------
    def header_drift(record):
        damaged = dict(record, message=record["message"].replace("Space Weather Message Code: ",
                                                                 "SWPC Message Code: ", 1))
        if damaged["message"] == record["message"]:
            raise ScenarioError("s20 header damage did not change the message")
        return damaged
    drift_a2691 = header_drift(a2691)
    drift_ef3754 = header_drift(ef3754)
    b = Builder("s20-record-level-drift",
                "Rejected records in an accepted body raise record drift once per episode; only a clean body with "
                "a newer stamp closes it; restart keeps the episode open",
                ["record-drift", "format-drift", "restart", "malformed", "stale-snapshot"], synthetic_records=True,
                notes=["The damaged ALTK04 2691 and ALTEF3 3754 records are the real records with their first "
                       "header line renamed; they are synthetic.",
                       "Steps 6 and 7 model an edge alternating between the step 1 and step 2 bodies. Both have "
                       "the stamp 19:38, because a rejected record does not count towards it, so each is "
                       "'updated'. The clean step 1 body is not newer than the episode, so it does not close it, "
                       "and step 7 raises nothing.",
                       "Step 9 is clean and newer, so it closes the episode. Step 10 opens a new one. Step 11 "
                       "serves the step 9 body again at the same stamp and does not close it."])
    first = alerts_ok([w5436], 'W/"fixture-s20-a"', "Fri, 16 Oct 2026 19:44:50 GMT")
    b.fetch("2026-10-16T19:45:00Z", first, {
        "outcome": "updated", "diagnostics": [],
        "attention": [{"group": "WARK04/5436", "reason": "new", "level": "K4"}],
        "product": {"recordDriftOpen": False, "recordDriftStampUtc": None,
                    "facts": {"records": 1, "acceptedRecords": 1}}})
    drifted = alerts_ok([drift_a2691, w5436], 'W/"fixture-s20-b"', "Fri, 16 Oct 2026 19:46:50 GMT")
    b.fetch("2026-10-16T19:47:00Z", drifted, {
        "outcome": "updated", "diagnostics": ["record-rejected:message-header:0"],
        "attention": [{"group": "provider/alerts/record-drift", "reason": "new", "level": None}],
        "notices": {"WARK04/5436": {"state": "active", "versions": 1}},
        "product": {"recordDriftOpen": True, "recordDriftStampUtc": "2026-10-16T19:38:00Z", "freshness": "fresh",
                    "consecutiveRejected": 0, "lkgSha256": sha256(drifted["body"].encode()),
                    "facts": {"records": 2, "acceptedRecords": 1}}})
    b.fetch("2026-10-16T19:49:00Z", dict(drifted, etag='W/"fixture-s20-c"'), {
        "outcome": "unchanged", "diagnostics": [], "attention": [], "product": {"recordDriftOpen": True}})
    b.step("restart")
    b.fetch("2026-10-16T19:51:00Z", dict(drifted, etag='W/"fixture-s20-d"'), {
        "outcome": "unchanged", "diagnostics": [], "attention": [],
        "product": {"recordDriftOpen": True, "recordDriftStampUtc": "2026-10-16T19:38:00Z"}})
    b.fetch("2026-10-16T19:53:00Z", first, {
        "outcome": "updated", "diagnostics": [], "attention": [],
        "product": {"recordDriftOpen": True, "recordDriftStampUtc": "2026-10-16T19:38:00Z",
                    "lkgSha256": sha256(first["body"].encode()), "facts": {"records": 1, "acceptedRecords": 1}}})
    b.fetch("2026-10-16T19:55:00Z", drifted, {
        "outcome": "updated", "diagnostics": ["record-rejected:message-header:0"], "attention": [],
        "product": {"recordDriftOpen": True, "recordDriftStampUtc": "2026-10-16T19:38:00Z",
                    "lkgSha256": sha256(drifted["body"].encode())}})
    b.fetch("2026-10-16T19:57:00Z", alerts_ok([drift_a2691, w5436, drift_ef3754], 'W/"fixture-s20-e"',
                                              "Fri, 16 Oct 2026 19:56:50 GMT"), {
        "outcome": "updated", "attention": [],
        "diagnostics": ["record-rejected:message-header:0", "record-rejected:message-header:2"],
        "notices": {"WARK04/5436": {"state": "active"}},
        "product": {"recordDriftOpen": True, "facts": {"records": 3, "acceptedRecords": 1}}})
    clean = alerts_ok([a2691, w5436], 'W/"fixture-s20-f"', "Fri, 16 Oct 2026 19:58:50 GMT")
    b.fetch("2026-10-16T19:59:00Z", clean, {
        "outcome": "updated", "diagnostics": [],
        "attention": [{"group": "ALTK04/2691", "reason": "new", "level": "K4"}],
        "notices": {"ALTK04/2691": {"state": "active"}, "WARK04/5436": {"state": "active"}},
        "product": {"recordDriftOpen": False, "recordDriftStampUtc": None,
                    "facts": {"records": 2, "acceptedRecords": 2}}})
    b.fetch("2026-10-16T20:01:00Z", alerts_ok([drift_ef3754, a2691, w5436], 'W/"fixture-s20-g"',
                                              "Fri, 16 Oct 2026 20:00:50 GMT"), {
        "outcome": "updated", "diagnostics": ["record-rejected:message-header:2"],
        "attention": [{"group": "provider/alerts/record-drift", "reason": "new", "level": None}],
        "notices": {"ALTK04/2691": {"state": "active", "versions": 1},
                    "WARK04/5436": {"state": "active", "versions": 1}},
        "product": {"recordDriftOpen": True, "recordDriftStampUtc": "2026-10-16T19:39:00Z",
                    "facts": {"records": 3, "acceptedRecords": 2}}})
    b.fetch("2026-10-16T20:03:00Z", clean, {
        "outcome": "updated", "diagnostics": [], "attention": [],
        "product": {"recordDriftOpen": True, "recordDriftStampUtc": "2026-10-16T19:39:00Z"}})
    out.append(b.build())

    # s21 ------------------------------------------------------------------
    s259 = cap.rec("WARSUD/259/2026-10-10T14:45:00Z")
    k219 = cap.rec("ALTK07/219/2026-10-10T17:58:00Z")
    k727 = cap.rec("ALTK06/727/2026-10-10T17:18:00Z")
    long_warning = synthetic("PX1W", "2026-10-10 18:55:12.000", "WARPX1", 200, [
        "WARNING: Proton 10MeV Integral Flux above 10pfu expected ",
        "Valid From: 2026 Oct 10 1900 UTC",
        "Valid To: 2026 Oct 15 1900 UTC",
        "Warning Condition: Onset",
        "NOAA Scale: S1 - Minor",
        "Comment: Synthetic fixture with a validity longer than the active cap; not an SWPC product.",
    ])
    b = Builder("s21-pending-short-window-and-cap",
                "A warning is pending before Valid From; an alert first seen just after its end alerts only within "
                "the grace window; the active cap ends a long validity",
                ["pending", "expired", "new-grace", "capped", "first-run-backlog"], synthetic_records=True,
                notes=["WARPX1 serial 200 is synthetic. Its 5-day validity exceeds the 96-hour active cap.",
                       "ALTK07 219 ended at 18:00 and was issued at 17:58, so at 18:00:30 it is inside the 300-second "
                       "grace window. ALTK06 727 ended at the same time but was issued at 17:18, so it never "
                       "alerts."])
    sud = {"family": "warning", "level": None, "relevance": {hvo: "NoLocalObservingImpact"},
           "fromUtc": "2026-10-10T15:10:00Z", "untilUtc": "2026-10-10T15:50:00Z"}
    b.fetch("2026-10-10T14:46:00Z", alerts_ok([s259], 'W/"fixture-s21-a"', "Sat, 10 Oct 2026 14:45:50 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "WARSUD/259", "reason": "new", "level": None}],
        "notices": {"WARSUD/259": dict(sud, state="pending", mayBeOutOfDate=False)}})
    b.step("evaluate", {"notices": {"WARSUD/259": {"state": "active", "mayBeOutOfDate": True}},
                        "products": {"alerts": {"freshness": "stale"}}}, atUtc="2026-10-10T15:10:00Z")
    b.step("evaluate", {"notices": {"WARSUD/259": {"state": "recent", "endReason": "expired",
                                                   "mayBeOutOfDate": False}}},
           atUtc="2026-10-10T15:50:00Z")
    b.fetch("2026-10-10T18:00:30Z", alerts_ok([k219, k727, s259], 'W/"fixture-s21-b"',
                                              "Sat, 10 Oct 2026 18:00:20 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "ALTK07/219", "reason": "new", "level": "G3"}],
        "notices": {"ALTK06/727": {"state": "recent", "endReason": "expired", "level": "G2",
                                   "untilUtc": "2026-10-10T18:00:00Z", "relevance": {hvo: "NotExpected"}},
                    "ALTK07/219": {"state": "recent", "endReason": "expired", "level": "G3",
                                   "fromUtc": "2026-10-10T17:58:00Z", "untilUtc": "2026-10-10T18:00:00Z",
                                   "relevance": {hvo: "Unlikely"}},
                    "WARSUD/259": {"state": "recent"}}})
    b.fetch("2026-10-10T18:56:00Z", alerts_ok([long_warning, k219, k727, s259], 'W/"fixture-s21-c"',
                                              "Sat, 10 Oct 2026 18:55:50 GMT"), {
        "outcome": "updated",
        "attention": [{"group": "WARPX1/200", "reason": "new", "level": "S1"}],
        "notices": {"ALTK06/727": {"state": "recent"}, "ALTK07/219": {"state": "recent"},
                    "WARPX1/200": {"family": "warning", "level": "S1", "state": "pending",
                                   "fromUtc": "2026-10-10T19:00:00Z", "untilUtc": "2026-10-14T18:55:00Z",
                                   "relevance": {hvo: "NoLocalObservingImpact"}},
                    "WARSUD/259": {"state": "recent"}}})
    b.step("evaluate", {"notices": {"ALTK06/727": {"state": "history"}, "ALTK07/219": {"state": "history"},
                                    "WARPX1/200": {"state": "active"}, "WARSUD/259": {"state": "history"}}},
           atUtc="2026-10-14T18:54:59Z")
    b.step("evaluate", {"notices": {"ALTK06/727": {"state": "history"}, "ALTK07/219": {"state": "history"},
                                    "WARPX1/200": {"state": "recent", "endReason": "capped",
                                                   "untilUtc": "2026-10-14T18:55:00Z"},
                                    "WARSUD/259": {"state": "history"}}},
           atUtc="2026-10-14T18:55:00Z")
    out.append(b.build())

    # s22 ------------------------------------------------------------------
    quiet = alerts_ok([s930], '"fixture-s22-a"', "Mon, 19 Oct 2026 18:59:30 GMT")
    b = Builder("s22-gone-client-error-network",
                "410 and 400 halt one product; a network error backs off; a 304 without validators is transient; "
                "an attempt ends the backoff before it",
                ["outage", "halt", "resume", "backoff", "not-modified", "manual-refresh"],
                notes=["Steps 11 to 15 follow a transient failure with a 404. The 404 attempt ends the backoff "
                       "that the 503 set, so after the resume a manual request is refused as min-interval, not "
                       "backoff."])
    b.fetch("2026-10-19T19:00:00Z", quiet, {"outcome": "updated"})
    b.fetch("2026-10-19T19:02:00Z", {"outcome": "network-error"}, {
        "outcome": "transient-failure",
        "product": {"consecutiveTransientFailures": 1, "nextAttemptNotBeforeUtc": "2026-10-19T19:06:00Z"}})
    b.fetch("2026-10-19T19:02:30Z", {"status": 410}, {
        "outcome": "halted",
        "product": {"halt": {"scope": "product", "reason": "gone", "sinceUtc": "2026-10-19T19:02:30Z"}}},
        product="scales")
    b.fetch("2026-10-19T19:03:00Z", {"status": 400}, {
        "outcome": "halted",
        "product": {"halt": {"scope": "product", "reason": "client-error", "sinceUtc": "2026-10-19T19:03:00Z"}}},
        product="kp-forecast")
    b.step("resume", {"products": {"scales": {"halt": None, "nextAttemptNotBeforeUtc": "2026-10-19T19:03:30Z"},
                                   "kp-forecast": {"halt": {"scope": "product", "reason": "client-error"}},
                                   "alerts": {"halt": None}}},
           atUtc="2026-10-19T19:03:10Z", scope="product", product="scales", operator="fixture-operator")
    b.fetch("2026-10-19T19:03:15Z", None, {"refused": "min-interval"}, product="scales", trigger="manual")
    b.fetch("2026-10-19T19:03:20Z", {"status": 304}, {
        "request": {"ifNoneMatch": None, "ifModifiedSince": None},
        "outcome": "transient-failure",
        "product": {"consecutiveTransientFailures": 1, "nextAttemptNotBeforeUtc": "2026-10-19T19:33:20Z",
                    "lkgSha256": None}},
        product="3-day-forecast")
    b.fetch("2026-10-19T19:03:30Z", {"status": 200, "contentType": "application/json", "etag": None,
                                     "lastModified": None, "bodyFile": CAPTURE + "/noaa-scales.json"}, {
        "outcome": "updated", "product": {"halt": None}}, product="scales")
    b.fetch("2026-10-19T19:04:00Z", None, {"refused": "halted"}, product="kp-forecast", trigger="manual")
    b.fetch("2026-10-19T19:06:00Z", dict(quiet, etag='"fixture-s22-b"'), {
        "request": {"ifNoneMatch": '"fixture-s22-a"'},
        "outcome": "unchanged",
        "product": {"consecutiveTransientFailures": 0, "freshness": "fresh"},
        "products": {"kp-forecast": {"halt": {"scope": "product", "reason": "client-error",
                                              "sinceUtc": "2026-10-19T19:03:00Z"}},
                     "scales": {"halt": None}}})
    b.fetch("2026-10-19T19:08:00Z", {"status": 503}, {
        "outcome": "transient-failure",
        "product": {"consecutiveTransientFailures": 1, "nextAttemptNotBeforeUtc": "2026-10-19T19:12:00Z"}})
    b.fetch("2026-10-19T19:12:00Z", {"status": 404}, {
        "outcome": "halted",
        "product": {"halt": {"scope": "product", "reason": "not-found", "sinceUtc": "2026-10-19T19:12:00Z"},
                    "consecutiveTransientFailures": 1, "nextAttemptNotBeforeUtc": None}})
    b.step("resume", {"products": {"alerts": {"halt": None, "nextAttemptNotBeforeUtc": "2026-10-19T19:13:00Z",
                                              "consecutiveTransientFailures": 1}}},
           atUtc="2026-10-19T19:12:05Z", scope="product", product="alerts", operator="fixture-operator")
    b.fetch("2026-10-19T19:12:10Z", None, {"refused": "min-interval"}, trigger="manual")
    b.fetch("2026-10-19T19:13:00Z", dict(quiet, etag='"fixture-s22-c"'), {
        "outcome": "unchanged",
        "product": {"consecutiveTransientFailures": 0, "halt": None,
                    "nextAttemptNotBeforeUtc": "2026-10-19T19:15:00Z"}})
    out.append(b.build())

    return out


# ---------------------------------------------------------------------------
# Files, manifest and commands
# ---------------------------------------------------------------------------

CAPTURE_FACTS = [
    {"product": "alerts", "file": CAPTURE + "/alerts.json", "fetchStartUtc": "2026-10-19T18:57:03.332Z",
     "http": {"status": 200, "contentType": "application/json", "contentEncoding": "gzip",
              "lastModified": "Mon, 19 Oct 2026 18:56:08 GMT", "etag": 'W/"d330-65e36111702aa"',
              "cacheControl": "max-age=60", "transferBytes": 5174},
     "decodedBytes": 54064, "sha256": "3A6DA456C1846F5D07E002514C57E997B2E3D456AC5AA636D0E7A6FBFC12114E",
     "transform": "none"},
    {"product": "scales", "file": CAPTURE + "/noaa-scales.json", "fetchStartUtc": "2026-10-19T18:57:03.430Z",
     "http": {"status": 200, "contentType": "application/json", "contentEncoding": "gzip",
              "lastModified": "Mon, 19 Oct 2026 18:55:44 GMT", "etag": 'W/"44e-65e360fa970f3"',
              "cacheControl": "max-age=60", "transferBytes": 216},
     "decodedBytes": 1102, "sha256": "D1CB0BD994FF2E0020DC44BA4F10F54E7A409D2C65DC49EAA2249A21C7FC8941",
     "transform": "none"},
    {"product": "kp-forecast", "file": CAPTURE + "/noaa-planetary-k-index-forecast.json",
     "fetchStartUtc": "2026-10-19T18:57:03.632Z",
     "http": {"status": 200, "contentType": "application/json", "contentEncoding": "gzip",
              "lastModified": "Mon, 19 Oct 2026 18:56:43 GMT", "etag": 'W/"1af9-65e361333c7ac"',
              "cacheControl": "max-age=60", "transferBytes": 509},
     "decodedBytes": 6905, "sha256": "12B8831BA5C88707FF6694C61D451C0714CC54EEB33B4B5127467443BFAACF31",
     "transform": "none"},
    {"product": "3-day-forecast", "file": THREE_DAY_ORIGINAL, "fetchStartUtc": "2026-10-19T18:57:03.895Z",
     "http": {"status": 200, "contentType": "text/plain", "contentEncoding": "gzip",
              "lastModified": "Mon, 19 Oct 2026 18:55:42 GMT", "etag": '"7fe-65e360f9121ca-gzip"',
              "cacheControl": "max-age=60", "transferBytes": 838},
     "decodedBytes": 2046, "sha256": "D8EC76FB6813BCF92DB38E70057689334DA5CA8DF8BABA5FA320F8591A7FAB3E",
     "transform": "json-string-wrapper; the decoded text is byte-exact",
     "normalizedFile": THREE_DAY_NORMALIZED},
    {"product": "ovation", "file": OVATION_SUBSET, "fetchStartUtc": "2026-10-19T18:57:04.464Z",
     "http": {"status": 200, "contentType": "application/json", "contentEncoding": "gzip",
              "lastModified": "Mon, 19 Oct 2026 18:55:29 GMT", "etag": 'W/"e03bc-65e360ec17552"',
              "cacheControl": "max-age=60", "transferBytes": 150748},
     "decodedBytes": OVATION_ORIGINAL_BYTES, "sha256": OVATION_ORIGINAL_SHA256,
     "transform": "sector subset: longitudes 236..256, latitudes -5..90; original not committed"},
]


def ovation_subset_bytes(original: Path) -> bytes:
    data = original.read_bytes()
    if len(data) != OVATION_ORIGINAL_BYTES or sha256(data) != OVATION_ORIGINAL_SHA256:
        raise ScenarioError("OVATION original does not match the recorded size and hash")
    obj = json.loads(data)
    s = OVATION_SECTOR
    cells = [c for c in obj["coordinates"]
             if s["lonMin"] <= c[0] <= s["lonMax"] and s["latMin"] <= c[1] <= s["latMax"]]
    lines = ["{",
             f'  "schemaVersion": "{OVATION_SUBSET_SCHEMA}",',
             f'  "originalSha256": "{OVATION_ORIGINAL_SHA256}",',
             f'  "originalBytes": {OVATION_ORIGINAL_BYTES},',
             f'  "sector": {json.dumps(s)},',
             f'  "Observation Time": {json.dumps(obj["Observation Time"])},',
             f'  "Forecast Time": {json.dumps(obj["Forecast Time"])},',
             f'  "Data Format": {json.dumps(obj["Data Format"])},',
             f'  "type": {json.dumps(obj["type"])},',
             '  "coordinates": [']
    lines += [f"    {json.dumps(c)}" + ("," if i < len(cells) - 1 else "") for i, c in enumerate(cells)]
    lines += ["  ]", "}"]
    return ("\n".join(lines) + "\n").encode("ascii")


def scenario_files(cap):
    return {f"scenarios/{doc['scenarioId']}.json": dump_json(doc) for doc in scenarios(cap)}


def manifest(cap, files):
    entries = []
    for rel in sorted(files):
        data = files[rel]
        entries.append({"path": rel, "bytes": len(data), "sha256": sha256(data)})
    return {
        "schemaVersion": MANIFEST_SCHEMA,
        "fixtureId": "swpc-v1",
        "issue": 1398,
        "source": {
            "provider": "NOAA Space Weather Prediction Center (SWPC)",
            "host": "services.swpc.noaa.gov",
            "rights": "U.S. Government work; not subject to copyright in the United States (17 U.S.C. 105). "
                      "See SOURCE.md for the NWS disclaimer terms that apply to use and attribution.",
            "credit": "Space weather data: NOAA Space Weather Prediction Center (SWPC).",
        },
        "retrievedBy": RETRIEVED_BY,
        "captures": CAPTURE_FACTS,
        "derived": {
            "scenarioSchema": SCENARIO_SCHEMA,
            "scenarios": sorted(f for f in files if f.startswith("scenarios/")),
            "generator": "fixtures.py",
            "normalization": "CRLF to LF, trailing spaces/tabs trimmed per line, trailing blank lines "
                             "removed, every line LF-terminated",
        },
        "files": entries,
    }


def collect_files(cap, ovation_bytes):
    files = {}
    for rel in ("SOURCE.md", "fixtures.py", CAPTURE + "/alerts.json", CAPTURE + "/noaa-scales.json",
                CAPTURE + "/noaa-planetary-k-index-forecast.json", THREE_DAY_ORIGINAL):
        files[rel] = (PACK / rel).read_bytes()
    original = json.loads(files[THREE_DAY_ORIGINAL])
    files[THREE_DAY_NORMALIZED] = normalize_text(original["text"]).encode("utf-8")
    files[OVATION_SUBSET] = ovation_bytes
    files.update(scenario_files(cap))
    return files


def check_captures(files):
    problems = []
    for fact in CAPTURE_FACTS:
        data = files[fact["file"]]
        if fact["product"] == "3-day-forecast":
            wrapper = json.loads(data)
            raw = wrapper["text"].encode("utf-8")
            if wrapper.get("schemaVersion") != TEXT_CAPTURE_SCHEMA:
                problems.append("3-day wrapper schema")
        elif fact["product"] == "ovation":
            subset = json.loads(data)
            if subset["originalSha256"] != fact["sha256"] or subset["originalBytes"] != fact["decodedBytes"]:
                problems.append("OVATION subset provenance mismatch")
            continue
        else:
            raw = data
        if len(raw) != fact["decodedBytes"] or sha256(raw) != fact["sha256"]:
            problems.append(f"{fact['file']}: capture bytes or hash differ from the recorded fetch")
    alerts = files[CAPTURE + "/alerts.json"]
    if encode_alerts(json.loads(alerts)).encode("ascii") != alerts:
        problems.append("alerts encoder does not reproduce the captured bytes")
    return problems


def command_check():
    problems = []
    cap = Capture()
    manifest_path = PACK / "manifest.json"
    recorded = json.loads(manifest_path.read_text("ascii"))
    on_disk = {p.relative_to(PACK).as_posix() for p in PACK.rglob("*") if p.is_file()}
    on_disk -= {"manifest.json"}
    on_disk = {p for p in on_disk if "__pycache__" not in p.split("/")}
    listed = {e["path"] for e in recorded["files"]}
    if on_disk != listed:
        problems.append(f"unlisted or missing files: {sorted(on_disk ^ listed)}")
    files = collect_files(cap, (PACK / OVATION_SUBSET).read_bytes())
    for entry in recorded["files"]:
        path = PACK / entry["path"]
        if not path.is_file():
            continue
        data = path.read_bytes()
        if len(data) != entry["bytes"] or sha256(data) != entry["sha256"]:
            problems.append(f"{entry['path']}: hash or size differs from manifest")
        if entry["path"] in files and files[entry["path"]] != data:
            problems.append(f"{entry['path']}: regenerated bytes differ")
    if dump_json(manifest(cap, files)) != manifest_path.read_bytes():
        problems.append("manifest.json differs from its regenerated form")
    problems.extend(check_captures(files))
    for rel, data in sorted(files.items()):
        if rel.startswith("scenarios/"):
            problems.extend(replay(json.loads(data)))
    for line in problems:
        print("FAIL", line)
    count = len([f for f in files if f.startswith("scenarios/")])
    print(f"{'FAILED' if problems else 'OK'}: {len(files)} files, {count} scenarios replayed, "
          f"{len(problems)} problems")
    return 1 if problems else 0


def command_write(ovation_original):
    cap = Capture()
    if ovation_original:
        ovation_bytes = ovation_subset_bytes(Path(ovation_original))
        (PACK / OVATION_SUBSET).write_bytes(ovation_bytes)
    else:
        ovation_bytes = (PACK / OVATION_SUBSET).read_bytes()
    files = collect_files(cap, ovation_bytes)
    problems = check_captures(files)
    for rel, data in sorted(files.items()):
        if rel.startswith("scenarios/"):
            problems.extend(replay(json.loads(data)))
    if problems:
        for line in problems:
            print("FAIL", line)
        return 1
    for rel, data in files.items():
        target = PACK / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        if not target.exists() or target.read_bytes() != data:
            target.write_bytes(data)
    stale = {p for p in (PACK / "scenarios").glob("*.json")} - {PACK / f for f in files}
    for path in stale:
        path.unlink()
    (PACK / "manifest.json").write_bytes(dump_json(manifest(cap, files)))
    print(f"wrote {len(files)} files and manifest.json")
    return 0


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("check")
    write = sub.add_parser("write")
    write.add_argument("--ovation-original")
    args = parser.parse_args(argv)
    try:
        return command_check() if args.command == "check" else command_write(args.ovation_original)
    except ScenarioError as error:
        print("FAIL", error)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
