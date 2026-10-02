#!/usr/bin/env python3
"""Build scientific review figures and explicitly mapped videos from recorded POC sources."""
import argparse
import csv
from datetime import datetime, timedelta, timezone
import hashlib
import html
import json
from pathlib import Path
import shutil
import subprocess
import time

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from PIL import Image, ImageDraw, ImageFont


def stamp(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("day", type=Path)
    parser.add_argument("samples", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("encoder", type=Path)
    parser.add_argument("--details", type=Path)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Use a new output directory; retained review evidence is immutable.")
    args.output.mkdir(parents=True)
    started = time.monotonic()
    scenario = json.loads((args.day / "scenario.json").read_text())
    coverage = json.loads((args.day / "coverage.json").read_text())
    rows = json.loads((args.day / "sequence.json").read_text())
    samples = json.loads((args.samples / "samples.json").read_text())
    start, end = stamp(scenario["start"]), stamp(scenario["end"])
    zone = timezone(timedelta(hours=-7))
    duration_minutes = (end - start).total_seconds() / 60
    raw = np.array(Image.open(args.day / "keogram-pixels.png"))
    if raw.shape[:2] != (361, len(rows)):
        raise AssertionError("Keogram dimensions do not cover all planned slots.")
    plt.rcParams.update({"text.color": "#dce5ed", "axes.labelcolor": "#dce5ed",
                         "xtick.color": "#b5c4d2", "ytick.color": "#b5c4d2",
                         "axes.edgecolor": "#536271", "font.size": 11})
    tick_times = []
    tick = start.astimezone(zone).replace(minute=0, second=0, microsecond=0) + timedelta(hours=1)
    while tick < end:
        tick_times.append(tick)
        tick += timedelta(hours=2)
    ticks = [(t - start).total_seconds() / 60 for t in tick_times]
    labels = [t.strftime("%H:%M\n%b %d") for t in tick_times]
    fig, ax = plt.subplots(figsize=(17, 5), facecolor="#10171e")
    ax.imshow(raw, extent=(0, len(rows), 180, 0), aspect="auto", interpolation="nearest")
    ax.set_xlim(0, duration_minutes)
    ax.set_yticks([0, 45, 90, 135, 180], ["N · horizon", "N · 45°", "Zenith", "S · 45°", "S · horizon"])
    ax.set_xticks(ticks, labels)
    ax.set_xlabel("Site time · America/Phoenix · MST (UTC−7)")
    ax.set_title("October 12 · sunrise → following sunrise\nActual VirtualSky CFA sources · fixed gamma 2.2 · one projected N–Z–S strip per minute", pad=18)
    for key, text in [("leadingEnd", "15 min unscheduled"), ("outageStart", "30 min outage"), ("trailingStart", "15 min missing")]:
        x = (stamp(coverage[key]) - start).total_seconds() / 60
        ax.annotate(text, xy=(x, 5), xytext=(x, -26), ha="center", fontsize=9,
                    arrowprops={"arrowstyle": "-", "color": "#f4cb76"}, color="#f4cb76")
    fig.text(.5, .015, f"Planned period {start.astimezone(zone):%b %d %H:%M:%S} – {end.astimezone(zone):%b %d %H:%M:%S} MST · hatched gray = no source pixels", ha="center")
    fig.tight_layout(rect=[0, .06, 1, .90])
    fig.savefig(args.output / "keogram.png", dpi=140, facecolor=fig.get_facecolor())
    plt.close(fig)

    # A source and column shown without changing either image's numeric pixel data.
    selected = min((r for r in rows if r["evidence"] is not None),
                   key=lambda r: abs(float(r["evidence"]["extra"]["stellarSolarAltitudeDegrees"]) + 12))
    slot = selected["slot"]
    path = json.loads((args.day / "sample-path.json").read_text())
    points = [p for p in path if p is not None]
    fig, axes = plt.subplots(1, 3, figsize=(15, 6), gridspec_kw={"width_ratios": [4, .65, 5]}, facecolor="#10171e")
    axes[0].imshow(Image.open(args.day / "sequence" / f"{slot:04d}.png"))
    axes[0].plot([p["x"] for p in points], [p["y"] for p in points], color="#f4cb76", lw=1.2)
    axes[0].set_title(f"Source {slot} · {stamp(selected['utc']).astimezone(zone):%b %d %H:%M:%S} MST\nGold: projected north–zenith–south sampling path")
    axes[0].axis("off")
    axes[1].imshow(raw[:, slot:slot + 1], aspect="auto", interpolation="nearest", extent=(0, 1, 180, 0))
    axes[1].set_title("Actual\ncolumn")
    axes[1].set_xticks([])
    axes[1].set_yticks([0, 90, 180], ["N", "Z", "S"])
    axes[2].imshow(raw, aspect="auto", interpolation="nearest", extent=(0, len(rows), 180, 0))
    axes[2].axvline(slot + .5, color="#f4cb76", lw=1)
    axes[2].set_title("The same column in the full planned period")
    axes[2].set_yticks([0, 90, 180], ["N", "Z", "S"])
    axes[2].set_xticks(ticks[::2], labels[::2])
    fig.tight_layout()
    fig.savefig(args.output / "source-strip-column.png", dpi=130, facecolor=fig.get_facecolor())
    plt.close(fig)
    shutil.copy2(args.day / "star-trail.png", args.output / "star-trail.png")

    # Preserve numerical evidence and make only selected public files available.
    sample_dir = args.output / "samples"
    sample_dir.mkdir()
    for sample in samples:
        for suffix in [".png", "-linear.png"]:
            shutil.copy2(args.samples / (sample["name"] + suffix), sample_dir / (sample["name"] + suffix))
    for file in ["scenario.json", "coverage.json", "day-resources.json", "sample-path.json", "verification.json"]:
        shutil.copy2(args.day / file, args.output / file)
    for label, source in [("day", args.day), ("samples", args.samples)]:
        if (source / "harness-identity.json").exists():
            shutil.copy2(source / "harness-identity.json", args.output / (label + "-harness-identity.json"))
            shutil.copy2(source / "harness-source.cs", args.output / (label + "-harness.cs.txt"))
    shutil.copy2(args.samples / "samples-resources.json", args.output / "samples-resources.json")
    (args.output / "samples.json").write_text(json.dumps(samples, indent=2))
    fields = ["slot", "source_utc", "acquired_utc", "exposure_seconds", "gain", "raw_mean", "p01", "p50", "p99", "adc_clipped_fraction", "raw_sha256", "display_pixels_sha256", "state", "trail_eligible", "daily_playback_seconds"]
    with (args.output / "source-index.csv").open("w", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(fields)
        for row in rows:
            e = row["evidence"] or {}
            writer.writerow([row["slot"], row["utc"], e.get("timestampUtc", ""), e.get("exposureSeconds", ""), e.get("gain", ""),
                             e.get("rawMean", ""), e.get("p01", ""), e.get("p50", ""), e.get("p99", ""), e.get("saturationFraction", ""),
                             e.get("rawSha256", ""), e.get("displayPixelsSha256", ""), row["missing"] or "captured", row["dark"], row["slot"] / 24])
    with (args.output / "source-settings.jsonl").open("w") as stream:
        for row in rows:
            stream.write(json.dumps(row, separators=(",", ":")) + "\n")

    font_file = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
    font = ImageFont.truetype(font_file, 20)
    small = ImageFont.truetype(font_file, 16)
    video_frames = args.output.parent / (args.output.name + "-video-frames-private")
    video_frames.mkdir()
    for row in rows:
        canvas = Image.new("RGB", (960, 720), "#10171e")
        draw = ImageDraw.Draw(canvas)
        source_time = stamp(row["utc"]).astimezone(zone)
        draw.text((20, 12), f"October 12 observing period · {source_time:%b %d %H:%M:%S} MST", font=font, fill="white")
        canvas.paste(Image.open(args.day / "sequence" / f"{row['slot']:04d}.png"), (0, 50))
        if row["missing"]:
            draw.multiline_text((95, 320), "NO CAPTURE\n" + row["missing"].replace("-", " "), font=font, fill="#f4cb76", spacing=12)
        e = row["evidence"]
        details = ["ACTUAL VIRTUALSKY", "640 × 640 CFA source", "Fixed black / white", "64 / 4095", "Fixed gamma 2.2", "No auto-stretch", "", "1 planned slot / minute", "20 s maximum exposure", "Gaps keep their slots"]
        if e:
            details += ["", f"Exposure {e['exposureSeconds']:.6g} s", f"Gain {e['gain']}", f"Raw mean {e['rawMean']:.1f}", f"ADC clipped {e['saturationFraction']:.2%}", f"Sun {float(e['extra']['stellarSolarAltitudeDegrees']):+.2f}°"]
        draw.multiline_text((656, 100), "\n".join(details), font=small, fill="#dce5ed", spacing=8)
        draw.text((20, 692), "Compressed playback · timestamps show source wall-clock time · no missing sky pixels invented", font=small, fill="#b5c4d2")
        canvas.save(video_frames / f"{row['slot']:04d}.png")
    ffmpeg = args.encoder / "ffmpeg"
    ffprobe = args.encoder / "ffprobe"
    videos = []
    # Hourly sample uses the actual site's civil 18:00–19:00 bucket, including its recorded timestamp offsets.
    hourly_rows = [r for r in rows if stamp(r["utc"]).astimezone(zone).day == 12 and stamp(r["utc"]).astimezone(zone).hour == 18]
    for name, first, count, fps in [("daily", 0, len(rows), 24), ("hourly-18", hourly_rows[0]["slot"], len(hourly_rows), 6)]:
        target = args.output / (name + ".mp4")
        command = [str(ffmpeg), "-hide_banner", "-loglevel", "warning", "-nostdin", "-n", "-framerate", str(fps),
                   "-start_number", str(first), "-i", str(video_frames / "%04d.png"), "-frames:v", str(count),
                   "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-threads", "2", str(target)]
        before = time.monotonic()
        subprocess.run(command, check=True)
        probe = json.loads(subprocess.check_output([str(ffprobe), "-v", "error", "-count_frames", "-show_streams", "-show_format", "-of", "json", str(target)]))
        stream = probe["streams"][0]
        if int(stream["nb_read_frames"]) != count or stream["width"] != 960 or stream["height"] != 720 or stream["codec_name"] != "h264":
            raise AssertionError("Encoded video does not match the planned source mapping.")
        # Decode the complete video; successful metadata inspection alone is not playback validation.
        subprocess.run([str(ffmpeg), "-v", "error", "-xerror", "-nostdin", "-i", str(target), "-f", "null", "-"], check=True)
        videos.append({"file": target.name, "firstSlot": first, "sourceSlotCount": count, "fps": fps,
                       "nominalCompression": 60 * fps, "sourcePeriodStart": rows[first]["utc"],
                       "sourcePeriodEndExclusive": scenario["end"] if name == "daily" else "2026-10-13T02:00:00Z",
                       "plannedPeriodStart": scenario["start"] if name == "daily" else "2026-10-13T01:00:00Z",
                       "firstSourceUtc": rows[first]["utc"], "lastSourceUtc": rows[first + count - 1]["utc"],
                       "gapPolicy": "Missing slots are text slates at the same playback duration as captured slots; no frame interpolation or duplicate-sky hold.",
                       "mapping": "For playback frame j, daily slot = firstSlot + j; playback seconds = j / fps. Source times and exposure durations are in source-index.csv.",
                       "command": command, "ffprobe": probe, "sha256": digest(target), "encodeAndVerifySeconds": time.monotonic() - before})
    encoder_evidence = {"ffmpegVersion": subprocess.check_output([str(ffmpeg), "-version"], text=True),
                        "ffmpegSha256": digest(ffmpeg), "ffprobeSha256": digest(ffprobe), "videos": videos}
    (args.output / "video-evidence.json").write_text(json.dumps(encoder_evidence, indent=2))
    build_visual_changes(args, samples)
    build_page(args.output, scenario, coverage, samples, videos, rows)
    (args.output / "presentation-resources.json").write_text(json.dumps({"elapsedSeconds": time.monotonic() - started,
        "videoEncoderThreads": 2, "annotatedFramesOnDisk": len(rows), "inMemoryFrames": 1}, indent=2))
    files = sorted(p.relative_to(args.output).as_posix() for p in args.output.rglob("*") if p.is_file())
    manifest = {file: {"sha256": digest(args.output / file), "bytes": (args.output / file).stat().st_size} for file in files}
    (args.output / "checksums.json").write_text(json.dumps(manifest, indent=2))
    files.append("checksums.json")
    (args.output / "allowlist.json").write_text(json.dumps(files, indent=2))
    print(json.dumps({"reviewRoot": str(args.output), "allowlistedFiles": len(files), "videos": len(videos)}))


def build_visual_changes(args, samples):
    fig, axes = plt.subplots(1, 2, figsize=(12, 6), facecolor="#10171e")
    for ax, name in zip(axes, ["day-policy", "night-policy"]):
        ax.imshow(Image.open(args.samples / (name + ".png")))
        ax.set_xlim(-25, 665); ax.set_ylim(665, -25); ax.set_facecolor("#10171e")
        for x, y, label in [(320, -8, "N"), (-8, 320, "E"), (648, 320, "W"), (320, 648, "S")]:
            ax.text(x, y, label, ha="center", va="center", color="#f4cb76", fontsize=14)
        ax.set_title(name.replace("-", " ")); ax.axis("off")
    fig.tight_layout(); fig.savefig(args.output / "orientation.png", dpi=130, facecolor=fig.get_facecolor()); plt.close(fig)
    fig, axes = plt.subplots(3, 1, figsize=(14, 9), facecolor="#10171e")
    for ax, name in zip(axes, ["day-policy", "sunset-policy", "night-policy"]):
        panorama = args.samples / (name + "-panorama.png")
        ax.imshow(Image.open(panorama), extent=(0, 360, 0, 90), aspect="auto")
        ax.set_xticks([0, 90, 180, 270, 360], ["N · 0°", "E · 90°", "S · 180°", "W · 270°", "N · 360°"])
        ax.set_yticks([0, 30, 60, 90]); ax.set_ylabel("Altitude °"); ax.set_title(name.replace("-", " "))
        sample = next(s for s in samples if s["name"] == name)
        for disk in json.loads(sample["extra"]["solarDiskAppearance"]):
            direction = disk["Direction"]
            if direction["AltitudeDegrees"] >= 0:
                x, y = direction["AzimuthDegrees"], direction["AltitudeDegrees"]
                ax.plot(x, y, marker="o", ms=10, mfc="none", mec="#f4cb76", lw=0)
                ax.annotate("Sun" if disk["Body"] == 0 else "Moon", (x, y), xytext=(10, 9), textcoords="offset points", color="#f4cb76")
        shutil.copy2(panorama, args.output / panorama.name)
        shutil.copy2(args.samples / (name + "-panorama.json"), args.output / (name + "-panorama.json"))
    fig.tight_layout(); fig.savefig(args.output / "panoramas.png", dpi=130, facecolor=fig.get_facecolor()); plt.close(fig)
    if args.details is None:
        raise ValueError("Revised review requires the actual narrow-field detail captures.")
    details = json.loads((args.details / "details.json").read_text())
    fig, axes = plt.subplots(1, len(details), figsize=(16, 5), facecolor="#10171e")
    for ax, detail in zip(axes, details):
        a, e = detail["appearance"], detail["source"]
        ax.imshow(Image.open(args.details / (detail["name"] + ".png")))
        ax.axis("off")
        ax.set_title(detail["name"].replace("-", " ") + "\n" + a["utc"][:19] + " UTC", fontsize=10)
        ax.text(.5, -.04, f"1.5° field · diameter {2*a['angularRadiusDegrees']:.3f}°\nLit {a['illuminatedFraction']:.1%} · {e['exposureSeconds']:.5g} s · gain {e['gain']}", transform=ax.transAxes, ha="center", va="top", fontsize=10)
        shutil.copy2(args.details / (detail["name"] + ".png"), args.output / (detail["name"] + ".png"))
    fig.tight_layout(); fig.savefig(args.output / "disk-details.png", dpi=130, bbox_inches="tight", facecolor=fig.get_facecolor()); plt.close(fig)
    for name in ["details.json", "details-resources.json", "harness-identity.json"]:
        shutil.copy2(args.details / name, args.output / ("detail-" + name if name == "harness-identity.json" else name))


def build_page(output, scenario, coverage, samples, videos, rows):
    grouped = {}
    for sample in samples:
        phase = sample["name"].removesuffix("-policy")
        if sample["name"].endswith("-policy"):
            grouped[phase] = sample
    def card(sample):
        name = html.escape(sample["name"])
        return f'<article><a href="samples/{name}.png"><img loading="lazy" src="samples/{name}.png" alt="{name}"></a><h3>{name.replace("-", " ")}</h3><p>{sample["exposureSeconds"]:.6g}s · gain {sample["gain"]} · mean {sample["rawMean"]:.1f} ADU<br>p99 {sample["p99"]} · clipped {sample["saturationFraction"]:.2%}</p><a href="samples/{name}-linear.png">Linear sensor-range display</a></article>'
    all_cards = "".join(card(s) for s in grouped.values())
    compare_cards = "".join(card(s) for s in samples if s["name"] in ["day-fixed-short", "day-policy", "day-policy-high-gain", "day-long-low-gain", "day-long-high-gain", "day-controlled-night", "night-policy", "night-long-high-gain"])
    numeric_rows = "".join(f'<tr><td>{html.escape(s["name"])}</td><td>{s["exposureSeconds"]:.7g}</td><td>{s["gain"]}</td><td>{s["rawMean"]:.2f}</td><td>{s["p01"]} / {s["p50"]} / {s["p99"]}</td><td>{s["saturationFraction"]:.3%}</td><td>{s["extra"]["stellarAdmittedCount"]}</td></tr>' for s in samples)
    captured = sum(r["evidence"] is not None for r in rows)
    page = f'''<!doctype html><html lang="en" data-theme="hvo-dark"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>VirtualSky · full-day POC review</title>
<style>:root{{color-scheme:dark}}*{{box-sizing:border-box}}body{{margin:0;background:#10171e;color:#dce5ed;font:16px/1.55 system-ui,sans-serif}}main{{max-width:1380px;margin:auto;padding:32px 24px 80px}}h1{{font-size:30px;font-weight:550}}h2{{margin-top:48px;font-size:23px;font-weight:550}}h3{{font-size:16px;font-weight:550}}a{{color:#8fbedf}}p{{max-width:1040px}}.note{{padding:18px 22px;background:#1b2833;border-left:3px solid #ddae60}}.grid{{display:grid;grid-template-columns:repeat(auto-fit,minmax(245px,1fr));gap:20px}}article{{background:#16212a;padding:14px;border:1px solid #2b3b48}}img{{max-width:100%;height:auto}}article img{{width:100%;background:black}}article p{{font-size:14px}}video{{display:block;width:min(100%,960px);background:black}}table{{border-collapse:collapse;font-size:13px}}td,th{{padding:7px 12px;text-align:left;border-bottom:1px solid #30414f}}.scroll{{overflow:auto}}.caption{{color:#b1c1ce;font-size:14px}}nav{{display:flex;flex-wrap:wrap;gap:18px}}summary{{cursor:pointer;color:#8fbedf}}code{{font-size:13px}}footer{{margin-top:42px;border-top:1px solid #30414f;padding-top:16px}}</style>
<main><p class="caption">HVO.SkyMonitor · issues #1131 and #1134 · operator review checkpoint</p><h1>A complete day, one continuous time axis</h1>
<p class="note"><strong>Prototype evidence — awaiting visual acceptance.</strong> These are actual VirtualSky sensor outputs from the verified 119,625-row HYG catalog. Production scheduling, sunrise/calendar adoption and FFmpeg integration remain separate issues.</p>
<nav><a href="#orientation">Orientation / panorama / disks</a><a href="#sources">Sources</a><a href="#exposure">Exposure comparison</a><a href="#keogram">Keogram</a><a href="#trail">Star trail</a><a href="#video">Playable videos</a><a href="#evidence">Evidence</a></nav>
<p><strong>“The 12th” proposes sunrise on October 12 through sunrise on October 13.</strong><br>2026-10-12 06:39:23.1586721 → 2026-10-13 06:40:13.4408130 MST (UTC−7), at 35.347° N, 113.878° W, elevation 0 m. Shared Astronomy resolved the exact half-open period. It lasts 24 hours 50.282 seconds. Historical archive compatibility is not implemented.</p>
<p>Daily finals become eligible after the ending sunrise and after source processing settles. The period, actual coverage, processing trigger and finality are distinct. This page proposes the date association; it does not change existing archives.</p>
<h2 id="orientation">Corrected upward-looking view</h2><p>North is up, East left, West right. These are newly rendered sources using the supported camera flip, not a relabeling of the old map-style frames. <a href="http://192.168.2.45:8094/">The original review remains preserved.</a></p><img src="orientation.png" alt="Source frames labeled North up, East left, West right, South down">
<h3>Per-image 360° panorama</h3><p>A calibrated azimuth/altitude reprojection of each source: North–East–South–West–North from left to right, horizon at the bottom and zenith at the top. The zenith is stretched across the top edge. Black unavailable samples are not extrapolated. Body markers locate the actual Sun/Moon and do not represent their size.</p><img src="panoramas.png" alt="Day, sunset and night source panoramas with compass and altitude axes">
<h3>True-size disks and separately labeled narrow-field captures</h3><p>The all-sky Sun and Moon are only a few pixels wide. These additional 1.5° perspective-field captures make their shapes readable; no oversized disks were pasted into the all-sky images. Sun/Moon positions and apparent sizes are topocentric. Lunar phase fraction is geocentric and the bright limb points toward the Sun. Geometry is sampled at each exposure midpoint. Refraction, surface texture, eclipses, occultations and optical flare are outside this approximation. Additional quarter/full dates are explicitly outside the daily sequence.</p><img src="disk-details.png" alt="Sun and lunar crescent, quarter and full phase narrow-field sensor captures"><p><a href="details.json">Exact detail times, rig, phase, exposure, gain and raw checksums</a></p>
<h2 id="sources">Daylight, twilight and night</h2><p>640×640 RGGB, native 12-bit samples. Every display subtracts the same black level (64), divides by the same white range (4095−64), then applies fixed gamma 2.2. Linear displays are linked. There is no per-frame auto-stretch. The clear-sky color model is an approximation with no atmosphere solver, surface texture or Milky Way renderer. Opt-in Sun/Moon disks retain their true apparent angular size; their light passes through the sensor path.</p>
<p>The sequence uses a declared open-loop exposure formula: clamp(2500 / modeled scalar sky rate, 1 µs, 20 s), gain 0. This is a POC recipe, not a claim of production automatic exposure. Actual acquisition timestamps and advancing celestial exposure times are retained separately.</p><div class="grid">{all_cards}</div>
<h2 id="exposure">Exposure, gain and illumination mode</h2><p>Excessive daytime integration washes the sensor out. Controlled-night mode below uses actual daytime celestial geometry with a night background and is labeled separately. It is not representative daylight. ADC clipping counts samples exactly at 4095; full-well clipping followed by read noise can also produce codes just below white.</p><div class="grid">{compare_cards}</div>
<details><summary>All matched raw numeric results</summary><div class="scroll"><table><thead><tr><th>Sample</th><th>Seconds</th><th>Gain</th><th>Raw mean</th><th>p01 / p50 / p99</th><th>ADC clipped</th><th>Admitted stars</th></tr></thead><tbody>{numeric_rows}</tbody></table></div></details>
<h2 id="keogram">The entire planned period</h2><p>{captured} real captured source slots of {len(rows)} planned minute slots. Gray patterned regions carry no sky pixels: leading 15 minutes intentionally unscheduled, a deliberate 30-minute outage, and trailing 15 minutes missing. The plot does not shrink to the available captures. Capture cadence is separate from exposure coverage.</p><a href="keogram.png"><img src="keogram.png" alt="Full sunrise-to-sunrise horizontal keogram with site time and north zenith south labels"></a>
<p class="caption">Coverage states are explicit. This completed-period example has no quality exclusions and no unelapsed slots; neither is silently counted as an outage or as captured coverage. One column samples the shared-projection N–Z–S strip, not a squeezed full frame.</p>
<a href="source-strip-column.png"><img src="source-strip-column.png" alt="A source image with its actual projected sampled strip and the corresponding keogram column"></a>
<h2 id="trail">Dark-night frames only</h2><p>{coverage['darkSourceCount']} eligible frames, {coverage['integratedSeconds']:.0f} actual integrated seconds, inside the astronomical dark window <code>{coverage['darkStart']}</code> to <code>{coverage['darkEnd']}</code>. Both exposure boundaries must lie in that window and recorded solar altitude must be ≤ −18°. All daylight and twilight frames are excluded. The deliberate outage remains absent.</p>
<img style="width:min(100%,640px)" src="star-trail.png" alt="Actual lighten composite from eligible dark-night frames only"><p class="caption">A maximum composite of discrete real exposures, without invented arcs or interpolation. One-minute cadence and at most 20-second exposures leave uncaptured time even outside the deliberate outage.</p>
<h2 id="video">Hourly and daily time-lapse coexist</h2><p>Both are genuine H.264 MP4s encoded with FFmpeg, inspected with ffprobe and fully decoded for verification. Source timestamps are burned in. Missing minute slots remain black text slates; their time is not silently compressed away.</p>
<h3>Daily · {len(rows)} slots / 24 fps · {len(rows)/24:.3f} seconds · nominal 1440×</h3><video controls preload="metadata" src="daily.mp4"></video>
<h3>Hourly · October 12, 18:00–19:00 MST bucket · 60 slots / 6 fps · 10 seconds · 360×</h3><video controls preload="metadata" src="hourly-18.mp4"></video><p class="caption">Source times follow the sunrise-anchored minute grid, at :23.1586721 each minute. The last daily slot represents the final 50.282 seconds. Playback is sampled time compression, not uninterrupted exposure or a live stream.</p>
<h2 id="evidence">Evidence index</h2><ul><li><a href="scenario.json">Site, exact period, recipe, sensor and prototype identities</a></li><li><a href="samples.json">Matched sample settings, statistics, raw checksums and provenance</a></li><li><a href="source-index.csv">Every source slot, gap state and playback mapping (CSV)</a></li><li><a href="source-settings.jsonl">Complete per-source settings and provenance (JSON Lines)</a></li><li><a href="coverage.json">Dark selection, expected window, actual integration and gaps</a></li><li><a href="video-evidence.json">FFmpeg identity, exact commands, ffprobe results and video checksums</a></li><li><a href="day-resources.json">Capture CPU, memory, elapsed time and buffer bounds</a> · <a href="samples-resources.json">matched sample resources</a> · <a href="presentation-resources.json">presentation resources</a></li><li><a href="verification.json">Independent source, coverage, strip and trail checks</a></li><li><a href="day-harness-identity.json">Day harness and assembly identities</a> · <a href="samples-harness-identity.json">sample identities</a> · <a href="day-harness.cs.txt">retained capture source</a></li><li><a href="checksums.json">SHA-256 and byte sizes for every public artifact</a></li></ul>
<footer><strong>Requested disposition:</strong> day/night appearance; keogram proportions and N–Z–S/time axes; full-period and gap mapping; hourly plus daily playback; and starting-sunrise date association. Acceptance is recorded explicitly in #1134. It does not deliver production issues #1135, #1136, #993 or #1130.</footer></main></html>'''
    (output / "index.html").write_text(page)


if __name__ == "__main__":
    main()
