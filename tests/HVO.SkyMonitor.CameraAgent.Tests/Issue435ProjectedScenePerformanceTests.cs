using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires a public test class.")]
public sealed class Issue435ProjectedScenePerformanceTests
{
    internal const int WarmupCount = 5;
    internal const int MeasuredCount = 30;
    internal const int ReconciliationRecordCount = 1024;
    internal const int ReconciliationBatchSize = 512;
    private const int TotalProductCount = WarmupCount + MeasuredCount;
    private const string ResultSchema = "issue-435-projected-scene-performance-v2";
    private static readonly DateTimeOffset FixtureUtc = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [TestMethod]
    public async Task W1ProjectedSceneStagePersistRetrieveAndReconciliationEvidence()
    {
        if (Environment.GetEnvironmentVariable("HVO_ISSUE435_EVIDENCE") != "1")
            Assert.Inconclusive("Set HVO_ISSUE435_EVIDENCE=1 for an explicit issue #435 evidence run.");
        Assert.AreEqual("Release", BuildConfiguration);

        var repositoryRoot = GetRepositoryRoot();
        var measuredSources = ReadMeasuredSourceProvenance(repositoryRoot);
        var git = ReadGitProvenance(repositoryRoot);
        Assert.AreEqual(string.Empty, git.CleanAllStatus,
            "Issue #435 claimable evidence refuses tracked or untracked/unignored worktree content. Commit the source and remove untracked files first; ignored TestResults are allowed.");
        var requestedRevision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION");
        Assert.AreEqual(git.HeadCommit, requestedRevision,
            "HVO_EVIDENCE_REVISION must be set and exactly equal git HEAD.");
        var receipt = ReadAndVerifyBuildReceipt(repositoryRoot, git, measuredSources);
        var dependencySet = ReadDependencyAssemblySetProvenance(repositoryRoot, measuredSources);
        var assembly = dependencySet.Assemblies.Single(static item =>
            item.Name == "HVO.SkyMonitor.CameraAgent.Tests");

        var evidenceRoot = Environment.GetEnvironmentVariable("HVO_ISSUE435_EVIDENCE_ROOT");
        Assert.IsFalse(string.IsNullOrWhiteSpace(evidenceRoot));
        var trialId = CreateTrialId(assembly.Sha256);
        Assert.AreEqual(receipt.Document.Trial, trialId);
        var output = Path.GetFullPath(Path.Combine(evidenceRoot!, git.HeadCommit, "trials", trialId));
        var work = Path.Combine(output, "work");
        Assert.IsFalse(Directory.Exists(work), "Evidence output must not overwrite a prior run.");
        Directory.CreateDirectory(work);

        try
        {
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = work,
                RawIngressReserveBytes = 0,
                ProjectedSceneStaging = new ProjectedSceneStagingOptions
                {
                    MaximumFileCount = 2048,
                    MaximumTotalBytes = 512L * 1024 * 1024,
                    MaximumReconciliationEntries = ReconciliationBatchSize
                }
            });
            using var staging = new ProjectedSceneStagingStore(options);
            var fixtures = new List<ProductFixture>(TotalProductCount);
            var measurements = new List<OperationMeasurement>(MeasuredCount);
            for (var ordinal = 0; ordinal < TotalProductCount; ordinal++)
            {
                var fixture = await CreateFixtureAsync(ordinal).ConfigureAwait(false);
                fixtures.Add(fixture);
                var measured = ordinal >= WarmupCount;
                var stage = await MeasureBoundaryAsync(async () =>
                {
                    await staging.StageAsync(fixture.StageKey, fixture.Scene.SceneIdentitySha256,
                        fixture.VisibleScene, CancellationToken.None).ConfigureAwait(false);
                    await staging.DeleteAsync(fixture.StageKey, CancellationToken.None).ConfigureAwait(false);
                }).ConfigureAwait(false);
                if (ordinal == WarmupCount)
                {
                    using var faultTelemetry = new CaptureProcessingTelemetry();
                    using var faultStore = new SqliteCaptureProcessingStore(options);
                    using var faultStorage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
                    var faultPersistence = new CaptureProcessingPersistence(
                        options, faultStore, faultStorage, faultTelemetry,
                        NullLogger<CaptureProcessingPersistence>.Instance, new AfterPublicationFaultInjector());
                    await Assert.ThrowsExactlyAsync<InjectedPublicationFaultException>(async () =>
                        await faultPersistence.WriteNodeAsync(
                            fixture.Receipt, fixture.Node, DurableProcessingNodeStatus.Completed, null, 1,
                            fixture.Descriptor.Timing.ExposureStartedUtc,
                            fixture.Descriptor.Timing.ReadoutCompletedUtc,
                            TimeSpan.Zero, ProcessingOutcomeStatus.Produced, ordinal + 1, null,
                            [fixture.Product], fixture.Context, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                    Assert.AreEqual(0L, ReadCount(work,
                        $"SELECT COUNT(*) FROM processing_outputs WHERE output_identity_sha256 = '{fixture.Product.OutputIdentitySha256}';"));
                }
                var publication = await MeasureBoundaryAsync(async () =>
                {
                    using var telemetry = new CaptureProcessingTelemetry();
                    using var store = new SqliteCaptureProcessingStore(options);
                    using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
                    var persistence = new CaptureProcessingPersistence(
                        options, store, storage, telemetry, NullLogger<CaptureProcessingPersistence>.Instance);
                    await persistence.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                    await persistence.WriteNodeAsync(
                        fixture.Receipt, fixture.Node, DurableProcessingNodeStatus.Completed, null, 1,
                        fixture.Descriptor.Timing.ExposureStartedUtc,
                        fixture.Descriptor.Timing.ReadoutCompletedUtc,
                        TimeSpan.Zero, ProcessingOutcomeStatus.Produced, ordinal + 1, null,
                        [fixture.Product], fixture.Context, CancellationToken.None).ConfigureAwait(false);
                }).ConfigureAwait(false);
                var retrieval = await MeasureBoundaryAsync(async () =>
                {
                    var restored = await RestoreFreshAsync(options, fixture).ConfigureAwait(false);
                    AssertProduct(fixture.Product, restored);
                }).ConfigureAwait(false);
                if (measured) measurements.Add(new(stage, publication, retrieval));
            }

            Assert.HasCount(MeasuredCount, measurements);
            Assert.AreEqual(TotalProductCount, fixtures.Select(static item => item.Descriptor.Capture.CaptureId).Distinct().Count());
            Assert.AreEqual(TotalProductCount, fixtures.Select(static item => item.Descriptor.Artifact.ArtifactId).Distinct().Count());
            Assert.AreEqual(TotalProductCount, fixtures.Select(static item => item.Scene.SceneIdentitySha256).Distinct().Count());
            Assert.AreEqual(TotalProductCount, fixtures.Select(static item => item.Product.OutputIdentitySha256).Distinct().Count());
            Assert.AreEqual(TotalProductCount, fixtures.Select(static item =>
                ProcessingIdentity.CreateArtifactId(item.Product.OutputIdentitySha256)).Distinct().Count());

            var duplicate = fixtures[WarmupCount];
            using (var duplicateTelemetry = new CaptureProcessingTelemetry())
            using (var duplicateStore = new SqliteCaptureProcessingStore(options))
            using (var duplicateStorage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var duplicatePersistence = new CaptureProcessingPersistence(
                    options, duplicateStore, duplicateStorage, duplicateTelemetry,
                    NullLogger<CaptureProcessingPersistence>.Instance);
                await duplicatePersistence.WriteNodeAsync(
                    duplicate.Receipt, duplicate.Node, DurableProcessingNodeStatus.Completed, null, 2,
                    duplicate.Descriptor.Timing.ExposureStartedUtc,
                    duplicate.Descriptor.Timing.ReadoutCompletedUtc,
                    TimeSpan.Zero, ProcessingOutcomeStatus.Produced, WarmupCount + 1, null,
                    [duplicate.Product], duplicate.Context, CancellationToken.None).ConfigureAwait(false);
            }

            using (var verificationStore = new SqliteCaptureProcessingStore(options))
            {
                foreach (var fixture in fixtures)
                {
                    var rows = await verificationStore.ReadCaptureProductsAsync(
                        fixture.Descriptor.Capture.CaptureId, ProjectedSceneV1.CurrentSchemaVersion, 2,
                        CancellationToken.None).ConfigureAwait(false);
                    Assert.HasCount(1, rows);
                    Assert.AreEqual(fixture.Product.OutputIdentitySha256, rows[0].OutputIdentitySha256);
                    Assert.AreEqual(ProcessingIdentity.CreateArtifactId(fixture.Product.OutputIdentitySha256), rows[0].ArtifactId);
                }
            }
            Assert.AreEqual(TotalProductCount, ReadCount(work, "SELECT COUNT(*) FROM processing_outputs WHERE product_schema_version = 'projected-scene-v1';"));
            Assert.AreEqual(TotalProductCount, ReadCount(work, "SELECT COUNT(DISTINCT output_identity_sha256) FROM processing_outputs WHERE product_schema_version = 'projected-scene-v1';"));
            Assert.AreEqual(TotalProductCount, ReadCount(work, "SELECT COUNT(DISTINCT payload_relative_path) FROM processing_outputs WHERE product_schema_version = 'projected-scene-v1';"));

            var restartEqual = true;
            foreach (var fixture in fixtures.Skip(WarmupCount))
                restartEqual &= ProductsEqual(fixture.Product, await RestoreFreshAsync(options, fixture).ConfigureAwait(false));
            Assert.IsTrue(restartEqual);

            var stagingDirectory = Path.Combine(work, "staging", "projected-scenes");
            Directory.CreateDirectory(stagingDirectory);
            for (var index = 0; index < ReconciliationRecordCount; index++)
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, $".{index:D4}.tmp"), [1]).ConfigureAwait(false);
            var reconciliation = Stopwatch.StartNew();
            var first = await staging.ReconcileAsync(new HashSet<string>(), CancellationToken.None).ConfigureAwait(false);
            var second = await staging.ReconcileAsync(new HashSet<string>(), CancellationToken.None).ConfigureAwait(false);
            reconciliation.Stop();
            Assert.AreEqual(ReconciliationBatchSize, first.Inspected);
            Assert.AreEqual(ReconciliationBatchSize, second.Inspected);
            Assert.AreEqual(0, second.BacklogCount);
            Assert.IsEmpty(Directory.EnumerateFiles(stagingDirectory, "*.tmp").ToArray());

