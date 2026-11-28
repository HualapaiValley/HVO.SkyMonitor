using System.Globalization;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

public sealed partial class SmallBodyGoldenFixtureTests
{
    private static readonly (string Wire, string Normalized)[] CommonColumns =
    [
        ("R.A.___(ICRF)", "astrometricRaDeg"), ("DEC____(ICRF)", "astrometricDecDeg"),
        ("R.A.__(a-app)", "apparentRaDeg"), ("DEC___(a-app)", "apparentDecDeg"),
        ("delta", "rangeAu"), ("deldot", "rangeRateKmPerSec"), ("S-O-T", "solarElongationDeg"),
        ("S-T-O", "phaseAngleDeg"), ("RA_3sigma", "ra3SigmaArcsec"), ("DEC_3sigma", "dec3SigmaArcsec")
    ];

    private static readonly string[] EopCoverageFields = ["dataBasedStartDate", "dataBasedEndDate", "predictsEndDate"];

    [TestMethod]
    public void SyntheticProviderRowsAndIndependentHalfStepsAgreeWithBothSnapshots()
    {
        using var first = Open("snapshots/payload-v1.json");
        using var second = Open("snapshots/payload-v2.json");
        using var truth = Open("analytical/half-step.json");
        var observer = first.RootElement.GetProperty("observerBinding");
        var astronomicalUnitKm = Num(truth.RootElement, "astronomicalUnitKm");
        Assert.AreEqual(3600, Int(truth.RootElement, "arcsecondsPerDegree"));
        var asteroid = Series(first.RootElement, AsteroidSpkId);
        var asteroidTruth = truth.RootElement.GetProperty("asteroid");
        foreach (var vector in Items(asteroidTruth, "timestampVariants"))
        {
            var wire = ObserverTable.Read(Str(vector, "path")!);
            Assert.AreEqual(vector.GetProperty("fractional").GetBoolean(), wire.Fractional);
            var misaligned = wire.GridDrift(asteroid).ToArray();
            if (vector.GetProperty("accepted").GetBoolean())
            {
                Assert.HasCount(0, misaligned);
                ValidateProvider(wire, asteroid, asteroidTruth, observer, astronomicalUnitKm, "APmag", "S-brt");
                continue;
            }

            Assert.HasCount(1, misaligned);
            var (index, requested, drift) = misaligned.Single();
            Assert.AreEqual(Int(vector, "misalignedRowIndex"), index);
            Assert.AreEqual(Instant(vector, "requestedSampleUtc"), requested);
            Assert.AreEqual((long)Int(vector, "driftMilliseconds"), drift.Ticks / TimeSpan.TicksPerMillisecond);
            var wholeSeconds = ObserverTable.Read("providers/asteroid.horizons.json");
            CollectionAssert.AreEqual(wholeSeconds.Rows[index][1..], wire.Rows[index][1..]);
        }

        var comet = Series(first.RootElement, CometSpkId);
        var cometTruth = truth.RootElement.GetProperty("comet");
        ValidateProvider(ObserverTable.Read("providers/comet.horizons.json"), comet, cometTruth, observer, astronomicalUnitKm, "T-mag", "N-mag");
        var cometKnots = Items(comet, "knots");
        Assert.IsGreaterThan(359d, Num(cometKnots[1], "astrometricRaDeg"));
        Assert.IsLessThan(1d, Num(cometKnots[2], "astrometricRaDeg"));

        var refreshed = Series(second.RootElement, AsteroidSpkId);
        ValidateProvider(ObserverTable.Read("providers/asteroid.horizons-refreshed.json"), refreshed, asteroidTruth, observer, astronomicalUnitKm, "APmag", "S-brt");
        foreach (var (before, after) in Items(asteroid, "knots").Zip(Items(refreshed, "knots")))
            Assert.AreEqual(0.1, Num(after, "predictedTotalMagnitude") - Num(before, "predictedTotalMagnitude"), 1e-12);
    }

