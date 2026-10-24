using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing.Tests;

[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[TestClass]
[TestCategory("Unit")]
public sealed class CaptureOrbitalPredictionRecipeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("iss")]
    [DataRow("starlink-56838")]
    [DataRow("starlink-65283")]
    public async Task ApprovedTracksRemainByteExactAcrossIndependentExecutors(string name)
    {
        var fixture = new CaptureOrbitalPredictionFixture(name);
        var input = fixture.Input();
        var first = await fixture.Run(input, cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
        var second = await fixture.Run(input, cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, first.Output.Status);
        CollectionAssert.AreEqual(OrbitalPredictionJson.SerializeTrack(fixture.Track), OrbitalPredictionJson.SerializeTrack(first.Output.Tracks.Single()));
        CollectionAssert.AreEqual(first.Product.Payload.ToArray(), second.Product.Payload.ToArray());
        CollectionAssert.AreEqual(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures",
            $"capture-orbital-{name}-v1.json"), TestContext.CancellationToken).ConfigureAwait(false), first.Product.Payload.ToArray());
        Assert.AreEqual(first.Product.Recipe.IdentitySha256, second.Product.Recipe.IdentitySha256);
        Assert.AreEqual(ProcessingIdentity.ComputePayloadSha256(first.Product.Payload), first.Product.ContentIdentitySha256);
        Assert.AreEqual("Unknown", first.Output.PhysicalVisibility);
        var track = first.Output.Tracks.Single();
        Assert.IsTrue(track.Samples.All(static s => s.Illumination == OrbitalIllumination.Unknown));
        Assert.IsNull(track.Uncertainty.Orbit.Value); Assert.IsNull(track.Uncertainty.Calibration.Value);
        Assert.AreEqual(OrbitalPopulationCompleteness.Partial, first.Output.Input.SnapshotManifest!.Coverage.Completeness);
        Assert.AreEqual(fixture.Track.Lineage.RecordSha256, first.Output.Input.Selections.Single().SelectedRecord!.RecordSha256);
        CollectionAssert.AreEqual(new[] { fixture.Raw.ArtifactId }, first.Product.SourceArtifactIds.ToArray());
    }

    [TestMethod]
    public async Task MissingSnapshotAndMissingObjectRetainExplicitNoResultEvidence()
    {
        var f = new CaptureOrbitalPredictionFixture();
        var unavailable = await f.Run(f.Input(snapshot: OrbitalSnapshotReadResult.Unavailable(OrbitalSnapshotUnavailableReason.NoActiveSnapshot))).ConfigureAwait(false);
        Assert.AreEqual(OrbitalSnapshotUnavailableReason.NoActiveSnapshot, unavailable.Output.Input.SnapshotUnavailableReason);
        Assert.AreEqual(OrbitalExecutionStatus.Unavailable, unavailable.Output.Status);
        Assert.AreEqual(0, unavailable.Output.Tracks.Count);
        var missing = await f.Run(f.Input(objects: [new("norad", "999999999")])).ConfigureAwait(false);
        Assert.AreEqual(OrbitalPredictionReason.ObjectMissing, missing.Output.Reason);
        Assert.AreEqual(OrbitalSelectionEligibility.MissingObject, missing.Output.Input.Selections.Single().Eligibility);
        Assert.AreEqual(OrbitalPredictionReason.ObjectMissing, missing.Output.Failures.Single().Reason);
    }

    [TestMethod]
    public async Task WithheldAndStaleSelectionCandidatesSurviveWithoutTracks()
    {
        var f = new CaptureOrbitalPredictionFixture();
        var withheld = await f.Run(f.Input(policy: new([new("approved:other", 0)]))).ConfigureAwait(false);
        Assert.AreEqual(OrbitalPredictionReason.SelectionWithheld, withheld.Output.Reason);
        Assert.AreEqual(OrbitalSelectionEligibility.UnapprovedSource, withheld.Output.Input.Selections.Single().Eligibility);
        Assert.IsGreaterThan(0, withheld.Output.Input.Selections.Single().Candidates.Count);
        var interval = new OrbitalUtcInterval(f.Track.RequestedInterval.StartUtc.AddDays(2), f.Track.RequestedInterval.EndUtc.AddDays(2));
        var descriptor = f.Descriptor with
        {
            Timing = f.Descriptor.Timing with
            {
                RequestedStartUtc = interval.StartUtc,
                ExposureStartedUtc = interval.StartUtc,
                ExposureEndedUtc = interval.EndUtc,
                ReadoutCompletedUtc = interval.EndUtc.AddMilliseconds(10),
                DurableIngressUtc = interval.EndUtc.AddMilliseconds(20)
            }
        };
        var availability = f.Availability with { OriginalCutoffUtc = descriptor.Timing.DurableIngressUtc, AssessmentCutoffUtc = descriptor.Timing.DurableIngressUtc };
        var input = f.Input(descriptor: descriptor, timing: f.Track.Timing with { Exposure = interval }, availability: availability);
        var stale = input.Plan.Selections.Single();
        Assert.AreEqual(OrbitalSelectionEligibility.StaleWithheld, stale.Eligibility);
        Assert.AreEqual(OrbitalElementFreshness.Stale, stale.Candidates.Single().Freshness);
        var raw = f.Raw with
        {
            DescriptorIdentitySha256 = input.Plan.DescriptorSha256,
            ObservationStartedUtc = interval.StartUtc,
            ObservationEndedUtc = interval.EndUtc
        };
        var executor = new ProcessingRecipeExecutor([new CaptureOrbitalPredictionRecipe(input, f.Time)]);
        var outcome = await executor.ExecuteAsync(f.Request(raw), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        var output = CaptureOrbitalPredictionJson.Parse(outcome.Products.Single().Payload.Span, input);
        Assert.AreEqual(OrbitalPredictionReason.SelectionWithheld, output.Reason);
        Assert.AreEqual(0, output.Tracks.Count);
    }

    [TestMethod]
    public async Task OriginalAndRetrospectiveKnowledgeChangeBothOutputAndRecipeIdentity()
    {
        var f = new CaptureOrbitalPredictionFixture();
        var original = await f.Run().ConfigureAwait(false);
        var input = f.Input(availability: f.Availability with
        {
            Mode = CaptureOrbitalAssessmentMode.RetrospectiveReassessment,
            AssessmentCutoffUtc = f.Availability.OriginalCutoffUtc.AddDays(1)
        });
        var retrospective = await f.Run(input).ConfigureAwait(false);
        Assert.AreNotEqual(original.Product.Recipe.IdentitySha256, retrospective.Product.Recipe.IdentitySha256);
        Assert.AreNotEqual(original.Product.ContentIdentitySha256, retrospective.Product.ContentIdentitySha256);
        Assert.ThrowsExactly<ArgumentException>(() => CaptureOrbitalPredictionJson.Parse(original.Product.Payload.Span, input));
        Assert.ThrowsExactly<ArgumentException>(() => f.Input(availability: f.Availability with
        { TimeDataAvailableUtc = f.Availability.AssessmentCutoffUtc.AddTicks(1) }));
        var late = OrbitalSnapshot.Create(f.Snapshot.Records, f.Snapshot.Manifest.Sources.Select(s => s with
        { RetrievedUtc = f.Availability.OriginalCutoffUtc.AddHours(1) }), f.Snapshot.Manifest.Coverage);
        Assert.ThrowsExactly<ArgumentException>(() => f.Input(snapshot: OrbitalSnapshotReadResult.Available(late)));
        _ = f.Input(snapshot: OrbitalSnapshotReadResult.Available(late), availability: input.Plan.Availability);
    }

    [TestMethod]
    public async Task WrongRawCaptureDescriptorExposureAndGeometryAreRejected()
    {
        var f = new CaptureOrbitalPredictionFixture(); var input = f.Input();
        var executor = new ProcessingRecipeExecutor([new CaptureOrbitalPredictionRecipe(input, f.Time)]);
        var payload = f.Raw.Payload.ToArray(); payload[0] = 1;
        foreach (var raw in new[] { f.Raw with { CaptureId = Guid.NewGuid() }, f.Raw with { ArtifactId = Guid.NewGuid() },
            f.Raw with { DescriptorIdentitySha256 = new('D', 64) }, f.Raw with { ObservationEndedUtc = f.Raw.ObservationEndedUtc!.Value.AddTicks(1) },
            f.Raw with { Payload = payload }, f.Raw with { CaptureSequence = 4 },
            f.Raw with { Layout = f.Raw.Layout! with { Readout = f.Raw.Layout.Readout! with { CfaOriginX = 1 } } } })
        {
            var outcome = await executor.ExecuteAsync(f.Request(raw), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status);
            Assert.AreEqual(0, outcome.Products.Count);
        }
        Assert.ThrowsExactly<ArgumentException>(() => f.Input(geometry: f.Track.Geometry with { RigIdentitySha256 = new('A', 64) }));
        Assert.ThrowsExactly<ArgumentException>(() => f.Input(timing: f.Track.Timing with
        { Exposure = f.Track.RequestedInterval with { EndUtc = f.Track.RequestedInterval.EndUtc.AddTicks(1) } }));
        Assert.ThrowsExactly<ArgumentException>(() => f.Input(geometry: f.Track.Geometry with
        { ImageTransform = f.Track.Geometry.ImageTransform with { BinX = 2 } }));
        var changedObserver = f.Input(observer: new(1, 2, 3));
        var product = (await f.Run(input).ConfigureAwait(false)).Product;
        Assert.ThrowsExactly<ArgumentException>(() => CaptureOrbitalPredictionJson.Parse(product.Payload.Span, changedObserver));
        var other = OrbitalSnapshot.Create(f.Snapshot.Records.Take(1), f.Snapshot.Manifest.Sources, f.Snapshot.Manifest.Coverage);
        Assert.ThrowsExactly<ArgumentException>(() => CaptureOrbitalPredictionJson.Parse(product.Payload.Span,
            f.Input(snapshot: OrbitalSnapshotReadResult.Available(other))));
    }

    [TestMethod]
    public async Task ActualTimeIdentitiesAndCoverageAreRequiredAndGapsRemainNoResult()
    {
        var f = new CaptureOrbitalPredictionFixture();
        var input = f.Input(model: f.Track.Model with { EarthOrientationSha256 = new('A', 64) });
        Assert.ThrowsExactly<ArgumentException>(() => new CaptureOrbitalPredictionRecipe(input, f.Time));
        Assert.ThrowsExactly<ArgumentException>(() => f.Input(model: f.Track.Model with
        { LeapTableCoverage = new(f.Track.RequestedInterval.EndUtc.AddSeconds(1), f.Track.RequestedInterval.EndUtc.AddDays(1)) }));
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nominal");
        var eop = await File.ReadAllLinesAsync(Path.Combine(root, "predicted-2026.finals2000A"), TestContext.CancellationToken).ConfigureAwait(false);
        var leaps = await File.ReadAllBytesAsync(Path.Combine(root, "leap-seconds.list"), TestContext.CancellationToken).ConfigureAwait(false);
        var gap = OrbitalTimeData.FromFinals2000A(Encoding.UTF8.GetBytes(string.Join('\n', eop.Where((_, i) => i != 2)) + "\n"),
            leaps);
        var gapInput = f.Input(model: OrbitalPredictionModel.Create(gap, true));
        var result = await new ProcessingRecipeExecutor([new CaptureOrbitalPredictionRecipe(gapInput, gap)])
            .ExecuteAsync(f.Request(), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status);
        var output = CaptureOrbitalPredictionJson.Parse(result.Products.Single().Payload.Span, gapInput);
        Assert.AreEqual(OrbitalPredictionReason.MissingEarthOrientation, output.Reason);
        Assert.AreEqual(0, output.Tracks.Count);
    }

    [TestMethod]
    [DataRow(1, 8192, "EvaluationBudget")]
    [DataRow(100000, 1, "SampleBudget")]
    public async Task ExhaustedBudgetsRetainBoundedFailure(int evaluations, int samples, string reason)
    {
        var f = new CaptureOrbitalPredictionFixture();
        var result = await f.Run(f.Input(bounds: f.Bounds with { MaximumEvaluations = evaluations, MaximumSamples = samples })).ConfigureAwait(false);
        Assert.AreEqual(reason, result.Output.Reason.ToString());
        Assert.AreNotEqual(OrbitalExecutionStatus.Complete, result.Output.Status);
        Assert.IsLessThanOrEqualTo(evaluations, result.Output.PropagationEvaluations);
        Assert.IsLessThanOrEqualTo(samples, result.Output.Tracks.Sum(static t => t.Samples.Count));
    }

    [TestMethod]
    public async Task PartialPopulationResultBudgetAndDeadlineRemainDistinctFromCoverage()
    {
        var f = new CaptureOrbitalPredictionFixture();
        var objects = new[] { f.Track.Lineage.ObjectIdentity, new OrbitalObjectIdentity("norad", "44714") };
        var input = f.Input(objects: objects, bounds: f.Bounds with { MaximumTracks = 1 });
        var result = await f.Run(input).ConfigureAwait(false);
        Assert.AreEqual(OrbitalPredictionReason.ResultBudget, result.Output.Reason);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, result.Output.Status);
        Assert.AreEqual(1, result.Output.Tracks.Count);
        var deadline = await f.Run(clock: new AdvancingClock()).ConfigureAwait(false);
        Assert.AreEqual(OrbitalPredictionReason.Deadline, deadline.Output.Reason);
        Assert.AreEqual(OrbitalExecutionStatus.Unavailable, deadline.Output.Status);
        Assert.AreEqual(2, result.Output.Input.Objects.Count);
        Assert.ThrowsExactly<ArgumentException>(() => f.Input(objects: Enumerable.Range(1, 257).Select(n => new OrbitalObjectIdentity("norad", n.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            bounds: f.Bounds with { MaximumObjects = 257 }));
    }

    [TestMethod]
    public async Task CancellationNeverPublishesAProduct()
    {
        var f = new CaptureOrbitalPredictionFixture();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await f.Run(cancellationToken: cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        using var during = new CancellationTokenSource();
        var clock = new CancellingClock(during);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await f.Run(clock: clock, cancellationToken: during.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MirrorRotationAndClippingAreAppliedByEngineExactlyOnce()
    {
        var f = new CaptureOrbitalPredictionFixture();
        var g = f.Track.Geometry with
        {
            ImageTransform = f.Track.Geometry.ImageTransform with
            { HorizontalMirror = true, VerticalMirror = true, Rotation = ProjectedSceneQuarterRotation.Degrees180 }
        };
        var input = f.Input(geometry: g);
        var output = await f.Run(input).ConfigureAwait(false);
        var direct = await new OrbitalPredictionEngine(f.Time).PredictExposureAsync(input.Query!, TestContext.CancellationToken).ConfigureAwait(false);
        CollectionAssert.AreEqual(OrbitalPredictionJson.SerializeTrack(direct.Products.Single()), OrbitalPredictionJson.SerializeTrack(output.Output.Tracks.Single()));
        // 180 degrees plus both mirrors is identity. A second application would violate other combinations below.
        foreach (var mirror in new[] { true, false })
        {
            var transformed = f.Input(geometry: g with { ImageTransform = g.ImageTransform with { VerticalMirror = mirror } });
            var actual = await f.Run(transformed).ConfigureAwait(false);
            var baseline = await new OrbitalPredictionEngine(f.Time).PredictExposureAsync(transformed.Query!, TestContext.CancellationToken).ConfigureAwait(false);
            CollectionAssert.AreEqual(OrbitalPredictionJson.SerializeTrack(baseline.Products.Single()), OrbitalPredictionJson.SerializeTrack(actual.Output.Tracks.Single()));
        }
        var readout = f.Track.Geometry.Readout with
        {
            RoiX = 80,
            RoiY = 80,
            RoiWidth = 480,
            RoiHeight = 480,
            BinX = 2,
            BinY = 2,
            BinningAlgorithm = FrameBinningAlgorithm.DigitalSumV1,
            CfaOriginX = null,
            CfaOriginY = null
        };
        var native = f.Track.Geometry with
        {
            ProjectionCoordinates = OrbitalProjectionCoordinates.NativeSensor,
            Readout = readout,
            ReadoutIdentitySha256 = CaptureContractJson.ComputeCanonicalJsonSha256(readout),
            ImageTransform = new(ProjectedSceneImageTransformV1.CurrentSchemaVersion, 640, 640,
                80, 80, 480, 480, 2, 2, false, false, ProjectedSceneQuarterRotation.Degrees0, 240, 240)
        };
        var nativeFixture = new CaptureOrbitalPredictionFixture(geometry: native);
        var nativeResult = await nativeFixture.Run(nativeFixture.Input(geometry: native)).ConfigureAwait(false);
        var projection = native.Projection;
        var view = native with
        {
            ProjectionCoordinates = OrbitalProjectionCoordinates.ReadoutView,
            Projection = projection with
            {
                PrincipalPointX = (projection.PrincipalPointX - 80) / 2,
                PrincipalPointY = (projection.PrincipalPointY - 80) / 2,
                FocalLengthXPixels = projection.FocalLengthXPixels / 2,
                FocalLengthYPixels = projection.FocalLengthYPixels / 2,
                WidthPixels = 240,
                HeightPixels = 240,
                ImageCircleRadiusPixels = projection.ImageCircleRadiusPixels / 2
            },
            ImageTransform = ProjectedSceneImageTransformV1.Identity(240, 240)
        };
        var viewResult = await nativeFixture.Run(nativeFixture.Input(geometry: view)).ConfigureAwait(false);
        var nativePixels = nativeResult.Output.Tracks.Single().Samples.Where(static s => s.Pixel is not null).ToArray();
        var viewPixels = viewResult.Output.Tracks.Single().Samples.Where(static s => s.Pixel is not null).ToArray();
        Assert.AreEqual(nativePixels.Length, viewPixels.Length);
        for (var i = 0; i < nativePixels.Length; i++)
        {
            Assert.AreEqual(nativePixels[i].Utc, viewPixels[i].Utc);
            Assert.AreEqual(nativePixels[i].Pixel!.Value.X, viewPixels[i].Pixel!.Value.X, 1e-10);
            Assert.AreEqual(nativePixels[i].Pixel!.Value.Y, viewPixels[i].Pixel!.Value.Y, 1e-10);
        }
        var clipped = f.Input(geometry: f.Track.Geometry with { Projection = f.Track.Geometry.Projection with { ImageCircleRadiusPixels = 119 } });
        var clips = await f.Run(clipped).ConfigureAwait(false);
        Assert.IsTrue(clips.Output.Tracks.Any(t => t.Segments.Any(s => s.StartReason != OrbitalClipReason.WindowBoundary || s.EndReason != OrbitalClipReason.WindowBoundary)));
        foreach (var track in clips.Output.Tracks)
            foreach (var segment in track.Segments)
                Assert.IsTrue(track.Samples.Skip(segment.StartSampleIndex).Take(segment.SampleCount).All(static s => s.Pixel is not null));
    }

    [TestMethod]
    public async Task CanonicalBoundaryRejectsForgedTracksUnknownFieldsAndWrongSemanticFacts()
    {
        var f = new CaptureOrbitalPredictionFixture(); var input = f.Input();
        var result = await f.Run(input).ConfigureAwait(false);
        Assert.ThrowsExactly<ArgumentException>(() => CaptureOrbitalPredictionJson.Serialize(result.Output with { PhysicalVisibility = "Visible" }, input));
        Assert.ThrowsExactly<ArgumentException>(() => CaptureOrbitalPredictionJson.Serialize(result.Output with
        { Tracks = [result.Output.Tracks.Single() with { ProductSha256 = new('A', 64) }] }, input));
        var json = Encoding.UTF8.GetString(result.Product.Payload.Span);
        Assert.ThrowsExactly<JsonException>(() => CaptureOrbitalPredictionJson.Parse(Encoding.UTF8.GetBytes(json.Insert(1, "\"extra\":1,")), input));
        Assert.ThrowsExactly<ArgumentException>(() => CaptureOrbitalPredictionJson.Parse(Encoding.UTF8.GetBytes(json + "\n"), input));
    }

    [TestMethod]
    public async Task ExactPlanRestoreAndNoTrackGeometryRetainTruthfulInputs()
    {
        var f = new CaptureOrbitalPredictionFixture(); var input = f.Input();
        var restored = CaptureOrbitalPredictionInput.Restore(input.SerializePlan(), OrbitalSnapshotReadResult.Available(f.Snapshot));
        Assert.AreEqual(input.IdentitySha256, restored.IdentitySha256);
        CollectionAssert.AreEqual(input.SerializePlan(), restored.SerializePlan());
        Assert.ThrowsExactly<ArgumentException>(() => CaptureOrbitalPredictionInput.Restore(input.SerializePlan(),
            OrbitalSnapshotReadResult.Unavailable(OrbitalSnapshotUnavailableReason.NoActiveSnapshot)));
        var noTrack = await f.Run(f.Input(geometry: f.Track.Geometry with
        { Projection = f.Track.Geometry.Projection with { ImageCircleRadiusPixels = 1 } })).ConfigureAwait(false);
        Assert.AreEqual(0, noTrack.Output.Tracks.Count);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, noTrack.Output.Status);
        Assert.AreEqual(1, noTrack.Output.Input.Selections.Count);
        var partial = await f.Run(f.Input(bounds: f.Bounds with { MaximumSamples = 3 })).ConfigureAwait(false);
        Assert.AreNotEqual(OrbitalExecutionStatus.Complete, partial.Output.Status);
        foreach (var track in partial.Output.Tracks)
        {
            Assert.AreEqual(OrbitalExecutionStatus.Partial, track.Status);
            Assert.IsLessThan(f.Track.RequestedInterval.EndUtc, track.CoveredInterval.EndUtc);
            Assert.IsTrue(track.Segments.All(s => s.StartSampleIndex + s.SampleCount <= track.Samples.Count));
        }
    }

    private sealed class AdvancingClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Add(ref ticks, 20_000);
    }
    private sealed class CancellingClock(CancellationTokenSource cancellation) : TimeProvider
    {
        public override long GetTimestamp() { cancellation.Cancel(); return 0; }
    }
}
