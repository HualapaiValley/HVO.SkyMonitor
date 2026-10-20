"""Offline, bounded research fixture; no production orbital implementation."""
import argparse
import hashlib
import importlib.metadata
import json
import math
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import skyfield.iokit
from sgp4.api import Satrec, WGS72
from skyfield.api import EarthSatellite, Loader, load_file, wgs84
from skyfield.data import iers


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def pixel(altitude, azimuth):
    # Independent check of the declared 640-square upward equidistant rig.
    radius = 313.6 * (90.0 - altitude) / 90.0
    az = math.radians(azimuth)
    return [320.0 - radius * math.sin(az), 320.0 - radius * math.cos(az)]


def enu(alt, az):
    a, z = math.radians(alt), math.radians(az)
    return np.array([math.cos(a) * math.sin(z), math.cos(a) * math.cos(z), math.sin(a)])


def reference_check():
    # Vallado verification satellite 00005 at +360 minutes; external literals.
    model = Satrec.twoline2rv(
        "1 00005U 58002B   00179.78495062  .00000023  00000-0  28098-4 0  4753",
        "2 00005  34.2682 348.7242 1859667 331.7664  19.3264 10.82419157413667", WGS72)
    error, position, velocity = model.sgp4_tsince(360.0)
    assert error == 0
    expected_r = np.array([-7154.03120202, -3783.17682504, -3536.19412294])
    expected_v = np.array([4.741887409, -4.151817765, -2.093935425])
    np.testing.assert_allclose(position, expected_r, rtol=0, atol=1e-7)
    np.testing.assert_allclose(velocity, expected_v, rtol=0, atol=1e-9)
    return {"case": "Vallado 00005 +360 minutes", "positionMaxErrorKm": float(np.max(np.abs(position - expected_r))),
            "velocityMaxErrorKmPerSecond": float(np.max(np.abs(velocity - expected_v))),
            "positionToleranceKm": 1e-7, "velocityToleranceKmPerSecond": 1e-9,
            "source": "https://github.com/brandon-rhodes/python-sgp4/blob/master/sgp4/tcppver.out"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("gp", type=Path)
    parser.add_argument("reference_directory", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--site", choices=("hvo", "seattle"), default="seattle")
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Use a new fixture path; retained evidence is immutable")
    if args.gp.stat().st_size > 20_000_000:
        raise ValueError("GP input exceeds research bound")
    check = reference_check()
    eop_path = args.reference_directory / "finals2000A.all"
    ephemeris_path = args.reference_directory / "de421.bsp"
    for path in (eop_path, ephemeris_path):
        if not path.is_file():
            raise ValueError(f"Explicit offline input required: {path}")
    loader = Loader(str(args.reference_directory), verbose=False)

    def no_download(*args, **kwargs):
        raise RuntimeError("Fixture replay must not perform network I/O")

    # Loader._assure calls this module function, not Loader.download.
    skyfield.iokit.download = no_download
    ts = loader.timescale(builtin=False)
    with eop_path.open("rb") as source:
        eop = iers.parse_x_y_dut1_from_finals_all(source)
    iers.install_polar_motion_table(ts, eop)
    eph = load_file(str(ephemeris_path))
    records = [r for r in json.loads(args.gp.read_text()) if int(r["NORAD_CAT_ID"]) == 25544]
    if len(records) != 1:
        raise ValueError("Require exactly one selected ISS GP record")
    record = dict(records[0])
    for key, expected in (("CENTER_NAME", "EARTH"), ("REF_FRAME", "TEME"),
                          ("TIME_SYSTEM", "UTC"), ("MEAN_ELEMENT_THEORY", "SGP4")):
        if record.get(key, expected) != expected:
            raise ValueError(f"Unsupported {key}")
        record[key] = expected
    epoch = datetime.fromisoformat(record["EPOCH"].replace("Z", "+00:00"))
    if epoch.tzinfo is None:
        epoch = epoch.replace(tzinfo=timezone.utc)
    epoch = epoch.astimezone(timezone.utc)
    record["EPOCH"] = epoch.replace(tzinfo=None).isoformat(timespec="microseconds")
    satellite = EarthSatellite.from_omm(ts, record)
    latitude, longitude, elevation, timezone_id = (
        (35.5599378, -113.9119818, 520.0, "America/Phoenix") if args.site == "hvo" else
        (47.6062, -122.3321, 50.0, "America/Los_Angeles"))
    site = wgs84.latlon(latitude, longitude, elevation_m=elevation)
    observer = eph["earth"] + site
    search_start, search_end = ts.from_datetime(epoch) - 0.5, ts.from_datetime(epoch) + 2.0

    def samples(times):
        geo = satellite.at(times)
        if geo.message is not None and any(m is not None for m in np.atleast_1d(geo.message)):
            raise RuntimeError(str(geo.message))
        alt, az, distance = (satellite - site).at(times).altaz()
        solar_alt, _, _ = observer.at(times).observe(eph["sun"]).apparent().altaz()
        illuminated = geo.is_sunlit(eph)
        return [{"utc": t.utc_iso(places=6), "altitudeDegrees": float(a), "azimuthDegrees": float(z),
                 "rangeKm": float(r), "sunlit": bool(s), "solarAltitudeDegrees": float(sa),
                 "elementAgeHours": float((t - satellite.epoch) * 24), "referencePixel": pixel(a, z)}
                for t, a, z, r, s, sa in zip(times, alt.degrees, az.degrees, distance.km, illuminated, solar_alt.degrees)]

    events, codes = satellite.find_events(site, search_start, search_end, altitude_degrees=0.0)
    candidates = []
    for index, code in enumerate(codes):
        if code != 1:
            continue
        peak = events[index]
        peak_state = samples(ts.tt_jd(peak.whole, np.atleast_1d(peak.tt_fraction)))[0]
        before = [i for i in range(index) if codes[i] == 0]
        after = [i for i in range(index + 1, len(codes)) if codes[i] == 2]
        if not before or not after:
            continue
        rise, setting = events[before[-1]], events[after[0]]
        duration = float((setting - rise) * 86400)
        scan_times = ts.tt_jd(rise.whole, rise.tt_fraction + np.arange(0, duration, 5) / 86400)
        states = samples(scan_times)
        windows = [i for i in range(10, len(states) - 10)
                   if all(s["altitudeDegrees"] >= 10 and s["solarAltitudeDegrees"] <= -6 and s["sunlit"]
                          for s in states[i - 10:i + 11])]
        center_index = max(windows, key=lambda i: states[i]["altitudeDegrees"]) if windows else None
        candidates.append({"eventIndex": index, "peakUtc": peak.utc_iso(places=6),
                           "peakAltitudeDegrees": peak_state["altitudeDegrees"],
                           "solarAltitudeDegrees": peak_state["solarAltitudeDegrees"],
                           "sunlitAtPeak": peak_state["sunlit"], "wholeWindowEligible": bool(windows),
                           "windowCenterUtc": states[center_index]["utc"] if windows else None,
                           "windowCenterAltitudeDegrees": states[center_index]["altitudeDegrees"] if windows else None})
    eligible = [c for c in candidates if c["wholeWindowEligible"]]
    if not eligible:
        print(json.dumps(candidates, indent=2))
        raise RuntimeError(f"No complete dark/sunlit 100-second {args.site} window in this bounded search")
    chosen = max(eligible, key=lambda c: c["windowCenterAltitudeDegrees"])
    index = chosen["eventIndex"]
    before = [i for i in range(index) if codes[i] == 0]
    after = [i for i in range(index + 1, len(codes)) if codes[i] == 2]
    if not before or not after:
        raise RuntimeError("Selected pass lacks a complete horizon bracket")
    rise, setting, peak = events[before[-1]], events[after[0]], events[index]
    center = ts.from_datetime(datetime.fromisoformat(chosen["windowCenterUtc"].replace("Z", "+00:00")))
    first = center - 50 / 86400
    exposure_starts = ts.tt_jd(first.whole, first.tt_fraction + np.arange(5) * 20 / 86400)
    exposure_samples = samples(ts.tt_jd(first.whole, first.tt_fraction + np.arange(401) * 0.25 / 86400))
    if not all(s["sunlit"] and s["altitudeDegrees"] >= 10 and s["solarAltitudeDegrees"] <= -6 for s in exposure_samples):
        raise RuntimeError("Dense samples invalidate the coarse eligible-window selection")
    dense = samples(ts.tt_jd(first.whole, first.tt_fraction + (np.arange(400) * 0.25 + 0.125) / 86400))
    maximum_interpolation_error = 0.0
    for left, right, middle in zip(exposure_samples[:-1], exposure_samples[1:], dense):
        vector = enu(left["altitudeDegrees"], left["azimuthDegrees"]) + enu(right["altitudeDegrees"], right["azimuthDegrees"])
        vector /= np.linalg.norm(vector)
        interp_alt = math.degrees(math.asin(vector[2]))
        interp_az = math.degrees(math.atan2(vector[0], vector[1])) % 360
        error = np.linalg.norm(np.array(pixel(interp_alt, interp_az)) - middle["referencePixel"])
        maximum_interpolation_error = max(maximum_interpolation_error, float(error))
    if maximum_interpolation_error > 0.01:
        raise RuntimeError("Fixed fixture sampling exceeds 0.01 raw pixel interpolation budget")
    pass_seconds = float((setting - rise) * 86400)
    if not 0 < pass_seconds < 1200:
        raise ValueError("Unexpected pass duration")
    full_path = samples(ts.tt_jd(rise.whole, rise.tt_fraction + np.linspace(0, pass_seconds, math.ceil(pass_seconds) + 1) / 86400))
    mjd_first = first.utc_datetime().timestamp() / 86400 + 40587
    mjd_last = (first + 100 / 86400).utc_datetime().timestamp() / 86400 + 40587
    if mjd_first < float(eop["utc_mjd"][0]) or mjd_last > float(eop["utc_mjd"][-1]):
        raise ValueError("Chosen observation is outside retained EOP coverage")
    result = {
        "schemaVersion": "satellite-virtual-poc-v1", "objectName": "ISS (ZARYA)", "noradCatalogId": 25544,
        "observer": {"name": args.site, "latitudeDegrees": latitude, "longitudeDegrees": longitude,
                     "elevationMeters": elevation, "timeZoneId": timezone_id,
                     "heightAssumption": "Illustrative/configured height treated as WGS84 ellipsoidal; not independently surveyed"},
        "inputGpSha256": digest(args.gp), "sourceRecord": records[0], "normalizedRecord": record,
        "referenceInputs": [{"file": p.name, "bytes": p.stat().st_size, "sha256": digest(p)} for p in (eop_path, ephemeris_path)],
        "packages": {name: importlib.metadata.version(name) for name in ("sgp4", "skyfield", "numpy", "jplephem", "certifi")},
        "model": "SGP4 WGS72 improved i; Skyfield TEME/topocentric conversion; retained USNO EOP including polar motion; no atmospheric refraction; DE421 sunlight",
        "referenceVerification": check, "searchStartUtc": search_start.utc_iso(places=6), "searchEndUtc": search_end.utc_iso(places=6),
        "passesConsidered": candidates, "chosenPass": chosen, "riseUtc": rise.utc_iso(places=6), "setUtc": setting.utc_iso(places=6),
        "sequenceStartUtc": first.utc_iso(places=6), "exposureSeconds": 20, "frameCount": 5,
        "exposureStartsUtc": [t.utc_iso(places=6) for t in exposure_starts],
        "syntheticMagnitude": -3.0, "syntheticAngularWidthDegrees": 0.05,
        "opticalModel": "Explicit constant synthetic magnitude -3; not inferred from orbital data, not a prediction of actual ISS brightness",
        "sampling": {"keyframeStepSeconds": 0.25, "maximumMidpointInterpolationErrorPixels": maximum_interpolation_error,
                     "testedMidpoints": len(dense), "errorBudgetPixels": 0.01, "rawWidth": 640, "rawHeight": 640, "imageCircleRadiusPixels": 313.6},
        "timeEvidence": {"dut1SecondsAtStart": float(first.dut1), "deltaTSecondsAtStart": float(first.delta_t),
                         "eopFirstMjd": float(eop["utc_mjd"][0]), "eopLastMjd": float(eop["utc_mjd"][-1])},
        "exposureSamples": exposure_samples, "passSamples": full_path,
        "limitations": ["Synthetic software evidence only; no live-sky accuracy or sensitivity claim",
                        "Sunlit status and dark sky are eligibility criteria, not detection guarantees",
                        "One frozen orbit; no maneuvers or covariance model",
                        "Sampling bound tested at temporal midpoints for this fixture, not globally proven"]}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n")
    print(json.dumps({"fixture": str(args.output), "sha256": digest(args.output), "selected": chosen,
                      "sequenceStartUtc": result["sequenceStartUtc"], "referenceCheck": check,
                      "maximumInterpolationErrorPixels": maximum_interpolation_error}, indent=2))


if __name__ == "__main__":
    main()
