using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.VirtualSkyPoc;
using static HVO.SkyMonitor.VirtualSkyPoc.NominalCausalEvaluation;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class NominalCausalEvaluationTests
{
    private static readonly string[] InputOnly = ["input.json"];
    private static readonly int[] GappedSequences = [1, 2, 4, 5, 6];
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public void AllTargetsRetainColdStartEveryComponentAndDeterministicProvenance()
    {
        using var package = new Package();
        package.Write(Enumerable.Range(1, 6).Select(n => package.Frame(n, signals: n == 3 ? 2 : 0)).ToArray());
        File.WriteAllText(Path.Combine(package.Root, "private-truth.json"), "never read");
        var reads = new List<string>();
        var report = Evaluate(package.Root, reads.Add);
        Assert.HasCount(6, report.Frames);
        Assert.AreEqual(TransientTemporalBackgroundStatus.Missing, report.Frames[0].Temporal.Status);
        Assert.AreEqual(TransientTemporalBackgroundStatus.Missing, report.Frames[1].Temporal.Status);
        foreach (var frame in report.Frames.Skip(2))
        {
            Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, frame.Temporal.Status);
            Assert.IsNotNull(frame.Extraction);
            CollectionAssert.AreEqual(new long[] { frame.Raw.CaptureSequence - 2, frame.Raw.CaptureSequence - 1, frame.Raw.CaptureSequence },
                frame.Temporal.Sources.Select(source => source.CaptureSequence).ToArray());
        }
        Assert.IsNull(report.Frames[0].Extraction);
        Assert.IsNull(report.Frames[1].Extraction);
        Assert.HasCount(2, report.Frames[2].Extraction!.Components);
        CollectionAssert.AreEqual(InputOnly.Concat(report.Frames.Select(frame => frame.Raw.Path)).ToArray(), reads);
        Assert.AreEqual(JsonSerializer.Serialize(report, Json), JsonSerializer.Serialize(Evaluate(package.Root), Json));
    }

    [TestMethod]
    public void DroppedSequencesAreRejectedUntilTwoConsecutivePredecessorsExist()
    {
        using var package = new Package();
        package.Write(GappedSequences.Select(n => package.Frame(n)).ToArray());
        var report = Evaluate(package.Root);
        Assert.AreEqual(TransientTemporalBackgroundReasonCodes.SequenceGap, report.Frames[2].Temporal.ReasonCode);
        Assert.AreEqual(TransientTemporalBackgroundReasonCodes.SequenceGap, report.Frames[3].Temporal.ReasonCode);
        Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, report.Frames[4].Temporal.Status);
        CollectionAssert.AreEqual(new long[] { 1, 2, 4 }, report.Frames[2].Temporal.Sources.Select(source => source.CaptureSequence).ToArray());
        package.Write([package.Frame(1), package.Frame(4)]);
        Assert.AreEqual(TransientTemporalBackgroundStatus.Missing, Evaluate(package.Root).Frames[1].Temporal.Status);
    }

    [TestMethod]
    public void TemporalResponseGeometryAndMaskIncompatibilitiesPreserveFactoryReasons()
    {
        using var package = new Package();
        var first = package.Frame(1);
        var second = package.Frame(2);
        var target = package.Frame(3);
        var variants = new[]
        {
            (target with { ObservationStartedUtc = target.ObservationStartedUtc.AddSeconds(20),
                ObservationEndedUtc = target.ObservationEndedUtc.AddSeconds(20) }, TransientTemporalBackgroundReasonCodes.TemporalGap),
            (target with { Sensitivity = new("different-response", 1, 1) }, TransientTemporalBackgroundReasonCodes.IncompatibleResponse),
            (package.Frame(3, width: 32), TransientTemporalBackgroundReasonCodes.IncompatibleLayout),
            (target with { Compatibility = target.Compatibility with { Sensor = "different-sensor" } }, TransientTemporalBackgroundReasonCodes.IncompatibleProfile),
            (target with { Masks = target.Masks with { ImageCircle = new("circle", 32, 32, 30) } }, TransientTemporalBackgroundReasonCodes.IncompatibleMask)
        };
        foreach (var (frame, reason) in variants)
        {
            package.Write([first, second, frame]);
            var actual = Evaluate(package.Root).Frames[2];
            Assert.AreEqual(TransientTemporalBackgroundStatus.Incompatible, actual.Temporal.Status);
            Assert.AreEqual(reason, actual.Temporal.ReasonCode);
            Assert.IsNull(actual.Extraction);
        }
    }

    [TestMethod]
    [DataRow("unexpected")]
    [DataRow("nested")]
    [DataRow("role")]
    [DataRow("format")]
    [DataRow("levels")]
    [DataRow("escape")]
    [DataRow("private")]
    [DataRow("oversized-layout")]
    [DataRow("missing")]
    [DataRow("null")]
    public void InvalidManifestFailsBeforeAnyRawRead(string fault)
    {
        using var package = new Package();
        package.Write([package.Frame(1)]);
        var document = JsonNode.Parse(File.ReadAllBytes(package.Manifest))!;
        var frame = document["frames"]![0]!;
        switch (fault)
        {
            case "unexpected": frame["expectedIdentity"] = "private-object"; break;
            case "nested": frame["compatibility"]!["metadata"] = "private"; break;
            case "role": frame["role"] = "preview"; break;
            case "format": frame["layout"]!["pixelFormat"] = "mono16"; break;
            case "levels": frame["levels"]!["whiteLevel"] = 65535; break;
            case "escape": frame["path"] = "../outside.raw"; break;
            case "private": frame["path"] = "private-truth/raw.raw"; break;
            case "oversized-layout": frame["layout"]!["width"] = int.MaxValue; break;
            case "missing": _ = frame.AsObject().Remove("captureSequence"); break;
            case "null": frame["masks"] = null; break;
        }
        File.WriteAllText(package.Manifest, document.ToJsonString());
        var reads = new List<string>();
        ExpectRejected(() => Evaluate(package.Root, reads.Add));
        CollectionAssert.AreEqual(InputOnly, reads);
    }

    [TestMethod]
    public void DuplicateKeysOversizedManifestAndFrameCountAreRejected()
    {
        using var package = new Package();
        package.Write([package.Frame(1)]);
        File.WriteAllText(package.Manifest, File.ReadAllText(package.Manifest).Replace("\"schema\":", "\"schema\":\"duplicate\",\"schema\":", StringComparison.Ordinal));
        ExpectRejected(() => Evaluate(package.Root));
        File.WriteAllBytes(package.Manifest, new byte[MaximumManifestBytes + 1]);
        ExpectRejected(() => Evaluate(package.Root));
        package.Write(Enumerable.Range(1, MaximumFrames + 1).Select(n => package.Frame(n)).ToArray());
        var reads = new List<string>();
        ExpectRejected(() => Evaluate(package.Root, reads.Add));
        CollectionAssert.AreEqual(InputOnly, reads);
    }

    [TestMethod]
    public void CorruptMissingAndOversizedRawFilesFailAdmission()
    {
        using var package = new Package();
        var frame = package.Frame(1);
        package.Write([frame]);
        var path = Path.Combine(package.Root, frame.Path);
        File.WriteAllBytes(path, new byte[frame.Layout.ByteLength]);
        ExpectRejected(() => Evaluate(package.Root));
        File.WriteAllBytes(path, new byte[frame.Layout.ByteLength + 1]);
        ExpectRejected(() => Evaluate(package.Root));
        File.Delete(path);
        Assert.ThrowsExactly<FileNotFoundException>(() => Evaluate(package.Root));
    }

    [TestMethod]
    public void SymlinkPayloadRootAndPrivateRootAreRejectedBeforeRawOpen()
    {
        using var package = new Package();
        var frame = package.Frame(1);
        package.Write([frame]);
        var path = Path.Combine(package.Root, frame.Path);
        var original = path + ".retained";
        File.Move(path, original);
        _ = File.CreateSymbolicLink(path, original);
        var reads = new List<string>();
        ExpectRejected(() => Evaluate(package.Root, reads.Add));
        CollectionAssert.AreEqual(InputOnly, reads);
        var link = package.Root + "-link";
        try
        {
            _ = Directory.CreateSymbolicLink(link, package.Root);
            ExpectRejected(() => Evaluate(link));
        }
        finally { Directory.Delete(link); }
        var forbidden = Path.Combine(package.Root, "private-truth");
        Directory.CreateDirectory(forbidden);
        File.Copy(package.Manifest, Path.Combine(forbidden, "input.json"));
        ExpectRejected(() => Evaluate(forbidden));
    }

    [TestMethod]
    public void CandidateAndForegroundLimitsRemainExplicit()
    {
        using var package = new Package();
        package.Write([package.Frame(1, width: 256), package.Frame(2, width: 256), package.Frame(3, width: 256, signals: 40)]);
        var report = Evaluate(package.Root);
        Assert.IsTrue(report.Frames[2].Extraction!.CandidateLimitExceeded);
        Assert.Contains("extraction-work-limit", report.Reasons);
        Assert.IsLessThanOrEqualTo(32, report.Frames[2].Extraction!.Components.Count);
        package.Write([package.Frame(1, width: 640), package.Frame(2, width: 640), package.Frame(3, width: 640, signals: -1)]);
        report = Evaluate(package.Root);
        Assert.IsTrue(report.Frames[2].Extraction!.CandidateLimitExceeded);
        Assert.AreEqual(Imaging.Linear16TransientExtractionLimit.ForegroundPixels, report.Frames[2].Extraction!.Limit);
    }

    [TestMethod]
    public void PublicationRefusesOverwriteAndPreservesExactBytes()
    {
        using var package = new Package();
        package.Write([package.Frame(1)]);
        var path = Path.Combine(package.Root, "report.json");
        Publish(path, Evaluate(package.Root));
        var bytes = File.ReadAllBytes(path);
        Assert.ThrowsExactly<IOException>(() => Publish(path, Evaluate(package.Root)));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        var oversized = Path.Combine(package.Root, "oversized.json");
        ExpectRejected(() => Publish(oversized, new string('x', MaximumReportBytes)));
        Assert.IsFalse(File.Exists(oversized));
    }

    [TestMethod]
    public void TransformOffsetsAndUnorderedEndpointsDoNotInventMotion()
    {
        var transform = new TransientDetectorTransformV1("test-affine", .25, .5, 2, -3);
        Assert.AreEqual(new PixelPoint(40, 20), NominalPredictionComparison.ToSource(12, 7, transform));
        PixelPoint[] points = [new(10, 10), new(20, 10), new(30, 10)];
        var forward = NominalPredictionComparison.Residual(0, points[0], points[^1], points);
        var backward = NominalPredictionComparison.Residual(0, points[^1], points[0], points);
        Assert.AreEqual(forward, backward);
        Assert.AreEqual(0, forward.UnorderedMaximumEndpointErrorPixels);
    }

    [TestMethod]
    public void ComparisonRetainsZeroOneAndMultipleCanonicalHypothesesForEveryComponent()
    {
        using var package = new Package();
        var tracks = new[] { Fixture("starlink-56838", 3), Fixture("starlink-65283", 3) };
        var canonical = tracks.Select(bytes => OrbitalPredictionJson.ParseTrack(bytes)).ToArray();
        var frames = Enumerable.Range(1, 3).Select(n => package.Frame(n, width: 640, signals: n == 3 ? 2 : 0) with
        {
            ObservationStartedUtc = canonical[0].RequestedInterval.StartUtc.AddSeconds((n - 3) * 5),
            ObservationEndedUtc = canonical[0].RequestedInterval.EndUtc.AddSeconds((n - 3) * 5),
            GeometryIdentitySha256 = NominalPredictionComparison.GeometryHash(canonical[0].Geometry),
            Observer = canonical[0].Observer,
            Compatibility = package.Frame(n).Compatibility with { Rig = canonical[0].Geometry.RigIdentitySha256 }
        }).ToArray();
        package.Write(frames);
        var evaluation = Path.Combine(package.Root, "evaluation.json");
        Publish(evaluation, Evaluate(package.Root));
        foreach (var count in new[] { 0, 1, 2 })
        {
            package.Predictions(tracks.Take(count));
            var reads = new List<string>();
            var report = NominalPredictionComparison.Compare(evaluation, package.Root, reads.Add);
            Assert.HasCount(2, report.Frames[2].Components);
            foreach (var component in report.Frames[2].Components)
            {
                Assert.HasCount(count, component.Hypotheses);
                Assert.IsTrue(component.Hypotheses.All(hypothesis => hypothesis.Eligible && hypothesis.Segments.Count > 0));
                Assert.IsTrue(component.Hypotheses.All(hypothesis => hypothesis.Reasons.Contains("prediction-population-partial-or-unknown", StringComparer.Ordinal)));
            }
            Assert.IsFalse(reads.Any(path => path.StartsWith("raw/", StringComparison.Ordinal)));
            Assert.AreEqual(count + 2, reads.Count);
            Assert.AreEqual(JsonSerializer.Serialize(report, Json), JsonSerializer.Serialize(NominalPredictionComparison.Compare(evaluation, package.Root), Json));
            Assert.Contains("unavailable", report.MotionFeatures);
        }
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("latitude")]
    [DataRow("longitude")]
    [DataRow("elevation")]
    [DataRow("matched")]
    public void ComparisonRequiresExplicitMatchingCaptureObserver(string variant)
    {
        using var package = new Package();
        var bytes = Fixture("iss", 3);
        var track = OrbitalPredictionJson.ParseTrack(bytes);
        ObserverLocation? observer = variant switch
        {
            "unknown" => null,
            "latitude" => track.Observer with { LatitudeDegrees = track.Observer.LatitudeDegrees + 1 },
            "longitude" => track.Observer with { LongitudeDegrees = track.Observer.LongitudeDegrees + 1 },
            "elevation" => track.Observer with { ElevationMeters = track.Observer.ElevationMeters + 1 },
            _ => track.Observer
        };
        package.Write(Enumerable.Range(1, 3).Select(n => package.Frame(n, width: 640, signals: n == 3 ? 2 : 0) with
        {
            ObservationStartedUtc = track.RequestedInterval.StartUtc.AddSeconds((n - 3) * 5),
            ObservationEndedUtc = track.RequestedInterval.EndUtc.AddSeconds((n - 3) * 5),
            GeometryIdentitySha256 = NominalPredictionComparison.GeometryHash(track.Geometry),
            Observer = observer,
            Compatibility = package.Frame(n).Compatibility with { Rig = track.Geometry.RigIdentitySha256 }
        }).ToArray());
        var path = Path.Combine(package.Root, "evaluation.json");
        Publish(path, Evaluate(package.Root));
        package.Predictions([bytes]);
        var report = NominalPredictionComparison.Compare(path, package.Root);
        Assert.HasCount(2, report.Frames[2].Components);
        foreach (var component in report.Frames[2].Components)
        {
            Assert.HasCount(1, component.Hypotheses);
            var hypothesis = component.Hypotheses[0];
            Assert.AreEqual(variant == "matched", hypothesis.Eligible);
            Assert.AreEqual(variant == "matched", hypothesis.Segments.Count > 0);
            if (variant != "matched")
                Assert.Contains(variant == "unknown" ? "capture-observer-unknown" : "capture-observer-mismatch", hypothesis.Reasons);
        }
    }

    [TestMethod]
    [DataRow(91d, 0d)]
    [DataRow(0d, 181d)]
    [DataRow(-91d, -181d)]
    public void InvalidObserverIsRejectedBeforeRawFilesAreOpened(double latitude, double longitude)
    {
        using var package = new Package();
        package.Write([package.Frame(1) with { Observer = new(latitude, longitude, 0) }]);
        var reads = new List<string>();
        ExpectRejected(() => Evaluate(package.Root, reads.Add));
        CollectionAssert.AreEqual(InputOnly, reads);
    }

    [TestMethod]
    public void WrongTimeUnknownOrMismatchedGeometryWithholdResidualsWithoutDiscardingHypotheses()
    {
        using var package = new Package();
        var bytes = Fixture("iss", 3);
        var track = OrbitalPredictionJson.ParseTrack(bytes);
        package.Predictions([bytes]);
        var source = package.Frame(3, width: 640, signals: 2);
        var variants = new[]
        {
            (source, "exposure-interval-mismatch"),
            (source with { ObservationStartedUtc = track.RequestedInterval.StartUtc, ObservationEndedUtc = track.RequestedInterval.EndUtc }, "capture-geometry-unknown"),
            (source with { ObservationStartedUtc = track.RequestedInterval.StartUtc, ObservationEndedUtc = track.RequestedInterval.EndUtc,
                GeometryIdentitySha256 = new string('A', 64) }, "capture-geometry-mismatch-or-unsupported-transform")
        };
        foreach (var (target, reason) in variants)
        {
            package.Write([package.Frame(1, width: 640) with { ObservationStartedUtc = target.ObservationStartedUtc.AddSeconds(-10), ObservationEndedUtc = target.ObservationEndedUtc.AddSeconds(-10) },
                package.Frame(2, width: 640) with { ObservationStartedUtc = target.ObservationStartedUtc.AddSeconds(-5), ObservationEndedUtc = target.ObservationEndedUtc.AddSeconds(-5) }, target]);
            var path = Path.Combine(package.Root, Guid.NewGuid().ToString("N") + ".json");
            Publish(path, Evaluate(package.Root));
            var report = NominalPredictionComparison.Compare(path, package.Root);
            foreach (var component in report.Frames[2].Components)
            {
                Assert.HasCount(1, component.Hypotheses);
                Assert.IsFalse(component.Hypotheses[0].Eligible);
                Assert.Contains(reason, component.Hypotheses[0].Reasons);
                Assert.IsEmpty(component.Hypotheses[0].Segments);
            }
        }
    }

    [TestMethod]
    public void ComparisonWorkLimitPreservesUnmeasuredAlternativesAndCoverageForEmptyFrames()
    {
        using var package = new Package();
        var bytes = Fixture("iss", 3);
        var track = OrbitalPredictionJson.ParseTrack(bytes);
        package.Write(Enumerable.Range(1, 3).Select(n => package.Frame(n, width: 640, signals: n == 3 ? 2 : 0) with
        {
            ObservationStartedUtc = track.RequestedInterval.StartUtc.AddSeconds((n - 3) * 5),
            ObservationEndedUtc = track.RequestedInterval.EndUtc.AddSeconds((n - 3) * 5),
            GeometryIdentitySha256 = NominalPredictionComparison.GeometryHash(track.Geometry),
            Observer = track.Observer,
            Compatibility = package.Frame(n).Compatibility with { Rig = track.Geometry.RigIdentitySha256 }
        }).ToArray());
        var path = Path.Combine(package.Root, "evaluation.json");
        Publish(path, Evaluate(package.Root));
        package.Predictions([bytes], maximumSegmentComparisons: 1);
        var report = NominalPredictionComparison.Compare(path, package.Root);
        Assert.AreEqual("partial-comparison-work-limit", report.Status);
        Assert.AreEqual(1, report.ComparedSegments);
        Assert.HasCount(2, report.Frames[2].Components);
        Assert.HasCount(1, report.Frames[2].Components[1].Hypotheses);
        Assert.Contains("comparison-work-limit", report.Frames[2].Components[1].Hypotheses[0].Reasons);
        Assert.HasCount(1, report.Frames[0].Coverage);
        Assert.Contains("exposure-interval-mismatch", report.Frames[0].Coverage[0].Reasons);
        Assert.IsEmpty(report.Frames[0].Components);
    }

    [TestMethod]
    public async Task ActualBoundedPredictionPrefixRetainsPartialCoverage()
    {
        using var package = new Package();
        var fixture = OrbitalPredictionJson.ParseTrack(Fixture("iss", 3));
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nominal");
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(path, "snapshot", "manifest.json"), TestContext.CancellationToken).ConfigureAwait(false);
        var recordBytes = await File.ReadAllBytesAsync(Path.Combine(path, "snapshot", "records.json"), TestContext.CancellationToken).ConfigureAwait(false);
        var parsed = OrbitalSnapshot.Parse(manifestBytes, recordBytes);
        Assert.IsNotNull(parsed.Snapshot, parsed.Reason);
        var eopBytes = await File.ReadAllBytesAsync(Path.Combine(path, "predicted-2026.finals2000A"), TestContext.CancellationToken).ConfigureAwait(false);
        var leapBytes = await File.ReadAllBytesAsync(Path.Combine(path, "leap-seconds.list"), TestContext.CancellationToken).ConfigureAwait(false);
        var time = OrbitalTimeData.FromFinals2000A(eopBytes, leapBytes);
        var policy = new OrbitalSelectionPolicy(parsed.Snapshot.Manifest.Sources.Select(source => new OrbitalSourcePriority(source.DatasetKey, 0)));
        var bounds = new OrbitalQueryBounds(1, 60, 1000, 1, 1, 2, 60000, 1, .01);
        var request = new OrbitalPredictionRequest(OrbitalPredictionUse.Diagnostic, parsed.Snapshot, policy,
            fixture.RequestedInterval, [fixture.Lineage.ObjectIdentity], fixture.Observer, fixture.Model, bounds);
        var result = await new OrbitalPredictionEngine(time).PredictExposureAsync(new(request, fixture.Geometry, fixture.Timing),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, result.Status);
        Assert.HasCount(1, result.Products);
        var actual = result.Products[0];
        Assert.AreEqual(OrbitalExecutionStatus.Partial, actual.Status);
        package.Predictions([OrbitalPredictionJson.SerializeTrack(actual)]);
        package.Write(Enumerable.Range(1, 3).Select(n => package.Frame(n, width: 640, signals: n == 3 ? 2 : 0) with
        {
            ObservationStartedUtc = actual.RequestedInterval.StartUtc.AddSeconds((n - 3) * 5),
            ObservationEndedUtc = actual.RequestedInterval.EndUtc.AddSeconds((n - 3) * 5),
            GeometryIdentitySha256 = NominalPredictionComparison.GeometryHash(actual.Geometry),
            Observer = actual.Observer,
            Compatibility = package.Frame(n).Compatibility with { Rig = actual.Geometry.RigIdentitySha256 }
        }).ToArray());
        var evaluation = Path.Combine(package.Root, "evaluation.json");
        Publish(evaluation, Evaluate(package.Root));
        var comparison = NominalPredictionComparison.Compare(evaluation, package.Root);
        Assert.Contains("prediction-interval-partial", comparison.Frames[2].Coverage[0].Reasons);
        Assert.IsTrue(comparison.Frames[2].Components.All(component => component.Hypotheses[0].Eligible &&
            component.Hypotheses[0].Reasons.Contains("prediction-interval-partial", StringComparer.Ordinal)));
        Assert.AreEqual(actual.ProductSha256, comparison.Predictions[0].Product.ProductSha256);
    }

    [TestMethod]
    public void ComparisonRejectsInconsistentLineageAndUnexpectedOrCorruptPredictionInputs()
    {
        using var package = new Package();
        package.Write([package.Frame(1), package.Frame(2), package.Frame(3, signals: 2)]);
        var report = Evaluate(package.Root);
        var changed = report.Frames[2] with { Raw = report.Frames[2].Raw with { RecipeIdentitySha256 = new string('B', 64) } };
        var changedPath = Path.Combine(package.Root, "changed.json");
        Publish(changedPath, report with { Frames = report.Frames.Take(2).Append(changed).ToArray() });
        var reads = new List<string>();
        ExpectRejected(() => NominalPredictionComparison.Compare(changedPath, package.Root, reads.Add));
        Assert.HasCount(1, reads);
        var path = Path.Combine(package.Root, "evaluation.json");
        Publish(path, report);
        package.Predictions([Fixture("iss", 3)]);
        var manifestPath = Path.Combine(package.Root, "predictions.json");
        var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!;
        manifest["expectedIdentity"] = "forbidden";
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        ExpectRejected(() => NominalPredictionComparison.Compare(path, package.Root));
        package.Predictions([Fixture("iss", 3)]);
        var file = Parse<NominalPredictionManifest>(File.ReadAllBytes(manifestPath)).Tracks[0];
        File.WriteAllText(Path.Combine(package.Root, file.Path), "{}");
        ExpectRejected(() => NominalPredictionComparison.Compare(path, package.Root));
    }

    private static byte[] Fixture(string group, int frame)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nominal", group, $"track-{frame:D2}.json"));

    private static void ExpectRejected(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (JsonException) { return; }
        Assert.Fail("Input should be rejected.");
    }

    private sealed class Package : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "hvo-nominal-" + Guid.NewGuid().ToString("N"));
        internal string Manifest => Path.Combine(Root, "input.json");
        private static readonly DateTimeOffset Start = new(2026, 10, 20, 13, 0, 0, TimeSpan.Zero);

        internal Package() => Directory.CreateDirectory(Path.Combine(Root, "raw"));

        internal NominalRawFrame Frame(int sequence, int width = 64, int signals = 0)
        {
            var bytes = new byte[width * width * 2];
            for (var pixel = 0; pixel < width * width; pixel++)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(pixel * 2), signals == -1 ? (ushort)500 : (ushort)64);
            for (var signal = 0; signal < signals; signal++)
            {
                var left = 4 + signal % 8 * 12;
                var top = 4 + signal / 8 * 12;
                for (var y = top * 2; y < (top + 3) * 2; y++)
                    for (var x = left * 2; x < (left + 3) * 2; x++)
                        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((y * width + x) * 2), 500);
            }
            var hash = Hash(bytes);
            var relative = "raw/" + hash + ".raw";
            File.WriteAllBytes(Path.Combine(Root, relative), bytes);
            return new(new Guid(sequence, 0, 0, new byte[8]), sequence, "raw", relative, hash, new string('A', 64),
                Start.AddSeconds((sequence - 1) * 5), Start.AddSeconds(sequence * 5), TransientTimingQuality.Estimated,
                new(width, width, width * 2, CameraPixelFormat.BayerRggb16, FrameByteOrder.LittleEndian, 12, 16,
                    FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb, 64, 4095, bytes.Length)
                { StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1, LevelCodeSpace = FrameLevelCodeSpace.NativeSample },
                new(64, 4095, 4095), new("nominal-rig", "orientation", "raw", "empty", "native12", "fixed", "nominal"),
                new("response", 1, 1), new("explicit-empty-v1", "explicit-empty-v1", "explicit-empty-v1", "explicit-empty-v1",
                    "explicit-empty-v1", new("empty", null, null, null)), null, null);
        }

        internal void Write(IReadOnlyList<NominalRawFrame> frames)
            => File.WriteAllBytes(Manifest, JsonSerializer.SerializeToUtf8Bytes(new NominalRawManifest("nominal-causal-raw-v1", 5.001, frames), Json));

        internal void Predictions(IEnumerable<byte[]> tracks, int maximumSegmentComparisons = NominalPredictionComparison.MaximumSegmentComparisons)
        {
            Directory.CreateDirectory(Path.Combine(Root, "tracks"));
            var files = tracks.Select(bytes =>
            {
                var hash = Hash(bytes);
                var relative = "tracks/" + hash + ".json";
                File.WriteAllBytes(Path.Combine(Root, relative), bytes);
                return new NominalTrackFile(relative, hash);
            }).ToArray();
            File.WriteAllBytes(Path.Combine(Root, "predictions.json"), JsonSerializer.SerializeToUtf8Bytes(
                new NominalPredictionManifest("nominal-canonical-predictions-v1", maximumSegmentComparisons, files), Json));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
