using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalTimeDataTests
{
    [TestMethod]
    public void ActualBytesCoverageStatusAndConventionsMustMatchModel()
    {
        var eop = OrbitalCoordinateTestData.Bytes("time", "predicted-2026.finals2000A");
        var leaps = OrbitalCoordinateTestData.Leaps;
        var data = OrbitalTimeData.FromFinals2000A(eop, leaps);
        var model = OrbitalCoordinateTestData.Model(data);
        Assert.AreEqual("DB5A895F16853B03BFC865E8D68F9FC8710EF1740E3400C701CD46A5BBBC3433", data.LeapSecondsSha256);
        var interval = new OrbitalUtcInterval(OrbitalCoordinateTestData.Utc("2026-10-19T12:00:00Z"), OrbitalCoordinateTestData.Utc("2026-10-20T12:00:00Z"));
        data.ValidateModel(model, interval, OrbitalPredictionUse.Diagnostic, OrbitalGeometryBasis.ConfiguredRig);
        data.ValidateModel(model, interval, OrbitalPredictionUse.MeasuredAssociation, OrbitalGeometryBasis.AcceptedMeasured);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(eop)), data.EarthOrientationSha256);
        Assert.AreEqual(OrbitalEarthOrientationStatus.Predicted, data.EarthOrientationStatus);
        foreach (var altered in new[]
        {
            model with { EarthOrientationSha256 = new string('A', 64) },
            model with { LeapSecondsSha256 = new string('B', 64) },
            model with { EarthOrientationCoverage = new(interval.StartUtc, interval.EndUtc) },
            model with { LeapTableCoverage = new(interval.StartUtc, interval.EndUtc) },
            model with { EarthOrientationStatus = OrbitalEarthOrientationStatus.Final },
            model with { TemeConversionVersion = "stellar-j2000" }
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() => data.ValidateModel(altered, interval,
                OrbitalPredictionUse.Diagnostic, OrbitalGeometryBasis.ConfiguredRig));
        }
        eop[0] ^= 1; leaps[0] ^= 1;
        data.ValidateModel(model, interval, OrbitalPredictionUse.Diagnostic, OrbitalGeometryBasis.ConfiguredRig);
        // Changing even a comment preserves the parsed leap rows but changes the resolved input identity.
        var alteredLeap = Encoding.ASCII.GetBytes("# altered identity\n" + Encoding.ASCII.GetString(OrbitalCoordinateTestData.Leaps));
        var other = OrbitalTimeData.FromFinals2000A(OrbitalCoordinateTestData.Bytes("time", "predicted-2026.finals2000A"), alteredLeap);
        Assert.ThrowsExactly<ArgumentException>(() => other.ValidateModel(model, interval,
            OrbitalPredictionUse.Diagnostic, OrbitalGeometryBasis.ConfiguredRig));
    }

    [TestMethod]
    public void FinalPredictedMixedInterpolationAndExactEndpointsPreserveActualFacts()
    {
        var final = OrbitalCoordinateTestData.Load("final-2004.finals2000A");
        Assert.AreEqual(OrbitalEarthOrientationStatus.Final, final.EarthOrientationStatus);
        var finalPoint = final.Evaluate(OrbitalCoordinateTestData.Utc("2004-04-06T00:00:00Z"));
        Assert.AreEqual(-.140720, finalPoint.PolarMotionXArcseconds);
        Assert.AreEqual(.333270, finalPoint.PolarMotionYArcseconds);
        Assert.AreEqual(-.4399620, finalPoint.Dut1Seconds);
        var data = OrbitalCoordinateTestData.Load("predicted-2026.finals2000A");
        var utc = OrbitalCoordinateTestData.Utc("2026-10-19T12:00:00Z");
        var p = data.Evaluate(utc);
        Assert.AreEqual((-.0389680 - .0398926) / 2, p.Dut1Seconds, 1e-15);
        Assert.AreEqual((.153125 + .151932) / 2, p.PolarMotionXArcseconds, 1e-15);
        Assert.AreEqual((.320394 + .320379) / 2, p.PolarMotionYArcseconds, 1e-15);
        Assert.AreEqual(37, p.TaiMinusUtcSeconds);
        var start = data.EarthOrientationCoverage!.StartUtc;
        var end = data.EarthOrientationCoverage.EndUtc;
        _ = data.Evaluate(start); _ = data.Evaluate(end);
        Assert.ThrowsExactly<ArgumentException>(() => data.Evaluate(start.AddTicks(-1)));
        Assert.ThrowsExactly<ArgumentException>(() => data.Evaluate(end.AddTicks(1)));
        // Explicit synthetic status fixture: preserve historical values, change one row to predicted A.
        var lines = OrbitalCoordinateTestData.Lines("final-2004.finals2000A");
        var predicted = lines[1].ToCharArray();
        predicted[16] = predicted[57] = 'P';
        Array.Fill(predicted, ' ', 134, predicted.Length - 134);
        var mixed = OrbitalTimeData.FromFinals2000A(Encoding.ASCII.GetBytes(lines[0] + "\n" + new string(predicted) + "\n"), OrbitalCoordinateTestData.Leaps);
        Assert.AreEqual(OrbitalEarthOrientationStatus.Mixed, mixed.EarthOrientationStatus);
        Assert.AreEqual(OrbitalEarthOrientationStatus.Mixed, mixed.Evaluate(mixed.EarthOrientationCoverage!.StartUtc.AddHours(12)).Status);
        // Final and predicted spans are reported separately; neither claims the other's rows.
        Assert.AreEqual(final.EarthOrientationCoverage, final.EarthOrientationFinalCoverage);
        Assert.IsNull(final.EarthOrientationPredictedCoverage);
        Assert.AreEqual(data.EarthOrientationCoverage, data.EarthOrientationPredictedCoverage);
        Assert.IsNull(data.EarthOrientationFinalCoverage);
        var first = mixed.EarthOrientationCoverage.StartUtc;
        Assert.AreEqual(new OrbitalUtcInterval(first, first), mixed.EarthOrientationFinalCoverage);
        Assert.AreEqual(new OrbitalUtcInterval(first.AddDays(1), first.AddDays(1)), mixed.EarthOrientationPredictedCoverage);
    }

    [TestMethod]
    public void GapsDuplicateUnorderedMalformedAndUnresolvedRapidRowsFailClosed()
    {
        var lines = OrbitalCoordinateTestData.Lines("predicted-2026.finals2000A");
        var gap = OrbitalCoordinateTestData.ParseLines(lines[0], lines[2], lines[3]);
        var interval = new OrbitalUtcInterval(gap.EarthOrientationCoverage!.StartUtc, gap.EarthOrientationCoverage.EndUtc);
        Assert.ThrowsExactly<ArgumentException>(() => gap.Evaluate(interval.StartUtc.AddHours(12)));
        Assert.ThrowsExactly<ArgumentException>(() => gap.ValidateModel(OrbitalCoordinateTestData.Model(gap), interval,
            OrbitalPredictionUse.Diagnostic, OrbitalGeometryBasis.ConfiguredRig));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalCoordinateTestData.ParseLines(lines[0], lines[0]));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalCoordinateTestData.ParseLines(lines[1], lines[0]));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalCoordinateTestData.ParseLines("short"));
        var rapid = lines[1].ToCharArray(); rapid[16] = rapid[57] = 'I';
        var rapidGap = OrbitalCoordinateTestData.ParseLines(lines[0], new string(rapid), lines[2]);
        Assert.ThrowsExactly<ArgumentException>(() => rapidGap.Evaluate(rapidGap.EarthOrientationCoverage!.StartUtc.AddDays(1)));
        var invalid = lines[0].ToCharArray(); "      NaN".CopyTo(0, invalid, 18, 9);
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalCoordinateTestData.ParseLines(new string(invalid), lines[1]));
        invalid = lines[0].ToCharArray(); invalid[16] = 'X';
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalCoordinateTestData.ParseLines(new string(invalid), lines[1]));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalTimeData.FromFinals2000A(new byte[8 * 1024 * 1024 + 1], OrbitalCoordinateTestData.Leaps));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalTimeData.CreateNominalZeroDiagnostic(new byte[64 * 1024 + 1]));
    }

    [TestMethod]
    public void IndependentLeapArtifactBoundsAndEpochToQueryCrossingsAreEnforced()
    {
        var data = OrbitalTimeData.CreateNominalZeroDiagnostic(OrbitalCoordinateTestData.Leaps);
        var before = OrbitalCoordinateTestData.Utc("2016-12-31T23:59:59.9999999Z");
        var at = before.AddTicks(1);
        Assert.AreEqual(36, data.Evaluate(before).TaiMinusUtcSeconds);
        Assert.AreEqual(37, data.Evaluate(at).TaiMinusUtcSeconds);
        Assert.AreEqual(37, data.Evaluate(at.AddTicks(1)).TaiMinusUtcSeconds);
        data.ValidatePropagationInterval(before.AddSeconds(-10), new(before.AddSeconds(-5), before));
        Assert.ThrowsExactly<ArgumentException>(() => data.ValidatePropagationInterval(before, new(at, at.AddSeconds(1))));
        Assert.ThrowsExactly<ArgumentException>(() => data.ValidatePropagationInterval(at.AddSeconds(1), new(before, at)));
        data.ValidatePropagationInterval(at, new(at, at.AddHours(1)));
        Assert.ThrowsExactly<ArgumentException>(() => data.Evaluate(data.LeapTableCoverage.StartUtc.AddTicks(-1)));
        _ = data.Evaluate(data.LeapTableCoverage.EndUtc);
        Assert.ThrowsExactly<ArgumentException>(() => data.Evaluate(data.LeapTableExpiresUtc));
        Assert.AreEqual(OrbitalCoordinateTestData.Utc("2027-06-28T00:00:00Z"), data.LeapTableExpiresUtc);
        var real = OrbitalCoordinateTestData.Load("leap-2016.finals2000A");
        _ = real.Evaluate(OrbitalCoordinateTestData.Utc("2016-12-30T12:00:00Z"));
        _ = real.Evaluate(at);
        _ = real.Evaluate(at.AddHours(1));
        Assert.ThrowsExactly<ArgumentException>(() => real.Evaluate(before)); // Daily DUT1 bracket crosses insertion.
        Assert.ThrowsExactly<ArgumentException>(() => data.ValidateModel(OrbitalCoordinateTestData.Model(data), new(before, at),
            OrbitalPredictionUse.Diagnostic, OrbitalGeometryBasis.ConfiguredRig));
    }

    [TestMethod]
    public void NominalZeroRequiresExplicitConfiguredRigDiagnosticAndValidLeapArtifact()
    {
        var data = OrbitalTimeData.CreateNominalZeroDiagnostic(OrbitalCoordinateTestData.Leaps);
        var utc = OrbitalCoordinateTestData.Utc("2026-10-20T13:04:21.1234567Z");
        var interval = new OrbitalUtcInterval(utc, utc.AddSeconds(1));
        var model = OrbitalCoordinateTestData.Model(data);
        data.ValidateModel(model, interval, OrbitalPredictionUse.Diagnostic, OrbitalGeometryBasis.ConfiguredRig);
        Assert.AreEqual(0, data.Evaluate(utc).Dut1Seconds);
        Assert.AreEqual(data.Evaluate(utc).Utc, data.Evaluate(utc).Ut1);
        Assert.ThrowsExactly<ArgumentException>(() => data.ValidateModel(model, interval, OrbitalPredictionUse.MeasuredAssociation, OrbitalGeometryBasis.ConfiguredRig));
        Assert.ThrowsExactly<ArgumentException>(() => data.ValidateModel(model, interval, OrbitalPredictionUse.Diagnostic, OrbitalGeometryBasis.AcceptedMeasured));
        foreach (var text in new[] { "2272060800 10\n", "#@ 4023129600\n", "#@ 4023129600\n2272060800 10\n2272060800 11\n",
            "#@ 4023129600\n2272060800 10\n3692217600 37\n", "#@ 4023129600\n2272060800 NaN\n" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => OrbitalTimeData.CreateNominalZeroDiagnostic(Encoding.ASCII.GetBytes(text)));
        }
    }

    [TestMethod]
    public void SplitUtcPreservesSubsecondTicksAndUt1DayRolloverIndependently()
    {
        var midnight = OrbitalCoordinateTestData.Utc("2026-10-20T00:00:00Z");
        var a = OrbitalJulianDate.FromUtc(midnight);
        Assert.AreEqual(2461333.5, a.Day);
        Assert.AreEqual(0, a.Fraction);
        var tick = OrbitalJulianDate.FromUtc(midnight.AddTicks(1));
        Assert.AreEqual(1e-7, tick.Fraction * 86400, 1e-21);
        var precise = OrbitalJulianDate.FromUtc(midnight.AddTicks(1234567));
        Assert.AreEqual(.1234567, precise.Fraction * 86400, 3e-17);
        var negative = a.AddSeconds(-.439961);
        Assert.AreEqual(a.Day - 1, negative.Day);
        Assert.AreEqual(-.439961, (negative.Fraction - 1) * 86400, 1e-11);
        var prior = OrbitalJulianDate.FromUtc(midnight.AddTicks(-1));
        var rollover = prior.AddSeconds(.5);
        Assert.AreEqual(a.Day, rollover.Day);
        Assert.AreEqual(.4999999, rollover.Fraction * 86400, 2e-11);
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalJulianDate.FromUtc(midnight.ToOffset(TimeSpan.FromHours(1))));
    }
}

