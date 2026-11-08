using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow("missing", false)]
    [DataRow("clear", false)]
    [DataRow("clear", true)]
    [DataRow("partial", false)]
    [DataRow("partial", true)]
    [DataRow("overcast", false)]
    [DataRow("overcast", true)]
    [DataRow("incompatible", false)]
    [DataRow("corrupt", false)]
    public async Task ShippedCloudProfileRetainsVirtualSkyEvidenceAndIsolatesUnavailableReference(string scenario, bool fullFrame)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-cloud-profile");
        try
        {
            var source = await CloudProfileAcceptanceTests.LoadAsync().ConfigureAwait(false);
            Assert.IsTrue(source.Pipeline.Steps.Any(step => step.Type == "CloudAssessment"), "The shipped graph must enable cloud assessment.");
            if (fullFrame)
            {
                source = source with
                {
                    Rig = source.Rig with
                    {
                        Sensor = source.Rig.Sensor with { WidthPixels = 1936, HeightPixels = 1216, StrideBytes = 3872 },
                        Readout = source.Rig.Readout! with { Roi = new SensorCrop(0, 0, 1936, 1216), StrideBytes = 3872 },
                        Optics = source.Rig.Optics with { PrincipalPointX = 968, PrincipalPointY = 608, ImageCircleRadiusPixels = 595.84 }
                    },
                    Pipeline = source.Pipeline with
                    {
                        Steps = source.Pipeline.Steps.Select(step => step.Type is "CloudPresentationLayer" or "EnvironmentPresentationLayer"
                            ? step with { Options = Resize(step.Options!.Value) } : step).ToArray()
                    }
                };
            }
            // Same graph/rig/setpoints for both captures. Scenario truth stays inside the camera renderer.
            var options = JsonNode.Parse(source.Module.Options!.Value.GetRawText())!.AsObject();
            options["includeConstellationEndpointStars"] = false;
            options["constellationIds"] = new JsonArray();
            options["fixedSceneUtc"] = "2026-01-15T08:00:00Z";
            options["backgroundElectronsPerSecond"] = 100;
            options["vignettingStrength"] = 0.4;
            options["shotNoiseEnabled"] = false;
            options["readNoiseStandardDeviation"] = 0;
            var config = source with
            {
                Module = source.Module with { Options = JsonSerializer.SerializeToElement(options) },
                Pipeline = source.Pipeline with
                {
                    Steps = source.Pipeline.Steps.Select(step => step.Type == "Storage"
                        ? step with { Options = JsonSerializer.SerializeToElement(new { storageRoot = root, queueForUpload = false }) }
                        : step).ToArray()
                }
            };
            using var provider = CreateProvider(root, configureServices: services =>
            {
                services.AddSingleton<IObservingDayCalendarProvider>(new FixedObservingDayCalendarProvider(ObservingDayCalendar.Utc));
                services.AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>();
            });
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(config);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
                .EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var catalog = new InMemoryCelestialCatalog(Enumerable.Range(0, 360).Select(index =>
                new CelestialCatalogObject($"cloud-star-{index}", $"Cloud star {index}", (index % 24), -60 + (index / 24) * 10, 1 + index % 5)));
            var reference = await CaptureAsync(config, 0).ConfigureAwait(false);
            // First frame must finish without creating a fabricated clear reference or losing the image path.
            await CompleteLiveAsync(provider, config).ConfigureAwait(false);
            var first = await provider.GetRequiredService<ICameraAgentGallery>()
                .GetCaptureAsync(reference.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("InsufficientEvidence", first!.Detail!.CloudAssessment.Status);
            Assert.IsNull(first.Detail.CloudAssessment.CoverageMillionths);
            CollectionAssert.Contains(first.Detail.CloudAssessment.ReasonCodes.ToArray(), CloudAssessmentReasonCodes.MissingClearReference);
            var referencePath = Path.Combine(root, "references", "cloud", "clear-reference.manifest.json");
            if (scenario != "missing")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
                await File.WriteAllBytesAsync(referencePath, scenario == "corrupt"
                    ? "invalid reference"u8.ToArray()
                    : CaptureContractJson.Serialize(reference.Manifest)).ConfigureAwait(false);
            }
            if (scenario is "partial" or "overcast")
            {
                options["cloudScenario"] = JsonSerializer.SerializeToNode(new VirtualCloudScenarioDefinition
                {
                    ScenarioId = "cloud-profile-acceptance",
                    ScenarioVersion = "1",
                    Seed = 1004,
                    EpochUtc = DateTimeOffset.Parse("2026-01-15T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                    SpatialFrequency = 3,
                    Octaves = 2,
                    EdgeSoftness = 0.01,
                    HorizonFadeDegrees = 0,
                    TemporalSampleCount = 1,
                    Keyframes = [new() { Coverage = scenario == "partial" ? 0.5 : 1, MaximumOpacity = 0.9, ScatterFraction = 0 }]
                });
            }
            var currentConfig = config with { Module = config.Module with { Options = JsonSerializer.SerializeToElement(options) } };
            var current = await CaptureAsync(currentConfig, 1, scenario == "incompatible").ConfigureAwait(false);
            var currentHash = SHA256.HashData(await File.ReadAllBytesAsync(current.StoredFrame.AbsolutePath).ConfigureAwait(false));
            var referenceHash = SHA256.HashData(await File.ReadAllBytesAsync(reference.StoredFrame.AbsolutePath).ConfigureAwait(false));
            var timer = Stopwatch.StartNew();
            await CompleteLiveAsync(provider, currentConfig).ConfigureAwait(false);
            var elapsed = timer.Elapsed;
            var captureId = current.Manifest.Descriptor.Capture.CaptureId;
            var gallery = await provider.GetRequiredService<ICameraAgentGallery>()
                .GetCaptureAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(gallery);
            Assert.IsTrue(gallery.Artifacts.Any(artifact => artifact.Role == FrameArtifactRole.Raw));
            Assert.IsTrue(gallery.Artifacts.Any(artifact => artifact.Role == FrameArtifactRole.AnnotatedPreview));
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var cloud = await store.ReadNodeAsync(captureId, "CloudAssessment", CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(cloud);
            if (scenario == "corrupt")
            {
                Assert.AreNotEqual(DurableProcessingNodeStatus.Completed, cloud.Status);
                Assert.IsNull(gallery.Detail!.CloudAssessment.CoverageMillionths);
            }
            else
            {
                Assert.AreEqual(DurableProcessingNodeStatus.Completed, cloud.Status, cloud.Reason);
                var output = cloud.Outputs.Single();
                var payload = await File.ReadAllBytesAsync(Path.Combine(root, output.PayloadRelativePath)).ConfigureAwait(false);
                Assert.AreEqual(output.ProductManifest!.Artifact.ChecksumSha256, ProcessingIdentity.ComputePayloadSha256(payload));
                var assessment = CloudAssessmentJson.Parse(payload).Assessment!;
                Assert.AreEqual(current.Manifest.Descriptor.Artifact.ArtifactId, assessment.Current.ArtifactId);
                if (scenario is "missing" or "incompatible")
                {
                    Assert.AreEqual(CloudAssessmentStatus.InsufficientEvidence, assessment.Status);
                    Assert.IsNull(assessment.CoverageMillionths);
                    CollectionAssert.Contains(assessment.ReasonCodes.ToArray(), scenario == "missing"
                        ? CloudAssessmentReasonCodes.MissingClearReference : CloudAssessmentReasonCodes.IncompatibleClearReference);
                }
                else
                {
                    Assert.AreEqual(CloudAssessmentStatus.Quantified, assessment.Status, string.Join(",", assessment.ReasonCodes));
                    Assert.AreEqual(reference.Manifest.Descriptor.Artifact.ArtifactId, assessment.ClearReference!.ArtifactId);
                    Assert.IsNotNull(assessment.Mask);
                    CollectionAssert.AreEqual(
                        new[] { current.Manifest.Descriptor.Artifact.ArtifactId, reference.Manifest.Descriptor.Artifact.ArtifactId },
                        (await store.ReadOutputSourcesAsync(output.OutputIdentitySha256, 3, CancellationToken.None).ConfigureAwait(false))
                            .Select(item => item.ArtifactId).ToArray());
                    Assert.AreEqual(assessment.CoverageMillionths, gallery.Detail!.CloudAssessment.CoverageMillionths);
                    if (scenario == "clear") Assert.AreEqual(0, assessment.CoverageMillionths);
                    if (scenario == "partial") Assert.IsTrue(assessment.CoverageMillionths is > 0 and < 1_000_000);
                    if (scenario == "overcast") Assert.AreEqual(1_000_000, assessment.CoverageMillionths);
                }
                var presentation = await provider.GetRequiredService<ICameraAgentLayeredPresentationService>()
                    .GetAsync(captureId, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, presentation.Status, presentation.Reason);
                var quantified = assessment.Status == CloudAssessmentStatus.Quantified;
                Assert.AreEqual(quantified, presentation.Presentation!.Layers.Any(layer => layer.Kind == "cloud-mask"));
                Assert.AreEqual(quantified, presentation.Presentation.Layers.Any(layer => layer.Kind == "cloud-labels"));
                Assert.IsLessThanOrEqualTo(1024 * 1024, payload.Length);
                Console.WriteLine($"{scenario} fullFrame={fullFrame}: coverage={assessment.CoverageMillionths}; identity={assessment.AssessmentIdentitySha256}; processingMs={elapsed.TotalMilliseconds:F1}; payloadBytes={payload.Length}");
                if (Environment.GetEnvironmentVariable("HVO_CLOUD_ACCEPTANCE_OUTPUT") is { Length: > 0 } evidenceRoot)
                {
                    var directory = Path.Combine(evidenceRoot, $"{scenario}-{(fullFrame ? "full" : "sample")}");
                    Directory.CreateDirectory(directory);
                    await File.WriteAllBytesAsync(Path.Combine(directory, "assessment.json"), payload).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(Path.Combine(directory, "layers.svg"), presentation.Presentation.Svg.ToArray()).ConfigureAwait(false);
                    var preview = await provider.GetRequiredService<ICameraAgentArtifactService>()
                        .GetPreviewAsync(presentation.Presentation.BaseArtifactId, CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual(CameraAgentArtifactReadStatus.Found, preview.Status);
                    await File.WriteAllBytesAsync(Path.Combine(directory, "base.jpg"), preview.Content.ToArray()).ConfigureAwait(false);
                }
            }
            CollectionAssert.AreEqual(currentHash, SHA256.HashData(await File.ReadAllBytesAsync(current.StoredFrame.AbsolutePath).ConfigureAwait(false)));
            CollectionAssert.AreEqual(referenceHash, SHA256.HashData(await File.ReadAllBytesAsync(reference.StoredFrame.AbsolutePath).ConfigureAwait(false)));

            static JsonElement Resize(JsonElement sourceOptions)
            {
                var resized = JsonNode.Parse(sourceOptions.GetRawText())!.AsObject();
                resized["widthPixels"] = 1936;
                resized["heightPixels"] = 1216;
                return JsonSerializer.SerializeToElement(resized);
            }

            async Task<RawCaptureReceipt> CaptureAsync(CameraModuleConfig cameraConfig, int ordinal, bool differentExposure = false)
            {
                var module = new VirtualSkyCameraModule(TimeProvider.System, catalog, provider.GetRequiredService<IProjectedSceneStore>(),
                    planetEphemeris: new AstronomyEnginePlanetEphemeris(), stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var moduleLifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(cameraConfig, CancellationToken.None).ConfigureAwait(false);
                var requested = DateTimeOffset.Parse("2026-01-15T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture).AddSeconds(ordinal * 25);
                var request = new CaptureRequest(requested, TimeSpan.FromSeconds(25), CaptureMode.Still,
                    new CaptureSetpoint(TimeSpan.FromSeconds(differentExposure ? 10 : 20), 1, null, null));
                var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
                return (await ingress.AcceptAsync(cameraConfig,
                    new CaptureLoopSubmission(request, result, requested, request.TargetInterval, TimeSpan.Zero),
                    CancellationToken.None).ConfigureAwait(false))!;
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
