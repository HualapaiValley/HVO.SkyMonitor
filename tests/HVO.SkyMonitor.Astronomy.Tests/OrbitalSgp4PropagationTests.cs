using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy.Orbital.Propagation;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalSgp4PropagationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void PortableAfspcPack_All32CasesAnd641Rows_MeetDeclaredVectorNormGates()
    {
        var bytes = File.ReadAllBytes(ReferencePath("vallado-afspc-vectors.json"));
        Assert.AreEqual("2AE742B494FE4106C6373D4B63FCE82E3A85E8A22AC7CFB05478A5D12BAFF408",
            Convert.ToHexString(SHA256.HashData(bytes)));
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.AreEqual("WGS72", root.GetProperty("gravity").GetString());
        Assert.AreEqual("a", root.GetProperty("operationMode").GetString());
        var cases = 0;
        var rows = 0;
        var successful = 0;
        var expectedErrors = 0;
        double maximumPosition = 0, maximumVelocity = 0;
        string worstPosition = "", worstVelocity = "";
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            cases++;
            var tle = item.GetProperty("tle");
            var record = FromTle(tle.GetProperty("line1").GetString()!, tle.GetProperty("line2").GetString()!);
            var created = Sgp4ObjectPropagator.TryCreate(record, out var propagator, out var creationFailure);
            foreach (var sample in item.GetProperty("samples").EnumerateArray())
            {
                rows++;
                var minutes = sample.GetProperty("minutesSinceEpoch").GetDouble();
                var expectedError = sample.GetProperty("expectedError").GetInt32();
                var label = $"{item.GetProperty("catalogNumber").GetString()} at {minutes:R} min";
                if (expectedError != 0)
                {
                    Assert.AreEqual("33334", record.ObjectIdentity.CatalogNumber);
                    Assert.AreEqual(3, expectedError);
                    Assert.IsFalse(created, label);
                    Assert.IsNull(propagator);
                    Assert.AreEqual(new(Sgp4FailureKind.Initialization, 3), creationFailure, label);
                    expectedErrors++;
                    continue;
                }

                Assert.IsTrue(created, $"{label}: {creationFailure}");
                Assert.IsNotNull(propagator);
                Assert.IsTrue(propagator.TryPropagateMinutes(minutes, out var actual, out var failure), $"{label}: {failure}");
                Assert.IsNotNull(actual);
                Assert.AreEqual(default, failure);
                var position = Residual(actual.PositionKilometers, sample.GetProperty("positionKm"));
                var velocity = Residual(actual.VelocityKilometersPerSecond, sample.GetProperty("velocityKmPerSecond"));
                if (position > maximumPosition) { maximumPosition = position; worstPosition = label; }
                if (velocity > maximumVelocity) { maximumVelocity = velocity; worstVelocity = label; }
                Assert.IsTrue(position <= 1e-6, $"{label}: position norm {position:R} km");
                Assert.IsTrue(velocity <= 1e-8, $"{label}: velocity norm {velocity:R} km/s");
                successful++;
            }
        }

        TestContext.WriteLine($"WGS72 / AFSPC a: {cases} cases, {rows} rows, {successful} finite states, {expectedErrors} expected errors.");
        TestContext.WriteLine($"Maximum position norm {maximumPosition:R} km: {worstPosition}.");
        TestContext.WriteLine($"Maximum velocity norm {maximumVelocity:R} km/s: {worstVelocity}.");
        Assert.AreEqual(32, cases);
        Assert.AreEqual(641, rows);
        Assert.AreEqual(640, successful);
        Assert.AreEqual(1, expectedErrors);
    }

    [TestMethod]
    [DataRow("00005")]
    [DataRow("04632")]
    [DataRow("08195")]
    [DataRow("24208")]
    [DataRow("25954")]
    public void FractionalNegativeRepeatedReorderedAndConcurrentEvaluations_AgreeExactlyWithFreshState(string catalog)
    {
        var record = ReferenceRecord(catalog);
        Assert.IsTrue(Sgp4ObjectPropagator.TryCreate(record, out var shared, out _));
        double[] minutes = [1440.125, -720.25, 0, 60.1234567, 1440.125, -0.0000001, 2880, -1440.75, 0];
        var expected = minutes.Select(m =>
        {
            Assert.IsTrue(Sgp4ObjectPropagator.TryCreate(record, out var fresh, out _));
            Assert.IsTrue(fresh.TryPropagateMinutes(m, out var state, out var failure), failure.ToString());
            return state;
        }).ToArray();
        for (var round = 0; round < 3; round++)
        {
            for (var i = minutes.Length - 1; i >= 0; i--)
            {
                Assert.IsTrue(shared.TryPropagateMinutes(minutes[i], out var state, out _));
                Assert.AreEqual(expected[i], state);
            }
        }
        Parallel.For(0, 90, i =>
        {
            var index = i % minutes.Length;
            Assert.IsTrue(shared.TryPropagateMinutes(minutes[index], out var state, out _));
            Assert.AreEqual(expected[index], state);
        });
    }

    [TestMethod]
    [DataRow("100000")]
    [DataRow("999999999")]
    public void DirectGpCoefficientsAndModernCatalogText_ArePreserved(string catalog)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "orbital", "contract-v1", "derivative-coefficients.json")));
        var expected = document.RootElement;
        Assert.AreEqual(OrbitalContract.GpDerivativeConvention, expected.GetProperty("convention").GetString());
        var record = OrbitalContractExamples.Record(catalog);
        record = OrbitalContract.CreateRecord(record with
        {
            Elements = record.Elements with
            {
                MeanMotionDot = expected.GetProperty("meanMotionDotGp").GetDouble(),
                MeanMotionDdot = expected.GetProperty("meanMotionDdotGp").GetDouble()
            }
        });
        Assert.IsTrue(Sgp4ObjectPropagator.TryCreate(record, out var actual, out _));
        Assert.AreEqual(catalog, actual.CatalogNumber);
        Assert.AreEqual(expected.GetProperty("expectedNdotRadiansPerMinuteSquared").GetDouble(),
            actual.MeanMotionDotRadiansPerMinuteSquared, 1e-25);
        Assert.AreEqual(expected.GetProperty("expectedNddotRadiansPerMinuteCubed").GetDouble(),
            actual.MeanMotionDdotRadiansPerMinuteCubed, 1e-30);
        Assert.IsTrue(actual.TryPropagate(record.EpochUtc.AddMinutes(1), out var state, out _));
        Assert.IsNotNull(state);
    }

    [TestMethod]
    public void SplitEpochAndUtcTicks_PreserveSubmillisecondPrecisionAndOffsets()
    {
        var epoch = new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        var record = OrbitalContract.CreateRecord(OrbitalContractExamples.Record() with { EpochUtc = epoch });
        Assert.IsTrue(Sgp4ObjectPropagator.TryCreate(record, out var propagator, out _));
        Assert.AreEqual(2451544.5, propagator.EpochJulianDay);
        Assert.AreEqual((TimeSpan.TicksPerDay / 2 + 1234567) / (double)TimeSpan.TicksPerDay,
            propagator.EpochJulianDayFraction);
        Assert.AreNotEqual(Math.Round(propagator.EpochJulianDayFraction * 86400000d)
            / 86400000d, propagator.EpochJulianDayFraction);
        var target = epoch.AddTicks(-9876543);
        Assert.IsTrue(propagator.TryPropagate(target.ToOffset(TimeSpan.FromHours(7)), out var utcState, out _));
        Assert.IsTrue(propagator.TryPropagateMinutes(-9876543 / (double)TimeSpan.TicksPerMinute, out var minuteState, out _));
        Assert.AreEqual(minuteState, utcState);
    }

    [TestMethod]
    [DataRow(1, 1, 1, 1721425.5)]
    [DataRow(1600, 3, 1, 2305507.5)]
    [DataRow(1900, 3, 1, 2415079.5)]
    [DataRow(2000, 3, 1, 2451604.5)]
    [DataRow(2100, 3, 1, 2488128.5)]
    [DataRow(9999, 12, 31, 5373483.5)]
    public void GregorianEpochMidnight_IsIndependentOfVendorJdayYearLimits(int year, int month, int day, double expected)
    {
        var epoch = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
        var record = OrbitalContract.CreateRecord(OrbitalContractExamples.Record() with { EpochUtc = epoch });
        Assert.IsTrue(Sgp4ObjectPropagator.TryCreate(record, out var propagator, out _));
        Assert.AreEqual(expected, propagator.EpochJulianDay);
        Assert.AreEqual(0, propagator.EpochJulianDayFraction);
    }

    [TestMethod]
    public void EpochInitializationError_IsRejectedDespiteCoreReturningTrue()
    {
        var record = ReferenceRecord("33334");
        var raw = RawElements(record);
        Assert.IsTrue(SGP4.sgp4init('a', raw));
        Assert.AreEqual(3, raw.error);
        Assert.IsFalse(Sgp4ObjectPropagator.TryCreate(record, out var result, out var failure));
        Assert.IsNull(result);
        Assert.AreEqual(new(Sgp4FailureKind.Initialization, 3), failure);
    }

    [TestMethod]
    public void MeanMotionUnderflowAtInitialization_ReturnsCoreErrorTwo()
    {
        var record = OrbitalContractExamples.Record();
        record = OrbitalContract.CreateRecord(record with
        {
            Elements = record.Elements with { MeanMotionRevolutionsPerDay = double.Epsilon }
        });
        Assert.IsFalse(Sgp4ObjectPropagator.TryCreate(record, out var result, out var failure));
        Assert.IsNull(result);
        Assert.AreEqual(new(Sgp4FailureKind.Initialization, 2), failure);
    }

    [TestMethod]
    [DataRow("28872", 60.0, 6)]
    [DataRow("29141", 440.0, 6)]
    [DataRow("33333", 25.0, 4)]
    public void LaterCoreFailures_ReturnExactCodeAndDiscardVectors(string catalog, double minutes, int expectedError)
    {
        var record = ReferenceRecord(catalog);
        var raw = RawElements(record);
        Assert.IsTrue(SGP4.sgp4init('a', raw));
        Assert.AreEqual(0, raw.error);
        var r = new double[3];
        var v = new double[3];
        Assert.IsFalse(SGP4.sgp4(raw, minutes, r, v));
        Assert.AreEqual(expectedError, raw.error);
        if (expectedError == 6)
        {
            Assert.IsTrue(r.All(double.IsFinite));
            Assert.IsTrue(v.All(double.IsFinite));
            Assert.IsTrue(r.Any(x => x != 0));
        }
        Assert.IsTrue(Sgp4ObjectPropagator.TryCreate(record, out var propagator, out _));
        Assert.IsFalse(propagator.TryPropagateMinutes(minutes, out var result, out var failure));
        Assert.IsNull(result);
        Assert.AreEqual(new(Sgp4FailureKind.Propagation, expectedError), failure);
        Assert.IsTrue(propagator.TryPropagateMinutes(0, out var epochState, out _));
        Assert.IsNotNull(epochState);
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void NonfiniteTime_IsRejectedWithoutState(double minutes)
    {
        Assert.IsTrue(Sgp4ObjectPropagator.TryCreate(OrbitalContractExamples.Record(), out var propagator, out _));
        Assert.IsFalse(propagator.TryPropagateMinutes(minutes, out var result, out var failure));
        Assert.IsNull(result);
        Assert.AreEqual(new(Sgp4FailureKind.NonFiniteTime, 0), failure);
    }

    [TestMethod]
    public void FiniteTimeThatOverflowsCoreArithmetic_PreservesCoreError()
    {
        Assert.IsTrue(Sgp4ObjectPropagator.TryCreate(ReferenceRecord("00005"), out var propagator, out _));
        Assert.IsFalse(propagator.TryPropagateMinutes(double.MaxValue, out var result, out var failure));
        Assert.IsNull(result);
        Assert.AreEqual(new(Sgp4FailureKind.Propagation, 1), failure);
    }

    [TestMethod]
    public void NonfiniteCoreVectorsWithoutCoreError_AreRejected()
    {
        var record = ReferenceRecord("00005");
        record = OrbitalContract.CreateRecord(record with { Elements = record.Elements with { BStarInverseEarthRadii = 0 } });
        Assert.IsTrue(Sgp4ObjectPropagator.TryCreate(record, out var propagator, out _));
        Assert.IsFalse(propagator.TryPropagateMinutes(double.MaxValue, out var result, out var failure));
        Assert.IsNull(result);
        Assert.AreEqual(new(Sgp4FailureKind.NonFiniteState, 0), failure);
    }

    [TestMethod]
    public void InvalidRecordAndFiniteOverflowingCoefficients_AreRejected()
    {
        var record = OrbitalContractExamples.Record();
        Assert.IsFalse(Sgp4ObjectPropagator.TryCreate(record with { RecordSha256 = new('0', 64) }, out var bad, out var invalid));
        Assert.IsNull(bad);
        Assert.AreEqual(new(Sgp4FailureKind.InvalidRecord, 0), invalid);
        var extreme = OrbitalContract.CreateRecord(record with
        {
            Elements = record.Elements with { MeanMotionRevolutionsPerDay = double.MaxValue }
        });
        Assert.IsFalse(Sgp4ObjectPropagator.TryCreate(extreme, out var overflow, out var failure));
        Assert.IsNull(overflow);
        Assert.AreEqual(new(Sgp4FailureKind.NonFiniteCoefficients, 0), failure);
        foreach (var elements in new[]
        {
            record.Elements with { MeanMotionDot = double.MaxValue },
            record.Elements with { MeanMotionDdot = double.MaxValue }
        })
        {
            var derivativeOverflow = OrbitalContract.CreateRecord(record with { Elements = elements });
            Assert.IsFalse(Sgp4ObjectPropagator.TryCreate(derivativeOverflow, out var result, out failure));
            Assert.IsNull(result);
            Assert.AreEqual(new(Sgp4FailureKind.NonFiniteCoefficients, 0), failure);
        }
    }

    private static double Residual(OrbitalVector3 actual, JsonElement expected)
    {
        Assert.AreEqual(3, expected.GetArrayLength());
        var x = actual.X - expected[0].GetDouble();
        var y = actual.Y - expected[1].GetDouble();
        var z = actual.Z - expected[2].GetDouble();
        var residual = Math.Sqrt(x * x + y * y + z * z);
        Assert.IsTrue(double.IsFinite(residual));
        return residual;
    }

    private static string ReferencePath(string filename)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital-reference-v1", filename);

    private static OrbitalRecordV1 ReferenceRecord(string catalog)
    {
        var bytes = File.ReadAllBytes(ReferencePath("SGP4-VER.TLE"));
        Assert.AreEqual("76727A72FC0D0A1AF115E03AF487C33213BEC8842AE6C32B5F0829454C8A45D7",
            Convert.ToHexString(SHA256.HashData(bytes)));
        var lines = File.ReadAllLines(ReferencePath("SGP4-VER.TLE"));
        var index = Array.FindIndex(lines, x => x.StartsWith("1 " + catalog, StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, index);
        return FromTle(lines[index], lines[index + 1]);
    }

    // Test-only fixed-width decoding of the pinned legacy verification inputs. Production consumes
    // normalized GP records directly and never serializes modern identities through a TLE wrapper.
    private static OrbitalRecordV1 FromTle(string line1, string line2)
    {
        var twoDigitYear = int.Parse(line1.AsSpan(18, 2), CultureInfo.InvariantCulture);
        var year = twoDigitYear < 57 ? 2000 + twoDigitYear : 1900 + twoDigitYear;
        var day = decimal.Parse(line1.AsSpan(20, 12), CultureInfo.InvariantCulture);
        var epoch = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero)
            .AddTicks(decimal.ToInt64((day - 1) * TimeSpan.TicksPerDay));
        double Field(string text, int start, int length) => double.Parse(text.AsSpan(start, length), CultureInfo.InvariantCulture);
        double Implied(string text) => double.Parse(text[..1] + "0." + text.Substring(1, 5) + "e" + text[6..], CultureInfo.InvariantCulture);
        var elements = new OrbitalMeanElements(Field(line2, 52, 11),
            double.Parse(string.Concat("0.", line2.AsSpan(26, 7)), CultureInfo.InvariantCulture),
            Field(line2, 8, 8), Field(line2, 17, 8), Field(line2, 34, 8), Field(line2, 43, 8),
            Implied(line1.Substring(53, 8)), Field(line1, 33, 10), Implied(line1.Substring(44, 8)), 0, "U", null, null);
        return OrbitalContract.CreateRecord(OrbitalContractExamples.Record(
            int.Parse(line1.AsSpan(2, 5), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))
            with
        { EpochUtc = epoch, Elements = elements });
    }

    private static ElsetRec RawElements(OrbitalRecordV1 record)
    {
        var e = record.Elements;
        var utc = record.EpochUtc.UtcDateTime;
        return new()
        {
            satid = record.ObjectIdentity.CatalogNumber,
            jdsatepoch = utc.Date.Ticks / TimeSpan.TicksPerDay + 1721425.5,
            jdsatepochF = utc.TimeOfDay.Ticks / (double)TimeSpan.TicksPerDay,
            no_kozai = e.MeanMotionRevolutionsPerDay * 2 * Math.PI / 1440,
            ndot = e.MeanMotionDot * 2 * Math.PI / (1440.0 * 1440),
            nddot = e.MeanMotionDdot * 2 * Math.PI / (1440.0 * 1440 * 1440),
            ecco = e.Eccentricity,
            bstar = e.BStarInverseEarthRadii,
            inclo = e.InclinationDegrees * Math.PI / 180,
            nodeo = e.AscendingNodeDegrees * Math.PI / 180,
            argpo = e.ArgumentOfPericenterDegrees * Math.PI / 180,
            mo = e.MeanAnomalyDegrees * Math.PI / 180
        };
    }
}
