using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalPassArcNumericalTests
{
    private const double TargetDegrees = .001;
    private const double OracleAllowanceDegrees = 1e-6;
    private const double ExactAnchorAllowanceDegrees = 1e-8;
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital", "pass-arc-v1", "independent");
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void IndependentOraclePackRetainsAllFrozenInputsSoftwareAndExpectedStates()
    {
        var manifest = File.ReadAllBytes(Path.Combine(Root, "artifact-manifest.json"));
        Assert.AreEqual("CB2DB87F3227087E200E6541E2386A472C4AA02E391E7C71AEF64D8DDEC248E6", Convert.ToHexString(SHA256.HashData(manifest)));
        using var doc = JsonDocument.Parse(manifest);
        Assert.AreEqual(23, doc.RootElement.GetProperty("files").GetArrayLength());
        foreach (var file in doc.RootElement.GetProperty("files").EnumerateArray())
        {
            var bytes = File.ReadAllBytes(Path.Combine(Root, file.GetProperty("path").GetString()!));
            Assert.AreEqual(file.GetProperty("bytes").GetInt32(), bytes.Length);
            Assert.AreEqual(file.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)));
        }
        var cases = File.ReadAllBytes(Path.Combine(Root, "cases.json"));
        Assert.AreEqual("582D78B90C17E890C6757D997293C69658739ABDC36A7CC59FB3EF6A53EB1EDC", Convert.ToHexString(SHA256.HashData(cases)));
        using var data = JsonDocument.Parse(cases);
        Assert.AreEqual(21665, data.RootElement.GetProperty("totalOutputStates").GetInt32());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ExactIndependentParentAnchorsAndFullPassOffPhaseDirectionsMeetFixedAngularGates(int caseIndex)
    {
        using var data = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(Root, "cases.json"), TestContext.CancellationToken).ConfigureAwait(false));
        var item = data.RootElement.GetProperty("cases")[caseIndex]; var (search, time) = Search(item);
        var parent = Parent(item, search); var query = new OrbitalPassArcQuery(search, parent, Bounds);
        var result = await new OrbitalPredictionEngine(time).PredictPassArcAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status, $"{Name(item)}: {result.Reason} {string.Join(';', result.Failures.Select(f => f.Detail))}");
        var arc = result.Products.Single();
        Assert.AreEqual(parent.ProductSha256, arc.Pass.ProductSha256); Assert.AreEqual(parent.PassInterval, arc.CoveredInterval);
        var anchors = item.GetProperty("anchors");
        foreach (var (name, index) in new[] { ("start", arc.Anchors.StartSampleIndex), ("peak", arc.Anchors.PeakSampleIndex), ("end", arc.Anchors.EndSampleIndex) })
        {
            Assert.IsNotNull(index); var reference = anchors.GetProperty(name); var sample = arc.Samples[index.Value];
            Assert.AreEqual(Utc(reference.GetProperty("utc")), sample.Utc);
            var error = Degrees(CameraBasis.FromHorizontal(sample.Horizontal), Unit(reference));
            Assert.IsLessThanOrEqualTo(ExactAnchorAllowanceDegrees, error, $"{Name(item)} {name} exact-UTC anchor");
            TestContext.WriteLine($"{Name(item)}/{name}: exact-UTC direction residual={error:R} deg");
        }
        Assert.AreEqual(parent.MaximumElevationDegrees, arc.Samples[arc.Anchors.PeakSampleIndex!.Value].Horizontal.AltitudeDegrees, 1e-8);
        CompareOffPhase(item, arc);
        TestContext.WriteLine($"{Name(item)}: {result.PropagationEvaluations} actual core calls; {arc.Samples.Count} samples; {result.ElapsedMilliseconds:R} ms; {OrbitalPassArcJson.Serialize(arc).Length} bytes; product={arc.ProductSha256}");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task RealPassSearchThenArcPreservesTrueEventsAndUsesOnlyCommonTimestampDirections(int caseIndex)
    {
        using var data = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(Root, "cases.json"), TestContext.CancellationToken).ConfigureAwait(false));
        var item = data.RootElement.GetProperty("cases")[caseIndex]; var (search, time) = Search(item);
        var engine = new OrbitalPredictionEngine(time);
        var found = await engine.FindPassesAsync(search, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, found.Status, $"{Name(item)} search: {found.Reason} {string.Join(';', found.Failures.Select(f => f.Detail))}");
        var pass = found.Products.Single(); var expected = item.GetProperty("pass");
        var rise = Math.Abs((pass.PassInterval.StartUtc - Utc(expected.GetProperty("startUtc"))).TotalSeconds);
        var peak = Math.Abs((pass.PeakUtc - Utc(expected.GetProperty("peakUtc"))).TotalSeconds);
        var set = Math.Abs((pass.PassInterval.EndUtc - Utc(expected.GetProperty("endUtc"))).TotalSeconds);
        Assert.IsLessThanOrEqualTo(.02, rise, Name(item) + " rise");
        Assert.IsLessThanOrEqualTo(.02, peak, Name(item) + " peak");
        Assert.IsLessThanOrEqualTo(.02, set, Name(item) + " set");
        Assert.AreEqual(expected.GetProperty("maximumElevationDegrees").GetDouble(), pass.MaximumElevationDegrees, 1e-6);
        Assert.AreEqual(OrbitalPassBoundary.ResolvedCrossing, pass.StartBoundary); Assert.AreEqual(OrbitalPassBoundary.ResolvedCrossing, pass.EndBoundary);
        var result = await engine.PredictPassArcAsync(new(search, pass, Bounds), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status, $"{Name(item)} arc: {result.Reason}");
        var arc = result.Products.Single();
        Assert.AreEqual(pass.ProductSha256, arc.Pass.ProductSha256);
        Assert.AreEqual(pass.MaximumElevationDegrees, arc.Samples[arc.Anchors.PeakSampleIndex!.Value].Horizontal.AltitudeDegrees, 1e-8);
        CompareOffPhase(item, arc);
        TestContext.WriteLine($"{Name(item)} independent event residuals: rise={rise:R}s peak={peak:R}s set={set:R}s. Exact-UTC 1e-8deg anchor comparison belongs to the separate independent-parent test.");
    }

    private void CompareOffPhase(JsonElement item, OrbitalPassArcV1 arc)
    {
        var count = 0; var worst = 0d; var ratio = 0d; var cursor = 0;
        foreach (var reference in item.GetProperty("samples").EnumerateArray())
        {
            var utc = Utc(reference.GetProperty("utc"));
            if (utc < arc.CoveredInterval.StartUtc || utc > arc.CoveredInterval.EndUtc) continue;
            Assert.AreEqual(0, reference.GetProperty("error").GetInt32());
            while (cursor < arc.Samples.Count - 2 && arc.Samples[cursor + 1].Utc < utc) cursor++;
            var left = arc.Samples[cursor]; var right = arc.Samples[cursor + 1];
            var fraction = (double)(utc.UtcTicks - left.Utc.UtcTicks) / (right.Utc.UtcTicks - left.Utc.UtcTicks);
            var interpolated = EnuVector.SphericalInterpolate(CameraBasis.FromHorizontal(left.Horizontal), CameraBasis.FromHorizontal(right.Horizontal), fraction);
            var error = Degrees(interpolated, Unit(reference));
            Assert.IsLessThanOrEqualTo(TargetDegrees + OracleAllowanceDegrees, error, $"{Name(item)} {utc:O} requested target");
            Assert.IsLessThanOrEqualTo(arc.Sampling.AchievedEstimatedAngularErrorDegrees!.Value + OracleAllowanceDegrees,
                error, $"{Name(item)} {utc:O} declared empirical estimate");
            worst = Math.Max(worst, error); ratio = Math.Max(ratio, error / (arc.Sampling.AchievedEstimatedAngularErrorDegrees.Value + OracleAllowanceDegrees)); count++;
        }
        Assert.IsTrue(count > 4000, Name(item));
        Assert.IsNull(arc.OrbitUncertainty.Value); Assert.IsTrue(arc.Samples.All(s => s.Illumination == OrbitalIllumination.Unknown && s.Pixel is null));
        TestContext.WriteLine($"{Name(item)}: {count} independent off-phase points; worst={worst:R} deg; declared={arc.Sampling.AchievedEstimatedAngularErrorDegrees:R} deg; error/(estimate+oracle allowance)={ratio:R}.");
    }

    private static OrbitalPassArcBounds Bounds => new(1_000_000, 100000, 600000, 1, TargetDegrees);

    internal static (OrbitalPassQuery Search, OrbitalTimeData Time) Search(JsonElement item)
    {
        var eop = File.ReadAllBytes(Path.Combine(Root, item.GetProperty("eopFile").GetString()!));
        var leaps = File.ReadAllBytes(Path.Combine(Root, item.GetProperty("leapFile").GetString()!));
        Assert.AreEqual(item.GetProperty("eopSha256").GetString(), Convert.ToHexString(SHA256.HashData(eop)));
        Assert.AreEqual(item.GetProperty("leapSha256").GetString(), Convert.ToHexString(SHA256.HashData(leaps)));
        var time = OrbitalTimeData.FromFinals2000A(eop, leaps);
        Assert.AreEqual(item.GetProperty("eopStatus").GetString(), time.EarthOrientationStatus.ToString());
        var record = OrbitalPredictionTestData.Record(item.GetProperty("facts"));
        var snapshot = OrbitalContractExamples.Snapshot(record); var o = item.GetProperty("observer");
        var observer = new ObserverLocation(o[0].GetDouble(), o[1].GetDouble(), o[2].GetDouble());
        var interval = new OrbitalUtcInterval(Utc(item.GetProperty("windowStartUtc")), Utc(item.GetProperty("windowEndUtc")));
        var bounds = OrbitalContractExamples.Bounds with { MaximumEvaluations = 1_000_000, MaximumElapsedMilliseconds = 600000 };
        return (new(new(OrbitalPredictionUse.Diagnostic, snapshot, OrbitalContractExamples.Policy(), interval,
            snapshot.ObjectIdentities, observer, OrbitalPredictionModel.Create(time, assumeEllipsoidalHeight: true), bounds),
            item.GetProperty("thresholdDegrees").GetDouble()), time);
    }

    private static OrbitalPassSummaryV1 Parent(JsonElement item, OrbitalPassQuery search)
    {
        var r = search.Request; var record = r.Snapshot.Records.Single(); var p = item.GetProperty("pass");
        var lineage = new OrbitalProductLineage(r.Snapshot.Manifest.ManifestSha256, r.Snapshot.Manifest.NormalizedPayloadSha256,
            record.RecordSha256, record.ObjectIdentity, record.SourceDataset, r.SelectionPolicy.IdentitySha256, record.EpochUtc,
            (r.Interval.StartUtc - record.EpochUtc).TotalHours, (r.Interval.EndUtc - record.EpochUtc).TotalHours,
            OrbitalElementFreshness.Fresh, OrbitalSourceValidity.Unknown, r.Snapshot.Manifest.Coverage);
        return OrbitalPredictionJson.CreatePass(new(OrbitalPredictionJson.PassSchemaVersion, "", lineage, r.Interval,
            new(Utc(p.GetProperty("startUtc")), Utc(p.GetProperty("endUtc"))), Utc(p.GetProperty("peakUtc")),
            p.GetProperty("maximumElevationDegrees").GetDouble(), search.MinimumElevationDegrees,
            OrbitalPassBoundary.ResolvedCrossing, OrbitalPassBoundary.ResolvedCrossing, r.Observer, r.Model,
            OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None));
    }

    private static DateTimeOffset Utc(JsonElement value) => DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture);
    private static string Name(JsonElement item) => item.GetProperty("name").GetString()!;
    private static EnuVector Unit(JsonElement state)
    {
        var v = state.GetProperty("unitEnu"); return new(v[0].GetDouble(), v[1].GetDouble(), v[2].GetDouble());
    }
    private static double Degrees(EnuVector a, EnuVector b) => Math.Atan2(EnuVector.Cross(a, b).Length, EnuVector.Dot(a, b)) * (180 / Math.PI);
}
