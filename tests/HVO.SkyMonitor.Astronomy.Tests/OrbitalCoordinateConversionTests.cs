using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalCoordinateConversionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void AppendixCLiteralRepresentedUt1MeetsUnmodifiedPerComponentGates()
    {
        using var fixture = Fixture();
        var item = fixture.RootElement.GetProperty("states")[0];
        var result = Convert(item);
        var p = new OrbitalVector3(-1033.47938300, 7901.29527540, 6380.35659580);
        var v = new OrbitalVector3(-3.225636520, -2.872451450, 5.531924446);
        var position = MaximumComponent(result.PositionKilometers, p);
        var velocity = MaximumComponent(result.VelocityKilometersPerSecond, v);
        TestContext.WriteLine($"Appendix C maximum component: position={position:R} km, velocity={velocity:R} km/s");
        Assert.IsLessThanOrEqualTo(5e-8, position);
        Assert.IsLessThanOrEqualTo(7e-8, velocity);
    }

    [TestMethod]
    public void IndependentExactConventionStatesMeetVectorRangeAndDirectionGates()
    {
        using var fixture = Fixture();
        double worstPosition = 0, worstVelocity = 0, worstRange = 0, worstRate = 0, worstDirection = 0, worstSpeed = 0;
        foreach (var item in fixture.RootElement.GetProperty("states").EnumerateArray())
        {
            var result = Convert(item);
            var name = item.GetProperty("name").GetString();
            var p = Distance(result.PositionKilometers, Vector(item.GetProperty("fixedPosition")));
            var v = Distance(result.VelocityKilometersPerSecond, Vector(item.GetProperty("fixedVelocity")));
            Assert.IsLessThanOrEqualTo(1e-6, p, name); Assert.IsLessThanOrEqualTo(1e-8, v, name);
            worstPosition = Math.Max(worstPosition, p); worstVelocity = Math.Max(worstVelocity, v);
            if (!item.TryGetProperty("observer", out _)) continue;
            var utc = OrbitalCoordinateTestData.Utc(item.GetProperty("utc").GetString()!);
            var data = OrbitalCoordinateTestData.Load("predicted-2026.finals2000A");
            var evaluated = OrbitalCoordinateConverter.ToEarthFixed(new(Vector(item.GetProperty("temePosition")), Vector(item.GetProperty("temeVelocity"))), data.Evaluate(utc));
            Assert.IsLessThanOrEqualTo(1e-6, Distance(evaluated.PositionKilometers, result.PositionKilometers), name);
            var residual = CheckObserver(result, item);
            worstRange = Math.Max(worstRange, residual.Range); worstRate = Math.Max(worstRate, residual.Rate);
            worstDirection = Math.Max(worstDirection, residual.Direction); worstSpeed = Math.Max(worstSpeed, residual.Speed);
            TestContext.WriteLine($"{name}: position={p:R}, velocity={v:R}, range={residual.Range:R}, rate={residual.Rate:R}, direction={residual.Direction:R}, speed={residual.Speed:R}");
        }
        TestContext.WriteLine($"Worst exact-convention residuals: position={worstPosition:R} km; velocity={worstVelocity:R} km/s; range={worstRange:R} km; rate={worstRate:R} km/s; direction={worstDirection:R} rad; speed={worstSpeed:R} deg/s");
    }

    [TestMethod]
    public void IndependentWgs84EnuPolesAntimeridianHeightWrapAndZenithRemainFinite()
    {
        using var fixture = Fixture();
        foreach (var item in fixture.RootElement.GetProperty("observerStates").EnumerateArray())
        {
            var residual = CheckObserver(new(Vector(item.GetProperty("fixedPosition")), Vector(item.GetProperty("fixedVelocity"))), item);
            TestContext.WriteLine($"{item.GetProperty("name").GetString()}: range={residual.Range:R}, rate={residual.Rate:R}, direction={residual.Direction:R}, speed={residual.Speed:R}");
        }
        var frame = OrbitalObserverFrame.Create(new(0, 0, 0));
        var zenith = frame.Evaluate(new(new(6878.137, 0, 0), new(1, 7, 0)));
        Assert.AreEqual(90, zenith.Horizontal.AltitudeDegrees);
        Assert.AreEqual(0, zenith.Horizontal.AzimuthDegrees);
        Assert.AreEqual(1, zenith.RangeRateKilometersPerSecond, 1e-15);
        Assert.AreEqual(7d / 500 * (180 / Math.PI), zenith.AngularSpeedDegreesPerSecond, 1e-15);
        Assert.ThrowsExactly<ArgumentException>(() => frame.Evaluate(new(frame.PositionKilometers, new(0, 0, 0))));
        Assert.ThrowsExactly<ArgumentException>(() => frame.Evaluate(new(new(double.NaN, 0, 0), new(0, 0, 0))));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => OrbitalObserverFrame.Create(new(91, 0, 0)));
    }

    private static (double Range, double Rate, double Direction, double Speed) CheckObserver(OrbitalEarthFixedState state, JsonElement item)
    {
        var s = item.GetProperty("observer");
        var observer = new ObserverLocation(s[0].GetDouble(), s[1].GetDouble(), s[2].GetDouble());
        var frame = OrbitalObserverFrame.Create(observer);
        var expected = item.GetProperty("topocentric");
        var name = item.GetProperty("name").GetString();
        Assert.IsLessThanOrEqualTo(1e-6, Distance(frame.PositionKilometers, Vector(expected.GetProperty("observerKilometers"))), name);
        var actual = frame.Evaluate(state);
        var range = Math.Abs(actual.RangeKilometers - expected.GetProperty("rangeKilometers").GetDouble());
        var rate = Math.Abs(actual.RangeRateKilometersPerSecond - expected.GetProperty("rangeRateKilometersPerSecond").GetDouble());
        var e = expected.GetProperty("unitEnu"); var direction = new EnuVector(e[0].GetDouble(), e[1].GetDouble(), e[2].GetDouble());
        var separation = Math.Atan2(EnuVector.Cross(actual.UnitDirection, direction).Length, EnuVector.Dot(actual.UnitDirection, direction));
        var speed = Math.Abs(actual.AngularSpeedDegreesPerSecond - expected.GetProperty("angularSpeedDegreesPerSecond").GetDouble());
        Assert.IsLessThanOrEqualTo(1e-6, range, name); Assert.IsLessThanOrEqualTo(1e-8, rate, name);
        Assert.IsLessThanOrEqualTo(1e-8, separation, name); Assert.IsLessThanOrEqualTo(1e-8, speed, name);
        Assert.AreEqual(expected.GetProperty("altitudeDegrees").GetDouble(), actual.Horizontal.AltitudeDegrees, 1e-8, name);
        // Qualify the returned alt/az as a direction, including near zenith and azimuth wrap.
        // A scalar azimuth difference is ill-conditioned near zenith and is not the declared angular gate.
        var (sa, ca) = Math.SinCos(actual.Horizontal.AltitudeDegrees * (Math.PI / 180));
        var (sz, cz) = Math.SinCos(actual.Horizontal.AzimuthDegrees * (Math.PI / 180));
        var reconstructed = new EnuVector(ca * sz, ca * cz, sa);
        Assert.IsLessThanOrEqualTo(1e-8, Math.Atan2(EnuVector.Cross(reconstructed, direction).Length,
            EnuVector.Dot(reconstructed, direction)), name);
        return (range, rate, separation, speed);
    }

    private static JsonDocument Fixture() => JsonDocument.Parse(OrbitalCoordinateTestData.Bytes("coordinates", "reference-states.json"));
    private static OrbitalVector3 Vector(JsonElement array) => new(array[0].GetDouble(), array[1].GetDouble(), array[2].GetDouble());
    private static OrbitalEarthFixedState Convert(JsonElement item) => OrbitalCoordinateConverter.ToEarthFixed(
        new(Vector(item.GetProperty("temePosition")), Vector(item.GetProperty("temeVelocity"))),
        new(item.GetProperty("ut1Day").GetDouble(), item.GetProperty("ut1Fraction").GetDouble()), item.GetProperty("xp").GetDouble(), item.GetProperty("yp").GetDouble());
    private static double Distance(OrbitalVector3 a, OrbitalVector3 b) => double.Hypot(double.Hypot(a.X - b.X, a.Y - b.Y), a.Z - b.Z);
    private static double MaximumComponent(OrbitalVector3 a, OrbitalVector3 b) => Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));
}
