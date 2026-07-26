using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "The retained-evidence harness passes only fixed SQL queries to its helpers.")]
public sealed class Issue208LegacyAndW0FaultRetainedEvidenceTests
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif
    private const int Width = 64;
    private const int Height = 48;
    private const int SourcesPerKind = 3;
    private const int Seed = 208;
    private const string ExpectedLegacyBundleId = "legacy-517BBB80CB2C7BF36CEC36F5832833E7";
    private const string ExpectedLegacyProfileIdentitySha256 =
        "0F37C86AC1F98FCB5EC3ADA82CCAC271A091E430D44174860ACF9D230A3B5851";
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions EvidenceJsonOptions = CreateEvidenceJsonOptions();
    private static readonly Dictionary<string, FileIdentity> ExpectedLegacyFiles = new(StringComparer.Ordinal)
    {
        ["bias.bin"] = new(6144, "70F4CE894BD99D133E756673F14A7C61379DEBD5666B12F16C19685496999654"),
        ["bias.json"] = new(2678, "E0335989251732B5D03EE9BCCA991F2558E77AB43D42ACD2055A9826BA246E79"),
        ["calibration-profile.json"] = new(1309, ExpectedLegacyProfileIdentitySha256),
        ["dark.bin"] = new(6144, "63390266A137CE75B7206685D5EF7E4127B59CF4E15666FEE318EE08448EE545"),
        ["dark.json"] = new(2646, "2150E2463691316DB7D899B701E56AB21CB6AD70D927C336ECC726AF7D0771F3"),
        ["defect.bin"] = new(6144, "8BBE6FB07EAB2C1D495B0ACE6EA73B0E05780594CE5D7622A507FB3375E4135A"),
        ["defect.json"] = new(2684, "DE7D81F95C51815513CA08B3063984B020CB619B82DF7AE5E98152CA95FFFC10"),
        ["flat.bin"] = new(6144, "DD9C69178B6E074F2DDAFEC3ED7EE26A107D27A3977B090BD8A94B74866DBB69"),
        ["flat.json"] = new(2646, "E0F955988AABACB1109202297BA0796F89FD0DFAC36830BF798927B5356EA08F")
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task C208LegacyAndW0Fault_RetainsCanonicalRestartEvidence()
    {
        if (!string.Equals(BuildConfiguration, "Release", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Issue #208 retained evidence requires a Release build.");
        }

        var repositoryRoot = GetRepositoryRoot();
        var git = await ReadGitEvidenceAsync(repositoryRoot).ConfigureAwait(false);
        var revision = GetEvidenceRevision(git);
        var outputDirectory = Path.Combine(repositoryRoot, "TestResults", "issue-208", revision);
        var outputPath = Path.Combine(outputDirectory, "c208-legacy-w0-fault-retained-evidence.json");
        var legacyRoot = CreateTemporaryRoot("legacy");
        var w0Root = CreateTemporaryRoot("w0");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var wallStarted = Stopwatch.GetTimestamp();
            var cpuStarted = process.TotalProcessorTime;
            var allocatedStarted = GC.GetTotalAllocatedBytes(precise: true);
            var workingSetStarted = process.WorkingSet64;

            var legacy = await RunLegacyAsync(legacyRoot).ConfigureAwait(false);
            var faultTrials = new List<W0FaultTrialEvidence>();
            foreach (var boundary in CreatePublicationBoundaries())
            {
                faultTrials.Add(await RunPublicationFaultTrialAsync(w0Root, boundary).ConfigureAwait(false));
            }
            var terminalBoundaries = await RunTerminalBoundaryEvidenceAsync(w0Root).ConfigureAwait(false);

            process.Refresh();
            var evidence = new
            {
                SchemaVersion = "issue-208-c208-legacy-w0-fault-retained-evidence-v1",
                Issue = 208,
                GeneratedUtc = DateTimeOffset.UtcNow,
                Revision = revision,
                ExactCommand = $"HVO_EVIDENCE_REVISION={revision} dotnet test " +
                    "tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj " +
                    "--configuration Release --filter \"FullyQualifiedName=" +
                    "HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration." +
                    "Issue208LegacyAndW0FaultRetainedEvidenceTests." +
                    "C208LegacyAndW0Fault_RetainsCanonicalRestartEvidence\"",
                Machine = new
                {
                    OperatingSystem = RuntimeInformation.OSDescription,
                    OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                    ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Runtime = RuntimeInformation.FrameworkDescription,
                    RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
                    RuntimeVersion = Environment.Version.ToString(),
                    Configuration = BuildConfiguration,
                    PinnedSdk = ReadPinnedSdkVersion(repositoryRoot),
                    ServerGc = GCSettings.IsServerGC,
                    ProcessorCount = Environment.ProcessorCount,
                    Cpu = ReadCpuModel(),
                    TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                    StopwatchFrequency = Stopwatch.Frequency,
                    Storage = new
                    {
                        FileSystem = new DriveInfo(Path.GetPathRoot(w0Root)!).DriveFormat,
                        PathRoot = Path.GetPathRoot(w0Root),
                        PhysicalDeviceClass = "Unavailable inside the development-container overlay."
                    }
                },
                Source = new
                {
                    Git = git,
                    Harness = Path.GetRelativePath(repositoryRoot, GetType().Assembly.Location),
                    HarnessSource = "tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/Calibration/" +
                        "Issue208LegacyAndW0FaultRetainedEvidenceTests.cs",
                    ProductionPaths = new[]
                    {
                        typeof(SyntheticCalibrationReferenceStore).FullName,
                        typeof(CalibrationLibraryReconciler).FullName,
                        typeof(CalibrationArtifactPublisher).FullName,
                        typeof(VirtualCalibrationAcquisitionCoordinator).FullName,
                        typeof(SqliteCalibrationLibraryStore).FullName,
                        typeof(CalibrationLibraryOperationsCoordinator).FullName
                    },
                    Assemblies = new[]
                    {
                        ReadAssemblyEvidence(typeof(Issue208LegacyAndW0FaultRetainedEvidenceTests)),
                        ReadAssemblyEvidence(typeof(SqliteCalibrationLibraryStore)),
                        ReadAssemblyEvidence(typeof(CalibrationLibraryBundleV1)),
                        ReadAssemblyEvidence(typeof(CalibrationMasterBuilder))
                    }
                },
                WorkloadManifest = new
                {
                    Legacy = new
                    {
                        Name = "C208-legacy",
                        ValidShippedFormat = ReferenceCalibrationProfileV1.CurrentSchemaVersion,
                        ValidBundleCount = 1,
                        IncompleteCopyCount = 1,
                        CorruptCopyCount = 1
                    },
                    W0Fault = new
                    {
                        Name = "C208-W0-fault",
                        Width,
                        Height,
                        PixelFormat = CameraPixelFormat.Mono16.ToString(),
                        SampleDepthBits = 16,
                        ContainerDepthBits = 16,
                        Seed,
                        Kinds = CalibrationReferenceKinds.All,
                        SourcesPerKind,
                        SourceCount = CalibrationReferenceKinds.All.Count * SourcesPerKind,
                        MasterCount = CalibrationReferenceKinds.All.Count,
                        Concurrency = 1,
                        Backlog = 0,
                        InjectedPublicationBoundaryCount = faultTrials.Count,
                        InjectedTerminalBoundaryCount = 2,
                        SQLitePublicationBoundary = terminalBoundaries.SqlitePublicationBoundary,
                        ActivationBoundary = terminalBoundaries.ActivationBoundary
                    }
                },
                Measurements = new
                {
                    TotalWallMilliseconds = Stopwatch.GetElapsedTime(wallStarted).TotalMilliseconds,
                    TotalCpuMilliseconds = (process.TotalProcessorTime - cpuStarted).TotalMilliseconds,
                    TotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedStarted,
                    WorkingSetStartBytes = workingSetStarted,
                    WorkingSetEndBytes = process.WorkingSet64,
                    Legacy = legacy.Measurements,
                    W0FaultTrials = faultTrials.Select(static trial => trial.Measurements).ToArray(),
                    TerminalBoundaries = terminalBoundaries.Measurements
                },
                Correctness = new
                {
                    Legacy = legacy.Correctness,
                    W0FaultTrials = faultTrials.Select(static trial => trial.Correctness).ToArray(),
                    TerminalBoundaries = terminalBoundaries.Correctness,
                    AllInjectedFaultsObserved = faultTrials.All(static trial => trial.Correctness.FaultObserved) &&
                        terminalBoundaries.Correctness.SqlitePublicationFaultObserved &&
                        terminalBoundaries.Correctness.ActivationFaultObserved,
                    AllFaultRestartsConverged = faultTrials.All(static trial => trial.Correctness.RestartConverged) &&
                        terminalBoundaries.Correctness.SqlitePublicationRestartConverged &&
                        terminalBoundaries.Correctness.ActivationRestartConverged,
                    NoPartialSelection = faultTrials.All(static trial => trial.Correctness.NoPartialSelection) &&
                        terminalBoundaries.Correctness.NoPartialSelection
                }
            };

            await File.WriteAllTextAsync(
                outputPath,
                JsonSerializer.Serialize(evidence, EvidenceJsonOptions)).ConfigureAwait(false);
            TestContext.WriteLine($"Issue #208 retained evidence: {outputPath}");
        }
        finally
        {
            DeleteRoot(legacyRoot);
            DeleteRoot(w0Root);
        }
    }

    private static async Task<LegacyEvidence> RunLegacyAsync(string root)
    {
        var started = Stopwatch.GetTimestamp();
        var options = CreateOptions(root);
        var syntheticStore = new SyntheticCalibrationReferenceStore(
            options, new CameraAgentClearReferenceLoader(options));
        var validModel = LegacyModel(Seed);
        var incompleteModel = LegacyModel(Seed + 1);
        var corruptModel = LegacyModel(Seed + 2);
        var validLight = LegacyLight(validModel);
        var valid = await syntheticStore.GetOrCreateAsync(validLight, validModel, CancellationToken.None)
            .ConfigureAwait(false);
        var incomplete = await syntheticStore.GetOrCreateAsync(
            LegacyLight(incompleteModel), incompleteModel, CancellationToken.None).ConfigureAwait(false);
        var corrupt = await syntheticStore.GetOrCreateAsync(
            LegacyLight(corruptModel), corruptModel, CancellationToken.None).ConfigureAwait(false);
        var validDirectory = RelativeDirectory(valid);
        var incompleteDirectory = RelativeDirectory(incomplete);
        var corruptDirectory = RelativeDirectory(corrupt);
        var incompleteBundleId = $"legacy-{Path.GetFileName(incompleteDirectory)[..32].ToUpperInvariant()}";
        var corruptBundleId = $"legacy-{Path.GetFileName(corruptDirectory)[..32].ToUpperInvariant()}";
        var validBefore = HashDirectory(root, validDirectory);
        Assert.AreEqual(ExpectedLegacyProfileIdentitySha256, valid.ProfileIdentitySha256);
        AssertHashesEqual(ExpectedLegacyFiles, validBefore);

        File.Delete(Resolve(root, $"{incompleteDirectory}/calibration-profile.json"));
        var incompleteBefore = HashDirectory(root, incompleteDirectory);
        var corruptPayload = Resolve(root, $"{corruptDirectory}/{CalibrationReferenceKinds.Bias}.bin");
        var corruptBytes = await File.ReadAllBytesAsync(corruptPayload).ConfigureAwait(false);
        corruptBytes[0] ^= 0xFF;
        await File.WriteAllBytesAsync(corruptPayload, corruptBytes).ConfigureAwait(false);
        var corruptBefore = HashDirectory(root, corruptDirectory);

        var ingress = new InitializingIngress(root);
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        CalibrationLibraryReconciliationSummary first;
        string validBundleId;
        using (var store = new SqliteCalibrationLibraryStore(ingress, options, new FixedTimeProvider(FixedUtcNow)))
        {
            var reconciler = new CalibrationLibraryReconciler(store, options);
            first = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(3, first.Inspected);
            Assert.AreEqual(1, first.Adopted);
            Assert.AreEqual(1, first.Quarantined);
            Assert.AreEqual(1, first.Failed);
            var bundles = await store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, bundles);
            validBundleId = bundles[0].Bundle.BundleId;
            Assert.AreEqual(ExpectedLegacyBundleId, validBundleId);
            Assert.AreEqual(CalibrationLibraryBundleSources.LegacySyntheticV1, bundles[0].Bundle.Source);
            Assert.AreEqual(valid.ProfileIdentitySha256, bundles[0].Bundle.ProfileIdentitySha256);
            Assert.AreEqual(valid.EvidenceFiles.Single(static file =>
                file.RelativePath.EndsWith("/calibration-profile.json", StringComparison.Ordinal)).RelativePath,
                bundles[0].Bundle.ProfileRelativePath);
            var beforeActivation = await store.SelectAsync(validLight, CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(beforeActivation.IsSelected);
            Assert.AreEqual(CalibrationLibraryReasonCodes.Inactive, beforeActivation.ReasonCode);
            await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => store.ActivateAsync(
                incompleteBundleId, "c208-incomplete-must-not-activate", 0, "evidence-harness", null,
                CancellationToken.None)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => store.ActivateAsync(
                corruptBundleId, "c208-corrupt-must-not-activate", 0, "evidence-harness", null,
                CancellationToken.None)).ConfigureAwait(false);
        }

        CalibrationLibraryReconciliationSummary restart;
        CalibrationLibrarySelectionResult selected;
        using (var restarted = new SqliteCalibrationLibraryStore(ingress, options, new FixedTimeProvider(FixedUtcNow)))
        {
            restart = await new CalibrationLibraryReconciler(restarted, options)
                .ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2, restart.Inspected);
            Assert.AreEqual(1, restart.Adopted);
            Assert.AreEqual(0, restart.Quarantined);
            Assert.AreEqual(1, restart.Failed);
            _ = await restarted.ActivateAsync(
                validBundleId, "c208-legacy-activate", 0, "evidence-harness", "valid v1 selection",
                CancellationToken.None).ConfigureAwait(false);
            selected = await restarted.SelectAsync(validLight, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(selected.IsSelected, selected.ReasonCode);
        }

        using (var reopened = new SqliteCalibrationLibraryStore(ingress, options, new FixedTimeProvider(FixedUtcNow)))
        {
            var restartSelected = await reopened.SelectAsync(validLight, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(restartSelected.IsSelected, restartSelected.ReasonCode);
            Assert.AreEqual(validBundleId, restartSelected.Bundle?.Bundle.BundleId);
        }

        var validAfter = HashDirectory(root, validDirectory);
        AssertHashesEqual(validBefore, validAfter);
        Assert.IsFalse(Directory.Exists(Resolve(root, incompleteDirectory)));
        var quarantineDirectory = Directory.EnumerateDirectories(
            Resolve(root, "quarantine/calibration"), "*", SearchOption.TopDirectoryOnly).Single();
        var incompleteAfter = HashDirectoryAbsolute(quarantineDirectory);
        AssertHashesByFileNameEqual(incompleteBefore, incompleteAfter);
        var corruptAfter = HashDirectory(root, corruptDirectory);
        AssertHashesEqual(corruptBefore, corruptAfter);

        using var connection = await OpenAsync(root).ConfigureAwait(false);
        var rows = await ReadSqliteSnapshotAsync(connection).ConfigureAwait(false);
        var corruptReconciliation = await ReadReconciliationSnapshotAsync(connection, corruptDirectory)
            .ConfigureAwait(false);
        Assert.AreEqual(1L, rows.BundleCount);
        Assert.AreEqual(4L, rows.ArtifactCount);
        Assert.AreEqual(1L, rows.ActivationCount);
        Assert.AreEqual(validBundleId, rows.ActiveBundleId);
        Assert.AreEqual("failed", corruptReconciliation.Outcome);
        Assert.AreEqual(CalibrationLibraryReasonCodes.Corrupt, corruptReconciliation.Reason);
        Assert.AreEqual("completed", corruptReconciliation.OperationState);
        Assert.IsNull(corruptReconciliation.QuarantineRelativePath);
        Assert.AreEqual(corruptAfter.Values.Sum(static file => file.Length), corruptReconciliation.ObservedBytes);
        Assert.IsNotNull(corruptReconciliation.CompletedUnixMs);
        return new LegacyEvidence(
            new
            {
                WallMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ValidFiles = validAfter.Count,
                ValidBytes = validAfter.Values.Sum(static file => file.Length),
                IncompleteFilesPreserved = incompleteAfter.Count,
                CorruptFilesPreserved = corruptAfter.Count,
                SQLite = rows
            },
            new
            {
                FirstReconciliation = first,
                RestartReconciliation = restart,
                ValidBundleId = validBundleId,
                ValidProfileIdentitySha256 = valid.ProfileIdentitySha256,
                PinnedValidProfileIdentitySha256 = ExpectedLegacyProfileIdentitySha256,
                ValidFiles = validAfter,
                PinnedValidFiles = ExpectedLegacyFiles,
                IncompleteOriginalRelativePath = incompleteDirectory,
                IncompleteQuarantineRelativePath = Normalize(Path.GetRelativePath(root, quarantineDirectory)),
                IncompleteFiles = incompleteAfter,
                IncompleteRejectedBundleId = incompleteBundleId,
                IncompleteActivationOutcome = nameof(KeyNotFoundException),
                IncompleteSelectable = false,
                CorruptRelativePath = corruptDirectory,
                CorruptFiles = corruptAfter,
                CorruptRejectedBundleId = corruptBundleId,
                CorruptActivationOutcome = nameof(KeyNotFoundException),
                CorruptReconciliation = corruptReconciliation,
                PublishedBundleCount = rows.BundleCount,
                SelectedBundleId = selected.Bundle?.Bundle.BundleId,
                ValidBytesAndPathsPreserved = ExpectedLegacyFiles.SequenceEqual(validAfter),
                InvalidEvidencePreserved = corruptBefore.SequenceEqual(corruptAfter) &&
                    incompleteBefore.Values.OrderBy(static file => file.Sha256)
                        .SequenceEqual(incompleteAfter.Values.OrderBy(static file => file.Sha256)),
                InvalidEvidenceExcludedFromSQLitePublication = rows.BundleCount == 1
            });
    }

    private static async Task<W0FaultTrialEvidence> RunPublicationFaultTrialAsync(
        string parentRoot,
        PublicationBoundary boundary)
    {
        var root = Path.Combine(parentRoot, $"{boundary.Ordinal:D2}-{boundary.Name}");
        Directory.CreateDirectory(root);
        var injector = new TargetedFaultInjector(boundary.Point, boundary.RelativePathSuffix);
        var initialStarted = Stopwatch.GetTimestamp();
        var process = Process.GetCurrentProcess();
        var cpuStarted = process.TotalProcessorTime;
        var allocatedStarted = GC.GetTotalAllocatedBytes(precise: true);
        VirtualCalibrationAcquisitionPlanV1 plan;
        IReadOnlyDictionary<string, FileIdentity> filesAtFault;
        SqliteSnapshot atFault;

        using (var interrupted = await W0Fixture.CreateAsync(root, injector).ConfigureAwait(false))
        {
            try
            {
                _ = await interrupted.Coordinator.AcquireAsync(
                    Request($"c208-w0-{boundary.Ordinal:D2}"), CancellationToken.None).ConfigureAwait(false);
                Assert.Fail("The targeted C208 publication fault was not observed.");
            }
            catch (InjectedCalibrationFaultException)
            {
            }
            Assert.IsTrue(injector.WasInjected);
            Assert.IsTrue(injector.ObservedRelativePath.EndsWith(
                boundary.RelativePathSuffix, StringComparison.Ordinal));
            var pending = await interrupted.Store.ReadPendingAcquisitionJobAsync(CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(pending);
            plan = pending.Plan;
            AssertPlan(plan);
            var beforeRestartSelection = await interrupted.Store.SelectAsync(
                CreateCompatibleLight(plan), CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(beforeRestartSelection.IsSelected);
            Assert.AreEqual(CalibrationLibraryReasonCodes.Missing, beforeRestartSelection.ReasonCode);
            Assert.IsEmpty(await interrupted.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
            filesAtFault = HashCalibrationFiles(root);
            using var connection = await OpenAsync(root).ConfigureAwait(false);
            atFault = await ReadSqliteSnapshotAsync(connection).ConfigureAwait(false);
            Assert.AreEqual(0L, atFault.BundleCount);
            Assert.AreEqual(0L, atFault.ActivationCount);
            Assert.AreEqual(1L, atFault.NonterminalJobCount);
        }
        var initialMilliseconds = Stopwatch.GetElapsedTime(initialStarted).TotalMilliseconds;

        var restartStarted = Stopwatch.GetTimestamp();
        CalibrationAcquisitionJobSnapshot resumed;
        CalibrationLibrarySelectionResult afterRestartSelection;
        SqliteSnapshot afterRestart;
        using (var restarted = await W0Fixture.CreateAsync(root).ConfigureAwait(false))
        {
            resumed = await restarted.Coordinator.ResumePendingAsync(CancellationToken.None).ConfigureAwait(false)
                ?? throw new AssertFailedException("The interrupted W0 acquisition did not resume.");
            Assert.AreEqual(CalibrationAcquisitionStates.Published, resumed.State);
            Assert.AreEqual(plan.JobId, resumed.Plan.JobId);
            var bundles = await restarted.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, bundles);
            AssertBundle(bundles[0].Bundle);
            afterRestartSelection = await restarted.Store.SelectAsync(
                CreateCompatibleLight(plan), CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(afterRestartSelection.IsSelected);
            Assert.AreEqual(CalibrationLibraryReasonCodes.Inactive, afterRestartSelection.ReasonCode);
            using var connection = await OpenAsync(root).ConfigureAwait(false);
            afterRestart = await ReadSqliteSnapshotAsync(connection).ConfigureAwait(false);
        }
        var restartMilliseconds = Stopwatch.GetElapsedTime(restartStarted).TotalMilliseconds;
        var filesAfterRestart = HashCalibrationFiles(root);
        foreach (var file in filesAtFault)
        {
            Assert.IsTrue(filesAfterRestart.TryGetValue(file.Key, out var after), file.Key);
            Assert.AreEqual(file.Value, after, file.Key);
        }
        Assert.AreEqual(1L, afterRestart.BundleCount);
        Assert.AreEqual(16L, afterRestart.ArtifactCount);
        Assert.AreEqual(0L, afterRestart.NonterminalJobCount);
        Assert.AreEqual(0L, afterRestart.ActivationCount);
        Assert.IsNull(afterRestart.ActiveBundleId);

        return new W0FaultTrialEvidence(
            new W0TrialMeasurements(
                boundary.Ordinal,
                boundary.Name,
                boundary.Point.ToString(),
                boundary.RelativePathSuffix,
                initialMilliseconds,
                restartMilliseconds,
                (process.TotalProcessorTime - cpuStarted).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(precise: true) - allocatedStarted,
                filesAtFault.Count,
                filesAtFault.Values.Sum(static file => file.Length),
                filesAfterRestart.Count,
                filesAfterRestart.Values.Sum(static file => file.Length),
                atFault,
                afterRestart),
            new W0TrialCorrectness(
                boundary.Ordinal,
                boundary.Name,
                true,
                injector.ObservedRelativePath,
                plan.JobId,
                plan.SourceModelIdentitySha256,
                atFault.BundleCount == 0 && atFault.ActivationCount == 0 &&
                    !afterRestartSelection.IsSelected,
                resumed.State == CalibrationAcquisitionStates.Published &&
                    afterRestart.BundleCount == 1 && afterRestart.ArtifactCount == 16,
                filesAtFault.All(file => filesAfterRestart.TryGetValue(file.Key, out var after) && after == file.Value),
                atFault.NonterminalJobCount - 1,
                afterRestartSelection.ReasonCode));
    }

    private static async Task<TerminalBoundaryEvidence> RunTerminalBoundaryEvidenceAsync(string parentRoot)
    {
        var sqliteRoot = Path.Combine(parentRoot, "terminal-sqlite-publication");
        Directory.CreateDirectory(sqliteRoot);
        var sqliteInjector = new TargetedFaultInjector(
            CalibrationPublicationFaultPoint.AfterSqlitePublication,
            "/reference-calibration-profile.json");
        var sqliteFaultStarted = Stopwatch.GetTimestamp();
        VirtualCalibrationAcquisitionPlanV1 sqlitePlan;
        IReadOnlyDictionary<string, FileIdentity> sqliteFilesAtFault;
        SqliteSnapshot sqliteAtFault;
        using (var interrupted = await W0Fixture.CreateAsync(sqliteRoot, sqliteInjector).ConfigureAwait(false))
        {
            try
            {
                _ = await interrupted.Coordinator.AcquireAsync(
                    Request("c208-w0-after-sqlite-publication"), CancellationToken.None).ConfigureAwait(false);
                Assert.Fail("The post-SQLite-publication fault was not observed.");
            }
            catch (InjectedCalibrationFaultException)
            {
            }
            Assert.IsTrue(sqliteInjector.WasInjected);
            var pending = await interrupted.Store.ReadPendingAcquisitionJobAsync(CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(pending);
            sqlitePlan = pending.Plan;
            AssertPlan(sqlitePlan);
            Assert.AreEqual(CalibrationAcquisitionStates.Publishing, pending.State);
            Assert.AreEqual("profile-published", pending.Phase);
            var bundle = (await interrupted.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false)).Single();
            AssertBundle(bundle.Bundle);
            var inactive = await interrupted.Store.SelectAsync(
                CreateCompatibleLight(sqlitePlan), CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(inactive.IsSelected);
            Assert.AreEqual(CalibrationLibraryReasonCodes.Inactive, inactive.ReasonCode);
            sqliteFilesAtFault = HashCalibrationFiles(sqliteRoot);
            using var connection = await OpenAsync(sqliteRoot).ConfigureAwait(false);
            sqliteAtFault = await ReadSqliteSnapshotAsync(connection).ConfigureAwait(false);
        }
        var sqliteFaultMilliseconds = Stopwatch.GetElapsedTime(sqliteFaultStarted).TotalMilliseconds;
        Assert.AreEqual(1L, sqliteAtFault.BundleCount);
        Assert.AreEqual(16L, sqliteAtFault.ArtifactCount);
        Assert.AreEqual(1L, sqliteAtFault.NonterminalJobCount);
        Assert.AreEqual(0L, sqliteAtFault.StateVersion);
        Assert.IsNull(sqliteAtFault.ActiveBundleId);

        var sqliteRestartStarted = Stopwatch.GetTimestamp();
        SqliteSnapshot sqliteAfterRestart;
        CalibrationLibrarySelectionResult sqliteRestartSelection;
        using (var restarted = await W0Fixture.CreateAsync(sqliteRoot).ConfigureAwait(false))
        {
            var resumed = await restarted.Coordinator.ResumePendingAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(resumed);
            Assert.AreEqual(CalibrationAcquisitionStates.Published, resumed.State);
            Assert.AreEqual(sqlitePlan.JobId, resumed.Plan.JobId);
            AssertBundle((await restarted.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false)).Single().Bundle);
            sqliteRestartSelection = await restarted.Store.SelectAsync(
                CreateCompatibleLight(sqlitePlan), CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(sqliteRestartSelection.IsSelected);
            Assert.AreEqual(CalibrationLibraryReasonCodes.Inactive, sqliteRestartSelection.ReasonCode);
            using var connection = await OpenAsync(sqliteRoot).ConfigureAwait(false);
            sqliteAfterRestart = await ReadSqliteSnapshotAsync(connection).ConfigureAwait(false);
        }
        var sqliteRestartMilliseconds = Stopwatch.GetElapsedTime(sqliteRestartStarted).TotalMilliseconds;
        var sqliteFilesAfterRestart = HashCalibrationFiles(sqliteRoot);
        AssertHashesEqual(sqliteFilesAtFault, sqliteFilesAfterRestart);
        Assert.AreEqual(1L, sqliteAfterRestart.BundleCount);
        Assert.AreEqual(16L, sqliteAfterRestart.ArtifactCount);
        Assert.AreEqual(0L, sqliteAfterRestart.NonterminalJobCount);
        Assert.AreEqual(0L, sqliteAfterRestart.StateVersion);
        Assert.IsNull(sqliteAfterRestart.ActiveBundleId);

        var activationRoot = Path.Combine(parentRoot, "terminal-activation-commit");
        Directory.CreateDirectory(activationRoot);
        CalibrationAcquisitionJobSnapshot activationAcquisition;
        using (var fixture = await W0Fixture.CreateAsync(activationRoot).ConfigureAwait(false))
        {
            activationAcquisition = await fixture.Coordinator.AcquireAsync(
                Request("c208-w0-after-activation-setup"), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CalibrationAcquisitionStates.Published, activationAcquisition.State);
        }
        var activationFilesBefore = HashCalibrationFiles(activationRoot);
        const string activationKey = "c208-w0-after-activation-commit";
        const string activationReason = "fault after committed activation";
        var activationInjector = new TargetedFaultInjector(
            CalibrationPublicationFaultPoint.AfterActivationCommitted,
            "activate");
        var activationFaultStarted = Stopwatch.GetTimestamp();
        SqliteSnapshot activationAtFault;
        CalibrationLibrarySelectionResult activationSelectionAtFault;
        using (var interrupted = await W0Fixture.CreateAsync(activationRoot, activationInjector).ConfigureAwait(false))
        {
            var operations = new CalibrationLibraryOperationsCoordinator(
                interrupted.Store, interrupted.Admission, telemetry: null, faultInjector: activationInjector);
            try
            {
                _ = await operations.ActivateAsync(
                    activationAcquisition.BundleId!, activationKey, 0, "evidence-harness", activationReason,
                    CancellationToken.None).ConfigureAwait(false);
                Assert.Fail("The post-activation-commit fault was not observed.");
            }
            catch (InjectedCalibrationFaultException)
            {
            }
            Assert.IsTrue(activationInjector.WasInjected);
            activationSelectionAtFault = await interrupted.Store.SelectAsync(
                CreateCompatibleLight(activationAcquisition.Plan), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(activationSelectionAtFault.IsSelected, activationSelectionAtFault.ReasonCode);
            Assert.AreEqual(1L, activationSelectionAtFault.StateVersion);
            Assert.AreEqual(activationAcquisition.BundleId, activationSelectionAtFault.Bundle?.Bundle.BundleId);
            using var connection = await OpenAsync(activationRoot).ConfigureAwait(false);
            activationAtFault = await ReadSqliteSnapshotAsync(connection).ConfigureAwait(false);
        }
        var activationFaultMilliseconds = Stopwatch.GetElapsedTime(activationFaultStarted).TotalMilliseconds;
        Assert.AreEqual(1L, activationAtFault.BundleCount);
        Assert.AreEqual(16L, activationAtFault.ArtifactCount);
        Assert.AreEqual(1L, activationAtFault.ActivationCount);
        Assert.AreEqual(1L, activationAtFault.StateVersion);
        Assert.AreEqual(activationAcquisition.BundleId, activationAtFault.ActiveBundleId);

        var activationRestartStarted = Stopwatch.GetTimestamp();
        SqliteSnapshot activationAfterRestart;
        CalibrationLibrarySelectionResult activationAfterRestartSelection;
        using (var restarted = await W0Fixture.CreateAsync(activationRoot).ConfigureAwait(false))
        {
            var replay = await new CalibrationLibraryOperationsCoordinator(restarted.Store, restarted.Admission)
                .ActivateAsync(
                    activationAcquisition.BundleId!, activationKey, 0, "evidence-harness", activationReason,
                    CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1L, replay.Version);
            activationAfterRestartSelection = await restarted.Store.SelectAsync(
                CreateCompatibleLight(activationAcquisition.Plan), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(activationAfterRestartSelection.IsSelected, activationAfterRestartSelection.ReasonCode);
            Assert.AreEqual(1L, activationAfterRestartSelection.StateVersion);
            using var connection = await OpenAsync(activationRoot).ConfigureAwait(false);
            activationAfterRestart = await ReadSqliteSnapshotAsync(connection).ConfigureAwait(false);
        }
        var activationRestartMilliseconds = Stopwatch.GetElapsedTime(activationRestartStarted).TotalMilliseconds;
        AssertHashesEqual(activationFilesBefore, HashCalibrationFiles(activationRoot));
        Assert.AreEqual(1L, activationAfterRestart.BundleCount);
        Assert.AreEqual(16L, activationAfterRestart.ArtifactCount);
        Assert.AreEqual(1L, activationAfterRestart.ActivationCount);
        Assert.AreEqual(1L, activationAfterRestart.StateVersion);
        Assert.AreEqual(activationAcquisition.BundleId, activationAfterRestart.ActiveBundleId);

        var sqliteBoundary = new TerminalFaultBoundary(
            "SQLite publication",
            CalibrationPublicationFaultPoint.AfterSqlitePublication.ToString(),
            sqliteInjector.WasInjected,
            sqliteInjector.ObservedRelativePath);
        var activationBoundary = new TerminalFaultBoundary(
            "Activation commit",
            CalibrationPublicationFaultPoint.AfterActivationCommitted.ToString(),
            activationInjector.WasInjected,
            activationInjector.ObservedRelativePath);
        return new TerminalBoundaryEvidence(
            sqliteBoundary,
            activationBoundary,
            new
            {
                SQLiteFaultMilliseconds = sqliteFaultMilliseconds,
                SQLiteRestartMilliseconds = sqliteRestartMilliseconds,
                SQLiteAtFault = sqliteAtFault,
                SQLiteAfterRestart = sqliteAfterRestart,
                ActivationFaultMilliseconds = activationFaultMilliseconds,
                ActivationRestartMilliseconds = activationRestartMilliseconds,
                ActivationAtFault = activationAtFault,
                ActivationAfterRestart = activationAfterRestart
            },
            new TerminalBoundaryCorrectness(
                sqliteInjector.WasInjected,
                sqliteAtFault.BundleCount == 1 && sqliteAtFault.NonterminalJobCount == 1 &&
                    sqliteAtFault.ActiveBundleId is null,
                sqliteAfterRestart.BundleCount == 1 && sqliteAfterRestart.ArtifactCount == 16 &&
                    sqliteAfterRestart.NonterminalJobCount == 0,
                !sqliteRestartSelection.IsSelected,
                activationInjector.WasInjected,
                activationAtFault.StateVersion == 1 && activationAtFault.ActivationCount == 1 &&
                    activationSelectionAtFault.IsSelected,
                activationAfterRestart.StateVersion == 1 && activationAfterRestart.ActivationCount == 1 &&
                    activationAfterRestartSelection.IsSelected,
                !sqliteRestartSelection.IsSelected && activationSelectionAtFault.IsSelected &&
                    activationAfterRestartSelection.IsSelected));
    }

    private static List<PublicationBoundary> CreatePublicationBoundaries()
    {
        var boundaries = new List<PublicationBoundary>();
        var ordinal = 0;
        foreach (var kind in CalibrationReferenceKinds.All)
        {
            for (var sourceIndex = 0; sourceIndex < SourcesPerKind; sourceIndex++)
            {
                boundaries.Add(new PublicationBoundary(
                    ++ordinal, $"source-{kind}-{sourceIndex}-payload", CalibrationPublicationFaultPoint.AfterPayloadPublished,
                    $"/sources/{kind}-{sourceIndex}.bin"));
                boundaries.Add(new PublicationBoundary(
                    ++ordinal, $"source-{kind}-{sourceIndex}-manifest", CalibrationPublicationFaultPoint.AfterManifestPublished,
                    $"/sources/{kind}-{sourceIndex}.json"));
            }
        }
        foreach (var kind in CalibrationReferenceKinds.All)
        {
            boundaries.Add(new PublicationBoundary(
                ++ordinal, $"master-{kind}-payload", CalibrationPublicationFaultPoint.AfterPayloadPublished,
                $"/masters/{kind}.bin"));
            boundaries.Add(new PublicationBoundary(
                ++ordinal, $"master-{kind}-manifest", CalibrationPublicationFaultPoint.AfterManifestPublished,
                $"/masters/{kind}.json"));
        }
        boundaries.Add(new PublicationBoundary(
            ++ordinal, "profile-marker", CalibrationPublicationFaultPoint.AfterProfilePublished,
            "/reference-calibration-profile.json"));
        boundaries.Add(new PublicationBoundary(
            ++ordinal, "profile-directory-sync", CalibrationPublicationFaultPoint.DirectorySynced,
            "/reference-calibration-profile.json"));
        return boundaries;
    }

    private static VirtualCalibrationAcquisitionRequestV1 Request(string idempotencyKey)
        => new(
            VirtualCalibrationAcquisitionRequestV1.CurrentSchemaVersion,
            idempotencyKey,
            82,
            1,
            -10,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(3),
            TimeSpan.FromMilliseconds(32),
            new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 7, 26, 0, 0, 0, TimeSpan.Zero),
            new VirtualCalibrationSourceModelV1 { Seed = Seed },
            "evidence-harness",
            "C208-W0-fault retained evidence");

    private static CameraModuleConfig W0Configuration()
    {
        var sensor = new SensorProfile(
            "C208W0Mono", Width, Height, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16,
            SensorResponseMode.Monochrome, 16, SampleByteOrder.LittleEndian, "c208-w0-mono-v1");
        var readout = new SensorReadoutProfile(
            new SensorCrop(0, 0, Width, Height), 1, 1, FrameBinningAlgorithm.IdentityV1,
            CameraPixelFormat.Mono16, 16, 16, FrameSamplePacking.ByteAligned,
            FrameStoredCodeTransform.RightAlignedV1, FrameLevelCodeSpace.NativeSample,
            0, ushort.MaxValue, Width * 2, SampleByteOrder.LittleEndian,
            ColorFilterArrayPattern.None, null, null);
        return new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                sensor,
                new OpticsProfile("EquidistantFisheye", 2.5, 170, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(32), TimeSpan.FromMilliseconds(32), 82, 82),
                ProfileVersion: "c208-w0-rig-v1",
                Readout: readout),
            AgentId: "c208-w0-agent");
    }

    private static ReconstructionDescriptor CreateCompatibleLight(VirtualCalibrationAcquisitionPlanV1 plan)
    {
        var started = plan.EffectiveFromUtc.AddDays(1);
        var ended = started.Add(plan.ApplicableLightExposure);
        return new ReconstructionDescriptor(
            new CaptureIdentityDescriptor(plan.AgentId, plan.RigId, 1, Guid.Parse("20800000-0000-0000-0000-000000000001")),
            new CaptureTimingDescriptor(started, started, ended, ended, ended),
            new CaptureControlDescriptor(
                plan.ApplicableLightExposure, plan.ApplicableLightExposure,
                plan.Gain, plan.Gain, plan.Offset, plan.Offset, plan.TemperatureC, plan.TemperatureC),
            new CaptureProfileSet(
                plan.RigProfile,
                new ProfileIdentityDescriptor(
                    "virtual-calibration-source-model",
                    plan.SourceModel.SchemaVersion,
                    plan.SourceModelIdentitySha256),
                new ProfileIdentityDescriptor("mask", "v1", new string('B', 64)),
                plan.SensorProfile,
                new ProfileIdentityDescriptor("capture", "v1", new string('C', 64))),
            plan.InputLayout,
            new ArtifactDescriptor(
                Guid.Parse("20800000-0000-0000-0000-000000000002"),
                FrameArtifactRole.Raw,
                "C208W0",
                "light",
                ended,
                [],
                RecipeIdentityDescriptor.Create(
                    "c208-w0-light", "1.0.0", "c208-w0-v1", JsonSerializer.SerializeToElement(new { Seed })),
                "application/x-skymonitor-mono16",
                new string('D', 64)));
    }

    private static void AssertPlan(VirtualCalibrationAcquisitionPlanV1 plan)
    {
        Assert.AreEqual(Width, plan.InputLayout.Width);
        Assert.AreEqual(Height, plan.InputLayout.Height);
        Assert.AreEqual(CameraPixelFormat.Mono16, plan.InputLayout.PixelFormat);
        Assert.AreEqual(16, plan.InputLayout.SampleDepthBits);
        Assert.AreEqual(Seed, plan.SourceModel.Seed);
    }

    private static void AssertBundle(CalibrationLibraryBundleV1 bundle)
    {
        Assert.AreEqual(CalibrationLibraryBundleSources.VirtualAcquisitionV1, bundle.Source);
        Assert.HasCount(16, bundle.Artifacts);
        var sources = bundle.Artifacts.Where(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Source).ToArray();
        var masters = bundle.Artifacts.Where(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Master).ToArray();
        Assert.HasCount(12, sources);
        Assert.HasCount(4, masters);
        foreach (var kind in CalibrationReferenceKinds.All)
        {
            var kindSources = sources.Where(source => source.Kind == kind)
                .OrderBy(static source => source.SourceIndex).ToArray();
            Assert.HasCount(SourcesPerKind, kindSources);
            CollectionAssert.AreEqual(
                new int?[] { 0, 1, 2 }, kindSources.Select(static source => source.SourceIndex).ToArray());
            CollectionAssert.AreEqual(
                kindSources.Select(static source => source.ArtifactId).ToArray(),
                masters.Single(master => master.Kind == kind).OrderedSourceArtifactIds.ToArray());
        }
    }

    private static SyntheticCalibrationModelV1 LegacyModel(int seed)
        => new()
        {
            Seed = seed,
            Gain = 82,
            TemperatureC = -10,
            Defects = [new SyntheticCalibrationDefect(seed % Width, seed % Height)]
        };

    private static ReconstructionDescriptor LegacyLight(SyntheticCalibrationModelV1 model)
    {
        var template = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, Width, Height, Width * 2, new byte[Width * Height * 2]).Descriptor;
        return template with
        {
            Controls = template.Controls with
            {
                RequestedGain = model.Gain,
                EffectiveGain = model.Gain,
                RequestedOffset = null,
                EffectiveOffset = null,
                TemperatureSetpointC = model.TemperatureC,
                EffectiveTemperatureC = model.TemperatureC
            },
            Layout = template.Layout with
            {
                StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
                LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
            }
        };
    }

    private static string RelativeDirectory(SyntheticCalibrationBundle bundle)
        => Normalize(Path.GetDirectoryName(bundle.EvidenceFiles[0].RelativePath)!);

    private static Dictionary<string, FileIdentity> HashCalibrationFiles(string root)
    {
        var calibrationRoot = Path.Combine(root, "calibration", "virtual");
        return Directory.Exists(calibrationRoot)
            ? HashDirectoryAbsolute(calibrationRoot)
            : new Dictionary<string, FileIdentity>(StringComparer.Ordinal);
    }

    private static Dictionary<string, FileIdentity> HashDirectory(string root, string relativeDirectory)
        => HashDirectoryAbsolute(Resolve(root, relativeDirectory));

    private static Dictionary<string, FileIdentity> HashDirectoryAbsolute(string directory)
        => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                path => Normalize(Path.GetRelativePath(directory, path)),
                path => new FileIdentity(new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))),
                StringComparer.Ordinal);

    private static void AssertHashesEqual(
        IReadOnlyDictionary<string, FileIdentity> expected,
        IReadOnlyDictionary<string, FileIdentity> actual)
    {
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var file in expected)
        {
            Assert.AreEqual(file.Value, actual[file.Key], file.Key);
        }
    }

    private static void AssertHashesByFileNameEqual(
        IReadOnlyDictionary<string, FileIdentity> expected,
        IReadOnlyDictionary<string, FileIdentity> actual)
    {
        var expectedByName = expected.ToDictionary(static file => Path.GetFileName(file.Key), static file => file.Value);
        var actualByName = actual.ToDictionary(static file => Path.GetFileName(file.Key), static file => file.Value);
        AssertHashesEqual(expectedByName, actualByName);
    }

    private static async Task<SqliteSnapshot> ReadSqliteSnapshotAsync(SqliteConnection connection)
        => new(
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM calibration_library_bundles;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM calibration_library_artifacts;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM calibration_library_activations;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM calibration_acquisition_jobs WHERE state NOT IN ('published', 'failed', 'cancelled');").ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT version FROM calibration_library_state WHERE state_key = 1;").ConfigureAwait(false),
            await ScalarNullableStringAsync(connection, "SELECT active_bundle_id FROM calibration_library_state WHERE state_key = 1;").ConfigureAwait(false),
            await ScalarStringAsync(connection, "SELECT sqlite_version();").ConfigureAwait(false));

    private static async Task<ReconciliationSnapshot> ReadReconciliationSnapshotAsync(
        SqliteConnection connection,
        string sourceRelativePath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT evidence_key, source_relative_path, quarantine_relative_path, outcome, reason,
                   operation_state, observed_bytes, observed_unix_ms, completed_unix_ms
            FROM calibration_library_reconciliation
            WHERE source_relative_path = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceRelativePath);
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        var quarantineIsNull = await reader.IsDBNullAsync(2).ConfigureAwait(false);
        var completedIsNull = await reader.IsDBNullAsync(8).ConfigureAwait(false);
        var result = new ReconciliationSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            quarantineIsNull ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            completedIsNull ? null : reader.GetInt64(8));
        Assert.IsFalse(await reader.ReadAsync().ConfigureAwait(false));
        return result;
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<string?> ScalarNullableStringAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<SqliteConnection> OpenAsync(string root)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static IOptions<CameraAgentHostOptions> CreateOptions(string root)
        => Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressSqliteBusyTimeoutSeconds = 5
        });

    private static string Resolve(string root, string relativePath)
        => Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string Normalize(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/');

    private static string CreateTemporaryRoot(string suffix)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-issue-208-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string GetEvidenceRevision(GitEvidence git)
    {
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ??
            $"{git.Head[..Math.Min(12, git.Head.Length)]}{(git.Dirty ? "-dirty" : string.Empty)}";
        if (string.IsNullOrWhiteSpace(revision) || revision is "." or ".." ||
            revision.Any(static character => character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')))
        {
            throw new InvalidOperationException(
                "HVO_EVIDENCE_REVISION must contain only ASCII letters, digits, period, underscore, or hyphen.");
        }
        return revision;
    }

    private static string ReadPinnedSdkVersion(string repositoryRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot, "global.json")));
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidDataException("global.json does not contain an SDK version.");
    }

    private static string ReadCpuModel()
    {
        const string cpuInfo = "/proc/cpuinfo";
        if (File.Exists(cpuInfo))
        {
            var model = File.ReadLines(cpuInfo).FirstOrDefault(
                static line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            if (model is not null && model.IndexOf(':', StringComparison.Ordinal) is var separator and >= 0)
            {
                return model[(separator + 1)..].Trim();
            }
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
    }

    private static AssemblyEvidence ReadAssemblyEvidence(Type type)
    {
        var location = type.Assembly.Location;
        using var stream = File.OpenRead(location);
        return new AssemblyEvidence(
            type.Assembly.GetName().Name ?? type.FullName ?? "unknown",
            type.Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static async Task<GitEvidence> ReadGitEvidenceAsync(string repositoryRoot)
    {
        var head = (await RunProcessAsync(repositoryRoot, "git", "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
        var branch = (await RunProcessAsync(
            repositoryRoot, "git", "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
        var status = await RunProcessAsync(
            repositoryRoot, "git", "status", "--porcelain=v1", "--untracked-files=all").ConfigureAwait(false);
        var diff = await RunProcessAsync(
            repositoryRoot, "git", "diff", "--binary", "--no-ext-diff", "HEAD", "--").ConfigureAwait(false);
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        fingerprint.AppendData(Encoding.UTF8.GetBytes(status));
        fingerprint.AppendData(Encoding.UTF8.GetBytes(diff));
        return new GitEvidence(
            head,
            branch,
            !string.IsNullOrWhiteSpace(status),
            Convert.ToHexString(fingerprint.GetHashAndReset()),
            status.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    private static async Task<string> RunProcessAsync(
        string workingDirectory,
        string fileName,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start {fileName} for retained evidence.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed while collecting retained evidence: {error}");
        }
        return output;
    }

    private static string GetRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static JsonSerializerOptions CreateEvidenceJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class W0Fixture : IDisposable
    {
        private readonly CaptureControlTelemetry _telemetry;

        private W0Fixture(
            SqliteCalibrationLibraryStore store,
            CaptureAdmissionCoordinator admission,
            CaptureControlTelemetry telemetry,
            VirtualCalibrationAcquisitionCoordinator coordinator)
        {
            Store = store;
            Admission = admission;
            _telemetry = telemetry;
            Coordinator = coordinator;
        }

        internal SqliteCalibrationLibraryStore Store { get; }
        internal CaptureAdmissionCoordinator Admission { get; }
        internal VirtualCalibrationAcquisitionCoordinator Coordinator { get; }

        internal static async Task<W0Fixture> CreateAsync(
            string root,
            ICalibrationPublicationFaultInjector? faultInjector = null)
        {
            var options = CreateOptions(root);
            var timeProvider = new FixedTimeProvider(FixedUtcNow);
            var ingress = new InitializingIngress(root);
            var telemetry = new CaptureControlTelemetry();
            var admission = new CaptureAdmissionCoordinator(ingress, options, timeProvider, telemetry);
            await admission.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var store = new SqliteCalibrationLibraryStore(ingress, options, timeProvider);
            _ = await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var accessor = new CameraAgentConfigurationAccessor();
            accessor.SetConfiguration(W0Configuration());
            var publisher = new CalibrationArtifactPublisher(
                options, faultInjector ?? NullCalibrationPublicationFaultInjector.Instance);
            var coordinator = new VirtualCalibrationAcquisitionCoordinator(
                store, publisher, admission, accessor, timeProvider);
            return new W0Fixture(store, admission, telemetry, coordinator);
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            Store.Dispose();
            Admission.Dispose();
            _telemetry.Dispose();
        }
    }

    private sealed class InitializingIngress(string root) : IRawCaptureIngress
    {
        private readonly object _sync = new();
        private Task? _initialization;

        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            Task initialization;
            lock (_sync)
            {
                initialization = _initialization ??= new SqliteRawCaptureJournal(
                    Path.Combine(root, "journal", "raw-ingress.db"), 5)
                    .InitializeAsync(CancellationToken.None);
            }
            await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TargetedFaultInjector(
        CalibrationPublicationFaultPoint target,
        string relativePathSuffix) : ICalibrationPublicationFaultInjector
    {
        private int _injected;

        internal bool WasInjected => Volatile.Read(ref _injected) != 0;
        internal string ObservedRelativePath { get; private set; } = string.Empty;

        public void Inject(CalibrationPublicationFaultPoint point, string relativePath)
        {
            if (point == target && relativePath.EndsWith(relativePathSuffix, StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _injected, 1) == 0)
            {
                ObservedRelativePath = relativePath;
                throw new InjectedCalibrationFaultException(point, relativePath);
            }
        }
    }

    private sealed class InjectedCalibrationFaultException : IOException
    {
        public InjectedCalibrationFaultException()
        {
        }

        public InjectedCalibrationFaultException(string message) : base(message)
        {
        }

        public InjectedCalibrationFaultException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        internal InjectedCalibrationFaultException(
            CalibrationPublicationFaultPoint point,
            string relativePath)
            : base($"Injected C208 calibration fault at {point}: {relativePath}")
        {
        }
    }

    private sealed record PublicationBoundary(
        int Ordinal,
        string Name,
        CalibrationPublicationFaultPoint Point,
        string RelativePathSuffix);

    private sealed record FileIdentity(long Length, string Sha256);

    private sealed record SqliteSnapshot(
        long BundleCount,
        long ArtifactCount,
        long ActivationCount,
        long NonterminalJobCount,
        long StateVersion,
        string? ActiveBundleId,
        string SQLiteVersion);

    private sealed record ReconciliationSnapshot(
        string EvidenceKey,
        string SourceRelativePath,
        string? QuarantineRelativePath,
        string Outcome,
        string Reason,
        string OperationState,
        long ObservedBytes,
        long ObservedUnixMs,
        long? CompletedUnixMs);

    private sealed record W0FaultTrialEvidence(
        W0TrialMeasurements Measurements,
        W0TrialCorrectness Correctness);

    private sealed record W0TrialMeasurements(
        int Ordinal,
        string Boundary,
        string FaultPoint,
        string RelativePathSuffix,
        double InitialMilliseconds,
        double RestartMilliseconds,
        double CpuMilliseconds,
        long AllocatedBytes,
        int FilesAtFault,
        long BytesAtFault,
        int FilesAfterRestart,
        long BytesAfterRestart,
        SqliteSnapshot SQLiteAtFault,
        SqliteSnapshot SQLiteAfterRestart);

    private sealed record W0TrialCorrectness(
        int Ordinal,
        string Boundary,
        bool FaultObserved,
        string FaultRelativePath,
        string JobId,
        string SourceModelIdentitySha256,
        bool NoPartialSelection,
        bool RestartConverged,
        bool ExistingImmutableFilesUnchanged,
        long ObservedBacklog,
        string PostRestartSelectionReason);

    private sealed record TerminalFaultBoundary(
        string Boundary,
        string FaultPoint,
        bool FaultObserved,
        string FaultRelativePath);

    private sealed record TerminalBoundaryCorrectness(
        bool SqlitePublicationFaultObserved,
        bool SqlitePublicationStateCompleteAtFault,
        bool SqlitePublicationRestartConverged,
        bool SqlitePublicationNotPartiallySelected,
        bool ActivationFaultObserved,
        bool ActivationStateCompleteAtFault,
        bool ActivationRestartConverged,
        bool NoPartialSelection);

    private sealed record TerminalBoundaryEvidence(
        TerminalFaultBoundary SqlitePublicationBoundary,
        TerminalFaultBoundary ActivationBoundary,
        object Measurements,
        TerminalBoundaryCorrectness Correctness);

    private sealed record LegacyEvidence(object Measurements, object Correctness);

    private sealed record GitEvidence(
        string Head,
        string Branch,
        bool Dirty,
        string DirtyFingerprintSha256,
        IReadOnlyList<string> Status);

    private sealed record AssemblyEvidence(string Name, string Configuration, string Sha256);
}
