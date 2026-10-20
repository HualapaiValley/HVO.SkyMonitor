using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalAdditionalPassTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("08195", "B5918F21ED350683F810C8012C1D31EC2F4B52D68462C9EB3980150EA7748BF1")]
    [DataRow("28623", "D0F2C40FD36DA34FD415042193959658A05844A127B805739124BA8044112969")]
    public async Task HighEccentricityAndResonantPassesMeetIndependentFixedReferences(string catalog, string sha256)
    {
        var bytes = OrbitalCoordinateTestData.Bytes("passes", catalog + "-passes.json");
        Assert.AreEqual(sha256, Convert.ToHexString(SHA256.HashData(bytes)));
        using var document = JsonDocument.Parse(bytes);
        var fixture = document.RootElement;
        var e = fixture.GetProperty("elements");
        double D(string name) => e.GetProperty(name).GetDouble();
        var record = OrbitalContract.CreateRecord(OrbitalContractExamples.Record(int.Parse(catalog, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)) with
        {
            EpochUtc = Utc(fixture.GetProperty("epochUtc")),
            Elements = new(D("meanMotionRevolutionsPerDay"), D("eccentricity"), D("inclinationDegrees"),
                D("ascendingNodeDegrees"), D("argumentOfPericenterDegrees"), D("meanAnomalyDegrees"),
                D("bStarInverseEarthRadii"), D("meanMotionDot"), D("meanMotionDdot"), 0, "U", null, null)
        });
        var snapshot = OrbitalContractExamples.Snapshot(record);
        var interval = new OrbitalUtcInterval(Utc(fixture.GetProperty("windowStartUtc")), Utc(fixture.GetProperty("windowEndUtc")));
        var site = fixture.GetProperty("observer");
        var observer = new ObserverLocation(site.GetProperty("latitudeDegrees").GetDouble(), site.GetProperty("longitudeDegrees").GetDouble(),
            site.GetProperty("elevationMeters").GetDouble());
        var time = OrbitalTimeData.CreateNominalZeroDiagnostic(OrbitalCoordinateTestData.Leaps);
        var model = OrbitalPredictionModel.Create(time);
        Assert.AreEqual(fixture.GetProperty("timeModel").GetProperty("leapSecondsSha256").GetString(), model.LeapSecondsSha256);
        Assert.IsTrue(interval.StartUtc < record.EpochUtc); // Exercise negative SGP4 time, not only positive offsets.
        var bounds = OrbitalContractExamples.Bounds with
        {
            MaximumWindowSeconds = 216000,
            MaximumElapsedMilliseconds = 60000,
            MaximumEvaluations = 500000,
            MaximumPasses = 100
        };
        var request = new OrbitalPredictionRequest(OrbitalPredictionUse.Diagnostic, snapshot, OrbitalContractExamples.Policy(),
            interval, snapshot.ObjectIdentities, observer, model, bounds);
        var engine = new OrbitalPredictionEngine(time);
        double worstCrossing = 0, worstPeak = 0, worstAltitude = 0;
        foreach (var thresholdCase in fixture.GetProperty("thresholds").EnumerateArray())
        {
            var threshold = thresholdCase.GetProperty("thresholdDegrees").GetDouble();
            var result = await engine.FindPassesAsync(new(request, threshold), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status,
                $"{catalog}/{threshold}: {result.Reason}; {string.Join(';', result.Failures.Select(static f => f.Detail))}; {result.PropagationEvaluations} evaluations");
            var expected = thresholdCase.GetProperty("passes").EnumerateArray().ToArray();
            Assert.AreEqual(expected.Length, result.Products.Count);
            var crossings = result.Products.SelectMany(static pass => new[]
            {
                (pass.StartBoundary, Utc: pass.PassInterval.StartUtc), (pass.EndBoundary, Utc: pass.PassInterval.EndUtc)
            }).Where(static p => p.Item1 == OrbitalPassBoundary.ResolvedCrossing).ToArray();
            var references = thresholdCase.GetProperty("crossings").EnumerateArray().ToArray();
            Assert.AreEqual(references.Length, crossings.Length);
            for (var i = 0; i < crossings.Length; i++)
            {
                Assert.IsLessThanOrEqualTo(.001, references[i].GetProperty("bracketWidthSeconds").GetDouble());
                var error = Math.Abs((crossings[i].Utc - Utc(references[i].GetProperty("utc"))).TotalSeconds);
                worstCrossing = Math.Max(worstCrossing, error);
                Assert.IsLessThanOrEqualTo(.02, error, $"{catalog}/{threshold}/crossing{i}");
            }
            for (var i = 0; i < expected.Length; i++)
            {
                var pass = result.Products[i];
                var oracle = expected[i];
                Assert.AreEqual(oracle.GetProperty("startsAtWindowBoundary").GetBoolean() ? OrbitalPassBoundary.SearchWindow : OrbitalPassBoundary.ResolvedCrossing, pass.StartBoundary);
                Assert.AreEqual(oracle.GetProperty("endsAtWindowBoundary").GetBoolean() ? OrbitalPassBoundary.SearchWindow : OrbitalPassBoundary.ResolvedCrossing, pass.EndBoundary);
                Assert.IsLessThanOrEqualTo(.02, Math.Abs((pass.PassInterval.StartUtc - Utc(oracle.GetProperty("startUtc"))).TotalSeconds));
                Assert.IsLessThanOrEqualTo(.02, Math.Abs((pass.PassInterval.EndUtc - Utc(oracle.GetProperty("endUtc"))).TotalSeconds));
                var peak = oracle.GetProperty("peak"); // Highest of all local maxima and clipped boundaries.
                var peakError = Math.Abs((pass.PeakUtc - Utc(peak.GetProperty("utc"))).TotalSeconds);
                var altitudeError = Math.Abs(pass.MaximumElevationDegrees - peak.GetProperty("altitudeDegrees").GetDouble());
                worstPeak = Math.Max(worstPeak, peakError); worstAltitude = Math.Max(worstAltitude, altitudeError);
                Assert.IsLessThanOrEqualTo(.02, peakError, $"{catalog}/{threshold}/peak{i}");
                Assert.IsLessThanOrEqualTo(1e-6, altitudeError, $"{catalog}/{threshold}/altitude{i}");
                Assert.AreEqual(OrbitalExecutionStatus.Complete, pass.Status);
                Assert.IsTrue(pass.Model.NominalZeroEarthOrientation);
                Assert.AreEqual(pass.ProductSha256, OrbitalPredictionJson.CreatePass(pass).ProductSha256);
            }
            TestContext.WriteLine($"{catalog}/{threshold}: {result.Products.Count} passes, {crossings.Length} crossings, {result.PropagationEvaluations} evaluations, {result.ElapsedMilliseconds:R}ms");
        }
        TestContext.WriteLine($"{catalog} maximum residuals: crossing={worstCrossing:R}s; global peak={worstPeak:R}s; altitude={worstAltitude:R}deg");
    }

    private static DateTimeOffset Utc(JsonElement value) => DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture);
}
