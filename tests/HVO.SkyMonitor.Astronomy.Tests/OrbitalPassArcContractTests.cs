using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalPassArcContractTests
{
    [TestMethod]
    public void SupplementalGoldenFixturesAndOriginalContractPackKeepTheirPinnedBytes()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital", "pass-arc-v1");
        using var pack = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "fixture-manifest.json")));
        foreach (var file in pack.RootElement.GetProperty("files").EnumerateArray())
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, file.GetProperty("path").GetString()!));
            Assert.AreEqual(file.GetProperty("lengthBytes").GetInt32(), bytes.Length);
            Assert.AreEqual(file.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)));
        }
        var old = File.ReadAllBytes(Path.Combine(root, "../contract-v1/fixture-manifest.json"));
        Assert.AreEqual(pack.RootElement.GetProperty("originalPackSha256").GetString(), Convert.ToHexString(SHA256.HashData(old)));
        using var oldPack = JsonDocument.Parse(old);
        foreach (var file in oldPack.RootElement.GetProperty("files").EnumerateArray())
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, "../contract-v1", file.GetProperty("path").GetString()!));
            Assert.AreEqual(file.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)));
        }
        var query = ExampleQuery();
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(root, "arc.json")), OrbitalPassArcJson.Serialize(ExampleArc(query)));
        var projection = File.ReadAllBytes(Path.Combine(root, "search-identity.json"));
        CollectionAssert.AreEqual(projection, OrbitalContract.SerializeCanonical(OrbitalPassArcQuery.SearchIdentityContent(query.SearchQuery)));
        Assert.AreEqual(query.SearchQuerySha256, Convert.ToHexString(SHA256.HashData(projection)));
        Assert.AreEqual(query.IdentitySha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "query-identity.json")))));
        Assert.AreEqual(query.IdentitySha256, OrbitalPassArcJson.Parse(File.ReadAllBytes(Path.Combine(root, "arc.json"))).QuerySha256);
    }

    [TestMethod]
    public void ArcCanonicalRoundTripFreezesSamplesSegmentsAndParentPopulation()
    {
        var query = ExampleQuery(); var original = ExampleArc(query);
        var samples = original.Samples.ToArray(); var segments = original.Segments.ToArray();
        var exclusions = original.Pass.Lineage.PopulationCoverage.KnownExclusions.ToArray();
        var pass = OrbitalPredictionJson.CreatePass(original.Pass with
        {
            Lineage = original.Pass.Lineage with
            { PopulationCoverage = original.Pass.Lineage.PopulationCoverage with { KnownExclusions = exclusions } }
        });
        var frozen = OrbitalPassArcJson.Create(original with { Samples = samples, Segments = segments, Pass = pass });
        samples[0] = samples[0] with { Pixel = new(1, 2) }; segments[0] = new(0, 1); exclusions[0] = "changed";
        Assert.AreEqual(original.Samples[0], frozen.Samples[0]); Assert.AreEqual(original.Segments[0], frozen.Segments[0]);
        Assert.AreNotEqual("changed", frozen.Pass.Lineage.PopulationCoverage.KnownExclusions[0]);
        var bytes = OrbitalPassArcJson.Serialize(frozen);
        CollectionAssert.AreEqual(bytes, OrbitalPassArcJson.Serialize(OrbitalPassArcJson.Parse(bytes)));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<OrbitalTrackSample>)frozen.Samples).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<OrbitalSkyArcSegmentV1>)frozen.Segments).Clear());
        Assert.IsTrue(bytes.Length < OrbitalPredictionJson.MaximumProductBytes);
    }

    [TestMethod]
    public void EscapedUnicodePopulationMetadataFitsTheConservativeReservedAllowance()
    {
        var arc = ExampleArc(ExampleQuery());
        var population = arc.Pass.Lineage.PopulationCoverage with
        {
            RequestedPopulation = new('\u754c', 256),
            KnownExclusions = Enumerable.Repeat(new string('\u754c', 256), 64).ToArray()
        };
        var pass = OrbitalPredictionJson.CreatePass(arc.Pass with { Lineage = arc.Pass.Lineage with { PopulationCoverage = population } });
        var bytes = OrbitalPassArcJson.Serialize(OrbitalPassArcJson.Create(arc with { Pass = pass }));
        Assert.IsLessThanOrEqualTo(256 * 1024, bytes.Length);
        Assert.AreEqual(64, OrbitalPassArcJson.Parse(bytes).Pass.Lineage.PopulationCoverage.KnownExclusions.Count);
    }

    [TestMethod]
    public void ArcParserRejectsTamperingUnknownDuplicateMissingFieldsAndNoncanonicalBytes()
    {
        var arc = ExampleArc(ExampleQuery()); var json = Encoding.UTF8.GetString(OrbitalPassArcJson.Serialize(arc));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPassArcJson.Parse(Encoding.UTF8.GetBytes(json + "\n")));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPassArcJson.Serialize(arc with { ProductSha256 = new('A', 64) }));
        Assert.ThrowsExactly<JsonException>(() => OrbitalPassArcJson.Parse(Encoding.UTF8.GetBytes("{\"extra\":1," + json[1..])));
        Assert.ThrowsExactly<JsonException>(() => OrbitalPassArcJson.Parse(Encoding.UTF8.GetBytes("{\"schemaVersion\":\"x\"," + json[1..])));
        Assert.ThrowsExactly<JsonException>(() => OrbitalPassArcJson.Parse(Encoding.UTF8.GetBytes(json.Replace("\"samples\":", "\"missingSamples\":", StringComparison.Ordinal))));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPassArcJson.Parse([]));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalPassArcJson.Parse(new byte[OrbitalPredictionJson.MaximumProductBytes + 1]));
        var tooMany = "{\"samples\":[" + string.Join(',', Enumerable.Repeat("null", OrbitalPassArcJson.MaximumMaterializedSamples + 1)) + "]}";
        var bound = Assert.ThrowsExactly<ArgumentException>(() => OrbitalPassArcJson.Parse(Encoding.UTF8.GetBytes(tooMany)));
        StringAssert.Contains(bound.Message, "arc-materialization-bound", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ArcContentRejectsInventedGeometryPhysicalCertaintyAndBrokenContinuity()
    {
        var arc = ExampleArc(ExampleQuery());
        void Reject(OrbitalPassArcV1 changed) => Assert.ThrowsExactly<ArgumentException>(() => OrbitalPassArcJson.Create(changed));
        Reject(arc with { Samples = [arc.Samples[0] with { Pixel = new(1, 1) }, .. arc.Samples.Skip(1)] });
        Reject(arc with { Samples = [arc.Samples[0] with { Illumination = OrbitalIllumination.Sunlit }, .. arc.Samples.Skip(1)] });
        Reject(arc with { Samples = [arc.Samples[0] with { RangeKilometers = double.NaN }, .. arc.Samples.Skip(1)] });
        Reject(arc with { Samples = arc.Samples.Reverse().ToArray() });
        Reject(arc with { Anchors = arc.Anchors with { PeakSampleIndex = 0 } });
        Reject(arc with { OrbitUncertainty = new(0, "degrees", "physical-orbit-error-unqualified") });
        Reject(arc with { Sampling = arc.Sampling with { EstimatorVersion = "pixel-error" } });
        Reject(arc with { Sampling = arc.Sampling with { AchievedEstimatedAngularErrorDegrees = null } });
        Reject(arc with { Segments = [new(0, 1), new(2, 1)] });
        Reject(arc with { Segments = [new(0, 2), new(1, 2)] });
        Reject(arc with { Samples = [arc.Samples[0], arc.Samples[1] with { Horizontal = new(0, 180) }, arc.Samples[2]] });
        Reject(arc with { Samples = [arc.Samples[0], arc.Samples[1], arc.Samples[2] with { Horizontal = new(-60, 181.00001) }] });
        Reject(arc with { Samples = [arc.Samples[0], arc.Samples[1] with { Horizontal = new(60.0000001, 1) }, arc.Samples[2]] });
        Reject(arc with { Samples = new OrbitalTrackSample[OrbitalPassArcJson.MaximumMaterializedSamples + 1] });
        var partial = OrbitalPassArcJson.Create(arc with
        {
            Status = OrbitalExecutionStatus.Partial,
            Reason = OrbitalPredictionReason.IncompleteInterval,
            Segments = [new(0, 1), new(2, 1)]
        });
        Assert.AreEqual(2, partial.Segments.Count);
    }

    [TestMethod]
    public void OriginalSearchIdentityIsExplicitAndBindsBoundsPopulationUseAndParent()
    {
        var q = ExampleQuery(); var r = q.SearchQuery.Request;
        OrbitalPassArcQuery Changed(OrbitalPredictionRequest request) => new(new(request, q.SearchQuery.MinimumElevationDegrees), q.Pass, q.Bounds);
        var bounds = r.Bounds with { MaximumEvaluations = r.Bounds.MaximumEvaluations + 1 };
        var changedBounds = Changed(Request(r, bounds: bounds));
        Assert.AreNotEqual(q.SearchQuerySha256, changedBounds.SearchQuerySha256);
        var added = Changed(Request(r, objects: [.. r.Objects, new("norad", "100001")]));
        Assert.AreNotEqual(q.SearchQuerySha256, added.SearchQuerySha256);
        var measured = Changed(new(OrbitalPredictionUse.MeasuredAssociation, r.Snapshot, r.SelectionPolicy, r.Interval,
            r.Objects, r.Observer, r.Model, r.Bounds));
        Assert.AreNotEqual(q.SearchQuerySha256, measured.SearchQuerySha256);
        var differentArcBounds = new OrbitalPassArcQuery(q.SearchQuery, q.Pass, q.Bounds with { MaximumSamples = 4 });
        Assert.AreEqual(q.SearchQuerySha256, differentArcBounds.SearchQuerySha256);
        Assert.AreNotEqual(q.IdentitySha256, differentArcBounds.IdentitySha256);
        var parent = OrbitalPredictionJson.CreatePass(q.Pass with { MaximumElevationDegrees = 61 });
        Assert.AreNotEqual(q.IdentitySha256, new OrbitalPassArcQuery(q.SearchQuery, parent, q.Bounds).IdentitySha256);
        var projection = JsonSerializer.SerializeToElement(OrbitalPassArcQuery.SearchIdentityContent(q.SearchQuery), OrbitalContract.JsonOptions);
        Assert.AreEqual(OrbitalPassArcQuery.SearchIdentityVersion, projection.GetProperty("schemaVersion").GetString());
        Assert.IsFalse(projection.TryGetProperty("snapshot", out _));
    }

    [TestMethod]
    public void SearchParentMismatchesAndTamperedParentHashesAreRejected()
    {
        var q = ExampleQuery(); var r = q.SearchQuery.Request;
        void Reject(OrbitalPassQuery search, OrbitalPassSummaryV1 pass) =>
            Assert.ThrowsExactly<ArgumentException>(() => new OrbitalPassArcQuery(search, pass, q.Bounds));
        Reject(new(Request(r, observer: new(0, 0, 0)), q.SearchQuery.MinimumElevationDegrees), q.Pass);
        Reject(new(r, 1), q.Pass);
        Reject(new(Request(r, interval: q.Pass.PassInterval), 0), q.Pass);
        Reject(new(Request(r, objects: [new("norad", "100001")]), 0), q.Pass);
        Reject(new(Request(r, snapshot: OrbitalContractExamples.Snapshot(OrbitalContractExamples.Record("100001"))), 0), q.Pass);
        Reject(new(Request(r, model: r.Model with { ObserverHeightDatum = "wgs84-ellipsoidal-assumed" }), 0), q.Pass);
        Reject(new(new(r.Use, r.Snapshot, OrbitalContractExamples.Policy(true), r.Interval, r.Objects, r.Observer, r.Model, r.Bounds), 0), q.Pass);
        Reject(q.SearchQuery, q.Pass with { MaximumElevationDegrees = 61 });
        var wrongRecord = OrbitalPredictionJson.CreatePass(q.Pass with { Lineage = q.Pass.Lineage with { RecordSha256 = new('A', 64) } });
        Reject(q.SearchQuery, wrongRecord);
    }

    [TestMethod]
    public void DedicatedResultEnforcesOneObjectOneMatchingArcAndCannotRelaxOldRoutes()
    {
        var query = ExampleQuery(); var arc = ExampleArc(query);
        OrbitalPredictionResult<OrbitalPassArcV1> Result(IEnumerable<OrbitalPassArcV1> products,
            OrbitalExecutionStatus status = OrbitalExecutionStatus.Complete, OrbitalPredictionReason reason = OrbitalPredictionReason.None,
            int evaluated = 1) => new(query, status, reason, evaluated, 4, 1, products, []);
        var result = Result([arc]);
        Assert.AreEqual(query.Pass.PassInterval, result.RequestedInterval);
        Assert.AreEqual(1, result.RequestedObjects); Assert.AreEqual(query.Pass.Lineage.ObjectIdentity, result.RequestedObjectIdentities[0]);
        Assert.ThrowsExactly<ArgumentException>(() => Result([]));
        Assert.ThrowsExactly<ArgumentException>(() => Result([arc, arc]));
        Assert.ThrowsExactly<ArgumentException>(() => Result([arc], evaluated: 0));
        Assert.ThrowsExactly<ArgumentException>(() => Result([arc], OrbitalExecutionStatus.Unavailable, OrbitalPredictionReason.Deadline));
        var wrong = OrbitalPassArcJson.Create(arc with { QuerySha256 = new('A', 64) });
        Assert.ThrowsExactly<ArgumentException>(() => Result([wrong]));
        var search = OrbitalPassArcJson.Create(arc with { SearchQuerySha256 = new('A', 64) });
        Assert.ThrowsExactly<ArgumentException>(() => Result([search]));
        Assert.ThrowsExactly<ArgumentException>(() => new OrbitalPredictionResult<OrbitalPassSummaryV1>(query,
            OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None, 1, 4, 1, [query.Pass], []));
        Assert.ThrowsExactly<ArgumentException>(() => new OrbitalPredictionResult<OrbitalPassArcV1>(query.SearchQuery,
            OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None, 1, 4, 1, [arc], []));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(.250001)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void AngularBoundsArePositiveFiniteAndAtMostQuarterDegree(double error)
        => Assert.ThrowsExactly<ArgumentException>(() => (ExampleBounds with { MaximumAngularErrorDegrees = error }).Validate());

    internal static OrbitalPassArcBounds ExampleBounds => new(100000, 10000, 600000, 1, .001);

    internal static OrbitalPassArcQuery ExampleQuery()
    {
        var record = OrbitalContractExamples.Record(); var snapshot = OrbitalContractExamples.Snapshot(record);
        var time = OrbitalCoordinateTestData.Load("predicted-2026.finals2000A");
        var model = OrbitalPredictionModel.Create(time);
        var interval = new OrbitalUtcInterval(OrbitalContractExamples.Epoch.AddHours(1), OrbitalContractExamples.Epoch.AddHours(4));
        var query = new OrbitalPassQuery(new(OrbitalPredictionUse.Diagnostic, snapshot, OrbitalContractExamples.Policy(), interval,
            snapshot.ObjectIdentities, OrbitalContractExamples.Observer, model, OrbitalContractExamples.Bounds), 0);
        var start = interval.StartUtc.AddMinutes(1);
        var pass = OrbitalPredictionJson.CreatePass(new(OrbitalPredictionJson.PassSchemaVersion, "", OrbitalContractExamples.Lineage(snapshot, interval),
            interval, new(start, start.AddSeconds(2)), start.AddSeconds(1), 60, 0,
            OrbitalPassBoundary.ResolvedCrossing, OrbitalPassBoundary.ResolvedCrossing, query.Request.Observer, model,
            OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None));
        return new(query, pass, ExampleBounds);
    }

    internal static OrbitalPassArcV1 ExampleArc(OrbitalPassArcQuery query)
        => OrbitalPassArcJson.Create(new(OrbitalPassArcJson.SchemaVersion, "", query.IdentitySha256, query.SearchQuerySha256,
            query.Pass, query.Pass.PassInterval,
            [new(query.Pass.PassInterval.StartUtc, new(0, 0), null, 1000, 0, .1, OrbitalIllumination.Unknown),
             new(query.Pass.PeakUtc, new(60, 1), null, 500, 0, .1, OrbitalIllumination.Unknown),
             new(query.Pass.PassInterval.EndUtc, new(0, 2), null, 1000, 0, .1, OrbitalIllumination.Unknown)],
            [new(0, 3)], new(0, 1, 2), new(1, .001, 1, .0005, OrbitalPassArcJson.InterpolationVersion, OrbitalPassArcJson.EstimatorVersion),
            new(null, "degrees", "physical-orbit-error-unqualified"), OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None));

    private static OrbitalPredictionRequest Request(OrbitalPredictionRequest r, OrbitalQueryBounds? bounds = null,
        IEnumerable<OrbitalObjectIdentity>? objects = null, OrbitalUtcInterval? interval = null, ObserverLocation? observer = null,
        OrbitalSnapshot? snapshot = null, OrbitalPredictionModelFacts? model = null)
        => new(r.Use, snapshot ?? r.Snapshot, r.SelectionPolicy, interval ?? r.Interval, objects ?? r.Objects,
            observer ?? r.Observer, model ?? r.Model, bounds ?? r.Bounds);
}
