using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Minio;
using Minio.DataModel.Args;
using System.Collections.Immutable;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CentralProcessingGraphMigrationTests
{
    [TestMethod]
    public async Task SqlServerReplayExpansionSealsAndProducesAClaimableFrozenLease()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphScheduler_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var owner = new ApplicationUser
            {
                Id = "operator",
                UserName = "operator",
                NormalizedUserName = "OPERATOR",
                AccountType = AccountType.User
            };
            var observatory = new Observatory
            {
                OwnerUserId = owner.Id,
                Name = "Graph Scheduler",
                CreatedAtUtc = now.AddDays(-1)
            };
            var devicePublicId = Guid.NewGuid();
            var registration = new DeviceRegistration
            {
                DeviceId = "graph-agent",
                Observatory = observatory,
                ObservatoryId = observatory.Id,
                FriendlyName = "Graph Agent",
                ObservatoryName = observatory.Name,
                OwnerUserId = owner.Id,
                OwnerDisplayName = owner.UserName,
                Status = DeviceRegistrationStatus.Active,
                VerificationCodeHash = new string('1', 64),
                DevicePublicId = devicePublicId,
                IssuedAtUtc = now.AddDays(-1),
                ActivatedAtUtc = now.AddDays(-1)
            };
            var camera = new LogicalCamera
            {
                Observatory = observatory,
                ObservatoryId = observatory.Id,
                Slug = "graph-camera",
                Name = "Graph Camera",
                Description = "SQL scheduler test",
                CreatedAtUtc = now.AddDays(-1),
                CreatedByUserId = owner.Id
            };
            var installation = new LogicalCameraInstallation
            {
                LogicalCamera = camera,
                LogicalCameraId = camera.Id,
                Registration = registration,
                RegistrationId = registration.Id,
                InstallationPublicId = Guid.NewGuid(),
                AssignedAtUtc = now.AddHours(-1),
                AssignedByUserId = owner.Id,
                AssignmentReasonCode = "test"
            };
            var frame = new CentralFrame
            {
                RegistrationId = registration.Id,
                LogicalCameraInstallation = installation,
                LogicalCameraInstallationId = installation.Id,
                DevicePublicId = devicePublicId,
                ObservatoryId = observatory.Id,
                AgentId = registration.DeviceId,
                FrameId = Guid.NewGuid(),
                CapturedAtUtc = now.AddMinutes(-1),
                FirstReceivedAtUtc = now.AddMinutes(-1),
                RigId = "rig"
            };
            var artifact = new CentralArtifact
            {
                CentralFrameId = frame.Id,
                Frame = frame,
                DevicePublicId = devicePublicId,
                ArtifactId = Guid.NewGuid(),
                Role = FrameArtifactRole.Raw,
                Variant = "source",
                RecipeVersion = "raw-v1",
                ManifestSchemaVersion = "manifest-v1",
                MediaType = "application/octet-stream",
                ByteLength = 1,
                ChecksumSha256 = new string('A', 64),
                StorageReference = $"s3://skymonitor-artifacts/{Guid.NewGuid():N}",
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                ReceivedAtUtc = now.AddMinutes(-1),
                CreatedUtc = now.AddMinutes(-1),
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete
            };
            var definition = CreatePreviewGraph();
            var portable = ProcessingGraphCompiler.Compile(definition).Plan!;
            var central = ProcessingGraphCompiler.Compile(
                definition, new(ProcessingGraphHosts.LogicHost, [])).Plan!;
            var revision = new CentralProcessingGraphRevision
            {
                Name = definition.Name,
                Revision = definition.Revision,
                DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)),
                DefinitionIdentitySha256 = portable.DefinitionIdentitySha256,
                PortablePlanIdentitySha256 = portable.PlanIdentitySha256,
                CentralPlanIdentitySha256 = central.PlanIdentitySha256,
                CreatedAtUtc = now.AddMinutes(-1),
                CreatedByUserId = owner.Id,
                PublishedAtUtc = now.AddSeconds(-1),
                PublishedByUserId = owner.Id
            };
            var assignment = new CentralProcessingGraphAssignment
            {
                Revision = revision,
                RevisionId = revision.Id,
                TargetHost = CentralProcessingGraphTargetHost.Central,
                Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
                ObservatoryId = observatory.Id,
                LogicalCamera = camera,
                LogicalCameraId = camera.Id,
                EffectiveFromUtc = now.AddMinutes(-1),
                CreatedAtUtc = now.AddMinutes(-1),
                ActorUserId = owner.Id,
                ReasonCode = "sql-live"
            };
            revision.Assignments.Add(assignment);
            context.AddRange(
                owner, observatory, registration, camera, installation, frame, artifact, revision, assignment);
            await context.SaveChangesAsync().ConfigureAwait(false);
            using var workerTelemetry = new CentralDerivativeWorkerTelemetry();
            using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
            var registry = new MultiOutputNodeRegistry(new CentralDerivativeRecipeCatalog());
            var catalog = new ProcessingGraphCatalogService(
                context,
                registry,
                TimeProvider.System,
                catalogTelemetry,
                NullLogger<ProcessingGraphCatalogService>.Instance);
            var scheduler = new CentralProcessingGraphScheduler(
                context,
                catalog,
                registry,
                new NoopWindowResolver(),
                new UnusedObjectReader(),
                workerTelemetry,
                TimeProvider.System);

            var scheduled = await scheduler.ScheduleReplayAsync(
                new(revision.Id, [artifact.Id], owner.Id, "sql-replay", "sql-test"),
                now,
                CancellationToken.None).ConfigureAwait(false);

            scheduled.Outcome.Should().Be(CentralProcessingGraphScheduleOutcome.Created);
            context.ChangeTracker.Clear();
            var execution = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Include(item => item.Sources)
                .Include(item => item.Dependencies)
                .Include(item => item.Jobs).ThenInclude(job => job.Outputs)
                .SingleAsync(item => item.Id == scheduled.Execution!.Id).ConfigureAwait(false);
            execution.ExpandedAtUtc.Should().NotBeNull();
            execution.Sources.Should().HaveCount(execution.ExpectedSourceCount);
            execution.Jobs.Should().HaveCount(execution.ExpectedNodeCount);
            execution.Dependencies.Should().HaveCount(execution.ExpectedDependencyCount);
            execution.Jobs.SelectMany(job => job.Outputs).Should().HaveCount(execution.ExpectedOutputCount);
            execution.Jobs.Single().Status.Should().Be(CentralDerivativeJobStatus.Pending);

            var graphJob = await context.CentralDerivativeJobs.SingleAsync(item => item.Id == execution.Jobs.Single().Id)
                .ConfigureAwait(false);
            graphJob.Status = CentralDerivativeJobStatus.Skipped;
            graphJob.AvailableAtUtc = null;
            graphJob.UpdatedAtUtc = now.AddTicks(1);
            await context.SaveChangesAsync().ConfigureAwait(false);
            var operations = new CentralDerivativeJobOperationsService(
                context,
                TimeProvider.System,
                workerTelemetry,
                NullLogger<CentralDerivativeJobOperationsService>.Instance);
            var requeue = async () => await operations.RequeueAsync(
                graphJob.Id, owner.Id, CancellationToken.None).ConfigureAwait(false);
            await requeue.Should().ThrowAsync<CentralDerivativeJobStateException>()
                .WithMessage("*replay the graph instead*").ConfigureAwait(false);
            graphJob.Status = CentralDerivativeJobStatus.Pending;
            graphJob.AvailableAtUtc = now;
            graphJob.UpdatedAtUtc = now.AddTicks(2);
            await context.SaveChangesAsync().ConfigureAwait(false);

            var lease = await new CentralDerivativeJobService(context, TimeProvider.System)
                .ClaimNextAsync("sql-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            lease.Should().NotBeNull();
            lease!.GraphExecutionId.Should().Be(execution.Id);
            lease.GraphRevisionId.Should().Be(revision.Id);
            CentralDerivativeJobExecutor.HasValidFrozenGraphPlan(lease).Should().BeTrue();

            var contracts = definition.Nodes.Single().Outputs;
            var products = contracts.Select((contract, index) => CreateProduct(contract, lease, index + 1)).ToArray();
            var interceptor = new ThrowOnCommitInterceptor(3);
            var interruptedOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(builder.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .AddInterceptors(interceptor)
                .Options;
            var services = AssemblyHooks.Fixture.Factory.Services;
            var objectStore = services.GetRequiredService<IObjectStore>();
            var objectReader = services.GetRequiredService<ICentralArtifactObjectReader>();
            var storageNames = services.GetRequiredService<CentralObjectStorageNames>();
            await using (var interruptedContext = new ApplicationDbContext(interruptedOptions))
            {
                var interruptedWriter = new CentralDerivativeOutputWriter(
                    interruptedContext,
                    objectStore,
                    objectReader,
                    workerTelemetry,
                    TimeProvider.System,
                    NullLogger<CentralDerivativeOutputWriter>.Instance,
                    storageNames);
                var interrupted = async () => await interruptedWriter.PersistSetAsync(
                    lease, [products[1], products[0]], 1, TimeSpan.FromMilliseconds(1), CancellationToken.None)
                    .ConfigureAwait(false);
                await interrupted.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("Injected graph output completion failure.").ConfigureAwait(false);
            }
            interceptor.Triggered.Should().BeTrue();
            context.ChangeTracker.Clear();
            (await context.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == lease.JobId)
                .ConfigureAwait(false)).Status.Should().Be(CentralDerivativeJobStatus.Leased);
            (await context.CentralDerivativeJobOutputs.AsNoTracking().Where(item => item.CentralDerivativeJobId == lease.JobId)
                .ToArrayAsync().ConfigureAwait(false)).Should().OnlyContain(output => output.ResultCentralArtifactId == null);
            (await context.CentralArtifactProcessingEvidence.AsNoTracking().CountAsync(item =>
                item.CentralDerivativeJobId == lease.JobId).ConfigureAwait(false)).Should().Be(2);

            await using (var recoveryContext = new ApplicationDbContext(options))
            {
                var recoveryWriter = new CentralDerivativeOutputWriter(
                    recoveryContext,
                    objectStore,
                    objectReader,
                    workerTelemetry,
                    TimeProvider.System,
                    NullLogger<CentralDerivativeOutputWriter>.Instance,
                    storageNames);
                var recovered = await recoveryWriter.TryCompletePendingSetAsync(lease, CancellationToken.None)
                    .ConfigureAwait(false);
                recovered.Should().Equal(products.Select(product =>
                    ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256)));
            }
            context.ChangeTracker.Clear();
            var completed = await context.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.ResultArtifact)
                .Include(item => item.Outputs).ThenInclude(output => output.ResultArtifact)
                .SingleAsync(item => item.Id == lease.JobId).ConfigureAwait(false);
            completed.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            completed.ResultArtifact!.ArtifactId.Should().Be(
                ProcessingIdentity.CreateArtifactId(products[0].OutputIdentitySha256));
            completed.Outputs.OrderBy(output => output.Ordinal).Select(output => output.ResultArtifact!.ArtifactId)
                .Should().Equal(products.Select(product => ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256)));

            var quarantineFrame = new CentralFrame
            {
                RegistrationId = registration.Id,
                LogicalCameraInstallationId = installation.Id,
                DevicePublicId = devicePublicId,
                ObservatoryId = observatory.Id,
                AgentId = registration.DeviceId,
                FrameId = Guid.NewGuid(),
                CapturedAtUtc = now,
                FirstReceivedAtUtc = now,
                RigId = frame.RigId
            };
            var quarantineSource = new CentralArtifact
            {
                CentralFrameId = quarantineFrame.Id,
                Frame = quarantineFrame,
                DevicePublicId = devicePublicId,
                ArtifactId = Guid.NewGuid(),
                Role = FrameArtifactRole.Raw,
                Variant = "source",
                RecipeVersion = "raw-v1",
                ManifestSchemaVersion = "manifest-v1",
                MediaType = "application/octet-stream",
                ByteLength = 1,
                ChecksumSha256 = new string('B', 64),
                StorageReference = $"s3://skymonitor-artifacts/{Guid.NewGuid():N}",
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                ReceivedAtUtc = now,
                CreatedUtc = now,
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete
            };
            context.AddRange(quarantineFrame, quarantineSource);
            await context.SaveChangesAsync().ConfigureAwait(false);
            var quarantineExecution = await scheduler.ScheduleReplayAsync(
                new(revision.Id, [quarantineSource.Id], owner.Id, "sql-quarantine", "integrity-test"),
                now,
                CancellationToken.None).ConfigureAwait(false);
            quarantineExecution.Outcome.Should().Be(CentralProcessingGraphScheduleOutcome.Created);
            var quarantineLease = await new CentralDerivativeJobService(context, TimeProvider.System)
                .ClaimNextAsync("quarantine-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            quarantineLease.Should().NotBeNull();
            var quarantineProducts = contracts.Select((contract, index) =>
                CreateProduct(contract, quarantineLease!, index + 10)).ToArray();
            await using (var quarantineContext = new ApplicationDbContext(options))
            {
                var quarantineWriter = new CentralDerivativeOutputWriter(
                    quarantineContext,
                    objectStore,
                    new IntegrityFailureObjectReader(),
                    workerTelemetry,
                    TimeProvider.System,
                    NullLogger<CentralDerivativeOutputWriter>.Instance,
                    storageNames);
                var quarantine = async () => await quarantineWriter.PersistSetAsync(
                    quarantineLease!, quarantineProducts, 1, TimeSpan.Zero, CancellationToken.None)
                    .ConfigureAwait(false);
                await quarantine.Should().ThrowAsync<CentralDerivativeOutputIntegrityException>()
                    .Where(exception => exception.ReasonCode == "object.test-integrity")
                    .ConfigureAwait(false);
            }
            context.ChangeTracker.Clear();
            (await context.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(item => item.Id == quarantineLease!.JobId).ConfigureAwait(false)).Status
                .Should().Be(CentralDerivativeJobStatus.Quarantined);
            var quarantinedArtifact = await context.CentralArtifactProcessingEvidence.AsNoTracking()
                .Include(item => item.Artifact)
                .Where(item => item.CentralDerivativeJobId == quarantineLease!.JobId)
                .Select(item => item.Artifact!)
                .SingleAsync().ConfigureAwait(false);
            quarantinedArtifact.ObjectState.Should().Be(CentralArtifactObjectState.Quarantined);
            quarantinedArtifact.ReconstructionState.Should().Be(CentralReconstructionState.Quarantined);
            quarantinedArtifact.StateReasonCode.Should().Be("object.test-integrity");

            await new CentralDerivativeJobScheduler(
                    context,
                    new CentralDerivativeRecipeCatalog(),
                    new NoopWindowResolver(),
                    graphScheduler: scheduler)
                .EnsureRequiredJobsAsync(artifact, now, CancellationToken.None).ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var liveExecution = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Include(item => item.Jobs)
                .SingleAsync(item => item.ExecutionClass == CentralProcessingGraphExecutionClass.Live)
                .ConfigureAwait(false);
            liveExecution.AssignmentId.Should().Be(assignment.Id);
            liveExecution.Jobs.Should().ContainSingle();
            liveExecution.Jobs.Should().OnlyContain(job => job.GraphExecutionId == liveExecution.Id);
            (await context.CentralDerivativeJobs.AsNoTracking().CountAsync(job =>
                job.SourceCentralArtifactId == artifact.Id && job.GraphExecutionId == null).ConfigureAwait(false))
                .Should().Be(0);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task SqlServerLiveRawBeforeCalibratedAwaitsSourcesWithoutLegacyJobsThenCompletesOneGraph()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphMultiSource_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var camera = await SeedCameraAsync(context, now, "multi-source").ConfigureAwait(false);
            var raw = CreateSourceArtifact(camera, FrameArtifactRole.Raw, 'A', now.AddMinutes(-1));
            var definition = CreateRawAndCalibratedPreviewGraph();
            var registry = new CentralProcessingGraphNodeRegistry(new CentralDerivativeRecipeCatalog());
            var portable = ProcessingGraphCompiler.Compile(definition);
            portable.IsValid.Should().BeTrue(string.Join(Environment.NewLine, portable.Diagnostics));
            var central = ProcessingGraphCompiler.Compile(
                definition, new(ProcessingGraphHosts.LogicHost, registry.Capabilities));
            central.IsValid.Should().BeTrue(string.Join(Environment.NewLine, central.Diagnostics));
            registry.Validate(central.Plan!).Should().BeTrue();
            var revision = new CentralProcessingGraphRevision
            {
                Name = definition.Name,
                Revision = definition.Revision,
                DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)),
                DefinitionIdentitySha256 = portable.Plan!.DefinitionIdentitySha256,
                PortablePlanIdentitySha256 = portable.Plan.PlanIdentitySha256,
                CentralPlanIdentitySha256 = central.Plan!.PlanIdentitySha256,
                CreatedAtUtc = now.AddMinutes(-1),
                CreatedByUserId = camera.Owner.Id,
                PublishedAtUtc = now.AddSeconds(-1),
                PublishedByUserId = camera.Owner.Id
            };
            var assignment = new CentralProcessingGraphAssignment
            {
                Revision = revision,
                RevisionId = revision.Id,
                TargetHost = CentralProcessingGraphTargetHost.Central,
                Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
                ObservatoryId = camera.Observatory.Id,
                LogicalCameraId = camera.Camera.Id,
                EffectiveFromUtc = now.AddMinutes(-1),
                CreatedAtUtc = now.AddMinutes(-1),
                ActorUserId = camera.Owner.Id,
                ReasonCode = "sql-multi-source"
            };
            revision.Assignments.Add(assignment);
            context.AddRange(raw, revision, assignment);
            await context.SaveChangesAsync().ConfigureAwait(false);
            using var workerTelemetry = new CentralDerivativeWorkerTelemetry();
            using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
            var catalog = new ProcessingGraphCatalogService(
                context, registry, TimeProvider.System, catalogTelemetry,
                NullLogger<ProcessingGraphCatalogService>.Instance);
            var graphScheduler = new CentralProcessingGraphScheduler(
                context, catalog, registry, new NoopWindowResolver(), new UnusedObjectReader(),
                workerTelemetry, TimeProvider.System);
            var jobScheduler = new CentralDerivativeJobScheduler(
                context, new CentralDerivativeRecipeCatalog(), new NoopWindowResolver(), graphScheduler: graphScheduler);

            // Raw arrives first: the assigned graph is awaiting its calibrated source. Legacy scheduling must not
            // run, otherwise the frame would receive duplicate non-graph derivative work.
            await jobScheduler.EnsureRequiredJobsAsync(raw, now, CancellationToken.None).ConfigureAwait(false);
            context.ChangeTracker.Clear();
            (await context.CentralDerivativeJobs.AsNoTracking().CountAsync().ConfigureAwait(false)).Should().Be(0);
            (await context.CentralProcessingGraphExecutions.AsNoTracking().CountAsync().ConfigureAwait(false))
                .Should().Be(0);

            var calibrated = CreateSourceArtifact(camera, FrameArtifactRole.Calibrated, 'B', now);
            calibrated.Variant = "calibrated";
            calibrated.MediaType = "application/x-hvo-linear-frame";
            context.Add(calibrated);
            await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var calibratedForScheduling = await context.CentralArtifacts.Include(item => item.Frame)
                .SingleAsync(item => item.Id == calibrated.Id).ConfigureAwait(false);
            await jobScheduler.EnsureRequiredJobsAsync(calibratedForScheduling, now, CancellationToken.None)
                .ConfigureAwait(false);
            context.ChangeTracker.Clear();
            await jobScheduler.EnsureRequiredJobsAsync(
                await context.CentralArtifacts.Include(item => item.Frame).SingleAsync(item => item.Id == raw.Id)
                    .ConfigureAwait(false),
                now.AddSeconds(1),
                CancellationToken.None).ConfigureAwait(false);
            context.ChangeTracker.Clear();

            var executions = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Include(item => item.Sources)
                .Include(item => item.Jobs)
                .ToListAsync().ConfigureAwait(false);
            executions.Should().ContainSingle();
            var execution = executions.Single();
            execution.ExecutionClass.Should().Be(CentralProcessingGraphExecutionClass.Live);
            execution.AssignmentId.Should().Be(assignment.Id);
            execution.Sources.Select(source => source.CentralArtifactId).Should().BeEquivalentTo([raw.Id, calibrated.Id]);
            execution.Jobs.Should().ContainSingle();
            execution.Jobs.Single().Status.Should().Be(CentralDerivativeJobStatus.Pending);
            (await context.CentralDerivativeJobs.AsNoTracking().CountAsync(job => job.GraphExecutionId == null)
                .ConfigureAwait(false)).Should().Be(0, "no legacy derivative job may be created for a graph-assigned frame");

            var lease = await new CentralDerivativeJobService(context, TimeProvider.System)
                .ClaimNextAsync("multi-source-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            lease.Should().NotBeNull();
            lease!.GraphExecutionId.Should().Be(execution.Id);
            var services = AssemblyHooks.Fixture.Factory.Services;
            await using (var writerContext = new ApplicationDbContext(options))
            {
                var writer = new CentralDerivativeOutputWriter(
                    writerContext,
                    services.GetRequiredService<IObjectStore>(),
                    services.GetRequiredService<ICentralArtifactObjectReader>(),
                    workerTelemetry,
                    TimeProvider.System,
                    NullLogger<CentralDerivativeOutputWriter>.Instance,
                    services.GetRequiredService<CentralObjectStorageNames>());
                await writer.PersistSetAsync(
                    lease,
                    definition.Nodes.Single().Outputs.Select(contract => CreateProduct(contract, lease, 7)).ToArray(),
                    1,
                    TimeSpan.Zero,
                    CancellationToken.None).ConfigureAwait(false);
            }
            context.ChangeTracker.Clear();
            await graphScheduler.ConvergeAsync(execution.Id, now.AddSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
            context.ChangeTracker.Clear();

            var completed = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Include(item => item.Jobs)
                .SingleAsync().ConfigureAwait(false);
            completed.Status.Should().Be(CentralProcessingGraphExecutionStatus.Completed);
            completed.Jobs.Single().Status.Should().Be(CentralDerivativeJobStatus.Completed);
            (await context.CentralDerivativeJobs.AsNoTracking().CountAsync().ConfigureAwait(false)).Should().Be(1);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow((int)FrameArtifactRole.Raw, false)]
    [DataRow((int)FrameArtifactRole.Raw, true)]
    [DataRow((int)FrameArtifactRole.Calibrated, true)]
    public async Task SqlServerLiveGraphWithoutTransientNodeKeepsExactlyOneLegacyTransientJob(
        int sourceRoleValue,
        bool twoSourceGraph)
    {
        var sourceRole = (FrameArtifactRole)sourceRoleValue;
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphTransient{sourceRole}{(twoSourceGraph ? "Two" : "One")}_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var camera = await SeedCameraAsync(
                context, now, sourceRole == FrameArtifactRole.Raw ? "transient-raw" : "transient-calibrated")
                .ConfigureAwait(false);
            var recipeCatalog = new CentralDerivativeRecipeCatalog(new CentralTransientOptions
            {
                Mode = TransientDetectorExecutionMode.Central,
                SourceRole = sourceRole
            });
            var transientRecipe = recipeCatalog.GetTransientRecipe(sourceRole);
            transientRecipe.Should().NotBeNull();
            var registry = new CentralProcessingGraphNodeRegistry(recipeCatalog);
            // One-source: a graph covering only Raw (Created on the Raw ingest). Two-source: a graph that needs both
            // frame sources, so the Raw ingest awaits sources and the Calibrated ingest creates the execution. For a
            // Raw transient recipe the two-source row proves the legacy transient job is created while the graph is
            // still AwaitingSources and is not duplicated once the graph is Created and the Raw is re-ingested.
            // Neither graph carries a transient node.
            var basic = DatabaseSeeder.CreateBasicCentralProcessingGraph(recipeCatalog);
            var definition = twoSourceGraph
                ? CreateRawAndCalibratedPreviewGraph()
                : basic with { Name = "sql-preview-no-transient", Nodes = [.. basic.Nodes.Where(node => node.Id == "Preview")] };
            var raw = CreateSourceArtifact(camera, FrameArtifactRole.Raw, 'A', now.AddMinutes(-1));
            context.AddRange(raw, CreateAssignment(definition, registry, camera, now));
            await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();
            using var workerTelemetry = new CentralDerivativeWorkerTelemetry();
            using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
            var catalog = new ProcessingGraphCatalogService(
                context, registry, TimeProvider.System, catalogTelemetry,
                NullLogger<ProcessingGraphCatalogService>.Instance);
            var graphScheduler = new CentralProcessingGraphScheduler(
                context, catalog, registry, new NoopWindowResolver(), new UnusedObjectReader(),
                workerTelemetry, TimeProvider.System);
            var jobScheduler = new CentralDerivativeJobScheduler(
                context, recipeCatalog, new NoopWindowResolver(), graphScheduler: graphScheduler);
            async Task IngestAsync(Guid artifactId, DateTimeOffset at)
            {
                context.ChangeTracker.Clear();
                var artifact = await context.CentralArtifacts
                    .Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
                    .SingleAsync(item => item.Id == artifactId).ConfigureAwait(false);
                await jobScheduler.EnsureRequiredJobsAsync(artifact, at, CancellationToken.None).ConfigureAwait(false);
                context.ChangeTracker.Clear();
            }

            await IngestAsync(raw.Id, now).ConfigureAwait(false);
            var transientSourceId = sourceRole == FrameArtifactRole.Raw ? raw.Id : Guid.Empty;
            if (twoSourceGraph)
            {
                // Raw awaited the calibrated source (no execution yet). A Raw transient recipe is legacy-scheduled on
                // that AwaitingSources disposition; a Calibrated one has no Raw job to make.
                (await context.CentralProcessingGraphExecutions.AsNoTracking().CountAsync().ConfigureAwait(false))
                    .Should().Be(0);
                (await context.CentralDerivativeJobs.AsNoTracking().CountAsync().ConfigureAwait(false))
                    .Should().Be(sourceRole == FrameArtifactRole.Raw ? 1 : 0);
                // Re-ingest while still AwaitingSources: idempotent.
                await IngestAsync(raw.Id, now.AddMilliseconds(500)).ConfigureAwait(false);
                (await context.CentralDerivativeJobs.AsNoTracking().CountAsync().ConfigureAwait(false))
                    .Should().Be(sourceRole == FrameArtifactRole.Raw ? 1 : 0);
                var calibrated = CreateSourceArtifact(camera, FrameArtifactRole.Calibrated, 'B', now);
                calibrated.Variant = "calibrated";
                calibrated.MediaType = "application/x-hvo-linear-frame";
                context.Add(calibrated);
                await context.SaveChangesAsync().ConfigureAwait(false);
                if (sourceRole == FrameArtifactRole.Calibrated)
                {
                    transientSourceId = calibrated.Id;
                }
                await IngestAsync(calibrated.Id, now).ConfigureAwait(false);
                (await context.CentralProcessingGraphExecutions.AsNoTracking().CountAsync().ConfigureAwait(false))
                    .Should().Be(1, "the calibrated source completes the two-source graph");
                await IngestAsync(calibrated.Id, now.AddSeconds(1)).ConfigureAwait(false);
            }
            // Re-ingest (graph Existing): idempotent for the transient source and, for two-source graphs, the Raw too.
            await IngestAsync(transientSourceId, now.AddSeconds(1)).ConfigureAwait(false);
            await IngestAsync(raw.Id, now.AddSeconds(2)).ConfigureAwait(false);

            var executions = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Include(item => item.Jobs).ToListAsync().ConfigureAwait(false);
            executions.Should().ContainSingle();
            executions.Single().Jobs.Should().ContainSingle(job => job.RecipeName == BuiltInProcessingRecipes.EncodedPreview);
            var legacyJobs = await context.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.GraphExecutionId == null).ToListAsync().ConfigureAwait(false);
            legacyJobs.Should().ContainSingle("exactly one legacy job: the transient recipe, created once");
            var transientJob = legacyJobs.Single();
            transientJob.RecipeName.Should().Be(CentralTransientRuntime.RecipeName);
            transientJob.SourceCentralArtifactId.Should().Be(transientSourceId);
            transientJob.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
            transientJob.WaitKind.Should().Be(CentralDerivativeWaitKind.Window);
            (await context.CentralTransientValidationJobs.AsNoTracking().CountAsync().ConfigureAwait(false)).Should().Be(1);
            (await context.CentralDerivativeJobs.AsNoTracking().CountAsync().ConfigureAwait(false))
                .Should().Be(2, "one graph node plus one legacy transient job; no other legacy recipe work");
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task SqlServerLiveGraphWithTransientNodeSchedulesNoLegacyTransientJob()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphTransientOwned_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var camera = await SeedCameraAsync(context, now, "transient-owned").ConfigureAwait(false);
            var recipeCatalog = new CentralDerivativeRecipeCatalog(new CentralTransientOptions
            {
                Mode = TransientDetectorExecutionMode.Central,
                SourceRole = FrameArtifactRole.Raw
            });
            var registry = new CentralProcessingGraphNodeRegistry(recipeCatalog);
            var basic = DatabaseSeeder.CreateBasicCentralProcessingGraph(recipeCatalog);
            var definition = basic with
            {
                Name = "sql-preview-and-transient",
                Nodes = [.. basic.Nodes.Where(node => node.Id is "Preview" or "TransientDetection")]
            };
            var raw = CreateSourceArtifact(camera, FrameArtifactRole.Raw, 'A', now.AddMinutes(-1));
            context.AddRange(raw, CreateAssignment(definition, registry, camera, now));
            await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();
            using var workerTelemetry = new CentralDerivativeWorkerTelemetry();
            using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
            var catalog = new ProcessingGraphCatalogService(
                context, registry, TimeProvider.System, catalogTelemetry,
                NullLogger<ProcessingGraphCatalogService>.Instance);
            var graphScheduler = new CentralProcessingGraphScheduler(
                context, catalog, registry, new NoopWindowResolver(), new UnusedObjectReader(),
                workerTelemetry, TimeProvider.System);
            var jobScheduler = new CentralDerivativeJobScheduler(
                context, recipeCatalog, new NoopWindowResolver(), graphScheduler: graphScheduler);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var artifact = await context.CentralArtifacts
                    .Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
                    .SingleAsync(item => item.Id == raw.Id).ConfigureAwait(false);
                await jobScheduler.EnsureRequiredJobsAsync(artifact, now.AddSeconds(attempt), CancellationToken.None)
                    .ConfigureAwait(false);
                context.ChangeTracker.Clear();
            }

            var executions = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Include(item => item.Jobs).ToListAsync().ConfigureAwait(false);
            executions.Should().ContainSingle();
            executions.Single().Jobs.Select(job => job.RecipeName).Should().BeEquivalentTo(
                [BuiltInProcessingRecipes.EncodedPreview, CentralTransientRuntime.RecipeName]);
            (await context.CentralDerivativeJobs.AsNoTracking().CountAsync(job => job.GraphExecutionId == null)
                .ConfigureAwait(false)).Should().Be(0, "the graph owns the transient node; no legacy transient job");
            (await context.CentralTransientValidationJobs.AsNoTracking().CountAsync().ConfigureAwait(false)).Should().Be(1);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Invalidating the anchor source of a Completed graph execution must not reopen its node (the frozen-column and
    /// terminal-outcome triggers would reject that) while legacy dependents of the same artifact still take the
    /// legacy invalidation path.
    /// </summary>
    [TestMethod]
    public async Task SqlServerInvalidatingAnchorOfCompletedGraphLeavesGraphUntouchedAndReopensLegacyDependents()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphInvalidateCompleted_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var (_, raw, definition, graphScheduler, workerTelemetry) =
                await ScheduleSingleNodePreviewGraphAsync(context, now, "invalidate-completed").ConfigureAwait(false);
            using var telemetry = workerTelemetry;
            var execution = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Include(item => item.Jobs)
                .SingleAsync().ConfigureAwait(false);
            execution.Jobs.Single().Status.Should().Be(CentralDerivativeJobStatus.Pending);

            var lease = await new CentralDerivativeJobService(context, TimeProvider.System)
                .ClaimNextAsync("invalidate-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            lease.Should().NotBeNull();
            lease!.GraphExecutionId.Should().Be(execution.Id);
            var services = AssemblyHooks.Fixture.Factory.Services;
            await using (var writerContext = new ApplicationDbContext(options))
            {
                var writer = new CentralDerivativeOutputWriter(
                    writerContext,
                    services.GetRequiredService<IObjectStore>(),
                    services.GetRequiredService<ICentralArtifactObjectReader>(),
                    telemetry,
                    TimeProvider.System,
                    NullLogger<CentralDerivativeOutputWriter>.Instance,
                    services.GetRequiredService<CentralObjectStorageNames>());
                await writer.PersistSetAsync(
                    lease,
                    definition.Nodes.Single().Outputs.Select(contract => CreateProduct(contract, lease, 11)).ToArray(),
                    1,
                    TimeSpan.Zero,
                    CancellationToken.None).ConfigureAwait(false);
            }
            context.ChangeTracker.Clear();
            await graphScheduler.ConvergeAsync(execution.Id, now.AddSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var completed = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Include(item => item.Jobs).ThenInclude(job => job.Inputs)
                .Include(item => item.Jobs).ThenInclude(job => job.InputRequirements)
                .SingleAsync().ConfigureAwait(false);
            completed.Status.Should().Be(CentralProcessingGraphExecutionStatus.Completed);
            var completedNode = completed.Jobs.Single();
            completedNode.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            completedNode.Inputs.Should().ContainSingle(input => input.CentralArtifactId == raw.Id);

            // A legacy (non-graph) Completed dependent of the same anchor artifact.
            var legacy = new CentralDerivativeJob
            {
                SourceCentralArtifactId = raw.Id,
                TargetRole = FrameArtifactRole.Metadata,
                TargetRecipeVersion = CentralDerivativeRecipeCatalog.ImageQualityRecipeVersion,
                TargetVariant = CentralDerivativeRecipeCatalog.ImageQualityVariant,
                RecipeName = BuiltInProcessingRecipes.ImageQuality,
                RecipeOptionsJson = "{}",
                InputSelectorJson = "{}",
                RequestedRecipeIdentitySha256 = CentralDerivativeRecipeCatalog.ImageQualityRequestedRecipeIdentity,
                ExpectedRecipeIdentitySha256 = CentralDerivativeRecipeCatalog.ImageQualityRequestedRecipeIdentity,
                RequestIdentitySha256 = new string('9', 64),
                Status = CentralDerivativeJobStatus.Completed,
                AttemptCount = 1,
                MaxAttempts = 3,
                CompletedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            context.Add(legacy);
            await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();

            var anchor = await context.CentralArtifacts.SingleAsync(item => item.Id == raw.Id).ConfigureAwait(false);
            anchor.ObjectState = CentralArtifactObjectState.Pending;
            anchor.StateReasonCode = "object.missing";
            await FluentActions.Awaiting(() => ArtifactIngestService.InvalidateDependentsAsync(
                    context, anchor, CancellationToken.None))
                .Should().NotThrowAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();

            var after = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Include(item => item.Jobs).ThenInclude(job => job.Inputs)
                .Include(item => item.Jobs).ThenInclude(job => job.InputRequirements)
                .SingleAsync().ConfigureAwait(false);
            after.Status.Should().Be(CentralProcessingGraphExecutionStatus.Completed);
            after.UpdatedAtUtc.Should().Be(completed.UpdatedAtUtc);
            var afterNode = after.Jobs.Single();
            afterNode.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            afterNode.UpdatedAtUtc.Should().Be(completedNode.UpdatedAtUtc);
            afterNode.LastError.Should().BeNull();
            afterNode.InputSetIdentitySha256.Should().Be(completedNode.InputSetIdentitySha256);
            afterNode.ResolutionStartedAtUtc.Should().Be(completedNode.ResolutionStartedAtUtc);
            afterNode.Inputs.Should().ContainSingle(input => input.CentralArtifactId == raw.Id);
            afterNode.InputRequirements.Single().ResolutionState.Should().Be(CentralDerivativeInputResolutionState.Resolved);
            var legacyAfter = await context.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == legacy.Id).ConfigureAwait(false);
            legacyAfter.Status.Should().Be(CentralDerivativeJobStatus.RetryableFailure);
            legacyAfter.LastError.Should().Be(CentralDerivativeJobScheduler.SourceInvalidatedReason);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A runnable node of a live execution that loses its anchor cannot re-resolve (graph inputs are frozen); it is
    /// terminalized through a legal outcome the triggers accept, and convergence then records the graph outcome.
    /// </summary>
    [TestMethod]
    public async Task SqlServerInvalidatingAnchorOfLiveGraphTerminalizesNodeAndConvergesToFailed()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphInvalidateLive_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var (_, raw, _, graphScheduler, workerTelemetry) =
                await ScheduleSingleNodePreviewGraphAsync(context, now, "invalidate-live").ConfigureAwait(false);
            using var telemetry = workerTelemetry;
            var execution = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Include(item => item.Jobs)
                .SingleAsync().ConfigureAwait(false);
            execution.Status.Should().Be(CentralProcessingGraphExecutionStatus.Running);
            execution.Jobs.Single().Status.Should().Be(CentralDerivativeJobStatus.Pending);

            var anchor = await context.CentralArtifacts.SingleAsync(item => item.Id == raw.Id).ConfigureAwait(false);
            anchor.ObjectState = CentralArtifactObjectState.Quarantined;
            anchor.ReconstructionState = CentralReconstructionState.Quarantined;
            anchor.StateReasonCode = "object.integrity-mismatch";
            await FluentActions.Awaiting(() => ArtifactIngestService.InvalidateDependentsAsync(
                    context, anchor, CancellationToken.None))
                .Should().NotThrowAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();

            var node = await context.CentralDerivativeJobs.AsNoTracking()
                .Include(job => job.Inputs)
                .SingleAsync().ConfigureAwait(false);
            node.Status.Should().Be(CentralDerivativeJobStatus.Quarantined);
            node.StateReasonCode.Should().Be(ArtifactIngestService.GraphSourceInvalidatedReason);
            node.LastError.Should().Be("object.integrity-mismatch");
            node.Inputs.Should().ContainSingle(input => input.CentralArtifactId == raw.Id);
            (await context.CentralProcessingGraphExecutions.AsNoTracking().SingleAsync().ConfigureAwait(false))
                .Status.Should().Be(CentralProcessingGraphExecutionStatus.Running, "invalidation never writes the execution row");

            await graphScheduler.ConvergeAsync(execution.Id, now.AddSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var converged = await context.CentralProcessingGraphExecutions.AsNoTracking().SingleAsync().ConfigureAwait(false);
            converged.Status.Should().Be(CentralProcessingGraphExecutionStatus.Failed);
            converged.CompletedAtUtc.Should().NotBeNull();
            (await new CentralDerivativeJobService(context, TimeProvider.System)
                .ClaimNextAsync("invalidate-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false)).Should().BeNull("a terminalized node is never leased");
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A leased graph node that reports its own input unavailable is terminalized by the graph-aware invalidation and
    /// must not be reopened as RetryableFailure by the legacy bulk reopen that follows it; only legacy jobs reopen.
    /// </summary>
    [TestMethod]
    public async Task SqlServerMarkInputUnavailableFromLeasedLiveGraphNodeTerminalizesNodeAndReopensOnlyLegacyJobs()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphInputUnavailableLive_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var (_, raw, _, graphScheduler, workerTelemetry) =
                await ScheduleSingleNodePreviewGraphAsync(context, now, "input-unavailable-live").ConfigureAwait(false);
            using var telemetry = workerTelemetry;
            var execution = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Include(item => item.Jobs)
                .SingleAsync().ConfigureAwait(false);
            var legacy = CreateLegacyCompletedImageQualityJob(raw, now);
            context.Add(legacy);
            await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();

            var service = new CentralDerivativeJobService(context, TimeProvider.System);
            var lease = await service.ClaimNextAsync("input-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            lease.Should().NotBeNull();
            lease!.GraphExecutionId.Should().Be(execution.Id);
            var source = await context.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == raw.Id)
                .ConfigureAwait(false);
            await service.MarkInputUnavailableAsync(
                lease.JobId,
                lease.LeaseToken,
                source.Id,
                source.RowVersion,
                "object.missing",
                quarantine: false,
                CancellationToken.None).ConfigureAwait(false);
            context.ChangeTracker.Clear();

            var node = await context.CentralDerivativeJobs.AsNoTracking()
                .Include(job => job.Attempts)
                .SingleAsync(job => job.Id == lease.JobId).ConfigureAwait(false);
            node.Status.Should().Be(CentralDerivativeJobStatus.TerminalFailure,
                "the legacy bulk reopen must not reset a graph-owned node to RetryableFailure");
            node.StateReasonCode.Should().Be(ArtifactIngestService.GraphSourceInvalidatedReason);
            node.LeaseToken.Should().BeNull();
            node.Attempts.Should().ContainSingle().Which.Outcome.Should().Be(CentralDerivativeAttemptOutcome.TerminalFailure);
            var legacyAfter = await context.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == legacy.Id).ConfigureAwait(false);
            legacyAfter.Status.Should().Be(CentralDerivativeJobStatus.RetryableFailure);

            await graphScheduler.ConvergeAsync(execution.Id, now.AddSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
            context.ChangeTracker.Clear();
            (await context.CentralProcessingGraphExecutions.AsNoTracking().SingleAsync().ConfigureAwait(false))
                .Status.Should().Be(CentralProcessingGraphExecutionStatus.Failed);
            (await service.ClaimNextAsync("input-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false)).Should().BeNull("neither the terminal node nor the unavailable-input legacy job is claimable");
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A legacy lease that reports a shared input unavailable must not reopen the Completed node of a terminal graph
    /// execution: the graph trigger rejects that reopen and would roll back the entire input-unavailable transaction.
    /// </summary>
    [TestMethod]
    public async Task SqlServerMarkInputUnavailableFromLegacyLeaseLeavesTerminalGraphNodeUntouched()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphInputUnavailableTerminal_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var (_, raw, definition, graphScheduler, workerTelemetry) =
                await ScheduleSingleNodePreviewGraphAsync(context, now, "input-unavailable-terminal").ConfigureAwait(false);
            using var telemetry = workerTelemetry;
            var execution = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Include(item => item.Jobs)
                .SingleAsync().ConfigureAwait(false);
            var service = new CentralDerivativeJobService(context, TimeProvider.System);
            var graphLease = await service.ClaimNextAsync("input-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            graphLease!.GraphExecutionId.Should().Be(execution.Id);
            var services = AssemblyHooks.Fixture.Factory.Services;
            await using (var writerContext = new ApplicationDbContext(options))
            {
                var writer = new CentralDerivativeOutputWriter(
                    writerContext,
                    services.GetRequiredService<IObjectStore>(),
                    services.GetRequiredService<ICentralArtifactObjectReader>(),
                    telemetry,
                    TimeProvider.System,
                    NullLogger<CentralDerivativeOutputWriter>.Instance,
                    services.GetRequiredService<CentralObjectStorageNames>());
                await writer.PersistSetAsync(
                    graphLease,
                    definition.Nodes.Single().Outputs.Select(contract => CreateProduct(contract, graphLease, 21)).ToArray(),
                    1,
                    TimeSpan.Zero,
                    CancellationToken.None).ConfigureAwait(false);
            }
            context.ChangeTracker.Clear();
            await graphScheduler.ConvergeAsync(execution.Id, now.AddSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var completedNode = await context.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == graphLease.JobId).ConfigureAwait(false);
            completedNode.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            (await context.CentralProcessingGraphExecutions.AsNoTracking().SingleAsync().ConfigureAwait(false))
                .Status.Should().Be(CentralProcessingGraphExecutionStatus.Completed);

            var legacy = CreateLegacyCompletedImageQualityJob(raw, now);
            legacy.Status = CentralDerivativeJobStatus.Pending;
            legacy.AttemptCount = 0;
            legacy.CompletedAtUtc = null;
            legacy.AvailableAtUtc = now.AddMinutes(-1);
            context.Add(legacy);
            await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var legacyLease = await service.ClaimNextAsync("legacy-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            legacyLease.Should().NotBeNull();
            legacyLease!.JobId.Should().Be(legacy.Id);
            var source = await context.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == raw.Id)
                .ConfigureAwait(false);
            await FluentActions.Awaiting(() => service.MarkInputUnavailableAsync(
                    legacyLease.JobId,
                    legacyLease.LeaseToken,
                    source.Id,
                    source.RowVersion,
                    "object.missing",
                    quarantine: false,
                    CancellationToken.None))
                .Should().NotThrowAsync("graph-owned nodes of a terminal execution are excluded from the legacy reopen")
                .ConfigureAwait(false);
            context.ChangeTracker.Clear();

            var nodeAfter = await context.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == graphLease.JobId).ConfigureAwait(false);
            nodeAfter.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            nodeAfter.UpdatedAtUtc.Should().Be(completedNode.UpdatedAtUtc);
            var legacyAfter = await context.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == legacy.Id).ConfigureAwait(false);
            legacyAfter.Status.Should().Be(CentralDerivativeJobStatus.RetryableFailure);
            legacyAfter.LeaseToken.Should().BeNull();
            (await context.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == raw.Id).ConfigureAwait(false))
                .ObjectState.Should().Be(CentralArtifactObjectState.Pending, "the input-unavailable transaction committed");
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static CentralDerivativeJob CreateLegacyCompletedImageQualityJob(CentralArtifact source, DateTimeOffset now)
        => CreateLegacyCompletedJob(source, now, BuiltInProcessingRecipes.ImageQuality);

    private static CentralDerivativeJob CreateLegacyCompletedJob(CentralArtifact source, DateTimeOffset now, string recipeName)
    {
        var recipe = new CentralDerivativeRecipeCatalog().GetRequiredRecipes(FrameArtifactRole.Raw)
            .Single(item => string.Equals(item.RecipeName, recipeName, StringComparison.Ordinal));
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = source.Id,
            TargetRole = recipe.TargetRole,
            TargetRecipeVersion = recipe.RecipeVersion,
            TargetVariant = recipe.TargetVariant,
            RecipeName = recipe.RecipeName,
            RecipeOptionsJson = CaptureContractJson.Canonicalize(recipe.Options).GetRawText(),
            InputSelectorJson = CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(recipe.InputSelector)).GetRawText(),
            RequestedRecipeIdentitySha256 = recipe.RequestedRecipeIdentitySha256,
            ExpectedRecipeIdentitySha256 = recipe.RequestedRecipeIdentitySha256,
            RequestIdentitySha256 = CentralDerivativeJobIdentity.CreateRequestIdentity(
                source.DevicePublicId, source.ArtifactId, recipe),
            Status = CentralDerivativeJobStatus.Completed,
            ResolutionCompletedAtUtc = now,
            AttemptCount = 1,
            MaxAttempts = 3,
            CompletedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var requirement = new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Ordinal = 0,
            BindingName = "input",
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            SequenceOffset = 0,
            IsRequired = true,
            SelectorJson = job.InputSelectorJson,
            CompatibilityMode = CentralDerivativeCompatibilityMode.None,
            ExpectedAgentId = source.Frame?.AgentId ?? string.Empty,
            ExpectedRigId = source.Frame?.RigId,
            ExpectedCaptureSequence = source.Frame?.CaptureSequence,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved,
            ResolvedAtUtc = now
        };
        job.InputRequirements.Add(requirement);
        job.Inputs.Add(new CentralDerivativeJobInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = requirement,
            CentralDerivativeJobInputRequirementId = requirement.Id,
            Ordinal = 0,
            CentralArtifactId = source.Id,
            CaptureSequence = source.Frame?.CaptureSequence,
            CompatibilityJson = "{}",
            CompatibilitySha256 = new string('0', 64),
            ByteLength = source.ByteLength,
            SelectedAtUtc = now
        });
        job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs);
        return job;
    }

    /// <summary>Seeds a camera, one Raw source, and a Preview-only graph assignment, then ingests the Raw (graph Created).</summary>
    private static Task<(SeededCamera Camera, CentralArtifact Raw, ProcessingGraphDefinition Definition,
        CentralProcessingGraphScheduler GraphScheduler, CentralDerivativeWorkerTelemetry Telemetry)>
        ScheduleSingleNodePreviewGraphAsync(ApplicationDbContext context, DateTimeOffset now, string slug)
        => ScheduleSingleNodeGraphAsync(context, now, slug, "Preview", sceneProvenanceJson: null);

    /// <summary>
    /// Seeds a camera (optionally with frame scene provenance), one Raw source, and a single-node graph assignment
    /// taken from the canonical basic graph, then ingests the Raw (graph Created).
    /// </summary>
    private static async Task<(SeededCamera Camera, CentralArtifact Raw, ProcessingGraphDefinition Definition,
        CentralProcessingGraphScheduler GraphScheduler, CentralDerivativeWorkerTelemetry Telemetry)>
        ScheduleSingleNodeGraphAsync(
            ApplicationDbContext context,
            DateTimeOffset now,
            string slug,
            string nodeId,
            string? sceneProvenanceJson)
    {
        var camera = await SeedCameraAsync(context, now, slug).ConfigureAwait(false);
        if (sceneProvenanceJson is not null)
        {
            camera.Frame.SceneProvenanceJson = sceneProvenanceJson;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        var recipeCatalog = new CentralDerivativeRecipeCatalog();
        var registry = new CentralProcessingGraphNodeRegistry(recipeCatalog);
        var basic = DatabaseSeeder.CreateBasicCentralProcessingGraph(recipeCatalog);
        var definition = basic with
        {
            Name = $"sql-{nodeId}-{slug}",
            Nodes = [.. basic.Nodes.Where(node => node.Id == nodeId)]
        };
        var raw = CreateSourceArtifact(camera, FrameArtifactRole.Raw, 'A', now.AddMinutes(-1));
        context.AddRange(raw, CreateAssignment(definition, registry, camera, now));
        await context.SaveChangesAsync().ConfigureAwait(false);
        context.ChangeTracker.Clear();
        var workerTelemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var catalog = new ProcessingGraphCatalogService(
            context, registry, TimeProvider.System, catalogTelemetry, NullLogger<ProcessingGraphCatalogService>.Instance);
        var graphScheduler = new CentralProcessingGraphScheduler(
            context, catalog, registry, new NoopWindowResolver(), new UnusedObjectReader(),
            workerTelemetry, TimeProvider.System);
        var jobScheduler = new CentralDerivativeJobScheduler(
            context, recipeCatalog, new NoopWindowResolver(), graphScheduler: graphScheduler);
        var artifact = await context.CentralArtifacts
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .SingleAsync(item => item.Id == raw.Id).ConfigureAwait(false);
        await jobScheduler.EnsureRequiredJobsAsync(artifact, now, CancellationToken.None).ConfigureAwait(false);
        context.ChangeTracker.Clear();
        (await context.CentralDerivativeJobs.AsNoTracking().CountAsync(job => job.GraphExecutionId == null)
            .ConfigureAwait(false)).Should().Be(0);
        return (camera, raw, definition, graphScheduler, workerTelemetry);
    }

    [TestMethod]
    public async Task SqlServerNodeAndExecutionTerminalizeInOneConvergencePass()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphTerminalPass_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("""
                ALTER TABLE [CentralProcessingGraphExecutions] NOCHECK CONSTRAINT ALL;
                ALTER TABLE [CentralDerivativeJobs] NOCHECK CONSTRAINT ALL;
                ALTER TABLE [CentralDerivativeJobDependencies] NOCHECK CONSTRAINT ALL;
                """).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var executionId = Guid.NewGuid();
            var failedProducerId = Guid.NewGuid();
            var waitingConsumerId = Guid.NewGuid();
            const string EmptyJson = "{}";
            // A sealed Running execution: node 0 already failed terminally, node 1 still waits on node 0's outcome.
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralProcessingGraphExecutions]
                    ([Id], [ExecutionClass], [Status], [RequestIdentitySha256], [RevisionId],
                     [DefinitionIdentitySha256], [FrozenDefinitionJson], [CentralPlanIdentitySha256],
                     [FrozenCentralPlanJson], [ExpectedSourceCount], [ExpectedNodeCount],
                     [ExpectedDependencyCount], [ExpectedOutputCount], [ExpandedAtUtc], [ObservatoryId],
                     [LogicalCameraId], [LogicalCameraInstallationId], [InstallationPublicId],
                     [AnchorSourceCentralArtifactId], [AnchorSourceArtifactId], [AnchorSourceChecksumSha256],
                     [Trigger], [ActorId], [IdempotencyKey], [ReasonCode], [CreatedAtUtc], [UpdatedAtUtc])
                VALUES
                    ({executionId}, N'Replay', N'Pending', {new string('A', 64)}, {Guid.NewGuid()},
                     {new string('B', 64)}, {EmptyJson}, {new string('C', 64)}, {EmptyJson}, 0, 2, 1, 0, NULL,
                     {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()},
                     {Guid.NewGuid()}, {new string('D', 64)}, N'Replay', N'operator',
                     {Guid.NewGuid().ToString("N")}, N'test', {now}, {now});

                INSERT INTO [CentralDerivativeJobs]
                    ([Id], [SourceCentralArtifactId], [TargetRole], [TargetRecipeVersion], [TargetVariant],
                     [RecipeName], [RecipeOptionsJson], [InputSelectorJson], [RequestedRecipeIdentitySha256],
                     [ExpectedRecipeIdentitySha256], [RequestIdentitySha256], [GraphExecutionId], [GraphNodeId],
                     [GraphNodeOrdinal], [SharedNodePlanIdentitySha256], [FrozenNodePlanJson], [GraphFailurePolicy],
                     [Status], [StateReasonCode], [LastError], [LastFailedAtUtc], [AttemptCount], [MaxAttempts],
                     [CompletedAtUtc], [CreatedAtUtc], [UpdatedAtUtc])
                VALUES
                    ({failedProducerId}, {Guid.NewGuid()}, N'Preview', N'recipe-v1', N'graph-a', N'preview', {EmptyJson},
                     {EmptyJson}, {new string('E', 64)}, {new string('E', 64)}, {new string('F', 64)},
                     {executionId}, N'producer', 0, {new string('1', 64)}, {EmptyJson}, N'Required',
                     N'TerminalFailure', N'processing.recipe-failed', N'recipe', {now}, 1, 1, {now}, {now}, {now});

                INSERT INTO [CentralDerivativeJobs]
                    ([Id], [SourceCentralArtifactId], [TargetRole], [TargetRecipeVersion], [TargetVariant],
                     [RecipeName], [RecipeOptionsJson], [InputSelectorJson], [RequestedRecipeIdentitySha256],
                     [ExpectedRecipeIdentitySha256], [RequestIdentitySha256], [GraphExecutionId], [GraphNodeId],
                     [GraphNodeOrdinal], [SharedNodePlanIdentitySha256], [FrozenNodePlanJson], [GraphFailurePolicy],
                     [Status], [WaitKind], [StateReasonCode], [ResolutionStartedAtUtc], [AttemptCount], [MaxAttempts],
                     [CreatedAtUtc], [UpdatedAtUtc])
                VALUES
                    ({waitingConsumerId}, {Guid.NewGuid()}, N'Preview', N'recipe-v1', N'graph-b', N'preview', {EmptyJson},
                     {EmptyJson}, {new string('E', 64)}, {new string('E', 64)}, {new string('7', 64)},
                     {executionId}, N'consumer', 1, {new string('2', 64)}, {EmptyJson}, N'Required',
                     N'Waiting', N'Dependencies', N'processing.graph.waiting-dependencies', {now}, 0, 1, {now}, {now});

                INSERT INTO [CentralDerivativeJobDependencies]
                    ([Id], [ExecutionId], [ConsumerJobId], [Ordinal], [ProducerJobId], [ProducerOutputOrdinal],
                     [ConsumerInputOrdinal], [ConsumerBindingName], [ConsumerBindingKind], [Kind], [Required])
                VALUES
                    ({Guid.NewGuid()}, {executionId}, {waitingConsumerId}, 0, {failedProducerId}, NULL, NULL, NULL,
                     NULL, N'Outcome', 1);

                UPDATE [CentralProcessingGraphExecutions]
                SET [ExpandedAtUtc] = {now}
                WHERE [Id] = {executionId};

                UPDATE [CentralProcessingGraphExecutions]
                SET [Status] = N'Running', [StartedAtUtc] = {now}, [UpdatedAtUtc] = {now}
                WHERE [Id] = {executionId};
                """).ConfigureAwait(false);

            // One convergence pass terminalizes the consumer (required predecessor failed) and the execution
            // (Failed). The triggers accept this only when the node row is terminal before the execution row is,
            // which the scheduler guarantees explicitly rather than through EF's incidental statement ordering.
            using var workerTelemetry = new CentralDerivativeWorkerTelemetry();
            using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
            var registry = new CentralProcessingGraphNodeRegistry(new CentralDerivativeRecipeCatalog());
            var converged = now.AddSeconds(1);
            await using (var recoveryContext = new ApplicationDbContext(options))
            {
                var catalog = new ProcessingGraphCatalogService(
                    recoveryContext, registry, TimeProvider.System, catalogTelemetry,
                    NullLogger<ProcessingGraphCatalogService>.Instance);
                await new CentralProcessingGraphScheduler(
                        recoveryContext, catalog, registry, new NoopWindowResolver(), new UnusedObjectReader(),
                        workerTelemetry, TimeProvider.System)
                    .ConvergeAsync(executionId, converged, CancellationToken.None).ConfigureAwait(false);
            }

            var execution = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(item => item.Id == executionId).ConfigureAwait(false);
            execution.Status.Should().Be(CentralProcessingGraphExecutionStatus.Failed);
            execution.CompletedAtUtc.Should().Be(converged);
            var consumer = await context.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(item => item.Id == waitingConsumerId).ConfigureAwait(false);
            consumer.Status.Should().Be(CentralDerivativeJobStatus.TerminalFailure);
            consumer.StateReasonCode.Should().Be("processing.graph.required-predecessor-failed");
            consumer.CompletedAtUtc.Should().Be(converged);
            workerTelemetry.HasRecentDependencyFailure(converged, TimeSpan.FromMinutes(1)).Should().BeFalse();
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A deterministic output already produced by the legacy (non-graph) scheduler carries no graph contract
    /// identity. A graph job whose frozen slot the durable evidence satisfies adopts it end to end: the C#
    /// validation admits it, <c>TR_CentralDerivativeJobOutputs_BindOnce</c> binds the slot, and the execution
    /// completes without a second artifact for the same output identity.
    /// </summary>
    [TestMethod]
    public async Task SqlServerGraphJobAdoptsLegacyDeterministicEvidenceThroughBindingTrigger()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphLegacyAdoption_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var (camera, raw, definition, graphScheduler, workerTelemetry) =
                await ScheduleSingleNodePreviewGraphAsync(context, now, "legacy-adoption").ConfigureAwait(false);
            using var _ = workerTelemetry;
            var lease = await new CentralDerivativeJobService(context, TimeProvider.System)
                .ClaimNextAsync("adoption-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            lease.Should().NotBeNull();
            lease!.GraphExecutionId.Should().NotBeNull();
            lease.InputSetIdentitySha256.Should().HaveLength(64);
            var contract = definition.Nodes.Single().Outputs.Single();
            var product = CreateProduct(contract, lease, 5);
            var services = AssemblyHooks.Fixture.Factory.Services;
            var storageNames = services.GetRequiredService<CentralObjectStorageNames>();

            // The legacy scheduler already produced exactly this deterministic output for the same frozen input set.
            var source = await context.CentralArtifacts.Include(item => item.Frame)
                .SingleAsync(item => item.Id == raw.Id).ConfigureAwait(false);
            var legacyJob = CreateLegacyCompletedJob(source, now.AddMinutes(-1), BuiltInProcessingRecipes.EncodedPreview);
            legacyJob.Inputs.Single().CompatibilitySha256 = lease.Inputs!.Single().CompatibilitySha256;
            legacyJob.Inputs.Single().CaptureSequence = lease.Inputs!.Single().CaptureSequence;
            legacyJob.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(legacyJob.Inputs);
            legacyJob.InputSetIdentitySha256.Should().Be(lease.InputSetIdentitySha256,
                "the legacy job froze the same single-input set the graph node froze");
            legacyJob.RequestedRecipeIdentitySha256.Should().Be(lease.RequestedRecipeIdentitySha256);
            var legacyArtifact = new CentralArtifact
            {
                CentralFrameId = source.CentralFrameId,
                DevicePublicId = source.DevicePublicId,
                ArtifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
                Role = product.Role,
                Variant = product.Variant,
                RecipeVersion = lease.TargetRecipeVersion,
                ManifestSchemaVersion = "central-v1",
                MediaType = product.MediaType,
                ByteLength = product.Payload.Length,
                ChecksumSha256 = product.ChecksumSha256,
                StorageReference = CentralDerivativeOutputWriter.CreateStorageReference(
                    lease, product, storageNames.ArtifactBucket),
                ReceivedAtUtc = now.AddMinutes(-1),
                IdempotencyKey = CentralDerivativeOutputWriter.CreateArtifactIdempotencyKey(
                    source.DevicePublicId, product.OutputIdentitySha256),
                SourceId = "central-derivative-worker",
                CreatedUtc = now.AddMinutes(-1),
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete,
                Recipe = new CentralArtifactRecipe
                {
                    Name = product.Recipe.Descriptor.Name,
                    SemanticVersion = product.Recipe.Descriptor.SemanticVersion,
                    ImplementationVersion = product.Recipe.Descriptor.ImplementationVersion,
                    OptionsJson = CaptureContractJson.Canonicalize(product.Recipe.Descriptor.Options).GetRawText(),
                    OptionsSha256 = product.Recipe.Descriptor.OptionsSha256
                }
            };
            legacyArtifact.Sources.Add(new CentralArtifactSource
            {
                Ordinal = 0,
                SourceArtifactId = source.ArtifactId,
                ResolvedCentralArtifactId = source.Id
            });
            legacyJob.ResultCentralArtifactId = legacyArtifact.Id;
            var legacyEvidence = new CentralArtifactProcessingEvidence
            {
                CentralArtifactId = legacyArtifact.Id,
                Artifact = legacyArtifact,
                DevicePublicId = source.DevicePublicId,
                OutputIdentitySha256 = product.OutputIdentitySha256,
                RequestedRecipeIdentitySha256 = legacyJob.RequestedRecipeIdentitySha256,
                RecipeIdentitySha256 = product.Recipe.IdentitySha256,
                RecipeOperationKind = product.Recipe.OperationKind,
                GraphProductContractIdentitySha256 = null,
                ProductKind = product.Kind,
                ProductSchemaVersion = product.SchemaVersion,
                ProductMediaType = product.MediaType,
                AlgorithmsJson = CaptureContractJson.Canonicalize(
                    CaptureContractJson.SerializeToElement(product.Algorithms)).GetRawText(),
                CompatibilityJson = CaptureContractJson.Canonicalize(
                    CaptureContractJson.SerializeToElement(product.Compatibility)).GetRawText(),
                TotalIntegrationTicks = 0,
                CentralDerivativeJobId = legacyJob.Id,
                Job = legacyJob,
                AttemptNumber = 1,
                CreatedAtUtc = now.AddMinutes(-1)
            };
            context.AddRange(legacyJob, legacyArtifact, legacyEvidence);
            await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();

            await using (var writerContext = new ApplicationDbContext(options))
            {
                var writer = new CentralDerivativeOutputWriter(
                    writerContext,
                    services.GetRequiredService<IObjectStore>(),
                    services.GetRequiredService<ICentralArtifactObjectReader>(),
                    workerTelemetry,
                    TimeProvider.System,
                    NullLogger<CentralDerivativeOutputWriter>.Instance,
                    storageNames);
                var adopted = await writer.PersistSetAsync(lease, [product], 1, TimeSpan.Zero, CancellationToken.None)
                    .ConfigureAwait(false);
                adopted.Should().Equal(legacyArtifact.ArtifactId);
            }
            context.ChangeTracker.Clear();
            var graphJob = await context.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.Outputs)
                .SingleAsync(item => item.Id == lease.JobId).ConfigureAwait(false);
            graphJob.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            graphJob.ResultCentralArtifactId.Should().Be(legacyArtifact.Id);
            var slot = graphJob.Outputs.Single();
            slot.ResultCentralArtifactId.Should().Be(legacyArtifact.Id, "the slot binds the adopted legacy artifact");
            slot.ResultOutputIdentitySha256.Should().Be(product.OutputIdentitySha256);
            slot.BoundAtUtc.Should().NotBeNull();
            (await context.CentralArtifacts.AsNoTracking().CountAsync(item =>
                item.DevicePublicId == source.DevicePublicId && item.ArtifactId == legacyArtifact.ArtifactId)
                .ConfigureAwait(false)).Should().Be(1, "adoption never duplicates the deterministic output");
            var evidence = await context.CentralArtifactProcessingEvidence.AsNoTracking()
                .SingleAsync(item => item.CentralArtifactId == legacyArtifact.Id).ConfigureAwait(false);
            evidence.GraphProductContractIdentitySha256.Should().BeNull("legacy evidence is immutable and stays untagged");
            evidence.CentralDerivativeJobId.Should().Be(legacyJob.Id);
            (await context.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == legacyJob.Id)
                .ConfigureAwait(false)).Status.Should().Be(CentralDerivativeJobStatus.Completed);

            await graphScheduler.ConvergeAsync(lease.GraphExecutionId!.Value, now.AddSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
            context.ChangeTracker.Clear();
            (await context.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(item => item.Id == lease.GraphExecutionId).ConfigureAwait(false)).Status
                .Should().Be(CentralProcessingGraphExecutionStatus.Completed);

            // Trigger parity: untagged evidence binds only when its job is a legacy (non-graph) job.
            var outputTrigger = await context.Database.SqlQuery<string>($"""
                SELECT OBJECT_DEFINITION(OBJECT_ID(N'TR_CentralDerivativeJobOutputs_BindOnce')) AS [Value]
                """).SingleAsync().ConfigureAwait(false);
            outputTrigger.Should().Contain("evidence.[GraphProductContractIdentitySha256] IS NULL");
            outputTrigger.Should().Contain("evidence_job.[GraphExecutionId] IS NULL");
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A frozen output contract that omits <c>algorithms</c> leaves the product's algorithm set unconstrained:
    /// evidence carrying a non-empty algorithm set passes the evidence contract trigger, the C# binder, and
    /// <c>TR_CentralDerivativeJobOutputs_BindOnce</c>, and the slot binds.
    /// </summary>
    [TestMethod]
    public async Task SqlServerEvidenceWithAlgorithmsBindsToSlotWhoseContractOmitsAlgorithms()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphOpenAlgorithms_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var (_, _, definition, graphScheduler, workerTelemetry) =
                await ScheduleSingleNodePreviewGraphAsync(context, now, "open-algorithms").ConfigureAwait(false);
            using var _ = workerTelemetry;

            // Rewrite the frozen slot so its contract omits the algorithms property entirely. Slot contracts are
            // immutable once expansion is sealed, so this test-only setup bypasses the binding trigger.
            var frozenSlot = await context.CentralDerivativeJobOutputs.AsNoTracking().SingleAsync().ConfigureAwait(false);
            frozenSlot.ContractJson.Should().Contain("\"algorithms\":[]");
            var openContractJson = frozenSlot.ContractJson.Replace("\"algorithms\":[],", string.Empty, StringComparison.Ordinal);
            openContractJson.Should().NotContain("algorithms");
            var openContractIdentity = CentralDerivativeJobOutput.ComputeContractIdentitySha256(openContractJson);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                DISABLE TRIGGER [TR_CentralDerivativeJobOutputs_BindOnce] ON [CentralDerivativeJobOutputs];
                UPDATE [CentralDerivativeJobOutputs]
                SET [ContractJson] = {openContractJson}, [ContractIdentitySha256] = {openContractIdentity}
                WHERE [Id] = {frozenSlot.Id};
                ENABLE TRIGGER [TR_CentralDerivativeJobOutputs_BindOnce] ON [CentralDerivativeJobOutputs];
                """).ConfigureAwait(false);
            (await context.Database.SqlQuery<string?>($"""
                SELECT JSON_QUERY([ContractJson], '$.algorithms') AS [Value]
                FROM [CentralDerivativeJobOutputs]
                WHERE [Id] = {frozenSlot.Id}
                """).SingleAsync().ConfigureAwait(false)).Should().BeNull();

            var lease = await new CentralDerivativeJobService(context, TimeProvider.System)
                .ClaimNextAsync("open-algorithms-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            lease.Should().NotBeNull();
            lease!.GraphExecutionId.Should().NotBeNull();
            var contract = definition.Nodes.Single().Outputs.Single();
            var product = CreateProduct(contract, lease, 9) with
            {
                Algorithms =
                [
                    new ProcessingAlgorithmIdentity("stretch", "2"),
                    new ProcessingAlgorithmIdentity("encode", "3")
                ]
            };
            var services = AssemblyHooks.Fixture.Factory.Services;
            var storageNames = services.GetRequiredService<CentralObjectStorageNames>();

            await using (var writerContext = new ApplicationDbContext(options))
            {
                var writer = new CentralDerivativeOutputWriter(
                    writerContext,
                    services.GetRequiredService<IObjectStore>(),
                    services.GetRequiredService<ICentralArtifactObjectReader>(),
                    workerTelemetry,
                    TimeProvider.System,
                    NullLogger<CentralDerivativeOutputWriter>.Instance,
                    storageNames);
                var written = await writer.PersistSetAsync(lease, [product], 1, TimeSpan.Zero, CancellationToken.None)
                    .ConfigureAwait(false);
                written.Should().ContainSingle();
            }
            context.ChangeTracker.Clear();

            var graphJob = await context.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.Outputs)
                .SingleAsync(item => item.Id == lease.JobId).ConfigureAwait(false);
            graphJob.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            var slot = graphJob.Outputs.Single();
            slot.ContractIdentitySha256.Should().Be(openContractIdentity);
            slot.ResultCentralArtifactId.Should().Be(graphJob.ResultCentralArtifactId);
            slot.ResultOutputIdentitySha256.Should().Be(product.OutputIdentitySha256);
            slot.BoundAtUtc.Should().NotBeNull();
            var evidence = await context.CentralArtifactProcessingEvidence.AsNoTracking()
                .SingleAsync(item => item.CentralArtifactId == slot.ResultCentralArtifactId).ConfigureAwait(false);
            evidence.GraphProductContractIdentitySha256.Should().Be(openContractIdentity);
            evidence.AlgorithmsJson.Should().Be(CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(product.Algorithms)).GetRawText());
            evidence.AlgorithmsJson.Should().NotBe("[]", "the bound evidence carries a non-empty algorithm set");

            await graphScheduler.ConvergeAsync(lease.GraphExecutionId!.Value, now.AddSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
            context.ChangeTracker.Clear();
            (await context.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(item => item.Id == lease.GraphExecutionId).ConfigureAwait(false)).Status
                .Should().Be(CentralProcessingGraphExecutionStatus.Completed);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The annotation decision is frozen at expansion. A frame without scene provenance at expansion freezes "no
    /// annotation": provenance enriched before the lease is withheld from the executor and evidence carrying the
    /// frozen (requested) identity binds through the SQL triggers. A frame annotated at expansion freezes the
    /// annotated identity, the lease carries the provenance, and annotated evidence binds.
    /// </summary>
    [TestMethod]
    public async Task SqlServerAnnotationNodeExecutesAgainstFrozenSceneProvenanceDecision()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphFrozenAnnotation_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var provenanceJson = JsonSerializer.Serialize(
                new SceneProvenance(
                    "scene-1", "rig-v1", "catalog", "1", new string('A', 64), "model", "1", "1", "1",
                    Objects: [new ProjectedObjectProvenance("star:1", "Vega", 10, 12, 0.03)]),
                JsonSerializerOptions.Web);
            var services = AssemblyHooks.Fixture.Factory.Services;
            var jobService = new CentralDerivativeJobService(context, TimeProvider.System);

            // Absent at expansion, enriched before the lease.
            var (absentCamera, _, definition, graphScheduler, workerTelemetry) = await ScheduleSingleNodeGraphAsync(
                context, now, "annotation-absent", "Annotation", sceneProvenanceJson: null).ConfigureAwait(false);
            using var _ = workerTelemetry;
            var absentJob = await context.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(item => item.SourceArtifact!.CentralFrameId == absentCamera.Frame.Id).ConfigureAwait(false);
            absentJob.ExpectedRecipeIdentitySha256.Should().Be(absentJob.RequestedRecipeIdentitySha256,
                "no provenance at expansion freezes the requested identity");
            await context.CentralFrames.Where(frame => frame.Id == absentCamera.Frame.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(frame => frame.SceneProvenanceJson, provenanceJson))
                .ConfigureAwait(false);
            var absentLease = await jobService.ClaimNextAsync("annotation-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            absentLease.Should().NotBeNull();
            absentLease!.JobId.Should().Be(absentJob.Id);
            absentLease.SceneProvenanceJson.Should().BeNull("the lease carries the frozen absence, not the live frame");
            CentralDerivativeJobExecutor.CreateAnnotation(absentLease.SceneProvenanceJson).Should().BeNull(
                "the executor runs the frozen decision and skips with the missing-annotation reason");
            var contract = definition.Nodes.Single().Outputs.Single();
            var unannotated = CreateProduct(contract, absentLease, 3);
            unannotated.Recipe.IdentitySha256.Should().Be(absentLease.RequestedRecipeIdentitySha256);
            await using (var writerContext = new ApplicationDbContext(options))
            {
                var writer = new CentralDerivativeOutputWriter(
                    writerContext,
                    services.GetRequiredService<IObjectStore>(),
                    services.GetRequiredService<ICentralArtifactObjectReader>(),
                    workerTelemetry,
                    TimeProvider.System,
                    NullLogger<CentralDerivativeOutputWriter>.Instance,
                    services.GetRequiredService<CentralObjectStorageNames>());
                await writer.PersistSetAsync(absentLease, [unannotated], 1, TimeSpan.Zero, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            context.ChangeTracker.Clear();
            var absentCompleted = await context.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.Outputs)
                .SingleAsync(item => item.Id == absentJob.Id).ConfigureAwait(false);
            absentCompleted.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            absentCompleted.Outputs.Single().ResultOutputIdentitySha256.Should().Be(unannotated.OutputIdentitySha256);
            await graphScheduler.ConvergeAsync(absentJob.GraphExecutionId!.Value, now.AddSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
            context.ChangeTracker.Clear();
            (await context.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(item => item.Id == absentJob.GraphExecutionId).ConfigureAwait(false)).Status
                .Should().Be(CentralProcessingGraphExecutionStatus.Completed);

            // Present at expansion: the frozen identity includes the annotation and the lease carries the provenance.
            var (presentCamera, _, presentDefinition, presentScheduler, presentTelemetry) = await ScheduleSingleNodeGraphAsync(
                context, now, "annotation-present", "Annotation", provenanceJson).ConfigureAwait(false);
            using var __ = presentTelemetry;
            var presentJob = await context.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(item => item.SourceArtifact!.CentralFrameId == presentCamera.Frame.Id).ConfigureAwait(false);
            var annotationNode = presentDefinition.Nodes.Single();
            var annotatedIdentity = BuiltInProcessingRecipes.CreateExecutionIdentity(
                BuiltInProcessingRecipes.Annotation,
                annotationNode.EffectiveOptions,
                ProcessingInputSelector.Raw(),
                CentralDerivativeJobExecutor.CreateAnnotation(provenanceJson)).IdentitySha256;
            presentJob.ExpectedRecipeIdentitySha256.Should().Be(annotatedIdentity);
            presentJob.ExpectedRecipeIdentitySha256.Should().NotBe(presentJob.RequestedRecipeIdentitySha256);
            var presentLease = await jobService.ClaimNextAsync("annotation-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            presentLease.Should().NotBeNull();
            presentLease!.JobId.Should().Be(presentJob.Id);
            presentLease.SceneProvenanceJson.Should().Be(provenanceJson);
            var annotated = CreateProduct(annotationNode.Outputs.Single(), presentLease, 4);
            annotated.Recipe.IdentitySha256.Should().Be(annotatedIdentity);
            await using (var writerContext = new ApplicationDbContext(options))
            {
                var writer = new CentralDerivativeOutputWriter(
                    writerContext,
                    services.GetRequiredService<IObjectStore>(),
                    services.GetRequiredService<ICentralArtifactObjectReader>(),
                    presentTelemetry,
                    TimeProvider.System,
                    NullLogger<CentralDerivativeOutputWriter>.Instance,
                    services.GetRequiredService<CentralObjectStorageNames>());
                await writer.PersistSetAsync(presentLease, [annotated], 1, TimeSpan.Zero, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            context.ChangeTracker.Clear();
            var presentCompleted = await context.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.Outputs)
                .SingleAsync(item => item.Id == presentJob.Id).ConfigureAwait(false);
            presentCompleted.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            presentCompleted.Outputs.Single().ResultOutputIdentitySha256.Should().Be(annotated.OutputIdentitySha256);
            await presentScheduler.ConvergeAsync(presentJob.GraphExecutionId!.Value, now.AddSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
            context.ChangeTracker.Clear();
            (await context.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(item => item.Id == presentJob.GraphExecutionId).ConfigureAwait(false)).Status
                .Should().Be(CentralProcessingGraphExecutionStatus.Completed);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A cancellation committed after the renewal pre-read but before the authoritative lease update must not extend
    /// the lease: the update predicate itself carries the cancellation guard and the renewal fails as canceled.
    /// </summary>
    [TestMethod]
    public async Task SqlServerLeaseRenewalRejectsCancellationCommittedAfterPreRead()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphRenewalRace_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var (_, _, _, _, workerTelemetry) =
                await ScheduleSingleNodePreviewGraphAsync(context, now, "renewal-race").ConfigureAwait(false);
            using var _ = workerTelemetry;
            var lease = await new CentralDerivativeJobService(context, TimeProvider.System)
                .ClaimNextAsync("renewal-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            lease.Should().NotBeNull();
            var expiresBefore = lease!.LeaseExpiresAtUtc;

            var interceptor = new CancelBeforeLeaseUpdateInterceptor(builder.ConnectionString, lease.JobId);
            var racedOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(builder.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .AddInterceptors(interceptor)
                .Options;
            await using (var racedContext = new ApplicationDbContext(racedOptions))
            {
                var renew = async () => await new CentralDerivativeJobService(racedContext, TimeProvider.System)
                    .RenewLeaseAsync(lease.JobId, lease.LeaseToken, TimeSpan.FromMinutes(5), CancellationToken.None)
                    .ConfigureAwait(false);
                await renew.Should().ThrowExactlyAsync<CentralDerivativeLeaseCanceledException>().ConfigureAwait(false);
            }
            interceptor.Triggered.Should().BeTrue("the cancellation was committed between the pre-read and the update");
            context.ChangeTracker.Clear();
            var job = await context.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(item => item.Id == lease.JobId).ConfigureAwait(false);
            job.CancellationRequestedAtUtc.Should().NotBeNull();
            job.Status.Should().Be(CentralDerivativeJobStatus.Leased);
            job.LeaseExpiresAtUtc.Should().Be(expiresBefore, "a raced cancellation never extends the lease");
            (await context.CentralDerivativeJobAttempts.AsNoTracking()
                .SingleAsync(item => item.CentralDerivativeJobId == lease.JobId && item.AttemptNumber == lease.AttemptCount)
                .ConfigureAwait(false)).LeaseExpiresAtUtc.Should().Be(expiresBefore);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A relational persistence failure during expansion that is not the anticipated uniqueness race surfaces as the
    /// original failure: the race handler owns the rollback and the outer failure handler must not roll back the
    /// completed transaction a second time.
    /// </summary>
    [TestMethod]
    public async Task SqlServerExpansionPersistenceFailureSurfacesWithoutDoubleRollback()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphExpansionFailure_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var camera = await SeedCameraAsync(context, now, "expansion-failure").ConfigureAwait(false);
            var recipeCatalog = new CentralDerivativeRecipeCatalog();
            var registry = new CentralProcessingGraphNodeRegistry(recipeCatalog);
            var basic = DatabaseSeeder.CreateBasicCentralProcessingGraph(recipeCatalog);
            var definition = basic with
            {
                Name = "sql-expansion-failure",
                Nodes = [.. basic.Nodes.Where(node => node.Id == "Preview")]
            };
            var raw = CreateSourceArtifact(camera, FrameArtifactRole.Raw, 'A', now.AddMinutes(-1));
            var assignment = CreateAssignment(definition, registry, camera, now);
            context.AddRange(raw, assignment);
            await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();

            var failingOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(builder.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .AddInterceptors(new ThrowOnSaveChangesInterceptor())
                .Options;
            using var workerTelemetry = new CentralDerivativeWorkerTelemetry();
            using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
            await using (var failingContext = new ApplicationDbContext(failingOptions))
            {
                var catalog = new ProcessingGraphCatalogService(
                    failingContext, registry, TimeProvider.System, catalogTelemetry,
                    NullLogger<ProcessingGraphCatalogService>.Instance);
                var scheduler = new CentralProcessingGraphScheduler(
                    failingContext, catalog, registry, new NoopWindowResolver(), new UnusedObjectReader(),
                    workerTelemetry, TimeProvider.System);
                var replay = async () => await scheduler.ScheduleReplayAsync(
                    new(assignment.RevisionId, [raw.Id], camera.Owner.Id, "sql-expansion-failure", "sql-test"),
                    now,
                    CancellationToken.None).ConfigureAwait(false);
                await replay.Should().ThrowExactlyAsync<DbUpdateException>()
                    .WithMessage("Injected expansion persistence failure.").ConfigureAwait(false);
            }
            context.ChangeTracker.Clear();
            (await context.CentralProcessingGraphExecutions.AsNoTracking().CountAsync().ConfigureAwait(false))
                .Should().Be(0, "the failed expansion left no execution behind");
            (await context.CentralDerivativeJobs.AsNoTracking().CountAsync().ConfigureAwait(false)).Should().Be(0);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static CentralProcessingGraphAssignment CreateAssignment(
        ProcessingGraphDefinition definition,
        CentralProcessingGraphNodeRegistry registry,
        SeededCamera camera,
        DateTimeOffset now)
    {
        var portable = ProcessingGraphCompiler.Compile(definition);
        portable.IsValid.Should().BeTrue(string.Join(Environment.NewLine, portable.Diagnostics));
        var central = ProcessingGraphCompiler.Compile(
            definition, new(ProcessingGraphHosts.LogicHost, registry.Capabilities));
        central.IsValid.Should().BeTrue(string.Join(Environment.NewLine, central.Diagnostics));
        registry.Validate(central.Plan!).Should().BeTrue();
        var revision = new CentralProcessingGraphRevision
        {
            Name = definition.Name,
            Revision = definition.Revision,
            DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)),
            DefinitionIdentitySha256 = portable.Plan!.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = portable.Plan.PlanIdentitySha256,
            CentralPlanIdentitySha256 = central.Plan!.PlanIdentitySha256,
            CreatedAtUtc = now.AddMinutes(-1),
            CreatedByUserId = camera.Owner.Id,
            PublishedAtUtc = now.AddSeconds(-1),
            PublishedByUserId = camera.Owner.Id
        };
        var assignment = new CentralProcessingGraphAssignment
        {
            Revision = revision,
            RevisionId = revision.Id,
            TargetHost = CentralProcessingGraphTargetHost.Central,
            Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
            ObservatoryId = camera.Observatory.Id,
            LogicalCameraId = camera.Camera.Id,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now.AddMinutes(-1),
            ActorUserId = camera.Owner.Id,
            ReasonCode = "sql-transient-ownership"
        };
        revision.Assignments.Add(assignment);
        return assignment;
    }

    /// <summary>
    /// Expansion selects its sources without holds and only later persists the execution that retention treats as a
    /// hold. Inside the expansion transaction every source row is fenced with the same UPDLOCK/HOLDLOCK retention's
    /// reservation takes, so a concurrent release must wait for the seal and then observe the Running execution as a
    /// hold instead of expiring a source the execution has already frozen.
    /// </summary>
    [TestMethod]
    public async Task SqlServerExpansionFencesSourceRetentionUntilTheExecutionIsSealed()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphRetentionFence_{Guid.NewGuid():N}"
        };
        var gate = new SourceFenceGateInterceptor(SourceFenceGateStage.AfterSourceRowLock);
        var schedulerOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(gate)
            .Options;
        var plainOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(schedulerOptions);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var (raw, graphScheduler, workerTelemetry) =
                await SeedFencedPreviewGraphAsync(context, now, "fence-seal").ConfigureAwait(false);
            using var _ = workerTelemetry;
            await using var retentionContext = new ApplicationDbContext(plainOptions);
            using var retentionTelemetry = new CentralArtifactRetentionTelemetry();
            var retention = CreateRetentionService(retentionContext, retentionTelemetry);

            var expansion = graphScheduler.ScheduleLiveAsync(raw.Id, now, CancellationToken.None);
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var release = retention.ReleaseAsync(raw.Id, CancellationToken.None);
            var completedFirst = await Task.WhenAny(release, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            completedFirst.Should().NotBeSameAs(release,
                "retention must block on the fenced source row until the expansion transaction completes");
            gate.Release();
            var expanded = await expansion.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var released = await release.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            expanded.Outcome.Should().Be(CentralProcessingGraphScheduleOutcome.Created);
            released.Should().Be(CentralArtifactRetentionResult.Held,
                "once the seal is visible the sealed execution's source reference is an active hold");
            context.ChangeTracker.Clear();
            var execution = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Include(item => item.Sources)
                .SingleAsync().ConfigureAwait(false);
            execution.Status.Should().Be(CentralProcessingGraphExecutionStatus.Running);
            execution.ExpandedAtUtc.Should().NotBeNull();
            execution.Sources.Should().ContainSingle(source => source.CentralArtifactId == raw.Id);
            var artifact = await context.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == raw.Id)
                .ConfigureAwait(false);
            artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available);
            artifact.RetentionDeletionToken.Should().BeNull();
            (await GetFixtureMinio().StatObjectAsync(new StatObjectArgs()
                .WithBucket(FenceBucket)
                .WithObject(raw.StorageReference[FenceBucketPrefix.Length..])).ConfigureAwait(false)).Size
                .Should().Be(raw.ByteLength, "the fenced source object survives the concurrent release");
        }
        finally
        {
            gate.Release();
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A source that retention expires after selection but before the fence is revalidated inside the expansion
    /// transaction: the expansion fails explicitly with <c>source-retention-expired</c> and persists nothing, instead
    /// of sealing an execution whose frozen source object no longer exists.
    /// </summary>
    [TestMethod]
    public async Task SqlServerExpansionRejectsSourceExpiredBetweenSelectionAndFence()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphRetentionExpired_{Guid.NewGuid():N}"
        };
        var gate = new SourceFenceGateInterceptor(SourceFenceGateStage.AfterExecutionIdentityLock);
        var schedulerOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(gate)
            .Options;
        var plainOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(schedulerOptions);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var (raw, graphScheduler, workerTelemetry) =
                await SeedFencedPreviewGraphAsync(context, now, "fence-expired").ConfigureAwait(false);
            using var _ = workerTelemetry;
            await using var retentionContext = new ApplicationDbContext(plainOptions);
            using var retentionTelemetry = new CentralArtifactRetentionTelemetry();
            var retention = CreateRetentionService(retentionContext, retentionTelemetry);

            var expansion = graphScheduler.ScheduleLiveAsync(raw.Id, now, CancellationToken.None);
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            // The source row is not fenced yet, so retention wins the race and expires the selected source.
            var released = await retention.ReleaseAsync(raw.Id, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            gate.Release();
            var expanded = await expansion.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            released.Should().Be(CentralArtifactRetentionResult.Released);
            expanded.Outcome.Should().Be(CentralProcessingGraphScheduleOutcome.Invalid);
            expanded.ReasonCode.Should().Be("source-retention-expired");
            expanded.Execution.Should().BeNull();
            context.ChangeTracker.Clear();
            (await context.CentralProcessingGraphExecutions.AsNoTracking().CountAsync().ConfigureAwait(false))
                .Should().Be(0, "a fenced-out expansion persists no execution, source, or node rows");
            (await context.CentralDerivativeJobs.AsNoTracking().CountAsync().ConfigureAwait(false)).Should().Be(0);
            (await context.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == raw.Id).ConfigureAwait(false))
                .ObjectState.Should().Be(CentralArtifactObjectState.Expired);
        }
        finally
        {
            gate.Release();
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The revision is read without a lock during selection; retirement serializes on the revision row. A retire
    /// committing after selection but before the seal is observed by the in-transaction revision fence, so no
    /// execution is ever sealed against a retired revision.
    /// </summary>
    [TestMethod]
    public async Task SqlServerExpansionRejectsRevisionRetiredBetweenSelectionAndSeal()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphRevisionFence_{Guid.NewGuid():N}"
        };
        var gate = new SourceFenceGateInterceptor(SourceFenceGateStage.AfterSourceRowLock);
        var schedulerOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(gate)
            .Options;
        var plainOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(schedulerOptions);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var (raw, graphScheduler, workerTelemetry) =
                await SeedFencedPreviewGraphAsync(context, now, "revision-fence").ConfigureAwait(false);
            using var _ = workerTelemetry;
            var revisionId = await context.CentralProcessingGraphRevisions.AsNoTracking().Select(item => item.Id)
                .SingleAsync().ConfigureAwait(false);
            await using var catalogContext = new ApplicationDbContext(plainOptions);
            using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
            var catalog = new ProcessingGraphCatalogService(
                catalogContext,
                new CentralProcessingGraphNodeRegistry(new CentralDerivativeRecipeCatalog()),
                TimeProvider.System,
                catalogTelemetry,
                NullLogger<ProcessingGraphCatalogService>.Instance);

            var expansion = graphScheduler.ScheduleLiveAsync(raw.Id, now, CancellationToken.None);
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            // Sources are fenced but the revision is not yet, so the retirement commits ahead of the seal.
            var retired = await catalog.RetireRevisionAsync(
                revisionId, "operator-revision-fence", "superseded", true, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            gate.Release();
            var expanded = await expansion.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            retired.Outcome.Should().Be(CentralProcessingGraphMutationOutcome.Applied);
            expanded.Outcome.Should().Be(CentralProcessingGraphScheduleOutcome.Invalid);
            expanded.ReasonCode.Should().Be("revision-retired");
            context.ChangeTracker.Clear();
            (await context.CentralProcessingGraphExecutions.AsNoTracking().CountAsync().ConfigureAwait(false))
                .Should().Be(0, "no execution is sealed against a revision retired before the seal");
            (await context.CentralDerivativeJobs.AsNoTracking().CountAsync().ConfigureAwait(false)).Should().Be(0);
            (await context.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == raw.Id).ConfigureAwait(false))
                .ObjectState.Should().Be(CentralArtifactObjectState.Available, "the source itself is untouched");
        }
        finally
        {
            gate.Release();
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private const string FenceBucket = "skymonitor-artifacts";
    private const string FenceBucketPrefix = "s3://" + FenceBucket + "/";

    private static IMinioClient GetFixtureMinio()
        => AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();

    /// <summary>
    /// Seeds a camera, a Raw source whose object really exists in the fixture object store (so retention can delete
    /// it), and a single-node Preview assignment, returning a scheduler bound to <paramref name="context"/>.
    /// </summary>
    private static async Task<(CentralArtifact Raw, CentralProcessingGraphScheduler GraphScheduler,
        CentralDerivativeWorkerTelemetry Telemetry)> SeedFencedPreviewGraphAsync(
        ApplicationDbContext context,
        DateTimeOffset now,
        string slug)
    {
        var camera = await SeedCameraAsync(context, now, slug).ConfigureAwait(false);
        var recipeCatalog = new CentralDerivativeRecipeCatalog();
        var registry = new CentralProcessingGraphNodeRegistry(recipeCatalog);
        var basic = DatabaseSeeder.CreateBasicCentralProcessingGraph(recipeCatalog);
        var definition = basic with
        {
            Name = $"sql-fence-{slug}",
            Nodes = [.. basic.Nodes.Where(node => node.Id == "Preview")]
        };
        var payload = new byte[] { 7, 11, 13, 17 };
        var objectKey = $"artifacts/graph-fence/{Guid.NewGuid():N}/{slug}.bin";
        var minio = GetFixtureMinio();
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(FenceBucket)).ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(FenceBucket)).ConfigureAwait(false);
        }
        await using (var stream = new MemoryStream(payload, writable: false))
        {
            await minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(FenceBucket)
                .WithObject(objectKey)
                .WithStreamData(stream)
                .WithObjectSize(payload.Length)
                .WithContentType("application/octet-stream")).ConfigureAwait(false);
        }
        var raw = CreateSourceArtifact(camera, FrameArtifactRole.Raw, 'A', now.AddMinutes(-1));
        raw.ByteLength = payload.Length;
        raw.ChecksumSha256 = Convert.ToHexString(SHA256.HashData(payload));
        raw.StorageReference = FenceBucketPrefix + objectKey;
        context.AddRange(raw, CreateAssignment(definition, registry, camera, now));
        await context.SaveChangesAsync().ConfigureAwait(false);
        context.ChangeTracker.Clear();
        var workerTelemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var catalog = new ProcessingGraphCatalogService(
            context, registry, TimeProvider.System, catalogTelemetry, NullLogger<ProcessingGraphCatalogService>.Instance);
        var graphScheduler = new CentralProcessingGraphScheduler(
            context, catalog, registry, new NoopWindowResolver(), new UnusedObjectReader(),
            workerTelemetry, TimeProvider.System);
        return (raw, graphScheduler, workerTelemetry);
    }

    private static CentralArtifactRetentionService CreateRetentionService(
        ApplicationDbContext db,
        CentralArtifactRetentionTelemetry telemetry)
    {
        var references = new CentralArtifactRetentionReferences(db);
        var processor = new CentralArtifactRetentionProcessor(
            db,
            references,
            ObjectStoreTestClient.Create(GetFixtureMinio()),
            TimeProvider.System,
            telemetry,
            NullLogger<CentralArtifactRetentionProcessor>.Instance);
        return new(
            db,
            references,
            processor,
            TimeProvider.System,
            telemetry,
            NullLogger<CentralArtifactRetentionService>.Instance);
    }

    private enum SourceFenceGateStage
    {
        /// <summary>After the request-identity application lock, before any source row is fenced.</summary>
        AfterExecutionIdentityLock,

        /// <summary>After the first source row UPDLOCK/HOLDLOCK inside the expansion transaction.</summary>
        AfterSourceRowLock
    }

    /// <summary>
    /// Pauses the expansion transaction at a chosen point so a concurrent retention release can be raced against it.
    /// </summary>
    private sealed class SourceFenceGateInterceptor(SourceFenceGateStage stage) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _triggered;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (stage == SourceFenceGateStage.AfterExecutionIdentityLock &&
                command.CommandText.Contains("sp_getapplock", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(parameter =>
                    parameter.Value is string resource &&
                    resource.StartsWith("processing-graph-execution:", StringComparison.Ordinal)))
            {
                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (stage == SourceFenceGateStage.AfterSourceRowLock &&
                command.CommandText.Contains("[CentralArtifacts] WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal))
            {
                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }

        private async Task PauseAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _triggered, 1) != 0)
            {
                return;
            }
            Reached.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task SqlServerCanceledExecutionNeverReclaimsExpiredLeaseAcrossRestart()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphCancelRecovery_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var now = clock.GetUtcNow();
            var camera = await SeedCameraAsync(context, now, "cancel-recovery").ConfigureAwait(false);
            var raw = CreateSourceArtifact(camera, FrameArtifactRole.Raw, 'C', now.AddMinutes(-1));
            var definition = CreatePreviewGraph();
            var portable = ProcessingGraphCompiler.Compile(definition).Plan!;
            var central = ProcessingGraphCompiler.Compile(
                definition, new(ProcessingGraphHosts.LogicHost, [])).Plan!;
            var revision = new CentralProcessingGraphRevision
            {
                Name = definition.Name,
                Revision = definition.Revision,
                DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)),
                DefinitionIdentitySha256 = portable.DefinitionIdentitySha256,
                PortablePlanIdentitySha256 = portable.PlanIdentitySha256,
                CentralPlanIdentitySha256 = central.PlanIdentitySha256,
                CreatedAtUtc = now.AddMinutes(-1),
                CreatedByUserId = camera.Owner.Id,
                PublishedAtUtc = now.AddSeconds(-1),
                PublishedByUserId = camera.Owner.Id
            };
            context.AddRange(raw, revision);
            context.ObservatoryMemberships.Add(new ObservatoryMembership
            {
                ObservatoryId = camera.Observatory.Id,
                UserId = camera.Owner.Id,
                Role = ObservatoryMembershipRole.Owner,
                AddedAtUtc = now.AddDays(-1)
            });
            await context.SaveChangesAsync().ConfigureAwait(false);
            using var workerTelemetry = new CentralDerivativeWorkerTelemetry();
            using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(clock);
            var registry = new MultiOutputNodeRegistry(new CentralDerivativeRecipeCatalog());
            var catalog = new ProcessingGraphCatalogService(
                context, registry, clock, catalogTelemetry, NullLogger<ProcessingGraphCatalogService>.Instance);
            var scheduler = new CentralProcessingGraphScheduler(
                context, catalog, registry, new NoopWindowResolver(), new UnusedObjectReader(), workerTelemetry, clock);
            var scheduled = await scheduler.ScheduleReplayAsync(
                new(revision.Id, [raw.Id], camera.Owner.Id, "cancel-recovery", "sql-test"), now, CancellationToken.None)
                .ConfigureAwait(false);
            scheduled.Outcome.Should().Be(CentralProcessingGraphScheduleOutcome.Created);
            var executionId = scheduled.Execution!.Id;
            context.ChangeTracker.Clear();

            var lease = await new CentralDerivativeJobService(context, clock)
                .ClaimNextAsync("worker-before-restart", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            lease.Should().NotBeNull();
            context.ChangeTracker.Clear();
            var signal = new CentralProcessingGraphConvergenceSignal();
            var cancelOutcome = await new CentralProcessingGraphExecutionService(context, scheduler, signal).CancelAsync(
                executionId, camera.Owner.Id, camera.Observatory.Id, clock.GetUtcNow().AddSeconds(1), CancellationToken.None)
                .ConfigureAwait(false);
            cancelOutcome.Should().Be(CentralProcessingGraphCancellationOutcome.Applied);
            context.ChangeTracker.Clear();

            // Cancellation bounds the active lease: the lease authority refuses the next renewal so the worker cancels
            // its local execution and the lease runs out instead of being renewed forever.
            var leaseBeforeRenewal = await context.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == lease!.JobId).ConfigureAwait(false);
            await FluentActions.Awaiting(() => new CentralDerivativeJobService(context, clock)
                    .RenewLeaseAsync(lease!.JobId, lease.LeaseToken, TimeSpan.FromMinutes(1), CancellationToken.None))
                .Should().ThrowExactlyAsync<CentralDerivativeLeaseCanceledException>().ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var leaseAfterRenewal = await context.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == lease!.JobId).ConfigureAwait(false);
            leaseAfterRenewal.LeaseExpiresAtUtc.Should().Be(leaseBeforeRenewal.LeaseExpiresAtUtc,
                "a refused renewal must not extend the lease");
            leaseAfterRenewal.Status.Should().Be(CentralDerivativeJobStatus.Leased);
            leaseAfterRenewal.CancellationRequestedAtUtc.Should().NotBeNull();
            // Simulate a host restart after the worker's lease expired: fresh contexts, no in-memory state.
            clock.UtcNow = now.AddMinutes(5);
            await using (var restartedContext = new ApplicationDbContext(options))
            {
                var reclaimed = await new CentralDerivativeJobService(restartedContext, clock)
                    .ClaimNextAsync("worker-after-restart", TimeSpan.FromMinutes(1), CancellationToken.None)
                    .ConfigureAwait(false);
                reclaimed.Should().BeNull("a cancel-requested execution must never hand out its expired node lease");
                var leasedJob = await restartedContext.CentralDerivativeJobs.AsNoTracking()
                    .Include(job => job.Attempts)
                    .SingleAsync(job => job.Id == lease!.JobId).ConfigureAwait(false);
                leasedJob.Status.Should().Be(CentralDerivativeJobStatus.Leased);
                leasedJob.CancellationRequestedBy.Should().Be(camera.Owner.Id);
                leasedJob.Attempts.Should().ContainSingle(attempt =>
                    attempt.Outcome == CentralDerivativeAttemptOutcome.Leased);
            }
            await using (var recoveryContext = new ApplicationDbContext(options))
            {
                using var recoveryCatalogTelemetry = new ProcessingGraphCatalogTelemetry(clock);
                var recoveryCatalog = new ProcessingGraphCatalogService(
                    recoveryContext, registry, clock, recoveryCatalogTelemetry,
                    NullLogger<ProcessingGraphCatalogService>.Instance);
                await new CentralProcessingGraphScheduler(
                        recoveryContext, recoveryCatalog, registry, new NoopWindowResolver(), new UnusedObjectReader(),
                        workerTelemetry, clock)
                    .ConvergeBatchAsync(clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            }
            await using (var verifyContext = new ApplicationDbContext(options))
            {
                var execution = await verifyContext.CentralProcessingGraphExecutions.AsNoTracking()
                    .Include(item => item.Jobs).ThenInclude(job => job.Attempts)
                    .SingleAsync(item => item.Id == executionId).ConfigureAwait(false);
                execution.Status.Should().Be(CentralProcessingGraphExecutionStatus.Canceled);
                var job = execution.Jobs.Single();
                job.Status.Should().Be(CentralDerivativeJobStatus.Canceled);
                job.LeaseOwner.Should().BeNull();
                job.LeaseToken.Should().BeNull();
                job.CancellationRequestedBy.Should().Be(camera.Owner.Id);
                job.Attempts.Single().Outcome.Should().Be(CentralDerivativeAttemptOutcome.Canceled);
                (await new CentralDerivativeJobService(verifyContext, clock)
                    .ClaimNextAsync("worker-after-recovery", TimeSpan.FromMinutes(1), CancellationToken.None)
                    .ConfigureAwait(false)).Should().BeNull();
            }
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Concurrent replay requests that reuse one actor/Idempotency-Key tuple with different request identities must
    /// serialize on the tuple and surface the explicit Conflict outcome, never a unique-index database exception.
    /// </summary>
    [TestMethod]
    public async Task SqlServerConcurrentReplaysSharingAnIdempotencyKeyConvergeOnOneExecutionAndOneConflict()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphIdempotencyRace_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var now = clock.GetUtcNow();
            var camera = await SeedCameraAsync(context, now, "idempotency-race").ConfigureAwait(false);
            var raw = CreateSourceArtifact(camera, FrameArtifactRole.Raw, 'C', now.AddMinutes(-1));
            var definition = CreatePreviewGraph();
            var portable = ProcessingGraphCompiler.Compile(definition).Plan!;
            var central = ProcessingGraphCompiler.Compile(
                definition, new(ProcessingGraphHosts.LogicHost, [])).Plan!;
            var revision = new CentralProcessingGraphRevision
            {
                Name = definition.Name,
                Revision = definition.Revision,
                DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)),
                DefinitionIdentitySha256 = portable.DefinitionIdentitySha256,
                PortablePlanIdentitySha256 = portable.PlanIdentitySha256,
                CentralPlanIdentitySha256 = central.PlanIdentitySha256,
                CreatedAtUtc = now.AddMinutes(-1),
                CreatedByUserId = camera.Owner.Id,
                PublishedAtUtc = now.AddSeconds(-1),
                PublishedByUserId = camera.Owner.Id
            };
            context.AddRange(raw, revision);
            await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();
            using var workerTelemetry = new CentralDerivativeWorkerTelemetry();
            using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(clock);
            var registry = new MultiOutputNodeRegistry(new CentralDerivativeRecipeCatalog());

            const int Attempts = 6;
            var contexts = Enumerable.Range(0, Attempts).Select(_ => new ApplicationDbContext(options)).ToArray();
            try
            {
                var schedulers = contexts.Select(candidate => new CentralProcessingGraphScheduler(
                    candidate,
                    new ProcessingGraphCatalogService(
                        candidate, registry, clock, catalogTelemetry, NullLogger<ProcessingGraphCatalogService>.Instance),
                    registry, new NoopWindowResolver(), new UnusedObjectReader(), workerTelemetry, clock)).ToArray();
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var tasks = schedulers.Select(async (scheduler, index) =>
                {
                    await gate.Task.ConfigureAwait(false);
                    // Same actor and Idempotency-Key, different reason: distinct request identities.
                    return await scheduler.ScheduleReplayAsync(
                        new(revision.Id, [raw.Id], camera.Owner.Id, "shared-key", $"reason-{index}"),
                        now,
                        CancellationToken.None).ConfigureAwait(false);
                }).ToArray();
                gate.SetResult();
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);

                results.Count(result => result.Outcome == CentralProcessingGraphScheduleOutcome.Created).Should().Be(1);
                results.Count(result => result.Outcome == CentralProcessingGraphScheduleOutcome.Conflict)
                    .Should().Be(Attempts - 1);
                results.Where(result => result.Outcome == CentralProcessingGraphScheduleOutcome.Conflict)
                    .Should().OnlyContain(result => result.ReasonCode == "idempotency-key-conflict");
            }
            finally
            {
                foreach (var candidate in contexts)
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }
            }
            context.ChangeTracker.Clear();
            (await context.CentralProcessingGraphExecutions.AsNoTracking().CountAsync().ConfigureAwait(false))
                .Should().Be(1);
            Assert.AreNotEqual(
                CentralProcessingGraphScheduler.CreateIdempotencyLockIdentity(
                    CentralProcessingGraphExecutionClass.Replay, camera.Owner.Id, "shared-key"),
                CentralProcessingGraphScheduler.CreateIdempotencyLockIdentity(
                    CentralProcessingGraphExecutionClass.Replay, camera.Owner.Id, "other-key"));
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Concurrent catalog mutations resolve through the advertised outcome contract instead of unique-index or
    /// append-only-trigger database errors: identical creates converge on one row (Applied/Unchanged), conflicting
    /// creates for the same name yield Conflict, and concurrent publish/retire transitions yield one Applied plus Unchanged.
    /// </summary>
    [TestMethod]
    public async Task SqlServerConcurrentCatalogMutationsResolveThroughOutcomeContract()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphCatalogRace_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(clock);
            var registry = new MultiOutputNodeRegistry(new CentralDerivativeRecipeCatalog());
            const int Attempts = 6;

            async Task<CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView>[]> RunAsync(
                Func<ProcessingGraphCatalogService, int, Task<CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView>>> action)
            {
                var contexts = Enumerable.Range(0, Attempts).Select(_ => new ApplicationDbContext(options)).ToArray();
                try
                {
                    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var tasks = contexts.Select(async (candidate, index) =>
                    {
                        var service = new ProcessingGraphCatalogService(
                            candidate, registry, clock, catalogTelemetry, NullLogger<ProcessingGraphCatalogService>.Instance);
                        await gate.Task.ConfigureAwait(false);
                        return await action(service, index).ConfigureAwait(false);
                    }).ToArray();
                    gate.SetResult();
                    return await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                finally
                {
                    foreach (var candidate in contexts)
                    {
                        await candidate.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }

            var definition = CreatePreviewGraph() with { Name = "catalog-race" };
            var identicalCreates = await RunAsync((service, _) =>
                service.CreateRevisionAsync(definition, "editor", true, CancellationToken.None)).ConfigureAwait(false);
            identicalCreates.Count(result => result.Outcome == CentralProcessingGraphMutationOutcome.Applied).Should().Be(1);
            identicalCreates.Should().OnlyContain(result =>
                result.Outcome == CentralProcessingGraphMutationOutcome.Applied ||
                result.Outcome == CentralProcessingGraphMutationOutcome.Unchanged);
            var revisionId = identicalCreates.Select(result => result.Value!.Id).Distinct().Should().ContainSingle().Subject;

            var conflictingBase = CreatePreviewGraph() with { Name = "catalog-race-conflict" };
            var conflictingCreates = await RunAsync((service, index) =>
            {
                var node = conflictingBase.Nodes.Single();
                var renamed = new ProcessingGraphNodeDefinition(
                    $"Preview{index}", node.StepAlias, node.StepVersion, node.OperationKind, node.Enabled,
                    node.FailurePolicy, node.Order, node.EffectiveOptions, node.Dependencies, node.Inputs,
                    node.Outputs, node.Window, node.CapabilityLabels, node.HostApplicability);
                return service.CreateRevisionAsync(
                    conflictingBase with { Nodes = [renamed] }, "editor", true, CancellationToken.None);
            }).ConfigureAwait(false);
            conflictingCreates.Count(result => result.Outcome == CentralProcessingGraphMutationOutcome.Applied).Should().Be(1);
            conflictingCreates.Count(result => result.Outcome == CentralProcessingGraphMutationOutcome.Conflict)
                .Should().Be(Attempts - 1);

            var publishes = await RunAsync((service, _) =>
                service.PublishRevisionAsync(revisionId, "editor", true, CancellationToken.None)).ConfigureAwait(false);
            publishes.Count(result => result.Outcome == CentralProcessingGraphMutationOutcome.Applied).Should().Be(1);
            publishes.Count(result => result.Outcome == CentralProcessingGraphMutationOutcome.Unchanged)
                .Should().Be(Attempts - 1);

            var retirements = await RunAsync((service, _) =>
                service.RetireRevisionAsync(revisionId, "editor", "race-test", true, CancellationToken.None))
                .ConfigureAwait(false);
            retirements.Count(result => result.Outcome == CentralProcessingGraphMutationOutcome.Applied).Should().Be(1);
            retirements.Count(result => result.Outcome == CentralProcessingGraphMutationOutcome.Unchanged)
                .Should().Be(Attempts - 1);

            context.ChangeTracker.Clear();
            var persisted = await context.CentralProcessingGraphRevisions.AsNoTracking()
                .SingleAsync(item => item.Id == revisionId).ConfigureAwait(false);
            persisted.Lifecycle.Should().Be(CentralProcessingGraphLifecycle.Retired);
            (await context.CentralProcessingGraphRevisions.AsNoTracking()
                .CountAsync(item => item.Name == "catalog-race-conflict").ConfigureAwait(false)).Should().Be(1);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CleanDatabaseEnforcesExpansionSealAndFrozenCounts()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphMigration_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);

            var columns = await context.Database.SqlQuery<string>($"""
                    SELECT [name] AS [Value]
                    FROM [sys].[columns]
                    WHERE [object_id] = OBJECT_ID(N'[CentralProcessingGraphExecutions]')
                      AND [name] IN (N'ExpectedSourceCount', N'ExpectedNodeCount', N'ExpectedDependencyCount',
                                     N'ExpectedOutputCount', N'ExpandedAtUtc')
                    """).ToArrayAsync().ConfigureAwait(false);
            columns.Should().BeEquivalentTo(
                "ExpectedSourceCount",
                "ExpectedNodeCount",
                "ExpectedDependencyCount",
                "ExpectedOutputCount",
                "ExpandedAtUtc");
            var constraints = await context.Database.SqlQuery<string>($"""
                    SELECT [name] AS [Value]
                    FROM [sys].[check_constraints]
                    WHERE [parent_object_id] = OBJECT_ID(N'[CentralProcessingGraphExecutions]')
                    """).ToArrayAsync().ConfigureAwait(false);
            constraints.Should().Contain([
                "CK_CentralProcessingGraphExecutions_Expansion",
                "CK_CentralProcessingGraphExecutions_ExpectedCounts",
                "CK_CentralProcessingGraphExecutions_Provenance",
                "CK_CentralProcessingGraphExecutions_StatusTimestamps"]);
            var outputConstraints = await context.Database.SqlQuery<string>($"""
                    SELECT [name] AS [Value]
                    FROM [sys].[check_constraints]
                    WHERE [parent_object_id] = OBJECT_ID(N'[CentralDerivativeJobOutputs]')
                    """).ToArrayAsync().ConfigureAwait(false);
            outputConstraints.Should().Contain("CK_CentralDerivativeJobOutputs_ContractIdentity");
            var contractJsonType = await context.Database.SqlQuery<string>($"""
                    SELECT TYPE_NAME([system_type_id]) AS [Value]
                    FROM [sys].[columns]
                    WHERE [object_id] = OBJECT_ID(N'[CentralDerivativeJobOutputs]')
                      AND [name] = N'ContractJson'
                    """).SingleAsync().ConfigureAwait(false);
            contractJsonType.Should().Be("varchar");
            var provenanceDefinition = await context.Database.SqlQuery<string>($"""
                    SELECT [definition] AS [Value]
                    FROM [sys].[check_constraints]
                    WHERE [parent_object_id] = OBJECT_ID(N'[CentralProcessingGraphExecutions]')
                      AND [name] = N'CK_CentralProcessingGraphExecutions_Provenance'
                    """).SingleAsync().ConfigureAwait(false);
            provenanceDefinition.Should().Contain("[AssignmentId] IS NULL");
            provenanceDefinition.Should().ContainEquivalentOf("char((9))");
            var triggers = await context.Database.SqlQuery<string>($"""
                    SELECT [name] AS [Value]
                    FROM [sys].[triggers]
                    WHERE [name] IN (N'TR_CentralArtifactProcessingEvidence_GraphContractImmutable',
                                     N'TR_CentralProcessingGraphExecutions_IdentityImmutable',
                                     N'TR_CentralDerivativeJobs_GraphIdentityImmutable',
                                     N'TR_CentralDerivativeJobInputRequirements_GraphBindingImmutable',
                                     N'TR_CentralDerivativeJobInputs_GraphImmutable',
                                     N'TR_CentralDerivativeJobCanonicalInputs_GraphImmutable',
                                     N'TR_CentralDerivativeJobOutputs_BindOnce')
                    """).ToArrayAsync().ConfigureAwait(false);
            triggers.Should().HaveCount(7);
            var evidenceColumns = await context.Database.SqlQuery<string>($"""
                    SELECT [name] AS [Value]
                    FROM [sys].[columns]
                    WHERE [object_id] = OBJECT_ID(N'[CentralArtifactProcessingEvidence]')
                       AND [name] IN (N'GraphProductContractIdentitySha256', N'RecipeOperationKind', N'ProductKind',
                                      N'ProductSchemaVersion', N'ProductMediaType')
                    """).ToArrayAsync().ConfigureAwait(false);
            evidenceColumns.Should().BeEquivalentTo(
                "GraphProductContractIdentitySha256", "RecipeOperationKind", "ProductKind",
                "ProductSchemaVersion", "ProductMediaType");
            var outputTriggerDefinition = await context.Database.SqlQuery<string>($"""
                    SELECT OBJECT_DEFINITION(OBJECT_ID(N'TR_CentralDerivativeJobOutputs_BindOnce')) AS [Value]
                    """).SingleAsync().ConfigureAwait(false);
            outputTriggerDefinition.Should().Contain("[GraphProductContractIdentitySha256]");
            outputTriggerDefinition.Should().Contain("[InputSetIdentitySha256]");
            outputTriggerDefinition.Should().Contain("$.schemaVersion");
            outputTriggerDefinition.Should().Contain("$.mediaType");
            outputTriggerDefinition.Should().Contain("$.recipe.operationKind");

            var executionId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            const string EmptyJson = "{}";
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE [CentralProcessingGraphExecutions] NOCHECK CONSTRAINT ALL").ConfigureAwait(false);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [CentralProcessingGraphExecutions]
                        ([Id], [ExecutionClass], [Status], [RequestIdentitySha256], [RevisionId],
                         [DefinitionIdentitySha256], [FrozenDefinitionJson], [CentralPlanIdentitySha256],
                         [FrozenCentralPlanJson], [ExpectedSourceCount], [ExpectedNodeCount],
                         [ExpectedDependencyCount], [ExpectedOutputCount], [ExpandedAtUtc], [ObservatoryId],
                         [LogicalCameraId], [LogicalCameraInstallationId], [InstallationPublicId],
                         [AnchorSourceCentralArtifactId], [AnchorSourceArtifactId], [AnchorSourceChecksumSha256],
                         [Trigger], [ActorId], [IdempotencyKey], [ReasonCode], [CreatedAtUtc], [UpdatedAtUtc])
                    VALUES
                        ({executionId}, N'Replay', N'Pending', {new string('A', 64)}, {Guid.NewGuid()},
                         {new string('B', 64)}, {EmptyJson}, {new string('C', 64)}, {EmptyJson}, 1, 0, 0, 0, NULL,
                         {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()},
                         {Guid.NewGuid()}, {new string('D', 64)}, N'Replay', N'operator',
                         {Guid.NewGuid().ToString("N")}, N'test', {now}, {now})
                    """).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE [CentralProcessingGraphExecutions] CHECK CONSTRAINT ALL").ConfigureAwait(false);

            Func<Task> guardedSeal = () => CentralProcessingGraphExpansionSeal.SealAsync(
                context, executionId, now, CancellationToken.None);
            await guardedSeal.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*frozen counts*").ConfigureAwait(false);

            Func<Task> mismatchedSeal = () => context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralProcessingGraphExecutions]
                SET [ExpandedAtUtc] = {now}, [UpdatedAtUtc] = {now}
                WHERE [Id] = {executionId}
                """);
            await mismatchedSeal.Should().ThrowAsync<SqlException>()
                .WithMessage("*frozen counts*").ConfigureAwait(false);

            Func<Task> rewriteFrozenCount = () => context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralProcessingGraphExecutions]
                SET [ExpectedSourceCount] = 0
                WHERE [Id] = {executionId}
                """);
            await rewriteFrozenCount.Should().ThrowAsync<SqlException>()
                .WithMessage("*identity is immutable*").ConfigureAwait(false);

            (await context.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CleanDatabaseSerializesExpansionAndProtectsFrozenGraphInputs()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphConcurrency_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("""
                ALTER TABLE [CentralProcessingGraphExecutions] NOCHECK CONSTRAINT ALL;
                ALTER TABLE [CentralDerivativeJobs] NOCHECK CONSTRAINT ALL;
                ALTER TABLE [CentralDerivativeJobInputRequirements] NOCHECK CONSTRAINT ALL;
                ALTER TABLE [CentralDerivativeJobInputs] NOCHECK CONSTRAINT ALL;
                ALTER TABLE [CentralDerivativeJobCanonicalInputs] NOCHECK CONSTRAINT ALL;
                ALTER TABLE [CentralProcessingGraphExecutionSources] NOCHECK CONSTRAINT ALL;
                """).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var executionId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var artifactRequirementId = Guid.NewGuid();
            var canonicalRequirementId = Guid.NewGuid();
            var appendedArtifactRequirementId = Guid.NewGuid();
            var appendedCanonicalRequirementId = Guid.NewGuid();
            const string EmptyJson = "{}";
            const string NoncanonicalContractJson =
                "{\"variant\":\"graph\",\"role\":\"Preview\",\"productKind\":\"PixelData\",\"recipe\":null," +
                "\"schemaVersion\":\"pixel-v1\",\"algorithms\":[],\"mediaType\":\"image/fits\"}";
            var frozenInputIdentity = new string('8', 64);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralProcessingGraphExecutions]
                    ([Id], [ExecutionClass], [Status], [RequestIdentitySha256], [RevisionId],
                     [DefinitionIdentitySha256], [FrozenDefinitionJson], [CentralPlanIdentitySha256],
                     [FrozenCentralPlanJson], [ExpectedSourceCount], [ExpectedNodeCount],
                     [ExpectedDependencyCount], [ExpectedOutputCount], [ExpandedAtUtc], [ObservatoryId],
                     [LogicalCameraId], [LogicalCameraInstallationId], [InstallationPublicId],
                     [AnchorSourceCentralArtifactId], [AnchorSourceArtifactId], [AnchorSourceChecksumSha256],
                     [Trigger], [ActorId], [IdempotencyKey], [ReasonCode], [CreatedAtUtc], [UpdatedAtUtc])
                VALUES
                    ({executionId}, N'Replay', N'Pending', {new string('A', 64)}, {Guid.NewGuid()},
                     {new string('B', 64)}, {EmptyJson}, {new string('C', 64)}, {EmptyJson}, 0, 1, 0, 1, NULL,
                     {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()},
                     {Guid.NewGuid()}, {new string('D', 64)}, N'Replay', N'operator',
                     {Guid.NewGuid().ToString("N")}, N'test', {now}, {now});

                INSERT INTO [CentralDerivativeJobs]
                    ([Id], [SourceCentralArtifactId], [TargetRole], [TargetRecipeVersion], [TargetVariant],
                     [RecipeName], [RecipeOptionsJson], [InputSelectorJson], [RequestedRecipeIdentitySha256],
                     [ExpectedRecipeIdentitySha256], [RequestIdentitySha256], [GraphExecutionId], [GraphNodeId],
                     [GraphNodeOrdinal], [SharedNodePlanIdentitySha256], [FrozenNodePlanJson], [GraphFailurePolicy],
                     [Status], [InputSetIdentitySha256], [AttemptCount], [MaxAttempts], [CreatedAtUtc], [UpdatedAtUtc])
                VALUES
                    ({jobId}, {Guid.NewGuid()}, N'Preview', N'recipe-v1', N'graph', N'preview', {EmptyJson},
                     {EmptyJson}, {new string('E', 64)}, {new string('E', 64)}, {new string('F', 64)},
                     {executionId}, N'node', 0, {new string('1', 64)}, {EmptyJson}, N'Required',
                     N'TerminalFailure', NULL, 0, 1, {now}, {now});

                INSERT INTO [CentralDerivativeJobInputRequirements]
                    ([Id], [CentralDerivativeJobId], [Ordinal], [BindingName], [SourceKind], [IsRequired],
                     [SelectorJson], [CompatibilityMode], [ExpectedAgentId], [ExpectedCentralArtifactId],
                     [ResolutionState], [ResolvedAtUtc])
                VALUES
                    ({artifactRequirementId}, {jobId}, 0, N'artifact', N'Artifact', 1, {EmptyJson}, N'None',
                      N'agent', {Guid.NewGuid()}, N'Resolved', {now}),
                    ({canonicalRequirementId}, {jobId}, 1, N'canonical', N'Canonical', 1, {EmptyJson}, N'None',
                     N'agent', NULL, N'Resolved', {now}),
                    ({appendedArtifactRequirementId}, {jobId}, 2, N'appended-artifact', N'Artifact', 0,
                     {EmptyJson}, N'None', N'agent', {Guid.NewGuid()}, N'Resolved', {now}),
                    ({appendedCanonicalRequirementId}, {jobId}, 3, N'appended-canonical', N'Canonical', 0,
                     {EmptyJson}, N'None', N'agent', NULL, N'Resolved', {now});
                """).ConfigureAwait(false);

            Func<Task> forgeOutputContractIdentity = () => context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralDerivativeJobOutputs]
                    ([Id], [CentralDerivativeJobId], [Ordinal], [Role], [Variant], [ProductKind],
                     [ContractJson], [ContractIdentitySha256])
                VALUES
                    ({Guid.NewGuid()}, {jobId}, 0, N'Preview', N'graph', N'PixelData', {NoncanonicalContractJson},
                      {new string('4', 64)})
                """);
            await forgeOutputContractIdentity.Should().ThrowAsync<SqlException>()
                .WithMessage("*CK_CentralDerivativeJobOutputs_ContractIdentity*").ConfigureAwait(false);
            var contractIdentity = CentralDerivativeJobOutput.ComputeContractIdentitySha256(
                NoncanonicalContractJson);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralDerivativeJobOutputs]
                    ([Id], [CentralDerivativeJobId], [Ordinal], [Role], [Variant], [ProductKind],
                     [ContractJson], [ContractIdentitySha256])
                VALUES
                    ({Guid.NewGuid()}, {jobId}, 0, N'Preview', N'graph', N'PixelData', {NoncanonicalContractJson},
                      {contractIdentity})
                """).ConfigureAwait(false);

            var persistedJob = await context.CentralDerivativeJobs.AsNoTracking()
                .Include(job => job.Outputs)
                .SingleAsync(job => job.Id == jobId)
                .ConfigureAwait(false);
            persistedJob.Outputs.Single().ContractJson.Should().Be(NoncanonicalContractJson);
            var descriptor = RecipeIdentityDescriptor.Create(
                "test", "1.0.0", "implementation-v1", JsonSerializer.SerializeToElement(new { }));
            var product = new ProcessingProduct(
                FrameArtifactRole.Preview,
                "graph",
                new string('5', 64),
                "image/fits",
                null,
                new byte[] { 1 },
                new string('6', 64),
                new ProcessingRecipeIdentity(descriptor, new string('7', 64)),
                ImmutableArray<ProcessingAlgorithmIdentity>.Empty,
                [],
                TimeSpan.Zero,
                new ProcessingCompatibilityIdentity(
                    "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"))
            {
                SchemaVersion = "pixel-v1"
            };
            CentralProcessingGraphOutputBinding.ResolveContractIdentity(persistedJob, product)
                .Should().Be(contractIdentity);

            await CentralProcessingGraphExpansionSeal.SealAsync(
                context, executionId, now, CancellationToken.None).ConfigureAwait(false);
            await using (var inputTransaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false))
            {
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [CentralDerivativeJobInputs]
                        ([Id], [CentralDerivativeJobId], [CentralDerivativeJobInputRequirementId], [Ordinal],
                         [CentralArtifactId], [CompatibilityJson], [CompatibilitySha256], [ByteLength], [SelectedAtUtc])
                    VALUES
                        ({Guid.NewGuid()}, {jobId}, {artifactRequirementId}, 0, {Guid.NewGuid()}, {EmptyJson},
                         {new string('2', 64)}, 1, {now});

                    INSERT INTO [CentralDerivativeJobCanonicalInputs]
                        ([Id], [CentralDerivativeJobId], [CentralDerivativeJobInputRequirementId], [Ordinal],
                         [SchemaVersion], [IdentitySha256], [CanonicalJson], [ByteLength], [SelectedAtUtc])
                    VALUES
                        ({Guid.NewGuid()}, {jobId}, {canonicalRequirementId}, 1, N'schema-v1', {new string('3', 64)},
                         {EmptyJson}, 2, {now});

                    UPDATE [CentralDerivativeJobs]
                    SET [InputSetIdentitySha256] = {frozenInputIdentity}, [ResolutionCompletedAtUtc] = {now}
                    WHERE [Id] = {jobId};
                    """).ConfigureAwait(false);
                await inputTransaction.CommitAsync().ConfigureAwait(false);
            }
            var maxAttemptUpdate = await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralDerivativeJobs] SET [MaxAttempts] = 6 WHERE [Id] = {jobId}
                """).ConfigureAwait(false);
            maxAttemptUpdate.Should().Be(1);

            Func<Task> appendArtifactInput = () => context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralDerivativeJobInputs]
                    ([Id], [CentralDerivativeJobId], [CentralDerivativeJobInputRequirementId], [Ordinal],
                     [CentralArtifactId], [CompatibilityJson], [CompatibilitySha256], [ByteLength], [SelectedAtUtc])
                VALUES
                    ({Guid.NewGuid()}, {jobId}, {appendedArtifactRequirementId}, 2, {Guid.NewGuid()}, {EmptyJson},
                     {new string('2', 64)}, 1, {now})
                """);
            await appendArtifactInput.Should().ThrowAsync<SqlException>()
                .WithMessage("*inconsistent or frozen*").ConfigureAwait(false);

            Func<Task> appendCanonicalInput = () => context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralDerivativeJobCanonicalInputs]
                    ([Id], [CentralDerivativeJobId], [CentralDerivativeJobInputRequirementId], [Ordinal],
                     [SchemaVersion], [IdentitySha256], [CanonicalJson], [ByteLength], [SelectedAtUtc])
                VALUES
                    ({Guid.NewGuid()}, {jobId}, {appendedCanonicalRequirementId}, 3, N'schema-v1', {new string('3', 64)},
                     {EmptyJson}, 2, {now})
                """);
            await appendCanonicalInput.Should().ThrowAsync<SqlException>()
                .WithMessage("*inconsistent or frozen*").ConfigureAwait(false);

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralProcessingGraphExecutions]
                SET [Status] = N'Running', [StartedAtUtc] = {now}
                WHERE [Id] = {executionId};
                """).ConfigureAwait(false);
            // Terminal executions are final at the database as well as in EF: a Failed execution cannot be reopened
            // to Running (node recovery goes through graph replay). Probe inside a rolled-back transaction so the
            // cancellation flow below still starts from Running.
            await using (var probe = await context.Database.BeginTransactionAsync().ConfigureAwait(false))
            {
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [CentralDerivativeJobs]
                    SET [Status] = N'TerminalFailure', [UpdatedAtUtc] = {now.AddTicks(1)}
                    WHERE [Id] = {jobId};

                    UPDATE [CentralProcessingGraphExecutions]
                    SET [Status] = N'Failed', [CompletedAtUtc] = {now.AddTicks(1)}, [UpdatedAtUtc] = {now.AddTicks(1)}
                    WHERE [Id] = {executionId};
                    """).ConfigureAwait(false);
                Func<Task> reopenFailedExecution = () => context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [CentralProcessingGraphExecutions]
                    SET [Status] = N'Running', [CompletedAtUtc] = NULL, [UpdatedAtUtc] = {now.AddTicks(2)}
                    WHERE [Id] = {executionId}
                    """);
                await reopenFailedExecution.Should().ThrowAsync<SqlException>()
                    .WithMessage("Processing graph execution*").ConfigureAwait(false);
                await probe.RollbackAsync().ConfigureAwait(false);
            }
            (await context.Database.SqlQuery<string>($"""
                SELECT [Status] AS [Value]
                FROM [CentralProcessingGraphExecutions]
                WHERE [Id] = {executionId}
                """).SingleAsync().ConfigureAwait(false)).Should().Be("Running");

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralDerivativeJobs]
                SET [Status] = N'TerminalFailure', [UpdatedAtUtc] = {now.AddTicks(2)}
                WHERE [Id] = {jobId};

                UPDATE [CentralProcessingGraphExecutions]
                SET [Status] = N'CancelRequested', [CancellationRequestedAtUtc] = {now.AddTicks(2)},
                    [UpdatedAtUtc] = {now.AddTicks(2)}
                WHERE [Id] = {executionId};

                UPDATE [CentralProcessingGraphExecutions]
                SET [Status] = N'Canceled', [CompletedAtUtc] = {now.AddTicks(2)}
                WHERE [Id] = {executionId};
                """).ConfigureAwait(false);
            Func<Task> reopenCanceledExecution = () => context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralProcessingGraphExecutions]
                SET [Status] = N'Running', [CancellationRequestedAtUtc] = NULL, [CompletedAtUtc] = NULL,
                    [UpdatedAtUtc] = {now.AddTicks(3)}
                WHERE [Id] = {executionId}
                """);
            await reopenCanceledExecution.Should().ThrowAsync<SqlException>().ConfigureAwait(false);

            var concurrentExecutionId = Guid.NewGuid();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralProcessingGraphExecutions]
                    ([Id], [ExecutionClass], [Status], [RequestIdentitySha256], [RevisionId],
                     [DefinitionIdentitySha256], [FrozenDefinitionJson], [CentralPlanIdentitySha256],
                     [FrozenCentralPlanJson], [ExpectedSourceCount], [ExpectedNodeCount],
                     [ExpectedDependencyCount], [ExpectedOutputCount], [ExpandedAtUtc], [ObservatoryId],
                     [LogicalCameraId], [LogicalCameraInstallationId], [InstallationPublicId],
                     [AnchorSourceCentralArtifactId], [AnchorSourceArtifactId], [AnchorSourceChecksumSha256],
                     [Trigger], [ActorId], [IdempotencyKey], [ReasonCode], [CreatedAtUtc], [UpdatedAtUtc])
                VALUES
                    ({concurrentExecutionId}, N'Replay', N'Pending', {new string('4', 64)}, {Guid.NewGuid()},
                     {new string('5', 64)}, {EmptyJson}, {new string('6', 64)}, {EmptyJson}, 0, 0, 0, 0, NULL,
                     {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()},
                     {Guid.NewGuid()}, {new string('7', 64)}, N'Replay', N'operator',
                     {Guid.NewGuid().ToString("N")}, N'test', {now}, {now})
                """).ConfigureAwait(false);

            await using var insertionContext = new ApplicationDbContext(options);
            await using var insertionTransaction = await insertionContext.Database.BeginTransactionAsync()
                .ConfigureAwait(false);
            await insertionContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralProcessingGraphExecutionSources]
                    ([Id], [ExecutionId], [Ordinal], [SourceId], [OutputOrdinal], [CentralArtifactId], [ArtifactId],
                     [ArtifactChecksumSha256], [ArtifactByteLength], [SelectionEvidenceJson],
                     [SelectionEvidenceSha256], [SelectedAtUtc])
                VALUES
                    ({Guid.NewGuid()}, {concurrentExecutionId}, 0, N'source', 0, {Guid.NewGuid()}, {Guid.NewGuid()},
                     {new string('9', 64)}, 1, {EmptyJson}, {new string('0', 64)}, {now})
                """).ConfigureAwait(false);
            await using var sealingContext = new ApplicationDbContext(options);
            var sealTask = CentralProcessingGraphExpansionSeal.SealAsync(
                sealingContext, concurrentExecutionId, now, CancellationToken.None);
            await Task.Delay(200).ConfigureAwait(false);
            sealTask.IsCompleted.Should().BeFalse("the child trigger must hold the execution row while inserting");
            await insertionTransaction.CommitAsync().ConfigureAwait(false);
            Func<Task> observeSeal = () => sealTask;
            await observeSeal.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*frozen counts*").ConfigureAwait(false);

            var sourceTriggerDefinition = await context.Database.SqlQuery<string>($"""
                SELECT OBJECT_DEFINITION(OBJECT_ID(N'TR_CentralProcessingGraphExecutionSources_Immutable')) AS [Value]
                """).SingleAsync().ConfigureAwait(false);
            sourceTriggerDefinition.Should().ContainEquivalentOf("WITH (UPDLOCK, HOLDLOCK)");
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task SqlServerPendingExecutionWithTerminalNodesConvergesThroughDurableRunningStep()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorGraphPendingConverge_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ApplicationDbContext(options);
        try
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("""
                ALTER TABLE [CentralProcessingGraphExecutions] NOCHECK CONSTRAINT ALL;
                ALTER TABLE [CentralDerivativeJobs] NOCHECK CONSTRAINT ALL;
                """).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var executionId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            const string EmptyJson = "{}";
            // A sealed Pending execution whose only node is already terminal: nothing ever moved it to Running.
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralProcessingGraphExecutions]
                    ([Id], [ExecutionClass], [Status], [RequestIdentitySha256], [RevisionId],
                     [DefinitionIdentitySha256], [FrozenDefinitionJson], [CentralPlanIdentitySha256],
                     [FrozenCentralPlanJson], [ExpectedSourceCount], [ExpectedNodeCount],
                     [ExpectedDependencyCount], [ExpectedOutputCount], [ExpandedAtUtc], [ObservatoryId],
                     [LogicalCameraId], [LogicalCameraInstallationId], [InstallationPublicId],
                     [AnchorSourceCentralArtifactId], [AnchorSourceArtifactId], [AnchorSourceChecksumSha256],
                     [Trigger], [ActorId], [IdempotencyKey], [ReasonCode], [CreatedAtUtc], [UpdatedAtUtc])
                VALUES
                    ({executionId}, N'Replay', N'Pending', {new string('A', 64)}, {Guid.NewGuid()},
                     {new string('B', 64)}, {EmptyJson}, {new string('C', 64)}, {EmptyJson}, 0, 1, 0, 0, NULL,
                     {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()},
                     {Guid.NewGuid()}, {new string('D', 64)}, N'Replay', N'operator',
                     {Guid.NewGuid().ToString("N")}, N'test', {now}, {now});

                INSERT INTO [CentralDerivativeJobs]
                    ([Id], [SourceCentralArtifactId], [TargetRole], [TargetRecipeVersion], [TargetVariant],
                     [RecipeName], [RecipeOptionsJson], [InputSelectorJson], [RequestedRecipeIdentitySha256],
                     [ExpectedRecipeIdentitySha256], [RequestIdentitySha256], [GraphExecutionId], [GraphNodeId],
                     [GraphNodeOrdinal], [SharedNodePlanIdentitySha256], [FrozenNodePlanJson], [GraphFailurePolicy],
                     [Status], [InputSetIdentitySha256], [AttemptCount], [MaxAttempts], [CompletedAtUtc],
                     [CreatedAtUtc], [UpdatedAtUtc])
                VALUES
                    ({jobId}, {Guid.NewGuid()}, N'Preview', N'recipe-v1', N'graph', N'preview', {EmptyJson},
                     {EmptyJson}, {new string('E', 64)}, {new string('E', 64)}, {new string('F', 64)},
                     {executionId}, N'node', 0, {new string('1', 64)}, {EmptyJson}, N'Required',
                     N'Completed', {new string('2', 64)}, 1, 1, {now}, {now}, {now});

                UPDATE [CentralProcessingGraphExecutions]
                SET [ExpandedAtUtc] = {now}
                WHERE [Id] = {executionId};
                """).ConfigureAwait(false);

            // Trigger parity with the EF lifecycle guard: the one-save shortcut is rejected at the database.
            Func<Task> shortcut = () => context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralProcessingGraphExecutions]
                SET [Status] = N'Completed', [StartedAtUtc] = {now.AddSeconds(1)},
                    [CompletedAtUtc] = {now.AddSeconds(1)}, [UpdatedAtUtc] = {now.AddSeconds(1)}
                WHERE [Id] = {executionId}
                """);
            await shortcut.Should().ThrowAsync<SqlException>()
                .WithMessage("*status transition is invalid*").ConfigureAwait(false);
            (await context.Database.SqlQuery<string>($"""
                SELECT [Status] AS [Value] FROM [CentralProcessingGraphExecutions] WHERE [Id] = {executionId}
                """).SingleAsync().ConfigureAwait(false)).Should().Be("Pending");

            // Restart recovery: a fresh context converges the Pending execution through the durable Running step.
            using var workerTelemetry = new CentralDerivativeWorkerTelemetry();
            using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
            var registry = new CentralProcessingGraphNodeRegistry(new CentralDerivativeRecipeCatalog());
            var converged = now.AddSeconds(2);
            await using (var recoveryContext = new ApplicationDbContext(options))
            {
                var catalog = new ProcessingGraphCatalogService(
                    recoveryContext, registry, TimeProvider.System, catalogTelemetry,
                    NullLogger<ProcessingGraphCatalogService>.Instance);
                await new CentralProcessingGraphScheduler(
                        recoveryContext, catalog, registry, new NoopWindowResolver(), new UnusedObjectReader(),
                        workerTelemetry, TimeProvider.System)
                    .ConvergeBatchAsync(converged, CancellationToken.None).ConfigureAwait(false);
            }
            var completed = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(item => item.Id == executionId).ConfigureAwait(false);
            completed.Status.Should().Be(CentralProcessingGraphExecutionStatus.Completed);
            completed.StartedAtUtc.Should().Be(converged);
            completed.CompletedAtUtc.Should().Be(converged);
            completed.UpdatedAtUtc.Should().Be(converged);

            // Same-execution replay after completion is idempotent.
            await using (var replayContext = new ApplicationDbContext(options))
            {
                var catalog = new ProcessingGraphCatalogService(
                    replayContext, registry, TimeProvider.System, catalogTelemetry,
                    NullLogger<ProcessingGraphCatalogService>.Instance);
                var scheduler = new CentralProcessingGraphScheduler(
                    replayContext, catalog, registry, new NoopWindowResolver(), new UnusedObjectReader(),
                    workerTelemetry, TimeProvider.System);
                await scheduler.ConvergeAsync(executionId, converged.AddMinutes(1), CancellationToken.None)
                    .ConfigureAwait(false);
                await scheduler.ConvergeBatchAsync(converged.AddMinutes(2), CancellationToken.None).ConfigureAwait(false);
            }
            var replayed = await context.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(item => item.Id == executionId).ConfigureAwait(false);
            replayed.Status.Should().Be(CentralProcessingGraphExecutionStatus.Completed);
            replayed.StartedAtUtc.Should().Be(converged);
            replayed.CompletedAtUtc.Should().Be(converged);
            replayed.UpdatedAtUtc.Should().Be(converged);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private sealed record SeededCamera(
        ApplicationUser Owner,
        Observatory Observatory,
        DeviceRegistration Registration,
        LogicalCamera Camera,
        LogicalCameraInstallation Installation,
        CentralFrame Frame);

    private static async Task<SeededCamera> SeedCameraAsync(
        ApplicationDbContext context,
        DateTimeOffset now,
        string slug)
    {
        var owner = new ApplicationUser
        {
            Id = $"operator-{slug}",
            UserName = $"operator-{slug}",
            NormalizedUserName = $"OPERATOR-{slug}".ToUpperInvariant(),
            AccountType = AccountType.User
        };
        var observatory = new Observatory
        {
            OwnerUserId = owner.Id,
            Name = $"Graph {slug}",
            CreatedAtUtc = now.AddDays(-1)
        };
        var devicePublicId = Guid.NewGuid();
        var registration = new DeviceRegistration
        {
            DeviceId = $"graph-agent-{slug}",
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            FriendlyName = "Graph Agent",
            ObservatoryName = observatory.Name,
            OwnerUserId = owner.Id,
            OwnerDisplayName = owner.UserName,
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = new string('1', 64),
            DevicePublicId = devicePublicId,
            IssuedAtUtc = now.AddDays(-1),
            ActivatedAtUtc = now.AddDays(-1)
        };
        var camera = new LogicalCamera
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            Slug = slug,
            Name = $"Camera {slug}",
            Description = "SQL scheduler test",
            CreatedAtUtc = now.AddDays(-1),
            CreatedByUserId = owner.Id
        };
        var installation = new LogicalCameraInstallation
        {
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            Registration = registration,
            RegistrationId = registration.Id,
            InstallationPublicId = Guid.NewGuid(),
            AssignedAtUtc = now.AddHours(-1),
            AssignedByUserId = owner.Id,
            AssignmentReasonCode = "test"
        };
        var frame = new CentralFrame
        {
            RegistrationId = registration.Id,
            LogicalCameraInstallation = installation,
            LogicalCameraInstallationId = installation.Id,
            DevicePublicId = devicePublicId,
            ObservatoryId = observatory.Id,
            AgentId = registration.DeviceId,
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = now.AddMinutes(-1),
            FirstReceivedAtUtc = now.AddMinutes(-1),
            RigId = "rig",
            CaptureSequence = 10
        };
        context.AddRange(owner, observatory, registration, camera, installation, frame);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return new(owner, observatory, registration, camera, installation, frame);
    }

    private static CentralArtifact CreateSourceArtifact(
        SeededCamera camera,
        FrameArtifactRole role,
        char checksumDigit,
        DateTimeOffset receivedAtUtc)
        => new()
        {
            CentralFrameId = camera.Frame.Id,
            DevicePublicId = camera.Frame.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = role,
            Variant = "source",
            RecipeVersion = $"{role}-v1",
            ManifestSchemaVersion = "manifest-v1",
            MediaType = "application/octet-stream",
            ByteLength = 1,
            ChecksumSha256 = new string(checksumDigit, 64),
            StorageReference = $"s3://skymonitor-artifacts/{Guid.NewGuid():N}",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReceivedAtUtc = receivedAtUtc,
            CreatedUtc = receivedAtUtc,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };

    /// <summary>A single Preview node that needs both frame sources: Raw as its primary artifact, Calibrated for ordering.</summary>
    private static ProcessingGraphDefinition CreateRawAndCalibratedPreviewGraph()
    {
        _ = BuiltInProcessingRecipes.TryGetDefinition(
            BuiltInProcessingRecipes.EncodedPreview, out var recipeDefinition);
        var options = BuiltInProcessingRecipes.NormalizeOptions(
            BuiltInProcessingRecipes.EncodedPreview,
            CaptureContractJson.SerializeToElement(new EncodedPreviewOptions()));
        return new(
            ProcessingGraphSchemaVersions.Current,
            "sql-raw-and-calibrated",
            "1",
            [
                new ProcessingGraphSourceDefinition(
                    "$raw",
                    [new ProcessingGraphProductContract(
                        FrameArtifactRole.Raw, "source", ProcessingProductKind.PixelData)]),
                new ProcessingGraphSourceDefinition(
                    "$calibrated",
                    [new ProcessingGraphProductContract(
                        FrameArtifactRole.Calibrated, "source", ProcessingProductKind.PixelData)])
            ],
            [new ProcessingGraphNodeDefinition(
                "Preview",
                BuiltInProcessingRecipes.EncodedPreview,
                CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
                ProcessingOperationKind.Transform,
                true,
                ProcessingGraphNodeFailurePolicy.Required,
                0,
                options,
                [
                    new ProcessingGraphDependencyDefinition("$raw"),
                    new ProcessingGraphDependencyDefinition("$calibrated", ProcessingGraphDependencyKind.Ordering)
                ],
                [new ProcessingGraphInputContract(
                    [FrameArtifactRole.Raw],
                    [ProcessingProductKind.PixelData],
                    [],
                    [],
                    [])],
                [new ProcessingGraphProductContract(
                    FrameArtifactRole.Preview,
                    CentralDerivativeRecipeCatalog.PreviewVariant,
                    ProcessingProductKind.PixelData,
                    recipeDefinition,
                    MediaType: "image/jpeg")],
                null,
                ImmutableArray<string>.Empty,
                [ProcessingGraphHosts.LogicHost])]);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private static ProcessingGraphDefinition CreatePreviewGraph()
    {
        _ = BuiltInProcessingRecipes.TryGetDefinition(
            BuiltInProcessingRecipes.EncodedPreview, out var recipeDefinition);
        var options = BuiltInProcessingRecipes.NormalizeOptions(
            BuiltInProcessingRecipes.EncodedPreview,
            CaptureContractJson.SerializeToElement(new EncodedPreviewOptions()));
        return new(
            ProcessingGraphSchemaVersions.Current,
            "sql-preview",
            "1",
            [new ProcessingGraphSourceDefinition(
                "$raw",
                [new ProcessingGraphProductContract(
                    FrameArtifactRole.Raw, "source", ProcessingProductKind.PixelData)])],
            [new ProcessingGraphNodeDefinition(
                "Preview",
                BuiltInProcessingRecipes.EncodedPreview,
                CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
                ProcessingOperationKind.Transform,
                true,
                ProcessingGraphNodeFailurePolicy.Required,
                0,
                options,
                [new ProcessingGraphDependencyDefinition("$raw")],
                [new ProcessingGraphInputContract(
                    [FrameArtifactRole.Raw],
                    [ProcessingProductKind.PixelData],
                    [],
                    [],
                    [])],
                [new ProcessingGraphProductContract(
                    FrameArtifactRole.Preview,
                    CentralDerivativeRecipeCatalog.PreviewVariant,
                    ProcessingProductKind.PixelData,
                    recipeDefinition,
                    MediaType: "image/jpeg"),
                 new ProcessingGraphProductContract(
                    FrameArtifactRole.AnnotatedPreview,
                    "central-preview-secondary",
                    ProcessingProductKind.PixelData,
                    recipeDefinition,
                    MediaType: "image/jpeg")],
                null,
                ImmutableArray<string>.Empty,
                [ProcessingGraphHosts.LogicHost])]);
    }

    private static ProcessingProduct CreateProduct(
        ProcessingGraphProductContract contract,
        CentralDerivativeJobLease lease,
        int payloadValue)
    {
        using var options = JsonDocument.Parse(lease.RecipeOptionsJson);
        var descriptor = RecipeIdentityDescriptor.Create(
            contract.Recipe!.Name,
            contract.Recipe.SemanticVersion,
            contract.Recipe.ImplementationVersion,
            options.RootElement);
        var recipe = new ProcessingRecipeIdentity(descriptor, lease.ExpectedRecipeIdentitySha256!)
        {
            OperationKind = contract.Recipe.OperationKind
        };
        var sources = lease.Inputs!.OrderBy(input => input.Ordinal).Select(input => input.ArtifactId).ToArray();
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
            contract.Role, contract.Variant, recipe.IdentitySha256, sources);
        ReadOnlyMemory<byte> payload = new byte[] { checked((byte)payloadValue) };
        return new ProcessingProduct(
            contract.Role,
            contract.Variant,
            outputIdentity,
            contract.MediaType!,
            null,
            payload,
            ProcessingIdentity.ComputePayloadSha256(payload),
            recipe,
            contract.Algorithms,
            sources,
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity(
                "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"))
        {
            Kind = contract.ProductKind,
            SchemaVersion = contract.SchemaVersion
        };
    }

    private sealed class MultiOutputNodeRegistry : ICentralProcessingGraphNodeRegistry
    {
        private readonly CentralProcessingGraphNodeHandler handler;

        public MultiOutputNodeRegistry(ICentralDerivativeRecipeCatalog recipeCatalog)
        {
            var recipe = recipeCatalog.GetRequiredRecipes(FrameArtifactRole.Raw).Single(candidate =>
                candidate.RecipeName == BuiltInProcessingRecipes.EncodedPreview);
            _ = BuiltInProcessingRecipes.TryGetDefinition(recipe.RecipeName, out var definition);
            handler = new(
                recipe.RecipeName,
                recipe.RecipeVersion,
                definition!.OperationKind,
                CentralProcessingGraphNodeHandlerKind.BuiltInRecipe,
                recipe,
                definition);
        }

        public ImmutableArray<string> Capabilities => [];

        public CentralProcessingGraphNodeHandler GetRequired(string stepAlias)
            => stepAlias == handler.StepAlias ? handler : throw new InvalidOperationException("Unexpected step alias.");

        public bool Validate(ProcessingGraphExecutionPlan plan) => plan.Nodes.Length == 1;
    }

    private sealed class ThrowOnSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
            => throw new DbUpdateException("Injected expansion persistence failure.");
    }

    /// <summary>Commits a job cancellation on a side connection just before the renewal's lease UPDATE executes.</summary>
    private sealed class CancelBeforeLeaseUpdateInterceptor(string connectionString, Guid jobId) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Triggered && command.CommandText.Contains("UPDATE [c]", StringComparison.Ordinal) &&
                command.CommandText.Contains("[LeaseExpiresAtUtc] =", StringComparison.Ordinal) &&
                command.CommandText.Contains("[CentralDerivativeJobs]", StringComparison.Ordinal))
            {
                Triggered = true;
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var cancel = connection.CreateCommand();
                cancel.CommandText = """
                    UPDATE [CentralDerivativeJobs]
                    SET [CancellationRequestedAtUtc] = SYSDATETIMEOFFSET(), [UpdatedAtUtc] = SYSDATETIMEOFFSET()
                    WHERE [Id] = @id;
                    """;
                cancel.Parameters.AddWithValue("@id", jobId);
                (await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)).Should().Be(1);
            }
            return result;
        }
    }

    private sealed class ThrowOnCommitInterceptor(int commitNumber) : DbTransactionInterceptor
    {
        private int commits;

        public bool Triggered { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref commits) == commitNumber)
            {
                Triggered = true;
                throw new InvalidOperationException("Injected graph output completion failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class NoopWindowResolver : ICentralDerivativeWindowResolver
    {
        public Task ResolveAffectedAsync(CentralArtifact artifact, DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ResolveWaitingAsync(DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ResolveAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class UnusedObjectReader : ICentralArtifactObjectReader
    {
        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class IntegrityFailureObjectReader : ICentralArtifactObjectReader
    {
        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken) => throw new CentralArtifactIntegrityException("object.test-integrity");

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