    [TestMethod]
    public void SyntheticIdentityAndOrbitMetadataPreserveProviderPrecedenceAndCivilTime()
    {
        using var payload = Open("snapshots/payload-v1.json");
        ValidateCompanionMetadata(Series(payload.RootElement, AsteroidSpkId), "asteroid",
            "AsteroidHG", [("H", "absoluteMagnitudeH"), ("G", "slopeParameterG")]);
        ValidateCompanionMetadata(Series(payload.RootElement, CometSpkId), "comet",
            "CometM1K1M2K2", [("M1", "totalMagnitudeM1"), ("K1", "totalSlopeK1"), ("M2", "nuclearMagnitudeM2"),
                ("K2", "nuclearSlopeK2"), ("PHCOF", "phaseCoefficient")]);

        using var document = Open("providers/identity-cases.json");
        var cases = document.RootElement;
        var ambiguous = cases.GetProperty("ambiguous");
        var lookup = ambiguous.GetProperty("lookup");
        var rows = Items(lookup, "result");
        var expected = ambiguous.GetProperty("expected");
        Assert.AreEqual("AmbiguousDesignation", Str(expected, "reasonCode"));
        Assert.IsNull(Str(expected, "selectedSpkId"));
        Assert.AreEqual(Int(lookup, "count"), rows.Length);
        var candidates = rows.Select(row => Str(row, "spkid")).Distinct().ToArray();
        Assert.IsTrue(candidates.Length is > 1 and <= 32);
        CollectionAssert.AreEqual(StringArray(expected, "candidateSpkIds"), candidates);
        var parents = rows.Where(row => Str(row, "pdes") == Str(ambiguous, "query")).ToArray();
        Assert.HasCount(2, parents);
        Assert.AreNotEqual(Int(parents[0], "apparitionYear"), Int(parents[1], "apparitionYear"));
        Assert.AreEqual("C", Str(rows.Single(row => Str(row, "fragment") != null), "fragment"));

        foreach (var date in Items(cases, "solutionDates"))
            Assert.AreEqual(Str(date, "utc"), SolutionUtc(Str(date, "raw")!, Str(date, "zone")!));

        foreach (var check in Items(cases, "solutionCrossChecks"))
        {
            var h = check.GetProperty("horizons");
            var s = check.GetProperty("sbdb");
            var matches = Str(h, "spkId") == Str(s, "spkId") &&
                Str(h, "sourceToken") == Str(s, "source")!.ToUpperInvariant() + "#" + Str(s, "orbitId") &&
                NormalizeSolutionDate(Str(h, "solnDate")!) == Str(s, "solnDate");
            Assert.AreEqual(Str(check, "reasonCode"), matches ? null : "SolutionMetadataMismatch", Str(check, "name"));
        }
    }

