"""#525 F6: independent numerical references for deep-sky extents, outlines and glyph centres.

This generator shares no code with HVO.SkyMonitor. Its astronomy comes from ERFA through pyerfa: the IAU 1976
precession matrix (pmat76) at TT, Greenwich mean sidereal time IAU 1982 (gmst82) with UT1 taken equal to UTC, and the
hour-angle to azimuth/elevation rotation (hd2ae). There is no nutation, aberration or refraction. Its geometry follows
published definitions only:

- resolved-footprint-v1 (docs/astronomy/resolved-footprint.md): a tangent basis at the centre with celestial north the
  projection of the pole and east = north x centre; limb a*cos(t)*major + b*sin(t)*minor with
  major = N*cos(PA) + E*sin(PA); the exponential map back onto the sphere. The catalogue position angle is measured
  from J2000 north, so the limb is built in the J2000 frame and each limb direction is then precessed. A rotation
  carries the tangent plane, its north and the exponential map with it, so this is the same curve as rotating the
  position angle to north of date at the precessed centre.
- deep-sky-openngc-v1 (docs/astronomy/deep-sky-openngc-v1.md): an outline ring's edges are great-circle arcs between
  consecutive J2000 vertices, the ring's last point repeats its first, and the level drawn is the available level
  closest to the preferred one, the wider on a tie.
- The camera conventions of docs/virtual-camera.md: continuous pixel-edge coordinates; equidistant fisheye r = f*theta;
  pinhole x = cx + f*tan; in the unflipped zenith basis north maps toward -Y and east toward +X. Off zenith, zero roll
  puts local vertical at image-up and the frame keeps the zenith basis's handedness (right x up = boresight). The zenith
  convention is checked against the documented ASI174 horizon cardinals before any case is generated.

Each curve is sampled by recursive bisection of its parameter until every chord is at most MAXIMUM_CHORD_PIXELS long
and its midpoint lies within MAXIMUM_SAG_PIXELS of it; the quarter points of every final chord are then checked
against twice that bound. Every evaluated point must be above the horizon and inside the sensor and aperture.

Usage: python -I deep_sky_reference.py OBJECTS_CSV OUTLINE_POINTS_CSV OUTPUT_JSON
"""
import csv
import hashlib
import json
import math
import os
import platform
import re
import sys

import erfa
import numpy

SCHEMA = "hvo-525-deep-sky-reference-v1"
LATITUDE_DEGREES = 35.347
LONGITUDE_DEGREES = -113.878
ELEVATION_METERS = 0.0
UTC = (2025, 1, 15, 8, 0, 0.0)
UTC_TEXT = "2025-01-15T08:00:00Z"
MAXIMUM_SAG_PIXELS = 0.002
MAXIMUM_CHORD_PIXELS = 2.0
MAXIMUM_DEPTH = 40
ELLIPSE_INITIAL_STEPS = 64
DECIMALS = 6


def sha256(path):
    with open(path, "rb") as handle:
        return hashlib.sha256(handle.read()).hexdigest()


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def cross(a, b):
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]


def scale(a, s):
    return [a[0] * s, a[1] * s, a[2] * s]


def add(a, b):
    return [a[0] + b[0], a[1] + b[1], a[2] + b[2]]


def normalize(a):
    return scale(a, 1 / math.sqrt(dot(a, a)))


def equatorial_unit(ra_radians, dec_radians):
    return [math.cos(dec_radians) * math.cos(ra_radians), math.cos(dec_radians) * math.sin(ra_radians),
            math.sin(dec_radians)]


def horizontal_unit(altitude_radians, azimuth_radians):
    """East, north, up components of a direction at an azimuth measured from north through east."""
    return [math.cos(altitude_radians) * math.sin(azimuth_radians),
            math.cos(altitude_radians) * math.cos(azimuth_radians), math.sin(altitude_radians)]


def slerp(a, b, t):
    omega = math.acos(max(-1.0, min(1.0, dot(a, b))))
    if omega == 0:
        return list(a)
    s = math.sin(omega)
    return add(scale(a, math.sin((1 - t) * omega) / s), scale(b, math.sin(t * omega) / s))


def distance(a, b):
    return math.hypot(b[0] - a[0], b[1] - a[1])


def distance_to_segment(p, a, b):
    dx, dy = b[0] - a[0], b[1] - a[1]
    length_squared = dx * dx + dy * dy
    if length_squared == 0:
        return distance(p, a)
    t = max(0.0, min(1.0, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / length_squared))
    return distance(p, [a[0] + t * dx, a[1] + t * dy])


