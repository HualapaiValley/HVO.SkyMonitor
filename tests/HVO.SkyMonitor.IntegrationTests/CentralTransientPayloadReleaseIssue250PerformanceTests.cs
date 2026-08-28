using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Exposure;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "The evidence harness drains and aggregates cleanup failures before reporting them.")]
public sealed class CentralTransientPayloadReleaseIssue250PerformanceTests
{
    private const string ProductionRevision = "89c3e5176417a70fcfc5c67d2b0adb233ee7a9e4";
    private const string AcceptedBaselineSourceRevision = "2d8c99f1c745386587482f82628672a304557d90";
    private const string AcceptedBaselineManifestPath = "/var/lib/hvo-agent-state/issues/268/datas-off-baseline-2d8c99f1c745386587482f82628672a304557d90/trial-1/manifest.json";
    private const string AcceptedBaselineRelativePath = "issues/268/datas-off-baseline-2d8c99f1c745386587482f82628672a304557d90/trial-1/manifest.json";
    private const string AcceptedBaselineManifestSha256 = "0390D4A70CDF18A5F1714EAD17D5D1790B88A9BA6D7388D1C067B29913746F14";
    private const string AcceptedBaselineHarnessSha256 = "E29FF6C91205CF689BAF07CB20C31A7DD269DE2ED745088FFF53C4B6C0C6E388";
    private const string AcceptedBaselineProtocolSha256 = "E773CE1FBD51377ECDE5948562B1A31112674B0476721C177F6DD0F12FE6DE3B";
    private const string AcceptedBaselineCompatibilityProtocolSha256 = "5C88E596C0802D624F7A5017416EFB5E7EE10DB4E03B2CB7651CB9EEA639811C";
    private const string AcceptedBaselineSemanticWorkloadSha256 = "F02F56D059143F6B1AC37AEAD5D027F438AE74BCB876AC7666E575320A04D02B";
    private const string Bucket = "skymonitor-artifacts";
    private const string BucketPrefix = "minio://skymonitor-artifacts/";
    private const int FullWidth = 3_096;
    private const int FullHeight = 2_080;
    private const int SourceBytes = 12_879_360;
    private const int DerivativeBytes = 19_319_040;
    private const string SourceSha256 = "E830DFEE9D58741376B8021874C06071D41524E3BBF29ECB90C47E9085A4B0EF";
    private const string DerivativePreviewSha256 = "4144D8B52594F99EB5181AF40D50504222FC1183128FD19FD0529A6ED2267B06";
    private const string DerivativeOverlaySha256 = "EA93DCCEE3060D11109A415FE9F84029EA41EE0F1CD49231FD364147C7976F22";
    private const string W0Sha256 = "CA5C32D3342EB2EAC9DEB6D0A6D91E6973AD417437EF292FD59E5132053A2818";
    private const int W3MSentinelByteLength = 16;
    private const string W3MSentinelGenerator = "byte((17 * index + 3) modulo 256), index=0..15";
    private const string W3MSentinelSha256 = "A23D99F2CC2B11F42045500D5073B1128068FFF05D23D61A8E90152099E1E647";
    private const int W3MSetupBatchParents = 250;
    private const int W3MCommandTimeoutSeconds = 120;
    private const int W3MPhaseTimeoutMinutes = 15;
    private const string TwoOutputFixtureName = "issue-250-two-output-release-fixture-v1";
    private const string CompatibilityProtocolSchema = "issue-250-release-compatibility-v2";
    private const string TrialPolicy = "single-authorized-trial-1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly int[] CanonicalConcurrency = [1, 4];
    private static readonly int[] CanonicalDelays = [0, 250, 2_000];
    private static readonly int[] SmokeDelays = [0, 250];
    private static readonly string[] ExpectedDerivativeOutputKinds = ["Overlay", "Preview"];
    private static readonly string[] MinioBackedDerivativeKinds = ["Preview", "Overlay"];
    private static readonly string[] FixtureHolds = ["source ordinal 0", "Overlay ordinal 6"];
    private static readonly string[] ProcessResourceCounters = ["Process.TotalProcessorTime", "System.Runtime alloc-rate", "GC.GetTotalAllocatedBytes(true)", "Process.WorkingSet64"];
    private static readonly Issue250RequiredTrigger[] RequiredW3MTriggers =
    [
        new("TR_CentralFrames_InstallationImmutable", "CentralFrames",
            ["AFTER UPDATE", "LogicalCameraInstallationId", "THROW 51000", "immutable"]),
        new("TR_CentralTransientEvents_Immutable", "CentralTransientEvents",
            ["AFTER UPDATE", "DELETE", "THROW 51000", "immutable"]),
        new("TR_CentralTransientPayloadReleases_Insert", "CentralTransientPayloadReleases",
            ["AFTER INSERT", "inserted", "State", "Pending", "THROW 51000"]),
        new("TR_CentralTransientPayloadReleases_Transition", "CentralTransientPayloadReleases",
            ["AFTER UPDATE", "DELETE", "inserted", "deleted", "Pending", "Completed", "Failed", "CentralTransientPayloadReleaseItems", "Outcome"]),
        new("TR_CentralTransientPayloadReleaseItems_Closed", "CentralTransientPayloadReleaseItems",
            ["AFTER INSERT", "inserted", "CentralTransientPayloadReleases", "State", "Outcome", "Pending", "THROW 51000"]),
        new("TR_CentralTransientPayloadReleaseItems_Transition", "CentralTransientPayloadReleaseItems",
            ["AFTER UPDATE", "DELETE", "inserted", "deleted", "Pending", "Released", "PreservedHeld", "Failed", "ReleasedUtc", "FailureReasonCode", "Kind", "RecordId", "ReservationToken", "RetryAtUtc", "RetryCount"])
    ];
    private static readonly Issue250RequiredSchemaRelation[] RequiredW3MForeignKeys =
    [
        new("FK_CentralArtifacts_CentralFrames_CentralFrameId", "CentralArtifacts", ["CentralFrameId"],
            "CentralFrames", ["Id"], "CASCADE", "NO_ACTION", Enabled: true, Trusted: true),
        new("FK_CentralTransientPayloadReleases_CentralTransientEvents_CentralTransientEventId",
            "CentralTransientPayloadReleases", ["CentralTransientEventId"], "CentralTransientEvents", ["Id"],
            "NO_ACTION", "NO_ACTION", Enabled: true, Trusted: true),
        new("FK_CentralTransientPayloadReleaseItems_CentralTransientPayloadReleases_ReleaseId",
            "CentralTransientPayloadReleaseItems", ["ReleaseId"], "CentralTransientPayloadReleases", ["ReleaseId"],
            "NO_ACTION", "NO_ACTION", Enabled: true, Trusted: true)
    ];
    private static readonly Issue250RequiredCheckConstraint[] RequiredW3MCheckConstraints =
    [
        new("CK_CentralArtifacts_ObjectVerification", "CentralArtifacts",
            ["ObjectVerificationToken", "ObjectVerificationRequestedAtUtc", "ObjectVerificationRetryCount", "ObjectVerificationRetryAtUtc", "ObjectState", "Pending"]),
        new("CK_CentralArtifacts_RetentionDeletion", "CentralArtifacts",
            ["RetentionDeletionToken", "RetentionDeletionRequestedAtUtc", "RetentionDeletionCompletedAtUtc", "ObjectState", "Expired"]),
        new("CK_CentralTransientPayloadReleases_Completion", "CentralTransientPayloadReleases",
            ["State", "Pending", "Completed", "Failed", "CompletedUtc"]),
        new("CK_CentralTransientPayloadReleaseItems_Ordinal", "CentralTransientPayloadReleaseItems",
            ["Ordinal", ">="]),
        new("CK_CentralTransientPayloadReleaseItems_Outcome", "CentralTransientPayloadReleaseItems",
            ["Outcome", "Pending", "Released", "PreservedHeld", "Failed", "ReleasedUtc", "FailureReasonCode"]),
        new("CK_CentralTransientPayloadReleaseItems_Reservation", "CentralTransientPayloadReleaseItems",
            ["RequestedAtUtc", "StorageReference", "TargetRowVersion", "DATALENGTH", "TargetGeneration", "ReservationToken", "RetryAtUtc", "Outcome", "Pending"]),
        new("CK_CentralTransientPayloadReleaseItems_RetryCount", "CentralTransientPayloadReleaseItems",
            ["RetryCount", ">="])
    ];
    private static readonly Issue250RequiredUniqueIndex[] RequiredW3MUniqueIndexes =
    [
        new("PK_CentralFrames", "CentralFrames", "PrimaryKey", [new("Id", Descending: false)], [], null,
            Unique: true, Enabled: true),
        new("IX_CentralFrames_DevicePublicId_FrameId", "CentralFrames", "UniqueIndex",
            [new("DevicePublicId", Descending: false), new("FrameId", Descending: false)], [], null,
            Unique: true, Enabled: true),
        new("PK_CentralArtifacts", "CentralArtifacts", "PrimaryKey", [new("Id", Descending: false)], [], null,
            Unique: true, Enabled: true),
        new("IX_CentralArtifacts_IdempotencyKey", "CentralArtifacts", "UniqueIndex",
            [new("IdempotencyKey", Descending: false)], [], null, Unique: true, Enabled: true),
        new("IX_CentralArtifacts_DevicePublicId_ArtifactId", "CentralArtifacts", "UniqueIndex",
            [new("DevicePublicId", Descending: false), new("ArtifactId", Descending: false)], [],
            "[DevicePublicId] IS NOT NULL", Unique: true, Enabled: true),
        new("IX_CentralArtifacts_CentralFrameId_ArtifactId", "CentralArtifacts", "UniqueIndex",
            [new("CentralFrameId", Descending: false), new("ArtifactId", Descending: false)], [], null,
            Unique: true, Enabled: true),
        new("PK_CentralTransientEvents", "CentralTransientEvents", "PrimaryKey", [new("Id", Descending: false)],
            [], null, Unique: true, Enabled: true),
        new("AK_CentralTransientEvents_AgentId_EventId", "CentralTransientEvents", "UniqueConstraint",
            [new("AgentId", Descending: false), new("EventId", Descending: false)], [], null,
            Unique: true, Enabled: true),
        new("PK_CentralTransientPayloadReleases", "CentralTransientPayloadReleases", "PrimaryKey",
            [new("ReleaseId", Descending: false)], [], null, Unique: true, Enabled: true),
        new("IX_CentralTransientPayloadReleases_CentralTransientEventId_ActorIdentity_IdempotencyKey",
            "CentralTransientPayloadReleases", "UniqueIndex",
            [new("CentralTransientEventId", Descending: false), new("ActorIdentity", Descending: false),
                new("IdempotencyKey", Descending: false)], [], null, Unique: true, Enabled: true),
        new("PK_CentralTransientPayloadReleaseItems", "CentralTransientPayloadReleaseItems", "PrimaryKey",
            [new("ReleaseId", Descending: false), new("Ordinal", Descending: false)], [], null,
            Unique: true, Enabled: true),
        new("IX_CentralTransientPayloadReleaseItems_ReleaseId_Kind_RecordId", "CentralTransientPayloadReleaseItems",
            "UniqueIndex", [new("ReleaseId", Descending: false), new("Kind", Descending: false),
                new("RecordId", Descending: false)], [], null, Unique: true, Enabled: true),
        new("IX_CentralTransientPayloadReleaseItems_Kind_RecordId", "CentralTransientPayloadReleaseItems",
            "NonUniqueIndex", [new("Kind", Descending: false), new("RecordId", Descending: false)], [], null,
            Unique: false, Enabled: true),
        new("IX_CentralTransientPayloadReleaseItems_RetryAtUtc_RequestedAtUtc_ReleaseId_Ordinal",
            "CentralTransientPayloadReleaseItems", "NonUniqueIndex",
            [new("RetryAtUtc", Descending: false), new("RequestedAtUtc", Descending: false),
                new("ReleaseId", Descending: false), new("Ordinal", Descending: false)],
            ["ReservationToken"], "[Outcome]=N'Pending'", Unique: false, Enabled: true)
    ];
    private static readonly string[] CanonicalFaultManifest =
    [
        "before-reservation-commit",
        "after-reservation",
        "before-delete",
        "object-already-absent",
        "delete-failure",
        "after-delete-before-finalize",
        "stale-target-rowversion-reference-generation",
        "new-independent-hold",
        "between-ordinals",
        "after-final-item-before-parent-completion",
        "processor-restart"
    ];
    private static readonly string[] BaselineChangedPaths =
    [
        "scripts/test-categories/Program.cs",
        "tests/HVO.SkyMonitor.IntegrationTests/AssemblyHooks.cs",
        "tests/HVO.SkyMonitor.IntegrationTests/CentralTransientPayloadReleaseIssue250PerformanceTests.cs",
        "tests/HVO.SkyMonitor.IntegrationTests/Issue170AllocationSampler.cs"
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void EvidenceCollector_CreationConflictTelemetry_IsBoundedAndResettable()
    {
        using var collector = new Issue250EvidenceCollector();
        using var telemetry = new CentralTransientLifecycleTelemetry(Issue250DiscardLogger.Instance);

        telemetry.RecordRetentionCreationConflict("deadlock", "retry", 1);
        telemetry.RecordRetentionCreationConflict("ambiguous-commit", "retry", 2);
        telemetry.RecordRetentionCreationConflict("ambiguous-commit", "recovered", 3);
        telemetry.RecordRetentionCreationConflict("deadlock", "exhausted", 4);

        var snapshot = collector.Snapshot();
        Assert.AreEqual(1L, snapshot.InternalCreationDeadlockRetries);
        Assert.AreEqual(1L, snapshot.InternalCreationAmbiguousCommitRetries);
        Assert.AreEqual(1L, snapshot.InternalCreationAmbiguousCommitRecoveries);
        Assert.AreEqual(1L, snapshot.InternalCreationConflictExhaustions);

        collector.Reset();
        snapshot = collector.Snapshot();
        Assert.AreEqual(0L, snapshot.InternalCreationDeadlockRetries);
        Assert.AreEqual(0L, snapshot.InternalCreationAmbiguousCommitRetries);
        Assert.AreEqual(0L, snapshot.InternalCreationAmbiguousCommitRecoveries);
        Assert.AreEqual(0L, snapshot.InternalCreationConflictExhaustions);
    }

    [TestMethod]
    [Timeout(1_800_000)]
    public async Task Release_W2W3MAndContention_RecordsEvidence()
    {
        var repositoryRoot = FindRepositoryRoot();
        var phase = ReadPhase();
        var smoke = string.Equals(
            Environment.GetEnvironmentVariable("HVO_ISSUE_250_SMOKE"), "1", StringComparison.Ordinal);
        var afterSmoke = smoke && phase == "after" && string.Equals(
            Environment.GetEnvironmentVariable("HVO_ISSUE_250_AFTER_SMOKE"), "1", StringComparison.Ordinal);
        var issue268Evidence = !smoke && phase == "after" && string.Equals(
            Environment.GetEnvironmentVariable("HVO_ISSUE_268_SYNTHETIC_EVIDENCE"), "1", StringComparison.Ordinal);
        if (!string.Equals(Environment.GetEnvironmentVariable("HVO_ISSUE_250_EVIDENCE"), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Issue #250 evidence requires HVO_ISSUE_250_EVIDENCE=1 before test-host startup.");
        }
        if (phase == "after" && !smoke && !issue268Evidence)
        {
            throw new InvalidOperationException(
                "Claimable issue #250 after evidence requires HVO_ISSUE_268_SYNTHETIC_EVIDENCE=1 from the dedicated consolidation campaign.");
        }
        if (smoke && phase != "development" && !afterSmoke)
        {
            throw new InvalidOperationException(
                "HVO_ISSUE_250_SMOKE=1 is development-only unless HVO_ISSUE_250_AFTER_SMOKE=1 explicitly selects unclaimable after-capability mechanics.");
        }
        if (phase != "development" && !GCSettings.IsServerGC)
        {
            throw new InvalidOperationException("Claimable issue #250 evidence requires DOTNET_gcServer=1.");
        }
        var gcDynamicAdaptationMode = ReadGcDynamicAdaptationMode();
        if (string.Equals(Environment.GetEnvironmentVariable("HVO_ISSUE_268_SYNTHETIC_EVIDENCE"), "1",
                StringComparison.Ordinal)
            && gcDynamicAdaptationMode != 0)
        {
            throw new InvalidOperationException(
                "Issue #268 evidence requires DOTNET_GCDynamicAdaptationMode=0 to avoid the .NET 10 Server GC allocation-counter regression.");
        }
        await ValidateHttpAccountingCollectorAsync().ConfigureAwait(false);
        var fixture = AssemblyHooks.Fixture;
        var preflight = await RunPreflightAsync(repositoryRoot, fixture, smoke, phase).ConfigureAwait(false);
        var resolvedProductionRevision = await ResolveRevisionAsync(
            repositoryRoot,
            Environment.GetEnvironmentVariable("HVO_EVIDENCE_PRODUCTION_REVISION") ?? ProductionRevision)
            .ConfigureAwait(false);
        var source = await CaptureSourceAsync(repositoryRoot).ConfigureAwait(false);
        await ValidateSourceAsync(
            repositoryRoot, phase, smoke, resolvedProductionRevision, source).ConfigureAwait(false);
        var lockContract = AuthenticateObjectLockContract(repositoryRoot);
        var schemaCapabilities = await ValidateSchemaCapabilitiesAsync(fixture, phase).ConfigureAwait(false);
        var payloads = CreatePayloads(smoke);
        using var w0GenerationTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var w0Fixture = await CreateW0FixtureAsync(repositoryRoot, w0GenerationTimeout.Token).ConfigureAwait(false);
        var harnessSha256 = ComputeHarnessSha256(repositoryRoot);
        var compatibilityWorkloadSha256 = ComputeCompatibilityWorkloadSha256(w0Fixture.Generator);
        var workloadSha256 = smoke
            ? ComputeActualWorkloadSha256(payloads, w0Fixture.Generator, compatibilityWorkloadSha256)
            : ComputeWorkloadSha256(w0Fixture.Generator);
        var protocolSha256 = ComputeProtocolSha256();
        var compatibilityProtocolSha256 = ComputeCompatibilityProtocolSha256();
        var environmentSha256 = ComputeEnvironmentSha256(preflight, gcDynamicAdaptationMode);
        var baselineBinding = await ValidateBaselineBindingAsync(
            repositoryRoot,
            phase,
            compatibilityWorkloadSha256,
            compatibilityProtocolSha256,
            environmentSha256).ConfigureAwait(false);
        var rawMinio = fixture.Factory.Services.GetRequiredService<IMinioClient>();
        await EnsureBucketAsync(rawMinio).ConfigureAwait(false);
        var privateValues = new Issue250PrivateValues(
            fixture.SqlServerConnectionString,
            repositoryRoot,
            IntegrationTestFixture.MinioAccessKey,
            IntegrationTestFixture.MinioSecretKey);

        var warmups = smoke ? 0 : 5;
        var measurements = smoke ? 1 : 30;
        var steadyState = new List<Issue250ReleaseScenarioEvidence>();
        foreach (var concurrency in new[] { 1, 4 })
        {
            steadyState.Add(await RunReleaseScenarioAsync(
                fixture,
                rawMinio,
                payloads,
                phase,
                concurrency,
                warmups,
                measurements,
                privateValues).ConfigureAwait(false));
        }

        var delayed = new List<Issue250DelayEvidence>();
        foreach (var delay in smoke ? SmokeDelays : CanonicalDelays)
        {
            delayed.Add(await RunDelayScenarioAsync(
                fixture,
                rawMinio,
                payloads,
                phase,
                delay,
                privateValues).ConfigureAwait(false));
        }
        var w0 = await RunW0FaultAndRuntimeAsync(
            fixture, rawMinio, w0Fixture, phase, privateValues).ConfigureAwait(false);
        var w3m = await RunW3MAsync(fixture, rawMinio, smoke, privateValues).ConfigureAwait(false);
        var sourceAfter = await CaptureSourceAsync(repositoryRoot).ConfigureAwait(false);
        AssertSourceUnchanged(source, sourceAfter);
        var trial = source.Trial ?? 1;
        var output = issue268Evidence
            ? ResolveIssue268OutputPath(trial)
            : smoke
            ? Path.Combine(repositoryRoot, "TestResults", "issue-250",
                afterSmoke ? "after-capability-smoke" : "development-smoke",
                source.OutputDirectoryName, source.RunId)
            : Path.Combine(repositoryRoot, "TestResults", "issue-250", source.OutputDirectoryName, $"trial-{trial}");
        if (Directory.Exists(output))
        {
            throw new IOException("Issue #250 evidence output collision; existing evidence is never overwritten or deleted.");
        }
        Directory.CreateDirectory(output);
        var normalReleaseCaseCount = 2 * (warmups + measurements) + delayed.Count * 5;
        var releaseCaseCount = normalReleaseCaseCount + 3;
        var objectBytesPerCase = payloads.TotalBytesPerCase;
        var heldBytesPerCase = payloads.HeldBytesPerCase;
        var setupObjectBytes = checked(
            normalReleaseCaseCount * checked(2 * objectBytesPerCase + heldBytesPerCase)
            + 8L * 6_144
            + 2L * W3MSentinelByteLength);
        var putPayloadBytes = checked(
            normalReleaseCaseCount * objectBytesPerCase + 4L * 6_144 + W3MSentinelByteLength);
        var preReleaseChecksumReadBytes = checked(
            normalReleaseCaseCount * objectBytesPerCase + 4L * 6_144 + W3MSentinelByteLength);
        var postReleaseHeldReadBytes = checked(normalReleaseCaseCount * heldBytesPerCase);
        var downloadPayloadBytes = checked(preReleaseChecksumReadBytes + postReleaseHeldReadBytes);
        var maximumStagedObjectBytes = Math.Max(
            checked(Math.Max(measurements, 4) * objectBytesPerCase),
            Math.Max(2L * 6_144, W3MSentinelByteLength));
        var maximumSharedManagedPayloadBufferBytes = checked(
            payloads.Source.Bytes.LongLength + payloads.Preview.Bytes.LongLength + payloads.Overlay.Bytes.LongLength
            + 6_144L + 1L + 1L);
        var maximumEstimatedLivePayloadBytes = checked(
            maximumStagedObjectBytes + maximumSharedManagedPayloadBufferBytes);
        Assert.AreEqual(setupObjectBytes, checked(putPayloadBytes + downloadPayloadBytes));

        var evidence = new
        {
            Schema = "hvo-issue-250-central-transient-payload-release-evidence-v5",
            Issue = 250,
            Phase = phase,
            HarnessSha256 = harnessSha256,
            WorkloadSha256 = workloadSha256,
            CompatibilityWorkloadSha256 = compatibilityWorkloadSha256,
            ProtocolSha256 = protocolSha256,
            CompatibilityProtocolSha256 = compatibilityProtocolSha256,
            EnvironmentSha256 = environmentSha256,
            TrialPolicy,
            BaselineBinding = baselineBinding,
            EvidencePolicy = new
            {
                Trial = trial,
                TrialPolicy,
                AuthorizedTrials = new[] { 1 },
                RequiredTrialCount = 1,
                TrialAuthority = "Issue #250 readiness authorizes HVO_EVIDENCE_TRIAL=1; general five-trial reporting guidance does not override this issue-specific protocol.",
                SourceFingerprintScope = "Git revision, dirty diff, harness/production/support assemblies, and selected source files are fingerprinted before work and before write.",
                SourceFingerprintLimitation = "Fingerprints prove byte identity of the recorded inputs; they do not independently prove compiler reproducibility, container image contents, or host firmware.",
                Privacy = "Raw credentials, connection strings, repository paths, object keys, database names, actor identities, idempotency keys, and record identifiers are excluded or represented only by SHA-256. SQL and plan preimages are parameterized and scrubbed before serialization."
            },
            Smoke = new
            {
                Enabled = smoke,
                Claimable = !smoke && phase is "baseline" or "after",
                Reason = smoke
                    ? "Reduced payloads, one measured release per concurrency level, zero/250 ms contention probes, and fewer than ten W3M parents are mechanics-only."
                    : "N/A: canonical workload was selected."
            },
            ProductionRevision = resolvedProductionRevision,
            Source = PublicSource(source),
            SourceValidation = new
            {
                BeforeWorkloadSha256 = SourceSnapshotSha256(source),
                BeforeEvidenceWriteSha256 = SourceSnapshotSha256(sourceAfter),
                Identical = true,
                Checks = "Revision, branch, dirty diff, claimability, trial, and exact assembly snapshots were validated before workload and re-captured unchanged before write."
            },
            Environment = new
            {
                Framework = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Configuration = "Release",
                ServerGc = GCSettings.IsServerGC,
                GcDynamicAdaptationMode = gcDynamicAdaptationMode,
                CpuCount = Environment.ProcessorCount,
                TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                PinnedSdk = ReadPinnedSdk(repositoryRoot),
                SqlServerImage = IntegrationTestFixture.SqlServerImage,
                MinioImage = IntegrationTestFixture.MinioImage,
                Topology = "Direct production service instances over isolated SQL databases and shared testcontainer MinIO; HVO_ISSUE_250_EVIDENCE suppresses unrelated recurring workers before host startup.",
                Preflight = preflight,
                EnvironmentSha256 = environmentSha256
            },
            Command = "HVO_ISSUE_250_EVIDENCE=1 DOTNET_gcServer=1 [DOTNET_GCDynamicAdaptationMode=0 for issue #268] HVO_EVIDENCE_PHASE=<baseline|after> HVO_EVIDENCE_REVISION=<HEAD> HVO_EVIDENCE_PRODUCTION_REVISION=<PRODUCTION_HEAD> HVO_EVIDENCE_TRIAL=1 HVO_EVIDENCE_BASELINE_MANIFEST=<ACCEPTED_BASELINE_MANIFEST> [HVO_ISSUE_268_SYNTHETIC_EVIDENCE=1 HVO_ISSUE_268_OUTPUT_ROOT=<PERSISTENT_ROOT> | HVO_ISSUE_250_SMOKE=1 HVO_ISSUE_250_AFTER_SMOKE=1] dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --no-build --configuration Release --filter FullyQualifiedName~CentralTransientPayloadReleaseIssue250PerformanceTests.Release_W2W3MAndContention_RecordsEvidence",
            Workload = new
            {
                Id = smoke ? "issue-250-smoke-unclaimable" : "W2/W3M/issue-250-baseline-v1",
                CandidateObjectsPerRelease = 7,
                ReleasedObjectsPerRelease = 5,
                PreservedObjectsPerRelease = 2,
                Sources = new
                {
                    Count = 5,
                    OrderedTemporalPositions = new[] { "NMinus2", "NMinus1", "N", "NPlus1", "NPlus2" },
                    Width = smoke ? 8 : FullWidth,
                    Height = smoke ? 4 : FullHeight,
                    PixelFormat = "Bayer16 little endian",
                    ByteLength = payloads.Source.Bytes.LongLength,
                    Sha256 = payloads.Source.Sha256,
                    CanonicalFullByteLength = SourceBytes,
                    CanonicalFullSha256 = SourceSha256,
                    Generator = "ushort((64 + 2025 + 31*x + 17*y + 997*((y&1)*2+(x&1))) & 0x3FFF), little endian"
                },
                Derivatives = new[]
                {
                    new
                    {
                        Kind = "Preview",
                        Role = "Preview",
                        ByteLength = payloads.Preview.Bytes.LongLength,
                        Sha256 = payloads.Preview.Sha256,
                        CanonicalFullByteLength = DerivativeBytes,
                        CanonicalFullSha256 = DerivativePreviewSha256,
                        Generator = "RGB24: (13*x+7*y+11, 3*x+17*y+29, 19*x+5*y+47) modulo 256"
                    },
                    new
                    {
                        Kind = "Overlay",
                        Role = "AnnotatedPreview",
                        ByteLength = payloads.Overlay.Bytes.LongLength,
                        Sha256 = payloads.Overlay.Sha256,
                        CanonicalFullByteLength = DerivativeBytes,
                        CanonicalFullSha256 = DerivativeOverlaySha256,
                        Generator = "RGB24: (x xor y xor 0x5A, 11*x+23*y+71, 29*x+2*y+101) modulo 256"
                    }
                },
                Holds = "Source ordinal 0 and derivative ordinal 6 have independent current-publication holds. Baseline excludes them before item creation.",
                DerivativeFixture = "issue-250-two-output-release-fixture-v1 is a trigger/constraint-valid custom release fixture with exactly Preview and Overlay intents/records. It is intentionally not a committed canonical five-output transient writer bundle and tests payload release only. Kind, role, media type, storage pattern, checksum, and output identity are frozen.",
                WarmupsPerConcurrency = warmups,
                MeasuredReleasesPerConcurrency = measurements,
                ConcurrencyLevels = new[] { 1, 4 },
                ArrivalModel = "Closed-loop distinct releases. All measured cases are staged before one continuous measured window; execution is sequential at C1 and in waves of four at C4. Verification and held-object cleanup follow the window.",
                Delay = new
                {
                    ReleasesPerDelay = 4,
                    Concurrency = 4,
                    DelaysMilliseconds = smoke ? SmokeDelays : CanonicalDelays,
                    SqlSamplingTargetMilliseconds = 10,
                    ExactWriterDeadlineMilliseconds = 1_000,
                    PublicFlow = "All four natural-delay releases and the separate exact-writer mechanism release enter through public ReleaseAsync with real request/item creation. No release parent/item is manually reserved.",
                    ContentionProbe = "The exact writer uses a separate public-flow release so baseline serialization does not create a harness-induced deadlock with the four-release natural latency wave."
                },
                W3MCompletedParents = w3m.CompletedParents,
                W3MItemsPerCompletedParent = 7,
                W3MOldestPendingParents = 1,
                W3MHistoryArtifacts = w3m.Topology.HistoryArtifacts,
                W3MDistinctHistoryTargetRecordIds = w3m.Topology.DistinctTerminalItemTargetRecordIds,
                W3MSharedHistoryFrames = w3m.Topology.HistoryFrames,
                W3MTotalFramesIncludingSentinel = w3m.Topology.TotalFrames,
                W3MSentinel = new
                {
                    w3m.SentinelGenerator,
                    w3m.SentinelExpectedByteLength,
                    w3m.SentinelExpectedSha256
                },
                W0 = new
                {
                    Width = 64,
                    Height = 48,
                    PixelFormat = "Mono16 little endian",
                    Generator = w0Fixture.Generator,
                    BytesPerObject = 6_144,
                    OneObjectFaults = new[] { "object-absent", "before-delete-failure" },
                    PriorOrdinalRestartObjects = 2,
                    PriorOrdinalReason = "Two W0-sized object instances are the minimum production-valid fixture needed to prove one committed ordinal and one pending later ordinal."
                }
            },
            SetupIo = new
            {
                ReleaseCasesIncludingWarmupsNaturalDelayContentionAndW0 = releaseCaseCount,
                NormalSevenObjectReleaseCases = normalReleaseCaseCount,
                W0ReleaseCases = 3,
                W0PayloadObjectInstances = 4,
                ObjectBytesPerSevenObjectCase = objectBytesPerCase,
                HeldBytesVerifiedAfterReleasePerCase = heldBytesPerCase,
                TotalPutAndChecksumReadAndPostHoldVerificationBytes = setupObjectBytes,
                TotalObjectPayloadTransferBytes = setupObjectBytes,
                NetworkUploadPayloadBytes = putPayloadBytes,
                NetworkDownloadPayloadBytes = downloadPayloadBytes,
                PutPayloadBytes = putPayloadBytes,
                PreReleaseChecksumReadBytes = preReleaseChecksumReadBytes,
                PostReleaseHeldVerificationBytes = postReleaseHeldReadBytes,
                PutOperations = checked(normalReleaseCaseCount * 7 + 5),
                PreReleaseStatOperations = checked(normalReleaseCaseCount * 7 + 5),
                PreReleaseGetOperations = checked(normalReleaseCaseCount * 7 + 5),
                PostReleaseAbsentStatOperations = checked(normalReleaseCaseCount * 5 + 5),
                PostReleaseHeldStatOperations = checked(normalReleaseCaseCount * 2),
                PostReleaseHeldGetOperations = checked(normalReleaseCaseCount * 2),
                W3MSentinelPostReleaseHeldGetOperations = 0,
                CleanupDeleteOperations = checked(normalReleaseCaseCount * 9 + 5),
                CleanupDeleteOperationScope = "Two held-object removals plus seven aggregate-finally removals per normal case, four W0 finally removals, and one W3M finally removal; removing an already absent key remains one issued cleanup operation.",
                FaultInjectionDeleteOperations = 1,
                ProductionReleaseDeleteOperations = checked(normalReleaseCaseCount * 5 + 6 + 1),
                ProductionReleaseDeleteOperationScope = "Normal release cases contribute five each; W0 contributes six logical RemoveObjectAsync boundaries; W3M contributes one. Provider HTTP retries are measured separately, not inferred.",
                MaximumStagedObjectBytes = maximumStagedObjectBytes,
                MaximumSharedManagedPayloadBufferBytes = maximumSharedManagedPayloadBufferBytes,
                MaximumEstimatedLivePayloadBytes = maximumEstimatedLivePayloadBytes,
                W3MProcessorSentinelBytes = W3MSentinelByteLength,
                W3MSentinelSetup = "One 16-byte PUT, one raw-client pre-release STAT, one raw-client pre-release checksum GET with 16 response payload bytes, zero post-hold GETs, and one raw-client post-release absence STAT.",
                NetworkPayloadDirectionScope = "Uploads count PUT request bodies. Downloads count pre-release checksum and preserved-held GET response bodies; STAT, DELETE, SQL, and protocol overhead carry no modeled object payload bytes.",
                Scope = "Setup PUT, pre-release STAT/GET checksum, post-release absence STAT, post-release held-object STAT/GET, and cleanup are excluded from measured CPU/allocation/RSS/wall/protocol windows."
            },
            Method = new
            {
                Boundary = "CentralTransientPayloadReleaseService.ReleaseAsync through durable parent/item completion and MinIO DELETE.",
                Latency = "Nearest-rank median/p95/p99/maximum over independent releases and DELETE item requests; warmups and all setup/checksum/cleanup work are excluded.",
                Resources = "One continuous process time series per scenario targets 10 ms and records Process.TotalProcessorTime, window-relative GC.GetTotalAllocatedBytes(true), and WorkingSet64. Issue #268 baseline/after runs disable .NET 10 Server GC DATAS because heap retirement can regress the allocation counter; observed precise-GC regressions still fail closed. Raw 100 ms System.Runtime alloc-rate increments, exact GC deltas, normalized agreement or explicit short-window unclaimability, observed p50/maximum cadence, RSS peak, and first/last sample uncertainty are reported.",
                Sql = "Measured service DbContexts use EF command/transaction interceptors. Dedicated sp_getapplock/sp_releaseapplock commands bypass EF and are not inferred. DMV sampling targets 10 ms and reports observed p50/maximum cadence and per-DELETE-window coverage. Baseline row blocking requires an exact waiting/granted KEY-resource match on CentralArtifacts.PK_CentralArtifacts, the release transaction/session/database/isolation attribution, and distinct dedicated application-lock fence sessions. Zero-delay lock duration is explicitly unclaimable when cadence cannot resolve it.",
                ObjectStore = "The service MinIO client is isolated behind a request/DELETE duration and entity-byte collector; seed and correctness GET/STAT traffic uses a separate client.",
                W3M = "Trigger/constraint-preserving setup uses one shared history frame and deterministic batches of at most 250 parents, reports setup timing separately, single-thread rebuilds the measured queue index to remove setup-transition ghost records, runs UPDATE STATISTICS FULLSCAN, drops session temp tables, and captures actual STATISTICS XML/IO over the normalized production pending-parent query. Logical reads describe normalized post-maintenance synthetic state, not production queue aging."
            },
            SteadyState = steadyState,
            DelayedDelete = delayed,
            W3MFile = "w3m-normalized-plan-evidence.json",
            FaultRuntimeFile = "w0-fault-runtime-evidence.json",
            SchemaCapabilities = schemaCapabilities,
            ObjectLockContract = lockContract,
            Correctness = new
            {
                ExactPreReleaseLengthAndSha256ForAllSeven = steadyState.All(item => item.Correctness.ExactPreReleaseObjects)
                    && delayed.All(item => item.NaturalCorrectness.ExactPreReleaseObjects
                        && item.ContentionCorrectness.ExactPreReleaseObjects),
                FiveAbsentTwoPreservedPerRelease = steadyState.All(item => item.Correctness.FiveAbsentTwoPreserved)
                    && delayed.All(item => item.NaturalCorrectness.FiveAbsentTwoPreserved
                        && item.ContentionCorrectness.FiveAbsentTwoPreserved),
                SqlSourceAndIntentStatesExact = steadyState.All(item => item.Correctness.SqlStatesExact)
                    && delayed.All(item => item.NaturalCorrectness.SqlStatesExact
                        && item.ContentionCorrectness.SqlStatesExact),
                ParentItemOutcomesAndOrdinalsExact = steadyState.All(item => item.Correctness.ParentItemsExact)
                    && delayed.All(item => item.NaturalCorrectness.ParentItemsExact
                        && item.ContentionCorrectness.ParentItemsExact),
                ImmutableEventReviewAuditStable = steadyState.All(item => item.Correctness.ImmutableRowsStable)
                    && delayed.All(item => item.NaturalCorrectness.ImmutableRowsStable
                        && item.ContentionCorrectness.ImmutableRowsStable),
                ExactReplay = steadyState.All(item => item.Correctness.ExactReplay)
                    && delayed.All(item => item.NaturalCorrectness.ExactReplay
                        && item.ContentionCorrectness.ExactReplay),
                NoUnresolvedReleaseBacklog = steadyState.All(item => item.Correctness.FinalPendingParents == 0
                        && item.Correctness.FinalPendingItems == 0)
                    && delayed.All(item => item.NaturalCorrectness.FinalPendingParents == 0
                        && item.NaturalCorrectness.FinalPendingItems == 0
                        && item.ContentionCorrectness.FinalPendingParents == 0
                        && item.ContentionCorrectness.FinalPendingItems == 0),
                BaselineHeldNormalization = "The seven-object universe includes both held objects. At baseline they remain available and verified but intentionally have no release-item row; five candidate rows are Released.",
                W3MProcessor = "One raw-client length/SHA256-verified oldest pending sentinel is selected, deleted, and completed by actual production ProcessNextAsync; 10,000 completed parents each retain seven terminal metadata items targeting 70,000 distinct expired artifacts on one valid shared history frame."
            },
            Interpretation = "Correctness and source identity are pass/fail. Timing, process resources, SQL reads/plans, and contention are container-environment observations for equivalent baseline/after comparison, not physical deployment claims.",
            RecordedAtUtc = DateTimeOffset.UtcNow
        };

        var serialized = JsonSerializer.SerializeToUtf8Bytes(evidence, JsonOptions);
        AssertEvidenceSafe(serialized, source, privateValues);
        var evidencePath = Path.Combine(output, "central-transient-payload-release-evidence.json");
        await EvidenceSourceIdentity.WriteJsonAsync(evidencePath, evidence, JsonOptions).ConfigureAwait(false);
        var evidenceBytes = await File.ReadAllBytesAsync(evidencePath).ConfigureAwait(false);
        CollectionAssert.AreEqual(serialized, evidenceBytes);
        var w0Envelope = new
        {
            Schema = "hvo-issue-250-w0-fault-runtime-evidence-v1",
            Issue = 250,
            Phase = phase,
            SourceHead = source.Head,
            HarnessSha256 = harnessSha256,
            WorkloadSha256 = workloadSha256,
            CompatibilityWorkloadSha256 = compatibilityWorkloadSha256,
            ProtocolSha256 = protocolSha256,
            CompatibilityProtocolSha256 = compatibilityProtocolSha256,
            EnvironmentSha256 = environmentSha256,
            TrialPolicy,
            Evidence = w0
        };
        var w0Bytes = JsonSerializer.SerializeToUtf8Bytes(w0Envelope, JsonOptions);
        AssertEvidenceSafe(w0Bytes, source, privateValues);
        var w0Path = Path.Combine(output, "w0-fault-runtime-evidence.json");
        await EvidenceSourceIdentity.WriteJsonAsync(w0Path, w0Envelope, JsonOptions).ConfigureAwait(false);
        var planEnvelope = new
        {
            Schema = "hvo-issue-250-w3m-normalized-plan-evidence-v5",
            Issue = 250,
            Phase = phase,
            SourceHead = source.Head,
            HarnessSha256 = harnessSha256,
            WorkloadSha256 = workloadSha256,
            CompatibilityWorkloadSha256 = compatibilityWorkloadSha256,
            ProtocolSha256 = protocolSha256,
            CompatibilityProtocolSha256 = compatibilityProtocolSha256,
            EnvironmentSha256 = environmentSha256,
            TrialPolicy,
            Evidence = w3m
        };
        var planBytes = JsonSerializer.SerializeToUtf8Bytes(planEnvelope, JsonOptions);
        AssertEvidenceSafe(planBytes, source, privateValues);
        var planPath = Path.Combine(output, "w3m-normalized-plan-evidence.json");
        await EvidenceSourceIdentity.WriteJsonAsync(planPath, planEnvelope, JsonOptions).ConfigureAwait(false);
        var manifest = new
        {
            Schema = "hvo-evidence-manifest-v1",
            Issue = 250,
            Phase = phase,
            SourceHead = source.Head,
            ProductionRevision = resolvedProductionRevision,
            Trial = trial,
            TrialPolicy,
            HarnessSha256 = harnessSha256,
            WorkloadSha256 = workloadSha256,
            CompatibilityWorkloadSha256 = compatibilityWorkloadSha256,
            ProtocolSha256 = protocolSha256,
            CompatibilityProtocolSha256 = compatibilityProtocolSha256,
            EnvironmentSha256 = environmentSha256,
            Files = new[]
            {
                DescribeEvidenceFile(evidencePath),
                DescribeEvidenceFile(w0Path),
                DescribeEvidenceFile(planPath)
            }
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        AssertEvidenceSafe(manifestBytes, source, privateValues);
        await EvidenceSourceIdentity.WriteJsonAsync(
            Path.Combine(output, "manifest.json"), manifest, JsonOptions).ConfigureAwait(false);
        TestContext.WriteLine("issue250 evidence: {0}", Path.GetRelativePath(repositoryRoot, output));
    }

    private static async Task<Issue250ReleaseScenarioEvidence> RunReleaseScenarioAsync(
        IntegrationTestFixture fixture,
        IMinioClient rawMinio,
        Issue250Payloads payloads,
        string phase,
        int concurrency,
        int warmups,
        int measurements,
        Issue250PrivateValues privateValues)
    {
        await using var database = CreateDatabase(fixture, $"C{concurrency}");
        var keys = new List<string>();
        using var scenarioTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellationToken = scenarioTimeout.Token;
        try
        {
            await database.Context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            var applicationName = $"HVO.SkyMonitor.Issue250.C{concurrency}.{Guid.NewGuid():N}";
            using var collector = new Issue250EvidenceCollector();
            collector.SqlLocks.Configure(database.ConnectionString, applicationName);
            using var serviceHttp = new HttpClient(collector.Http, disposeHandler: false);
            var serviceMinio = CreateMinio(fixture, serviceHttp);
            collector.Http.Configure(TimeSpan.Zero);
            var sequence = 0;
            foreach (var waveCount in WaveCounts(warmups, concurrency))
            {
                var cases = await SeedWaveAsync(
                    database.ConnectionString, rawMinio, payloads, waveCount, sequence, keys, privateValues,
                    cancellationToken)
                    .ConfigureAwait(false);
                sequence += waveCount;
                _ = await ExecuteReleaseWaveAsync(
                    database.ConnectionString, applicationName, serviceMinio, collector, cases, measure: false,
                    externalCancellation: cancellationToken)
                    .ConfigureAwait(false);
                foreach (var item in cases)
                {
                    _ = await VerifyReleaseCaseAsync(
                        database.ConnectionString, rawMinio, serviceMinio, item, phase,
                        cancellationToken).ConfigureAwait(false);
                }
                await CleanupHeldObjectsAsync(rawMinio, cases, cancellationToken).ConfigureAwait(false);
            }

            var measuredCases = await SeedWaveAsync(
                database.ConnectionString, rawMinio, payloads, measurements, sequence, keys, privateValues,
                cancellationToken)
                .ConfigureAwait(false);
            collector.Reset();
            var measured = await MeasureReleaseScenarioAsync(
                database.ConnectionString,
                applicationName,
                serviceMinio,
                collector,
                measuredCases,
                concurrency,
                cancellationToken).ConfigureAwait(false);
            var correctness = new List<Issue250CaseCorrectness>();
            foreach (var item in measuredCases)
            {
                correctness.Add(await VerifyReleaseCaseAsync(
                    database.ConnectionString, rawMinio, serviceMinio, item, phase,
                    cancellationToken).ConfigureAwait(false));
            }
            await CleanupHeldObjectsAsync(rawMinio, measuredCases, cancellationToken).ConfigureAwait(false);

            var protocol = collector.Snapshot();
            AssertProtocolAccounting(protocol, measurements, phase);
            if (phase == "after")
            {
                AssertNoNaturalLeaseRecovery(protocol);
            }
            Assert.AreEqual(
                (long)measurements * 5 + protocol.RecoveryProcessorCompletions,
                protocol.ObjectStore.Deletes);
            var finalBacklog = await ReadBacklogAsync(database.ConnectionString, cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0L, finalBacklog.PendingParents);
            Assert.AreEqual(0L, finalBacklog.PendingItems);
            return CreateReleaseScenarioEvidence(
                concurrency,
                warmups,
                measurements,
                measured.ReleaseLatenciesMilliseconds,
                measured.WallMilliseconds,
                [measured.Resources],
                protocol,
                correctness,
                finalBacklog,
                payloads.ReleasedBytesPerCase);
        }
        finally
        {
            await CleanupScenarioAsync(rawMinio, keys, database.Context).ConfigureAwait(false);
        }
    }

    private static async Task<Issue250DelayEvidence> RunDelayScenarioAsync(
        IntegrationTestFixture fixture,
        IMinioClient rawMinio,
        Issue250Payloads payloads,
        string phase,
        int delayMilliseconds,
        Issue250PrivateValues privateValues)
    {
        await using var database = CreateDatabase(fixture, $"Delay{delayMilliseconds}");
        var keys = new List<string>();
        using var scenarioTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var scenarioCancellation = scenarioTimeout.Token;
        try
        {
            await database.Context.Database.MigrateAsync(scenarioCancellation).ConfigureAwait(false);
            var applicationName = $"HVO.SkyMonitor.Issue250.Delay{delayMilliseconds}.{Guid.NewGuid():N}";
            using var collector = new Issue250EvidenceCollector();
            collector.SqlLocks.Configure(database.ConnectionString, applicationName);
            using var serviceHttp = new HttpClient(collector.Http, disposeHandler: false);
            var serviceMinio = CreateMinio(fixture, serviceHttp);
            var cases = await SeedWaveAsync(
                database.ConnectionString, rawMinio, payloads, 4, 0, keys, privateValues,
                scenarioCancellation).ConfigureAwait(false);
            collector.Reset();
            var origin = Stopwatch.GetTimestamp();
            collector.Http.Configure(
                TimeSpan.FromMilliseconds(delayMilliseconds), origin, CreateDeleteCaseMap(cases));
            using var sampleCancellation = CancellationTokenSource.CreateLinkedTokenSource(scenarioCancellation);
            var sampleTask = SampleSqlAsync(
                database.ConnectionString, applicationName, origin, cases, null, sampleCancellation.Token);
            Issue250MeasuredWave? measured = null;
            IReadOnlyList<Issue250SqlSample> naturalSamples;
            try
            {
                measured = await MeasureReleaseWaveAsync(
                    database.ConnectionString,
                    applicationName,
                    serviceMinio,
                    collector,
                    cases,
                    scenarioCancellation).ConfigureAwait(false);
            }
            finally
            {
                await sampleCancellation.CancelAsync().ConfigureAwait(false);
                naturalSamples = await sampleTask.ConfigureAwait(false);
            }
            Assert.IsNotNull(measured);
            var naturalProtocol = collector.Snapshot();
            AssertProtocolAccounting(naturalProtocol, 4, phase);
            if (phase == "after")
            {
                AssertNoNaturalLeaseRecovery(naturalProtocol);
            }
            var naturalSql = CreateSqlEvidence(
                naturalSamples, collector.Http.Windows, delayMilliseconds, phase, contentionProbe: false);
            var correctness = new List<Issue250CaseCorrectness>();
            foreach (var item in cases)
            {
                correctness.Add(await VerifyReleaseCaseAsync(
                    database.ConnectionString, rawMinio, serviceMinio, item, phase,
                    scenarioCancellation).ConfigureAwait(false));
            }
            await CleanupHeldObjectsAsync(rawMinio, cases, scenarioCancellation).ConfigureAwait(false);

            var contentionCase = (await SeedWaveAsync(
                database.ConnectionString, rawMinio, payloads, 1, 10_000 + delayMilliseconds, keys, privateValues,
                scenarioCancellation)
                .ConfigureAwait(false)).Single();
            collector.Reset();
            var contentionApplicationName = applicationName + ".Contention";
            collector.SqlLocks.Configure(database.ConnectionString, contentionApplicationName);
            var contentionOrigin = Stopwatch.GetTimestamp();
            collector.Http.Configure(
                TimeSpan.FromMilliseconds(delayMilliseconds), contentionOrigin,
                CreateDeleteCaseMap([contentionCase]));
            using var contentionCancellation = CancellationTokenSource.CreateLinkedTokenSource(scenarioCancellation);
            contentionCancellation.CancelAfter(TimeSpan.FromMinutes(3));
            using var contentionSampleCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                contentionCancellation.Token);
            Task<IReadOnlyList<Issue250SqlSample>> contentionSamplesTask =
                Task.FromResult<IReadOnlyList<Issue250SqlSample>>([]);
            Task<double[]>? releaseTask = null;
            Task<Issue250WriterEvidence>? writerTask = null;
            try
            {
                releaseTask = ExecuteReleaseWaveAsync(
                    database.ConnectionString,
                    contentionApplicationName,
                    serviceMinio,
                    collector,
                    [contentionCase],
                    measure: true,
                    externalCancellation: contentionCancellation.Token,
                    recoverPending: phase == "after");
                await collector.Http.FirstEntered.WaitAsync(
                    TimeSpan.FromSeconds(30), contentionCancellation.Token).ConfigureAwait(false);
                var firstKey = collector.Http.FirstObjectKey
                    ?? throw new InvalidOperationException("The delayed DELETE did not expose its bounded test key.");
                var writerRecordId = contentionCase.Objects.Single(item => item.Key == firstKey).RecordId;
                contentionSamplesTask = SampleSqlAsync(
                    database.ConnectionString,
                    contentionApplicationName,
                    contentionOrigin,
                    [contentionCase],
                    writerRecordId,
                    contentionSampleCancellation.Token);
                writerTask = ExecuteWriterAsync(
                    database.ConnectionString,
                    contentionApplicationName,
                    writerRecordId,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                    contentionCancellation.Token);
                await Task.WhenAll(releaseTask, writerTask).ConfigureAwait(false);
            }
            finally
            {
                await contentionSampleCancellation.CancelAsync().ConfigureAwait(false);
                var unfinished = new Task?[] { releaseTask, writerTask }
                    .Where(static task => task is { IsCompleted: false }).Cast<Task>().ToArray();
                if (unfinished.Length > 0)
                {
                    await contentionCancellation.CancelAsync().ConfigureAwait(false);
                    await EvidenceTaskCleanup.DrainAsync(
                        unfinished, contentionCancellation, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }
            }
            var contentionSamples = await contentionSamplesTask.ConfigureAwait(false);
            Assert.IsNotNull(writerTask);
            var writer = await writerTask.ConfigureAwait(false);
            var contentionProtocol = collector.Snapshot();
            AssertProtocolAccounting(contentionProtocol, 1, phase);
            var contentionSql = CreateSqlEvidence(
                contentionSamples, collector.Http.Windows, delayMilliseconds, phase, contentionProbe: true);
            var contentionCorrectness = await VerifyReleaseCaseAsync(
                database.ConnectionString, rawMinio, serviceMinio, contentionCase, phase,
                scenarioCancellation).ConfigureAwait(false);
            await CleanupHeldObjectsAsync(rawMinio, [contentionCase], scenarioCancellation).ConfigureAwait(false);
            var finalBacklog = await ReadBacklogAsync(database.ConnectionString, scenarioCancellation).ConfigureAwait(false);
            Assert.AreEqual(0L, finalBacklog.PendingParents);
            Assert.AreEqual(0L, finalBacklog.PendingItems);
            if (phase == "baseline" && delayMilliseconds == 2_000)
            {
                Assert.AreEqual("timeout", writer.Outcome);
                Assert.AreEqual(writer.TargetRecordIdSha256, contentionSql.ExactRowBlockTargetRecordIdSha256);
                Assert.IsGreaterThanOrEqualTo(delayMilliseconds - 100d, contentionSql.TransactionOverlapMilliseconds);
                Assert.IsGreaterThanOrEqualTo(delayMilliseconds - 100d, contentionSql.ObjectLockWindowMilliseconds);
            }
            if (phase == "baseline" && delayMilliseconds == 250)
            {
                Assert.AreEqual("completed", writer.Outcome);
                Assert.AreEqual(writer.TargetRecordIdSha256, contentionSql.ExactRowBlockTargetRecordIdSha256);
                Assert.IsLessThan(1_000d, writer.ElapsedMilliseconds);
            }
            if (phase == "development" && delayMilliseconds >= 250)
            {
                Assert.AreEqual(writer.TargetRecordIdSha256, contentionSql.ExactRowBlockTargetRecordIdSha256);
            }
            if (phase == "after" && delayMilliseconds >= 250)
            {
                Assert.AreEqual("completed", writer.Outcome);
                Assert.IsLessThan(1_000d, writer.ElapsedMilliseconds);
            }
            return new Issue250DelayEvidence(
                delayMilliseconds,
                4,
                measured!.WallMilliseconds,
                Latency(measured.ReleaseLatenciesMilliseconds),
                CreateResourceAggregate([measured.Resources]),
                CreateProtocolEvidence(naturalProtocol),
                naturalSql,
                CreateProtocolEvidence(contentionProtocol),
                contentionSql,
                writer,
                CreateCorrectnessEvidence(correctness, finalBacklog),
                CreateCorrectnessEvidence([contentionCorrectness], finalBacklog));
        }
        finally
        {
            await CleanupScenarioAsync(rawMinio, keys, database.Context).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<Issue250ReleaseCase>> SeedWaveAsync(
        string connectionString,
        IMinioClient minio,
        Issue250Payloads payloads,
        int count,
        int sequence,
        List<string> keys,
        Issue250PrivateValues privateValues,
        CancellationToken cancellationToken = default)
    {
        var cases = new List<Issue250ReleaseCase>(count);
        for (var index = 0; index < count; index++)
        {
            var seeded = await SeedReleaseCaseAsync(
                connectionString, minio, payloads, sequence + index, keys, privateValues,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var item in seeded.Objects)
            {
                privateValues.Add(item.Key);
            }
            cases.Add(seeded);
        }
        return cases;
    }

    private static async Task<Issue250ReleaseCase> SeedReleaseCaseAsync(
        string connectionString,
        IMinioClient minio,
        Issue250Payloads payloads,
        int sequence,
        List<string> cleanupKeys,
        Issue250PrivateValues privateValues,
        int w0CandidateCount = 0,
        CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext(connectionString);
        var token = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.AddTicks(sequence * 10L);
        var eventRecord = new CentralTransientEventRecord
        {
            AgentId = $"issue-250-agent-{token}",
            EventId = Guid.NewGuid(),
            EventCreatedUtc = now
        };
        var frames = new List<CentralFrame>();
        var sourceObjects = new List<Issue250Object>();
        for (var ordinal = 0; ordinal < 5; ordinal++)
        {
            var key = $"issue-250/{token}/source-{ordinal}.bin";
            var frame = new CentralFrame
            {
                RegistrationId = Guid.NewGuid(),
                DevicePublicId = Guid.NewGuid(),
                ObservatoryId = Guid.NewGuid(),
                AgentId = eventRecord.AgentId,
                FrameId = Guid.NewGuid(),
                CapturedAtUtc = now.AddSeconds(-10 + ordinal),
                FirstReceivedAtUtc = now.AddSeconds(-9 + ordinal),
                CaptureSequence = sequence * 10L + ordinal
            };
            var artifactIdempotency = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray()));
            privateValues.Add(artifactIdempotency);
            var artifact = new CentralArtifact
            {
                CentralFrameId = frame.Id,
                Frame = frame,
                ArtifactId = Guid.NewGuid(),
                DevicePublicId = frame.DevicePublicId,
                Role = FrameArtifactRole.Raw,
                RecipeVersion = "issue-250-w2-v1",
                ManifestSchemaVersion = "evidence-v1",
                MediaType = "application/x-hvo-frame",
                ByteLength = payloads.Source.Bytes.LongLength,
                ChecksumSha256 = payloads.Source.Sha256,
                StorageReference = BucketPrefix + key,
                ReceivedAtUtc = now,
                IdempotencyKey = artifactIdempotency,
                Variant = $"w2-{ordinal}",
                CreatedUtc = now,
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete,
                ObjectVerifiedAtUtc = now
            };
            frame.Artifacts.Add(artifact);
            frames.Add(frame);
            sourceObjects.Add(new Issue250Object(
                "Source", ordinal, key, artifact.Id, artifact.ArtifactId, payloads.Source, ordinal == 0));
        }

        var observationId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        var observation = new CentralTransientObservationRecord
        {
            ObservationId = observationId,
            CentralTransientEventId = eventRecord.Id,
            Event = eventRecord,
            DetectorInputIdentitySha256 = Sha($"detector-{token}"),
            CalibrationIdentity = "issue-250-calibration-v1",
            MaskIdentity = "issue-250-mask-v1",
            ProcessingProfileIdentity = "issue-250-profile-v1",
            ExtractionProducerSchemaVersion = "issue-250-extraction-v1",
            ExtractionProducerKind = TransientExtractionProducerKind.DeterministicAlgorithm,
            ExtractionProducerName = "issue-250-baseline",
            ExtractionProducerVersion = "v1",
            ExtractionRecipeIdentitySha256 = Sha("issue-250-extraction-recipe"),
            ExtractionReceiptIdentitySha256 = Sha($"receipt-{token}"),
            GeometryJson = "{}",
            FeaturesJson = "{}",
            SourceReferenceId = observationId
        };
        var center = sourceObjects[2];
        observation.Source = new CentralTransientObservationSourceReference
        {
            ObservationId = observationId,
            CentralArtifactId = center.RecordId,
            Artifact = frames[2].Artifacts.Single(),
            EvidenceSchemaVersion = TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
            EvidenceId = evidenceId,
            LocatorSchemaVersion = TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
            LocatorKind = TransientSourceLocatorKind.WholeArtifact,
            ArtifactId = center.ArtifactId,
            ArtifactRole = FrameArtifactRole.Raw,
            ArtifactVariant = "w2-2",
            ArtifactRecipeIdentitySha256 = Sha("issue-250-source-recipe"),
            ArtifactChecksumSha256 = payloads.Source.Sha256,
            ObservationStartedUtc = now.AddSeconds(-8),
            ObservationEndedUtc = now.AddSeconds(-7),
            TimingQuality = TransientTimingQuality.Reported,
            TimingProvenanceSource = "issue-250",
            TimingProvenanceVersion = "v1"
        };
        foreach (var (source, ordinal) in sourceObjects.Where(item => item.WorkloadOrdinal != 2).Select((item, index) => (item, index)))
        {
            observation.Backgrounds.Add(new CentralTransientObservationBackgroundReference
            {
                ObservationId = observationId,
                Observation = observation,
                Ordinal = ordinal,
                CentralArtifactId = source.RecordId,
                Artifact = frames[source.WorkloadOrdinal].Artifacts.Single(),
                ArtifactId = source.ArtifactId,
                ArtifactRole = FrameArtifactRole.Raw,
                ArtifactVariant = $"w2-{source.WorkloadOrdinal}",
                ArtifactRecipeIdentitySha256 = Sha("issue-250-source-recipe"),
                ArtifactChecksumSha256 = payloads.Source.Sha256
            });
        }
        eventRecord.Observations.Add(observation);

        var assessment = new CentralTransientAssessmentRecord
        {
            AssessmentId = Guid.NewGuid(),
            CentralTransientEventId = eventRecord.Id,
            Event = eventRecord,
            CreatedUtc = now.AddSeconds(-5),
            Authority = TransientAssessmentAuthority.Authoritative,
            Classification = TransientClassification.Meteor,
            MeteorSeverity = TransientMeteorSeverity.Meteor,
            ConfidenceMillionths = 900_000,
            ProducerSchemaVersion = "issue-250-assessment-v1",
            ProducerKind = TransientAssessmentProducerKind.DeterministicAlgorithm,
            ProducerName = "issue-250-baseline",
            ProducerVersion = "v1",
            RecipeIdentitySha256 = Sha("issue-250-assessment-recipe"),
            ReceiptSchemaVersion = "issue-250-assessment-receipt-v1",
            ExecutionIdentitySha256 = Sha($"execution-{token}"),
            OptionsIdentitySha256 = Sha("issue-250-assessment-options"),
            CanonicalReceiptJson = "{}",
            CanonicalReceiptSha256 = Sha("{}"),
            CanonicalReceiptByteLength = 2
        };
        assessment.EvidenceObservations.Add(new CentralTransientAssessmentObservation
        {
            CentralTransientEventId = eventRecord.Id,
            AssessmentId = assessment.AssessmentId,
            Assessment = assessment,
            Ordinal = 0,
            ObservationId = observationId,
            Observation = observation
        });
        eventRecord.Assessments.Add(assessment);
        var reviewActor = $"issue-250-reviewer-{token}";
        var review = new CentralTransientReviewRecord
        {
            ReviewId = Guid.NewGuid(),
            CentralTransientEventId = eventRecord.Id,
            Event = eventRecord,
            CreatedUtc = now.AddSeconds(-2),
            ReviewerIdentity = reviewActor,
            Disposition = TransientReviewDisposition.Confirmed,
            AssessmentId = assessment.AssessmentId,
            Assessment = assessment,
            ReasonCodesJson = "[\"human.release-approved\"]"
        };
        eventRecord.Reviews.Add(review);
        privateValues.Add(reviewActor);

        var version1 = CreateVersion(eventRecord, 1, now.AddSeconds(-4), null, null);
        var version2 = CreateVersion(eventRecord, 2, now.AddSeconds(-1), version1.EventVersionId, version1.VersionCreatedUtc);
        version1.Observations.Add(new CentralTransientEventVersionObservation
        {
            CentralTransientEventId = eventRecord.Id,
            EventVersionId = version1.EventVersionId,
            EventVersion = version1,
            Ordinal = 0,
            ObservationId = observationId,
            Observation = observation
        });
        version1.Assessments.Add(new CentralTransientEventVersionAssessment
        {
            CentralTransientEventId = eventRecord.Id,
            EventVersionId = version1.EventVersionId,
            EventVersion = version1,
            Ordinal = 0,
            AssessmentId = assessment.AssessmentId,
            Assessment = assessment
        });
        version2.Observations.Add(new CentralTransientEventVersionObservation
        {
            CentralTransientEventId = eventRecord.Id,
            EventVersionId = version2.EventVersionId,
            EventVersion = version2,
            Ordinal = 0,
            ObservationId = observationId,
            Observation = observation
        });
        version2.Assessments.Add(new CentralTransientEventVersionAssessment
        {
            CentralTransientEventId = eventRecord.Id,
            EventVersionId = version2.EventVersionId,
            EventVersion = version2,
            Ordinal = 0,
            AssessmentId = assessment.AssessmentId,
            Assessment = assessment
        });
        version2.Reviews.Add(new CentralTransientEventVersionReview
        {
            CentralTransientEventId = eventRecord.Id,
            EventVersionId = version2.EventVersionId,
            EventVersion = version2,
            Ordinal = 0,
            ReviewId = review.ReviewId,
            Review = review
        });
        eventRecord.Versions.Add(version1);
        eventRecord.Versions.Add(version2);
        eventRecord.Current = new CentralTransientEventCurrent
        {
            CentralTransientEventId = eventRecord.Id,
            Event = eventRecord,
            LatestEventVersionId = version2.EventVersionId,
            LatestEventVersion = version2,
            LatestVersion = 2,
            ActiveAssessmentId = assessment.AssessmentId,
            ActiveAssessment = assessment,
            LatestReviewId = review.ReviewId,
            LatestReview = review,
            ReviewState = CentralTransientReviewState.Reviewed,
            EffectiveClassification = TransientClassification.Meteor,
            EffectiveMeteorSeverity = TransientMeteorSeverity.Meteor,
            EffectiveConfidenceMillionths = 900_000,
            UpdatedUtc = now
        };

        var parentJob = new CentralDerivativeJob
        {
            SourceCentralArtifactId = center.RecordId,
            SourceArtifact = frames[2].Artifacts.Single(),
            TargetRole = FrameArtifactRole.Metadata,
            TargetRecipeVersion = TwoOutputFixtureName,
            TargetVariant = "event-derivatives",
            RecipeName = TwoOutputFixtureName,
            RecipeOptionsJson = "{}",
            InputSelectorJson = "{}",
            RequestedRecipeIdentitySha256 = Sha("issue-250-derivative-recipe"),
            ExpectedRecipeIdentitySha256 = Sha("issue-250-derivative-recipe"),
            RequestIdentitySha256 = Sha($"derivative-request-{token}"),
            Status = CentralDerivativeJobStatus.Completed,
            MaxAttempts = 3,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CompletedAtUtc = now
        };
        var derivativeJob = new CentralTransientDerivativeJob
        {
            CentralDerivativeJobId = parentJob.Id,
            Job = parentJob,
            CentralTransientEventId = eventRecord.Id,
            Event = eventRecord,
            SourceEventVersionId = version2.EventVersionId,
            SourceEventVersion = version2,
            RequestIdentitySha256 = parentJob.RequestIdentitySha256,
            ProducerSchemaVersion = "issue-250-derivative-producer-v1",
            ProducerName = "issue-250-rgb24",
            ProducerVersion = "v1",
            RecipeIdentitySha256 = parentJob.RequestedRecipeIdentitySha256,
            OptionsIdentitySha256 = Sha("issue-250-derivative-options"),
            CanonicalRequestJson = "{}",
            CanonicalRequestSha256 = Sha("{}"),
            CanonicalRequestByteLength = 2,
            ExpectedOutputCount = 5,
            CreatedAtUtc = now
        };
        var derivativeObjects = new List<Issue250Object>();
        var derivativeDefinitions = new[]
        {
            (Kind: TransientDerivativeKind.Preview, Role: FrameArtifactRole.Preview, Variant: "rgb24-preview", Payload: payloads.Preview),
            (Kind: TransientDerivativeKind.Overlay, Role: FrameArtifactRole.AnnotatedPreview, Variant: "rgb24-overlay", Payload: payloads.Overlay)
        };
        for (var index = 0; index < derivativeDefinitions.Length; index++)
        {
            var definition = derivativeDefinitions[index];
            var derivativeId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var key = $"issue-250/{token}/derivative-{index}.rgb";
            var storageReference = BucketPrefix + key;
            var intent = new CentralTransientDerivativeOutputIntent
            {
                CentralDerivativeJobId = parentJob.Id,
                DerivativeJob = derivativeJob,
                CentralTransientEventId = eventRecord.Id,
                Kind = definition.Kind,
                DerivativeId = derivativeId,
                ArtifactId = artifactId,
                ArtifactRole = definition.Role,
                ArtifactVariant = definition.Variant,
                MediaType = "application/x-rgb24",
                ByteLength = definition.Payload.Bytes.LongLength,
                ChecksumSha256 = definition.Payload.Sha256,
                OutputIdentitySha256 = Sha($"{TwoOutputFixtureName}:{definition.Kind}"),
                StorageReference = storageReference,
                StorageETag = "issue-250-fixture-etag",
                ObjectState = CentralArtifactObjectState.Available,
                CreatedAtUtc = now,
                ObjectVerifiedAtUtc = now
            };
            derivativeJob.OutputIntents.Add(intent);
            var derivative = new CentralTransientDerivativeRecord
            {
                DerivativeId = derivativeId,
                CentralTransientEventId = eventRecord.Id,
                Event = eventRecord,
                SourceEventVersionId = version2.EventVersionId,
                SourceEventVersion = version2,
                CentralDerivativeJobId = parentJob.Id,
                DerivativeJob = derivativeJob,
                OutputIntentId = intent.Id,
                OutputIntent = intent,
                CreatedUtc = now,
                Kind = definition.Kind,
                ArtifactId = artifactId,
                ArtifactRole = definition.Role,
                ArtifactVariant = definition.Variant,
                MediaType = "application/x-rgb24",
                ByteLength = definition.Payload.Bytes.LongLength,
                ArtifactChecksumSha256 = definition.Payload.Sha256,
                RecipeIdentitySha256 = derivativeJob.RecipeIdentitySha256,
                OptionsIdentitySha256 = derivativeJob.OptionsIdentitySha256,
                OutputIdentitySha256 = intent.OutputIdentitySha256,
                LimitationsJson = "[]",
                AssessmentId = assessment.AssessmentId,
                ReviewId = review.ReviewId
            };
            version2.Derivatives.Add(new CentralTransientEventVersionDerivative
            {
                CentralTransientEventId = eventRecord.Id,
                EventVersionId = version2.EventVersionId,
                EventVersion = version2,
                Ordinal = index,
                DerivativeId = derivativeId,
                Derivative = derivative
            });
            db.CentralTransientDerivatives.Add(derivative);
            derivativeObjects.Add(new Issue250Object(
                "Derivative", 5 + index, key, intent.Id, artifactId, definition.Payload, index == 1));
        }

        var authority = new Observatory
        {
            OwnerUserId = $"issue-250-owner-{token}",
            Name = "Issue 250 evidence authority",
            TimeZoneId = "UTC",
            CreatedAtUtc = now,
            IsActive = true
        };
        var publicationActor = $"issue-250-publication-{token}";
        privateValues.Add(publicationActor);
        var publicationDecisions = new List<PublicRecordPublicationDecision>();
        foreach (var source in sourceObjects.Where(item => w0CandidateCount > 0
                     ? item.WorkloadOrdinal < 2 || item.WorkloadOrdinal >= 2 + w0CandidateCount
                     : item.WorkloadOrdinal == 0))
        {
            publicationDecisions.Add(new()
            {
                AuthorityObservatoryId = authority.Id,
                AuthorityObservatory = authority,
                SubjectKind = PublicRecordSubjectKind.Artifact,
                State = PublicationDecisionState.Released,
                CentralArtifactId = source.RecordId,
                CentralArtifact = frames[source.WorkloadOrdinal].Artifacts.Single(),
                ProjectionSchemaVersion = "issue-250-hold-v1",
                OccurredAtUtc = now,
                ActorUserId = publicationActor,
                ReasonCode = "issue-250-independent-hold"
            });
        }
        foreach (var derivative in w0CandidateCount > 0 ? version2.Derivatives : version2.Derivatives.Skip(1))
        {
            publicationDecisions.Add(new PublicRecordPublicationDecision
            {
                AuthorityObservatoryId = authority.Id,
                AuthorityObservatory = authority,
                SubjectKind = PublicRecordSubjectKind.TransientDerivative,
                State = PublicationDecisionState.Released,
                CentralTransientDerivativeId = derivative.DerivativeId,
                CentralTransientDerivative = derivative.Derivative,
                ProjectionSchemaVersion = "issue-250-hold-v1",
                OccurredAtUtc = now,
                ActorUserId = publicationActor,
                ReasonCode = "issue-250-independent-hold"
            });
        }
        db.PublicRecordPublicationDecisions.AddRange(publicationDecisions);
        db.CentralFrames.AddRange(frames);
        db.CentralTransientEvents.Add(eventRecord);
        db.CentralDerivativeJobs.Add(parentJob);
        db.CentralTransientDerivativeJobs.Add(derivativeJob);
        db.Observatories.Add(authority);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var reviewMutationKey = $"issue-250-review-{token}";
        privateValues.Add(reviewMutationKey);
        db.CentralTransientReviewMutations.Add(new CentralTransientReviewMutationRecord
        {
            CentralTransientEventId = eventRecord.Id,
            ActorIdentity = reviewActor,
            IdempotencyKey = reviewMutationKey,
            CanonicalRequestSha256 = Sha("issue-250-reviewed"),
            PreviousEventVersionId = version1.EventVersionId,
            ResultEventVersionId = version2.EventVersionId,
            ResultReviewId = review.ReviewId,
            ResultRowVersion = eventRecord.Current.RowVersion.ToArray(),
            RecordedAtUtc = now
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var releaseActor = $"issue-250-release-{token}";
        var releaseKey = $"issue-250-release-key-{token}";
        privateValues.Add(token);
        privateValues.Add(eventRecord.AgentId);
        privateValues.Add(authority.OwnerUserId);
        privateValues.Add(releaseActor);
        privateValues.Add(releaseKey);

        var objects = (w0CandidateCount > 0
                ? sourceObjects.Where(item => item.WorkloadOrdinal >= 2
                    && item.WorkloadOrdinal < 2 + w0CandidateCount)
                : sourceObjects.Concat(derivativeObjects))
            .OrderBy(item => item.WorkloadOrdinal).ToArray();
        await AssertFixtureTopologyAsync(db, eventRecord.Id, objects, payloads, cancellationToken).ConfigureAwait(false);
        foreach (var item in objects)
        {
            cleanupKeys.Add(item.Key);
            privateValues.Add(item.Key);
            await PutObjectAsync(minio, item, cancellationToken).ConfigureAwait(false);
            await VerifyPresentAsync(minio, item, cancellationToken).ConfigureAwait(false);
        }
        db.ChangeTracker.Clear();
        var current = await db.CentralTransientEventCurrent.AsNoTracking()
            .SingleAsync(item => item.CentralTransientEventId == eventRecord.Id, cancellationToken).ConfigureAwait(false);
        var immutable = await ReadImmutableSnapshotAsync(db, eventRecord.Id, cancellationToken).ConfigureAwait(false);
        var targets = await ReadTargetSnapshotsAsync(db, objects, cancellationToken).ConfigureAwait(false);
        var holds = await ReadHoldSnapshotAsync(db, eventRecord.Id, objects, cancellationToken).ConfigureAwait(false);
        return new Issue250ReleaseCase(
            eventRecord.Id,
            current.RowVersion,
            releaseActor,
            releaseKey,
            objects,
            immutable,
            targets,
            holds,
            w0CandidateCount > 0 ? $"W0-{w0CandidateCount}-ordinal" : "W2-seven-object");
    }

    private static async Task AssertFixtureTopologyAsync(
        ApplicationDbContext db,
        Guid eventId,
        IReadOnlyList<Issue250Object> objects,
        Issue250Payloads payloads,
        CancellationToken cancellationToken)
    {
        var intents = await db.CentralTransientDerivativeOutputIntents.AsNoTracking()
            .Where(item => item.CentralTransientEventId == eventId)
            .Select(item => new
            {
                item.Id,
                item.Kind,
                item.ArtifactRole,
                item.ArtifactVariant,
                item.MediaType,
                item.ByteLength,
                item.ChecksumSha256,
                item.OutputIdentitySha256,
                item.StorageReference,
                item.CommittedAtUtc
            })
            .OrderBy(item => item.Kind)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        Assert.HasCount(2, intents);
        CollectionAssert.AreEquivalent(
            ExpectedDerivativeOutputKinds,
            intents.Select(item => item.Kind.ToString()).ToArray());
        Assert.AreEqual(2, intents.Count(item => item.StorageReference.StartsWith(BucketPrefix, StringComparison.Ordinal)));
        Assert.AreEqual(2, intents.Select(item => item.StorageReference).Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(intents.All(item => item.CommittedAtUtc is null));
        var preview = intents.Single(item => item.Kind == TransientDerivativeKind.Preview);
        Assert.AreEqual(FrameArtifactRole.Preview, preview.ArtifactRole);
        Assert.AreEqual("rgb24-preview", preview.ArtifactVariant);
        Assert.AreEqual("application/x-rgb24", preview.MediaType);
        Assert.AreEqual(payloads.Preview.Bytes.LongLength, preview.ByteLength);
        Assert.AreEqual(payloads.Preview.Sha256, preview.ChecksumSha256);
        Assert.AreEqual(Sha($"{TwoOutputFixtureName}:Preview"), preview.OutputIdentitySha256);
        var overlay = intents.Single(item => item.Kind == TransientDerivativeKind.Overlay);
        Assert.AreEqual(FrameArtifactRole.AnnotatedPreview, overlay.ArtifactRole);
        Assert.AreEqual("rgb24-overlay", overlay.ArtifactVariant);
        Assert.AreEqual("application/x-rgb24", overlay.MediaType);
        Assert.AreEqual(payloads.Overlay.Bytes.LongLength, overlay.ByteLength);
        Assert.AreEqual(payloads.Overlay.Sha256, overlay.ChecksumSha256);
        Assert.AreEqual(Sha($"{TwoOutputFixtureName}:Overlay"), overlay.OutputIdentitySha256);
        var bundle = await db.CentralTransientDerivativeJobs.AsNoTracking()
            .Where(item => item.CentralTransientEventId == eventId)
            .Select(item => new { item.ExpectedOutputCount, item.CommittedAtUtc })
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(5, bundle.ExpectedOutputCount,
            "The production schema hard-codes five only for committed canonical bundles.");
        Assert.IsNull(bundle.CommittedAtUtc,
            "The issue-specific two-output release fixture must not claim canonical writer commit conformance.");

        var selectedDerivativeIds = await db.CentralTransientDerivatives.AsNoTracking()
            .Where(item => item.CentralTransientEventId == eventId)
            .Where(item => !db.PublicRecordPublicationDecisions.Any(decision =>
                decision.CentralTransientDerivativeId == item.DerivativeId
                && decision.State == PublicationDecisionState.Released
                && !db.PublicRecordPublicationDecisions.Any(successor =>
                    successor.SupersedesDecisionId == decision.Id)))
            .Select(item => item.OutputIntentId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(
            objects.Where(item => item.Kind == "Derivative" && !item.Held)
                .Select(item => item.RecordId).ToArray(),
            selectedDerivativeIds);
        Assert.AreEqual(2, await db.CentralTransientDerivatives.AsNoTracking()
            .CountAsync(item => item.CentralTransientEventId == eventId, cancellationToken)
            .ConfigureAwait(false));
    }

    private static CentralTransientEventVersionRecord CreateVersion(
        CentralTransientEventRecord eventRecord,
        int version,
        DateTimeOffset createdUtc,
        Guid? previousId,
        DateTimeOffset? previousUtc)
    {
        var json = $"{{\"schema\":\"issue-250-event-v1\",\"version\":{version}}}";
        return new CentralTransientEventVersionRecord
        {
            EventVersionId = Guid.NewGuid(),
            CentralTransientEventId = eventRecord.Id,
            Event = eventRecord,
            Version = version,
            PreviousVersionNumber = version == 1 ? null : version - 1,
            PreviousEventVersionId = previousId,
            PreviousVersionCreatedUtc = previousUtc,
            State = TransientEventState.Validated,
            VersionCreatedUtc = createdUtc,
            FirstObservedUtc = createdUtc.AddSeconds(-2),
            LastObservedUtc = createdUtc.AddSeconds(-1),
            SchemaVersion = "issue-250-event-v1",
            CanonicalEventJson = json,
            CanonicalEventSha256 = Sha(json),
            CanonicalEventByteLength = Encoding.UTF8.GetByteCount(json)
        };
    }

    private static async Task<Issue250MeasuredWave> MeasureReleaseWaveAsync(
        string connectionString,
        string applicationName,
        IMinioClient minio,
        Issue250EvidenceCollector collector,
        IReadOnlyList<Issue250ReleaseCase> cases,
        CancellationToken cancellationToken)
        => await MeasureReleaseOperationAsync(async () => await ExecuteReleaseWaveAsync(
            connectionString, applicationName, minio, collector, cases, measure: true,
            externalCancellation: cancellationToken)
            .ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

    private static async Task<Issue250MeasuredWave> MeasureReleaseScenarioAsync(
        string connectionString,
        string applicationName,
        IMinioClient minio,
        Issue250EvidenceCollector collector,
        IReadOnlyList<Issue250ReleaseCase> cases,
        int concurrency,
        CancellationToken cancellationToken)
        => await MeasureReleaseOperationAsync(async () =>
        {
            var latencies = new List<double>(cases.Count);
            for (var offset = 0; offset < cases.Count; offset += concurrency)
            {
                var wave = cases.Skip(offset).Take(concurrency).ToArray();
                latencies.AddRange(await ExecuteReleaseWaveAsync(
                    connectionString, applicationName, minio, collector, wave, measure: true,
                    externalCancellation: cancellationToken)
                    .ConfigureAwait(false));
            }
            return latencies.ToArray();
        }, cancellationToken).ConfigureAwait(false);

    private static async Task<Issue250MeasuredWave> MeasureReleaseOperationAsync(
        Func<Task<double[]>> operation,
        CancellationToken cancellationToken)
    {
        using var sampledAllocations = new Issue170AllocationSampler();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var allocationStart = GC.GetTotalAllocatedBytes(precise: true);
        var rssStart = process.WorkingSet64;
        var samples = new ConcurrentQueue<Issue250ProcessSample>();
        sampledAllocations.Start();
        var started = Stopwatch.GetTimestamp();
        samples.Enqueue(new(0, cpuStart.TotalMilliseconds, 0, rssStart));
        using var sampleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sampleTask = SampleProcessAsync(started, allocationStart, samples, sampleCancellation.Token);
        double[] latencies;
        TimeSpan wall;
        double cpuMilliseconds;
        long allocatedBytes;
        long rssEnd;
        try
        {
            latencies = await operation().ConfigureAwait(false);
            wall = Stopwatch.GetElapsedTime(started);
            process.Refresh();
            cpuMilliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
            var allocationEnd = GC.GetTotalAllocatedBytes(precise: true);
            allocatedBytes = RequireMonotonicAllocationTotal(allocationStart, allocationEnd) - allocationStart;
            rssEnd = process.WorkingSet64;
        }
        finally
        {
            await sampleCancellation.CancelAsync().ConfigureAwait(false);
        }
        await sampleTask.ConfigureAwait(false);
        var sampledAllocation = await sampledAllocations.StopAsync().ConfigureAwait(false);
        process.Refresh();
        var finalElapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var finalAllocationTotal = GC.GetTotalAllocatedBytes(precise: true);
        samples.Enqueue(new(
            finalElapsed,
            process.TotalProcessorTime.TotalMilliseconds,
            RequireMonotonicAllocationTotal(allocationStart, finalAllocationTotal) - allocationStart,
            process.WorkingSet64));
        var series = samples.OrderBy(item => item.ElapsedMilliseconds).ToArray();
        Assert.IsGreaterThanOrEqualTo(2, series.Length);
        if (series.Zip(series.Skip(1),
                (first, second) => second.AllocatedBytesSinceWindowStart >= first.AllocatedBytesSinceWindowStart)
            .Any(monotonic => !monotonic))
        {
            throw new InvalidDataException("GC.GetTotalAllocatedBytes(true) decreased during the measured window.");
        }
        var intervals = series.Zip(series.Skip(1),
            (first, second) => second.ElapsedMilliseconds - first.ElapsedMilliseconds).ToArray();
        var orderedIntervals = intervals.Order().ToArray();
        var effectiveInterval = orderedIntervals.Length == 0 ? 0 : Percentile(orderedIntervals, 0.50);
        var maximumInterval = orderedIntervals.Length == 0 ? 0 : orderedIntervals[^1];
        var exactAllocationRate = allocatedBytes / Math.Max(0.001, wall.TotalSeconds);
        var allocationDifference = Math.Abs(allocatedBytes - sampledAllocation.SampledBytes);
        var allocationDifferenceRatio = allocationDifference
            / (double)Math.Max(1, Math.Max(allocatedBytes, sampledAllocation.SampledBytes));
        var allocationBoundaryUncertaintyRatio =
            2d * sampledAllocation.IntervalMilliseconds / Math.Max(1d, wall.TotalMilliseconds);
        var allocationAgreementThreshold = allocationBoundaryUncertaintyRatio + 0.10d;
        var allocationCrossCheckClaimable = allocationAgreementThreshold < 1d;
        var allocationAgreement = allocationCrossCheckClaimable
            && allocationDifferenceRatio <= allocationAgreementThreshold;
        if (allocationCrossCheckClaimable)
        {
            Assert.IsTrue(allocationAgreement,
                "System.Runtime alloc-rate and exact GC allocation deltas disagree beyond the normalized two-boundary uncertainty.");
        }
        return new Issue250MeasuredWave(
            latencies,
            wall.TotalMilliseconds,
            new Issue250ResourceEvidence(
                cpuMilliseconds,
                allocatedBytes,
                series.Length,
                10,
                effectiveInterval,
                maximumInterval,
                exactAllocationRate,
                new Issue250AllocationEvidence(
                    sampledAllocation.SampledBytes,
                    sampledAllocation.Samples,
                    sampledAllocation.IntervalMilliseconds,
                    allocationDifference,
                    allocationDifferenceRatio,
                    allocationBoundaryUncertaintyRatio,
                    allocationCrossCheckClaimable,
                    allocationCrossCheckClaimable
                        ? "Claimable: normalized two-boundary uncertainty plus tolerance is below 100%."
                        : "Unclaimable: normalized two-boundary uncertainty plus tolerance reaches or exceeds 100%.",
                    allocationAgreement,
                    sampledAllocation.RawSamples),
                rssStart,
                series.Max(item => item.RssBytes),
                rssEnd,
                series[0].ElapsedMilliseconds,
                Math.Abs(finalElapsed - wall.TotalMilliseconds),
                series));
    }

    private static async Task SampleProcessAsync(
        long origin,
        long allocationStart,
        ConcurrentQueue<Issue250ProcessSample> samples,
        CancellationToken cancellationToken)
    {
        using var process = Process.GetCurrentProcess();
        var next = TimeSpan.FromMilliseconds(10);
        var previousAllocationTotal = allocationStart;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var remaining = next - Stopwatch.GetElapsedTime(origin);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }
                process.Refresh();
                var allocationTotal = GC.GetTotalAllocatedBytes(precise: true);
                RequireMonotonicAllocationTotal(previousAllocationTotal, allocationTotal);
                samples.Enqueue(new(
                    Stopwatch.GetElapsedTime(origin).TotalMilliseconds,
                    process.TotalProcessorTime.TotalMilliseconds,
                    allocationTotal - allocationStart,
                    process.WorkingSet64));
                previousAllocationTotal = allocationTotal;
                next += TimeSpan.FromMilliseconds(10);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static long RequireMonotonicAllocationTotal(long previous, long current)
    {
        if (current < previous)
        {
            throw new InvalidDataException("GC.GetTotalAllocatedBytes(true) decreased during the measured window.");
        }
        return current;
    }

    private static async Task<double[]> ExecuteReleaseWaveAsync(
        string connectionString,
        string applicationName,
        IMinioClient minio,
        Issue250EvidenceCollector collector,
        IReadOnlyList<Issue250ReleaseCase> cases,
        bool measure,
        bool recoverPending = false,
        CancellationToken externalCancellation = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token, externalCancellation);
        var tasks = cases.Select(async (item, caseIndex) =>
        {
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                var retryStage = Issue250SqlRetryStage.Primary;
                collector.RecordReleaseAttempt();
                await using var db = CreateContext(
                    connectionString, CreateCaseApplicationName(applicationName, caseIndex), collector);
                using var telemetry = new CentralTransientLifecycleTelemetry(Issue250DiscardLogger.Instance);
                try
                {
                    var result = await CreateService(db, minio, telemetry).ReleaseAsync(
                        CreatePrincipal(item.Actor),
                        item.EventId,
                        item.RowVersion,
                        item.IdempotencyKey,
                        cancellation.Token).ConfigureAwait(false);
                    Assert.IsTrue(result.Status is CentralTransientPayloadReleaseStatus.Released
                        or CentralTransientPayloadReleaseStatus.Accepted);
                    Assert.IsNotNull(result.Response);
                    var publicReleaseElapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (result.Status == CentralTransientPayloadReleaseStatus.Released)
                    {
                        Assert.AreEqual(CentralTransientPayloadReleaseState.Completed, result.Response.State);
                        Assert.AreEqual(5, result.Response.ReleasedPayloadCount);
                        return measure ? publicReleaseElapsed : 0d;
                    }

                    if (result.Status == CentralTransientPayloadReleaseStatus.Accepted)
                    {
                        if (!recoverPending)
                        {
                            throw new InvalidDataException(
                                "Steady-state and natural-delay release unexpectedly required lease recovery.");
                        }
                        collector.RecordAcceptedReleaseResponse();
                        Assert.AreEqual(CentralTransientPayloadReleaseState.Pending, result.Response.State);
                        CentralTransientPayloadRelease? recovered = null;
                        for (var processorAttempt = 0; processorAttempt < 100; processorAttempt++)
                        {
                            retryStage = Issue250SqlRetryStage.Recovery;
                            collector.RecordRecoveryProcessorAttempt();
                            await using var recoveryDb = CreateContext(
                                connectionString, CreateCaseApplicationName(applicationName, caseIndex), collector,
                                recoveryProtocol: true);
                            using var recoveryTelemetry = new CentralTransientLifecycleTelemetry(Issue250DiscardLogger.Instance);
                            var processed = await CreateService(recoveryDb, minio, recoveryTelemetry)
                                .ProcessNextAsync(cancellation.Token).ConfigureAwait(false);
                            if (processed)
                            {
                                collector.RecordRecoveryProcessorCompletion();
                            }
                            retryStage = Issue250SqlRetryStage.Inspection;
                            await using var recoveryInspectDb = CreateContext(connectionString);
                            recovered = await recoveryInspectDb.CentralTransientPayloadReleases.AsNoTracking()
                                .Include(value => value.Items)
                                .SingleAsync(value => value.ReleaseId == result.Response.ReleaseId, cancellation.Token)
                                .ConfigureAwait(false);
                            if (recovered.State != CentralTransientPayloadReleaseState.Pending)
                            {
                                break;
                            }
                            var retryAtUtc = recovered.Items.Where(value => value.RetryAtUtc.HasValue)
                                .Select(value => value.RetryAtUtc!.Value).DefaultIfEmpty().Min();
                            var reservationReadyUtc = recovered.Items.Where(value => value.ReservationToken.HasValue)
                                .Select(value => value.RequestedAtUtc!.Value.AddMinutes(1)).DefaultIfEmpty().Min();
                            var readyUtc = new[] { retryAtUtc, reservationReadyUtc }
                                .Where(value => value > DateTimeOffset.UtcNow).DefaultIfEmpty().Min();
                            var delay = readyUtc > DateTimeOffset.UtcNow
                                ? readyUtc - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(50)
                                : TimeSpan.FromMilliseconds(recoverPending ? 100 : 10);
                            await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
                        }
                        Assert.IsNotNull(recovered);
                        Assert.AreEqual(
                            CentralTransientPayloadReleaseState.Completed,
                            recovered.State,
                            string.Join(",", recovered.Items.OrderBy(value => value.Ordinal).Select(value =>
                                $"{value.Ordinal}:{value.Outcome}:retry={value.RetryCount}:reserved={value.ReservationToken.HasValue}")));
                        Assert.AreEqual(5, recovered.Items.Count(value =>
                            value.Outcome == CentralTransientPayloadReleaseItemOutcome.Released));
                        Assert.AreEqual(2, recovered.Items.Count(value =>
                            value.Outcome == CentralTransientPayloadReleaseItemOutcome.PreservedHeld));
                        var recoveredElapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        return measure ? recoveredElapsed : 0d;
                    }
                    throw new InvalidOperationException("The release returned an unsupported measured status.");
                }
                catch (Exception exception) when (IsSqlDeadlock(exception))
                {
                    collector.RecordExternalDeadlockRetry(retryStage);
                    throw new InvalidOperationException(
                        "A SQL deadlock escaped the public payload-release operation.", exception);
                }
            }
        }).ToArray();
        try
        {
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await EvidenceTaskCleanup.DrainAsync(tasks, cancellation, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsSqlDeadlock(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SqlException { Number: 1205 })
            {
                return true;
            }
            if (current.InnerException is null)
            {
                break;
            }
        }
        return false;
    }

    private static async Task<Issue250CaseCorrectness> VerifyReleaseCaseAsync(
        string connectionString,
        IMinioClient rawMinio,
        IMinioClient serviceMinio,
        Issue250ReleaseCase releaseCase,
        string phase,
        CancellationToken cancellationToken)
    {
        var absent = 0;
        var preserved = 0;
        foreach (var item in releaseCase.Objects)
        {
            if (item.Held)
            {
                await VerifyPresentAsync(rawMinio, item, cancellationToken).ConfigureAwait(false);
                preserved++;
            }
            else
            {
                await AssertAbsentAsync(rawMinio, item.Key, cancellationToken).ConfigureAwait(false);
                absent++;
            }
        }
        var expectedAbsent = releaseCase.Objects.Count(item => !item.Held);
        var expectedPreserved = releaseCase.Objects.Count(item => item.Held);
        Assert.AreEqual(expectedAbsent, absent);
        Assert.AreEqual(expectedPreserved, preserved);

        await using var db = CreateContext(connectionString);
        var release = await db.CentralTransientPayloadReleases.AsNoTracking()
            .Include(item => item.Items)
            .SingleAsync(item => item.CentralTransientEventId == releaseCase.EventId, cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(CentralTransientPayloadReleaseState.Completed, release.State);
        var expectedRequestSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            "central-transient-payload-release-v1",
            releaseCase.EventId.ToString("N")))));
        Assert.AreEqual(releaseCase.EventId, release.CentralTransientEventId);
        Assert.AreEqual(releaseCase.Actor, release.ActorIdentity);
        Assert.AreEqual(releaseCase.IdempotencyKey, release.IdempotencyKey);
        Assert.AreEqual(expectedRequestSha, release.CanonicalRequestSha256);
        var items = release.Items.OrderBy(item => item.Ordinal).ToArray();
        var workloadIds = releaseCase.Objects.Select(item => item.RecordId).ToHashSet();
        var baselineIds = releaseCase.Objects.Where(item => !item.Held).Select(item => item.RecordId).ToHashSet();
        var actualIds = items.Select(item => item.RecordId).ToHashSet();
        var w0 = releaseCase.Workload.StartsWith("W0-", StringComparison.Ordinal);
        var expectedBaselineItems = w0 ? releaseCase.Objects.Count : 5;
        const int expectedAfterItems = 7;
        var baselineRepresentation = items.Length == expectedBaselineItems && actualIds.SetEquals(baselineIds)
            && items.All(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Released);
        var candidateRepresentation = items.Length == expectedAfterItems
            && (w0 ? actualIds.IsSupersetOf(workloadIds) : actualIds.SetEquals(workloadIds))
            && items.Count(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Released) == expectedAbsent
            && items.Count(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.PreservedHeld)
                == (w0 ? expectedAfterItems - expectedAbsent : expectedPreserved);
        if (phase == "after")
        {
            Assert.IsTrue(candidateRepresentation,
                "After evidence requires exactly seven terminal rows: five Released and two PreservedHeld.");
        }
        else
        {
            Assert.IsTrue(baselineRepresentation,
                "Baseline/development evidence requires exactly the current five Released item rows.");
        }
        CollectionAssert.AreEqual(Enumerable.Range(0, items.Length).ToArray(), items.Select(item => item.Ordinal).ToArray());

        var sourceIds = releaseCase.Objects.Where(item => item.Kind == "Source").Select(item => item.RecordId).ToArray();
        var sourceStates = await db.CentralArtifacts.AsNoTracking()
            .Where(item => sourceIds.Contains(item.Id))
            .Select(item => new { item.Id, item.ObjectState, item.StateReasonCode })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var intentIds = releaseCase.Objects.Where(item => item.Kind == "Derivative").Select(item => item.RecordId).ToArray();
        var intentStates = await db.CentralTransientDerivativeOutputIntents.AsNoTracking()
            .Where(item => intentIds.Contains(item.Id))
            .Select(item => new { item.Id, item.ObjectState, item.StateReasonCode })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(releaseCase.Objects.Count(item => item.Kind == "Source" && !item.Held),
            sourceStates.Count(item => item.ObjectState == CentralArtifactObjectState.Expired
            && item.StateReasonCode == "transient-retention.evidence-released"));
        Assert.AreEqual(releaseCase.Objects.Count(item => item.Kind == "Source" && item.Held),
            sourceStates.Count(item => item.ObjectState == CentralArtifactObjectState.Available));
        Assert.AreEqual(releaseCase.Objects.Count(item => item.Kind == "Derivative" && !item.Held),
            intentStates.Count(item => item.ObjectState == CentralArtifactObjectState.Expired
            && item.StateReasonCode == "transient-retention.evidence-released"));
        Assert.AreEqual(releaseCase.Objects.Count(item => item.Kind == "Derivative" && item.Held),
            intentStates.Count(item => item.ObjectState == CentralArtifactObjectState.Available));
        var immutableAfter = await ReadImmutableSnapshotAsync(db, releaseCase.EventId, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(releaseCase.ImmutableBefore.Hash, immutableAfter.Hash);
        CollectionAssert.AreEquivalent(
            releaseCase.ImmutableBefore.Counts.OrderBy(item => item.Key).ToArray(),
            immutableAfter.Counts.OrderBy(item => item.Key).ToArray());
        var targetsAfter = await ReadTargetSnapshotsAsync(db, releaseCase.Objects, cancellationToken).ConfigureAwait(false);
        var holdsAfter = await ReadHoldSnapshotAsync(
            db, releaseCase.EventId, releaseCase.Objects, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(releaseCase.HoldsBefore.Hash, holdsAfter.Hash);
        CollectionAssert.AreEqual(releaseCase.HoldsBefore.Semantics.ToArray(), holdsAfter.Semantics.ToArray());
        var itemByRecord = items.ToDictionary(item => item.RecordId);
        var expectedOrder = items
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.RecordId)
            .Select((item, ordinal) => new { item.RecordId, Ordinal = ordinal })
            .ToDictionary(item => item.RecordId, item => item.Ordinal);
        var semanticVector = new List<string>(releaseCase.Objects.Count);
        var privateMap = new List<object>(releaseCase.Objects.Count);
        foreach (var target in releaseCase.Objects.OrderBy(item => item.WorkloadOrdinal))
        {
            var before = releaseCase.TargetsBefore.Single(item => item.RecordId == target.RecordId);
            var after = targetsAfter.Single(item => item.RecordId == target.RecordId);
            var itemPresent = itemByRecord.TryGetValue(target.RecordId, out var actualItem);
            Assert.AreEqual(baselineRepresentation ? !target.Held : true, itemPresent);
            if (itemPresent)
            {
                Assert.AreEqual(expectedOrder[target.RecordId], actualItem!.Ordinal);
                Assert.AreEqual(
                    target.Kind == "Source"
                        ? CentralTransientPayloadReleaseItemKind.SourceArtifact
                        : CentralTransientPayloadReleaseItemKind.Derivative,
                    actualItem.Kind);
                Assert.AreEqual(target.Held
                        ? CentralTransientPayloadReleaseItemOutcome.PreservedHeld
                        : CentralTransientPayloadReleaseItemOutcome.Released,
                    actualItem.Outcome);
            }
            Assert.AreEqual(before.StorageReference, after.StorageReference);
            Assert.AreEqual(before.RecoveryGeneration, after.RecoveryGeneration);
            Assert.AreEqual(target.Held ? "Available" : "Expired", after.State);
            Assert.AreEqual(target.Held, string.Equals(before.RowVersion, after.RowVersion, StringComparison.Ordinal));
            var semantic = SemanticName(target.WorkloadOrdinal);
            semanticVector.Add($"{target.WorkloadOrdinal}:{semantic}:{target.Kind}:{(itemPresent ? actualItem!.Outcome : "held-outside-baseline-item")}:{after.State}:{(target.Held ? "present" : "absent")}");
            privateMap.Add(new
            {
                target.WorkloadOrdinal,
                Semantic = semantic,
                target.Kind,
                target.RecordId,
                ExpectedItemPresence = baselineRepresentation ? !target.Held : true,
                ActualItemPresence = itemPresent,
                ActualItemOrdinal = actualItem?.Ordinal,
                before.StorageReference,
                PreRowVersion = before.RowVersion,
                PostRowVersion = after.RowVersion,
                before.RecoveryGeneration,
                after.State,
                ItemOutcome = actualItem?.Outcome.ToString() ?? "N/A",
                ObjectExists = target.Held
            });
        }
        var semanticMapSha256 = Sha(JsonSerializer.Serialize(privateMap));
        var releaseAuditSha256 = Sha(JsonSerializer.Serialize(new
        {
            release.ReleaseId,
            release.CentralTransientEventId,
            release.ActorIdentity,
            release.IdempotencyKey,
            release.CanonicalRequestSha256,
            Items = items.Select(item => new { item.Ordinal, item.Kind, item.RecordId, item.Outcome })
        }));
        db.ChangeTracker.Clear();
        using var replayTelemetry = new CentralTransientLifecycleTelemetry(Issue250DiscardLogger.Instance);
        var replay = await CreateService(db, serviceMinio, replayTelemetry).ReleaseAsync(
            CreatePrincipal(releaseCase.Actor),
            releaseCase.EventId,
            releaseCase.RowVersion,
            releaseCase.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CentralTransientPayloadReleaseStatus.Released, replay.Status);
        Assert.IsNotNull(replay.Response);
        Assert.IsTrue(replay.Response.Replayed);
        Assert.AreEqual(release.ReleaseId, replay.Response.ReleaseId);
        return new Issue250CaseCorrectness(
            ExactPreReleaseObjects: true,
            FiveAbsentTwoPreserved: absent == expectedAbsent && preserved == expectedPreserved,
            SqlStatesExact: true,
            ParentItemsExact: true,
            ImmutableRowsStable: releaseCase.ImmutableBefore.Hash == immutableAfter.Hash,
            ExactReplay: replay.Response.Replayed,
            ItemShape: items.Select(item => $"{item.Ordinal}:{item.Kind}:{item.Outcome}").ToArray(),
            ImmutableHash: immutableAfter.Hash,
            SemanticMapSha256: semanticMapSha256,
            ReleaseAuditSha256: releaseAuditSha256,
            SemanticOutcomeVector: semanticVector,
            Representation: w0
                ? baselineRepresentation
                    ? $"baseline-{releaseCase.Objects.Count}-item-w0"
                    : $"candidate-seven-item-w0-{releaseCase.Objects.Count}-released"
                : baselineRepresentation ? "baseline-five-items" : "candidate-seven-terminal-items");
    }

    private static string SemanticName(int ordinal) => ordinal switch
    {
        0 => "NMinus2",
        1 => "NMinus1",
        2 => "N",
        3 => "NPlus1",
        4 => "NPlus2",
        5 => "Preview",
        6 => "Overlay",
        _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
    };

    private static async Task<Issue250W0Evidence> RunW0FaultAndRuntimeAsync(
        IntegrationTestFixture fixture,
        IMinioClient rawMinio,
        Issue250W0Fixture w0Fixture,
        string phase,
        Issue250PrivateValues privateValues)
    {
        await using var database = CreateDatabase(fixture, "W0");
        var keys = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var cancellationToken = timeout.Token;
        try
        {
            var w0Payloads = w0Fixture.Payloads;
            await database.Context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            var absentCase = await SeedReleaseCaseAsync(
                database.ConnectionString, rawMinio, w0Payloads, 90_000, keys, privateValues,
                w0CandidateCount: 1, cancellationToken: cancellationToken).ConfigureAwait(false);
            var absentTarget = absentCase.Objects.Single();
            await rawMinio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(absentTarget.Key))
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            Issue250RuntimeEvidence absentRuntime;
            using (var signals = new Issue250RuntimeCollector())
            using (var telemetry = new CentralTransientLifecycleTelemetry(signals.Logger))
            await using (var absentDb = CreateContext(database.ConnectionString, "HVO.SkyMonitor.Issue250.W0.Absent"))
            {
                var result = await CreateService(absentDb, rawMinio, telemetry).ReleaseAsync(
                    CreatePrincipal(absentCase.Actor), absentCase.EventId, absentCase.RowVersion,
                    absentCase.IdempotencyKey, cancellationToken).ConfigureAwait(false);
                Assert.AreEqual(CentralTransientPayloadReleaseStatus.Released, result.Status);
                Assert.AreEqual(1, result.Response!.ReleasedPayloadCount);
                absentRuntime = signals.SnapshotAndAssertSingleSuccess(expectedItemCount: 1);
            }
            var absentCorrectness = await VerifyReleaseCaseAsync(
                database.ConnectionString,
                rawMinio,
                rawMinio,
                absentCase,
                phase,
                cancellationToken).ConfigureAwait(false);
            await CleanupHeldObjectsAsync(rawMinio, [absentCase], cancellationToken).ConfigureAwait(false);

            var beforeDeleteCase = await SeedReleaseCaseAsync(
                database.ConnectionString, rawMinio, w0Payloads, 90_001, keys, privateValues,
                w0CandidateCount: 1, cancellationToken: cancellationToken).ConfigureAwait(false);
            Issue250RuntimeEvidence beforeDeleteFailureRuntime;
            using (var failureSignals = new Issue250RuntimeCollector())
            using (var failureTelemetry = new CentralTransientLifecycleTelemetry(failureSignals.Logger))
            using (var failureHandler = new Issue250FailingDeleteHandler(failFromDelete: 1)
            {
                InnerHandler = new SocketsHttpHandler()
            })
            using (var failureHttp = new HttpClient(failureHandler, disposeHandler: false))
            await using (var failureDb = CreateContext(database.ConnectionString, "HVO.SkyMonitor.Issue250.W0.BeforeDelete"))
            {
                var operation = CreateService(failureDb, CreateMinio(fixture, failureHttp), failureTelemetry)
                    .ReleaseAsync(
                        CreatePrincipal(beforeDeleteCase.Actor), beforeDeleteCase.EventId, beforeDeleteCase.RowVersion,
                        beforeDeleteCase.IdempotencyKey, cancellationToken);
                if (phase == "after")
                {
                    var result = await operation.ConfigureAwait(false);
                    Assert.AreEqual(CentralTransientPayloadReleaseStatus.Accepted, result.Status);
                    Assert.AreEqual(CentralTransientPayloadReleaseState.Pending, result.Response!.State);
                }
                else
                {
                    await Assert.ThrowsAsync<MinioException>(async () => await operation.ConfigureAwait(false))
                        .ConfigureAwait(false);
                }
                beforeDeleteFailureRuntime = failureSignals.SnapshotAndAssertFailedCallGap(phase);
            }
            DateTimeOffset beforeDeleteCreatedUtc;
            await using (var inspectDb = CreateContext(database.ConnectionString))
            {
                var pendingRelease = await inspectDb.CentralTransientPayloadReleases.AsNoTracking()
                    .Include(item => item.Items)
                    .SingleAsync(item => item.CentralTransientEventId == beforeDeleteCase.EventId, cancellationToken)
                    .ConfigureAwait(false);
                Assert.AreEqual(CentralTransientPayloadReleaseState.Pending, pendingRelease.State);
                Assert.AreEqual(0, pendingRelease.Items.Count(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Released));
                if (phase == "after")
                {
                    Assert.HasCount(7, pendingRelease.Items);
                    Assert.IsGreaterThanOrEqualTo(1, pendingRelease.Items.Count(item =>
                        item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending));
                    Assert.AreEqual(1, pendingRelease.Items.Count(item =>
                        item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending
                        && item.RetryCount == 1 && item.RetryAtUtc.HasValue
                        && item.ReservationToken == null));
                }
                else
                {
                    Assert.AreEqual(1, pendingRelease.Items.Count(item =>
                        item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending));
                }
                beforeDeleteCreatedUtc = pendingRelease.CreatedUtc;
            }
            var healthyFresh = await ReadHealthAsync(
                database.ConnectionString, beforeDeleteCreatedUtc.AddSeconds(1), phase, cancellationToken).ConfigureAwait(false);
            var degradedStale = await ReadHealthAsync(
                database.ConnectionString, beforeDeleteCreatedUtc.AddSeconds(31), phase, cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(HealthStatus.Healthy.ToString(), healthyFresh.Status);
            Assert.AreEqual(1, healthyFresh.PendingParentCount);
            Assert.AreEqual(1d, healthyFresh.OldestPendingParentAgeSeconds);
            Assert.AreEqual(HealthStatus.Degraded.ToString(), degradedStale.Status);
            Assert.AreEqual(31d, degradedStale.OldestPendingParentAgeSeconds);
            if (phase == "after")
            {
                Assert.IsGreaterThanOrEqualTo(1, healthyFresh.PendingItemCount);
                Assert.AreEqual(0, healthyFresh.RetryDueItemCount);
                Assert.AreEqual(0, healthyFresh.StaleReservedItemCount);
                Assert.IsGreaterThan(0L, healthyFresh.PendingLogicalBytes);
                Assert.AreEqual(1, degradedStale.RetryDueItemCount);
                Assert.IsGreaterThanOrEqualTo(31d, degradedStale.OldestPendingItemAgeSeconds);
            }

            Issue250RuntimeEvidence beforeDeleteRecoveryRuntime;
            if (phase == "after")
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5_100), cancellationToken).ConfigureAwait(false);
            }
            using (var recoverySignals = new Issue250RuntimeCollector())
            using (var recoveryTelemetry = new CentralTransientLifecycleTelemetry(recoverySignals.Logger))
            await using (var recoveryDb = CreateContext(database.ConnectionString, "HVO.SkyMonitor.Issue250.W0.BeforeDeleteRecovery"))
            {
                Assert.IsTrue(await CreateService(recoveryDb, rawMinio, recoveryTelemetry)
                    .ProcessNextAsync(cancellationToken).ConfigureAwait(false));
                beforeDeleteRecoveryRuntime = recoverySignals.SnapshotAndAssertSingleSuccess(expectedItemCount: 1);
            }
            var beforeDeleteCorrectness = await VerifyReleaseCaseAsync(
                database.ConnectionString,
                rawMinio,
                rawMinio,
                beforeDeleteCase,
                phase,
                cancellationToken).ConfigureAwait(false);
            _ = beforeDeleteCorrectness;
            var healthyDrained = await ReadHealthAsync(
                database.ConnectionString, beforeDeleteCreatedUtc.AddSeconds(31), phase, cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(HealthStatus.Healthy.ToString(), healthyDrained.Status);
            Assert.AreEqual(0, healthyDrained.PendingParentCount);
            Assert.AreEqual(0, healthyDrained.PendingItemCount);

            var restartCase = await SeedReleaseCaseAsync(
                database.ConnectionString, rawMinio, w0Payloads, 90_002, keys, privateValues,
                w0CandidateCount: 2, cancellationToken: cancellationToken).ConfigureAwait(false);
            Issue250RuntimeEvidence ordinalFailureRuntime;
            using (var ordinalSignals = new Issue250RuntimeCollector())
            using (var ordinalTelemetry = new CentralTransientLifecycleTelemetry(ordinalSignals.Logger))
            using (var ordinalHandler = new Issue250FailingDeleteHandler(failFromDelete: 2)
            {
                InnerHandler = new SocketsHttpHandler()
            })
            using (var ordinalHttp = new HttpClient(ordinalHandler, disposeHandler: false))
            await using (var ordinalDb = CreateContext(database.ConnectionString, "HVO.SkyMonitor.Issue250.W0.OrdinalFailure"))
            {
                var operation = CreateService(ordinalDb, CreateMinio(fixture, ordinalHttp), ordinalTelemetry)
                    .ReleaseAsync(
                        CreatePrincipal(restartCase.Actor), restartCase.EventId, restartCase.RowVersion,
                        restartCase.IdempotencyKey, cancellationToken);
                if (phase == "after")
                {
                    var result = await operation.ConfigureAwait(false);
                    Assert.AreEqual(CentralTransientPayloadReleaseStatus.Accepted, result.Status);
                    Assert.AreEqual(CentralTransientPayloadReleaseState.Pending, result.Response!.State);
                }
                else
                {
                    await Assert.ThrowsAsync<MinioException>(async () => await operation.ConfigureAwait(false))
                        .ConfigureAwait(false);
                }
                ordinalFailureRuntime = ordinalSignals.SnapshotAndAssertFailedCallGap(phase);
            }
            int releasedBeforeRestart;
            int pendingBeforeRestart;
            await using (var inspectDb = CreateContext(database.ConnectionString))
            {
                var release = await inspectDb.CentralTransientPayloadReleases.AsNoTracking()
                    .Include(item => item.Items)
                    .SingleAsync(item => item.CentralTransientEventId == restartCase.EventId, cancellationToken)
                    .ConfigureAwait(false);
                releasedBeforeRestart = release.Items.Count(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Released);
                pendingBeforeRestart = release.Items.Count(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending);
                Assert.AreEqual(1, releasedBeforeRestart);
                if (phase == "after")
                {
                    Assert.HasCount(7, release.Items);
                    Assert.IsGreaterThanOrEqualTo(1, pendingBeforeRestart);
                    Assert.AreEqual(1, release.Items.Count(item =>
                        item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending
                        && item.RetryCount == 1 && item.RetryAtUtc.HasValue
                        && item.ReservationToken == null));
                }
                else
                {
                    Assert.AreEqual(1, pendingBeforeRestart);
                }
            }
            Issue250RuntimeEvidence restartRuntime;
            if (phase == "after")
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5_100), cancellationToken).ConfigureAwait(false);
            }
            using (var restartSignals = new Issue250RuntimeCollector())
            using (var restartTelemetry = new CentralTransientLifecycleTelemetry(restartSignals.Logger))
            await using (var restartDb = CreateContext(database.ConnectionString, "HVO.SkyMonitor.Issue250.W0.FreshProcessor"))
            {
                Assert.IsTrue(await CreateService(restartDb, rawMinio, restartTelemetry)
                    .ProcessNextAsync(cancellationToken).ConfigureAwait(false));
                restartRuntime = restartSignals.SnapshotAndAssertSingleSuccess(expectedItemCount: 2);
            }
            var restartCorrectness = await VerifyReleaseCaseAsync(
                database.ConnectionString, rawMinio, rawMinio, restartCase, phase, cancellationToken)
                .ConfigureAwait(false);
            return new Issue250W0Evidence(
                ObjectAlreadyAbsentCompletedIdempotently: absentCorrectness.ExactReplay,
                Generator: w0Fixture.Generator,
                BeforeDeleteFailureLeftRetryablePendingParentAndItem: true,
                FalseParentCompletionObserved: false,
                ReleasedEarlierOrdinalsBeforeRestart: releasedBeforeRestart,
                PendingLaterOrdinalsBeforeRestart: pendingBeforeRestart,
                FreshProcessorProcessNextRecoveredAndCompleted: restartCorrectness.ExactReplay,
                BaselineRestartBoundary: "A failed service/DbContext is disposed; a fresh CentralTransientPayloadReleaseProcessor service instance recovers through ProcessNextAsync without public ReleaseAsync replay.",
                FutureOnlyBoundaries: phase == "after"
                    ? "Candidate fault stages and schema are authenticated by preflight. This measured W0 exercises absent-object idempotency, retryable DELETE failure, prior-ordinal restart, recovery, and health transitions; remaining issue #250 fault stages stay in the merged functional suite."
                    : "N/A at baseline: no reservation-committed-before-DELETE, process-termination analogue, after-DELETE/before-finalize, stale rowversion/generation, late-hold, final-item/before-parent-completion, or bounded retry hook exists.",
                Runtime: new(absentRuntime, beforeDeleteFailureRuntime, beforeDeleteRecoveryRuntime,
                    ordinalFailureRuntime, restartRuntime),
                Health: new(healthyFresh, degradedStale, healthyDrained),
                Privacy: "Runtime labels are asserted to operation/outcome only; logs retain event ID and bounded field names/values but omit RuntimeId and all durable identities.");
        }
        finally
        {
            await CleanupScenarioAsync(rawMinio, keys, database.Context).ConfigureAwait(false);
        }
    }

    private static async Task<Issue250W3MEvidence> RunW3MAsync(
        IntegrationTestFixture fixture,
        IMinioClient minio,
        bool smoke,
        Issue250PrivateValues privateValues)
    {
        await using var database = CreateDatabase(fixture, "W3M");
        var objectKey = $"issue-250/w3m/{Guid.NewGuid():N}.bin";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(W3MPhaseTimeoutMinutes));
        var cancellationToken = timeout.Token;
        privateValues.Add(objectKey);
        try
        {
            await database.Context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            var completedParents = smoke ? 8 : 10_000;
            var pendingEventId = Guid.NewGuid();
            var pendingReleaseId = Guid.NewGuid();
            var payload = Enumerable.Range(0, W3MSentinelByteLength)
                .Select(index => (byte)(index * 17 + 3)).ToArray();
            var payloadSha256 = Convert.ToHexString(SHA256.HashData(payload));
            Assert.AreEqual(W3MSentinelByteLength, payload.LongLength);
            Assert.AreEqual(W3MSentinelSha256, payloadSha256,
                "The deterministic W3M processor sentinel changed; review its authenticated generator before updating it.");
            var frame = new CentralFrame
            {
                RegistrationId = Guid.NewGuid(),
                DevicePublicId = Guid.NewGuid(),
                ObservatoryId = Guid.NewGuid(),
                AgentId = "issue-250-w3m-processor",
                FrameId = Guid.NewGuid(),
                CapturedAtUtc = DateTimeOffset.UnixEpoch,
                FirstReceivedAtUtc = DateTimeOffset.UnixEpoch
            };
            var artifact = new CentralArtifact
            {
                CentralFrameId = frame.Id,
                Frame = frame,
                ArtifactId = Guid.NewGuid(),
                DevicePublicId = frame.DevicePublicId,
                Role = FrameArtifactRole.Raw,
                RecipeVersion = "issue-250-w3m-v1",
                ManifestSchemaVersion = "evidence-v1",
                MediaType = "application/octet-stream",
                ByteLength = payload.LongLength,
                ChecksumSha256 = payloadSha256,
                StorageReference = BucketPrefix + objectKey,
                ReceivedAtUtc = DateTimeOffset.UnixEpoch,
                IdempotencyKey = Sha("issue-250-w3m-artifact"),
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete
            };
            frame.Artifacts.Add(artifact);
            database.Context.CentralFrames.Add(frame);
            var sentinelDurableStarted = Stopwatch.GetTimestamp();
            var sentinelDurableRows = await database.Context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var sentinelDurableDurationMilliseconds = Stopwatch.GetElapsedTime(sentinelDurableStarted).TotalMilliseconds;
            Assert.AreEqual(2, sentinelDurableRows);
            // Cleanup ownership is registered before the first object-store write.
            await using (var stream = new MemoryStream(payload, writable: false))
            {
                await minio.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(objectKey)
                    .WithStreamData(stream).WithObjectSize(payload.LongLength)
                    .WithContentType("application/octet-stream"), cancellationToken).ConfigureAwait(false);
            }
            var sentinelVerification = await VerifyObjectAsync(
                minio,
                objectKey,
                W3MSentinelByteLength,
                W3MSentinelSha256,
                cancellationToken).ConfigureAwait(false);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var sqlSetupStarted = Stopwatch.GetTimestamp();
            var historyFrameId = Guid.NewGuid();
            var historyDevicePublicId = Guid.NewGuid();
            var tempTables = await CreateW3MTempTablesAsync(
                connection, completedParents, cancellationToken).ConfigureAwait(false);
            var foundation = await InsertW3MFoundationAsync(
                connection,
                pendingEventId,
                pendingReleaseId,
                artifact.Id,
                historyFrameId,
                historyDevicePublicId,
                cancellationToken).ConfigureAwait(false);
            var batches = new List<Issue250W3MSetupBatchEvidence>();
            for (var firstSequence = 1; firstSequence <= completedParents; firstSequence += W3MSetupBatchParents)
            {
                var lastSequence = Math.Min(firstSequence + W3MSetupBatchParents - 1, completedParents);
                batches.Add(await ExecuteW3MSetupBatchAsync(
                    connection,
                    historyFrameId,
                    historyDevicePublicId,
                    firstSequence,
                    lastSequence,
                    cancellationToken).ConfigureAwait(false));
            }
            var queueIndexNormalization = await NormalizeW3MQueueIndexAsync(
                connection, completedParents + 1L, cancellationToken).ConfigureAwait(false);
            var schemaIntegrity = await ReadW3MSchemaIntegrityAsync(
                connection, cancellationToken).ConfigureAwait(false);
            var tempTableDropDurationMilliseconds = await DropW3MTempTablesAsync(
                connection, cancellationToken).ConfigureAwait(false);
            var sqlSetupDurationMilliseconds = Stopwatch.GetElapsedTime(sqlSetupStarted).TotalMilliseconds;

            var expectedTargets = checked((long)completedParents * 7);
            Assert.AreEqual(completedParents, tempTables.ParentRows);
            Assert.AreEqual(expectedTargets, tempTables.TargetRows);
            Assert.AreEqual(1, foundation.HistoryFrames);
            Assert.AreEqual(1, foundation.SentinelEvents);
            Assert.AreEqual(1, foundation.SentinelReleases);
            Assert.AreEqual(1, foundation.SentinelItems);
            var expectedSetupRows = new Issue250W3MSetupRowCounts(
                Events: completedParents + 1L,
                Releases: completedParents + 1L,
                Artifacts: expectedTargets + 1,
                Items: expectedTargets + 1,
                ItemTransitions: expectedTargets,
                ReleaseTransitions: completedParents);
            var actualSetupRows = new Issue250W3MSetupRowCounts(
                Events: batches.Sum(item => (long)item.EventRows) + foundation.SentinelEvents,
                Releases: batches.Sum(item => (long)item.ReleaseRows) + foundation.SentinelReleases,
                Artifacts: batches.Sum(item => item.ArtifactRows) + 1,
                Items: batches.Sum(item => item.ItemRows) + foundation.SentinelItems,
                ItemTransitions: batches.Sum(item => item.ItemTransitionRows),
                ReleaseTransitions: batches.Sum(item => (long)item.ReleaseTransitionRows));
            Assert.AreEqual(expectedSetupRows, actualSetupRows);
            var setupEvidence = new Issue250W3MSetupEvidence(
                W3MSetupBatchParents,
                batches.Count,
                tempTables.ParentRows,
                tempTables.TargetRows,
                tempTables.DurationMilliseconds,
                foundation.DurationMilliseconds,
                expectedSetupRows,
                actualSetupRows,
                sentinelDurableRows,
                sentinelDurableDurationMilliseconds,
                sqlSetupDurationMilliseconds,
                sentinelDurableDurationMilliseconds + sqlSetupDurationMilliseconds,
                batches.Sum(item => item.TotalDurationMilliseconds),
                batches.Max(item => item.TotalDurationMilliseconds),
                batches.Max(item => Math.Max(item.PhaseADurationMilliseconds,
                    Math.Max(item.PhaseBDurationMilliseconds, item.PhaseCDurationMilliseconds))),
                Math.Max(
                    Math.Max(tempTables.DurationMilliseconds, foundation.DurationMilliseconds),
                    Math.Max(queueIndexNormalization.DurationMilliseconds, tempTableDropDurationMilliseconds)),
                queueIndexNormalization.DurationMilliseconds,
                queueIndexNormalization,
                tempTableDropDurationMilliseconds,
                W3MCommandTimeoutSeconds,
                W3MPhaseTimeoutMinutes * 60,
                SharedHistoryFrameCount: 1,
                TotalW3MFrameCount: 2,
                SetupExcludedFromMeasuredClaims: true,
                schemaIntegrity,
                batches);

            var counts = await ReadW3MCountsAsync(connection, cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(completedParents, counts.CompletedParents);
            Assert.AreEqual((long)completedParents * 7, counts.TerminalItems);
            Assert.AreEqual(1, counts.PendingParents);
            Assert.AreEqual(1L, counts.PendingItems);
            Assert.AreEqual(completedParents + 1, counts.DistinctEvents);
            Assert.AreEqual(
                Sha(string.Join('\n', "central-transient-payload-release-v1", pendingEventId.ToString("N"))),
                await ReadW3MCanonicalRequestShaAsync(connection, pendingReleaseId, cancellationToken)
                    .ConfigureAwait(false));
            Assert.AreEqual(expectedTargets,
                await ReadW3MValidTerminalTargetsAsync(connection, cancellationToken).ConfigureAwait(false));
            Assert.AreEqual(0L, await ReadW3MInvalidCanonicalRequestHashesAsync(connection, cancellationToken).ConfigureAwait(false));
            var topology = await ReadW3MTopologyAsync(
                connection,
                frame.Id,
                historyFrameId,
                historyDevicePublicId,
                cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(2L, topology.TotalFrames);
            Assert.AreEqual(1L, topology.HistoryFrames);
            Assert.AreEqual(1L, topology.SentinelFrames);
            Assert.AreEqual(expectedTargets, topology.HistoryArtifacts);
            Assert.AreEqual(expectedTargets, topology.DistinctHistoryArtifactRecordIds);
            Assert.AreEqual(expectedTargets, topology.DistinctHistoryArtifactIds);
            Assert.AreEqual(expectedTargets, topology.DistinctHistoryIdempotencyKeys);
            Assert.AreEqual(expectedTargets, topology.DistinctHistoryStorageReferences);
            Assert.AreEqual(expectedTargets, topology.HistoryArtifactsWithMatchingDevicePublicId);
            Assert.AreEqual(expectedTargets, topology.DistinctTerminalItemTargetRecordIds);
            var capture = new Issue250ProcessNextQueryCaptureInterceptor();
            await using (var captureDb = CreateContext(
                             database.ConnectionString,
                             "HVO.SkyMonitor.Issue250.W3M.Capture",
                             additionalInterceptor: capture))
            using (var captureTelemetry = new CentralTransientLifecycleTelemetry(Issue250DiscardLogger.Instance))
            {
                await Assert.ThrowsAsync<Issue250QueryCapturedException>(async () =>
                    await CreateService(captureDb, minio, captureTelemetry).ProcessNextAsync(cancellationToken)
                        .ConfigureAwait(false)).ConfigureAwait(false);
            }
            var capturedQuery = capture.SingleParent();
            var plan = await CapturePlanAsync(
                connection,
                capturedQuery,
                pendingReleaseId,
                "IX_CentralTransientPayloadReleases_State_CreatedUtc_ReleaseId",
                cancellationToken).ConfigureAwait(false);
            var dueItemPlan = await CapturePlanAsync(
                connection,
                capture.SingleDueItem(),
                pendingReleaseId,
                "PK_CentralTransientPayloadReleaseItems",
                cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(1, plan.SelectedRows);
            Assert.IsTrue(plan.IndexUsed);
            Assert.AreEqual(1, dueItemPlan.SelectedRows);
            Assert.IsTrue(dueItemPlan.IndexUsed);
            await using (var processorDb = CreateContext(database.ConnectionString, "HVO.SkyMonitor.Issue250.W3M.Processor"))
            using (var telemetry = new CentralTransientLifecycleTelemetry(Issue250DiscardLogger.Instance))
            {
                var processor = CreateService(processorDb, minio, telemetry);
                Assert.IsTrue(await processor.ProcessNextAsync(cancellationToken).ConfigureAwait(false));
                processorDb.ChangeTracker.Clear();
                var selected = await processorDb.CentralTransientPayloadReleases.AsNoTracking()
                    .Include(item => item.Items).SingleAsync(item => item.ReleaseId == pendingReleaseId)
                    .ConfigureAwait(false);
                Assert.AreEqual(CentralTransientPayloadReleaseState.Completed, selected.State);
                Assert.AreEqual(CentralTransientPayloadReleaseItemOutcome.Released, selected.Items.Single().Outcome);
                Assert.IsFalse(await processor.ProcessNextAsync(cancellationToken).ConfigureAwait(false));
            }
            await AssertAbsentAsync(minio, objectKey, cancellationToken).ConfigureAwait(false);
            var afterCounts = await ReadW3MCountsAsync(connection, cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, afterCounts.PendingParents);
            Assert.AreEqual(0L, afterCounts.PendingItems);
            return new Issue250W3MEvidence(
                completedParents,
                counts.TerminalItems,
                counts.PendingParents,
                counts.PendingItems,
                counts.DistinctEvents,
                (long)completedParents * 7,
                setupEvidence,
                topology,
                W3MSentinelGenerator,
                W3MSentinelByteLength,
                W3MSentinelSha256,
                sentinelVerification.ObservedByteLength,
                sentinelVerification.ObservedSha256,
                sentinelVerification.ExactLengthAndSha256,
                SentinelSetupPutPayloadBytes: W3MSentinelByteLength,
                SentinelSetupPutRequests: 1,
                SentinelSetupPreReleaseStatRequests: 1,
                SentinelSetupPreReleaseGetPayloadBytes: W3MSentinelByteLength,
                SentinelSetupPreReleaseGetRequests: 1,
                SentinelPostReleaseAbsent: true,
                SentinelPostReleaseAbsenceStatRequests: 1,
                QueryCapturedFromProductionProcessNextAsync: true,
                Sha(pendingReleaseId.ToString("N")),
                plan.NormalizedParameterizedQuery,
                plan.NormalizedQuerySha256,
                plan.CanonicalPlanXml,
                plan.CanonicalPlanSha256,
                "ShowPlan XML serialized without formatting after Database, Server, DatabaseContextSettingsId, and compact GUID-like plan attribute values are replaced with [scrubbed].",
                plan.LogicalReads,
                plan.SelectedRows,
                plan.IndexUsed,
                plan.CanonicalPlanFactsJson,
                plan.NormalizedPlanFactsSha256,
                plan.Operators,
                plan.Indexes,
                "IX_CentralTransientPayloadReleases_State_CreatedUtc_ReleaseId",
                BoundedSelection: true,
                MetadataOnly: true,
                TriggerAndConstraintPreservingSetup: true,
                ActualProcessorSelectedOldest: true,
                ActualProcessorCompletedOldest: true,
                FinalPendingParents: afterCounts.PendingParents,
                FinalPendingItems: afterCounts.PendingItems,
                CandidateItemDueWorkPlan: JsonSerializer.Serialize(new
                {
                    dueItemPlan.NormalizedParameterizedQuery,
                    dueItemPlan.NormalizedQuerySha256,
                    dueItemPlan.CanonicalPlanXml,
                    dueItemPlan.CanonicalPlanSha256,
                    dueItemPlan.LogicalReads,
                    dueItemPlan.SelectedRows,
                    dueItemPlan.IndexUsed,
                    dueItemPlan.CanonicalPlanFactsJson,
                    dueItemPlan.NormalizedPlanFactsSha256,
                    dueItemPlan.Operators,
                    dueItemPlan.Indexes,
                    ExpectedLoadNextIndex = "PK_CentralTransientPayloadReleaseItems",
                    ClaimPlanIndexes = plan.Indexes,
                    ConfiguredDueIndex =
                        "IX_CentralTransientPayloadReleaseItems_RetryAtUtc_RequestedAtUtc_ReleaseId_Ordinal",
                    ConfiguredDueIndexUsed = plan.Indexes.Contains(
                        "IX_CentralTransientPayloadReleaseItems_RetryAtUtc_RequestedAtUtc_ReleaseId_Ordinal",
                        StringComparer.Ordinal)
                }),
                IsolatedDatabaseDeletedAfterCapture: true);
        }
        finally
        {
            await CleanupScenarioAsync(minio, [objectKey], database.Context).ConfigureAwait(false);
        }
    }

    private static async Task<Issue250W3MTempTableEvidence> CreateW3MTempTablesAsync(
        SqlConnection connection,
        int completedParents,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        await using (var create = connection.CreateCommand())
        {
            create.CommandTimeout = W3MCommandTimeoutSeconds;
            create.CommandText = """
            CREATE TABLE #parents
            (
                [Sequence] int NOT NULL PRIMARY KEY CLUSTERED,
                [ReleaseId] uniqueidentifier NOT NULL UNIQUE,
                [CentralTransientEventId] uniqueidentifier NOT NULL UNIQUE,
                [EventId] uniqueidentifier NOT NULL UNIQUE
            );
            CREATE TABLE #targets
            (
                [TargetSequence] bigint NOT NULL PRIMARY KEY CLUSTERED,
                [ReleaseId] uniqueidentifier NOT NULL,
                [Ordinal] int NOT NULL,
                [RecordId] uniqueidentifier NOT NULL UNIQUE,
                [ArtifactId] uniqueidentifier NOT NULL UNIQUE,
                UNIQUE ([ReleaseId], [Ordinal])
            );
            """;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var populate = connection.CreateCommand();
        populate.CommandTimeout = W3MCommandTimeoutSeconds;
        populate.CommandText = """
            SET NOCOUNT ON;
            ;WITH sequences AS
            (
                SELECT 1 AS [Sequence]
                UNION ALL
                SELECT [Sequence] + 1 FROM sequences WHERE [Sequence] < @completed_parents
            )
            INSERT INTO #parents ([Sequence], [ReleaseId], [CentralTransientEventId], [EventId])
            SELECT [Sequence], NEWID(), NEWID(), NEWID() FROM sequences
            OPTION (MAXRECURSION 0);
            DECLARE @parent_rows int = @@ROWCOUNT;

            INSERT INTO #targets ([TargetSequence], [ReleaseId], [Ordinal], [RecordId], [ArtifactId])
            SELECT (CAST(parent.[Sequence] - 1 AS bigint) * 7) + ordinal.[value] + 1,
                   parent.[ReleaseId], ordinal.[value], NEWID(), NEWID()
            FROM #parents AS parent
            CROSS JOIN (VALUES (0), (1), (2), (3), (4), (5), (6)) AS ordinal([value]);
            DECLARE @target_rows int = @@ROWCOUNT;
            SELECT @parent_rows, @target_rows;
            """;
        populate.Parameters.AddWithValue("@completed_parents", completedParents);
        await using var reader = await populate.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new(
            reader.GetInt32(0),
            reader.GetInt32(1),
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static async Task<Issue250W3MFoundationEvidence> InsertW3MFoundationAsync(
        SqlConnection connection,
        Guid pendingEventId,
        Guid pendingReleaseId,
        Guid sentinelArtifactId,
        Guid historyFrameId,
        Guid historyDevicePublicId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = W3MCommandTimeoutSeconds;
        command.CommandText = """
            SET NOCOUNT ON;
            INSERT INTO [CentralFrames]
                ([Id], [RegistrationId], [DevicePublicId], [ObservatoryId], [AgentId], [FrameId],
                 [CapturedAtUtc], [FirstReceivedAtUtc], [LocationEvidenceState])
            VALUES
                (@history_frame_id, NEWID(), @history_device_public_id, NEWID(), N'issue-250-w3m-history', NEWID(),
                 '2025-01-02T00:00:00+00:00', '2025-01-02T00:00:00+00:00', N'PendingReference');
            DECLARE @history_frames int = @@ROWCOUNT;

            INSERT INTO [CentralTransientEvents] ([Id], [AgentId], [EventId], [EventCreatedUtc])
            VALUES (@pending_event_id, N'issue-250-w3m-pending', NEWID(), '2025-01-01T00:00:00+00:00');
            DECLARE @sentinel_events int = @@ROWCOUNT;

            INSERT INTO [CentralTransientPayloadReleases]
                ([ReleaseId], [CentralTransientEventId], [ActorIdentity], [IdempotencyKey],
                 [CanonicalRequestSha256], [State], [CreatedUtc], [CompletedUtc], [ReasonCode])
            VALUES
                (@pending_release_id, @pending_event_id, N'issue-250-w3m-pending', N'pending',
                 CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varchar(max),
                    'central-transient-payload-release-v1' + CHAR(10) +
                    LOWER(REPLACE(CONVERT(char(36), @pending_event_id), '-', '')))), 2),
                 N'Pending', '2025-01-01T00:00:00+00:00', NULL, NULL);
            DECLARE @sentinel_releases int = @@ROWCOUNT;

            INSERT INTO [CentralTransientPayloadReleaseItems]
                ([ReleaseId], [Ordinal], [Kind], [RecordId], [Outcome], [ReleasedUtc])
            VALUES (@pending_release_id, 0, N'SourceArtifact', @sentinel_artifact_id, N'Pending', NULL);
            DECLARE @sentinel_items int = @@ROWCOUNT;
            SELECT @history_frames, @sentinel_events, @sentinel_releases, @sentinel_items;
            """;
        command.Parameters.AddWithValue("@history_frame_id", historyFrameId);
        command.Parameters.AddWithValue("@history_device_public_id", historyDevicePublicId);
        command.Parameters.AddWithValue("@pending_event_id", pendingEventId);
        command.Parameters.AddWithValue("@pending_release_id", pendingReleaseId);
        command.Parameters.AddWithValue("@sentinel_artifact_id", sentinelArtifactId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static async Task<Issue250W3MSetupBatchEvidence> ExecuteW3MSetupBatchAsync(
        SqlConnection connection,
        Guid historyFrameId,
        Guid historyDevicePublicId,
        int firstSequence,
        int lastSequence,
        CancellationToken cancellationToken)
    {
        var batchStarted = Stopwatch.GetTimestamp();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var phaseA = await ExecuteW3MSetupPhaseAsync(
                connection,
                transaction,
                """
                SET NOCOUNT ON;
                INSERT INTO [CentralTransientEvents] ([Id], [AgentId], [EventId], [EventCreatedUtc])
                SELECT [CentralTransientEventId], CONCAT(N'issue-250-w3m-', [Sequence]), [EventId],
                       DATEADD(millisecond, [Sequence], CAST('2025-01-02T00:00:00+00:00' AS datetimeoffset))
                FROM #parents WHERE [Sequence] BETWEEN @first_sequence AND @last_sequence;
                DECLARE @event_rows int = @@ROWCOUNT;

                INSERT INTO [CentralTransientPayloadReleases]
                    ([ReleaseId], [CentralTransientEventId], [ActorIdentity], [IdempotencyKey],
                     [CanonicalRequestSha256], [State], [CreatedUtc], [CompletedUtc], [ReasonCode])
                SELECT [ReleaseId], [CentralTransientEventId], CONCAT(N'issue-250-w3m-', [Sequence]),
                       CONCAT(N'completed-', [Sequence]),
                       CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varchar(max),
                           'central-transient-payload-release-v1' + CHAR(10) +
                           LOWER(REPLACE(CONVERT(char(36), [CentralTransientEventId]), '-', '')))), 2),
                       N'Pending',
                       DATEADD(millisecond, [Sequence], CAST('2025-01-02T00:00:00+00:00' AS datetimeoffset)),
                       NULL, NULL
                FROM #parents WHERE [Sequence] BETWEEN @first_sequence AND @last_sequence;
                DECLARE @release_rows int = @@ROWCOUNT;
                SELECT @event_rows, @release_rows;
                """,
                firstSequence,
                lastSequence,
                historyFrameId: null,
                historyDevicePublicId: null,
                cancellationToken).ConfigureAwait(false);
            var phaseB = await ExecuteW3MSetupPhaseAsync(
                connection,
                transaction,
                """
                SET NOCOUNT ON;
                INSERT INTO [CentralArtifacts]
                    ([Id], [CentralFrameId], [ArtifactId], [DevicePublicId], [Role], [RecipeVersion],
                     [ManifestSchemaVersion], [MediaType], [ByteLength], [ChecksumSha256], [StorageReference],
                     [ReceivedAtUtc], [IdempotencyKey], [Variant], [CreatedUtc], [ObjectState],
                     [ReconstructionState], [StateReasonCode], [ReconciledAtUtc], [ObjectVerifiedAtUtc],
                     [ObjectVerificationRetryCount], [RecoveryGeneration], [ReferenceRetryCount])
                SELECT [RecordId], @history_frame_id, [ArtifactId], @history_device_public_id,
                       N'Raw', N'issue-250-w3m-history-v1', N'evidence-v1', N'application/octet-stream',
                       1, REPLICATE('C', 64),
                       CONCAT(N'minio://skymonitor-artifacts/issue-250/w3m-metadata/', [TargetSequence]),
                       '2025-01-02T00:00:00+00:00',
                       RIGHT(REPLICATE('0', 64) + CONVERT(varchar(20), [TargetSequence]), 64),
                       CONCAT(N'ordinal-', [Ordinal]), '2025-01-02T00:00:00+00:00', N'Expired',
                       N'Complete', N'transient-retention.evidence-released',
                       '2025-01-03T00:00:00+00:00', '2025-01-02T00:00:00+00:00', 0, 0, 0
                FROM #targets
                WHERE [TargetSequence] BETWEEN ((CAST(@first_sequence AS bigint) - 1) * 7) + 1
                    AND CAST(@last_sequence AS bigint) * 7;
                DECLARE @artifact_rows int = @@ROWCOUNT;

                INSERT INTO [CentralTransientPayloadReleaseItems]
                    ([ReleaseId], [Ordinal], [Kind], [RecordId], [Outcome], [ReleasedUtc])
                SELECT [ReleaseId], [Ordinal], N'SourceArtifact', [RecordId], N'Pending', NULL
                FROM #targets
                WHERE [TargetSequence] BETWEEN ((CAST(@first_sequence AS bigint) - 1) * 7) + 1
                    AND CAST(@last_sequence AS bigint) * 7;
                DECLARE @item_rows int = @@ROWCOUNT;
                SELECT @artifact_rows, @item_rows;
                """,
                firstSequence,
                lastSequence,
                historyFrameId,
                historyDevicePublicId,
                cancellationToken).ConfigureAwait(false);
            var phaseC = await ExecuteW3MSetupPhaseAsync(
                connection,
                transaction,
                """
                SET NOCOUNT ON;
                UPDATE item
                SET [Outcome] = N'Released', [ReleasedUtc] = '2025-01-03T00:00:00+00:00'
                FROM [CentralTransientPayloadReleaseItems] AS item
                INNER JOIN #targets AS target
                    ON target.[ReleaseId] = item.[ReleaseId] AND target.[Ordinal] = item.[Ordinal]
                WHERE target.[TargetSequence] BETWEEN ((CAST(@first_sequence AS bigint) - 1) * 7) + 1
                    AND CAST(@last_sequence AS bigint) * 7;
                DECLARE @item_transition_rows int = @@ROWCOUNT;

                UPDATE release
                SET [State] = N'Completed', [CompletedUtc] = '2025-01-03T00:00:00+00:00'
                FROM [CentralTransientPayloadReleases] AS release
                INNER JOIN #parents AS parent ON parent.[ReleaseId] = release.[ReleaseId]
                WHERE parent.[Sequence] BETWEEN @first_sequence AND @last_sequence;
                DECLARE @release_transition_rows int = @@ROWCOUNT;
                SELECT @item_transition_rows, @release_transition_rows;
                """,
                firstSequence,
                lastSequence,
                historyFrameId: null,
                historyDevicePublicId: null,
                cancellationToken).ConfigureAwait(false);
            var expectedParents = lastSequence - firstSequence + 1;
            var expectedItems = checked(expectedParents * 7);
            Assert.AreEqual(expectedParents, phaseA.FirstRows);
            Assert.AreEqual(expectedParents, phaseA.SecondRows);
            Assert.AreEqual(expectedItems, phaseB.FirstRows);
            Assert.AreEqual(expectedItems, phaseB.SecondRows);
            Assert.AreEqual(expectedItems, phaseC.FirstRows);
            Assert.AreEqual(expectedParents, phaseC.SecondRows);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(
                Batch: ((firstSequence - 1) / W3MSetupBatchParents) + 1,
                FirstSequence: firstSequence,
                LastSequence: lastSequence,
                ParentRows: phaseA.FirstRows,
                EventRows: phaseA.FirstRows,
                ReleaseRows: phaseA.SecondRows,
                ArtifactRows: phaseB.FirstRows,
                ItemRows: phaseB.SecondRows,
                ItemTransitionRows: phaseC.FirstRows,
                ReleaseTransitionRows: phaseC.SecondRows,
                PhaseADurationMilliseconds: phaseA.DurationMilliseconds,
                PhaseBDurationMilliseconds: phaseB.DurationMilliseconds,
                PhaseCDurationMilliseconds: phaseC.DurationMilliseconds,
                TotalDurationMilliseconds: Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds);
        }
        catch (Exception exception)
        {
            try
            {
                using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await transaction.RollbackAsync(rollbackTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException("W3M setup phase failed and its transaction rollback also failed.",
                    exception, rollbackException);
            }
            throw;
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only fixed internal W3M setup command literals are supplied by this evidence harness.")]
    private static async Task<Issue250W3MSetupPhaseResult> ExecuteW3MSetupPhaseAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string commandText,
        int firstSequence,
        int lastSequence,
        Guid? historyFrameId,
        Guid? historyDevicePublicId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = W3MCommandTimeoutSeconds;
        command.CommandText = commandText;
        command.Parameters.AddWithValue("@first_sequence", firstSequence);
        command.Parameters.AddWithValue("@last_sequence", lastSequence);
        if (historyFrameId.HasValue)
        {
            command.Parameters.AddWithValue("@history_frame_id", historyFrameId.Value);
            command.Parameters.AddWithValue("@history_device_public_id", historyDevicePublicId!.Value);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new(
            reader.GetInt32(0),
            reader.GetInt32(1),
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static async Task<Issue250W3MQueueIndexNormalizationEvidence> NormalizeW3MQueueIndexAsync(
        SqlConnection connection,
        long expectedRecordCount,
        CancellationToken cancellationToken)
    {
        var logicalRecordCountBefore = await ReadW3MReleaseRowCountAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var before = await ReadW3MQueueIndexPhysicalStateAsync(connection, cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = W3MCommandTimeoutSeconds;
        command.CommandText = """
            ALTER INDEX [IX_CentralTransientPayloadReleases_State_CreatedUtc_ReleaseId]
                ON [CentralTransientPayloadReleases]
                REBUILD WITH (FILLFACTOR = 100, PAD_INDEX = OFF, SORT_IN_TEMPDB = OFF, MAXDOP = 1);
            UPDATE STATISTICS [CentralTransientPayloadReleases]
                [IX_CentralTransientPayloadReleases_State_CreatedUtc_ReleaseId] WITH FULLSCAN;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var durationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var after = await ReadW3MQueueIndexPhysicalStateAsync(connection, cancellationToken).ConfigureAwait(false);
        var logicalRecordCountAfter = await ReadW3MReleaseRowCountAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(0L, after.GhostRecordCount,
            "The measured W3M queue index must not retain setup-transition ghost records after normalization.");
        Assert.AreEqual(0L, after.VersionGhostRecordCount,
            "The measured W3M queue index must not retain version ghost records after normalization.");
        Assert.AreEqual(expectedRecordCount, logicalRecordCountBefore,
            "W3M setup must create the expected logical release rows before queue-index normalization.");
        Assert.AreEqual(logicalRecordCountBefore, logicalRecordCountAfter,
            "Queue-index normalization must preserve every logical release row.");
        Assert.AreEqual(logicalRecordCountAfter, after.RecordCount,
            "The normalized queue index must contain exactly one physical leaf record per live release row.");
        return new(durationMilliseconds, logicalRecordCountBefore, logicalRecordCountAfter, before, after,
            "Offline index rebuild with FILLFACTOR 100, PAD_INDEX OFF, SORT_IN_TEMPDB OFF, MAXDOP 1, followed by UPDATE STATISTICS FULLSCAN.",
            "Normalized post-maintenance synthetic state; not representative of production queue aging.");
    }

    private static async Task<long> ReadW3MReleaseRowCountAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = W3MCommandTimeoutSeconds;
        command.CommandText = "SELECT COUNT_BIG(*) FROM [CentralTransientPayloadReleases];";
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<Issue250W3MQueueIndexPhysicalState> ReadW3MQueueIndexPhysicalStateAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = W3MCommandTimeoutSeconds;
        command.CommandText = """
            SELECT COALESCE(CAST(SUM([page_count]) AS bigint), 0),
                   COALESCE(CAST(SUM([record_count]) AS bigint), 0),
                   COALESCE(CAST(SUM([ghost_record_count]) AS bigint), 0),
                   COALESCE(CAST(SUM([version_ghost_record_count]) AS bigint), 0),
                   COALESCE(MAX([index_depth]), 0)
            FROM [sys].[dm_db_index_physical_stats](
                DB_ID(),
                OBJECT_ID(N'CentralTransientPayloadReleases'),
                INDEXPROPERTY(
                    OBJECT_ID(N'CentralTransientPayloadReleases'),
                    N'IX_CentralTransientPayloadReleases_State_CreatedUtc_ReleaseId',
                    N'IndexID'),
                NULL,
                N'DETAILED')
            WHERE [index_level] = 0 AND [alloc_unit_type_desc] = N'IN_ROW_DATA';
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        var state = new Issue250W3MQueueIndexPhysicalState(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4));
        Assert.IsTrue(state.PageCount > 0, "The measured W3M queue index must expose leaf-page diagnostics.");
        Assert.IsTrue(state.IndexDepth > 0, "The measured W3M queue index must expose its physical depth.");
        return state;
    }

    private static async Task<double> DropW3MTempTablesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = W3MCommandTimeoutSeconds;
        command.CommandText = "DROP TABLE #targets; DROP TABLE #parents;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static async Task<Issue250W3MSchemaIntegrityEvidence> ReadW3MSchemaIntegrityAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = W3MCommandTimeoutSeconds;
        command.CommandText = """
            DECLARE @tables TABLE ([object_id] int NOT NULL PRIMARY KEY);
            INSERT INTO @tables ([object_id]) VALUES
                (OBJECT_ID(N'CentralFrames')),
                (OBJECT_ID(N'CentralArtifacts')),
                (OBJECT_ID(N'CentralTransientEvents')),
                (OBJECT_ID(N'CentralTransientPayloadReleases')),
                (OBJECT_ID(N'CentralTransientPayloadReleaseItems'));
            SELECT trigger_value.[name], OBJECT_NAME(trigger_value.[parent_id]), trigger_value.[is_disabled],
                   OBJECT_DEFINITION(trigger_value.[object_id])
            FROM [sys].[triggers] AS trigger_value
            WHERE trigger_value.[parent_id] IN (SELECT [object_id] FROM @tables)
            ORDER BY trigger_value.[name];
            SELECT foreign_key.[name], OBJECT_NAME(foreign_key.[parent_object_id]),
                   OBJECT_NAME(foreign_key.[referenced_object_id]), foreign_key.[is_disabled],
                   foreign_key.[is_not_trusted], foreign_key.[delete_referential_action_desc],
                   foreign_key.[update_referential_action_desc]
            FROM [sys].[foreign_keys] AS foreign_key
            WHERE foreign_key.[parent_object_id] IN (SELECT [object_id] FROM @tables)
            ORDER BY foreign_key.[name];
            SELECT foreign_key.[name], foreign_key_column.[constraint_column_id],
                   parent_column.[name], referenced_column.[name]
            FROM [sys].[foreign_keys] AS foreign_key
            INNER JOIN [sys].[foreign_key_columns] AS foreign_key_column
                ON foreign_key_column.[constraint_object_id] = foreign_key.[object_id]
            INNER JOIN [sys].[columns] AS parent_column
                ON parent_column.[object_id] = foreign_key.[parent_object_id]
               AND parent_column.[column_id] = foreign_key_column.[parent_column_id]
            INNER JOIN [sys].[columns] AS referenced_column
                ON referenced_column.[object_id] = foreign_key.[referenced_object_id]
               AND referenced_column.[column_id] = foreign_key_column.[referenced_column_id]
            WHERE foreign_key.[parent_object_id] IN (SELECT [object_id] FROM @tables)
            ORDER BY foreign_key.[name], foreign_key_column.[constraint_column_id];
            SELECT check_value.[name], OBJECT_NAME(check_value.[parent_object_id]),
                   check_value.[is_disabled], check_value.[is_not_trusted], check_value.[definition]
            FROM [sys].[check_constraints] AS check_value
            WHERE check_value.[parent_object_id] IN (SELECT [object_id] FROM @tables)
            ORDER BY check_value.[name];
            SELECT index_value.[name], OBJECT_NAME(index_value.[object_id]), index_value.[is_unique],
                   index_value.[is_disabled], index_value.[is_primary_key], index_value.[is_unique_constraint],
                   index_value.[has_filter], index_value.[filter_definition]
            FROM [sys].[indexes] AS index_value
            WHERE index_value.[object_id] IN (SELECT [object_id] FROM @tables)
              AND index_value.[name] IS NOT NULL
            ORDER BY index_value.[name];
            SELECT index_value.[name], index_column.[key_ordinal], index_column.[index_column_id],
                   index_column.[is_descending_key], index_column.[is_included_column], column_value.[name]
            FROM [sys].[indexes] AS index_value
            INNER JOIN [sys].[index_columns] AS index_column
                ON index_column.[object_id] = index_value.[object_id]
               AND index_column.[index_id] = index_value.[index_id]
            INNER JOIN [sys].[columns] AS column_value
                ON column_value.[object_id] = index_column.[object_id]
               AND column_value.[column_id] = index_column.[column_id]
            WHERE index_value.[object_id] IN (SELECT [object_id] FROM @tables)
              AND index_value.[name] IS NOT NULL
            ORDER BY index_value.[name], index_column.[is_included_column],
                     index_column.[key_ordinal], index_column.[index_column_id];
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var triggerRows = new List<Issue250ObservedTrigger>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            triggerRows.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3)));
        }
        Assert.IsTrue(await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        var foreignKeyRows = new List<Issue250ObservedForeignKey>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            foreignKeyRows.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetString(5),
                reader.GetString(6)));
        }
        Assert.IsTrue(await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        var foreignKeyColumnRows = new List<Issue250ObservedForeignKeyColumn>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            foreignKeyColumnRows.Add(new(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3)));
        }
        Assert.IsTrue(await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        var checkRows = new List<Issue250ObservedCheckConstraint>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            checkRows.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetString(4)));
        }
        Assert.IsTrue(await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        var indexRows = new List<Issue250ObservedUniqueIndex>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            indexRows.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7)));
        }
        Assert.IsTrue(await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        var indexColumnRows = new List<Issue250ObservedIndexColumn>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            indexColumnRows.Add(new(
                reader.GetString(0),
                reader.GetByte(1),
                reader.GetInt32(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetString(5)));
        }

        var requiredTriggerByName = RequiredW3MTriggers.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var requiredForeignKeyByName = RequiredW3MForeignKeys.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var requiredCheckByName = RequiredW3MCheckConstraints.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var requiredIndexByName = RequiredW3MUniqueIndexes.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var observedTriggerByName = triggerRows.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var observedForeignKeyByName = foreignKeyRows.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var observedCheckByName = checkRows.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var observedIndexByName = indexRows.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var foreignKeyColumnsByName = foreignKeyColumnRows.GroupBy(item => item.ForeignKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Ordinal).ToArray(), StringComparer.Ordinal);
        var indexColumnsByName = indexColumnRows.GroupBy(item => item.Index, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var missingTriggers = MissingRequiredNames(requiredTriggerByName.Keys, observedTriggerByName.Keys);
        var disabledTriggers = RequiredW3MTriggers.Where(required => observedTriggerByName.TryGetValue(required.Name, out var observed)
                && observed.Disabled)
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var triggerTableMismatches = RequiredW3MTriggers.Where(required => observedTriggerByName.TryGetValue(required.Name, out var observed)
                && !string.Equals(required.Table, observed.Table, StringComparison.Ordinal))
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var unavailableTriggerDefinitions = RequiredW3MTriggers.Where(required => observedTriggerByName.TryGetValue(required.Name, out var observed)
                && string.IsNullOrWhiteSpace(observed.Definition))
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var triggerSemanticFailures = RequiredW3MTriggers.Where(required => observedTriggerByName.TryGetValue(required.Name, out var observed)
                && observed.Definition is not null
                && !ContainsAllSchemaConcepts(NormalizeSchemaDefinition(observed.Definition), required.ExpectedConcepts))
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();

        var missingForeignKeys = MissingRequiredNames(requiredForeignKeyByName.Keys, observedForeignKeyByName.Keys);
        var disabledForeignKeys = RequiredW3MForeignKeys.Where(required => observedForeignKeyByName.TryGetValue(required.Name, out var observed)
                && observed.Disabled)
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var untrustedForeignKeys = RequiredW3MForeignKeys.Where(required => observedForeignKeyByName.TryGetValue(required.Name, out var observed)
                && observed.Untrusted)
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var foreignKeyRelationMismatches = RequiredW3MForeignKeys.Where(required => observedForeignKeyByName.TryGetValue(required.Name, out var observed)
                && (!string.Equals(required.ParentTable, observed.ParentTable, StringComparison.Ordinal)
                    || !string.Equals(required.ReferencedTable, observed.ReferencedTable, StringComparison.Ordinal)))
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var foreignKeyColumnMappingMismatches = RequiredW3MForeignKeys.Where(required => observedForeignKeyByName.ContainsKey(required.Name)
                && (!required.ParentColumns.SequenceEqual(
                        ForeignKeyParentColumns(foreignKeyColumnsByName, required.Name), StringComparer.Ordinal)
                    || !required.ReferencedColumns.SequenceEqual(
                        ForeignKeyReferencedColumns(foreignKeyColumnsByName, required.Name), StringComparer.Ordinal)))
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var foreignKeyActionMismatches = RequiredW3MForeignKeys.Where(required => observedForeignKeyByName.TryGetValue(required.Name, out var observed)
                && (!string.Equals(required.DeleteAction, observed.DeleteAction, StringComparison.Ordinal)
                    || !string.Equals(required.UpdateAction, observed.UpdateAction, StringComparison.Ordinal)))
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();

        var missingChecks = MissingRequiredNames(requiredCheckByName.Keys, observedCheckByName.Keys);
        var disabledChecks = RequiredW3MCheckConstraints.Where(required => observedCheckByName.TryGetValue(required.Name, out var observed)
                && observed.Disabled)
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var untrustedChecks = RequiredW3MCheckConstraints.Where(required => observedCheckByName.TryGetValue(required.Name, out var observed)
                && observed.Untrusted)
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var checkTableMismatches = RequiredW3MCheckConstraints.Where(required => observedCheckByName.TryGetValue(required.Name, out var observed)
                && !string.Equals(required.Table, observed.Table, StringComparison.Ordinal))
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var checkSemanticFailures = RequiredW3MCheckConstraints.Where(required => observedCheckByName.TryGetValue(required.Name, out var observed)
                && !ContainsAllSchemaConcepts(NormalizeSchemaDefinition(observed.Definition), required.ExpectedConcepts))
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();

        var missingUniqueIndexes = MissingRequiredNames(requiredIndexByName.Keys, observedIndexByName.Keys);
        var disabledUniqueIndexes = RequiredW3MUniqueIndexes.Where(required => observedIndexByName.TryGetValue(required.Name, out var observed)
                && observed.Disabled)
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var nonUniqueRequiredIndexes = RequiredW3MUniqueIndexes.Where(required => observedIndexByName.TryGetValue(required.Name, out var observed)
                && required.Unique && !observed.Unique)
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var uniqueIndexStateMismatches = RequiredW3MUniqueIndexes.Where(required => observedIndexByName.TryGetValue(required.Name, out var observed)
                && (!string.Equals(required.Table, observed.Table, StringComparison.Ordinal)
                    || !string.Equals(required.Kind, UniqueIndexKind(observed), StringComparison.Ordinal)
                    || required.Unique != observed.Unique
                    || required.Enabled == observed.Disabled))
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var uniqueIndexKeyColumnMismatches = RequiredW3MUniqueIndexes.Where(required => observedIndexByName.ContainsKey(required.Name)
                && !required.KeyColumns.SequenceEqual(IndexKeyColumns(indexColumnsByName, required.Name)))
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var uniqueIndexIncludedColumnMismatches = RequiredW3MUniqueIndexes.Where(required => observedIndexByName.ContainsKey(required.Name)
                && !required.IncludedColumns.SequenceEqual(
                    IndexIncludedColumns(indexColumnsByName, required.Name), StringComparer.Ordinal))
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var uniqueIndexFilterMismatches = RequiredW3MUniqueIndexes.Where(required => observedIndexByName.TryGetValue(required.Name, out var observed)
                && ((NormalizeIndexFilter(required.FilterDefinition) is not null) != observed.Filtered
                    || !string.Equals(NormalizeIndexFilter(required.FilterDefinition),
                        NormalizeIndexFilter(observed.FilterDefinition), StringComparison.Ordinal)))
            .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();

        var failures = new Issue250W3MSchemaIntegrityFailures(
            missingTriggers,
            disabledTriggers,
            triggerTableMismatches,
            unavailableTriggerDefinitions,
            triggerSemanticFailures,
            missingForeignKeys,
            disabledForeignKeys,
            untrustedForeignKeys,
            foreignKeyRelationMismatches,
            foreignKeyColumnMappingMismatches,
            foreignKeyActionMismatches,
            missingChecks,
            disabledChecks,
            untrustedChecks,
            checkTableMismatches,
            checkSemanticFailures,
            missingUniqueIndexes,
            disabledUniqueIndexes,
            nonUniqueRequiredIndexes,
            uniqueIndexStateMismatches,
            uniqueIndexKeyColumnMismatches,
            uniqueIndexIncludedColumnMismatches,
            uniqueIndexFilterMismatches);
        var allFailures = failures.All().ToArray();
        Assert.IsEmpty(allFailures,
            $"Required W3M schema integrity failed: {string.Join(", ", allFailures)}");

        var observedTriggers = triggerRows.Select(item =>
        {
            var required = requiredTriggerByName.GetValueOrDefault(item.Name);
            var normalized = item.Definition is null ? null : NormalizeSchemaDefinition(item.Definition);
            return new Issue250W3MTriggerEvidence(
                item.Name,
                item.Table,
                Enabled: !item.Disabled,
                Required: required is not null,
                DefinitionSha256: normalized is null ? null : Sha(normalized),
                SemanticGuardsSatisfied: required is null || normalized is null
                    ? null
                    : ContainsAllSchemaConcepts(normalized, required.ExpectedConcepts));
        }).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        var observedForeignKeys = foreignKeyRows.Select(item => new Issue250W3MForeignKeyEvidence(
                item.Name,
                item.ParentTable,
                ForeignKeyParentColumns(foreignKeyColumnsByName, item.Name),
                item.ReferencedTable,
                ForeignKeyReferencedColumns(foreignKeyColumnsByName, item.Name),
                item.DeleteAction,
                item.UpdateAction,
                Enabled: !item.Disabled,
                Trusted: !item.Untrusted,
                Required: requiredForeignKeyByName.ContainsKey(item.Name)))
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        var observedChecks = checkRows.Select(item =>
        {
            var required = requiredCheckByName.GetValueOrDefault(item.Name);
            var normalized = NormalizeSchemaDefinition(item.Definition);
            return new Issue250W3MCheckConstraintEvidence(
                item.Name,
                item.Table,
                Enabled: !item.Disabled,
                Trusted: !item.Untrusted,
                Required: required is not null,
                DefinitionSha256: Sha(normalized),
                SemanticGuardsSatisfied: required is null
                    ? null
                    : ContainsAllSchemaConcepts(normalized, required.ExpectedConcepts));
        }).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        var observedUniqueIndexes = indexRows
            .Where(item => item.Unique || requiredIndexByName.ContainsKey(item.Name))
            .Select(item => new Issue250W3MUniqueIndexEvidence(
                item.Name,
                item.Table,
                Unique: item.Unique,
                Enabled: !item.Disabled,
                Kind: UniqueIndexKind(item),
                KeyColumns: IndexKeyColumns(indexColumnsByName, item.Name),
                IncludedColumns: IndexIncludedColumns(indexColumnsByName, item.Name),
                FilterDefinition: NormalizeIndexFilter(item.FilterDefinition),
                Required: requiredIndexByName.ContainsKey(item.Name)))
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        return new(
            new(RequiredW3MTriggers, RequiredW3MForeignKeys, RequiredW3MCheckConstraints, RequiredW3MUniqueIndexes),
            new(observedTriggers, observedForeignKeys, observedChecks, observedUniqueIndexes),
            failures,
            "Trigger/check definitions are whitespace-normalized and SHA256-authenticated per run. Baseline and after may record different hashes, but every required semantic concept remains fail-closed; additive objects cannot replace required names.",
            "Every required W3M trigger, foreign key, check constraint, and unique index is present with exact table/relation/ordered-column/action/key/include/filter/kind and enabled/trusted/unique state.");
    }

    private static string[] MissingRequiredNames(IEnumerable<string> required, IEnumerable<string> observed)
        => required.Except(observed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string NormalizeSchemaDefinition(string definition)
        => Issue250Regex.Whitespace().Replace(definition, " ").Trim();

    private static string? NormalizeIndexFilter(string? definition)
    {
        if (string.IsNullOrWhiteSpace(definition))
        {
            return null;
        }
        var normalized = NormalizeSchemaDefinition(definition);
        while (HasRedundantOuterParentheses(normalized))
        {
            normalized = normalized[1..^1].Trim();
        }
        return normalized;
    }

    private static bool HasRedundantOuterParentheses(string value)
    {
        if (value.Length < 2 || value[0] != '(' || value[^1] != ')')
        {
            return false;
        }
        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            depth += value[index] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0
            };
            if (depth == 0 && index < value.Length - 1)
            {
                return false;
            }
            if (depth < 0)
            {
                return false;
            }
        }
        return depth == 0;
    }

    private static bool ContainsAllSchemaConcepts(string definition, IEnumerable<string> concepts)
        => concepts.All(concept => definition.Contains(concept, StringComparison.OrdinalIgnoreCase));

    private static string UniqueIndexKind(Issue250ObservedUniqueIndex index)
        => index.PrimaryKey ? "PrimaryKey" : index.UniqueConstraint ? "UniqueConstraint" :
            index.Unique ? "UniqueIndex" : "NonUniqueIndex";

    private static string[] ForeignKeyParentColumns(
        IReadOnlyDictionary<string, Issue250ObservedForeignKeyColumn[]> columnsByName,
        string name)
        => columnsByName.GetValueOrDefault(name)?.Select(item => item.ParentColumn).ToArray() ?? [];

    private static string[] ForeignKeyReferencedColumns(
        IReadOnlyDictionary<string, Issue250ObservedForeignKeyColumn[]> columnsByName,
        string name)
        => columnsByName.GetValueOrDefault(name)?.Select(item => item.ReferencedColumn).ToArray() ?? [];

    private static Issue250IndexKeyColumn[] IndexKeyColumns(
        IReadOnlyDictionary<string, Issue250ObservedIndexColumn[]> columnsByName,
        string name)
        => columnsByName.GetValueOrDefault(name)?.Where(item => !item.Included && item.KeyOrdinal > 0)
            .OrderBy(item => item.KeyOrdinal)
            .Select(item => new Issue250IndexKeyColumn(item.Column, item.Descending)).ToArray() ?? [];

    private static string[] IndexIncludedColumns(
        IReadOnlyDictionary<string, Issue250ObservedIndexColumn[]> columnsByName,
        string name)
        => columnsByName.GetValueOrDefault(name)?.Where(item => item.Included)
            .OrderBy(item => item.IndexColumnId).Select(item => item.Column).ToArray() ?? [];

    private static async Task<Issue250W3MTopologyEvidence> ReadW3MTopologyAsync(
        SqlConnection connection,
        Guid sentinelFrameId,
        Guid historyFrameId,
        Guid historyDevicePublicId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = W3MCommandTimeoutSeconds;
        command.CommandText = """
            SELECT COUNT_BIG(*),
                   SUM(CASE WHEN [Id] = @history_frame_id THEN CAST(1 AS bigint) ELSE 0 END),
                   SUM(CASE WHEN [Id] = @sentinel_frame_id THEN CAST(1 AS bigint) ELSE 0 END)
            FROM [CentralFrames];
            SELECT COUNT_BIG(*), COUNT_BIG(DISTINCT [Id]), COUNT_BIG(DISTINCT [ArtifactId]),
                   COUNT_BIG(DISTINCT [IdempotencyKey]), COUNT_BIG(DISTINCT [StorageReference]),
                   SUM(CASE WHEN [DevicePublicId] = @history_device_public_id THEN CAST(1 AS bigint) ELSE 0 END)
            FROM [CentralArtifacts] WHERE [CentralFrameId] = @history_frame_id;
            SELECT COUNT_BIG(DISTINCT item.[RecordId])
            FROM [CentralTransientPayloadReleaseItems] AS item
            INNER JOIN [CentralTransientPayloadReleases] AS release ON release.[ReleaseId] = item.[ReleaseId]
            WHERE release.[State] = N'Completed' AND item.[Outcome] = N'Released';
            """;
        command.Parameters.AddWithValue("@sentinel_frame_id", sentinelFrameId);
        command.Parameters.AddWithValue("@history_frame_id", historyFrameId);
        command.Parameters.AddWithValue("@history_device_public_id", historyDevicePublicId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        var totalFrames = reader.GetInt64(0);
        var historyFrames = reader.GetInt64(1);
        var sentinelFrames = reader.GetInt64(2);
        Assert.IsTrue(await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        var historyArtifacts = reader.GetInt64(0);
        var distinctHistoryArtifactRecordIds = reader.GetInt64(1);
        var distinctHistoryArtifactIds = reader.GetInt64(2);
        var distinctHistoryIdempotencyKeys = reader.GetInt64(3);
        var distinctHistoryStorageReferences = reader.GetInt64(4);
        var matchingDevicePublicIds = reader.GetInt64(5);
        Assert.IsTrue(await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new(
            totalFrames,
            historyFrames,
            sentinelFrames,
            historyArtifacts,
            distinctHistoryArtifactRecordIds,
            distinctHistoryArtifactIds,
            distinctHistoryIdempotencyKeys,
            distinctHistoryStorageReferences,
            matchingDevicePublicIds,
            reader.GetInt64(0));
    }

    private static async Task<Issue250HealthEvidence> ReadHealthAsync(
        string connectionString,
        DateTimeOffset now,
        string phase,
        CancellationToken cancellationToken)
    {
        await using var db = CreateContext(connectionString);
        var check = new CentralTransientLifecycleHealthCheck(
            db,
            new Issue250FixedTimeProvider(now),
            Options.Create(new CentralTransientNotificationOptions()),
            Options.Create(new CentralTransientPayloadReleaseOptions
            {
                Enabled = true,
                PollInterval = TimeSpan.FromSeconds(5)
            }));
        var result = await check.CheckHealthAsync(new HealthCheckContext(), cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(phase == "after" ? 14 : 6, result.Data.Count);
        return new(
            result.Status.ToString(),
            Convert.ToInt32(result.Data["PendingPayloadReleaseCount"], CultureInfo.InvariantCulture),
            Convert.ToDouble(result.Data["OldestPendingPayloadReleaseAgeSeconds"], CultureInfo.InvariantCulture),
            phase == "after"
                ? Convert.ToInt32(result.Data["PendingPayloadReleaseItemCount"], CultureInfo.InvariantCulture)
                : 0,
            phase == "after"
                ? Convert.ToInt32(result.Data["RetryDuePayloadReleaseItemCount"], CultureInfo.InvariantCulture)
                : 0,
            phase == "after"
                ? Convert.ToInt32(result.Data["ReservedPayloadReleaseItemCount"], CultureInfo.InvariantCulture)
                : 0,
            phase == "after"
                ? Convert.ToInt32(result.Data["StaleReservedPayloadReleaseItemCount"], CultureInfo.InvariantCulture)
                : 0,
            phase == "after"
                ? Convert.ToInt64(result.Data["ReservedPayloadReleaseLogicalBytes"], CultureInfo.InvariantCulture)
                : 0,
            phase == "after"
                ? Convert.ToDouble(result.Data["OldestReservedPayloadReleaseItemAgeSeconds"], CultureInfo.InvariantCulture)
                : 0,
            phase == "after"
                ? Convert.ToInt64(result.Data["PendingPayloadReleaseLogicalBytes"], CultureInfo.InvariantCulture)
                : 0,
            phase == "after"
                ? Convert.ToDouble(result.Data["OldestPendingPayloadReleaseItemAgeSeconds"], CultureInfo.InvariantCulture)
                : 0,
            phase == "after"
                ? "Exact durable pending, retry, and reservation item counts, bytes, and ages are recorded."
                : "N/A at baseline: health exposes exact parent count/age only; after capability requires durable item retry count/age fields before evidence can run.");
    }

    private static async Task<Issue250W3MCounts> ReadW3MCountsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN [State] = N'Completed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN [State] = N'Pending' THEN 1 ELSE 0 END),
                COUNT(DISTINCT [CentralTransientEventId])
            FROM [CentralTransientPayloadReleases];
            SELECT
                SUM(CASE WHEN [Outcome] <> N'Pending' THEN CAST(1 AS bigint) ELSE 0 END),
                SUM(CASE WHEN [Outcome] = N'Pending' THEN CAST(1 AS bigint) ELSE 0 END)
            FROM [CentralTransientPayloadReleaseItems];
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        var completed = reader.GetInt32(0);
        var pending = reader.GetInt32(1);
        var events = reader.GetInt32(2);
        Assert.IsTrue(await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new Issue250W3MCounts(completed, reader.GetInt64(0), pending, reader.GetInt64(1), events);
    }

    private static async Task<long> ReadW3MValidTerminalTargetsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM [CentralTransientPayloadReleaseItems] AS item
            INNER JOIN [CentralArtifacts] AS artifact
                ON item.[Kind] = N'SourceArtifact' AND artifact.[Id] = item.[RecordId]
            INNER JOIN [CentralTransientPayloadReleases] AS release
                ON release.[ReleaseId] = item.[ReleaseId]
            WHERE release.[State] = N'Completed'
              AND item.[Outcome] = N'Released'
              AND item.[ReleasedUtc] IS NOT NULL
              AND artifact.[ObjectState] = N'Expired'
              AND artifact.[StateReasonCode] = N'transient-retention.evidence-released';
            """;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadW3MInvalidCanonicalRequestHashesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM [CentralTransientPayloadReleases]
            WHERE [CanonicalRequestSha256] <>
                CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varchar(max),
                    'central-transient-payload-release-v1' + CHAR(10) +
                    LOWER(REPLACE(CONVERT(char(36), [CentralTransientEventId]), '-', '')))), 2);
            """;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadW3MCanonicalRequestShaAsync(
        SqlConnection connection,
        Guid releaseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [CanonicalRequestSha256] FROM [CentralTransientPayloadReleases] WHERE [ReleaseId] = @id;";
        command.Parameters.AddWithValue("@id", releaseId);
        return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("W3M pending release request identity is absent."));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The command text and parameters are captured directly from EF Core production ProcessNextAsync.")]
    private static async Task<Issue250PlanEvidence> CapturePlanAsync(
        SqlConnection connection,
        Issue250CapturedCommand captured,
        Guid expectedReleaseId,
        string expectedIndex,
        CancellationToken cancellationToken)
    {
        var messages = new StringBuilder();
        connection.InfoMessage += (_, args) => messages.AppendLine(args.Message);
        var plan = string.Empty;
        var selectedRows = 0;
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = $"SET STATISTICS XML ON; SET STATISTICS IO ON; {captured.CommandText} SET STATISTICS IO OFF; SET STATISTICS XML OFF;";
        command.Parameters.AddRange(captured.Parameters.Select(CopySqlParameter).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        do
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.FieldCount == 1 && reader.GetValue(0) is string value
                    && value.Contains("ShowPlanXML", StringComparison.Ordinal))
                {
                    plan = value;
                }
                else
                {
                    selectedRows++;
                    Assert.AreEqual(expectedReleaseId, reader.GetGuid(0));
                }
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        Assert.IsFalse(string.IsNullOrWhiteSpace(plan));
        var normalized = Issue250Regex.Whitespace().Replace(captured.CommandText, " ").Trim();
        var logicalReads = Issue250Regex.LogicalReads().Matches(messages.ToString())
            .Sum(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        var planDocument = XDocument.Parse(plan);
        var operators = planDocument.Descendants()
            .Where(element => element.Name.LocalName == "RelOp")
            .Select(element => string.Join('/',
                element.Attribute("LogicalOp")?.Value ?? "unknown",
                element.Attribute("PhysicalOp")?.Value ?? "unknown"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var indexes = planDocument.Descendants()
            .Where(element => element.Name.LocalName == "Object")
            .Select(element => element.Attribute("Index")?.Value?.Trim('[', ']'))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var attribute in planDocument.Descendants().Attributes()
                     .Where(attribute => attribute.Name.LocalName is "Database" or "Server" or "DatabaseContextSettingsId"))
        {
            attribute.Value = "[scrubbed]";
        }
        foreach (var attribute in planDocument.Descendants().Attributes()
                     .Where(attribute => Issue250Regex.GuidN().IsMatch(attribute.Value)))
        {
            attribute.Value = Issue250Regex.GuidN().Replace(attribute.Value, "[scrubbed]");
        }
        var canonicalPlanXml = planDocument.ToString(SaveOptions.DisableFormatting);
        var normalizedFacts = JsonSerializer.Serialize(new { operators, indexes, SelectedRows = selectedRows });
        return new Issue250PlanEvidence(
            normalized,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))),
            canonicalPlanXml,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPlanXml))),
            logicalReads,
            selectedRows,
            plan.Contains(expectedIndex, StringComparison.Ordinal),
            normalizedFacts,
            Sha(normalizedFacts),
            operators,
            indexes);
    }

    private static SqlParameter CopySqlParameter(SqlParameter source)
        => new(source.ParameterName, source.SqlDbType, source.Size)
        {
            Direction = source.Direction,
            IsNullable = source.IsNullable,
            Precision = source.Precision,
            Scale = source.Scale,
            Value = source.Value
        };

    private static async Task<IReadOnlyList<Issue250SqlSample>> SampleSqlAsync(
        string connectionString,
        string applicationName,
        long origin,
        IReadOnlyList<Issue250ReleaseCase> cases,
        Guid? writerTargetRecordId,
        CancellationToken cancellationToken)
    {
        var samples = new List<Issue250SqlSample>();
        var next = TimeSpan.Zero;
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                samples.Add(await ReadSqlSampleAsync(
                    connection,
                    applicationName,
                    cases,
                    writerTargetRecordId,
                    origin,
                    cancellationToken).ConfigureAwait(false));
                next += TimeSpan.FromMilliseconds(10);
                var remaining = next - Stopwatch.GetElapsedTime(origin);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SqlException) when (cancellationToken.IsCancellationRequested)
        {
        }
        return samples;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only fixed SQL and generated parameter names are interpolated; every resource and event value remains parameterized.")]
    private static async Task<Issue250SqlSample> ReadSqlSampleAsync(
        SqlConnection connection,
        string applicationName,
        IReadOnlyList<Issue250ReleaseCase> cases,
        Guid? writerTargetRecordId,
        long origin,
        CancellationToken cancellationToken)
    {
        var observationStartedMilliseconds = Stopwatch.GetElapsedTime(origin).TotalMilliseconds;
        int sessions;
        int requests;
        int transactions;
        int[] transactionsByCase;
        int applicationLocks;
        int objectLocks;
        int parentLocks;
        int blockedWriters;
        int attributedBlockedWriters;
        int unexpectedApplicationLocks;
        int? objectLockOwnerSessionId;
        int? parentLockOwnerSessionId;
        int? writerSessionId;
        int? blockerSessionId;
        long? blockerTransactionId;
        Issue250RowBlockEvidence? rowBlock = null;
        long logBytes;
        double observedMilliseconds;
        var eventIds = cases.Select(item => item.EventId).ToArray();
        var caseApplicationNames = cases.Select((_, index) => CreateCaseApplicationName(applicationName, index)).ToArray();
        var caseApplicationParameters = caseApplicationNames.Select((_, index) => $"@case_application_{index}").ToArray();
        var writerCaseIndex = writerTargetRecordId.HasValue
            ? cases.Select((item, index) => new { item, index }).Single(value =>
                value.item.Objects.Any(target => target.RecordId == writerTargetRecordId.Value)).index
            : -1;
        var releaseIds = new List<Guid>();
        await using (var releases = connection.CreateCommand())
        {
            var names = eventIds.Select((_, index) => $"@event_{index}").ToArray();
            releases.CommandText = $"SELECT [ReleaseId] FROM [CentralTransientPayloadReleases] WHERE [CentralTransientEventId] IN ({string.Join(',', names)});";
            for (var index = 0; index < eventIds.Length; index++)
            {
                releases.Parameters.AddWithValue(names[index], eventIds[index]);
            }
            await using var reader = await releases.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                releaseIds.Add(reader.GetGuid(0));
            }
        }
        var objectResources = cases.SelectMany(item => item.Objects)
            .Select(item => CreateExpectedObjectLockResource(BucketPrefix + item.Key))
            .Distinct(StringComparer.Ordinal).ToArray();
        var parentResources = releaseIds.Select(id => CreateExpectedObjectLockResource(
                $"central-transient-payload-release:{id:N}"))
            .Distinct(StringComparer.Ordinal).ToArray();
        foreach (var (releaseId, resource) in releaseIds.Zip(parentResources))
        {
            Assert.AreEqual(resource, CentralObjectApplicationLock.CreateResource(
                $"central-transient-payload-release:{releaseId:N}"));
        }
        await using (var dmv = connection.CreateCommand())
        {
            var objectNames = objectResources.Select((_, index) => $"@object_resource_{index}").ToArray();
            var eventNames = eventIds.Select((_, index) => $"@parent_event_{index}").ToArray();
            // SQL Server exposes only the first 32 resource-name characters in DMV text and hashes the remainder.
            // The visible value therefore matches the exact transformed production resource, not a raw storage prefix.
            var objectPredicate = objectNames.Length == 0
                ? "1 = 0"
                : $"({string.Join(" OR ", objectNames.Select(name => $"CHARINDEX(LEFT({name}, 32), [resource_description]) > 0"))})";
            var parentPredicate = eventNames.Length == 0
                ? "1 = 0"
                : $"EXISTS (SELECT 1 FROM [CentralTransientPayloadReleases] AS expected_parent WHERE expected_parent.[CentralTransientEventId] IN ({string.Join(',', eventNames)}) AND CHARINDEX(LEFT('hvo-central-object:' + CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varchar(max), 'central-transient-payload-release:' + LOWER(REPLACE(CONVERT(char(36), expected_parent.[ReleaseId]), '-', '')))), 2), 32), [resource_description]) > 0)";
            var caseTransactionColumns = string.Concat(caseApplicationParameters.Select(name => $@",
                    (SELECT COUNT(DISTINCT session_transaction.[session_id])
                     FROM [sys].[dm_tran_session_transactions] AS session_transaction
                     INNER JOIN [sys].[dm_exec_sessions] AS session
                         ON session.[session_id] = session_transaction.[session_id]
                     WHERE session.[program_name] = {name})"));
            dmv.CommandText = $$"""
                DECLARE @attributed TABLE ([session_id] smallint PRIMARY KEY);
                INSERT INTO @attributed ([session_id])
                SELECT [session_id] FROM [sys].[dm_exec_sessions]
                WHERE [program_name] IN ({{string.Join(',', caseApplicationParameters)}});
                DECLARE @application_locks TABLE
                (
                    [request_session_id] smallint NOT NULL,
                    [resource_description] nvarchar(256) NOT NULL
                );
                INSERT INTO @application_locks ([request_session_id], [resource_description])
                SELECT [request_session_id], [resource_description]
                FROM [sys].[dm_tran_locks]
                WHERE [request_session_id] IN (SELECT [session_id] FROM @attributed)
                  AND [resource_type] = N'APPLICATION' AND [request_owner_type] = N'SESSION'
                  AND [request_status] = N'GRANT';
                DECLARE @row_block TABLE
                (
                    [writer_session_id] smallint NOT NULL,
                    [blocker_session_id] smallint NOT NULL,
                    [blocker_transaction_id] bigint NOT NULL,
                    [database_id] int NOT NULL,
                    [writer_isolation_level] smallint NOT NULL,
                    [blocker_isolation_level] smallint NOT NULL,
                    [wait_type] nvarchar(60) NULL,
                    [resource_type] nvarchar(60) NOT NULL,
                    [resource_description] nvarchar(256) NOT NULL,
                    [resource_associated_entity_id] bigint NOT NULL,
                    [table_name] sysname NOT NULL,
                    [index_name] sysname NOT NULL,
                    [writer_request_mode] nvarchar(60) NOT NULL,
                    [writer_request_status] nvarchar(60) NOT NULL,
                    [writer_request_owner_type] nvarchar(60) NOT NULL,
                    [blocker_request_mode] nvarchar(60) NOT NULL,
                    [blocker_request_status] nvarchar(60) NOT NULL,
                    [blocker_request_owner_type] nvarchar(60) NOT NULL
                );
                INSERT INTO @row_block
                SELECT TOP(1)
                    request.[session_id], request.[blocking_session_id], blocker_transaction.[transaction_id],
                    waiting.[resource_database_id], writer.[transaction_isolation_level],
                    blocker.[transaction_isolation_level], request.[wait_type], waiting.[resource_type],
                    waiting.[resource_description], waiting.[resource_associated_entity_id],
                    table_definition.[name], index_definition.[name], waiting.[request_mode],
                    waiting.[request_status], waiting.[request_owner_type], granted.[request_mode],
                    granted.[request_status], granted.[request_owner_type]
                FROM [sys].[dm_exec_requests] AS request
                INNER JOIN [sys].[dm_exec_sessions] AS writer ON writer.[session_id] = request.[session_id]
                INNER JOIN [sys].[dm_exec_sessions] AS blocker ON blocker.[session_id] = request.[blocking_session_id]
                INNER JOIN [sys].[dm_tran_locks] AS waiting ON waiting.[request_session_id] = request.[session_id]
                    AND waiting.[request_status] = N'WAIT' AND waiting.[resource_type] = N'KEY'
                    AND waiting.[request_owner_type] = N'TRANSACTION'
                INNER JOIN [sys].[dm_tran_locks] AS granted ON granted.[request_session_id] = request.[blocking_session_id]
                    AND granted.[request_status] = N'GRANT' AND granted.[resource_type] = waiting.[resource_type]
                    AND granted.[request_owner_type] = N'TRANSACTION'
                    AND granted.[resource_database_id] = waiting.[resource_database_id]
                    AND granted.[resource_associated_entity_id] = waiting.[resource_associated_entity_id]
                    AND granted.[resource_description] = waiting.[resource_description]
                INNER JOIN [sys].[dm_tran_session_transactions] AS blocker_transaction
                    ON blocker_transaction.[session_id] = blocker.[session_id]
                    AND blocker_transaction.[transaction_id] = granted.[request_owner_id]
                INNER JOIN [sys].[partitions] AS partition_definition
                    ON partition_definition.[hobt_id] = waiting.[resource_associated_entity_id]
                INNER JOIN [sys].[indexes] AS index_definition
                    ON index_definition.[object_id] = partition_definition.[object_id]
                    AND index_definition.[index_id] = partition_definition.[index_id]
                INNER JOIN [sys].[tables] AS table_definition
                    ON table_definition.[object_id] = partition_definition.[object_id]
                WHERE writer.[program_name] = @writer_application_name
                  AND blocker.[program_name] = @blocker_application_name
                  AND waiting.[resource_database_id] = DB_ID()
                  AND table_definition.[name] = N'CentralArtifacts'
                  AND index_definition.[name] = N'PK_CentralArtifacts'
                ORDER BY request.[session_id], blocker_transaction.[transaction_id];
                SELECT
                    (SELECT COUNT(*) FROM @attributed),
                    (SELECT COUNT(*) FROM [sys].[dm_exec_requests]
                        WHERE [session_id] IN (SELECT [session_id] FROM @attributed)),
                    (SELECT COUNT(DISTINCT [session_id]) FROM [sys].[dm_tran_session_transactions]
                        WHERE [session_id] IN (SELECT [session_id] FROM @attributed)),
                    (SELECT COUNT(DISTINCT [request_session_id]) FROM @application_locks),
                    (SELECT COUNT(DISTINCT [request_session_id]) FROM @application_locks WHERE {{objectPredicate}}),
                    (SELECT COUNT(DISTINCT [request_session_id]) FROM @application_locks WHERE {{parentPredicate}}),
                    (SELECT COUNT(*) FROM [sys].[dm_exec_requests] AS request
                        INNER JOIN [sys].[dm_exec_sessions] AS session ON session.[session_id] = request.[session_id]
                        WHERE session.[program_name] = @writer_application_name AND request.[blocking_session_id] <> 0),
                    (SELECT COUNT(*) FROM [sys].[dm_exec_requests] AS request
                        INNER JOIN [sys].[dm_exec_sessions] AS writer ON writer.[session_id] = request.[session_id]
                        INNER JOIN [sys].[dm_exec_sessions] AS blocker ON blocker.[session_id] = request.[blocking_session_id]
                        WHERE writer.[program_name] = @writer_application_name
                          AND blocker.[program_name] = @blocker_application_name),
                    (SELECT COUNT(*) FROM @application_locks
                        WHERE NOT ({{objectPredicate}}) AND NOT ({{parentPredicate}})),
                    (SELECT MIN(CONVERT(int, [request_session_id])) FROM @application_locks WHERE {{objectPredicate}}),
                    (SELECT MIN(CONVERT(int, [request_session_id])) FROM @application_locks WHERE {{parentPredicate}}),
                    (SELECT MIN(CONVERT(int, request.[session_id])) FROM [sys].[dm_exec_requests] AS request
                        INNER JOIN [sys].[dm_exec_sessions] AS writer ON writer.[session_id] = request.[session_id]
                        WHERE writer.[program_name] = @writer_application_name AND request.[blocking_session_id] <> 0),
                    (SELECT MIN(CONVERT(int, request.[blocking_session_id])) FROM [sys].[dm_exec_requests] AS request
                        INNER JOIN [sys].[dm_exec_sessions] AS writer ON writer.[session_id] = request.[session_id]
                        INNER JOIN [sys].[dm_exec_sessions] AS blocker ON blocker.[session_id] = request.[blocking_session_id]
                        WHERE writer.[program_name] = @writer_application_name
                          AND blocker.[program_name] = @blocker_application_name),
                    (SELECT MIN(session_transaction.[transaction_id]) FROM [sys].[dm_exec_requests] AS request
                        INNER JOIN [sys].[dm_exec_sessions] AS writer ON writer.[session_id] = request.[session_id]
                        INNER JOIN [sys].[dm_exec_sessions] AS blocker ON blocker.[session_id] = request.[blocking_session_id]
                        INNER JOIN [sys].[dm_tran_session_transactions] AS session_transaction
                            ON session_transaction.[session_id] = blocker.[session_id]
                        WHERE writer.[program_name] = @writer_application_name
                          AND blocker.[program_name] = @blocker_application_name),
                    (SELECT COALESCE(SUM(database_transaction.[database_transaction_log_bytes_used]), 0)
                        FROM [sys].[dm_tran_database_transactions] AS database_transaction
                        INNER JOIN [sys].[dm_tran_session_transactions] AS session_transaction
                            ON session_transaction.[transaction_id] = database_transaction.[transaction_id]
                        WHERE session_transaction.[session_id] IN (SELECT [session_id] FROM @attributed)
                          AND database_transaction.[database_id] = DB_ID())
                    {{caseTransactionColumns}};
                SELECT [writer_session_id], [blocker_session_id], [blocker_transaction_id], [database_id],
                    [writer_isolation_level], [blocker_isolation_level], [wait_type], [resource_type],
                    [resource_description], [resource_associated_entity_id], [table_name], [index_name],
                    [writer_request_mode], [writer_request_status], [writer_request_owner_type],
                    [blocker_request_mode], [blocker_request_status], [blocker_request_owner_type],
                    (SELECT COUNT(*) FROM @application_locks AS application_lock
                        WHERE application_lock.[request_session_id] = row_block.[blocker_session_id])
                FROM @row_block AS row_block;
                """;
            for (var index = 0; index < caseApplicationNames.Length; index++)
            {
                dmv.Parameters.AddWithValue(caseApplicationParameters[index], caseApplicationNames[index]);
            }
            dmv.Parameters.AddWithValue(
                "@blocker_application_name",
                writerCaseIndex >= 0 ? caseApplicationNames[writerCaseIndex] : applicationName + ".Case.none");
            dmv.Parameters.AddWithValue(
                "@writer_application_name",
                writerTargetRecordId.HasValue
                    ? CreateWriterApplicationName(applicationName, writerTargetRecordId.Value)
                    : applicationName + ".Writer.none");
            for (var index = 0; index < objectResources.Length; index++)
            {
                dmv.Parameters.AddWithValue(objectNames[index], objectResources[index]);
            }
            for (var index = 0; index < eventIds.Length; index++)
            {
                dmv.Parameters.AddWithValue(eventNames[index], eventIds[index]);
            }
            await using var reader = await dmv.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            sessions = reader.GetInt32(0);
            requests = reader.GetInt32(1);
            transactions = reader.GetInt32(2);
            applicationLocks = reader.GetInt32(3);
            objectLocks = reader.GetInt32(4);
            parentLocks = reader.GetInt32(5);
            blockedWriters = reader.GetInt32(6);
            attributedBlockedWriters = reader.GetInt32(7);
            unexpectedApplicationLocks = reader.GetInt32(8);
            objectLockOwnerSessionId = await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(9);
            parentLockOwnerSessionId = await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(10);
            writerSessionId = await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(11);
            blockerSessionId = await reader.IsDBNullAsync(12, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(12);
            blockerTransactionId = await reader.IsDBNullAsync(13, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(13);
            logBytes = reader.GetInt64(14);
            transactionsByCase = Enumerable.Range(0, caseApplicationNames.Length)
                .Select(index => reader.GetInt32(15 + index)).ToArray();
            observedMilliseconds = Stopwatch.GetElapsedTime(origin).TotalMilliseconds;
            Assert.IsTrue(await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Assert.IsTrue(writerTargetRecordId.HasValue);
                rowBlock = new Issue250RowBlockEvidence(
                    Sha(writerTargetRecordId.Value.ToString("N")),
                    reader.GetInt16(0),
                    reader.GetInt16(1),
                    reader.GetInt64(2),
                    reader.GetInt32(3),
                    true,
                    true,
                    true,
                    true,
                    reader.GetInt16(4),
                    reader.GetInt16(5),
                    await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? "unknown" : reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetInt64(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    reader.GetString(13),
                    reader.GetString(14),
                    reader.GetString(15),
                    reader.GetString(16),
                    reader.GetString(17),
                    reader.GetInt32(18) == 0);
            }
        }

        Issue250Backlog? backlog = null;
        var unavailable = false;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SET LOCK_TIMEOUT 5;
                SELECT
                    (SELECT COUNT_BIG(*) FROM [CentralTransientPayloadReleases] WHERE [State] = N'Pending'),
                    (SELECT COUNT_BIG(*) FROM [CentralTransientPayloadReleaseItems] WHERE [Outcome] = N'Pending'),
                    (SELECT COALESCE(SUM(CASE WHEN item.[Kind] = N'SourceArtifact' THEN artifact.[ByteLength]
                                             ELSE intent.[ByteLength] END), 0)
                     FROM [CentralTransientPayloadReleaseItems] AS item
                     LEFT JOIN [CentralArtifacts] AS artifact
                        ON item.[Kind] = N'SourceArtifact' AND artifact.[Id] = item.[RecordId]
                     LEFT JOIN [CentralTransientDerivativeOutputIntents] AS intent
                        ON item.[Kind] = N'Derivative' AND intent.[Id] = item.[RecordId]
                     WHERE item.[Outcome] = N'Pending'),
                    (SELECT COALESCE(MAX(DATEDIFF_BIG(millisecond, [CreatedUtc], SYSUTCDATETIME())), 0)
                     FROM [CentralTransientPayloadReleases] WHERE [State] = N'Pending');
                SET LOCK_TIMEOUT -1;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            backlog = new Issue250Backlog(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }
        catch (SqlException exception) when (exception.Number == 1222)
        {
            unavailable = true;
        }
        return new Issue250SqlSample(
            observedMilliseconds,
            observationStartedMilliseconds,
            sessions,
            requests,
            transactions,
            transactionsByCase,
            applicationLocks,
            objectLocks,
            parentLocks,
            blockedWriters,
            attributedBlockedWriters,
            unexpectedApplicationLocks,
            objectLockOwnerSessionId,
            parentLockOwnerSessionId,
            writerSessionId,
            blockerSessionId,
            blockerTransactionId,
            rowBlock,
            logBytes,
            backlog?.PendingParents,
            backlog?.PendingItems,
            backlog?.LogicalPendingBytes,
            backlog?.OldestAgeMilliseconds,
            unavailable);
    }

    private static async Task<Issue250WriterEvidence> ExecuteWriterAsync(
        string connectionString,
        string applicationName,
        Guid recordId,
        TaskCompletionSource ready,
        CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = CreateWriterApplicationName(applicationName, recordId)
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "UPDATE [CentralArtifacts] SET [StateReasonCode] = [StateReasonCode] WHERE [Id] = @id;";
        command.Parameters.AddWithValue("@id", recordId);
        ready.TrySetResult();
        var started = Stopwatch.GetTimestamp();
        try
        {
            var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new Issue250WriterEvidence("completed", Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                rows == 1, 1_000, Sha(recordId.ToString("N")));
        }
        catch (SqlException exception) when (exception.Number == -2)
        {
            return new Issue250WriterEvidence("timeout", Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                false, 1_000, Sha(recordId.ToString("N")));
        }
        catch (SqlException exception) when (exception.Number == 1205)
        {
            return new Issue250WriterEvidence("deadlock", Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                false, 1_000, Sha(recordId.ToString("N")));
        }
    }

    private static string CreateWriterApplicationName(string applicationName, Guid recordId)
        => $"{applicationName}.Writer.{Sha(recordId.ToString("N"))[..16]}";

    private static string CreateCaseApplicationName(string applicationName, int caseIndex)
        => $"{applicationName}.Case{caseIndex.ToString(CultureInfo.InvariantCulture)}";

    private static Dictionary<string, int> CreateDeleteCaseMap(
        IReadOnlyList<Issue250ReleaseCase> cases)
        => cases.SelectMany((release, caseIndex) => release.Objects.Select(item => new { item.Key, caseIndex }))
            .ToDictionary(item => item.Key, item => item.caseIndex, StringComparer.Ordinal);

    private static Issue250ReleaseScenarioEvidence CreateReleaseScenarioEvidence(
        int concurrency,
        int warmups,
        int measurements,
        IReadOnlyList<double> releaseLatencies,
        double wallMilliseconds,
        IReadOnlyList<Issue250ResourceEvidence> resources,
        Issue250CollectorSnapshot protocol,
        IReadOnlyList<Issue250CaseCorrectness> correctness,
        Issue250Backlog finalBacklog,
        long bytesPerCase)
    {
        var seconds = wallMilliseconds / 1_000d;
        return new Issue250ReleaseScenarioEvidence(
            concurrency,
            Math.Min(concurrency, measurements),
            warmups,
            measurements,
            wallMilliseconds,
            Latency(releaseLatencies),
            Latency(protocol.ObjectStore.DeleteDurationMilliseconds),
            measurements / seconds,
            measurements * 5 / seconds,
            measurements * bytesPerCase / seconds,
            CreateResourceAggregate(resources),
            CreateProtocolEvidence(protocol),
            CreateCorrectnessEvidence(correctness, finalBacklog));
    }

    private static Issue250ProtocolEvidence CreateProtocolEvidence(Issue250CollectorSnapshot protocol)
        => new(
            protocol.EfCommands,
            protocol.ExternalSqlDeadlockRetries,
            protocol.PrimarySqlDeadlockRetries,
            protocol.RecoverySqlDeadlockRetries,
            protocol.InspectionSqlDeadlockRetries,
            protocol.InternalCreationDeadlockRetries,
            protocol.InternalCreationAmbiguousCommitRetries,
            protocol.InternalCreationAmbiguousCommitRecoveries,
            protocol.InternalCreationConflictExhaustions,
            protocol.ReleaseAttempts,
            protocol.AcceptedReleaseResponses,
            protocol.RecoveryProcessorAttempts,
            protocol.RecoveryProcessorCompletions,
            protocol.PrimaryEfCommands,
            protocol.RecoveryEfCommands,
            protocol.ApplicationLocks,
            protocol.SqlTransactions.StartAttempts,
            protocol.SqlTransactions.SuccessfullyStarted,
            protocol.SqlTransactions.Committed,
            protocol.SqlTransactions.RolledBack,
            protocol.SqlTransactions.Failed,
            Latency(protocol.SqlTransactions.CommittedDurationMilliseconds),
            CreateTransactionCountEvidence(protocol.PrimarySqlTransactions),
            CreateTransactionCountEvidence(protocol.RecoverySqlTransactions),
            protocol.ObjectStore.Requests,
            protocol.ObjectStore.Deletes,
            protocol.ObjectStore.BucketHeadRequests,
            protocol.ObjectStore.ObjectHeadRequests,
            protocol.ObjectStore.OtherMethodRequests,
            protocol.ObjectStore.UniqueDeleteTargets,
            protocol.ObjectStore.DuplicateDeleteRequests,
            protocol.ObjectStore.DeleteResponses,
            protocol.ObjectStore.DeleteExceptions,
            protocol.ObjectStore.RequestEntityBytes,
            protocol.ObjectStore.ResponseEntityBytes,
            protocol.ObjectStore.UnknownRequestEntityLengths,
            protocol.ObjectStore.UnknownResponseEntityLengths,
            Latency(protocol.ObjectStore.DeleteDurationMilliseconds));

    private static Issue250TransactionCountEvidence CreateTransactionCountEvidence(
        Issue246TransactionSnapshot transactions)
        => new(
            transactions.StartAttempts,
            transactions.SuccessfullyStarted,
            transactions.Committed,
            transactions.RolledBack,
            transactions.Failed);

    private static void AssertProtocolAccounting(
        Issue250CollectorSnapshot protocol,
        int releases,
        string phase)
    {
        if (phase == "after")
        {
            var retries = protocol.ExternalSqlDeadlockRetries;
            var primaryRetries = protocol.PrimarySqlDeadlockRetries;
            var recoveryRetries = protocol.RecoverySqlDeadlockRetries;
            var inspectionRetries = protocol.InspectionSqlDeadlockRetries;
            var internalCreationRetries = protocol.InternalCreationDeadlockRetries
                + protocol.InternalCreationAmbiguousCommitRetries;
            var internalCreationConflictTerminals = internalCreationRetries
                + protocol.InternalCreationAmbiguousCommitRecoveries;
            var releaseAttempts = protocol.ReleaseAttempts;
            var recoveryAttempts = protocol.RecoveryProcessorAttempts;
            var recoveryCompletions = protocol.RecoveryProcessorCompletions;
            var primaryTransactions = protocol.PrimarySqlTransactions;
            var recoveryTransactions = protocol.RecoverySqlTransactions;
            Assert.AreEqual(0L, retries);
            Assert.AreEqual(0L, primaryRetries);
            Assert.AreEqual(0L, recoveryRetries);
            Assert.AreEqual(0L, inspectionRetries);
            Assert.AreEqual(retries, primaryRetries + recoveryRetries + inspectionRetries);
            Assert.AreEqual(releases, releaseAttempts);
            Assert.IsTrue(internalCreationRetries <= (long)releases * 3);
            Assert.IsTrue(protocol.InternalCreationAmbiguousCommitRecoveries <= releases);
            Assert.AreEqual(0L, protocol.InternalCreationConflictExhaustions);
            Assert.IsTrue(protocol.AcceptedReleaseResponses <= releases);
            Assert.IsTrue(recoveryAttempts >= protocol.AcceptedReleaseResponses);
            Assert.IsTrue(recoveryAttempts <= protocol.AcceptedReleaseResponses * 100);
            Assert.IsTrue(recoveryCompletions <= recoveryAttempts);
            Assert.IsTrue(protocol.PrimaryEfCommands >= releaseAttempts);
            Assert.IsTrue(protocol.PrimaryEfCommands <= releaseAttempts * 170);
            Assert.AreEqual(primaryTransactions.StartAttempts, primaryTransactions.SuccessfullyStarted);
            Assert.AreEqual(primaryTransactions.StartAttempts,
                primaryTransactions.Committed + primaryTransactions.RolledBack + primaryTransactions.Failed);
            Assert.IsTrue(primaryTransactions.StartAttempts >= releases);
            Assert.IsTrue(primaryTransactions.StartAttempts <= releaseAttempts * 20);
            Assert.IsTrue(primaryTransactions.RolledBack <= protocol.AcceptedReleaseResponses);
            Assert.IsTrue(primaryTransactions.Failed <= internalCreationConflictTerminals);
            Assert.IsTrue(protocol.RecoveryEfCommands >= recoveryAttempts);
            Assert.IsTrue(protocol.RecoveryEfCommands <= recoveryAttempts * 170);
            Assert.AreEqual(recoveryTransactions.StartAttempts, recoveryTransactions.SuccessfullyStarted);
            Assert.AreEqual(recoveryTransactions.StartAttempts,
                recoveryTransactions.Committed + recoveryTransactions.RolledBack + recoveryTransactions.Failed);
            Assert.IsTrue(recoveryTransactions.StartAttempts <= recoveryAttempts * 20);
            Assert.AreEqual(0L, recoveryTransactions.Failed);
            Assert.AreEqual(
                protocol.ObjectStore.Deletes + protocol.ObjectStore.BucketHeadRequests
                    + protocol.ObjectStore.ObjectHeadRequests + protocol.ObjectStore.OtherMethodRequests,
                protocol.ObjectStore.Requests);
            Assert.AreEqual(0L, protocol.ObjectStore.OtherMethodRequests);
            Assert.AreEqual((long)releases * 5, protocol.ObjectStore.UniqueDeleteTargets);
            Assert.AreEqual(
                protocol.ObjectStore.UniqueDeleteTargets + protocol.ObjectStore.DuplicateDeleteRequests,
                protocol.ObjectStore.Deletes);
            Assert.AreEqual(protocol.ObjectStore.BucketHeadRequests, protocol.ObjectStore.ObjectHeadRequests);
            Assert.AreEqual(
                protocol.ObjectStore.DeleteResponses + protocol.ObjectStore.DeleteExceptions,
                protocol.ObjectStore.Deletes);
            Assert.AreEqual(0L, protocol.ObjectStore.DeleteExceptions);
            Assert.AreEqual(0L, protocol.ObjectStore.RequestEntityBytes);
            Assert.AreEqual(0L, protocol.ObjectStore.ResponseEntityBytes);
            Assert.AreEqual(0L, protocol.ObjectStore.UnknownRequestEntityLengths);
            Assert.AreEqual(0L, protocol.ObjectStore.UnknownResponseEntityLengths);
        }

        var diagnostics = protocol.ApplicationLocks;
        var locksPerRelease = phase == "baseline" ? 6 : 8;
        var minimumLocks = (long)releases * locksPerRelease;
        var maximumLocks = minimumLocks
            + protocol.RecoveryProcessorAttempts * 23;
        if (phase == "baseline")
        {
            Assert.AreEqual(minimumLocks, diagnostics.GetApplicationLockStarts);
        }
        else
        {
            Assert.IsTrue(diagnostics.GetApplicationLockStarts >= minimumLocks);
            Assert.IsTrue(diagnostics.GetApplicationLockStarts <= maximumLocks,
                "Every excess application-lock acquisition must be attributable to a recorded recovery attempt.");
        }
        Assert.IsTrue(diagnostics.ReleaseApplicationLockStarts <= diagnostics.GetApplicationLockStarts);
        Assert.IsTrue(diagnostics.GetApplicationLockStarts - diagnostics.ReleaseApplicationLockStarts
            <= protocol.AcceptedReleaseResponses + protocol.RecoveryProcessorAttempts * 16);
        Assert.AreEqual(diagnostics.TotalStarts,
            diagnostics.TotalCompletions + diagnostics.TotalErrors);
        Assert.AreEqual(0L, diagnostics.TotalErrors);
        Assert.AreEqual("complete", diagnostics.CompletionCoverage);
    }

    private static void AssertNoNaturalLeaseRecovery(Issue250CollectorSnapshot protocol)
    {
        Assert.AreEqual(0L, protocol.AcceptedReleaseResponses);
        Assert.AreEqual(0L, protocol.RecoveryProcessorAttempts);
        Assert.AreEqual(0L, protocol.RecoveryProcessorCompletions);
        Assert.AreEqual(0L, protocol.ObjectStore.DuplicateDeleteRequests);
        Assert.AreEqual(0L, protocol.ObjectStore.BucketHeadRequests);
        Assert.AreEqual(0L, protocol.ObjectStore.ObjectHeadRequests);
    }

    private static Issue250CorrectnessEvidence CreateCorrectnessEvidence(
        IReadOnlyList<Issue250CaseCorrectness> cases,
        Issue250Backlog finalBacklog)
    {
        var beforeAndAfterHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', cases.Select(item => item.ImmutableHash)))));
        return new Issue250CorrectnessEvidence(
            cases.Count,
            cases.All(item => item.ExactPreReleaseObjects),
            cases.All(item => item.FiveAbsentTwoPreserved),
            cases.All(item => item.SqlStatesExact),
            cases.All(item => item.ParentItemsExact),
            cases.All(item => item.ImmutableRowsStable),
            beforeAndAfterHash,
            cases.All(item => item.ExactReplay),
            cases.Select(item => string.Join(',', item.ItemShape)).Distinct(StringComparer.Ordinal).Order().ToArray(),
            cases.Select(item => item.SemanticMapSha256).Order(StringComparer.Ordinal).ToArray(),
            cases.Select(item => item.ReleaseAuditSha256).Order(StringComparer.Ordinal).ToArray(),
            cases.SelectMany(item => item.SemanticOutcomeVector).Distinct(StringComparer.Ordinal).Order().ToArray(),
            cases.Select(item => item.Representation).Distinct(StringComparer.Ordinal).Order().ToArray(),
            finalBacklog.PendingParents,
            finalBacklog.PendingItems,
            finalBacklog.LogicalPendingBytes,
            finalBacklog.OldestAgeMilliseconds);
    }

    private static Issue250ResourceAggregate CreateResourceAggregate(IReadOnlyList<Issue250ResourceEvidence> resources)
    {
        var exactAllocated = resources.Sum(item => item.AllocatedBytesDelta);
        var sampledAllocated = resources.Sum(item => item.Allocation.SampledBytes);
        var allocationDifference = Math.Abs(exactAllocated - sampledAllocated);
        var allocationDifferenceRatio = allocationDifference
            / (double)Math.Max(1, Math.Max(exactAllocated, sampledAllocated));
        var boundaryUncertainty = resources.Count == 0
            ? 1d
            : resources.Sum(item => item.Allocation.BoundaryUncertaintyRatio);
        var allocationAgreementThreshold = boundaryUncertainty + 0.10d;
        var allocationCrossCheckClaimable = resources.Count > 0
            && resources.All(item => item.Allocation.CrossCheckClaimable)
            && allocationAgreementThreshold < 1d;
        var allocationAgreement = allocationCrossCheckClaimable
            && resources.All(item => item.Allocation.AgreementWithinBoundaryUncertainty)
            && allocationDifferenceRatio <= allocationAgreementThreshold;
        if (allocationCrossCheckClaimable)
        {
            Assert.IsTrue(allocationAgreement);
        }
        return new Issue250ResourceAggregate(
            resources.Sum(item => item.ProcessCpuMilliseconds),
            exactAllocated,
            resources.Sum(item => item.ProcessSamples),
            10,
            resources.Count == 0 ? 0 : resources.Average(item => item.EffectiveSamplingIntervalMilliseconds),
            resources.Count == 0 ? 0 : resources.Max(item => item.MaximumSamplingIntervalMilliseconds),
            resources.Count == 0 ? 0 : resources.Average(item => item.AllocatedBytesPerSecond),
            new Issue250AllocationEvidence(
                sampledAllocated,
                resources.Sum(item => item.Allocation.Samples),
                100,
                allocationDifference,
                allocationDifferenceRatio,
                boundaryUncertainty,
                allocationCrossCheckClaimable,
                allocationCrossCheckClaimable
                    ? "Claimable: all constituent windows are claimable and aggregate boundary uncertainty plus tolerance is below 100%."
                    : "Unclaimable: a constituent window is unclaimable or aggregate boundary uncertainty plus tolerance reaches or exceeds 100%.",
                allocationAgreement,
                resources.SelectMany(item => item.Allocation.RawSamples).ToArray()),
            resources.Count == 0 ? 0 : resources[0].RssStartBytes,
            resources.Count == 0 ? 0 : resources.Max(item => item.RssPeakBytes),
            resources.Count == 0 ? 0 : resources[^1].RssEndBytes,
            resources.Count == 0 ? 0 : resources.Max(item => item.WindowStartUncertaintyMilliseconds),
            resources.Count == 0 ? 0 : resources.Max(item => item.WindowEndUncertaintyMilliseconds),
            resources.SelectMany(item => item.TimeSeries).ToArray());
    }

    private static Issue250Latency Latency(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length == 0
            ? new Issue250Latency(0, 0, 0, null, null, 0, [], Sha("[]"))
            : new Issue250Latency(
                ordered.Length,
                ordered[0],
                Percentile(ordered, 0.50),
                ordered.Length >= 30 ? Percentile(ordered, 0.95) : null,
                ordered.Length >= 30 ? Percentile(ordered, 0.99) : null,
                ordered[^1],
                ordered,
                Sha(JsonSerializer.Serialize(ordered)));
    }

    private static double Percentile(double[] ordered, double percentile)
        => ordered[Math.Min(ordered.Length - 1, Math.Max(0, (int)Math.Ceiling(ordered.Length * percentile) - 1))];

    private static Issue250SqlEvidence CreateSqlEvidence(
        IReadOnlyList<Issue250SqlSample> samples,
        IReadOnlyList<Issue250DeleteWindow> deleteWindows,
        int configuredDelayMilliseconds,
        string phase,
        bool contentionProbe)
    {
        var intervals = samples.Zip(samples.Skip(1),
                (previous, current) => current.ElapsedMilliseconds - previous.ElapsedMilliseconds)
            .Order().ToArray();
        var interval = intervals.Length == 0 ? 0 : Percentile(intervals, 0.50);
        var maximumInterval = intervals.Length == 0 ? 0 : intervals[^1];
        var inDeleteWindow = samples.Where(sample => deleteWindows.Any(window =>
            IsObservationInsideWindow(sample, window))).ToArray();
        var samplesPerWindow = deleteWindows.Select(window => samples.Count(sample =>
            IsObservationInsideWindow(sample, window))).ToArray();
        var windowsWithSamples = samplesPerWindow.Count(count => count > 0);
        var deleteWindowCoverage = deleteWindows.Count == 0 ? 0 : windowsWithSamples / (double)deleteWindows.Count;
        Assert.IsTrue(samples.Count == 0 || deleteWindows.All(window => window.CaseIndex >= 0
            && window.CaseIndex < samples[0].OpenTransactionSessionsByCase.Count));
        var lockDurationClaimable = configuredDelayMilliseconds > 0
            && deleteWindows.Count > 0
            && windowsWithSamples == deleteWindows.Count
            && maximumInterval <= configuredDelayMilliseconds;
        var lockDurationClaimability = lockDurationClaimable
            ? "claimable: every DELETE window contains samples and observed maximum cadence is no larger than the configured delay"
            : configuredDelayMilliseconds == 0
                ? "unclaimable: a zero-delay lock interval is below the target DMV cadence"
                : "unclaimable: sample coverage or observed maximum cadence cannot resolve every DELETE lock interval";
        if (configuredDelayMilliseconds == 0)
        {
            Assert.IsFalse(lockDurationClaimable);
            Assert.AreEqual("unclaimable: a zero-delay lock interval is below the target DMV cadence",
                lockDurationClaimability);
        }
        var transactionWindow = deleteWindows.Select(window => ObservedWindow(
            samples.Where(sample => IsObservationInsideWindow(sample, window)).ToArray(),
            item => item.OpenTransactionSessionsByCase[window.CaseIndex] > 0,
            interval)).DefaultIfEmpty().Max();
        var objectLockWindow = deleteWindows.Select(window => ObservedWindow(
            samples.Where(sample => IsObservationInsideWindow(sample, window)).ToArray(),
            item => item.ObjectApplicationLocks > 0,
            interval)).DefaultIfEmpty().Max();
        var validBacklogSamples = samples.Count(item => !item.BacklogUnavailable);
        var requiredBacklogSamples = Math.Max(1, (int)Math.Ceiling(samples.Count * 0.80));
        var backlogClaimable = configuredDelayMilliseconds > 0
            && samples.Count > 0
            && validBacklogSamples >= requiredBacklogSamples;
        var backlogClaimability = backlogClaimable
            ? "claimable: at least 80% of attributed DMV samples include a valid bounded backlog observation"
            : configuredDelayMilliseconds == 0
                ? "unclaimable: zero-delay execution completed within the DMV/backlog sampling uncertainty"
                : "unclaimable: fewer than 80% of attributed DMV samples include a valid bounded backlog observation";
        if (configuredDelayMilliseconds >= 250)
        {
            Assert.IsTrue(backlogClaimable,
                "Attributed backlog sampling must retain at least 80% valid coverage for delayed windows.");
        }
        Assert.AreEqual(0, samples.Sum(item => item.UnexpectedApplicationLocks));
        if (configuredDelayMilliseconds >= 250 && phase is "baseline" or "development")
        {
            var minimum = configuredDelayMilliseconds - 100d;
            Assert.IsGreaterThanOrEqualTo(minimum, transactionWindow);
            Assert.IsGreaterThanOrEqualTo(minimum, objectLockWindow);
            Assert.IsGreaterThanOrEqualTo(
                Math.Max(2, (int)Math.Floor(minimum / Math.Max(10, interval))),
                samplesPerWindow.DefaultIfEmpty().Max());
            Assert.IsTrue(inDeleteWindow.Any(item => item.ObjectLockOwnerSessionId.HasValue));
            Assert.IsTrue(inDeleteWindow.Any(item => item.ParentLockOwnerSessionId.HasValue));
            if (contentionProbe)
            {
                var exactRowBlocks = inDeleteWindow.Where(item =>
                    item.WriterSessionId.HasValue
                    && item.BlockerSessionId.HasValue
                    && item.BlockerTransactionId.HasValue
                    && item.AttributedBlockedWriterRequests == 1
                    && item.ExactRowBlock is not null).ToArray();
                Assert.IsNotEmpty(exactRowBlocks);
                foreach (var sample in exactRowBlocks)
                {
                    var row = sample.ExactRowBlock!;
                    Assert.AreEqual(sample.WriterSessionId, row.WriterSessionId);
                    Assert.AreEqual(sample.BlockerSessionId, row.BlockerSessionId);
                    Assert.AreEqual(sample.BlockerTransactionId, row.BlockerTransactionId);
                    Assert.IsTrue(sample.ObjectLockOwnerSessionId.HasValue);
                    Assert.IsTrue(sample.ParentLockOwnerSessionId.HasValue);
                    Assert.AreNotEqual(row.BlockerSessionId, sample.ObjectLockOwnerSessionId);
                    Assert.AreNotEqual(row.BlockerSessionId, sample.ParentLockOwnerSessionId);
                    Assert.IsTrue(row.DatabaseIsCurrent);
                    Assert.IsTrue(row.BlockerApplicationMatchesRelease);
                    Assert.IsTrue(row.BlockerTransactionOwnsGrantedResource);
                    Assert.IsTrue(row.WaitingAndGrantedResourceIdentityExact);
                    Assert.AreEqual("KEY", row.ResourceType);
                    Assert.AreEqual("CentralArtifacts", row.Table);
                    Assert.AreEqual("PK_CentralArtifacts", row.Index);
                    Assert.AreEqual("WAIT", row.WriterRequestStatus);
                    Assert.AreEqual("U", row.WriterRequestMode);
                    Assert.AreEqual("TRANSACTION", row.WriterRequestOwnerType);
                    Assert.AreEqual("GRANT", row.BlockerRequestStatus);
                    Assert.AreEqual("X", row.BlockerRequestMode);
                    Assert.AreEqual("TRANSACTION", row.BlockerRequestOwnerType);
                    Assert.AreEqual(2, row.WriterIsolationLevel);
                    Assert.AreEqual(4, row.BlockerIsolationLevel);
                    Assert.IsTrue(row.WaitType.StartsWith("LCK_M_", StringComparison.Ordinal));
                    Assert.IsTrue(row.ApplicationLockFenceSessionsDistinctFromBlocker);
                }
            }
        }
        if (configuredDelayMilliseconds >= 250 && phase == "after")
        {
            var transactionSamples = string.Join(';', inDeleteWindow
                .Where(item => deleteWindows.Any(window => IsObservationInsideWindow(item, window)
                    && item.OpenTransactionSessionsByCase[window.CaseIndex] > 0))
                .Select(item =>
                {
                    var window = deleteWindows.First(value =>
                        IsObservationInsideWindow(item, value)
                        && item.OpenTransactionSessionsByCase[value.CaseIndex] > 0);
                    return FormattableString.Invariant(
                        $"t={item.ElapsedMilliseconds:F3},from-enter={item.ElapsedMilliseconds - window.EnteredMilliseconds:F3},to-exit={window.ExitedMilliseconds - item.ElapsedMilliseconds:F3},case={window.CaseIndex},tx={item.OpenTransactionSessionsByCase[window.CaseIndex]},aggregate-tx={item.OpenTransactionSessions},requests={item.ActiveRequests},locks={item.SessionApplicationLocks},objects={item.ObjectApplicationLocks},parents={item.ParentReleaseApplicationLocks}");
                }));
            Assert.AreEqual(0d, transactionWindow, transactionSamples);
            Assert.IsGreaterThanOrEqualTo(configuredDelayMilliseconds - 100d, objectLockWindow);
            Assert.IsTrue(inDeleteWindow.Any(item => item.ObjectLockOwnerSessionId.HasValue));
            Assert.IsFalse(inDeleteWindow.Any(item => item.ExactRowBlock is not null));
        }
        return new Issue250SqlEvidence(
            samples.Count,
            10,
            interval,
            maximumInterval,
            deleteWindows.Count,
            windowsWithSamples,
            deleteWindowCoverage,
            samplesPerWindow,
            inDeleteWindow.Length,
            inDeleteWindow.FirstOrDefault()?.ElapsedMilliseconds,
            inDeleteWindow.LastOrDefault()?.ElapsedMilliseconds,
            samples.Count == 0 ? 0 : samples.Max(item => item.Sessions),
            samples.Count == 0 ? 0 : samples.Max(item => item.ActiveRequests),
            samples.Count == 0 ? 0 : samples.Max(item => item.OpenTransactionSessions),
            samples.Count == 0 ? 0 : samples.Max(item => item.SessionApplicationLocks),
            samples.Count == 0 ? 0 : samples.Max(item => item.ObjectApplicationLocks),
            samples.Count == 0 ? 0 : samples.Max(item => item.ParentReleaseApplicationLocks),
            samples.Count == 0 ? 0 : samples.Max(item => item.BlockedWriterRequests),
            samples.Count == 0 ? 0 : samples.Max(item => item.AttributedBlockedWriterRequests),
            samples.Count == 0 ? 0 : samples.Max(item => item.UnexpectedApplicationLocks),
            samples.Count(item => item.ExactRowBlock is not null),
            samples.Select(item => item.ExactRowBlock?.TargetRecordIdSha256)
                .Where(static value => value is not null).Distinct(StringComparer.Ordinal).SingleOrDefault(),
            samples.Count == 0 ? 0 : samples.Max(item => item.ActiveTransactionLogBytes),
            transactionWindow,
            objectLockWindow,
            ObservedWindow(inDeleteWindow, item => item.ParentReleaseApplicationLocks > 0, interval),
            lockDurationClaimable,
            lockDurationClaimability,
            backlogClaimable,
            backlogClaimability,
            samples.Count(item => item.BacklogUnavailable),
            validBacklogSamples,
            samples.Count == 0 ? 0 : validBacklogSamples / (double)samples.Count,
            samples.Where(item => item.PendingParents.HasValue).Select(item => item.PendingParents!.Value).DefaultIfEmpty().Max(),
            samples.Where(item => item.PendingItems.HasValue).Select(item => item.PendingItems!.Value).DefaultIfEmpty().Max(),
            samples.Where(item => item.LogicalPendingBytes.HasValue).Select(item => item.LogicalPendingBytes!.Value).DefaultIfEmpty().Max(),
            samples.Where(item => item.OldestPendingAgeMilliseconds.HasValue)
                .Select(item => item.OldestPendingAgeMilliseconds!.Value).DefaultIfEmpty().Max(),
            samples);
    }

    private static double ObservedWindow(
        IReadOnlyList<Issue250SqlSample> samples,
        Func<Issue250SqlSample, bool> predicate,
        double interval)
    {
        var longest = 0d;
        Issue250SqlSample? first = null;
        foreach (var sample in samples)
        {
            if (!predicate(sample))
            {
                first = null;
                continue;
            }
            first ??= sample;
            longest = Math.Max(longest, sample.ElapsedMilliseconds - first.ElapsedMilliseconds + interval);
        }
        return longest;
    }

    private static bool IsObservationInsideWindow(Issue250SqlSample sample, Issue250DeleteWindow window)
        => sample.ObservationStartedMilliseconds >= window.EnteredMilliseconds
           && sample.ElapsedMilliseconds <= window.ExitedMilliseconds;

    private static async Task<Issue250Backlog> ReadBacklogAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT_BIG(*) FROM [CentralTransientPayloadReleases] WHERE [State] = N'Pending'),
                (SELECT COUNT_BIG(*) FROM [CentralTransientPayloadReleaseItems] WHERE [Outcome] = N'Pending'),
                (SELECT COALESCE(SUM(CASE WHEN item.[Kind] = N'SourceArtifact' THEN artifact.[ByteLength]
                                         ELSE intent.[ByteLength] END), 0)
                 FROM [CentralTransientPayloadReleaseItems] AS item
                 LEFT JOIN [CentralArtifacts] AS artifact
                    ON item.[Kind] = N'SourceArtifact' AND artifact.[Id] = item.[RecordId]
                 LEFT JOIN [CentralTransientDerivativeOutputIntents] AS intent
                    ON item.[Kind] = N'Derivative' AND intent.[Id] = item.[RecordId]
                 WHERE item.[Outcome] = N'Pending'),
                (SELECT COALESCE(MAX(DATEDIFF_BIG(millisecond, [CreatedUtc], SYSUTCDATETIME())), 0)
                 FROM [CentralTransientPayloadReleases] WHERE [State] = N'Pending');
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new Issue250Backlog(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static async Task<Issue250ImmutableSnapshot> ReadImmutableSnapshotAsync(
        ApplicationDbContext db,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        var specifications = new[]
        {
            new Issue250CanonicalTable("CentralTransientEvents", "[Id] = @event_id", Array.Empty<string>()),
            new Issue250CanonicalTable("CentralTransientEventCurrent", "[CentralTransientEventId] = @event_id", ["UpdatedUtc", "RowVersion"]),
            new Issue250CanonicalTable("CentralTransientEventVersions", "[CentralTransientEventId] = @event_id", Array.Empty<string>()),
            new Issue250CanonicalTable("CentralTransientObservations", "[CentralTransientEventId] = @event_id", Array.Empty<string>()),
            new Issue250CanonicalTable("CentralTransientObservationSources", "[ObservationId] IN (SELECT [ObservationId] FROM [CentralTransientObservations] WHERE [CentralTransientEventId] = @event_id)", Array.Empty<string>()),
            new Issue250CanonicalTable("CentralTransientObservationBackgrounds", "[ObservationId] IN (SELECT [ObservationId] FROM [CentralTransientObservations] WHERE [CentralTransientEventId] = @event_id)", Array.Empty<string>()),
            new Issue250CanonicalTable("CentralTransientAssessments", "[CentralTransientEventId] = @event_id", Array.Empty<string>()),
            new Issue250CanonicalTable("CentralTransientAssessmentObservations", "[CentralTransientEventId] = @event_id", Array.Empty<string>()),
            new Issue250CanonicalTable("CentralTransientReviews", "[CentralTransientEventId] = @event_id", Array.Empty<string>()),
            new Issue250CanonicalTable("CentralTransientReviewMutations", "[CentralTransientEventId] = @event_id", Array.Empty<string>()),
            new Issue250CanonicalTable("CentralTransientEventVersionObservations", "[CentralTransientEventId] = @event_id", Array.Empty<string>()),
            new Issue250CanonicalTable("CentralTransientEventVersionAssessments", "[CentralTransientEventId] = @event_id", Array.Empty<string>()),
            new Issue250CanonicalTable("CentralTransientEventVersionReviews", "[CentralTransientEventId] = @event_id", Array.Empty<string>()),
            new Issue250CanonicalTable("CentralTransientEventVersionDerivatives", "[CentralTransientEventId] = @event_id", Array.Empty<string>()),
            new Issue250CanonicalTable("CentralTransientDerivativeJobs", "[CentralTransientEventId] = @event_id", Array.Empty<string>()),
            new Issue250CanonicalTable("CentralTransientDerivativeOutputIntents", "[CentralTransientEventId] = @event_id", ["ObjectState", "StateReasonCode", "RowVersion"]),
            new Issue250CanonicalTable("CentralTransientDerivatives", "[CentralTransientEventId] = @event_id", Array.Empty<string>()),
            new Issue250CanonicalTable("PublicRecordPublicationDecisions", "[CentralArtifactId] IN (SELECT [CentralArtifactId] FROM [CentralTransientObservationSources] WHERE [ObservationId] IN (SELECT [ObservationId] FROM [CentralTransientObservations] WHERE [CentralTransientEventId] = @event_id)) OR [CentralArtifactId] IN (SELECT [CentralArtifactId] FROM [CentralTransientObservationBackgrounds] WHERE [ObservationId] IN (SELECT [ObservationId] FROM [CentralTransientObservations] WHERE [CentralTransientEventId] = @event_id)) OR [CentralTransientDerivativeId] IN (SELECT [DerivativeId] FROM [CentralTransientDerivatives] WHERE [CentralTransientEventId] = @event_id)", Array.Empty<string>())
        };
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var specification in specifications)
        {
            var canonical = await ReadCanonicalTableAsync(
                connection, specification, eventId, cancellationToken).ConfigureAwait(false);
            counts[specification.Table] = canonical.Count;
            hash.AppendData(Encoding.UTF8.GetBytes(specification.Table));
            hash.AppendData(canonical.Bytes);
        }
        return new Issue250ImmutableSnapshot(
            counts,
            Convert.ToHexString(hash.GetHashAndReset()),
            "Every physical column is included in primary-key order except current UpdatedUtc/RowVersion and output-intent ObjectState/StateReasonCode/RowVersion, which are expected release mutations.");
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table/column names come only from a fixed harness allowlist and SQL Server schema metadata; event identity remains parameterized.")]
    private static async Task<(int Count, byte[] Bytes)> ReadCanonicalTableAsync(
        SqlConnection connection,
        Issue250CanonicalTable specification,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        var primaryKey = new List<string>();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                SELECT column_value.[name], COALESCE(index_column.[key_ordinal], 0)
                FROM [sys].[columns] AS column_value
                LEFT JOIN [sys].[indexes] AS index_value
                  ON index_value.[object_id] = column_value.[object_id] AND index_value.[is_primary_key] = 1
                LEFT JOIN [sys].[index_columns] AS index_column
                  ON index_column.[object_id] = index_value.[object_id]
                 AND index_column.[index_id] = index_value.[index_id]
                 AND index_column.[column_id] = column_value.[column_id]
                WHERE column_value.[object_id] = OBJECT_ID(@table)
                ORDER BY column_value.[column_id];
                """;
            schema.Parameters.AddWithValue("@table", specification.Table);
            await using var reader = await schema.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                if (!specification.ExcludedColumns.Contains(name, StringComparer.Ordinal))
                {
                    columns.Add(name);
                }
                if (reader.GetInt32(1) > 0)
                {
                    while (primaryKey.Count < reader.GetInt32(1)) primaryKey.Add(string.Empty);
                    primaryKey[reader.GetInt32(1) - 1] = name;
                }
            }
        }
        Assert.IsNotEmpty(columns);
        Assert.IsFalse(primaryKey.Any(string.IsNullOrEmpty));
        var select = string.Join(",", columns.Select(QuoteIdentifier));
        var order = string.Join(",", primaryKey.Select(QuoteIdentifier));
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {select} FROM {QuoteIdentifier(specification.Table)} WHERE {specification.Predicate} ORDER BY {order} FOR JSON PATH, INCLUDE_NULL_VALUES;";
        command.Parameters.AddWithValue("@event_id", eventId);
        var jsonBuilder = new StringBuilder();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                jsonBuilder.Append(reader.GetString(0));
            }
        }
        var json = jsonBuilder.Length == 0 ? "[]" : jsonBuilder.ToString();
        using var document = JsonDocument.Parse(json);
        return (document.RootElement.GetArrayLength(), Encoding.UTF8.GetBytes(json));
    }

    private static async Task<IReadOnlyList<Issue250TargetSnapshot>> ReadTargetSnapshotsAsync(
        ApplicationDbContext db,
        IReadOnlyList<Issue250Object> objects,
        CancellationToken cancellationToken = default)
    {
        var sourceIds = objects.Where(item => item.Kind == "Source").Select(item => item.RecordId).ToArray();
        var intentIds = objects.Where(item => item.Kind == "Derivative").Select(item => item.RecordId).ToArray();
        var sources = await db.CentralArtifacts.AsNoTracking().Where(item => sourceIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken).ConfigureAwait(false);
        var intents = await db.CentralTransientDerivativeOutputIntents.AsNoTracking()
            .Where(item => intentIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken).ConfigureAwait(false);
        return objects.Select(item => item.Kind == "Source"
            ? new Issue250TargetSnapshot(
                item.RecordId,
                sources[item.RecordId].StorageReference,
                Convert.ToHexString(sources[item.RecordId].RowVersion),
                sources[item.RecordId].RecoveryGeneration.ToString(CultureInfo.InvariantCulture),
                sources[item.RecordId].ObjectState.ToString())
            : new Issue250TargetSnapshot(
                item.RecordId,
                intents[item.RecordId].StorageReference,
                Convert.ToHexString(intents[item.RecordId].RowVersion),
                "N/A: derivative intents do not expose recovery generation at baseline.",
                intents[item.RecordId].ObjectState.ToString())).ToArray();
    }

    private static async Task<Issue250HoldSnapshot> ReadHoldSnapshotAsync(
        ApplicationDbContext db,
        Guid eventId,
        IReadOnlyList<Issue250Object> objects,
        CancellationToken cancellationToken = default)
    {
        var sourceIds = objects.Where(item => item.Kind == "Source").Select(item => item.RecordId).ToArray();
        var derivativeIds = await db.CentralTransientDerivatives.AsNoTracking()
            .Where(item => item.CentralTransientEventId == eventId)
            .Select(item => new { item.OutputIntentId, item.DerivativeId })
            .ToDictionaryAsync(item => item.OutputIntentId, item => item.DerivativeId, cancellationToken).ConfigureAwait(false);
        var relevantDerivativeIds = derivativeIds.Values.ToArray();
        var decisions = await db.PublicRecordPublicationDecisions.AsNoTracking()
            .Where(item => (item.CentralArtifactId.HasValue && sourceIds.Contains(item.CentralArtifactId.Value))
                || (item.CentralTransientDerivativeId.HasValue
                    && relevantDerivativeIds.Contains(item.CentralTransientDerivativeId.Value)))
            .Select(item => new
            {
                item.Id,
                item.SubjectKind,
                item.State,
                item.CentralArtifactId,
                item.CentralTransientDerivativeId,
                item.ProjectionSchemaVersion,
                item.ReasonCode,
                item.SupersedesDecisionId
            })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var currentReleased = decisions.Where(decision => decision.State == PublicationDecisionState.Released
                && !decisions.Any(successor => successor.SupersedesDecisionId == decision.Id))
            .ToArray();
        foreach (var target in objects)
        {
            var held = target.Kind == "Source"
                ? currentReleased.Any(item => item.CentralArtifactId == target.RecordId)
                : currentReleased.Any(item => item.CentralTransientDerivativeId == derivativeIds[target.RecordId]);
            Assert.AreEqual(target.Held, held, $"Persisted hold decision mismatch for workload ordinal {target.WorkloadOrdinal}.");
        }
        if (objects.Count == 7)
        {
            Assert.AreEqual(2, currentReleased.Length);
            Assert.IsTrue(currentReleased.All(item => item.ReasonCode == "issue-250-independent-hold"));
        }
        var semantics = currentReleased.Select(item => string.Join(':',
                item.SubjectKind,
                item.State,
                item.ProjectionSchemaVersion,
                item.ReasonCode,
                item.CentralArtifactId.HasValue ? "source" : "derivative"))
            .Order(StringComparer.Ordinal).ToArray();
        return new Issue250HoldSnapshot(Sha(JsonSerializer.Serialize(semantics)), semantics);
    }

    private static string QuoteIdentifier(string value)
        => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static async Task<long> SampleRssAsync(long initial, CancellationToken cancellationToken)
    {
        var peak = initial;
        using var process = Process.GetCurrentProcess();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        return peak;
    }

    private static IEnumerable<int> WaveCounts(int total, int concurrency)
    {
        for (var remaining = total; remaining > 0; remaining -= Math.Min(remaining, concurrency))
        {
            yield return Math.Min(remaining, concurrency);
        }
    }

    private static Issue250Payloads CreatePayloads(bool smoke)
    {
        var width = smoke ? 8 : FullWidth;
        var height = smoke ? 4 : FullHeight;
        var source = new byte[width * height * 2];
        var preview = new byte[width * height * 3];
        var overlay = new byte[width * height * 3];
        var sourceOffset = 0;
        var rgbOffset = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (ushort)((64 + 2025 + 31 * x + 17 * y + 997 * (((y & 1) * 2) + (x & 1))) & 0x3FFF);
                source[sourceOffset++] = (byte)value;
                source[sourceOffset++] = (byte)(value >> 8);
                preview[rgbOffset] = (byte)(13 * x + 7 * y + 11);
                preview[rgbOffset + 1] = (byte)(3 * x + 17 * y + 29);
                preview[rgbOffset + 2] = (byte)(19 * x + 5 * y + 47);
                overlay[rgbOffset] = (byte)(x ^ y ^ 0x5A);
                overlay[rgbOffset + 1] = (byte)(11 * x + 23 * y + 71);
                overlay[rgbOffset + 2] = (byte)(29 * x + 2 * y + 101);
                rgbOffset += 3;
            }
        }
        var result = new Issue250Payloads(
            CreatePayload("source", source),
            CreatePayload("preview", preview),
            CreatePayload("overlay", overlay));
        if (!smoke)
        {
            Assert.AreEqual(SourceBytes, result.Source.Bytes.LongLength);
            Assert.AreEqual(DerivativeBytes, result.Preview.Bytes.LongLength);
            Assert.AreEqual(DerivativeBytes, result.Overlay.Bytes.LongLength);
            Assert.AreEqual(SourceSha256, result.Source.Sha256);
            Assert.AreEqual(DerivativePreviewSha256, result.Preview.Sha256);
            Assert.AreEqual(DerivativeOverlaySha256, result.Overlay.Sha256);
        }
        return result;
    }

    private static async Task<Issue250W0Fixture> CreateW0FixtureAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var start = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var catalog = new InMemoryCelestialCatalog(
            [new CelestialCatalogObject("star", "Star", 2.5, 20, 1)]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICelestialCatalog>(catalog);
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        var config = new CameraModuleConfig(
            new ObservatoryLocation(35.347, -113.878, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(
                new VirtualSkyCameraModuleOptions { MaximumResults = 10, ShotNoiseEnabled = false })),
            new CameraRigConfig(
                new SensorProfile("Soak", 64, 48, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16,
                    SensorResponseMode.Monochrome),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    PrincipalPointX: 32, PrincipalPointY: 24, ImageCircleRadiusPixels: 23),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 10, 10)),
            CapturePipelineConfig.Empty,
            AgentId: "accelerated-soak");
        var clock = new Issue250FixedTimeProvider(start);
        await using var module = new VirtualSkyCameraModule(
            clock,
            catalog,
            provider.GetRequiredService<IProjectedSceneStore>(),
            provider.GetRequiredService<IConstellationTopology>());
        await module.InitializeAsync(config, cancellationToken).ConfigureAwait(false);
        var initialSetpoint = ExposureController.Initial(config.Rig.Pipeline, CaptureSolarRegime.Night);
        Assert.AreEqual(config.Rig.Pipeline.NightExposure, initialSetpoint.Exposure);
        Assert.AreEqual(config.Rig.Pipeline.NightGain, initialSetpoint.Gain);
        Assert.IsNull(initialSetpoint.NextIntervalOverride);
        Assert.IsNull(initialSetpoint.TargetFps);
        var request = new CaptureRequest(
            start,
            config.Rig.Pipeline.CaptureInterval,
            CaptureMode.Still,
            initialSetpoint);
        var result = await module.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
        Assert.IsNotNull(result.Frame);
        var frame = result.Frame;
        Assert.AreEqual(CameraPixelFormat.Mono16, frame.PixelFormat);
        Assert.AreEqual(64, frame.Width);
        Assert.AreEqual(48, frame.Height);
        Assert.AreEqual(128, frame.StrideBytes);
        Assert.AreEqual(6_144, frame.PixelData.Length);
        Assert.IsNotNull(frame.Layout);
        Assert.AreEqual(128, frame.Layout.StrideBytes);
        Assert.AreEqual(FrameByteOrder.LittleEndian, frame.Layout.ByteOrder);
        Assert.AreEqual(CameraPixelFormat.Mono16, frame.Layout.PixelFormat);
        Assert.AreEqual(6_144L, frame.Layout.ByteLength);
        var payload = CreatePayload("w0-virtual-sky-mono16", frame.PixelData.ToArray());
        Assert.AreEqual(W0Sha256, payload.Sha256,
            $"Canonical W0 VirtualSky checksum changed; observed {payload.Sha256}. Review the authenticated fixture/module sources before updating it.");

        const string fixturePath = "tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/AcceleratedCameraAgentSoakTests.cs";
        const string runnerPath = "src/HVO.SkyMonitor.CameraAgent.Common/Capture/CameraModuleRunner.cs";
        const string exposurePath = "src/HVO.SkyMonitor.CameraAgent.Common/Capture/Exposure/ExposureController.cs";
        const string virtualSkyPath = "src/HVO.SkyMonitor.CameraAgent.Common/Modules/VirtualSky/VirtualSkyCameraModule.cs";
        var configSha256 = Sha(JsonSerializer.Serialize(new
        {
            Catalog = new { Id = "star", Name = "Star", Magnitude = 2.5, RightAscensionDegrees = 20, DeclinationDegrees = 1 },
            SceneUtc = start,
            Observatory = new { Latitude = 35.347, Longitude = -113.878, Elevation = 0, TimeZone = "UTC" },
            Module = new { Type = "VirtualSky", MaximumResults = 10, ShotNoiseEnabled = false },
            Sensor = new { Name = "Soak", Width = 64, Height = 48, BitDepth = 5, Color = "Mono", PixelFormat = "Mono16", Response = "Monochrome" },
            Optics = new { Projection = "EquidistantFisheye", Minimum = 0, Maximum = 180, Rotation = 0, PrincipalX = 32, PrincipalY = 24, Radius = 23 },
            Orientation = new { Azimuth = 90, Altitude = 0, Roll = 0 },
            Pipeline = new { Interval = "00:05:00", DayExposure = "00:00:01", NightExposure = "00:00:01", DayGain = 10, NightGain = 10 },
            InitialRegime = "Night",
            Request = new { RequestedStartUtc = start, TargetInterval = "00:05:00", Mode = "Still", Exposure = "00:00:01", Gain = 10 }
        }));
        var assemblyBytes = await File.ReadAllBytesAsync(
            typeof(VirtualSkyCameraModule).Assembly.Location, cancellationToken).ConfigureAwait(false);
        return new(
            new Issue250Payloads(
                payload,
                CreatePayload("w0-unused-preview", [1]),
                CreatePayload("w0-unused-overlay", [2])),
            new Issue250W0GeneratorEvidence(
                "W0 actual VirtualSky first raw capture",
                payload.Sha256,
                frame.Width,
                frame.Height,
                frame.StrideBytes!.Value,
                frame.PixelFormat.ToString(),
                frame.Layout.ByteOrder.ToString(),
                frame.PixelData.Length,
                "star/Star/mag2.5/RA20/Dec1",
                start.ToString("O", CultureInfo.InvariantCulture),
                "Night: exposure=1s gain=10 next-interval=null target-fps=null",
                "RequestedStart=2026-01-15T00:00:00Z target-interval=5m mode=Still requested-setpoint=initial-Night",
                configSha256,
                fixturePath,
                FileSha256(repositoryRoot, fixturePath),
                FileSha256(repositoryRoot, runnerPath),
                FileSha256(repositoryRoot, exposurePath),
                FileSha256(repositoryRoot, virtualSkyPath),
                Convert.ToHexString(SHA256.HashData(assemblyBytes))));
    }

    private static string FileSha256(string repositoryRoot, string relativePath)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(repositoryRoot, relativePath))));

    private static Issue250Payload CreatePayload(string name, byte[] bytes)
        => new(name, bytes, Convert.ToHexString(SHA256.HashData(bytes)));

    private static async Task PutObjectAsync(
        IMinioClient minio,
        Issue250Object item,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new MemoryStream(item.Payload.Bytes, writable: false);
        await minio.PutObjectAsync(new PutObjectArgs()
            .WithBucket(Bucket)
            .WithObject(item.Key)
            .WithStreamData(stream)
            .WithObjectSize(item.Payload.Bytes.LongLength)
            .WithContentType(item.Kind == "Source" ? "application/x-hvo-frame" : "application/x-rgb24"), cancellationToken)
            .WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyPresentAsync(
        IMinioClient minio,
        Issue250Object item,
        CancellationToken cancellationToken = default)
        => _ = await VerifyObjectAsync(
            minio,
            item.Key,
            item.Payload.Bytes.LongLength,
            item.Payload.Sha256,
            cancellationToken).ConfigureAwait(false);

    private static async Task<Issue250ObjectVerificationEvidence> VerifyObjectAsync(
        IMinioClient minio,
        string key,
        long expectedByteLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var stat = await minio.StatObjectAsync(
                new StatObjectArgs().WithBucket(Bucket).WithObject(key), cancellationToken)
            .WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(expectedByteLength, stat.Size);
        string? checksum = null;
        await minio.GetObjectAsync(new GetObjectArgs()
            .WithBucket(Bucket)
            .WithObject(key)
            .WithCallbackStream(stream => checksum = Convert.ToHexString(SHA256.HashData(stream))), cancellationToken)
            .WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        var observedSha256 = checksum
            ?? throw new InvalidOperationException("MinIO correctness GET did not produce an observed SHA256.");
        Assert.AreEqual(expectedSha256, observedSha256);
        return new(stat.Size, observedSha256, ExactLengthAndSha256: true);
    }

    private static async Task AssertAbsentAsync(
        IMinioClient minio,
        string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await minio.StatObjectAsync(
                    new StatObjectArgs().WithBucket(Bucket).WithObject(key), cancellationToken)
                .WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            Assert.Fail("Released object remains present.");
        }
        catch (MinioException exception) when (MinioObjectVerification.IsNotFound(exception)
                                                && exception is not BucketNotFoundException)
        {
        }
    }

    private static async Task CleanupHeldObjectsAsync(
        IMinioClient minio,
        IReadOnlyList<Issue250ReleaseCase> cases,
        CancellationToken cancellationToken)
    {
        foreach (var item in cases.SelectMany(item => item.Objects).Where(item => item.Held))
        {
            await minio.RemoveObjectAsync(
                    new RemoveObjectArgs().WithBucket(Bucket).WithObject(item.Key), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CleanupScenarioAsync(
        IMinioClient minio,
        IEnumerable<string> keys,
        ApplicationDbContext db)
    {
        var failures = new List<Exception>();
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            try
            {
                using var objectCleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await minio.RemoveObjectAsync(
                        new RemoveObjectArgs().WithBucket(Bucket).WithObject(key), objectCleanupTimeout.Token)
                    .WaitAsync(objectCleanupTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        try
        {
            using var databaseCleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await db.Database.EnsureDeletedAsync(databaseCleanupTimeout.Token)
                .WaitAsync(databaseCleanupTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        if (failures.Count > 0)
        {
            throw new AggregateException("Issue #250 evidence cleanup failed.", failures);
        }
    }

    private static async Task EnsureBucketAsync(IMinioClient minio)
    {
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket)).ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket)).ConfigureAwait(false);
        }
    }

    private static IMinioClient CreateMinio(IntegrationTestFixture fixture, HttpClient httpClient)
        => new MinioClient()
            .WithEndpoint(fixture.MinioEndpoint)
            .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
            .WithHttpClient(httpClient, disposeHttpClient: false)
            .Build();

    private static CentralTransientPayloadReleaseService CreateService(
        ApplicationDbContext db,
        IMinioClient minio,
        CentralTransientLifecycleTelemetry telemetry)
        => new(
            db,
            new CentralArtifactRetentionReferences(db),
            minio,
            Options.Create(new CentralTransientPayloadReleaseOptions { Enabled = true }),
            TimeProvider.System,
            telemetry);

    private static ClaimsPrincipal CreatePrincipal(string actor)
        => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actor),
            new Claim("sub", actor),
            new Claim("account_type", "User"),
            new Claim("scope", "api.viewer api.admin")
        ], "Issue250Evidence"));

    private static Issue250Database CreateDatabase(IntegrationTestFixture fixture, string scenario)
    {
        var builder = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorIssue250{scenario}_{Guid.NewGuid():N}"
        };
        return new Issue250Database(CreateContext(builder.ConnectionString), builder.ConnectionString);
    }

    private static ApplicationDbContext CreateContext(
        string connectionString,
        string? applicationName = null,
        Issue250EvidenceCollector? collector = null,
        bool recoveryProtocol = false,
        DbCommandInterceptor? additionalInterceptor = null)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (applicationName is not null)
        {
            builder.ApplicationName = applicationName;
        }
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (collector is not null)
        {
            options.AddInterceptors(
                recoveryProtocol ? collector.RecoveryCommands : collector.Commands,
                recoveryProtocol ? collector.RecoveryTransactions : collector.Transactions);
        }
        if (additionalInterceptor is not null)
        {
            options.AddInterceptors(additionalInterceptor);
        }
        return new ApplicationDbContext(options.Options);
    }

    private static async Task<Issue250SchemaCapabilities> ValidateSchemaCapabilitiesAsync(
        IntegrationTestFixture fixture,
        string phase)
    {
        await using var database = CreateDatabase(fixture, "Capabilities");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await database.Context.Database.MigrateAsync(timeout.Token).ConfigureAwait(false);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(timeout.Token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT column_value.[name], type_value.[name], column_value.[max_length], column_value.[is_nullable]
                FROM [sys].[columns] AS column_value
                INNER JOIN [sys].[types] AS type_value
                    ON type_value.[user_type_id] = column_value.[user_type_id]
                WHERE column_value.[object_id] = OBJECT_ID(N'CentralTransientPayloadReleaseItems')
                ORDER BY column_value.[column_id];
                """;
            var actual = new List<Issue250SchemaColumn>();
            await using (var reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    actual.Add(new(
                        reader.GetString(0), reader.GetString(1), reader.GetInt16(2), reader.GetBoolean(3)));
                }
            }
            var baseline = new[]
            {
                new Issue250SchemaColumn("ReleaseId", "uniqueidentifier", 16, false),
                new Issue250SchemaColumn("Ordinal", "int", 4, false),
                new Issue250SchemaColumn("Kind", "nvarchar", 64, false),
                new Issue250SchemaColumn("RecordId", "uniqueidentifier", 16, false),
                new Issue250SchemaColumn("ReleasedUtc", "datetimeoffset", 10, true),
                new Issue250SchemaColumn("Outcome", "nvarchar", 64, false)
            };
            if (actual.SequenceEqual(baseline))
            {
                if (phase == "after")
                {
                    throw new InvalidOperationException(
                        "After evidence requires the issue #250 release-item reservation schema.");
                }
                return new(
                    phase,
                    actual,
                    "Exactly the current six-column release-item schema is present.",
                    "N/A at baseline: reservation token/request time/exact storage reference/target rowversion or generation/retry schedule/item rowversion do not exist.",
                    CandidateHarnessImplemented: false,
                    RequiredAfterColumns: CandidateSchemaColumns,
                    RequiredAfterFaultHooks: CandidateFaultHooks);
            }
            CollectionAssert.AreEqual(CandidateSchemaColumns, actual.ToArray());
            if (phase == "baseline")
            {
                throw new InvalidOperationException(
                    "Baseline evidence cannot run against the issue #250 candidate schema; use the preserved accepted baseline.");
            }
            CollectionAssert.AreEquivalent(
                new[]
                {
                    nameof(CentralTransientPayloadReleaseFaultStage.ReservationCommittedBeforeDelete),
                    nameof(CentralTransientPayloadReleaseFaultStage.BeforeDelete),
                    nameof(CentralTransientPayloadReleaseFaultStage.DeleteCompletedBeforeFinalize),
                    nameof(CentralTransientPayloadReleaseFaultStage.FinalItemBeforeParentCompletion)
                },
                Enum.GetNames<CentralTransientPayloadReleaseFaultStage>()
                    .Where(name => name != nameof(CentralTransientPayloadReleaseFaultStage.BeforeReservationCommit))
                    .ToArray());
            return new(
                phase,
                actual,
                "The exact fifteen-column candidate release-item schema is present.",
                "Candidate reservation, target-fence, retry, and rowversion fields are active; legacy terminal null/default rows remain valid.",
                CandidateHarnessImplemented: true,
                RequiredAfterColumns: CandidateSchemaColumns,
                RequiredAfterFaultHooks: CandidateFaultHooks);
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await database.Context.Database.EnsureDeletedAsync(cleanupTimeout.Token)
                .WaitAsync(cleanupTimeout.Token).ConfigureAwait(false);
        }
    }

    private static readonly Issue250SchemaColumn[] CandidateSchemaColumns =
    [
        new("ReleaseId", "uniqueidentifier", 16, false),
        new("Ordinal", "int", 4, false),
        new("Kind", "nvarchar", 64, false),
        new("RecordId", "uniqueidentifier", 16, false),
        new("ReleasedUtc", "datetimeoffset", 10, true),
        new("Outcome", "nvarchar", 64, false),
        new("FailureReasonCode", "nvarchar", 256, true),
        new("RequestedAtUtc", "datetimeoffset", 10, true),
        new("ReservationToken", "uniqueidentifier", 16, true),
        new("RetryAtUtc", "datetimeoffset", 10, true),
        new("RetryCount", "int", 4, false),
        new("RowVersion", "timestamp", 8, false),
        new("StorageReference", "nvarchar", 2048, true),
        new("TargetGeneration", "bigint", 8, true),
        new("TargetRowVersion", "varbinary", 8, true)
    ];

    private static readonly string[] CandidateFaultHooks =
    [
        "reservation-committed-before-delete",
        "process-termination-restart",
        "delete-completed-before-finalize",
        "stale-rowversion-generation",
        "late-hold",
        "final-item-before-parent-completion",
        "bounded-retry-no-duplicate-harm"
    ];

    private static Issue250ObjectLockContract AuthenticateObjectLockContract(string repositoryRoot)
    {
        const string probe = "minio://skymonitor-artifacts/issue-250/lock-contract-probe.bin";
        var expected = CreateExpectedObjectLockResource(probe);
        Assert.AreEqual(expected, CentralObjectApplicationLock.CreateResource(probe));
        var sourcePath = Path.Combine(repositoryRoot,
            "src/HVO.SkyMonitor.LogicHost/Services/CentralObjectApplicationLock.cs");
        var source = File.ReadAllText(sourcePath);
        Assert.IsTrue(source.Contains(
            "ResourcePrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalStorageReference)))",
            StringComparison.Ordinal));
        return new(
            "hvo-central-object:<uppercase SHA-256 of UTF-8 canonical storage reference>",
            "SQL Server exposes the first 32 resource-name characters and hashes the remainder; DMV predicates compare that exact visible segment of each fully derived transformed resource.",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))),
            Sha(probe),
            expected,
            ProductionIntegrationAssertionPassed: true);
    }

    private static string CreateExpectedObjectLockResource(string canonicalStorageReference)
        => "hvo-central-object:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalStorageReference)));

    private static string ReadPhase()
    {
        var phase = Environment.GetEnvironmentVariable("HVO_EVIDENCE_PHASE") ?? "development";
        if (phase is not ("development" or "baseline" or "after"))
        {
            throw new InvalidOperationException("HVO_EVIDENCE_PHASE must be development, baseline, or after.");
        }
        return phase;
    }

    private static string ResolveIssue268OutputPath(int trial)
    {
        var stateRoot = Environment.GetEnvironmentVariable("HVO_AGENT_STATE_ROOT");
        var outputRoot = Environment.GetEnvironmentVariable("HVO_ISSUE_268_OUTPUT_ROOT");
        if (string.IsNullOrWhiteSpace(stateRoot) || string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new InvalidOperationException(
                "Issue #268 evidence requires HVO_AGENT_STATE_ROOT and HVO_ISSUE_268_OUTPUT_ROOT.");
        }

        var fullStateRoot = ResolveExistingDirectory(stateRoot);
        var fullOutputRoot = ResolveExistingDirectory(outputRoot);
        if (!Path.IsPathFullyQualified(outputRoot)
            || !fullOutputRoot.StartsWith(fullStateRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Issue #268 output must be an absolute path beneath HVO_AGENT_STATE_ROOT.");
        }

        return Path.Combine(fullOutputRoot, $"trial-{trial}");
    }

    private static string ResolveExistingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Evidence directory does not exist: {fullPath}");
        }

        var current = Path.GetPathRoot(fullPath)!;
        foreach (var segment in Path.GetRelativePath(current, fullPath)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (new DirectoryInfo(current).LinkTarget is not null)
            {
                throw new InvalidOperationException("Evidence paths must not traverse symbolic links.");
            }
        }
        return current.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static async Task<Issue250Preflight> RunPreflightAsync(
        string repositoryRoot,
        IntegrationTestFixture fixture,
        bool smoke,
        string phase)
    {
        var pinnedSdk = ReadPinnedSdk(repositoryRoot);
        var executingSdk = (await RunProcessAsync(repositoryRoot, "dotnet", "--version").ConfigureAwait(false)).Trim();
        var totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var hostTotalMemory = ReadLinuxHostMemoryBytes();
        var containerMemoryLimit = ReadCgroupMemoryLimitBytes(hostTotalMemory);
        var cpuModel = ReadCpuModel();
        var drive = new DriveInfo(Path.GetPathRoot(repositoryRoot)!);
        var availableDisk = drive.AvailableFreeSpace;
        var minioStorageFree = await ReadContainerStorageFreeBytesAsync(
            fixture, IntegrationDependency.Minio, "/data").ConfigureAwait(false);
        var sqlStorageFree = await ReadContainerStorageFreeBytesAsync(
            fixture, IntegrationDependency.SqlServer, "/var/opt/mssql/data").ConfigureAwait(false);
        const long minimumMemory = 8L * 1024 * 1024 * 1024;
        const long minimumDisk = 25L * 1024 * 1024 * 1024;
        if (!smoke)
        {
            if (!OperatingSystem.IsLinux()
                || RuntimeInformation.ProcessArchitecture != Architecture.X64
                || !string.Equals(pinnedSdk, "10.0.100", StringComparison.Ordinal)
                || !string.Equals(executingSdk, pinnedSdk, StringComparison.Ordinal)
                || totalMemory < minimumMemory
                || availableDisk < minimumDisk
                || minioStorageFree < minimumDisk
                || sqlStorageFree < minimumDisk)
            {
                throw new InvalidOperationException(
                    "Full issue #250 evidence requires Linux X64, SDK 10.0.100, at least 8 GiB available memory, and at least 25 GiB free on workspace, MinIO /data, and SQL Server /var/opt/mssql/data filesystems.");
            }
        }
        if (phase != "development"
            && (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64))
        {
            throw new InvalidOperationException("Claimable issue #250 evidence requires Linux X64.");
        }
        return new Issue250Preflight(
            OperatingSystem.IsLinux(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            pinnedSdk,
            executingSdk,
            cpuModel,
            totalMemory,
            hostTotalMemory,
            containerMemoryLimit,
            availableDisk,
            drive.DriveFormat,
            minioStorageFree,
            sqlStorageFree,
            minimumMemory,
            minimumDisk,
            IntegrationTestFixture.SqlServerImage,
            IntegrationTestFixture.MinioImage,
            1_800,
            smoke ? "Smoke bypasses full memory/disk thresholds and remains unclaimable." : "Full capacity thresholds passed.");
    }

    private static string ReadCpuModel()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/cpuinfo"))
        {
            return RuntimeInformation.ProcessArchitecture.ToString();
        }
        var line = File.ReadLines("/proc/cpuinfo")
            .FirstOrDefault(value => value.StartsWith("model name", StringComparison.Ordinal));
        return line?.Split(':', 2)[1].Trim() ?? RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static long ReadLinuxHostMemoryBytes()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/meminfo"))
        {
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        var value = File.ReadLines("/proc/meminfo")
            .First(line => line.StartsWith("MemTotal:", StringComparison.Ordinal))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];
        return checked(long.Parse(value, CultureInfo.InvariantCulture) * 1024);
    }

    private static long ReadCgroupMemoryLimitBytes(long hostMemoryBytes)
    {
        const string path = "/sys/fs/cgroup/memory.max";
        if (!File.Exists(path))
        {
            return hostMemoryBytes;
        }
        var value = File.ReadAllText(path).Trim();
        return string.Equals(value, "max", StringComparison.Ordinal)
            ? hostMemoryBytes
            : long.Parse(value, CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadContainerStorageFreeBytesAsync(
        IntegrationTestFixture fixture,
        IntegrationDependency dependency,
        string path)
    {
        var result = await fixture.GetDependencyContainer(dependency)
            .ExecAsync(["df", "-Pk", path]).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Dependency container storage preflight failed.");
        }
        var line = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1];
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4)
        {
            throw new InvalidOperationException("Dependency container storage preflight returned an invalid bounded result.");
        }
        return checked(long.Parse(fields[3], CultureInfo.InvariantCulture) * 1024);
    }

    private static string ComputeHarnessSha256(string repositoryRoot)
    {
        var paths = BaselineChangedPaths;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData(File.ReadAllBytes(Path.Combine(repositoryRoot, path)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ComputeWorkloadSha256(Issue250W0GeneratorEvidence w0)
        => Sha(JsonSerializer.Serialize(new
        {
            Schema = "issue-250-frozen-seven-object-workload-v4",
            OrderedReleaseUniverse = new object[]
            {
                new { Ordinal = 0, TemporalOrRole = "NMinus2", Kind = "SourceArtifact", MediaType = "application/x-hvo-frame", Bytes = SourceBytes, Sha256 = SourceSha256, Generator = "w2-rggb16-v1", Held = true, RecordTopology = "observation-background", BaselineItem = "filtered", AfterItem = "PreservedHeld" },
                new { Ordinal = 1, TemporalOrRole = "NMinus1", Kind = "SourceArtifact", MediaType = "application/x-hvo-frame", Bytes = SourceBytes, Sha256 = SourceSha256, Generator = "w2-rggb16-v1", Held = false, RecordTopology = "observation-background", BaselineItem = "Released", AfterItem = "Released" },
                new { Ordinal = 2, TemporalOrRole = "N", Kind = "SourceArtifact", MediaType = "application/x-hvo-frame", Bytes = SourceBytes, Sha256 = SourceSha256, Generator = "w2-rggb16-v1", Held = false, RecordTopology = "observation-source", BaselineItem = "Released", AfterItem = "Released" },
                new { Ordinal = 3, TemporalOrRole = "NPlus1", Kind = "SourceArtifact", MediaType = "application/x-hvo-frame", Bytes = SourceBytes, Sha256 = SourceSha256, Generator = "w2-rggb16-v1", Held = false, RecordTopology = "observation-background", BaselineItem = "Released", AfterItem = "Released" },
                new { Ordinal = 4, TemporalOrRole = "NPlus2", Kind = "SourceArtifact", MediaType = "application/x-hvo-frame", Bytes = SourceBytes, Sha256 = SourceSha256, Generator = "w2-rggb16-v1", Held = false, RecordTopology = "observation-background", BaselineItem = "Released", AfterItem = "Released" },
                new { Ordinal = 5, TemporalOrRole = "Preview", Kind = "Derivative", Role = "Preview", Variant = "rgb24-preview", MediaType = "application/x-rgb24", Bytes = DerivativeBytes, Sha256 = DerivativePreviewSha256, OutputIdentitySha256 = Sha($"{TwoOutputFixtureName}:Preview"), Generator = "w2-preview-rgb24-v1", Held = false, RecordTopology = "issue-specific-uncommitted-preview-output-intent", BaselineItem = "Released", AfterItem = "Released" },
                new { Ordinal = 6, TemporalOrRole = "Overlay", Kind = "Derivative", Role = "AnnotatedPreview", Variant = "rgb24-overlay", MediaType = "application/x-rgb24", Bytes = DerivativeBytes, Sha256 = DerivativeOverlaySha256, OutputIdentitySha256 = Sha($"{TwoOutputFixtureName}:Overlay"), Generator = "w2-overlay-rgb24-v1", Held = true, RecordTopology = "issue-specific-uncommitted-overlay-output-intent", BaselineItem = "filtered", AfterItem = "PreservedHeld" }
            },
            ItemNormalization = "SourceArtifact before Derivative; each kind ordered by RecordId; dense ordinal from zero",
            DerivativeTopology = new
            {
                Name = TwoOutputFixtureName,
                Purpose = "Payload-release fixture only; not CentralTransientDerivativeOutputWriter conformance.",
                OutputKinds = ExpectedDerivativeOutputKinds,
                MinioBackedReleaseKinds = MinioBackedDerivativeKinds,
                StoragePattern = "minio://skymonitor-artifacts/issue-250/<case-token>/derivative-<0|1>.rgb",
                ProductionSchemaDisposition = "ExpectedOutputCount remains schema-required 5, but CommittedAtUtc is null and exactly two valid intents/records exist; no canonical bundle commit is claimed.",
                Holds = FixtureHolds
            },
            Measurement = new
            {
                Warmups = 5,
                Measurements = 30,
                Concurrency = CanonicalConcurrency,
                DelaysMilliseconds = CanonicalDelays,
                DelayReleases = 4,
                ContinuousWindowPerScenario = true,
                SamplingMilliseconds = 10,
                Counters = ProcessResourceCounters,
                WarmupAndSetupExcluded = true
            },
            W3M = new
            {
                CompletedParents = 10_000,
                TerminalItemsPerParent = 7,
                ValidExpiredSourceTargets = 70_000,
                OldestPendingParents = 1,
                PayloadObjects = 1,
                Sentinel = new
                {
                    Generator = W3MSentinelGenerator,
                    ByteLength = W3MSentinelByteLength,
                    Sha256 = W3MSentinelSha256
                }
            },
            W0 = w0,
            FaultManifest = CanonicalFaultManifest
        }));

    private static string ComputeCompatibilityWorkloadSha256(Issue250W0GeneratorEvidence w0)
        => ComputeWorkloadSha256(w0 with
        {
            CameraAgentCommonAssemblySha256 = "revision-specific-provenance-excluded"
        });

    private static string ComputeActualWorkloadSha256(
        Issue250Payloads payloads,
        Issue250W0GeneratorEvidence w0,
        string compatibilityWorkloadSha256)
        => Sha(JsonSerializer.Serialize(new
        {
            Schema = "issue-250-actual-development-smoke-workload-v2",
            CompatibilityWorkloadSha256 = compatibilityWorkloadSha256,
            Sources = new { Count = 5, Width = 8, Height = 4, payloads.Source.Bytes.LongLength, payloads.Source.Sha256 },
            Derivatives = new[]
            {
                new { Kind = "Preview", payloads.Preview.Bytes.LongLength, payloads.Preview.Sha256 },
                new { Kind = "Overlay", payloads.Overlay.Bytes.LongLength, payloads.Overlay.Sha256 }
            },
            DerivativeFixture = TwoOutputFixtureName,
            Warmups = 0,
            Measurements = 1,
            Concurrency = CanonicalConcurrency,
            DelaysMilliseconds = SmokeDelays,
            DelayReleases = 4,
            W3MCompletedParents = 8,
            W3MSentinel = new
            {
                Generator = W3MSentinelGenerator,
                ByteLength = W3MSentinelByteLength,
                Sha256 = W3MSentinelSha256
            },
            W0 = w0,
            FaultManifest = CanonicalFaultManifest
        }));

    private static string ComputeProtocolSha256()
        => Sha(JsonSerializer.Serialize(new
        {
            Schema = "issue-250-public-release-protocol-v6",
            Boundary = "public ReleaseAsync including request/item creation, DELETE, finalize, replay",
            Percentiles = "nearest-rank",
            SqlSamplingMilliseconds = 10,
            AllocationSamplingMilliseconds = 100,
            LockResource = "hvo-central-object:<uppercase SHA-256 of UTF-8 canonical reference>",
            RowBlockAttribution = "exact matching waiting/granted KEY resource on CentralArtifacts.PK_CentralArtifacts with blocker transaction/session/database/isolation attribution",
            WriterDeadlineMilliseconds = 1_000,
            W3M = "actual ProcessNextAsync plus exact normalized baseline parent query plan",
            FaultManifest = CanonicalFaultManifest,
            ProtocolCount = "EF commands are separate from scoped SqlClient application-lock command diagnostics and direct harness SQL",
            HttpAccounting = "Requests partition into DELETE, MinIO bucket HEAD, object HEAD, and other methods; DELETEs partition into unique and duplicate targets plus response and exception outcomes",
            NaturalRecovery = "Steady-state and natural-delay release fail if the public operation returns Accepted; lease recovery remains claimable only in the explicit contention and fault paths",
            CreationConflictAccounting = "SQL creation deadlocks are retried inside one public ReleaseAsync and counted by bounded creation-conflict Meter telemetry; any SQL deadlock escaping the public operation fails the campaign"
        }));

    private static string ComputeCompatibilityProtocolSha256()
        => Sha(JsonSerializer.Serialize(new
        {
            Schema = CompatibilityProtocolSchema,
            TrialPolicy,
            Boundary = "public ReleaseAsync through durable terminal parent/items and MinIO DELETE; replay outside measured window",
            WarmupAndSetupExcluded = true,
            MeasuredReleasesPerConcurrency = 30,
            Concurrency = CanonicalConcurrency,
            DelayReleases = 4,
            DelaysMilliseconds = CanonicalDelays,
            Sampling = new
            {
                IntervalMilliseconds = 10,
                ContinuousScenarioWindow = true,
                Cpu = "Process.TotalProcessorTime delta and sampled cumulative values",
                Allocations = "monotonic window-relative GC.GetTotalAllocatedBytes(true) samples plus raw 100 ms System.Runtime alloc-rate increments and non-vacuous normalized agreement or explicit unclaimability",
                Rss = "Process.WorkingSet64 start/peak/end and time series",
                BoundaryUncertainty = "first and last sample each bounded by one observed sampling interval"
            },
            Latency = "nearest-rank median/p95/p99 only at >=30 samples; min/median/max below 30",
            Contention = "exact attributed hashed parent/object application locks plus waiting/granted CentralArtifacts.PK_CentralArtifacts KEY-resource identity and blocker transaction/session/database/isolation",
            W3M = "actual intercepted ProcessNextAsync selection SQL replayed for STATISTICS XML/IO with parameterized SQL, scrubbed canonical plan XML, and canonical plan-facts preimages",
            FaultManifest = CanonicalFaultManifest
        }));

    private static string ComputeEnvironmentSha256(Issue250Preflight preflight, long gcDynamicAdaptationMode)
        => Sha(JsonSerializer.Serialize(new
        {
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture,
            Environment.ProcessorCount,
            preflight.CpuModel,
            preflight.TotalAvailableMemoryBytes,
            preflight.HostTotalMemoryBytes,
            preflight.ContainerMemoryLimitBytes,
            preflight.WorkspaceFileSystem,
            preflight.MinimumFullMemoryBytes,
            preflight.MinimumFullDiskBytes,
            WorkspaceStorageHeadroomSatisfied = preflight.AvailableWorkspaceDiskBytes >= preflight.MinimumFullDiskBytes,
            MinioStorageHeadroomSatisfied = preflight.AvailableMinioContainerStorageBytes >= preflight.MinimumFullDiskBytes,
            SqlStorageHeadroomSatisfied = preflight.AvailableSqlContainerStorageBytes >= preflight.MinimumFullDiskBytes,
            preflight.PinnedSdk,
            preflight.ExecutingSdk,
            GCSettings.IsServerGC,
            GcDynamicAdaptationMode = gcDynamicAdaptationMode,
            preflight.SqlServerImage,
            preflight.MinioImage
        }));

    private static long ReadGcDynamicAdaptationMode()
    {
        var configuration = GC.GetConfigurationVariables();
        if (!configuration.TryGetValue("GCDynamicAdaptationMode", out var value))
        {
            throw new InvalidOperationException("The runtime did not report GCDynamicAdaptationMode.");
        }
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<Issue250BaselineBinding> ValidateBaselineBindingAsync(
        string repositoryRoot,
        string phase,
        string compatibilityWorkloadSha256,
        string compatibilityProtocolSha256,
        string environmentSha256)
    {
        var path = Environment.GetEnvironmentVariable("HVO_EVIDENCE_BASELINE_MANIFEST");
        if (phase != "after")
        {
            return new Issue250BaselineBinding(
                phase == "baseline" ? "not-required-for-baseline" : "not-applicable-development",
                "N/A: only after evidence requires HVO_EVIDENCE_BASELINE_MANIFEST.",
                "N/A",
                "N/A",
                "N/A",
                "N/A",
                "N/A",
                compatibilityWorkloadSha256,
                Array.Empty<Issue250EvidenceFile>());
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("After evidence requires HVO_EVIDENCE_BASELINE_MANIFEST.");
        }
        var requestedFullPath = Path.GetFullPath(path, repositoryRoot);
        var fullPath = Path.Combine(
            ResolveExistingDirectory(Path.GetDirectoryName(requestedFullPath)!),
            Path.GetFileName(requestedFullPath));
        var stateRoot = Environment.GetEnvironmentVariable("HVO_AGENT_STATE_ROOT");
        var requestedAcceptedPath = string.IsNullOrWhiteSpace(stateRoot)
            ? Path.GetFullPath(AcceptedBaselineManifestPath)
            : Path.Combine(ResolveExistingDirectory(stateRoot), AcceptedBaselineRelativePath);
        var acceptedPath = Path.Combine(
            ResolveExistingDirectory(Path.GetDirectoryName(requestedAcceptedPath)!),
            Path.GetFileName(requestedAcceptedPath));
        if (!string.Equals(fullPath, acceptedPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "After evidence requires the exact preserved accepted issue #250 baseline manifest.");
        }
        var manifestBytes = await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false);
        Assert.AreEqual(AcceptedBaselineManifestSha256,
            Convert.ToHexString(SHA256.HashData(manifestBytes)));
        using var manifest = JsonDocument.Parse(manifestBytes);
        var root = manifest.RootElement;
        Assert.AreEqual("hvo-evidence-manifest-v1", root.GetProperty("Schema").GetString());
        Assert.AreEqual(250, root.GetProperty("Issue").GetInt32());
        Assert.AreEqual("baseline", root.GetProperty("Phase").GetString());
        Assert.AreEqual(ProductionRevision, root.GetProperty("ProductionRevision").GetString());
        Assert.AreEqual(AcceptedBaselineSourceRevision, root.GetProperty("SourceHead").GetString());
        Assert.AreEqual(1, root.GetProperty("Trial").GetInt32());
        Assert.AreEqual(TrialPolicy, root.GetProperty("TrialPolicy").GetString());
        Assert.AreEqual(root.GetProperty("WorkloadSha256").GetString(),
            root.GetProperty("CompatibilityWorkloadSha256").GetString());
        Assert.AreEqual(compatibilityProtocolSha256, root.GetProperty("CompatibilityProtocolSha256").GetString());
        Assert.AreEqual(environmentSha256, root.GetProperty("EnvironmentSha256").GetString());
        var manifestHarness = root.GetProperty("HarnessSha256").GetString()
            ?? throw new InvalidOperationException("Baseline manifest harness identity is absent.");
        var manifestProtocol = root.GetProperty("ProtocolSha256").GetString()
            ?? throw new InvalidOperationException("Baseline manifest protocol identity is absent.");
        Assert.AreEqual(AcceptedBaselineHarnessSha256, manifestHarness);
        Assert.AreEqual(AcceptedBaselineProtocolSha256, manifestProtocol);
        Assert.AreEqual(AcceptedBaselineCompatibilityProtocolSha256,
            root.GetProperty("CompatibilityProtocolSha256").GetString());
        var directory = Path.GetDirectoryName(fullPath)!;
        var expectedFiles = new[]
        {
            "central-transient-payload-release-evidence.json",
            "w0-fault-runtime-evidence.json",
            "w3m-normalized-plan-evidence.json"
        };
        var manifestFiles = root.GetProperty("Files").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(
            expectedFiles,
            manifestFiles.Select(file => file.GetProperty("Name").GetString()).ToArray());
        Assert.AreEqual(expectedFiles.Length, manifestFiles.Length);
        string? evidenceSha = null;
        string? baselineSemanticWorkloadSha256 = null;
        var verifiedFiles = new List<Issue250EvidenceFile>();
        foreach (var file in manifestFiles)
        {
            var name = file.GetProperty("Name").GetString()!;
            if (!expectedFiles.Contains(name, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Baseline manifest contains an unexpected payload file.");
            }
            var filePath = Path.GetFullPath(Path.Combine(directory, name));
            if (!filePath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Baseline payload path escapes its manifest directory.");
            }
            var bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            Assert.AreEqual(file.GetProperty("ByteLength").GetInt64(), bytes.LongLength);
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            Assert.AreEqual(file.GetProperty("Sha256").GetString(), sha);
            verifiedFiles.Add(new(name, bytes.LongLength, sha));
            using var payload = JsonDocument.Parse(bytes);
            var payloadRoot = payload.RootElement;
            Assert.AreEqual(250, payloadRoot.GetProperty("Issue").GetInt32());
            Assert.AreEqual("baseline", payloadRoot.GetProperty("Phase").GetString());
            Assert.AreEqual(TrialPolicy, payloadRoot.GetProperty("TrialPolicy").GetString());
            Assert.AreEqual(manifestHarness, payloadRoot.GetProperty("HarnessSha256").GetString());
            Assert.AreEqual(root.GetProperty("WorkloadSha256").GetString(),
                payloadRoot.GetProperty("WorkloadSha256").GetString());
            Assert.AreEqual(root.GetProperty("CompatibilityWorkloadSha256").GetString(),
                payloadRoot.GetProperty("CompatibilityWorkloadSha256").GetString());
            Assert.AreEqual(manifestProtocol, payloadRoot.GetProperty("ProtocolSha256").GetString());
            Assert.AreEqual(compatibilityProtocolSha256,
                payloadRoot.GetProperty("CompatibilityProtocolSha256").GetString());
            Assert.AreEqual(environmentSha256, payloadRoot.GetProperty("EnvironmentSha256").GetString());
            if (string.Equals(name, expectedFiles[0], StringComparison.Ordinal))
            {
                evidenceSha = sha;
                Assert.AreEqual("hvo-issue-250-central-transient-payload-release-evidence-v3",
                    payloadRoot.GetProperty("Schema").GetString());
                Assert.AreEqual(ProductionRevision, payloadRoot.GetProperty("ProductionRevision").GetString());
                var source = payloadRoot.GetProperty("Source");
                Assert.IsFalse(source.GetProperty("Dirty").GetBoolean());
                Assert.AreEqual("clean-source-attributed-review-required",
                    source.GetProperty("Claimability").GetString());
                Assert.AreEqual(root.GetProperty("SourceHead").GetString(), source.GetProperty("Head").GetString());
                Assert.AreEqual(source.GetProperty("Head").GetString(), source.GetProperty("RequestedRevision").GetString());
                Assert.AreEqual(root.GetProperty("Trial").GetInt32(), source.GetProperty("Trial").GetInt32());
                baselineSemanticWorkloadSha256 = AcceptedBaselineSemanticWorkloadSha256;
                Assert.AreEqual(compatibilityWorkloadSha256, baselineSemanticWorkloadSha256,
                    "Current workload differs from the independently pinned accepted-baseline semantic identity after excluding only revision-specific assembly provenance.");
            }
            else
            {
                Assert.AreEqual(name == expectedFiles[1]
                        ? "hvo-issue-250-w0-fault-runtime-evidence-v1"
                        : "hvo-issue-250-w3m-normalized-plan-evidence-v5",
                    payloadRoot.GetProperty("Schema").GetString());
            }
        }
        Assert.IsNotNull(evidenceSha);
        Assert.IsNotNull(baselineSemanticWorkloadSha256);
        return new Issue250BaselineBinding(
            "reviewed-baseline-manifest-validated",
            Convert.ToHexString(SHA256.HashData(manifestBytes)),
            evidenceSha,
            root.GetProperty("SourceHead").GetString() ?? "unknown",
            manifestHarness,
            manifestProtocol,
            baselineSemanticWorkloadSha256,
            compatibilityWorkloadSha256,
            verifiedFiles);
    }

    private static Issue250EvidenceFile DescribeEvidenceFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new Issue250EvidenceFile(
            Path.GetFileName(path), bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static async Task<string> RunProcessAsync(
        string workingDirectory,
        string fileName,
        params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start preflight process.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Issue #250 preflight process failed.");
        }
        _ = await errorTask.ConfigureAwait(false);
        return await outputTask.ConfigureAwait(false);
    }

    private static async Task ValidateSourceAsync(
        string repositoryRoot,
        string phase,
        bool smoke,
        string productionRevision,
        EvidenceSourceSnapshot source)
    {
        if (smoke || phase == "development")
        {
            return;
        }
        if (source.Dirty || source.RequestedRevision is null || source.Trial != 1
            || !string.Equals(source.Claimability, "clean-source-attributed-review-required", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Baseline/after evidence requires exact HVO_EVIDENCE_REVISION, a clean committed HEAD, and the issue-authorized HVO_EVIDENCE_TRIAL=1.");
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HVO_EVIDENCE_PRODUCTION_REVISION")))
        {
            throw new InvalidOperationException("Baseline/after evidence requires HVO_EVIDENCE_PRODUCTION_REVISION.");
        }
        if (phase == "baseline")
        {
            if (!string.Equals(productionRevision, ProductionRevision, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Baseline production revision must be the merged #249 HEAD.");
            }
            var ancestor = await RunGitExitCodeAsync(
                repositoryRoot, "merge-base", "--is-ancestor", productionRevision, source.Head).ConfigureAwait(false);
            if (ancestor != 0)
            {
                throw new InvalidOperationException("Baseline production revision is not an ancestor of evidence HEAD.");
            }
            var changedPaths = (await RunGitAsync(
                    repositoryRoot, "diff", "--name-only", $"{productionRevision}..{source.Head}", "--")
                .ConfigureAwait(false))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Order(StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(BaselineChangedPaths, changedPaths);
        }
        else if (!string.Equals(productionRevision, source.Head, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("After evidence requires production revision to equal evidence HEAD.");
        }
    }

    private static Task<EvidenceSourceSnapshot> CaptureSourceAsync(string repositoryRoot)
        => EvidenceSourceIdentity.CaptureAsync(
            repositoryRoot,
            typeof(CentralTransientPayloadReleaseIssue250PerformanceTests),
            typeof(CentralTransientPayloadReleaseService),
            typeof(VirtualSkyCameraModule),
            typeof(ArtifactManifestV2),
            typeof(TransientEventV1),
            typeof(EvidenceSourceIdentity));

    private static void AssertSourceUnchanged(EvidenceSourceSnapshot before, EvidenceSourceSnapshot after)
        => Assert.AreEqual(SourceSnapshotSha256(before), SourceSnapshotSha256(after),
            "Source/revision/assembly identity changed while issue #250 evidence was running.");

    private static string SourceSnapshotSha256(EvidenceSourceSnapshot source)
        => Sha(JsonSerializer.Serialize(PublicSource(source)));

    private static Issue250PublicSource PublicSource(EvidenceSourceSnapshot source)
        => new(
            source.RequestedRevision,
            source.Head,
            source.Branch,
            source.Dirty,
            source.DirtyDiffSha256,
            source.OutputDirectoryName,
            source.Trial,
            source.Claimability,
            source.Assemblies);

    private static void AssertEvidenceSafe(
        byte[] evidence,
        EvidenceSourceSnapshot source,
        Issue250PrivateValues privateValues)
    {
        var json = Encoding.UTF8.GetString(evidence);
        foreach (var secret in privateValues.SmallSecrets)
        {
            Assert.IsFalse(
                json.Contains(secret, StringComparison.OrdinalIgnoreCase),
                "Evidence privacy scan found a registered secret scalar.");
        }
        var permittedGuids = source.Assemblies.Select(item => item.ModuleVersionId.ToString("D"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Issue250Regex.Guid().Matches(json))
        {
            Assert.IsTrue(permittedGuids.Contains(match.Value), "Evidence privacy scan found an unregistered entity-like identifier.");
        }
        var compactGuidMatches = Issue250Regex.GuidN().Matches(json);
        Assert.IsEmpty(compactGuidMatches,
            $"Evidence privacy scan found a compact GUID-like identifier at: {string.Join(", ", FindCompactGuidPaths(json))}.");
        Assert.IsFalse(Issue250Regex.HexSha().Matches(json).Cast<Match>().Any(match => privateValues.Contains(match.Value)),
            "Evidence privacy scan found a registered private token/hash.");
        Assert.IsFalse(Issue250Regex.PrivateIssueToken().Matches(json).Cast<Match>().Any(match => privateValues.Contains(match.Value)),
            "Evidence privacy scan found a registered private issue token.");
        Assert.IsFalse(Issue250Regex.PrivateObjectReference().IsMatch(json),
            "Evidence privacy scan found a MinIO object URI/key.");
        Assert.IsFalse(Issue250Regex.CredentialAssignment().IsMatch(json),
            "Evidence privacy scan found a credential/connection assignment.");
    }

    private static List<string> FindCompactGuidPaths(string json)
    {
        using var document = JsonDocument.Parse(json);
        var paths = new List<string>();
        Visit(document.RootElement, "$", paths);
        return paths;

        static void Visit(JsonElement element, string path, List<string> paths)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    Visit(property.Value, $"{path}.{property.Name}", paths);
                }
                return;
            }
            if (element.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item, $"{path}[{index++}]", paths);
                }
                return;
            }
            if (element.ValueKind == JsonValueKind.String
                && Issue250Regex.GuidN().IsMatch(element.GetString() ?? string.Empty))
            {
                paths.Add(path);
            }
        }
    }

    private static string ReadPinnedSdk(string repositoryRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot, "global.json")));
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidOperationException("global.json SDK version is absent.");
    }

    private static async Task<string> ResolveRevisionAsync(string repositoryRoot, string revision)
        => (await RunGitAsync(repositoryRoot, "rev-parse", $"{revision}^{{commit}}").ConfigureAwait(false)).Trim();

    private static async Task<string> RunGitAsync(string repositoryRoot, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start git.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }
        return output;
    }

    private static async Task<int> RunGitExitCodeAsync(string repositoryRoot, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start git.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static string Sha(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class Issue250PrivateValues
    {
        private readonly ConcurrentDictionary<string, byte> values = new(StringComparer.OrdinalIgnoreCase);
        private readonly string[] smallSecrets;

        internal Issue250PrivateValues(params string[] initial)
        {
            foreach (var value in initial)
            {
                Add(value);
            }
            Add(new SqlConnectionStringBuilder(initial[0]).Password);
            smallSecrets = initial.Append(new SqlConnectionStringBuilder(initial[0]).Password)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal IReadOnlyList<string> SmallSecrets => smallSecrets;
        internal bool Contains(string value) => values.ContainsKey(value);

        internal void Add(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.TryAdd(value, 0);
                if (value.StartsWith("issue-250/", StringComparison.Ordinal))
                {
                    values.TryAdd(BucketPrefix + value, 0);
                }
            }
        }
    }

    private sealed class Issue250EvidenceCollector : IDisposable
    {
        private long externalDeadlockRetries;
        private long primarySqlDeadlockRetries;
        private long recoverySqlDeadlockRetries;
        private long inspectionSqlDeadlockRetries;
        private long internalCreationDeadlockRetries;
        private long internalCreationAmbiguousCommitRetries;
        private long internalCreationAmbiguousCommitRecoveries;
        private long internalCreationConflictExhaustions;
        private long releaseAttempts;
        private long acceptedReleaseResponses;
        private long recoveryProcessorAttempts;
        private long recoveryProcessorCompletions;
        private readonly MeterListener meterListener = new();
        internal Issue250EvidenceCollector()
        {
            Http = new Issue250DeleteHandler { InnerHandler = new SocketsHttpHandler() };
            SqlLocks = new Issue250SqlClientApplicationLockCollector();
            meterListener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CentralTransientLifecycleTelemetry.MeterName
                    && instrument.Name == "skymonitor.central.transient.retention.creation.conflicts")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            meterListener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
                RecordInternalCreationConflict(measurement, tags));
            meterListener.Start();
        }

        internal CountingDbCommandInterceptor Commands { get; } = new();
        internal CountingDbTransactionInterceptor Transactions { get; } = new();
        internal CountingDbCommandInterceptor RecoveryCommands { get; } = new();
        internal CountingDbTransactionInterceptor RecoveryTransactions { get; } = new();
        internal Issue250DeleteHandler Http { get; }
        internal Issue250SqlClientApplicationLockCollector SqlLocks { get; }

        internal void Reset()
        {
            Commands.Reset();
            Transactions.Reset();
            RecoveryCommands.Reset();
            RecoveryTransactions.Reset();
            Http.Reset();
            SqlLocks.Reset();
            Interlocked.Exchange(ref externalDeadlockRetries, 0);
            Interlocked.Exchange(ref primarySqlDeadlockRetries, 0);
            Interlocked.Exchange(ref recoverySqlDeadlockRetries, 0);
            Interlocked.Exchange(ref inspectionSqlDeadlockRetries, 0);
            Interlocked.Exchange(ref internalCreationDeadlockRetries, 0);
            Interlocked.Exchange(ref internalCreationAmbiguousCommitRetries, 0);
            Interlocked.Exchange(ref internalCreationAmbiguousCommitRecoveries, 0);
            Interlocked.Exchange(ref internalCreationConflictExhaustions, 0);
            Interlocked.Exchange(ref releaseAttempts, 0);
            Interlocked.Exchange(ref acceptedReleaseResponses, 0);
            Interlocked.Exchange(ref recoveryProcessorAttempts, 0);
            Interlocked.Exchange(ref recoveryProcessorCompletions, 0);
        }

        internal void RecordExternalDeadlockRetry(Issue250SqlRetryStage stage)
        {
            Interlocked.Increment(ref externalDeadlockRetries);
            switch (stage)
            {
                case Issue250SqlRetryStage.Primary:
                    Interlocked.Increment(ref primarySqlDeadlockRetries);
                    break;
                case Issue250SqlRetryStage.Recovery:
                    Interlocked.Increment(ref recoverySqlDeadlockRetries);
                    break;
                case Issue250SqlRetryStage.Inspection:
                    Interlocked.Increment(ref inspectionSqlDeadlockRetries);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stage));
            }
        }
        internal void RecordReleaseAttempt() => Interlocked.Increment(ref releaseAttempts);
        internal void RecordAcceptedReleaseResponse() => Interlocked.Increment(ref acceptedReleaseResponses);
        internal void RecordRecoveryProcessorAttempt() => Interlocked.Increment(ref recoveryProcessorAttempts);
        internal void RecordRecoveryProcessorCompletion() => Interlocked.Increment(ref recoveryProcessorCompletions);

        private void RecordInternalCreationConflict(
            long measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (measurement != 1 || tags.Length != 2)
            {
                throw new InvalidDataException("Creation-conflict telemetry must emit one with two bounded tags.");
            }

            string? reason = null;
            string? outcome = null;
            foreach (var tag in tags)
            {
                switch (tag.Key)
                {
                    case "reason" when reason is null:
                        reason = tag.Value?.ToString();
                        break;
                    case "outcome" when outcome is null:
                        outcome = tag.Value?.ToString();
                        break;
                    default:
                        throw new InvalidDataException("Creation-conflict telemetry emitted unsupported tags.");
                }
            }

            if (outcome == "exhausted" && reason is ("deadlock" or "ambiguous-commit"))
            {
                Interlocked.Increment(ref internalCreationConflictExhaustions);
            }
            else if (reason == "deadlock" && outcome == "retry")
            {
                Interlocked.Increment(ref internalCreationDeadlockRetries);
            }
            else if (reason == "ambiguous-commit" && outcome == "retry")
            {
                Interlocked.Increment(ref internalCreationAmbiguousCommitRetries);
            }
            else if (reason == "ambiguous-commit" && outcome == "recovered")
            {
                Interlocked.Increment(ref internalCreationAmbiguousCommitRecoveries);
            }
            else
            {
                throw new InvalidDataException(
                    $"Creation-conflict telemetry emitted unsupported values: {reason}/{outcome}.");
            }
        }

        internal Issue250CollectorSnapshot Snapshot()
        {
            var primaryTransactions = Transactions.Snapshot();
            var recoveryTransactions = RecoveryTransactions.Snapshot();
            var combinedTransactions = new Issue246TransactionSnapshot(
                primaryTransactions.StartAttempts + recoveryTransactions.StartAttempts,
                primaryTransactions.SuccessfullyStarted + recoveryTransactions.SuccessfullyStarted,
                primaryTransactions.Committed + recoveryTransactions.Committed,
                primaryTransactions.RolledBack + recoveryTransactions.RolledBack,
                primaryTransactions.Failed + recoveryTransactions.Failed,
                primaryTransactions.CommittedDurationMilliseconds
                    .Concat(recoveryTransactions.CommittedDurationMilliseconds).ToArray());
            return new(
                Commands.Count + RecoveryCommands.Count,
                Interlocked.Read(ref externalDeadlockRetries),
                Interlocked.Read(ref primarySqlDeadlockRetries),
                Interlocked.Read(ref recoverySqlDeadlockRetries),
                Interlocked.Read(ref inspectionSqlDeadlockRetries),
                Interlocked.Read(ref internalCreationDeadlockRetries),
                Interlocked.Read(ref internalCreationAmbiguousCommitRetries),
                Interlocked.Read(ref internalCreationAmbiguousCommitRecoveries),
                Interlocked.Read(ref internalCreationConflictExhaustions),
                Interlocked.Read(ref releaseAttempts),
                Interlocked.Read(ref acceptedReleaseResponses),
                Interlocked.Read(ref recoveryProcessorAttempts),
                Interlocked.Read(ref recoveryProcessorCompletions),
                Commands.Count,
                RecoveryCommands.Count,
                combinedTransactions,
                primaryTransactions,
                recoveryTransactions,
                Http.Snapshot(),
                SqlLocks.Snapshot());
        }

        public void Dispose()
        {
            meterListener.Dispose();
            SqlLocks.Dispose();
            Http.Dispose();
        }
    }

    private sealed class Issue250SqlClientApplicationLockCollector :
        IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>,
        IDisposable
    {
        private const string ListenerName = "SqlClientDiagnosticListener";
        private const string Before = "Microsoft.Data.SqlClient.WriteCommandBefore";
        private const string After = "Microsoft.Data.SqlClient.WriteCommandAfter";
        private const string Error = "Microsoft.Data.SqlClient.WriteCommandError";
        private readonly ConcurrentBag<IDisposable> subscriptions = [];
        private readonly ConcurrentDictionary<Guid, bool> inFlight = [];
        private readonly IDisposable allListeners;
        private string database = string.Empty;
        private string applicationName = string.Empty;
        private long getStarts;
        private long getCompletions;
        private long getErrors;
        private long releaseStarts;
        private long releaseCompletions;
        private long releaseErrors;

        internal Issue250SqlClientApplicationLockCollector()
        {
            allListeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        internal void Configure(string connectionString, string exactApplicationName)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            database = builder.InitialCatalog;
            applicationName = exactApplicationName;
        }

        internal void Reset()
        {
            if (!inFlight.IsEmpty)
            {
                throw new InvalidOperationException("SqlClient application-lock diagnostics still have in-flight commands.");
            }
            Interlocked.Exchange(ref getStarts, 0);
            Interlocked.Exchange(ref getCompletions, 0);
            Interlocked.Exchange(ref getErrors, 0);
            Interlocked.Exchange(ref releaseStarts, 0);
            Interlocked.Exchange(ref releaseCompletions, 0);
            Interlocked.Exchange(ref releaseErrors, 0);
        }

        internal Issue250ApplicationLockDiagnosticEvidence Snapshot()
        {
            var getStartValue = Interlocked.Read(ref getStarts);
            var getCompletionValue = Interlocked.Read(ref getCompletions);
            var getErrorValue = Interlocked.Read(ref getErrors);
            var releaseStartValue = Interlocked.Read(ref releaseStarts);
            var releaseCompletionValue = Interlocked.Read(ref releaseCompletions);
            var releaseErrorValue = Interlocked.Read(ref releaseErrors);
            var starts = getStartValue + releaseStartValue;
            var outcomes = getCompletionValue + getErrorValue + releaseCompletionValue + releaseErrorValue;
            return new(
                "SqlClientDiagnosticListener Microsoft.Data.SqlClient.WriteCommandBefore/After/Error",
                Sha(database),
                Sha(applicationName),
                getStartValue,
                getCompletionValue,
                getErrorValue,
                releaseStartValue,
                releaseCompletionValue,
                releaseErrorValue,
                starts,
                getCompletionValue + releaseCompletionValue,
                getErrorValue + releaseErrorValue,
                starts == outcomes ? "complete" : "incomplete",
                starts == outcomes
                    ? "Starts and terminal outcomes are exact for the scoped application/database."
                    : "Provider terminal diagnostic coverage was incomplete; starts/errors and exact DMV ownership remain authoritative.");
        }

        public void OnNext(DiagnosticListener value)
        {
            if (string.Equals(value.Name, ListenerName, StringComparison.Ordinal))
            {
                subscriptions.Add(value.Subscribe(this,
                    static eventName => eventName is Before or After or Error));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is null || GetProperty(value.Value, "OperationId") is not Guid operationId)
            {
                return;
            }
            if (value.Key == Before)
            {
                if (GetProperty(value.Value, "Command") is not DbCommand command
                    || !MatchesScope(command, out var operation))
                {
                    return;
                }
                var startsWithGet = operation == "get";
                if (!inFlight.TryAdd(operationId, startsWithGet))
                {
                    throw new InvalidOperationException("Duplicate SqlClient application-lock operation identifier.");
                }
                if (startsWithGet)
                {
                    Interlocked.Increment(ref getStarts);
                }
                else
                {
                    Interlocked.Increment(ref releaseStarts);
                }
                return;
            }
            if (!inFlight.TryRemove(operationId, out var get))
            {
                return;
            }
            switch (value.Key)
            {
                case After:
                    if (get)
                    {
                        Interlocked.Increment(ref getCompletions);
                    }
                    else
                    {
                        Interlocked.Increment(ref releaseCompletions);
                    }
                    break;
                case Error:
                    if (get)
                    {
                        Interlocked.Increment(ref getErrors);
                    }
                    else
                    {
                        Interlocked.Increment(ref releaseErrors);
                    }
                    break;
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            allListeners.Dispose();
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }

        private bool MatchesScope(DbCommand command, out string operation)
        {
            operation = string.Equals(command.CommandText, "sys.sp_getapplock", StringComparison.OrdinalIgnoreCase)
                ? "get"
                : string.Equals(command.CommandText, "sys.sp_releaseapplock", StringComparison.OrdinalIgnoreCase)
                    ? "release"
                    : string.Empty;
            if (operation.Length == 0 || command.Connection is null)
            {
                return false;
            }
            var builder = new SqlConnectionStringBuilder(command.Connection.ConnectionString);
            return string.Equals(builder.InitialCatalog, database, StringComparison.Ordinal)
                && builder.ApplicationName.StartsWith(applicationName + ".Case", StringComparison.Ordinal);
        }

        private static object? GetProperty(object value, string name)
            => value.GetType().GetProperty(name)?.GetValue(value);
    }

    private sealed class Issue250DiscardLogger : ILogger<CentralTransientLifecycleTelemetry>
    {
        internal static Issue250DiscardLogger Instance { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class Issue250FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Issue250ProcessNextQueryCaptureInterceptor : DbCommandInterceptor
    {
        private Issue250CapturedCommand? parent;
        private Issue250CapturedCommand? dueItem;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (parent is null
                && command.CommandText.Contains("CentralTransientPayloadReleases", StringComparison.Ordinal)
                && command.CommandText.Contains("ORDER BY", StringComparison.Ordinal)
                && command.CommandText.Contains("TOP(16)", StringComparison.Ordinal))
            {
                parent = new(
                    command.CommandText,
                    command.Parameters.Cast<SqlParameter>().Select(CopySqlParameter).ToArray());
            }
            if (dueItem is null
                && command.CommandText.Contains("CentralTransientPayloadReleaseItems", StringComparison.Ordinal)
                && command.CommandText.Contains("ORDER BY", StringComparison.Ordinal)
                && command.CommandText.Contains("TOP(1)", StringComparison.Ordinal))
            {
                dueItem = new(
                    command.CommandText,
                    command.Parameters.Cast<SqlParameter>().Select(CopySqlParameter).ToArray());
                throw new Issue250QueryCapturedException();
            }
            return ValueTask.FromResult(result);
        }

        internal Issue250CapturedCommand SingleParent()
            => parent ?? throw new InvalidOperationException(
                "Production ProcessNextAsync did not emit its pending-release selection query.");

        internal Issue250CapturedCommand SingleDueItem()
            => dueItem ?? throw new InvalidOperationException(
                "Production ProcessNextAsync did not emit its due-item selection query.");
    }

    private sealed class Issue250QueryCapturedException : Exception
    {
        public Issue250QueryCapturedException()
        {
        }

        public Issue250QueryCapturedException(string message)
            : base(message)
        {
        }

        public Issue250QueryCapturedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed record Issue250CapturedCommand(
        string CommandText,
        IReadOnlyList<SqlParameter> Parameters);

    private sealed class Issue250RuntimeCollector : IDisposable
    {
        private readonly ConcurrentQueue<Issue250RuntimeMetric> metrics = [];
        private readonly ConcurrentQueue<Issue250RuntimeActivity> activities = [];
        private readonly ConcurrentQueue<Issue250RuntimeLog> logs = [];
        private readonly MeterListener meter = new();
        private readonly ActivityListener activity;

        internal Issue250RuntimeCollector()
        {
            Logger = new Issue250RuntimeLogger(logs);
            meter.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CentralTransientLifecycleTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                RecordMetric(instrument, value, tags));
            meter.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                RecordMetric(instrument, value, tags));
            meter.Start();
            activity = new ActivityListener
            {
                ShouldListenTo = source => source.Name == CentralTransientLifecycleTelemetry.ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = value => activities.Enqueue(new(
                    value.OperationName,
                    value.Kind.ToString(),
                    value.Status.ToString(),
                    value.Duration.TotalMilliseconds,
                    value.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal)
                        .Select(tag => $"{tag.Key}={tag.Value}").ToArray()))
            };
            ActivitySource.AddActivityListener(activity);
        }

        internal ILogger<CentralTransientLifecycleTelemetry> Logger { get; }

        internal Issue250RuntimeEvidence Snapshot(string exceptionBehavior)
        {
            var result = new Issue250RuntimeEvidence(
                metrics.ToArray(), activities.ToArray(), logs.ToArray(),
                "operation/outcome", "central-transient.retention/Internal/Unset", 2166,
                exceptionBehavior);
            Assert.IsTrue(result.Metrics.All(item => item.Instrument == "skymonitor.central.transient.retention.items"
                ? item.TagKeys.SequenceEqual(["kind", "outcome"])
                : item.TagKeys.All(key => key is "operation" or "outcome")));
            Assert.IsTrue(result.Activities.All(item => item.Tags.Count == 0));
            return result;
        }

        internal Issue250RuntimeEvidence SnapshotAndAssertSingleSuccess(int expectedItemCount)
        {
            var result = Snapshot("No exception escaped; the operation reached durable completion.");
            Assert.IsGreaterThanOrEqualTo(2, result.Metrics.Count);
            var operation = result.Metrics.Single(item =>
                item.Instrument == "skymonitor.central.transient.lifecycle.operations");
            Assert.AreEqual(1d, operation.Value);
            Assert.AreEqual("retention", operation.Operation);
            Assert.AreEqual("completed", operation.Outcome);
            var duration = result.Metrics.Single(item =>
                item.Instrument == "skymonitor.central.transient.lifecycle.duration");
            Assert.IsGreaterThanOrEqualTo(0d, duration.Value);
            Assert.AreEqual("retention", duration.Operation);
            Assert.AreEqual("completed", duration.Outcome);
            var log = result.Logs.Single(item => item.EventId == 2166);
            Assert.AreEqual(2166, log.EventId);
            Assert.AreEqual(LogLevel.Information.ToString(), log.Level);
            Assert.AreEqual("completed", log.Outcome);
            Assert.AreEqual(expectedItemCount, log.ItemCount);
            Assert.IsFalse(log.ExceptionPresent);
            var itemMetrics = result.Metrics.Where(item =>
                item.Instrument == "skymonitor.central.transient.retention.items").ToArray();
            var itemLogs = result.Logs.Where(item => item.EventId == 2167).ToArray();
            Assert.AreEqual(itemMetrics.Length, itemLogs.Length);
            Assert.IsGreaterThanOrEqualTo(2, itemLogs.Length);
            Assert.IsTrue(itemLogs.All(item => item.Kind is "source" or "derivative"
                && item.Outcome is "reserved" or "released" or "preserved" or "retry"
                && item.RetryCount >= 0));
            var activityValue = result.Activities.Single();
            Assert.AreEqual("central-transient.retention", activityValue.Name);
            Assert.AreEqual(ActivityKind.Internal.ToString(), activityValue.Kind);
            Assert.AreEqual(ActivityStatusCode.Unset.ToString(), activityValue.Status);
            return result;
        }

        internal Issue250RuntimeEvidence SnapshotAndAssertFailedCallGap(string phase)
        {
            var result = Snapshot(
                phase == "after"
                    ? "Injected retryable MinIO failure is durably scheduled without parent completion; bounded item signals and the Unset activity close."
                    : "Injected MinIO failure escapes as MinioException. Baseline emits no event 2166 or operation/duration metric for this failed call; only the Unset activity closes.");
            if (phase == "after")
            {
                Assert.IsNotEmpty(result.Metrics);
                Assert.IsTrue(result.Metrics.All(item =>
                    item.Instrument == "skymonitor.central.transient.retention.items"));
                Assert.IsNotEmpty(result.Logs);
                Assert.IsTrue(result.Logs.All(item => item.EventId == 2167));
            }
            else
            {
                Assert.IsEmpty(result.Metrics);
                Assert.IsEmpty(result.Logs);
            }
            var activityValue = result.Activities.Single();
            Assert.AreEqual("central-transient.retention", activityValue.Name);
            Assert.AreEqual(ActivityKind.Internal.ToString(), activityValue.Kind);
            Assert.AreEqual(ActivityStatusCode.Unset.ToString(), activityValue.Status);
            Assert.IsEmpty(activityValue.Tags);
            return result;
        }

        public void Dispose()
        {
            activity.Dispose();
            meter.Dispose();
        }

        private void RecordMetric<T>(
            Instrument instrument,
            T value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
            where T : struct
        {
            var values = tags.ToArray();
            metrics.Enqueue(new Issue250RuntimeMetric(
                instrument.Name,
                Convert.ToDouble(value, CultureInfo.InvariantCulture),
                Tag(values, "operation"),
                Tag(values, "outcome"),
                values.Select(item => item.Key).Order(StringComparer.Ordinal).ToArray()));
        }

        private static string? Tag(IReadOnlyList<KeyValuePair<string, object?>> tags, string key)
            => tags.FirstOrDefault(item => item.Key == key).Value?.ToString();

        private sealed class Issue250RuntimeLogger(ConcurrentQueue<Issue250RuntimeLog> logs)
            : ILogger<CentralTransientLifecycleTelemetry>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                string? outcome = null;
                int? itemCount = null;
                string? kind = null;
                int? retryCount = null;
                if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                {
                    var bounded = fields.Where(item => item.Key != "{OriginalFormat}").ToArray();
                    if (eventId.Id == 2167)
                    {
                        Assert.IsTrue(bounded.All(item => item.Key is "Kind" or "Outcome" or "RetryCount"));
                        kind = bounded.Single(item => item.Key == "Kind").Value?.ToString();
                        outcome = bounded.Single(item => item.Key == "Outcome").Value?.ToString();
                        retryCount = Convert.ToInt32(
                            bounded.Single(item => item.Key == "RetryCount").Value,
                            CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        Assert.IsTrue(bounded.All(item => item.Key is "RuntimeId" or "Outcome" or "ItemCount"));
                        Assert.IsTrue(bounded.Where(item => item.Key == "Outcome")
                            .All(item => item.Value?.ToString() is "completed" or "failed"));
                        Assert.IsTrue(bounded.Any(item => item.Key == "RuntimeId"
                            && !string.IsNullOrWhiteSpace(item.Value?.ToString())));
                        outcome = bounded.Single(item => item.Key == "Outcome").Value?.ToString();
                        itemCount = Convert.ToInt32(
                            bounded.Single(item => item.Key == "ItemCount").Value,
                            CultureInfo.InvariantCulture);
                    }
                }
                logs.Enqueue(new(eventId.Id, logLevel.ToString(), outcome, itemCount, kind, retryCount,
                    exception is not null));
            }
        }
    }

    private sealed class Issue250FailingDeleteHandler(int failFromDelete) : DelegatingHandler
    {
        private int deletes;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete && Interlocked.Increment(ref deletes) >= failFromDelete)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("<Error><Code>ServiceUnavailable</Code><Message>Injected issue 250 failure.</Message><RequestId>bounded</RequestId><HostId>bounded</HostId></Error>", Encoding.UTF8, "application/xml")
                });
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Issue250DeleteHandler : DelegatingHandler
    {
        private readonly ConcurrentQueue<double> durations = [];
        private readonly ConcurrentQueue<Issue250DeleteWindow> windows = [];
        private readonly ConcurrentDictionary<string, byte> deleteTargets = new(StringComparer.Ordinal);
        private TaskCompletionSource firstEntered = NewSignal();
        private TimeSpan delay;
        private long origin;
        private long requests;
        private long deletes;
        private long bucketHeadRequests;
        private long objectHeadRequests;
        private long otherMethodRequests;
        private long duplicateDeleteRequests;
        private long deleteResponses;
        private long deleteExceptions;
        private long requestBytes;
        private long responseBytes;
        private long unknownRequestLengths;
        private long unknownResponseLengths;
        private string? firstObjectKey;
        private IReadOnlyDictionary<string, int> caseByObjectKey = new Dictionary<string, int>();

        internal Task FirstEntered => firstEntered.Task;
        internal string? FirstObjectKey => Volatile.Read(ref firstObjectKey);
        internal IReadOnlyList<Issue250DeleteWindow> Windows => windows.OrderBy(item => item.EnteredMilliseconds).ToArray();

        internal void Configure(
            TimeSpan configuredDelay,
            long measurementOrigin = 0,
            IReadOnlyDictionary<string, int>? configuredCaseByObjectKey = null)
        {
            delay = configuredDelay;
            origin = measurementOrigin;
            caseByObjectKey = configuredCaseByObjectKey ?? new Dictionary<string, int>();
        }

        internal void Reset()
        {
            Interlocked.Exchange(ref requests, 0);
            Interlocked.Exchange(ref deletes, 0);
            Interlocked.Exchange(ref bucketHeadRequests, 0);
            Interlocked.Exchange(ref objectHeadRequests, 0);
            Interlocked.Exchange(ref otherMethodRequests, 0);
            Interlocked.Exchange(ref duplicateDeleteRequests, 0);
            Interlocked.Exchange(ref deleteResponses, 0);
            Interlocked.Exchange(ref deleteExceptions, 0);
            Interlocked.Exchange(ref requestBytes, 0);
            Interlocked.Exchange(ref responseBytes, 0);
            Interlocked.Exchange(ref unknownRequestLengths, 0);
            Interlocked.Exchange(ref unknownResponseLengths, 0);
            durations.Clear();
            windows.Clear();
            deleteTargets.Clear();
            firstEntered = NewSignal();
            firstObjectKey = null;
        }

        internal Issue250ObjectProtocolSnapshot Snapshot()
            => new(
                Interlocked.Read(ref requests),
                Interlocked.Read(ref deletes),
                Interlocked.Read(ref bucketHeadRequests),
                Interlocked.Read(ref objectHeadRequests),
                Interlocked.Read(ref otherMethodRequests),
                deleteTargets.Count,
                Interlocked.Read(ref duplicateDeleteRequests),
                Interlocked.Read(ref deleteResponses),
                Interlocked.Read(ref deleteExceptions),
                Interlocked.Read(ref requestBytes),
                Interlocked.Read(ref responseBytes),
                Interlocked.Read(ref unknownRequestLengths),
                Interlocked.Read(ref unknownResponseLengths),
                durations.ToArray());

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            RecordLength(request.Content?.Headers.ContentLength, ref requestBytes, ref unknownRequestLengths,
                request.Content is not null);
            var isDelete = request.Method == HttpMethod.Delete;
            var isHead = request.Method == HttpMethod.Head;
            if (isHead)
            {
                var path = GetRequestPath(request);
                if (path is Bucket or Bucket + "/")
                {
                    Interlocked.Increment(ref bucketHeadRequests);
                }
                else if (path.StartsWith(Bucket + "/", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref objectHeadRequests);
                }
                else
                {
                    Interlocked.Increment(ref otherMethodRequests);
                }
            }
            else if (!isDelete)
            {
                Interlocked.Increment(ref otherMethodRequests);
            }
            var started = isDelete ? Stopwatch.GetTimestamp() : 0;
            var entered = isDelete ? ElapsedMilliseconds(started) : 0;
            if (isDelete)
            {
                Interlocked.Increment(ref deletes);
                var objectKey = GetObjectKey(request);
                if (!deleteTargets.TryAdd(objectKey, 0))
                {
                    Interlocked.Increment(ref duplicateDeleteRequests);
                }
                Interlocked.CompareExchange(ref firstObjectKey, objectKey, null);
                firstEntered.TrySetResult();
            }
            HttpResponseMessage response;
            try
            {
                if (isDelete && delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (isDelete)
                {
                    Interlocked.Increment(ref deleteResponses);
                }
            }
            catch
            {
                if (isDelete)
                {
                    Interlocked.Increment(ref deleteExceptions);
                }
                throw;
            }
            finally
            {
                if (isDelete)
                {
                    durations.Enqueue(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }
            if (!isHead && response.StatusCode != HttpStatusCode.NoContent)
            {
                RecordLength(response.Content.Headers.ContentLength, ref responseBytes, ref unknownResponseLengths, true);
            }
            if (isDelete)
            {
                var exited = ElapsedMilliseconds(Stopwatch.GetTimestamp());
                var key = GetObjectKey(request);
                windows.Enqueue(new Issue250DeleteWindow(
                    entered,
                    exited,
                    caseByObjectKey.GetValueOrDefault(key, -1)));
            }
            return response;
        }

        private double ElapsedMilliseconds(long timestamp)
            => origin == 0 ? 0 : Stopwatch.GetElapsedTime(origin, timestamp).TotalMilliseconds;

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static string GetObjectKey(HttpRequestMessage request)
        {
            var path = GetRequestPath(request);
            const string prefix = Bucket + "/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("MinIO DELETE did not target the evidence bucket.");
            }
            return path[prefix.Length..];
        }

        private static string GetRequestPath(HttpRequestMessage request)
            => request.RequestUri?.GetComponents(UriComponents.Path, UriFormat.Unescaped)
                ?? throw new InvalidOperationException("MinIO request URI is unavailable.");

        private static void RecordLength(long? value, ref long bytes, ref long unknown, bool present)
        {
            if (!present)
            {
                return;
            }
            if (value.HasValue)
            {
                Interlocked.Add(ref bytes, value.Value);
            }
            else
            {
                Interlocked.Increment(ref unknown);
            }
        }
    }

    private static async Task ValidateHttpAccountingCollectorAsync()
    {
        using var handler = new Issue250DeleteHandler { InnerHandler = new Issue250HttpAccountingStubHandler() };
        using var client = new HttpClient(handler);
        static HttpRequestMessage Request(HttpMethod method, string path)
            => new(method, $"http://localhost/{path}");

        using (await client.SendAsync(Request(HttpMethod.Delete, $"{Bucket}/first")).ConfigureAwait(false)) { }
        using (await client.SendAsync(Request(HttpMethod.Delete, $"{Bucket}/first")).ConfigureAwait(false)) { }
        using (await client.SendAsync(Request(HttpMethod.Head, Bucket)).ConfigureAwait(false)) { }
        using (await client.SendAsync(Request(HttpMethod.Head, $"{Bucket}/first")).ConfigureAwait(false)) { }
        using (await client.SendAsync(Request(HttpMethod.Get, $"{Bucket}/first")).ConfigureAwait(false)) { }
        await Assert.ThrowsExactlyAsync<HttpRequestException>(async () =>
            await client.SendAsync(Request(HttpMethod.Delete, $"{Bucket}/throws")).ConfigureAwait(false));

        var snapshot = handler.Snapshot();
        Assert.AreEqual(6L, snapshot.Requests);
        Assert.AreEqual(3L, snapshot.Deletes);
        Assert.AreEqual(1L, snapshot.BucketHeadRequests);
        Assert.AreEqual(1L, snapshot.ObjectHeadRequests);
        Assert.AreEqual(1L, snapshot.OtherMethodRequests);
        Assert.AreEqual(2L, snapshot.UniqueDeleteTargets);
        Assert.AreEqual(1L, snapshot.DuplicateDeleteRequests);
        Assert.AreEqual(2L, snapshot.DeleteResponses);
        Assert.AreEqual(1L, snapshot.DeleteExceptions);
        Assert.AreEqual(3L, snapshot.ResponseEntityBytes);
        Assert.AreEqual(3, snapshot.DeleteDurationMilliseconds.Count);

        handler.Reset();
        handler.Configure(TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        var cancelledDelete = client.SendAsync(
            Request(HttpMethod.Delete, $"{Bucket}/cancelled"), cancellation.Token);
        await handler.FirstEntered.ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await cancelledDelete.ConfigureAwait(false));
        snapshot = handler.Snapshot();
        Assert.AreEqual(1L, snapshot.Requests);
        Assert.AreEqual(1L, snapshot.Deletes);
        Assert.AreEqual(1L, snapshot.UniqueDeleteTargets);
        Assert.AreEqual(1L, snapshot.DeleteExceptions);
        Assert.AreEqual(1, snapshot.DeleteDurationMilliseconds.Count);
    }

    private sealed class Issue250HttpAccountingStubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/throws", StringComparison.Ordinal) == true)
            {
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("Injected collector preflight failure."));
            }
            var response = new HttpResponseMessage(
                request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK);
            if (request.Method != HttpMethod.Delete)
            {
                response.Content = new ByteArrayContent([1, 2, 3]);
            }
            return Task.FromResult(response);
        }
    }

    private sealed record Issue250CollectorSnapshot(
        long EfCommands,
        long ExternalSqlDeadlockRetries,
        long PrimarySqlDeadlockRetries,
        long RecoverySqlDeadlockRetries,
        long InspectionSqlDeadlockRetries,
        long InternalCreationDeadlockRetries,
        long InternalCreationAmbiguousCommitRetries,
        long InternalCreationAmbiguousCommitRecoveries,
        long InternalCreationConflictExhaustions,
        long ReleaseAttempts,
        long AcceptedReleaseResponses,
        long RecoveryProcessorAttempts,
        long RecoveryProcessorCompletions,
        long PrimaryEfCommands,
        long RecoveryEfCommands,
        Issue246TransactionSnapshot SqlTransactions,
        Issue246TransactionSnapshot PrimarySqlTransactions,
        Issue246TransactionSnapshot RecoverySqlTransactions,
        Issue250ObjectProtocolSnapshot ObjectStore,
        Issue250ApplicationLockDiagnosticEvidence ApplicationLocks);

    private sealed record Issue250ObjectProtocolSnapshot(
        long Requests,
        long Deletes,
        long BucketHeadRequests,
        long ObjectHeadRequests,
        long OtherMethodRequests,
        long UniqueDeleteTargets,
        long DuplicateDeleteRequests,
        long DeleteResponses,
        long DeleteExceptions,
        long RequestEntityBytes,
        long ResponseEntityBytes,
        long UnknownRequestEntityLengths,
        long UnknownResponseEntityLengths,
        IReadOnlyList<double> DeleteDurationMilliseconds);

    private enum Issue250SqlRetryStage
    {
        Primary,
        Recovery,
        Inspection
    }

    private sealed record Issue250ApplicationLockDiagnosticEvidence(
        string Provider,
        string DatabaseSha256,
        string ApplicationNameSha256,
        long GetApplicationLockStarts,
        long GetApplicationLockCompletions,
        long GetApplicationLockErrors,
        long ReleaseApplicationLockStarts,
        long ReleaseApplicationLockCompletions,
        long ReleaseApplicationLockErrors,
        long TotalStarts,
        long TotalCompletions,
        long TotalErrors,
        string CompletionCoverage,
        string Limitation);

    private sealed record Issue250DeleteWindow(
        double EnteredMilliseconds,
        double ExitedMilliseconds,
        int CaseIndex);

    private sealed class Issue250Database(ApplicationDbContext context, string connectionString) : IAsyncDisposable
    {
        internal ApplicationDbContext Context { get; } = context;
        internal string ConnectionString { get; } = connectionString;
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed record Issue250Payload(string Name, byte[] Bytes, string Sha256);

    private sealed record Issue250Payloads(Issue250Payload Source, Issue250Payload Preview, Issue250Payload Overlay)
    {
        internal long ReleasedBytesPerCase => Source.Bytes.LongLength * 4 + Preview.Bytes.LongLength;
        internal long TotalBytesPerCase => Source.Bytes.LongLength * 5 + Preview.Bytes.LongLength + Overlay.Bytes.LongLength;
        internal long HeldBytesPerCase => Source.Bytes.LongLength + Overlay.Bytes.LongLength;
    }

    private sealed record Issue250W0Fixture(
        Issue250Payloads Payloads,
        Issue250W0GeneratorEvidence Generator);

    private sealed record Issue250W0GeneratorEvidence(
        string Workload,
        string Sha256,
        int Width,
        int Height,
        int StrideBytes,
        string PixelFormat,
        string ByteOrder,
        long ByteLength,
        string Catalog,
        string SceneUtc,
        string InitialSetpoint,
        string FirstCaptureRequest,
        string ConfigSha256,
        string FixtureSourcePath,
        string FixtureSourceSha256,
        string RunnerSourceSha256,
        string ExposureControllerSourceSha256,
        string VirtualSkySourceSha256,
        string CameraAgentCommonAssemblySha256);

    private sealed record Issue250Object(
        string Kind,
        int WorkloadOrdinal,
        string Key,
        Guid RecordId,
        Guid ArtifactId,
        Issue250Payload Payload,
        bool Held);

    private sealed record Issue250ReleaseCase(
        Guid EventId,
        byte[] RowVersion,
        string Actor,
        string IdempotencyKey,
        IReadOnlyList<Issue250Object> Objects,
        Issue250ImmutableSnapshot ImmutableBefore,
        IReadOnlyList<Issue250TargetSnapshot> TargetsBefore,
        Issue250HoldSnapshot HoldsBefore,
        string Workload);

    private sealed record Issue250ImmutableSnapshot(
        IReadOnlyDictionary<string, int> Counts,
        string Hash,
        string IncludedColumns);

    private sealed record Issue250TargetSnapshot(
        Guid RecordId,
        string StorageReference,
        string RowVersion,
        string RecoveryGeneration,
        string State);

    private sealed record Issue250HoldSnapshot(string Hash, IReadOnlyList<string> Semantics);

    private sealed record Issue250CanonicalTable(
        string Table,
        string Predicate,
        IReadOnlyList<string> ExcludedColumns);

    private sealed record Issue250MeasuredWave(
        IReadOnlyList<double> ReleaseLatenciesMilliseconds,
        double WallMilliseconds,
        Issue250ResourceEvidence Resources);

    private sealed record Issue250ResourceEvidence(
        double ProcessCpuMilliseconds,
        long AllocatedBytesDelta,
        int ProcessSamples,
        int TargetSamplingIntervalMilliseconds,
        double EffectiveSamplingIntervalMilliseconds,
        double MaximumSamplingIntervalMilliseconds,
        double AllocatedBytesPerSecond,
        Issue250AllocationEvidence Allocation,
        long RssStartBytes,
        long RssPeakBytes,
        long RssEndBytes,
        double WindowStartUncertaintyMilliseconds,
        double WindowEndUncertaintyMilliseconds,
        IReadOnlyList<Issue250ProcessSample> TimeSeries);

    private sealed record Issue250ResourceAggregate(
        double ProcessCpuMilliseconds,
        long AllocatedBytesDelta,
        int ProcessSamples,
        int TargetSamplingIntervalMilliseconds,
        double EffectiveSamplingIntervalMilliseconds,
        double MaximumSamplingIntervalMilliseconds,
        double AllocatedBytesPerSecond,
        Issue250AllocationEvidence Allocation,
        long RssStartBytes,
        long RssPeakBytes,
        long RssEndBytes,
        double WindowStartUncertaintyMilliseconds,
        double WindowEndUncertaintyMilliseconds,
        IReadOnlyList<Issue250ProcessSample> TimeSeries);

    private sealed record Issue250ProcessSample(
        double ElapsedMilliseconds,
        double ProcessCpuTotalMilliseconds,
        long AllocatedBytesSinceWindowStart,
        long RssBytes);

    private sealed record Issue250AllocationEvidence(
        long SampledBytes,
        int Samples,
        int IntervalMilliseconds,
        long AbsoluteDifferenceFromExactGcBytes,
        double DifferenceRatio,
        double BoundaryUncertaintyRatio,
        bool CrossCheckClaimable,
        string CrossCheckClaimability,
        bool AgreementWithinBoundaryUncertainty,
        IReadOnlyList<Issue170AllocationSample> RawSamples);

    private sealed record Issue250Latency(
        int Samples,
        double MinimumMilliseconds,
        double MedianMilliseconds,
        double? P95Milliseconds,
        double? P99Milliseconds,
        double MaximumMilliseconds,
        IReadOnlyList<double> OrderedSamplesMilliseconds,
        string OrderedSamplesSha256);

    private sealed record Issue250ProtocolEvidence(
        long EfSqlCommands,
        long ExternalSqlDeadlockRetries,
        long PrimarySqlDeadlockRetries,
        long RecoverySqlDeadlockRetries,
        long InspectionSqlDeadlockRetries,
        long InternalCreationDeadlockRetries,
        long InternalCreationAmbiguousCommitRetries,
        long InternalCreationAmbiguousCommitRecoveries,
        long InternalCreationConflictExhaustions,
        long ReleaseAttempts,
        long AcceptedReleaseResponses,
        long RecoveryProcessorAttempts,
        long RecoveryProcessorCompletions,
        long PrimaryEfSqlCommands,
        long RecoveryEfSqlCommands,
        Issue250ApplicationLockDiagnosticEvidence ApplicationLockCommands,
        long TransactionStartAttempts,
        long TransactionsStarted,
        long TransactionsCommitted,
        long TransactionsRolledBack,
        long TransactionsFailed,
        Issue250Latency CommittedTransactionDuration,
        Issue250TransactionCountEvidence PrimaryTransactions,
        Issue250TransactionCountEvidence RecoveryTransactions,
        long MinioRequests,
        long MinioDeletes,
        long MinioBucketHeadRequests,
        long MinioObjectHeadRequests,
        long MinioOtherMethodRequests,
        long MinioUniqueDeleteTargets,
        long MinioDuplicateDeleteRequests,
        long MinioDeleteResponses,
        long MinioDeleteExceptions,
        long MinioRequestEntityBytes,
        long MinioResponseEntityBytes,
        long MinioUnknownRequestEntityLengths,
        long MinioUnknownResponseEntityLengths,
        Issue250Latency MinioDeleteDuration);

    private sealed record Issue250TransactionCountEvidence(
        long StartAttempts,
        long SuccessfullyStarted,
        long Committed,
        long RolledBack,
        long Failed);

    private sealed record Issue250ReleaseScenarioEvidence(
        int Concurrency,
        int EffectiveMaximumConcurrency,
        int Warmups,
        int MeasuredReleases,
        double WallMilliseconds,
        Issue250Latency ReleaseLatency,
        Issue250Latency DeleteItemLatency,
        double ReleasesPerSecond,
        double ReleasedItemsPerSecond,
        double ReleasedLogicalBytesPerSecond,
        Issue250ResourceAggregate Resources,
        Issue250ProtocolEvidence Protocol,
        Issue250CorrectnessEvidence Correctness);

    private sealed record Issue250DelayEvidence(
        int DeleteDelayMilliseconds,
        int Releases,
        double WallMilliseconds,
        Issue250Latency ReleaseLatency,
        Issue250ResourceAggregate Resources,
        Issue250ProtocolEvidence NaturalProtocol,
        Issue250SqlEvidence NaturalSql,
        Issue250ProtocolEvidence ContentionProtocol,
        Issue250SqlEvidence ContentionSql,
        Issue250WriterEvidence ExactRowWriter,
        Issue250CorrectnessEvidence NaturalCorrectness,
        Issue250CorrectnessEvidence ContentionCorrectness);

    private sealed record Issue250CaseCorrectness(
        bool ExactPreReleaseObjects,
        bool FiveAbsentTwoPreserved,
        bool SqlStatesExact,
        bool ParentItemsExact,
        bool ImmutableRowsStable,
        bool ExactReplay,
        IReadOnlyList<string> ItemShape,
        string ImmutableHash,
        string SemanticMapSha256,
        string ReleaseAuditSha256,
        IReadOnlyList<string> SemanticOutcomeVector,
        string Representation);

    private sealed record Issue250CorrectnessEvidence(
        int Releases,
        bool ExactPreReleaseObjects,
        bool FiveAbsentTwoPreserved,
        bool SqlStatesExact,
        bool ParentItemsExact,
        bool ImmutableRowsStable,
        string StableImmutableAggregateSha256,
        bool ExactReplay,
        IReadOnlyList<string> NormalizedItemShapes,
        IReadOnlyList<string> SemanticMapSha256,
        IReadOnlyList<string> ReleaseAuditSha256,
        IReadOnlyList<string> SemanticOutcomeVector,
        IReadOnlyList<string> Representations,
        long FinalPendingParents,
        long FinalPendingItems,
        long FinalLogicalPendingBytes,
        long FinalOldestPendingAgeMilliseconds);

    private sealed record Issue250WriterEvidence(
        string Outcome,
        double ElapsedMilliseconds,
        bool ExactRowUpdated,
        int DeadlineMilliseconds,
        string TargetRecordIdSha256);

    private sealed record Issue250Backlog(
        long PendingParents,
        long PendingItems,
        long LogicalPendingBytes,
        long OldestAgeMilliseconds);

    private sealed record Issue250SqlSample(
        double ElapsedMilliseconds,
        double ObservationStartedMilliseconds,
        int Sessions,
        int ActiveRequests,
        int OpenTransactionSessions,
        IReadOnlyList<int> OpenTransactionSessionsByCase,
        int SessionApplicationLocks,
        int ObjectApplicationLocks,
        int ParentReleaseApplicationLocks,
        int BlockedWriterRequests,
        int AttributedBlockedWriterRequests,
        int UnexpectedApplicationLocks,
        int? ObjectLockOwnerSessionId,
        int? ParentLockOwnerSessionId,
        int? WriterSessionId,
        int? BlockerSessionId,
        long? BlockerTransactionId,
        Issue250RowBlockEvidence? ExactRowBlock,
        long ActiveTransactionLogBytes,
        long? PendingParents,
        long? PendingItems,
        long? LogicalPendingBytes,
        long? OldestPendingAgeMilliseconds,
        bool BacklogUnavailable);

    private sealed record Issue250RowBlockEvidence(
        string TargetRecordIdSha256,
        int WriterSessionId,
        int BlockerSessionId,
        long BlockerTransactionId,
        int DatabaseId,
        bool DatabaseIsCurrent,
        bool BlockerApplicationMatchesRelease,
        bool BlockerTransactionOwnsGrantedResource,
        bool WaitingAndGrantedResourceIdentityExact,
        int WriterIsolationLevel,
        int BlockerIsolationLevel,
        string WaitType,
        string ResourceType,
        string ResourceDescription,
        long ResourceAssociatedEntityId,
        string Table,
        string Index,
        string WriterRequestMode,
        string WriterRequestStatus,
        string WriterRequestOwnerType,
        string BlockerRequestMode,
        string BlockerRequestStatus,
        string BlockerRequestOwnerType,
        bool ApplicationLockFenceSessionsDistinctFromBlocker);

    private sealed record Issue250SqlEvidence(
        int Samples,
        int TargetSamplingIntervalMilliseconds,
        double EffectiveSamplingIntervalMilliseconds,
        double MaximumSamplingIntervalMilliseconds,
        int DeleteWindows,
        int DeleteWindowsWithSamples,
        double DeleteWindowCoverage,
        IReadOnlyList<int> SamplesPerDeleteWindow,
        int SamplesInsideDeleteWindows,
        double? FirstDeleteWindowSampleMilliseconds,
        double? LastDeleteWindowSampleMilliseconds,
        int MaximumSessions,
        int MaximumActiveRequests,
        int MaximumOpenTransactionSessions,
        int MaximumSessionApplicationLocks,
        int MaximumObjectApplicationLocks,
        int MaximumParentReleaseApplicationLocks,
        int MaximumBlockedWriterRequests,
        int MaximumAttributedBlockedWriterRequests,
        int MaximumUnexpectedApplicationLocks,
        int ExactAttributedBlockerSamples,
        string? ExactRowBlockTargetRecordIdSha256,
        long MaximumActiveTransactionLogBytes,
        double TransactionOverlapMilliseconds,
        double ObjectLockWindowMilliseconds,
        double ParentReleaseLockWindowMilliseconds,
        bool LockDurationClaimable,
        string LockDurationClaimability,
        bool BacklogClaimable,
        string BacklogClaimability,
        int BacklogUnavailableSamples,
        int ValidBacklogSamples,
        double BacklogSampleCoverage,
        long MaximumPendingParents,
        long MaximumPendingItems,
        long MaximumLogicalPendingBytes,
        long MaximumOldestPendingAgeMilliseconds,
        IReadOnlyList<Issue250SqlSample> RawSamples);

    private sealed record Issue250W3MCounts(
        int CompletedParents,
        long TerminalItems,
        int PendingParents,
        long PendingItems,
        int DistinctEvents);

    private sealed record Issue250PlanEvidence(
        string NormalizedParameterizedQuery,
        string NormalizedQuerySha256,
        string CanonicalPlanXml,
        string CanonicalPlanSha256,
        long LogicalReads,
        int SelectedRows,
        bool IndexUsed,
        string CanonicalPlanFactsJson,
        string NormalizedPlanFactsSha256,
        IReadOnlyList<string> Operators,
        IReadOnlyList<string> Indexes);

    private sealed record Issue250ObjectVerificationEvidence(
        long ObservedByteLength,
        string ObservedSha256,
        bool ExactLengthAndSha256);

    private sealed record Issue250W3MTempTableEvidence(
        int ParentRows,
        long TargetRows,
        double DurationMilliseconds);

    private sealed record Issue250W3MFoundationEvidence(
        int HistoryFrames,
        int SentinelEvents,
        int SentinelReleases,
        int SentinelItems,
        double DurationMilliseconds);

    private sealed record Issue250W3MSetupPhaseResult(
        int FirstRows,
        int SecondRows,
        double DurationMilliseconds);

    private sealed record Issue250W3MSetupBatchEvidence(
        int Batch,
        int FirstSequence,
        int LastSequence,
        int ParentRows,
        int EventRows,
        int ReleaseRows,
        int ArtifactRows,
        int ItemRows,
        int ItemTransitionRows,
        int ReleaseTransitionRows,
        double PhaseADurationMilliseconds,
        double PhaseBDurationMilliseconds,
        double PhaseCDurationMilliseconds,
        double TotalDurationMilliseconds);

    private sealed record Issue250W3MSetupRowCounts(
        long Events,
        long Releases,
        long Artifacts,
        long Items,
        long ItemTransitions,
        long ReleaseTransitions);

    private sealed record Issue250RequiredTrigger(
        string Name,
        string Table,
        IReadOnlyList<string> ExpectedConcepts);

    private sealed record Issue250RequiredSchemaRelation(
        string Name,
        string ParentTable,
        IReadOnlyList<string> ParentColumns,
        string ReferencedTable,
        IReadOnlyList<string> ReferencedColumns,
        string DeleteAction,
        string UpdateAction,
        bool Enabled,
        bool Trusted);

    private sealed record Issue250RequiredCheckConstraint(
        string Name,
        string Table,
        IReadOnlyList<string> ExpectedConcepts);

    private sealed record Issue250IndexKeyColumn(string Name, bool Descending);

    private sealed record Issue250RequiredUniqueIndex(
        string Name,
        string Table,
        string Kind,
        IReadOnlyList<Issue250IndexKeyColumn> KeyColumns,
        IReadOnlyList<string> IncludedColumns,
        string? FilterDefinition,
        bool Unique,
        bool Enabled);

    private sealed record Issue250ObservedTrigger(
        string Name,
        string Table,
        bool Disabled,
        string? Definition);

    private sealed record Issue250ObservedForeignKey(
        string Name,
        string ParentTable,
        string ReferencedTable,
        bool Disabled,
        bool Untrusted,
        string DeleteAction,
        string UpdateAction);

    private sealed record Issue250ObservedForeignKeyColumn(
        string ForeignKey,
        int Ordinal,
        string ParentColumn,
        string ReferencedColumn);

    private sealed record Issue250ObservedCheckConstraint(
        string Name,
        string Table,
        bool Disabled,
        bool Untrusted,
        string Definition);

    private sealed record Issue250ObservedUniqueIndex(
        string Name,
        string Table,
        bool Unique,
        bool Disabled,
        bool PrimaryKey,
        bool UniqueConstraint,
        bool Filtered,
        string? FilterDefinition);

    private sealed record Issue250ObservedIndexColumn(
        string Index,
        int KeyOrdinal,
        int IndexColumnId,
        bool Descending,
        bool Included,
        string Column);

    private sealed record Issue250W3MTriggerEvidence(
        string Name,
        string Table,
        bool Enabled,
        bool Required,
        string? DefinitionSha256,
        bool? SemanticGuardsSatisfied);

    private sealed record Issue250W3MForeignKeyEvidence(
        string Name,
        string ParentTable,
        IReadOnlyList<string> ParentColumns,
        string ReferencedTable,
        IReadOnlyList<string> ReferencedColumns,
        string DeleteAction,
        string UpdateAction,
        bool Enabled,
        bool Trusted,
        bool Required);

    private sealed record Issue250W3MCheckConstraintEvidence(
        string Name,
        string Table,
        bool Enabled,
        bool Trusted,
        bool Required,
        string DefinitionSha256,
        bool? SemanticGuardsSatisfied);

    private sealed record Issue250W3MUniqueIndexEvidence(
        string Name,
        string Table,
        bool Unique,
        bool Enabled,
        string Kind,
        IReadOnlyList<Issue250IndexKeyColumn> KeyColumns,
        IReadOnlyList<string> IncludedColumns,
        string? FilterDefinition,
        bool Required);

    private sealed record Issue250W3MSchemaRequiredEvidence(
        IReadOnlyList<Issue250RequiredTrigger> Triggers,
        IReadOnlyList<Issue250RequiredSchemaRelation> ForeignKeys,
        IReadOnlyList<Issue250RequiredCheckConstraint> CheckConstraints,
        IReadOnlyList<Issue250RequiredUniqueIndex> UniqueIndexes);

    private sealed record Issue250W3MSchemaObservedEvidence(
        IReadOnlyList<Issue250W3MTriggerEvidence> Triggers,
        IReadOnlyList<Issue250W3MForeignKeyEvidence> ForeignKeys,
        IReadOnlyList<Issue250W3MCheckConstraintEvidence> CheckConstraints,
        IReadOnlyList<Issue250W3MUniqueIndexEvidence> UniqueIndexes);

    private sealed record Issue250W3MSchemaIntegrityFailures(
        IReadOnlyList<string> MissingTriggers,
        IReadOnlyList<string> DisabledTriggers,
        IReadOnlyList<string> TriggerTableMismatches,
        IReadOnlyList<string> UnavailableTriggerDefinitions,
        IReadOnlyList<string> TriggerSemanticGuardFailures,
        IReadOnlyList<string> MissingForeignKeys,
        IReadOnlyList<string> DisabledForeignKeys,
        IReadOnlyList<string> UntrustedForeignKeys,
        IReadOnlyList<string> ForeignKeyRelationMismatches,
        IReadOnlyList<string> ForeignKeyColumnMappingMismatches,
        IReadOnlyList<string> ForeignKeyActionMismatches,
        IReadOnlyList<string> MissingCheckConstraints,
        IReadOnlyList<string> DisabledCheckConstraints,
        IReadOnlyList<string> UntrustedCheckConstraints,
        IReadOnlyList<string> CheckConstraintTableMismatches,
        IReadOnlyList<string> CheckConstraintSemanticGuardFailures,
        IReadOnlyList<string> MissingUniqueIndexes,
        IReadOnlyList<string> DisabledUniqueIndexes,
        IReadOnlyList<string> NonUniqueRequiredIndexes,
        IReadOnlyList<string> UniqueIndexStateMismatches,
        IReadOnlyList<string> UniqueIndexKeyColumnMismatches,
        IReadOnlyList<string> UniqueIndexIncludedColumnMismatches,
        IReadOnlyList<string> UniqueIndexFilterMismatches)
    {
        internal IEnumerable<string> All()
        {
            foreach (var (category, values) in new (string, IReadOnlyList<string>)[]
            {
                (nameof(MissingTriggers), MissingTriggers),
                (nameof(DisabledTriggers), DisabledTriggers),
                (nameof(TriggerTableMismatches), TriggerTableMismatches),
                (nameof(UnavailableTriggerDefinitions), UnavailableTriggerDefinitions),
                (nameof(TriggerSemanticGuardFailures), TriggerSemanticGuardFailures),
                (nameof(MissingForeignKeys), MissingForeignKeys),
                (nameof(DisabledForeignKeys), DisabledForeignKeys),
                (nameof(UntrustedForeignKeys), UntrustedForeignKeys),
                (nameof(ForeignKeyRelationMismatches), ForeignKeyRelationMismatches),
                (nameof(ForeignKeyColumnMappingMismatches), ForeignKeyColumnMappingMismatches),
                (nameof(ForeignKeyActionMismatches), ForeignKeyActionMismatches),
                (nameof(MissingCheckConstraints), MissingCheckConstraints),
                (nameof(DisabledCheckConstraints), DisabledCheckConstraints),
                (nameof(UntrustedCheckConstraints), UntrustedCheckConstraints),
                (nameof(CheckConstraintTableMismatches), CheckConstraintTableMismatches),
                (nameof(CheckConstraintSemanticGuardFailures), CheckConstraintSemanticGuardFailures),
                (nameof(MissingUniqueIndexes), MissingUniqueIndexes),
                (nameof(DisabledUniqueIndexes), DisabledUniqueIndexes),
                (nameof(NonUniqueRequiredIndexes), NonUniqueRequiredIndexes),
                (nameof(UniqueIndexStateMismatches), UniqueIndexStateMismatches),
                (nameof(UniqueIndexKeyColumnMismatches), UniqueIndexKeyColumnMismatches),
                (nameof(UniqueIndexIncludedColumnMismatches), UniqueIndexIncludedColumnMismatches),
                (nameof(UniqueIndexFilterMismatches), UniqueIndexFilterMismatches)
            })
            {
                foreach (var value in values)
                {
                    yield return $"{category}:{value}";
                }
            }
        }
    }

    private sealed record Issue250W3MSchemaIntegrityEvidence(
        Issue250W3MSchemaRequiredEvidence Required,
        Issue250W3MSchemaObservedEvidence Observed,
        Issue250W3MSchemaIntegrityFailures Failures,
        string DefinitionAuthenticationPolicy,
        string Result);

    private sealed record Issue250W3MSetupEvidence(
        int ParentBatchSize,
        int BatchCount,
        int TempParentRows,
        long TempTargetRows,
        double TempTablePopulationDurationMilliseconds,
        double FoundationDurationMilliseconds,
        Issue250W3MSetupRowCounts ExpectedRows,
        Issue250W3MSetupRowCounts ActualRows,
        int SentinelFrameAndArtifactRows,
        double SentinelFrameAndArtifactDurationMilliseconds,
        double SqlSetupDurationMilliseconds,
        double TotalDurableSetupDurationMilliseconds,
        double TotalBatchDurationMilliseconds,
        double MaximumBatchDurationMilliseconds,
        double MaximumBatchPhaseDurationMilliseconds,
        double MaximumNonBatchCommandDurationMilliseconds,
        double StatisticsDurationMilliseconds,
        Issue250W3MQueueIndexNormalizationEvidence QueueIndexNormalization,
        double TempTableDropDurationMilliseconds,
        int CommandTimeoutSeconds,
        int PhaseTimeoutSeconds,
        int SharedHistoryFrameCount,
        int TotalW3MFrameCount,
        bool SetupExcludedFromMeasuredClaims,
        Issue250W3MSchemaIntegrityEvidence SchemaIntegrity,
        IReadOnlyList<Issue250W3MSetupBatchEvidence> Batches);

    private sealed record Issue250W3MQueueIndexNormalizationEvidence(
        double DurationMilliseconds,
        long LogicalRecordCountBefore,
        long LogicalRecordCountAfter,
        Issue250W3MQueueIndexPhysicalState Before,
        Issue250W3MQueueIndexPhysicalState After,
        string Method,
        string MeasurementScope);

    private sealed record Issue250W3MQueueIndexPhysicalState(
        long PageCount,
        long RecordCount,
        long GhostRecordCount,
        long VersionGhostRecordCount,
        int IndexDepth);

    private sealed record Issue250W3MTopologyEvidence(
        long TotalFrames,
        long HistoryFrames,
        long SentinelFrames,
        long HistoryArtifacts,
        long DistinctHistoryArtifactRecordIds,
        long DistinctHistoryArtifactIds,
        long DistinctHistoryIdempotencyKeys,
        long DistinctHistoryStorageReferences,
        long HistoryArtifactsWithMatchingDevicePublicId,
        long DistinctTerminalItemTargetRecordIds);

    private sealed record Issue250W3MEvidence(
        int CompletedParents,
        long TerminalItems,
        int PendingParents,
        long PendingItems,
        int DistinctEvents,
        long ValidTerminalTargetRows,
        Issue250W3MSetupEvidence Setup,
        Issue250W3MTopologyEvidence Topology,
        string SentinelGenerator,
        long SentinelExpectedByteLength,
        string SentinelExpectedSha256,
        long SentinelObservedByteLength,
        string SentinelObservedSha256,
        bool SentinelPreReleaseVerificationSucceeded,
        long SentinelSetupPutPayloadBytes,
        int SentinelSetupPutRequests,
        int SentinelSetupPreReleaseStatRequests,
        long SentinelSetupPreReleaseGetPayloadBytes,
        int SentinelSetupPreReleaseGetRequests,
        bool SentinelPostReleaseAbsent,
        int SentinelPostReleaseAbsenceStatRequests,
        bool QueryCapturedFromProductionProcessNextAsync,
        string SelectedOldestReleaseIdentitySha256,
        string NormalizedParameterizedProductionQuery,
        string NormalizedProductionQuerySha256,
        string CanonicalActualPlanXml,
        string CanonicalActualPlanSha256,
        string CanonicalPlanTransformation,
        long LogicalReads,
        int SelectedRows,
        bool IndexUsed,
        string CanonicalPlanFactsJson,
        string NormalizedPlanFactsSha256,
        IReadOnlyList<string> Operators,
        IReadOnlyList<string> Indexes,
        string ExpectedIndex,
        bool BoundedSelection,
        bool MetadataOnly,
        bool TriggerAndConstraintPreservingSetup,
        bool ActualProcessorSelectedOldest,
        bool ActualProcessorCompletedOldest,
        int FinalPendingParents,
        long FinalPendingItems,
        string CandidateItemDueWorkPlan,
        bool IsolatedDatabaseDeletedAfterCapture);

    private sealed record Issue250W0Evidence(
        bool ObjectAlreadyAbsentCompletedIdempotently,
        Issue250W0GeneratorEvidence Generator,
        bool BeforeDeleteFailureLeftRetryablePendingParentAndItem,
        bool FalseParentCompletionObserved,
        int ReleasedEarlierOrdinalsBeforeRestart,
        int PendingLaterOrdinalsBeforeRestart,
        bool FreshProcessorProcessNextRecoveredAndCompleted,
        string BaselineRestartBoundary,
        string FutureOnlyBoundaries,
        Issue250W0RuntimeEvidence Runtime,
        Issue250HealthTransitionEvidence Health,
        string Privacy);

    private sealed record Issue250Preflight(
        bool Linux,
        string Architecture,
        string PinnedSdk,
        string ExecutingSdk,
        string CpuModel,
        long TotalAvailableMemoryBytes,
        long HostTotalMemoryBytes,
        long ContainerMemoryLimitBytes,
        long AvailableWorkspaceDiskBytes,
        string WorkspaceFileSystem,
        long AvailableMinioContainerStorageBytes,
        long AvailableSqlContainerStorageBytes,
        long MinimumFullMemoryBytes,
        long MinimumFullDiskBytes,
        string SqlServerImage,
        string MinioImage,
        int OverallTimeoutSeconds,
        string CapacityResult);

    private sealed record Issue250SchemaColumn(string Name, string SqlType, short MaxLength, bool Nullable);

    private sealed record Issue250SchemaCapabilities(
        string Phase,
        IReadOnlyList<Issue250SchemaColumn> ActualColumns,
        string CurrentContract,
        string BaselineFutureBoundaryDisposition,
        bool CandidateHarnessImplemented,
        IReadOnlyList<Issue250SchemaColumn> RequiredAfterColumns,
        IReadOnlyList<string> RequiredAfterFaultHooks);

    private sealed record Issue250ObjectLockContract(
        string Algorithm,
        string DmvResourceVisibility,
        string ProductionSourceSha256,
        string ProbeInputSha256,
        string ProbeExpectedResource,
        bool ProductionIntegrationAssertionPassed);

    private sealed record Issue250BaselineBinding(
        string Status,
        string BaselineManifestSha256,
        string BaselineEvidenceSha256,
        string BaselineSourceHead,
        string BaselineHarnessSha256,
        string BaselineProtocolSha256,
        string BaselineSemanticWorkloadSha256,
        string CurrentSemanticWorkloadSha256,
        IReadOnlyList<Issue250EvidenceFile> IndependentlyVerifiedPayloads);

    private sealed record Issue250EvidenceFile(string Name, long ByteLength, string Sha256);

    private sealed record Issue250PublicSource(
        string? RequestedRevision,
        string Head,
        string Branch,
        bool Dirty,
        string DirtyDiffSha256,
        string OutputDirectoryName,
        int? Trial,
        string Claimability,
        IReadOnlyList<EvidenceAssemblySnapshot> Assemblies);

    private sealed record Issue250RuntimeEvidence(
        IReadOnlyList<Issue250RuntimeMetric> Metrics,
        IReadOnlyList<Issue250RuntimeActivity> Activities,
        IReadOnlyList<Issue250RuntimeLog> Logs,
        string BoundedMetricLabels,
        string ExpectedSpan,
        int ExpectedLogEventId,
        string ExceptionBehavior);

    private sealed record Issue250W0RuntimeEvidence(
        Issue250RuntimeEvidence AbsentObjectSuccess,
        Issue250RuntimeEvidence BeforeDeleteFailure,
        Issue250RuntimeEvidence BeforeDeleteRecovery,
        Issue250RuntimeEvidence PriorOrdinalFailure,
        Issue250RuntimeEvidence RestartRecovery);

    private sealed record Issue250RuntimeMetric(
        string Instrument,
        double Value,
        string? Operation,
        string? Outcome,
        IReadOnlyList<string> TagKeys);

    private sealed record Issue250RuntimeActivity(
        string Name,
        string Kind,
        string Status,
        double DurationMilliseconds,
        IReadOnlyList<string> Tags);

    private sealed record Issue250RuntimeLog(
        int EventId,
        string Level,
        string? Outcome,
        int? ItemCount,
        string? Kind,
        int? RetryCount,
        bool ExceptionPresent);

    private sealed record Issue250HealthEvidence(
        string Status,
        int PendingParentCount,
        double OldestPendingParentAgeSeconds,
        int PendingItemCount,
        int RetryDueItemCount,
        int ReservedItemCount,
        int StaleReservedItemCount,
        long ReservedLogicalBytes,
        double OldestReservedItemAgeSeconds,
        long PendingLogicalBytes,
        double OldestPendingItemAgeSeconds,
        string ItemBacklogSignals);

    private sealed record Issue250HealthTransitionEvidence(
        Issue250HealthEvidence FreshPending,
        Issue250HealthEvidence StalePending,
        Issue250HealthEvidence Drained);
}

internal static partial class Issue250Regex
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    internal static partial Regex Whitespace();

    [GeneratedRegex(@"logical reads (\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex LogicalReads();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant)]
    internal static partial Regex Guid();

    [GeneratedRegex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", RegexOptions.CultureInvariant)]
    internal static partial Regex GuidN();

    [GeneratedRegex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])", RegexOptions.CultureInvariant)]
    internal static partial Regex HexSha();

    [GeneratedRegex("""issue-250[^"\\\s,}\]]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex PrivateIssueToken();

    [GeneratedRegex("""(?:minio://skymonitor-artifacts/issue-250/|"issue-250/)[^"\\\s]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex PrivateObjectReference();

    [GeneratedRegex(@"(?:password|secret|access[_-]?key|connection(?:string)?)\s*[:=]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex CredentialAssignment();
}
