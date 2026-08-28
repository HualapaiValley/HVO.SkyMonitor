using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class C208W3MCalibrationLibraryRetainedPerformanceTests
{
    private const int CanonicalBundleCount = 10_000;
    private const int CanonicalWarmupCount = 1_000;
    private const int CanonicalSelectionCount = 10_000;
    private const int PageSize = 100;
    private const int PageMeasurements = 32;
    private const int UiWarmupRounds = 5;
    private const int UiMeasuredOperations = 30;
    private const string MetadataOnlyFailureReason = "issue-208.metadata-only-no-files";
    private const string BundleCountVariable = "HVO_ISSUE208_W3M_BUNDLE_COUNT";
    private const string WarmupCountVariable = "HVO_ISSUE208_W3M_WARMUPS";
    private const string SelectionCountVariable = "HVO_ISSUE208_W3M_SELECTIONS";
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 1, 2, 4, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions UiResponseJsonOptions = new(JsonSerializerDefaults.Web);

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    [TestMethod]
    public async Task W3M_OneCompatibleActiveAmongTenThousandBundles_WritesRetainedEvidence()
    {
        var bundleCount = ReadCount(BundleCountVariable, CanonicalBundleCount, PageSize + 1);
        var warmupCount = ReadCount(WarmupCountVariable, CanonicalWarmupCount, 4);
        var selectionCount = ReadCount(SelectionCountVariable, CanonicalSelectionCount, 4);
        var canonical = bundleCount == CanonicalBundleCount &&
                        warmupCount == CanonicalWarmupCount &&
                        selectionCount == CanonicalSelectionCount;
        if (canonical && !string.Equals(BuildConfiguration, "Release", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Canonical issue #208 performance evidence requires a Release build.");
        }

        var repositoryRoot = GetRepositoryRoot();
        var revision = GetEvidenceRevision();
        var outputDirectory = Path.Combine(repositoryRoot, "TestResults", "issue-208", revision);
        var evidencePath = Path.Combine(outputDirectory, "calibration-library-w3m-selection-performance.json");
        var root = Path.Combine(Path.GetTempPath(), "hvo-issue-208-calibration-w3m", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(root);

        try
        {
            var databasePath = Path.Combine(root, "journal", "raw-ingress.db");
            var journal = new SqliteRawCaptureJournal(databasePath, busyTimeoutSeconds: 30);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 30
            });
            var fixture = await CreateCompatibleFixtureAsync(root, options).ConfigureAwait(false);
            FileSizeEvidence filesBeforeSetup;
            FileSizeEvidence filesAfterSetup;
            TimeSpan setupElapsed;

            using (var setupStore = CreateStore(options))
            {
                var setupStarted = Stopwatch.GetTimestamp();
                _ = await setupStore.AdoptPublishedBundleAsync(
                    fixture.Bundle, CancellationToken.None).ConfigureAwait(false);
                await SeedIncompatibleMetadataAsync(
                    databasePath, fixture.Bundle, bundleCount - 1, CancellationToken.None).ConfigureAwait(false);
                _ = await setupStore.ActivateAsync(
                    fixture.Bundle.BundleId,
                    "issue-208-activate-compatible",
                    expectedVersion: 0,
                    "issue-208-performance",
                    "activate the sole compatible bundle",
                    CancellationToken.None).ConfigureAwait(false);
                setupElapsed = Stopwatch.GetElapsedTime(setupStarted);
                filesBeforeSetup = ReadFileSizes(databasePath);
            }

            await CheckpointAsync(databasePath).ConfigureAwait(false);
            filesAfterSetup = ReadFileSizes(databasePath);
            var plans = await ReadQueryPlansAsync(databasePath, fixture.Bundle.BundleId).ConfigureAwait(false);
            AssertQueryPlans(plans);

            C208UiReadEvidence uiReads;
            using (var uiStore = CreateStore(options))
            using (var owner = CreateOwnerAuthorizationFixture())
            {
                var tokens = new CalibrationOperationsTokenService(
                    new EphemeralDataProtectionProvider(), TimeSpan.FromHours(1));
                var service = new CameraAgentCalibrationUiService(
                    owner.Authentication,
                    owner.Authorization,
                    uiStore,
                    acquisitionCoordinator: null!,
                    operationsCoordinator: null!,
                    tokens,
                    NullLogger<CameraAgentCalibrationUiService>.Instance);
                uiReads = await MeasureUiReadsAsync(
                    service, owner, fixture.Bundle, CancellationToken.None).ConfigureAwait(false);
            }

            SelectionMeasurement singleSelection;
            PageMeasurement singlePage;
            using (var store = CreateStore(options))
            {
                singleSelection = await MeasureSelectionsAsync(
                    store, 1, fixture.Light, warmupCount, selectionCount, databasePath)
                    .ConfigureAwait(false);
                singlePage = await MeasurePagesAsync(
                    store, 1, PageMeasurements, databasePath).ConfigureAwait(false);
            }

            SelectionMeasurement fourSelection;
            PageMeasurement fourPage;
            using (var store = CreateStore(options))
            {
                fourSelection = await MeasureSelectionsAsync(
                    store, 4, fixture.Light, warmupCount, selectionCount, databasePath)
                    .ConfigureAwait(false);
                fourPage = await MeasurePagesAsync(
                    store, 4, PageMeasurements, databasePath).ConfigureAwait(false);
            }

            using var finalStore = CreateStore(options);
            var finalState = await finalStore.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
            var finalSelection = await finalStore.SelectAsync(fixture.Light, CancellationToken.None).ConfigureAwait(false);
            var finalPage = await finalStore.GetBundlePageAsync(
                PageSize, cursor: null, CancellationToken.None).ConfigureAwait(false);
            var database = await ReadDatabaseEvidenceAsync(
                databasePath, fixture.Light, fixture.Bundle.BundleId).ConfigureAwait(false);

            Assert.AreEqual((long)bundleCount, database.Rows.Bundles);
            Assert.AreEqual((long)bundleCount * CalibrationReferenceKinds.All.Count, database.Rows.Artifacts);
            Assert.AreEqual(bundleCount - 1L, database.IncompatibleBundleCount);
            Assert.AreEqual(1L, database.CompatibleBundleCount);
            Assert.AreEqual(1L, database.PublishedBundleCount);
            Assert.AreEqual(bundleCount - 1L, database.MetadataOnlyBundleCount);
            Assert.AreEqual(1L, database.RetentionHeldBundleCount);
            Assert.AreEqual(fixture.Bundle.BundleId, database.ActiveBundleId);
            Assert.AreEqual(fixture.Bundle.BundleId, finalState.ActiveBundle?.Bundle.BundleId);
            Assert.AreEqual(1L, finalState.Version);
            Assert.IsTrue(finalSelection.IsSelected, finalSelection.ReasonCode);
            Assert.AreEqual("calibration.library.selected", finalSelection.ReasonCode);
            Assert.AreEqual(1L, finalSelection.StateVersion);
            Assert.HasCount(PageSize, finalPage.Items);
            Assert.IsNotNull(finalPage.NextCursor);
            Assert.AreEqual(PageSize, finalPage.Items.Select(static item => item.Bundle.BundleId).Distinct().Count());
            Assert.IsTrue(singleSelection.AllSelected);
            Assert.IsTrue(fourSelection.AllSelected);
            Assert.AreEqual(1, singleSelection.MaximumConcurrency);
            Assert.AreEqual(4, fourSelection.MaximumConcurrency);
            Assert.AreEqual(1, singleSelection.StoreInstanceCount);
            Assert.AreEqual(1, fourSelection.StoreInstanceCount);
            Assert.AreEqual(PageSize, singlePage.MinimumRowsPerPage);
            Assert.AreEqual(PageSize, fourPage.MinimumRowsPerPage);
            Assert.AreEqual(1, singlePage.MaximumConcurrency);
            Assert.AreEqual(4, fourPage.MaximumConcurrency);
            Assert.IsTrue(finalPage.Items.All(static item => item.PublicationState == "incomplete"));
            Assert.AreEqual("ok", database.IntegrityCheck, ignoreCase: true);
            Assert.AreEqual(0L, database.ForeignKeyFailureCount);

            var contentionSamples = fourSelection.SortedLatencyMilliseconds
                .Select(value => Math.Max(0, value - singleSelection.MedianMilliseconds))
                .Order()
                .ToArray();
            var sourceFile = Path.Combine(
                repositoryRoot,
                "tests",
                "HVO.SkyMonitor.CameraAgent.Tests",
                "Capture",
                "Calibration",
                "C208W3MCalibrationLibraryRetainedPerformanceTests.cs");
            var git = await ReadGitEvidenceAsync(repositoryRoot).ConfigureAwait(false);
            var exactCommand = CreateExactCommand(revision, bundleCount, warmupCount, selectionCount);
            var evidence = new
            {
                Issue = 208,
                Workload = "C208-W3M retained calibration-library selection and page-read evidence",
                Revision = revision,
                Canonical = canonical,
                Claimability = canonical
                    ? "Canonical workload; compare only with equivalently provisioned Release runs."
                    : "Development-only reduced workload; not valid as canonical performance evidence.",
                GeneratedUtc = DateTimeOffset.UtcNow,
                ExactCommand = exactCommand,
                Source = new
                {
                    Git = git,
                    TestFile = new
                    {
                        RelativePath = Path.GetRelativePath(repositoryRoot, sourceFile).Replace(Path.DirectorySeparatorChar, '/'),
                        Sha256 = FileSha256(sourceFile)
                    },
                    Assemblies = new[]
                    {
                        ReadAssemblyEvidence(typeof(C208W3MCalibrationLibraryRetainedPerformanceTests)),
                        ReadAssemblyEvidence(typeof(SqliteCalibrationLibraryStore)),
                        ReadAssemblyEvidence(typeof(CalibrationLibraryBundleV1))
                    }
                },
                Environment = new
                {
                    OperatingSystem = RuntimeInformation.OSDescription,
                    OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                    ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Runtime = RuntimeInformation.FrameworkDescription,
                    RuntimeVersion = Environment.Version.ToString(),
                    Configuration = BuildConfiguration,
                    PinnedSdk = ReadPinnedSdkVersion(repositoryRoot),
                    ServerGc = GCSettings.IsServerGC,
                    ProcessorCount = Environment.ProcessorCount,
                    Cpu = ReadCpuModel(),
                    TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                    SQLiteVersion = database.SqliteVersion,
                    StopwatchFrequency = Stopwatch.Frequency,
                    Storage = new
                    {
                        Root = Path.GetPathRoot(root),
                        FileSystem = new DriveInfo(Path.GetPathRoot(root)!).DriveFormat,
                        Note = "Temporary local SQLite database in the test host file system; physical device identity is unavailable in the development container."
                    }
                },
                WorkloadManifest = new
                {
                    ProductionStore = nameof(SqliteCalibrationLibraryStore),
                    MetadataBundles = bundleCount,
                    IncompatibleMetadataOnlyBundles = bundleCount - 1,
                    CompatiblePublishedBundles = 1,
                    ActiveCompatibleBundles = 1,
                    ArtifactsPerBundle = CalibrationReferenceKinds.All.Count,
                    PageSize,
                    PageMeasurementsPerConcurrency = PageMeasurements,
                    WarmupsPerConcurrency = warmupCount,
                    MeasuredSelectionsPerConcurrency = selectionCount,
                    ConcurrencyLevels = new[] { 1, 4 },
                    Overrides = new
                    {
                        BundleCount = Environment.GetEnvironmentVariable(BundleCountVariable),
                        Warmups = Environment.GetEnvironmentVariable(WarmupCountVariable),
                        Selections = Environment.GetEnvironmentVariable(SelectionCountVariable)
                    },
                    StoreTopology = "Each concurrency scenario uses exactly one production SqliteCalibrationLibraryStore instance. Concurrency 4 is four workers sharing that singleton.",
                    StartSynchronization = "Every measured worker enters the concurrency tracker, signals an all-arrived barrier, and waits for one coordinated release before its first operation.",
                    Setup = "One compatible bundle with real files is adopted and activated through production APIs. The incompatible rows are deliberately incomplete, non-retained metadata-only W3M fixtures inserted in one setup transaction.",
                    MeasuredSelectionPath = "SqliteCalibrationLibraryStore.SelectAsync",
                    MeasuredReadPath = "SqliteCalibrationLibraryStore.GetBundlePageAsync with page size 100"
                },
                Setup = new
                {
                    WallMilliseconds = setupElapsed.TotalMilliseconds,
                    SqliteTransactions = new
                    {
                        IncompatibleMetadataSeedWriteTransactions = 1,
                        ProductionPublishTransactions = "One precheck read transaction and one publish write transaction.",
                        ProductionActivationTransactions = "Production replay/target reads followed by one activation write transaction."
                    },
                    MetadataOnlyFixtures = new
                    {
                        Count = bundleCount - 1,
                        PublicationState = "incomplete",
                        RetentionHold = false,
                        FailureReason = MetadataOnlyFailureReason,
                        MetadataValidation = "Every generated bundle passes CalibrationLibraryContract.Validate and every paged row passes the production metadata/envelope reader.",
                        PhysicalEvidence = "Intentionally absent. Required path/hash columns contain deterministic fixture values only; these rows are not production-published evidence and no retention or file-validity claim is made.",
                        Limitation = "Direct setup bypasses AdoptPublishedBundleAsync only for incompatible metadata scale. Selection and page measurements use production APIs; the sole compatible bundle uses production publish, activation, and evidence validation."
                    },
                    FilesBeforeSetupCheckpoint = filesBeforeSetup,
                    FilesAfterSetupCheckpoint = filesAfterSetup
                },
                Measurements = new
                {
                    SelectionConcurrency1 = singleSelection,
                    SelectionConcurrency4 = fourSelection,
                    PageReadConcurrency1 = singlePage,
                    PageReadConcurrency4 = fourPage,
                    LockWait = new
                    {
                        DirectMeasurement = "Unavailable: SqliteCalibrationLibraryStore and Microsoft.Data.Sqlite expose no busy-handler or lock-wait duration callback.",
                        BusyOrLockedErrors = 0,
                        EstimatedSamples = contentionSamples.Length,
                        EstimatedMedianMilliseconds = Percentile(contentionSamples, 0.50),
                        EstimatedP95Milliseconds = Percentile(contentionSamples, 0.95),
                        Method = "Concurrency-4 selection latency minus the concurrency-1 median, floored at zero. This is contention-correlated delay, including scheduler and CPU effects, not direct SQLite lock wait."
                    },
                    TransactionAccounting = new
                    {
                        SelectionReadTransactionsPerMeasuredConcurrency = selectionCount,
                        SelectionReadTransactionsTotal = selectionCount * 2L,
                        WarmupReadTransactionsPerConcurrency = warmupCount,
                        PageReadTransactionsPerConcurrency = PageMeasurements,
                        SelectionRecordWrites = "The singleton store may perform one throttled state UPDATE during warmup; no selection-record write occurs in the measured fixed-time window.",
                        Basis = "Modeled from production method boundaries because the store exposes no transaction recorder. SelectAsync opens one deferred read transaction; GetBundlePageAsync opens one deferred read transaction."
                    }
                },
                C208UiReads = uiReads,
                QueryPlans = plans,
                FinalInvariants = new
                {
                    database.Rows,
                    database.CompatibleBundleCount,
                    database.IncompatibleBundleCount,
                    database.PublishedBundleCount,
                    database.MetadataOnlyBundleCount,
                    database.RetentionHeldBundleCount,
                    database.ActiveBundleId,
                    StateVersion = finalState.Version,
                    FinalSelectionReason = finalSelection.ReasonCode,
                    FinalSelectionBundleId = finalSelection.Bundle?.Bundle.BundleId,
                    FinalPageRows = finalPage.Items.Count,
                    FinalPageHasNextCursor = finalPage.NextCursor is not null,
                    FinalPageDistinctBundleIds = finalPage.Items.Select(static item => item.Bundle.BundleId).Distinct().Count(),
                    FinalPageMetadataOnlyRows = finalPage.Items.Count(static item => item.PublicationState == "incomplete"),
                    database.IntegrityCheck,
                    database.ForeignKeyFailureCount,
                    database.JournalMode,
                    database.Synchronous,
                    database.UserVersion
                }
            };

            await File.WriteAllTextAsync(
                evidencePath,
                JsonSerializer.Serialize(evidence, EvidenceOptions) + Environment.NewLine).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<CompatibleFixture> CreateCompatibleFixtureAsync(
        string root,
        IOptions<CameraAgentHostOptions> options)
    {
        var model = new SyntheticCalibrationModelV1
        {
            Gain = 99.5,
            TemperatureC = -9.5,
            Defects = [new SyntheticCalibrationDefect(1, 1)]
        };
        var payload = new byte[8];
        var template = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var light = template.Descriptor with
        {
            Controls = template.Descriptor.Controls with
            {
                RequestedGain = model.Gain,
                EffectiveGain = model.Gain,
                RequestedOffset = null,
                EffectiveOffset = null,
                TemperatureSetpointC = model.TemperatureC,
                EffectiveTemperatureC = model.TemperatureC
            },
            Profiles = template.Descriptor.Profiles with
            {
                Calibration = new ProfileIdentityDescriptor(
                    "synthetic-calibration-model",
                    model.SchemaVersion,
                    SyntheticCalibrationReferenceGenerator.ComputeModelIdentitySha256(model))
            }
        };
        var syntheticStore = new SyntheticCalibrationReferenceStore(
            options, new CameraAgentClearReferenceLoader(options));
        var synthetic = await syntheticStore.GetOrCreateAsync(
            light, model, CancellationToken.None).ConfigureAwait(false);
        var bundle = synthetic.LibraryBundle;
        Assert.IsTrue(CalibrationLibraryContract.Validate(bundle).IsValid);
        return new CompatibleFixture(bundle, light);
    }

    private static async Task SeedIncompatibleMetadataAsync(
        string databasePath,
        CalibrationLibraryBundleV1 template,
        int count,
        CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using (var settings = connection.CreateCommand())
        {
            settings.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
            _ = await settings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        for (var index = 1; index <= count; index++)
        {
            var stem = $"issue-208/incompatible-{index:D5}";
            var artifacts = template.Artifacts.Select((artifact, ordinal) => artifact with
            {
                ArtifactId = DeterministicGuid($"artifact-{index:D5}-{ordinal}"),
                ManifestRelativePath = $"{stem}/{artifact.Kind}.json",
                PayloadSha256 = Sha256($"payload-{index:D5}-{ordinal}")
            }).ToArray();
            var bundle = template with
            {
                BundleId = $"issue-208-incompatible-{index:D5}",
                CreatedUtc = DateTimeOffset.UnixEpoch.AddSeconds(index),
                ProfileRelativePath = $"{stem}/reference-calibration-profile.json",
                ProfileIdentitySha256 = Sha256($"profile-{index:D5}"),
                AcquisitionModelIdentitySha256 = Sha256($"model-{index:D5}"),
                Applicability = template.Applicability with
                {
                    AgentId = $"incompatible-agent-{index:D5}"
                },
                Artifacts = artifacts
            };
            if (!CalibrationLibraryContract.Validate(bundle).IsValid)
            {
                throw new InvalidDataException($"Generated incompatible metadata bundle {index} is invalid.");
            }
            await InsertBundleAsync(connection, transaction, bundle, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertBundleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CalibrationLibraryBundleV1 bundle,
        CancellationToken cancellationToken)
    {
        var applicability = bundle.Applicability;
        var bundleJson = CalibrationLibraryContractJson.Serialize(bundle);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO calibration_library_bundles(
                    bundle_id, bundle_identity_sha256, source, bundle_json, profile_relative_path,
                    profile_identity_sha256, acquisition_model_identity_sha256, agent_id, rig_id,
                    rig_profile_sha256, sensor_profile_sha256, input_layout_sha256, output_layout_sha256,
                    minimum_gain, maximum_gain, minimum_offset, maximum_offset,
                    minimum_light_exposure_ticks, maximum_light_exposure_ticks,
                    minimum_temperature_c, maximum_temperature_c, effective_from_unix_ms,
                    effective_until_unix_ms, publication_state, retention_hold, failure_reason,
                    created_unix_ms, updated_unix_ms)
                VALUES (
                    $id, $identity, $source, $json, $profile_path, $profile_identity, $model_identity,
                    $agent, $rig, $rig_profile, $sensor_profile, $input_layout, $output_layout,
                    $minimum_gain, $maximum_gain, $minimum_offset, $maximum_offset,
                    $minimum_exposure, $maximum_exposure, $minimum_temperature, $maximum_temperature,
                    $effective_from, $effective_until, 'incomplete', 0, $failure_reason, $created, $updated);
                """;
            Add(command, "$id", bundle.BundleId);
            Add(command, "$identity", CalibrationLibraryContractJson.ComputeIdentitySha256(bundle));
            Add(command, "$source", bundle.Source);
            Add(command, "$json", bundleJson);
            Add(command, "$profile_path", bundle.ProfileRelativePath);
            Add(command, "$profile_identity", bundle.ProfileIdentitySha256);
            Add(command, "$model_identity", bundle.AcquisitionModelIdentitySha256);
            Add(command, "$agent", applicability.AgentId);
            Add(command, "$rig", applicability.RigId);
            Add(command, "$rig_profile", applicability.RigProfileSha256);
            Add(command, "$sensor_profile", applicability.SensorProfileSha256);
            Add(command, "$input_layout", CaptureContractJson.ComputeCanonicalJsonSha256(applicability.InputLayout));
            Add(command, "$output_layout", CaptureContractJson.ComputeCanonicalJsonSha256(applicability.OutputLayout));
            Add(command, "$minimum_gain", applicability.MinimumGain);
            Add(command, "$maximum_gain", applicability.MaximumGain);
            Add(command, "$minimum_offset", DbValue(applicability.MinimumOffset));
            Add(command, "$maximum_offset", DbValue(applicability.MaximumOffset));
            Add(command, "$minimum_exposure", DbValue(applicability.MinimumLightExposure?.Ticks));
            Add(command, "$maximum_exposure", DbValue(applicability.MaximumLightExposure?.Ticks));
            Add(command, "$minimum_temperature", DbValue(applicability.MinimumTemperatureC));
            Add(command, "$maximum_temperature", DbValue(applicability.MaximumTemperatureC));
            Add(command, "$effective_from", applicability.EffectiveFromUtc.ToUnixTimeMilliseconds());
            Add(command, "$effective_until", DbValue(applicability.EffectiveUntilUtc?.ToUnixTimeMilliseconds()));
            Add(command, "$failure_reason", MetadataOnlyFailureReason);
            Add(command, "$created", bundle.CreatedUtc.ToUnixTimeMilliseconds());
            Add(command, "$updated", FixedUtcNow.ToUnixTimeMilliseconds());
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var ordinal = 0; ordinal < bundle.Artifacts.Count; ordinal++)
        {
            var artifact = bundle.Artifacts[ordinal];
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO calibration_library_artifacts(
                    bundle_id, ordinal, artifact_id, reference_kind, role, source_index,
                    manifest_relative_path, manifest_sha256, payload_relative_path, payload_sha256,
                    ordered_source_artifact_ids_json, master_recipe_json)
                VALUES ($bundle, $ordinal, $artifact, $kind, $role, $source_index,
                        $manifest, $manifest_sha, $payload, $payload_sha, $sources, $recipe);
                """;
            Add(command, "$bundle", bundle.BundleId);
            Add(command, "$ordinal", ordinal);
            Add(command, "$artifact", artifact.ArtifactId.ToString("N"));
            Add(command, "$kind", artifact.Kind);
            Add(command, "$role", artifact.Role);
            Add(command, "$source_index", DbValue(artifact.SourceIndex));
            Add(command, "$manifest", artifact.ManifestRelativePath);
            Add(command, "$manifest_sha", Sha256($"manifest-{bundle.BundleId}-{ordinal}"));
            Add(command, "$payload", artifact.ManifestRelativePath[..^".json".Length] + ".bin");
            Add(command, "$payload_sha", artifact.PayloadSha256);
            Add(command, "$sources", JsonSerializer.SerializeToUtf8Bytes(artifact.OrderedSourceArtifactIds));
            Add(command, "$recipe", artifact.MasterBuildRecipe is null
                ? DBNull.Value
                : JsonSerializer.SerializeToUtf8Bytes(artifact.MasterBuildRecipe));
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<C208UiReadEvidence> MeasureUiReadsAsync(
        CameraAgentCalibrationUiService service,
        OwnerAuthorizationFixture owner,
        CalibrationLibraryBundleV1 compatibleBundle,
        CancellationToken cancellationToken)
    {
        for (var round = 0; round < UiWarmupRounds; round++)
        {
            AssertUiPage(RequireSuccess(await service.GetBundlesAsync(
                PageSize, cursor: null, cancellationToken).ConfigureAwait(false)));
            AssertUiDetail(
                RequireSuccess(await service.GetBundleAsync(
                    compatibleBundle.BundleId, cancellationToken).ConfigureAwait(false)),
                compatibleBundle);
            AssertUiStatus(
                RequireSuccess(await service.GetStatusAsync(cancellationToken).ConfigureAwait(false)),
                compatibleBundle);
        }

        var page = await MeasureUiReadAsync(
            "GetBundlesAsync(pageSize: 100, newest)",
            () => service.GetBundlesAsync(PageSize, cursor: null, cancellationToken),
            AssertUiPage).ConfigureAwait(false);
        var detail = await MeasureUiReadAsync(
            "GetBundleAsync(real compatible bundle id)",
            () => service.GetBundleAsync(compatibleBundle.BundleId, cancellationToken),
            value => AssertUiDetail(value, compatibleBundle)).ConfigureAwait(false);
        var status = await MeasureUiReadAsync(
            "GetStatusAsync",
            () => service.GetStatusAsync(cancellationToken),
            value => AssertUiStatus(value, compatibleBundle)).ConfigureAwait(false);

        var expectedAuthorizations = (UiWarmupRounds + UiMeasuredOperations) * 3;
        Assert.AreEqual(expectedAuthorizations, owner.Authentication.ReadCount);
        Assert.AreEqual(expectedAuthorizations, owner.Authorization.ReadPolicyEvaluations);
        Assert.AreEqual(expectedAuthorizations, owner.Authorization.SuccessfulReadAuthorizations);
        Assert.AreEqual(expectedAuthorizations, owner.UserLookups.Count);
        Assert.AreEqual(0, owner.Authorization.DeniedPolicyEvaluations);

        return new C208UiReadEvidence(
            UiWarmupRounds,
            UiMeasuredOperations,
            AuthenticatedClientCount: 1,
            AuthenticationStateReads: owner.Authentication.ReadCount,
            AuthorizationEvaluations: owner.Authorization.ReadPolicyEvaluations,
            OwnerIdentityLookups: owner.UserLookups.Count,
            AuthenticatedOwnerId: owner.Principal.FindFirstValue(ClaimTypes.NameIdentifier)!,
            AuthorizationImplementation:
                "Production OperationsReadV1 policy and SiteOwnerAuthorizationHandler with a deterministic owner identity lookup, wrapped to reject every non-read policy.",
            AuthorizedPolicies: [CameraAgentAuthorizationPolicyNames.OperationsReadV1],
            RejectedMutationPolicies: [CameraAgentAuthorizationPolicyNames.OperationsMutateV1],
            MutationDependencyDisposition:
                "No mutation method is invoked. The read-only authorization wrapper rejects every policy except OperationsReadV1, so the deliberately unused null mutation coordinators are unreachable.",
            BundlesNewestPage: page,
            CompatibleBundleDetail: detail,
            Status: status,
            FunctionalEvidenceDisposition:
                "This in-process authenticated read envelope does not replace browser acquisition/activation/rollback functional evidence.",
            ExistingFunctionalGates:
            [
                "CameraAgentBrowserAcceptanceTests.OwnerOperationsGalleryAndResponsiveAcceptanceAsync",
                "CalibrationPageTests.Render_ShowsStatusAcquisitionAndLibrary",
                "CalibrationPageTests.AcquireRetry_AfterUnavailable_ReusesPinnedRequestKeyAndVersion",
                "OwnerAuthorizationTests.OperationsSummaryAndMutationEndpointsEnforceOwnerAndAntiforgeryAsync"
            ]);
    }

    private static async Task<UiReadOperationMeasurement> MeasureUiReadAsync<T>(
        string operation,
        Func<ValueTask<OperatorUiResult<T>>> read,
        Action<T> validate)
        where T : class
    {
        var samples = new double[UiMeasuredOperations];
        var rssBefore = Environment.WorkingSet;
        var peakRss = rssBefore;
        var lohBefore = GetLohSize();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        T? last = null;
        var batchStarted = Stopwatch.GetTimestamp();
        for (var index = 0; index < UiMeasuredOperations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            last = RequireSuccess(await read().ConfigureAwait(false));
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            validate(last);
            UpdateMaximum(ref peakRss, Environment.WorkingSet);
        }
        var elapsed = Stopwatch.GetElapsedTime(batchStarted);
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var rssAfter = Environment.WorkingSet;
        var lohAfter = GetLohSize();
        UpdateMaximum(ref peakRss, rssAfter);
        Array.Sort(samples);
        return new UiReadOperationMeasurement(
            operation,
            UiMeasuredOperations,
            elapsed.TotalMilliseconds,
            UiMeasuredOperations / elapsed.TotalSeconds,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            samples[0],
            samples[^1],
            cpu.TotalMilliseconds,
            cpu.TotalMilliseconds * 100d / elapsed.TotalMilliseconds,
            allocated,
            (double)allocated / UiMeasuredOperations,
            rssBefore,
            peakRss,
            rssAfter,
            lohBefore,
            lohAfter,
            ReadCanonicalResponse(last));
    }

    private static void AssertUiPage(CalibrationUiBundlePage page)
    {
        Assert.HasCount(PageSize, page.Items);
        Assert.AreEqual(PageSize, page.Items.Select(static item => item.BundleId).Distinct().Count());
        Assert.IsFalse(string.IsNullOrWhiteSpace(page.NextCursor));
        Assert.IsTrue(page.Items.All(static item => item.PublicationState == "incomplete"));
    }

    private static void AssertUiDetail(
        CalibrationUiBundleDetail detail,
        CalibrationLibraryBundleV1 compatibleBundle)
    {
        Assert.AreEqual(compatibleBundle.BundleId, detail.Summary.BundleId);
        Assert.AreEqual(
            CalibrationLibraryContractJson.ComputeIdentitySha256(compatibleBundle),
            detail.Summary.BundleIdentitySha256);
        Assert.AreEqual(compatibleBundle.ProfileIdentitySha256, detail.Summary.ProfileIdentitySha256);
        Assert.AreEqual(compatibleBundle.AcquisitionModelIdentitySha256, detail.Summary.AcquisitionModelIdentitySha256);
        Assert.AreEqual("published", detail.Summary.PublicationState);
        Assert.AreEqual(0, detail.Summary.SourceCount);
        Assert.AreEqual(CalibrationReferenceKinds.All.Count, detail.Summary.MasterCount);
        Assert.HasCount(CalibrationReferenceKinds.All.Count, detail.Artifacts);
        CollectionAssert.AreEquivalent(
            compatibleBundle.Artifacts.Select(static artifact => artifact.ArtifactId).ToArray(),
            detail.Artifacts.Select(static artifact => artifact.ArtifactId).ToArray());
        CollectionAssert.AreEquivalent(
            CalibrationReferenceKinds.All.ToArray(),
            detail.Artifacts.Select(static artifact => artifact.Kind).ToArray());
    }

    private static void AssertUiStatus(
        CalibrationUiStatus status,
        CalibrationLibraryBundleV1 compatibleBundle)
    {
        Assert.AreEqual(1L, status.Version);
        Assert.AreEqual(compatibleBundle.BundleId, status.ActiveBundle?.BundleId);
        Assert.AreEqual(
            CalibrationLibraryContractJson.ComputeIdentitySha256(compatibleBundle),
            status.ActiveBundle?.BundleIdentitySha256);
        Assert.AreEqual(1L, status.PublishedBundleCount);
        Assert.AreEqual(0L, status.QuarantineCount);
        Assert.IsNull(status.PendingAcquisition);
        Assert.AreEqual(compatibleBundle.BundleId, status.LastActivation?.ToBundleId);
        Assert.AreEqual(1L, status.LastActivation?.StateVersion);
    }

    private static T RequireSuccess<T>(OperatorUiResult<T> result)
        where T : class
    {
        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind, result.Message);
        return result.Value ?? throw new AssertFailedException("A successful UI response did not contain a value.");
    }

    private static CanonicalResponseEvidence ReadCanonicalResponse<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var element = JsonSerializer.SerializeToElement(value, UiResponseJsonOptions);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(element));
        return new CanonicalResponseEvidence(bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static OwnerAuthorizationFixture CreateOwnerAuthorizationFixture()
    {
        const string ownerId = "issue-208-ui-owner";
        const string ownerEmail = "issue-208-owner@cameraagent.test";
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, ownerId),
            new Claim(ClaimTypes.Email, ownerEmail)
        ], "issue-208-performance"));
        var user = new ApplicationUser
        {
            Id = ownerId,
            UserName = ownerEmail,
            Email = ownerEmail,
            NormalizedEmail = ownerEmail.ToUpperInvariant(),
            IsSiteOwner = true
        };
        var lookups = new OwnerLookupCounter();
        var userManager = new Mock<UserManager<ApplicationUser>>(
            Mock.Of<IUserStore<ApplicationUser>>(),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(),
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            Mock.Of<IServiceProvider>(),
            NullLogger<UserManager<ApplicationUser>>.Instance);
        userManager.Setup(manager => manager.GetUserAsync(principal))
            .Callback(lookups.Increment)
            .ReturnsAsync(user);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(userManager.Object);
        services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = ownerEmail }));
        services.AddCameraAgentAuthorization();
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var productionAuthorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var authorization = new ReadOnlyOwnerAuthorizationService(productionAuthorization, principal);
        return new OwnerAuthorizationFixture(
            provider,
            scope,
            principal,
            new OwnerAuthenticationStateProvider(principal),
            authorization,
            lookups);
    }

    private static async Task<SelectionMeasurement> MeasureSelectionsAsync(
        SqliteCalibrationLibraryStore store,
        int workerCount,
        ReconstructionDescriptor light,
        int warmupCount,
        int selectionCount,
        string databasePath)
    {
        await RunDistributedAsync(workerCount, warmupCount, async (_, _) =>
        {
            var result = await store.SelectAsync(light, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(result.IsSelected, result.ReasonCode);
        }).ConfigureAwait(false);

        var filesBefore = ReadFileSizes(databasePath);
        var latencies = new double[selectionCount];
        var selected = 0;
        var concurrency = new ConcurrencyTracker();
        var rssBefore = Environment.WorkingSet;
        var peakRss = rssBefore;
        var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var barrier = new WorkerStartBarrier(workerCount);
        var ranges = Distribute(selectionCount, workerCount);
        var tasks = Enumerable.Range(0, workerCount).Select(worker => Task.Run(async () =>
        {
            var range = ranges[worker];
            for (var local = 0; local < range.Count; local++)
            {
                concurrency.Enter();
                try
                {
                    if (local == 0)
                    {
                        barrier.Arrive();
                        await barrier.Release.ConfigureAwait(false);
                    }
                    var started = Stopwatch.GetTimestamp();
                    var result = await store.SelectAsync(light, CancellationToken.None).ConfigureAwait(false);
                    latencies[range.Offset + local] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (result.IsSelected && result.StateVersion == 1)
                    {
                        Interlocked.Increment(ref selected);
                    }
                }
                finally
                {
                    concurrency.Exit();
                }
                if ((local & 63) == 0)
                {
                    UpdateMaximum(ref peakRss, Environment.WorkingSet);
                }
            }
        })).ToArray();
        await barrier.AllArrived.ConfigureAwait(false);
        var batchStarted = Stopwatch.GetTimestamp();
        barrier.Open();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(batchStarted);
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
        var rssAfter = Environment.WorkingSet;
        UpdateMaximum(ref peakRss, rssAfter);
        Array.Sort(latencies);
        return new SelectionMeasurement(
            workerCount,
            1,
            warmupCount,
            selectionCount,
            selected,
            selected == selectionCount,
            concurrency.Maximum,
            elapsed.TotalMilliseconds,
            selectionCount / elapsed.TotalSeconds,
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            latencies[0],
            latencies[^1],
            cpu.TotalMilliseconds,
            cpu.TotalMilliseconds * 100d / elapsed.TotalMilliseconds,
            allocated,
            (double)allocated / selectionCount,
            rssBefore,
            peakRss,
            rssAfter,
            ReadFileSizes(databasePath).WalBytes - filesBefore.WalBytes,
            latencies);
    }

    private static async Task<PageMeasurement> MeasurePagesAsync(
        SqliteCalibrationLibraryStore store,
        int workerCount,
        int operationCount,
        string databasePath)
    {
        var warm = await store.GetBundlePageAsync(PageSize, null, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(PageSize, warm.Items);
        var filesBefore = ReadFileSizes(databasePath);
        var samples = new double[operationCount];
        var minimumRows = int.MaxValue;
        var maximumRows = 0;
        var concurrency = new ConcurrencyTracker();
        var rssBefore = Environment.WorkingSet;
        var peakRss = rssBefore;
        var ranges = Distribute(operationCount, workerCount);
        var barrier = new WorkerStartBarrier(workerCount);
        var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var tasks = Enumerable.Range(0, workerCount).Select(worker => Task.Run(async () =>
        {
            var range = ranges[worker];
            for (var local = 0; local < range.Count; local++)
            {
                concurrency.Enter();
                try
                {
                    if (local == 0)
                    {
                        barrier.Arrive();
                        await barrier.Release.ConfigureAwait(false);
                    }
                    var started = Stopwatch.GetTimestamp();
                    var page = await store.GetBundlePageAsync(PageSize, null, CancellationToken.None).ConfigureAwait(false);
                    samples[range.Offset + local] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    UpdateMinimum(ref minimumRows, page.Items.Count);
                    UpdateMaximum(ref maximumRows, page.Items.Count);
                    UpdateMaximum(ref peakRss, Environment.WorkingSet);
                }
                finally
                {
                    concurrency.Exit();
                }
            }
        })).ToArray();
        await barrier.AllArrived.ConfigureAwait(false);
        var batchStarted = Stopwatch.GetTimestamp();
        barrier.Open();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(batchStarted);
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
        var rssAfter = Environment.WorkingSet;
        UpdateMaximum(ref peakRss, rssAfter);
        Array.Sort(samples);
        return new PageMeasurement(
            workerCount,
            1,
            concurrency.Maximum,
            operationCount,
            PageSize,
            minimumRows,
            maximumRows,
            elapsed.TotalMilliseconds,
            operationCount / elapsed.TotalSeconds,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            samples[0],
            samples[^1],
            cpu.TotalMilliseconds,
            cpu.TotalMilliseconds * 100d / elapsed.TotalMilliseconds,
            allocated,
            (double)allocated / operationCount,
            rssBefore,
            peakRss,
            rssAfter,
            ReadFileSizes(databasePath).WalBytes - filesBefore.WalBytes);
    }

    private static async Task RunDistributedAsync(
        int workerCount,
        int operationCount,
        Func<int, int, Task> operation)
    {
        var ranges = Distribute(operationCount, workerCount);
        var tasks = Enumerable.Range(0, workerCount).Select(async worker =>
        {
            for (var local = 0; local < ranges[worker].Count; local++)
            {
                await operation(worker, local).ConfigureAwait(false);
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static WorkRange[] Distribute(int operationCount, int workerCount)
    {
        var ranges = new WorkRange[workerCount];
        var offset = 0;
        for (var worker = 0; worker < workerCount; worker++)
        {
            var count = operationCount / workerCount + (worker < operationCount % workerCount ? 1 : 0);
            ranges[worker] = new WorkRange(offset, count);
            offset += count;
        }
        return ranges;
    }

    private static async Task<QueryPlanEvidence[]> ReadQueryPlansAsync(string databasePath, string activeBundleId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        return
        [
            await ReadQueryPlanAsync(
                connection,
                "selection-state",
                "SELECT active_bundle_id, version FROM calibration_library_state WHERE state_key = 1;")
                .ConfigureAwait(false),
            await ReadQueryPlanAsync(
                connection,
                "selection-active-bundle",
                "SELECT bundle_json FROM calibration_library_bundles WHERE bundle_id = $id;",
                ("$id", activeBundleId)).ConfigureAwait(false),
            await ReadQueryPlanAsync(
                connection,
                "selection-published-count",
                "SELECT COUNT(*) FROM calibration_library_bundles WHERE publication_state = 'published';")
                .ConfigureAwait(false),
            await ReadQueryPlanAsync(
                connection,
                "first-page-100",
                "SELECT bundle_id FROM calibration_library_bundles " +
                "WHERE $has_cursor = 0 OR created_unix_ms < $created OR " +
                "(created_unix_ms = $created AND bundle_id > $bundle) " +
                "ORDER BY created_unix_ms DESC, bundle_id LIMIT $limit;",
                ("$has_cursor", 0), ("$created", 0), ("$bundle", string.Empty), ("$limit", PageSize + 1))
                .ConfigureAwait(false)
        ];
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only fixed SQL statements declared by this performance test are explained.")]
    private static async Task<QueryPlanEvidence> ReadQueryPlanAsync(
        SqliteConnection connection,
        string name,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        foreach (var parameter in parameters)
        {
            Add(command, parameter.Name, parameter.Value);
        }
        var details = new List<string>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            details.Add(reader.GetString(3));
        }
        return new QueryPlanEvidence(
            name,
            sql,
            details.ToArray(),
            details.Any(static detail => detail.Contains("INDEX", StringComparison.OrdinalIgnoreCase)));
    }

    private static void AssertQueryPlans(IReadOnlyList<QueryPlanEvidence> plans)
    {
        Assert.IsTrue(plans.Single(plan => plan.Name == "selection-state").Details.Any(
            static detail => detail.Contains("INTEGER PRIMARY KEY", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(plans.Single(plan => plan.Name == "selection-active-bundle").UsesIndex);
        Assert.IsTrue(plans.Single(plan => plan.Name == "selection-published-count").Details.Any(
            static detail => detail.Contains(
                "ix_calibration_library_bundles_selection", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(plans.Single(plan => plan.Name == "first-page-100").Details.Any(
            static detail => detail.Contains(
                "ix_calibration_library_bundles_created", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task<DatabaseEvidence> ReadDatabaseEvidenceAsync(
        string databasePath,
        ReconstructionDescriptor light,
        string expectedActiveBundleId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        var rows = new RowCounts(
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM calibration_library_bundles;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM calibration_library_artifacts;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM calibration_library_state;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM calibration_library_activations;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM calibration_library_commands;").ConfigureAwait(false));
        using var compatible = connection.CreateCommand();
        compatible.CommandText = """
            SELECT COUNT(*) FROM calibration_library_bundles
            WHERE agent_id = $agent AND rig_id = $rig
              AND rig_profile_sha256 = $rig_profile AND sensor_profile_sha256 = $sensor_profile
              AND minimum_gain <= $gain AND maximum_gain >= $gain
              AND effective_from_unix_ms <= $observed
              AND (effective_until_unix_ms IS NULL OR effective_until_unix_ms > $observed);
            """;
        Add(compatible, "$agent", light.Capture.AgentId);
        Add(compatible, "$rig", light.Capture.RigId);
        Add(compatible, "$rig_profile", light.Profiles.Rig.Sha256);
        Add(compatible, "$sensor_profile", light.Profiles.Sensor.Sha256);
        Add(compatible, "$gain", light.Controls.EffectiveGain);
        Add(compatible, "$observed", light.Timing.ExposureStartedUtc.ToUnixTimeMilliseconds());
        var compatibleCount = Convert.ToInt64(
            await compatible.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
        var publishedCount = await ScalarLongAsync(
            connection, "SELECT COUNT(*) FROM calibration_library_bundles WHERE publication_state = 'published';")
            .ConfigureAwait(false);
        using var metadataOnly = connection.CreateCommand();
        metadataOnly.CommandText = """
            SELECT COUNT(*) FROM calibration_library_bundles
            WHERE publication_state = 'incomplete' AND retention_hold = 0 AND failure_reason = $reason;
            """;
        Add(metadataOnly, "$reason", MetadataOnlyFailureReason);
        var metadataOnlyCount = Convert.ToInt64(
            await metadataOnly.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
        var retentionHeldCount = await ScalarLongAsync(
            connection, "SELECT COUNT(*) FROM calibration_library_bundles WHERE retention_hold = 1;")
            .ConfigureAwait(false);
        var activeBundleId = await ScalarStringAsync(
            connection, "SELECT active_bundle_id FROM calibration_library_state WHERE state_key = 1;")
            .ConfigureAwait(false);
        Assert.AreEqual(expectedActiveBundleId, activeBundleId);
        return new DatabaseEvidence(
            rows,
            compatibleCount,
            rows.Bundles - compatibleCount,
            publishedCount,
            metadataOnlyCount,
            retentionHeldCount,
            activeBundleId,
            await ScalarStringAsync(connection, "PRAGMA integrity_check;").ConfigureAwait(false),
            await CountRowsAsync(connection, "PRAGMA foreign_key_check;").ConfigureAwait(false),
            await ScalarStringAsync(connection, "PRAGMA journal_mode;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "PRAGMA synchronous;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "PRAGMA user_version;").ConfigureAwait(false),
            await ScalarStringAsync(connection, "SELECT sqlite_version();").ConfigureAwait(false));
    }

    private static async Task CheckpointAsync(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        Assert.AreEqual(0L, reader.GetInt64(0));
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
        => Convert.ToInt64(await ScalarAsync(connection, sql).ConfigureAwait(false), CultureInfo.InvariantCulture);

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
        => Convert.ToString(await ScalarAsync(connection, sql).ConfigureAwait(false), CultureInfo.InvariantCulture)
            ?? throw new InvalidDataException("A required SQLite scalar was null.");

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only fixed SQL statements declared by this performance test are executed.")]
    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only fixed SQL statements declared by this performance test are executed.")]
    private static async Task<long> CountRowsAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var count = 0L;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            count++;
        }
        return count;
    }

    private static SqliteCalibrationLibraryStore CreateStore(IOptions<CameraAgentHostOptions> options)
        => new(new InitializedIngress(), options, new FixedTimeProvider(FixedUtcNow));

    private static int ReadCount(string variable, int defaultValue, int minimum)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (raw is null)
        {
            return defaultValue;
        }
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum)
        {
            throw new InvalidOperationException($"{variable} must be an integer greater than or equal to {minimum}.");
        }
        return value;
    }

    private static string GetEvidenceRevision()
    {
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "working-tree";
        if (string.IsNullOrWhiteSpace(revision) || revision is "." or ".." ||
            revision.Any(static character => character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')))
        {
            throw new InvalidOperationException(
                "HVO_EVIDENCE_REVISION must contain only ASCII letters, digits, period, underscore, or hyphen.");
        }
        return revision;
    }

    private static string CreateExactCommand(
        string revision,
        int bundleCount,
        int warmupCount,
        int selectionCount)
    {
        var variables = new List<string> { $"HVO_EVIDENCE_REVISION={revision}" };
        if (bundleCount != CanonicalBundleCount)
        {
            variables.Add($"{BundleCountVariable}={bundleCount}");
        }
        if (warmupCount != CanonicalWarmupCount)
        {
            variables.Add($"{WarmupCountVariable}={warmupCount}");
        }
        if (selectionCount != CanonicalSelectionCount)
        {
            variables.Add($"{SelectionCountVariable}={selectionCount}");
        }
        var gcServer = Environment.GetEnvironmentVariable("DOTNET_gcServer");
        if (!string.IsNullOrWhiteSpace(gcServer))
        {
            variables.Add($"DOTNET_gcServer={gcServer}");
        }
        return string.Join(' ', variables) +
               " dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj" +
               " --no-build --configuration " + BuildConfiguration +
               " --filter \"FullyQualifiedName=HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration." +
               nameof(C208W3MCalibrationLibraryRetainedPerformanceTests) + "." +
               nameof(W3M_OneCompatibleActiveAmongTenThousandBundles_WritesRetainedEvidence) + "\"";
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
            var separator = model?.IndexOf(':', StringComparison.Ordinal) ?? -1;
            if (separator >= 0)
            {
                return model![(separator + 1)..].Trim();
            }
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
    }

    private static AssemblyEvidence ReadAssemblyEvidence(Type type)
        => new(
            type.Assembly.GetName().Name ?? type.FullName ?? "unknown",
            FileSha256(type.Assembly.Location));

    private static async Task<GitEvidence> ReadGitEvidenceAsync(string repositoryRoot)
    {
        var head = (await RunProcessAsync(repositoryRoot, "git", "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
        var branch = (await RunProcessAsync(
            repositoryRoot, "git", "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
        var status = await RunProcessAsync(
            repositoryRoot, "git", "status", "--porcelain=v1", "--untracked-files=all").ConfigureAwait(false);
        return new GitEvidence(head, branch, !string.IsNullOrWhiteSpace(status), Sha256(status));
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
            ?? throw new InvalidOperationException($"Unable to start {fileName} for evidence collection.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed while collecting evidence: {error}");
        }
        return output;
    }

    private static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static Guid DeterministicGuid(string value)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private static object DbValue<T>(T? value) where T : struct
        => value.HasValue ? value.Value : DBNull.Value;

    private static void Add(SqliteCommand command, string name, object value)
        => command.Parameters.AddWithValue(name, value);

    private static double Percentile(double[] sorted, double percentile)
        => sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static long GetLohSize()
    {
        var generations = GC.GetGCMemoryInfo().GenerationInfo;
        return generations.Length > 3 ? generations[3].SizeAfterBytes : 0;
    }

    private static FileSizeEvidence ReadFileSizes(string databasePath)
        => new(
            FileSize(databasePath),
            FileSize(string.Concat(databasePath, "-wal")),
            FileSize(string.Concat(databasePath, "-shm")));

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static void UpdateMaximum(ref long target, long value)
    {
        long current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static void UpdateMinimum(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value >= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private sealed class ConcurrencyTracker
    {
        private int _current;
        private int _maximum;

        public int Maximum => Volatile.Read(ref _maximum);

        public void Enter()
        {
            var current = Interlocked.Increment(ref _current);
            UpdateMaximum(ref _maximum, current);
        }

        public void Exit() => Interlocked.Decrement(ref _current);
    }

    private sealed class WorkerStartBarrier(int participantCount)
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task AllArrived => _allArrived.Task;

        public Task Release => _release.Task;

        public void Arrive()
        {
            if (Interlocked.Increment(ref _arrived) == participantCount)
            {
                _allArrived.SetResult();
            }
        }

        public void Open() => _release.SetResult();
    }

    private sealed class OwnerAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            Interlocked.Increment(ref _readCount);
            return Task.FromResult(new AuthenticationState(principal));
        }
    }

    private sealed class ReadOnlyOwnerAuthorizationService(
        IAuthorizationService productionAuthorization,
        ClaimsPrincipal expectedOwner) : IAuthorizationService
    {
        private int _deniedPolicyEvaluations;
        private int _readPolicyEvaluations;
        private int _successfulReadAuthorizations;

        public int DeniedPolicyEvaluations => Volatile.Read(ref _deniedPolicyEvaluations);

        public int ReadPolicyEvaluations => Volatile.Read(ref _readPolicyEvaluations);

        public int SuccessfulReadAuthorizations => Volatile.Read(ref _successfulReadAuthorizations);

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements)
        {
            Interlocked.Increment(ref _deniedPolicyEvaluations);
            return Task.FromResult(AuthorizationResult.Failed());
        }

        public async Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName)
        {
            if (!ReferenceEquals(user, expectedOwner) ||
                !string.Equals(
                    policyName,
                    CameraAgentAuthorizationPolicyNames.OperationsReadV1,
                    StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _deniedPolicyEvaluations);
                return AuthorizationResult.Failed();
            }
            Interlocked.Increment(ref _readPolicyEvaluations);
            var result = await productionAuthorization.AuthorizeAsync(user, resource, policyName).ConfigureAwait(false);
            if (result.Succeeded)
            {
                Interlocked.Increment(ref _successfulReadAuthorizations);
            }
            return result;
        }
    }

    private sealed class OwnerLookupCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class OwnerAuthorizationFixture(
        ServiceProvider provider,
        IServiceScope scope,
        ClaimsPrincipal principal,
        OwnerAuthenticationStateProvider authentication,
        ReadOnlyOwnerAuthorizationService authorization,
        OwnerLookupCounter userLookups) : IDisposable
    {
        public ClaimsPrincipal Principal { get; } = principal;

        public OwnerAuthenticationStateProvider Authentication { get; } = authentication;

        public ReadOnlyOwnerAuthorizationService Authorization { get; } = authorization;

        public OwnerLookupCounter UserLookups { get; } = userLookups;

        public void Dispose()
        {
            scope.Dispose();
            provider.Dispose();
        }
    }

    private sealed class InitializedIngress : IRawCaptureIngress
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record CompatibleFixture(
        CalibrationLibraryBundleV1 Bundle,
        ReconstructionDescriptor Light);

    private sealed record WorkRange(int Offset, int Count);

    private sealed record FileSizeEvidence(long DatabaseBytes, long WalBytes, long SharedMemoryBytes);

    private sealed record C208UiReadEvidence(
        int WarmupRounds,
        int MeasuredOperationsPerRead,
        int AuthenticatedClientCount,
        int AuthenticationStateReads,
        int AuthorizationEvaluations,
        int OwnerIdentityLookups,
        string AuthenticatedOwnerId,
        string AuthorizationImplementation,
        IReadOnlyList<string> AuthorizedPolicies,
        IReadOnlyList<string> RejectedMutationPolicies,
        string MutationDependencyDisposition,
        UiReadOperationMeasurement BundlesNewestPage,
        UiReadOperationMeasurement CompatibleBundleDetail,
        UiReadOperationMeasurement Status,
        string FunctionalEvidenceDisposition,
        IReadOnlyList<string> ExistingFunctionalGates);

    private sealed record UiReadOperationMeasurement(
        string Operation,
        int MeasuredOperations,
        double WallMilliseconds,
        double OperationsPerSecond,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MinimumMilliseconds,
        double MaximumMilliseconds,
        double CpuMilliseconds,
        double CpuUtilizationOfOneCorePercent,
        long AllocatedBytes,
        double AllocatedBytesPerOperation,
        long RssBeforeBytes,
        long PeakSampledRssBytes,
        long RssAfterBytes,
        long LohBeforeBytes,
        long LohAfterBytes,
        CanonicalResponseEvidence ResponseCanonicalJson);

    private sealed record CanonicalResponseEvidence(long ByteCount, string Sha256);

    private sealed record SelectionMeasurement(
        int RequestedConcurrency,
        int StoreInstanceCount,
        int WarmupOperations,
        int MeasuredOperations,
        int SelectedOperations,
        bool AllSelected,
        int MaximumConcurrency,
        double WallMilliseconds,
        double OperationsPerSecond,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MinimumMilliseconds,
        double MaximumMilliseconds,
        double CpuMilliseconds,
        double CpuUtilizationOfOneCorePercent,
        long AllocatedBytes,
        double AllocatedBytesPerOperation,
        long RssBeforeBytes,
        long PeakSampledRssBytes,
        long RssAfterBytes,
        long WalGrowthBytes,
        [property: System.Text.Json.Serialization.JsonIgnore] double[] SortedLatencyMilliseconds);

    private sealed record PageMeasurement(
        int RequestedConcurrency,
        int StoreInstanceCount,
        int MaximumConcurrency,
        int MeasuredOperations,
        int RequestedRowsPerPage,
        int MinimumRowsPerPage,
        int MaximumRowsPerPage,
        double WallMilliseconds,
        double OperationsPerSecond,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MinimumMilliseconds,
        double MaximumMilliseconds,
        double CpuMilliseconds,
        double CpuUtilizationOfOneCorePercent,
        long AllocatedBytes,
        double AllocatedBytesPerOperation,
        long RssBeforeBytes,
        long PeakSampledRssBytes,
        long RssAfterBytes,
        long WalGrowthBytes);

    private sealed record QueryPlanEvidence(
        string Name,
        string Sql,
        IReadOnlyList<string> Details,
        bool UsesIndex);

    private sealed record RowCounts(
        long Bundles,
        long Artifacts,
        long StateRows,
        long Activations,
        long Commands);

    private sealed record DatabaseEvidence(
        RowCounts Rows,
        long CompatibleBundleCount,
        long IncompatibleBundleCount,
        long PublishedBundleCount,
        long MetadataOnlyBundleCount,
        long RetentionHeldBundleCount,
        string ActiveBundleId,
        string IntegrityCheck,
        long ForeignKeyFailureCount,
        string JournalMode,
        long Synchronous,
        long UserVersion,
        string SqliteVersion);

    private sealed record AssemblyEvidence(string Name, string Sha256);

    private sealed record GitEvidence(string Head, string Branch, bool Dirty, string StatusSha256);
}