class Sky:
    """J2000 direction -> geometric horizontal direction at one instant and site."""

    def __init__(self):
        utc1, utc2 = erfa.dtf2d("UTC", *UTC)
        tai1, tai2 = erfa.utctai(utc1, utc2)
        tt1, tt2 = erfa.taitt(tai1, tai2)
        self.precession = numpy.asarray(erfa.pmat76(tt1, tt2)).tolist()
        self.gmst_radians = float(erfa.gmst82(utc1, utc2))
        self.local_sidereal = self.gmst_radians + math.radians(LONGITUDE_DEGREES)
        self.latitude = math.radians(LATITUDE_DEGREES)
        self.tt = (float(tt1), float(tt2))

    def horizontal(self, j2000):
        """Returns altitude and azimuth in radians and the east-north-up unit vector of a J2000 direction."""
        v = [dot(row, j2000) for row in self.precession]
        ra = math.atan2(v[1], v[0])
        dec = math.asin(max(-1.0, min(1.0, v[2] / math.sqrt(dot(v, v)))))
        azimuth, altitude = erfa.hd2ae(self.local_sidereal - ra, dec, self.latitude)
        altitude, azimuth = float(altitude), float(azimuth)
        return altitude, azimuth, horizontal_unit(altitude, azimuth)


class Camera:
    def __init__(self, model, cx, cy, focal, width, height, aperture_radius, altitude_degrees, azimuth_degrees):
        self.model, self.cx, self.cy, self.focal = model, cx, cy, focal
        self.width, self.height, self.aperture_radius = width, height, aperture_radius
        self.altitude_degrees, self.azimuth_degrees = altitude_degrees, azimuth_degrees
        if altitude_degrees == 90:
            # The documented unflipped zenith basis: east toward +X, north toward -Y (image-up).
            self.boresight, self.right, self.up = [0.0, 0.0, 1.0], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0]
        else:
            b = horizontal_unit(math.radians(altitude_degrees), math.radians(azimuth_degrees))
            zenith = [0.0, 0.0, 1.0]
            self.boresight = b
            self.up = normalize(add(zenith, scale(b, -dot(zenith, b))))
            self.right = cross(self.up, b)

    def describe(self):
        return {"model": self.model, "principalPointX": self.cx, "principalPointY": self.cy, "focalPixels": self.focal,
                "widthPixels": self.width, "heightPixels": self.height, "apertureRadiusPixels": self.aperture_radius,
                "boresightAltitudeDegrees": self.altitude_degrees, "boresightAzimuthDegrees": self.azimuth_degrees,
                "rollDegrees": 0.0, "horizontalFlip": False, "radialDistortion": 0.0}

    def project(self, d):
        along = dot(d, self.boresight)
        u, v = dot(d, self.right), dot(d, self.up)
        if self.model == "perspective":
            if along <= 0:
                return None
            return [self.cx + self.focal * u / along, self.cy - self.focal * v / along]
        lateral = math.hypot(u, v)
        if lateral == 0:
            return [self.cx, self.cy]
        theta = math.atan2(lateral, along)
        return [self.cx + self.focal * theta * u / lateral, self.cy - self.focal * theta * v / lateral]

    def admits(self, pixel):
        if pixel is None or not (0 <= pixel[0] <= self.width and 0 <= pixel[1] <= self.height):
            return False
        return self.aperture_radius is None or distance(pixel, [self.cx, self.cy]) <= self.aperture_radius


def check_documented_zenith_cardinals():
    """docs/virtual-camera.md: ASI174 principal point (968, 608), image circle 595.84 px at the horizon."""
    radius = 595.84
    camera = Camera("equidistant-fisheye", 968.0, 608.0, radius / (math.pi / 2), 1936, 1216, radius, 90, 0)
    expected = {0: (968.0, 12.16), 90: (1563.84, 608.0), 180: (968.0, 1203.84), 270: (372.16, 608.0)}
    results = []
    for azimuth, (x, y) in expected.items():
        pixel = camera.project(horizontal_unit(0.0, math.radians(azimuth)))
        error = distance(pixel, [x, y])
        if error > 1e-9:
            sys.exit(f"documented zenith cardinal at azimuth {azimuth} misses by {error} px")
        results.append({"azimuthDegrees": azimuth, "documented": [x, y], "generated": pixel})
    return results