internal static class OrbitalCoordinateTestData
{
    internal static byte[] Bytes(string directory, string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital-prediction-v1", directory, name));
    internal static byte[] Leaps => Bytes("time", "leap-seconds.list");
    internal static string[] Lines(string name) => Encoding.ASCII.GetString(Bytes("time", name)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    internal static OrbitalTimeData Load(string name) => OrbitalTimeData.FromFinals2000A(Bytes("time", name), Leaps);
    internal static DateTimeOffset Utc(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
    internal static OrbitalTimeData ParseLines(params string[] lines) => OrbitalTimeData.FromFinals2000A(Encoding.ASCII.GetBytes(string.Join('\n', lines) + "\n"), Leaps);
    internal static OrbitalPredictionModelFacts Model(OrbitalTimeData data) => OrbitalContractExamples.Model with
    {
        TemeConversionVersion = OrbitalTimeData.ConversionVersion,
        Ut1Convention = data.NominalZeroEarthOrientation ? "utc-assumed-ut1" : "utc-plus-pinned-dut1",
        PolarMotionConvention = data.NominalZeroEarthOrientation ? "zero-assumed" : "pinned-eop",
        EarthOrientationSha256 = data.EarthOrientationSha256,
        EarthOrientationCoverage = data.EarthOrientationCoverage,
        EarthOrientationStatus = data.EarthOrientationStatus,
        NominalZeroEarthOrientation = data.NominalZeroEarthOrientation,
        LeapSecondsSha256 = data.LeapSecondsSha256,
        LeapTableCoverage = data.LeapTableCoverage
    };
}
