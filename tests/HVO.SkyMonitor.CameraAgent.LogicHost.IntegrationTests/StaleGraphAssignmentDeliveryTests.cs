using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

/// <summary>
/// The edge view of a central graph assignment this binary cannot expand, as a recipe ImplementationVersion change
/// leaves an operator assignment: ingest commits the upload and then fails scheduling with HTTP 500. The test runs its
/// own CameraAgent host and device, so its drain uploads only that host's outbox and the shared device's backlog and
/// central frames are neither consumed nor added to.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class StaleGraphAssignmentDeliveryTests
{
    private static CameraAgentIntegrationFixture Fixture => AssemblyHooks.Fixture;

    [TestMethod]
    public async Task StaleAssignmentKeepsTheUploadRetryingAndHeldWithoutBlockingTheOutboxAndRecoversAfterReassignment()
    {
        var sharedHeadBefore = ReadOutboxState(Fixture.StorageRoot);
        await using var agent = await Fixture.StartIsolatedCameraAgentAsync().ConfigureAwait(false);
        var root = agent.StorageRoot;
        using var scope = agent.Services.CreateScope();
        var services = scope.ServiceProvider;
        var seeded = await SeedStaleCameraAssignmentAsync(agent.DeviceId).ConfigureAwait(false);
        var configured = services.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value;
        var drainOptions = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = configured.RawIngressRoot,
            CaptureDistribution = configured.CaptureDistribution,
            UploadBatchSize = 1,
            UploadPollIntervalSeconds = 1,
            UploadRetryInitialDelaySeconds = 1,
            UploadRetryMaximumDelaySeconds = 1
        });
        var drain = ActivatorUtilities.CreateInstance<ArtifactOutboxDrainService>(services, drainOptions);
        await drain.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // Condition 1: a raw of the stale camera is committed centrally, fails scheduling with HTTP 500, and the
            // edge keeps it as ordinary retry work: never terminal, never quarantined, and still a retention hold.
            var stale = await WaitForAsync(
                () => FindStaleRetryAsync(agent, seeded.InstallationId),
                TimeSpan.FromSeconds(60),
                "a stale-camera raw retrying on http-500",
                root).ConfigureAwait(false);
            Assert.AreEqual("retry", stale.Status);
            Assert.AreEqual("http-500", stale.LastReason);
            Assert.IsNull(stale.TerminalReason);
            Assert.AreEqual(0, CountQuarantinedOnHttp500(root),
                "A stale graph assignment must never quarantine edge delivery.");
            var holds = await services.GetRequiredService<IArtifactOutbox>()
                .GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(holds.Any(hold => hold.ArtifactId == stale.ArtifactId),
                "The retrying upload must keep its local payload from retention.");
            using (var hostScope = Fixture.CreateHostScope())
            {
                var db = hostScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var central = await db.CentralArtifacts.AsNoTracking()
                    .SingleAsync(item => item.Id == stale.CentralArtifactId).ConfigureAwait(false);
                Assert.AreEqual(CentralArtifactObjectState.Available, central.ObjectState,
                    "The upload committed before scheduling failed.");
                Assert.AreEqual(0, await db.CentralProcessingGraphExecutions.AsNoTracking()
                    .CountAsync(item => item.AnchorSourceCentralArtifactId == stale.CentralArtifactId)
                    .ConfigureAwait(false));
            }

            // No head-of-line block: while the stale record keeps retrying, the drain still settles other records.
            await WaitForAsync(
                () => Task.FromResult<int?>(
                    CountOtherSettledSince(root, stale.IdempotencyKey, stale.UpdatedUnixMs) is var settled and > 0
                        ? settled
                        : null),
                TimeSpan.FromSeconds(60),
                "another record settled after the stale failure",
                root).ConfigureAwait(false);
            Assert.AreNotEqual("acknowledged", ReadStatus(root, stale.IdempotencyKey),
                "The stale record cannot be acknowledged before the assignment is corrected.");

            // Condition 2: the operator correction. The edge's next attempt probes central status, which reconciles
            // the committed duplicate and schedules it on the newly resolved revision. That revision is the basic
            // graph on current recipe versions without its scene-bound Annotation and MeasuredStellarAssociations nodes: on this base an edge-staged projected
            // scene records its calibration version as the projection algorithm version, so central scene validation
            // rejects it for any installed camera, independently of the assignment being corrected here.
            var recoveredRevisionId = await ReassignToCurrentRecipeRevisionAsync(seeded).ConfigureAwait(false);
            var reassignedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await WaitForAsync(
                () => Task.FromResult<string?>(ReadStatus(root, stale.IdempotencyKey) is "acknowledged" ? "acknowledged" : null),
                TimeSpan.FromSeconds(60),
                "the stale record acknowledged after reassignment",
                root,
                () => DescribeCentralSchedulingAsync(stale.CentralArtifactId)).ConfigureAwait(false);
            using (var hostScope = Fixture.CreateHostScope())
            {
                var db = hostScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var execution = await db.CentralProcessingGraphExecutions.AsNoTracking()
                    .SingleAsync(item => item.AnchorSourceCentralArtifactId == stale.CentralArtifactId)
                    .ConfigureAwait(false);
                Assert.AreEqual(recoveredRevisionId, execution.RevisionId);
                Assert.IsGreaterThan(0, await db.CentralDerivativeJobs.AsNoTracking()
                    .CountAsync(item => item.GraphExecutionId == execution.Id).ConfigureAwait(false));
            }
            Assert.AreEqual(0, CountQuarantinedOnHttp500(root));

            // Later frames of the camera ingest cleanly on the recovered revision. Each one notifies the window
            // resolver about every window it neighbours, including windows whose earlier positions have settled, so
            // ingest must never fail scheduling on a settled graph window.
            var later = await WaitForAsync(
                async () => await ReadInstallationRecordsCreatedSinceAsync(agent, seeded.InstallationId, reassignedUnixMs)
                    .ConfigureAwait(false) is { } records &&
                    records.Count(static record => record.Status == "acknowledged") >= 2
                        ? records
                        : null,
                TimeSpan.FromSeconds(60),
                "two later frames of the camera acknowledged after reassignment",
                root,
                () => DescribeCentralSchedulingAsync(stale.CentralArtifactId)).ConfigureAwait(false);
            Assert.IsFalse(later.Any(static record => record.LastReason == "http-500"),
                "No frame created after reassignment may fail central scheduling.");

            // The recovered execution's windows reach a terminal state. Each still-Waiting window is resolved at its
            // own deadline, the periodic pass's timeout path, so the test does not wait out the recipe timeout; the
            // central worker then runs or skips it.
            using (var hostScope = Fixture.CreateHostScope())
            {
                var hostServices = hostScope.ServiceProvider;
                var db = hostServices.GetRequiredService<ApplicationDbContext>();
                var windows = await db.CentralDerivativeJobs.AsNoTracking()
                    .Where(item => item.GraphExecution!.AnchorSourceCentralArtifactId == stale.CentralArtifactId &&
                        item.WaitKind == CentralDerivativeWaitKind.Window)
                    .Select(item => new { item.Id, item.Status, item.ResolutionDeadlineUtc })
                    .ToListAsync().ConfigureAwait(false);
                Assert.IsNotEmpty(windows, "The recovered revision includes the RollingMean window node.");
                var resolver = hostServices.GetRequiredService<ICentralDerivativeWindowResolver>();
                foreach (var window in windows.Where(static item => item.Status == CentralDerivativeJobStatus.Waiting))
                {
                    await resolver.ResolveAsync(window.Id, window.ResolutionDeadlineUtc!.Value.AddTicks(1),
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            _ = await WaitForAsync(
                () => ReadTerminalWindowStatusesAsync(stale.CentralArtifactId),
                TimeSpan.FromSeconds(120),
                "the recovered execution's windows terminal",
                root,
                () => DescribeExecutionJobsAsync(stale.CentralArtifactId)).ConfigureAwait(false);

            // Every other raw of the camera recovers the same way, so no record is left retrying on the stale failure.
            await WaitForAsync(
                () => Task.FromResult<string?>(
                    CountRetryingOnHttp500(root) == 0 ? "drained" : null),
                TimeSpan.FromSeconds(60),
                "no record left retrying on http-500",
                root).ConfigureAwait(false);
        }
        finally
        {
            await drain.StopAsync(CancellationToken.None).ConfigureAwait(false);
            drain.Dispose();
            await RetireInstallationAsync(seeded).ConfigureAwait(false);
        }

        // The shared outbox is untouched: every record it held before the test is still in the same state. The shared
        // device keeps capturing, so records it commits meanwhile are excluded by key, not by creation time: a record's
        // creation time is its capture's, which can precede a commit that lands after the first read.
        var heldBefore = sharedHeadBefore.Select(static record => record.IdempotencyKey).ToHashSet(StringComparer.Ordinal);
        CollectionAssert.AreEqual(sharedHeadBefore, ReadOutboxState(Fixture.StorageRoot)
            .Where(record => heldBefore.Contains(record.IdempotencyKey)).ToList());
    }

    private sealed record SeededStaleCamera(
        Guid InstallationId, Guid ObservatoryId, Guid LogicalCameraId, Guid RevisionId, string OwnerUserId);

    private sealed record StaleRetry(
        string IdempotencyKey,
        Guid ArtifactId,
        Guid CentralArtifactId,
        string Status,
        string? LastReason,
        string? TerminalReason,
        long UpdatedUnixMs);

    /// <summary>
    /// Installs a camera on the test's device and assigns it a revision embedding an earlier Annotation
    /// ImplementationVersion, so only the node registry rejects it. The installation predates every capture of the
    /// device, so their frames bind to it on first ingest.
    /// </summary>
    private static async Task<SeededStaleCamera> SeedStaleCameraAssignmentAsync(string deviceId)
    {
        using var hostScope = Fixture.CreateHostScope();
        var db = hostScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registry = hostScope.ServiceProvider.GetRequiredService<ICentralProcessingGraphNodeRegistry>();
        var registration = await db.DeviceRegistrations
            .SingleAsync(item => item.DeviceId == deviceId).ConfigureAwait(false);
        var seededAtUtc = DateTimeOffset.UnixEpoch.AddDays(-1);
        var camera = new LogicalCamera
        {
            ObservatoryId = registration.ObservatoryId,
            Slug = $"stale-delivery-{Guid.NewGuid():N}",
            Name = "Stale Graph Delivery Camera",
            Description = "Camera assigned a revision this binary cannot expand",
            CreatedAtUtc = seededAtUtc.AddDays(-1),
            CreatedByUserId = registration.OwnerUserId
        };
        var installation = new LogicalCameraInstallation
        {
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            RegistrationId = registration.Id,
            InstallationPublicId = Guid.NewGuid(),
            AssignedAtUtc = seededAtUtc,
            AssignedByUserId = registration.OwnerUserId,
            AssignmentReasonCode = "stale-graph-delivery-test"
        };
        camera.Installations.Add(installation);
        var (revision, centralPlan) = CreatePublishedRevision(
            WithStaleAnnotationImplementationVersion(Encoding.UTF8.GetString(
                ProcessingGraphJson.SerializeCanonical(DatabaseSeeder.CreateBasicCentralProcessingGraph() with
                {
                    Name = camera.Slug
                }))),
            registry,
            registration.OwnerUserId,
            seededAtUtc);
        Assert.AreEqual(BuiltInProcessingRecipes.Annotation, registry.FindUnsupported(centralPlan));
        var assignment = new CentralProcessingGraphAssignment
        {
            Revision = revision,
            RevisionId = revision.Id,
            TargetHost = CentralProcessingGraphTargetHost.Central,
            Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
            ObservatoryId = registration.ObservatoryId,
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            EffectiveFromUtc = seededAtUtc,
            CreatedAtUtc = seededAtUtc,
            ActorUserId = registration.OwnerUserId,
            ReasonCode = "stale-graph-delivery-test"
        };
        revision.Assignments.Add(assignment);
        db.AddRange(camera, installation, revision, assignment);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new(installation.Id, registration.ObservatoryId, camera.Id, revision.Id, registration.OwnerUserId);
    }

    /// <summary>Validates and compiles a graph definition into a published revision row, as publication does.</summary>
    private static (CentralProcessingGraphRevision Revision, ProcessingGraphExecutionPlan CentralPlan) CreatePublishedRevision(
        string definitionJson,
        ICentralProcessingGraphNodeRegistry registry,
        string ownerUserId,
        DateTimeOffset publishedAtUtc)
    {
        var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(definitionJson));
        Assert.IsTrue(parsed.IsValid);
        var portable = ProcessingGraphCompiler.Compile(parsed.Definition!);
        var central = LogicHostProcessingGraphAdapter.Compile(parsed.Definition!, registry.Capabilities);
        Assert.IsTrue(central.IsValid, string.Join(Environment.NewLine, central.Diagnostics));
        return (new CentralProcessingGraphRevision
        {
            Name = parsed.Definition!.Name,
            Revision = parsed.Definition.Revision,
            DefinitionJson = definitionJson,
            DefinitionIdentitySha256 = portable.Plan!.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = portable.Plan.PlanIdentitySha256,
            CentralPlanIdentitySha256 = central.Plan!.PlanIdentitySha256,
            CreatedAtUtc = publishedAtUtc,
            CreatedByUserId = ownerUserId,
            PublishedAtUtc = publishedAtUtc,
            PublishedByUserId = ownerUserId
        }, central.Plan);
    }

    /// <summary>The operator correction: a later camera-scoped assignment of a revision this binary can expand.</summary>
    private static async Task<Guid> ReassignToCurrentRecipeRevisionAsync(SeededStaleCamera seeded)
    {
        using var hostScope = Fixture.CreateHostScope();
        var db = hostScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registry = hostScope.ServiceProvider.GetRequiredService<ICentralProcessingGraphNodeRegistry>();
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var basic = DatabaseSeeder.CreateBasicCentralProcessingGraph();
        var (revision, centralPlan) = CreatePublishedRevision(
            Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(basic with
            {
                Name = $"stale-delivery-recovered-{seeded.LogicalCameraId:N}",
                Nodes = [.. basic.Nodes.Where(static node => node.StepAlias is not (BuiltInProcessingRecipes.Annotation or
                    BuiltInProcessingRecipes.MeasuredStellarAssociations))]
            })),
            registry,
            seeded.OwnerUserId,
            now);
        Assert.IsTrue(registry.Validate(centralPlan), "The recovery revision must be expandable by this binary.");
        db.CentralProcessingGraphRevisions.Add(revision);
        db.CentralProcessingGraphAssignments.Add(new CentralProcessingGraphAssignment
        {
            RevisionId = revision.Id,
            TargetHost = CentralProcessingGraphTargetHost.Central,
            Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
            ObservatoryId = seeded.ObservatoryId,
            LogicalCameraId = seeded.LogicalCameraId,
            EffectiveFromUtc = now,
            CreatedAtUtc = now,
            ActorUserId = seeded.OwnerUserId,
            ReasonCode = "stale-graph-delivery-reassigned"
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        return revision.Id;
    }

    /// <summary>Retires the test camera's installation: a retired installation is never scheduled.</summary>
    private static async Task RetireInstallationAsync(SeededStaleCamera seeded)
    {
        using var hostScope = Fixture.CreateHostScope();
        var db = hostScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var installation = await db.LogicalCameraInstallations
            .SingleAsync(item => item.Id == seeded.InstallationId).ConfigureAwait(false);
        installation.RetiredAtUtc = DateTimeOffset.UtcNow;
        installation.RetiredByUserId = seeded.OwnerUserId;
        installation.RetirementReasonCode = "stale-graph-delivery-test";
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>The first outbox record retrying on HTTP 500 whose central artifact is bound to the stale camera.</summary>
    private static async Task<StaleRetry?> FindStaleRetryAsync(
        CameraAgentIntegrationFixture.IsolatedCameraAgent agent, Guid installationId)
    {
        var candidates = new List<(string Key, Guid ArtifactId, string Status, string? LastReason, string? Terminal, long Updated)>();
        using (var connection = OpenOutboxReadConnection(agent.StorageRoot))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT idempotency_key, artifact_id, status, last_reason, terminal_reason, updated_unix_ms
                FROM artifact_outbox_records
                WHERE manifest_kind = 'v2' AND status = 'retry' AND last_reason = 'http-500'
                  AND artifact_id IS NOT NULL
                ORDER BY updated_unix_ms;
                """;
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                candidates.Add((
                    reader.GetString(0),
                    Guid.ParseExact(reader.GetString(1), "N"),
                    reader.GetString(2),
                    await reader.IsDBNullAsync(3).ConfigureAwait(false) ? null : reader.GetString(3),
                    await reader.IsDBNullAsync(4).ConfigureAwait(false) ? null : reader.GetString(4),
                    reader.GetInt64(5)));
            }
        }
        if (candidates.Count == 0)
        {
            return null;
        }
        using var hostScope = Fixture.CreateHostScope();
        var db = hostScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifactIds = candidates.Select(static item => item.ArtifactId).ToArray();
        var bound = await db.CentralArtifacts.AsNoTracking()
            .Where(item => item.DevicePublicId == agent.DevicePublicId && artifactIds.Contains(item.ArtifactId) &&
                item.Frame!.LogicalCameraInstallationId == installationId)
            .Select(item => new { item.Id, item.ArtifactId })
            .ToListAsync().ConfigureAwait(false);
        return candidates
            .Join(bound, static item => item.ArtifactId, static item => item.ArtifactId, static (record, central) =>
                new StaleRetry(record.Key, record.ArtifactId, central.Id, record.Status, record.LastReason,
                    record.Terminal, record.Updated))
            .FirstOrDefault();
    }

    private sealed record OutboxRecord(string IdempotencyKey, Guid ArtifactId, string Status, string? LastReason);

    /// <summary>Outbox records created since a moment whose central artifact is bound to the installation.</summary>
    private static async Task<IReadOnlyList<OutboxRecord>?> ReadInstallationRecordsCreatedSinceAsync(
        CameraAgentIntegrationFixture.IsolatedCameraAgent agent,
        Guid installationId,
        long sinceUnixMs)
    {
        var records = new List<OutboxRecord>();
        using (var connection = OpenOutboxReadConnection(agent.StorageRoot))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT idempotency_key, artifact_id, status, last_reason
                FROM artifact_outbox_records
                WHERE manifest_kind = 'v2' AND artifact_id IS NOT NULL AND created_unix_ms >= $since
                ORDER BY created_unix_ms;
                """;
            command.Parameters.AddWithValue("$since", sinceUnixMs);
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                records.Add(new OutboxRecord(
                    reader.GetString(0),
                    Guid.ParseExact(reader.GetString(1), "N"),
                    reader.GetString(2),
                    await reader.IsDBNullAsync(3).ConfigureAwait(false) ? null : reader.GetString(3)));
            }
        }
        if (records.Count == 0)
        {
            return null;
        }
        using var hostScope = Fixture.CreateHostScope();
        var db = hostScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifactIds = records.Select(static item => item.ArtifactId).ToArray();
        var bound = await db.CentralArtifacts.AsNoTracking()
            .Where(item => item.DevicePublicId == agent.DevicePublicId && artifactIds.Contains(item.ArtifactId) &&
                item.Frame!.LogicalCameraInstallationId == installationId)
            .Select(item => item.ArtifactId)
            .ToListAsync().ConfigureAwait(false);
        var installationRecords = records.Where(record => bound.Contains(record.ArtifactId)).ToList();
        return installationRecords.Count == 0 ? null : installationRecords;
    }

    private static async Task<string?> ReadTerminalWindowStatusesAsync(Guid centralArtifactId)
    {
        using var hostScope = Fixture.CreateHostScope();
        var db = hostScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var statuses = await db.CentralDerivativeJobs.AsNoTracking()
            .Where(item => item.GraphExecution!.AnchorSourceCentralArtifactId == centralArtifactId &&
                item.WaitKind == CentralDerivativeWaitKind.Window)
            .Select(item => item.Status)
            .ToListAsync().ConfigureAwait(false);
        return statuses.Count > 0 && statuses.All(static status => status is CentralDerivativeJobStatus.Completed
                or CentralDerivativeJobStatus.Skipped
                or CentralDerivativeJobStatus.TerminalFailure)
            ? string.Join(",", statuses)
            : null;
    }

    private static async Task<string> DescribeExecutionJobsAsync(Guid centralArtifactId)
    {
        using var hostScope = Fixture.CreateHostScope();
        var db = hostScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var jobs = await db.CentralDerivativeJobs.AsNoTracking()
            .Where(item => item.GraphExecution!.AnchorSourceCentralArtifactId == centralArtifactId)
            .OrderBy(item => item.GraphNodeOrdinal)
            .Select(item => new { item.RecipeName, item.Status, item.WaitKind, item.StateReasonCode, item.LastError })
            .ToListAsync().ConfigureAwait(false);
        return string.Join("; ", jobs.Select(static item =>
            $"{item.RecipeName}={item.Status}/{item.WaitKind}/{item.StateReasonCode}/{item.LastError}"));
    }

    private static string? ReadStatus(string root, string idempotencyKey)
    {
        using var connection = OpenOutboxReadConnection(root);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM artifact_outbox_records WHERE idempotency_key = $key;";
        command.Parameters.AddWithValue("$key", idempotencyKey);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int CountQuarantinedOnHttp500(string root)
    {
        using var connection = OpenOutboxReadConnection(root);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM artifact_outbox_records
            WHERE status = 'quarantined' AND last_reason = 'http-500';
            """;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int CountRetryingOnHttp500(string root)
    {
        using var connection = OpenOutboxReadConnection(root);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM artifact_outbox_records
            WHERE status = 'retry' AND last_reason = 'http-500';
            """;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Other records the drain settled (acknowledged, or retried on the same stale camera) since a moment.</summary>
    private static int CountOtherSettledSince(string root, string idempotencyKey, long sinceUnixMs)
    {
        using var connection = OpenOutboxReadConnection(root);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM artifact_outbox_records
            WHERE idempotency_key <> $key AND updated_unix_ms > $since
              AND (status = 'acknowledged' OR (status = 'retry' AND last_reason = 'http-500'));
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$since", sinceUnixMs);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private sealed record OutboxRecordState(
        string IdempotencyKey, string Status, string? LastReason, long AttemptCount, long CreatedUnixMs);

    /// <summary>The settlement state of every record an outbox holds, in key order.</summary>
    private static List<OutboxRecordState> ReadOutboxState(string root)
    {
        using var connection = OpenOutboxReadConnection(root);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT idempotency_key, status, last_reason, attempt_count, created_unix_ms
            FROM artifact_outbox_records
            ORDER BY idempotency_key;
            """;
        using var reader = command.ExecuteReader();
        var records = new List<OutboxRecordState>();
        while (reader.Read())
        {
            records.Add(new OutboxRecordState(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }
        return records;
    }

    private static string DescribeOutbox(string root)
    {
        using var connection = OpenOutboxReadConnection(root);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, COALESCE(last_reason, '-'), COUNT(*), MAX(attempt_count)
            FROM artifact_outbox_records
            GROUP BY status, last_reason
            ORDER BY status, last_reason;
            """;
        using var reader = command.ExecuteReader();
        var groups = new List<string>();
        while (reader.Read())
        {
            groups.Add(string.Create(CultureInfo.InvariantCulture,
                $"{reader.GetString(0)}/{reader.GetString(1)}={reader.GetInt64(2)} (max attempt {reader.GetInt64(3)})"));
        }
        return string.Join(", ", groups);
    }

    private static SqliteConnection OpenOutboxReadConnection(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "outbox", "artifact-outbox.db"),
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task<T> WaitForAsync<T>(
        Func<Task<T?>> observe, TimeSpan timeout, string expectation, string root, Func<Task<string>>? diagnose = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (await observe().ConfigureAwait(false) is { } observed)
            {
                return observed;
            }
            if (stopwatch.Elapsed >= timeout)
            {
                var central = diagnose is null ? string.Empty : " Central: " + await diagnose().ConfigureAwait(false);
                Assert.Fail(
                    $"Not observed within {timeout}: {expectation}. Outbox: {DescribeOutbox(root)}.{central}");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
    }

    /// <summary>Resolves and schedules the committed artifact directly, so a failed wait names the central cause.</summary>
    private static async Task<string> DescribeCentralSchedulingAsync(Guid centralArtifactId)
    {
        using var hostScope = Fixture.CreateHostScope();
        var services = hostScope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts.AsNoTracking()
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.LogicalCameraInstallation)
            .SingleAsync(item => item.Id == centralArtifactId).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var assignment = await services.GetRequiredService<ProcessingGraphCatalogService>().ResolveAsync(
            CentralProcessingGraphTargetHost.Central,
            artifact.Frame!.ObservatoryId,
            artifact.Frame.LogicalCameraInstallation?.LogicalCameraId,
            now,
            CancellationToken.None).ConfigureAwait(false);
        var described = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"role={artifact.Role} installation={artifact.Frame.LogicalCameraInstallationId} ")
            .Append(CultureInfo.InvariantCulture, $"resolved={assignment?.Id}/{assignment?.RevisionId}/{assignment?.Scope}; ");
        try
        {
            await services.GetRequiredService<ICentralDerivativeJobScheduler>().EnsureRequiredJobsAsync(
                artifact.DevicePublicId, artifact.ArtifactId, now, CancellationToken.None).ConfigureAwait(false);
            described.Append("schedule=ok");
        }
#pragma warning disable CA1031 // Diagnostic only: the failure is reported, not handled.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            described.Append("schedule=").Append(exception.GetType().Name).Append(": ").Append(exception.Message)
                .Append(" reason=").Append((exception as CentralArtifactIntegrityException)?.ReasonCode)
                .Append(" at ").Append(string.Join(" <- ", (exception.StackTrace ?? string.Empty)
                    .Split('\n').Where(static line => line.Contains("HVO.SkyMonitor", StringComparison.Ordinal))
                    .Take(8).Select(static line => line.Trim())))
                .Append("; ");
            var executions = await db.CentralProcessingGraphExecutions.AsNoTracking()
                .Where(item => item.AnchorSourceCentralArtifactId == centralArtifactId)
                .Select(item => new { item.Id, item.RevisionId, item.Status, item.ExpandedAtUtc })
                .ToArrayAsync().ConfigureAwait(false);
            foreach (var execution in executions)
            {
                var requirements = await db.CentralDerivativeJobInputRequirements.AsNoTracking()
                    .Where(item => item.Job!.GraphExecutionId == execution.Id)
                    .GroupBy(item => item.ResolutionState)
                    .Select(group => new { group.Key, Count = group.Count() })
                    .ToArrayAsync().ConfigureAwait(false);
                described.Append(CultureInfo.InvariantCulture,
                    $"execution={execution.Id}/{execution.RevisionId}/{execution.Status}/expanded={execution.ExpandedAtUtc:O} requirements=")
                    .AppendJoin(',', requirements.Select(static item => $"{item.Key}:{item.Count}")).Append("; ");
            }
            described.Append(await DescribeAffectedJobsAsync(db, artifact.Frame).ConfigureAwait(false));
            described.Append(await DescribeSceneMismatchAsync(services, artifact.Frame).ConfigureAwait(false));
        }
        return described.ToString();
    }

    private static async Task<string> DescribeAffectedJobsAsync(ApplicationDbContext db, CentralFrame frame)
    {
        var jobs = await db.CentralDerivativeJobs.AsNoTracking()
            .Include(item => item.InputRequirements)
            .Where(job => job.Status == CentralDerivativeJobStatus.Waiting && job.InputRequirements.Any(requirement =>
                requirement.ExpectedAgentId == frame.AgentId && requirement.ExpectedCaptureSequence == frame.CaptureSequence))
            .ToArrayAsync().ConfigureAwait(false);
        var described = new StringBuilder().Append(CultureInfo.InvariantCulture, $"affected={jobs.Length} ");
        foreach (var job in jobs)
        {
            described.Append(CultureInfo.InvariantCulture,
                $"[job {job.RecipeName} wait={job.WaitKind} graph={job.GraphExecutionId is not null} reqs=")
                .AppendJoin('|', job.InputRequirements.OrderBy(static item => item.Ordinal).Select(static item =>
                    $"{item.BindingName}:{item.SourceKind}:{item.ResolutionState}:{item.ResolutionReasonCode}:req={item.IsRequired}:dep={item.GraphDependencyId is not null}:seq={item.ExpectedCaptureSequence}"))
                .Append("] ");
        }
        return described.ToString();
    }

    /// <summary>Repeats the projected-scene source checks field by field and names each one that differs.</summary>
    private static async Task<string> DescribeSceneMismatchAsync(IServiceProvider services, CentralFrame frame)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        var provenance = CentralProjectedSceneResolver.ReadProvenance(frame.SceneProvenanceJson);
        var scenes = await db.CentralArtifacts.AsNoTracking().Include(item => item.StructuredProduct)
            .Where(item => item.CentralFrameId == frame.Id && item.Role == FrameArtifactRole.Metadata &&
                item.MediaType == StructuredProcessingProductContracts.ProjectedSceneMediaType)
            .ToArrayAsync().ConfigureAwait(false);
        var described = new StringBuilder().Append(CultureInfo.InvariantCulture, $"scenes={scenes.Length}");
        if (provenance is null || scenes.Length != 1 || scenes[0].StructuredProduct is null)
        {
            return described.Append(" provenance=").Append(frame.SceneProvenanceJson).ToString();
        }
        var reader = services.GetRequiredService<ICentralArtifactObjectReader>();
        var snapshot = await reader.VerifyAsync(scenes[0], CancellationToken.None).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        await reader.CopyToAsync(snapshot, bytes, null, CancellationToken.None).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Parse(bytes.ToArray()).Scene!;
        var product = scenes[0].StructuredProduct!;
        var raw = StructuredProcessingProductManifestJson.ParseDescriptor(product.DescriptorJson).SourceCapture;
        void Check(string name, object? left, object? right)
        {
            if (!Equals(left, right))
            {
                described.Append(CultureInfo.InvariantCulture, $" {name}: scene={left} other={right};");
            }
        }
        Check("source.capture", scene.Source.CaptureId, frame.FrameId);
        Check("raw.capture", raw.Capture.CaptureId, frame.FrameId);
        Check("source.artifact", scene.Source.ArtifactId, raw.Artifact.ArtifactId);
        Check("source.identity", scene.Source.ArtifactIdentitySha256, CaptureContractJson.ComputeDescriptorSha256(raw));
        Check("scene.identity", scene.SceneIdentitySha256, product.ContentIdentitySha256);
        Check("width", scene.ImageTransform.OutputWidthPixels, raw.Layout.Width);
        Check("height", scene.ImageTransform.OutputHeightPixels, raw.Layout.Height);
        Check("sceneUtc", scene.EffectiveUtc, provenance.SceneUtc);
        Check("catalog.name", scene.Catalog.Name, provenance.CatalogName);
        Check("catalog.version", scene.Catalog.Version, provenance.CatalogVersion);
        Check("catalog.sha", scene.Catalog.ChecksumSha256.ToUpperInvariant(), provenance.CatalogChecksumSha256.ToUpperInvariant());
        Check("catalog.url", scene.Catalog.SourceUrl, provenance.CatalogSourceUrl ?? scene.Catalog.SourceUrl);
        Check("catalog.license", scene.Catalog.License, provenance.CatalogLicense ?? scene.Catalog.License);
        Check("catalog.schema", scene.Catalog.SchemaVersion, provenance.CatalogSchemaVersion ?? scene.Catalog.SchemaVersion);
        Check("catalog.preprocessing", scene.Catalog.PreprocessingVersion,
            provenance.CatalogPreprocessingVersion ?? scene.Catalog.PreprocessingVersion);
        Check("projection.model", scene.Projection.Model.ToString().ToUpperInvariant(), provenance.ProjectionModel.ToUpperInvariant());
        Check("projection.algorithm", scene.Projection.AlgorithmVersion, provenance.ProjectionAlgorithmVersion);
        Check("projection.calibration", scene.Projection.CalibrationVersion,
            provenance.ProjectionCalibrationVersion ?? scene.Projection.CalibrationVersion);
        Check("astronomy", scene.AstronomyAlgorithmVersion, provenance.AstronomyAlgorithmVersion);
        Check("ephemeris", scene.EphemerisModelVersion, provenance.EphemerisModelVersion);
        Check("topology.version", scene.ConstellationTopology?.Version, provenance.ConstellationTopologyVersion);
        Check("topology.url", scene.ConstellationTopology?.SourceUrl, provenance.ConstellationTopologySourceUrl);
        Check("topology.sha", scene.ConstellationTopology?.SourceSha256?.ToUpperInvariant(),
            provenance.ConstellationTopologySha256?.ToUpperInvariant());
        Check("topology.license", scene.ConstellationTopology?.License, provenance.ConstellationTopologyLicense);
        Check("topology.preprocessing", scene.ConstellationTopology?.PreprocessingVersion,
            provenance.ConstellationTopologyPreprocessingVersion);
        Check("constellations", string.Join(',', scene.Selection.ConstellationIds), string.Join(',', provenance.ConstellationIds ?? []));
        Check("endpointStars", scene.Selection.IncludeConstellationEndpointStars, provenance.IncludeConstellationEndpointStars);
        return described.ToString();
    }

    private static string WithStaleAnnotationImplementationVersion(string definitionJson)
    {
        Assert.IsTrue(BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.Annotation, out var annotation));
        var current = $"\"{annotation!.ImplementationVersion}\"";
        Assert.Contains(current, definitionJson, StringComparison.Ordinal);
        return definitionJson.Replace(current, "\"projected-annotation-v0\"", StringComparison.Ordinal);
    }
}
