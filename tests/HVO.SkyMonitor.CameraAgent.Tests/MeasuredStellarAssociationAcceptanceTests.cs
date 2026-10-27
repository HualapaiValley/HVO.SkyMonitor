using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Issue #1187 F3 Route B: actual VirtualSky final noisy pixels through the ordinary host composition (module capture,
/// durable raw ingress, production projected-scene staging and publication, standard lane) into the opt-in
/// MeasuredStellarAssociations stage. Identity and centroid truth come only from the independent
/// <see cref="VirtualAstrometryReference"/> equations; the production scene and projector never serve as the oracle.
/// Every durable product is rehydrated exactly as the worker loads committed raw evidence and re-executed through the
/// production recipe adapter. Predeclared on #1187 (issuecomment-6104952144) before measurement.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class MeasuredStellarAssociationAcceptanceTests
{
    // #1103 declarations, reused unchanged: the same measurer and settings, now inside the production stage.
    private const double MaximumCentroidRmsPixels = .25;
    // #1103 measurer ceilings, borrowed deliberately for the whole MSA stage and predeclared on #1187 before measurement.
    private const double MaximumExecutionMilliseconds = 2500;
    private const double MaximumAllocatedBytesPerPixel = 64;
    private const long MaximumPeakWorkingSetBytes = 2L << 30;
    private const int ReconstructionReruns = 3;
    private const int Warmup = 5;
    private const int Measured = 30;
    private const string AgentId = "issue1187-msa-acceptance";
    private const string SchemaVersion = "issue1541-msa-acceptance-evidence-v2";
    private const string CatalogSha256 = "B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2";
    private const string SceneNode = "ProjectedScene";
    private const string MeasuredNode = "MeasuredStellarAssociations";
    private static readonly TimeSpan Exposure = TimeSpan.FromSeconds(1);
    private static readonly (DateTimeOffset Utc, int Seed)[] Partitions =
    [
        (new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero), 110201),
        (new(2026, 5, 15, 8, 0, 0, TimeSpan.Zero), 110205),
        (new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero), 110209)
    ];
    private static readonly string[] AccuracyProfiles =
        ["mono-native", "mono-roi", "mono-bin2", "mono-roi-bin2", "mono-mirror", "mono-roll", "cfa-native"];
    private static readonly string[] CostProfiles = ["mono-native", "cfa-native"];
    private static readonly string[] EvenOrder = ["A", "B"];
    private static readonly string[] OddOrder = ["B", "A"];

    // The declared opt-in shape of tests/fixtures/cameraagent.integration.json with default stage settings.
    private static readonly CapturePipelineConfig Pipeline = new(
    [
        new(Type: "ProjectedScene", Id: SceneNode, Order: 15, DependsOn: ["$raw"], Required: true),
        new(Type: "MeasuredStellarAssociations", Id: MeasuredNode, Order: 16, DependsOn: [SceneNode])
    ]);

    // Undefined RMS and empty subsets are evidence, not serialization failures.
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(VirtualAstrometryFixture.JsonOptions)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = true
    };

    public TestContext TestContext { get; set; } = null!;

    /// <summary>Fixed 21 cases: seven #1103 profiles by the three held-out partitions, one actual capture each.</summary>
    [TestMethod]
    public Task ActualCaptureAccuracyAndReconstruction() => RunAsync(cost: false);

    /// <summary>
    /// Partition 1 mono-native and cfa-native: 5 warm-up and 30 measured operations alternating the bare qualified
    /// measurer (A) and the full production recipe (B) on identical durable final pixels.
    /// </summary>
    [TestMethod]
    public Task RecipeStageCost() => RunAsync(cost: true);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A failed case is recorded with its partial evidence and fails the test after every case has run.")]
    private async Task RunAsync(bool cost)
    {
        if (Environment.GetEnvironmentVariable("HVO_MSA_ACCEPTANCE") != "1")
            Assert.Inconclusive("Requires an explicitly scheduled #1187 MSA acceptance run (HVO_MSA_ACCEPTANCE=1).");
        // A labelled tc0 side cell, comparable with the #1103 ceilings declared under it; never a production-latency claim.
        Assert.AreEqual("0", Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), "The declared measurement disables tiered compilation.");
        VirtualAstrometryQualificationTests.RequireRevision();
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION")!;
        var binaryRevision = GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.AreEqual("Release", GetType().Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration);
        var kind = cost ? "stage-cost" : "accuracy-reconstruction";
        var outputRoot = Environment.GetEnvironmentVariable("HVO_MSA_OUTPUT") is { Length: > 0 } explicitOutput
            ? Path.GetFullPath(explicitOutput)
            : Path.Combine(TestContext.TestRunDirectory ?? Path.Combine(AppContext.BaseDirectory, "TestResults"), "issue-1187",
                $"msa-{revision.AsSpan(0, 12)}-{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmss'Z'}");
        var output = Path.Combine(outputRoot, $"{kind}.json");
        Assert.IsFalse(File.Exists(output), "Evidence is append-only; select a fresh output path.");
        var root = Environment.GetEnvironmentVariable("HVO_MSA_WORK_ROOT") is { Length: > 0 } explicitRoot
            ? Path.Combine(Path.GetFullPath(explicitRoot), kind)
            : Path.Combine(Path.GetTempPath(), $"hvo-1187-msa-{kind}-{Guid.NewGuid():N}");
        Assert.IsFalse(Directory.Exists(root), "Each run composes fresh durable roots.");
        var family = VirtualAstrometryFixture.Families.Single(static f => f.Model == ProjectionModel.EquidistantFisheye);
        var variant = VirtualAstrometryFixture.Variants[0];

        var assemblySha256 = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(GetType().Assembly.Location).ConfigureAwait(false)));
        using var process = Process.GetCurrentProcess();
        var failures = new List<string>();
        var cases = new List<CaseEvidence>();
        object? catalogEvidence = null;
        var start = Probe.Before(process);
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = VirtualAstrometryQualificationTests.Snapshot();
            var catalog = await snapshot.Catalog.ReadAsync(5, AstrometricCatalogData.MaximumEntries).ConfigureAwait(false);
            Assert.HasCount(1637, catalog.Stars);
            Assert.IsTrue(catalog.IsCompleteForRequestedMagnitude);
            catalogEvidence = new
            {
                snapshot.CatalogId,
                snapshot.CatalogVersion,
                snapshot.SnapshotVersion,
                snapshot.PreprocessingVersion,
                snapshot.RowCount,
                snapshot.DatabaseSha256,
                maximumMagnitude = 5,
                stars = catalog.Stars.Count,
                catalog.IsCompleteForRequestedMagnitude,
                catalog.Provenance
            };
            foreach (var (utc, seed) in cost ? Partitions[..1] : Partitions)
            {
                var profiles = VirtualAstrometryFixture.Profiles(seed, family, variant);
                CollectionAssert.AreEqual(AccuracyProfiles, profiles.Select(static p => p.Name).ToArray(), "The #1103 profile manifest changed.");
                foreach (var profile in profiles.Where(p => !cost || CostProfiles.Contains(p.Name)))
                {
                    var evidence = new CaseEvidence($"{utc:yyyy-MM-dd}-{seed}-{profile.Name}", profile.Name, utc, seed);
                    cases.Add(evidence);
                    try
                    {
                        await RunCaseAsync(evidence, snapshot, catalog, profile, Path.Combine(root, evidence.CaseId), cost, process)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        evidence.Failures.Add($"exception: {exception}");
                    }
                    failures.AddRange(evidence.Failures.Select(failure => $"{evidence.CaseId}: {failure}"));
                }
            }
            var expected = cost ? CostProfiles.Length : Partitions.Length * AccuracyProfiles.Length;
            if (cases.Count != expected) failures.Add($"Ran {cases.Count} cases; the manifest declares exactly {expected}.");
        }
        catch (Exception exception)
        {
            failures.Add(exception.ToString());
            throw;
        }
        finally
        {
            var finish = Probe.After(process);
            process.Refresh();
            var peakWorkingSet = process.PeakWorkingSet64;
            if (cost && peakWorkingSet > MaximumPeakWorkingSetBytes)
                failures.Add($"Process-lifetime peak working set {peakWorkingSet} B exceeds the declared {MaximumPeakWorkingSetBytes} B.");
            var evidence = new
            {
                schemaVersion = SchemaVersion,
                kind,
                revision,
                binaryRevision,
                assemblySha256,
                runtimeProfile = "tc0-side-cell",
                productionLatency = false,
                predeclaration = "https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1541#issuecomment-6360029027",
                originalPredeclaration = "https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1187#issuecomment-6104952144",
                environment = new
                {
                    dotnetVariables = Environment.GetEnvironmentVariables().Keys.Cast<string>()
                        .Where(static key => key.StartsWith("DOTNET_", StringComparison.Ordinal) || key.StartsWith("COMPlus_", StringComparison.Ordinal))
                        .Order(StringComparer.Ordinal).ToDictionary(static key => key, static key => Environment.GetEnvironmentVariable(key), StringComparer.Ordinal),
                    machine = Environment.MachineName,
                    os = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    framework = RuntimeInformation.FrameworkDescription,
                    processorCount = Environment.ProcessorCount,
                    serverGc = GCSettings.IsServerGC,
                    gcLatencyMode = GCSettings.LatencyMode.ToString(),
                    vector128 = Vector128.IsHardwareAccelerated,
                    vector256 = Vector256.IsHardwareAccelerated,
                    vector512 = Vector512.IsHardwareAccelerated
                },
                catalog = catalogEvidence,
                catalogSha256 = CatalogSha256,
                declared = new
                {
                    family = family.Name,
                    variant = variant.Name,
                    exposureSeconds = Exposure.TotalSeconds,
                    partitions = Partitions.Select(static p => new { p.Utc, p.Seed }).ToArray(),
                    profiles = AccuracyProfiles,
                    costProfiles = CostProfiles,
                    pipeline = Pipeline,
                    stellarCandidateSelection = nameof(VirtualSkyStellarCandidateSelection.Exhaustive),
                    MaximumCentroidRmsPixels,
                    MaximumExecutionMilliseconds,
                    MaximumAllocatedBytesPerPixel,
                    MaximumPeakWorkingSetBytes,
                    ReconstructionReruns,
                    warmup = cost ? Warmup : 0,
                    measured = cost ? Measured : 0,
                    recall = "reported, not gated: no MSA recall threshold is declared",
                    truth = "VirtualAstrometryReference Trails/Nearest and the #1103 eligibility rule; never the production scene or projector",
                    allocation = "process-wide GC.GetTotalAllocatedBytes(precise: true) deltas; the filtered test runs alone and serially",
                    io = "/proc/self/io deltas; rchar/syscr include the probes' own procfs reads"
                },
                cases,
                totals = finish.Since(start),
                jit = new
                {
                    compiledMethods = JitInfo.GetCompiledMethodCount(),
                    compiledIlBytes = JitInfo.GetCompiledILBytes(),
                    compilationMilliseconds = JitInfo.GetCompilationTime().TotalMilliseconds
                },
                memory = new
                {
                    status = ReadStatus(),
                    peakWorkingSetBytes = peakWorkingSet,
                    peakWorkingSetScope = "process lifetime, including capture, rendering, composition and both arms",
                    gcPauseMilliseconds = GC.GetTotalPauseDuration().TotalMilliseconds
                },
                failures
            };
            Directory.CreateDirectory(outputRoot);
            await File.WriteAllBytesAsync(output, JsonSerializer.SerializeToUtf8Bytes(evidence, EvidenceJsonOptions)).ConfigureAwait(false);
            TestContext.AddResultFile(output);
            SqliteConnection.ClearAllPools();
            if (Environment.GetEnvironmentVariable("HVO_MSA_KEEP_ROOT") != "1" && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    internal static CameraModuleConfig CreateConfiguration(VirtualAstrometryProfile profile)
    {
        // Full-catalog independent truth requires the full qualification render, even when host composition supplies
        // today's operator scene limit. Keep this override local to this harness; shipped profiles retain their policy.
        var options = JsonNode.Parse(profile.Config.Module.Options!.Value.GetRawText())!.AsObject();
        options["stellarCandidateSelection"] = nameof(VirtualSkyStellarCandidateSelection.Exhaustive);
        return profile.Config with
        {
            AgentId = AgentId,
            Pipeline = Pipeline,
            Module = profile.Config.Module with { Options = JsonSerializer.SerializeToElement(options, EvidenceJsonOptions) }
        };
    }

    private static async Task RunCaseAsync(CaseEvidence evidence, CatalogSnapshotResult snapshot, AstrometricCatalogData catalog,
        VirtualAstrometryProfile profile, string root, bool cost, Process process)
    {
        var failures = evidence.Failures;
        var ingressRoot = Path.GetFullPath(Path.Combine(root, "ingress"));
        var config = CreateConfiguration(profile);
        evidence.Configuration = config;

        // Ordinary host composition, as Issue1170ComposedPipelineEvidenceTests composes the shipped graph.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICelestialCatalog>(snapshot.Catalog);
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CameraAgent:RawIngressRoot"] = ingressRoot,
            ["CameraAgent:RawIngressReserveBytes"] = "0",
            ["CameraAgent:AgentId"] = AgentId,
            ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
        }).Build());
        services.AddCameraModule<VirtualSkyCameraModule>("VirtualSky");
        var provider = services.BuildServiceProvider();
        await using var providerLifetime = provider.ConfigureAwait(false);
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
            .EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
        var module = provider.GetRequiredService<ICameraModuleFactory>().Create(config.ModuleType);
        await using var moduleLifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var host = new CaptureHostContext(config, ingress, provider.GetRequiredService<ICaptureDistributor>(), null,
            provider.GetRequiredService<IProjectedSceneStagingStore>(),
            provider.GetRequiredService<ProjectedSceneStageLifecycleCoordinator>(),
            provider.GetRequiredService<CaptureProjectedSceneStager>());
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var handler = provider.GetServices<ICaptureLaneHandler>().Single(static h => h.Lane == "standard");
        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static lane => lane.Name == "standard");

        var request = VirtualAstrometryFixture.Request(evidence.Utc, Exposure);
        var p0 = Probe.Before(process);
        var captured = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var p1 = Probe.After(process);
        var capturedFrame = captured.Frame ?? throw new InvalidDataException("The module returned no frame.");
        await host.PublishAsync(new CaptureLoopSubmission(request, captured, request.RequestedStartUtc, request.TargetInterval,
            p1.Elapsed(p0)), CancellationToken.None).ConfigureAwait(false);
        var p2 = Probe.After(process);
        var lease = await laneStore.ClaimAsync(standard, AgentId, config, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidDataException("The standard lane offered no committed capture.");
        var handled = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        var p3 = Probe.After(process);
        evidence.Composition["capture"] = p1.Since(p0);
        evidence.Composition["publish"] = p2.Since(p1);
        evidence.Composition["claimHandleComplete"] = p3.Since(p2);
        evidence.Composition["laneOutcome"] = handled.Outcome.ToString();
        evidence.Composition["laneReason"] = handled.Reason;
        if (handled.Outcome != CaptureLaneHandlerOutcome.Completed)
            failures.Add($"Standard lane ended {handled.Outcome}: {handled.Reason}");

        // Committed raw evidence hydrated exactly as FrameProcessingWorker loads it.
        var receipt = lease.Context.RawCapture;
        var sidecar = await File.ReadAllBytesAsync(Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json")).ConfigureAwait(false);
        var parsedManifest = CaptureContractJson.ParseManifest(sidecar);
        if (!parsedManifest.IsValid || parsedManifest.Document?.Manifest is not { } manifest)
            throw new InvalidDataException($"Committed raw sidecar could not be parsed ({parsedManifest.Validation.ReasonCode}).");
        if (!string.Equals(CaptureContractJson.ComputeManifestSha256(sidecar), receipt.CommittedManifestSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Committed raw sidecar differs from the journaled manifest.");
        var rawPayload = await File.ReadAllBytesAsync(receipt.StoredFrame.AbsolutePath).ConfigureAwait(false);
        var reconstruction = FrameReconstructor.TryReconstruct(manifest.Descriptor, rawPayload, out var frame);
        if (!reconstruction.IsValid || frame is null)
            throw new InvalidDataException($"Committed raw evidence could not be reconstructed ({reconstruction.ReasonCode}).");
        if (manifest.Scene is not null)
            frame = frame with { Metadata = frame.Metadata with { Scene = manifest.Scene } };
        var descriptor = manifest.Descriptor;
        var raw = new FrameArtifact(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, frame,
            recipeVersion: ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256);
        var captureId = descriptor.Capture.CaptureId;
        var descriptorSha256 = CaptureContractJson.ComputeDescriptorSha256(descriptor);
        var moduleRawSha256 = Sha256(capturedFrame.PixelData);
        var durableRawSha256 = Sha256(rawPayload);
        var reconstructedSha256 = Sha256(frame.PixelData);
        Check(failures, moduleRawSha256 == durableRawSha256, "Durable raw payload differs from the module's final pixels.");
        Check(failures, durableRawSha256 == reconstructedSha256, "Reconstructed frame pixels differ from the durable raw payload.");
        Check(failures, string.Equals(durableRawSha256, descriptor.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase),
            "Durable raw payload differs from its descriptor checksum.");
        var layout = frame.Layout ?? throw new InvalidDataException("The reconstructed frame has no authoritative layout.");
        var pixels = (long)layout.Width * layout.Height;

        // Durable products of both nodes.
        var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
        var sceneNode = await store.ReadNodeAsync(captureId, SceneNode, CancellationToken.None).ConfigureAwait(false);
        var measuredNode = await store.ReadNodeAsync(captureId, MeasuredNode, CancellationToken.None).ConfigureAwait(false);
        evidence.Nodes = new[] { sceneNode, measuredNode }.Select(static node => node is null ? null : new
        {
            node.NodeId,
            status = node.Status.ToString(),
            outcome = node.Outcome?.ToString(),
            node.Reason,
            node.Attempt,
            durationMilliseconds = node.Duration?.TotalMilliseconds,
            outputs = node.Outputs.Select(static output => new
            {
                output.ArtifactId,
                output.OutputIdentitySha256,
                output.RecipeIdentitySha256,
                output.ProductSchemaVersion,
                output.ContentIdentitySha256,
                output.PayloadRelativePath,
                output.Artifact.Variant,
                output.Artifact.ChecksumSha256,
                output.Artifact.SourceArtifactIds,
                manifest = output.ProductManifest?.GetType().Name
            }).ToArray()
        }).ToArray();
        if (sceneNode is not { Status: DurableProcessingNodeStatus.Completed, Outputs: [var sceneOutput] } ||
            measuredNode is not { Status: DurableProcessingNodeStatus.Completed, Outputs: [var measuredOutput] })
        {
            failures.Add($"Nodes ended {sceneNode?.Status.ToString() ?? "missing"} ({sceneNode?.Reason}) and " +
                $"{measuredNode?.Status.ToString() ?? "missing"} ({measuredNode?.Reason}); each must complete with one output.");
            return;
        }
        evidence.DurableMeasuredNodeMilliseconds = measuredNode.Duration?.TotalMilliseconds;
        var scenePayload = await File.ReadAllBytesAsync(Path.Combine(ingressRoot, sceneOutput.PayloadRelativePath)).ConfigureAwait(false);
        var measuredPayload = await File.ReadAllBytesAsync(Path.Combine(ingressRoot, measuredOutput.PayloadRelativePath)).ConfigureAwait(false);
        evidence.ProductBytes = measuredPayload.Length;
        var scene = ProjectedSceneJson.Parse(scenePayload).Scene ?? throw new InvalidDataException("The durable projected scene does not parse.");
        Check(failures, scene.Selection.MaximumResults == 2000 && scene.Selection.MaximumMagnitude == 5,
            "The full-catalog qualification render was replaced by an operator-limited scene.");
        var product = MeasuredStellarAssociationJson.Parse(measuredPayload).Associations
            ?? throw new InvalidDataException("The durable measured association product does not parse.");

        // Source, capture, artifact, scene, recipe and ordered lineage.
        var rawRecipeIdentity = ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256;
        evidence.Lineage = new
        {
            captureId,
            rawArtifactId = descriptor.Artifact.ArtifactId,
            descriptorSha256,
            rawRecipeIdentity,
            moduleRawSha256,
            durableRawSha256,
            committedManifestSha256 = receipt.CommittedManifestSha256,
            sceneIdentitySha256 = scene.SceneIdentitySha256,
            sceneSource = scene.Source,
            productSource = product.Source,
            productScene = product.Scene,
            measuredPayloadSha256 = Sha256(measuredPayload),
            product.AssociationIdentitySha256
        };
        Check(failures, ProcessingIdentity.ComputePayloadSha256(scenePayload).Equals(sceneOutput.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase),
            "Durable scene payload differs from its artifact checksum.");
        Check(failures, ProcessingIdentity.ComputePayloadSha256(measuredPayload).Equals(measuredOutput.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase),
            "Durable measured payload differs from its artifact checksum.");
        Check(failures, scene.Source.CaptureId == captureId && scene.Source.ArtifactId == descriptor.Artifact.ArtifactId &&
            scene.Source.ArtifactIdentitySha256.Equals(descriptorSha256, StringComparison.OrdinalIgnoreCase),
            "The durable scene is not bound to the durable raw capture, artifact and descriptor.");
        Check(failures, string.Equals(scene.SceneIdentitySha256, sceneOutput.ContentIdentitySha256, StringComparison.OrdinalIgnoreCase),
            "The durable scene identity differs from its durable content identity.");
        var rawLineage = new[] { descriptor.Artifact.ArtifactId };
        // The product carries canonical uppercase identities; compare exactly against the canonical form.
        var canonicalDescriptorSha256 = descriptorSha256.ToUpperInvariant();
        var canonicalRecipeIdentity = rawRecipeIdentity.ToUpperInvariant();
        var canonicalSceneIdentity = scene.SceneIdentitySha256.ToUpperInvariant();
        Check(failures, sceneOutput.Artifact.SourceArtifactIds.SequenceEqual(rawLineage),
            "The durable scene lineage is not exactly the raw artifact.");
        Check(failures, product.Source.CaptureId == captureId && product.Source.ArtifactId == descriptor.Artifact.ArtifactId &&
            product.Source.DescriptorIdentitySha256 == canonicalDescriptorSha256 && product.Source.Role == FrameArtifactRole.Raw &&
            product.Source.Variant == descriptor.Artifact.Variant && product.Source.RecipeIdentitySha256 == canonicalRecipeIdentity &&
            product.Source.PixelFormat == layout.PixelFormat && product.Source.WidthPixels == layout.Width && product.Source.HeightPixels == layout.Height,
            "The measured product source does not identify the durable raw capture, artifact, descriptor, recipe and layout.");
        Check(failures, product.Scene.SceneIdentitySha256 == canonicalSceneIdentity,
            "The measured product is not bound to the durable projected scene.");
        Check(failures, string.Equals(product.AssociationIdentitySha256, measuredOutput.ContentIdentitySha256, StringComparison.OrdinalIgnoreCase) &&
            measuredOutput.ProductSchemaVersion == MeasuredStellarAssociationsV1.CurrentSchemaVersion &&
            measuredOutput.ProductManifest is DurableTypedMetadataProductManifestV3 && measuredOutput.Capture.CaptureId == captureId,
            "The durable measured output does not carry the product's content identity, schema and capture.");
        Check(failures, measuredOutput.Artifact.SourceArtifactIds.SequenceEqual(rawLineage),
            "The durable measured lineage is not exactly the raw artifact.");

        // Settings and algorithm binding to the #1103-qualified measurer configuration.
        var expectedSettings = StellarSourceMeasurer.SettingsIdentity(VirtualAstrometryFixture.MeasurementOptions(layout)).ToUpperInvariant();
        evidence.Settings = new
        {
            product.Measurement.AlgorithmVersion,
            product.Measurement.SettingsIdentitySha256,
            expectedAlgorithmVersion = StellarSourceMeasurer.AlgorithmVersion,
            expectedSettingsIdentitySha256 = expectedSettings,
            product.Measurement.Reconstruction,
            product.Method
        };
        Check(failures, product.Measurement.AlgorithmVersion == StellarSourceMeasurer.AlgorithmVersion, "The stage did not run the qualified measurer algorithm.");
        Check(failures, product.Measurement.SettingsIdentitySha256 == expectedSettings, "The stage did not bind the qualified measurer settings.");
        Check(failures, product.Measurement.Status == StellarMeasurementStatus.Completed, $"Measurement ended {product.Measurement.Status}.");

        Score(evidence, product, catalog, profile, frame.Metadata.Scene?.SceneUtc
            ?? capturedFrame.Metadata.Scene?.SceneUtc ?? throw new InvalidDataException("The capture carries no scene time."));

        // Production recipe re-execution from durable state, shaped exactly as the production step requests it.
        var adapter = provider.GetRequiredService<CameraAgentRecipeExecutionAdapter>();
        var source = CameraAgentRecipeExecutionAdapter.CreateArtifact(config, raw, descriptor.Artifact.Variant, null, descriptor);
        var auxiliary = new ProcessingAuxiliaryInput("scene", ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: sceneOutput.ProductSchemaVersion, IdentitySha256: sceneOutput.ContentIdentitySha256, Payload: scenePayload)
        {
            ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(scenePayload)
        };
        var executionRequest = new ProcessingExecutionRequest(BuiltInProcessingRecipes.MeasuredStellarAssociations,
            JsonSerializer.SerializeToElement(new MeasuredStellarAssociationRecipeOptions(null, null)),
            ProcessingInputSelector.Raw(source.Variant), [source], measuredOutput.Artifact.Variant,
            AuxiliaryInputs: [auxiliary], InputArtifactId: source.ArtifactId);
        string? RerunMismatch(ProcessingOutcome outcome)
        {
            if (outcome.Status != ProcessingOutcomeStatus.Produced || outcome.Products.Count != 1)
                return $"rerun ended {outcome.Status} ({outcome.ReasonCode})";
            var rerun = outcome.Products[0];
            if (!rerun.Payload.Span.SequenceEqual(measuredPayload.AsSpan())) return "rerun payload differs from the durable product";
            if (!string.Equals(rerun.ContentIdentitySha256, measuredOutput.ContentIdentitySha256, StringComparison.OrdinalIgnoreCase))
                return "rerun content identity differs";
            if (!string.Equals(rerun.OutputIdentitySha256, measuredOutput.OutputIdentitySha256, StringComparison.OrdinalIgnoreCase))
                return "rerun output identity differs";
            if (!string.Equals(rerun.Recipe.IdentitySha256, measuredOutput.RecipeIdentitySha256, StringComparison.OrdinalIgnoreCase))
                return "rerun recipe identity differs";
            if (!string.Equals(rerun.ChecksumSha256, measuredOutput.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
                return "rerun checksum differs";
            if (!rerun.SourceArtifactIds.SequenceEqual(rawLineage)) return "rerun lineage differs";
            return null;
        }
        var reruns = new List<object>();
        for (var attempt = 0; attempt < ReconstructionReruns; attempt++)
        {
            var outcome = await adapter.ExecuteAsync(executionRequest, CancellationToken.None).ConfigureAwait(false);
            var mismatch = RerunMismatch(outcome);
            reruns.Add(new
            {
                attempt,
                status = outcome.Status.ToString(),
                outcome.ReasonCode,
                payloadSha256 = outcome.Products.Count == 1 ? Sha256(outcome.Products[0].Payload) : null,
                contentIdentitySha256 = outcome.Products.Count == 1 ? outcome.Products[0].ContentIdentitySha256 : null,
                mismatch
            });
            if (mismatch is not null) failures.Add($"Reconstruction rerun {attempt}: {mismatch}.");
        }
        evidence.Reruns = reruns;

        if (cost)
            await MeasureCostAsync(evidence, adapter, executionRequest, RerunMismatch,
                VirtualAstrometryPixels.FromCapture(frame), VirtualAstrometryFixture.NominalCalibration(profile), pixels, process)
                .ConfigureAwait(false);
    }

    // Accuracy against the independent reference: identity for every label-eligible association, centroid error over
    // #1103-eligible truth, and reported (not gated) recall.
    private static void Score(CaseEvidence evidence, MeasuredStellarAssociationsV1 product, AstrometricCatalogData catalog,
        VirtualAstrometryProfile profile, DateTimeOffset sceneUtc)
    {
        var failures = evidence.Failures;
        var nominal = VirtualAstrometryFixture.NominalCalibration(profile);
        var truth = Truth(profile.Config.Rig, sceneUtc, catalog, nominal);
        var byTrail = truth.ToDictionary(static t => t.Trail);
        var labelEligible = product.Associations.Where(static a => a.LabelEligible).ToArray();
        var falseLabelEligible = new List<object>();
        var falseOther = 0;
        var errors = new List<double>();
        var correctIds = new HashSet<string>(StringComparer.Ordinal);
        var correctLabelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var association in product.Associations)
        {
            var pixel = new PixelPoint(association.MeasuredX, association.MeasuredY);
            var nearestTrail = VirtualAstrometryReference.Nearest(byTrail.Keys, pixel);
            var nearest = nearestTrail is null ? null : byTrail[nearestTrail];
            if (nearest is null || nearest.Id != association.CatalogId)
            {
                if (association.LabelEligible)
                    falseLabelEligible.Add(new { association.CatalogId, truthId = nearest?.Id, association.MeasuredX, association.MeasuredY });
                else falseOther++;
                continue;
            }
            correctIds.Add(nearest.Id);
            if (!association.LabelEligible) continue;
            correctLabelIds.Add(nearest.Id);
            if (nearest.Eligible) errors.Add(VirtualAstrometryReference.Distance(pixel, nearest.Pixel));
        }
        var detections = product.Associations.Select(static a => new PixelPoint(a.MeasuredX, a.MeasuredY))
            .Concat(product.UnassociatedDetections.Select(static d => new PixelPoint(d.X, d.Y))).ToArray();
        var eligible = truth.Where(static t => t.Eligible).ToArray();
        var detected = eligible.Count(t => detections.Any(d => t.Trail.DistanceTo(d) < 1.5));
        var sorted = errors.Order().ToArray();
        var rms = sorted.Length == 0 ? double.NaN : Math.Sqrt(sorted.Sum(static e => e * e) / sorted.Length);
        evidence.Accuracy = new
        {
            sceneUtc,
            truthVisibleStars = truth.Length,
            eligibleTruthStars = eligible.Length,
            detections = detections.Length,
            associations = product.Associations.Count,
            labelEligibleAssociations = labelEligible.Length,
            falseLabelEligibleAssociations = falseLabelEligible.Count,
            falseLabelEligible,
            falseIneligibleAssociations = falseOther,
            centroidSubset = sorted.Length,
            centroidRmsPixels = rms,
            centroidP95Pixels = sorted.Length == 0 ? double.NaN : sorted[(int)Math.Ceiling(.95 * sorted.Length) - 1],
            centroidMaximumPixels = sorted.Length == 0 ? double.NaN : sorted[^1],
            detectionRecall = eligible.Length == 0 ? double.NaN : detected / (double)eligible.Length,
            associationRecall = eligible.Length == 0 ? double.NaN : eligible.Count(t => correctIds.Contains(t.Id)) / (double)eligible.Length,
            labelEligibleRecall = eligible.Length == 0 ? double.NaN : eligible.Count(t => correctLabelIds.Contains(t.Id)) / (double)eligible.Length,
            labelRejectionReasons = Counts(product.Associations.Where(static a => !a.LabelEligible).Select(static a => a.LabelRejectionReason ?? "none")),
            unmatchedPredictionReasons = Counts(product.UnmatchedPredictions.Select(static p => p.ReasonCode)),
            unassociatedDetectionReasons = Counts(product.UnassociatedDetections.Select(static d => d.ReasonCode)),
            measurement = product.Measurement
        };
        Check(failures, falseLabelEligible.Count == 0, $"{falseLabelEligible.Count} label-eligible associations carry a wrong catalog identity.");
        Check(failures, labelEligible.Length > 0, "No label-eligible association was produced.");
        Check(failures, sorted.Length > 0 && double.IsFinite(rms), "The eligible centroid subset is empty or undefined.");
        Check(failures, !(rms > MaximumCentroidRmsPixels), $"Centroid RMS {rms:F4} px exceeds {MaximumCentroidRmsPixels} px.");
    }

    // Arms alternate first position per ordinal so neither arm systematically inherits the other's cache or GC state.
    // Verification of each operation runs outside its probe window and is accounted separately.
    private static async Task MeasureCostAsync(CaseEvidence evidence, CameraAgentRecipeExecutionAdapter adapter,
        ProcessingExecutionRequest request, Func<ProcessingOutcome, string?> mismatch, VirtualAstrometryPixels pixelsInput,
        AstrometricCalibration nominal, long pixels, Process process)
    {
        var failures = evidence.Failures;
        var samples = new List<OperationSample>();
        var verification = new List<object>();
        for (var ordinal = 0; ordinal < Warmup + Measured; ordinal++)
        {
            var measured = ordinal >= Warmup;
            foreach (var arm in ordinal % 2 == 0 ? EvenOrder : OddOrder)
            {
                if (arm == "A")
                {
                    var before = Probe.Before(process);
                    var result = VirtualAstrometryFixture.MeasureV2(pixelsInput, nominal);
                    var after = Probe.After(process);
                    var check = Probe.Before(process);
                    var detectionCount = result.Detections.Count;
                    if (result.Status != StellarMeasurementStatus.Completed) failures.Add($"Arm A ordinal {ordinal} ended {result.Status}.");
                    verification.Add(new { ordinal, arm, cost = Probe.After(process).Since(check) });
                    samples.Add(new(ordinal, arm, measured, after.Since(before), (after.Allocated - before.Allocated) / (double)pixels,
                        null, detectionCount));
                }
                else
                {
                    var before = Probe.Before(process);
                    var outcome = await adapter.ExecuteAsync(request, CancellationToken.None).ConfigureAwait(false);
                    var after = Probe.After(process);
                    var check = Probe.Before(process);
                    var difference = mismatch(outcome);
                    if (difference is not null) failures.Add($"Arm B ordinal {ordinal}: {difference}.");
                    verification.Add(new { ordinal, arm, cost = Probe.After(process).Since(check) });
                    samples.Add(new(ordinal, arm, measured, after.Since(before), (after.Allocated - before.Allocated) / (double)pixels,
                        outcome.Products.Count == 1 ? outcome.Products[0].Payload.Length : null, null));
                }
            }
        }
        var a = samples.Where(static s => s.Measured && s.Arm == "A").ToArray();
        var b = samples.Where(static s => s.Measured && s.Arm == "B").ToArray();
        foreach (var sample in b)
        {
            if (sample.Cost.Milliseconds > MaximumExecutionMilliseconds)
                failures.Add($"Arm B ordinal {sample.Ordinal} took {sample.Cost.Milliseconds:F1} ms; the ceiling is {MaximumExecutionMilliseconds} ms.");
            if (sample.AllocatedBytesPerPixel > MaximumAllocatedBytesPerPixel)
                failures.Add($"Arm B ordinal {sample.Ordinal} allocated {sample.AllocatedBytesPerPixel:F2} B/pixel; the ceiling is {MaximumAllocatedBytesPerPixel}.");
        }
        // Paired by ordinal: both arms of one ordinal ran back to back on identical final pixels.
        var paired = b.Join(a, static s => s.Ordinal, static s => s.Ordinal, static (x, y) => (B: x, A: y)).ToArray();
        process.Refresh();
        evidence.Cost = new
        {
            pixels,
            arms = new
            {
                A = "VirtualAstrometryFixture.MeasureV2: bare #1103-qualified StellarSourceMeasurer, nominal aperture",
                B = "production MeasuredStellarAssociations recipe through CameraAgentRecipeExecutionAdapter; absolute net-new MSA baseline"
            },
            samples,
            verification,
            summary = new
            {
                A = Summarize(a),
                B = Summarize(b),
                BMinusA = new
                {
                    milliseconds = Describe(paired.Select(static p => p.B.Cost.Milliseconds - p.A.Cost.Milliseconds)),
                    cpuMilliseconds = Describe(paired.Select(static p => p.B.Cost.CpuMilliseconds - p.A.Cost.CpuMilliseconds)),
                    allocatedBytes = Describe(paired.Select(static p => (double)(p.B.Cost.AllocatedBytes - p.A.Cost.AllocatedBytes))),
                    allocatedBytesPerPixel = Describe(paired.Select(static p => p.B.AllocatedBytesPerPixel - p.A.AllocatedBytesPerPixel))
                }
            },
            durableMeasuredNodeMilliseconds = evidence.DurableMeasuredNodeMilliseconds,
            productBytes = evidence.ProductBytes,
            peakWorkingSetBytesAfterCase = process.PeakWorkingSet64
        };
    }

    private static object Summarize(OperationSample[] samples) => new
    {
        milliseconds = Describe(samples.Select(static s => s.Cost.Milliseconds)),
        cpuMilliseconds = Describe(samples.Select(static s => s.Cost.CpuMilliseconds)),
        allocatedBytes = Describe(samples.Select(static s => (double)s.Cost.AllocatedBytes)),
        allocatedBytesPerPixel = Describe(samples.Select(static s => s.AllocatedBytesPerPixel)),
        gen0 = samples.Sum(static s => s.Cost.Gen0),
        gen1 = samples.Sum(static s => s.Cost.Gen1),
        gen2 = samples.Sum(static s => s.Cost.Gen2),
        readBytes = samples.Sum(static s => s.Cost.Io.GetValueOrDefault("read_bytes")),
        writeBytes = samples.Sum(static s => s.Cost.Io.GetValueOrDefault("write_bytes"))
    };

    private sealed record TruthStar(string Id, PixelPoint Pixel, bool Eligible, VirtualAstrometryReference.TruthTrail Trail);

    // The #1103 eligibility rule (VirtualMeasuredStarQualificationTests.Truth), unchanged: a whole trail, every sample
    // interior and 6 px from the readout border, isolated by 12 px from every other visible trail. The scored position is
    // the mid-exposure sample.
    private static TruthStar[] Truth(CameraRigConfig rig, DateTimeOffset utc, AstrometricCatalogData catalog, AstrometricCalibration nominal)
    {
        var visible = VirtualAstrometryReference.Trails(rig, utc, Exposure, catalog.Stars).Where(static trail => trail.Mid is not null).ToArray();
        var width = nominal.Projection.WidthPixels; var height = nominal.Projection.HeightPixels;
        bool Interior(PixelPoint pixel) => pixel.X > 6 && pixel.Y > 6 && pixel.X < width - 6 && pixel.Y < height - 6 &&
            VirtualAstrometryReference.IsApertureInterior(rig, pixel, 12);
        return [.. visible.Select(v => new TruthStar(v.Star.Id, v.Mid!.Value,
            !v.Truncated && v.Samples.All(sample => Interior(sample!.Value)) &&
            visible.All(o => ReferenceEquals(o, v) || o.BoundsGapTo(v) > 12 || v.Samples.All(sample => o.DistanceTo(sample!.Value) > 12)), v))];
    }

    private sealed class CaseEvidence(string caseId, string profile, DateTimeOffset utc, int seed)
    {
        public string CaseId { get; } = caseId;
        public string Profile { get; } = profile;
        public DateTimeOffset Utc { get; } = utc;
        public int Seed { get; } = seed;
        public Dictionary<string, object?> Composition { get; } = new(StringComparer.Ordinal);
        public CameraModuleConfig? Configuration { get; set; }
        public object? Nodes { get; set; }
        public object? Lineage { get; set; }
        public object? Settings { get; set; }
        public object? Accuracy { get; set; }
        public object? Reruns { get; set; }
        public object? Cost { get; set; }
        public double? DurableMeasuredNodeMilliseconds { get; set; }
        public long? ProductBytes { get; set; }
        public List<string> Failures { get; } = [];
    }

    private sealed record OperationSample(int Ordinal, string Arm, bool Measured, ProbeDelta Cost, double AllocatedBytesPerPixel,
        int? ProductBytes, int? Detections);

    private sealed record ProbeDelta(double Milliseconds, double CpuMilliseconds, long AllocatedBytes, int Gen0, int Gen1, int Gen2,
        Dictionary<string, long> Io);

    // Process-wide counters: the async recipe may resume on another thread, so current-thread allocation would miss work.
    // Before-probes read procfs first and the clock last; after-probes read the clock first, so probe work stays outside.
    private readonly record struct Probe(long Timestamp, TimeSpan Cpu, long Allocated, int Gen0, int Gen1, int Gen2,
        Dictionary<string, long> Io)
    {
        public static Probe Before(Process process)
        {
            var io = ReadIo();
            process.Refresh();
            var cpu = process.TotalProcessorTime;
            var (gen0, gen1, gen2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            return new(Stopwatch.GetTimestamp(), cpu, allocated, gen0, gen1, gen2, io);
        }

        public static Probe After(Process process)
        {
            var timestamp = Stopwatch.GetTimestamp();
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var (gen0, gen1, gen2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            process.Refresh();
            return new(timestamp, process.TotalProcessorTime, allocated, gen0, gen1, gen2, ReadIo());
        }

        public TimeSpan Elapsed(Probe before) => Stopwatch.GetElapsedTime(before.Timestamp, Timestamp);

        public ProbeDelta Since(Probe before) => new(Elapsed(before).TotalMilliseconds, (Cpu - before.Cpu).TotalMilliseconds,
            Allocated - before.Allocated, Gen0 - before.Gen0, Gen1 - before.Gen1, Gen2 - before.Gen2,
            Io.ToDictionary(static item => item.Key, item => item.Value - before.Io.GetValueOrDefault(item.Key), StringComparer.Ordinal));
    }

    private static void Check(List<string> failures, bool condition, string message)
    {
        if (!condition) failures.Add(message);
    }

    private static string Sha256(ReadOnlyMemory<byte> payload) => Convert.ToHexString(SHA256.HashData(payload.Span));

    private static SortedDictionary<string, int> Counts(IEnumerable<string> values)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in values) counts[value] = counts.GetValueOrDefault(value) + 1;
        return counts;
    }

    private static object? Describe(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0) return null;
        return new
        {
            count = ordered.Length,
            minimum = ordered[0],
            median = ordered.Length % 2 == 1 ? ordered[ordered.Length / 2] : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2,
            p95 = ordered[(int)Math.Ceiling(.95 * ordered.Length) - 1],
            maximum = ordered[^1],
            mean = ordered.Average()
        };
    }

    private static Dictionary<string, long> ReadIo()
    {
        if (!OperatingSystem.IsLinux()) return [];
        return File.ReadAllLines("/proc/self/io").Select(static line => line.Split(':', 2))
            .ToDictionary(static parts => parts[0], static parts => long.Parse(parts[1].Trim(), CultureInfo.InvariantCulture), StringComparer.Ordinal);
    }

    private static Dictionary<string, long> ReadStatus()
    {
        if (!OperatingSystem.IsLinux()) return [];
        return File.ReadAllLines("/proc/self/status").Select(static line => line.Split(':', 2))
            .Where(static parts => parts[0] is "VmRSS" or "VmHWM" or "RssAnon" or "RssFile" or "Threads")
            .ToDictionary(static parts => parts[0],
                static parts => long.Parse(parts[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture) * (parts[0] == "Threads" ? 1 : 1024),
                StringComparer.Ordinal);
    }
}
