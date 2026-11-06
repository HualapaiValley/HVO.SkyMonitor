using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CaptureSatelliteProductTests
{
    [TestMethod]
    public async Task CompactCarrierRequiresTheExactRetainedSnapshotAndReconstructsTheSameResult()
    {
        var f = new CaptureSatelliteFixture();
        var compact = f.Evidence with { SnapshotRecords = null };
        Assert.IsLessThan(CaptureSatelliteProducts.SerializeInput(f.Evidence).Length,
            CaptureSatelliteProducts.SerializeInput(compact).Length);
        Assert.ThrowsExactly<ArgumentNullException>(() => CaptureSatelliteProducts.Restore(compact));
        var other = OrbitalSnapshot.Create(f.Snapshot.Records.Take(1), f.Snapshot.Manifest.Sources, f.Snapshot.Manifest.Coverage);
        Assert.ThrowsExactly<ArgumentException>(() => CaptureSatelliteProducts.Restore(compact, OrbitalSnapshotReadResult.Available(other)));
        var embedded = await CaptureSatelliteProducts.PredictAsync(f.Evidence, f.Raw, "satellites", CancellationToken.None).ConfigureAwait(false);
        var resolved = await CaptureSatelliteProducts.PredictAsync(compact, f.Raw, "satellites", CancellationToken.None,
            exactSnapshot: OrbitalSnapshotReadResult.Available(f.Snapshot)).ConfigureAwait(false);
        Assert.IsNotNull(embedded); Assert.IsNotNull(resolved);
        Assert.AreEqual(embedded.OutputIdentitySha256, resolved.OutputIdentitySha256);
        CollectionAssert.AreEqual(embedded.Payload.ToArray(), resolved.Payload.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StaleSelectionWithholdingAndDiagnosticFreshnessSurvivePublication(bool allowStale)
    {
        var f = new CaptureSatelliteFixture();
        var original = f.Input.Plan;
        var d = f.Descriptor with
        {
            Timing = f.Descriptor.Timing with
            {
                RequestedStartUtc = f.Descriptor.Timing.RequestedStartUtc.AddDays(3),
                ExposureStartedUtc = f.Descriptor.Timing.ExposureStartedUtc.AddDays(3),
                ExposureEndedUtc = f.Descriptor.Timing.ExposureEndedUtc.AddDays(3),
                ReadoutCompletedUtc = f.Descriptor.Timing.ReadoutCompletedUtc.AddDays(3),
                DurableIngressUtc = f.Descriptor.Timing.DurableIngressUtc.AddDays(3)
            },
            Artifact = f.Descriptor.Artifact with { CreatedUtc = f.Descriptor.Artifact.CreatedUtc.AddDays(3) }
        };
        var timing = original.Timing with
        {
            Exposure = new(d.Timing.ExposureStartedUtc, d.Timing.ExposureEndedUtc),
            ReadoutCompletedUtc = original.Timing.ReadoutCompletedUtc?.AddDays(3)
        };
        var availability = original.Availability with { OriginalCutoffUtc = d.Timing.DurableIngressUtc, AssessmentCutoffUtc = d.Timing.DurableIngressUtc };
        var input = new CaptureOrbitalPredictionInput(d, original.Observer, original.Geometry, timing, original.Model,
            original.Bounds, availability, OrbitalSnapshotReadResult.Available(f.Snapshot),
            new(original.SelectionPolicy.Sources, allowStale), original.Objects);
        var evidence = f.Evidence with { Descriptor = d, Plan = input.SerializePlan() };
        var raw = f.Raw with
        {
            CreatedUtc = d.Artifact.CreatedUtc,
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(d),
            ObservationStartedUtc = d.Timing.ExposureStartedUtc,
            ObservationEndedUtc = d.Timing.ExposureEndedUtc
        };
        var result = await CaptureSatelliteProducts.PredictAsync(evidence, raw, "satellites", CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(result);
        var products = CaptureSatelliteProducts.Create(evidence, raw, "satellites", result.Payload);
        var status = CaptureSatelliteProducts.ParseStatus(products.Single(p => p.SchemaVersion == CaptureSatelliteProducts.StatusSchema).Payload.Span);
        StringAssert.Contains(status.ElementFreshness, "Stale", StringComparison.Ordinal);
        Assert.IsGreaterThan(48d, status.MaximumEpochAgeHours!.Value);
        if (!allowStale)
        {
            Assert.AreEqual("Unavailable", status.Status);
            Assert.AreEqual("SelectionWithheld", status.Reason);
            Assert.IsFalse(status.Renderable);
        }
    }

    [TestMethod]
    public async Task PartialQueryQualifiesEvenItsCompleteUsefulTrackPrefix()
    {
        var f = new CaptureSatelliteFixture();
        var p = f.Input.Plan;
        var input = new CaptureOrbitalPredictionInput(p.Descriptor, p.Observer, p.Geometry, p.Timing, p.Model, p.Bounds,
            p.Availability, OrbitalSnapshotReadResult.Available(f.Snapshot),
            new(p.SelectionPolicy.Sources), [new("norad", "25544"), new("norad", "999999999")]);
        var evidence = f.Evidence with { Plan = input.SerializePlan() };
        var product = await CaptureSatelliteProducts.PredictAsync(evidence, f.Raw, "satellites", CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(product);
        var output = CaptureOrbitalPredictionJson.Parse(product.Payload.Span, input);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, output.Status);
        Assert.HasCount(1, output.Tracks);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, output.Tracks[0].Status);
        var products = CaptureSatelliteProducts.Create(evidence, f.Raw, "satellites", product.Payload);
        var layer = PresentationLayerPayloadJson.Parse(products[^1].Payload).Payload!;
        Assert.IsTrue(layer.TextBlocks.SelectMany(static block => block.Lines).Contains(OrbitalTrackPresentation.PartialLegend));
    }

    [TestMethod]
    public async Task PortableEvidenceReconstructsExactCanonicalProductsAndLeavesRawUntouched()
    {
        var fixture = new CaptureSatelliteFixture();
        var checksum = ProcessingIdentity.ComputePayloadSha256(fixture.Raw.Payload);
        var result = await CaptureSatelliteProducts.PredictAsync(fixture.Evidence, fixture.Raw, "satellites", CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(result);
        var output = CaptureOrbitalPredictionJson.Parse(result.Payload.Span, fixture.Input);
        Assert.HasCount(1, output.Tracks);
        var first = CaptureSatelliteProducts.Create(fixture.Evidence, fixture.Raw, "satellites", result.Payload);
        var portable = CaptureSatelliteProducts.ParseInput(CaptureSatelliteProducts.SerializeInput(fixture.Evidence));
        var independentlyProduced = await CaptureSatelliteProducts.PredictAsync(portable, fixture.Raw, "satellites", CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(independentlyProduced);
        CollectionAssert.AreEqual(result.Payload.ToArray(), independentlyProduced.Payload.ToArray());
        var second = CaptureSatelliteProducts.Create(portable, fixture.Raw, "satellites", result.Payload);
        Assert.HasCount(5, first);
        foreach (var (a, b) in first.Zip(second))
        {
            Assert.AreEqual(a.OutputIdentitySha256, b.OutputIdentitySha256);
            Assert.AreEqual(a.ChecksumSha256, b.ChecksumSha256);
            CollectionAssert.AreEqual(a.Payload.ToArray(), b.Payload.ToArray());
        }
        var layer = PresentationLayerPayloadJson.Parse(first[^1].Payload).Payload!;
        var expected = OrbitalTrackPresentation.CreatePayloadAndSourceFacts(640, 640, output.Tracks);
        Assert.AreEqual(expected.Payload.ContentIdentitySha256, layer.ContentIdentitySha256);
        CollectionAssert.AreEqual(expected.SourceFactsJson, first.Single(p => p.SchemaVersion == CaptureSatelliteProducts.SourceSchema).Payload.ToArray());
        Assert.AreEqual(checksum, ProcessingIdentity.ComputePayloadSha256(fixture.Raw.Payload));
        Assert.IsTrue(CaptureSatelliteProducts.ParseStatus(first.Single(p => p.SchemaVersion == CaptureSatelliteProducts.StatusSchema).Payload.Span).Renderable);
    }

    [TestMethod]
    public void ChangedRigSnapshotTimeOrRawEvidenceCannotMasqueradeAsTheOriginal()
    {
        var f = new CaptureSatelliteFixture();
        Assert.ThrowsExactly<ArgumentException>(() => CaptureSatelliteProducts.SerializeInput(f.Evidence with
        { Rig = f.Configuration.Rig with { ProfileVersion = "changed" } }));
        Assert.ThrowsExactly<ArgumentException>(() => CaptureSatelliteProducts.SerializeInput(f.Evidence with { LeapSeconds = new byte[] { 0 } }));
        Assert.ThrowsExactly<ArgumentException>(() => CaptureSatelliteProducts.SerializeInput(f.Evidence with { SnapshotRecords = new byte[] { 0 } }));
        Assert.ThrowsExactly<ArgumentException>(() => CaptureSatelliteProducts.SerializeInput(f.Evidence with
        { Observer = new ObserverLocation(0, 0, 0) }));
        var mutated = f.Raw.Payload.ToArray();
        mutated[0] = 1;
        Assert.ThrowsExactly<ArgumentException>(() => CaptureSatelliteProducts.Create(f.Evidence, f.Raw with { Payload = mutated }, "satellites", null));
    }

    [TestMethod]
    [DataRow("TimeCoverageUnavailable")]
    [DataRow("TimeAvailabilityUnknown")]
    [DataRow("SnapshotRetiredBeforePin")]
    public void UnavailableInputsRetainStatusAndEmptyGeometryWithoutFabricatedTracks(string reason)
    {
        var f = new CaptureSatelliteFixture();
        var input = f.Evidence with { Plan = null, UnavailableReason = reason };
        var products = CaptureSatelliteProducts.Create(input, f.Raw, "satellites", null);
        var status = CaptureSatelliteProducts.ParseStatus(products.Single(p => p.SchemaVersion == CaptureSatelliteProducts.StatusSchema).Payload.Span);
        Assert.AreEqual(reason, status.Reason);
        Assert.AreEqual("Unavailable", status.Status);
        Assert.IsFalse(status.Renderable);
        Assert.AreEqual(0, status.TrackCount);
        Assert.AreEqual(OrbitalPopulationCompleteness.Partial, status.Coverage.Completeness);
        Assert.IsNotNull(status.SourceRetrievedUtc);
        var layer = PresentationLayerPayloadJson.Parse(products[^1].Payload).Payload!;
        Assert.IsEmpty(layer.Segments);
        Assert.IsEmpty(layer.Markers);
    }
}