            var databasePath = Path.Combine(work, "journal", "raw-ingress.db");
            var walPath = string.Concat(databasePath, "-wal");
            var measuredFixtures = fixtures.Skip(WarmupCount).ToArray();
            var evidence = new
            {
                schemaVersion = ResultSchema,
                issue = 435,
                provenance = new
                {
                    headCommit = git.HeadCommit,
                    headTree = git.HeadTree,
                    mergeBase = git.MergeBase,
                    cleanAllStatus = git.CleanAllStatus,
                    cleanTrackedAndUntrackedWorktree = true,
                    sourceDiffSha256 = git.SourceDiffSha256,
                    sourceDiffRange = "origin/main...HEAD",
                    measuredSourceTreeSha256 = measuredSources.Sha256,
                    measuredSourceFileCount = measuredSources.Files.Count,
                    measuredSourceRoots = MeasuredSourcePathSpecs,
                    dependencyAssemblies = dependencySet.Assemblies,
                    dependencyAssemblyCount = dependencySet.Assemblies.Count,
                    dependencyBinarySetSha256 = dependencySet.BinarySetSha256,
                    dependencySourceSetSha256 = dependencySet.SourceSetSha256,
                    buildReceipt = new
                    {
                        path = receipt.Path,
                        sha256 = receipt.Sha256,
                        receipt.Document.SchemaVersion,
                        receipt.Document.SdkVersion,
                        receipt.Document.BuildStartedUtc,
                        receipt.Document.BuildCompletedUtc
                    },
                    runtimeDependencyInventory = receipt.RuntimeInventory,
                    runtimeDependencyFileCount = receipt.RuntimeInventory.Files.Count,
                    runtimeDependencySetSha256 = receipt.RuntimeInventory.Sha256,
                    testAssembly = assembly
                },
                environment = new
                {
                    operatingSystem = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    runtime = RuntimeInformation.FrameworkDescription,
                    gc = new { server = GCSettings.IsServerGC, latencyMode = GCSettings.LatencyMode.ToString() },
                    processorCount = Environment.ProcessorCount,
                    cpu = ReadCpuModel(),
                    configuration = BuildConfiguration,
                    command = $"dotnet build tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --configuration Release -warnaserror && HVO_ISSUE435_EVIDENCE=1 HVO_ISSUE435_EVIDENCE_ROOT={evidenceRoot} HVO_EVIDENCE_REVISION={git.HeadCommit} HVO_EVIDENCE_TRIAL={trialId} dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --no-build --configuration Release --filter FullyQualifiedName={typeof(Issue435ProjectedScenePerformanceTests).FullName}.{nameof(W1ProjectedSceneStagePersistRetrieveAndReconciliationEvidence)}"
                },
                workload = new
                {
                    id = "W1-metadata-geometry",
                    width = 1936,
                    height = 1216,
                    objectCount = measuredFixtures[0].Scene.Objects.Count,
                    segmentCount = measuredFixtures[0].Scene.Segments.Count,
                    warmupCount = WarmupCount,
                    measuredCount = MeasuredCount,
                    committedRowCount = TotalProductCount,
                    warmupsIncludedInStorage = true,
                    trialId,
                    reconciliationRecordCount = ReconciliationRecordCount,
                    reconciliationBatchSize = ReconciliationBatchSize
                },
                bytes = new
                {
                    canonicalTotal = measuredFixtures.Sum(static item => (long)item.Product.Payload.Length),
                    v3SidecarTotal = measuredFixtures.Sum(item => new FileInfo(FindSidecar(work, item.Product)).Length),
                    storedPayloadTotal = measuredFixtures.Sum(item => new FileInfo(FindPayload(work, item.Product)).Length),
                    sqlite = new FileInfo(databasePath).Length,
                    wal = File.Exists(walPath) ? new FileInfo(walPath).Length : 0
                },
                latencyMilliseconds = new
                {
                    stage = Summary(measurements.Select(static item => item.Stage.WallMilliseconds)),
                    publicationFilesystemAndSqlite = Summary(measurements.Select(static item => item.Publication.WallMilliseconds)),
                    freshRetrieval = Summary(measurements.Select(static item => item.Retrieval.WallMilliseconds)),
                    endToEnd = Summary(measurements.Select(static item => item.EndToEndMilliseconds)),
                    reconciliationDrain = reconciliation.Elapsed.TotalMilliseconds
                },
                processWideResources = new
                {
                    qualification = "CPU, GC allocation, and RSS are process-wide deltas sampled around each isolated serial boundary; runtime/test-host activity may contribute.",
                    stage = ResourceSummary(measurements.Select(static item => item.Stage)),
                    publicationFilesystemAndSqlite = ResourceSummary(measurements.Select(static item => item.Publication)),
                    freshRetrieval = ResourceSummary(measurements.Select(static item => item.Retrieval)),
                    rssSampleCount = measurements.Count * 6
                },
                throughput = new
                {
                    definition = "Thirty unique measured products divided by the sum of their stage, transactional publication, and fresh retrieval wall time.",
                    uniqueEndToEndProductsPerSecond = MeasuredCount /
                        measurements.Sum(static item => item.EndToEndMilliseconds) * 1000
                },
                logicalOperations = new
                {
                    filesystem = new
                    {
                        stagePublishes = TotalProductCount,
                        stageDeletes = TotalProductCount,
                        uniquePayloadPublishes = TotalProductCount,
                        uniqueV3SidecarPublishes = TotalProductCount,
                        duplicateExistingFileAuthentications = 1,
                        freshPayloadRetrievals = TotalProductCount + MeasuredCount,
                        reconciliationFilesCreated = ReconciliationRecordCount,
                        reconciliationFilesDeleted = first.Deleted + second.Deleted
                    },
                    sqlite = new
                    {
                        committedNodeRows = TotalProductCount,
                        committedOutputRows = TotalProductCount,
                        statementCount = "N/A",
                        reason = "Microsoft.Data.Sqlite statement tracing was not instrumented; exact committed logical rows are reported."
                    },
                    filesystemSyscalls = new
                    {
                        value = "N/A",
                        reason = "Kernel syscall tracing was not instrumented; exact logical file publications/retrievals are reported."
                    }
                },
                reconciliation = new
                {
                    passes = 2,
                    inspected = first.Inspected + second.Inspected,
                    deleted = first.Deleted + second.Deleted,
                    outcomes = new { deleted = first.Deleted + second.Deleted, retained = 0 },
                    initialBacklog = ReconciliationRecordCount,
                    finalBacklog = second.BacklogCount,
                    drainMilliseconds = reconciliation.Elapsed.TotalMilliseconds
                },
                correctness = new
                {
                    measuredRowCount = MeasuredCount,
                    totalRowCount = TotalProductCount,
                    uniqueCaptureIds = fixtures.Select(static item => item.Descriptor.Capture.CaptureId).Distinct().Count(),
                    uniqueOutputIdentities = fixtures.Select(static item => item.Product.OutputIdentitySha256).Distinct().Count(),
                    uniqueArtifactIds = fixtures.Select(static item => ProcessingIdentity.CreateArtifactId(item.Product.OutputIdentitySha256)).Distinct().Count(),
                    restartEqualityAllMeasuredProducts = restartEqual,
                    duplicatePublicationConvergence = new
                    {
                        submissions = 3,
                        faultWindowBeforeCommit = true,
                        rowCount = ReadCount(work,
                            $"SELECT COUNT(*) FROM processing_outputs WHERE output_identity_sha256 = '{duplicate.Product.OutputIdentitySha256}';"),
                        payloadPathCount = Directory.EnumerateFiles(Path.Combine(work, "derived"),
                            $"*{ProcessingIdentity.CreateArtifactId(duplicate.Product.OutputIdentitySha256):N}.json", SearchOption.AllDirectories)
                            .Count(static path => !path.EndsWith(".manifest.json", StringComparison.Ordinal)),
                        sidecarPathCount = Directory.EnumerateFiles(Path.Combine(work, "derived"),
                            $"*{ProcessingIdentity.CreateArtifactId(duplicate.Product.OutputIdentitySha256):N}.manifest.json", SearchOption.AllDirectories).Count(),
                        duplicate.Product.OutputIdentitySha256,
                        artifactId = ProcessingIdentity.CreateArtifactId(duplicate.Product.OutputIdentitySha256)
                    },
                    measuredChecksums = measuredFixtures.Select(static item => new
                    {
                        item.Descriptor.Capture.CaptureId,
                        item.Product.OutputIdentitySha256,
                        artifactId = ProcessingIdentity.CreateArtifactId(item.Product.OutputIdentitySha256),
                        item.Product.ChecksumSha256,
                        item.Scene.SceneIdentitySha256
                    }).ToArray()
                },
                measuredBoundaries = new
                {
                    included = new[]
                    {
                        "source-independent canonical ProjectedSceneStagingStore stage/delete",
                        "CaptureProcessingPersistence plus SqliteCaptureProcessingStore transactional V3 filesystem/SQLite publication",
                        "fresh CaptureProcessingPersistence/SqliteCaptureProcessingStore retrieval",
                        "ProjectedSceneStagingStore bounded reconciliation"
                    },
                    correctnessOnly = new[]
                    {
                        "duplicate existing-file authentication and SQLite transaction convergence",
                        "DerivedProductReconciliationService correctness and span evidence"
                    },
                    notMeasured = new[]
                    {
                        "physical CaptureProjectedSceneStager catalog computation",
                        "CameraAgent host scheduling and capture-loop contention",
                        "DerivedProductReconciliationService performance"
                    }
                },
                interpretation = "The isolated serial timings apply only to canonical staging-store I/O, transactional V3 filesystem/SQLite publication, fresh retrieval, and bounded stage reconciliation.",
                residualRisk = "Physical stager computation, host scheduling, and DerivedProductReconciliationService are N/A for performance; the derived service has correctness/span evidence only. SQLite statement/syscall counts require external tracing; process-wide resource deltas can include test-host/runtime activity."
            };
            var convergence = evidence.correctness.duplicatePublicationConvergence;
            Assert.AreEqual(1L, convergence.rowCount);
            Assert.AreEqual(1, convergence.payloadPathCount);
            Assert.AreEqual(1, convergence.sidecarPathCount);
            Assert.AreEqual(git.HeadCommit, ReadGit(repositoryRoot, "rev-parse", "HEAD"));
            Assert.AreEqual(string.Empty,
                ReadGit(repositoryRoot, "status", "--porcelain", "--untracked-files=all"));
            var finalRuntimeInventory = ReadRuntimeOutputProvenance(
                Path.GetDirectoryName(typeof(Issue435ProjectedScenePerformanceTests).Assembly.Location)!);
            Assert.AreEqual(receipt.RuntimeInventory.Sha256, finalRuntimeInventory.Sha256,
                "Runtime output changed after receipt verification.");
            Assert.AreEqual(receipt.RuntimeInventory.Files.Count, finalRuntimeInventory.Files.Count);
            Directory.CreateDirectory(output);
            var evidencePath = Path.Combine(output, "issue-435-projected-scene-performance.json");
            var stream = new FileStream(
                evidencePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            try
            {
                await JsonSerializer.SerializeAsync(stream, evidence, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            Console.WriteLine($"Issue #435 evidence: {evidencePath}");
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    private static async Task<ProductFixture> CreateFixtureAsync(int ordinal)
    {
        var utc = FixtureUtc.AddMilliseconds(ordinal);
        var visible = await CreateRepresentativeSceneAsync(utc).ConfigureAwait(false);
        Assert.HasCount(300, visible.Objects);
        Assert.IsNotEmpty(visible.Segments);
        var rawBytes = new byte[1936 * 1216 * 2];
        var template = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 1936, 1216, 1936 * 2, rawBytes);
        var captureId = Guid.Parse($"43500000-0000-0000-0001-{ordinal + 1:D12}");
        var rawArtifactId = Guid.Parse($"43500000-0000-0000-0002-{ordinal + 1:D12}");
        var descriptor = template.Descriptor with
        {
            Capture = template.Descriptor.Capture with { CaptureId = captureId, CaptureSequence = ordinal + 1 },
            Timing = template.Descriptor.Timing with
            {
                RequestedStartUtc = utc,
                ExposureStartedUtc = utc,
                ExposureEndedUtc = utc.AddSeconds(1),
                ReadoutCompletedUtc = utc.AddSeconds(1),
                DurableIngressUtc = utc.AddSeconds(1)
            },
            Artifact = template.Descriptor.Artifact with { ArtifactId = rawArtifactId, CreatedUtc = utc.AddSeconds(1) }
        };
        var manifest = new ArtifactManifestV2(ArtifactManifestV2.CurrentSchemaVersion, descriptor, $"raw/{ordinal:D3}.bin");
        var source = new ProjectedSceneSource(captureId, rawArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor));
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.VirtualRenderAuthoritative, visible,
            ProjectedSceneImageTransformV1.Identity(1936, 1216), source,
            "issue-435-w1-calibration-v1", "issue-435-w1-projection-v1");
        var payload = ProjectedSceneJson.Serialize(scene);
        var product = CreateProduct(descriptor, scene, payload, ordinal);
        var config = CreateConfig();
        var frame = new CameraFrame(utc, 1, 1, CameraPixelFormat.Mono8, new byte[] { 0 },
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0), 1);
        var submission = new CaptureLoopSubmission(
            new CaptureRequest(utc, TimeSpan.FromSeconds(1), CaptureMode.Still),
            new CaptureResult(frame, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null),
                TimeSpan.Zero, CaptureMode.Still, false), utc, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed, manifest,
            new StoredFrameReference(manifest.RelativeArtifactPath, manifest.RelativeArtifactPath, utc, FrameArtifactRole.Raw),
            CaptureContractJson.ComputeManifestSha256(manifest));
        var context = new CaptureProcessingContext(config, submission, receipt);
        var step = new FixedProductStep(product);
        var node = new CaptureProcessingGraphNode(
            $"projected-scene-{ordinal:D3}", step, ["$raw"], true, step.RecipeName,
            step.OutputRole, step.OutputVariant, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"plan-{ordinal}"))));
        return new(descriptor, receipt, context, node, visible, scene, product,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"stage-{ordinal}"))));
    }

    private static async Task<ProcessingProduct> RestoreFreshAsync(
        IOptions<CameraAgentHostOptions> options,
        ProductFixture fixture)
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var store = new SqliteCaptureProcessingStore(options);
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        var persistence = new CaptureProcessingPersistence(
            options, store, storage, telemetry, NullLogger<CaptureProcessingPersistence>.Instance);
        var durable = await persistence.ReadNodeAsync(
            fixture.Descriptor.Capture.CaptureId, fixture.Node.Id, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(durable);
        var context = new CaptureProcessingContext(CreateConfig(), fixture.Context.Submission, fixture.Receipt);
        await persistence.RestoreNodeAsync(durable, context, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, context.ProcessingProducts);
        return context.ProcessingProducts[0];
    }

    private static async Task<BoundaryMeasurement> MeasureBoundaryAsync(Func<Task> operation)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var rssBefore = process.WorkingSet64;
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        await operation().ConfigureAwait(false);
        stopwatch.Stop();
        process.Refresh();
        return new(stopwatch.Elapsed.TotalMilliseconds,
            (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            rssBefore, process.WorkingSet64);
    }

    private static ProcessingProduct CreateProduct(
        ReconstructionDescriptor descriptor, ProjectedSceneV1 scene, byte[] payload, int ordinal)
    {
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            BuiltInProcessingRecipes.ProjectedScene, "1.0.0", "issue-435-performance-v1",
            JsonSerializer.SerializeToElement(new { workload = "W1", ordinal })));
        var sources = new[] { descriptor.Artifact.ArtifactId };
        return new ProcessingProduct(FrameArtifactRole.Metadata, $"projected-scene-{ordinal:D3}",
            ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, $"projected-scene-{ordinal:D3}", recipe.IdentitySha256, sources),
            "application/json", null, payload, ProcessingIdentity.ComputePayloadSha256(payload), recipe,
            [new ProcessingAlgorithmIdentity("visible-scene", "issue-435-performance-v1")], sources,
            TimeSpan.Zero, CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor))
        {
            Kind = ProcessingProductKind.Metadata,
            SchemaVersion = ProjectedSceneV1.CurrentSchemaVersion,
            ContentIdentitySha256 = scene.SceneIdentitySha256
        };
    }

    private static async Task<VisibleScene> CreateRepresentativeSceneAsync(DateTimeOffset utc)
    {
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(utc, -115) / 15d;
        var objects = Enumerable.Range(0, 300).Select(index => new CelestialCatalogObject(
            $"star-{index:D3}", $"Star {index:D3}", (rightAscension + (index % 20 - 10) * 0.005 + 24) % 24,
            35 + (index / 20 - 7) * 0.05, -1 + index * 0.02,
            HipparcosId: (1000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        return await new VisibleSceneBuilder(new InMemoryCelestialCatalog(objects), new RepresentativeTopology(objects))
            .BuildAsync(new VisibleSceneRequest(utc, new ObserverLocation(35, -115, 1000),
                new EquidistantProjectionContext(968, 608, 560, 560, WidthPixels: 1936, HeightPixels: 1216),
                new CatalogQuery(6.5, 300),
                new CatalogMetadata("issue-435-deterministic", "1", new Uri("https://example.invalid/issue-435"), new string('A', 64), "fixture", "1"),
                projectionVersion: "issue-435-w1-projection-v1",
                algorithmVersion: "visible-scene-iau1976-constellation-v2",
                constellationIds: ["TST"], includeConstellationEndpointStars: true)).ConfigureAwait(false);
    }

    private static CameraModuleConfig CreateConfig() => new(
        new ObservatoryLocation(35, -115, 1000, "UTC"), new CameraModuleDescriptor("VirtualSky"),
        new CameraRigConfig(new SensorProfile("W1", 1936, 1216, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16),
            new OpticsProfile("EquidistantFisheye", 2.5, 170, 0), new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
        AgentId: "issue-435-performance");

    private static void AssertProduct(ProcessingProduct expected, ProcessingProduct actual) =>
        Assert.IsTrue(ProductsEqual(expected, actual));

    private static bool ProductsEqual(ProcessingProduct expected, ProcessingProduct actual) =>
        expected.OutputIdentitySha256 == actual.OutputIdentitySha256 &&
        expected.ChecksumSha256 == actual.ChecksumSha256 &&
        expected.ContentIdentitySha256 == actual.ContentIdentitySha256 &&
        expected.Payload.Span.SequenceEqual(actual.Payload.Span);

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Callers pass fixed private evidence queries without external input.")]
    private static long ReadCount(string root, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FindPayload(string root, ProcessingProduct product) =>
        FindProductFile(root, product, manifest: false);

    private static string FindSidecar(string root, ProcessingProduct product) =>
        FindProductFile(root, product, manifest: true);

    private static string FindProductFile(string root, ProcessingProduct product, bool manifest)
    {
        var artifact = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256).ToString("N");
        return Directory.EnumerateFiles(Path.Combine(root, "derived"), manifest ? "*.manifest.json" : "*.json", SearchOption.AllDirectories)
            .Single(path => path.Contains(artifact, StringComparison.Ordinal) &&
                (manifest || !path.EndsWith(".manifest.json", StringComparison.Ordinal)));
    }

    private static object Summary(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new { median = Percentile(sorted, 0.5), p95 = Percentile(sorted, 0.95) };
    }

    private static object ResourceSummary(IEnumerable<BoundaryMeasurement> values)
    {
        var samples = values.ToArray();
        return new
        {
            cpuDeltaMilliseconds = Summary(samples.Select(static item => item.CpuMilliseconds)),
            allocatedBytes = Summary(samples.Select(static item => (double)item.AllocatedBytes)),
            rssBeforeBytes = Summary(samples.Select(static item => (double)item.RssBeforeBytes)),
            rssAfterBytes = Summary(samples.Select(static item => (double)item.RssAfterBytes)),
            rssPeakSampleBytes = samples.Max(static item => Math.Max(item.RssBeforeBytes, item.RssAfterBytes))
        };
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static GitProvenance ReadGitProvenance(string root)
    {
        _ = ReadGit(root, "rev-parse", "--verify", "origin/main");
        var head = ReadGit(root, "rev-parse", "HEAD");
        var tree = ReadGit(root, "rev-parse", "HEAD^{tree}");
        var mergeBase = ReadGit(root, "merge-base", "origin/main", "HEAD");
        var status = ReadGit(root, "status", "--porcelain", "--untracked-files=all");
        var diff = ReadGitBytes(root, ["diff", "--binary", "origin/main...HEAD"]);
        return new(head, tree, mergeBase, status, Convert.ToHexString(SHA256.HashData(diff)));
    }

    private static MeasuredSourceProvenance ReadMeasuredSourceProvenance(string repositoryRoot)
    {
        var output = ReadGitBytes(repositoryRoot, ["ls-files", "-z", "--", .. MeasuredSourcePathSpecs]);
        var files = Encoding.UTF8.GetString(output).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.IsNotEmpty(files, "The measured source inventory is empty.");
        using var manifest = new MemoryStream();
        foreach (var relativePath in files)
        {
            var fullPath = Path.Combine(repositoryRoot, relativePath);
            Assert.IsTrue(File.Exists(fullPath), $"Tracked measured source is missing: {relativePath}");
            var content = File.ReadAllBytes(fullPath);
            var line = Encoding.UTF8.GetBytes($"{relativePath}\t{content.LongLength}\t{Convert.ToHexString(SHA256.HashData(content))}\n");
            manifest.Write(line);
        }
        return new(files, Convert.ToHexString(SHA256.HashData(manifest.ToArray())));
    }

    private static VerifiedBuildReceipt ReadAndVerifyBuildReceipt(
        string repositoryRoot,
        GitProvenance git,
        MeasuredSourceProvenance measuredSources)
    {
        var configuredPath = Environment.GetEnvironmentVariable("HVO_ISSUE435_BUILD_RECEIPT");
        Assert.IsFalse(string.IsNullOrWhiteSpace(configuredPath),
            "HVO_ISSUE435_BUILD_RECEIPT must identify the runner-created receipt.");
        var path = Path.GetFullPath(configuredPath!);
        Assert.IsTrue(File.Exists(path), "Issue #435 build receipt is missing.");
        var bytes = File.ReadAllBytes(path);
        var document = JsonSerializer.Deserialize<BuildReceiptDocument>(bytes, JsonOptions);
        Assert.IsNotNull(document);
        Assert.AreEqual("issue-435-build-receipt-v1", document.SchemaVersion);
        Assert.AreEqual(git.HeadCommit, document.HeadCommit);
        Assert.AreEqual(git.HeadTree, document.HeadTree);
        Assert.AreEqual(git.MergeBase, document.MergeBase);
        Assert.AreEqual(git.SourceDiffSha256, document.SourceDiffSha256, ignoreCase: true);
        Assert.AreEqual(measuredSources.Sha256, document.MeasuredSourceTreeSha256, ignoreCase: true);
        Assert.AreEqual(measuredSources.Files.Count, document.MeasuredSourceFileCount);
        Assert.AreEqual("Release", document.Configuration);
        Assert.AreEqual("10.0.100", document.SdkVersion);
        Assert.IsGreaterThan(document.BuildStartedUtc, document.BuildCompletedUtc);
        Assert.AreEqual("tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj", document.Project);
        var outputDirectory = Path.GetFullPath(document.OutputDirectory);
        Assert.AreEqual(
            Path.GetFullPath(Path.GetDirectoryName(typeof(Issue435ProjectedScenePerformanceTests).Assembly.Location)!),
            outputDirectory);
        var runtimeInventory = ReadRuntimeOutputProvenance(outputDirectory);
        Assert.AreEqual(document.RuntimeOutputSetSha256, runtimeInventory.Sha256, ignoreCase: true);
        Assert.AreEqual(document.RuntimeOutputFileCount, runtimeInventory.Files.Count);
        var trial = Environment.GetEnvironmentVariable("HVO_EVIDENCE_TRIAL");
        Assert.AreEqual(document.Trial, trial);
        var expectedReceipt = Path.GetFullPath(Path.Combine(
            Environment.GetEnvironmentVariable("HVO_ISSUE435_EVIDENCE_ROOT")!, git.HeadCommit,
            "trials", document.Trial, "issue-435-build-receipt.json"));
        Assert.AreEqual(expectedReceipt, path);
        return new(path, Convert.ToHexString(SHA256.HashData(bytes)), document, runtimeInventory);
    }

    private static RuntimeOutputProvenance ReadRuntimeOutputProvenance(string outputDirectory)
    {
        var files = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(outputDirectory, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.IsNotEmpty(files, "The runtime output inventory is empty.");
        using var manifest = new MemoryStream();
        var entries = new List<RuntimeOutputEntry>(files.Length);
        foreach (var relativePath in files)
        {
            var content = File.ReadAllBytes(Path.Combine(outputDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var sha256 = Convert.ToHexString(SHA256.HashData(content));
            entries.Add(new(relativePath, content.LongLength, sha256));
            var line = Encoding.UTF8.GetBytes($"{relativePath}\t{content.LongLength}\t{sha256}\n");
            manifest.Write(line);
        }
        return new(entries, Convert.ToHexString(SHA256.HashData(manifest.ToArray())));
    }

    private static DependencyAssemblySetProvenance ReadDependencyAssemblySetProvenance(
        string repositoryRoot,
        MeasuredSourceProvenance measuredSources)
    {
        var testAssemblyPath = Path.GetFullPath(typeof(Issue435ProjectedScenePerformanceTests).Assembly.Location);
        var outputDirectory = Path.GetDirectoryName(testAssemblyPath)!;
        var projectFiles = Encoding.UTF8.GetString(ReadGitBytes(repositoryRoot, ["ls-files", "-z", "*.csproj"]))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .GroupBy(static path => Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var tracked = Encoding.UTF8.GetString(ReadGitBytes(repositoryRoot, ["ls-files", "-z"]))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var assemblies = new List<DependencyAssemblyProvenance>();
        foreach (var assemblyPath in Directory.EnumerateFiles(
                     outputDirectory, "HVO.SkyMonitor*.dll", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(assemblyPath);
            Assert.IsTrue(projectFiles.TryGetValue(name, out var candidates),
                $"Unknown local HVO assembly has no tracked project mapping: {name}");
            Assert.HasCount(1, candidates!, $"Local HVO assembly project mapping is ambiguous: {name}");
            var projectRelativePath = candidates![0];
            var projectRoot = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(repositoryRoot, projectRelativePath)))!;
            var compile = ReadProjectCompileProvenance(repositoryRoot, projectRelativePath, projectRoot, tracked);
            var assemblyInfo = new FileInfo(assemblyPath);
            var projectFileWriteUtc = new FileInfo(Path.Combine(repositoryRoot, projectRelativePath)).LastWriteTimeUtc;
            var latestSourceUtc = new[] { compile.LatestItemWriteUtc, projectFileWriteUtc }.Max();
            Assert.IsGreaterThanOrEqualTo(assemblyInfo.LastWriteTimeUtc, latestSourceUtc,
                $"Loaded {name} assembly predates its evaluated source/generated items. Build Release before the --no-build run.");
            var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            string? pdbSha256 = null;
            DateTime? pdbLastWriteUtc = null;
            if (File.Exists(pdbPath))
            {
                var pdbInfo = new FileInfo(pdbPath);
                Assert.IsGreaterThanOrEqualTo(pdbInfo.LastWriteTimeUtc, latestSourceUtc,
                    $"Loaded {name} PDB predates its evaluated source/generated items. Build Release before the --no-build run.");
                pdbSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pdbPath)));
                pdbLastWriteUtc = pdbInfo.LastWriteTimeUtc;
            }
            assemblies.Add(new(name, assemblyPath,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))), assemblyInfo.LastWriteTimeUtc,
                File.Exists(pdbPath) ? pdbPath : null, pdbSha256, pdbLastWriteUtc,
                projectRelativePath, Path.GetRelativePath(repositoryRoot, projectRoot).Replace(Path.DirectorySeparatorChar, '/'),
                compile.CompileItemCount, compile.TrackedCompileItemCount, compile.GeneratedCompileItemCount,
                compile.CompileItemsSha256, latestSourceUtc, compile.GeneratedItemBinding));
        }
        var required = new[]
        {
            "HVO.SkyMonitor.AgentCore", "HVO.SkyMonitor.Astronomy", "HVO.SkyMonitor.Processing",
            "HVO.SkyMonitor.CameraAgent.Common", "HVO.SkyMonitor.CameraAgent.Tests"
        };
        foreach (var name in required)
            Assert.IsTrue(assemblies.Any(item => item.Name == name), $"Required measured dependency assembly is absent: {name}");

        using var binaryHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var assembly in assemblies.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            AppendFingerprint(binaryHash, assembly.Name, Convert.FromHexString(assembly.Sha256));
            if (assembly.PdbSha256 is not null)
                AppendFingerprint(binaryHash, $"{assembly.Name}.pdb", Convert.FromHexString(assembly.PdbSha256));
            AppendFingerprint(sourceHash, assembly.ProjectPath, Convert.FromHexString(assembly.CompileItemsSha256));
        }
        return new(assemblies, Convert.ToHexString(binaryHash.GetHashAndReset()),
            Convert.ToHexString(sourceHash.GetHashAndReset()), measuredSources.Sha256);
    }

    private static ProjectCompileProvenance ReadProjectCompileProvenance(
        string repositoryRoot,
        string projectRelativePath,
        string projectRoot,
        HashSet<string> tracked)
    {
        var output = RunProcessBytes(repositoryRoot, "dotnet",
            ["msbuild", projectRelativePath, "-property:Configuration=Release", "-getItem:Compile"]);
        using var document = JsonDocument.Parse(output);
        var items = document.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
            .Select(static item => Path.GetFullPath(item.GetProperty("FullPath").GetString()!))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Order(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        Assert.IsNotEmpty(items, $"Evaluated Compile inventory is empty: {projectRelativePath}");
        var generatedRoot = Path.Combine(projectRoot, "obj") + Path.DirectorySeparatorChar;
        var trackedCount = 0;
        var generatedCount = 0;
        var latestWriteUtc = DateTime.MinValue;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var fullPath in items)
        {
            Assert.IsTrue(File.Exists(fullPath), $"Evaluated Compile item is missing: {fullPath}");
            latestWriteUtc = new[] { latestWriteUtc, new FileInfo(fullPath).LastWriteTimeUtc }.Max();
            if (fullPath.StartsWith(generatedRoot,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                generatedCount++;
                AppendFingerprint(hash, Path.GetRelativePath(projectRoot, fullPath), File.ReadAllBytes(fullPath));
                continue;
            }
            Assert.IsTrue(fullPath.StartsWith(repositoryRoot + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
                $"Evaluated non-generated Compile item is outside the repository: {fullPath}");
            var relativePath = Path.GetRelativePath(repositoryRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            Assert.IsTrue(tracked.Contains(relativePath), $"Evaluated Compile item is not tracked by git: {relativePath}");
            trackedCount++;
            AppendFingerprint(hash, relativePath, File.ReadAllBytes(fullPath));
        }
        return new(items.Length, trackedCount, generatedCount, Convert.ToHexString(hash.GetHashAndReset()),
            latestWriteUtc, generatedCount == 0
                ? "No generated obj Compile items were evaluated; assembly/PDB hashes bind compiler output."
                : "Generated obj Compile items are hashed directly and assembly/PDB hashes bind compiler output.");
    }

    private static void AppendFingerprint(IncrementalHash hash, string path, byte[] content)
    {
        var pathBytes = Encoding.UTF8.GetBytes(path);
        hash.AppendData(BitConverter.GetBytes(pathBytes.Length));
        hash.AppendData(pathBytes);
        hash.AppendData(BitConverter.GetBytes((long)content.Length));
        hash.AppendData(content);
    }

    private static string CreateTrialId(string assemblySha256)
    {
        var configured = Environment.GetEnvironmentVariable("HVO_EVIDENCE_TRIAL");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Assert.IsTrue(configured.Length <= 64 && configured.All(static value =>
                char.IsAsciiLetterOrDigit(value) || value is '-' or '_'),
                "HVO_EVIDENCE_TRIAL must be a bounded filesystem-safe identifier.");
            return configured;
        }
        return $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{assemblySha256[..12]}";
    }

    private static string ReadGit(string root, params string[] arguments) =>
        Encoding.UTF8.GetString(ReadGitBytes(root, arguments)).Trim();

    private static byte[] ReadGitBytes(string root, IReadOnlyList<string> arguments)
        => RunProcessBytes(root, "git", arguments);

    private static byte[] RunProcessBytes(
        string root,
        string fileName,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, error);
        return output.ToArray();
    }

    private static string ReadCpuModel() => OperatingSystem.IsLinux()
        ? File.ReadLines("/proc/cpuinfo").FirstOrDefault(static line => line.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? "unavailable"
        : "unavailable";

    private static string GetRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string BuildConfiguration => typeof(Issue435ProjectedScenePerformanceTests).Assembly
        .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;

    private static readonly string[] MeasuredSourcePathSpecs =
    [
        "src/HVO.SkyMonitor.AgentCore",
        "src/HVO.SkyMonitor.Astronomy",
        "src/HVO.SkyMonitor.Processing",
        "src/HVO.SkyMonitor.CameraAgent.Common",
        "src/HVO.SkyMonitor.CameraAgent",
        "tests/HVO.SkyMonitor.CameraAgent.Tests",
        "scripts/evidence:issue-435",
        "docs/validation/issue-435-runtime-signals.json",
        "Directory.Build.props",
        "Directory.Packages.props",
        "global.json"
    ];

    private sealed record ProductFixture(
        ReconstructionDescriptor Descriptor, RawCaptureReceipt Receipt, CaptureProcessingContext Context,
        CaptureProcessingGraphNode Node, VisibleScene VisibleScene, ProjectedSceneV1 Scene,
        ProcessingProduct Product, string StageKey);
    private sealed record BoundaryMeasurement(
        double WallMilliseconds, double CpuMilliseconds, long AllocatedBytes, long RssBeforeBytes, long RssAfterBytes);
    private sealed record OperationMeasurement(
        BoundaryMeasurement Stage, BoundaryMeasurement Publication, BoundaryMeasurement Retrieval)
    {
        internal double EndToEndMilliseconds => Stage.WallMilliseconds + Publication.WallMilliseconds + Retrieval.WallMilliseconds;
    }
    private sealed record GitProvenance(
        string HeadCommit, string HeadTree, string MergeBase, string CleanAllStatus, string SourceDiffSha256);
    private sealed record MeasuredSourceProvenance(IReadOnlyList<string> Files, string Sha256);
    private sealed record VerifiedBuildReceipt(
        string Path, string Sha256, BuildReceiptDocument Document, RuntimeOutputProvenance RuntimeInventory);
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "System.Text.Json constructs the private receipt DTO.")]
    private sealed record BuildReceiptDocument(
        string SchemaVersion, string HeadCommit, string HeadTree, string MergeBase,
        string SourceDiffSha256, string MeasuredSourceTreeSha256, int MeasuredSourceFileCount,
        string RuntimeOutputSetSha256, int RuntimeOutputFileCount, string SdkVersion,
        string Configuration, string Project, string OutputDirectory, string Command,
        DateTimeOffset BuildStartedUtc, DateTimeOffset BuildCompletedUtc, string Trial);
    private sealed record RuntimeOutputProvenance(IReadOnlyList<RuntimeOutputEntry> Files, string Sha256);
    private sealed record RuntimeOutputEntry(string Path, long ByteLength, string Sha256);
    private sealed record DependencyAssemblySetProvenance(
        IReadOnlyList<DependencyAssemblyProvenance> Assemblies,
        string BinarySetSha256,
        string SourceSetSha256,
        string MeasuredSourceTreeSha256);
    private sealed record DependencyAssemblyProvenance(
        string Name, string Path, string Sha256, DateTime LastWriteUtc,
        string? PdbPath, string? PdbSha256, DateTime? PdbLastWriteUtc,
        string ProjectPath, string SourceRoot, int CompileItemCount, int TrackedCompileItemCount,
        int GeneratedCompileItemCount, string CompileItemsSha256, DateTime LatestSourceWriteUtc,
        string GeneratedItemBinding);
    private sealed record ProjectCompileProvenance(
        int CompileItemCount, int TrackedCompileItemCount, int GeneratedCompileItemCount,
        string CompileItemsSha256, DateTime LatestItemWriteUtc, string GeneratedItemBinding);

    private sealed class FixedProductStep(ProcessingProduct product) : ICaptureProcessingStep, ICaptureProcessingGraphStep
    {
        public bool Enabled => true;
        public string Name => "issue-435-projected-scene";
        public int Order => 0;
        public string RecipeName => product.Recipe.Descriptor.Name;
        public FrameArtifactRole OutputRole => product.Role;
        public string OutputVariant => product.Variant;
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };
        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class AfterPublicationFaultInjector : ICaptureProcessingFaultInjector
    {
        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point == CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit)
                throw new InjectedPublicationFaultException();
        }
    }

    [SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Private deterministic fault marker is never serialized or externally constructed.")]
    private sealed class InjectedPublicationFaultException : Exception;

    private sealed class RepresentativeTopology(IReadOnlyList<CelestialCatalogObject> objects) : IConstellationTopology
    {
        public ConstellationTopologyMetadata Metadata { get; } = new(
            "issue-435-topology", "1", new Uri("https://example.invalid/issue-435-topology"),
            new string('B', 64), "fixture", "deterministic-v1");
        public IReadOnlyList<ConstellationSegment> GetSegments(string constellationId) =>
            constellationId == "TST"
                ? objects.Zip(objects.Skip(1), static (left, right) => new ConstellationSegment("TST", left.HipparcosId!, right.HipparcosId!)).ToArray()
                : [];
    }
}

