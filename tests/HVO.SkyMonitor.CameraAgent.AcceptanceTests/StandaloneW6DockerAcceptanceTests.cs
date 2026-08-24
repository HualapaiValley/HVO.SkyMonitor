using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured async disposal would hide the strongly typed Playwright resources used throughout the acceptance scope.")]
public sealed class StandaloneW6DockerAcceptanceTests
{
    private const string ExpectedAgentId = "cameraagent-standalone-w6-asi676mc";
    private const string ExpectedCatalogSha256 = "B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2";
    private const string ExpectedRigSha256 = "7B395B8DD577024944C263E1EA642BB472E3144110FF3FE75DBBCE67B187F172";
    private const string ExpectedProcessingSha256 = "D79F743C9BDBFE027D088ADCE2B95CF23ED6E07880348CF47E01DEFF95E1FCB9";
    private const string ExpectedLocalProfileSha256 = "7D4B0803BE56ED1B898AB71FD141385C41B1D74331474AFB3A94DADEE7D7EE80";
    private const string ExpectedScheduleSha256 = "DDD63791961E018687C7E6FA095CA17EFB88F1CC5AE5861D58690129FB9A27B2";
    private const string ExpectedDesiredGraphSha256 = "E17DB795563078FD0B8C054586A3F059C8968BD1ADB45FBBB88E188B28F76C6E";
    private const string ExpectedEffectiveGraphSha256 = "9CC4C1FF098B9EAADE1EEA7646E8E9F8C248C8C494A40DCBB0834D89D51852D3";
    private const string ExpectedMonoAgentId = "cameraagent-standalone-w6-asi174-mono8";
    private const string ExpectedMonoRigSha256 = "3233765432377F454526BAF795268FC9A73B3DEB8F0800A0D21BD652006E500C";
    private const string ExpectedMonoProcessingSha256 = "F5BA5B24130B8C2359E9518DBA7C4DC3E899FB2FDD826FA46269F1A2AC9DDC0B";
    private const string ExpectedMonoLocalProfileSha256 = "5185EEA24AE697CD841FFF787F96D886AF8EF5DF08A162098671F6B6EBFCBDB4";
    private const string ExpectedMonoScheduleSha256 = "355D9C9A53CB600E6F1798109A4BFAC8D539F6A9B0A1DF9A9D44A9EF6283ED01";
    private const string ExpectedMonoDesiredGraphSha256 = "A5F687646DFBC2BFE5716E45AA2ED9028901EAE940D2A9FC8EC70F98C283512D";
    private const string ExpectedMonoEffectiveGraphSha256 = "DBA167BAF1AAF12FDEBF9BAC029D72BBE0943CAE0AB00C9BB187D70DB0A78FEF";
    private const string CentralHandlerReadyRecord = "HVO211_HANDLER_READY";
    private const string CentralHandlerAttemptRecord = "HVO211_HANDLER_ATTEMPT";
    private static readonly string[] ExpectedCatalogRows = ["11734", "24378", "24549", "27919", "32263", "37173"];
    private static readonly string[] QualityDependentNodeIds = ["storage", "telemetry"];
    private static readonly IReadOnlyDictionary<string, DurableQueueBound> DurableQueueBounds =
        new Dictionary<string, DurableQueueBound>(StringComparer.Ordinal)
        {
            ["capture-lanes"] = RequiredLaneBound(),
            ["processing-retries"] = RequiredLaneBound(),
            ["transient-capture"] = RequiredLaneBound(),
            ["transient-frames"] = RequiredLaneBound(),
            ["transient-candidates"] = RequiredLaneBound(),
            ["artifact-outbox"] = DisabledLaneBound(),
            ["environmental-outbox"] = DisabledLaneBound(),
            ["fleet-outbox"] = DisabledLaneBound()
        };

    private static DurableQueueBound RequiredLaneBound()
        => new(10_000, 128_793_600_000, TimeSpan.FromMinutes(10_080).TotalSeconds);