def read_objects(path):
    with open(path, newline="", encoding="utf-8") as handle:
        return {row["id"]: row for row in csv.DictReader(handle)}


def read_outlines(path):
    grouped = {}
    with open(path, newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            rings = grouped.setdefault((row["object_id"], int(row["level"])), {})
            rings.setdefault(int(row["ring"]), []).append(
                (int(row["sequence"]), float(row["right_ascension_degrees"]), float(row["declination_degrees"])))
    result = {}
    for (object_id, level), rings in grouped.items():
        ordered = []
        for ring in sorted(rings):
            points = sorted(rings[ring])
            if [p[0] for p in points] != list(range(len(points))):
                sys.exit(f"{object_id} level {level} ring {ring} has a sequence gap")
            if points[0][1:] != points[-1][1:]:
                sys.exit(f"{object_id} level {level} ring {ring} does not repeat its first point last")
            ordered.append([(p[1], p[2]) for p in points])
        result.setdefault(object_id, {})[level] = ordered
    return result


def centre_of(row):
    return equatorial_unit(math.radians(float(row["right_ascension_hours"]) * 15),
                           math.radians(float(row["declination_degrees"])))


class Sampler:
    """Adaptive bisection of a sky curve's parameter, with admission and quarter-point checks."""

    def __init__(self, sky, camera, label):
        self.sky, self.camera, self.label = sky, camera, label
        self.worst_quarter_sag = 0.0
        self.evaluations = 0

    def pixel(self, direction_at, s):
        self.evaluations += 1
        altitude, _, d = self.sky.horizontal(direction_at(s))
        pixel = self.camera.project(d)
        if altitude < 0 or not self.camera.admits(pixel):
            sys.exit(f"{self.label}: the point at parameter {s} is not admitted; reference cases must be unclipped")
        return pixel

    def run(self, direction_at, steps):
        """Returns the pixels at parameters 0 .. 1 inclusive."""
        params = [i / steps for i in range(steps + 1)]
        pixels = [self.pixel(direction_at, s) for s in params]
        result = [pixels[0]]
        for i in range(steps):
            self._refine(direction_at, params[i], pixels[i], params[i + 1], pixels[i + 1], 0, result)
        return result

    def _refine(self, direction_at, sa, pa, sb, pb, depth, result):
        sm = (sa + sb) / 2
        pm = self.pixel(direction_at, sm)
        if depth < MAXIMUM_DEPTH and (distance(pa, pb) > MAXIMUM_CHORD_PIXELS or
                                      distance_to_segment(pm, pa, pb) > MAXIMUM_SAG_PIXELS):
            self._refine(direction_at, sa, pa, sm, pm, depth + 1, result)
            self._refine(direction_at, sm, pm, sb, pb, depth + 1, result)
            return
        if depth >= MAXIMUM_DEPTH:
            sys.exit(f"{self.label}: bisection did not converge")
        for s in (sa + (sb - sa) / 4, sa + 3 * (sb - sa) / 4):
            sag = distance_to_segment(self.pixel(direction_at, s), pa, pb)
            self.worst_quarter_sag = max(self.worst_quarter_sag, sag)
            if sag > 2 * MAXIMUM_SAG_PIXELS:
                sys.exit(f"{self.label}: quarter point at parameter {s} lies {sag} px from its chord")
        result.append(pb)


def ellipse_rings(sky, camera, row, label):
    """The resolved-footprint-v1 limb of a catalogue extent, built at J2000 and precessed per direction."""
    c = centre_of(row)
    north = normalize(add([0.0, 0.0, 1.0], scale(c, -c[2])))
    east = cross(north, c)
    major_arcminutes = float(row["major_axis_arcminutes"])
    minor_arcminutes = float(row["minor_axis_arcminutes"])
    circle = major_arcminutes == minor_arcminutes
    position_angle = 0.0 if circle else math.radians(float(row["position_angle_degrees"]))
    a = math.radians(major_arcminutes / 120)
    b = math.radians(minor_arcminutes / 120)
    major = add(scale(north, math.cos(position_angle)), scale(east, math.sin(position_angle)))
    minor = add(scale(north, -math.sin(position_angle)), scale(east, math.cos(position_angle)))

    def direction_at(s):
        t = 2 * math.pi * s
        p = add(scale(major, a * math.cos(t)), scale(minor, b * math.sin(t)))
        length = math.sqrt(dot(p, p))
        return add(scale(c, math.cos(length)), scale(p, math.sin(length) / length))

    sampler = Sampler(sky, camera, label)
    pixels = sampler.run(direction_at, ELLIPSE_INITIAL_STEPS)
    detail = {"shape": "circle" if circle else "ellipse", "semiMajorAxisDegrees": major_arcminutes / 120,
              "semiMinorAxisDegrees": minor_arcminutes / 120,
              "j2000PositionAngleDegrees": math.degrees(position_angle)}
    return [pixels[:-1]], sampler, detail


def select_level(available, preferred):
    return min(available, key=lambda level: (abs(level - preferred), level))


def outline_rings(sky, camera, rings, label):
    """Each edge is the great-circle arc between consecutive J2000 vertices."""
    sampler = Sampler(sky, camera, label)
    result = []
    for ring in rings:
        vertices = [equatorial_unit(math.radians(ra), math.radians(dec)) for ra, dec in ring[:-1]]
        pixels = []
        for edge in range(len(vertices)):
            start, end = vertices[edge], vertices[(edge + 1) % len(vertices)]
            run = sampler.run(lambda s, a=start, b=end: slerp(a, b, s), 1)
            pixels.extend(run[:-1])
        result.append(pixels)
    return result, sampler, {"ringVertexCounts": [len(ring) - 1 for ring in rings]}


def plate_scale_bounds(sky, camera, row):
    """Smallest and largest pixels per radian of the projection at the centre over 360 tangent directions."""
    c = centre_of(row)
    north = normalize(add([0.0, 0.0, 1.0], scale(c, -c[2])))
    east = cross(north, c)
    step = 1e-6
    _, _, d0 = sky.horizontal(c)
    p0 = camera.project(d0)
    scales = []
    for degree in range(360):
        angle = math.radians(degree)
        tangent = add(scale(north, math.cos(angle)), scale(east, math.sin(angle)))
        _, _, d = sky.horizontal(add(scale(c, math.cos(step)), scale(tangent, math.sin(step))))
        scales.append(distance(camera.project(d), p0) / step)
    return min(scales), max(scales)


def rounded(pixels):
    return [[round(p[0], DECIMALS), round(p[1], DECIMALS)] for p in pixels]


def main():
    if len(sys.argv) != 4:
        sys.exit("usage: deep_sky_reference.py OBJECTS_CSV OUTLINE_POINTS_CSV OUTPUT_JSON")
    if not sys.flags.isolated:
        sys.exit("run with python -I")
    objects_path, outlines_path, output_path = sys.argv[1:]
    generator_path = os.path.abspath(__file__)
    objects = read_objects(objects_path)
    outlines = read_outlines(outlines_path)
    sky = Sky()
    cardinals = check_documented_zenith_cardinals()
    fisheye = Camera("equidistant-fisheye", 3500.0, 3500.0, 2000.0, 7000, 7000, 3400.0, 90, 0)

    def centred_on(object_id):
        altitude, azimuth, _ = sky.horizontal(centre_of(objects[object_id]))
        return Camera("perspective", 500.0, 500.0, 8000.0, 1000, 1000, None, math.degrees(altitude),
                      math.degrees(azimuth))

    cases = []

    def add_case(name, object_id, kind, camera, maximum_outlines, preferred_level):
        row = objects[object_id]
        altitude, azimuth, d = sky.horizontal(centre_of(row))
        case = {"name": name, "objectId": object_id, "kind": kind, "maximumOutlines": maximum_outlines,
                "preferredOutlineLevel": preferred_level, "camera": camera.describe(),
                "centerAltitudeDegrees": math.degrees(altitude), "centerAzimuthDegrees": math.degrees(azimuth),
                "centerPixel": camera.project(d)}
        if not camera.admits(case["centerPixel"]):
            sys.exit(f"{name}: the centre is not admitted")
        if kind == "footprint":
            rings, sampler, detail = ellipse_rings(sky, camera, row, name)
        elif kind == "outline":
            level = select_level(sorted(outlines[object_id]), preferred_level)
            rings, sampler, detail = outline_rings(sky, camera, outlines[object_id][level], name)
            detail = {"level": level, **detail}
        else:
            low, high = plate_scale_bounds(sky, camera, row)
            major = math.radians(float(row["major_axis_arcminutes"]) / 60)
            rings, sampler, detail = [], None, {"pixelsPerRadianMinimum": low, "pixelsPerRadianMaximum": high,
                                                "majorAxisPixelsMinimum": low * major,
                                                "majorAxisPixelsMaximum": high * major}
        case.update(detail)
        if sampler is not None:
            case["evaluations"] = sampler.evaluations
            case["worstQuarterPointSagPixels"] = sampler.worst_quarter_sag
        case["rings"] = [rounded(ring) for ring in rings]
        cases.append(case)

    add_case("ngc0224-ellipse-perspective-centred", "NGC0224", "footprint", centred_on("NGC0224"), 64, 1)
    add_case("ngc0224-ellipse-fisheye-off-axis", "NGC0224", "footprint", fisheye, 64, 1)
    add_case("mel022-outline-level1-perspective-centred", "Mel022", "outline", centred_on("Mel022"), 64, 1)
    add_case("mel022-outline-level1-fisheye-off-axis", "Mel022", "outline", fisheye, 64, 1)
    add_case("mel022-circle-outline-limit-perspective-centred", "Mel022", "footprint", centred_on("Mel022"), 0, 1)
    add_case("mel022-circle-outline-limit-fisheye-off-axis", "Mel022", "footprint", fisheye, 0, 1)
    add_case("ngc1976-outline-level3-perspective-centred", "NGC1976", "outline", centred_on("NGC1976"), 64, 3)
    add_case("ngc1976-outline-level3-fisheye-off-axis", "NGC1976", "outline", fisheye, 64, 3)
    add_case("ngc1976-glyph-outline-limit-perspective-centred", "NGC1976", "glyph", centred_on("NGC1976"), 0, 1)
    add_case("ngc1976-glyph-outline-limit-fisheye-off-axis", "NGC1976", "glyph", fisheye, 0, 1)

    erfa_library = None
    try:
        erfa_library = erfa.version.erfa_version
    except AttributeError:
        pass
    document = {
        "schema": SCHEMA,
        "provenance": {
            "generator": os.path.basename(generator_path),
            "generatorSha256": sha256(generator_path),
            "python": platform.python_version(),
            "pyerfa": erfa.__version__,
            "erfa": erfa_library,
            "numpy": numpy.__version__,
            "inputs": [{"file": os.path.basename(objects_path), "sha256": sha256(objects_path)},
                       {"file": os.path.basename(outlines_path), "sha256": sha256(outlines_path)}],
            "model": "erfa.pmat76 (IAU 1976 precession, J2000 to mean of date, at TT from erfa.utctai and "
                     "erfa.taitt); erfa.gmst82 with UT1 = UTC plus east longitude; erfa.hd2ae; no nutation, "
                     "aberration or refraction",
            "sampling": {"maximumSagPixels": MAXIMUM_SAG_PIXELS, "maximumChordPixels": MAXIMUM_CHORD_PIXELS,
                         "ellipseInitialSteps": ELLIPSE_INITIAL_STEPS, "decimals": DECIMALS},
        },
        "site": {"latitudeDegrees": LATITUDE_DEGREES, "longitudeDegrees": LONGITUDE_DEGREES,
                 "elevationMeters": ELEVATION_METERS},
        "utc": UTC_TEXT,
        "terrestrialTimeJulianDate": list(sky.tt),
        "greenwichMeanSiderealRadians": sky.gmst_radians,
        "documentedZenithCardinals": cardinals,
        "cases": cases,
    }
    # One pixel pair per line keeps the file reviewable and its diffs local.
    text = re.sub(r"\[\s+(-?[0-9.eE+-]+),\s+(-?[0-9.eE+-]+)\s+\]", r"[\1,\2]",
                  json.dumps(document, indent=1, allow_nan=False))
    with open(output_path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text + "\n")
    total = sum(len(ring) for case in cases for ring in case["rings"])
    print(f"wrote {output_path}: {len(cases)} cases, {total} ring points, "
          f"generator {document['provenance']['generatorSha256']}")
    for case in cases:
        print(f"  {case['name']}: centre {case['centerAltitudeDegrees']:.4f} alt "
              f"{case['centerAzimuthDegrees']:.4f} az pixel {case['centerPixel'][0]:.3f},{case['centerPixel'][1]:.3f} "
              f"rings {[len(ring) for ring in case['rings']]} evaluations {case.get('evaluations')} "
              f"worst quarter sag {case.get('worstQuarterPointSagPixels')}")


if __name__ == "__main__":
    main()
