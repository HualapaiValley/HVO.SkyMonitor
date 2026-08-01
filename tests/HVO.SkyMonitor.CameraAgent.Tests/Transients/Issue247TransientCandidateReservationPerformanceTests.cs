using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed partial class Issue247TransientCandidateReservationPerformanceTests
{
    private const string Schema = "hvo-issue-247-transient-candidate-reservation-evidence-v1";
    private const string BaselineRevision = "4a7501610e5ddf426131fa1a2030fc3b06757a40";
    private const string ProfileName = "virtual-asi178mc.full.json";
    private const int Seed = 2025;
    private const int Width = 3096;
    private const int Height = 2080;
    private const int Stride = Width * 2;
    private const int PayloadLength = 12_879_360;
    private const int RawRows = 10_000;
    private const int PhysicalRows = 100;
    private const int Warmups = 5;
    private const int Measured = 30;
    private const int SourcesPerReservation = 5;
    private const int WriterCadenceMilliseconds = 100;
    private static readonly DateTimeOffset Epoch = new(2026, 7, 20, 11, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan BarrierDuration = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] BaselineEnvelope =
    [
        "docs/runbooks/ci-pipeline.md",
        "scripts/test-categories/Program.cs",
        "tests/HVO.SkyMonitor.CameraAgent.Tests/Transients/Issue247TransientCandidateReservationPerformanceTests.cs"
    ];
    private static readonly string[] ImmutableHarnessFiles =
    [
        "tests/HVO.SkyMonitor.CameraAgent.Tests/Transients/Issue247TransientCandidateReservationPerformanceTests.cs",
        "tests/HVO.SkyMonitor.CameraAgent.Tests/Transients/SqliteTransientCandidateJournalTests.cs",
        "src/HVO.SkyMonitor.TestSupport/EvidenceSourceIdentity.cs",
        "src/HVO.SkyMonitor.CameraAgent/virtual-asi178mc.full.json",
        "tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj",
        "Directory.Build.props",
        "Directory.Packages.props",
        "global.json",
        "src/HVO.SkyMonitor.CameraAgent.Common/RawIngress/SqliteRawCaptureJournal.cs",
        "src/HVO.SkyMonitor.CameraAgent.Common/Capture/Distribution/SqliteCaptureLaneStore.cs",
        "src/HVO.SkyMonitor.CameraAgent.Common/Scheduling/SqliteCaptureScheduleStore.cs",
        "src/HVO.SkyMonitor.CameraAgent.Common/Capture/CaptureAdmissionCoordinator.cs",
        "src/HVO.SkyMonitor.CameraAgent.Common/Storage/StorageCapacity.cs",
        "src/HVO.SkyMonitor.AgentCore/ArtifactManifestV2.cs",
        "src/HVO.SkyMonitor.AgentCore/CaptureContracts.cs",
        "src/HVO.SkyMonitor.AgentCore/CaptureContractJson.cs",
        "src/HVO.SkyMonitor.AgentCore/CaptureContractValidation.cs",
        "src/HVO.SkyMonitor.Processing/TransientEventContracts.cs",
        "src/HVO.SkyMonitor.Processing/TransientContractJson.cs",
        "src/HVO.SkyMonitor.Processing/TransientCandidateDeliveryContracts.cs",
        "src/HVO.SkyMonitor.Processing/ProcessingIdentity.cs"
    ];
    private static readonly string[] MutableProductionFiles =
    [
        "src/HVO.SkyMonitor.CameraAgent.Common/Transients/SqliteTransientCandidateJournal.cs",
        "src/HVO.SkyMonitor.CameraAgent.Common/Transients/TransientCandidateFaults.cs"
    ];

    private static readonly WriterDefinition[] Writers =
    [
        WriterDefinition.Create(
            "raw",
            "src/HVO.SkyMonitor.CameraAgent.Common/RawIngress/SqliteRawCaptureJournal.cs:455",
            "UPDATE raw_captures SET evidence_origin = $origin WHERE raw_capture_row_id = $raw;",
            [new("$origin", "Text"), new("$raw", "Integer")]),
        WriterDefinition.Create(
            "lane",
            "src/HVO.SkyMonitor.CameraAgent.Common/Capture/Distribution/SqliteCaptureLaneStore.cs:243-247",
            """
            UPDATE capture_lane_work
            SET lease_expires_unix_ms = $expires, updated_unix_ms = $now
            WHERE work_id = $work AND state = 'leased'
              AND lease_token = $token AND lease_owner = $owner
              AND lease_expires_unix_ms > $now;
            """,
            [new("$expires", "Integer"), new("$now", "Integer"), new("$work", "Integer"), new("$token", "Text"), new("$owner", "Text")]),
        WriterDefinition.Create(
            "schedule",
            "src/HVO.SkyMonitor.CameraAgent.Common/Scheduling/SqliteCaptureScheduleStore.cs:792-803",
            """
            UPDATE capture_schedule_state
            SET last_evaluated_unix_ms = CASE
                    WHEN last_evaluated_unix_ms IS NULL OR last_evaluated_unix_ms < $decision
                        THEN $decision ELSE last_evaluated_unix_ms END,
                last_decision_unix_ms = $decision,
                last_decision_admitted = $admitted,
                last_decision_reason = $reason,
                last_decision_profile_id = $profile,
                last_decision_interval_id = $interval,
                next_transition_unix_ms = $next,
                updated_unix_ms = $updated
            WHERE state_key = 1 AND active_revision_id = $revision;
            """,
            [new("$decision", "Integer"), new("$admitted", "Integer"), new("$reason", "Text"), new("$profile", "Text"), new("$interval", "Text"), new("$next", "Integer"), new("$updated", "Integer"), new("$revision", "Text")]),
        WriterDefinition.Create(
            "control",
            "src/HVO.SkyMonitor.CameraAgent.Common/Capture/CaptureAdmissionCoordinator.cs:773-775",
            """
            UPDATE capture_control_state
            SET state = $state, version = $version, updated_unix_ms = $updated
            WHERE state_key = 1;
            """,
            [new("$state", "Text"), new("$version", "Integer"), new("$updated", "Integer")])
    ];

    [TestMethod]
    public async Task W2W3MAndW3P_FourWritersAndBarrier_RecordEvidence()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The issue #247 physical-read barrier requires Linux FIFO semantics.");
        }
        var evidenceRun = Environment.GetEnvironmentVariable("HVO_ISSUE_247_EVIDENCE") == "1";
        var smoke = Environment.GetEnvironmentVariable("HVO_ISSUE_247_DEVELOPMENT_SMOKE") == "1";
        if (!evidenceRun && !smoke)
        {
            Assert.Inconclusive("Set HVO_ISSUE_247_EVIDENCE=1 or HVO_ISSUE_247_DEVELOPMENT_SMOKE=1.");
        }
        Assert.IsFalse(evidenceRun && smoke, "Evidence and smoke modes are mutually exclusive.");

        var root = RepositoryRoot();
        var phase = evidenceRun ? Required("HVO_EVIDENCE_PHASE") : "development-smoke";
        if (evidenceRun && phase is not ("baseline" or "after"))
        {
            throw new InvalidOperationException("HVO_EVIDENCE_PHASE must be baseline or after.");
        }
        var trial = evidenceRun ? ReadTrial() : 0;
        var scale = evidenceRun
            ? new Scale(RawRows, PhysicalRows, Warmups, Measured, BarrierDuration, WriterCadenceMilliseconds)
            : new Scale(100, 10, 0, 1, TimeSpan.FromMilliseconds(150), 10);
        AssertWriterSources(root);
        var inputHashes = HashInputs(root);
        var environment = await EnvironmentEvidenceAsync(root).ConfigureAwait(false);
        var workloadDefinitionSha256 = WorkloadDefinitionSha256(inputHashes.ProfileSha256);
        EvidenceSourceSnapshot? sourceStart = null;
        string? productionRevision = null;
        string[] changedPaths = [];
        string? sourceDirectory = null;
        string? output = null;
        string? staging = null;
        if (evidenceRun)
        {
            productionRevision = Required("HVO_EVIDENCE_PRODUCTION_REVISION");
            sourceStart = await EvidenceSourceIdentity.CaptureAsync(
                root, typeof(Issue247TransientCandidateReservationPerformanceTests),
                typeof(CameraAgentServiceCollectionExtensions)).ConfigureAwait(false);
            changedPaths = await ValidateSourceAsync(root, phase, productionRevision, sourceStart, trial).ConfigureAwait(false);
            sourceDirectory = Path.Combine(root, "TestResults", "issue-247", sourceStart.OutputDirectoryName);
            output = Path.Combine(sourceDirectory, $"trial-{trial}");
            staging = Path.Combine(sourceDirectory, $".staging-trial-{trial}");
            if (Directory.Exists(output))
                throw new IOException("Issue #247 output already exists; evidence is create-new and fail-closed.");
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (phase == "after")
            {
                await ValidateReviewedBaselineAsync(
                    inputHashes.ImmutableSha256,
                    workloadDefinitionSha256,
                    environment.EnvironmentFingerprintSha256).ConfigureAwait(false);
            }
        }

        var payload = CreatePayload();
        var payloadSha = Convert.ToHexString(SHA256.HashData(payload));
        var configuration = await LoadConfigurationAsync(root).ConfigureAwait(false);
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var seed = await SeedAsync(fixture.Root, configuration, payload, payloadSha, scale).ConfigureAwait(false);
        await AssertCorruptionBarriersAsync(fixture, seed.Sources[0], payload).ConfigureAwait(false);
        var initial = await fixture.Journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, initial.ActiveCount);
        Assert.AreEqual(0L, initial.HeldSourceBytes);
        Assert.AreEqual(0, initial.PressureLevel);

        using var stageCollector = new ReserveStageCollector();
        var normal = new List<OperationSample>(scale.Warmups + scale.Measured);
        for (var ordinal = 0; ordinal < scale.Warmups; ordinal++)
        {
            normal.Add(await RunNormalAsync(
                fixture, SelectSources(seed.Sources, ordinal), ordinal,
                "warmup", scale).ConfigureAwait(false));
        }
        stageCollector.Reset();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var allocationStart = GC.GetTotalAllocatedBytes(precise: true);
        var rssStart = process.WorkingSet64;
        var peakRss = rssStart;
        var rssSamples = new List<long>(scale.Measured);
        var processIoStart = ReadProcessIo();
        var sizesBeforeMeasured = DatabaseSizes(DatabasePath(fixture.Root));
        for (var index = 0; index < scale.Measured; index++)
        {
            var ordinal = scale.Warmups + index;
            normal.Add(await RunNormalAsync(
                fixture, SelectSources(seed.Sources, ordinal), ordinal, "measured", scale).ConfigureAwait(false));
            process.Refresh();
            peakRss = Math.Max(peakRss, process.WorkingSet64);
            rssSamples.Add(process.WorkingSet64);
        }
        process.Refresh();
        var processIoEnd = ReadProcessIo();
        var resources = new ResourceEvidence(
            (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocationStart,
            rssStart,
            peakRss,
            process.WorkingSet64,
            rssSamples,
            processIoStart,
            processIoEnd,
            processIoEnd - processIoStart);
        var sizesAfterMeasured = DatabaseSizes(DatabasePath(fixture.Root));
        var measuredStageTelemetry = stageCollector.Snapshot();
        stageCollector.Reset();
        var barrierOrdinal = scale.Warmups + scale.Measured;
        var barrier = await RunBarrierAsync(
            fixture, SelectSources(seed.Sources, barrierOrdinal), barrierOrdinal, scale, phase).ConfigureAwait(false);
        var barrierStageTelemetry = stageCollector.Snapshot();
        if (phase == "after")
        {
            Assert.HasCount(scale.Measured, measuredStageTelemetry.SnapshotMilliseconds);
            Assert.HasCount(scale.Measured, measuredStageTelemetry.ImmediateMilliseconds);
            Assert.HasCount(1, barrierStageTelemetry.SnapshotMilliseconds);
            Assert.HasCount(1, barrierStageTelemetry.ImmediateMilliseconds);
        }
        else if (phase == "baseline")
        {
            Assert.IsEmpty(measuredStageTelemetry.SnapshotMilliseconds);
            Assert.IsEmpty(measuredStageTelemetry.ImmediateMilliseconds);
            Assert.IsEmpty(barrierStageTelemetry.SnapshotMilliseconds);
            Assert.IsEmpty(barrierStageTelemetry.ImmediateMilliseconds);
        }
        else
        {
            var unavailable = measuredStageTelemetry.SnapshotMilliseconds.Count == 0
                && measuredStageTelemetry.ImmediateMilliseconds.Count == 0
                && barrierStageTelemetry.SnapshotMilliseconds.Count == 0
                && barrierStageTelemetry.ImmediateMilliseconds.Count == 0;
            var available = measuredStageTelemetry.SnapshotMilliseconds.Count == scale.Measured
                && measuredStageTelemetry.ImmediateMilliseconds.Count == scale.Measured
                && barrierStageTelemetry.SnapshotMilliseconds.Count == 1
                && barrierStageTelemetry.ImmediateMilliseconds.Count == 1;
            Assert.IsTrue(unavailable || available,
                "Development smoke requires either no stage telemetry or a complete corrected-protocol stage set.");
        }
        await AssertConvergenceAsync(fixture, seed, normal, barrier, scale).ConfigureAwait(false);
        var backlog = await fixture.Journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);
        var plans = await CapturePlansAsync(fixture.Root).ConfigureAwait(false);
        var checkpoint = await CheckpointAsync(fixture.Root).ConfigureAwait(false);

        if (smoke)
        {
            TestContext.WriteLine(
                "Issue #247 smoke passed: raw={0}, physical={1}, normal={2}, barrier={3:F3}ms.",
                scale.RawRows, scale.PhysicalRows, normal.Count, barrier.ReservationElapsedMilliseconds);
            return;
        }

        var sourceEnd = await EvidenceSourceIdentity.CaptureAsync(
            root, typeof(Issue247TransientCandidateReservationPerformanceTests),
            typeof(CameraAgentServiceCollectionExtensions)).ConfigureAwait(false);
        AssertSourceEqual(sourceStart!, sourceEnd);
        var sourceIdentity = PrivateSource(sourceStart!);
        var workload = CreateWorkloadIdentity(seed, normal, barrier, scale, payloadSha, inputHashes.ProfileSha256);
        var measured = normal.Where(static sample => sample.Kind == "measured").ToArray();
        var evidence = new
        {
            Schema,
            Issue = 247,
            Phase = phase,
            Trial = trial,
            Source = sourceIdentity,
            ProductionRevision = productionRevision,
            BaselineRevision,
            ChangedPathsFromBaseline = changedPaths,
            ChangedPathsSha256 = HashStrings(changedPaths),
            HarnessDefinitionSha256 = inputHashes.ImmutableSha256,
            HarnessSha256 = inputHashes.AllSha256,
            BehaviorInputsSha256 = inputHashes.AllSha256,
            BehaviorInputFiles = inputHashes.Files,
            WorkloadDefinitionSha256 = workloadDefinitionSha256,
            WorkloadSha256 = workload.Sha256,
            Workload = workload.Public,
            Environment = environment,
            Method = new
            {
                Command = "HVO_ISSUE_247_EVIDENCE=1 HVO_EVIDENCE_REVISION=<HEAD> HVO_EVIDENCE_PRODUCTION_REVISION=<REVISION> HVO_EVIDENCE_PHASE=baseline|after HVO_EVIDENCE_TRIAL=1..5 HVO_EVIDENCE_BASELINE_SUMMARY=<PATH> HVO_EVIDENCE_BASELINE_SUMMARY_SHA256=<SHA256> HVO_EVIDENCE_BASELINE_MANIFEST_SHA256=<SHA256> dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --no-build --configuration Release --filter FullyQualifiedName~Issue247TransientCandidateReservationPerformanceTests.W2W3MAndW3P_FourWritersAndBarrier_RecordEvidence",
                SteadyBoundary = "ReserveAsync only; writer cadence is concurrent but excluded from reservation elapsed time.",
                BarrierBoundary = "One separate reservation with a two-second blocked physical sidecar read and four writer offers.",
                InternalSnapshotDuration = phase == "after" ? "Measured by StageTelemetry transient-candidate.reserve.snapshot activities." : "N/A: baseline production emits no stable snapshot activity.",
                InternalImmediateTransactionDuration = phase == "after" ? "Measured by StageTelemetry transient-candidate.reserve.immediate activities." : "N/A: baseline production emits no stable immediate-transaction activity.",
                InternalReservationStatementCount = "N/A: Microsoft.Data.Sqlite exposes no stable counter for internally opened production connections.",
                WriterThroughput = "N/A: four cadence offers are an attribution probe, not a steady-state throughput workload.",
                BacklogDrainRecovery = "N/A: reservations intentionally remain active; no drain or recovery operation is in this workload.",
                StorageType = "N/A: workspace backing media is not attributable from the test process.",
                RssSampling = "Process RSS is sampled at every measured operation boundary; no intra-operation peak, plateau, or native-allocation attribution claim is made.",
                FsyncAndDeviceIo = "N/A: APIs expose no attributable fsync or device-level I/O counts.",
                AutomaticCheckpointCount = "N/A: Microsoft.Data.Sqlite exposes no automatic WAL checkpoint counter."
            },
            RawSamples = new { Normal = normal, Barrier = barrier },
            StageTelemetry = new
            {
                Operations = new[] { ReserveStageCollector.SnapshotOperation, ReserveStageCollector.ImmediateOperation },
                Availability = phase == "after" ? "measured" : "unavailable-baseline-production-emits-no-stage-activities",
                MeasuredRaw = measuredStageTelemetry,
                BarrierRaw = barrierStageTelemetry,
                MeasuredSnapshotSummary = measuredStageTelemetry.SnapshotMilliseconds.Count >= 30 ? Summary(measuredStageTelemetry.SnapshotMilliseconds) : null,
                MeasuredImmediateSummary = measuredStageTelemetry.ImmediateMilliseconds.Count >= 30 ? Summary(measuredStageTelemetry.ImmediateMilliseconds) : null,
                BarrierSnapshot = barrierStageTelemetry.SnapshotMilliseconds.Count == 1 ? barrierStageTelemetry.SnapshotMilliseconds[0] : (double?)null,
                BarrierImmediate = barrierStageTelemetry.ImmediateMilliseconds.Count == 1 ? barrierStageTelemetry.ImmediateMilliseconds[0] : (double?)null
            },
            Summaries = new
            {
                Reservation = Summary(measured.Select(static sample => sample.ReservationElapsedMilliseconds)),
                PreReservation = Summary(measured.Select(static sample => sample.ReservationPreCallMilliseconds)),
                Writers = Writers.ToDictionary(
                    static writer => writer.Kind,
                    writer => Summary(measured.SelectMany(static sample => sample.Writers)
                        .Where(sample => sample.Kind == writer.Kind).Select(static sample => sample.ElapsedMilliseconds))),
                BarrierWriters = MinMedianMax(barrier.Writers.Select(static sample => sample.ElapsedMilliseconds)),
                ReservationThroughputPerSecond = scale.Measured / (measured.Sum(static sample => sample.ReservationElapsedMilliseconds) / 1000d)
            },
            Protocol = new
            {
                Writers,
                ExplicitWriterTransactions = normal.SelectMany(static sample => sample.Writers).AppendRange(barrier.Writers).Count(),
                LogicalPayloadBytesHashedSteady = checked((long)(scale.Warmups + scale.Measured) * SourcesPerReservation * PayloadLength),
                LogicalPayloadBytesHashedBarrier = (long)SourcesPerReservation * PayloadLength,
                LogicalSidecarBytesReadSteady = LogicalSidecarBytes(seed, normal),
                LogicalSidecarBytesReadBarrier = LogicalSidecarBytes(seed, [barrier]),
                LogicalPayloadFileReadOperations = (long)(normal.Count + 1) * SourcesPerReservation,
                LogicalSidecarFileReadOperations = (long)(normal.Count + 1) * SourcesPerReservation,
                QueryPlans = plans
            },
            Resources = resources,
            Backlog = new { backlog.ActiveCount, backlog.HeldSourceBytes, backlog.OldestCreatedUtc, backlog.PressureLevel },
            Sqlite = new
            {
                BeforeMeasured = sizesBeforeMeasured,
                AfterMeasured = sizesAfterMeasured,
                AfterBarrierAndCheckpoint = DatabaseSizes(DatabasePath(fixture.Root)),
                Checkpoint = checkpoint
            },
            Topology = new
            {
                Kind = "native in-process Microsoft.Data.Sqlite over a fixture-local WAL database",
                FixtureFileSystemFormat = new DriveInfo(Path.GetPathRoot(fixture.Root)!).DriveFormat,
                PhysicalMedia = "unknown/unattributable"
            },
            Correctness = new
            {
                seed.RawRows,
                seed.PhysicalRows,
                seed.MetadataOnlyRows,
                seed.PayloadBytes,
                seed.PayloadBytesHashed,
                seed.ManifestSetSha256,
                seed.DurableStateSha256,
                ExactConvergence = true,
                CorruptionReason = "source-evidence-invalid"
            },
            Privacy = "Before publication, each serialized byte sequence is scanned for repository/fixture/temp roots, broad Unix/Windows absolute paths, connection-string and common secret/token forms, every deterministic GUID in D/N form, and payload prefix/middle/suffix in Base64 and hex. Hash-only identity leakage and unknown credential naming remain limitations.",
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        Directory.CreateDirectory(staging!);
        try
        {
            var evidencePath = Path.Combine(staging!, "transient-candidate-reservation-evidence.json");
            await WritePrivateJsonAsync(evidencePath, evidence, root, fixture.Root, seed.DeterministicIds, payload).ConfigureAwait(false);
            var manifest = new
            {
                Schema = "hvo-issue-247-trial-manifest-v1",
                Phase = phase,
                Trial = trial,
                Source = sourceIdentity,
                Files = new[] { Describe(evidencePath) }
            };
            await WritePrivateJsonAsync(
                Path.Combine(staging!, "manifest.json"), manifest, root, fixture.Root, seed.DeterministicIds, payload).ConfigureAwait(false);
            Directory.Move(staging!, output!);
            try
            {
                await TryAggregateAsync(
                    root, sourceDirectory!, phase, sourceIdentity, productionRevision!, changedPaths,
                    inputHashes, workloadDefinitionSha256, workload.Sha256, environment.EnvironmentFingerprintSha256,
                    seed.DeterministicIds, payload).ConfigureAwait(false);
            }
            catch
            {
                if (trial == 5 && Directory.Exists(output!)) Directory.Delete(output!, recursive: true);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<SeedEvidence> SeedAsync(
        string root,
        CameraModuleConfig configuration,
        byte[] payload,
        string payloadSha,
        Scale scale)
    {
        var started = Stopwatch.GetTimestamp();
        var emptySha = Convert.ToHexString(SHA256.HashData([]));
        var ids = new List<Guid>(scale.RawRows * 3 + (scale.Warmups + scale.Measured + 8) * 2);
        var sources = new List<TransientSourceEvidenceReferenceV1>(scale.PhysicalRows);
        var manifestIdentities = new List<string>(scale.PhysicalRows);
        var sidecarLengths = new List<long>(scale.PhysicalRows);
        Directory.CreateDirectory(Path.Combine(root, "frames"));
        using var connection = await OpenAsync(root, CancellationToken.None).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
        using var assignment = connection.CreateCommand();
        assignment.Transaction = transaction;
        assignment.CommandText = "INSERT INTO raw_capture_assignments(capture_id, raw_artifact_id, agent_id, capture_sequence) VALUES ($capture, $artifact, 'agent', $sequence);";
        var assignmentCapture = assignment.Parameters.Add("$capture", SqliteType.Text);
        var assignmentArtifact = assignment.Parameters.Add("$artifact", SqliteType.Text);
        var assignmentSequence = assignment.Parameters.Add("$sequence", SqliteType.Integer);
        using var raw = connection.CreateCommand();
        raw.Transaction = transaction;
        raw.CommandText = """
            INSERT INTO raw_captures(
                capture_id, raw_artifact_id, agent_id, capture_sequence, descriptor_sha256,
                manifest_sha256, payload_sha256, payload_length, payload_relative_path,
                sidecar_relative_path, manifest_json, exposure_started_unix_ms,
                durable_ingress_unix_ms, committed_unix_ms, state, retention_hold, evidence_origin, failure_reason)
            VALUES ($capture, $artifact, 'agent', $sequence, $descriptor, $manifest, $payload,
                $length, $payload_path, $sidecar_path, $manifest_json, $time, $time, $time,
                $state, 0, 'DeveloperFixture', $failure);
            """;
        var p = raw.Parameters;
        var rawCapture = p.Add("$capture", SqliteType.Text);
        var rawArtifact = p.Add("$artifact", SqliteType.Text);
        var rawSequence = p.Add("$sequence", SqliteType.Integer);
        var rawDescriptor = p.Add("$descriptor", SqliteType.Text);
        var rawManifest = p.Add("$manifest", SqliteType.Text);
        var rawPayload = p.Add("$payload", SqliteType.Text);
        var rawLength = p.Add("$length", SqliteType.Integer);
        var rawPayloadPath = p.Add("$payload_path", SqliteType.Text);
        var rawSidecarPath = p.Add("$sidecar_path", SqliteType.Text);
        var rawManifestJson = p.Add("$manifest_json", SqliteType.Blob);
        var rawTime = p.Add("$time", SqliteType.Integer);
        var rawState = p.Add("$state", SqliteType.Text);
        var rawFailure = p.Add("$failure", SqliteType.Text);
        for (var index = 0; index < scale.RawRows; index++)
        {
            var sequence = index + 1;
            var captureId = DeterministicGuid("capture", index);
            var artifactId = DeterministicGuid("artifact", index);
            ids.Add(captureId);
            ids.Add(artifactId);
            var physical = index < scale.PhysicalRows;
            var timestamp = Epoch.AddMilliseconds(index);
            var storedChecksum = physical ? payloadSha : emptySha;
            var storedLength = physical ? payload.LongLength : 0;
            var submission = Submission(timestamp, payload);
            var descriptor = RawCaptureDescriptorFactory.Create(
                configuration, submission, new RawCaptureIdentity("agent", sequence, captureId, artifactId),
                payloadSha, timestamp.AddSeconds(21));
            var payloadPath = physical ? $"frames/w2-{index:D3}.bin" : $"metadata/row-{index:D5}.bin";
            var sidecarPath = physical ? $"frames/w2-{index:D3}.json" : $"metadata/row-{index:D5}.json";
            var manifest = new ArtifactManifestV2(
                ArtifactManifestV2.CurrentSchemaVersion, descriptor, payloadPath, null);
            var manifestJson = CaptureContractJson.Serialize(manifest);
            var manifestSha = CaptureContractJson.ComputeManifestSha256(manifestJson);
            if (physical)
            {
                await File.WriteAllBytesAsync(Path.Combine(root, payloadPath), payload).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.Combine(root, sidecarPath), manifestJson).ConfigureAwait(false);
                var evidenceId = DeterministicGuid("evidence", index);
                ids.Add(evidenceId);
                sources.Add(new TransientSourceEvidenceReferenceV1(
                    TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
                    evidenceId,
                    new TransientWholeArtifactLocatorV1(
                        TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                        TransientSourceLocatorKind.WholeArtifact,
                        new TransientArtifactReferenceV1(
                            artifactId, FrameArtifactRole.Raw, descriptor.Artifact.Variant,
                            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                            payloadSha)),
                    descriptor.Timing.ExposureStartedUtc,
                    descriptor.Timing.ExposureEndedUtc,
                    TransientTimingQuality.Reported,
                    new TransientTimingProvenanceV1("camera", "v1")));
                manifestIdentities.Add(HashStrings([index.ToString(CultureInfo.InvariantCulture), manifestSha, Convert.ToHexString(SHA256.HashData(manifestJson))]));
                sidecarLengths.Add(manifestJson.LongLength);
            }
            assignmentCapture.Value = captureId.ToString("N");
            assignmentArtifact.Value = artifactId.ToString("N");
            assignmentSequence.Value = sequence;
            Assert.AreEqual(1, await assignment.ExecuteNonQueryAsync().ConfigureAwait(false));
            rawCapture.Value = captureId.ToString("N");
            rawArtifact.Value = artifactId.ToString("N");
            rawSequence.Value = sequence;
            rawDescriptor.Value = CaptureContractJson.ComputeDescriptorSha256(descriptor);
            rawManifest.Value = manifestSha;
            rawPayload.Value = storedChecksum;
            rawLength.Value = storedLength;
            rawPayloadPath.Value = payloadPath;
            rawSidecarPath.Value = sidecarPath;
            rawManifestJson.Value = manifestJson;
            rawTime.Value = timestamp.ToUnixTimeMilliseconds();
            rawState.Value = physical ? "committed" : "missing_evidence";
            rawFailure.Value = physical ? DBNull.Value : "metadata-only-history";
            Assert.AreEqual(1, await raw.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        await transaction.CommitAsync().ConfigureAwait(false);
        await ValidateRawSeedAsync(root, scale).ConfigureAwait(false);
        await SeedWriterStateAsync(root, scale.PhysicalRows + 1).ConfigureAwait(false);
        var physicalFiles = Directory.EnumerateFiles(Path.Combine(root, "frames"), "*.bin").Order().ToArray();
        var sidecarFiles = Directory.EnumerateFiles(Path.Combine(root, "frames"), "*.json").Order().ToArray();
        Assert.AreEqual(scale.PhysicalRows, physicalFiles.Length);
        Assert.AreEqual(scale.PhysicalRows, sidecarFiles.Length);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "metadata")),
            "Metadata-only rows have nominal paths and must not create physical payloads or sidecars.");
        long hashed = 0;
        foreach (var file in physicalFiles)
        {
            using var stream = File.OpenRead(file);
            Assert.AreEqual(payloadSha, Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false)));
            hashed += stream.Length;
        }
        var stateSha = await DurableStateShaAsync(root).ConfigureAwait(false);
        for (var ordinal = 0; ordinal <= scale.Warmups + scale.Measured; ordinal++)
        {
            ids.Add(DeterministicGuid("candidate", ordinal));
            ids.Add(DeterministicGuid("event", ordinal));
        }
        for (var ordinal = 0; ordinal < 6; ordinal++)
        {
            ids.Add(DeterministicGuid("corrupt-candidate", ordinal));
            ids.Add(DeterministicGuid("corrupt-event", ordinal));
        }
        return new SeedEvidence(
            sources, sidecarLengths, ids, scale.RawRows, scale.PhysicalRows, scale.RawRows - scale.PhysicalRows,
            physicalFiles.Sum(static path => new FileInfo(path).Length), hashed,
            HashStrings(manifestIdentities), stateSha, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static async Task SeedWriterStateAsync(string root, int metadataSequence)
    {
        using var connection = await OpenAsync(root, CancellationToken.None).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO capture_lane_work(
                work_id, raw_capture_row_id, lane_name, agent_id, capture_sequence, required, ordered,
                state, attempt_count, available_unix_ms, lease_token, lease_owner, lease_expires_unix_ms,
                created_unix_ms, updated_unix_ms)
            SELECT 1, raw_capture_row_id, 'standard', agent_id, capture_sequence, 1, 1,
                'leased', 1, $now, 'issue-247-token', 'issue-247-owner', $expires, $now, $now
            FROM raw_captures WHERE capture_sequence = $sequence;
            INSERT INTO capture_schedule_revisions(
                revision_id, revision_number, profile_json, profile_sha256, schedule_sha256,
                source, actor, reason, created_unix_ms)
            VALUES ('issue-247-profile', 1, $json, $sha, $sha, 'local', 'issue-247', NULL, $now);
            INSERT INTO capture_schedule_state(
                state_key, active_revision_id, pending_revision_id, version, updated_unix_ms)
            VALUES (1, 'issue-247-profile', NULL, 0, $now);
            """;
        command.Parameters.Add("$now", SqliteType.Integer).Value = Epoch.ToUnixTimeMilliseconds();
        command.Parameters.Add("$expires", SqliteType.Integer).Value = Epoch.AddDays(1).ToUnixTimeMilliseconds();
        command.Parameters.Add("$sequence", SqliteType.Integer).Value = metadataSequence;
        command.Parameters.Add("$json", SqliteType.Blob).Value = "{}"u8.ToArray();
        command.Parameters.Add("$sha", SqliteType.Text).Value = new string('A', 64);
        Assert.AreEqual(3, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    private static async Task ValidateRawSeedAsync(string root, Scale scale)
    {
        var database = DatabasePath(root);
        Assert.AreEqual(scale.RawRows, await ScalarAsync(database, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
        Assert.AreEqual(scale.PhysicalRows, await ScalarAsync(database, $"SELECT COUNT(*) FROM raw_captures WHERE capture_sequence <= {scale.PhysicalRows} AND state = 'committed' AND payload_length = {PayloadLength} AND failure_reason IS NULL;").ConfigureAwait(false));
        Assert.AreEqual(scale.RawRows - scale.PhysicalRows, await ScalarAsync(database, $"SELECT COUNT(*) FROM raw_captures WHERE capture_sequence > {scale.PhysicalRows} AND state = 'missing_evidence' AND payload_length = 0 AND failure_reason = 'metadata-only-history';").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(database, "SELECT COUNT(*) FROM raw_captures WHERE state = 'missing_evidence' AND (payload_length != 0 OR failure_reason IS NULL OR length(failure_reason) > 512);").ConfigureAwait(false));
        Assert.AreEqual(scale.RawRows - scale.PhysicalRows, await ScalarAsync(
            database,
            "SELECT COUNT(*) FROM raw_captures WHERE state = 'missing_evidence' AND payload_sha256 = $empty;",
            ("$empty", Convert.ToHexString(SHA256.HashData([])))).ConfigureAwait(false));
        using var connection = await OpenAsync(root, CancellationToken.None).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT manifest_json, manifest_sha256, descriptor_sha256 FROM raw_captures ORDER BY capture_sequence;";
        var count = 0;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var bytes = (byte[])reader[0];
            var parsed = CaptureContractJson.ParseManifest(bytes);
            Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
            Assert.IsNotNull(parsed.Document?.Manifest);
            Assert.AreEqual(reader.GetString(1), CaptureContractJson.ComputeManifestSha256(bytes));
            Assert.AreEqual(reader.GetString(2), CaptureContractJson.ComputeDescriptorSha256(parsed.Document.Manifest.Descriptor));
            count++;
        }
        Assert.AreEqual(scale.RawRows, count);
    }

    private static TransientSourceEvidenceReferenceV1[] SelectSources(
        IReadOnlyList<TransientSourceEvidenceReferenceV1> sources,
        int operation)
    {
        var start = operation * SourcesPerReservation % sources.Count;
        return Enumerable.Range(0, SourcesPerReservation)
            .Select(index => sources[(start + index) % sources.Count]).ToArray();
    }

    private static long LogicalSidecarBytes(SeedEvidence seed, IEnumerable<OperationSample> operations)
        => operations.Sum(operation => Enumerable.Range(0, SourcesPerReservation)
            .Sum(index => seed.SidecarLengths[(operation.Ordinal * SourcesPerReservation + index) % seed.SidecarLengths.Count]));

    private static async Task<OperationSample> RunNormalAsync(
        SqliteTransientCandidateJournalTests.Fixture fixture,
        IReadOnlyList<TransientSourceEvidenceReferenceV1> sources,
        int ordinal,
        string kind,
        Scale scale)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var operationStart = Stopwatch.GetTimestamp();
        var writers = StartWriters(fixture.Root, operationStart, ordinal, scale.WriterCadenceMilliseconds, null, cancellation.Token);
        var reservation = Reservation(ordinal, sources);
        var preCall = Stopwatch.GetElapsedTime(operationStart).TotalMilliseconds;
        var reserveStart = Stopwatch.GetTimestamp();
        var result = await fixture.Journal.ReserveAsync(reservation, cancellation.Token).ConfigureAwait(false);
        var reserveElapsed = Stopwatch.GetElapsedTime(reserveStart).TotalMilliseconds;
        var writerResults = await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        AssertReservation(result, reservation);
        AssertWriters(writerResults);
        return new OperationSample(ordinal, kind, preCall, reserveElapsed, writerResults);
    }

    private static async Task<OperationSample> RunBarrierAsync(
        SqliteTransientCandidateJournalTests.Fixture fixture,
        IReadOnlyList<TransientSourceEvidenceReferenceV1> sources,
        int ordinal,
        Scale scale,
        string phase)
    {
        var sidecar = Path.ChangeExtension(fixture.ResolvePayload(sources[^1]), ".json");
        var original = await File.ReadAllBytesAsync(sidecar).ConfigureAwait(false);
        Task<TransientCandidateReservationResult>? reservationTask = null;
        Task? fifoTask = null;
        Task<WriterSample>[] writerTasks = [];
        Task? drainTask = null;
        var connected = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var operationStart = Stopwatch.GetTimestamp();
        long releaseTimestamp = 0;
        try
        {
            File.Delete(sidecar);
            await CreateFifoAsync(sidecar).ConfigureAwait(false);
            fifoTask = Task.Run(async () =>
            {
                using var stream = new FileStream(
                    sidecar, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 64 * 1024,
                    FileOptions.Asynchronous);
                connected.TrySetResult(Stopwatch.GetTimestamp());
                await release.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
                await stream.WriteAsync(original, cancellation.Token).ConfigureAwait(false);
            }, cancellation.Token);
            var reservation = Reservation(ordinal, sources);
            var reserveStart = Stopwatch.GetTimestamp();
            reservationTask = fixture.Journal.ReserveAsync(reservation, cancellation.Token).AsTask();
            var barrierEntered = await connected.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            writerTasks = StartWriters(
                fixture.Root, barrierEntered, ordinal, scale.WriterCadenceMilliseconds,
                () => Volatile.Read(ref releaseTimestamp), cancellation.Token);
            await Task.Delay(scale.Barrier, cancellation.Token).ConfigureAwait(false);
            if (phase == "baseline")
            {
                Assert.IsTrue(writerTasks.All(static task => !task.IsCompleted),
                    "Baseline writers must remain blocked before FIFO release.");
            }
            else if (phase == "after")
            {
                Assert.IsTrue(writerTasks.All(static task => task.IsCompletedSuccessfully),
                    "After writers must complete before FIFO release.");
            }
            releaseTimestamp = Stopwatch.GetTimestamp();
            release.TrySetResult();
            var result = await reservationTask.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            var reservationElapsed = Stopwatch.GetElapsedTime(reserveStart).TotalMilliseconds;
            var writerResults = await Task.WhenAll(writerTasks).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            var releaseOffset = Stopwatch.GetElapsedTime(barrierEntered, releaseTimestamp).TotalMilliseconds;
            writerResults = writerResults.Select(sample => sample with
            {
                CompletionRelativeToBarrierReleaseMilliseconds =
                    sample.ActualOfferOffsetMilliseconds + sample.ElapsedMilliseconds - releaseOffset
            }).ToArray();
            AssertReservation(result, reservation);
            AssertWriters(writerResults);
            if (phase == "baseline")
            {
                Assert.IsTrue(writerResults.All(static sample => sample.CompletionRelativeToBarrierReleaseMilliseconds >= 0));
            }
            else if (phase == "after")
            {
                Assert.IsTrue(writerResults.All(static sample => sample.ElapsedMilliseconds <= 250));
            }
            return new OperationSample(
                ordinal, "barrier", Stopwatch.GetElapsedTime(operationStart, reserveStart).TotalMilliseconds,
                reservationElapsed, writerResults);
        }
        finally
        {
            if (!connected.Task.IsCompleted)
            {
                drainTask = DrainFifoAsync(sidecar);
            }
            release.TrySetResult();
            var tasks = new List<Task>();
            if (fifoTask is not null) tasks.Add(fifoTask);
            if (reservationTask is not null) tasks.Add(reservationTask);
            tasks.AddRange(writerTasks);
            if (drainTask is not null) tasks.Add(drainTask);
            try
            {
                await EvidenceTaskCleanup.DrainAsync(tasks, cancellation, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(sidecar)) File.Delete(sidecar);
                await File.WriteAllBytesAsync(sidecar, original).ConfigureAwait(false);
            }
        }
    }

    private static Task<WriterSample>[] StartWriters(
        string root,
        long origin,
        int operation,
        int cadence,
        Func<long>? release,
        CancellationToken cancellationToken)
        => Writers.Select((writer, index) => Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(index * cadence), cancellationToken).ConfigureAwait(false);
            return await ExecuteWriterAsync(root, writer, operation, index * cadence, origin, release, cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken)).ToArray();

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL is selected only from fixed byte-for-byte production statements.")]
    private static async Task<WriterSample> ExecuteWriterAsync(
        string root,
        WriterDefinition writer,
        int operation,
        int scheduledOffset,
        long origin,
        Func<long>? release,
        CancellationToken cancellationToken)
    {
        var offered = Stopwatch.GetTimestamp();
        var transactionStarted = false;
        try
        {
            using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
            var acquisitionStart = Stopwatch.GetTimestamp();
#pragma warning disable CA1849 // Required to measure the same immediate SQLite writer acquisition boundary.
            using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
            var acquired = Stopwatch.GetTimestamp();
            transactionStarted = true;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = writer.Statement;
            AddWriterParameters(command, writer.Kind, operation);
            var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var completed = Stopwatch.GetTimestamp();
            var releaseValue = release?.Invoke() ?? 0;
            return new WriterSample(
                writer.Kind, scheduledOffset, Stopwatch.GetElapsedTime(origin, offered).TotalMilliseconds,
                Stopwatch.GetElapsedTime(offered, completed).TotalMilliseconds,
                Stopwatch.GetElapsedTime(acquisitionStart, acquired).TotalMilliseconds,
                Stopwatch.GetElapsedTime(acquired, completed).TotalMilliseconds,
                release is null ? null : releaseValue == 0
                    ? -Stopwatch.GetElapsedTime(completed).TotalMilliseconds
                    : Stopwatch.GetElapsedTime(releaseValue, completed).TotalMilliseconds,
                rows, "committed", false, true, true, false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new WriterSample(
                writer.Kind, scheduledOffset, Stopwatch.GetElapsedTime(origin, offered).TotalMilliseconds,
                Stopwatch.GetElapsedTime(offered).TotalMilliseconds, 0, 0, null, 0,
                "busy-or-locked", true, transactionStarted, false, true);
        }
    }

    private static void AddWriterParameters(SqliteCommand command, string kind, int operation)
    {
        var now = Epoch.AddMilliseconds(operation + 1).ToUnixTimeMilliseconds();
        switch (kind)
        {
            case "raw":
                command.Parameters.Add("$origin", SqliteType.Text).Value = operation % 2 == 0 ? "Unknown" : "DeveloperFixture";
                command.Parameters.Add("$raw", SqliteType.Integer).Value = 1L;
                break;
            case "lane":
                command.Parameters.Add("$expires", SqliteType.Integer).Value = Epoch.AddDays(1).AddMilliseconds(operation + 1).ToUnixTimeMilliseconds();
                command.Parameters.Add("$now", SqliteType.Integer).Value = now;
                command.Parameters.Add("$work", SqliteType.Integer).Value = 1L;
                command.Parameters.Add("$token", SqliteType.Text).Value = "issue-247-token";
                command.Parameters.Add("$owner", SqliteType.Text).Value = "issue-247-owner";
                break;
            case "schedule":
                command.Parameters.Add("$decision", SqliteType.Integer).Value = now;
                command.Parameters.Add("$admitted", SqliteType.Integer).Value = operation % 2;
                command.Parameters.Add("$reason", SqliteType.Text).Value = "OpenInterval";
                command.Parameters.Add("$profile", SqliteType.Text).Value = "issue-247-setpoint";
                command.Parameters.Add("$interval", SqliteType.Text).Value = "issue-247-interval";
                command.Parameters.Add("$next", SqliteType.Integer).Value = now + 1000;
                command.Parameters.Add("$updated", SqliteType.Integer).Value = now;
                command.Parameters.Add("$revision", SqliteType.Text).Value = "issue-247-profile";
                break;
            case "control":
                command.Parameters.Add("$state", SqliteType.Text).Value = operation % 2 == 0 ? "running" : "pause_requested";
                command.Parameters.Add("$version", SqliteType.Integer).Value = operation + 1;
                command.Parameters.Add("$updated", SqliteType.Integer).Value = now;
                break;
            default:
                throw new InvalidOperationException("Unknown writer.");
        }
    }

    private static TransientCandidateReservation Reservation(
        int ordinal,
        IReadOnlyList<TransientSourceEvidenceReferenceV1> sources)
        => new(DeterministicGuid("candidate", ordinal), DeterministicGuid("event", ordinal), "agent", sources);

    private static void AssertReservation(
        TransientCandidateReservationResult result,
        TransientCandidateReservation reservation)
    {
        Assert.AreEqual(TransientCandidateReservationDisposition.Created, result.Disposition);
        Assert.AreEqual(reservation.CandidateId, result.Entry.CandidateId);
        Assert.AreEqual(reservation.EventId, result.Entry.EventId);
        CollectionAssert.AreEqual(reservation.Sources.ToArray(), result.Entry.Sources.ToArray());
    }

    private static void AssertWriters(IEnumerable<WriterSample> writers)
    {
        foreach (var writer in writers)
        {
            Assert.AreEqual("committed", writer.Outcome);
            Assert.AreEqual(1, writer.RowsAffected, writer.Kind);
            Assert.IsFalse(writer.BusyOrLocked);
            Assert.IsTrue(writer.TransactionStarted);
            Assert.IsTrue(writer.TransactionCommitted);
            Assert.IsFalse(writer.TransactionFailed);
        }
    }

    private static async Task AssertCorruptionBarriersAsync(
        SqliteTransientCandidateJournalTests.Fixture fixture,
        TransientSourceEvidenceReferenceV1 source,
        byte[] expectedPayload)
    {
        var database = DatabasePath(fixture.Root);
        var payloadPath = fixture.ResolvePayload(source);
        var sidecarPath = Path.ChangeExtension(payloadPath, ".json");
        var sidecar = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
        var artifact = source.Locator.Artifact.ArtifactId.ToString("N");
        var originalPayload = expectedPayload.ToArray();
        var cases = new (string Name, Func<Task> Corrupt, Func<Task> Restore)[]
        {
            ("path",
                () => ExecuteAsync(database, "UPDATE raw_captures SET payload_relative_path = $value WHERE raw_artifact_id = $artifact;", ("$value", "frames/missing.bin"), ("$artifact", artifact)),
                () => ExecuteAsync(database, "UPDATE raw_captures SET payload_relative_path = $value WHERE raw_artifact_id = $artifact;", ("$value", Path.GetRelativePath(fixture.Root, payloadPath).Replace('\\', '/')), ("$artifact", artifact))),
            ("length",
                () => ExecuteAsync(database, "UPDATE raw_captures SET payload_length = payload_length + 1 WHERE raw_artifact_id = $artifact;", ("$artifact", artifact)),
                () => ExecuteAsync(database, "UPDATE raw_captures SET payload_length = $length WHERE raw_artifact_id = $artifact;", ("$length", PayloadLength), ("$artifact", artifact))),
            ("payload-checksum",
                async () => { var changed = originalPayload.ToArray(); changed[0] ^= 0xFF; await File.WriteAllBytesAsync(payloadPath, changed).ConfigureAwait(false); },
                () => File.WriteAllBytesAsync(payloadPath, originalPayload)),
            ("manifest-checksum",
                () => ExecuteAsync(database, "UPDATE raw_captures SET manifest_sha256 = $sha WHERE raw_artifact_id = $artifact;", ("$sha", new string('0', 64)), ("$artifact", artifact)),
                () => ExecuteAsync(database, "UPDATE raw_captures SET manifest_sha256 = $sha WHERE raw_artifact_id = $artifact;", ("$sha", CaptureContractJson.ComputeManifestSha256(sidecar)), ("$artifact", artifact))),
            ("manifest-version",
                async () =>
                {
                    var node = JsonNode.Parse(sidecar)!.AsObject();
                    node["schemaVersion"] = "hvo-artifact-manifest-v999";
                    var changed = Encoding.UTF8.GetBytes(node.ToJsonString());
                    await File.WriteAllBytesAsync(sidecarPath, changed).ConfigureAwait(false);
                    await ExecuteAsync(database, "UPDATE raw_captures SET manifest_json = $json, manifest_sha256 = $sha WHERE raw_artifact_id = $artifact;", ("$json", changed), ("$sha", CaptureContractJson.ComputeManifestSha256(changed)), ("$artifact", artifact)).ConfigureAwait(false);
                },
                async () =>
                {
                    await File.WriteAllBytesAsync(sidecarPath, sidecar).ConfigureAwait(false);
                    await ExecuteAsync(database, "UPDATE raw_captures SET manifest_json = $json, manifest_sha256 = $sha WHERE raw_artifact_id = $artifact;", ("$json", sidecar), ("$sha", CaptureContractJson.ComputeManifestSha256(sidecar)), ("$artifact", artifact)).ConfigureAwait(false);
                }),
            ("sidecar",
                async () => { var changed = sidecar.ToArray(); changed[^1] ^= 1; await File.WriteAllBytesAsync(sidecarPath, changed).ConfigureAwait(false); },
                () => File.WriteAllBytesAsync(sidecarPath, sidecar))
        };
        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var reservation = new TransientCandidateReservation(
                DeterministicGuid("corrupt-candidate", index), DeterministicGuid("corrupt-event", index), "agent", [source]);
            await item.Corrupt().ConfigureAwait(false);
            try
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false))
                    .ConfigureAwait(false);
                Assert.AreEqual(1L, await ScalarAsync(database, "SELECT COUNT(*) FROM transient_candidate_conflicts WHERE candidate_id = $candidate AND event_id = $event AND reason = 'source-evidence-invalid';", ("$candidate", reservation.CandidateId.ToString("N")), ("$event", reservation.EventId.ToString("N"))).ConfigureAwait(false));
                Assert.AreEqual(1L, await ScalarAsync(database, "SELECT COUNT(*) FROM transient_event_identities WHERE event_id = $event;", ("$event", reservation.EventId.ToString("N"))).ConfigureAwait(false));
                Assert.AreEqual(0L, await ScalarAsync(database, "SELECT COUNT(*) FROM transient_candidates WHERE candidate_id = $candidate;", ("$candidate", reservation.CandidateId.ToString("N"))).ConfigureAwait(false));
                Assert.AreEqual(0L, await ScalarAsync(database, "SELECT COUNT(*) FROM transient_candidate_sources WHERE candidate_id = $candidate;", ("$candidate", reservation.CandidateId.ToString("N"))).ConfigureAwait(false));
                Assert.AreEqual(0L, await ScalarAsync(database, "SELECT COUNT(*) FROM raw_captures WHERE retention_hold != 0;").ConfigureAwait(false));
                Assert.AreEqual(0L, await ScalarAsync(database, "SELECT COUNT(*) FROM transient_capture_work;").ConfigureAwait(false));
                Assert.AreEqual(2, (await fixture.Journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false)).PressureLevel);
            }
            finally
            {
                await item.Restore().ConfigureAwait(false);
                await ExecuteAsync(database, "DELETE FROM transient_candidate_conflicts WHERE candidate_id = $candidate; DELETE FROM transient_event_identities WHERE event_id = $event;", ("$candidate", reservation.CandidateId.ToString("N")), ("$event", reservation.EventId.ToString("N"))).ConfigureAwait(false);
                await ExecuteAsync(database, "UPDATE capture_lane_definitions SET pressure_state = 0 WHERE lane_name = 'transient';").ConfigureAwait(false);
            }
        }
        Assert.AreEqual(0L, await ScalarAsync(database, "SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false));
    }

    private static async Task AssertConvergenceAsync(
        SqliteTransientCandidateJournalTests.Fixture fixture,
        SeedEvidence seed,
        IReadOnlyList<OperationSample> normal,
        OperationSample barrier,
        Scale scale)
    {
        var database = DatabasePath(fixture.Root);
        var operations = normal.Count + 1;
        Assert.AreEqual(operations, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
        Assert.AreEqual(operations, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_event_identities;").ConfigureAwait(false));
        Assert.AreEqual((long)operations * SourcesPerReservation, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidate_sources;").ConfigureAwait(false));
        Assert.AreEqual(scale.PhysicalRows, await fixture.ScalarLongAsync("SELECT COUNT(DISTINCT raw_capture_row_id) FROM transient_candidate_sources;").ConfigureAwait(false));
        Assert.AreEqual(scale.PhysicalRows, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;").ConfigureAwait(false));
        Assert.AreEqual(0L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_capture_work;").ConfigureAwait(false));
        Assert.AreEqual(0L, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(database, "SELECT COUNT(*) FROM capture_lane_work WHERE work_id = 1 AND state = 'leased' AND lease_token = 'issue-247-token' AND lease_owner = 'issue-247-owner';").ConfigureAwait(false));
        Assert.AreEqual(operations, await ScalarAsync(database, "SELECT version FROM capture_control_state WHERE state_key = 1;").ConfigureAwait(false));
        Assert.AreEqual(Epoch.AddMilliseconds(operations).ToUnixTimeMilliseconds(), await ScalarAsync(database, "SELECT last_decision_unix_ms FROM capture_schedule_state WHERE state_key = 1;").ConfigureAwait(false));
        Assert.AreEqual(operations % 2 == 0 ? "pause_requested" : "running", await ScalarStringAsync(database, "SELECT state FROM capture_control_state WHERE state_key = 1;").ConfigureAwait(false));
        Assert.AreEqual(Epoch.AddDays(1).AddMilliseconds(operations).ToUnixTimeMilliseconds(), await ScalarAsync(database, "SELECT lease_expires_unix_ms FROM capture_lane_work WHERE work_id = 1;").ConfigureAwait(false));
        Assert.AreEqual((operations - 1) % 2 == 0 ? "Unknown" : "DeveloperFixture", await ScalarStringAsync(database, "SELECT evidence_origin FROM raw_captures WHERE raw_capture_row_id = 1;").ConfigureAwait(false));
        Assert.AreEqual((operations - 1) % 2, await ScalarAsync(database, "SELECT last_decision_admitted FROM capture_schedule_state WHERE state_key = 1;").ConfigureAwait(false));
        Assert.AreEqual("OpenInterval", await ScalarStringAsync(database, "SELECT last_decision_reason FROM capture_schedule_state WHERE state_key = 1;").ConfigureAwait(false));
        var backlog = await fixture.Journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(operations, backlog.ActiveCount);
        Assert.AreEqual(checked((long)scale.PhysicalRows * PayloadLength), backlog.HeldSourceBytes);
        Assert.AreEqual(0, backlog.PressureLevel);
        var expected = normal.Append(barrier).Select(sample => Reservation(sample.Ordinal, SelectSources(seed.Sources, sample.Ordinal))).ToArray();
        foreach (var reservation in expected)
        {
            Assert.AreEqual(1L, await ScalarAsync(database, "SELECT COUNT(*) FROM transient_candidates WHERE candidate_id = $candidate AND event_id = $event;", ("$candidate", reservation.CandidateId.ToString("N")), ("$event", reservation.EventId.ToString("N"))).ConfigureAwait(false));
            for (var ordinal = 0; ordinal < SourcesPerReservation; ordinal++)
            {
                var source = reservation.Sources[ordinal];
                Assert.AreEqual(1L, await ScalarAsync(database, "SELECT COUNT(*) FROM transient_candidate_sources WHERE candidate_id = $candidate AND source_ordinal = $ordinal AND evidence_id = $evidence AND source_schema = $source_schema AND locator_schema = $locator_schema AND locator_kind = $locator_kind AND artifact_id = $artifact AND artifact_role = $role AND artifact_variant = $variant AND recipe_identity_sha256 = $recipe AND checksum_sha256 = $checksum AND observation_started_utc_ticks = $started AND observation_ended_utc_ticks = $ended AND timing_quality = $quality AND timing_source = $timing_source AND timing_version = $timing_version;", ("$candidate", reservation.CandidateId.ToString("N")), ("$ordinal", ordinal), ("$evidence", source.EvidenceId.ToString("N")), ("$source_schema", source.SchemaVersion), ("$locator_schema", source.Locator.SchemaVersion), ("$locator_kind", (int)source.Locator.Kind), ("$artifact", source.Locator.Artifact.ArtifactId.ToString("N")), ("$role", (int)source.Locator.Artifact.Role), ("$variant", source.Locator.Artifact.Variant), ("$recipe", source.Locator.Artifact.RecipeIdentitySha256), ("$checksum", source.Locator.Artifact.ChecksumSha256), ("$started", source.ObservationStartedUtc.UtcTicks), ("$ended", source.ObservationEndedUtc.UtcTicks), ("$quality", (int)source.TimingQuality), ("$timing_source", source.TimingProvenance.Source), ("$timing_version", source.TimingProvenance.Version)).ConfigureAwait(false));
            }
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Plans use fixed exact production statements and typed dummy parameters.")]
    private static async Task<IReadOnlyList<PlanEvidence>> CapturePlansAsync(string root)
    {
        var plans = new List<(string Name, string Owner, string Sql, IReadOnlyList<WriterParameter> Parameters)>
        {
            ("reservation-source", "SqliteTransientCandidateJournal.cs:1097-1101", "SELECT raw_capture_row_id, payload_length, agent_id, payload_sha256, manifest_sha256, payload_relative_path, sidecar_relative_path, manifest_json, state FROM raw_captures WHERE raw_artifact_id = $artifact;", [new("$artifact", "Text")]),
            ("reservation-event", "SqliteTransientCandidateJournal.cs:1005", "SELECT agent_id FROM transient_event_identities WHERE event_id = $event;", [new("$event", "Text")]),
            ("reservation-candidate", "SqliteTransientCandidateJournal.cs:1658-1666", "SELECT event_id, agent_id, mode, required, state, phase, reservation_identity_sha256, candidate_payload_sha256, candidate_payload, finalization_receipt_identity_sha256, finalization_payload, submission_identity_sha256, submission_payload, acknowledgement_payload_sha256, acknowledgement_payload, source_hold_released, quarantine_reason, timeout_unix_ms, created_unix_ms, updated_unix_ms FROM transient_candidates WHERE candidate_id = $candidate;", [new("$candidate", "Text")]),
            ("capacity-active-totals", "SqliteTransientCandidateJournal.cs:1819-1836", "SELECT (SELECT COUNT(*) FROM transient_capture_work WHERE state IN ('pending', 'quarantined')) + (SELECT COUNT(*) FROM transient_candidates WHERE source_hold_released = 0), (SELECT COALESCE(SUM(payload_length), 0) FROM raw_captures WHERE raw_capture_row_id IN (SELECT raw_capture_row_id FROM transient_capture_work WHERE state IN ('pending', 'quarantined') UNION SELECT s.raw_capture_row_id FROM transient_candidate_sources s JOIN transient_candidates c ON c.candidate_id = s.candidate_id WHERE c.source_hold_released = 0)), (SELECT MIN(created_unix_ms) FROM (SELECT created_unix_ms FROM transient_capture_work WHERE state IN ('pending', 'quarantined') UNION ALL SELECT created_unix_ms FROM transient_candidates WHERE source_hold_released = 0)), (SELECT COUNT(*) FROM transient_capture_work WHERE state = 'quarantined') + (SELECT COUNT(*) FROM transient_candidates WHERE phase = 'quarantined') + (SELECT COUNT(*) FROM transient_candidate_conflicts);", []),
            ("capacity-source-held", "SqliteTransientCandidateJournal.cs:1187-1195", "SELECT EXISTS(SELECT 1 FROM transient_candidate_sources s JOIN transient_candidates c ON c.candidate_id = s.candidate_id WHERE s.raw_capture_row_id = $raw AND c.source_hold_released = 0 UNION ALL SELECT 1 FROM transient_capture_work WHERE raw_capture_row_id = $raw AND state = 'pending');", [new("$raw", "Integer")]),
            ("pressure", "SqliteTransientCandidateJournal.cs:1225", "SELECT pressure_state FROM capture_lane_definitions WHERE lane_name = 'transient';", [])
        };
        plans.AddRange(Writers.Select(writer => (writer.Kind, writer.Owner, writer.Statement, writer.Parameters)));
        var output = new List<PlanEvidence>();
        using var connection = await OpenAsync(root, CancellationToken.None).ConfigureAwait(false);
        foreach (var plan in plans)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + plan.Sql;
            foreach (var parameter in plan.Parameters)
            {
                command.Parameters.Add(parameter.Name, ParseSqliteType(parameter.SqliteType)).Value = parameter.SqliteType == "Integer" ? 1 : "00000000000000000000000000000000";
            }
            var details = new List<string>();
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) details.Add(reader.GetString(3));
            output.Add(new PlanEvidence(plan.Name, plan.Owner, HashText(plan.Sql), plan.Parameters, details));
        }
        return output;
    }

    private static WorkloadIdentity CreateWorkloadIdentity(
        SeedEvidence seed,
        IReadOnlyList<OperationSample> normal,
        OperationSample barrier,
        Scale scale,
        string payloadSha,
        string profileSha)
    {
        var identitySet = seed.DeterministicIds.Select(static id => id.ToString("N")).Order().ToArray();
        var operationOrder = normal.Append(barrier).Select(sample => new
        {
            sample.Ordinal,
            sample.Kind,
            Candidate = Reservation(sample.Ordinal, SelectSources(seed.Sources, sample.Ordinal)).CandidateId.ToString("N"),
            Event = Reservation(sample.Ordinal, SelectSources(seed.Sources, sample.Ordinal)).EventId.ToString("N"),
            Sources = SelectSources(seed.Sources, sample.Ordinal).Select(static source => source.EvidenceId.ToString("N")).ToArray()
        }).ToArray();
        var canonical = new
        {
            Seed,
            ProfileName,
            profileSha,
            Width,
            Height,
            Stride,
            PayloadLength,
            payloadSha,
            scale.RawRows,
            scale.PhysicalRows,
            scale.Warmups,
            scale.Measured,
            SourcesPerReservation,
            scale.WriterCadenceMilliseconds,
            BarrierMilliseconds = scale.Barrier.TotalMilliseconds,
            seed.ManifestSetSha256,
            IdentitySet = identitySet,
            Operations = operationOrder,
            Writers,
            CorruptionOrder = new[] { "path", "length", "payload-checksum", "manifest-checksum", "manifest-version", "sidecar" }
        };
        var sha = HashText(JsonSerializer.Serialize(canonical));
        return new WorkloadIdentity(sha, new
        {
            Seed,
            Profile = ProfileName,
            ProfileSha256 = profileSha,
            Dimensions = "3096x2080",
            PixelFormat = "RGGB16",
            PayloadBytes = PayloadLength,
            PayloadSha256 = payloadSha,
            RawRows = scale.RawRows,
            PhysicalPayloadRows = scale.PhysicalRows,
            MetadataOnlyRows = scale.RawRows - scale.PhysicalRows,
            PhysicalPayloadBytes = checked((long)scale.PhysicalRows * PayloadLength),
            scale.Warmups,
            scale.Measured,
            NormalArtificialDelayMilliseconds = 0,
            BarrierMilliseconds = scale.Barrier.TotalMilliseconds,
            WriterOfferOffsetsMilliseconds = Enumerable.Range(0, Writers.Length).Select(index => index * scale.WriterCadenceMilliseconds).ToArray(),
            IdentitySetSha256 = HashStrings(identitySet),
            seed.ManifestSetSha256,
            Claim = "W3M is exactly 10,000 durable raw rows: 100 committed physical W2 captures and 9,900 missing_evidence history rows with payload_length=0, empty stored-payload hash, bounded failure reason, nominal paths, and no physical payload/sidecar. Only the 100 committed rows are reservation sources. No renderer or physical-sensitivity claim."
        });
    }

    private static InputHashes HashInputs(string root)
    {
        var files = ImmutableHarnessFiles.Concat(MutableProductionFiles).Select(path =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, path));
            return new FileHash(path, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
        }).ToArray();
        return new InputHashes(
            HashStrings(files.Where(file => ImmutableHarnessFiles.Contains(file.Name, StringComparer.Ordinal)).Select(static file => $"{file.Name}|{file.ByteLength}|{file.Sha256}")),
            HashStrings(files.Select(static file => $"{file.Name}|{file.ByteLength}|{file.Sha256}")),
            files.Single(static file => file.Name.EndsWith(ProfileName, StringComparison.Ordinal)).Sha256,
            files);
    }

    private static void AssertWriterSources(string root)
    {
        foreach (var writer in Writers)
        {
            var ownerPath = writer.Owner.Split(':', 2)[0];
            var source = File.ReadAllText(Path.Combine(root, ownerPath));
            Assert.IsTrue(
                NormalizeSql(source).Contains(NormalizeSql(writer.Statement), StringComparison.Ordinal),
                $"Writer '{writer.Kind}' no longer matches its owner source.");
        }
    }

    private static string NormalizeSql(string value)
        => string.Concat(value.Where(static character => !char.IsWhiteSpace(character)));

    private static string WorkloadDefinitionSha256(string profileSha256)
        => HashText(JsonSerializer.Serialize(new
        {
            Seed,
            ProfileName,
            profileSha256,
            Width,
            Height,
            Stride,
            PayloadLength,
            RawRows,
            PhysicalRows,
            Warmups,
            Measured,
            SourcesPerReservation,
            WriterCadenceMilliseconds,
            BarrierMilliseconds = BarrierDuration.TotalMilliseconds,
            Writers,
            IdentitySet = Enumerable.Range(0, RawRows).SelectMany(index => new[]
            {
                DeterministicGuid("capture", index).ToString("N"),
                DeterministicGuid("artifact", index).ToString("N")
            }).Concat(Enumerable.Range(0, PhysicalRows).Select(index => DeterministicGuid("evidence", index).ToString("N")))
                .Concat(Enumerable.Range(0, Warmups + Measured + 1).SelectMany(index => new[]
                {
                    DeterministicGuid("candidate", index).ToString("N"),
                    DeterministicGuid("event", index).ToString("N")
                })).Order().ToArray()
        }));

    private static async Task ValidateReviewedBaselineAsync(
        string harnessDefinitionSha256,
        string workloadDefinitionSha256,
        string environmentSha256)
    {
        var summaryPath = Required("HVO_EVIDENCE_BASELINE_SUMMARY");
        var expectedSummarySha = Required("HVO_EVIDENCE_BASELINE_SUMMARY_SHA256").ToUpperInvariant();
        var expectedManifestSha = Required("HVO_EVIDENCE_BASELINE_MANIFEST_SHA256").ToUpperInvariant();
        var summaryBytes = await File.ReadAllBytesAsync(summaryPath).ConfigureAwait(false);
        Assert.AreEqual(expectedSummarySha, Convert.ToHexString(SHA256.HashData(summaryBytes)));
        var manifestPath = Path.Combine(Path.GetDirectoryName(summaryPath)!, "baseline-five-trial-manifest.json");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
        Assert.AreEqual(expectedManifestSha, Convert.ToHexString(SHA256.HashData(manifestBytes)));
        using var summary = JsonDocument.Parse(summaryBytes);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var value = summary.RootElement;
        Assert.AreEqual("hvo-issue-247-five-trial-summary-v1", value.GetProperty("Schema").GetString());
        Assert.AreEqual("baseline", value.GetProperty("Phase").GetString());
        Assert.AreEqual(BaselineRevision, value.GetProperty("ProductionRevision").GetString());
        CollectionAssert.AreEqual(BaselineEnvelope, value.GetProperty("ChangedPathsFromBaseline").EnumerateArray().Select(static item => item.GetString()).ToArray());
        Assert.AreEqual(harnessDefinitionSha256, value.GetProperty("ImmutableSha256").GetString());
        Assert.AreEqual(workloadDefinitionSha256, value.GetProperty("WorkloadDefinitionSha256").GetString());
        Assert.AreEqual(environmentSha256, value.GetProperty("EnvironmentFingerprintSha256").GetString());
        Assert.AreEqual("hvo-issue-247-five-trial-manifest-v1", manifest.RootElement.GetProperty("Schema").GetString());
        Assert.AreEqual("baseline", manifest.RootElement.GetProperty("Phase").GetString());
        var expectedFiles = Enumerable.Range(1, 5)
            .SelectMany(index => new[] { $"../trial-{index}/manifest.json", $"../trial-{index}/transient-candidate-reservation-evidence.json" })
            .Append("baseline-five-trial-summary.json").Order().ToArray();
        var files = manifest.RootElement.GetProperty("Files").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(expectedFiles, files.Select(file => file.GetProperty("Name").GetString()).Order().ToArray());
        var sourceHead = value.GetProperty("Source").GetProperty("Head").GetString();
        var assemblies = value.GetProperty("Source").GetProperty("Assemblies").GetRawText();
        foreach (var file in files)
        {
            var path = Path.Combine(Path.GetDirectoryName(manifestPath)!, file.GetProperty("Name").GetString()!);
            var actual = Describe(path);
            Assert.AreEqual(actual.ByteLength, file.GetProperty("ByteLength").GetInt64());
            Assert.AreEqual(actual.Sha256, file.GetProperty("Sha256").GetString());
        }
        for (var trial = 1; trial <= 5; trial++)
        {
            var path = Path.Combine(
                Path.GetDirectoryName(manifestPath)!,
                "..",
                $"trial-{trial}",
                "transient-candidate-reservation-evidence.json");
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
            var trialValue = document.RootElement;
            Assert.AreEqual(BaselineRevision, trialValue.GetProperty("ProductionRevision").GetString());
            Assert.AreEqual(sourceHead, trialValue.GetProperty("Source").GetProperty("Head").GetString());
            Assert.AreEqual(assemblies, trialValue.GetProperty("Source").GetProperty("Assemblies").GetRawText());
            Assert.AreEqual(trial, trialValue.GetProperty("Source").GetProperty("Trial").GetInt32());
            Assert.AreEqual(harnessDefinitionSha256, trialValue.GetProperty("HarnessDefinitionSha256").GetString());
            Assert.AreEqual(workloadDefinitionSha256, trialValue.GetProperty("WorkloadDefinitionSha256").GetString());
            Assert.AreEqual(environmentSha256, trialValue.GetProperty("Environment").GetProperty("EnvironmentFingerprintSha256").GetString());
        }
    }

    private static async Task<string[]> ValidateSourceAsync(
        string root,
        string phase,
        string productionRevision,
        EvidenceSourceSnapshot source,
        int trial)
    {
        if (source.Dirty || source.RequestedRevision is null || source.Trial != trial)
            throw new InvalidOperationException("Claimable evidence requires clean, requested, trial-bound source identity.");
        var production = (await GitAsync(root, "rev-parse", $"{productionRevision}^{{commit}}").ConfigureAwait(false)).Trim();
        var ancestry = await GitExitAsync(root, "merge-base", "--is-ancestor", BaselineRevision, source.Head).ConfigureAwait(false);
        if (ancestry != 0) throw new InvalidOperationException("Production baseline is not an ancestor of evidence HEAD.");
        if (phase == "baseline" && !string.Equals(production, BaselineRevision, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Baseline production revision is not pinned.");
        if (phase == "after" && !string.Equals(production, source.Head, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("After production revision must resolve exactly to evidence HEAD.");
        var changed = (await GitAsync(root, "diff", "--name-only", $"{BaselineRevision}..{source.Head}", "--").ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order().ToArray();
        if (phase == "baseline" && !changed.SequenceEqual(BaselineEnvelope, StringComparer.Ordinal))
            throw new InvalidOperationException("Baseline source does not match the exact reviewed harness envelope.");
        return changed;
    }

    private static async Task TryAggregateAsync(
        string root,
        string sourceDirectory,
        string phase,
        PrivateSourceSnapshot source,
        string productionRevision,
        string[] changedPaths,
        InputHashes inputs,
        string workloadDefinitionSha256,
        string workloadSha,
        string environmentSha,
        IReadOnlyList<Guid> deterministicIds,
        byte[] payload)
    {
        var evidencePaths = Enumerable.Range(1, 5).Select(i => Path.Combine(sourceDirectory, $"trial-{i}", "transient-candidate-reservation-evidence.json")).ToArray();
        var trialManifestPaths = Enumerable.Range(1, 5).Select(i => Path.Combine(sourceDirectory, $"trial-{i}", "manifest.json")).ToArray();
        if (evidencePaths.Concat(trialManifestPaths).Any(path => !File.Exists(path))) return;
        var trialFiles = evidencePaths.Concat(trialManifestPaths)
            .Select(path => DescribeAs(path, "../" + Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/')))
            .OrderBy(static file => file.Name).ToArray();
        var trialMetrics = new List<AggregateTrial>();
        var measuredSnapshotStages = new List<double>();
        var measuredImmediateStages = new List<double>();
        var barrierSnapshotStages = new List<double>();
        var barrierImmediateStages = new List<double>();
        string? canonicalHead = null;
        string? canonicalAssemblies = null;
        foreach (var (path, index) in evidencePaths.Select((path, index) => (path, index)))
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
            var value = document.RootElement;
            Assert.AreEqual(Schema, value.GetProperty("Schema").GetString());
            Assert.AreEqual(phase, value.GetProperty("Phase").GetString());
            Assert.AreEqual(index + 1, value.GetProperty("Trial").GetInt32());
            Assert.AreEqual(productionRevision, value.GetProperty("ProductionRevision").GetString());
            Assert.AreEqual(inputs.ImmutableSha256, value.GetProperty("HarnessDefinitionSha256").GetString());
            Assert.AreEqual(workloadSha, value.GetProperty("WorkloadSha256").GetString());
            Assert.AreEqual(environmentSha, value.GetProperty("Environment").GetProperty("EnvironmentFingerprintSha256").GetString());
            Assert.AreEqual(HashStrings(changedPaths), value.GetProperty("ChangedPathsSha256").GetString());
            var trialSource = value.GetProperty("Source");
            canonicalHead ??= trialSource.GetProperty("Head").GetString();
            canonicalAssemblies ??= trialSource.GetProperty("Assemblies").GetRawText();
            Assert.AreEqual(canonicalHead, trialSource.GetProperty("Head").GetString());
            Assert.AreEqual(canonicalAssemblies, trialSource.GetProperty("Assemblies").GetRawText());
            Assert.AreEqual(index + 1, trialSource.GetProperty("Trial").GetInt32());
            var summary = value.GetProperty("Summaries");
            var reservation = summary.GetProperty("Reservation");
            var barrierWriters = value.GetProperty("RawSamples").GetProperty("Barrier").GetProperty("Writers")
                .EnumerateArray().Select(sample => sample.GetProperty("ElapsedMilliseconds").GetDouble()).Order().ToArray();
            var normalWriters = value.GetProperty("RawSamples").GetProperty("Normal").EnumerateArray()
                .Where(sample => sample.GetProperty("Kind").GetString() == "measured")
                .SelectMany(sample => sample.GetProperty("Writers").EnumerateArray())
                .Select(sample => sample.GetProperty("ElapsedMilliseconds").GetDouble()).Order().ToArray();
            var resources = value.GetProperty("Resources");
            var sqlite = value.GetProperty("Sqlite").GetProperty("AfterMeasured");
            if (phase == "after")
            {
                var stages = value.GetProperty("StageTelemetry");
                measuredSnapshotStages.AddRange(stages.GetProperty("MeasuredRaw").GetProperty("SnapshotMilliseconds").EnumerateArray().Select(static item => item.GetDouble()));
                measuredImmediateStages.AddRange(stages.GetProperty("MeasuredRaw").GetProperty("ImmediateMilliseconds").EnumerateArray().Select(static item => item.GetDouble()));
                barrierSnapshotStages.Add(stages.GetProperty("BarrierSnapshot").GetDouble());
                barrierImmediateStages.Add(stages.GetProperty("BarrierImmediate").GetDouble());
            }
            trialMetrics.Add(new AggregateTrial(
                reservation.GetProperty("P50Milliseconds").GetDouble(),
                reservation.GetProperty("P95Milliseconds").GetDouble(),
                reservation.GetProperty("MaximumMilliseconds").GetDouble(),
                summary.GetProperty("ReservationThroughputPerSecond").GetDouble(),
                Percentile(normalWriters, .95),
                barrierWriters[^1],
                resources.GetProperty("ProcessCpuMilliseconds").GetDouble(),
                resources.GetProperty("AllocatedBytes").GetDouble(),
                resources.GetProperty("PeakObservedRssBytes").GetDouble(),
                sqlite.GetProperty("DatabaseBytes").GetDouble(),
                sqlite.GetProperty("WalBytes").GetDouble(),
                sqlite.GetProperty("ShmBytes").GetDouble()));
            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(trialManifestPaths[index]).ConfigureAwait(false));
            var described = manifest.RootElement.GetProperty("Files")[0];
            var actual = Describe(path);
            Assert.AreEqual(actual.Sha256, described.GetProperty("Sha256").GetString());
            Assert.AreEqual(actual.ByteLength, described.GetProperty("ByteLength").GetInt64());
        }
        var summaryObject = new
        {
            Schema = "hvo-issue-247-five-trial-summary-v1",
            Issue = 247,
            Phase = phase,
            Source = source,
            ProductionRevision = productionRevision,
            BaselineRevision,
            ChangedPathsFromBaseline = changedPaths,
            ChangedPathsSha256 = HashStrings(changedPaths),
            inputs.ImmutableSha256,
            inputs.AllSha256,
            WorkloadDefinitionSha256 = workloadDefinitionSha256,
            WorkloadSha256 = workloadSha,
            EnvironmentFingerprintSha256 = environmentSha,
            TrialCount = 5,
            TrialFiles = trialFiles,
            Metrics = new
            {
                ReservationP50Milliseconds = SummarizeTrials(trialMetrics.Select(static metric => metric.P50)),
                ReservationP95Milliseconds = SummarizeTrials(trialMetrics.Select(static metric => metric.P95)),
                ReservationMaximumMilliseconds = SummarizeTrials(trialMetrics.Select(static metric => metric.Max)),
                ReservationThroughputPerSecond = SummarizeTrials(trialMetrics.Select(static metric => metric.Throughput)),
                NormalWriterP95Milliseconds = SummarizeTrials(trialMetrics.Select(static metric => metric.NormalWriterP95)),
                BarrierWriterMaximumMilliseconds = SummarizeTrials(trialMetrics.Select(static metric => metric.BarrierWriterMaximum)),
                ProcessCpuMilliseconds = SummarizeTrials(trialMetrics.Select(static metric => metric.Cpu)),
                AllocatedBytes = SummarizeTrials(trialMetrics.Select(static metric => metric.Allocated)),
                PeakObservedRssBytes = SummarizeTrials(trialMetrics.Select(static metric => metric.PeakRss)),
                DatabaseBytes = SummarizeTrials(trialMetrics.Select(static metric => metric.Database)),
                WalBytes = SummarizeTrials(trialMetrics.Select(static metric => metric.Wal)),
                ShmBytes = SummarizeTrials(trialMetrics.Select(static metric => metric.Shm))
            },
            CandidateStages = phase == "after" ? (object)new
            {
                MeasuredSnapshot = Summary(measuredSnapshotStages),
                MeasuredImmediate = Summary(measuredImmediateStages),
                BarrierSnapshot = MinMedianMax(barrierSnapshotStages),
                BarrierImmediate = MinMedianMax(barrierImmediateStages),
                Comparison = "after-only absolute candidate observations; baseline is unavailable and no baseline delta is computed"
            } : null,
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        var aggregateStage = Path.Combine(sourceDirectory, $".staging-aggregate-{phase}");
        var aggregateDirectory = Path.Combine(sourceDirectory, $"aggregate-{phase}");
        if (Directory.Exists(aggregateDirectory))
            throw new IOException("Issue #247 aggregate output already exists; publication is create-new.");
        if (Directory.Exists(aggregateStage)) Directory.Delete(aggregateStage, recursive: true);
        Directory.CreateDirectory(aggregateStage);
        try
        {
            var summaryName = $"{phase}-five-trial-summary.json";
            var summaryPath = Path.Combine(aggregateStage, summaryName);
            await WritePrivateJsonAsync(summaryPath, summaryObject, root, sourceDirectory, deterministicIds, payload).ConfigureAwait(false);
            string? comparisonPath = null;
            if (phase == "after")
            {
                comparisonPath = Path.Combine(aggregateStage, "baseline-after-comparison.json");
                await WriteComparisonAsync(root, summaryPath, comparisonPath, inputs.ImmutableSha256, workloadSha, environmentSha, deterministicIds, payload).ConfigureAwait(false);
            }
            var finalFiles = trialFiles.ToList();
            finalFiles.Add(DescribeAs(summaryPath, summaryName));
            if (comparisonPath is not null) finalFiles.Add(DescribeAs(comparisonPath, "baseline-after-comparison.json"));
            var finalManifest = new
            {
                Schema = "hvo-issue-247-five-trial-manifest-v1",
                Phase = phase,
                Source = source,
                Files = finalFiles.OrderBy(static file => file.Name).ToArray(),
                SelfHash = "N/A: recursive self-hashing is impossible."
            };
            var manifestName = $"{phase}-five-trial-manifest.json";
            await WritePrivateJsonAsync(Path.Combine(aggregateStage, manifestName), finalManifest, root, sourceDirectory, deterministicIds, payload).ConfigureAwait(false);
            Directory.Move(aggregateStage, aggregateDirectory);
        }
        finally
        {
            if (Directory.Exists(aggregateStage)) Directory.Delete(aggregateStage, recursive: true);
        }
    }

    private static async Task WriteComparisonAsync(
        string root,
        string afterSummaryPath,
        string outputPath,
        string harnessSha,
        string workloadSha,
        string environmentSha,
        IReadOnlyList<Guid> deterministicIds,
        byte[] payload)
    {
        var baselinePath = Required("HVO_EVIDENCE_BASELINE_SUMMARY");
        var expectedSha = Required("HVO_EVIDENCE_BASELINE_SUMMARY_SHA256").ToUpperInvariant();
        var expectedManifestSha = Required("HVO_EVIDENCE_BASELINE_MANIFEST_SHA256").ToUpperInvariant();
        var baselineBytes = await File.ReadAllBytesAsync(baselinePath).ConfigureAwait(false);
        Assert.AreEqual(expectedSha, Convert.ToHexString(SHA256.HashData(baselineBytes)));
        var baselineManifestPath = Path.Combine(Path.GetDirectoryName(baselinePath)!, "baseline-five-trial-manifest.json");
        if (!File.Exists(baselineManifestPath)) throw new InvalidDataException("Reviewed baseline aggregate manifest is missing.");
        Assert.AreEqual(expectedManifestSha, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(baselineManifestPath).ConfigureAwait(false))));
        using var baseline = JsonDocument.Parse(baselineBytes);
        using var after = JsonDocument.Parse(await File.ReadAllBytesAsync(afterSummaryPath).ConfigureAwait(false));
        var b = baseline.RootElement;
        var a = after.RootElement;
        Assert.AreEqual("baseline", b.GetProperty("Phase").GetString());
        Assert.AreEqual(BaselineRevision, b.GetProperty("ProductionRevision").GetString());
        CollectionAssert.AreEqual(BaselineEnvelope, b.GetProperty("ChangedPathsFromBaseline").EnumerateArray().Select(static item => item.GetString()).ToArray());
        Assert.AreEqual(harnessSha, b.GetProperty("ImmutableSha256").GetString());
        Assert.AreEqual(workloadSha, b.GetProperty("WorkloadSha256").GetString());
        Assert.AreEqual(environmentSha, b.GetProperty("EnvironmentFingerprintSha256").GetString());
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(baselineManifestPath).ConfigureAwait(false));
        Assert.AreEqual("hvo-issue-247-five-trial-manifest-v1", manifest.RootElement.GetProperty("Schema").GetString());
        Assert.AreEqual("baseline", manifest.RootElement.GetProperty("Phase").GetString());
        var expectedFiles = Enumerable.Range(1, 5).SelectMany(i => new[] { $"../trial-{i}/manifest.json", $"../trial-{i}/transient-candidate-reservation-evidence.json" })
            .Append("baseline-five-trial-summary.json").Order().ToArray();
        var manifestFiles = manifest.RootElement.GetProperty("Files").EnumerateArray().Select(file => file.GetProperty("Name").GetString()).Order().ToArray();
        CollectionAssert.AreEqual(expectedFiles, manifestFiles);
        foreach (var file in manifest.RootElement.GetProperty("Files").EnumerateArray())
        {
            var path = Path.Combine(Path.GetDirectoryName(baselineManifestPath)!, file.GetProperty("Name").GetString()!);
            var actual = Describe(path);
            Assert.AreEqual(actual.ByteLength, file.GetProperty("ByteLength").GetInt64());
            Assert.AreEqual(actual.Sha256, file.GetProperty("Sha256").GetString());
        }
        var baselineTrialFiles = b.GetProperty("TrialFiles").EnumerateArray()
            .Select(file => file.GetProperty("Name").GetString()).Order().ToArray();
        CollectionAssert.AreEqual(expectedFiles.Where(static name => name.Contains("trial-", StringComparison.Ordinal)).ToArray(), baselineTrialFiles);
        foreach (var summaryFile in b.GetProperty("TrialFiles").EnumerateArray())
        {
            var name = summaryFile.GetProperty("Name").GetString();
            var manifestFile = manifest.RootElement.GetProperty("Files").EnumerateArray()
                .Single(file => file.GetProperty("Name").GetString() == name);
            Assert.AreEqual(summaryFile.GetProperty("ByteLength").GetInt64(), manifestFile.GetProperty("ByteLength").GetInt64());
            Assert.AreEqual(summaryFile.GetProperty("Sha256").GetString(), manifestFile.GetProperty("Sha256").GetString());
        }
        var baselineHead = b.GetProperty("Source").GetProperty("Head").GetString();
        var baselineAssemblies = b.GetProperty("Source").GetProperty("Assemblies").GetRawText();
        for (var trial = 1; trial <= 5; trial++)
        {
            var trialPath = Path.Combine(
                Path.GetDirectoryName(baselineManifestPath)!,
                "..",
                $"trial-{trial}",
                "transient-candidate-reservation-evidence.json");
            using var trialDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(trialPath).ConfigureAwait(false));
            var trialRoot = trialDocument.RootElement;
            Assert.AreEqual("baseline", trialRoot.GetProperty("Phase").GetString());
            Assert.AreEqual(trial, trialRoot.GetProperty("Trial").GetInt32());
            Assert.AreEqual(BaselineRevision, trialRoot.GetProperty("ProductionRevision").GetString());
            Assert.AreEqual(baselineHead, trialRoot.GetProperty("Source").GetProperty("Head").GetString());
            Assert.AreEqual(baselineAssemblies, trialRoot.GetProperty("Source").GetProperty("Assemblies").GetRawText());
            Assert.AreEqual(trial, trialRoot.GetProperty("Source").GetProperty("Trial").GetInt32());
            Assert.AreEqual(harnessSha, trialRoot.GetProperty("HarnessDefinitionSha256").GetString());
            Assert.AreEqual(workloadSha, trialRoot.GetProperty("WorkloadSha256").GetString());
            Assert.AreEqual(environmentSha, trialRoot.GetProperty("Environment").GetProperty("EnvironmentFingerprintSha256").GetString());
        }
        var baselineMetric = b.GetProperty("Metrics").GetProperty("BarrierWriterMaximumMilliseconds");
        var afterMetric = a.GetProperty("Metrics").GetProperty("BarrierWriterMaximumMilliseconds");
        var baselineMedian = baselineMetric.GetProperty("Median").GetDouble();
        var threshold = Math.Max(.20, 2 * (baselineMetric.GetProperty("Maximum").GetDouble() - baselineMetric.GetProperty("Minimum").GetDouble()) / baselineMedian);
        var reduction = (baselineMedian - afterMetric.GetProperty("Median").GetDouble()) / baselineMedian;
        var metricComparisons = new List<MetricComparison>();
        foreach (var metric in b.GetProperty("Metrics").EnumerateObject())
        {
            var baselineValue = metric.Value;
            var afterValue = a.GetProperty("Metrics").GetProperty(metric.Name);
            var median = baselineValue.GetProperty("Median").GetDouble();
            var afterMedian = afterValue.GetProperty("Median").GetDouble();
            var materialThreshold = median == 0
                ? .20
                : Math.Max(.20, 2 * (baselineValue.GetProperty("Maximum").GetDouble() - baselineValue.GetProperty("Minimum").GetDouble()) / Math.Abs(median));
            double? relativeChange = median == 0 ? null : (afterMedian - median) / Math.Abs(median);
            var higherIsRegression = metric.Name != "ReservationThroughputPerSecond";
            var materialRegression = relativeChange is null
                ? higherIsRegression && afterMedian > 0
                : (higherIsRegression ? relativeChange.Value : -relativeChange.Value) > materialThreshold;
            metricComparisons.Add(new MetricComparison(
                metric.Name, median, afterMedian, relativeChange, materialThreshold, materialRegression));
        }
        var regressions = metricComparisons.Where(static metric => metric.MaterialRegression)
            .Select(static metric => metric.Name).ToArray();
        var comparison = new
        {
            Schema = "hvo-issue-247-baseline-after-comparison-v1",
            ReviewedBaselineSummarySha256 = expectedSha,
            ReviewedBaselineManifestSha256 = expectedManifestSha,
            HarnessDefinitionSha256 = harnessSha,
            WorkloadSha256 = workloadSha,
            EnvironmentFingerprintSha256 = environmentSha,
            BarrierWriterMaximumReduction = reduction,
            MaterialThreshold = threshold,
            ReductionBeyondMateriality = reduction > threshold,
            Metrics = metricComparisons,
            MaterialRegressions = regressions,
            Result = reduction <= threshold
                ? "failed-writer-wait-reduction"
                : regressions.Length > 0 ? "failed-material-regression" : "passed"
        };
        await WritePrivateJsonAsync(outputPath, comparison, root, Path.GetDirectoryName(outputPath)!, deterministicIds, payload).ConfigureAwait(false);
        Assert.IsTrue(reduction > threshold, "Writer wait reduction did not exceed materiality.");
        Assert.IsEmpty(regressions, "After evidence contains a material performance/resource regression.");
    }

    private static async Task WritePrivateJsonAsync(
        string path,
        object value,
        string repositoryRoot,
        string fixtureRoot,
        IReadOnlyList<Guid> ids,
        byte[] payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        AssertPrivate(bytes, repositoryRoot, fixtureRoot, ids, payload);
        await EvidenceSourceIdentity.WriteJsonAsync(path, value, JsonOptions).ConfigureAwait(false);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path).ConfigureAwait(false));
    }

    private static void AssertPrivate(
        ReadOnlySpan<byte> bytes,
        string repositoryRoot,
        string fixtureRoot,
        IReadOnlyList<Guid> ids,
        byte[] payload)
    {
        Assert.IsLessThan(4_000_000, bytes.Length);
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var forbidden in new[] { repositoryRoot, fixtureRoot, Path.GetTempPath(), "Data Source=", "DataSource=", "Filename=", "Server=", "Password=", "Pwd=", "AccessKey=", "Secret=", "Token=", "ApiKey=" })
            Assert.IsFalse(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"Forbidden evidence value: {forbidden}");
        Assert.IsFalse(GuidRegex().IsMatch(text));
        Assert.IsFalse(AbsolutePathRegex().IsMatch(text));
        Assert.IsFalse(SecretNameRegex().IsMatch(text));
        foreach (var id in ids)
        {
            Assert.IsFalse(text.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(text.Contains(id.ToString("N"), StringComparison.OrdinalIgnoreCase));
        }
        foreach (var offset in new[] { 0, payload.Length / 2 - 32, payload.Length - 64 })
        {
            var sample = payload.AsSpan(offset, 64);
            Assert.IsFalse(text.Contains(Convert.ToBase64String(sample), StringComparison.Ordinal));
            Assert.IsFalse(text.Contains(Convert.ToHexString(sample), StringComparison.OrdinalIgnoreCase));
        }
    }

    private static byte[] CreatePayload()
    {
        var payload = new byte[PayloadLength];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)((index * 31L + Seed) % 251);
        return payload;
    }

    private static async Task<CameraModuleConfig> LoadConfigurationAsync(string root)
    {
        var loader = new FileCameraAgentConfigurationLoader(
            Options.Create(new CameraAgentHostOptions
            {
                ConfigFilePath = Path.Combine(root, "src", "HVO.SkyMonitor.CameraAgent", ProfileName),
                RawIngressRoot = "issue-247-test-only",
                AgentId = "agent",
                Observatory = new ObservatoryLocation(35.5599378, -113.9119818, 520, "America/Phoenix")
            }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        return await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static CaptureLoopSubmission Submission(DateTimeOffset timestamp, byte[] payload)
    {
        var frame = new CameraFrame(
            timestamp, Width, Height, CameraPixelFormat.BayerRggb16, payload,
            new FrameMetadata(TimeSpan.FromSeconds(20), 150, double.NaN, "Issue247"), Stride);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null);
        return new CaptureLoopSubmission(
            new CaptureRequest(timestamp, TimeSpan.FromSeconds(25), CaptureMode.Still, setpoint),
            new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(timestamp, timestamp.AddSeconds(20), timestamp.AddSeconds(20))
            }, timestamp, TimeSpan.FromSeconds(25), TimeSpan.Zero);
    }

    private static Guid DeterministicGuid(string scope, int ordinal)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"hvo-issue-247|{Seed}|{scope}|{ordinal}"));
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static async Task<EnvironmentRecord> EnvironmentEvidenceAsync(string root)
    {
        var cpuLines = File.Exists("/proc/cpuinfo") ? await File.ReadAllLinesAsync("/proc/cpuinfo").ConfigureAwait(false) : [];
        var cpuModel = cpuLines.FirstOrDefault(static line => line.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? "unavailable";
        var memory = File.Exists("/proc/meminfo") ? await File.ReadAllLinesAsync("/proc/meminfo").ConfigureAwait(false) : [];
        static long Kb(IReadOnlyList<string> lines, string name) => long.TryParse(
            lines.FirstOrDefault(line => line.StartsWith(name, StringComparison.Ordinal))?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1),
            NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value * 1024 : 0;
        var sdkResult = await RunAsync(root, "dotnet", "--version").ConfigureAwait(false);
        var values = new
        {
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Framework = RuntimeInformation.FrameworkDescription,
            CpuModel = cpuModel,
            Environment.ProcessorCount,
            TotalMemoryBytes = Kb(memory, "MemTotal:"),
            SwapTotalBytes = Kb(memory, "SwapTotal:"),
            Configuration = "Release",
            ServerGc = System.Runtime.GCSettings.IsServerGC,
            Sdk = sdkResult.Output.Trim(),
            SqliteVersion = await SqliteVersionAsync().ConfigureAwait(false),
            FileSystemFormat = new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).DriveFormat,
            Topology = "native in-process Microsoft.Data.Sqlite over fixture-local WAL",
            PhysicalMedia = "unknown/unattributable"
        };
        return new EnvironmentRecord(
            values.OperatingSystem,
            values.Architecture,
            values.Framework,
            values.CpuModel,
            values.ProcessorCount,
            values.TotalMemoryBytes,
            values.SwapTotalBytes,
            values.Configuration,
            values.ServerGc,
            values.Sdk,
            values.SqliteVersion,
            values.FileSystemFormat,
            values.Topology,
            values.PhysicalMedia,
            HashText(JsonSerializer.Serialize(values)));
    }

    private static async Task<string> SqliteVersionAsync()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture)!;
    }

    private static SampleSummary Summary(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new SampleSummary(Percentile(sorted, .5), Percentile(sorted, .95), sorted[^1], sorted[0], sorted.Length);
    }

    private static MinMedianMaxSummary MinMedianMax(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new MinMedianMaxSummary(sorted[0], Percentile(sorted, .5), sorted[^1], sorted.Length);
    }

    private static double Percentile(double[] sorted, double percentile)
        => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];

    private static TrialSummary SummarizeTrials(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        Assert.HasCount(5, sorted);
        return new TrialSummary(sorted[0], sorted[2], sorted[^1]);
    }

    private static string HashStrings(IEnumerable<string> values) => HashText(string.Join('\n', values));
    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task<string> DurableStateShaAsync(string root)
    {
        using var connection = await OpenAsync(root, CancellationToken.None).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT capture_sequence, descriptor_sha256, manifest_sha256, payload_sha256, payload_length, payload_relative_path, sidecar_relative_path FROM raw_captures ORDER BY capture_sequence;";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            hash.AppendData(Encoding.UTF8.GetBytes(string.Join('|', Enumerable.Range(0, 7).Select(reader.GetValue))));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<CheckpointEvidence> CheckpointAsync(string root)
    {
        using var connection = await OpenAsync(root, CancellationToken.None).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return new CheckpointEvidence(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private static DatabaseSizeEvidence DatabaseSizes(string path) => new(
        new FileInfo(path).Length,
        File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0,
        File.Exists(path + "-shm") ? new FileInfo(path + "-shm").Length : 0);

    private static ProcessIoEvidence ReadProcessIo()
    {
        if (!File.Exists("/proc/self/io")) return new ProcessIoEvidence(0, 0, 0, 0);
        var values = File.ReadLines("/proc/self/io").Select(static line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(static parts => parts.Length == 2)
            .ToDictionary(static parts => parts[0], static parts => long.Parse(parts[1], CultureInfo.InvariantCulture), StringComparer.Ordinal);
        return new ProcessIoEvidence(values.GetValueOrDefault("read_bytes"), values.GetValueOrDefault("write_bytes"), values.GetValueOrDefault("syscr"), values.GetValueOrDefault("syscw"));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test callers provide fixed statements and parameterized values only.")]
    private static async Task ExecuteAsync(string database, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = await OpenDatabaseAsync(database).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test callers provide fixed scalar statements and parameterized values only.")]
    private static async Task<long> ScalarAsync(string database, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = await OpenDatabaseAsync(database).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test callers provide fixed scalar statements only.")]
    private static async Task<string> ScalarStringAsync(string database, string sql)
    {
        using var connection = await OpenDatabaseAsync(database).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture)!;
    }

    private static async Task<SqliteConnection> OpenAsync(string root, CancellationToken cancellationToken)
        => await OpenDatabaseAsync(DatabasePath(root), cancellationToken).ConfigureAwait(false);

    private static async Task<SqliteConnection> OpenDatabaseAsync(string database, CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true,
            DefaultTimeout = 15
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 15000; PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static SqliteType ParseSqliteType(string value) => value == "Integer" ? SqliteType.Integer : SqliteType.Text;
    private static string DatabasePath(string root) => Path.Combine(root, "journal", "raw-ingress.db");

    private static async Task CreateFifoAsync(string path)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("mkfifo") { RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add(path);
        Assert.IsTrue(process.Start());
        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode, error);
    }

    private static async Task DrainFifoAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        await stream.CopyToAsync(Stream.Null).ConfigureAwait(false);
    }

    private static void AssertSourceEqual(EvidenceSourceSnapshot start, EvidenceSourceSnapshot end)
    {
        Assert.AreEqual(start.Head, end.Head);
        Assert.AreEqual(start.DirtyDiffSha256, end.DirtyDiffSha256);
        Assert.AreEqual(start.RunId, end.RunId);
        CollectionAssert.AreEqual(start.Assemblies.ToArray(), end.Assemblies.ToArray());
    }

    private static PrivateSourceSnapshot PrivateSource(EvidenceSourceSnapshot source) => new(
        source.RequestedRevision, source.Head, source.Branch, source.Dirty, source.DirtyDiffSha256,
        source.OutputDirectoryName, source.Trial, source.Claimability,
        source.Assemblies.Select(static assembly => new PrivateAssemblySnapshot(
            assembly.Name, assembly.Sha256, assembly.Configuration, assembly.InformationalVersion,
            assembly.SourceSha256, assembly.AssemblyWrittenUtc, assembly.LatestSourceWriteUtc)).ToArray());

    private static FileDescription Describe(string path, string? relativeTo = null)
    {
        var bytes = File.ReadAllBytes(path);
        return new FileDescription(
            relativeTo is null ? Path.GetFileName(path) : Path.GetRelativePath(relativeTo, path).Replace('\\', '/'),
            bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static FileDescription DescribeAs(string path, string name)
    {
        var bytes = File.ReadAllBytes(path);
        return new FileDescription(name, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"{name} is required.");

    private static int ReadTrial() => int.TryParse(Required("HVO_EVIDENCE_TRIAL"), NumberStyles.None, CultureInfo.InvariantCulture, out var trial) && trial is >= 1 and <= 5
        ? trial : throw new InvalidOperationException("HVO_EVIDENCE_TRIAL must be 1 through 5.");

    private static async Task<string> GitAsync(string root, params string[] arguments)
    {
        var (exit, output, error) = await RunAsync(root, "git", arguments).ConfigureAwait(false);
        if (exit != 0) throw new InvalidOperationException($"git failed: {error}");
        return output;
    }

    private static async Task<int> GitExitAsync(string root, params string[] arguments)
        => (await RunAsync(root, "git", arguments).ConfigureAwait(false)).Exit;

    private static async Task<(int Exit, string Output, string Error)> RunAsync(string root, string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {file}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    [GeneratedRegex("(?i)\\b(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\\b", RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();

    [GeneratedRegex("(?i)(?:[a-z]:\\\\|/(?:home|mnt|opt|proc|root|run|tmp|var|workspaces)/)[^\\s\\\"]+", RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePathRegex();

    [GeneratedRegex("(?i)\\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|client[_-]?secret)\\b\\s*[:=]", RegexOptions.CultureInvariant)]
    private static partial Regex SecretNameRegex();

    private sealed record Scale(int RawRows, int PhysicalRows, int Warmups, int Measured, TimeSpan Barrier, int WriterCadenceMilliseconds);
    private sealed record WriterParameter(string Name, string SqliteType);
    private sealed record WriterDefinition(string Kind, string Owner, string Statement, string StatementSha256, IReadOnlyList<WriterParameter> Parameters)
    {
        internal static WriterDefinition Create(string kind, string owner, string statement, IReadOnlyList<WriterParameter> parameters)
            => new(kind, owner, statement, HashText(statement), parameters);
    }
    private sealed record WriterSample(string Kind, int ScheduledOfferOffsetMilliseconds, double ActualOfferOffsetMilliseconds, double ElapsedMilliseconds, double TransactionAcquisitionMilliseconds, double TransactionHoldMilliseconds, double? CompletionRelativeToBarrierReleaseMilliseconds, int RowsAffected, string Outcome, bool BusyOrLocked, bool TransactionStarted, bool TransactionCommitted, bool TransactionFailed)
    {
        public double CompletionOffsetMilliseconds => ActualOfferOffsetMilliseconds + ElapsedMilliseconds;
    }
    private sealed record OperationSample(int Ordinal, string Kind, double ReservationPreCallMilliseconds, double ReservationElapsedMilliseconds, IReadOnlyList<WriterSample> Writers);
    private sealed record SeedEvidence(IReadOnlyList<TransientSourceEvidenceReferenceV1> Sources, IReadOnlyList<long> SidecarLengths, IReadOnlyList<Guid> DeterministicIds, int RawRows, int PhysicalRows, int MetadataOnlyRows, long PayloadBytes, long PayloadBytesHashed, string ManifestSetSha256, string DurableStateSha256, double SeedElapsedMilliseconds);
    private sealed record SampleSummary(double P50Milliseconds, double P95Milliseconds, double MaximumMilliseconds, double MinimumMilliseconds, int Samples);
    private sealed record MinMedianMaxSummary(double MinimumMilliseconds, double MedianMilliseconds, double MaximumMilliseconds, int Samples);
    private sealed record TrialSummary(double Minimum, double Median, double Maximum);
    private sealed record PlanEvidence(string Name, string Owner, string StatementSha256, IReadOnlyList<WriterParameter> Parameters, IReadOnlyList<string> Detail);
    private sealed record CheckpointEvidence(int Busy, int LogFrames, int CheckpointedFrames);
    private sealed record DatabaseSizeEvidence(long DatabaseBytes, long WalBytes, long ShmBytes);
    private sealed record ResourceEvidence(double ProcessCpuMilliseconds, long AllocatedBytes, long RssStartBytes, long PeakObservedRssBytes, long RssEndBytes, IReadOnlyList<long> OperationBoundaryRssBytes, ProcessIoEvidence ProcessIoStart, ProcessIoEvidence ProcessIoEnd, ProcessIoEvidence ProcessIoDelta);
    private sealed record ProcessIoEvidence(long ReadBytes, long WriteBytes, long ReadOperations, long WriteOperations)
    {
        public static ProcessIoEvidence operator -(ProcessIoEvidence end, ProcessIoEvidence start) => new(
            Math.Max(0, end.ReadBytes - start.ReadBytes),
            Math.Max(0, end.WriteBytes - start.WriteBytes),
            Math.Max(0, end.ReadOperations - start.ReadOperations),
            Math.Max(0, end.WriteOperations - start.WriteOperations));
    }
    private sealed record FileHash(string Name, long ByteLength, string Sha256);
    private sealed record InputHashes(string ImmutableSha256, string AllSha256, string ProfileSha256, IReadOnlyList<FileHash> Files);
    private sealed record WorkloadIdentity(string Sha256, object Public);
    private sealed record FileDescription(string Name, long ByteLength, string Sha256);
    private sealed record PrivateSourceSnapshot(string? RequestedRevision, string Head, string Branch, bool Dirty, string DirtyDiffSha256, string OutputDirectoryName, int? Trial, string Claimability, IReadOnlyList<PrivateAssemblySnapshot> Assemblies);
    private sealed record PrivateAssemblySnapshot(string Name, string Sha256, string Configuration, string InformationalVersion, string SourceSha256, DateTime AssemblyWrittenUtc, DateTime LatestSourceWriteUtc);
    private sealed record EnvironmentRecord(string OperatingSystem, string Architecture, string Framework, string CpuModel, int ProcessorCount, long TotalMemoryBytes, long SwapTotalBytes, string Configuration, bool ServerGc, string SdkVersion, string SqliteVersion, string FileSystemFormat, string Topology, string PhysicalMedia, string EnvironmentFingerprintSha256);
    private sealed record AggregateTrial(double P50, double P95, double Max, double Throughput, double NormalWriterP95, double BarrierWriterMaximum, double Cpu, double Allocated, double PeakRss, double Database, double Wal, double Shm);
    private sealed record MetricComparison(string Name, double BaselineMedian, double AfterMedian, double? RelativeChange, double MaterialThreshold, bool MaterialRegression);

    private sealed class ReserveStageCollector : IDisposable
    {
        internal const string SnapshotOperation = "transient-candidate.reserve.snapshot";
        internal const string ImmediateOperation = "transient-candidate.reserve.immediate";
        private readonly object _gate = new();
        private readonly List<double> _snapshot = [];
        private readonly List<double> _immediate = [];
        private readonly ActivityListener _listener;

        internal ReserveStageCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static _ => true,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity => Record(activity.OperationName, activity.Duration.TotalMilliseconds)
            };
            ActivitySource.AddActivityListener(_listener);
        }

        private void Record(string operation, double milliseconds)
        {
            lock (_gate)
            {
                if (operation == SnapshotOperation) _snapshot.Add(milliseconds);
                else if (operation == ImmediateOperation) _immediate.Add(milliseconds);
            }
        }

        internal StageTelemetry Snapshot()
        {
            lock (_gate) return new StageTelemetry(_snapshot.ToArray(), _immediate.ToArray());
        }

        internal void Reset()
        {
            lock (_gate)
            {
                _snapshot.Clear();
                _immediate.Clear();
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed record StageTelemetry(IReadOnlyList<double> SnapshotMilliseconds, IReadOnlyList<double> ImmediateMilliseconds);
}

internal static class Issue247EnumerableExtensions
{
    internal static IEnumerable<T> AppendRange<T>(this IEnumerable<T> source, IEnumerable<T> values) => source.Concat(values);
}