    private static DurableQueueBound DisabledLaneBound() => new(0, 0, 0);
    private static readonly FrameArtifactRole[] ExpectedRoles =
    [
        FrameArtifactRole.Raw,
        FrameArtifactRole.Calibrated,
        FrameArtifactRole.Combined,
        FrameArtifactRole.Preview,
        FrameArtifactRole.AnnotatedPreview,
        FrameArtifactRole.Metadata
    ];
    private static readonly FrameArtifactRole[] ExpectedMonoRoles =
    [
        FrameArtifactRole.Raw,
        FrameArtifactRole.Preview,
        FrameArtifactRole.AnnotatedPreview
    ];
    private static readonly string[] ExpectedRetainedArtifacts =
    [
        "Raw/source",
        "Calibrated/pseudo-calibrated",
        "Combined/rolling-mean",
        "Preview/calibrated-preview",
        "Preview/combined-preview",
        "Metadata/image-quality-v1",
        "Metadata/cloud-assessment-v1",
        "AnnotatedPreview/w6-annotated",
        "AnnotatedPreview/w6-weather-overlay"
    ];
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(1_800_000)]
    public async Task FullCatalogAsi676StandaloneAsync()
    {
        var baseUriText = Environment.GetEnvironmentVariable("HVO_ISSUE_211_BASE_URI");
        if (string.IsNullOrWhiteSpace(baseUriText))
        {
            Assert.Inconclusive("Run through scripts/test:cameraagent-standalone-211.");
        }
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The W6 container evidence requires Linux.");
        }

        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        if (!File.Exists(playwright.Chromium.ExecutablePath))
        {
            Assert.Inconclusive("Install the pinned Playwright Chromium with scripts/test:cameraagent-ui-106 --install-browser.");
        }

        var baseUri = new Uri(baseUriText, UriKind.Absolute);
        var runtimeRoot = RequiredPath("HVO_ISSUE_211_RUNTIME_ROOT");
        var evidenceRoot = RequiredPath("HVO_ISSUE_211_EVIDENCE_ROOT");
        var container = Required("HVO_ISSUE_211_CONTAINER");
        var denyContainer = Required("HVO_ISSUE_211_CENTRAL_DENY_CONTAINER");
        var centralHandlerLog = RequiredPath("HVO_ISSUE_211_CENTRAL_HANDLER_LOG");
        var password = (await File.ReadAllTextAsync(RequiredPath("HVO_ISSUE_211_OWNER_PASSWORD_FILE")).ConfigureAwait(false)).Trim();
        Directory.CreateDirectory(evidenceRoot);

        await WaitForHostAsync(baseUri).ConfigureAwait(false);
        var containerEnvironment = await ReadContainerExecutionEnvironmentAsync(container).ConfigureAwait(false);
        var serviceImages = await ReadServiceImageProvenanceAsync(container).ConfigureAwait(false);
        using var session = await LoginAsync(baseUri, password).ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }).ConfigureAwait(false);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseUri.ToString(),
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        }).ConfigureAwait(false);
        context.SetDefaultTimeout(60_000);
        context.SetDefaultNavigationTimeout(60_000);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var browserErrors = new List<string>();
        page.PageError += (_, error) => browserErrors.Add(error);
        await BrowserLoginAsync(page, password).ConfigureAwait(false);

        await SetCaptureStateAsync(page, pause: true).ConfigureAwait(false);
        await AcquireCalibrationAsync(page).ConfigureAwait(false);
        var calibrationRestarted = false;
        CalibrationPublicationSnapshot? calibrationBeforeRestart = null;
        double? calibrationPublicationToRestartMilliseconds = null;
        double? calibrationRestartMilliseconds = null;
        string? preRestartMetrics = null;
        if (string.Equals(Environment.GetEnvironmentVariable("HVO_ISSUE_211_TRIAL"), "trial-1", StringComparison.Ordinal))
        {
            calibrationBeforeRestart = ReadPublishedCalibrationSnapshot(runtimeRoot, requireActive: false);
            preRestartMetrics = await ReadMetricsAsync(session).ConfigureAwait(false);
            var publicationToRestart = DateTimeOffset.UtcNow - calibrationBeforeRestart.CreatedUtc;
            Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, publicationToRestart);
            Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(30), publicationToRestart);
            calibrationPublicationToRestartMilliseconds = publicationToRestart.TotalMilliseconds;
            var calibrationRestart = Stopwatch.StartNew();
            await RestartContainerAsync(baseUri, container).ConfigureAwait(false);
            calibrationRestart.Stop();
            Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(3), calibrationRestart.Elapsed);
            calibrationRestartMilliseconds = calibrationRestart.Elapsed.TotalMilliseconds;
            calibrationRestarted = true;
        }
        await ActivateCalibrationAsync(page).ConfigureAwait(false);
        CalibrationBoundaryEvidence? calibrationBoundary = null;
        if (calibrationBeforeRestart is not null)
        {
            var afterActivation = ReadPublishedCalibrationSnapshot(runtimeRoot, requireActive: true);
            Assert.AreEqual(calibrationBeforeRestart.BundleId, afterActivation.BundleId);
            Assert.AreEqual(calibrationBeforeRestart.BundleIdentitySha256, afterActivation.BundleIdentitySha256);
            CollectionAssert.AreEquivalent(calibrationBeforeRestart.ArtifactSha256, afterActivation.ArtifactSha256);
            calibrationBoundary = new CalibrationBoundaryEvidence(
                calibrationRestarted,
                calibrationBeforeRestart,
                afterActivation,
                calibrationPublicationToRestartMilliseconds,
                calibrationRestartMilliseconds);
        }
        await ActivateCanonicalCaptureProfileAsync(session).ConfigureAwait(false);
        var scheduleUi = await AssertScheduleUiRoundTripAsync(page, runtimeRoot).ConfigureAwait(false);
        var runtimeIdentity = await ReadRuntimeIdentityAsync(
            session,
            ExpectedLocalProfileSha256,
            ExpectedScheduleSha256,
            ExpectedDesiredGraphSha256,
            ExpectedEffectiveGraphSha256).ConfigureAwait(false);
        var expectedDeploymentLocation = (await LoadW6ConfigurationAsync().ConfigureAwait(false)).DeploymentLocation
            ?? throw new InvalidDataException("The pinned W6 deployment location is missing.");
        var sequenceBeforeClear = ReadMaximumRawSequence(runtimeRoot);
        await SetCaptureStateAsync(page, pause: false).ConfigureAwait(false);
        var clearCapture = (await WaitForCompleteCapturesAsync(
            session, 1, sequenceBeforeClear, TimeSpan.FromMinutes(5)).ConfigureAwait(false)).Single();
        await SetCaptureStateAsync(page, pause: true).ConfigureAwait(false);
        var clearManifest = ReadManifests(runtimeRoot).Single(item =>
            item.Manifest.Descriptor.Capture.CaptureId == clearCapture.CaptureId &&
            item.Manifest.Descriptor.Artifact.Role == FrameArtifactRole.Calibrated);
        var clearReferencePath = Path.Combine(runtimeRoot, "w6", "clear-reference.manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(clearReferencePath)!);
        File.Copy(clearManifest.Path, clearReferencePath, overwrite: true);

        var requestedWarmupCount = ReadPositiveInteger("HVO_ISSUE_211_WARMUP_CAPTURES", 2);
        var requestedMeasuredCount = ReadPositiveInteger("HVO_ISSUE_211_MEASURED_CAPTURES", 10);
        var warmupCaptures = await CaptureExactWindowAsync(
            session,
            runtimeRoot,
            requestedWarmupCount,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        var framesRoot = Path.Combine(runtimeRoot, "frames");
        var derivedRoot = Path.Combine(runtimeRoot, "derived");
        var measuredFrameBytesBefore = DirectoryBytes(framesRoot);
        var measuredRawBytesBefore = RoleDirectoryBytes(framesRoot, FrameArtifactRole.Raw);
        var measuredProductBytesBefore = DirectoryBytes(derivedRoot);
        using var sampler = new DockerResourceSampler(container, runtimeRoot, session);
        var measuredWindow = await CaptureExactWindowAsync(
            session,
            runtimeRoot,
            requestedMeasuredCount,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(12),
            sampler: sampler).ConfigureAwait(false);
        sampler.WriteCsv(Path.Combine(evidenceRoot, "process.csv"));
        sampler.WriteQueueCsv(Path.Combine(evidenceRoot, "durable-queues.csv"));
        sampler.WriteFilesystemCsv(Path.Combine(evidenceRoot, "filesystem.csv"));
        using var faultSampler = new DockerResourceSampler(container, runtimeRoot, session);
        await faultSampler.BeginAsync().ConfigureAwait(false);
        var measuredCaptures = measuredWindow.Captures.ToArray();
        var measuredCount = measuredCaptures.Length;
        var warmupCount = warmupCaptures.Captures.Count;
        var measuredRawPayloadBytes = measuredWindow.Admissions.Sum(static admission => admission.PayloadLength);
        var rawBytesPerCapture = measuredWindow.Admissions.Select(static admission => admission.PayloadLength).Distinct().Single();
        var measuredFrameStorageGrowthBytes = DirectoryBytes(framesRoot) - measuredFrameBytesBefore;
        var measuredRawStorageGrowthBytes = RoleDirectoryBytes(framesRoot, FrameArtifactRole.Raw) - measuredRawBytesBefore;
        var measuredDerivedStorageGrowthBytes = measuredFrameStorageGrowthBytes - measuredRawStorageGrowthBytes +
            DirectoryBytes(derivedRoot) - measuredProductBytesBefore;
        var boundedTrialFootprintBytes = long.Parse(
            Required("HVO_ISSUE_211_BOUNDED_FOOTPRINT_BYTES"), CultureInfo.InvariantCulture);

        Assert.HasCount(requestedMeasuredCount, measuredCaptures);
        var captureProvenance = AssertCaptureContracts(
            runtimeRoot, measuredCaptures, expectedDeploymentLocation);
        var geometry = await AssertRepresentativeGeometryAsync(runtimeRoot, measuredCaptures).ConfigureAwait(false);
        var calibration = ReadCalibrationEvidence(runtimeRoot);
        var calibrationResiduals = await AssertCalibrationResidualsAsync(
            runtimeRoot,
            measuredCaptures[0],
            calibration).ConfigureAwait(false);
        var representative = RetainRepresentativeEvidence(runtimeRoot, evidenceRoot, measuredCaptures[0].CaptureId);
        var comparisonPreview = await AssertComparisonApiAsync(
            session, measuredCaptures[0], geometry, evidenceRoot).ConfigureAwait(false);
        var environmental = await AssertEnvironmentalEvidenceAsync(
            session, page, runtimeRoot, measuredCaptures[^1]).ConfigureAwait(false);
        var pipelinePerformance = ReadPipelinePerformance(runtimeRoot, measuredCaptures);
        var transient = await WaitForTransientConvergenceAsync(runtimeRoot, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        var transientEvidence = RetainTransientEvidence(runtimeRoot, evidenceRoot, measuredCaptures);
        var transientBrowser = await AssertTransientBrowserEvidenceAsync(page, transientEvidence).ConfigureAwait(false);
        var metricsText = await WaitForMetricSurfaceAsync(
            session,
            preRestartMetrics,
            AssertRuntimeMetricSurface,
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "metrics.prom"), metricsText).ConfigureAwait(false);
        OptionalQualityEvidence? optionalQuality = null;
        RestartEvidence? restart = null;
        PressureDrainEvidence? pressureDrain = null;
        GracefulShutdownEvidence? gracefulShutdown = null;
        CalibrationUiEvidence? calibrationUi = null;
        IReadOnlyList<SemanticFaultEvidence>? semanticFaults = null;
        var isFirstTrial = string.Equals(
            Environment.GetEnvironmentVariable("HVO_ISSUE_211_TRIAL"), "trial-1", StringComparison.Ordinal);
        if (isFirstTrial)
        {
            optionalQuality = await AssertOptionalQualityDisableAsync(session, page, runtimeRoot).ConfigureAwait(false);
        }
        if (isFirstTrial || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HVO_PHASE14_EVIDENCE_ROOT")))
        {
            semanticFaults = await AssertSemanticFaultsAsync(
                baseUri, container, runtimeRoot, page, session).ConfigureAwait(false);
        }
        if (isFirstTrial)
        {
            pressureDrain = await AssertPressureAndDrainAsync(
                runtimeRoot, clearReferencePath, measuredCaptures[0].CaptureId, page, session).ConfigureAwait(false);
            restart = await AssertDurableRestartAsync(
                baseUri, container, runtimeRoot, clearReferencePath, page).ConfigureAwait(false);
            gracefulShutdown = await AssertBoundedShutdownAsync(
                baseUri, container, runtimeRoot, page).ConfigureAwait(false);
            calibrationUi = await AssertCalibrationUiRollbackAsync(page, calibration.BundleId).ConfigureAwait(false);
            transient = await WaitForTransientConvergenceAsync(runtimeRoot, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        }
        await WaitForDurableConditionAsync(
            runtimeRoot,
            static snapshot => snapshot.PendingRawCaptures == 0 && snapshot.PendingLaneWork == 0 &&
                snapshot.PendingProcessingNodes == 0 && snapshot.DuplicateLogicalOutputs == 0,
            "final required backlog convergence",
            TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        var health = await WaitForHealthyAsync(session, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        health = await WaitForZeroBacklogHealthAsync(session, health, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        AssertPublicCatalogHealthIsSanitized(health);
        await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "health.json"), health).ConfigureAwait(false);
        await AssertBrowserEvidenceAsync(page, measuredCaptures[^1]).ConfigureAwait(false);
        Assert.IsEmpty(browserErrors, string.Join(Environment.NewLine, browserErrors));
        await faultSampler.CompleteAsync().ConfigureAwait(false);
        faultSampler.WriteCsv(Path.Combine(evidenceRoot, "process-faults.csv"));
        faultSampler.WriteQueueCsv(Path.Combine(evidenceRoot, "durable-queues-faults.csv"));
        faultSampler.WriteFilesystemCsv(Path.Combine(evidenceRoot, "filesystem-faults.csv"));
        var retainedFilesystemBytes = DirectoryBytes(runtimeRoot);

        RetainJournalSnapshot(runtimeRoot, evidenceRoot);
        var cameraLogs = await DockerLogsAsync(container).ConfigureAwait(false);
        var unexpectedErrors = cameraLogs.Split('\n').Where(static line =>
            line.Contains("[ERR]", StringComparison.Ordinal)).ToArray();
        Assert.IsEmpty(unexpectedErrors, string.Join(Environment.NewLine, unexpectedErrors));
        var centralTrafficAttempts = CountDenyHits(await DockerLogsAsync(denyContainer).ConfigureAwait(false));
        Assert.AreEqual(0, centralTrafficAttempts);
        var centralHandlerAttempts = AssertCentralHandlerAttempts(centralHandlerLog);
        var resource = sampler.Summarize();
        var faultResource = faultSampler.Summarize(requireContinuousCoverage: false);
        var durableQueueMaxima = MergeDurableQueueMaxima(
            resource.MaximumDurableQueues,
            faultResource.MaximumDurableQueues);
        AssertDurableQueueBounds(durableQueueMaxima);
        var peakFilesystemBytes = Math.Max(
            retainedFilesystemBytes,
            Math.Max(resource.PeakFilesystemBytes, faultResource.PeakFilesystemBytes));
        Assert.IsLessThanOrEqualTo(boundedTrialFootprintBytes, peakFilesystemBytes);
        var evidence = new
        {
            schemaVersion = "issue-211-w6-evidence-v1",
            trial = Environment.GetEnvironmentVariable("HVO_ISSUE_211_TRIAL") ?? "local",
            revision = new
            {
                commit = Environment.GetEnvironmentVariable("HVO_ISSUE_211_REVISION") ?? "working-tree",
                branch = Environment.GetEnvironmentVariable("HVO_ISSUE_211_BRANCH") ?? "unknown",
                dirtyStateSha256 = Environment.GetEnvironmentVariable("HVO_ISSUE_211_DIRTY_SHA256") ?? "unrecorded",
                cameraImage = Required("HVO_ISSUE_211_CAMERA_IMAGE")
            },
            workload = new
            {
                id = "W6",
                width = 3552,
                height = 3552,
                pixelFormat = CameraPixelFormat.BayerRggb16.ToString(),
                rawBytes = rawBytesPerCapture,
                observedRawPayloadBytes = measuredRawPayloadBytes,
                exposureSeconds = 5,
                cadenceSeconds = 10,
                warmupCount,
                measuredCount,
                catalogRows = 119_625,
                catalogSha256 = ExpectedCatalogSha256,
                rigSha256 = ExpectedRigSha256,
                processingSha256 = ExpectedProcessingSha256,
                scheduleSha256 = ExpectedScheduleSha256,
                localProfileSha256 = ExpectedLocalProfileSha256,
                desiredGraphSha256 = ExpectedDesiredGraphSha256,
                effectiveGraphSha256 = ExpectedEffectiveGraphSha256
            },
            method = new
            {
                worktreeFingerprint = Required("HVO_ISSUE_211_WORKTREE_FINGERPRINT"),
                samplingIntervalSeconds = 1,
                boundedTrialFootprintBytes,
                requiredFreeBytes = long.Parse(
                    Required("HVO_ISSUE_211_REQUIRED_FREE_BYTES"), CultureInfo.InvariantCulture)
            },
            clearReference = new
            {
                artifactId = clearManifest.Manifest.Descriptor.Artifact.ArtifactId,
                sha256 = clearManifest.Manifest.Descriptor.Artifact.ChecksumSha256,
                clearManifest.Manifest.Descriptor.Capture.CaptureSequence
            },
            calibrationBoundary,
            runtimeIdentity,
            expectedDeploymentLocation,
            captureProvenance,
            scheduleUi,
            warmupAdmissions = warmupCaptures.Admissions,
            measuredRawAdmissions = measuredWindow.Admissions,
            captures = measuredCaptures.Select(static capture => new
            {
                capture.CaptureId,
                capture.CaptureSequence,
                roles = capture.Artifacts.Select(static artifact => artifact.Role.ToString()).Distinct().Order().ToArray(),
                nodes = capture.ProcessingNodes.Select(static node => new { node.NodeId, node.Status }).ToArray()
            }).ToArray(),
            geometry,
            performance = new
            {
                executionEnvironment = new
                {
                    kind = "development-host-container",
                    container = containerEnvironment,
                    serviceImages,
                    harness = new
                    {
                        architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                        operatingSystem = RuntimeInformation.OSDescription,
                        processorCount = Environment.ProcessorCount,
                        availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                        framework = RuntimeInformation.FrameworkDescription
                    },
                    storage = "bind-mounted local filesystem",
                    dotnetSdk = Required("HVO_ISSUE_211_DOTNET_SDK_VERSION"),
                    dockerServer = Required("HVO_ISSUE_211_DOCKER_SERVER_VERSION"),
                    collectorImage = Required("HVO_ISSUE_211_COLLECTOR_IMAGE")
                },
                elapsedMilliseconds = measuredWindow.Elapsed.TotalMilliseconds,
                cpuSeconds = resource.EstimatedCpuSeconds,
                resource.PeakRssBytes,
                resource.PeakContainerMemoryUsageBytes,
                allocatedBytes = resource.AllocatedBytes,
                resource.MaximumObservedLastCollectionLohBytes,
                operationalCaptureRatePerSecond = measuredCaptures.Length / measuredWindow.Elapsed.TotalSeconds,
                observedRawPayloadBytes = measuredRawPayloadBytes,
                rawStorageGrowthBytes = measuredRawStorageGrowthBytes,
                derivativeStorageGrowthBytes = measuredDerivedStorageGrowthBytes,
                sqliteFootprintGrowthBytes = resource.SqliteFootprintGrowthBytes,
                declaredRawBytesPerDay = rawBytesPerCapture * (24 * 60 * 60 / 10),
                hypotheticalFiveSecondStartIntervalRawBytesPerDay = rawBytesPerCapture * (24 * 60 * 60 / 5),
                resource.ProcessReadBytes,
                resource.ProcessWriteBytes,
                containerBlockReadBytes = resource.BlockReadBytes,
                containerBlockWriteBytes = resource.BlockWriteBytes,
                resource.MaximumBacklogCount,
                resource.MaximumBacklogBytes,
                resource.MaximumBacklogAgeSeconds,
                maximumDurableQueues = durableQueueMaxima,
                declaredDurableQueueBounds = DurableQueueBounds,
                durableQueuesWithinDeclaredBounds = true,
                peakFilesystemBytes,
                retainedFilesystemBytes,
                resource.SampleCount,
                resource.SamplingFailureCount,
                resource.SamplingFailures,
                resource.MaximumSuccessfulSampleGapSeconds,
                resource.FilesystemSampleCount,
                resource.FilesystemSamplingFailureCount,
                resource.FilesystemSamplingFailures,
                resource.MaximumFilesystemSampleGapSeconds,
                resource.QueueSampleCount,
                resource.QueueSamplingFailureCount,
                resource.QueueSamplingFailures,
                resource.MaximumQueueSampleGapSeconds,
                faultSampling = new
                {
                    faultResource.SampleCount,
                    faultResource.SamplingFailureCount,
                    faultResource.SamplingFailures,
                    faultResource.MaximumSuccessfulSampleGapSeconds,
                    faultResource.FilesystemSampleCount,
                    faultResource.FilesystemSamplingFailureCount,
                    faultResource.FilesystemSamplingFailures,
                    faultResource.MaximumFilesystemSampleGapSeconds,
                    faultResource.QueueSampleCount,
                    faultResource.QueueSamplingFailureCount,
                    faultResource.QueueSamplingFailures,
                    faultResource.MaximumQueueSampleGapSeconds
                },
                pipeline = pipelinePerformance
            },
            optionalQuality,
            semanticFaults,
            pressureDrain,
            restart,
            gracefulShutdown,
            calibrationUi,
            transient,
            transientEvidence,
            transientBrowser,
            environmental,
            calibration,
            calibrationResiduals,
            representative,
            comparisonPreview,
            centralTrafficAttempts,
            centralHandlerAttempts,
            result = new { passed = true }
        };
        var evidencePath = Path.Combine(evidenceRoot, "issue-211-w6.json");
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, EvidenceJson)).ConfigureAwait(false);
        TestContext.WriteLine($"Issue #211 W6 evidence: {evidencePath}");
    }

    [TestMethod]
    [Timeout(600_000)]
    public async Task Asi174Mono8ControlAsync()
    {
        var baseUriText = Environment.GetEnvironmentVariable("HVO_ISSUE_211_BASE_URI");
        if (string.IsNullOrWhiteSpace(baseUriText))
        {
            Assert.Inconclusive("Run through scripts/test:cameraagent-standalone-211.");
        }

        var baseUri = new Uri(baseUriText, UriKind.Absolute);
        var runtimeRoot = RequiredPath("HVO_ISSUE_211_RUNTIME_ROOT");
        var evidenceRoot = RequiredPath("HVO_ISSUE_211_EVIDENCE_ROOT");
        var container = Required("HVO_ISSUE_211_CONTAINER");
        var denyContainer = Required("HVO_ISSUE_211_CENTRAL_DENY_CONTAINER");
        var centralHandlerLog = RequiredPath("HVO_ISSUE_211_CENTRAL_HANDLER_LOG");
        var password = (await File.ReadAllTextAsync(RequiredPath("HVO_ISSUE_211_OWNER_PASSWORD_FILE")).ConfigureAwait(false)).Trim();
        Directory.CreateDirectory(evidenceRoot);

        await WaitForHostAsync(baseUri).ConfigureAwait(false);
        var containerEnvironment = await ReadContainerExecutionEnvironmentAsync(container).ConfigureAwait(false);
        var serviceImages = await ReadServiceImageProvenanceAsync(container).ConfigureAwait(false);
        using var session = await LoginAsync(baseUri, password).ConfigureAwait(false);
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        if (!File.Exists(playwright.Chromium.ExecutablePath))
        {
            Assert.Inconclusive("Install the pinned Playwright Chromium with scripts/test:cameraagent-ui-106 --install-browser.");
        }
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }).ConfigureAwait(false);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseUri.ToString(),
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await BrowserLoginAsync(page, password).ConfigureAwait(false);
        await SetCaptureStateAsync(page, pause: true).ConfigureAwait(false);
        await ActivateMonoCaptureProfileAsync(session).ConfigureAwait(false);
        var runtimeIdentity = await ReadRuntimeIdentityAsync(
            session,
            ExpectedMonoLocalProfileSha256,
            ExpectedMonoScheduleSha256,
            ExpectedMonoDesiredGraphSha256,
            ExpectedMonoEffectiveGraphSha256).ConfigureAwait(false);
        var expectedDeploymentLocation = (await LoadW6ConfigurationAsync().ConfigureAwait(false)).DeploymentLocation
            ?? throw new InvalidDataException("The pinned W6 deployment location is missing.");

        var warmup = await CaptureExactWindowAsync(
            session,
            runtimeRoot,
            1,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMinutes(3),
            ExpectedMonoAgentId,
            ExpectedMonoRoles,
            4).ConfigureAwait(false);
        Assert.HasCount(1, warmup.Captures);
        using var sampler = new DockerResourceSampler(container, runtimeRoot, session);
        var measuredWindow = await CaptureExactWindowAsync(
            session,
            runtimeRoot,
            5,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMinutes(3),
            ExpectedMonoAgentId,
            ExpectedMonoRoles,
            4,
            sampler).ConfigureAwait(false);
        sampler.WriteCsv(Path.Combine(evidenceRoot, "process.csv"));
        sampler.WriteQueueCsv(Path.Combine(evidenceRoot, "durable-queues.csv"));
        sampler.WriteFilesystemCsv(Path.Combine(evidenceRoot, "filesystem.csv"));

        var measured = measuredWindow.Captures.ToArray();
        Assert.HasCount(5, measured);
        var readout = AssertMonoCaptureContracts(runtimeRoot, measured, expectedDeploymentLocation);
        var observedRawPayloadBytes = measuredWindow.Admissions.Sum(static admission => admission.PayloadLength);
        await AssertBrowserEvidenceAsync(page, measured[^1]).ConfigureAwait(false);
        await WaitForDurableConditionAsync(
            runtimeRoot,
            static snapshot => snapshot.PendingRawCaptures == 0 && snapshot.PendingLaneWork == 0 &&
                snapshot.PendingProcessingNodes == 0,
            "Mono8 final required backlog convergence",
            TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        var health = await WaitForHealthyAsync(session, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "health.json"), health).ConfigureAwait(false);
        _ = await WaitForMetricSurfaceAsync(
            session,
            prefix: null,
            AssertMonoRuntimeMetricSurface,
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var centralTrafficAttempts = CountDenyHits(await DockerLogsAsync(denyContainer).ConfigureAwait(false));
        Assert.AreEqual(0, centralTrafficAttempts);
        var centralHandlerAttempts = AssertCentralHandlerAttempts(centralHandlerLog);
        var resource = sampler.Summarize();
        AssertDurableQueueBounds(resource.MaximumDurableQueues);
        var retainedFilesystemBytes = DirectoryBytes(runtimeRoot);
        var peakFilesystemBytes = Math.Max(retainedFilesystemBytes, resource.PeakFilesystemBytes);
        var boundedTrialFootprintBytes = long.Parse(
            Required("HVO_ISSUE_211_BOUNDED_FOOTPRINT_BYTES"), CultureInfo.InvariantCulture);
        Assert.IsLessThanOrEqualTo(boundedTrialFootprintBytes, peakFilesystemBytes);
        var evidence = new
        {
            schemaVersion = "issue-211-mono8-evidence-v1",
            trial = Environment.GetEnvironmentVariable("HVO_ISSUE_211_TRIAL") ?? "local",
            revision = new
            {
                commit = Required("HVO_ISSUE_211_REVISION"),
                branch = Required("HVO_ISSUE_211_BRANCH"),
                dirtyStateSha256 = Required("HVO_ISSUE_211_DIRTY_SHA256"),
                cameraImage = Required("HVO_ISSUE_211_CAMERA_IMAGE")
            },
            workload = new
            {
                sensor = "ASI174MM",
                nativeRoi = new { x = 648, y = 368, width = 640, height = 480 },
                bin = new { x = 4, y = 4, algorithm = "DigitalAverageV1" },
                output = new { width = 160, height = 120, strideBytes = 160, pixelFormat = "Mono8", byteLength = 19_200 },
                exposureSeconds = 1,
                cadenceSeconds = 2,
                warmupCount = warmup.Captures.Count,
                measuredCount = measured.Length,
                observedRawPayloadBytes,
                rigSha256 = ExpectedMonoRigSha256,
                processingSha256 = ExpectedMonoProcessingSha256,
                localProfileSha256 = ExpectedMonoLocalProfileSha256,
                scheduleSha256 = ExpectedMonoScheduleSha256,
                desiredGraphSha256 = ExpectedMonoDesiredGraphSha256,
                effectiveGraphSha256 = ExpectedMonoEffectiveGraphSha256
            },
            runtimeIdentity,
            method = new
            {
                worktreeFingerprint = Required("HVO_ISSUE_211_WORKTREE_FINGERPRINT"),
                samplingIntervalSeconds = 1,
                boundedTrialFootprintBytes,
                requiredFreeBytes = long.Parse(
                    Required("HVO_ISSUE_211_REQUIRED_FREE_BYTES"), CultureInfo.InvariantCulture)
            },
            warmupAdmissions = warmup.Admissions,
            measuredRawAdmissions = measuredWindow.Admissions,
            captures = measured.Select(static capture => new { capture.CaptureId, capture.CaptureSequence }).ToArray(),
            performance = new
            {
                executionEnvironment = new
                {
                    kind = "development-host-container",
                    container = containerEnvironment,
                    serviceImages,
                    harness = new
                    {
                        architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                        operatingSystem = RuntimeInformation.OSDescription,
                        processorCount = Environment.ProcessorCount,
                        availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                        framework = RuntimeInformation.FrameworkDescription
                    }
                },
                elapsedMilliseconds = measuredWindow.Elapsed.TotalMilliseconds,
                observedRawPayloadBytes,
                cpuSeconds = resource.EstimatedCpuSeconds,
                resource.PeakRssBytes,
                resource.PeakContainerMemoryUsageBytes,
                resource.ProcessReadBytes,
                resource.ProcessWriteBytes,
                containerBlockReadBytes = resource.BlockReadBytes,
                containerBlockWriteBytes = resource.BlockWriteBytes,
                sqliteFootprintGrowthBytes = resource.SqliteFootprintGrowthBytes,
                resource.MaximumDurableQueues,
                declaredDurableQueueBounds = DurableQueueBounds,
                peakFilesystemBytes,
                retainedFilesystemBytes,
                resource.SampleCount,
                resource.SamplingFailureCount,
                resource.SamplingFailures,
                resource.MaximumSuccessfulSampleGapSeconds,
                resource.FilesystemSampleCount,
                resource.FilesystemSamplingFailureCount,
                resource.FilesystemSamplingFailures,
                resource.MaximumFilesystemSampleGapSeconds,
                resource.QueueSampleCount,
                resource.QueueSamplingFailureCount,
                resource.QueueSamplingFailures,
                resource.MaximumQueueSampleGapSeconds
            },
            readout,
            centralTrafficAttempts,
            centralHandlerAttempts,
            result = new { passed = true }
        };
        var evidencePath = Path.Combine(evidenceRoot, "issue-211-mono8.json");
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, EvidenceJson)).ConfigureAwait(false);
        TestContext.WriteLine($"Issue #211 Mono8 evidence: {evidencePath}");
    }

    private static async Task WaitForHostAsync(Uri baseUri)
    {
        using var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative)).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        Assert.Fail("The W6 CameraAgent did not expose its login endpoint.");
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned client owns its handler.")]
    private static async Task<HttpClient> LoginAsync(Uri baseUri, string password)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer(),
            CheckCertificateRevocationList = true
        };
        var client = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromMinutes(3) };
        using var login = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative)).ConfigureAwait(false);
        login.EnsureSuccessStatusCode();
        var html = await login.Content.ReadAsStringAsync().ConfigureAwait(false);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.IsTrue(token.Success);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
            ["Input.Email"] = "standalone-owner@cameraagent.test",
            ["Input.Password"] = password,
            ["Input.RememberMe"] = "false",
            ["_handler"] = "login"
        });
        using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), form).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task BrowserLoginAsync(IPage page, string password)
    {
        await page.GotoAsync("/Account/Login").ConfigureAwait(false);
        await page.GetByLabel("Email").FillAsync("standalone-owner@cameraagent.test").ConfigureAwait(false);
        await page.GetByLabel("Password").FillAsync(password).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(
            url => !url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { WaitUntil = WaitUntilState.Commit }).ConfigureAwait(false);
    }

    private static async Task SetCaptureStateAsync(IPage page, bool pause)
    {
        await page.GotoAsync("/operations").ConfigureAwait(false);
        var action = page.Locator("#capture-action");
        await action.WaitForAsync().ConfigureAwait(false);
        var expected = pause ? "Review pause" : "Review resume";
        if (!string.Equals((await action.InnerTextAsync().ConfigureAwait(false)).Trim(), expected, StringComparison.Ordinal))
        {
            return;
        }
        var confirmation = page.Locator("dialog.confirmation");
        await OpenDialogAsync(action, confirmation).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = pause ? "Confirm pause capture" : "Confirm resume capture" })
            .ClickAsync().ConfigureAwait(false);
        await page.Locator(".receipt[role='status']").WaitForAsync().ConfigureAwait(false);
    }

    private static async Task<ExactCaptureWindow> CaptureExactWindowAsync(
        HttpClient client,
        string runtimeRoot,
        int count,
        TimeSpan cadence,
        TimeSpan timeout,
        string agentId = ExpectedAgentId,
        IReadOnlyList<FrameArtifactRole>? expectedRoles = null,
        int expectedNodeCount = 10,
        DockerResourceSampler? sampler = null)
    {
        var token = await GetAntiforgeryTokenAsync(client).ConfigureAwait(false);
        await SetCaptureStateAsync(client, pause: true, token).ConfigureAwait(false);
        await WaitForDurableConditionAsync(
            runtimeRoot,
            static snapshot => snapshot.PendingRawCaptures == 0 && snapshot.PendingLaneWork == 0 &&
                snapshot.PendingProcessingNodes == 0,
            $"{agentId} pre-window drain",
            timeout).ConfigureAwait(false);
        if (string.Equals(agentId, ExpectedAgentId, StringComparison.Ordinal))
        {
            await WaitForTransientDrainAsync(runtimeRoot, $"{agentId} pre-window transient drain", timeout)
                .ConfigureAwait(false);
        }
        var minimumSequence = ReadMaximumRawSequenceFromJournal(runtimeRoot, agentId);

        if (sampler is not null)
        {
            await sampler.BeginAsync().ConfigureAwait(false);
        }
        var elapsed = Stopwatch.StartNew();
        await SetCaptureStateAsync(client, pause: false, token).ConfigureAwait(false);
        var admissions = await WaitForExactRawAdmissionsAsync(
            runtimeRoot, agentId, minimumSequence, count, timeout).ConfigureAwait(false);
        await SetCaptureStateAsync(client, pause: true, token).ConfigureAwait(false);

        var guardUntil = admissions[^1].ExposureStartedUtc + cadence + TimeSpan.FromMilliseconds(250);
        if (guardUntil > DateTimeOffset.UtcNow)
        {
            await Task.Delay(guardUntil - DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
        AssertNoRawSequenceAbove(runtimeRoot, agentId, admissions[^1].CaptureSequence);

        var captures = (await WaitForCompleteCapturesAsync(
            client,
            count,
            minimumSequence,
            timeout,
            expectedRoles,
            expectedNodeCount).ConfigureAwait(false)).ToArray();
        CollectionAssert.AreEqual(
            admissions.Select(static admission => admission.CaptureSequence).ToArray(),
            captures.Select(static capture => capture.CaptureSequence).ToArray());
        await WaitForDurableConditionAsync(
            runtimeRoot,
            static snapshot => snapshot.PendingRawCaptures == 0 && snapshot.PendingLaneWork == 0 &&
                snapshot.PendingProcessingNodes == 0,
            $"{agentId} exact-window completion",
            timeout).ConfigureAwait(false);
        if (string.Equals(agentId, ExpectedAgentId, StringComparison.Ordinal))
        {
            await WaitForTransientDrainAsync(runtimeRoot, $"{agentId} exact-window transient completion", timeout)
                .ConfigureAwait(false);
        }
        AssertNoRawSequenceAbove(runtimeRoot, agentId, admissions[^1].CaptureSequence);
        elapsed.Stop();
        if (sampler is not null)
        {
            await sampler.CompleteAsync().ConfigureAwait(false);
        }
        return new ExactCaptureWindow(admissions, captures, elapsed.Elapsed);
    }

    private static async Task SetCaptureStateAsync(HttpClient client, bool pause, string token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/api/v1/operations/capture/{(pause ? "pause" : "resume")}", UriKind.Relative));
        request.Headers.Add("Idempotency-Key", $"issue-211-exact-window-{Guid.NewGuid():N}");
        request.Headers.Add("RequestVerificationToken", token);
        request.Content = JsonContent.Create(new { reason = "issue-211 exact capture window" });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var receipt = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false))!.AsObject();
        Assert.AreEqual(
            (int)(pause ? CaptureAdmissionState.Paused : CaptureAdmissionState.Running),
            receipt["state"]!.GetValue<int>());
    }

    private static async Task<IReadOnlyList<RawAdmissionEvidence>> WaitForExactRawAdmissionsAsync(
        string runtimeRoot,
        string agentId,
        long minimumSequence,
        int count,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var admissions = ReadRawAdmissions(runtimeRoot, agentId, minimumSequence);
            Assert.IsLessThanOrEqualTo(count, admissions.Count,
                $"A raw sequence above the requested {count}-capture window was admitted.");
            if (admissions.Count == count)
            {
                return admissions;
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
        Assert.Fail($"Timed out waiting for exactly {count} raw admissions after sequence {minimumSequence}.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static List<RawAdmissionEvidence> ReadRawAdmissions(
        string runtimeRoot,
        string agentId,
        long minimumSequence)
    {
        using var connection = OpenJournal(runtimeRoot);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT capture_id, capture_sequence, payload_length, exposure_started_unix_ms
            FROM raw_captures
            WHERE agent_id = $agent AND capture_sequence > $minimum
            ORDER BY capture_sequence;
            """;
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$minimum", minimumSequence);
        var admissions = new List<RawAdmissionEvidence>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            admissions.Add(new RawAdmissionEvidence(
                Guid.ParseExact(reader.GetString(0), "N"),
                reader.GetInt64(1),
                reader.GetInt64(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
        }
        return admissions;
    }

    private static long ReadMaximumRawSequenceFromJournal(string runtimeRoot, string agentId)
    {
        using var connection = OpenJournal(runtimeRoot);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(capture_sequence), 0) FROM raw_captures WHERE agent_id = $agent;";
        command.Parameters.AddWithValue("$agent", agentId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void AssertNoRawSequenceAbove(string runtimeRoot, string agentId, long maximumSequence)
    {
        var unexpected = ReadRawAdmissions(runtimeRoot, agentId, maximumSequence);
        Assert.IsEmpty(unexpected,
            $"Raw sequence(s) above the exact window were admitted: {string.Join(", ", unexpected.Select(static item => item.CaptureSequence))}.");
    }

    private static async Task AcquireCalibrationAsync(IPage page)
    {
        await page.GotoAsync("/calibration").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Calibration library", Level = 1 }).WaitForAsync().ConfigureAwait(false);
        var activeHeading = page.Locator(".calibration-card--active h2");
        if (!string.Equals(
                (await activeHeading.InnerTextAsync().ConfigureAwait(false)).Trim(),
                "No active bundle",
                StringComparison.Ordinal))
        {
            return;
        }
        var confirmation = page.Locator(".confirmation-panel[role='alertdialog']");
        await OpenDialogAsync(
            page.GetByRole(AriaRole.Button, new() { Name = "Review acquisition" }),
            confirmation).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm", Exact = true }).ClickAsync().ConfigureAwait(false);
        _ = await WaitForCalibrationActivateAsync(page).ConfigureAwait(false);
    }

    private static async Task ActivateCalibrationAsync(IPage page)
    {
        await page.GotoAsync("/calibration").ConfigureAwait(false);
        var activeHeading = page.Locator(".calibration-card--active h2");
        if (!string.Equals(
                (await activeHeading.InnerTextAsync().ConfigureAwait(false)).Trim(),
                "No active bundle",
                StringComparison.Ordinal))
        {
            return;
        }
        var confirmation = page.Locator(".confirmation-panel[role='alertdialog']");
        var activate = await WaitForCalibrationActivateAsync(page).ConfigureAwait(false);
        await OpenDialogAsync(activate, confirmation).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            "() => document.querySelector('.calibration-card--active h2')?.textContent?.trim() !== 'No active bundle'",
            null,
            new PageWaitForFunctionOptions { Timeout = 120_000 }).ConfigureAwait(false);
    }

    private static async Task<ILocator> WaitForCalibrationActivateAsync(IPage page)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await page.GotoAsync("/calibration").ConfigureAwait(false);
            var activate = page.GetByRole(AriaRole.Button, new() { Name = "Review activate" }).First;
            if (await activate.CountAsync().ConfigureAwait(false) > 0 &&
                await activate.IsVisibleAsync().ConfigureAwait(false))
            {
                return activate;
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
        Assert.Fail("The published W6 calibration bundle did not become activatable.");
        throw new InvalidOperationException();
    }

    private static async Task<ScheduleRoundTripEvidence> AssertScheduleUiRoundTripAsync(
        IPage page,
        string runtimeRoot)
    {
        await page.GotoAsync("/schedule").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Schedule control", Level = 1 })
            .WaitForAsync().ConfigureAwait(false);
        var activeHash = page.Locator(".schedule-card:has-text('Active immutable profile') code");
        var originalHash = (await activeHash.InnerTextAsync().ConfigureAwait(false)).Trim();
        Assert.AreEqual(ExpectedLocalProfileSha256[..12], originalHash);
        await ValidateSchedulePreviewAsync(page).ConfigureAwait(false);
        Assert.IsGreaterThanOrEqualTo(2, await page.Locator(".preview-card time").CountAsync().ConfigureAwait(false));
        var confirmation = page.Locator(".confirmation-panel[role='alertdialog']");
        await page.GetByRole(AriaRole.Button, new() { Name = "Review rollback" }).First.ClickAsync().ConfigureAwait(false);
        await confirmation.WaitForAsync(new() { State = WaitForSelectorState.Visible }).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm rollback" }).ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            """
            expected => [...document.querySelectorAll('.schedule-card')]
                .find(card => card.textContent.includes('Active immutable profile'))
                ?.querySelector('code')?.textContent.trim() !== expected
            """,
            originalHash).ConfigureAwait(false);

        await page.GetByRole(AriaRole.Button, new() { Name = "Review apply" }).First.ClickAsync().ConfigureAwait(false);
        await confirmation.WaitForAsync(new() { State = WaitForSelectorState.Visible }).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm apply" }).ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            """
            expected => [...document.querySelectorAll('.schedule-card')]
                .find(card => card.textContent.includes('Active immutable profile'))
                ?.querySelector('code')?.textContent.trim() === expected
            """,
            originalHash).ConfigureAwait(false);
        await page.GetByText("Revision applied at the capture boundary.", new() { Exact = true })
            .WaitForAsync().ConfigureAwait(false);
        var restoredHash = (await activeHash.InnerTextAsync().ConfigureAwait(false)).Trim();
        using var connection = OpenJournal(runtimeRoot);
        using var rollbackCommand = connection.CreateCommand();
        rollbackCommand.CommandText = "SELECT COUNT(*) FROM capture_schedule_commands WHERE command_kind = 'rollback';";
        var rollbackCommands = Convert.ToInt64(
            await rollbackCommand.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
        using var activationCommand = connection.CreateCommand();
        activationCommand.CommandText = "SELECT COUNT(*) FROM capture_schedule_commands WHERE command_kind = 'activate';";
        var activationCommands = Convert.ToInt64(
            await activationCommand.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
        Assert.IsGreaterThan(0, rollbackCommands);
        Assert.IsGreaterThan(0, activationCommands);
        return new ScheduleRoundTripEvidence(originalHash, restoredHash, rollbackCommands, activationCommands);
    }

    private static async Task<CalibrationUiEvidence> AssertCalibrationUiRollbackAsync(
        IPage page,
        string originalBundleId)
    {
        await page.GotoAsync("/calibration").ConfigureAwait(false);
        var activeHeading = page.Locator(".calibration-card--active h2");
        Assert.AreEqual(originalBundleId, (await activeHeading.InnerTextAsync().ConfigureAwait(false)).Trim());
        await page.GetByRole(AriaRole.Button, new() { Name = "Review acquisition" }).ClickAsync().ConfigureAwait(false);
        var confirmation = page.Locator(".confirmation-panel[role='alertdialog']");
        await confirmation.WaitForAsync(new() { State = WaitForSelectorState.Visible }).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm", Exact = true }).ClickAsync().ConfigureAwait(false);
        var activate = await WaitForCalibrationActivateAsync(page).ConfigureAwait(false);
        await activate.ClickAsync().ConfigureAwait(false);
        await confirmation.WaitForAsync(new() { State = WaitForSelectorState.Visible }).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            "prior => document.querySelector('.calibration-card--active h2')?.textContent?.trim() !== prior",
            originalBundleId,
            new() { Timeout = 120_000 }).ConfigureAwait(false);
        var acquiredBundleId = (await activeHeading.InnerTextAsync().ConfigureAwait(false)).Trim();
        Assert.AreNotEqual(originalBundleId, acquiredBundleId);

        await page.GetByRole(AriaRole.Button, new() { Name = "Review rollback" }).ClickAsync().ConfigureAwait(false);
        await confirmation.WaitForAsync(new() { State = WaitForSelectorState.Visible }).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            "expected => document.querySelector('.calibration-card--active h2')?.textContent?.trim() === expected",
            originalBundleId,
            new() { Timeout = 120_000 }).ConfigureAwait(false);
        return new CalibrationUiEvidence(originalBundleId, acquiredBundleId, originalBundleId);
    }

    private static async Task ValidateSchedulePreviewAsync(IPage page)
    {
        var success = page.GetByText(
            "Schedule and desired graph previews are valid. No durable state changed.",
            new() { Exact = true });
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Validate and preview" })
                .ClickAsync().ConfigureAwait(false);
            try
            {
                await success.WaitForAsync(new() { Timeout = 5_000 }).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                if (attempt == 9)
                {
                    var banners = await page.Locator(".schedule-banner").AllTextContentsAsync().ConfigureAwait(false);
                    Assert.Fail($"Schedule preview did not report success. Banners: {string.Join(" | ", banners)}");
                }
                await page.GotoAsync("/schedule").ConfigureAwait(false);
                await page.GetByRole(AriaRole.Heading, new() { Name = "Schedule control", Level = 1 })
                    .WaitForAsync().ConfigureAwait(false);
            }
        }
    }

    private static Task ActivateBootstrapCaptureProfileAsync(HttpClient client)
        => ActivateCaptureProfileAsync(client, profile => EditW6Profile(profile, "00:00:10.001", enableCanonicalTail: false), expectedSha256: null);

    private static Task ActivateCanonicalCaptureProfileAsync(HttpClient client)
        => ActivateCaptureProfileAsync(client, profile => EditW6Profile(profile, "00:00:10", enableCanonicalTail: true), ExpectedLocalProfileSha256);

    private static Task ActivateMonoCaptureProfileAsync(HttpClient client)
        => ActivateCaptureProfileAsync(
            client,
            static profile => _ = profile["schedule"]!.AsObject().Remove("blackouts"),
            ExpectedMonoLocalProfileSha256);

    private static Task ActivateOptionalQualityDisabledProfileAsync(HttpClient client)
        => ActivateCaptureProfileAsync(
            client,
            static profile =>
            {
                var quality = profile["processingSteps"]!.AsArray()
                    .Single(step => step?["id"]?.GetValue<string>() == "quality")!.AsObject();
                quality["enabled"] = false;
                foreach (var id in QualityDependentNodeIds)
                {
                    var step = profile["processingSteps"]!.AsArray()
                        .Single(item => item?["id"]?.GetValue<string>() == id)!.AsObject();
                    var dependencies = step["dependsOn"]!.AsArray();
                    for (var index = dependencies.Count - 1; index >= 0; index--)
                    {
                        if (dependencies[index]?.GetValue<string>() == "quality")
                        {
                            dependencies.RemoveAt(index);
                        }
                    }
                }
            },
            expectedSha256: null);

    private static void EditW6Profile(JsonObject profile, string captureInterval, bool enableCanonicalTail)
    {
        var scheduleProfile = profile["schedule"]!.AsObject();
        scheduleProfile["blackouts"] = new JsonArray();
        scheduleProfile["setpointProfiles"]!.AsArray()[0]!["captureInterval"] = captureInterval;
        foreach (var step in profile["processingSteps"]!.AsArray())
        {
            if (step?["id"]?.GetValue<string>() is "cloud" or "weather-overlay" or "storage" or "telemetry")
            {
                if (enableCanonicalTail)
                {
                    _ = step.AsObject().Remove("enabled");
                }
                else
                {
                    step["enabled"] = false;
                }
            }
        }
        if (enableCanonicalTail)
        {
            var quality = profile["processingSteps"]!.AsArray()
                .Single(step => step?["id"]?.GetValue<string>() == "quality")!.AsObject();
            _ = quality.Remove("enabled");
            foreach (var id in QualityDependentNodeIds)
            {
                var step = profile["processingSteps"]!.AsArray()
                    .Single(item => item?["id"]?.GetValue<string>() == id)!.AsObject();
                var dependencies = step["dependsOn"]!.AsArray();
                if (!dependencies.Any(item => item?.GetValue<string>() == "quality"))
                {
                    var cloudIndex = dependencies.IndexOf(dependencies.Single(item => item?.GetValue<string>() == "cloud"));
                    dependencies.Insert(cloudIndex, "quality");
                }
            }
        }
    }

    private static async Task ActivateCaptureProfileAsync(
        HttpClient client,
        Action<JsonObject> editProfile,
        string? expectedSha256)
    {
        using var scheduleResponse = await client.GetAsync(
            new Uri("/api/v1/operations/schedule/", UriKind.Relative)).ConfigureAwait(false);
        scheduleResponse.EnsureSuccessStatusCode();
        var schedule = JsonNode.Parse(await scheduleResponse.Content.ReadAsStringAsync().ConfigureAwait(false))!.AsObject();
        var active = schedule["activeRevision"]!.AsObject();
        var profile = active["profile"]!.AsObject();
        editProfile(profile);

        var token = await GetAntiforgeryTokenAsync(client).ConfigureAwait(false);
        using var stageRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/v1/operations/schedule/stage", UriKind.Relative));
        stageRequest.Headers.Add("Idempotency-Key", $"issue-211-canonical-stage-{Guid.NewGuid():N}");
        stageRequest.Headers.Add("RequestVerificationToken", token);
        stageRequest.Content = JsonContent.Create(new
        {
            profile,
            basisRevisionId = active["revisionId"]!.GetValue<string>(),
            expectedVersion = schedule["stateVersion"]!.GetValue<long>(),
            reason = "issue-211 canonical measured profile"
        });
        using var stageResponse = await client.SendAsync(stageRequest).ConfigureAwait(false);
        stageResponse.EnsureSuccessStatusCode();
        var staged = JsonNode.Parse(await stageResponse.Content.ReadAsStringAsync().ConfigureAwait(false))!.AsObject();
        var pending = staged["pendingRevision"]!.AsObject();
        var pendingSha256 = pending["profileSha256"]!.GetValue<string>();
        if (expectedSha256 is null)
        {
            Assert.AreNotEqual(active["profileSha256"]!.GetValue<string>(), pendingSha256);
        }
        else
        {
            Assert.AreEqual(expectedSha256, pendingSha256);
        }
        var activeRevisionNumber = active["revisionNumber"]!.GetValue<long>();
        var pendingRevisionNumber = pending["revisionNumber"]!.GetValue<long>();
        Assert.AreNotEqual(activeRevisionNumber, pendingRevisionNumber);
        var action = pendingRevisionNumber < activeRevisionNumber ? "rollback" : "activate";

        using var activateRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/api/v1/operations/schedule/{action}", UriKind.Relative));
        activateRequest.Headers.Add("Idempotency-Key", $"issue-211-canonical-{action}-{Guid.NewGuid():N}");
        activateRequest.Headers.Add("RequestVerificationToken", token);
        activateRequest.Content = JsonContent.Create(new
        {
            revisionId = pending["revisionId"]!.GetValue<string>(),
            expectedVersion = staged["version"]!.GetValue<long>(),
            reason = "issue-211 canonical measured profile"
        });
        using var activateResponse = await client.SendAsync(activateRequest).ConfigureAwait(false);
        activateResponse.EnsureSuccessStatusCode();
        var activated = JsonNode.Parse(await activateResponse.Content.ReadAsStringAsync().ConfigureAwait(false))!.AsObject();
        Assert.AreEqual(
            pendingSha256,
            activated["activeRevision"]!["profileSha256"]!.GetValue<string>());
    }

    private static async Task<RuntimeIdentityEvidence> ReadRuntimeIdentityAsync(
        HttpClient client,
        string expectedProfileSha256,
        string expectedScheduleSha256,
        string expectedDesiredGraphSha256,
        string expectedEffectiveGraphSha256)
    {
        using var scheduleResponse = await client.GetAsync(
            new Uri("/api/v1/operations/schedule/", UriKind.Relative)).ConfigureAwait(false);
        scheduleResponse.EnsureSuccessStatusCode();
        var schedule = JsonNode.Parse(await scheduleResponse.Content.ReadAsStringAsync().ConfigureAwait(false))!.AsObject();
        var activeSchedule = schedule["activeRevision"]!.AsObject();

        using var pipelineResponse = await client.GetAsync(
            new Uri("/api/v1/operations/pipeline/", UriKind.Relative)).ConfigureAwait(false);
        pipelineResponse.EnsureSuccessStatusCode();
        var pipeline = JsonNode.Parse(await pipelineResponse.Content.ReadAsStringAsync().ConfigureAwait(false))!.AsObject();
        var activePipeline = pipeline["active"]!.AsObject();
        var plan = activePipeline["plan"]!.AsObject();
        var evidence = new RuntimeIdentityEvidence(
            activeSchedule["revisionId"]!.GetValue<string>(),
            activeSchedule["revisionNumber"]!.GetValue<long>(),
            activeSchedule["profileSha256"]!.GetValue<string>(),
            activeSchedule["scheduleSha256"]!.GetValue<string>(),
            activePipeline["revisionId"]!.GetValue<string>(),
            activePipeline["profileSha256"]!.GetValue<string>(),
            plan["desiredSha256"]!.GetValue<string>(),
            plan["effectiveSha256"]!.GetValue<string>());
        Assert.AreEqual(expectedProfileSha256, evidence.ActiveProfileSha256);
        Assert.AreEqual(expectedScheduleSha256, evidence.ActiveScheduleSha256);
        Assert.AreEqual(evidence.ActiveRevisionId, evidence.PipelineRevisionId);
        Assert.AreEqual(expectedProfileSha256, evidence.PipelineProfileSha256);
        Assert.AreEqual(expectedDesiredGraphSha256, evidence.DesiredGraphSha256);
        Assert.AreEqual(expectedEffectiveGraphSha256, evidence.EffectiveGraphSha256);
        return evidence;
    }

    private static async Task<OptionalQualityEvidence> AssertOptionalQualityDisableAsync(
        HttpClient client,
        IPage page,
        string runtimeRoot)
    {
        await ActivateOptionalQualityDisabledProfileAsync(client).ConfigureAwait(false);
        var minimumSequence = ReadMaximumRawSequence(runtimeRoot);
        await SetCaptureStateAsync(page, pause: false).ConfigureAwait(false);
        var captures = await WaitForCompleteCapturesAsync(
            client,
            2,
            minimumSequence,
            TimeSpan.FromMinutes(3),
            ExpectedRoles,
            expectedNodeCount: 9).ConfigureAwait(false);
        await SetCaptureStateAsync(page, pause: true).ConfigureAwait(false);
        Assert.IsTrue(captures.All(static capture => capture.ProcessingNodes.All(node => node.NodeId != "quality")));
        Assert.IsTrue(captures.All(static capture => capture.Artifacts.Any(
            artifact => artifact.Variant == "cloud-assessment-v1")));
        await ActivateCanonicalCaptureProfileAsync(client).ConfigureAwait(false);
        minimumSequence = ReadMaximumRawSequence(runtimeRoot);
        await SetCaptureStateAsync(page, pause: false).ConfigureAwait(false);
        var restored = (await WaitForCompleteCapturesAsync(
            client,
            1,
            minimumSequence,
            TimeSpan.FromMinutes(3)).ConfigureAwait(false)).Single();
        await SetCaptureStateAsync(page, pause: true).ConfigureAwait(false);
        Assert.HasCount(10, restored.ProcessingNodes);
        Assert.IsTrue(restored.ProcessingNodes.All(static node => node.Status == "Completed"));
        Assert.IsTrue(restored.ProcessingNodes.Any(static node => node.NodeId == "quality"));
        Assert.IsTrue(restored.ProcessingNodes.Any(static node => node.NodeId == "storage" && node.Required));
        Assert.IsTrue(restored.ProcessingNodes.Any(static node => node.NodeId == "telemetry" && node.Required));
        var quality = restored.Artifacts.Single(static artifact => artifact.Variant == "image-quality-v1");
        var restoredIdentity = ReadRestoredCaptureIdentity(runtimeRoot, restored, quality.ArtifactId);
        return new OptionalQualityEvidence(
            captures.Select(static capture => new CaptureIdentityEvidence(
                capture.CaptureId,
                capture.CaptureSequence)).ToArray(),
            restoredIdentity);
    }

    private static RestoredCaptureIdentity ReadRestoredCaptureIdentity(
        string runtimeRoot,
        CameraAgentGalleryCapture capture,
        Guid qualityArtifactId)
    {
        var raw = ReadManifests(runtimeRoot).Single(item =>
            item.Manifest.Descriptor.Capture.CaptureId == capture.CaptureId &&
            item.Manifest.Descriptor.Artifact.Role == FrameArtifactRole.Raw).Manifest;
        var localProfileSha256 = raw.Descriptor.CycleEvidence?.ScheduleAdmission?.LocalProfileSha256;
        Assert.AreEqual(ExpectedLocalProfileSha256, localProfileSha256);
        using var connection = OpenJournal(runtimeRoot);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, plan_sha256, attempt
            FROM processing_nodes
            WHERE capture_id = $capture AND status = 'Completed'
            ORDER BY node_id;
            """;
        command.Parameters.AddWithValue("$capture", capture.CaptureId.ToString("N"));
        var nodes = new List<RestoredNodeIdentity>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            nodes.Add(new RestoredNodeIdentity(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }
        Assert.HasCount(10, nodes);
        return new RestoredCaptureIdentity(
            capture.CaptureId,
            capture.CaptureSequence,
            qualityArtifactId,
            localProfileSha256!,
            raw.Descriptor.Profiles.Processing.Sha256,
            nodes);
    }

    private static async Task<RestartEvidence> AssertDurableRestartAsync(
        Uri baseUri,
        string container,
        string runtimeRoot,
        string clearReferencePath,
        IPage page)
    {
        var clearReference = await File.ReadAllBytesAsync(clearReferencePath).ConfigureAwait(false);
        var minimumSequence = ReadMaximumRawSequence(runtimeRoot);
        File.Delete(clearReferencePath);
        var killed = false;
        try
        {
            await SetCaptureStateAsync(page, pause: false).ConfigureAwait(false);
            await WaitForDurableConditionAsync(
                runtimeRoot,
                snapshot => snapshot.RawCaptureCount > minimumSequence && snapshot.PendingLaneWork > 0,
                "required processing backlog before restart",
                TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            await SetCaptureStateAsync(page, pause: true).ConfigureAwait(false);
            var before = ReadDurableSnapshot(runtimeRoot);
            var interruptedChecksums = ReadManifests(runtimeRoot)
                .Where(item => item.Manifest.Descriptor.Artifact.Role == FrameArtifactRole.Raw &&
                    item.Manifest.Descriptor.Capture.CaptureSequence > minimumSequence)
                .ToDictionary(
                    item => item.Manifest.Descriptor.Artifact.ArtifactId,
                    item => item.Manifest.Descriptor.Artifact.ChecksumSha256);
            Assert.IsNotEmpty(interruptedChecksums);

            await ProcessAsync("docker", ["kill", "--signal", "KILL", container]).ConfigureAwait(false);
            killed = true;
            await File.WriteAllBytesAsync(clearReferencePath, clearReference).ConfigureAwait(false);
            var restart = Stopwatch.StartNew();
            await ProcessAsync("docker", ["start", container]).ConfigureAwait(false);
            await WaitForHostAsync(baseUri).ConfigureAwait(false);
            await SetCaptureStateAsync(page, pause: true).ConfigureAwait(false);
            await WaitForDurableConditionAsync(
                runtimeRoot,
                static snapshot => snapshot.PendingRawCaptures == 0 &&
                    snapshot.PendingLaneWork == 0 &&
                    snapshot.PendingProcessingNodes == 0,
                "required work to converge after restart",
                TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            restart.Stop();
            var after = ReadDurableSnapshot(runtimeRoot);
            Assert.AreEqual(before.RawCaptureCount, after.RawCaptureCount);
            Assert.AreEqual(0L, after.DuplicateLogicalOutputs);
            var recovered = ReadManifests(runtimeRoot)
                .Where(item => interruptedChecksums.ContainsKey(item.Manifest.Descriptor.Artifact.ArtifactId))
                .ToDictionary(
                    item => item.Manifest.Descriptor.Artifact.ArtifactId,
                    item => item.Manifest.Descriptor.Artifact.ChecksumSha256);
            CollectionAssert.AreEquivalent(interruptedChecksums, recovered);
            Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(3), restart.Elapsed);
            await Phase14ScenarioEvidence.RecordAsync(
                "cameraagent-host-failure",
                "durable-restart",
                "AssertDurableRestartAsync",
                [
                    "interrupted raw work remained durable across process termination",
                    "recovered artifacts retained their checksums",
                    "recovery produced no duplicate logical outputs",
                    "restart recovery completed within three minutes"
                ]).ConfigureAwait(false);
            return new RestartEvidence(before, after, restart.Elapsed.TotalMilliseconds, interruptedChecksums.Count);
        }
        finally
        {
            if (!File.Exists(clearReferencePath))
            {
                await File.WriteAllBytesAsync(clearReferencePath, clearReference).ConfigureAwait(false);
            }
            if (killed)
            {
                var running = (await ProcessAsync(
                    "docker", ["inspect", "--format", "{{.State.Running}}", container]).ConfigureAwait(false)).Trim();
                if (!string.Equals(running, "true", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessAsync("docker", ["start", container]).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<GracefulShutdownEvidence> AssertBoundedShutdownAsync(
        Uri baseUri,
        string container,
        string runtimeRoot,
        IPage page)
    {
        const string operationId = "bounded-shutdown";
        const int configuredDrainSeconds = 30;
        var minimumSequence = ReadMaximumRawSequence(runtimeRoot);
        ArmFault(
            runtimeRoot,
            operationId,
            "processing.BeforeNodeExecution",
            "block",
            "calibration",
            blockTimeoutSeconds: 120);
        await SetCaptureStateAsync(page, pause: false).ConfigureAwait(false);
        await WaitForFaultHitAsync(runtimeRoot, operationId, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        await SetCaptureStateAsync(page, pause: true).ConfigureAwait(false);
        await WaitForDurableConditionAsync(
            runtimeRoot,
            snapshot => snapshot.RawCaptureCount > minimumSequence && snapshot.PendingLaneWork > 0,
            "discoverable bounded-shutdown work",
            TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        var before = ReadDurableSnapshot(runtimeRoot);
        var interrupted = ReadManifests(runtimeRoot)
            .Where(item => item.Manifest.Descriptor.Artifact.Role == FrameArtifactRole.Raw &&
                item.Manifest.Descriptor.Capture.CaptureSequence > minimumSequence)
            .ToDictionary(
                item => item.Manifest.Descriptor.Artifact.ArtifactId,
                item => item.Manifest.Descriptor.Artifact.ChecksumSha256);
        Assert.IsNotEmpty(interrupted);

        var shutdown = Stopwatch.StartNew();
        await ProcessAsync(
            "docker",
            ["stop", "--time", configuredDrainSeconds.ToString(CultureInfo.InvariantCulture), container])
            .ConfigureAwait(false);
        shutdown.Stop();
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(configuredDrainSeconds + 5), shutdown.Elapsed);
        var stopped = ReadDurableSnapshot(runtimeRoot);
        Assert.IsGreaterThan(0, stopped.PendingLaneWork + stopped.PendingProcessingNodes);
        File.WriteAllBytes(Path.Combine(runtimeRoot, "acceptance-faults", $"release-{operationId}"), []);

        var recovery = Stopwatch.StartNew();
        await ProcessAsync("docker", ["start", container]).ConfigureAwait(false);
        await WaitForHostAsync(baseUri).ConfigureAwait(false);
        await SetCaptureStateAsync(page, pause: true).ConfigureAwait(false);
        await WaitForDurableConditionAsync(
            runtimeRoot,
            static snapshot => snapshot.PendingRawCaptures == 0 && snapshot.PendingLaneWork == 0 &&
                snapshot.PendingProcessingNodes == 0,
            "bounded-shutdown restart convergence",
            TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        recovery.Stop();
        var after = ReadDurableSnapshot(runtimeRoot);
        Assert.AreEqual(before.RawCaptureCount, after.RawCaptureCount);
        Assert.AreEqual(0, after.DuplicateLogicalOutputs);
        var recovered = ReadManifests(runtimeRoot)
            .Where(item => interrupted.ContainsKey(item.Manifest.Descriptor.Artifact.ArtifactId))
            .ToDictionary(
                item => item.Manifest.Descriptor.Artifact.ArtifactId,
                item => item.Manifest.Descriptor.Artifact.ChecksumSha256);
        CollectionAssert.AreEquivalent(interrupted, recovered);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(3), recovery.Elapsed);
        await Phase14ScenarioEvidence.RecordAsync(
            "bounded-shutdown",
            operationId,
            "AssertBoundedShutdownAsync",
            [
                "container shutdown completed within the configured drain bound plus five seconds",
                "unfinished durable work remained discoverable after shutdown",
                "interrupted artifacts recovered without checksum changes or duplicate logical outputs",
                "restart recovery completed within three minutes"
            ]).ConfigureAwait(false);
        return new GracefulShutdownEvidence(
            configuredDrainSeconds,
            shutdown.Elapsed.TotalMilliseconds,
            before,
            stopped,
            after,
            recovery.Elapsed.TotalMilliseconds,
            interrupted.Count);
    }

    private static async Task<IReadOnlyList<SemanticFaultEvidence>> AssertSemanticFaultsAsync(
        Uri baseUri,
        string container,
        string runtimeRoot,
        IPage page,
        HttpClient session)
    {
        var results = new List<SemanticFaultEvidence>
        {
            await AssertRawPublicationKillBoundaryAsync(
                baseUri, container, runtimeRoot, page, TimeSpan.FromMinutes(2)).ConfigureAwait(false),
            await AssertAnnotationPublicationKillBoundaryAsync(
                baseUri, container, runtimeRoot, page, TimeSpan.FromMinutes(2)).ConfigureAwait(false)
        };

        await ActivateBootstrapCaptureProfileAsync(session).ConfigureAwait(false);
        await ActivateCanonicalCaptureProfileAsync(session).ConfigureAwait(false);
        results.Add(await AssertTransientCandidateKillBoundaryAsync(
            baseUri, container, runtimeRoot, page, TimeSpan.FromMinutes(2)).ConfigureAwait(false));

        for (var index = 1; index <= 2; index++)
        {
            var operationId = $"weather-failure-{index}";
            ArmFault(runtimeRoot, operationId, "processing.BeforeNodeExecution", "throw", "weather-overlay");
            await SetCaptureStateAsync(page, pause: false).ConfigureAwait(false);
            var hit = await WaitForFaultHitAsync(runtimeRoot, operationId, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            AssertFaultHit(hit, operationId, "processing.BeforeNodeExecution", "weather-overlay");
            var retry = await WaitForWeatherRetryAsync(
                runtimeRoot, hit.HitUtc, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var degradedHealth = await WaitForHealthCheckStatusAsync(
                session, "capture-processing", "Degraded", TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            Assert.AreEqual("Degraded", degradedHealth.OverallStatus);
            var continuationRaw = await WaitForManifestAsync(
                runtimeRoot, FrameArtifactRole.Raw, retry.CaptureSequence, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await SetCaptureStateAsync(page, pause: true).ConfigureAwait(false);
            var recovered = await WaitForCompleteCaptureAsync(
                session, retry.CaptureId, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            var final = ReadWeatherRecoverySnapshot(runtimeRoot, recovered);
            AssertWeatherRecovery(retry, final);
            var healthy = await WaitForHealthyAsync(session, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            results.Add(new SemanticFaultEvidence(
                operationId,
                "processing.BeforeNodeExecution",
                hit,
                ProcessKilled: false,
                Recovered: true,
                RecoveryMilliseconds: null,
                HitToKillMilliseconds: null,
                RawPublication: null,
                AnnotationPublication: null,
                TransientCandidate: null,
                WeatherRetry: new WeatherRetryEvidence(
                    retry,
                    final,
                    new CaptureIdentityEvidence(
                        continuationRaw.Manifest.Descriptor.Capture.CaptureId,
                        continuationRaw.Manifest.Descriptor.Capture.CaptureSequence),
                    degradedHealth,
                    ReadHealthCheckEvidence(healthy, "capture-processing"))));
        }
        await WaitForDurableConditionAsync(
            runtimeRoot,
            static snapshot => snapshot.PendingRawCaptures == 0 && snapshot.PendingLaneWork == 0 &&
                               snapshot.PendingProcessingNodes == 0 && snapshot.DuplicateLogicalOutputs == 0,
            "semantic fault matrix convergence",
            TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        return results;
    }

    private static async Task<PressureDrainEvidence> AssertPressureAndDrainAsync(
        string runtimeRoot,
        string clearReferencePath,
        Guid retentionCaptureId,
        IPage page,
        HttpClient session)
    {
        const long totalCapacityBytes = 100_000_000_000;
        const long pressuredAvailableBytes = 9_000_000_000;
        const long recoveredAvailableBytes = 20_000_000_000;
        const string operationId = "bounded-drain";
        var eligibleRetention = await CreateEligibleRetentionCopiesAsync(
            runtimeRoot,
            retentionCaptureId).ConfigureAwait(false);
        var holdStartedUtc = DateTimeOffset.UtcNow;
        await WriteAcceptanceRetentionHoldsAsync(runtimeRoot, eligibleRetention, enabled: true).ConfigureAwait(false);
        var minimumSequence = ReadMaximumRawSequence(runtimeRoot);
        ArmFault(
            runtimeRoot,
            operationId,
            "processing.BeforeNodeExecution",
            "block",
            "calibration",
            blockTimeoutSeconds: 240);
        await SetCaptureStateAsync(page, pause: false).ConfigureAwait(false);
        await WaitForFaultHitAsync(runtimeRoot, operationId, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        var initial = await WaitForDrainConditionAsync(
            runtimeRoot,
            minimumSequence,
            static snapshot => snapshot.CaptureCount >= 6,
            "six-capture bounded drain workload",
            TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        await SetCaptureStateAsync(page, pause: true).ConfigureAwait(false);
        initial = ReadDrainSnapshot(runtimeRoot, minimumSequence);
        Assert.AreEqual(6, initial.CaptureCount);
        Assert.AreEqual(151_400_448, initial.RawBytes);
        initial = await WaitForDrainConditionAsync(
            runtimeRoot,
            minimumSequence,
            static snapshot => snapshot.OldestAge >= TimeSpan.FromSeconds(60),
            "60-second oldest bounded drain work",
            TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        Assert.AreEqual(6, initial.RetentionHoldCount);
        Assert.AreEqual(6, initial.IncompleteStandardWorkCount);

        var retainedRaw = ReadManifests(runtimeRoot)
            .Where(item => item.Manifest.Descriptor.Artifact.Role == FrameArtifactRole.Raw &&
                item.Manifest.Descriptor.Capture.CaptureSequence > minimumSequence)
            .OrderBy(item => item.Manifest.Descriptor.Capture.CaptureSequence)
            .Select(item => SnapshotArtifactFiles(runtimeRoot, item))
            .ToArray();
        Assert.HasCount(6, retainedRaw);
        var clearReference = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(clearReferencePath).ConfigureAwait(false));
        Assert.IsTrue(clearReference.IsValid);
        var clearReferenceManifest = clearReference.Document?.Manifest
            ?? throw new InvalidDataException("The W6 clear reference is invalid.");
        var clearPayloadPath = Path.Combine(runtimeRoot, clearReferenceManifest.RelativeArtifactPath);
        var clearPayloadSha256 = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(clearPayloadPath).ConfigureAwait(false)));

        var baselineStorage = await ReadStorageObservationAsync(session).ConfigureAwait(false);
        while (DateTimeOffset.UtcNow >= baselineStorage.ObservedUtc.AddSeconds(55))
        {
            baselineStorage = await WaitForStorageObservationAsync(
                session,
                observation => observation.ObservedUtc > baselineStorage.ObservedUtc,
                TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        }
        var injectAt = baselineStorage.ObservedUtc.AddSeconds(50);
        if (injectAt > DateTimeOffset.UtcNow)
        {
            await Task.Delay(injectAt - DateTimeOffset.UtcNow).ConfigureAwait(false);
        }

        var pressureStartedUtc = DateTimeOffset.UtcNow;
        WriteCapacityOverride(runtimeRoot, totalCapacityBytes, pressuredAvailableBytes);
        var pressured = await WaitForStorageObservationAsync(
            session,
            observation => observation.ObservedUtc > baselineStorage.ObservedUtc &&
                observation.State.IsUnderPressure &&
                observation.State.AvailableBytes == pressuredAvailableBytes,
            TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        var degradedHealth = await ReadHealthAsync(session).ConfigureAwait(false);
        AssertHealthOverallStatus(degradedHealth, "Degraded");
        AssertHealthCheckStatus(degradedHealth, "disk-pressure", "Degraded");
        WriteCapacityOverride(runtimeRoot, totalCapacityBytes, recoveredAvailableBytes);
        var pressureDuration = DateTimeOffset.UtcNow - pressureStartedUtc;
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(60), pressureDuration);
        AssertArtifactFilesUnchanged(retainedRaw);
        Assert.AreEqual(clearPayloadSha256, Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(clearPayloadPath).ConfigureAwait(false))));
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(60), DateTimeOffset.UtcNow - holdStartedUtc);
        AssertEligibleRetentionFilesExist(eligibleRetention);

        var releasePath = Path.Combine(runtimeRoot, "acceptance-faults", $"release-{operationId}");
        File.WriteAllBytes(releasePath, []);
        var drain = Stopwatch.StartNew();
        var completed = await WaitForDrainConditionAsync(
            runtimeRoot,
            minimumSequence,
            static snapshot => snapshot.IncompleteStandardWorkCount == 0,
            "bounded drain completion",
            TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        drain.Stop();
        var throughput = completed.CaptureCount / drain.Elapsed.TotalSeconds;
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(3), drain.Elapsed);
        Assert.IsGreaterThanOrEqualTo(0.2, throughput);
        Assert.AreEqual(0, completed.DuplicateLogicalOutputs);
        AssertArtifactFilesUnchanged(retainedRaw);

        var holdDuration = DateTimeOffset.UtcNow - holdStartedUtc;
        await WriteAcceptanceRetentionHoldsAsync(runtimeRoot, eligibleRetention, enabled: false).ConfigureAwait(false);
        var cleanup = Stopwatch.StartNew();
        await WaitForEligibleRetentionCleanupAsync(eligibleRetention, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        cleanup.Stop();

        var recovered = await WaitForStorageObservationAsync(
            session,
            observation => observation.ObservedUtc > pressured.ObservedUtc &&
                !observation.State.IsUnderPressure &&
                observation.State.AvailableBytes == recoveredAvailableBytes,
            TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        var recoveredHealth = await WaitForHealthyAsync(session, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        await Phase14ScenarioEvidence.RecordAsync(
            "disk-pressure",
            operationId,
            "AssertPressureAndDrainAsync",
            [
                "disk pressure was detected and reported degraded within sixty seconds",
                "retained raw and reference artifacts remained checksum-stable under pressure",
                "six queued captures drained within three minutes at no less than 0.2 captures per second",
                "drain completed without duplicate logical outputs and healthy storage observations resumed"
            ]).ConfigureAwait(false);
        return new PressureDrainEvidence(
            initial.CaptureCount,
            initial.RawBytes,
            initial.OldestAge.TotalSeconds,
            initial.RetentionHoldCount,
            pressureStartedUtc,
            pressureDuration.TotalSeconds,
            pressured.State.AvailableBytes,
            recovered.State.AvailableBytes,
            drain.Elapsed.TotalSeconds,
            throughput,
            retainedRaw,
            new EligibleRetentionEvidence(
                holdDuration.TotalSeconds,
                cleanup.Elapsed.TotalSeconds,
                eligibleRetention),
            degradedHealth,
            recoveredHealth);
    }

    private static async Task<List<EligibleRetentionArtifactEvidence>> CreateEligibleRetentionCopiesAsync(
        string runtimeRoot,
        Guid captureId)
    {
        var source = ReadManifests(runtimeRoot).Where(item =>
                item.Manifest.Descriptor.Capture.CaptureId == captureId &&
                item.Manifest.Descriptor.Artifact.Role is FrameArtifactRole.Raw or FrameArtifactRole.Calibrated)
            .OrderBy(item => item.Manifest.Descriptor.Artifact.Role)
            .ToArray();
        Assert.HasCount(2, source);
        var createdUtc = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = new List<EligibleRetentionArtifactEvidence>(2);
        foreach (var observation in source)
        {
            var artifact = observation.Manifest.Descriptor.Artifact;
            var isRaw = artifact.Role == FrameArtifactRole.Raw;
            var relativeDirectory = isRaw
                ? Path.Combine("frames", "2020", "01", "01", "Raw")
                : Path.Combine("derived", "retention-acceptance");
            var relativePayload = Path.Combine(relativeDirectory, $"{artifact.ArtifactId:N}.bin");
            var relativeSidecar = Path.Combine(
                relativeDirectory,
                $"{artifact.ArtifactId:N}{(isRaw ? ".json" : ".manifest.json")}");
            var payloadPath = Path.Combine(runtimeRoot, relativePayload);
            var sidecarPath = Path.Combine(runtimeRoot, relativeSidecar);
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
            File.Copy(Path.Combine(runtimeRoot, observation.Manifest.RelativeArtifactPath), payloadPath, overwrite: true);
            if (isRaw)
            {
                var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(observation.Path).ConfigureAwait(false))!.AsObject();
                manifest["relativeArtifactPath"] = relativePayload.Replace(Path.DirectorySeparatorChar, '/');
                manifest["descriptor"]!["artifact"]!["createdUtc"] = createdUtc;
                await File.WriteAllBytesAsync(sidecarPath, JsonSerializer.SerializeToUtf8Bytes(manifest, EvidenceJson))
                    .ConfigureAwait(false);
            }
            else
            {
                File.Copy(observation.Path, sidecarPath, overwrite: true);
            }
            File.SetLastWriteTimeUtc(payloadPath, createdUtc.UtcDateTime);
            File.SetLastWriteTimeUtc(sidecarPath, createdUtc.UtcDateTime);
            result.Add(new EligibleRetentionArtifactEvidence(
                artifact.ArtifactId,
                artifact.Role.ToString(),
                relativePayload.Replace(Path.DirectorySeparatorChar, '/'),
                relativeSidecar.Replace(Path.DirectorySeparatorChar, '/'),
                payloadPath,
                sidecarPath,
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false))),
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false)))));
        }
        return result;
    }

    private static async Task WriteAcceptanceRetentionHoldsAsync(
        string runtimeRoot,
        IReadOnlyList<EligibleRetentionArtifactEvidence> artifacts,
        bool enabled)
    {
        var path = Path.Combine(runtimeRoot, "acceptance-faults", "retention-holds.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var holds = enabled
            ? artifacts.Select(static artifact => new
            {
                artifact.ArtifactId,
                artifact.PayloadRelativePath,
                artifact.SidecarRelativePath
            }).ToArray()
            : [];
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(holds, EvidenceJson)).ConfigureAwait(false);
    }

    private static void AssertEligibleRetentionFilesExist(
        IReadOnlyList<EligibleRetentionArtifactEvidence> artifacts)
    {
        foreach (var artifact in artifacts)
        {
            Assert.IsTrue(File.Exists(artifact.PayloadPath));
            Assert.IsTrue(File.Exists(artifact.SidecarPath));
            Assert.AreEqual(artifact.PayloadSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifact.PayloadPath))));
            Assert.AreEqual(artifact.SidecarSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifact.SidecarPath))));
        }
    }

    private static async Task WaitForEligibleRetentionCleanupAsync(
        IReadOnlyList<EligibleRetentionArtifactEvidence> artifacts,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (artifacts.All(static artifact =>
                    !File.Exists(artifact.PayloadPath) && !File.Exists(artifact.SidecarPath)))
            {
                return;
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
        Assert.Fail("Eligible raw and derivative evidence was not removed after its acceptance hold was released.");
    }

    private static ArtifactFileEvidence SnapshotArtifactFiles(string root, ManifestObservation observation)
    {
        var payloadPath = Path.Combine(root, observation.Manifest.RelativeArtifactPath);
        return new ArtifactFileEvidence(
            observation.Manifest.Descriptor.Capture.CaptureId,
            observation.Manifest.Descriptor.Artifact.ArtifactId,
            Path.GetRelativePath(root, payloadPath).Replace(Path.DirectorySeparatorChar, '/'),
            Path.GetRelativePath(root, observation.Path).Replace(Path.DirectorySeparatorChar, '/'),
            payloadPath,
            observation.Path,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(payloadPath))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(observation.Path))));
    }

    private static void AssertArtifactFilesUnchanged(IEnumerable<ArtifactFileEvidence> evidence)
    {
        foreach (var item in evidence)
        {
            Assert.AreEqual(item.PayloadSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.PayloadPath))));
            Assert.AreEqual(item.ManifestSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.ManifestPath))));
        }
    }

    private static void WriteCapacityOverride(string runtimeRoot, long totalBytes, long availableBytes)
    {
        var path = Path.Combine(runtimeRoot, "acceptance-faults", "capacity.json");
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new { totalBytes, availableBytes }, EvidenceJson));
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<SemanticFaultEvidence> AssertRawPublicationKillBoundaryAsync(
        Uri baseUri,
        string container,
        string runtimeRoot,
        IPage page,
        TimeSpan timeout)
    {
        const string operationId = "raw-publication";
        const string boundary = "raw.SidecarDirectorySynced";
        var minimumSequence = ReadMaximumRawSequence(runtimeRoot);
        ArmFault(runtimeRoot, operationId, boundary, "block", null);
        await SetCaptureStateAsync(page, pause: false).ConfigureAwait(false);
        var hit = await WaitForFaultHitAsync(runtimeRoot, operationId, timeout).ConfigureAwait(false);
        AssertFaultHit(hit, operationId, boundary, null);

        var interrupted = await WaitForManifestAsync(
            runtimeRoot, FrameArtifactRole.Raw, minimumSequence, timeout).ConfigureAwait(false);
        var payloadPath = Path.Combine(runtimeRoot, interrupted.Manifest.RelativeArtifactPath);
        var payloadSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false)));
        var manifestSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(interrupted.Path).ConfigureAwait(false)));
        Assert.AreEqual(interrupted.Manifest.Descriptor.Artifact.ChecksumSha256, payloadSha256);

        var recovery = Stopwatch.StartNew();
        var hitToKill = await KillAndStartContainerAsync(baseUri, container, hit).ConfigureAwait(false);
        await SetCaptureStateAsync(page, pause: true).ConfigureAwait(false);
        await WaitForDurableConditionAsync(
            runtimeRoot,
            static snapshot => snapshot.PendingRawCaptures == 0 && snapshot.PendingLaneWork == 0 &&
                               snapshot.PendingProcessingNodes == 0 && snapshot.DuplicateLogicalOutputs == 0,
            $"{operationId} recovery",
            TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        recovery.Stop();
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(3), recovery.Elapsed);

        var descriptor = interrupted.Manifest.Descriptor;
        Assert.IsTrue(IsRawCaptureCommitted(runtimeRoot, descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId));
        Assert.AreEqual(0, ReadJournalCount(
            runtimeRoot,
            "SELECT COUNT(*) FROM raw_ingress_reconciliation WHERE outcome = 'quarantined';"));
        Assert.AreEqual(payloadSha256, Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false))));
        Assert.AreEqual(manifestSha256, Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(interrupted.Path).ConfigureAwait(false))));
        await Phase14ScenarioEvidence.RecordAsync(
            "raw-boundary-sidecar-directory-sync",
            operationId,
            "AssertRawPublicationKillBoundaryAsync",
            [
                "raw payload and sidecar were durable at the sidecar directory sync boundary",
                "restart reconciliation committed the raw capture without quarantine",
                "payload and sidecar checksums were unchanged after recovery",
                "recovery completed within three minutes with no duplicate logical outputs"
            ]).ConfigureAwait(false);
        return new SemanticFaultEvidence(
            operationId,
            boundary,
            hit,
            ProcessKilled: true,
            Recovered: true,
            recovery.Elapsed.TotalMilliseconds,
            hitToKill.TotalMilliseconds,
            new RawPublicationEvidence(
                descriptor.Capture.CaptureId,
                descriptor.Capture.CaptureSequence,
                descriptor.Artifact.ArtifactId,
                payloadSha256,
                manifestSha256),
            AnnotationPublication: null,
            TransientCandidate: null,
            WeatherRetry: null);
    }

    private static async Task<SemanticFaultEvidence> AssertAnnotationPublicationKillBoundaryAsync(
        Uri baseUri,
        string container,
        string runtimeRoot,
        IPage page,
        TimeSpan timeout)
    {
        const string operationId = "annotation-publication";
        const string boundary = "processing.AfterOutputsPublishedBeforeNodeCommit";
        const string nodeId = "sky-annotation";
        var minimumSequence = ReadMaximumRawSequence(runtimeRoot);
        ArmFault(runtimeRoot, operationId, boundary, "block", nodeId);
        await SetCaptureStateAsync(page, pause: false).ConfigureAwait(false);
        var hit = await WaitForFaultHitAsync(runtimeRoot, operationId, timeout).ConfigureAwait(false);
        AssertFaultHit(hit, operationId, boundary, nodeId);
        var before = await WaitForAnnotationPublicationAsync(
            runtimeRoot, minimumSequence, timeout).ConfigureAwait(false);
        Assert.AreEqual(0, CountLogicalProcessingOutputs(
            runtimeRoot, before.CaptureId, nodeId, before.Role, before.Variant));
        var recovery = Stopwatch.StartNew();
        var hitToKill = await KillAndStartContainerAsync(baseUri, container, hit).ConfigureAwait(false);
        await SetCaptureStateAsync(page, pause: true).ConfigureAwait(false);
        await WaitForDurableConditionAsync(
            runtimeRoot,
            static snapshot => snapshot.PendingRawCaptures == 0 && snapshot.PendingLaneWork == 0 &&
                               snapshot.PendingProcessingNodes == 0 && snapshot.DuplicateLogicalOutputs == 0,
            $"{operationId} recovery",
            TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        recovery.Stop();
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(3), recovery.Elapsed);
        var after = ReadAnnotationPublication(runtimeRoot, before.CaptureId, before.ArtifactId);
        AssertAnnotationPublicationUnchanged(before, after);
        Assert.AreEqual(1, CountLogicalProcessingOutputs(
            runtimeRoot, before.CaptureId, nodeId, before.Role, before.Variant));
        AssertCommittedAnnotationOutput(runtimeRoot, after);
        Assert.AreEqual(1, ReadJournalCount(runtimeRoot, $"""
            SELECT COUNT(*) FROM processing_nodes
            WHERE capture_id = '{before.CaptureId:N}' AND node_id = 'sky-annotation' AND status = 'Completed';
            """));
        Assert.AreEqual(0, ReadDurableSnapshot(runtimeRoot).DuplicateLogicalOutputs);
        await Phase14ScenarioEvidence.RecordAsync(
            "processing-output-before-node-commit",
            operationId,
            "AssertAnnotationPublicationKillBoundaryAsync",
            [
                "annotation output was published before its processing node commit",
                "restart preserved the published output identity and checksums",
                "recovery committed exactly one logical annotation output and completed node",
                "recovery completed within three minutes with no duplicate logical outputs"
            ]).ConfigureAwait(false);
        return new SemanticFaultEvidence(
            operationId,
            boundary,
            hit,
            ProcessKilled: true,
            Recovered: true,
            recovery.Elapsed.TotalMilliseconds,
            hitToKill.TotalMilliseconds,
            RawPublication: null,
            AnnotationPublication: new AnnotationPublicationEvidence(before, after),
            TransientCandidate: null,
            WeatherRetry: null);
    }

    private static async Task<SemanticFaultEvidence> AssertTransientCandidateKillBoundaryAsync(
        Uri baseUri,
        string container,
        string runtimeRoot,
        IPage page,
        TimeSpan timeout)
    {
        const string operationId = "transient-candidate";
        const string boundary = "transient-runtime.AfterCandidateJournalCommit";
        ArmFault(runtimeRoot, operationId, boundary, "block", null);
        await SetCaptureStateAsync(page, pause: false).ConfigureAwait(false);
        var hit = await WaitForFaultHitAsync(runtimeRoot, operationId, timeout).ConfigureAwait(false);
        AssertFaultHit(hit, operationId, boundary, null);
        var before = ReadTransientCandidateBoundary(runtimeRoot, requireFinal: false);
        var recovery = Stopwatch.StartNew();
        var hitToKill = await KillAndStartContainerAsync(baseUri, container, hit).ConfigureAwait(false);
        await SetCaptureStateAsync(page, pause: true).ConfigureAwait(false);
        _ = await WaitForTransientConvergenceAsync(runtimeRoot, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        recovery.Stop();
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(3), recovery.Elapsed);
        var after = ReadTransientCandidateBoundary(runtimeRoot, requireFinal: true, before.CandidateId);
        AssertTransientCandidateRecovery(before, after);
        await Phase14ScenarioEvidence.RecordAsync(
            "transient-runtime-candidate-journal-after-commit",
            operationId,
            "AssertTransientCandidateKillBoundaryAsync",
            [
                "transient candidate journal commit was durable before process termination",
                "restart converged the same candidate to its final state",
                "candidate recovery preserved the asserted identity and provenance",
                "recovery completed within three minutes"
            ]).ConfigureAwait(false);
        return new SemanticFaultEvidence(
            operationId,
            boundary,
            hit,
            ProcessKilled: true,
            Recovered: true,
            recovery.Elapsed.TotalMilliseconds,
            hitToKill.TotalMilliseconds,
            RawPublication: null,
            AnnotationPublication: null,
            TransientCandidate: new TransientCandidateBoundaryEvidence(before, after),
            WeatherRetry: null);
    }

    private static async Task<AnnotationPublicationSnapshot> WaitForAnnotationPublicationAsync(
        string runtimeRoot,
        long minimumSequence,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = ReadManifests(runtimeRoot)
                .Where(item => item.Manifest.Descriptor.Capture.CaptureSequence > minimumSequence &&
                    item.Manifest.Descriptor.Artifact.Role == FrameArtifactRole.AnnotatedPreview &&
                    item.Manifest.Descriptor.Artifact.Variant == "w6-annotated")
                .OrderBy(static item => item.Manifest.Descriptor.Capture.CaptureSequence)
                .FirstOrDefault(item => CountProcessingNodes(
                    runtimeRoot, item.Manifest.Descriptor.Capture.CaptureId, "sky-annotation") == 0);
            if (match is not null)
            {
                return ReadAnnotationPublication(
                    runtimeRoot,
                    match.Manifest.Descriptor.Capture.CaptureId,
                    match.Manifest.Descriptor.Artifact.ArtifactId);
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        Assert.Fail("The sky annotation publication was not visible at its pre-commit fault boundary.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static AnnotationPublicationSnapshot ReadAnnotationPublication(
        string runtimeRoot,
        Guid captureId,
        Guid artifactId)
    {
        var observation = ReadManifests(runtimeRoot).Single(item =>
            item.Manifest.Descriptor.Capture.CaptureId == captureId &&
            item.Manifest.Descriptor.Artifact.ArtifactId == artifactId);
        var descriptor = observation.Manifest.Descriptor;
        var payloadPath = Path.Combine(runtimeRoot, observation.Manifest.RelativeArtifactPath);
        var payloadSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(payloadPath)));
        var manifestSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(observation.Path)));
        Assert.AreEqual(descriptor.Artifact.ChecksumSha256, payloadSha256);
        var recipeIdentitySha256 = ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256;
        var outputIdentitySha256 = ProcessingIdentity.CreateOutputIdentity(
            descriptor.Artifact.Role,
            descriptor.Artifact.Variant,
            recipeIdentitySha256,
            descriptor.Artifact.SourceArtifactIds);
        Assert.AreEqual(artifactId, ProcessingIdentity.CreateArtifactId(outputIdentitySha256));
        return new AnnotationPublicationSnapshot(
            captureId,
            descriptor.Capture.CaptureSequence,
            artifactId,
            descriptor.Artifact.Role.ToString(),
            descriptor.Artifact.Variant,
            payloadSha256,
            manifestSha256,
            outputIdentitySha256,
            recipeIdentitySha256,
            descriptor.Artifact.SourceArtifactIds.ToArray());
    }

    private static long CountProcessingNodes(string runtimeRoot, Guid captureId, string nodeId)
    {
        using var connection = OpenJournal(runtimeRoot);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM processing_nodes WHERE capture_id = $capture AND node_id = $node;";
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node", nodeId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long CountLogicalProcessingOutputs(
        string runtimeRoot,
        Guid captureId,
        string nodeId,
        string role,
        string variant)
    {
        using var connection = OpenJournal(runtimeRoot);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM processing_outputs
            WHERE capture_id = $capture AND node_id = $node AND role = $role AND variant = $variant;
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node", nodeId);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$variant", variant);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void AssertCommittedAnnotationOutput(
        string runtimeRoot,
        AnnotationPublicationSnapshot expected)
    {
        using var connection = OpenJournal(runtimeRoot);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT artifact_id, output_identity_sha256, recipe_identity_sha256
            FROM processing_outputs
            WHERE capture_id = $capture AND node_id = 'sky-annotation'
              AND role = $role AND variant = $variant;
            """;
        command.Parameters.AddWithValue("$capture", expected.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$role", expected.Role);
        command.Parameters.AddWithValue("$variant", expected.Variant);
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(expected.ArtifactId, Guid.ParseExact(reader.GetString(0), "N"));
        Assert.AreEqual(expected.OutputIdentitySha256, reader.GetString(1));
        Assert.AreEqual(expected.RecipeIdentitySha256, reader.GetString(2));
        Assert.IsFalse(reader.Read());
    }

    private static void AssertAnnotationPublicationUnchanged(
        AnnotationPublicationSnapshot before,
        AnnotationPublicationSnapshot after)
    {
        Assert.AreEqual(before.CaptureId, after.CaptureId);
        Assert.AreEqual(before.CaptureSequence, after.CaptureSequence);
        Assert.AreEqual(before.ArtifactId, after.ArtifactId);
        Assert.AreEqual(before.Role, after.Role);
        Assert.AreEqual(before.Variant, after.Variant);
        Assert.AreEqual(before.PayloadSha256, after.PayloadSha256);
        Assert.AreEqual(before.ManifestSha256, after.ManifestSha256);
        Assert.AreEqual(before.OutputIdentitySha256, after.OutputIdentitySha256);
        Assert.AreEqual(before.RecipeIdentitySha256, after.RecipeIdentitySha256);
        Assert.IsTrue(before.OrderedSourceArtifactIds.SequenceEqual(after.OrderedSourceArtifactIds));
    }

    private static TransientCandidateBoundarySnapshot ReadTransientCandidateBoundary(
        string runtimeRoot,
        bool requireFinal,
        Guid? candidateId = null)
    {
        using var connection = OpenJournal(runtimeRoot);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.candidate_id, c.event_id, c.reservation_identity_sha256,
                   c.candidate_payload, c.candidate_payload_sha256, c.state, c.phase,
                   w.observation_id, w.assessment_id, w.event_version_id, w.allocated_unix_ms,
                   w.causal_extraction_json, w.causal_extraction_sha256,
                   w.observation_extraction_json, w.observation_extraction_sha256,
                   w.assessment_execution_json, w.assessment_execution_sha256, w.state,
                   r.capture_id, r.capture_sequence,
                   c.finalization_payload, c.finalization_receipt_identity_sha256
            FROM transient_candidates c
            LEFT JOIN transient_worker_candidates w ON w.candidate_id = c.candidate_id
            LEFT JOIN raw_captures r ON r.raw_capture_row_id = w.target_raw_capture_row_id
            WHERE ($candidate IS NULL AND c.phase = 'candidate_persisted') OR c.candidate_id = $candidate
            ORDER BY c.updated_unix_ms DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$candidate", candidateId is null ? DBNull.Value : candidateId.Value.ToString("N"));
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        var candidatePayload = reader.GetFieldValue<byte[]>(3);
        var candidatePayloadSha256 = reader.GetString(4);
        Assert.AreEqual(candidatePayloadSha256, Convert.ToHexString(SHA256.HashData(candidatePayload)));
        var causalSha256 = ReadAndVerifyOptionalPayload(reader, 11, 12);
        var centeredSha256 = ReadAndVerifyOptionalPayload(reader, 13, 14);
        var assessmentSha256 = ReadAndVerifyOptionalPayload(reader, 15, 16);
        var finalizationPayloadSha256 = reader.IsDBNull(20)
            ? null
            : Convert.ToHexString(SHA256.HashData(reader.GetFieldValue<byte[]>(20)));
        var resolvedCandidateId = Guid.ParseExact(reader.GetString(0), "N");
        var eventId = Guid.ParseExact(reader.GetString(1), "N");
        var reservationIdentitySha256 = reader.GetString(2);
        var state = reader.GetString(5);
        var phase = reader.GetString(6);
        var observationId = ReadNullableGuid(reader, 7);
        var assessmentId = ReadNullableGuid(reader, 8);
        var eventVersionId = ReadNullableGuid(reader, 9);
        var allocatedUtc = reader.IsDBNull(10)
            ? (DateTimeOffset?)null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10));
        var workerState = reader.IsDBNull(17) ? null : reader.GetString(17);
        var targetCaptureId = ReadNullableGuid(reader, 18);
        var targetCaptureSequence = reader.IsDBNull(19) ? (long?)null : reader.GetInt64(19);
        var finalizationReceiptIdentitySha256 = reader.IsDBNull(21) ? null : reader.GetString(21);
        if (!reader.IsDBNull(20))
        {
            var parsed = TransientCandidateDeliveryJson.ParseFinalization(reader.GetFieldValue<byte[]>(20));
            Assert.IsTrue(parsed.Validation.IsValid);
            var receipt = parsed.Value ?? throw new InvalidDataException("The finalization receipt is missing.");
            Assert.AreEqual(resolvedCandidateId, receipt.CandidateId);
            Assert.AreEqual(eventId, receipt.EventId);
            Assert.AreEqual(finalizationReceiptIdentitySha256, receipt.ReceiptIdentitySha256);
            Assert.AreEqual(WriteEventState(receipt.Event.State), state);
        }
        reader.Close();
        var snapshot = new TransientCandidateBoundarySnapshot(
            resolvedCandidateId,
            eventId,
            reservationIdentitySha256,
            candidatePayloadSha256,
            state,
            phase,
            observationId,
            assessmentId,
            eventVersionId,
            allocatedUtc,
            causalSha256,
            centeredSha256,
            assessmentSha256,
            workerState,
            targetCaptureId,
            targetCaptureSequence,
            finalizationPayloadSha256,
            finalizationReceiptIdentitySha256,
            ReadTransientSources(connection, resolvedCandidateId),
            ReadTransientBoundaryCounts(connection, resolvedCandidateId, eventId));
        if (requireFinal)
        {
            Assert.AreEqual("finalized", snapshot.Phase);
            Assert.IsNotNull(snapshot.FinalizationPayloadSha256);
            Assert.IsNotNull(snapshot.FinalizationReceiptIdentitySha256);
            Assert.IsNotNull(snapshot.CenteredExtractionSha256);
            Assert.IsNotNull(snapshot.AssessmentExecutionSha256);
            Assert.AreEqual("completed", snapshot.WorkerState);
            Assert.IsNotNull(snapshot.ObservationId);
            Assert.IsNotNull(snapshot.AssessmentId);
            Assert.IsNotNull(snapshot.EventVersionId);
            Assert.AreEqual(1, snapshot.Counts.CandidateRows);
            Assert.AreEqual(1, snapshot.Counts.EventIdentityRows);
            Assert.AreEqual(1, snapshot.Counts.FinalizedCandidates);
            Assert.AreEqual(1, snapshot.Counts.FinalizedEvents);
            Assert.AreEqual(0, snapshot.Counts.ConflictsOrQuarantine);
        }
        else
        {
            Assert.AreEqual("candidate_persisted", snapshot.Phase);
            Assert.IsNotNull(snapshot.CausalExtractionSha256);
            Assert.IsNull(snapshot.FinalizationPayloadSha256);
            Assert.IsNull(snapshot.FinalizationReceiptIdentitySha256);
        }
        return snapshot;
    }

    private static string? ReadAndVerifyOptionalPayload(SqliteDataReader reader, int payloadOrdinal, int shaOrdinal)
    {
        if (reader.IsDBNull(payloadOrdinal) && reader.IsDBNull(shaOrdinal))
        {
            return null;
        }
        Assert.IsFalse(reader.IsDBNull(payloadOrdinal));
        Assert.IsFalse(reader.IsDBNull(shaOrdinal));
        var payload = reader.GetFieldValue<byte[]>(payloadOrdinal);
        var sha256 = reader.GetString(shaOrdinal);
        Assert.AreEqual(sha256, Convert.ToHexString(SHA256.HashData(payload)));
        return sha256;
    }

    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Guid.ParseExact(reader.GetString(ordinal), "N");

    private static List<TransientSourceIdentity> ReadTransientSources(
        SqliteConnection connection,
        Guid candidateId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.source_ordinal, s.evidence_id, r.capture_id, r.capture_sequence,
                   s.artifact_id, s.artifact_role, s.artifact_variant,
                   s.recipe_identity_sha256, s.checksum_sha256
            FROM transient_candidate_sources s
            JOIN raw_captures r ON r.raw_capture_row_id = s.raw_capture_row_id
            WHERE s.candidate_id = $candidate
            ORDER BY s.source_ordinal;
            """;
        command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
        var sources = new List<TransientSourceIdentity>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sources.Add(new TransientSourceIdentity(
                reader.GetInt32(0),
                Guid.ParseExact(reader.GetString(1), "N"),
                Guid.ParseExact(reader.GetString(2), "N"),
                reader.GetInt64(3),
                Guid.ParseExact(reader.GetString(4), "N"),
                reader.GetInt32(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }
        Assert.IsNotEmpty(sources);
        return sources;
    }

    private static TransientBoundaryCounts ReadTransientBoundaryCounts(
        SqliteConnection connection,
        Guid candidateId,
        Guid eventId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM transient_candidates WHERE candidate_id = $candidate),
                (SELECT COUNT(*) FROM transient_event_identities WHERE event_id = $event),
                (SELECT COUNT(*) FROM transient_candidates
                    WHERE candidate_id = $candidate AND phase = 'finalized' AND finalization_payload IS NOT NULL),
                (SELECT COUNT(DISTINCT event_id) FROM transient_candidates
                    WHERE event_id = $event AND phase = 'finalized' AND finalization_payload IS NOT NULL),
                (SELECT COUNT(*) FROM transient_candidate_conflicts) +
                (SELECT COUNT(*) FROM transient_candidates WHERE phase = 'quarantined') +
                (SELECT COUNT(*) FROM transient_worker_candidates WHERE state = 'quarantined') +
                (SELECT COUNT(*) FROM transient_worker_frames WHERE state = 'quarantined');
            """;
        command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
        command.Parameters.AddWithValue("$event", eventId.ToString("N"));
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        return new TransientBoundaryCounts(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }

    private static void AssertTransientCandidateRecovery(
        TransientCandidateBoundarySnapshot before,
        TransientCandidateBoundarySnapshot after)
    {
        Assert.AreEqual(before.CandidateId, after.CandidateId);
        Assert.AreEqual(before.EventId, after.EventId);
        Assert.AreEqual(before.ReservationIdentitySha256, after.ReservationIdentitySha256);
        Assert.AreEqual(before.CandidatePayloadSha256, after.CandidatePayloadSha256);
        Assert.AreEqual("provisional", before.State);
        Assert.AreEqual("needs_review", after.State);
        Assert.AreEqual(before.CausalExtractionSha256, after.CausalExtractionSha256);
        Assert.IsTrue(before.Sources.SequenceEqual(after.Sources));
        AssertOptionalIdentityUnchanged(before.ObservationId, after.ObservationId);
        AssertOptionalIdentityUnchanged(before.AssessmentId, after.AssessmentId);
        AssertOptionalIdentityUnchanged(before.EventVersionId, after.EventVersionId);
        AssertOptionalIdentityUnchanged(before.TargetCaptureId, after.TargetCaptureId);
        if (before.TargetCaptureSequence is not null)
        {
            Assert.AreEqual(before.TargetCaptureSequence, after.TargetCaptureSequence);
        }
        Assert.AreEqual(before.AllocatedUtc, after.AllocatedUtc);

        static void AssertOptionalIdentityUnchanged(Guid? expected, Guid? actual)
        {
            if (expected is not null)
            {
                Assert.AreEqual(expected, actual);
            }
        }
    }

    private static string WriteEventState(TransientEventState state) => state switch
    {
        TransientEventState.Pending => "pending",
        TransientEventState.Provisional => "provisional",
        TransientEventState.Validated => "validated",
        TransientEventState.Rejected => "rejected",
        TransientEventState.NeedsReview => "needs_review",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static async Task<WeatherRetrySnapshot> WaitForWeatherRetryAsync(
        string runtimeRoot,
        DateTimeOffset faultHitUtc,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var connection = OpenJournal(runtimeRoot);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT r.capture_id, r.capture_sequence, n.attempt, n.reason,
                           lane.attempt_count, lane.available_unix_ms
                    FROM processing_nodes n
                    JOIN raw_captures r ON r.capture_id = n.capture_id
                    JOIN capture_lane_work lane ON lane.raw_capture_row_id = r.raw_capture_row_id
                        AND lane.lane_name = 'standard'
                    WHERE n.node_id = 'weather-overlay' AND n.status = 'RetryableFailure'
                      AND lane.state = 'retry_wait' AND n.completed_unix_ms >= $hit
                    ORDER BY n.completed_unix_ms DESC
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$hit", faultHitUtc.AddSeconds(-1).ToUnixTimeMilliseconds());
                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var captureId = Guid.ParseExact(reader.GetString(0), "N");
                    var artifacts = ReadCaptureArtifactIdentities(runtimeRoot, captureId);
                    Assert.IsFalse(artifacts.Any(static artifact => artifact.Variant == "w6-weather-overlay"));
                    return new WeatherRetrySnapshot(
                        captureId,
                        reader.GetInt64(1),
                        "RetryableFailure",
                        reader.GetInt32(2),
                        reader.GetString(3),
                        "retry_wait",
                        reader.GetInt32(4),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                        artifacts);
                }
            }
            catch (SqliteException)
            {
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        Assert.Fail("The weather-overlay failure did not become retryable in both node and lane state.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static WeatherRecoverySnapshot ReadWeatherRecoverySnapshot(
        string runtimeRoot,
        CameraAgentGalleryCapture capture)
    {
        Assert.HasCount(10, capture.ProcessingNodes);
        Assert.IsTrue(capture.ProcessingNodes.All(static node => node.Status == "Completed"));
        Assert.IsTrue(capture.ProcessingNodes.Any(static node => node.NodeId == "storage" && node.Required));
        Assert.IsTrue(capture.ProcessingNodes.Any(static node => node.NodeId == "telemetry" && node.Required));
        var artifacts = ReadCaptureArtifactIdentities(runtimeRoot, capture.CaptureId);
        Assert.IsTrue(artifacts.Any(static artifact => artifact.Variant == "w6-weather-overlay"));
        using var connection = OpenJournal(runtimeRoot);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, attempt,
                   (SELECT attempt_count FROM capture_lane_work lane
                    JOIN raw_captures raw ON raw.raw_capture_row_id = lane.raw_capture_row_id
                    WHERE raw.capture_id = $capture AND lane.lane_name = 'standard')
            FROM processing_nodes
            WHERE capture_id = $capture AND status = 'Completed'
            ORDER BY node_id;
            """;
        command.Parameters.AddWithValue("$capture", capture.CaptureId.ToString("N"));
        var attempts = new List<NodeAttemptEvidence>();
        int? laneAttempt = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            attempts.Add(new NodeAttemptEvidence(reader.GetString(0), reader.GetInt32(1)));
            laneAttempt ??= reader.GetInt32(2);
            Assert.AreEqual(laneAttempt.Value, reader.GetInt32(2));
        }
        Assert.HasCount(10, attempts);
        Assert.IsNotNull(laneAttempt);
        Assert.AreEqual(0, ReadDurableSnapshot(runtimeRoot).DuplicateLogicalOutputs);
        return new WeatherRecoverySnapshot(capture.CaptureId, capture.CaptureSequence, laneAttempt.Value, attempts, artifacts);
    }

    private static void AssertWeatherRecovery(WeatherRetrySnapshot retry, WeatherRecoverySnapshot final)
    {
        Assert.AreEqual(retry.CaptureId, final.CaptureId);
        Assert.AreEqual(retry.CaptureSequence, final.CaptureSequence);
        Assert.AreEqual(retry.NodeAttempt, retry.LaneAttempt);
        Assert.AreEqual(retry.LaneAttempt + 1, final.LaneAttempt);
        Assert.IsTrue(final.NodeAttempts
            .Where(static item => item.NodeId is "weather-overlay" or "storage" or "telemetry")
            .All(item => item.Attempt == retry.NodeAttempt + 1));
        Assert.IsTrue(final.NodeAttempts
            .Where(static item => item.NodeId is not ("weather-overlay" or "storage" or "telemetry"))
            .All(item => item.Attempt is >= 1 && item.Attempt <= retry.NodeAttempt));
        foreach (var artifact in retry.Artifacts)
        {
            Assert.IsTrue(final.Artifacts.Contains(artifact), $"Artifact {artifact.ArtifactId} changed during retry.");
        }
    }

    private static CaptureArtifactIdentity[] ReadCaptureArtifactIdentities(string runtimeRoot, Guid captureId)
    {
        var artifacts = ReadManifests(runtimeRoot)
            .Where(item => item.Manifest.Descriptor.Capture.CaptureId == captureId)
            .Select(item => Snapshot(
                item.Manifest.Descriptor.Artifact,
                item.Manifest.RelativeArtifactPath))
            .Concat(ReadProductManifests(runtimeRoot, ExpectedAgentId)
                .Where(item => item.Manifest.Capture.CaptureId == captureId)
                .Select(item => Snapshot(item.Manifest.Artifact, item.Manifest.RelativeArtifactPath)))
            .Distinct()
            .OrderBy(static item => item.Role, StringComparer.Ordinal)
            .ThenBy(static item => item.Variant, StringComparer.Ordinal)
            .ThenBy(static item => item.ArtifactId)
            .ToArray();
        Assert.IsNotEmpty(artifacts);
        return artifacts;

        CaptureArtifactIdentity Snapshot(ArtifactDescriptor artifact, string relativePath)
        {
            var payloadSha256 = Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(runtimeRoot, relativePath))));
            Assert.AreEqual(artifact.ChecksumSha256, payloadSha256);
            return new CaptureArtifactIdentity(
                artifact.ArtifactId,
                artifact.Role.ToString(),
                artifact.Variant,
                payloadSha256);
        }
    }

    private static async Task<CameraAgentGalleryCapture> WaitForCompleteCaptureAsync(
        HttpClient client,
        Guid captureId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var capture = await client.GetFromJsonAsync<CameraAgentGalleryCapture>(
                $"/api/v1/operations/gallery/{captureId:D}").ConfigureAwait(false);
            if (capture?.Detail is { EvidenceAvailability: "Available" } &&
                capture.ProcessingNodes.Count == 10 &&
                capture.ProcessingNodes.All(static node => node.Status == "Completed"))
            {
                return capture;
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        Assert.Fail($"Capture {captureId:D} did not recover all processing nodes.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static void ArmFault(
        string runtimeRoot,
        string operationId,
        string boundary,
        string action,
        string? nodeId,
        int? blockTimeoutSeconds = null)
    {
        var root = Path.Combine(runtimeRoot, "acceptance-faults");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "arm.json");
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(
            new { operationId, boundary, action, nodeId, blockTimeoutSeconds }, EvidenceJson));
        File.Move(temporary, path, overwrite: false);
    }

    private static async Task<FaultHitEvidence> WaitForFaultHitAsync(
        string runtimeRoot,
        string operationId,
        TimeSpan timeout)
    {
        var path = Path.Combine(runtimeRoot, "acceptance-faults", $"hit-{operationId}.json");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    var hit = JsonSerializer.Deserialize<FaultHitEvidence>(
                        await File.ReadAllBytesAsync(path).ConfigureAwait(false), EvidenceJson);
                    if (hit is not null && hit.HitUtc.Offset == TimeSpan.Zero)
                    {
                        return hit;
                    }
                }
                catch (IOException)
                {
                }
                catch (JsonException)
                {
                }
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        Assert.Fail($"Acceptance fault '{operationId}' was not reached within {timeout}.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static void AssertFaultHit(
        FaultHitEvidence hit,
        string operationId,
        string boundary,
        string? nodeId)
    {
        Assert.AreEqual(operationId, hit.OperationId);
        Assert.AreEqual(boundary, hit.Boundary);
        Assert.AreEqual(nodeId, hit.NodeId);
        Assert.IsLessThanOrEqualTo(DateTimeOffset.UtcNow, hit.HitUtc);
    }

    private static async Task<TimeSpan> KillAndStartContainerAsync(
        Uri baseUri,
        string container,
        FaultHitEvidence hit)
    {
        await ProcessAsync("docker", ["kill", "--signal", "KILL", container]).ConfigureAwait(false);
        var hitToKill = DateTimeOffset.UtcNow - hit.HitUtc;
        Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, hitToKill);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(30), hitToKill);
        await ProcessAsync("docker", ["start", container]).ConfigureAwait(false);
        await WaitForHostAsync(baseUri).ConfigureAwait(false);
        return hitToKill;
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.IsTrue(token.Success);
        return WebUtility.HtmlDecode(token.Groups[1].Value);
    }

    private static async Task<ManifestObservation> WaitForManifestAsync(
        string root,
        FrameArtifactRole role,
        long minimumSequence,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = ReadManifests(root)
                .Where(item => item.Manifest.Descriptor.Artifact.Role == role &&
                    item.Manifest.Descriptor.Capture.CaptureSequence > minimumSequence)
                .OrderByDescending(item => item.Manifest.Descriptor.Capture.CaptureSequence)
                .FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
        Assert.Fail($"Timed out waiting for a {role} manifest after sequence {minimumSequence}.");
        throw new InvalidOperationException();
    }

    private static IEnumerable<ManifestObservation> ReadManifests(string root, string agentId = ExpectedAgentId)
    {
        var clearReferencePath = Path.Combine(root, "w6", "clear-reference.manifest.json");
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            if (string.Equals(path, clearReferencePath, StringComparison.Ordinal))
            {
                continue;
            }
            ArtifactManifestParseResult parsed;
            try
            {
                parsed = CaptureContractJson.ParseManifest(File.ReadAllBytes(path));
            }
            catch (IOException)
            {
                continue;
            }
            if (parsed.IsValid &&
                parsed.Document?.Manifest is { } manifest &&
                manifest.Descriptor.Capture.AgentId == agentId &&
                manifest.Descriptor.Profiles.Processing.Name == "processing")
            {
                yield return new ManifestObservation(path, manifest);
            }
        }
    }

    private static long ReadMaximumRawSequence(string root, string agentId = ExpectedAgentId)
        => ReadManifests(root, agentId)
            .Where(static item => item.Manifest.Descriptor.Artifact.Role == FrameArtifactRole.Raw)
            .Select(static item => item.Manifest.Descriptor.Capture.CaptureSequence)
            .DefaultIfEmpty(0)
            .Max();

    private static async Task<IReadOnlyList<CameraAgentGalleryCapture>> WaitForCompleteCapturesAsync(
        HttpClient client,
        int count,
        long minimumSequence,
        TimeSpan timeout,
        IReadOnlyList<FrameArtifactRole>? expectedRoles = null,
        int expectedNodeCount = 10)
    {
        expectedRoles ??= ExpectedRoles;
        var captures = new Dictionary<long, CameraAgentGalleryCapture>();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (captures.Count < count && DateTimeOffset.UtcNow < deadline)
        {
            var page = await client.GetFromJsonAsync<CameraAgentGalleryPage>(
                $"/api/v1/operations/gallery/?pageSize=100&minimumSequence={minimumSequence + 1}&evidenceOrigin=Simulated")
                .ConfigureAwait(false);
            Assert.IsNotNull(page);
            foreach (var candidate in page.Items.OrderBy(static item => item.CaptureSequence))
            {
                if (captures.ContainsKey(candidate.CaptureSequence) ||
                    !expectedRoles.All(role => candidate.Artifacts.Any(artifact => artifact.Role == role)) ||
                    candidate.ProcessingNodes.Count != expectedNodeCount ||
                    candidate.ProcessingNodes.Any(static node => node.Status != "Completed"))
                {
                    continue;
                }
                var detail = await client.GetFromJsonAsync<CameraAgentGalleryCapture>(
                    $"/api/v1/operations/gallery/{candidate.CaptureId:D}").ConfigureAwait(false);
                if (detail?.Detail is { EvidenceAvailability: "Available" } &&
                    detail.ProcessingNodes.Count == expectedNodeCount &&
                    detail.ProcessingNodes.All(static node => node.Status == "Completed"))
                {
                    captures[candidate.CaptureSequence] = detail;
                }
            }
            if (captures.Count < count)
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
        Assert.IsGreaterThanOrEqualTo(count, captures.Count,
            $"Timed out waiting for {count} complete captures after sequence {minimumSequence}.");
        return captures.Values.OrderBy(static capture => capture.CaptureSequence).Take(count).ToArray();
    }

    private static List<CaptureProvenanceEvidence> AssertCaptureContracts(
        string root,
        CameraAgentGalleryCapture[] captures,
        DeploymentLocationSnapshot expectedDeploymentLocation)
    {
        var manifests = ReadManifests(root)
            .GroupBy(static item => item.Manifest.Descriptor.Artifact.ArtifactId)
            .ToDictionary(static group => group.Key, static group => group.First().Manifest);
        var productManifests = ReadProductManifests(root, ExpectedAgentId)
            .GroupBy(static item => item.Manifest.Artifact.ArtifactId)
            .ToDictionary(static group => group.Key, static group => group.First().Manifest);
        var provenance = new List<CaptureProvenanceEvidence>(captures.Length);
        foreach (var capture in captures)
        {
            Assert.HasCount(10, capture.ProcessingNodes);
            foreach (var artifact in capture.Artifacts)
            {
                if (manifests.TryGetValue(artifact.ArtifactId, out var manifest))
                {
                    var descriptor = manifest.Descriptor;
                    Assert.AreEqual(ExpectedRigSha256, descriptor.Profiles.Rig.Sha256);
                    Assert.AreEqual(ExpectedProcessingSha256, descriptor.Profiles.Processing.Sha256);
                    var payload = File.ReadAllBytes(Path.Combine(root, manifest.RelativeArtifactPath));
                    Assert.AreEqual(descriptor.Artifact.ChecksumSha256, Convert.ToHexString(SHA256.HashData(payload)));
                    Assert.AreEqual(descriptor.Layout.ByteLength, payload.LongLength);
                    CollectionAssert.AreEqual(
                        artifact.SourceArtifactIds.ToArray(),
                        descriptor.Artifact.SourceArtifactIds.ToArray());
                    if (descriptor.Artifact.Role == FrameArtifactRole.Raw && capture == captures[0])
                    {
                        AssertRightAlignedTwelveBit(payload);
                    }
                }
                else
                {
                    Assert.IsTrue(productManifests.TryGetValue(artifact.ArtifactId, out var product));
                    Assert.AreEqual(capture.CaptureId, product.Capture.CaptureId);
                    var payload = File.ReadAllBytes(Path.Combine(root, product.RelativeArtifactPath));
                    Assert.AreEqual(product.Artifact.ChecksumSha256, Convert.ToHexString(SHA256.HashData(payload)));
                    Assert.AreEqual(product.ByteLength, payload.LongLength);
                    CollectionAssert.AreEqual(
                        artifact.SourceArtifactIds.ToArray(),
                        product.Artifact.SourceArtifactIds.ToArray());
                }
            }
            var raw = manifests.Values.Single(item =>
                item.Descriptor.Capture.CaptureId == capture.CaptureId &&
                item.Descriptor.Artifact.Role == FrameArtifactRole.Raw);
            Assert.AreEqual(3552, raw.Descriptor.Layout.Width);
            Assert.AreEqual(3552, raw.Descriptor.Layout.Height);
            Assert.AreEqual(25_233_408L, raw.Descriptor.Layout.ByteLength);
            Assert.AreEqual(CameraPixelFormat.BayerRggb16, raw.Descriptor.Layout.PixelFormat);
            var sceneObjects = raw.Scene?.Objects
                ?? throw new InvalidDataException("The measured W6 projected scene is missing.");
            var segmentEndpointIds = (raw.Scene.Segments ?? [])
                .SelectMany(static segment => new[] { segment.FromObjectId, segment.ToObjectId })
                .ToHashSet(StringComparer.Ordinal);
            var nonEndpointObjects = sceneObjects.Where(item => !segmentEndpointIds.Contains(item.Id)).ToArray();
            Assert.IsLessThanOrEqualTo(300, nonEndpointObjects.Length);
            Assert.IsTrue(sceneObjects.All(static item => item.Magnitude <= 6.5));
            Assert.IsLessThanOrEqualTo(300 + segmentEndpointIds.Count, sceneObjects.Count);
            Assert.IsTrue(ExpectedCatalogRows.All(expected => sceneObjects.Any(item => item.Id == expected)));
            var admission = raw.Descriptor.CycleEvidence?.ScheduleAdmission
                ?? throw new InvalidDataException("The measured W6 schedule admission evidence is missing.");
            Assert.AreEqual(ExpectedLocalProfileSha256, admission.LocalProfileSha256);
            Assert.AreEqual(ExpectedScheduleSha256, admission.ScheduleRevisionSha256);
            Assert.AreEqual(expectedDeploymentLocation.LocationId, admission.DeploymentLocationId);
            Assert.AreEqual(expectedDeploymentLocation.Version, admission.DeploymentLocationVersion);
            Assert.AreEqual(expectedDeploymentLocation.ToProvenance(), raw.Descriptor.Location);
            provenance.Add(new CaptureProvenanceEvidence(
                capture.CaptureId,
                capture.CaptureSequence,
                admission.LocalProfileSha256,
                admission.ScheduleRevisionId,
                admission.ScheduleRevisionSha256,
                admission.DeploymentLocationId,
                admission.DeploymentLocationVersion,
                raw.Descriptor.Location!));
        }
        return provenance;
    }

    private static void AssertRightAlignedTwelveBit(ReadOnlySpan<byte> payload)
    {
        Assert.AreEqual(0, payload.Length % 2);
        for (var offset = 0; offset < payload.Length; offset += 2)
        {
            Assert.IsLessThanOrEqualTo(4095, ReadSample(payload, offset));
        }
    }

    private static async Task<GeometryEvidence> AssertRepresentativeGeometryAsync(
        string root,
        IReadOnlyCollection<CameraAgentGalleryCapture> measuredCaptures)
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "hualapai-asi676-w6-conformance-v1.json")).ConfigureAwait(false));
        var fixtureRoot = fixture.RootElement;
        Assert.AreEqual(1, fixtureRoot.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("hualapai-asi676-w6-conformance-v1", fixtureRoot.GetProperty("fixtureId").GetString());
        Assert.AreEqual("Astropy FK5/ERFA rounded independent reference",
            fixtureRoot.GetProperty("referenceModel").GetString());
        var observer = fixtureRoot.GetProperty("observer");
        Assert.AreEqual(35.5599378, observer.GetProperty("latitudeDegrees").GetDouble());
        Assert.AreEqual(-113.9119818, observer.GetProperty("longitudeDegrees").GetDouble());
        Assert.AreEqual(520, observer.GetProperty("elevationMeters").GetInt32());
        Assert.AreEqual("0B3F2F069338CBD420FF214926E97760A9B59EEACB444F0B8F08149DF698DF6E",
            observer.GetProperty("identitySha256").GetString());
        var fixtureProjection = fixtureRoot.GetProperty("projection");
        Assert.AreEqual("EquidistantFisheye", fixtureProjection.GetProperty("model").GetString());
        Assert.AreEqual(3552, fixtureProjection.GetProperty("width").GetInt32());
        Assert.AreEqual(3552, fixtureProjection.GetProperty("height").GetInt32());
        Assert.AreEqual(ExpectedRigSha256, fixtureProjection.GetProperty("rigSha256").GetString());
        var tolerances = fixtureRoot.GetProperty("tolerances");
        var projectedTolerance = tolerances.GetProperty("projectedSensorPixels").GetDouble();
        var centroidTolerance = tolerances.GetProperty("rawCentroidPixels").GetDouble();
        var markerTolerance = tolerances.GetProperty("renderedMarkerPixels").GetDouble();
        var cardinalTolerance = tolerances.GetProperty("cardinalSensorPixels").GetDouble();
        var fixtureSceneUtc = fixtureRoot.GetProperty("sceneUtc").GetDateTimeOffset();
        var measuredCaptureIds = measuredCaptures.Select(static capture => capture.CaptureId).ToHashSet();
        var raw = ReadManifests(root).Single(item =>
            item.Manifest.Descriptor.Artifact.Role == FrameArtifactRole.Raw &&
            measuredCaptureIds.Contains(item.Manifest.Descriptor.Capture.CaptureId) &&
            item.Manifest.Scene?.SceneUtc == fixtureSceneUtc);
        var captureId = raw.Manifest.Descriptor.Capture.CaptureId;
        var scene = raw.Manifest.Scene ?? throw new InvalidDataException("The W6 scene is missing.");
        var sceneUtc = scene.SceneUtc
            ?? throw new InvalidDataException("The W6 scene UTC is missing.");
        var rawPayload = await File.ReadAllBytesAsync(Path.Combine(root, raw.Manifest.RelativeArtifactPath)).ConfigureAwait(false);
        var annotated = ReadManifests(root).Single(item =>
            item.Manifest.Descriptor.Capture.CaptureId == captureId &&
            item.Manifest.Descriptor.Artifact.Role == FrameArtifactRole.AnnotatedPreview &&
            item.Manifest.Descriptor.Artifact.Variant == "w6-annotated");
        var combinedPreview = ReadManifests(root).Single(item =>
            item.Manifest.Descriptor.Capture.CaptureId == captureId &&
            item.Manifest.Descriptor.Artifact.Role == FrameArtifactRole.Preview &&
            item.Manifest.Descriptor.Artifact.Variant == "combined-preview");
        var annotatedPayload = await File.ReadAllBytesAsync(
            Path.Combine(root, annotated.Manifest.RelativeArtifactPath)).ConfigureAwait(false);
        var previewPayload = await File.ReadAllBytesAsync(
            Path.Combine(root, combinedPreview.Manifest.RelativeArtifactPath)).ConfigureAwait(false);
        var observations = new List<GeometryObjectEvidence>();
        foreach (var expected in fixtureRoot.GetProperty("objects").EnumerateArray())
        {
            var rowId = expected.GetProperty("rowId").GetString()!;
            var projected = scene.Objects!.Single(item => item.Id == rowId);
            Assert.AreEqual(expected.GetProperty("name").GetString(), projected.DisplayName);
            var expectedPixel = expected.GetProperty("expectedPixel");
            var expectedX = expectedPixel.GetProperty("x").GetDouble();
            var expectedY = expectedPixel.GetProperty("y").GetDouble();
            var projectedError = Distance(projected.PixelX, projected.PixelY, expectedX, expectedY);
            Assert.IsLessThanOrEqualTo(projectedTolerance, projectedError, rowId);
            var centroid = CalculateRawCentroid(
                rawPayload,
                raw.Manifest.Descriptor.Layout.StrideBytes,
                raw.Manifest.Descriptor.Layout.Width,
                raw.Manifest.Descriptor.Layout.Height,
                expectedX,
                expectedY,
                radius: 6);
            var centroidError = Distance(centroid.X, centroid.Y, expectedX, expectedY);
            Assert.IsLessThanOrEqualTo(centroidTolerance, centroidError, rowId);
            var marker = FindPackedMarkerCenter(
                previewPayload,
                annotatedPayload,
                annotated.Manifest.Descriptor.Layout.Width,
                annotated.Manifest.Descriptor.Layout.Height,
                expectedX,
                expectedY,
                radius: 6);
            var markerError = Distance(marker.X, marker.Y, expectedX, expectedY);
            Assert.IsGreaterThan(4, marker.ChangedPoints, rowId);
            Assert.IsLessThanOrEqualTo(markerTolerance, markerError, rowId);
            observations.Add(new GeometryObjectEvidence(
                rowId,
                expectedX,
                expectedY,
                projected.PixelX,
                projected.PixelY,
                projectedError,
                centroid.X,
                centroid.Y,
                centroidError,
                marker.X,
                marker.Y,
                markerError,
                marker.ChangedPoints));
        }

        var configuration = await LoadW6ConfigurationAsync().ConfigureAwait(false);
        var landmarks = RigProjectionContextFactory.CreateAnnotationLandmarks(
            RigProjectionContextFactory.Create(configuration.Rig));
        Assert.IsNotNull(landmarks);
        var expectedLandmarks = fixtureRoot.GetProperty("cardinalLandmarks");
        AssertPoint(landmarks.Center, expectedLandmarks.GetProperty("center"), cardinalTolerance, "center");
        Assert.AreEqual(expectedLandmarks.GetProperty("radius").GetDouble(), landmarks.ImageCircleRadius, cardinalTolerance);
        AssertPoint(landmarks.North, expectedLandmarks.GetProperty("north"), cardinalTolerance, "north");
        AssertPoint(landmarks.East, expectedLandmarks.GetProperty("east"), cardinalTolerance, "east");
        AssertPoint(landmarks.South, expectedLandmarks.GetProperty("south"), cardinalTolerance, "south");
        AssertPoint(landmarks.West, expectedLandmarks.GetProperty("west"), cardinalTolerance, "west");
        var fixtureLandmarks = new ProjectionAnnotationLandmarks(
            ReadPoint(expectedLandmarks.GetProperty("center")),
            expectedLandmarks.GetProperty("radius").GetDouble(),
            ReadPoint(expectedLandmarks.GetProperty("north")),
            ReadPoint(expectedLandmarks.GetProperty("east")),
            ReadPoint(expectedLandmarks.GetProperty("south")),
            ReadPoint(expectedLandmarks.GetProperty("west")));
        var renderedLandmarks = new Dictionary<string, PackedMarkerObservation>(StringComparer.Ordinal)
        {
            ["center"] = CalculatePackedChangedCentroidAtValue(previewPayload, annotatedPayload,
                annotated.Manifest.Descriptor.Layout, fixtureLandmarks.Center, 8, 255),
            ["north"] = CalculatePackedChangedCentroidAtValue(previewPayload, annotatedPayload,
                annotated.Manifest.Descriptor.Layout, fixtureLandmarks.North, 8, 255),
            ["east"] = CalculatePackedChangedCentroidAtValue(previewPayload, annotatedPayload,
                annotated.Manifest.Descriptor.Layout, fixtureLandmarks.East, 8, 255),
            ["south"] = CalculatePackedChangedCentroidAtValue(previewPayload, annotatedPayload,
                annotated.Manifest.Descriptor.Layout, fixtureLandmarks.South, 8, 255),
            ["west"] = CalculatePackedChangedCentroidAtValue(previewPayload, annotatedPayload,
                annotated.Manifest.Descriptor.Layout, fixtureLandmarks.West, 8, 255)
        };
        Assert.IsTrue(renderedLandmarks.Where(static item => item.Key != "center").All(item =>
            item.Value.ChangedPoints > 0 && Distance(
                item.Value.X,
                item.Value.Y,
                item.Key switch
                {
                    "north" => fixtureLandmarks.North.X,
                    "east" => fixtureLandmarks.East.X,
                    "south" => fixtureLandmarks.South.X,
                    _ => fixtureLandmarks.West.X
                },
                item.Key switch
                {
                    "north" => fixtureLandmarks.North.Y,
                    "east" => fixtureLandmarks.East.Y,
                    "south" => fixtureLandmarks.South.Y,
                    _ => fixtureLandmarks.West.Y
                }) <= markerTolerance),
            string.Join(", ", renderedLandmarks.Select(static item => $"{item.Key}={item.Value.ChangedPoints}")));
        var cloud = ReadProductManifests(root, ExpectedAgentId).Single(item =>
            item.Manifest.Capture.CaptureId == captureId &&
            item.Manifest.Artifact.Role == FrameArtifactRole.Metadata &&
            item.Manifest.Artifact.Variant == "cloud-assessment-v1");
        var cloudPayload = await File.ReadAllBytesAsync(Path.Combine(root, cloud.Manifest.RelativeArtifactPath))
            .ConfigureAwait(false);
        var cloudAssessment = CloudAssessmentJson.Parse(cloudPayload);
        Assert.IsTrue(cloudAssessment.Validation.IsValid, cloudAssessment.Validation.ReasonCode);
        var assessment = cloudAssessment.Assessment!;
        Assert.AreEqual(CloudAssessmentStatus.Quantified, assessment.Status);
        Assert.AreEqual(CloudAssessmentQuality.Degraded, assessment.Quality);
        CollectionAssert.AreEqual(new[] { CloudAssessmentReasonCodes.EnvironmentMissing },
            assessment.ReasonCodes.ToArray());
        var coverageMillionths = assessment.CoverageMillionths
            ?? throw new InvalidDataException("The quantified cloud assessment omitted coverage.");
        Assert.IsGreaterThan(0, coverageMillionths);
        Assert.IsLessThan(1_000_000, coverageMillionths);
        Assert.AreEqual(16, assessment.Grid.Columns);
        Assert.AreEqual(16, assessment.Grid.Rows);
        Assert.IsGreaterThan(0, assessment.Grid.CloudyRegionCount);
        Assert.IsNotNull(assessment.Mask);
        Assert.IsNotNull(assessment.ClearReference);
        Assert.IsNull(assessment.Environment.SolarRegime);
        Assert.AreEqual(EnvironmentalObservationMatchStatus.Fresh, assessment.Environment.PrecipitationStatus);
        Assert.IsFalse(assessment.Environment.PrecipitationDetected);
        Assert.IsNotNull(assessment.Environment.PrecipitationObservationId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(assessment.Environment.PrecipitationContentSha256));
        var weatherOverlay = ReadManifests(root).Single(item =>
            item.Manifest.Descriptor.Capture.CaptureId == captureId &&
            item.Manifest.Descriptor.Artifact.Role == FrameArtifactRole.AnnotatedPreview &&
            item.Manifest.Descriptor.Artifact.Variant == "w6-weather-overlay");
        var weatherOverlayPayload = await File.ReadAllBytesAsync(
            Path.Combine(root, weatherOverlay.Manifest.RelativeArtifactPath)).ConfigureAwait(false);
        Assert.AreEqual(annotatedPayload.Length, weatherOverlayPayload.Length);
        var weatherOverlayChangedPixels = CountPackedPixelDifferences(annotatedPayload, weatherOverlayPayload);
        Assert.IsGreaterThan(0, weatherOverlayChangedPixels);
        var weatherOverlayCorrespondence = AssertCloudOverlayCorrespondence(
            annotatedPayload,
            weatherOverlayPayload,
            weatherOverlay.Manifest.Descriptor.Layout,
            assessment);
        return new GeometryEvidence(
            fixtureRoot.GetProperty("fixtureId").GetString()!,
            fixtureRoot.GetProperty("referenceModel").GetString()!,
            sceneUtc,
            observations,
            fixtureLandmarks,
            renderedLandmarks,
            weatherOverlayChangedPixels,
            weatherOverlayCorrespondence);
    }

    private static CloudOverlayCorrespondenceEvidence AssertCloudOverlayCorrespondence(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> overlay,
        FrameLayoutDescriptor layout,
        CloudAssessmentV1 assessment)
    {
        Assert.AreEqual(CameraPixelFormat.Rgb24, layout.PixelFormat);
        Assert.AreEqual(checked(layout.Width * 3), layout.StrideBytes);
        var mask = assessment.Mask ?? throw new InvalidDataException("The cloud mask is missing.");
        Assert.AreEqual(assessment.Grid.Columns, mask.Width);
        Assert.AreEqual(assessment.Grid.Rows, mask.Height);
        var cloudyTiles = 0;
        var expectedBorderPixels = new HashSet<int>();
        for (var row = 0; row < assessment.Grid.Rows; row++)
        {
            for (var column = 0; column < assessment.Grid.Columns; column++)
            {
                var index = row * assessment.Grid.Columns + column;
                if ((mask.Bits.Span[index >> 3] & 1 << (index & 7)) == 0)
                {
                    continue;
                }
                cloudyTiles++;
                var x0 = (int)((long)column * layout.Width / assessment.Grid.Columns);
                var x1 = (int)((long)(column + 1) * layout.Width / assessment.Grid.Columns) - 1;
                var y0 = (int)((long)row * layout.Height / assessment.Grid.Rows);
                var y1 = (int)((long)(row + 1) * layout.Height / assessment.Grid.Rows) - 1;
                var tileMatches = 0;
                for (var x = x0; x <= x1; x++)
                {
                    expectedBorderPixels.Add(y0 * layout.Width + x);
                    expectedBorderPixels.Add(y1 * layout.Width + x);
                    tileMatches += IsCloudBorderPixel(source, overlay, layout, x, y0) ? 1 : 0;
                    tileMatches += IsCloudBorderPixel(source, overlay, layout, x, y1) ? 1 : 0;
                }
                for (var y = y0 + 1; y < y1; y++)
                {
                    expectedBorderPixels.Add(y * layout.Width + x0);
                    expectedBorderPixels.Add(y * layout.Width + x1);
                    tileMatches += IsCloudBorderPixel(source, overlay, layout, x0, y) ? 1 : 0;
                    tileMatches += IsCloudBorderPixel(source, overlay, layout, x1, y) ? 1 : 0;
                }
                Assert.IsGreaterThan(0, tileMatches, $"Cloudy tile ({column}, {row}) has no rendered border pixels.");
            }
        }
        Assert.AreEqual(assessment.Grid.CloudyRegionCount, cloudyTiles);
        var matchedBorderPixels = 0L;
        var unexpectedBorderPixels = 0L;
        for (var y = 0; y < layout.Height; y++)
        {
            for (var x = 0; x < layout.Width; x++)
            {
                if (!IsCloudBorderPixel(source, overlay, layout, x, y))
                {
                    continue;
                }
                matchedBorderPixels++;
                if (!expectedBorderPixels.Contains(y * layout.Width + x))
                {
                    unexpectedBorderPixels++;
                }
            }
        }
        Assert.IsGreaterThan(0, matchedBorderPixels);
        Assert.AreEqual(0, unexpectedBorderPixels);
        return new CloudOverlayCorrespondenceEvidence(cloudyTiles, matchedBorderPixels, unexpectedBorderPixels);
    }

    private static bool IsCloudBorderPixel(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> overlay,
        FrameLayoutDescriptor layout,
        int x,
        int y)
    {
        var offset = y * layout.StrideBytes + x * 3;
        return (source[offset] != overlay[offset] || source[offset + 1] != overlay[offset + 1] ||
                source[offset + 2] != overlay[offset + 2]) &&
            overlay[offset] == byte.MaxValue && overlay[offset + 1] == 64 && overlay[offset + 2] == 32;
    }

    private static long CountPackedPixelDifferences(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        Assert.AreEqual(left.Length, right.Length);
        Assert.AreEqual(0, left.Length % 3);
        var changed = 0L;
        for (var offset = 0; offset < left.Length; offset += 3)
        {
            if (left[offset] != right[offset] || left[offset + 1] != right[offset + 1] ||
                left[offset + 2] != right[offset + 2])
            {
                changed++;
            }
        }
        return changed;
    }

    private static PackedMarkerObservation CalculatePackedChangedCentroidAtValue(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> annotated,
        FrameLayoutDescriptor layout,
        PixelPoint point,
        int radius,
        byte expectedValue)
    {
        var centerX = (int)Math.Round(point.X, MidpointRounding.AwayFromZero);
        var centerY = (int)Math.Round(point.Y, MidpointRounding.AwayFromZero);
        var changed = 0;
        var totalX = 0d;
        var totalY = 0d;
        for (var y = Math.Max(0, centerY - radius); y <= Math.Min(layout.Height - 1, centerY + radius); y++)
        {
            for (var x = Math.Max(0, centerX - radius); x <= Math.Min(layout.Width - 1, centerX + radius); x++)
            {
                var offset = checked((y * layout.Width + x) * 3);
                if ((annotated[offset] != source[offset] || annotated[offset + 1] != source[offset + 1] ||
                    annotated[offset + 2] != source[offset + 2]) &&
                    annotated[offset] == expectedValue && annotated[offset + 1] == expectedValue &&
                    annotated[offset + 2] == expectedValue)
                {
                    changed++;
                    totalX += x;
                    totalY += y;
                }
            }
        }
        return changed == 0
            ? new PackedMarkerObservation(point.X, point.Y, 0)
            : new PackedMarkerObservation(totalX / changed, totalY / changed, changed);
    }

    private static PixelPoint ReadPoint(JsonElement value)
        => new(value.GetProperty("x").GetDouble(), value.GetProperty("y").GetDouble());

    private static async Task<CameraModuleConfig> LoadW6ConfigurationAsync()
    {
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.standalone-w6.json"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            Observatory = new ObservatoryLocation(35.5599378, -113.9119818, 520, "America/Phoenix"),
            DeploymentLocation = new DeploymentLocationOptions
            {
                LocationId = "hualapai-cameraagent-standalone-full",
                Source = "issue-211-operator-pinned-w6-configuration",
                SourceKind = DeploymentLocationSourceKind.Manual,
                EffectiveFromUtc = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
            }
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        return await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static PixelPoint CalculateRawCentroid(
        ReadOnlySpan<byte> pixels,
        int strideBytes,
        int width,
        int height,
        double sourceX,
        double sourceY,
        int radius)
    {
        var centerX = (int)Math.Round(sourceX, MidpointRounding.AwayFromZero);
        var centerY = (int)Math.Round(sourceY, MidpointRounding.AwayFromZero);
        var minimum = ushort.MaxValue;
        for (var y = Math.Max(0, centerY - radius); y <= Math.Min(height - 1, centerY + radius); y++)
        {
            for (var x = Math.Max(0, centerX - radius); x <= Math.Min(width - 1, centerX + radius); x++)
            {
                minimum = Math.Min(minimum, ReadRawSample(pixels, strideBytes, x, y));
            }
        }
        double total = 0;
        double weightedX = 0;
        double weightedY = 0;
        for (var y = Math.Max(0, centerY - radius); y <= Math.Min(height - 1, centerY + radius); y++)
        {
            for (var x = Math.Max(0, centerX - radius); x <= Math.Min(width - 1, centerX + radius); x++)
            {
                var weight = ReadRawSample(pixels, strideBytes, x, y) - minimum;
                total += weight;
                weightedX += (x + 0.5) * weight;
                weightedY += (y + 0.5) * weight;
            }
        }
        Assert.IsGreaterThan(0, total);
        return new PixelPoint(weightedX / total, weightedY / total);
    }

    private static ushort ReadRawSample(ReadOnlySpan<byte> pixels, int strideBytes, int x, int y)
    {
        var offset = checked(y * strideBytes + x * 2);
        return (ushort)(pixels[offset] | pixels[offset + 1] << 8);
    }

    private static PackedMarkerObservation FindPackedMarkerCenter(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> annotated,
        int width,
        int height,
        double expectedX,
        double expectedY,
        int radius)
    {
        var expectedCenterX = (int)Math.Round(expectedX, MidpointRounding.AwayFromZero);
        var expectedCenterY = (int)Math.Round(expectedY, MidpointRounding.AwayFromZero);
        var best = new PackedMarkerObservation(expectedCenterX, expectedCenterY, -1);
        for (var centerY = expectedCenterY - 2; centerY <= expectedCenterY + 2; centerY++)
        {
            for (var centerX = expectedCenterX - 2; centerX <= expectedCenterX + 2; centerX++)
            {
                var points = new HashSet<(int X, int Y)>();
                var pointCount = Math.Max(8, (int)Math.Round(Math.PI * radius));
                for (var point = 0; point < pointCount; point++)
                {
                    var angle = 2 * Math.PI * point / pointCount;
                    points.Add((
                        centerX + (int)Math.Round(radius * Math.Cos(angle), MidpointRounding.AwayFromZero),
                        centerY + (int)Math.Round(radius * Math.Sin(angle), MidpointRounding.AwayFromZero)));
                }
                var changed = 0;
                foreach (var point in points)
                {
                    if ((uint)point.X >= (uint)width || (uint)point.Y >= (uint)height)
                    {
                        continue;
                    }
                    var offset = checked((point.Y * width + point.X) * 3);
                    if (annotated[offset] > source[offset] ||
                        annotated[offset + 1] > source[offset + 1] ||
                        annotated[offset + 2] > source[offset + 2])
                    {
                        changed++;
                    }
                }
                if (changed > best.ChangedPoints)
                {
                    best = new PackedMarkerObservation(centerX, centerY, changed);
                }
            }
        }
        return best;
    }

    private static void AssertPoint(PixelPoint actual, JsonElement expected, double tolerance, string name)
    {
        var error = Distance(
            actual.X,
            actual.Y,
            expected.GetProperty("x").GetDouble(),
            expected.GetProperty("y").GetDouble());
        Assert.IsLessThanOrEqualTo(tolerance, error, name);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
        => Math.Sqrt(Math.Pow(x1 - x2, 2) + Math.Pow(y1 - y2, 2));

    private static PipelinePerformanceEvidence ReadPipelinePerformance(
        string root,
        CameraAgentGalleryCapture[] captures)
    {
        var manifests = ReadManifests(root)
            .Select(static item => item.Manifest)
            .Where(static manifest => manifest.Descriptor.Artifact.Role == FrameArtifactRole.Raw)
            .ToDictionary(static manifest => manifest.Descriptor.Capture.CaptureId);
        var captureIds = captures.Select(static capture => capture.CaptureId).ToHashSet();
        var nodesByCapture = new Dictionary<Guid, List<ProcessingNodeTiming>>();
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT capture_id, node_id, started_unix_ms, completed_unix_ms, duration_ticks
                FROM processing_nodes
                WHERE completed_unix_ms IS NOT NULL AND duration_ticks IS NOT NULL
                ORDER BY started_unix_ms, node_id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var captureId = Guid.Parse(reader.GetString(0));
                if (!captureIds.Contains(captureId))
                {
                    continue;
                }
                if (!nodesByCapture.TryGetValue(captureId, out var nodes))
                {
                    nodes = [];
                    nodesByCapture[captureId] = nodes;
                }
                nodes.Add(new ProcessingNodeTiming(
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    TimeSpan.FromTicks(reader.GetInt64(4)).TotalMilliseconds));
            }
        }

        var observations = new List<CapturePerformanceObservation>(captures.Length);
        foreach (var capture in captures.OrderBy(static capture => capture.CaptureSequence))
        {
            Assert.IsTrue(manifests.TryGetValue(capture.CaptureId, out var manifest));
            Assert.IsTrue(nodesByCapture.TryGetValue(capture.CaptureId, out var nodes));
            Assert.HasCount(10, nodes);
            var evidence = manifest.Descriptor.CycleEvidence!;
            var moduleDuration = evidence.Decision.StartedUtc - evidence.ModuleCallStartedUtc;
            var graphStartedUtc = DateTimeOffset.FromUnixTimeMilliseconds(nodes.Min(static node => node.StartedUnixMs));
            var graphCompletedUtc = DateTimeOffset.FromUnixTimeMilliseconds(nodes.Max(static node => node.CompletedUnixMs));
            var graphDuration = graphCompletedUtc - graphStartedUtc;
            var ingressDuration = manifest.Descriptor.Timing.DurableIngressUtc - evidence.IngressHandoffStartedUtc;
            observations.Add(new CapturePerformanceObservation(
                capture.CaptureSequence,
                evidence.ModuleCallStartedUtc,
                moduleDuration.TotalMilliseconds,
                (moduleDuration - manifest.Descriptor.Controls.EffectiveExposure).TotalMilliseconds,
                ingressDuration.TotalMilliseconds,
                graphDuration.TotalMilliseconds,
                (graphCompletedUtc - evidence.ModuleCallStartedUtc).TotalMilliseconds));
        }

        var intervals = observations.Zip(observations.Skip(1), static (left, right) =>
            (right.ModuleCallStartedUtc - left.ModuleCallStartedUtc).TotalMilliseconds).ToArray();
        var cadenceHeadroom = observations.Zip(observations.Skip(1), static (current, next) =>
            (next.ModuleCallStartedUtc - current.ModuleCallStartedUtc).TotalMilliseconds -
            current.EndToEndDurationMilliseconds).ToArray();
        var nodeSummaries = nodesByCapture.Values
            .SelectMany(static nodes => nodes)
            .GroupBy(static node => node.NodeId, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => new ProcessingNodePerformanceEvidence(
                group.Key,
                Summarize(group.Select(static node => node.DurationMilliseconds))))
            .ToArray();
        var result = new PipelinePerformanceEvidence(
            observations,
            Summarize(intervals),
            Summarize(observations.Select(static item => item.ModuleDurationMilliseconds)),
            Summarize(observations.Select(static item => item.ModuleOverheadMilliseconds)),
            Summarize(observations.Select(static item => item.IngressDurationMilliseconds)),
            Summarize(observations.Select(static item => item.GraphDurationMilliseconds)),
            Summarize(observations.Select(static item => item.EndToEndDurationMilliseconds)),
            Summarize(cadenceHeadroom),
            nodeSummaries);
        Assert.IsLessThanOrEqualTo(12_000, result.StartIntervalMilliseconds.Maximum);
        Assert.IsLessThanOrEqualTo(180_000, result.GraphDurationMilliseconds.Maximum);
        Assert.IsLessThanOrEqualTo(180_000, result.EndToEndDurationMilliseconds.Maximum);
        return result;
    }

    private static DistributionSummary Summarize(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        Assert.IsNotEmpty(values);
        var middle = values.Length / 2;
        var median = values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
        return new DistributionSummary(
            values,
            values.Length,
            values[0],
            values.Average(),
            median,
            values.Length >= 30 ? values[(int)Math.Ceiling(values.Length * 0.95) - 1] : null,
            values[^1]);
    }

    private static CalibrationEvidence ReadCalibrationEvidence(string runtimeRoot)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(runtimeRoot, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        string bundleId;
        string bundleIdentitySha256;
        string profileIdentitySha256;
        string acquisitionModelIdentitySha256;
        string profileRelativePath;
        using (var bundle = connection.CreateCommand())
        {
            bundle.CommandText = """
                SELECT b.bundle_id, b.bundle_identity_sha256, b.profile_identity_sha256,
                       b.acquisition_model_identity_sha256, b.profile_relative_path
                FROM calibration_library_state s
                JOIN calibration_library_bundles b ON b.bundle_id = s.active_bundle_id
                WHERE s.state_key = 1 AND b.publication_state = 'published';
                """;
            using var reader = bundle.ExecuteReader();
            Assert.IsTrue(reader.Read());
            bundleId = reader.GetString(0);
            bundleIdentitySha256 = reader.GetString(1);
            profileIdentitySha256 = reader.GetString(2);
            acquisitionModelIdentitySha256 = reader.GetString(3);
            profileRelativePath = reader.GetString(4);
            Assert.IsFalse(reader.Read());
        }
        Assert.IsTrue(File.Exists(Path.Combine(runtimeRoot, profileRelativePath)));
        var artifacts = new List<CalibrationArtifactEvidence>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT artifact_id, reference_kind, role, source_index,
                       manifest_relative_path, manifest_sha256, payload_relative_path,
                       payload_sha256, ordered_source_artifact_ids_json
                FROM calibration_library_artifacts
                WHERE bundle_id = $bundle
                ORDER BY ordinal;
                """;
            command.Parameters.AddWithValue("$bundle", bundleId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var manifestPath = Path.Combine(runtimeRoot, reader.GetString(4));
                var payloadPath = Path.Combine(runtimeRoot, reader.GetString(6));
                var manifestSha256 = reader.GetString(5);
                var payloadSha256 = reader.GetString(7);
                Assert.AreEqual(manifestSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))));
                Assert.AreEqual(payloadSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(payloadPath))));
                artifacts.Add(new CalibrationArtifactEvidence(
                    Guid.ParseExact(reader.GetString(0), "N"),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    Path.GetRelativePath(runtimeRoot, manifestPath).Replace(Path.DirectorySeparatorChar, '/'),
                    manifestSha256,
                    Path.GetRelativePath(runtimeRoot, payloadPath).Replace(Path.DirectorySeparatorChar, '/'),
                    payloadSha256,
                    JsonSerializer.Deserialize<string[]>(reader.GetFieldValue<byte[]>(8), EvidenceJson) ?? []));
            }
        }
        Assert.HasCount(16, artifacts);
        Assert.AreEqual(12, artifacts.Count(static artifact => artifact.Role == "source"));
        Assert.AreEqual(4, artifacts.Count(static artifact => artifact.Role == "master"));
        return new CalibrationEvidence(
            bundleId,
            bundleIdentitySha256,
            profileIdentitySha256,
            acquisitionModelIdentitySha256,
            profileRelativePath,
            artifacts);
    }

    private static CalibrationPublicationSnapshot ReadPublishedCalibrationSnapshot(
        string runtimeRoot,
        bool requireActive)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(runtimeRoot, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        string bundleId;
        string bundleIdentitySha256;
        DateTimeOffset createdUtc;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT bundle.bundle_id, bundle.bundle_identity_sha256, bundle.created_unix_ms
                FROM calibration_library_bundles bundle
                LEFT JOIN calibration_library_state state
                  ON state.state_key = 1 AND state.active_bundle_id = bundle.bundle_id
                WHERE bundle.publication_state = 'published'
                  AND (($active = 1 AND state.active_bundle_id IS NOT NULL) OR
                       ($active = 0 AND state.active_bundle_id IS NULL))
                ORDER BY bundle.created_unix_ms DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$active", requireActive ? 1 : 0);
            using var reader = command.ExecuteReader();
            Assert.IsTrue(reader.Read());
            bundleId = reader.GetString(0);
            bundleIdentitySha256 = reader.GetString(1);
            createdUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
        }
        var artifactSha256 = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT manifest_sha256, payload_sha256
                FROM calibration_library_artifacts
                WHERE bundle_id = $bundle
                ORDER BY ordinal;
                """;
            command.Parameters.AddWithValue("$bundle", bundleId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                artifactSha256.Add(reader.GetString(0));
                artifactSha256.Add(reader.GetString(1));
            }
        }
        Assert.HasCount(32, artifactSha256);
        return new CalibrationPublicationSnapshot(
            bundleId,
            bundleIdentitySha256,
            artifactSha256.ToArray(),
            requireActive,
            createdUtc);
    }

    private static TransientEvidence RetainTransientEvidence(
        string runtimeRoot,
        string evidenceRoot,
        IReadOnlyCollection<CameraAgentGalleryCapture> measuredCaptures)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(runtimeRoot, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        Guid candidateId;
        Guid eventId;
        Guid centerCaptureId;
        long centerCaptureSequence;
        string state;
        string phase;
        string candidatePayloadSha256;
        string finalizationReceiptIdentitySha256;
        string scenarioId;
        DateTimeOffset scenarioEpochUtc;
        byte[] candidatePayload;
        byte[] finalizationPayload;
        long sourceCount;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.candidate_id, c.event_id, c.state, c.phase, c.candidate_payload,
                       c.candidate_payload_sha256, c.finalization_payload,
                       c.finalization_receipt_identity_sha256, r.capture_id, r.capture_sequence,
                       json_extract(r.manifest_json, '$.scene.transientScenario.scenarioId'),
                       json_extract(r.manifest_json, '$.scene.transientScenario.epochUtc'),
                       (SELECT COUNT(*) FROM transient_candidate_sources all_sources
                        WHERE all_sources.candidate_id = c.candidate_id)
                FROM transient_candidates c
                JOIN transient_candidate_sources center_source ON center_source.candidate_id = c.candidate_id
                  AND center_source.evidence_id = replace(json_extract(c.candidate_payload, '$.centerEvidenceId'), '-', '')
                JOIN raw_captures r ON r.raw_capture_row_id = center_source.raw_capture_row_id
                WHERE c.phase = 'finalized'
                ORDER BY r.capture_sequence, c.candidate_id
                LIMIT 1;
                """;
            using var reader = command.ExecuteReader();
            Assert.IsTrue(reader.Read());
            candidateId = Guid.ParseExact(reader.GetString(0), "N");
            eventId = Guid.ParseExact(reader.GetString(1), "N");
            state = reader.GetString(2);
            phase = reader.GetString(3);
            candidatePayload = reader.GetFieldValue<byte[]>(4);
            candidatePayloadSha256 = reader.GetString(5);
            finalizationPayload = reader.GetFieldValue<byte[]>(6);
            finalizationReceiptIdentitySha256 = reader.GetString(7);
            centerCaptureId = Guid.ParseExact(reader.GetString(8), "N");
            centerCaptureSequence = reader.GetInt64(9);
            scenarioId = reader.GetString(10);
            scenarioEpochUtc = DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture);
            sourceCount = reader.GetInt64(12);
        }
        Assert.AreEqual(candidatePayloadSha256, Convert.ToHexString(SHA256.HashData(candidatePayload)));
        Assert.AreEqual("needs_review", state);
        Assert.AreEqual("finalized", phase);
        Assert.AreEqual(3, sourceCount);
        Assert.AreEqual("scn-3D134B0F05F1C937E4D5B301", scenarioId);
        Assert.AreEqual(new DateTimeOffset(2026, 1, 15, 8, 0, 20, TimeSpan.Zero), scenarioEpochUtc);

        var measuredCaptureIds = measuredCaptures.Select(static capture => capture.CaptureId).ToHashSet();
        TransientInjectedEventEvidence? injectedEvent = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT capture_id, capture_sequence, raw_artifact_id, payload_sha256
                FROM raw_captures
                WHERE agent_id = $agent
                  AND json_extract(manifest_json, '$.scene.transientScenario.integrationStartUtc') =
                      json_extract(manifest_json, '$.scene.transientScenario.epochUtc')
                ORDER BY capture_sequence;
                """;
            command.Parameters.AddWithValue("$agent", ExpectedAgentId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var captureId = Guid.ParseExact(reader.GetString(0), "N");
                if (measuredCaptureIds.Contains(captureId))
                {
                    injectedEvent = new TransientInjectedEventEvidence(
                        captureId,
                        reader.GetInt64(1),
                        Guid.ParseExact(reader.GetString(2), "N"),
                        reader.GetString(3),
                        scenarioEpochUtc);
                    break;
                }
            }
        }
        Assert.IsNotNull(injectedEvent);

        var execution = ReadTransientExecution(connection, candidateId);
        Guid controlCaptureId;
        long controlCaptureSequence;
        Guid controlArtifactId;
        string controlChecksumSha256;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT r.capture_id, r.capture_sequence, r.raw_artifact_id, r.payload_sha256
                FROM transient_worker_frames frame
                JOIN raw_captures r ON r.raw_capture_row_id = frame.raw_capture_row_id
                WHERE frame.state = 'completed'
                  AND NOT EXISTS (
                      SELECT 1 FROM transient_worker_candidates candidate
                      WHERE candidate.target_raw_capture_row_id = frame.raw_capture_row_id)
                ORDER BY r.capture_sequence
                LIMIT 1;
                """;
            using var reader = command.ExecuteReader();
            Assert.IsTrue(reader.Read());
            controlCaptureId = Guid.ParseExact(reader.GetString(0), "N");
            controlCaptureSequence = reader.GetInt64(1);
            controlArtifactId = Guid.ParseExact(reader.GetString(2), "N");
            controlChecksumSha256 = reader.GetString(3);
        }

        var destination = Path.Combine(evidenceRoot, "transient");
        Directory.CreateDirectory(destination);
        var candidatePath = Path.Combine(destination, $"candidate-{candidateId:N}.json");
        var finalizationPath = Path.Combine(destination, $"finalization-{candidateId:N}.json");
        File.WriteAllBytes(candidatePath, candidatePayload);
        File.WriteAllBytes(finalizationPath, finalizationPayload);
        return new TransientEvidence(
            candidateId,
            eventId,
            state,
            phase,
            sourceCount,
            centerCaptureId,
            centerCaptureSequence,
            scenarioId,
            scenarioEpochUtc,
            injectedEvent,
            Path.GetRelativePath(evidenceRoot, candidatePath).Replace(Path.DirectorySeparatorChar, '/'),
            candidatePayloadSha256,
            Path.GetRelativePath(evidenceRoot, finalizationPath).Replace(Path.DirectorySeparatorChar, '/'),
            Convert.ToHexString(SHA256.HashData(finalizationPayload)),
            finalizationReceiptIdentitySha256,
            execution,
            new TransientNoCandidateEvidence(
                controlCaptureId,
                controlCaptureSequence,
                controlArtifactId,
                controlChecksumSha256,
                "completed"));
    }

    private static TransientExecutionEvidence ReadTransientExecution(SqliteConnection connection, Guid candidateId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT causal_extraction_json, causal_extraction_sha256,
                   observation_extraction_json, observation_extraction_sha256,
                   assessment_execution_json, assessment_execution_sha256,
                   state
            FROM transient_worker_candidates
            WHERE candidate_id = $candidate;
            """;
        command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        var causal = reader.GetFieldValue<byte[]>(0);
        var causalSha256 = reader.GetString(1);
        var observation = reader.GetFieldValue<byte[]>(2);
        var observationSha256 = reader.GetString(3);
        var assessment = reader.GetFieldValue<byte[]>(4);
        var assessmentSha256 = reader.GetString(5);
        Assert.AreEqual(causalSha256, Convert.ToHexString(SHA256.HashData(causal)));
        Assert.AreEqual(observationSha256, Convert.ToHexString(SHA256.HashData(observation)));
        Assert.AreEqual(assessmentSha256, Convert.ToHexString(SHA256.HashData(assessment)));
        Assert.AreEqual("completed", reader.GetString(6));
        var causalDescriptor = TransientCandidateExtractionJson.Parse(causal);
        var centeredDescriptor = TransientCandidateExtractionJson.Parse(observation);
        var assessmentDescriptor = TransientAssessmentJson.Parse(assessment);
        return new TransientExecutionEvidence(
            causalSha256,
            causalDescriptor.ExtractionIdentitySha256,
            observationSha256,
            centeredDescriptor.ExtractionIdentitySha256,
            assessmentSha256,
            assessmentDescriptor.ExecutionIdentitySha256);
    }

    private static async Task<CalibrationResidualEvidence> AssertCalibrationResidualsAsync(
        string runtimeRoot,
        CameraAgentGalleryCapture capture,
        CalibrationEvidence calibration)
    {
        var calibratedArtifactId = capture.Artifacts.Single(artifact =>
            artifact.Role == FrameArtifactRole.Calibrated).ArtifactId;
        var raw = ReadManifests(runtimeRoot).Single(item =>
            item.Manifest.Descriptor.Capture.CaptureId == capture.CaptureId &&
            item.Manifest.Descriptor.Artifact.Role == FrameArtifactRole.Raw);
        var corrected = ReadManifests(runtimeRoot).Single(item =>
            item.Manifest.Descriptor.Artifact.ArtifactId == calibratedArtifactId);
        var captureSequence = raw.Manifest.Descriptor.Capture.CaptureSequence;
        Assert.IsInRange(0L, 20L, captureSequence);

        var loader = new FileCameraAgentConfigurationLoader(
            Options.Create(new CameraAgentHostOptions
            {
                ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.standalone-w6.json"),
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
            }),
            NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        var configured = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var cleanOptions = JsonNode.Parse(configured.ModuleOptions!.Value.GetRawText())!.AsObject();
        Assert.IsNotNull(cleanOptions["virtualCalibration"]);
        cleanOptions.Remove("virtualCalibration");
        var cleanConfig = configured with
        {
            Module = new CameraModuleDescriptor(
                "VirtualSky",
                JsonSerializer.SerializeToElement(cleanOptions, EvidenceJson))
        };
        FileCameraAgentConfigurationLoader.ValidateConfig(cleanConfig);

        var catalogRoot = Environment.GetEnvironmentVariable("HVO_CATALOG_PERF_ROOT");
        Assert.IsFalse(string.IsNullOrWhiteSpace(catalogRoot));
        var catalog = CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(
            catalogRoot!, "hyg-v42-production"));
        Assert.AreEqual(ExpectedCatalogSha256, catalog.DatabaseSha256, ignoreCase: true);
        await using var module = new VirtualSkyCameraModule(
            new FixedUtcTimeProvider(raw.Manifest.Descriptor.Timing.RequestedStartUtc),
            catalog.Catalog,
            new ProjectedSceneStore(),
            StandardConstellationTopology.CreateD3Celestial(),
            new AstronomyEnginePlanetEphemeris());
        await module.InitializeAsync(cleanConfig, CancellationToken.None).ConfigureAwait(false);

        CaptureResult? clean = null;
        for (var sequence = 0L; sequence <= captureSequence; sequence++)
        {
            clean = await module.CaptureAsync(
                new CaptureRequest(
                    raw.Manifest.Descriptor.Timing.RequestedStartUtc,
                    cleanConfig.Rig.Pipeline.CaptureInterval,
                    CaptureMode.Still,
                    new CaptureSetpoint(
                        raw.Manifest.Descriptor.Controls.EffectiveExposure,
                        raw.Manifest.Descriptor.Controls.EffectiveGain,
                        null,
                        null)),
                CancellationToken.None).ConfigureAwait(false);
        }
        var cleanResult = clean ?? throw new InvalidDataException("The clean W6 replay did not produce a frame.");
        var cleanFrame = cleanResult.Frame
            ?? throw new InvalidDataException("The clean W6 replay returned no frame payload.");
        var normalizedClean = CalibrationMasterBuilder.Normalize(
            new CalibrationSourceFrame(cleanFrame.Layout!, cleanFrame.PixelData));
        var rawPayload = await File.ReadAllBytesAsync(
            Path.Combine(runtimeRoot, raw.Manifest.RelativeArtifactPath)).ConfigureAwait(false);
        var correctedPayload = await File.ReadAllBytesAsync(
            Path.Combine(runtimeRoot, corrected.Manifest.RelativeArtifactPath)).ConfigureAwait(false);
        var defectArtifact = calibration.Artifacts.Single(artifact =>
            artifact.ReferenceKind == "defect" && artifact.Role == "master");
        var defectMask = await File.ReadAllBytesAsync(
            Path.Combine(runtimeRoot, defectArtifact.PayloadRelativePath)).ConfigureAwait(false);
        var residuals = CalculateCalibrationResiduals(
            cleanFrame.Layout!,
            cleanFrame.PixelData.Span,
            rawPayload,
            normalizedClean.PixelData.Span,
            correctedPayload,
            defectMask);
        Assert.IsLessThan(residuals.RawMeanAbsoluteErrorNativeAdu, residuals.CorrectedMeanAbsoluteErrorNativeAdu);
        Assert.IsLessThanOrEqualTo(2d, residuals.CorrectedMeanAbsoluteErrorNativeAdu);
        Assert.IsLessThan(
            residuals.RawRepairableDefectMeanAbsoluteErrorNativeAdu,
            residuals.CorrectedRepairableDefectMeanAbsoluteErrorNativeAdu);
        Assert.IsLessThanOrEqualTo(2d, residuals.CorrectedRepairableDefectMeanAbsoluteErrorNativeAdu);
        foreach (var region in residuals.SpatialRegions)
        {
            Assert.IsLessThan(
                region.RawMeanAbsoluteErrorNativeAdu,
                region.CorrectedMeanAbsoluteErrorNativeAdu,
                $"The W6 calibration residual did not strictly improve for {region.Name}.");
        }
        return residuals;
    }

    private static CalibrationResidualEvidence CalculateCalibrationResiduals(
        FrameLayoutDescriptor layout,
        ReadOnlySpan<byte> clean,
        ReadOnlySpan<byte> raw,
        ReadOnlySpan<byte> normalizedClean,
        ReadOnlySpan<byte> corrected,
        ReadOnlySpan<byte> defectMask)
    {
        string[] regionNames = ["center", "outer", "north-west", "north-east", "south-west", "south-east"];
        var rawRegionTotals = new double[regionNames.Length];
        var correctedRegionTotals = new double[regionNames.Length];
        var regionCounts = new long[regionNames.Length];
        double rawTotal = 0;
        double correctedTotal = 0;
        double rawDefectTotal = 0;
        double correctedDefectTotal = 0;
        var repairableDefectCount = 0;
        var nativeRange = (layout.WhiteLevel ?? throw new InvalidDataException("The W6 white level is missing.")) -
            (layout.BlackLevel ?? throw new InvalidDataException("The W6 black level is missing."));
        for (var y = 0; y < layout.Height; y++)
        {
            for (var x = 0; x < layout.Width; x++)
            {
                var nativeOffset = checked(y * layout.StrideBytes + x * 2);
                var packedOffset = checked((y * layout.Width + x) * 2);
                var rawDifference = Math.Abs(ReadSample(raw, nativeOffset) - ReadSample(clean, nativeOffset));
                var correctedDifference = Math.Abs(
                    ReadSample(corrected, packedOffset) - ReadSample(normalizedClean, packedOffset)) *
                    nativeRange / ushort.MaxValue;
                rawTotal += rawDifference;
                correctedTotal += correctedDifference;
                var center = x >= layout.Width / 4 && x < layout.Width * 3 / 4 &&
                    y >= layout.Height / 4 && y < layout.Height * 3 / 4;
                AddRegion(center ? 0 : 1);
                AddRegion(2 + (y >= layout.Height / 2 ? 2 : 0) + (x >= layout.Width / 2 ? 1 : 0));
                if (ReadSample(defectMask, packedOffset) != 0 &&
                    HasSameLaneRepairNeighbor(defectMask, layout.Width, layout.Height, x, y))
                {
                    rawDefectTotal += rawDifference;
                    correctedDefectTotal += correctedDifference;
                    repairableDefectCount++;
                }

                void AddRegion(int region)
                {
                    rawRegionTotals[region] += rawDifference;
                    correctedRegionTotals[region] += correctedDifference;
                    regionCounts[region]++;
                }
            }
        }
        Assert.IsGreaterThan(0, repairableDefectCount);
        var sampleCount = checked((long)layout.Width * layout.Height);
        return new CalibrationResidualEvidence(
            sampleCount,
            rawTotal / sampleCount,
            correctedTotal / sampleCount,
            repairableDefectCount,
            rawDefectTotal / repairableDefectCount,
            correctedDefectTotal / repairableDefectCount,
            regionNames.Select((name, index) => new CalibrationSpatialResidualEvidence(
                name,
                regionCounts[index],
                rawRegionTotals[index] / regionCounts[index],
                correctedRegionTotals[index] / regionCounts[index])).ToArray());
    }

    private static bool HasSameLaneRepairNeighbor(
        ReadOnlySpan<byte> defectMask,
        int width,
        int height,
        int x,
        int y)
    {
        ReadOnlySpan<(int X, int Y)> directions = [(2, 0), (-2, 0), (0, 2), (0, -2)];
        foreach (var direction in directions)
        {
            var neighborX = x + direction.X;
            var neighborY = y + direction.Y;
            if (neighborX >= 0 && neighborX < width && neighborY >= 0 && neighborY < height &&
                ReadSample(defectMask, checked((neighborY * width + neighborX) * 2)) == 0)
            {
                return true;
            }
        }
        return false;
    }

    private static ushort ReadSample(ReadOnlySpan<byte> pixels, int offset)
        => (ushort)(pixels[offset] | pixels[offset + 1] << 8);

    private static List<RetainedArtifactEvidence> RetainRepresentativeEvidence(
        string runtimeRoot,
        string evidenceRoot,
        Guid captureId)
    {
        var destinationRoot = Path.Combine(evidenceRoot, "representative");
        Directory.CreateDirectory(destinationRoot);
        var retained = new List<RetainedArtifactEvidence>();
        foreach (var observation in ReadManifests(runtimeRoot)
                     .Where(item => item.Manifest.Descriptor.Capture.CaptureId == captureId))
        {
            var manifest = observation.Manifest;
            Retain(
                observation.Path,
                Path.Combine(runtimeRoot, manifest.RelativeArtifactPath),
                manifest.Descriptor.Artifact.ArtifactId,
                manifest.Descriptor.Artifact.Role.ToString(),
                manifest.Descriptor.Artifact.Variant,
                manifest.Descriptor.Artifact.ChecksumSha256);
        }
        foreach (var observation in ReadProductManifests(runtimeRoot, ExpectedAgentId)
                     .Where(item => item.Manifest.Capture.CaptureId == captureId))
        {
            var manifest = observation.Manifest;
            Retain(
                observation.Path,
                Path.Combine(runtimeRoot, manifest.RelativeArtifactPath),
                manifest.Artifact.ArtifactId,
                manifest.Artifact.Role.ToString(),
                manifest.Artifact.Variant,
                manifest.Artifact.ChecksumSha256);
        }
        CollectionAssert.AreEquivalent(
            ExpectedRetainedArtifacts,
            retained.Select(static item => $"{item.Role}/{item.Variant}").ToArray());
        return retained;

        void Retain(
            string manifestPath,
            string payloadPath,
            Guid artifactId,
            string role,
            string variant,
            string expectedSha256)
        {
            var stem = $"{role}-{variant}-{artifactId:N}";
            var retainedManifest = Path.Combine(destinationRoot, $"{stem}.manifest.json");
            var retainedPayload = Path.Combine(destinationRoot, stem + Path.GetExtension(payloadPath));
            File.Copy(manifestPath, retainedManifest, overwrite: true);
            File.Copy(payloadPath, retainedPayload, overwrite: true);
            var bytes = File.ReadAllBytes(retainedPayload);
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            Assert.AreEqual(expectedSha256, sha256);
            retained.Add(new RetainedArtifactEvidence(
                artifactId,
                role,
                variant,
                Path.GetRelativePath(evidenceRoot, retainedPayload).Replace(Path.DirectorySeparatorChar, '/'),
                Path.GetRelativePath(evidenceRoot, retainedManifest).Replace(Path.DirectorySeparatorChar, '/'),
                bytes.LongLength,
                sha256));
        }
    }

    private static void RetainJournalSnapshot(string runtimeRoot, string evidenceRoot)
    {
        var destinationPath = Path.Combine(evidenceRoot, "raw-ingress.snapshot.db");
        File.Delete(destinationPath);
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(runtimeRoot, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static List<MonoReadoutEvidence> AssertMonoCaptureContracts(
        string root,
        CameraAgentGalleryCapture[] captures,
        DeploymentLocationSnapshot expectedDeploymentLocation)
    {
        var manifests = ReadManifests(root, ExpectedMonoAgentId)
            .GroupBy(static item => item.Manifest.Descriptor.Artifact.ArtifactId)
            .ToDictionary(static group => group.Key, static group => group.First().Manifest);
        var evidence = new List<MonoReadoutEvidence>(captures.Length);
        foreach (var capture in captures)
        {
            Assert.HasCount(4, capture.ProcessingNodes);
            Assert.IsFalse(capture.ProcessingNodes.Any(static node => node.NodeId is "calibration" or "rolling"));
            foreach (var artifact in capture.Artifacts)
            {
                Assert.IsTrue(manifests.TryGetValue(artifact.ArtifactId, out var manifest));
                var descriptor = manifest.Descriptor;
                Assert.AreEqual(ExpectedMonoRigSha256, descriptor.Profiles.Rig.Sha256);
                Assert.AreEqual(ExpectedMonoProcessingSha256, descriptor.Profiles.Processing.Sha256);
                var payload = File.ReadAllBytes(Path.Combine(root, manifest.RelativeArtifactPath));
                Assert.AreEqual(descriptor.Artifact.ChecksumSha256, Convert.ToHexString(SHA256.HashData(payload)));
                Assert.AreEqual(descriptor.Layout.ByteLength, payload.LongLength);
            }
            var raw = manifests.Values.Single(item =>
                item.Descriptor.Capture.CaptureId == capture.CaptureId &&
                item.Descriptor.Artifact.Role == FrameArtifactRole.Raw);
            Assert.AreEqual(160, raw.Descriptor.Layout.Width);
            Assert.AreEqual(120, raw.Descriptor.Layout.Height);
            Assert.AreEqual(160, raw.Descriptor.Layout.StrideBytes);
            Assert.AreEqual(19_200L, raw.Descriptor.Layout.ByteLength);
            Assert.AreEqual(CameraPixelFormat.Mono8, raw.Descriptor.Layout.PixelFormat);
            Assert.AreEqual(8, raw.Descriptor.Layout.SampleDepthBits);
            Assert.AreEqual(8, raw.Descriptor.Layout.ContainerDepthBits);
            var storedCodeTransform = raw.Descriptor.Layout.StoredCodeTransform
                ?? throw new InvalidDataException("The measured Mono8 stored-code transform is missing.");
            var levelCodeSpace = raw.Descriptor.Layout.LevelCodeSpace
                ?? throw new InvalidDataException("The measured Mono8 level code space is missing.");
            Assert.AreEqual(FrameStoredCodeTransform.IdentityV1, storedCodeTransform);
            Assert.AreEqual(FrameLevelCodeSpace.StoredContainer, levelCodeSpace);
            var readout = raw.Descriptor.Layout.Readout
                ?? throw new InvalidDataException("The measured Mono8 readout descriptor is missing.");
            Assert.AreEqual(1936, readout.NativeWidth);
            Assert.AreEqual(1216, readout.NativeHeight);
            Assert.AreEqual(4, readout.BinX);
            Assert.AreEqual(4, readout.BinY);
            Assert.AreEqual(FrameBinningAlgorithm.DigitalAverageV1, readout.BinningAlgorithm);
            Assert.AreEqual(648, readout.RoiX);
            Assert.AreEqual(368, readout.RoiY);
            Assert.AreEqual(640, readout.RoiWidth);
            Assert.AreEqual(480, readout.RoiHeight);
            Assert.AreEqual(TimeSpan.FromSeconds(1), raw.Descriptor.Controls.EffectiveExposure);
            Assert.AreEqual(150d, raw.Descriptor.Controls.EffectiveGain);
            var sceneObjects = raw.Scene?.Objects
                ?? throw new InvalidDataException("The measured Mono8 projected scene is missing.");
            Assert.IsLessThanOrEqualTo(300, sceneObjects.Count);
            Assert.IsTrue(sceneObjects.All(static item => item.Magnitude <= 6.5));
            var admission = raw.Descriptor.CycleEvidence?.ScheduleAdmission
                ?? throw new InvalidDataException("The measured Mono8 schedule admission evidence is missing.");
            Assert.AreEqual(ExpectedMonoLocalProfileSha256, admission.LocalProfileSha256);
            Assert.AreEqual(ExpectedMonoScheduleSha256, admission.ScheduleRevisionSha256);
            Assert.AreEqual(expectedDeploymentLocation.LocationId, admission.DeploymentLocationId);
            Assert.AreEqual(expectedDeploymentLocation.Version, admission.DeploymentLocationVersion);
            Assert.AreEqual(expectedDeploymentLocation.ToProvenance(), raw.Descriptor.Location);
            evidence.Add(new MonoReadoutEvidence(
                capture.CaptureId,
                capture.CaptureSequence,
                raw.Descriptor.Layout.Width,
                raw.Descriptor.Layout.Height,
                raw.Descriptor.Layout.StrideBytes,
                raw.Descriptor.Layout.ByteLength,
                raw.Descriptor.Layout.PixelFormat,
                storedCodeTransform,
                levelCodeSpace,
                readout,
                admission.LocalProfileSha256,
                admission.ScheduleRevisionId,
                admission.ScheduleRevisionSha256,
                admission.DeploymentLocationId,
                admission.DeploymentLocationVersion,
                raw.Descriptor.Location!));
        }
        return evidence;
    }

    private static IEnumerable<ProductManifestObservation> ReadProductManifests(string root, string agentId)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            IDurableProcessingProductManifest parsed;
            try
            {
                parsed = DurableProcessingProductManifestJson.Parse(File.ReadAllBytes(path));
            }
            catch (IOException)
            {
                continue;
            }
            catch (InvalidDataException)
            {
                continue;
            }
            if (parsed is not DurableProcessingProductManifestV1 manifest)
            {
                continue;
            }
            if (manifest.Capture.AgentId == agentId)
            {
                yield return new ProductManifestObservation(path, manifest);
            }
        }
    }

    private static async Task<EnvironmentalEvidence> AssertEnvironmentalEvidenceAsync(
        HttpClient client,
        IPage page,
        string runtimeRoot,
        CameraAgentGalleryCapture capture)
    {
        using var sourcesResponse = await client.GetAsync(
            new Uri("/api/v1/operations/environmental/sources", UriKind.Relative)).ConfigureAwait(false);
        sourcesResponse.EnsureSuccessStatusCode();
        using var sources = JsonDocument.Parse(
            await sourcesResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        Assert.AreEqual(12, sources.RootElement.GetArrayLength());
        Assert.IsTrue(sources.RootElement.EnumerateArray().All(source =>
            source.GetProperty("lastDisposition").ValueKind != JsonValueKind.Null));

        using var historyResponse = await client.GetAsync(
            new Uri("/api/v1/operations/environmental/history?pageSize=50", UriKind.Relative)).ConfigureAwait(false);
        historyResponse.EnsureSuccessStatusCode();
        using var history = JsonDocument.Parse(
            await historyResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        var historyCount = history.RootElement.GetProperty("items").GetArrayLength();
        Assert.IsGreaterThanOrEqualTo(12, historyCount);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(runtimeRoot, ".environment", "environmental-observation-outbox.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   SUM(CASE WHEN status = 'Fresh' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN selected_record_id IS NOT NULL THEN 1 ELSE 0 END),
                   COUNT(DISTINCT rig_id),
                   MIN(rig_id)
            FROM environmental_capture_associations
            WHERE capture_id = $capture;
            """;
        command.Parameters.AddWithValue("$capture", capture.CaptureId.ToString("D"));
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        var associationCount = reader.GetInt32(0);
        var freshCount = reader.GetInt32(1);
        var selectedCount = reader.GetInt32(2);
        var rigCount = reader.GetInt32(3);
        var rigId = reader.GetString(4);
        Assert.AreEqual(12, associationCount);
        Assert.AreEqual(12, freshCount);
        Assert.AreEqual(12, selectedCount);
        Assert.AreEqual(1, rigCount);
        Assert.AreEqual($"rig-{ExpectedRigSha256[..16]}", rigId);

        await page.GotoAsync("/environmental").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Environmental acquisition", Level = 1 })
            .WaitForAsync().ConfigureAwait(false);
        var sourceCards = page.Locator(".source-grid article");
        await sourceCards.First.WaitForAsync().ConfigureAwait(false);
        var uiSourceCount = await sourceCards.CountAsync().ConfigureAwait(false);
        var uiHistoryCount = await page.Locator(".history article").CountAsync().ConfigureAwait(false);
        var uiAttemptCount = await page.Locator(".attempts > div").CountAsync().ConfigureAwait(false);
        Assert.AreEqual(12, uiSourceCount);
        Assert.IsGreaterThan(0, uiHistoryCount);
        Assert.IsGreaterThan(0, uiAttemptCount);
        StringAssert.Contains(await page.Locator(".summary").InnerTextAsync().ConfigureAwait(false), "Enabled",
            StringComparison.Ordinal);
        return new EnvironmentalEvidence(
            12,
            historyCount,
            associationCount,
            freshCount,
            selectedCount,
            rigId,
            uiSourceCount,
            uiHistoryCount,
            uiAttemptCount);
    }

    private static async Task AssertBrowserEvidenceAsync(IPage page, CameraAgentGalleryCapture capture)
    {
        foreach (var route in new[] { "/operations", "/schedule", "/calibration", "/environmental", "/gallery", "/system" })
        {
            await page.GotoAsync(route).ConfigureAwait(false);
            await page.Locator("main#mainContent h1").First.WaitForAsync().ConfigureAwait(false);
            Assert.AreEqual(1, await page.Locator("main").CountAsync().ConfigureAwait(false));
        }
        await page.GotoAsync($"/gallery/{capture.CaptureId:D}").ConfigureAwait(false);
        await page.GetByText("Capture detail", new() { Exact = true }).WaitForAsync().ConfigureAwait(false);
        var comparisonImages = page.Locator(".comparison-grid img");
        await comparisonImages.First.WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual(2, await comparisonImages.CountAsync().ConfigureAwait(false));
        await page.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('.comparison-grid img')].every(image => image.complete && image.naturalWidth > 0)")
            .ConfigureAwait(false);
        Assert.IsTrue(await comparisonImages.EvaluateAllAsync<bool>(
            "images => images.every(image => image.naturalWidth <= 2048 && image.naturalHeight <= 2048)")
            .ConfigureAwait(false));
        var previewUrl = await comparisonImages.First.GetAttributeAsync("src").ConfigureAwait(false);
        Assert.IsNotNull(previewUrl);
        var previewResponse = await page.Context.APIRequest.GetAsync(new Uri(new Uri(page.Url), previewUrl).ToString())
            .ConfigureAwait(false);
        try
        {
            Assert.IsTrue(previewResponse.Ok);
            var bytes = await previewResponse.BodyAsync().ConfigureAwait(false);
            Assert.IsLessThanOrEqualTo(16L * 1024 * 1024, bytes.LongLength);
        }
        finally
        {
            await previewResponse.DisposeAsync().ConfigureAwait(false);
        }
        var downloadUrl = await page.Locator("a[download]").First.GetAttributeAsync("href").ConfigureAwait(false);
        Assert.IsNotNull(downloadUrl);
        var downloadResponse = await page.Context.APIRequest.GetAsync(new Uri(new Uri(page.Url), downloadUrl).ToString())
            .ConfigureAwait(false);
        try
        {
            Assert.IsTrue(downloadResponse.Ok);
            Assert.IsNotEmpty(await downloadResponse.BodyAsync().ConfigureAwait(false));
        }
        finally
        {
            await downloadResponse.DisposeAsync().ConfigureAwait(false);
        }
        await page.SetViewportSizeAsync(390, 844).ConfigureAwait(false);
        Assert.IsTrue(await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1").ConfigureAwait(false));
    }

    private static async Task<TransientBrowserEvidence> AssertTransientBrowserEvidenceAsync(
        IPage page,
        TransientEvidence evidence)
    {
        await page.SetViewportSizeAsync(1440, 900).ConfigureAwait(false);
        await page.GotoAsync("/transients?pageSize=100").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Transient candidates", Level = 1 })
            .WaitForAsync().ConfigureAwait(false);
        var candidate = page.Locator(".candidate-list article").Filter(new() { HasText = evidence.CandidateId.ToString() });
        Assert.AreEqual(1, await candidate.CountAsync().ConfigureAwait(false));
        await candidate.GetByText(evidence.EventId.ToString(), new() { Exact = true }).WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual(4, await candidate.Locator(".stage--available").CountAsync().ConfigureAwait(false));

        await page.GotoAsync($"/transients/{evidence.CandidateId:D}").ConfigureAwait(false);
        await page.GetByText("Candidate detail", new() { Exact = true }).WaitForAsync().ConfigureAwait(false);
        await page.GetByText(evidence.CandidateId.ToString(), new() { Exact = true }).First.WaitForAsync().ConfigureAwait(false);
        await page.GetByText(evidence.EventId.ToString(), new() { Exact = true }).WaitForAsync().ConfigureAwait(false);
        foreach (var (stage, identity) in new[]
        {
            ("Causal window", evidence.Execution.CausalIdentitySha256),
            ("Centered window", evidence.Execution.CenteredIdentitySha256),
            ("Assessment", evidence.Execution.AssessmentIdentitySha256),
            ("Final evidence", evidence.FinalizationReceiptIdentitySha256)
        })
        {
            await page.Locator(".stage-grid article").Filter(new() { HasText = stage })
                .GetByText(identity, new() { Exact = true }).WaitForAsync().ConfigureAwait(false);
        }

        await page.SetViewportSizeAsync(390, 844).ConfigureAwait(false);
        Assert.IsTrue(await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1").ConfigureAwait(false));
        await page.GotoAsync("/transients?pageSize=100").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Transient candidates", Level = 1 })
            .WaitForAsync().ConfigureAwait(false);
        Assert.IsTrue(await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1").ConfigureAwait(false));
        await page.SetViewportSizeAsync(1440, 900).ConfigureAwait(false);
        return new TransientBrowserEvidence(
            evidence.CandidateId,
            evidence.EventId,
            evidence.ScenarioId,
            evidence.Execution.CausalIdentitySha256,
            evidence.Execution.CenteredIdentitySha256,
            evidence.Execution.AssessmentIdentitySha256,
            evidence.FinalizationReceiptIdentitySha256,
            MobileNoOverflow: true);
    }

    private static async Task<SanitizedPreviewEvidence> AssertComparisonApiAsync(
        HttpClient client,
        CameraAgentGalleryCapture capture,
        GeometryEvidence geometry,
        string evidenceRoot)
    {
        var artifact = capture.Artifacts.First(item => item.Role == FrameArtifactRole.AnnotatedPreview);
        var sourceArtifact = capture.Artifacts.Single(item =>
            item.Role == FrameArtifactRole.Preview && item.Variant == "combined-preview");
        var bytes = await ReadPreviewAsync(artifact.ArtifactId).ConfigureAwait(false);
        var sourceBytes = await ReadPreviewAsync(sourceArtifact.ArtifactId).ConfigureAwait(false);
        Assert.IsNotEmpty(bytes);
        Assert.IsLessThanOrEqualTo(16L * 1024 * 1024, bytes.LongLength);
        var decoded = JpegImageCodec.DecodeJpeg(bytes);
        var decodedSource = JpegImageCodec.DecodeJpeg(sourceBytes);
        Assert.AreEqual(CameraPixelFormat.Rgb24, decoded.PixelFormat);
        Assert.AreEqual(decoded.Width, decodedSource.Width);
        Assert.AreEqual(decoded.Height, decodedSource.Height);
        Assert.AreEqual(2_048, decoded.Width);
        Assert.AreEqual(2_048, decoded.Height);
        var scaleX = decoded.Width / 3552d;
        var scaleY = decoded.Height / 3552d;
        var markerObservations = geometry.Objects.Select(item =>
        {
            var expectedX = item.ExpectedX * scaleX;
            var expectedY = item.ExpectedY * scaleY;
            var marker = CalculateJpegMarkerCentroid(
                decodedSource.PixelData.Span,
                decoded.PixelData.Span,
                decoded.Width,
                decoded.Height,
                expectedX,
                expectedY,
                radius: 4);
            var error = Distance(marker.X, marker.Y, expectedX, expectedY);
            Assert.IsGreaterThan(1, marker.ChangedPoints, item.RowId);
            Assert.IsLessThanOrEqualTo(2, error, item.RowId);
            return new JpegMarkerEvidence(item.RowId, expectedX, expectedY, marker.X, marker.Y, error,
                marker.ChangedPoints);
        }).ToArray();
        var cardinalMarkers = new Dictionary<string, PixelPoint>(StringComparer.Ordinal)
        {
            ["north"] = geometry.CardinalLandmarks.North,
            ["east"] = geometry.CardinalLandmarks.East,
            ["south"] = geometry.CardinalLandmarks.South,
            ["west"] = geometry.CardinalLandmarks.West
        }.Select(item =>
        {
            var expectedX = item.Value.X * scaleX;
            var expectedY = item.Value.Y * scaleY;
            var marker = CalculateJpegMarkerCentroid(
                decodedSource.PixelData.Span,
                decoded.PixelData.Span,
                decoded.Width,
                decoded.Height,
                expectedX,
                expectedY,
                radius: 6);
            var error = Distance(marker.X, marker.Y, expectedX, expectedY);
            Assert.IsGreaterThan(0, marker.ChangedPoints, item.Key);
            Assert.IsLessThanOrEqualTo(2, error, item.Key);
            return new JpegMarkerEvidence(
                item.Key, expectedX, expectedY, marker.X, marker.Y, error, marker.ChangedPoints);
        }).ToArray();
        var relativePath = "representative/comparison-preview.jpg";
        var path = Path.Combine(evidenceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
        var sourceRelativePath = "representative/comparison-source-preview.jpg";
        await File.WriteAllBytesAsync(Path.Combine(evidenceRoot, sourceRelativePath), sourceBytes).ConfigureAwait(false);
        return new SanitizedPreviewEvidence(
            artifact.ArtifactId,
            sourceArtifact.ArtifactId,
            relativePath,
            sourceRelativePath,
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)),
            decoded.Width,
            decoded.Height,
            markerObservations,
            cardinalMarkers);

        async Task<byte[]> ReadPreviewAsync(Guid artifactId)
        {
            using var response = await client.GetAsync(
                new Uri($"/api/v1/operations/artifacts/{artifactId:D}/preview", UriKind.Relative))
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            Assert.AreEqual("image/jpeg", response.Content.Headers.ContentType?.MediaType);
            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
    }

    private static PackedMarkerObservation CalculateJpegMarkerCentroid(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> annotated,
        int width,
        int height,
        double expectedX,
        double expectedY,
        int radius)
    {
        var centerX = (int)Math.Round(expectedX, MidpointRounding.AwayFromZero);
        var centerY = (int)Math.Round(expectedY, MidpointRounding.AwayFromZero);
        var totalWeight = 0d;
        var weightedX = 0d;
        var weightedY = 0d;
        var changed = 0;
        for (var y = Math.Max(0, centerY - radius); y <= Math.Min(height - 1, centerY + radius); y++)
        {
            for (var x = Math.Max(0, centerX - radius); x <= Math.Min(width - 1, centerX + radius); x++)
            {
                var offset = checked((y * width + x) * 3);
                var difference = 0;
                for (var channel = 0; channel < 3; channel++)
                {
                    difference += Math.Max(0, annotated[offset + channel] - source[offset + channel] - 15);
                }
                if (difference <= 30)
                {
                    continue;
                }
                totalWeight += difference;
                weightedX += x * difference;
                weightedY += y * difference;
                changed++;
            }
        }
        Assert.IsGreaterThan(0, totalWeight);
        return new PackedMarkerObservation(weightedX / totalWeight, weightedY / totalWeight, changed);
    }

    private static void AssertRuntimeMetricSurface(string metrics)
    {
        foreach (var name in new[]
        {
            "camera_agent_capture_count",
            "camera_agent_ingress_committed",
            "camera_agent_lanes_completed",
            "camera_agent_processing_graphs",
            "camera_agent_processing_graph_duration",
            "camera_agent_processing_recipe_duration",
            "camera_agent_calibration_acquisitions",
            "skymonitor_environment_local_acquisitions",
            "hvo_transient_worker_outcomes",
            "camera_agent_processing_plan_previews",
            "camera_agent_processing_plan_mutations",
            "camera_agent_processing_comparison_requests",
            "camera_agent_processing_plan_preview_duration_seconds",
            "camera_agent_processing_comparison_duration_seconds",
            "camera_agent_processing_comparison_output_bytes",
            "hvo_transient_operator_queries",
            "hvo_transient_operator_query_duration_milliseconds",
            "hvo_transient_operator_response_bytes"
        })
        {
            StringAssert.Contains(metrics, name, StringComparison.Ordinal);
        }
        foreach (var prohibited in new[] { "artifact_id=", "capture_id=", "candidate_id=", "connection_string=" })
        {
            Assert.IsFalse(metrics.Contains(prohibited, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void AssertMonoRuntimeMetricSurface(string metrics)
    {
        foreach (var name in new[]
        {
            "camera_agent_capture_count",
            "camera_agent_ingress_committed",
            "camera_agent_lanes_completed",
            "camera_agent_processing_graphs"
        })
        {
            StringAssert.Contains(metrics, name, StringComparison.Ordinal);
        }
    }

    private static int CountDenyHits(string logs)
        => Regex.Matches(logs, "HVO211_DENY_HIT", RegexOptions.CultureInvariant).Count;

    private static int AssertCentralHandlerAttempts(string path)
    {
        var records = File.ReadAllLines(path);
        Assert.AreEqual(CentralHandlerReadyRecord, records.FirstOrDefault());
        Assert.IsTrue(records.All(static record =>
            record == CentralHandlerReadyRecord ||
            record.StartsWith(CentralHandlerAttemptRecord, StringComparison.Ordinal)));
        var attempts = records.Count(static record =>
            record.StartsWith(CentralHandlerAttemptRecord, StringComparison.Ordinal));
        Assert.AreEqual(0, attempts);
        return attempts;
    }

    private static async Task OpenDialogAsync(ILocator trigger, ILocator dialog)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await trigger.ClickAsync().ConfigureAwait(false);
            try
            {
                await dialog.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 2_000
                }).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException) when (attempt < 9)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }

    private static long DirectoryBytes(string path)
        => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(static file => new FileInfo(file).Length);

    private static DurableQueueMaximum[] MergeDurableQueueMaxima(
        params IReadOnlyList<DurableQueueMaximum>[] samples)
        => samples
            .SelectMany(static sample => sample)
            .GroupBy(static queue => queue.Name, StringComparer.Ordinal)
            .Select(static group => new DurableQueueMaximum(
                group.Key,
                group.Max(static queue => queue.Count),
                group.Max(static queue => queue.Bytes),
                group.Max(static queue => queue.AgeSeconds)))
            .OrderBy(static queue => queue.Name, StringComparer.Ordinal)
            .ToArray();

    private static void AssertDurableQueueBounds(IReadOnlyList<DurableQueueMaximum> maxima)
    {
        CollectionAssert.AreEquivalent(
            DurableQueueBounds.Keys.ToArray(),
            maxima.Select(static queue => queue.Name).ToArray());
        foreach (var queue in maxima)
        {
            var bound = DurableQueueBounds[queue.Name];
            Assert.IsLessThanOrEqualTo(
                bound.Count,
                queue.Count,
                $"Durable queue {queue.Name} exceeded its declared W6 count bound.");
            Assert.IsLessThanOrEqualTo(
                bound.Bytes,
                queue.Bytes,
                $"Durable queue {queue.Name} exceeded its declared W6 byte bound.");
            Assert.IsLessThanOrEqualTo(
                bound.AgeSeconds,
                queue.AgeSeconds,
                $"Durable queue {queue.Name} exceeded its declared W6 age bound.");
        }
    }

    private static long RoleDirectoryBytes(string root, FrameArtifactRole role)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar)
                .Contains(role.ToString(), StringComparer.Ordinal))
            .Sum(static file => new FileInfo(file).Length);

    private static DurableSnapshot ReadDurableSnapshot(string root) => new(
        ReadJournalCount(root, "SELECT COUNT(*) FROM raw_captures;"),
        ReadJournalCount(root, "SELECT COUNT(*) FROM processing_outputs;"),
        ReadJournalCount(root, "SELECT COUNT(*) FROM raw_captures WHERE state <> 'committed';"),
        ReadJournalCount(root, "SELECT COUNT(*) FROM capture_lane_work WHERE state NOT IN ('completed', 'abandoned');"),
        ReadJournalCount(root, "SELECT COUNT(*) FROM processing_nodes WHERE status NOT IN ('Completed', 'Skipped');"),
        ReadJournalCount(root, """
            SELECT COALESCE(SUM(output_count - 1), 0)
            FROM (
                SELECT COUNT(*) AS output_count
                FROM processing_outputs
                GROUP BY capture_id, node_id, role, variant
                HAVING COUNT(*) > 1
            );
            """));

    private static async Task WaitForDurableConditionAsync(
        string root,
        Func<DurableSnapshot, bool> condition,
        string description,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (condition(ReadDurableSnapshot(root)))
                {
                    return;
                }
            }
            catch (SqliteException)
            {
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        Assert.Fail($"Timed out waiting for {description}.");
    }

    private static async Task<TransientSnapshot> WaitForTransientConvergenceAsync(string root, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        TransientSnapshot? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                latest = ReadTransientSnapshot(root);
                if (latest.PendingFrames == 0 &&
                    latest.PendingCandidates == 0 &&
                    latest.PendingCaptureWork == 0 &&
                    latest.ActiveSourceHolds == 0)
                {
                    Assert.AreEqual(0, latest.QuarantinedOrConflicting);
                    Assert.IsGreaterThan(0, latest.FinalizedCandidates);
                    Assert.IsGreaterThan(0, latest.FinalizedEvents);
                    Assert.IsGreaterThanOrEqualTo(3, latest.MinimumFinalizedSourceCount);
                    Assert.IsGreaterThan(0, latest.CompletedNoCandidateFrames);
                    return latest;
                }
            }
            catch (SqliteException)
            {
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        Assert.Fail($"Transient work did not converge within {timeout}. Last snapshot: {latest}.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static async Task WaitForTransientDrainAsync(string root, string description, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        TransientSnapshot? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                latest = ReadTransientSnapshot(root);
                if (latest.PendingFrames == 0 && latest.PendingCandidates == 0 &&
                    latest.PendingCaptureWork == 0 && latest.ActiveSourceHolds == 0)
                {
                    Assert.AreEqual(0, latest.QuarantinedOrConflicting);
                    return;
                }
            }
            catch (SqliteException)
            {
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        Assert.Fail($"Timed out waiting for {description}. Last snapshot: {latest}.");
    }

    private static TransientSnapshot ReadTransientSnapshot(string root)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM transient_worker_frames WHERE state IN ('queued', 'retry_wait')),
                (SELECT COUNT(*) FROM transient_worker_candidates WHERE state = 'pending'),
                (SELECT COUNT(*) FROM transient_capture_work w
                    LEFT JOIN transient_worker_frames f ON f.raw_capture_row_id = w.raw_capture_row_id
                    WHERE w.state = 'pending' AND COALESCE(f.state, 'queued') != 'history'),
                (SELECT COUNT(*) FROM transient_candidates WHERE source_hold_released = 0),
                (SELECT COUNT(*) FROM transient_worker_frames WHERE state = 'quarantined') +
                    (SELECT COUNT(*) FROM transient_worker_candidates WHERE state = 'quarantined') +
                    (SELECT COUNT(*) FROM transient_candidates WHERE phase = 'quarantined') +
                    (SELECT COUNT(*) FROM transient_candidate_conflicts),
                (SELECT COUNT(*) FROM transient_candidates c
                    JOIN transient_worker_candidates w ON w.candidate_id = c.candidate_id
                    WHERE c.phase = 'finalized' AND c.finalization_payload IS NOT NULL AND
                          w.state = 'completed' AND w.observation_extraction_json IS NOT NULL AND
                          w.assessment_execution_json IS NOT NULL),
                (SELECT COUNT(DISTINCT event_id) FROM transient_candidates
                    WHERE phase = 'finalized' AND finalization_payload IS NOT NULL),
                (SELECT COUNT(*) FROM transient_worker_frames f
                    WHERE f.state = 'completed' AND f.causal_succeeded = 1 AND NOT EXISTS (
                        SELECT 1 FROM transient_worker_candidates c
                        WHERE c.target_raw_capture_row_id = f.raw_capture_row_id)),
                COALESCE((SELECT MIN(source_count) FROM (
                    SELECT COUNT(*) AS source_count
                    FROM transient_candidate_sources s
                    JOIN transient_candidates c ON c.candidate_id = s.candidate_id
                    WHERE c.phase = 'finalized'
                    GROUP BY s.candidate_id)), 0);
            """;
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        return new TransientSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Queries are fixed private test constants and contain no external input.")]
    private static long ReadJournalCount(string root, string sql)
    {
        using var connection = OpenJournal(root);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static SqliteConnection OpenJournal(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static DrainSnapshot ReadDrainSnapshot(string root, long minimumSequence)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(r.payload_length), 0), MIN(r.durable_ingress_unix_ms),
                   COALESCE(SUM(r.retention_hold), 0),
                   COALESCE(SUM(CASE WHEN w.state != 'completed' THEN 1 ELSE 0 END), 0)
            FROM raw_captures r
            JOIN capture_lane_work w ON w.raw_capture_row_id = r.raw_capture_row_id AND w.lane_name = 'standard'
            WHERE r.agent_id = $agent AND r.capture_sequence > $sequence;

            SELECT COALESCE(SUM(output_count - 1), 0)
            FROM (
                SELECT COUNT(*) AS output_count
                FROM processing_outputs
                GROUP BY capture_id, node_id, role, variant
                HAVING COUNT(*) > 1
            );
            """;
        command.Parameters.AddWithValue("$agent", ExpectedAgentId);
        command.Parameters.AddWithValue("$sequence", minimumSequence);
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        var captureCount = reader.GetInt64(0);
        var rawBytes = reader.GetInt64(1);
        var oldestAge = reader.IsDBNull(2)
            ? TimeSpan.Zero
            : DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
        var retentionHoldCount = reader.GetInt64(3);
        var incomplete = reader.GetInt64(4);
        Assert.IsTrue(reader.NextResult());
        Assert.IsTrue(reader.Read());
        return new DrainSnapshot(
            captureCount,
            rawBytes,
            oldestAge,
            retentionHoldCount,
            incomplete,
            reader.GetInt64(0));
    }

    private static async Task<DrainSnapshot> WaitForDrainConditionAsync(
        string root,
        long minimumSequence,
        Func<DrainSnapshot, bool> condition,
        string description,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        DrainSnapshot? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                latest = ReadDrainSnapshot(root, minimumSequence);
                if (condition(latest))
                {
                    return latest;
                }
            }
            catch (SqliteException)
            {
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        Assert.Fail($"Timed out waiting for {description}. Last snapshot: {latest}");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static async Task<StorageObservation> ReadStorageObservationAsync(HttpClient client)
    {
        var summary = await client.GetFromJsonAsync<CameraAgentOperationsSummary>(
            new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false);
        Assert.IsNotNull(summary);
        Assert.IsNotNull(summary.Storage.ObservedUtc);
        return new StorageObservation(
            summary.Storage.ObservedUtc.Value,
            summary.Storage.Value.Single(static value => value.Alias == "raw-ingress"));
    }

    private static async Task<StorageObservation> WaitForStorageObservationAsync(
        HttpClient client,
        Func<StorageObservation, bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        StorageObservation? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            latest = await ReadStorageObservationAsync(client).ConfigureAwait(false);
            if (condition(latest))
            {
                return latest;
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        Assert.Fail($"Storage pressure did not reach the expected state within {timeout}. Last observation: {latest}");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static bool IsRawCaptureCommitted(string root, Guid captureId, Guid artifactId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM raw_captures
            WHERE capture_id = $capture AND raw_artifact_id = $artifact AND state = 'committed';
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        command.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static string Required(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required environment variable {name} is missing.");

    private static string RequiredPath(string name) => Path.GetFullPath(Required(name));

    private static int ReadPositiveInteger(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    private static async Task<string> DockerLogsAsync(string container)
        => await ProcessAsync("docker", ["logs", container]).ConfigureAwait(false);

    private static async Task<string> ReadMetricsAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/metrics", UriKind.Relative)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private static async Task<string> WaitForMetricSurfaceAsync(
        HttpClient client,
        string? prefix,
        Action<string> assertSurface,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var latest = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            latest = string.Concat(prefix, Environment.NewLine, await ReadMetricsAsync(client).ConfigureAwait(false));
            try
            {
                assertSurface(latest);
                return latest;
            }
            catch (AssertFailedException)
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
        assertSurface(latest);
        throw new InvalidOperationException("Unreachable after metric-surface assertion failure.");
    }

    private static long ReadPrometheusValue(string metrics, string name, string? requiredLabel = null)
    {
        foreach (var line in metrics.Split('\n').Reverse())
        {
            if ((!line.StartsWith(string.Concat(name, "{"), StringComparison.Ordinal) &&
                 !line.StartsWith(string.Concat(name, " "), StringComparison.Ordinal)) ||
                requiredLabel is not null && !line.Contains(requiredLabel, StringComparison.Ordinal))
            {
                continue;
            }
            var separator = line.LastIndexOf(' ');
            if (separator > 0 && long.TryParse(
                    line.AsSpan(separator + 1),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                return value;
            }
        }
        Assert.Fail($"Prometheus metric '{name}' was not present with the required labels.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static async Task<string> ReadHealthAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private static void AssertHealthCheckStatus(string health, string name, string status)
    {
        using var document = JsonDocument.Parse(health);
        var check = document.RootElement.GetProperty("checks").EnumerateArray().Single(item =>
            string.Equals(item.GetProperty("name").GetString(), name, StringComparison.Ordinal));
        Assert.AreEqual(status, check.GetProperty("status").GetString());
    }

    private static void AssertHealthOverallStatus(string health, string status)
    {
        using var document = JsonDocument.Parse(health);
        Assert.AreEqual(status, document.RootElement.GetProperty("status").GetString());
    }

    private static async Task<HealthCheckEvidence> WaitForHealthCheckStatusAsync(
        HttpClient client,
        string name,
        string status,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        string? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            latest = await ReadHealthAsync(client).ConfigureAwait(false);
            var evidence = ReadHealthCheckEvidence(latest, name);
            if (string.Equals(evidence.CheckStatus, status, StringComparison.Ordinal))
            {
                return evidence;
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        Assert.Fail($"Health check '{name}' did not report {status}. Last response: {latest}");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static HealthCheckEvidence ReadHealthCheckEvidence(string health, string name)
    {
        using var document = JsonDocument.Parse(health);
        var check = document.RootElement.GetProperty("checks").EnumerateArray().Single(item =>
            string.Equals(item.GetProperty("name").GetString(), name, StringComparison.Ordinal));
        var data = check.GetProperty("data");
        return new HealthCheckEvidence(
            document.RootElement.GetProperty("status").GetString()!,
            check.GetProperty("status").GetString()!,
            data.TryGetProperty("PendingCount", out var pending) ? pending.GetInt64() : null,
            data.TryGetProperty("RetryCount", out var retry) ? retry.GetInt64() : null,
            data.TryGetProperty("TerminalCount", out var terminal) ? terminal.GetInt64() : null,
            data.TryGetProperty("Reason", out var reason) ? reason.GetString() : null);
    }

    private static void AssertPublicCatalogHealthIsSanitized(string health)
    {
        using var document = JsonDocument.Parse(health);
        var catalog = document.RootElement.GetProperty("checks").EnumerateArray().Single(item =>
            string.Equals(item.GetProperty("name").GetString(), "catalog", StringComparison.Ordinal));
        var keys = catalog.GetProperty("data").EnumerateObject().Select(static property => property.Name).ToArray();
        Assert.IsFalse(keys.Any(static key =>
            key.Contains("Sha", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Path", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(health.Contains(ExpectedCatalogSha256, StringComparison.OrdinalIgnoreCase));
        var forbidden = new List<string>();
        Inspect(document.RootElement, "$", forbidden);
        Assert.IsEmpty(forbidden, string.Join(Environment.NewLine, forbidden));

        static void Inspect(JsonElement element, string path, List<string> forbidden)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = $"{path}.{property.Name}";
                    if (Regex.IsMatch(
                            property.Name,
                            "(sha|checksum|path|payload|token|secret|connection|string|agent[._-]?id|profile[._-]?id|artifact[._-]?id|candidate[._-]?id|event[._-]?id|actor|latitude|longitude|coordinate|environment[._-]?value)",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        forbidden.Add($"Forbidden public-health key: {propertyPath}");
                    }
                    Inspect(property.Value, propertyPath, forbidden);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Inspect(item, $"{path}[{index++}]", forbidden);
                }
            }
            else if (element.ValueKind == JsonValueKind.String && element.GetString() is { } value &&
                (Regex.IsMatch(value, "(^|[^0-9A-Fa-f])[0-9A-Fa-f]{64}([^0-9A-Fa-f]|$)", RegexOptions.CultureInvariant) ||
                 Regex.IsMatch(value, "[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}", RegexOptions.CultureInvariant) ||
                 value.Contains("/var/lib/hvo", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("/workspaces/", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("standalone-owner@", StringComparison.OrdinalIgnoreCase)))
            {
                forbidden.Add($"Forbidden public-health value: {path}");
            }
        }
    }

    private static async Task<string> WaitForZeroBacklogHealthAsync(
        HttpClient client,
        string initial,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var latest = initial;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var document = JsonDocument.Parse(latest);
            var checks = document.RootElement.GetProperty("checks").EnumerateArray().ToDictionary(
                item => item.GetProperty("name").GetString()!,
                item => item,
                StringComparer.Ordinal);
            if (Read("raw-ingress", "QuarantineCount") == 0 &&
                Read("capture-lanes", "LeasedCount") == 0 &&
                Read("capture-lanes", "QuarantineCount") == 0 &&
                Read("capture-processing", "PendingCount") == 0 &&
                Read("capture-processing", "RetryCount") == 0 &&
                Read("capture-processing", "TerminalCount") == 0)
            {
                return latest;
            }

            await Task.Delay(500).ConfigureAwait(false);
            latest = await ReadHealthAsync(client).ConfigureAwait(false);

            long Read(string check, string property)
                => checks[check].GetProperty("data").GetProperty(property).GetInt64();
        }
        Assert.Fail($"Health did not report zero required backlog. Last response: {latest}");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static async Task<string> WaitForHealthyAsync(HttpClient client, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        string? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
            latest = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(latest);
                if (string.Equals(
                        document.RootElement.GetProperty("status").GetString(),
                        "Healthy",
                        StringComparison.Ordinal))
                {
                    return latest;
                }
            }
            catch (JsonException)
            {
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
        Assert.Fail($"CameraAgent health did not recover within {timeout}. Last response: {latest}");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static async Task RestartContainerAsync(Uri baseUri, string container)
    {
        _ = await ProcessAsync("docker", ["kill", "--signal", "KILL", container]).ConfigureAwait(false);
        _ = await ProcessAsync("docker", ["start", container]).ConfigureAwait(false);
        await WaitForHostAsync(baseUri).ConfigureAwait(false);
    }

    private static async Task<ContainerExecutionEnvironment> ReadContainerExecutionEnvironmentAsync(string container)
    {
        var architecture = (await ProcessAsync("docker", ["exec", container, "uname", "-m"])
            .ConfigureAwait(false)).Trim();
        var operatingSystem = (await ProcessAsync("docker", ["exec", container, "cat", "/etc/os-release"])
            .ConfigureAwait(false)).Trim();
        var processorCount = int.Parse(
            (await ProcessAsync("docker", ["exec", container, "nproc"]).ConfigureAwait(false)).Trim(),
            CultureInfo.InvariantCulture);
        var memoryLimit = (await ProcessAsync(
            "docker", ["exec", container, "cat", "/sys/fs/cgroup/memory.max"]).ConfigureAwait(false)).Trim();
        var frameworkVersion = (await ProcessAsync("docker", ["exec", container, "dotnet", "--list-runtimes"])
            .ConfigureAwait(false)).Trim();
        var image = await ReadContainerImageProvenanceAsync(
            container, Required("HVO_ISSUE_211_CAMERA_IMAGE")).ConfigureAwait(false);
        return new ContainerExecutionEnvironment(
            architecture,
            operatingSystem,
            processorCount,
            memoryLimit,
            frameworkVersion,
            image);
    }

    private static async Task<IReadOnlyList<ContainerImageProvenance>> ReadServiceImageProvenanceAsync(
        string cameraContainer)
        => new[]
        {
            await ReadContainerImageProvenanceAsync(
                cameraContainer, Required("HVO_ISSUE_211_CAMERA_IMAGE")).ConfigureAwait(false),
            await ReadContainerImageProvenanceAsync(
                Required("HVO_ISSUE_211_COLLECTOR_CONTAINER"), Required("HVO_ISSUE_211_COLLECTOR_IMAGE"))
                .ConfigureAwait(false),
            await ReadContainerImageProvenanceAsync(
                Required("HVO_ISSUE_211_CENTRAL_DENY_CONTAINER"), Required("HVO_ISSUE_211_CENTRAL_DENY_IMAGE"))
                .ConfigureAwait(false),
            await ReadContainerImageProvenanceAsync(
                Required("HVO_ISSUE_211_PROXY_CONTAINER"), Required("HVO_ISSUE_211_CENTRAL_DENY_IMAGE"))
                .ConfigureAwait(false)
        };

    private static async Task<ContainerImageProvenance> ReadContainerImageProvenanceAsync(
        string container,
        string requestedReference)
    {
        var resolvedImageId = (await ProcessAsync(
            "docker", ["image", "inspect", "--format", "{{.Id}}", requestedReference]).ConfigureAwait(false)).Trim();
        var containerImageId = (await ProcessAsync(
            "docker", ["inspect", "--format", "{{.Image}}", container]).ConfigureAwait(false)).Trim();
        Assert.IsTrue(resolvedImageId.StartsWith("sha256:", StringComparison.Ordinal));
        Assert.AreEqual(resolvedImageId, containerImageId);
        return new ContainerImageProvenance(container, requestedReference, resolvedImageId, containerImageId);
    }

    private static async Task<string> ProcessAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed: {error}");
        }
        return string.Concat(output, error);
    }

    private sealed record ManifestObservation(string Path, ArtifactManifestV2 Manifest);

    private sealed record ProductManifestObservation(string Path, DurableProcessingProductManifestV1 Manifest);

    private sealed record RawAdmissionEvidence(
        Guid CaptureId,
        long CaptureSequence,
        long PayloadLength,
        DateTimeOffset ExposureStartedUtc);

    private sealed record ExactCaptureWindow(
        IReadOnlyList<RawAdmissionEvidence> Admissions,
        IReadOnlyList<CameraAgentGalleryCapture> Captures,
        TimeSpan Elapsed);

    private sealed record ContainerExecutionEnvironment(
        string Architecture,
        string OperatingSystemRelease,
        int ProcessorCount,
        string CgroupMemoryLimitBytes,
        string FrameworkVersion,
        ContainerImageProvenance Image);

    private sealed record ContainerImageProvenance(
        string Container,
        string RequestedReference,
        string ResolvedImageId,
        string ContainerImageId);

    private sealed record RuntimeIdentityEvidence(
        string ActiveRevisionId,
        long ActiveRevisionNumber,
        string ActiveProfileSha256,
        string ActiveScheduleSha256,
        string PipelineRevisionId,
        string PipelineProfileSha256,
        string DesiredGraphSha256,
        string EffectiveGraphSha256);

    private sealed record EnvironmentalEvidence(
        int SourceCount,
        int HistoryCount,
        int AssociationCount,
        int FreshAssociationCount,
        int SelectedAssociationCount,
        string RigId,
        int UiSourceCount,
        int UiHistoryCount,
        int UiAttemptCount);

    private sealed record CaptureProvenanceEvidence(
        Guid CaptureId,
        long CaptureSequence,
        string LocalProfileSha256,
        string ScheduleRevisionId,
        string ScheduleRevisionSha256,
        string DeploymentLocationId,
        long DeploymentLocationVersion,
        CaptureLocationProvenance Location);

    private sealed record ScheduleRoundTripEvidence(
        string OriginalProfileHashPrefix,
        string RestoredProfileHashPrefix,
        long DurableRollbackCommandCount,
        long DurableActivationCommandCount);

    private sealed record MonoReadoutEvidence(
        Guid CaptureId,
        long CaptureSequence,
        int Width,
        int Height,
        int StrideBytes,
        long ByteLength,
        CameraPixelFormat PixelFormat,
        FrameStoredCodeTransform StoredCodeTransform,
        FrameLevelCodeSpace LevelCodeSpace,
        FrameReadoutDescriptor Readout,
        string LocalProfileSha256,
        string ScheduleRevisionId,
        string ScheduleRevisionSha256,
        string DeploymentLocationId,
        long DeploymentLocationVersion,
        CaptureLocationProvenance Location);

    private sealed record PipelinePerformanceEvidence(
        IReadOnlyList<CapturePerformanceObservation> Captures,
        DistributionSummary StartIntervalMilliseconds,
        DistributionSummary ModuleDurationMilliseconds,
        DistributionSummary ModuleOverheadMilliseconds,
        DistributionSummary IngressDurationMilliseconds,
        DistributionSummary GraphDurationMilliseconds,
        DistributionSummary EndToEndDurationMilliseconds,
        DistributionSummary CadenceHeadroomMilliseconds,
        IReadOnlyList<ProcessingNodePerformanceEvidence> Nodes);

    private sealed record CapturePerformanceObservation(
        long CaptureSequence,
        DateTimeOffset ModuleCallStartedUtc,
        double ModuleDurationMilliseconds,
        double ModuleOverheadMilliseconds,
        double IngressDurationMilliseconds,
        double GraphDurationMilliseconds,
        double EndToEndDurationMilliseconds);

    private sealed record ProcessingNodeTiming(
        string NodeId,
        long StartedUnixMs,
        long CompletedUnixMs,
        double DurationMilliseconds);

    private sealed record ProcessingNodePerformanceEvidence(
        string NodeId,
        DistributionSummary DurationMilliseconds);

    private sealed record DistributionSummary(
        IReadOnlyList<double> Samples,
        int SampleCount,
        double Minimum,
        double Average,
        double Median,
        double? P95,
        double Maximum);

    private sealed record RetainedArtifactEvidence(
        Guid ArtifactId,
        string Role,
        string Variant,
        string PayloadPath,
        string ManifestPath,
        long ByteLength,
        string Sha256);

    private sealed record SanitizedPreviewEvidence(
        Guid SourceArtifactId,
        Guid BaselineArtifactId,
        string RelativePath,
        string BaselineRelativePath,
        long ByteLength,
        string Sha256,
        int Width,
        int Height,
        IReadOnlyList<JpegMarkerEvidence> Markers,
        IReadOnlyList<JpegMarkerEvidence> CardinalMarkers);

    private sealed record JpegMarkerEvidence(
        string RowId,
        double ExpectedX,
        double ExpectedY,
        double ActualX,
        double ActualY,
        double ErrorPixels,
        int ChangedPoints);

    private sealed record OptionalQualityEvidence(
        IReadOnlyList<CaptureIdentityEvidence> DisabledCaptures,
        RestoredCaptureIdentity RestoredCapture);

    private sealed record CaptureIdentityEvidence(Guid CaptureId, long CaptureSequence);

    private sealed record RestoredCaptureIdentity(
        Guid CaptureId,
        long CaptureSequence,
        Guid QualityArtifactId,
        string LocalProfileSha256,
        string ProcessingProfileSha256,
        IReadOnlyList<RestoredNodeIdentity> Nodes);

    private sealed record RestoredNodeIdentity(string NodeId, string PlanSha256, int Attempt);

    private sealed record RestartEvidence(
        DurableSnapshot Before,
        DurableSnapshot After,
        double DrainMilliseconds,
        int InterruptedCaptureCount);

    private sealed record GracefulShutdownEvidence(
        int ConfiguredDrainSeconds,
        double ShutdownMilliseconds,
        DurableSnapshot Before,
        DurableSnapshot Stopped,
        DurableSnapshot After,
        double RecoveryMilliseconds,
        int InterruptedCaptureCount);

    private sealed record CalibrationUiEvidence(
        string OriginalBundleId,
        string AcquiredBundleId,
        string RolledBackBundleId);

    private sealed record SemanticFaultEvidence(
        string OperationId,
        string Boundary,
        FaultHitEvidence Hit,
        bool ProcessKilled,
        bool Recovered,
        double? RecoveryMilliseconds,
        double? HitToKillMilliseconds,
        RawPublicationEvidence? RawPublication,
        AnnotationPublicationEvidence? AnnotationPublication,
        TransientCandidateBoundaryEvidence? TransientCandidate,
        WeatherRetryEvidence? WeatherRetry);

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "System.Text.Json constructs fault-hit evidence from the acceptance control file.")]
    private sealed record FaultHitEvidence(
        string OperationId,
        string Boundary,
        string? NodeId,
        DateTimeOffset HitUtc);

    private sealed record RawPublicationEvidence(
        Guid CaptureId,
        long CaptureSequence,
        Guid ArtifactId,
        string PayloadSha256,
        string ManifestSha256);

    private sealed record AnnotationPublicationEvidence(
        AnnotationPublicationSnapshot BeforeKill,
        AnnotationPublicationSnapshot AfterRecovery);

    private sealed record AnnotationPublicationSnapshot(
        Guid CaptureId,
        long CaptureSequence,
        Guid ArtifactId,
        string Role,
        string Variant,
        string PayloadSha256,
        string ManifestSha256,
        string OutputIdentitySha256,
        string RecipeIdentitySha256,
        IReadOnlyList<Guid> OrderedSourceArtifactIds);

    private sealed record TransientCandidateBoundaryEvidence(
        TransientCandidateBoundarySnapshot BeforeKill,
        TransientCandidateBoundarySnapshot AfterRecovery);

    private sealed record TransientCandidateBoundarySnapshot(
        Guid CandidateId,
        Guid EventId,
        string ReservationIdentitySha256,
        string CandidatePayloadSha256,
        string State,
        string Phase,
        Guid? ObservationId,
        Guid? AssessmentId,
        Guid? EventVersionId,
        DateTimeOffset? AllocatedUtc,
        string? CausalExtractionSha256,
        string? CenteredExtractionSha256,
        string? AssessmentExecutionSha256,
        string? WorkerState,
        Guid? TargetCaptureId,
        long? TargetCaptureSequence,
        string? FinalizationPayloadSha256,
        string? FinalizationReceiptIdentitySha256,
        IReadOnlyList<TransientSourceIdentity> Sources,
        TransientBoundaryCounts Counts);

    private sealed record TransientSourceIdentity(
        int Ordinal,
        Guid EvidenceId,
        Guid CaptureId,
        long CaptureSequence,
        Guid ArtifactId,
        int ArtifactRole,
        string ArtifactVariant,
        string RecipeIdentitySha256,
        string ChecksumSha256);

    private sealed record TransientBoundaryCounts(
        long CandidateRows,
        long EventIdentityRows,
        long FinalizedCandidates,
        long FinalizedEvents,
        long ConflictsOrQuarantine);

    private sealed record WeatherRetryEvidence(
        WeatherRetrySnapshot Retry,
        WeatherRecoverySnapshot Recovery,
        CaptureIdentityEvidence ContinuationCapture,
        HealthCheckEvidence DegradedHealth,
        HealthCheckEvidence RecoveredHealth);

    private sealed record WeatherRetrySnapshot(
        Guid CaptureId,
        long CaptureSequence,
        string NodeStatus,
        int NodeAttempt,
        string Reason,
        string LaneState,
        int LaneAttempt,
        DateTimeOffset AvailableUtc,
        IReadOnlyList<CaptureArtifactIdentity> Artifacts);

    private sealed record WeatherRecoverySnapshot(
        Guid CaptureId,
        long CaptureSequence,
        int LaneAttempt,
        IReadOnlyList<NodeAttemptEvidence> NodeAttempts,
        IReadOnlyList<CaptureArtifactIdentity> Artifacts);

    private sealed record NodeAttemptEvidence(string NodeId, int Attempt);

    private sealed record CaptureArtifactIdentity(
        Guid ArtifactId,
        string Role,
        string Variant,
        string ChecksumSha256);

    private sealed record HealthCheckEvidence(
        string OverallStatus,
        string CheckStatus,
        long? PendingCount,
        long? RetryCount,
        long? TerminalCount,
        string? Reason);

    private sealed record PressureDrainEvidence(
        long InitialCaptureCount,
        long InitialRawBytes,
        double InitialOldestAgeSeconds,
        long InitialRetentionHoldCount,
        DateTimeOffset PressureStartedUtc,
        double PressureDurationSeconds,
        long PressuredAvailableBytes,
        long RecoveredAvailableBytes,
        double DrainSeconds,
        double DrainCapturesPerSecond,
        IReadOnlyList<ArtifactFileEvidence> RawArtifacts,
        EligibleRetentionEvidence EligibleRetention,
        string DegradedHealth,
        string RecoveredHealth);

    private sealed record EligibleRetentionEvidence(
        double HeldSeconds,
        double CleanupSeconds,
        IReadOnlyList<EligibleRetentionArtifactEvidence> Artifacts);

    private sealed record EligibleRetentionArtifactEvidence(
        Guid ArtifactId,
        string Role,
        string PayloadRelativePath,
        string SidecarRelativePath,
        [property: System.Text.Json.Serialization.JsonIgnore] string PayloadPath,
        [property: System.Text.Json.Serialization.JsonIgnore] string SidecarPath,
        string PayloadSha256,
        string SidecarSha256);

    private sealed record ArtifactFileEvidence(
        Guid CaptureId,
        Guid ArtifactId,
        string PayloadRelativePath,
        string ManifestRelativePath,
        [property: System.Text.Json.Serialization.JsonIgnore] string PayloadPath,
        [property: System.Text.Json.Serialization.JsonIgnore] string ManifestPath,
        string PayloadSha256,
        string ManifestSha256);

    private sealed record DrainSnapshot(
        long CaptureCount,
        long RawBytes,
        TimeSpan OldestAge,
        long RetentionHoldCount,
        long IncompleteStandardWorkCount,
        long DuplicateLogicalOutputs);

    private sealed record StorageObservation(
        DateTimeOffset ObservedUtc,
        OperationsStorageState State);

    private sealed record GeometryEvidence(
        string FixtureId,
        string ReferenceModel,
        DateTimeOffset SceneUtc,
        IReadOnlyList<GeometryObjectEvidence> Objects,
        ProjectionAnnotationLandmarks CardinalLandmarks,
        IReadOnlyDictionary<string, PackedMarkerObservation> RenderedCardinalMarkers,
        long WeatherOverlayChangedPixels,
        CloudOverlayCorrespondenceEvidence WeatherOverlayCorrespondence);

    private sealed record CloudOverlayCorrespondenceEvidence(
        int CloudyTileCount,
        long MatchedBorderPixelCount,
        long UnexpectedBorderPixelCount);

    private sealed record CalibrationEvidence(
        string BundleId,
        string BundleIdentitySha256,
        string ProfileIdentitySha256,
        string AcquisitionModelIdentitySha256,
        string ProfileRelativePath,
        List<CalibrationArtifactEvidence> Artifacts);

    private sealed record CalibrationBoundaryEvidence(
        bool RestartedBeforeActivation,
        CalibrationPublicationSnapshot BeforeRestart,
        CalibrationPublicationSnapshot AfterActivation,
        double? PublicationToRestartMilliseconds,
        double? RestartMilliseconds);

    private sealed record CalibrationPublicationSnapshot(
        string BundleId,
        string BundleIdentitySha256,
        string[] ArtifactSha256,
        bool Active,
        DateTimeOffset CreatedUtc);

    private sealed record CalibrationArtifactEvidence(
        Guid ArtifactId,
        string ReferenceKind,
        string Role,
        int? SourceIndex,
        string ManifestRelativePath,
        string ManifestSha256,
        string PayloadRelativePath,
        string PayloadSha256,
        string[] OrderedSourceArtifactIds);

    private sealed record CalibrationResidualEvidence(
        long SampleCount,
        double RawMeanAbsoluteErrorNativeAdu,
        double CorrectedMeanAbsoluteErrorNativeAdu,
        int RepairableDefectSampleCount,
        double RawRepairableDefectMeanAbsoluteErrorNativeAdu,
        double CorrectedRepairableDefectMeanAbsoluteErrorNativeAdu,
        CalibrationSpatialResidualEvidence[] SpatialRegions);

    private sealed record CalibrationSpatialResidualEvidence(
        string Name,
        long SampleCount,
        double RawMeanAbsoluteErrorNativeAdu,
        double CorrectedMeanAbsoluteErrorNativeAdu);

    private sealed record GeometryObjectEvidence(
        string RowId,
        double ExpectedX,
        double ExpectedY,
        double ProjectedX,
        double ProjectedY,
        double ProjectedErrorPixels,
        double RawCentroidX,
        double RawCentroidY,
        double RawCentroidErrorPixels,
        double PackedMarkerX,
        double PackedMarkerY,
        double PackedMarkerErrorPixels,
        int PackedMarkerChangedPoints);

    private sealed record PackedMarkerObservation(double X, double Y, int ChangedPoints);

    private sealed record TransientSnapshot(
        long PendingFrames,
        long PendingCandidates,
        long PendingCaptureWork,
        long ActiveSourceHolds,
        long QuarantinedOrConflicting,
        long FinalizedCandidates,
        long FinalizedEvents,
        long CompletedNoCandidateFrames,
        long MinimumFinalizedSourceCount);

    private sealed record TransientEvidence(
        Guid CandidateId,
        Guid EventId,
        string State,
        string Phase,
        long SourceCount,
        Guid CenterCaptureId,
        long CenterCaptureSequence,
        string ScenarioId,
        DateTimeOffset ScenarioEpochUtc,
        TransientInjectedEventEvidence InjectedEvent,
        string CandidateRelativePath,
        string CandidateSha256,
        string FinalizationRelativePath,
        string FinalizationSha256,
        string FinalizationReceiptIdentitySha256,
        TransientExecutionEvidence Execution,
        TransientNoCandidateEvidence NoCandidateControl);

    private sealed record TransientInjectedEventEvidence(
        Guid CaptureId,
        long CaptureSequence,
        Guid ArtifactId,
        string PayloadSha256,
        DateTimeOffset ScenarioEpochUtc);

    private sealed record TransientExecutionEvidence(
        string CausalPayloadSha256,
        string CausalIdentitySha256,
        string CenteredPayloadSha256,
        string CenteredIdentitySha256,
        string AssessmentPayloadSha256,
        string AssessmentIdentitySha256);

    private sealed record TransientBrowserEvidence(
        Guid CandidateId,
        Guid EventId,
        string ScenarioId,
        string CausalIdentitySha256,
        string CenteredIdentitySha256,
        string AssessmentIdentitySha256,
        string FinalizationReceiptIdentitySha256,
        bool MobileNoOverflow);

    private sealed record TransientNoCandidateEvidence(
        Guid CaptureId,
        long CaptureSequence,
        Guid ArtifactId,
        string PayloadSha256,
        string State);

    private sealed record DurableSnapshot(
        long RawCaptureCount,
        long ProcessingOutputCount,
        long PendingRawCaptures,
        long PendingLaneWork,
        long PendingProcessingNodes,
        long DuplicateLogicalOutputs);

    private sealed class FixedUtcTimeProvider(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed class DockerResourceSampler(
        string container,
        string runtimeRoot,
        HttpClient client) : IDisposable
    {
        private readonly List<ResourceSample> _samples = [];
        private readonly List<ResourceSamplingFailure> _failures = [];
        private readonly List<FilesystemSample> _filesystemSamples = [];
        private readonly List<ResourceSamplingFailure> _filesystemFailures = [];
        private readonly List<QueueSample> _queueSamples = [];
        private readonly List<ResourceSamplingFailure> _queueFailures = [];
        private readonly CancellationTokenSource _resourceStop = new();
        private readonly CancellationTokenSource _filesystemStop = new();
        private readonly CancellationTokenSource _queueStop = new();
        private Task? _loop;
        private Task? _filesystemLoop;
        private Task? _queueLoop;

        internal async Task BeginAsync()
        {
            TakeRequiredFilesystemSample(ResourceSampleKind.StartBoundary);
            _filesystemLoop = Task.Run(SampleFilesystemLoopAsync);
            TakeRequiredQueueSample(ResourceSampleKind.StartBoundary);
            _queueLoop = Task.Run(SampleQueueLoopAsync);
            await TakeRequiredBoundarySampleAsync(ResourceSampleKind.StartBoundary).ConfigureAwait(false);
            _loop = Task.Run(SampleLoopAsync);
        }

        internal async Task CompleteAsync()
        {
            await _resourceStop.CancelAsync().ConfigureAwait(false);
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }
            await TakeRequiredBoundarySampleAsync(ResourceSampleKind.EndBoundary).ConfigureAwait(false);
            await _filesystemStop.CancelAsync().ConfigureAwait(false);
            if (_filesystemLoop is not null)
            {
                await _filesystemLoop.ConfigureAwait(false);
            }
            TakeRequiredFilesystemSample(ResourceSampleKind.EndBoundary);
            await _queueStop.CancelAsync().ConfigureAwait(false);
            if (_queueLoop is not null)
            {
                await _queueLoop.ConfigureAwait(false);
            }
            TakeRequiredQueueSample(ResourceSampleKind.EndBoundary);
        }

        internal ResourceSummary Summarize(bool requireContinuousCoverage = true)
        {
            Assert.IsGreaterThanOrEqualTo(2, _samples.Count);
            Assert.AreEqual(ResourceSampleKind.StartBoundary, _samples[0].Kind);
            Assert.AreEqual(ResourceSampleKind.EndBoundary, _samples[^1].Kind);
            var first = _samples[0];
            var last = _samples[^1];
            var maximumSampleGapSeconds = _samples.Zip(_samples.Skip(1), static (left, right) =>
                (right.TimestampUtc - left.TimestampUtc).TotalSeconds).DefaultIfEmpty(0).Max();
            if (requireContinuousCoverage)
            {
                Assert.IsLessThanOrEqualTo(3, maximumSampleGapSeconds,
                    $"Successful resource-sample coverage contained a {maximumSampleGapSeconds:R}-second gap.");
            }
            Assert.IsGreaterThanOrEqualTo(2, _filesystemSamples.Count);
            Assert.AreEqual(ResourceSampleKind.StartBoundary, _filesystemSamples[0].Kind);
            Assert.AreEqual(ResourceSampleKind.EndBoundary, _filesystemSamples[^1].Kind);
            var maximumFilesystemSampleGapSeconds = _filesystemSamples
                .Zip(_filesystemSamples.Skip(1), static (left, right) =>
                    (right.TimestampUtc - left.TimestampUtc).TotalSeconds)
                .DefaultIfEmpty(0)
                .Max();
            Assert.IsLessThanOrEqualTo(3, maximumFilesystemSampleGapSeconds,
                $"Filesystem coverage contained a {maximumFilesystemSampleGapSeconds:R}-second gap.");
            Assert.IsGreaterThanOrEqualTo(2, _queueSamples.Count);
            Assert.AreEqual(ResourceSampleKind.StartBoundary, _queueSamples[0].Kind);
            Assert.AreEqual(ResourceSampleKind.EndBoundary, _queueSamples[^1].Kind);
            var maximumQueueSampleGapSeconds = _queueSamples
                .Zip(_queueSamples.Skip(1), static (left, right) =>
                    (right.TimestampUtc - left.TimestampUtc).TotalSeconds)
                .DefaultIfEmpty(0)
                .Max();
            Assert.IsLessThanOrEqualTo(3, maximumQueueSampleGapSeconds,
                $"Durable-queue coverage contained a {maximumQueueSampleGapSeconds:R}-second gap.");
            var cpuSeconds = _samples.Zip(_samples.Skip(1), static (left, right) =>
                (left.CpuPercent + right.CpuPercent) / 200 *
                (right.TimestampUtc - left.TimestampUtc).TotalSeconds).Sum();
            var queueMaxima = _queueSamples
                .SelectMany(static sample => sample.Backlog.Queues)
                .GroupBy(static queue => queue.Name, StringComparer.Ordinal)
                .Select(static group => new DurableQueueMaximum(
                    group.Key,
                    group.Max(static queue => queue.Count),
                    group.Max(static queue => queue.Bytes),
                    group.Max(static queue => queue.AgeSeconds)))
                .OrderBy(static queue => queue.Name, StringComparer.Ordinal)
                .ToArray();
            return new ResourceSummary(
                _samples.Count,
                _failures.Count,
                _failures.ToArray(),
                maximumSampleGapSeconds,
                _filesystemSamples.Count,
                _filesystemFailures.Count,
                _filesystemFailures.ToArray(),
                maximumFilesystemSampleGapSeconds,
                _queueSamples.Count,
                _queueFailures.Count,
                _queueFailures.ToArray(),
                maximumQueueSampleGapSeconds,
                cpuSeconds,
                _samples.Max(static sample => sample.ProcessRssBytes),
                _samples.Max(static sample => sample.ContainerMemoryUsageBytes),
                Math.Max(0, last.AllocatedBytes - first.AllocatedBytes),
                _samples.Max(static sample => sample.LastCollectionLohBytes),
                Math.Max(0, last.BlockReadBytes - first.BlockReadBytes),
                Math.Max(0, last.BlockWriteBytes - first.BlockWriteBytes),
                Math.Max(0, last.ProcessReadBytes - first.ProcessReadBytes),
                Math.Max(0, last.ProcessWriteBytes - first.ProcessWriteBytes),
                Math.Max(0, last.SqliteFootprintBytes - first.SqliteFootprintBytes),
                _filesystemSamples.Max(static sample => sample.Bytes),
                _queueSamples.Max(static sample => sample.Backlog.Count),
                _queueSamples.Max(static sample => sample.Backlog.Bytes),
                _queueSamples.Max(static sample => sample.Backlog.AgeSeconds),
                queueMaxima);
        }

        internal void WriteCsv(string path)
        {
            var lines = new List<string>(_samples.Count + 1)
            {
                "timestampUtc,kind,cpuPercent,processRssBytes,containerMemoryUsageBytes,allocatedBytes,lastCollectionLohBytes,containerBlockReadBytes,containerBlockWriteBytes,processReadBytes,processWriteBytes,sqliteFootprintBytes"
            };
            lines.AddRange(_samples.Select(static sample => string.Join(",",
                sample.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                sample.Kind,
                sample.CpuPercent.ToString("R", CultureInfo.InvariantCulture),
                sample.ProcessRssBytes.ToString(CultureInfo.InvariantCulture),
                sample.ContainerMemoryUsageBytes.ToString(CultureInfo.InvariantCulture),
                sample.AllocatedBytes.ToString(CultureInfo.InvariantCulture),
                sample.LastCollectionLohBytes.ToString(CultureInfo.InvariantCulture),
                sample.BlockReadBytes.ToString(CultureInfo.InvariantCulture),
                sample.BlockWriteBytes.ToString(CultureInfo.InvariantCulture),
                sample.ProcessReadBytes.ToString(CultureInfo.InvariantCulture),
                sample.ProcessWriteBytes.ToString(CultureInfo.InvariantCulture),
                sample.SqliteFootprintBytes.ToString(CultureInfo.InvariantCulture))));
            File.WriteAllLines(path, lines);
        }

        internal void WriteFilesystemCsv(string path)
        {
            var lines = new List<string> { "timestampUtc,kind,filesystemBytes" };
            lines.AddRange(_filesystemSamples.Select(static sample => string.Join(",",
                sample.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                sample.Kind,
                sample.Bytes.ToString(CultureInfo.InvariantCulture))));
            File.WriteAllLines(path, lines);
        }

        internal void WriteQueueCsv(string path)
        {
            var lines = new List<string> { "timestampUtc,kind,queue,count,bytes,ageSeconds" };
            lines.AddRange(_queueSamples.SelectMany(sample => sample.Backlog.Queues.Select(queue => string.Join(",",
                sample.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                sample.Kind,
                queue.Name,
                queue.Count.ToString(CultureInfo.InvariantCulture),
                queue.Bytes.ToString(CultureInfo.InvariantCulture),
                queue.AgeSeconds.ToString("R", CultureInfo.InvariantCulture)))));
            File.WriteAllLines(path, lines);
        }

        private async Task SampleLoopAsync()
        {
            var nextSampleUtc = DateTimeOffset.UtcNow.AddSeconds(1);
            while (!_resourceStop.IsCancellationRequested)
            {
                try
                {
                    var delay = nextSampleUtc - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _resourceStop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (_resourceStop.IsCancellationRequested)
                {
                    break;
                }
                _ = await TryTakeSampleAsync(ResourceSampleKind.Periodic).ConfigureAwait(false);
                nextSampleUtc = DateTimeOffset.UtcNow.AddSeconds(1);
            }
        }

        private async Task SampleFilesystemLoopAsync()
        {
            var nextSampleUtc = DateTimeOffset.UtcNow.AddSeconds(1);
            while (!_filesystemStop.IsCancellationRequested)
            {
                try
                {
                    var delay = nextSampleUtc - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _filesystemStop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (_filesystemStop.IsCancellationRequested)
                {
                    break;
                }
                TryTakeFilesystemSample(ResourceSampleKind.Periodic);
                nextSampleUtc = DateTimeOffset.UtcNow.AddSeconds(1);
            }
        }

        private async Task SampleQueueLoopAsync()
        {
            var nextSampleUtc = DateTimeOffset.UtcNow.AddSeconds(1);
            while (!_queueStop.IsCancellationRequested)
            {
                try
                {
                    var delay = nextSampleUtc - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _queueStop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (_queueStop.IsCancellationRequested)
                {
                    break;
                }
                TryTakeQueueSample(ResourceSampleKind.Periodic);
                nextSampleUtc = DateTimeOffset.UtcNow.AddSeconds(1);
            }
        }

        private void TakeRequiredQueueSample(ResourceSampleKind kind)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (TryTakeQueueSample(kind))
                {
                    return;
                }
                Thread.Sleep(100);
            }
            Assert.Fail($"A successful {kind} durable-queue sample was not recorded within 30 seconds.");
        }

        private bool TryTakeQueueSample(ResourceSampleKind kind)
        {
            try
            {
                _queueSamples.Add(new QueueSample(DateTimeOffset.UtcNow, kind, ReadBacklogSample(runtimeRoot)));
                return true;
            }
            catch (Exception exception) when (exception is InvalidDataException or SqliteException or IOException)
            {
                _queueFailures.Add(new ResourceSamplingFailure(
                    DateTimeOffset.UtcNow, kind, exception.GetType().Name));
                return false;
            }
        }

        private void TakeRequiredFilesystemSample(ResourceSampleKind kind)
        {
            if (!TryTakeFilesystemSample(kind))
            {
                Assert.Fail($"A successful {kind} filesystem sample was not recorded.");
            }
        }

        private bool TryTakeFilesystemSample(ResourceSampleKind kind)
        {
            try
            {
                _filesystemSamples.Add(new FilesystemSample(DateTimeOffset.UtcNow, kind, DirectoryBytes(runtimeRoot)));
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _filesystemFailures.Add(
                    new ResourceSamplingFailure(DateTimeOffset.UtcNow, kind, exception.GetType().Name));
                return false;
            }
        }

        private async Task TakeRequiredBoundarySampleAsync(ResourceSampleKind kind)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (await TryTakeSampleAsync(kind).ConfigureAwait(false))
                {
                    return;
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
            Assert.Fail($"A successful {kind} resource sample was not recorded within 30 seconds.");
        }

        private async Task<bool> TryTakeSampleAsync(ResourceSampleKind kind)
        {
            try
            {
                var output = await ProcessAsync("docker", ["stats", "--no-stream", "--format", "{{json .}}", container])
                    .ConfigureAwait(false);
                using var document = JsonDocument.Parse(output);
                var cpu = double.Parse(
                    document.RootElement.GetProperty("CPUPerc").GetString()!.TrimEnd('%'),
                    CultureInfo.InvariantCulture);
                var containerMemory = ParseDockerBytes(
                    document.RootElement.GetProperty("MemUsage").GetString()!.Split('/')[0].Trim());
                var processRss = await ReadProcessRssAsync(container).ConfigureAwait(false);
                var processIo = await ReadProcessIoAsync(container).ConfigureAwait(false);
                var blockIo = document.RootElement.GetProperty("BlockIO").GetString()!.Split('/');
                var metrics = await ReadMetricsAsync(client).ConfigureAwait(false);
                _samples.Add(new ResourceSample(
                    DateTimeOffset.UtcNow,
                    kind,
                    cpu,
                    processRss,
                    containerMemory,
                    checked((long)ReadPrometheusValue(metrics, "dotnet_gc_heap_total_allocated_bytes_total")),
                    checked((long)ReadPrometheusValue(
                        metrics,
                        "dotnet_gc_last_collection_heap_size_bytes",
                        "gc_heap_generation=\"loh\"")),
                    ParseDockerBytes(blockIo[0].Trim()),
                    ParseDockerBytes(blockIo[1].Trim()),
                    processIo.ReadBytes,
                    processIo.WriteBytes,
                    SqliteFootprintBytes(runtimeRoot)));
                return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or
                FormatException or SqliteException or HttpRequestException or IOException or TaskCanceledException)
            {
                _failures.Add(new ResourceSamplingFailure(DateTimeOffset.UtcNow, kind, exception.GetType().Name));
                return false;
            }
        }

        private static long ParseDockerBytes(string value)
        {
            var match = Regex.Match(value, "^([0-9.]+)([kKMGT]?i?B)$", RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                throw new FormatException($"Unsupported Docker byte value: {value}");
            }
            var multiplier = match.Groups[2].Value switch
            {
                "B" => 1d,
                "kB" or "KiB" => 1024d,
                "MB" or "MiB" => 1024d * 1024,
                "GB" or "GiB" => 1024d * 1024 * 1024,
                "TB" or "TiB" => 1024d * 1024 * 1024 * 1024,
                _ => throw new FormatException($"Unsupported Docker byte unit: {value}")
            };
            return checked((long)(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * multiplier));
        }

        private static async Task<long> ReadProcessRssAsync(string containerName)
        {
            var status = await ProcessAsync("docker", ["exec", containerName, "cat", "/proc/1/status"])
                .ConfigureAwait(false);
            var match = Regex.Match(
                status,
                "^VmRSS:[ \\t]+([0-9]+)[ \\t]+kB$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            return match.Success
                ? checked(long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 1024)
                : throw new FormatException("The CameraAgent process RSS was unavailable.");
        }

        private static async Task<(long ReadBytes, long WriteBytes)> ReadProcessIoAsync(string containerName)
        {
            var io = await ProcessAsync("docker", ["exec", containerName, "cat", "/proc/1/io"])
                .ConfigureAwait(false);
            return (Read("read_bytes"), Read("write_bytes"));

            long Read(string name)
            {
                var match = Regex.Match(
                    io,
                    $"^{Regex.Escape(name)}:[ \\t]+([0-9]+)$",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant);
                return match.Success
                    ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
                    : throw new FormatException($"The CameraAgent process {name} counter was unavailable.");
            }
        }

        [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
            Justification = "All backlog queries are fixed acceptance-test literals without external input.")]
        private static BacklogSample ReadBacklogSample(string root)
        {
            var queues = new List<DurableQueueSample>();
            var journalDatabase = Path.Combine(root, "journal", "raw-ingress.db");
            queues.Add(ReadQueue("capture-lanes", journalDatabase,
                """
                SELECT COUNT(*), COALESCE(SUM(raw.payload_length), 0), MIN(work.created_unix_ms)
                FROM capture_lane_work work
                JOIN raw_captures raw ON raw.raw_capture_row_id = work.raw_capture_row_id
                WHERE work.state NOT IN ('completed', 'abandoned', 'quarantined');
                """));
            queues.Add(ReadQueue("processing-retries", journalDatabase,
                """
                SELECT COUNT(*), COALESCE(SUM(raw.payload_length), 0), MIN(nodes.completed_unix_ms)
                FROM processing_nodes nodes
                JOIN raw_captures raw ON raw.capture_id = nodes.capture_id
                WHERE nodes.status = 'RetryableFailure';
                """));
            queues.Add(ReadQueue("transient-capture", journalDatabase,
                """
                SELECT COUNT(*), COALESCE(SUM(raw.payload_length), 0), MIN(work.created_unix_ms)
                FROM transient_capture_work work
                JOIN raw_captures raw ON raw.raw_capture_row_id = work.raw_capture_row_id
                WHERE work.state NOT IN ('completed', 'abandoned', 'quarantined');
                """));
            if (TableExists(journalDatabase, "transient_worker_frames"))
            {
                queues.Add(ReadQueue("transient-frames", journalDatabase,
                """
                SELECT COUNT(*), COALESCE(SUM(raw.payload_length), 0), MIN(work.created_unix_ms)
                FROM transient_worker_frames work
                JOIN raw_captures raw ON raw.raw_capture_row_id = work.raw_capture_row_id
                WHERE work.state NOT IN ('completed', 'abandoned', 'quarantined', 'history');
                """));
                queues.Add(ReadQueue("transient-candidates", journalDatabase,
                """
                SELECT COUNT(*), COALESCE(SUM(raw.payload_length), 0), MIN(work.allocated_unix_ms)
                FROM transient_worker_candidates work
                JOIN raw_captures raw ON raw.raw_capture_row_id = work.target_raw_capture_row_id
                WHERE work.state = 'pending';
                """));
            }
            else
            {
                queues.Add(new DurableQueueSample("transient-frames", 0, 0, 0));
                queues.Add(new DurableQueueSample("transient-candidates", 0, 0, 0));
            }
            queues.Add(ReadQueue("artifact-outbox", Path.Combine(root, "outbox", "artifact-outbox.db"),
                """
                SELECT COUNT(*), COALESCE(SUM(COALESCE(payload_length, length(manifest_bytes))), 0),
                       MIN(created_unix_ms)
                FROM artifact_outbox_records
                WHERE status IN ('pending', 'leased', 'retry');
                """));
            queues.Add(ReadQueue(
                "environmental-outbox",
                Path.Combine(root, ".environment", "environmental-observation-outbox.db"),
                """
                SELECT COUNT(*), COALESCE(SUM(payload_bytes), 0), MIN(created_unix_ms)
                FROM environmental_observation_outbox
                WHERE status IN ('pending', 'leased', 'retry');
                """));
            var fleetDatabase = Path.Combine(root, ".fleet", "fleet-status.db");
            if (File.Exists(fleetDatabase))
            {
                queues.Add(ReadQueue("fleet-outbox", fleetDatabase,
                """
                SELECT COUNT(*), COALESCE(SUM(length(payload)), 0), MIN(created_unix_ms)
                FROM fleet_status_records
                WHERE status IN ('pending', 'leased', 'retry');
                """));
            }
            else
            {
                queues.Add(new DurableQueueSample("fleet-outbox", 0, 0, 0));
            }
            return new BacklogSample(
                queues.Sum(static queue => queue.Count),
                queues.Sum(static queue => queue.Bytes),
                queues.Max(static queue => queue.AgeSeconds),
                queues);

            static bool TableExists(string databasePath, string tableName)
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = $table);";
                command.Parameters.AddWithValue("$table", tableName);
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
            }

            static DurableQueueSample ReadQueue(string name, string databasePath, string query)
            {
                if (!File.Exists(databasePath))
                {
                    return new DurableQueueSample(name, 0, 0, 0);
                }
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = query;
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    throw new InvalidDataException($"The {name} durable-queue query returned no aggregate row.");
                }
                var age = reader.IsDBNull(2)
                    ? 0
                    : Math.Max(0, (DateTimeOffset.UtcNow -
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))).TotalSeconds);
                return new DurableQueueSample(name, reader.GetInt64(0), reader.GetInt64(1), age);
            }
        }

        private static long SqliteFootprintBytes(string root) => Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(static path => path.EndsWith(".db", StringComparison.Ordinal) ||
                path.EndsWith(".db-wal", StringComparison.Ordinal) ||
                path.EndsWith(".db-shm", StringComparison.Ordinal))
            .Sum(static path => new FileInfo(path).Length);

        public void Dispose()
        {
            _resourceStop.Dispose();
            _filesystemStop.Dispose();
            _queueStop.Dispose();
        }
    }

    private sealed record ResourceSample(
        DateTimeOffset TimestampUtc,
        ResourceSampleKind Kind,
        double CpuPercent,
        long ProcessRssBytes,
        long ContainerMemoryUsageBytes,
        long AllocatedBytes,
        long LastCollectionLohBytes,
        long BlockReadBytes,
        long BlockWriteBytes,
        long ProcessReadBytes,
        long ProcessWriteBytes,
        long SqliteFootprintBytes);

    private sealed record FilesystemSample(
        DateTimeOffset TimestampUtc,
        ResourceSampleKind Kind,
        long Bytes);

    private sealed record QueueSample(
        DateTimeOffset TimestampUtc,
        ResourceSampleKind Kind,
        BacklogSample Backlog);

    private sealed record BacklogSample(
        long Count,
        long Bytes,
        double AgeSeconds,
        IReadOnlyList<DurableQueueSample> Queues);

    private sealed record DurableQueueSample(string Name, long Count, long Bytes, double AgeSeconds);

    private sealed record DurableQueueMaximum(string Name, long Count, long Bytes, double AgeSeconds);

    private sealed record DurableQueueBound(long Count, long Bytes, double AgeSeconds);

    private sealed record ResourceSamplingFailure(
        DateTimeOffset TimestampUtc,
        ResourceSampleKind Kind,
        string ExceptionType);

    private enum ResourceSampleKind
    {
        StartBoundary,
        Periodic,
        EndBoundary
    }

    private sealed record ResourceSummary(
        int SampleCount,
        int SamplingFailureCount,
        IReadOnlyList<ResourceSamplingFailure> SamplingFailures,
        double MaximumSuccessfulSampleGapSeconds,
        int FilesystemSampleCount,
        int FilesystemSamplingFailureCount,
        IReadOnlyList<ResourceSamplingFailure> FilesystemSamplingFailures,
        double MaximumFilesystemSampleGapSeconds,
        int QueueSampleCount,
        int QueueSamplingFailureCount,
        IReadOnlyList<ResourceSamplingFailure> QueueSamplingFailures,
        double MaximumQueueSampleGapSeconds,
        double EstimatedCpuSeconds,
        long PeakRssBytes,
        long PeakContainerMemoryUsageBytes,
        long AllocatedBytes,
        long MaximumObservedLastCollectionLohBytes,
        long BlockReadBytes,
        long BlockWriteBytes,
        long ProcessReadBytes,
        long ProcessWriteBytes,
        long SqliteFootprintGrowthBytes,
        long PeakFilesystemBytes,
        long MaximumBacklogCount,
        long MaximumBacklogBytes,
        double MaximumBacklogAgeSeconds,
        IReadOnlyList<DurableQueueMaximum> MaximumDurableQueues);
}