[TestClass]
[TestCategory("Unit")]
public sealed class Issue435ProjectedScenePerformanceHarnessManifestTests
{
    private static readonly string[] ExpectedPerformanceWorkloads =
    [
        "representative-scene-publication",
        "representative-scene-retrieval",
        "bounded-stage-reconciliation"
    ];

    [TestMethod]
    public void ManualHarnessManifestRemainsPinned()
    {
        var root = GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "tests", "HVO.SkyMonitor.CameraAgent.Tests", "Issue435ProjectedScenePerformanceTests.cs"));
        StringAssert.Contains(source, "internal const int WarmupCount = 5;", StringComparison.Ordinal);
        StringAssert.Contains(source, "internal const int MeasuredCount = 30;", StringComparison.Ordinal);
        StringAssert.Contains(source, "internal const int ReconciliationRecordCount = 1024;", StringComparison.Ordinal);
        StringAssert.Contains(source, "internal const int ReconciliationBatchSize = 512;", StringComparison.Ordinal);
        var harness = typeof(Issue435ProjectedScenePerformanceTests);
        Assert.IsNotNull(harness.GetMethod(nameof(Issue435ProjectedScenePerformanceTests.W1ProjectedSceneStagePersistRetrieveAndReconciliationEvidence)));
        CollectionAssert.Contains(harness.GetCustomAttributes<TestCategoryAttribute>().SelectMany(static item => item.TestCategories).ToArray(), "Manual");
        Assert.HasCount(1, harness.GetCustomAttributes<DoNotParallelizeAttribute>());

        var acceptance = File.ReadAllText(Path.Combine(root, "tests", "HVO.SkyMonitor.CameraAgent.AcceptanceTests", "StandaloneCameraAgentAcceptanceTests.cs"));
        StringAssert.Contains(acceptance, "[TestCategory(\"Integration\")]", StringComparison.Ordinal);
        StringAssert.Contains(acceptance, "Task VirtualSkyProjectedScenePersistsAndRecoversStandaloneAsync()", StringComparison.Ordinal);

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Validation", "issue-435-runtime-signals.json")));
        Assert.AreEqual("issue-435-projected-scene-performance-v2", manifest.RootElement.GetProperty("resultSchema").GetString());
        Assert.AreEqual("$HVO_ISSUE435_EVIDENCE_ROOT/$HVO_EVIDENCE_REVISION/trials/$HVO_EVIDENCE_TRIAL/issue-435-projected-scene-performance.json",
            manifest.RootElement.GetProperty("resultPath").GetString());
        Assert.AreEqual("$HVO_ISSUE435_EVIDENCE_ROOT/$HVO_EVIDENCE_REVISION/trials/$HVO_EVIDENCE_TRIAL/issue-435-build-receipt.json",
            manifest.RootElement.GetProperty("buildReceiptPath").GetString());
        Assert.AreEqual("scripts/evidence:issue-435", manifest.RootElement.GetProperty("runner").GetString());
        Assert.IsTrue(manifest.RootElement.GetProperty("retainedTrials").GetBoolean());
        CollectionAssert.AreEqual(
            ExpectedPerformanceWorkloads,
            manifest.RootElement.GetProperty("performanceWorkloads").EnumerateArray()
                .Select(static item => item.GetString()).ToArray());
        StringAssert.Contains(source, "sourceDiffRange = \"origin/main...HEAD\"", StringComparison.Ordinal);
        StringAssert.Contains(source, "measuredSourceTreeSha256", StringComparison.Ordinal);
        StringAssert.Contains(source, "measuredSourceFileCount", StringComparison.Ordinal);
        StringAssert.Contains(source, "mergeBase", StringComparison.Ordinal);
        StringAssert.Contains(source,
            "status\", \"--porcelain\", \"--untracked-files=all\"", StringComparison.Ordinal);
        StringAssert.Contains(source, "-getItem:Compile", StringComparison.Ordinal);
        StringAssert.Contains(source, "Evaluated Compile item is not tracked by git", StringComparison.Ordinal);
        StringAssert.Contains(source, "compileItemCount", StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(source, "dependencyAssemblies", StringComparison.Ordinal);
        StringAssert.Contains(source, "dependencyBinarySetSha256", StringComparison.Ordinal);
        StringAssert.Contains(source, "dependencySourceSetSha256", StringComparison.Ordinal);
        StringAssert.Contains(source, "dependencyAssemblyCount", StringComparison.Ordinal);
        StringAssert.Contains(source, "Unknown local HVO assembly has no tracked project mapping", StringComparison.Ordinal);
        StringAssert.Contains(source, "Required measured dependency assembly is absent", StringComparison.Ordinal);
        StringAssert.Contains(source, "HVO_ISSUE435_BUILD_RECEIPT", StringComparison.Ordinal);
        StringAssert.Contains(source, "runtimeDependencyInventory", StringComparison.Ordinal);
        StringAssert.Contains(source, "runtimeDependencySetSha256", StringComparison.Ordinal);
        StringAssert.Contains(source, "Runtime output changed after receipt verification", StringComparison.Ordinal);
        var runner = File.ReadAllText(Path.Combine(root, "scripts", "evidence:issue-435"));
        StringAssert.Contains(runner, "--no-incremental -warnaserror", StringComparison.Ordinal);
        StringAssert.Contains(runner, "git status --porcelain --untracked-files=all", StringComparison.Ordinal);
        StringAssert.Contains(runner, "set -o noclobber", StringComparison.Ordinal);
        StringAssert.Contains(runner, "HVO_ISSUE435_BUILD_RECEIPT", StringComparison.Ordinal);
        var mappings = manifest.RootElement.GetProperty("resultFieldMappings").EnumerateObject()
            .ToDictionary(static item => item.Name, static item => item.Value.GetString()!, StringComparer.Ordinal);
        foreach (var measurement in manifest.RootElement.GetProperty("measurements").EnumerateArray().Select(static item => item.GetString()!))
        {
            Assert.IsTrue(mappings.TryGetValue(measurement, out var field), measurement);
            StringAssert.Contains(source, field.Split('.')[0], StringComparison.Ordinal);
        }
    }

    private static string GetRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
