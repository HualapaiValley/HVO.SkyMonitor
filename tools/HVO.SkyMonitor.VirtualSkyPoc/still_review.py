#!/usr/bin/env python3
"""Build an exact-allowlist review export from verified #993 qualification evidence.

Sky pixels come exclusively from retained production products and source previews.
Shared C# meridian-strip proof supplies coordinates and byte equality; this script
only encodes lossless PNGs and adds scientific labels in SVG/HTML.
"""
import argparse
import base64
import csv
from datetime import datetime, timedelta, timezone
import hashlib
import html
import json
from pathlib import Path
import struct
import zlib


def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def checked(path, expected):
    data = path.read_bytes()
    if digest(data) != expected.upper():
        raise ValueError(f"Checksum mismatch: {path.name}")
    return data


def png(path, width, height, pixels):
    if len(pixels) != width * height * 3:
        raise ValueError("RGB byte count differs from declared geometry")

    def chunk(kind, payload):
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)

    rows = b"".join(b"\0" + pixels[row * width * 3:(row + 1) * width * 3] for row in range(height))
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
                    + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


def utc(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def local(value):
    return utc(value).astimezone(timezone(timedelta(hours=-7)))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("evidence_root", type=Path)
    parser.add_argument("public_root", type=Path)
    args = parser.parse_args()
    root = args.evidence_root.resolve(strict=True)
    public = args.public_root
    public.mkdir(parents=True, exist_ok=False)
    evidence = json.loads((root / "evidence.json").read_text())
    if evidence["sourceStrideMinutes"] != 1:
        raise ValueError("Sparse smoke evidence cannot become a full-cadence review page")
    proof_root = root / "strip-proof"
    proof = json.loads((proof_root / "evidence.json").read_text())
    if not proof["pixelEquality"] or proof["stripSha256"] != proof["columnSha256"]:
        raise ValueError("Shared source strip does not equal the final column")
    source = checked(proof_root / "source.rgb", proof["sourceSha256"])
    strip = checked(proof_root / "strip.rgb", proof["stripSha256"])
    column = checked(proof_root / "column.rgb", proof["columnSha256"])
    layout = proof["sourceLayout"]
    if layout["strideBytes"] != layout["width"] * 3:
        raise ValueError("Review export requires the declared packed RGB source")
    png(public / "sampled-source.png", layout["width"], layout["height"], source)
    png(public / "sampled-strip.png", 1, proof["rows"], strip)
    png(public / "sampled-column.png", 1, proof["rows"], column)
    (public / "strip-evidence.json").write_text(json.dumps(proof, indent=2) + "\n")

    products = {}
    for item in evidence["products"]:
        kind = item["kind"]
        final = item["final"]
        summary = final["summary"]
        date = summary["observingDate"]
        stem = root / "runtime" / "nightly-products" / date[:4] / date[5:7] / date[8:10] / kind.lower().replace("startrail", "star-trail") / summary["productId"].replace("-", "")
        packed = checked(stem.with_suffix(".bin"), final["payloadSha256"])
        rendition = checked(stem.with_suffix(".jpg"), final["renditionSha256"])
        provenance = checked(stem.with_suffix(".provenance.json"), final["provenanceSha256"])
        name = "keogram" if kind == "Keogram" else "star-trail"
        png(public / f"{name}.png", summary["width"], summary["height"], packed)
        (public / f"{name}.jpg").write_bytes(rendition)
        (public / f"{name}.provenance.json").write_bytes(provenance)
        products[kind] = item

    start = utc(evidence["period"]["startUtc"])
    end = utc(evidence["period"]["endUtc"])
    keogram = products["Keogram"]["final"]["summary"]
    width, height = keogram["width"], keogram["height"]
    svg_width, svg_height = width + 160, height + 120
    plot = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{svg_width}" height="{svg_height}" viewBox="0 0 {svg_width} {svg_height}">',
            '<rect width="100%" height="100%" fill="#101923"/>',
            '<g fill="#e4edf6" font-family="sans-serif" font-size="16">',
            '<text x="90" y="26">Sunrise October 12 → sunrise October 13 · America/Phoenix (MST, UTC−7)</text>',
            f'<image href="keogram.png" x="90" y="50" width="{width}" height="{height}"/>']
    for label, row in (("North", 0), ("Zenith", height // 2), ("South", height - 1)):
        plot.append(f'<text x="10" y="{row + 56}">{label}</text>')
    ticks = [start + timedelta(hours=hour) for hour in range(0, 24, 3)] + [end]
    for instant in ticks:
        x = 90 + min(width, (instant - start).total_seconds() / 60)
        label = instant.astimezone(timezone(timedelta(hours=-7))).strftime("%d Oct %H:%M")
        anchor = "end" if instant == end else "middle"
        plot.append(f'<line x1="{x:.3f}" y1="{height + 50}" x2="{x:.3f}" y2="{height + 58}" stroke="#e4edf6"/>')
        plot.append(f'<text x="{x:.3f}" y="{height + 82}" text-anchor="{anchor}" font-size="14">{label}</text>')
    plot.append(f'<rect x="{90 + proof["column"]}" y="50" width="1" height="{height}" fill="none" stroke="#2fe2df"/>')
    plot.append('</g></svg>')
    plot_inline = "\n".join(plot)
    plot_standalone = plot_inline.replace('href="keogram.png"', 'href="data:image/png;base64,'
        + base64.b64encode((public / "keogram.png").read_bytes()).decode("ascii") + '"')
    (public / "keogram-labelled.svg").write_text(plot_standalone)

    points = " ".join(f'{point["pixel"]["x"]:.6f},{point["pixel"]["y"]:.6f}' for point in proof["geometry"] if point["pixel"] is not None)
    source_inline = f'''<svg xmlns="http://www.w3.org/2000/svg" class="proof" width="790" height="710" viewBox="0 0 790 710">
<rect width="100%" height="100%" fill="#101923"/>
<image href="sampled-source.png" x="0" y="40" width="640" height="640"/>
<polyline points="{points}" transform="translate(0 40)" fill="none" stroke="#2fe2df" stroke-width="2"/>
<image href="sampled-strip.png" x="663" y="46.4" width="24" height="627.2" style="image-rendering:pixelated"/>
<image href="sampled-column.png" x="729" y="46.4" width="24" height="627.2" style="image-rendering:pixelated"/>
<g fill="#e4edf6" font-family="sans-serif" font-size="14">
<text x="10" y="24">Actual fixed preview · cyan = shared sampled meridian</text>
<text x="655" y="24">Strip</text><text x="708" y="24">Column {proof["column"]}</text>
<text x="328" y="64">N</text><text x="328" y="368">Z</text><text x="328" y="665">S</text>
<text x="10" y="703">{height} RGB rows match byte for byte. Columns enlarged for display.</text></g></svg>'''
    source_standalone = source_inline
    for name in ("sampled-source.png", "sampled-strip.png", "sampled-column.png"):
        source_standalone = source_standalone.replace(f'href="{name}"', 'href="data:image/png;base64,'
            + base64.b64encode((public / name).read_bytes()).decode("ascii") + '"')
    (public / "source-strip.svg").write_text(source_standalone)

    by_capture = {item["captureId"]: item for item in evidence["sources"]}
    cards = []
    for sample in evidence["samples"]:
        data = checked(root / sample["name"], sample["sha256"])
        (public / sample["name"]).write_bytes(data)
        facts = by_capture[sample["candidate"]["captureId"]]
        statistics = facts["statistics"]
        cards.append(f'''<figure><img src="{html.escape(sample["name"])}" alt="Actual {html.escape(sample["label"])} fixed preview">
<figcaption><strong>{html.escape(sample["label"].replace('-', ' '))}</strong> · {local(facts["utc"]):%d Oct %H:%M:%S} MST<br>
Sun {facts["altitude"]:.2f}° · exposure {html.escape(facts["setpoint"]["exposure"])} · gain {facts["setpoint"]["gain"]}<br>
Raw ADU mean {statistics["mean"]:.1f} · median {statistics["p50"]} · p99 {statistics["p99"]}<br>
Native saturation {statistics["saturationFraction"] * 100:.3f}% · active samples {statistics["activeSamples"]:,}</figcaption></figure>''')

    with (public / "actual-source-times.csv").open("w", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["slot", "exposure_start_utc", "capture_id", "sun_altitude_degrees", "exposure", "gain", "raw_sha256", "mean_adu", "p50_adu", "p99_adu", "saturation_fraction"])
        for item in evidence["sources"]:
            writer.writerow([item["slot"], item["utc"], item["captureId"], item["altitude"], item["setpoint"]["exposure"], item["setpoint"]["gain"], item["rawSha256"], item["statistics"]["mean"], item["statistics"]["p50"], item["statistics"]["p99"], item["statistics"]["saturationFraction"]])

    exported = {key: evidence[key] for key in ("schema", "period", "sourceHead", "configuration", "options", "catalog", "products", "scheduledGenerationMilliseconds", "scheduledGenerationCpuSeconds", "scheduledGenerationAllocatedBytes", "sourceStrideMinutes", "elapsedSeconds", "cpuSeconds", "allocatedBytes", "peakWorkingSet64", "workingSet64", "assemblies", "exposurePolicy", "gapPolicy")}
    exported["sourceCount"] = len(evidence["sources"])
    exported["qualificationEvidenceSha256"] = digest((root / "evidence.json").read_bytes())
    exported["displayExport"] = "Fixed-transfer production bytes; lossless PNG container and SVG labels only. No per-frame stretch."
    (public / "evidence-index.json").write_text(json.dumps(exported, indent=2) + "\n")
    trail_count = products["StarTrail"]["actualLeafCount"]
    (public / "index.html").write_text(f'''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>#993 actual full-day still products</title><style>
body{{background:#09121b;color:#dce8f2;font:16px/1.5 system-ui;margin:0 auto;padding:28px;max-width:1700px}}a{{color:#67dcd7}}
h1{{font-size:28px}}h2{{font-size:22px;margin-top:36px}}.cards{{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:20px}}
figure{{margin:0;padding:14px;background:#101923;border:1px solid #263b49;border-radius:8px}}figure img{{width:100%;height:auto}}figcaption{{font-size:14px;margin-top:10px}}
.scroll{{overflow-x:auto;border:1px solid #263b49;padding:10px}}.trail{{max-width:900px}}.proof{{max-width:790px;width:100%;height:auto}}code{{color:#b7dce6}}small{{color:#b4c4cf}}
</style><main><h1>#993 · actual full-day still products</h1>
<p><strong>Sunrise on October 12 through sunrise on October 13.</strong> {local(evidence["period"]["startUtc"]):%d Oct %H:%M:%S} → {local(evidence["period"]["endUtc"]):%d Oct %H:%M:%S} MST, resolved from the configured site through shared Astronomy.</p>
<p>{len(evidence["sources"]):,} actual one-minute VirtualSky captures → durable raw ingress → fixed Preview lane → durable scheduled keogram and star-trail producers. Verified production catalog: {evidence["catalog"]["rowCount"]:,} rows. Production-path samples; local qualification and independent review are tracked on <a href="https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1128">PR #1128</a>.</p>
<p>Every image uses the same absolute display transfer (black 64, white 4095 native ADU, gamma 2.2). The raw measurements below are independent of the display. The bounded exposure recipe is recorded in the evidence.</p>
<h2>Representative actual sources</h2><div class="cards">{''.join(cards)}</div>
<h2>Full planned keogram</h2><p>Time runs left to right; north → zenith → south runs top to bottom. One column is one UTC minute; the earliest actual source supplies that bin. Grey pattern marks missing coverage, including ten-minute leading/trailing omissions and the deliberate 30-minute gap at 16:39–17:09 MST. Cyan identifies the source column shown below.</p>
<div class="scroll" aria-label="Full sunrise-to-sunrise horizontal keogram with MST labels and north zenith south rows">{plot_inline}</div>
<p><a href="keogram-labelled.svg">Standalone labelled figure</a> · <a href="keogram.png">Lossless full-size keogram</a> · <a href="keogram.jpg">Published JPEG</a> · <a href="keogram.provenance.json">Published provenance</a></p>
<h2>Source strip and corresponding column</h2><p>Actual source at {local(proof["candidate"]["exposureStartedUtc"]):%d Oct %H:%M:%S} MST. Shared C# sampling independently reproduces all {proof["rows"]} RGB rows of retained keogram column {proof["column"]}; both checksums match.</p>
{source_inline}
<p><a href="source-strip.svg">Standalone strip figure</a> · <a href="strip-evidence.json">Exact strip coordinates and pixel proof</a></p>
<h2>Actual dark-night star trail</h2><p>Lighten composite of {trail_count:,} actual eligible frames. Each source has geometric Sun altitude ≤−18° at its own exposure time. Retained lineage preserves every contributing source and intermediate product.</p>
<img class="trail" src="star-trail.png" width="640" height="640" alt="Star trail composed from actual dark-night VirtualSky exposures">
<p><a href="star-trail.png">Lossless full-size trail</a> · <a href="star-trail.jpg">Published JPEG</a> · <a href="star-trail.provenance.json">Published provenance</a></p>
<h2>Evidence index</h2><p><a href="evidence-index.json">Period, identities, catalog, lineage, checksums and measured resources</a> · <a href="actual-source-times.csv">Every actual capture time, exposure, raw statistics and checksum</a> · <a href="checksums.json">Review-file checksums</a></p>
<p>Scheduled generation took {evidence["scheduledGenerationMilliseconds"] / 1000:.3f} seconds and {evidence["scheduledGenerationCpuSeconds"]:.3f} process CPU seconds in the declared capture-harness build. Release W1/W2 resource measurements and candidate gates are reported separately on the PR.</p>
<small>This exact-allowlist export contains review artifacts only. Private runtime state stays in the retained evidence root. The <a href="http://192.168.2.45:8095/">accepted full-day POC</a> remains available for comparison.</small></main></html>''')
    files = sorted(path.name for path in public.iterdir() if path.is_file())
    (public / "checksums.json").write_text(json.dumps({name: digest((public / name).read_bytes()) for name in files}, indent=2) + "\n")
    (public / "allowlist.json").write_text(json.dumps(files + ["checksums.json"], indent=2) + "\n")
    print(json.dumps({"publicRoot": str(public.resolve()), "files": len(files) + 1, "sourceCount": len(evidence["sources"]), "stripMatches": True}))


if __name__ == "__main__":
    main()
