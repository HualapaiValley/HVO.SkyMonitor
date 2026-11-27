using System.Globalization;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

public sealed partial class SmallBodyGoldenFixtureTests
{
    private const double ArcsecondsPerRadian = 180 * 3600 / Math.PI;
    private const double RadiansPerDegree = Math.PI / 180;

    [TestMethod]
    public void RectilinearFlybyReproducesAlignedAndAllPhaseBenchmarks()
    {
        using var document = Open("analytical/rectilinear-flyby.json");
        var model = document.RootElement;
        var rangeKm = Num(model, "minimumRangeAu") * Num(model, "astronomicalUnitKm");
        var rate = Num(model, "transverseSpeedKmPerSecond") / rangeKm;
        Assert.AreEqual(0.002, Num(model, "minimumRangeAu"));
        Assert.AreEqual(75d, Num(model, "transverseSpeedKmPerSecond"));
        Assert.AreEqual(0d, Num(model, "closestApproachSeconds"));
        Assert.AreEqual(174.07, Math.Round(2.5 * RadiansPerDegree / rate, 2));
        var motionCap = Num(model, "angularStepCapDegrees") * RadiansPerDegree / rate;
        Assert.AreEqual(Num(model, "motionStepCapSeconds"), motionCap, 1e-10);
        var distanceCap = Int(model, "distanceStepCapSeconds");
        var selectedStep = Items(model, "supportedStepSeconds").Select(value => value.GetInt32())
            .Max(h => h <= distanceCap && h <= motionCap ? h : 0);
        Assert.AreEqual(120, selectedStep);
        Assert.AreEqual(Int(model, "selectedStepSeconds"), selectedStep);

        foreach (var step in Items(model, "steps"))
        {
            var h = Int(step, "stepSeconds");
            var scale = h * rate;
            var midpoint = step.GetProperty("alignedMidpoint");
            var continuous = step.GetProperty("alignedContinuous");
            var phased = step.GetProperty("allPhase");
            var cap = Num(model, "angularErrorCapArcsec");

            // Symmetry maps (start,u) to (-start-1,1-u). At start >= 128,
            // every stencil point is at least 127 steps from closest approach.
            const int searchEnd = 128;
            var tail = FlybyErrorBound(scale, 127 * scale);
            Assert.IsLessThan(Num(midpoint, "errorArcsec"), Math.Asin(tail) * ArcsecondsPerRadian);
            var starts = Enumerable.Range(0, searchEnd + 1).ToArray();
            var aligned = starts.MaxBy(k => FlybyResidual(scale, k, 0.5));
            Assert.AreEqual(Int(midpoint, "k"), aligned);
            AssertBenchmark(midpoint, FlybyResidual(scale, aligned, Num(midpoint, "u")));

            // Maximize each aligned interval over fractional position. The coarse
            // sweep brackets the peak before the local bounded search.
            var peak = starts.Select(k => (k, peak: Maximize(u => FlybyResidual(scale, k, u), 0, 1, 20)))
                .MaxBy(p => p.peak.Error);
            Assert.AreEqual(Int(continuous, "k"), peak.k);
            Assert.AreEqual(Num(continuous, "u"), peak.peak.Argument, 1e-5);
            AssertBenchmark(continuous, peak.peak.Error);
            AssertBenchmark(continuous, FlybyResidual(scale, Int(continuous, "k"), Num(continuous, "u")));

            // Shifted grids can be searched as a continuous interval start.
            // Search the entire compact range, then refine the best start and u.
            var phasePeak = Enumerable.Range(0, searchEnd * 8 + 5)
                .Select(i => i / 8d - 0.5)
                .Select(x => (Start: x, Peak: Maximize(u => FlybyResidual(scale, x, u), 0, 1, 20)))
                .MaxBy(p => p.Peak.Error);
            var bestStart = phasePeak.Start;
            var bestU = phasePeak.Peak.Argument;
            for (var i = 0; i < 4; i++)
            {
                bestStart = Maximize(x => FlybyResidual(scale, x, bestU), phasePeak.Start - 0.125, phasePeak.Start + 0.125, 4).Argument;
                bestU = Maximize(u => FlybyResidual(scale, bestStart, u), 0.45, 0.55, 4).Argument;
            }
            Assert.AreEqual(Num(phased, "startInStepUnits"), bestStart, 2e-5);
            Assert.AreEqual(Num(phased, "u"), bestU, 1e-5);
            AssertBenchmark(phased, FlybyResidual(scale, bestStart, bestU));
            AssertBenchmark(phased, FlybyResidual(scale, Num(phased, "startInStepUnits"), Num(phased, "u")));

            if (step.TryGetProperty("phaseWitness", out var witness))
                AssertBenchmark(witness, FlybyResidual(scale, Num(witness, "startInStepUnits"), Num(witness, "u")));

            if (h == selectedStep)
            {
                // A global fourth derivative bound certifies every phase and
                // every fractional position independently of numerical search.
                Assert.IsLessThan(cap, Math.Asin(FlybyErrorBound(scale, 0)) * ArcsecondsPerRadian);
            }
            else
            {
                Assert.IsGreaterThan(cap, Num(midpoint, "errorArcsec"));
                // For every phase there is a start in [4,5]. The derivative of
                // the midpoint error vector is bounded by the same remainder
                // applied to the first derivative. Rotating into the true
                // direction adds <= scale * error to its derivative.
                var errorBound = FlybyErrorBound(scale, 0);
                var derivativeBound = (3d / 128 * 120 * Math.Pow(scale, 5) + scale * errorBound) /
                    (1 - errorBound) * ArcsecondsPerRadian;
                const int phaseIntervals = 100;
                var minimumWitness = Enumerable.Range(0, phaseIntervals + 1)
                    .Min(i => FlybyResidual(scale, 4 + i / (double)phaseIntervals, 0.5));
                Assert.IsGreaterThan(cap, minimumWitness - derivativeBound / (2 * phaseIntervals));
            }
        }
    }

