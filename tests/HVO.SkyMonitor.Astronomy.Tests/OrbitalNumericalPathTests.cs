namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalNumericalPathTests
{
    [TestMethod]
    public void QuarticHullContainsOffPhaseCurveIncludingZeroMidpointDeviation()
    {
        static EnuVector Path(double t) => new(100 + 20 * t, 10 * t * (t - .5) * (t - 1), 50 + 3 * t * t * t * t);
        var nodes = Enumerable.Range(0, 5).Select(i => Path(i / 4d)).ToArray();
        var controls = OrbitalNumericalPath.Fit(nodes);
        var bound = controls.Select((p, i) => (p - (nodes[0] * (1 - i / 4d) + nodes[4] * (i / 4d))).Length).Max();
        Assert.IsGreaterThan(0d, bound);
        for (var i = 0; i < 997; i++)
        {
            var t = (i + .37) / 997;
            Assert.IsLessThan(1e-12, (Path(t) - OrbitalNumericalPath.Evaluate(controls, t)).Length);
            var chord = nodes[0] * (1 - t) + nodes[4] * t;
            Assert.IsLessThanOrEqualTo(bound, (Path(t) - chord).Length);
        }
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalNumericalPath.Fit([new(0, 0, 0)]));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalNumericalPath.Evaluate(controls, double.NaN));
    }

    [TestMethod]
    public void CartesianTubeContainsPolynomialDirectionsAcrossZenithAndRetainsRemainderFloor()
    {
        static EnuVector Position(double t) => new(30 * (t - .5), 5 * (t - .5) * (t - .5), 500);
        var samples = Enumerable.Range(0, 9).Select(i =>
        {
            var p = Position(i / 8d);
            var unit = p / p.Length;
            return new OrbitalTopocentricState(CameraBasis.ToHorizontal(unit), p.Length, 0, 0, unit);
        }).ToArray();
        var tube = OrbitalNumericalPath.Enclose(samples);
        Assert.AreEqual(OrbitalNumericalPath.CartesianFloorKilometers, tube.CartesianRemainderKilometers);
        Assert.IsGreaterThan(400d, tube.MinimumRangeKilometers);
        var center = CameraBasis.FromHorizontal(tube.Center);
        for (var i = 0; i < 997; i++)
        {
            var p = Position((i + .37) / 997);
            var unit = p / p.Length;
            var angle = Math.Atan2(EnuVector.Cross(center, unit).Length, EnuVector.Dot(center, unit));
            Assert.IsLessThanOrEqualTo(tube.RadiusRadians, angle);
        }
        var start = OrbitalContractExamples.Epoch;
        Assert.AreEqual(start.AddTicks(2), OrbitalNumericalPath.At(start, start.AddTicks(9), 1, 4));
    }
}
