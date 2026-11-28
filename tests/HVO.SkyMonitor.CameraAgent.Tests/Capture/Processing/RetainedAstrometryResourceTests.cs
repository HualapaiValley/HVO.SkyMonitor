using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    private static readonly JsonSerializerOptions AstrometryEvidenceJson = new() { WriteIndented = true };

    [TestMethod]
    [TestCategory("Unit")]
    [Timeout(120_000)]
    public async Task RetainedAstrometry_FullResolutionCapturePublishesAndAppliesABoundedMapping()
    {
        const int size = 3552;
        var root = CreateTestRoot();
        try
        {
            var projection = new ProjectionContext(ProjectionModel.EquidistantFisheye, size / 2d, size / 2d,
                1000, 1000, size, size, ProjectionAperture.Circular, 1610, 72, 243, 17);
            var reference = await MeasuredAstrometryFixture.CreateAtAsync(0, projectionOverride: projection).ConfigureAwait(false);
            var fixture = await CreateAstrometryDurabilityFixtureAsync(root, reference: reference,
                recipe: MeasuredAstrometryFixture.PerformanceOptions).ConfigureAwait(false);
            using var provider = SiloInfrastructureFixture.OpenStore(root);
            var silo = provider.GetRequiredService<SiloArtifactStorage>();
            var objects = provider.GetRequiredService<SiloTestStore>();
            var rawManifest = fixture.Fixture.Manifest;
            await SiloInfrastructureFixture.PublishArtifactAsync(silo, rawManifest.Descriptor,
                CaptureContractJson.ComputeDescriptorSha256(rawManifest.Descriptor), reference.Raw.Payload,
                SiloArtifactStorage.RawEvidenceRevision, CaptureContractJson.Serialize(rawManifest), CancellationToken.None)
                .ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Fixture.Options, silo: silo);
            using var gate = new CaptureAstrometryExecutionGate();
            using var process = Process.GetCurrentProcess();
            var persistence = new CaptureProcessingPersistence(fixture.Fixture.Options, store, telemetry,
                NullLogger<CaptureProcessingPersistence>.Instance, silo: silo);
            var initialBytes = RetainedBytes();
            var before = Sample();
            var result = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Fixture.Item,
                AstrometryGraph(fixture, new(new ProcessingRecipeExecutor()), gate, persistence: persistence), persistence,
                telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            var afterFit = Sample();
            var addedBytes = RetainedBytes() - initialBytes;
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            var captureId = fixture.Fixture.Manifest.Descriptor.Capture.CaptureId;
            var node = await store.ReadNodeAsync(captureId, "astrometry", CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(node);
            using var artifacts = new CameraAgentArtifactService(fixture.Fixture.Options, store,
                new CameraAgentPreviewEncoder(), silo: silo);
            using var reader = new CameraAgentRetainedAstrometryService(store, artifacts);
            var beforeRead = Sample();
            var view = await reader.GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            var afterRead = Sample();
            Assert.IsNotNull(view.Mapping, view.Reason);
            Assert.AreEqual(AstrometricAssessmentStatus.Accepted, view.Solution!.Assessment.Status);
            Assert.IsLessThan(.5, view.Solution.Residuals.VerificationRmsPixels!.Value);
            var calculator = view.Mapping.CreateCalculator();
            var pixels = Enumerable.Repeat(new PixelPoint(size / 2d, size / 2d), AstrometricCaptureCalculator.MaximumTrackSamples).ToArray();
            var beforeMap = Sample();
            var track = calculator.MapTrack(pixels);
            var afterMap = Sample();
            Assert.IsTrue(track.All(point => point is not null && point.CoordinateEpochUtc == calculator.Frame.MidpointUtc));
            var compactBytes = MeasuredAstrometryJson.Serialize(view.Solution).Length;
            var peakWorkingSetDeltaBytes = Math.Max(afterFit.WorkingSet, Math.Max(afterRead.WorkingSet, afterMap.WorkingSet))
                - before.WorkingSet;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                size,
                rawBytes = reference.Raw.Payload.Length,
                addedRetainedBytes = addedBytes,
                compactBytes,
                view.Solution.Residuals.QualifiedCount,
                view.Solution.Residuals.VerificationRmsPixels,
                solutionIdentity = view.SolutionIdentitySha256,
                fitNodeMilliseconds = node.Duration?.TotalMilliseconds,
                peakWorkingSetDeltaBytes,
                produce = Delta(before, afterFit),
                reconstruct = Delta(beforeRead, afterRead),
                calculate = Delta(beforeMap, afterMap)
            }));
            Assert.IsLessThan(TimeSpan.FromSeconds(30), Stopwatch.GetElapsedTime(before.Ticks, afterFit.Ticks));
            Assert.IsLessThan(TimeSpan.FromSeconds(2), Stopwatch.GetElapsedTime(beforeRead.Ticks, afterRead.Ticks));
            Assert.IsLessThan(TimeSpan.FromSeconds(1), Stopwatch.GetElapsedTime(beforeMap.Ticks, afterMap.Ticks));
            Assert.IsLessThan(1L << 30, afterFit.Allocations - before.Allocations);
            Assert.IsLessThan(128L << 20, afterRead.Allocations - beforeRead.Allocations);
            Assert.IsLessThan(1L << 30, peakWorkingSetDeltaBytes);
            Assert.IsLessThan(12L << 20, addedBytes);
            Assert.IsLessThanOrEqualTo(MeasuredAstrometryJson.MaximumSolutionBytes, compactBytes);

            var source = fixture.Fixture.Item;
            var descriptor = fixture.Fixture.Manifest.Descriptor;
            var frame = source.Submission.Result.Frame!;
            var submission = source.Submission with
            {
                Result = source.Submission.Result with
                {
                    Artifacts = new(new FrameArtifact(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, frame, []))
                }
            };
            var replay = new CaptureProcessingContext(source.Config, submission, source.RawCapture, null,
                new(Guid.NewGuid(), ProcessingGraphExecutionClass.Replay, "frozen-revision", new string('F', 64), false, 1, "lease"));
            foreach (var dependency in new[] { "scene", "measured" })
            {
                var persisted = await store.ReadNodeAsync(captureId, dependency, CancellationToken.None).ConfigureAwait(false);
                var output = persisted!.Outputs.Single();
                var artifact = await ReadExportAsync(output).ConfigureAwait(false);
                var product = new ProcessingProduct(artifact.Role, artifact.Variant, output.OutputIdentitySha256,
                    artifact.MediaType, null, artifact.Payload, output.Artifact.ChecksumSha256,
                    ProcessingIdentity.CreateRecipeIdentity(artifact.RecipeDescriptor!), output.ProductManifest!.Algorithms,
                    output.Artifact.SourceArtifactIds, artifact.Integration, artifact.Compatibility)
                { Kind = artifact.ProductKind, SchemaVersion = artifact.SchemaVersion, ContentIdentitySha256 = artifact.ContentIdentitySha256 };
                replay.RestoreProduct(dependency, artifact.ArtifactId, product);
            }
            var frozen = await Task.WhenAll(node.Outputs.Select(ReadExportAsync)).ConfigureAwait(false);
            replay.BeginNode("astrometry", ["scene", "measured"]);
            replay.SetFrozenAuxiliaryInputs(frozen);
            var replayStep = new MeasuredAstrometryCaptureProcessingStep(new("astrometry", MeasuredAstrometryCaptureProcessingStep.Alias, 2),
                new() { Catalog = fixture.Options.Catalog! with { Complete = false }, Recipe = new(MaximumExecutionMilliseconds: .000001) },
                new(new FixedOutcomeExecutor(ProcessingOutcome.TerminalFailure("unexpected-refit"))), gate);
            await replayStep.ProcessAsync(replay, CancellationToken.None).ConfigureAwait(false);
            var replayed = replay.ProcessingOutcomes.Single().Products.Single(product => product.SchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion);
            CollectionAssert.AreEqual(MeasuredAstrometryJson.Serialize(view.Solution), replayed.Payload.ToArray());
            Assert.AreEqual(view.SolutionIdentitySha256, replayed.ContentIdentitySha256);

            async Task<ProcessingArtifact> ReadExportAsync(DurableProcessingOutput output)
            {
                var manifest = output.ProductManifest!;
                var bytes = await silo.ReadMemberAsync(output.ArtifactId, output.OutputIdentitySha256,
                    manifest.ByteLength, manifest.Artifact.ChecksumSha256, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(bytes);
                var export = new StructuredProcessingProductManifestV1(StructuredProcessingProductManifestV1.CurrentSchemaVersion,
                    new(descriptor, manifest.Artifact, manifest.OutputIdentitySha256, manifest.Algorithms, manifest.Compatibility,
                        manifest.TotalIntegrationTicks, manifest.ByteLength, manifest.Kind, manifest.ProductSchemaVersion!, manifest.ContentIdentitySha256!),
                    manifest.RelativeArtifactPath, null);
                return ImportAstrometryProduct(StructuredProcessingProductManifestJson.Serialize(export), bytes);
            }

            long RetainedBytes() => objects.BytesStored +
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);

            // This class runs serially. Process allocation deltas include work across async continuations.
            (long Ticks, double Cpu, long Allocations, long WorkingSet) Sample()
            {
                process.Refresh();
                return (Stopwatch.GetTimestamp(), process.TotalProcessorTime.TotalMilliseconds, GC.GetTotalAllocatedBytes(true), process.WorkingSet64);
            }

            static object Delta((long Ticks, double Cpu, long Allocations, long WorkingSet) first,
                (long Ticks, double Cpu, long Allocations, long WorkingSet) last) => new
                {
                    elapsedMilliseconds = Stopwatch.GetElapsedTime(first.Ticks, last.Ticks).TotalMilliseconds,
                    cpuMilliseconds = last.Cpu - first.Cpu,
                    allocatedBytes = last.Allocations - first.Allocations,
                    workingSetBytes = last.WorkingSet,
                    workingSetDeltaBytes = last.WorkingSet - first.WorkingSet
                };
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Manual")]
    public async Task RetainedAstrometry_ResourceEvidenceMeasuresBaselineAnalyzerPersistenceAndRead()
    {
        var evidenceRoot = Environment.GetEnvironmentVariable("HVO_ISSUE1643_EVIDENCE_ROOT")
            ?? throw new InvalidOperationException("Set HVO_ISSUE1643_EVIDENCE_ROOT to an hvo scratch directory.");
        Directory.CreateDirectory(evidenceRoot);
        var rows = new List<object>();
        var warmEnabled = new List<(double ProducerMs, long ProducerBytes, double ReadMs, long ReadBytes)>();
        string? acceptedIdentity = null;
        using var process = Process.GetCurrentProcess();
        foreach (var enabled in new[] { false, true })
        {
            for (var index = 0; index < 6; index++)
            {
                var root = Path.Combine(evidenceRoot, $"{(enabled ? "enabled" : "baseline")}-{index}");
                Directory.CreateDirectory(root);
                var f = await CreateAstrometryDurabilityFixtureAsync(root, recipe: MeasuredAstrometryFixture.PerformanceOptions)
                    .ConfigureAwait(false);
                using var telemetry = new CaptureProcessingTelemetry();
                using var store = new SqliteCaptureProcessingStore(f.Fixture.Options);
                using var gate = new CaptureAstrometryExecutionGate();
                var persistence = CreatePersistence(f.Fixture.Options, store, telemetry);
                var before = Sample();
                var started = Stopwatch.GetTimestamp();
                var result = await FrameProcessingWorker.ProcessGraphItemAsync(f.Fixture.Item,
                    AstrometryGraph(f, new(new ProcessingRecipeExecutor()), gate, enabled, persistence), persistence,
                    telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                var producerElapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                var produced = Sample();
                using var artifactReader = new CameraAgentArtifactService(f.Fixture.Options, store, new CameraAgentPreviewEncoder());
                started = Stopwatch.GetTimestamp();
                using var astrometryReader = new CameraAgentRetainedAstrometryService(store, artifactReader);
                var view = await astrometryReader.GetAsync(f.Fixture.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
                var readElapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var read = Sample();
                Assert.AreEqual(enabled ? "Retained" : "NotRetained", view.Availability, view.Reason);
                if (enabled)
                {
                    Assert.AreEqual(AstrometricAssessmentStatus.Accepted, view.Solution!.Assessment.Status, view.Reason);
                    Assert.AreEqual(MeasuredAstrometrySolutionV1.ScientificSelectionVersion, view.Solution.AssociationSelectionVersion);
                    acceptedIdentity ??= view.SolutionIdentitySha256;
                    Assert.AreEqual(acceptedIdentity, view.SolutionIdentitySha256, "Repeated fits must preserve the accepted solution identity.");
                }
                if (enabled && index > 0) warmEnabled.Add((producerElapsed, produced.Allocated - before.Allocated, readElapsed, read.Allocated - produced.Allocated));
                rows.Add(new
                {
                    enabled,
                    index,
                    cold = index == 0,
                    rawDescriptorIdentity = CaptureContractJson.ComputeDescriptorSha256(f.Fixture.Manifest.Descriptor),
                    rawPayloadIdentity = f.Fixture.Manifest.Descriptor.Artifact.ChecksumSha256,
                    recipeOptions = f.Options.Recipe,
                    producerAndPersistence = Delta(before, produced, producerElapsed),
                    read = Delta(produced, read, readElapsed),
                    solutionIdentity = view.SolutionIdentitySha256,
                    solutionBytes = view.Solution is null ? 0 : MeasuredAstrometryJson.Serialize(view.Solution).Length,
                    rootBytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length)
                });
            }
        }
        var path = Path.Combine(evidenceRoot, "retained-astrometry-resources.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schema = "issue-1643-resources-v1",
            environment = new
            {
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount
            },
            bounds = new
            {
                rawBytes = CameraAgentRetainedAstrometryService.MaximumRawBytes,
                readBytes = CameraAgentRetainedAstrometryService.MaximumReadBytes,
                contextBytes = MeasuredAstrometryJson.MaximumContextBytes,
                solutionBytes = MeasuredAstrometryJson.MaximumSolutionBytes,
                fitMilliseconds = 20000,
                captureStride = 60
            },
            comparisonCeilings = new
            {
                producerMilliseconds = 5000,
                producerAllocationBytes = 200L * 1024 * 1024,
                readMilliseconds = 500,
                readAllocationBytes = 100L * 1024 * 1024
            },
            limitation = "512-square generated raw, local filesystem and SQLite; process counters include runtime work. No physical/full-resolution/capture-interference qualification.",
            rows
        }, AstrometryEvidenceJson)).ConfigureAwait(false);
        Console.WriteLine(path);
        Assert.IsTrue(Median(warmEnabled.Select(r => (double)r.ProducerBytes)) <= 200L * 1024 * 1024,
            $"Warm enabled producer median {Median(warmEnabled.Select(r => (double)r.ProducerBytes))} exceeds 209715200 bytes.");
        Assert.IsTrue(Median(warmEnabled.Select(r => r.ProducerMs)) <= 5000, "Producer latency exceeds 5000 ms.");
        Assert.IsTrue(Median(warmEnabled.Select(r => (double)r.ReadBytes)) <= 100L * 1024 * 1024, "Read allocation exceeds 104857600 bytes.");
        Assert.IsTrue(Median(warmEnabled.Select(r => r.ReadMs)) <= 500, "Read latency exceeds 500 ms.");

        static double Median(IEnumerable<double> samples) => samples.Order().ElementAt(2);

        ResourceSample Sample()
        {
            process.Refresh();
            var io = OperatingSystem.IsLinux() ? File.ReadAllLines("/proc/self/io").Select(l => l.Split(':', 2))
                .ToDictionary(p => p[0], p => long.Parse(p[1], CultureInfo.InvariantCulture)) : new Dictionary<string, long>();
            return new(GC.GetTotalAllocatedBytes(true), process.TotalProcessorTime.TotalMilliseconds,
                process.WorkingSet64, process.PeakWorkingSet64, io);
        }
        static object Delta(ResourceSample before, ResourceSample after, double elapsed) => new
        {
            elapsedMilliseconds = elapsed,
            cpuMilliseconds = after.Cpu - before.Cpu,
            allocatedBytes = after.Allocated - before.Allocated,
            workingSetBytes = after.Working,
            workingSetDeltaBytes = after.Working - before.Working,
            peakWorkingSetBytes = after.Peak,
            io = after.Io.ToDictionary(p => p.Key, p => p.Value - before.Io.GetValueOrDefault(p.Key))
        };
    }

    private sealed record ResourceSample(long Allocated, double Cpu, long Working, long Peak, IReadOnlyDictionary<string, long> Io);
}