    [TestMethod]
    public void IndependentHalfStepsCatchTheOscillationThatIntegerKnotsMiss()
    {
        using var document = Open("analytical/oscillation.json");
        var vector = document.RootElement;
        var expected = vector.GetProperty("expected");
        var amplitude = Num(vector, "amplitudeArcsec");
        var step = Int(vector, "stepSeconds");
        Assert.AreEqual(60, step);
        var knotIndices = Items(vector, "knotIndices").Select(item => item.GetInt32()).ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(0, 7).ToArray(), knotIndices);
        var knots = knotIndices.Select(index => Reference(index * step)).ToArray();
        var withheld = Int(vector, "integerLeaveOutIndex");
        var integerStride = Interpolate(knots[withheld - 3], knots[withheld - 1], knots[withheld + 1], knots[withheld + 3], 0.5);
        Assert.AreEqual(Str(expected, "integerStrideResidualArcsec"), FormatArcsec(Angle(integerStride, knots[withheld])));

        var midpoints = Items(vector, "midpointIndices");
        Assert.HasCount(knots.Length - 3, midpoints);
        for (var i = 0; i < midpoints.Length; i++)
        {
            var position = midpoints[i].GetDouble();
            Assert.AreEqual(i + 1.5, position);
            var interpolated = Interpolate(knots[i], knots[i + 1], knots[i + 2], knots[i + 3], 0.5);
            var error = Angle(interpolated, Reference(position * step));
            Assert.AreEqual(Str(expected, "midpointResidualArcsec"), FormatArcsec(error));
            Assert.IsGreaterThan(1d, error);
        }
        Assert.IsFalse(expected.GetProperty("interpolationBoundVerified").GetBoolean());
        Assert.AreEqual("InterpolationBoundExceeded", Str(expected, "reasonCode"));

