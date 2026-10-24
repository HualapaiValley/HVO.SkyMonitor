using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    private static readonly string[] PhysicalOriginalStars = ["bright", "second", "faint"];
    private static readonly string[] PhysicalEndpointStars = ["bright", "endpoint"];
    private static readonly string[] PhysicalConstellations = ["TST"];
    private static readonly SolarSystemBody[] PhysicalSolarBodies = [SolarSystemBody.Mars];
    private static readonly string[] PhysicalSolarBodyNames = ["Mars"];

    [TestMethod]
    [DataRow("absent")]
    [DataRow("null")]
    public async Task PhysicalSceneQuery_ActivatedStringSolarSelectionStagesAndRetainsScene(string curated)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-physical-query-string-solar");
        try
        {
            using var provider = CreateProvider(root);
            var config = PhysicalQueryConfiguration(curated);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var pipeline = QueryPipeline(config, 2, 1, true);
            var options = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(pipeline.Steps[0].Options!.Value.GetRawText())!;
            options["solarSystemBodies"] = JsonSerializer.SerializeToElement(PhysicalSolarBodyNames);
            pipeline = pipeline with { Steps = [pipeline.Steps[0] with { Options = JsonSerializer.SerializeToElement(options) }] };
            var draft = await operations.CreateRevisionAsync("string-solar", "2", pipeline, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);

            var submission = PhysicalQuerySubmission(0);
            var catalog = new PhysicalSceneCatalog();
            await PhysicalHost(provider, config, catalog).PublishAsync(submission, CancellationToken.None).ConfigureAwait(false);
            var paths = PhysicalRawPaths(provider, config, submission);
            var manifestBytes = await File.ReadAllBytesAsync(paths.SidecarAbsolutePath).ConfigureAwait(false);
            var manifest = CaptureContractJson.ParseManifest(manifestBytes).Document!.Manifest;
            Assert.IsNotNull(manifest.Scene, "A validated string-valued solar selection must stage the physical scene.");
            Assert.AreEqual(draft.RevisionId, manifest.CaptureGraphRevisionId);
            var stage = await provider.GetRequiredService<IProjectedSceneStagingReader>().ReadAsync(manifest.Scene.ProjectedSceneStageKey!, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(stage);
            Assert.AreEqual(2, stage.Selection.MaximumMagnitude);
            Assert.AreEqual(1, stage.Selection.MaximumResults);
            Assert.IsTrue(stage.Selection.IncludeConstellationEndpointStars);
            CollectionAssert.AreEquivalent(PhysicalSolarBodies, stage.Selection.SolarSystemBodies.ToArray());
            Assert.AreEqual(stage.AstronomyAlgorithmVersion, manifest.Scene.AstronomyAlgorithmVersion);
            Assert.AreEqual(stage.EphemerisModelVersion, manifest.Scene.EphemerisModelVersion);
            Assert.IsTrue(manifest.Scene.IncludeConstellationEndpointStars);

            await CompleteLiveAsync(provider, config).ConfigureAwait(false);
            var execution = await PhysicalExecutionAsync(provider, config, submission).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, execution.Execution.GraphRevisionId);
            var output = await ReadPhysicalSceneOutputAsync(provider, execution).ConfigureAwait(false);
            Assert.AreEqual(ProjectedSceneKind.Predicted, output.Scene.Kind);
            CollectionAssert.AreEqual(ProjectedSceneJson.Serialize(stage.Bind(output.Scene.Source, ProjectedSceneKind.Predicted)), output.Bytes);
            CollectionAssert.AreEquivalent(PhysicalEndpointStars, StarIds(output.Scene));
            Assert.IsTrue(output.Scene.Objects.Any(static item => item.Kind == CelestialObjectKind.SolarSystemBody && item.Id == "solar-system:Mars"));
            Assert.AreEqual("query-fixture-astronomy-v1", output.Scene.AstronomyAlgorithmVersion);
            Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor), output.Scene.Source.ArtifactIdentitySha256);
            var retained = (await operations.ReadRevisionPipelineAsync(draft.RevisionId, CancellationToken.None).ConfigureAwait(false))!;
            Assert.AreEqual(CaptureContractJson.ComputeCanonicalJsonSha256(pipeline), CaptureContractJson.ComputeCanonicalJsonSha256(retained));
            Assert.AreEqual("Mars", retained.Steps[0].Options!.Value.GetProperty("solarSystemBodies")[0].GetString());
            Assert.AreEqual(17, retained.Steps[0].Options!.Value.GetProperty("future").GetProperty("keep").GetInt32());
            Assert.AreEqual(curated == "null", retained.Steps[0].Options!.Value.TryGetProperty("curatedAnnotations", out var annotations));
            if (curated == "null") Assert.AreEqual(JsonValueKind.Null, annotations.ValueKind);
            CollectionAssert.AreEqual(manifestBytes, await File.ReadAllBytesAsync(paths.SidecarAbsolutePath).ConfigureAwait(false));
            CollectionAssert.AreEqual(submission.Result.Frame!.PixelData.ToArray(), await File.ReadAllBytesAsync(paths.PayloadAbsolutePath).ConfigureAwait(false));
            Assert.AreEqual(1, catalog.QueryCount);
            Console.WriteLine($"string-solar={curated}; revision={draft.RevisionId}; scene-bytes={output.Bytes.Length}; SHA256={Convert.ToHexString(SHA256.HashData(output.Bytes))}");
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    [DataRow("scene")]
    [DataRow("raw")]
    [DataRow("descriptor")]
    [DataRow("identical")]
    public async Task PhysicalSceneQuery_BindingLookupFailurePreservesRetryEvidence(string changed)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-physical-query-binding-retry");
        try
        {
            using var provider = CreateProvider(root);
            var config = PhysicalQueryConfiguration();
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var catalog = new PhysicalSceneCatalog();
            var capture = await CapturePhysicalSceneAsync(provider, config, 0, catalog).ConfigureAwait(false);
            var submission = PhysicalQuerySubmission(0);
            var frame = submission.Result.Frame!;
            frame = frame with { Metadata = frame.Metadata with { Scene = capture.Manifest.Scene } };
            frame = changed switch
            {
                "scene" => frame with { Metadata = frame.Metadata with { Scene = capture.Manifest.Scene! with { SceneId = new string('E', 64) } } },
                "raw" => frame with { PixelData = new byte[] { 9, 0, 2, 0, 3, 0, 4, 0 } },
                "descriptor" => frame with { Metadata = frame.Metadata with { Exposure = TimeSpan.FromSeconds(2) } },
                _ => frame
            };
            submission = submission with { Result = submission.Result with { Frame = frame } };
            catalog.DuringQuery = () => throw new AssertFailedException("A retry must consume retained scene evidence.");
            if (changed == "identical")
                await PhysicalHost(provider, config, catalog, new PhysicalBindingFailureIngress(ingress)).PublishAsync(submission, CancellationToken.None).ConfigureAwait(false);
            else
            {
                await Assert.ThrowsExactlyAsync<RawIngressConflictException>(async () =>
                    await PhysicalHost(provider, config, catalog).PublishAsync(submission, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<RawIngressConflictException>(async () =>
                    await PhysicalHost(provider, config, catalog, new PhysicalBindingFailureIngress(ingress)).PublishAsync(submission, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            }
            var paths = PhysicalRawPaths(provider, config, submission);
            CollectionAssert.AreEqual(capture.ManifestBytes, await File.ReadAllBytesAsync(paths.SidecarAbsolutePath).ConfigureAwait(false));
            CollectionAssert.AreEqual(PhysicalQuerySubmission(0).Result.Frame!.PixelData.ToArray(), await File.ReadAllBytesAsync(paths.PayloadAbsolutePath).ConfigureAwait(false));
            var output = await ReadPhysicalSceneOutputAsync(provider, capture.Execution).ConfigureAwait(false);
            CollectionAssert.AreEqual(capture.Bytes, output.Bytes);
            Assert.AreEqual(1, catalog.QueryCount);
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    public async Task PhysicalSceneQuery_BindingLookupFailureCommitsNewRawWithoutScene()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-physical-query-binding-new");
        try
        {
            using var provider = CreateProvider(root);
            var config = PhysicalQueryConfiguration();
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var submission = PhysicalQuerySubmission(0);
            var catalog = new PhysicalSceneCatalog();
            var failingIngress = new PhysicalBindingFailureIngress(ingress);
            await PhysicalHost(provider, config, catalog, failingIngress).PublishAsync(submission, CancellationToken.None).ConfigureAwait(false);
            var paths = PhysicalRawPaths(provider, config, submission);
            var manifest = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(paths.SidecarAbsolutePath).ConfigureAwait(false)).Document!.Manifest;
            Assert.IsNull(manifest.Scene);
            CollectionAssert.AreEqual(submission.Result.Frame!.PixelData.ToArray(), await File.ReadAllBytesAsync(paths.PayloadAbsolutePath).ConfigureAwait(false));
            var observed = failingIngress.Submission!.Result.Frame!;
            Assert.AreEqual("Unavailable", observed.Metadata.Extra!["projectedSceneAvailability"]);
            Assert.AreEqual("storage-unavailable", observed.Metadata.Extra["projectedSceneUnavailableReason"]);
            Assert.AreEqual(0, catalog.QueryCount);
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    [DataRow(2.0, 2000, false, "bright,second")]
    [DataRow(6.5, 1, false, "bright")]
    [DataRow(2.0, 1, false, "bright")]
    [DataRow(2.0, 1, true, "bright,endpoint")]
    public async Task PhysicalSceneQuery_DraftInactiveActivationAndRollbackUseOneRevision(
        double magnitude, int limit, bool endpoints, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-physical-query");
        try
        {
            using var provider = CreateProvider(root);
            var config = PhysicalQueryConfiguration();
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var catalog = new PhysicalSceneCatalog();
            var original = await CapturePhysicalSceneAsync(provider, config, 0, catalog).ConfigureAwait(false);
            var pipeline = QueryPipeline(config, magnitude, limit, endpoints);
            var draft = await operations.CreateRevisionAsync("physical-query", "2", pipeline, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            var draftCapture = await CapturePhysicalSceneAsync(provider, config, 1, catalog).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var inactiveCapture = await CapturePhysicalSceneAsync(provider, config, 2, catalog).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, original.Execution.Execution.GraphRevisionId);
            foreach (var capture in new[] { original, draftCapture, inactiveCapture })
            {
                Assert.AreEqual(initial.ActiveRevisionId, capture.Manifest.CaptureGraphRevisionId);
                Assert.AreEqual(6.5, capture.Scene.Selection.MaximumMagnitude);
                Assert.AreEqual(2000, capture.Scene.Selection.MaximumResults);
                Assert.IsFalse(capture.Scene.Selection.IncludeConstellationEndpointStars);
                CollectionAssert.AreEquivalent(PhysicalOriginalStars, StarIds(capture.Scene));
            }
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var changed = await CapturePhysicalSceneAsync(provider, config, 3, catalog).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, changed.Manifest.CaptureGraphRevisionId);
            Assert.AreEqual(draft.RevisionId, changed.Execution.Execution.GraphRevisionId);
            Assert.AreEqual(magnitude, changed.Scene.Selection.MaximumMagnitude);
            Assert.AreEqual(limit, changed.Scene.Selection.MaximumResults);
            Assert.AreEqual(endpoints, changed.Scene.Selection.IncludeConstellationEndpointStars);
            CollectionAssert.AreEquivalent(expected.Split(','), StarIds(changed.Scene));
            await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            var rollback = await CapturePhysicalSceneAsync(provider, config, 4, catalog).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, rollback.Execution.Execution.GraphRevisionId);
            CollectionAssert.AreEquivalent(StarIds(original.Scene), StarIds(rollback.Scene));
            Assert.AreEqual(JsonSerializer.Serialize(original.Scene.Selection), JsonSerializer.Serialize(rollback.Scene.Selection));
            var retained = await ReadPhysicalSceneOutputAsync(provider, original.Execution).ConfigureAwait(false);
            CollectionAssert.AreEqual(original.Bytes, retained.Bytes);
            Assert.AreEqual(CaptureContractJson.ComputeCanonicalJsonSha256(config.Pipeline.Steps[0].Options!.Value),
                CaptureContractJson.ComputeCanonicalJsonSha256((await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false))!.Steps[0].Options!.Value));
            Console.WriteLine($"query={magnitude}/{limit}/{endpoints}; selected={string.Join(',', StarIds(changed.Scene))}; revision={draft.RevisionId}; retained={Convert.ToHexString(SHA256.HashData(original.Bytes))}");
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    public async Task PhysicalSceneQuery_ActivationDuringCatalogQueryKeepsSelectedRevisionAndRetryNeverRequeries()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-physical-query-race");
        try
        {
            using var provider = CreateProvider(root);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = PhysicalQueryConfiguration();
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var draft = await operations.CreateRevisionAsync("race", "2", QueryPipeline(config, 2, 1, true), "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var catalog = new PhysicalSceneCatalog
            {
                DuringQuery = async () =>
                _ = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false)
            };
            var capture = await CapturePhysicalSceneAsync(provider, config, 0, catalog).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, capture.Manifest.CaptureGraphRevisionId);
            Assert.AreEqual(initial.ActiveRevisionId, capture.Execution.Execution.GraphRevisionId);
            Assert.AreEqual(6.5, capture.Scene.Selection.MaximumMagnitude);
            Assert.AreEqual(draft.RevisionId, (await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
            catalog.DuringQuery = () => throw new AssertFailedException("A duplicate capture must not query again.");
            await PhysicalHost(provider, config, catalog).PublishAsync(PhysicalQuerySubmission(0), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, catalog.QueryCount);
            await Assert.ThrowsExactlyAsync<RawIngressConflictException>(async () =>
                await PhysicalHost(provider, config, catalog).PublishAsync(PhysicalQuerySubmission(0) with
                {
                    LoopDuration = TimeSpan.FromSeconds(5)
                }, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var paths = PhysicalRawPaths(provider, config, PhysicalQuerySubmission(0));
            CollectionAssert.AreEqual(capture.ManifestBytes, await File.ReadAllBytesAsync(paths.SidecarAbsolutePath).ConfigureAwait(false));
            var retained = await ReadPhysicalSceneOutputAsync(provider, capture.Execution).ConfigureAwait(false);
            CollectionAssert.AreEqual(capture.Bytes, retained.Bytes);
            catalog.DuringQuery = null;
            var next = await CapturePhysicalSceneAsync(provider, config, 1, catalog).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, next.Execution.Execution.GraphRevisionId);
            CollectionAssert.AreEquivalent(PhysicalEndpointStars, StarIds(next.Scene));
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    [DataRow((int)RawIngressFaultPoint.SidecarPublished)]
    [DataRow((int)RawIngressFaultPoint.BeforeJournalCommit)]
    [DataRow((int)RawIngressFaultPoint.AfterJournalCommit)]
    public async Task PhysicalSceneQuery_PublicationInterruptionRecoveryRetainsBindingSceneAndBytes(int fault)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-physical-query-recovery");
        try
        {
            var config = PhysicalQueryConfiguration();
            var submission = PhysicalQuerySubmission(0);
            string revisionId;
            byte[] sidecar;
            StagedProjectedSceneDocument staged;
            var catalog = new PhysicalSceneCatalog();
            using (var original = CreateProvider(root, configureServices: services =>
                services.AddSingleton<IRawIngressFaultInjector>(new PhysicalQueryFault((RawIngressFaultPoint)fault))))
            {
                var ingress = original.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = original.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
                var draft = await operations.CreateRevisionAsync("interrupted", "2", QueryPipeline(config, 2, 1, true), "create", "test", null, CancellationToken.None).ConfigureAwait(false);
                await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
                var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
                revisionId = draft.RevisionId;
                await Assert.ThrowsExactlyAsync<IOException>(async () =>
                    await PhysicalHost(original, config, catalog).PublishAsync(submission, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                sidecar = await File.ReadAllBytesAsync(PhysicalRawPaths(original, config, submission).SidecarAbsolutePath).ConfigureAwait(false);
                var manifest = CaptureContractJson.ParseManifest(sidecar).Document!.Manifest;
                Assert.AreEqual(revisionId, manifest.CaptureGraphRevisionId);
                staged = (await original.GetRequiredService<IProjectedSceneStagingReader>().ReadAsync(
                    manifest.Scene!.ProjectedSceneStageKey!, CancellationToken.None).ConfigureAwait(false))!;
                Assert.IsNotNull(staged, "Ambiguous or committed publication must retain its capture-owned stage.");
                Assert.AreEqual(2, staged.Selection.MaximumMagnitude);
                await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            }
            using (var recovered = CreateProvider(root))
            {
                var ingress = recovered.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                await ingress.BindRecoveredLiveExecutionsAsync(config, CancellationToken.None).ConfigureAwait(false);
                catalog.DuringQuery = () => throw new AssertFailedException("Recovery and retry must consume retained scene evidence.");
                await PhysicalHost(recovered, config, catalog).PublishAsync(submission, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(1, catalog.QueryCount);
                var paths = PhysicalRawPaths(recovered, config, submission);
                CollectionAssert.AreEqual(sidecar, await File.ReadAllBytesAsync(paths.SidecarAbsolutePath).ConfigureAwait(false));
                CollectionAssert.AreEqual(submission.Result.Frame!.PixelData.ToArray(), await File.ReadAllBytesAsync(paths.PayloadAbsolutePath).ConfigureAwait(false));
                var retainedManifest = CaptureContractJson.ParseManifest(sidecar).Document!.Manifest;
                var frame = submission.Result.Frame;
                foreach (var changedFrame in new[]
                {
                    frame with { PixelData = new byte[] { 9, 0, 2, 0, 3, 0, 4, 0 } },
                    frame with { Metadata = frame.Metadata with { Exposure = TimeSpan.FromSeconds(2) } },
                    frame with { Metadata = frame.Metadata with { Scene = retainedManifest.Scene! with { SceneId = new string('E', 64) } } }
                })
                {
                    await Assert.ThrowsExactlyAsync<RawIngressConflictException>(async () =>
                        await PhysicalHost(recovered, config, catalog).PublishAsync(submission with
                        {
                            Result = submission.Result with { Frame = changedFrame }
                        }, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                }
                CollectionAssert.AreEqual(sidecar, await File.ReadAllBytesAsync(paths.SidecarAbsolutePath).ConfigureAwait(false));
                var context = ReadPhysicalLaneContext(root);
                if ((RawIngressFaultPoint)fault != RawIngressFaultPoint.AfterJournalCommit)
                {
                    Assert.AreEqual("manifest-fallback", context.Source);
                    Assert.AreEqual(TimeSpan.Zero, context.Envelope.Submission.LoopDuration,
                        "Recovered loop timing is a fallback, never a new acquired observation.");
                    await PhysicalHost(recovered, config, catalog).PublishAsync(submission with { LoopDuration = TimeSpan.FromSeconds(5) }, CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual(context.Sha256, ReadPhysicalLaneContext(root).Sha256);
                }
                else Assert.AreEqual("capture", context.Source);
                await CompleteLiveAsync(recovered, config).ConfigureAwait(false);
                var execution = await PhysicalExecutionAsync(recovered, config, submission).ConfigureAwait(false);
                Assert.AreEqual(revisionId, execution.Execution.GraphRevisionId);
                var output = await ReadPhysicalSceneOutputAsync(recovered, execution).ConfigureAwait(false);
                CollectionAssert.AreEqual(ProjectedSceneJson.Serialize(staged.Bind(output.Scene.Source, ProjectedSceneKind.Predicted)), output.Bytes);
                Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(CaptureContractJson.ParseManifest(sidecar).Document!.Manifest.Descriptor), output.Scene.Source.ArtifactIdentitySha256);
                Assert.HasCount(1, execution.Nodes.Single().Outputs);
                Console.WriteLine($"fault={(RawIngressFaultPoint)fault}; retained-revision={revisionId}; scene-bytes={output.Bytes.Length}; SHA256={Convert.ToHexString(SHA256.HashData(output.Bytes))}");
            }
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PhysicalSceneQuery_OptionalFailureOrCancellationPreservesSelectedGraphAndRaw(bool cancel)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-physical-query-optional");
        try
        {
            using var provider = CreateProvider(root);
            using var cancellation = new CancellationTokenSource();
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = PhysicalQueryConfiguration();
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var draft = await operations.CreateRevisionAsync("optional", "2", QueryPipeline(config, 2, 1, true), "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var catalog = new PhysicalSceneCatalog
            {
                DuringQuery = async () =>
            {
                _ = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
                if (cancel) { await cancellation.CancelAsync().ConfigureAwait(false); throw new OperationCanceledException(cancellation.Token); }
                throw new InvalidOperationException("fixture optional catalog failure");
            }
            };
            var submission = PhysicalQuerySubmission(0);
            await PhysicalHost(provider, config, catalog).PublishAsync(submission, cancellation.Token).ConfigureAwait(false);
            var paths = PhysicalRawPaths(provider, config, submission);
            var manifest = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(paths.SidecarAbsolutePath).ConfigureAwait(false)).Document!.Manifest;
            Assert.IsNull(manifest.Scene);
            Assert.AreEqual(initial.ActiveRevisionId, manifest.CaptureGraphRevisionId);
            CollectionAssert.AreEqual(submission.Result.Frame!.PixelData.ToArray(), await File.ReadAllBytesAsync(paths.PayloadAbsolutePath).ConfigureAwait(false));
            Assert.AreEqual(initial.ActiveRevisionId, (await PhysicalExecutionAsync(provider, config, submission).ConfigureAwait(false)).Execution.GraphRevisionId);
            var stages = Path.Combine(root, "staging", "projected-scenes");
            Assert.IsFalse(Directory.Exists(stages) && Directory.EnumerateFiles(stages, "*.json").Any());
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    [DataRow("absent")]
    [DataRow("null")]
    public async Task PhysicalSceneQuery_PreservesUneditedOptionsAlgorithmsAndDefaultIdentities(string curated)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-physical-query-options");
        try
        {
            using var provider = CreateProvider(root);
            var config = PhysicalQueryConfiguration(curated);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var pipelineIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(config.Pipeline);
            var result = await CapturePhysicalSceneAsync(provider, config, 0, new PhysicalSceneCatalog()).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, result.Execution.Execution.GraphRevisionId);
            Assert.AreEqual(initial.ActiveRevisionId, (await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
            Assert.AreEqual(pipelineIdentity, CaptureContractJson.ComputeCanonicalJsonSha256(config.Pipeline));
            Assert.AreEqual("query-fixture-astronomy-v1", result.Scene.AstronomyAlgorithmVersion);
            Assert.AreEqual("projection-v1", result.Scene.Projection.CalibrationVersion);
            Assert.IsNotNull(result.Scene.ConstellationTopology);
            Assert.AreEqual("fixed-fixture-v1", result.Scene.EphemerisModelVersion);
            CollectionAssert.AreEquivalent(PhysicalConstellations, result.Scene.Selection.ConstellationIds.ToArray());
            CollectionAssert.AreEquivalent(PhysicalSolarBodies, result.Scene.Selection.SolarSystemBodies.ToArray());
            var retained = (await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false))!;
            Assert.AreEqual(CaptureContractJson.ComputeCanonicalJsonSha256(config.Pipeline.Steps[0].Options!.Value), CaptureContractJson.ComputeCanonicalJsonSha256(retained.Steps[0].Options!.Value));
            var options = retained.Steps[0].Options!.Value;
            Assert.AreEqual(17, options.GetProperty("future").GetProperty("keep").GetInt32());
            Assert.AreEqual(curated == "null", options.TryGetProperty("curatedAnnotations", out var annotations));
            if (curated == "null") Assert.AreEqual(JsonValueKind.Null, annotations.ValueKind);
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    public void PhysicalSceneQuery_ManifestBindingIsOptionalAndOutsideReconstructionIdentity()
    {
        var bytes = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
        var manifest = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, bytes);
        var legacyJson = CaptureContractJson.Serialize(manifest);
        using var legacy = JsonDocument.Parse(legacyJson);
        Assert.IsFalse(legacy.RootElement.TryGetProperty("captureGraphRevisionId", out _));
        CollectionAssert.AreEqual(legacyJson, CaptureContractJson.Serialize(manifest with { CaptureGraphRevisionId = null }));
        var bound = manifest with { CaptureGraphRevisionId = new string('A', 64) };
        Assert.IsTrue(bound.Validate().IsValid);
        Assert.AreEqual(manifest.IdempotencyKey, bound.IdempotencyKey);
        Assert.AreEqual(manifest.Descriptor.Capture, bound.Descriptor.Capture);
        Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor), CaptureContractJson.ComputeDescriptorSha256(bound.Descriptor));
        Assert.AreNotEqual(CaptureContractJson.ComputeManifestSha256(manifest), CaptureContractJson.ComputeManifestSha256(bound));
        var parsed = CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(bound));
        Assert.IsTrue(parsed.IsValid);
        Assert.IsTrue(FrameReconstructor.TryReconstruct(parsed.Document!.Manifest.Descriptor, bytes, out _).IsValid,
            "Reconstruction and central consumers require no capture-local graph registry.");
        Assert.IsFalse((bound with { CaptureGraphRevisionId = "invalid" }).Validate().IsValid);
    }

    [TestMethod]
    public async Task PhysicalSceneQuery_DefaultGraphKeepsDescriptorAndSceneOutputIdentities()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-physical-query-default");
        try
        {
            using var provider = CreateProvider(root);
            var configured = PhysicalQueryConfiguration();
            var config = configured with { Pipeline = CreateProjectedSceneConfiguration().Pipeline };
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var submission = PhysicalQuerySubmission(0);
            var catalog = new PhysicalSceneCatalog();
            var startupStager = new CaptureProjectedSceneStager(provider.GetRequiredService<IProjectedSceneStagingStore>(), catalog);
            var startupStaged = await startupStager.StageAsync(config, submission, CancellationToken.None).ConfigureAwait(false);
            var startupProvenance = startupStaged.Result.Frame!.Metadata.Scene!;
            var stage = await provider.GetRequiredService<IProjectedSceneStagingReader>().ReadAsync(startupProvenance.ProjectedSceneStageKey!, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(stage);
            await PhysicalHost(provider, config, catalog).PublishAsync(submission, CancellationToken.None).ConfigureAwait(false);
            var paths = PhysicalRawPaths(provider, config, submission);
            var bound = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(paths.SidecarAbsolutePath).ConfigureAwait(false)).Document!.Manifest;
            Assert.AreEqual(initial.ConfiguredBasicRevisionId, bound.CaptureGraphRevisionId);
            var descriptor = RawCaptureDescriptorFactory.Create(config, startupStaged,
                new RawCaptureIdentity(config.AgentId!, bound.Descriptor.Capture.CaptureSequence, bound.Descriptor.Capture.CaptureId, bound.Descriptor.Artifact.ArtifactId),
                Convert.ToHexString(SHA256.HashData(submission.Result.Frame!.PixelData.Span)), bound.Descriptor.Timing.DurableIngressUtc);
            Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(descriptor), CaptureContractJson.ComputeDescriptorSha256(bound.Descriptor));
            Assert.AreEqual(RawCaptureDescriptorFactory.CreateStableIds(config, submission).CaptureId, bound.Descriptor.Capture.CaptureId);
            var unbound = new ArtifactManifestV2(ArtifactManifestV2.CurrentSchemaVersion, descriptor, paths.PayloadRelativePath, startupProvenance);
            CollectionAssert.AreEqual(CaptureContractJson.Serialize(unbound), CaptureContractJson.Serialize(bound with { CaptureGraphRevisionId = null }));
            Assert.AreNotEqual(CaptureContractJson.ComputeManifestSha256(unbound), CaptureContractJson.ComputeManifestSha256(bound));
            await CompleteLiveAsync(provider, config).ConfigureAwait(false);
            var execution = await PhysicalExecutionAsync(provider, config, submission).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, execution.Execution.GraphRevisionId);
            var retained = await ReadPhysicalSceneOutputAsync(provider, execution, "projected-scene-v1").ConfigureAwait(false);
            var expected = stage.Bind(new(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor)), ProjectedSceneKind.Predicted);
            Assert.AreEqual(expected.Source, retained.Scene.Source);
            Assert.AreEqual(expected.SceneIdentitySha256, retained.Scene.SceneIdentitySha256);
            CollectionAssert.AreEqual(ProjectedSceneJson.Serialize(expected), retained.Bytes);
            Console.WriteLine($"configured-basic={initial.ActiveRevisionId}; descriptor={CaptureContractJson.ComputeDescriptorSha256(descriptor)}; scene={retained.Scene.SceneIdentitySha256}; payload={Convert.ToHexString(SHA256.HashData(retained.Bytes))}; optional-null-legacy-manifest={CaptureContractJson.ComputeManifestSha256(unbound)}; bound-manifest={CaptureContractJson.ComputeManifestSha256(bound)}");
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PhysicalSceneQuery_RecoveryRejectsMissingOrNeverValidatedBindingWithoutRelabeling(bool missing)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-physical-query-invalid-binding");
        try
        {
            var config = PhysicalQueryConfiguration();
            var submission = PhysicalQuerySubmission(0);
            byte[] retained;
            string sidecarPath;
            using (var provider = CreateProvider(root, configureServices: services =>
                services.AddSingleton<IRawIngressFaultInjector>(new PhysicalQueryFault(RawIngressFaultPoint.SidecarPublished))))
            {
                await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
                var draft = await operations.CreateRevisionAsync("invalid-binding", "2", QueryPipeline(config, 2, 1, true), "create", "test", null, CancellationToken.None).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<IOException>(async () =>
                    await PhysicalHost(provider, config, new PhysicalSceneCatalog()).PublishAsync(submission, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                sidecarPath = PhysicalRawPaths(provider, config, submission).SidecarAbsolutePath;
                var manifest = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false)).Document!.Manifest;
                retained = CaptureContractJson.Serialize(manifest with { CaptureGraphRevisionId = missing ? new string('F', 64) : draft.RevisionId });
                // Deliberately corrupt the interrupted fixture's binding; recovery must refuse, never choose active.
                await File.WriteAllBytesAsync(sidecarPath, retained).ConfigureAwait(false);
            }
            using (var recovered = CreateProvider(root))
            {
                var ingress = recovered.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                if (missing)
                    await Assert.ThrowsExactlyAsync<KeyNotFoundException>(async () => await ingress.BindRecoveredLiveExecutionsAsync(config, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                else
                    await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () => await ingress.BindRecoveredLiveExecutionsAsync(config, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                CollectionAssert.AreEqual(retained, await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false));
                Assert.IsNull(await recovered.GetRequiredService<ProcessingGraphOperationsCoordinator>().ReadLiveExecutionIdAsync(
                    RawCaptureDescriptorFactory.CreateStableIds(config, submission).CaptureId, CancellationToken.None).ConfigureAwait(false));
            }
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    public async Task PhysicalSceneQuery_DefinitelyUnpublishedFailureReleasesStageOwnership()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-physical-query-cleanup");
        try
        {
            using var provider = CreateProvider(root, configureServices: services =>
                services.AddSingleton<IRawIngressFaultInjector>(new PhysicalQueryFault(RawIngressFaultPoint.BeforeIdentityReservation)));
            var config = PhysicalQueryConfiguration();
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<IOException>(async () => await PhysicalHost(provider, config, new PhysicalSceneCatalog())
                .PublishAsync(PhysicalQuerySubmission(0), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsEmpty(Directory.EnumerateFiles(Path.Combine(root, "staging", "projected-scenes"), "*.json"));
            using var lease = await provider.GetRequiredService<ProjectedSceneStageLifecycleCoordinator>().AcquireReconciliationLeaseAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsEmpty(lease.PendingStageKeys);
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PhysicalSceneQuery_VirtualSkyRenderedSceneSurvivesGraphQueryEditsUnchanged(bool bindingFailure)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-physical-query-virtual");
        try
        {
            using var provider = CreateProvider(root);
            var config = PhysicalQueryConfiguration() with
            {
                Module = new CameraModuleDescriptor(bindingFailure ? "vIrTuAlSkY" : "VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    maximumMagnitude = 6.5,
                    maximumResults = 3,
                    illuminationMode = "ControlledNight",
                    backgroundElectronsPerSecond = 0,
                    readNoiseStandardDeviation = 0,
                    bias = 0
                }))
            };
            config = config with
            {
                Rig = config.Rig with
                {
                    Sensor = config.Rig.Sensor with { ResponseMode = SensorResponseMode.Monochrome, WidthPixels = 64, HeightPixels = 64 },
                    Optics = config.Rig.Optics with { PrincipalPointX = 32, PrincipalPointY = 32, ImageCircleRadiusPixels = 31 }
                }
            };
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var catalog = new PhysicalSceneCatalog();
            var module = new VirtualSkyCameraModule(TimeProvider.System, catalog,
                provider.GetRequiredService<IProjectedSceneStore>(), stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var moduleDisposal = module.ConfigureAwait(false);
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var template = PhysicalQuerySubmission(0);
            var result = await module.CaptureAsync(template.Request, CancellationToken.None).ConfigureAwait(false);
            var submission = template with { Result = result };
            var rawBytes = result.Frame!.PixelData.ToArray();
            var provenance = result.Frame.Metadata.Scene!;
            var stage = await provider.GetRequiredService<IProjectedSceneStagingReader>().ReadAsync(provenance.ProjectedSceneStageKey!, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(stage);
            var draft = await operations.CreateRevisionAsync("virtual-query", "2", QueryPipeline(config, -10, 1, true), "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var queryCount = catalog.QueryCount;
            catalog.DuringQuery = () => throw new AssertFailedException("VirtualSky host staging must never query again.");
            await PhysicalHost(provider, config, catalog, bindingFailure ? new PhysicalBindingFailureIngress(ingress) : null)
                .PublishAsync(submission, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(queryCount, catalog.QueryCount);
            var manifest = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(
                PhysicalRawPaths(provider, config, submission).SidecarAbsolutePath).ConfigureAwait(false)).Document!.Manifest;
            Assert.IsNotNull(manifest.Scene, "An optional binding-lookup failure must not erase the captured VirtualSky scene.");
            Assert.AreEqual(CaptureContractJson.ComputeCanonicalJsonSha256(provenance),
                CaptureContractJson.ComputeCanonicalJsonSha256(manifest.Scene));
            await CompleteLiveAsync(provider, config).ConfigureAwait(false);
            var execution = await PhysicalExecutionAsync(provider, config, submission).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, execution.Execution.GraphRevisionId);
            var output = await ReadPhysicalSceneOutputAsync(provider, execution).ConfigureAwait(false);
            Assert.AreEqual(ProjectedSceneKind.VirtualRenderAuthoritative, output.Scene.Kind);
            Assert.AreEqual(stage.Selection.MaximumMagnitude, output.Scene.Selection.MaximumMagnitude);
            Assert.AreEqual(3, output.Scene.Selection.MaximumResults);
            Assert.IsFalse(output.Scene.Selection.IncludeConstellationEndpointStars);
            CollectionAssert.AreEqual(ProjectedSceneJson.Serialize(stage.Bind(output.Scene.Source, ProjectedSceneKind.VirtualRenderAuthoritative)), output.Bytes);
            CollectionAssert.AreEqual(rawBytes, await File.ReadAllBytesAsync(PhysicalRawPaths(provider, config, submission).PayloadAbsolutePath).ConfigureAwait(false));
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    public async Task PhysicalSceneQuery_BoundedCaptureMeasurement()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-physical-query-measurement");
        try
        {
            using var provider = CreateProvider(root);
            var config = PhysicalQueryConfiguration();
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var catalog = new PhysicalSceneCatalog();
            for (var index = 0; index < 5; index++)
                _ = await CapturePhysicalSceneAsync(provider, config, index, catalog).ConfigureAwait(false);
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes();
            var durations = new List<double>();
            long outputBytes = 0;
            for (var index = 5; index < 15; index++)
            {
                var start = Stopwatch.GetTimestamp();
                var capture = await CapturePhysicalSceneAsync(provider, config, index, catalog).ConfigureAwait(false);
                outputBytes += capture.Bytes.Length;
                durations.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            var milliseconds = durations.Sum();
            durations.Sort();
            process.Refresh();
            Console.WriteLine($"physical-binding measurement: 5 warmup + 10 measured; 2x2 Mono16; 8 raw bytes/capture; 4 catalog objects; concurrency=1; query-count={catalog.QueryCount}; output-bytes={outputBytes}; CPU-ms={(process.TotalProcessorTime - cpu).TotalMilliseconds:F2}; allocated-bytes={GC.GetTotalAllocatedBytes() - allocated}; working-set={process.WorkingSet64}; median-ms={durations[5]:F2}; max-ms={durations[^1]:F2}; captures/s={10000 / milliseconds:F2}");
            Assert.AreEqual(15, catalog.QueryCount);
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    private static (string Source, string Sha256, CaptureLaneEnvelope Envelope) ReadPhysicalLaneContext(string root)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT context_source, context_sha256, context_json FROM capture_lane_contexts;";
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        return (reader.GetString(0), reader.GetString(1), CaptureLaneEnvelopeSerializer.Deserialize(
            reader.GetFieldValue<byte[]>(2), reader.GetString(1)));
    }

    private static CameraModuleConfig PhysicalQueryConfiguration(string curated = "absent")
    {
        var config = CreateProjectedSceneConfiguration() with
        {
            Module = new CameraModuleDescriptor("PhysicalCamera"),
            DeploymentLocation = DeploymentLocationSnapshot.Create("test", 1, "test", null, DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC")
        };
        var options = JsonSerializer.SerializeToElement(new
        {
            outputVariant = "fixture-scene",
            maximumMagnitude = 6.5,
            maximumResults = 2000,
            includeConstellationEndpointStars = false,
            astronomyAlgorithmVersion = "query-fixture-astronomy-v1",
            constellationIds = PhysicalConstellations,
            solarSystemBodies = PhysicalSolarBodies,
            future = new { keep = 17 }
        });
        if (curated == "null")
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(options.GetRawText())!;
            values["curatedAnnotations"] = JsonSerializer.SerializeToElement<object?>(null);
            options = JsonSerializer.SerializeToElement(values);
        }
        return config with { Pipeline = config.Pipeline with { Steps = [config.Pipeline.Steps[0] with { Options = options }] } };
    }

    private static CapturePipelineConfig QueryPipeline(CameraModuleConfig config, double magnitude, int limit, bool endpoints)
    {
        var options = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.Pipeline.Steps[0].Options!.Value.GetRawText())!;
        options["maximumMagnitude"] = JsonSerializer.SerializeToElement(magnitude);
        options["maximumResults"] = JsonSerializer.SerializeToElement(limit);
        options["includeConstellationEndpointStars"] = JsonSerializer.SerializeToElement(endpoints);
        return config.Pipeline with { Steps = [config.Pipeline.Steps[0] with { Options = JsonSerializer.SerializeToElement(options) }] };
    }

    private static CaptureLoopSubmission PhysicalQuerySubmission(int sequence)
    {
        var submission = CreateSubmission(sequence, CameraPixelFormat.Mono16, DateTimeOffset.UnixEpoch.AddDays(1));
        var frame = submission.Result.Frame!;
        return submission with
        {
            Result = submission.Result with
            {
                Frame = frame with
                {
                    Layout = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, frame.PixelData.ToArray()).Descriptor.Layout
                }
            }
        };
    }

    private static CaptureHostContext PhysicalHost(ServiceProvider provider, CameraModuleConfig config, PhysicalSceneCatalog catalog, IRawCaptureIngress? ingress = null)
        => new(config, ingress ?? provider.GetRequiredService<IRawCaptureIngress>(), new QueryTestDistributor(),
            projectedSceneStaging: provider.GetRequiredService<IProjectedSceneStagingStore>(),
            projectedSceneLifecycle: provider.GetRequiredService<ProjectedSceneStageLifecycleCoordinator>(),
            projectedSceneStager: new CaptureProjectedSceneStager(provider.GetRequiredService<IProjectedSceneStagingStore>(), catalog,
                new InMemoryConstellationTopology([new ConstellationSegment("TST", "1", "4")], new("fixture", "1", new Uri("https://example.test/topology"), new string('D', 64), "test", "v1")),
                new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition> { [SolarSystemBody.Mars] = new(new(PhysicalSceneCatalog.SiderealHours, 0), -1) })));

    private static RawIngressPaths PhysicalRawPaths(ServiceProvider provider, CameraModuleConfig config, CaptureLoopSubmission submission)
        => RawIngressFileStore.GetPaths(provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.RawIngressRoot,
            RawCaptureDescriptorFactory.ResolveExposureStartedUtc(submission, submission.Result.Frame!),
            RawCaptureDescriptorFactory.CreateStableIds(config, submission).ArtifactId);

    private static async Task<ProcessingGraphExecutionDetail> PhysicalExecutionAsync(ServiceProvider provider, CameraModuleConfig config, CaptureLoopSubmission submission)
    {
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var executionId = await operations.ReadLiveExecutionIdAsync(RawCaptureDescriptorFactory.CreateStableIds(config, submission).CaptureId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(executionId);
        return (await operations.ReadExecutionDetailAsync(executionId.Value, CancellationToken.None).ConfigureAwait(false))!;
    }

    private sealed record PhysicalSceneCapture(ArtifactManifestV2 Manifest, byte[] ManifestBytes, StagedProjectedSceneDocument Stage,
        ProcessingGraphExecutionDetail Execution, ProjectedSceneV1 Scene, byte[] Bytes);

    private static async Task<PhysicalSceneCapture> CapturePhysicalSceneAsync(ServiceProvider provider, CameraModuleConfig config, int sequence, PhysicalSceneCatalog catalog)
    {
        var submission = PhysicalQuerySubmission(sequence);
        await PhysicalHost(provider, config, catalog).PublishAsync(submission, CancellationToken.None).ConfigureAwait(false);
        var paths = PhysicalRawPaths(provider, config, submission);
        var manifestBytes = await File.ReadAllBytesAsync(paths.SidecarAbsolutePath).ConfigureAwait(false);
        var manifest = CaptureContractJson.ParseManifest(manifestBytes).Document!.Manifest;
        var stage = await provider.GetRequiredService<IProjectedSceneStagingReader>().ReadAsync(manifest.Scene!.ProjectedSceneStageKey!, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(stage);
        Assert.AreEqual(stage.AstronomyAlgorithmVersion, manifest.Scene.AstronomyAlgorithmVersion);
        Assert.AreEqual(stage.Selection.IncludeConstellationEndpointStars, manifest.Scene.IncludeConstellationEndpointStars);
        await CompleteLiveAsync(provider, config).ConfigureAwait(false);
        var execution = await PhysicalExecutionAsync(provider, config, submission).ConfigureAwait(false);
        var output = await ReadPhysicalSceneOutputAsync(provider, execution).ConfigureAwait(false);
        Assert.AreEqual(ProjectedSceneKind.Predicted, output.Scene.Kind);
        CollectionAssert.AreEqual(ProjectedSceneJson.Serialize(stage.Bind(output.Scene.Source, ProjectedSceneKind.Predicted)), output.Bytes);
        return new(manifest, manifestBytes, stage, execution, output.Scene, output.Bytes);
    }

    private static async Task<(ProjectedSceneV1 Scene, byte[] Bytes)> ReadPhysicalSceneOutputAsync(ServiceProvider provider, ProcessingGraphExecutionDetail execution, string variant = "fixture-scene")
    {
        var output = execution.Nodes.Single().Outputs.Single();
        Assert.AreEqual(variant, output.Variant);
        var opened = await provider.GetRequiredService<ICameraAgentArtifactService>().OpenContentAsync(output.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, opened.Status);
        using var stream = opened.Content!;
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory).ConfigureAwait(false);
        var bytes = memory.ToArray();
        Assert.AreEqual(stream.ByteLength, bytes.LongLength);
        Assert.AreEqual(stream.ChecksumSha256, Convert.ToHexString(SHA256.HashData(bytes)), ignoreCase: true);
        return (ProjectedSceneJson.Parse(bytes).Scene!, bytes);
    }

    private static string[] StarIds(ProjectedSceneV1 scene)
        => scene.Objects.Where(static item => item.Kind == CelestialObjectKind.Star).Select(static item => item.Id).ToArray();

    private static void DeletePhysicalQueryRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    private sealed class PhysicalSceneCatalog : ICelestialCatalog, ICelestialCatalogMetadataSource, IHipparcosCatalog
    {
        internal static readonly double SiderealHours = AstronomyTime.LocalMeanSiderealDegrees(DateTimeOffset.UnixEpoch.AddDays(1).AddMilliseconds(500), 0) / 15;
        private readonly InMemoryCelestialCatalog _catalog = new([
            new("bright", "Bright", SiderealHours, 0, 1, HipparcosId: "1"),
            new("second", "Second", SiderealHours, 10, 1.5, HipparcosId: "2"),
            new("faint", "Faint", SiderealHours, 15, 4, HipparcosId: "3"),
            new("endpoint", "Endpoint", SiderealHours, 20, 8, HipparcosId: "4")]);
        public CatalogMetadata Metadata { get; } = new("fixture", "1", new Uri("https://example.test/catalog"), new string('A', 64), "test", "v1");
        internal Func<Task>? DuringQuery { get; set; }
        internal int QueryCount { get; private set; }
        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _catalog.Query(query);
        public async ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(CatalogCandidateQuery query, CancellationToken cancellationToken = default)
        {
            QueryCount++;
            var result = await _catalog.QueryCandidatesAsync(query, cancellationToken).ConfigureAwait(false);
            if (DuringQuery is { } action) await action().ConfigureAwait(false);
            return result;
        }
        public ValueTask<IReadOnlyList<CelestialCatalogObject>> GetByHipparcosIdsAsync(IReadOnlyCollection<string> hipparcosIds, CancellationToken cancellationToken = default)
            => _catalog.GetByHipparcosIdsAsync(hipparcosIds, cancellationToken);
    }

    private sealed class PhysicalBindingFailureIngress(IRawCaptureIngress inner) : IRawCaptureIngress
    {
        internal CaptureLoopSubmission? Submission { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => inner.InitializeAsync(cancellationToken);
        public ValueTask<RawCaptureReceipt?> AcceptAsync(CameraModuleConfig configuration, CaptureLoopSubmission submission,
            CancellationToken cancellationToken) => inner.AcceptAsync(configuration, submission, cancellationToken);
        ValueTask<RawCaptureGraphBinding?> IRawCaptureIngress.PrepareGraphBindingAsync(CameraModuleConfig configuration,
            CaptureLoopSubmission submission, CancellationToken cancellationToken)
            => ValueTask.FromException<RawCaptureGraphBinding?>(new IOException("fixture transient binding lookup failure"));
        ValueTask<RawCaptureReceipt?> IRawCaptureIngress.AcceptAsync(CameraModuleConfig configuration,
            CaptureLoopSubmission submission, RawCaptureGraphBinding? binding, CancellationToken cancellationToken)
        {
            Submission = submission;
            return inner.AcceptAsync(configuration, submission, binding, cancellationToken);
        }
    }

    private sealed class PhysicalQueryFault(RawIngressFaultPoint target) : IRawIngressFaultInjector
    {
        private int _remaining = 1;
        public void Inject(RawIngressFaultPoint point)
        {
            if (point == target && Interlocked.Exchange(ref _remaining, 0) == 1) throw new IOException("fixture publication interruption");
        }
    }
}