    private static void ValidateProvider(ObserverTable wire, JsonElement series, JsonElement truth, JsonElement observer,
        double astronomicalUnitKm, string totalColumn, string nuclearColumn)
    {
        Assert.HasCount(0, wire.GridDrift(series).ToArray());
        Assert.AreEqual("HVO SYNTHETIC OBSERVER FIXTURE", wire.Header[0]);
        Assert.AreEqual(Str(series, "horizonsApiVersion"), wire.Version);
        var site = wire.HeaderValue("Center geodetic: ").Split(", ", StringSplitOptions.None).Select(ParseNumber).ToArray();
        CollectionAssert.AreEqual(new[] { Num(observer, "longitudeDegrees"), Num(observer, "latitudeDegrees"),
            Num(observer, "elevationMeters") / 1000 }, site);
        Assert.IsTrue(wire.HeaderValue("Center-site name: ").EndsWith("(coord@399)", StringComparison.Ordinal));
        var solution = series.GetProperty("solution");
        var identity = series.GetProperty("identity");
        Assert.AreEqual($"{Str(identity, "fullName")} {{source: {Str(solution, "orbitId")}}}",
            wire.HeaderValue("Target body name: "));
        Assert.AreEqual(Str(solution, "solutionDateSourceRaw"), NormalizeSolutionDate(wire.HeaderValue("Soln.date: ")));
        var rec = Str(solution, "horizonsRecordNumber") + (solution.GetProperty("hasCovariance").GetBoolean() ? " (+COV)" : "");
        Assert.AreEqual(rec, wire.HeaderValue("Rec #: "));
        Assert.AreEqual(Str(solution, "planetaryEphemeris"), wire.HeaderValue("Planetary ephemeris: "));
        Assert.AreEqual(Str(solution, "smallBodyPerturbers"), wire.HeaderValue("Small-body perturbers: "));
        Assert.AreEqual(Str(solution, "horizonsEopFile"), wire.HeaderValue("EOP file        : "));
        var coverage = wire.HeaderValue("EOP coverage    : DATA-BASED ").Split([" TO ", ". PREDICTS-> "], StringSplitOptions.None);
        var normalizedCoverage = EopCoverageFields
            .Select(field => Str(solution.GetProperty("horizonsEopCoverage"), field)).ToArray();
        CollectionAssert.AreEqual(normalizedCoverage, coverage.Select(date => DateOnly.ParseExact(date, "yyyy-MMM-dd",
            CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray());
        var ng = solution.GetProperty("nonGravitationalModel");
        if (ng.ValueKind == JsonValueKind.Object)
        {
            foreach (var parameter in ng.EnumerateObject())
            {
                var fields = wire.HeaderValue(parameter.Name + "= ").Split(" [", StringSplitOptions.None);
                Assert.AreEqual(Num(parameter.Value, "value"), ParseNumber(fields[0]));
                Assert.AreEqual(Str(parameter.Value, "unit") + "]", fields[1]);
            }
        }

        var knots = Items(series, "knots");
        Assert.HasCount(2 * knots.Length - 1, wire.Rows);
        for (var i = 0; i < knots.Length; i++)
        {
            var row = 2 * i;
            var knot = knots[i];
            Assert.AreEqual(Instant(knot, "utc"), wire.Utc(row));
            foreach (var (column, field) in CommonColumns)
                VerifyCell(knot, field, wire.Cell(row, column));
            VerifyCell(knot, "predictedTotalMagnitude", wire.Cell(row, totalColumn));
            VerifyCell(knot, "predictedNuclearOrSurface", wire.Cell(row, nuclearColumn));
            Assert.AreEqual(Str(knot, "solarElongationSide"), wire.Cell(row, "/r") == "/T" ? "Trailing" : "Leading");
            Assert.IsTrue(double.IsFinite(Num(knot, "predictedTotalMagnitude")));
            if (totalColumn == "APmag")
            {
                Assert.IsTrue(Num(knot, "ra3SigmaArcsec") > 0 && Num(knot, "dec3SigmaArcsec") > 0);
            }
            else
            {
                Assert.IsTrue(double.IsFinite(Num(knot, "predictedNuclearOrSurface")));
                Assert.AreEqual("n.a.", wire.Cell(row, "RA_3sigma"));
                Assert.AreEqual("n.a.", wire.Cell(row, "DEC_3sigma"));
            }
        }
        VerifyHalfSteps(wire, series, truth, astronomicalUnitKm);
    }

    private static void VerifyHalfSteps(ObserverTable wire, JsonElement series, JsonElement truth, double astronomicalUnitKm)
    {
        var knots = Items(series, "knots");
        var midpoints = Items(truth, "midpoints");
        var step = Int(series, "stepSeconds");
        Assert.AreEqual(Int(truth, "stepSeconds"), step);
        Assert.HasCount(knots.Length - 3, midpoints);
        Assert.AreEqual("HalfStepSample", Str(series, "interpolationVerificationMethod"));
        var trajectory = truth.GetProperty("trajectory");
        var origin = Vector(trajectory, "originAu");
        var delta = Vector(trajectory, "deltaAuPerStep");
        var epoch = Instant(truth, "referenceUtc");
        var worst = 0d;
        foreach (var (frame, raColumn, decColumn) in new[]
        {
            ("astrometric", "R.A.___(ICRF)", "DEC____(ICRF)"), ("apparent", "R.A.__(a-app)", "DEC___(a-app)")
        })
        {
            Direction Model(DateTimeOffset utc)
            {
                var position = origin + delta.Scale((utc - epoch).TotalSeconds / step);
                return frame == "astrometric" ? position : ApplyApparentRotation(position,
                    Num(trajectory, "rotateXDegrees"), Num(trajectory, "rotateZDegrees"));
            }

            // Verify the wire samples are direct trajectory evaluations, including odd rows.
            for (var row = 0; row < wire.Rows.Length; row++)
            {
                var sample = WireDirection(row);
                var model = Model(wire.Utc(row));
                Assert.IsLessThan(3e-6, Angle(sample, model), $"{frame}, row {row}");
                Assert.AreEqual(model.Length(), ParseNumber(wire.Cell(row, "delta")), 6e-13);
                if (frame == "astrometric")
                {
                    var rangeRate = model.Dot(delta) / model.Length() * astronomicalUnitKm / step;
                    Assert.AreEqual(rangeRate, ParseNumber(wire.Cell(row, "deldot")), 6e-7);
                }
            }

            for (var i = 0; i < midpoints.Length; i++)
            {
                var midpoint = midpoints[i];
                var t = Instant(midpoint, "utc");
                var row = 2 * i + 3;
                Assert.AreEqual(Instant(knots[i + 1], "utc").AddSeconds(step / 2), t);
                Assert.AreEqual(t, wire.Utc(row));
                Assert.AreEqual(Num(midpoint, frame + "ReferenceRaDeg"), ParseNumber(wire.Cell(row, raColumn)));
                Assert.AreEqual(Num(midpoint, frame + "ReferenceDecDeg"), ParseNumber(wire.Cell(row, decColumn)));
                var interpolated = Interpolate(WireDirection(row - 3), WireDirection(row - 1), WireDirection(row + 1), WireDirection(row + 3), 0.5);
                var residual = Angle(interpolated, WireDirection(row));
                Assert.AreEqual(Num(midpoint, frame + "ResidualArcsec"), residual, 2e-10);
                worst = Math.Max(worst, residual);

                if (truth.TryGetProperty("unroundedMidpointResidualArcsec", out var analyticalResidual))
                {
                    var exact = Interpolate(Model(wire.Utc(row - 3)).Unit(), Model(wire.Utc(row - 1)).Unit(),
                        Model(wire.Utc(row + 1)).Unit(), Model(wire.Utc(row + 3)).Unit(), 0.5);
                    var storedVsExact = frame == "astrometric" ? "storedKnotsVsUnroundedAstrometricArcsec" : "storedKnotsVsUnroundedApparentArcsec";
                    Assert.AreEqual(Num(truth, storedVsExact), Angle(interpolated, Model(t)), 2e-10);
                    Assert.AreEqual(analyticalResidual.GetDouble(), Angle(exact, Model(t)), 2e-10);
                    worst = Math.Max(worst, Angle(exact, Model(t)));
                }
            }

            Direction WireDirection(int row) =>
                Direction.FromAngles(ParseNumber(wire.Cell(row, raColumn)), ParseNumber(wire.Cell(row, decColumn)));
        }

        var declared = Num(series, "maxVerifiedMidpointResidualArcsec");
        Assert.IsTrue(declared >= worst && declared <= 1);
        Assert.AreEqual(Math.Ceiling(worst * 1e6) / 1e6, declared);
    }

    private static void ValidateCompanionMetadata(JsonElement series, string name, string modelName, (string Wire, string Normalized)[] photometry)
    {
        using var sbdb = Open($"providers/{name}.sbdb.json");
        using var lookup = Open($"providers/{name}.lookup.json");
        Assert.AreEqual(Str(series, "sbdbApiVersion"), Str(sbdb.RootElement.GetProperty("signature"), "version"));
        Assert.AreEqual(Str(series, "lookupApiVersion"), Str(lookup.RootElement.GetProperty("signature"), "version"));
        var obj = sbdb.RootElement.GetProperty("object");
        var match = Items(lookup.RootElement, "result").Single();
        var identity = series.GetProperty("identity");
        var designation = Str(obj, "des");
        Assert.AreEqual(designation, Str(identity, "primaryDesignation"));
        Assert.AreEqual(Str(obj, "spkid"), Str(identity, "spkId"));
        Assert.AreEqual(Str(match, "spkid"), Str(identity, "spkId"));
        Assert.AreEqual(Str(obj, "fullname"), Str(identity, "fullName"));
        Assert.AreEqual(Str(obj.GetProperty("orbit_class"), "code"), Str(identity, "orbitClassCode"));
        Assert.AreEqual(Str(obj, "kind") == "an" ? "Asteroid" : "Comet", Str(identity, "bodyKind"));
        var sbdbAliases = Items(obj, "des_alt").SelectMany(alternate => alternate.EnumerateObject())
            .Select(designation => designation.Value.GetString());
        var aliases = sbdbAliases.Concat(StringArray(match, "alias")).Append(Str(match, "pdes"))
            .Where(value => value != designation).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(aliases, StringArray(identity, "aliases"));
        if (name == "asteroid")
            Assert.AreNotEqual(designation, Str(match, "pdes"));

        var orbit = sbdb.RootElement.GetProperty("orbit");
        var solution = series.GetProperty("solution");
        Assert.AreEqual(Str(orbit, "source")!.ToUpperInvariant() + "#" + Str(orbit, "orbit_id"), Str(solution, "orbitId"));
        Assert.AreEqual(Str(orbit, "soln_date"), Str(solution, "solutionDateSourceRaw"));
        Assert.AreEqual(SolutionUtc(Str(orbit, "soln_date")!, Str(solution, "solutionDateSourceTimeZone")!), Str(solution, "solutionDateUtc"));
        Assert.AreEqual(Str(orbit, "first_obs"), Str(solution, "firstObsDateRaw"));
        Assert.AreEqual(Str(orbit, "last_obs"), Str(solution, "lastObsDateRaw"));
        Assert.AreEqual(ParseNumber(Str(orbit, "epoch")!), Num(solution, "epochTdbJd"));

        var magnitude = solution.GetProperty("magnitudeModel");
        Assert.AreEqual(modelName, Str(magnitude, "kind"));
        var physical = Items(sbdb.RootElement, "phys_par");
        foreach (var (field, normalized) in photometry)
        {
            var parameter = physical.Single(item => Str(item, "name") == field);
            Assert.IsFalse(parameter.TryGetProperty("ref", out _));
            Assert.AreEqual(ParseNumber(Str(parameter, "value")!), Num(magnitude, normalized));
        }
        var ng = solution.GetProperty("nonGravitationalModel");
        if (name == "comet")
        {
            Assert.AreEqual("1919-??-??", Str(solution, "firstObsDateRaw"));
            Assert.AreEqual("2026-01-??", Str(solution, "lastObsDateRaw"));
            foreach (var param in Items(orbit, "model_pars"))
            {
                var normalized = ng.GetProperty(Str(param, "name")!);
                Assert.AreEqual(ParseNumber(Str(param, "value")!), Num(normalized, "value"));
                Assert.AreEqual(Str(param, "units"), Str(normalized, "unit"));
            }
        }
        else
        {
            Assert.AreEqual(JsonValueKind.Null, ng.ValueKind);
        }
    }

    private static Direction Vector(JsonElement value, string field)
    {
        var xyz = Items(value, field);
        return new(xyz[0].GetDouble(), xyz[1].GetDouble(), xyz[2].GetDouble());
    }

    private static Direction ApplyApparentRotation(Direction p, double xDegrees, double zDegrees)
    {
        var (sx, cx) = Math.SinCos(xDegrees * RadiansPerDegree);
        var (sz, cz) = Math.SinCos(zDegrees * RadiansPerDegree);
        var y = p.Y * cx - p.Z * sx;
        return new(p.X * cz - y * sz, p.X * sz + y * cz, p.Y * sx + p.Z * cx);
    }

    private static void VerifyCell(JsonElement knot, string field, string value)
    {
        if (value == "n.a.")
            Assert.AreEqual(JsonValueKind.Null, knot.GetProperty(field).ValueKind, field);
        else
            Assert.AreEqual(ParseNumber(value), Num(knot, field), field);
    }

    private static string? SolutionUtc(string raw, string zone)
    {
        Assert.AreEqual("America/Los_Angeles", zone);
        var pasadena = TimeZoneInfo.FindSystemTimeZoneById(zone);
        var local = DateTime.ParseExact(raw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None);
        return pasadena.IsAmbiguousTime(local) ? null :
            TimeZoneInfo.ConvertTimeToUtc(local, pasadena).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static string NormalizeSolutionDate(string raw) =>
        DateTime.ParseExact(raw, "yyyy-MMM-dd_HH:mm:ss", CultureInfo.InvariantCulture)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static double ParseNumber(string raw) => double.Parse(raw, CultureInfo.InvariantCulture);

    // This reads the synthetic fixture grammar for evidence checks. The S1 parser must
    // implement the actual provider grammar, version checks and fail-closed behavior.
    private sealed record ObserverTable(string Version, string[] Header, string[] Columns, string[][] Rows)
    {
        public bool Fractional => Columns[0].EndsWith(".fff", StringComparison.Ordinal);

        public string HeaderValue(string prefix) =>
            Header.Single(line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];

        public string Cell(int row, string column) => Rows[row][Array.IndexOf(Columns, column)];

        public DateTimeOffset Utc(int row) =>
            DateTimeOffset.ParseExact(Rows[row][0], Fractional ? "yyyy-MMM-dd HH:mm:ss.fff" : "yyyy-MMM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        public IEnumerable<(int Row, DateTimeOffset Requested, TimeSpan Drift)> GridDrift(JsonElement series)
        {
            var start = Instant(series, "paddedStartUtc");
            var seconds = Int(series, "stepSeconds") / 2;
            for (var row = 0; row < Rows.Length; row++)
            {
                var requested = start.AddSeconds(row * seconds);
                var drift = Utc(row) - requested;
                if (drift != TimeSpan.Zero)
                    yield return (row, requested, drift);
            }
        }

        public static ObserverTable Read(string path)
        {
            using var document = Open(path);
            var signature = document.RootElement.GetProperty("signature");
            Assert.AreEqual("HVO synthetic Horizons-format fixture", Str(signature, "source"));
            var lines = Str(document.RootElement, "result")!.Split('\n');
            var start = Array.IndexOf(lines, "$$SOE");
            var end = Array.IndexOf(lines, "$$EOE");
            Assert.IsTrue(start > 0 && end > start);
            var columns = lines[start - 1].Split(',');
            var rows = lines[(start + 1)..end].Select(line => line.Split(',')).ToArray();
            Assert.IsTrue(rows.All(row => row.Length == columns.Length));
            Assert.IsTrue(columns[0] is "Date__(UT)__HR:MN:SC" or "Date__(UT)__HR:MN:SC.fff");
            return new(Str(signature, "version")!, lines[..(start - 1)], columns, rows);
        }
    }
}