        Direction Reference(double seconds) =>
            Direction.FromAngles(amplitude / 3600 * Math.Sin(Math.PI * seconds / step), 0);
    }

    private static double FlybyResidual(double scale, double start, double fraction)
    {
        // Work in the exact direction's frame. The sine numerator simplifies
        // to scale * (offset - fraction), avoiding subtraction of near-equal
        // direction products when locating the shallow 120-second maximum.
        var referenceSlope = scale * (start + fraction);
        Direction InReferenceFrame(int offset)
        {
            var slope = scale * (start + offset);
            var norm = Math.Sqrt(1 + slope * slope);
            return new((1 + referenceSlope * slope) / norm, scale * (offset - fraction) / norm, 0);
        }

        var interpolated = Interpolate(InReferenceFrame(-1), InReferenceFrame(0),
            InReferenceFrame(1), InReferenceFrame(2), fraction);
        return Math.Abs(Math.Atan2(interpolated.Y, interpolated.X)) * ArcsecondsPerRadian;
    }

    // For z(x) = (1+ix)^(1/2) (1-ix)^(-1/2), the Leibniz sum bounds |z''''|
    // by (105+60+18+12+15)/16 / (1+x²)^2. The analogous fifth derivative
    // sum is <= 120 globally. The four-point remainder product is maximized
    // at u=1/2, giving 3/128. asin(error) bounds the change after normalization.
    private static double FlybyErrorBound(double scale, double minAbsoluteX) =>
        3d / 128 * (210d / 16) * Math.Pow(scale, 4) / Math.Pow(1 + minAbsoluteX * minAbsoluteX, 2);

    private static (double Argument, double Error) Maximize(Func<double, double> evaluate, double start, double end, int intervals)
    {
        var step = (end - start) / intervals;
        var sample = Enumerable.Range(0, intervals + 1).MaxBy(i => evaluate(start + i * step));
        var left = start + Math.Max(0, sample - 1) * step;
        var right = start + Math.Min(intervals, sample + 1) * step;
        for (var i = 0; i < 32; i++)
        {
            var lo = (left * 2 + right) / 3;
            var hi = (left + right * 2) / 3;
            if (evaluate(lo) < evaluate(hi))
                left = lo;
            else
                right = hi;
        }
        var middle = (left + right) / 2;
        return (middle, evaluate(middle));
    }

    private static void AssertBenchmark(JsonElement expected, double arcsec)
    {
        Assert.AreEqual(Num(expected, "errorArcsec"), arcsec, 2e-9);
        Assert.AreEqual(Str(expected, "roundedArcsec"), FormatArcsec(arcsec));
    }

    private static string FormatArcsec(double arcsec) => arcsec.ToString("F6", CultureInfo.InvariantCulture);

    private static Direction Interpolate(Direction a, Direction b, Direction c, Direction d, double u) =>
        a.Scale(-u * (u - 1) * (u - 2) / 6) +
        b.Scale((u + 1) * (u - 1) * (u - 2) / 2) +
        c.Scale(-(u + 1) * u * (u - 2) / 2) +
        d.Scale((u + 1) * u * (u - 1) / 6);

    private static double Angle(Direction a, Direction b)
    {
        var cross = new Direction(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        return Math.Atan2(cross.Length(), a.Dot(b)) * ArcsecondsPerRadian;
    }

    private readonly record struct Direction(double X, double Y, double Z)
    {
        public static Direction FromAngles(double ra, double dec)
        {
            var (sinRa, cosRa) = Math.SinCos(ra * RadiansPerDegree);
            var (sinDec, cosDec) = Math.SinCos(dec * RadiansPerDegree);
            return new(cosDec * cosRa, cosDec * sinRa, sinDec);
        }

        public static Direction operator +(Direction a, Direction b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public Direction Scale(double scale) => new(X * scale, Y * scale, Z * scale);
        public double Dot(Direction other) => X * other.X + Y * other.Y + Z * other.Z;
        public double Length() => Math.Sqrt(Dot(this));
        public Direction Unit() => Scale(1 / Length());
    }
}
