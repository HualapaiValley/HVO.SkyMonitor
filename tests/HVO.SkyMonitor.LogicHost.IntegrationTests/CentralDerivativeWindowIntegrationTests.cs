using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Minio;
using Minio.DataModel.Args;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text.Json;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class CentralDerivativeWindowIntegrationTests
{
    private const string Bucket = "skymonitor-artifacts";

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CentralTransientRuntime_UsesDelayedExactWindowAndPersistsCanonicalOutcome(bool hasCandidate)
    {
        var scenario = $"transient-runtime-{hasCandidate}-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            StarMaximumMagnitude = -30
        };
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 100, 102, 98, 101, 99 })
        {
            var payload = hasCandidate && sequence == 100
                ? CreatePayload([100, 1_000, 1_000, 100])
                : CreatePayload(100);
            var sourceId = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, payload, "transient-compatible")
                .ConfigureAwait(false);
            sources.Add(sequence, sourceId);
            await ScheduleTransientAsync(sourceId, options).ConfigureAwait(false);
        }

        Guid jobId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            jobId = await db.CentralDerivativeJobs.AsNoTracking()
                .Where(item => item.SourceCentralArtifactId == sources[100]
                    && item.RecipeName == CentralTransientRuntime.RecipeName)
                .Select(item => item.Id).SingleAsync().ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveAsync(jobId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            var job = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.InputRequirements)
                .Include(item => item.Inputs)
                .Include(item => item.CanonicalInputs)
                .SingleAsync(item => item.SourceCentralArtifactId == sources[100]
                    && item.RecipeName == CentralTransientRuntime.RecipeName).ConfigureAwait(false);
            job.Status.Should().Be(CentralDerivativeJobStatus.Pending,
                string.Join(";", job.InputRequirements.OrderBy(item => item.Ordinal).Select(item =>
                    $"{item.Ordinal}:{item.SourceKind}:{item.ExpectedCaptureSequence}:{item.ResolutionState}:{item.ResolutionReasonCode}")));
            job.InputRequirements.Where(item => item.SourceKind == CentralDerivativeInputSourceKind.Artifact)
                .OrderBy(item => item.Ordinal).Select(item => item.SequenceOffset).Should().Equal(-2, -1, 0, 1, 2);
            job.Inputs.OrderBy(item => item.Ordinal).Select(item => item.CaptureSequence)
                .Should().Equal(98, 99, 100, 101, 102);
            job.CanonicalInputs.Should().ContainSingle();
            var validation = await db.CentralTransientValidationJobs.AsNoTracking()
                .Include(item => item.IdentitySlots)
                .SingleAsync(item => item.CentralDerivativeJobId == job.Id).ConfigureAwait(false);
            validation.IdentitySlots.Should().HaveCount(32);
            validation.IdentitySlots.Select(item => item.CandidateId).Should().OnlyHaveUniqueItems();
            validation.ExecutionOptionsIdentitySha256.Should().HaveLength(64);
        }
        await DisableOtherActiveJobsAsync(jobId).ConfigureAwait(false);

        CentralDerivativeJobLease lease;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            lease = (await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("transient-runtime-worker", TimeSpan.FromMinutes(2), CancellationToken.None)
                .ConfigureAwait(false))!;
            lease.JobId.Should().Be(jobId);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            result.Status.Should().Be(hasCandidate ? ProcessingOutcomeStatus.Produced : ProcessingOutcomeStatus.Skipped);
            result.ReasonCode.Should().Be(hasCandidate ? null : TransientCandidateExtractionReasonCodes.NoCandidate);
        }

        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.AsNoTracking().Include(item => item.Attempts)
                .SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            job.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            job.ResultCentralArtifactId.Should().BeNull();
            job.StateReasonCode.Should().Be(hasCandidate
                ? CentralTransientRuntimeReasonCodes.Persisted
                : TransientCandidateExtractionReasonCodes.NoCandidate);
            job.Attempts.Single().Outcome.Should().Be(CentralDerivativeAttemptOutcome.Completed);
            var receipt = await db.CentralTransientExtractionReceipts.AsNoTracking()
                .Include(item => item.Sources)
                .SingleAsync(item => item.CentralDerivativeJobId == jobId).ConfigureAwait(false);
            receipt.Sources.OrderBy(item => item.Ordinal).Select(item => item.Position).Should().Equal(
                TransientTemporalPosition.NMinus2,
                TransientTemporalPosition.NMinus1,
                TransientTemporalPosition.N,
                TransientTemporalPosition.NPlus1,
                TransientTemporalPosition.NPlus2);
            var validation = await db.CentralTransientValidationJobs.AsNoTracking()
                .Include(item => item.IdentitySlots)
                .SingleAsync(item => item.CentralDerivativeJobId == jobId).ConfigureAwait(false);
            validation.IdentitySlots.Count(item => item.State == CentralTransientValidationIdentitySlotState.Committed)
                .Should().Be(hasCandidate ? 1 : 0);
            validation.IdentitySlots.Count(item => item.State == CentralTransientValidationIdentitySlotState.Unused)
                .Should().Be(hasCandidate ? 31 : 32);
            (await db.CentralTransientEventVersions.CountAsync(version =>
                    version.Event!.AgentId == scenario).ConfigureAwait(false))
                .Should().Be(hasCandidate ? 1 : 0);
            if (hasCandidate)
            {
                var assessment = await db.CentralTransientAssessments.AsNoTracking()
                    .SingleAsync(item => item.Event!.AgentId == scenario).ConfigureAwait(false);
                assessment.Authority.Should().Be(TransientAssessmentAuthority.Authoritative);
            }
            var retention = scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>();
            foreach (var sourceId in sources.Values)
            {
                (await retention.IsHeldAsync(sourceId, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            }
        }

        if (hasCandidate)
        {
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var derivativeJobId = await db.CentralDerivativeJobs.AsNoTracking()
                    .Where(item => item.RecipeName == CentralTransientDerivativeRuntime.RecipeName &&
                        item.SourceArtifact!.Frame!.AgentId == scenario)
                    .Select(item => item.Id).SingleAsync().ConfigureAwait(false);
                await db.CentralDerivativeJobs.Where(item => item.Status == CentralDerivativeJobStatus.Pending &&
                        item.Id != derivativeJobId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.AvailableAtUtc, DateTimeOffset.UtcNow.AddHours(1)))
                    .ConfigureAwait(false);
                await db.CentralDerivativeJobs.Where(item => item.Id == derivativeJobId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.AvailableAtUtc, DateTimeOffset.UtcNow))
                    .ConfigureAwait(false);
            }
            CentralDerivativeJobLease derivativeLease;
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                derivativeLease = (await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                    .ClaimNextAsync("transient-derivative-worker", TimeSpan.FromMinutes(2), CancellationToken.None)
                    .ConfigureAwait(false))!;
                derivativeLease.RecipeName.Should().Be(CentralTransientDerivativeRuntime.RecipeName);
            }
            CentralTransientDerivativeBundle derivativeBundle;
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                derivativeBundle = await scope.ServiceProvider.GetRequiredService<ICentralTransientDerivativeBundleFactory>()
                    .CreateAsync(derivativeLease, CancellationToken.None).ConfigureAwait(false);
            }
            var publicationReferences = CentralTransientDerivativeOutputWriter.CreateStorageReferences(
                derivativeLease, derivativeBundle);
            var blockedReference = publicationReferences[0];
            await using (var blockerScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            await using (var publisherScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var blocker = await CentralObjectApplicationLock.AcquireAsync(
                    blockerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                    blockedReference,
                    CancellationToken.None).ConfigureAwait(false);
                try
                {
                    var publisherDb = publisherScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var blockedWriter = new CentralTransientDerivativeOutputWriter(
                        publisherDb,
                        publisherScope.ServiceProvider.GetRequiredService<IObjectStore>(),
                        new CentralTransientEventVersionAppender(publisherDb),
                        TimeProvider.System);
                    var blockedPublication = blockedWriter.PersistAsync(
                        derivativeLease, derivativeBundle, CancellationToken.None);
                    await Task.Delay(100).ConfigureAwait(false);
                    blockedPublication.IsCompleted.Should().BeFalse();

                    var operationToken = Guid.NewGuid();
                    Guid tombstoneArtifactId;
                    Guid tombstoneDispositionId;
                    await using (var tombstoneScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
                    {
                        var db = tombstoneScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        var sourceId = await db.CentralDerivativeJobs.AsNoTracking()
                            .Where(item => item.Id == derivativeLease.JobId)
                            .Select(item => item.SourceCentralArtifactId).SingleAsync().ConfigureAwait(false);
                        var source = await db.CentralArtifacts.AsNoTracking()
                            .SingleAsync(item => item.Id == sourceId).ConfigureAwait(false);
                        var now = DateTimeOffset.UtcNow;
                        var tombstone = new CentralArtifact
                        {
                            CentralFrameId = source.CentralFrameId,
                            DevicePublicId = source.DevicePublicId,
                            ArtifactId = Guid.NewGuid(),
                            Role = FrameArtifactRole.Metadata,
                            RecipeVersion = "retention-race-v1",
                            ManifestSchemaVersion = "central-v1",
                            MediaType = "application/octet-stream",
                            ByteLength = 1,
                            ChecksumSha256 = new string('A', 64),
                            StorageReference = blockedReference,
                            ReceivedAtUtc = now,
                            IdempotencyKey = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                            ObjectState = CentralArtifactObjectState.Expired,
                            ReconstructionState = CentralReconstructionState.Complete,
                            RetentionDeletionToken = operationToken,
                            RetentionDeletionRequestedAtUtc = now
                        };
                        var objectKey = blockedReference[$"object://{Bucket}/".Length..];
                        var disposition = new CentralObjectRecoveryDisposition
                        {
                            SourceObjectIdentitySha256 = CentralObjectOwnershipFence.CreateObjectKeyIdentity(objectKey),
                            SourceObjectKey = objectKey,
                            Kind = CentralObjectRecoveryKinds.ExpiredDelete,
                            State = CentralObjectRecoveryStates.PendingDelete,
                            CentralArtifactId = tombstone.Id,
                            OperationToken = operationToken,
                            ByteLength = 1,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            NextAttemptAtUtc = now
                        };
                        db.CentralArtifacts.Add(tombstone);
                        db.CentralObjectRecoveryDispositions.Add(disposition);
                        await db.SaveChangesAsync().ConfigureAwait(false);
                        tombstoneArtifactId = tombstone.Id;
                        tombstoneDispositionId = disposition.Id;
                        (await db.CentralTransientDerivativeOutputIntents.CountAsync(item =>
                            item.CentralDerivativeJobId == derivativeLease.JobId).ConfigureAwait(false)).Should().Be(0);
                    }
                    await blocker.DisposeAsync().ConfigureAwait(false);
                    var rejected = async () => await blockedPublication.ConfigureAwait(false);
                    await rejected.Should().ThrowAsync<CentralDerivativeJobStateException>().ConfigureAwait(false);

                    await using (var cleanupScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
                    {
                        var db = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        (await db.CentralTransientDerivativeOutputIntents.CountAsync(item =>
                            item.CentralDerivativeJobId == derivativeLease.JobId).ConfigureAwait(false)).Should().Be(0);
                        await db.CentralObjectRecoveryDispositions.Where(item => item.Id == tombstoneDispositionId)
                            .ExecuteDeleteAsync().ConfigureAwait(false);
                        await db.CentralArtifacts.Where(item => item.Id == tombstoneArtifactId)
                            .ExecuteDeleteAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    await blocker.DisposeAsync().ConfigureAwait(false);
                }
            }
            var lockLossInterceptor = new BlockAfterRetirementReadInterceptor();
            var lockLossApplicationName = $"HVO.Retention.PublisherLoss.{Guid.NewGuid():N}";
            var lockLossConnectionString = new SqlConnectionStringBuilder(
                AssemblyHooks.Fixture.SqlServerConnectionString)
            {
                ApplicationName = lockLossApplicationName
            }.ConnectionString;
            await using (var lockLossDb = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlServer(lockLossConnectionString)
                    .AddInterceptors(lockLossInterceptor)
                    .Options))
            {
                var writer = new CentralTransientDerivativeOutputWriter(
                    lockLossDb,
                    AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IObjectStore>(),
                    new CentralTransientEventVersionAppender(lockLossDb),
                    TimeProvider.System);
                var stalePublication = writer.PersistAsync(
                    derivativeLease, derivativeBundle, CancellationToken.None);
                await lockLossInterceptor.Entered.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await KillApplicationLockSessionsAsync(
                    AssemblyHooks.Fixture.SqlServerConnectionString, lockLossApplicationName).ConfigureAwait(false);

                var operationToken = Guid.NewGuid();
                Guid tombstoneArtifactId;
                Guid tombstoneDispositionId;
                await using (var tombstoneScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
                {
                    var db = tombstoneScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var sourceId = await db.CentralDerivativeJobs.AsNoTracking()
                        .Where(item => item.Id == derivativeLease.JobId)
                        .Select(item => item.SourceCentralArtifactId).SingleAsync().ConfigureAwait(false);
                    var source = await db.CentralArtifacts.AsNoTracking()
                        .SingleAsync(item => item.Id == sourceId).ConfigureAwait(false);
                    var now = DateTimeOffset.UtcNow;
                    var tombstone = new CentralArtifact
                    {
                        CentralFrameId = source.CentralFrameId,
                        DevicePublicId = source.DevicePublicId,
                        ArtifactId = Guid.NewGuid(),
                        Role = FrameArtifactRole.Metadata,
                        RecipeVersion = "retention-lock-loss-v1",
                        ManifestSchemaVersion = "central-v1",
                        MediaType = "application/octet-stream",
                        ByteLength = 1,
                        ChecksumSha256 = new string('B', 64),
                        StorageReference = blockedReference,
                        ReceivedAtUtc = now,
                        IdempotencyKey = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                        ObjectState = CentralArtifactObjectState.Expired,
                        ReconstructionState = CentralReconstructionState.Complete,
                        RetentionDeletionToken = operationToken,
                        RetentionDeletionRequestedAtUtc = now
                    };
                    var objectKey = blockedReference[$"object://{Bucket}/".Length..];
                    var disposition = new CentralObjectRecoveryDisposition
                    {
                        SourceObjectIdentitySha256 = CentralObjectOwnershipFence.CreateObjectKeyIdentity(objectKey),
                        SourceObjectKey = objectKey,
                        Kind = CentralObjectRecoveryKinds.ExpiredDelete,
                        State = CentralObjectRecoveryStates.PendingDelete,
                        CentralArtifactId = tombstone.Id,
                        OperationToken = operationToken,
                        ByteLength = 1,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        NextAttemptAtUtc = now
                    };
                    db.AddRange(tombstone, disposition);
                    await db.SaveChangesAsync().ConfigureAwait(false);
                    tombstoneArtifactId = tombstone.Id;
                    tombstoneDispositionId = disposition.Id;
                }
                lockLossInterceptor.Release();
                var rejected = async () => await stalePublication.ConfigureAwait(false);
                await rejected.Should().ThrowAsync<CentralDerivativeJobStateException>().ConfigureAwait(false);

                await using var cleanupScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
                var cleanup = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var rejectedIntents = await cleanup.CentralTransientDerivativeOutputIntents.AsNoTracking()
                    .Where(item => item.CentralDerivativeJobId == derivativeLease.JobId)
                    .ToArrayAsync().ConfigureAwait(false);
                rejectedIntents.Length.Should().BeOneOf(0, 5);
                rejectedIntents.Should().OnlyContain(item =>
                    item.ObjectState == CentralArtifactObjectState.Expired
                    && item.StateReasonCode == "retention.publication-rejected");
                await cleanup.CentralObjectRecoveryDispositions.Where(item => item.Id == tombstoneDispositionId)
                    .ExecuteDeleteAsync().ConfigureAwait(false);
                await cleanup.CentralArtifacts.Where(item => item.Id == tombstoneArtifactId)
                    .ExecuteDeleteAsync().ConfigureAwait(false);
                if (rejectedIntents.Length != 0)
                {
                    await cleanup.CentralTransientDerivativeOutputIntents.Where(item =>
                            item.CentralDerivativeJobId == derivativeLease.JobId
                            && item.CommittedAtUtc == null
                            && item.ObjectState == CentralArtifactObjectState.Expired)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(item => item.ObjectState, CentralArtifactObjectState.Pending)
                            .SetProperty(item => item.StateReasonCode, "transient-derivative.output-pending"))
                        .ConfigureAwait(false);
                }
            }
            using (var copyFault = new FailCanonicalCopyHandler(2) { InnerHandler = new SocketsHttpHandler() })
            using (var faultMinio = CreateMinio(copyFault))
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var writer = new CentralTransientDerivativeOutputWriter(
                    db,
                    ObjectStoreTestClient.Create(faultMinio),
                    new CentralTransientEventVersionAppender(db),
                    TimeProvider.System);
                var action = () => writer.PersistAsync(
                    derivativeLease, derivativeBundle, CancellationToken.None);
                await action.Should().ThrowAsync<Exception>().ConfigureAwait(false);
            }
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var partial = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                    .CentralTransientDerivativeOutputIntents.AsNoTracking()
                    .Where(item => item.CentralDerivativeJobId == derivativeLease.JobId)
                    .ToArrayAsync().ConfigureAwait(false);
                partial.Should().HaveCount(5);
                partial.Count(item => item.ObjectState == CentralArtifactObjectState.Available).Should().Be(1);
                partial.Count(item => item.ObjectState == CentralArtifactObjectState.Pending).Should().Be(4);
                partial.Should().OnlyContain(item => item.CommittedAtUtc == null);
                await Phase14ScenarioEvidence.RecordAsync(
                    "central-window-output-object-publication",
                    $"{scenario}-canonical-copy-fault",
                    "FailCanonicalCopyHandler",
                    ["canonical-copy-fault-observed", "one-output-object-published", "four-output-objects-remained-pending", "no-output-intents-committed"],
                    [
                        new Phase14EvidenceMeasurement("published-output-objects", 1, "count"),
                        new Phase14EvidenceMeasurement("pending-output-objects", 4, "count")
                    ]).ConfigureAwait(false);
            }

            var commitFault = new ThrowBeforeCommitInterceptor(6);
            await using (var faultDb = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlServer(AssemblyHooks.Fixture.SqlServerConnectionString)
                    .AddInterceptors(commitFault)
                    .Options))
            {
                var writer = new CentralTransientDerivativeOutputWriter(
                    faultDb,
                    AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IObjectStore>(),
                    new CentralTransientEventVersionAppender(faultDb),
                    TimeProvider.System);
                var action = () => writer.PersistAsync(
                    derivativeLease, derivativeBundle, CancellationToken.None);
                await action.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
                commitFault.Triggered.Should().BeTrue();
            }
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var verified = await db.CentralTransientDerivativeOutputIntents.AsNoTracking()
                    .Where(item => item.CentralDerivativeJobId == derivativeLease.JobId)
                    .ToArrayAsync().ConfigureAwait(false);
                verified.Should().HaveCount(5).And.OnlyContain(item =>
                    item.ObjectState == CentralArtifactObjectState.Available
                    && item.ObjectVerifiedAtUtc != null
                    && item.StorageETag != null
                    && item.CommittedAtUtc == null);
                (await db.CentralTransientDerivatives.CountAsync(item =>
                    item.CentralDerivativeJobId == derivativeLease.JobId).ConfigureAwait(false)).Should().Be(0);
                await Phase14ScenarioEvidence.RecordAsync(
                    "central-window-output-finalization-commit",
                    $"{scenario}-finalization-commit-fault",
                    "ThrowBeforeCommitInterceptor",
                    ["finalization-commit-fault-observed", "all-output-objects-verified", "no-output-intents-committed", "no-derivative-rows-committed"],
                    [
                        new Phase14EvidenceMeasurement("verified-output-objects", verified.Length, "count"),
                        new Phase14EvidenceMeasurement("committed-derivatives", 0, "count")
                    ]).ConfigureAwait(false);
            }
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var produced = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                    .ExecuteAsync(derivativeLease, CancellationToken.None).ConfigureAwait(false);
                produced.Status.Should().Be(ProcessingOutcomeStatus.Produced);
                produced.ReasonCode.Should().Be("transient-derivative.persisted");
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var intents = await db.CentralTransientDerivativeOutputIntents.AsNoTracking()
                    .Where(item => item.CentralDerivativeJobId == derivativeLease.JobId)
                    .ToArrayAsync().ConfigureAwait(false);
                intents.Should().HaveCount(5).And.OnlyContain(item =>
                    item.CommittedAtUtc != null && item.ObjectState == CentralArtifactObjectState.Available);
                intents.Select(item => item.Kind).Should().BeEquivalentTo(Enum.GetValues<TransientDerivativeKind>());
                (await db.CentralTransientDerivatives.CountAsync(item =>
                    item.CentralDerivativeJobId == derivativeLease.JobId).ConfigureAwait(false)).Should().Be(5);
                var eventId = await db.CentralTransientDerivativeJobs.AsNoTracking()
                    .Where(item => item.CentralDerivativeJobId == derivativeLease.JobId)
                    .Select(item => item.CentralTransientEventId).SingleAsync().ConfigureAwait(false);
                var principal = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim("sub", "derivative-test-admin"),
                    new Claim("account_type", "User"),
                    new Claim("scope", "api.admin")
                ], CanonicalCredentialClaims.BearerAuthenticationType));
                var retrieval = scope.ServiceProvider.GetRequiredService<ICentralTransientDerivativeRetrievalService>();
                foreach (var intent in intents)
                {
                    byte[] derivativeBytes;
                    await using (var content = await retrieval.GetAsync(
                                     principal, eventId, intent.DerivativeId, CancellationToken.None)
                                     .ConfigureAwait(false))
                    {
                        content.Status.Should().Be(CentralTransientDerivativeLookupStatus.Found);
                        await using var derivativePayload = new MemoryStream();
                        await retrieval.CopyToAsync(content, derivativePayload, null, CancellationToken.None)
                            .ConfigureAwait(false);
                        derivativeBytes = derivativePayload.ToArray();
                        Convert.ToHexString(SHA256.HashData(derivativeBytes)).Should()
                            .Be(intent.ChecksumSha256);
                        var rangeLength = Math.Min(3, derivativeBytes.Length - 1);
                        await using var rangePayload = new MemoryStream();
                        await retrieval.CopyToAsync(
                            content,
                            rangePayload,
                            new CentralArtifactByteRange(1, rangeLength),
                            CancellationToken.None).ConfigureAwait(false);
                        rangePayload.ToArray().Should().Equal(derivativeBytes.AsSpan(1, rangeLength).ToArray());
                    }
                    if (intent.Id == intents[0].Id)
                    {
                        var objectKey = intent.StorageReference[$"object://{Bucket}/".Length..];
                        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
                        await using (var corrupt = new MemoryStream(new byte[derivativeBytes.Length], writable: false))
                        {
                            await minio.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(objectKey)
                                .WithStreamData(corrupt).WithObjectSize(corrupt.Length)
                                .WithContentType(intent.MediaType), CancellationToken.None).ConfigureAwait(false);
                        }
                        (await retrieval.GetAsync(principal, eventId, intent.DerivativeId, CancellationToken.None)
                            .ConfigureAwait(false)).Status.Should().Be(CentralTransientDerivativeLookupStatus.IntegrityFailure);
                        await using var restore = new MemoryStream(derivativeBytes, writable: false);
                        await minio.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(objectKey)
                            .WithStreamData(restore).WithObjectSize(restore.Length)
                            .WithContentType(intent.MediaType), CancellationToken.None).ConfigureAwait(false);
                    }
                }
                (await retrieval.GetAsync(
                    new ClaimsPrincipal(), eventId, intents[0].DerivativeId, CancellationToken.None)
                    .ConfigureAwait(false)).Status.Should().Be(CentralTransientDerivativeLookupStatus.NotFound);
                (await db.CentralTransientEventVersions.CountAsync(version =>
                    version.Event!.AgentId == scenario).ConfigureAwait(false)).Should().Be(2);
                await db.CentralDerivativeJobs.Where(item => item.Id == derivativeLease.JobId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, CentralDerivativeJobStatus.Pending)
                        .SetProperty(item => item.AvailableAtUtc, DateTimeOffset.UtcNow)
                        .SetProperty(item => item.CompletedAtUtc, (DateTimeOffset?)null)
                        .SetProperty(item => item.StateReasonCode, (string?)null))
                    .ConfigureAwait(false);
            }
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var retry = (await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                    .ClaimNextAsync("transient-derivative-restart", TimeSpan.FromMinutes(2), CancellationToken.None)
                    .ConfigureAwait(false))!;
                retry.JobId.Should().Be(derivativeLease.JobId);
                var adopted = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                    .ExecuteAsync(retry, CancellationToken.None).ConfigureAwait(false);
                adopted.ReasonCode.Should().Be("transient-derivative.output-adopted");
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                (await db.CentralTransientDerivatives.CountAsync(item =>
                    item.CentralDerivativeJobId == derivativeLease.JobId).ConfigureAwait(false)).Should().Be(5);
                (await db.CentralTransientEventVersions.CountAsync(version =>
                    version.Event!.AgentId == scenario).ConfigureAwait(false)).Should().Be(2);
            }

            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.CentralDerivativeJobs.Where(item => item.Id == jobId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, CentralDerivativeJobStatus.Pending)
                        .SetProperty(item => item.AvailableAtUtc, DateTimeOffset.UtcNow)
                        .SetProperty(item => item.CompletedAtUtc, (DateTimeOffset?)null)
                        .SetProperty(item => item.StateReasonCode, (string?)null))
                    .ConfigureAwait(false);
                await db.CentralDerivativeJobs.Where(item =>
                        item.RecipeName == CentralTransientDerivativeRuntime.RecipeName &&
                        item.SourceArtifact!.Frame!.AgentId == scenario)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.AvailableAtUtc, DateTimeOffset.UtcNow.AddHours(1)))
                    .ConfigureAwait(false);
            }
            CentralDerivativeJobLease retryLease;
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                retryLease = (await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                    .ClaimNextAsync("transient-restart-worker", TimeSpan.FromMinutes(2), CancellationToken.None)
                    .ConfigureAwait(false))!;
            }
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var adopted = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                    .ExecuteAsync(retryLease, CancellationToken.None).ConfigureAwait(false);
                adopted.ReasonCode.Should().Be(CentralTransientRuntimeReasonCodes.OutputAdopted);
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var job = await db.CentralDerivativeJobs.AsNoTracking().Include(item => item.Attempts)
                    .SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
                job.Status.Should().Be(CentralDerivativeJobStatus.Completed);
                job.StateReasonCode.Should().Be(CentralTransientRuntimeReasonCodes.OutputAdopted);
                job.Attempts.OrderBy(item => item.AttemptNumber).Select(item => item.Outcome)
                    .Should().Equal(CentralDerivativeAttemptOutcome.Completed, CentralDerivativeAttemptOutcome.Completed);
                (await db.CentralTransientEventVersions.CountAsync(version => version.Event!.AgentId == scenario)
                    .ConfigureAwait(false)).Should().Be(2);
            }

            var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            var expiredToken = Guid.NewGuid();
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.CentralDerivativeJobs.Where(item => item.Id == jobId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, CentralDerivativeJobStatus.Leased)
                        .SetProperty(item => item.AttemptCount, 2)
                        .SetProperty(item => item.MaxAttempts, 2)
                        .SetProperty(item => item.CompletedAtUtc, (DateTimeOffset?)null)
                        .SetProperty(item => item.LeaseOwner, "expired-committed-worker")
                        .SetProperty(item => item.LeaseToken, expiredToken)
                        .SetProperty(item => item.LeaseAcquiredAtUtc, expiredAt.AddMinutes(-1))
                        .SetProperty(item => item.LeaseExpiresAtUtc, expiredAt))
                    .ConfigureAwait(false);
                await db.CentralDerivativeJobAttempts.Where(item => item.CentralDerivativeJobId == jobId &&
                        item.AttemptNumber == 2)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Outcome, CentralDerivativeAttemptOutcome.Leased)
                        .SetProperty(item => item.EndedAtUtc, (DateTimeOffset?)null)
                        .SetProperty(item => item.ReasonCode, (string?)null)
                        .SetProperty(item => item.LeaseExpiresAtUtc, expiredAt))
                    .ConfigureAwait(false);
            }
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
                (await service.ClaimNextAsync("max-attempt-adopter", TimeSpan.FromMinutes(1), CancellationToken.None)
                    .ConfigureAwait(false)).Should().BeNull();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var adopted = await db.CentralDerivativeJobs.AsNoTracking().Include(item => item.Attempts)
                    .SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
                adopted.Status.Should().Be(CentralDerivativeJobStatus.Completed);
                adopted.StateReasonCode.Should().Be(CentralTransientRuntimeReasonCodes.OutputAdopted);
                adopted.Attempts.Single(item => item.AttemptNumber == 2).Outcome
                    .Should().Be(CentralDerivativeAttemptOutcome.Completed);
            }

            await using (var corruptScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var db = corruptScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var source = await db.CentralArtifacts.AsNoTracking()
                    .SingleAsync(item => item.Id == sources[100]).ConfigureAwait(false);
                var objectName = source.StorageReference[$"object://{Bucket}/".Length..];
                await using var corrupt = new MemoryStream(CreatePayload(101), writable: false);
                await corruptScope.ServiceProvider.GetRequiredService<IMinioClient>().PutObjectAsync(new PutObjectArgs()
                    .WithBucket(Bucket)
                    .WithObject(objectName)
                    .WithStreamData(corrupt)
                    .WithObjectSize(corrupt.Length)
                    .WithContentType(source.MediaType), CancellationToken.None).ConfigureAwait(false);
            }

            async Task InvalidateFromRetrievalAsync()
            {
                await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var source = await db.CentralArtifacts.AsNoTracking()
                    .SingleAsync(item => item.Id == sources[100]).ConfigureAwait(false);
                await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetrievalService>()
                    .MarkUnavailableAsync(
                        source, "object.checksum-mismatch", quarantine: true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            await Task.WhenAll(InvalidateFromRetrievalAsync(), InvalidateFromRetrievalAsync()).ConfigureAwait(false);

            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.ChangeTracker.Clear();
                var versions = await db.CentralTransientEventVersions.AsNoTracking()
                    .Where(item => item.Event!.AgentId == scenario)
                    .OrderBy(item => item.Version)
                    .ToListAsync().ConfigureAwait(false);
                versions.Select(item => item.Version).Should().Equal(1, 2, 3);
                versions[^1].State.Should().Be(TransientEventState.NeedsReview);
                (await db.CentralTransientValidationOutcomeVersions.AsNoTracking()
                    .Where(item => item.CentralDerivativeJobId == jobId)
                    .OrderBy(item => item.Version)
                    .Select(item => item.ReasonCode)
                    .ToListAsync().ConfigureAwait(false)).Should().Equal(
                        CentralTransientRuntimeReasonCodes.Persisted,
                        "object.checksum-mismatch");
                (await db.CentralArtifacts.AsNoTracking().Where(item => item.Id == sources[100])
                    .Select(item => item.ObjectState).SingleAsync().ConfigureAwait(false))
                    .Should().Be(CentralArtifactObjectState.Quarantined);
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AdjacentMeasuredCandidates_AssociateAndPreservePreviousEventVersion(bool reverseExecution)
    {
        var scenario = $"transient-boundary-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            StarMaximumMagnitude = -30
        };
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 100, 103, 98, 102, 99, 101 })
        {
            var payload = sequence is 100 or 101
                ? CreatePayload([100, 1_000, 1_000, 100])
                : CreatePayload(100);
            var sourceId = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, payload, "boundary-compatible")
                .ConfigureAwait(false);
            sources.Add(sequence, sourceId);
            await ScheduleTransientAsync(sourceId, options).ConfigureAwait(false);
        }

        Guid firstJobId;
        Guid secondJobId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            firstJobId = await db.CentralDerivativeJobs.Where(item =>
                    item.SourceCentralArtifactId == sources[100]
                    && item.RecipeName == CentralTransientRuntime.RecipeName)
                .Select(item => item.Id).SingleAsync().ConfigureAwait(false);
            secondJobId = await db.CentralDerivativeJobs.Where(item =>
                    item.SourceCentralArtifactId == sources[101]
                    && item.RecipeName == CentralTransientRuntime.RecipeName)
                .Select(item => item.Id).SingleAsync().ConfigureAwait(false);
            var resolver = scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>();
            await resolver.ResolveAsync(firstJobId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            await resolver.ResolveAsync(secondJobId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
        var initialJobId = reverseExecution ? secondJobId : firstJobId;
        var followingJobId = reverseExecution ? firstJobId : secondJobId;
        await DisableOtherActiveJobsAsync(initialJobId).ConfigureAwait(false);
        await ExecuteClaimedTransientAsync(initialJobId, "boundary-first").ConfigureAwait(false);

        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CentralDerivativeJobs.Where(item => item.Id == followingJobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, CentralDerivativeJobStatus.Pending)
                    .SetProperty(item => item.AvailableAtUtc, DateTimeOffset.UtcNow)
                    .SetProperty(item => item.StateReasonCode, (string?)null)
                    .SetProperty(item => item.LastError, (string?)null))
                .ConfigureAwait(false);
        }
        await DisableOtherActiveJobsAsync(followingJobId).ConfigureAwait(false);
        await ExecuteClaimedTransientAsync(followingJobId, "boundary-second").ConfigureAwait(false);

        await using var verifyScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var transientEvent = await verify.CentralTransientEvents.AsNoTracking()
            .Include(item => item.Versions)
            .SingleAsync(item => item.AgentId == scenario).ConfigureAwait(false);
        transientEvent.Versions.OrderBy(item => item.Version).Select(item => item.Version).Should().Equal(1, 2);
        var canonical = TransientContractJson.ParseEvent(System.Text.Encoding.UTF8.GetBytes(
            transientEvent.Versions.Single(item => item.Version == 2).CanonicalEventJson));
        canonical.Validation.IsValid.Should().BeTrue();
        canonical.Value!.Observations.Should().HaveCount(2);
        canonical.Value.Assessments.Should().HaveCount(2);
        canonical.Value.PreviousEventVersionId.Should().Be(
            transientEvent.Versions.Single(item => item.Version == 1).EventVersionId);
        var followingReceipt = await verify.CentralTransientExtractionReceipts.AsNoTracking()
            .SingleAsync(item => item.CentralDerivativeJobId == followingJobId).ConfigureAwait(false);
        var followingExtraction = TransientCandidateExtractionJson.Parse(
            System.Text.Encoding.UTF8.GetBytes(followingReceipt.CanonicalReceiptJson));
        var initialPosition = reverseExecution
            ? TransientTemporalPosition.NPlus1
            : TransientTemporalPosition.NMinus1;
        followingExtraction.Background.Sources.Single(item => item.Position == initialPosition).Disposition
            .Should().Be(TransientTemporalSourceDisposition.ExcludedKnownEvent);
        followingExtraction.CenteredContextConverged.Should().BeFalse();
        canonical.Value.State.Should().Be(TransientEventState.Pending);
        var eventIds = await verify.CentralTransientValidationIdentitySlots.AsNoTracking()
            .Where(item => item.CentralDerivativeJobId == firstJobId || item.CentralDerivativeJobId == secondJobId)
            .Where(item => item.State == CentralTransientValidationIdentitySlotState.Committed)
            .Select(item => item.PersistedEventId).ToListAsync().ConfigureAwait(false);
        eventIds.Should().HaveCount(2).And.OnlyContain(item => item == transientEvent.EventId);

        var twoObservationVersionId = transientEvent.Versions.Single(item => item.Version == 2).EventVersionId;
        var derivativeJobId = await verify.CentralTransientDerivativeJobs.AsNoTracking()
            .Where(item => item.SourceEventVersionId == twoObservationVersionId)
            .Select(item => item.CentralDerivativeJobId).SingleAsync().ConfigureAwait(false);
        await DisableOtherActiveJobsAsync(derivativeJobId).ConfigureAwait(false);
        var derivativeResult = await ExecuteClaimedTransientAsync(
            derivativeJobId, "boundary-derivative", ProcessingOutcomeStatus.Produced).ConfigureAwait(false);
        derivativeResult.ReasonCode.Should().Be("transient-derivative.persisted");
        await using var derivativeScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var derivativeDb = derivativeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await derivativeDb.CentralTransientDerivatives.CountAsync(item =>
            item.CentralDerivativeJobId == derivativeJobId).ConfigureAwait(false)).Should().Be(5);
        (await derivativeDb.CentralTransientDerivativeSources.CountAsync(item =>
            item.Derivative!.CentralDerivativeJobId == derivativeJobId).ConfigureAwait(false)).Should().Be(10);
        var latest = await derivativeDb.CentralTransientEventVersions.AsNoTracking()
            .Where(item => item.CentralTransientEventId == transientEvent.Id)
            .OrderByDescending(item => item.Version).FirstAsync().ConfigureAwait(false);
        var derivativeEvent = TransientContractJson.ParseEvent(System.Text.Encoding.UTF8.GetBytes(
            latest.CanonicalEventJson));
        derivativeEvent.Validation.IsValid.Should().BeTrue();
        derivativeEvent.Value!.Observations.Should().HaveCount(2);
        derivativeEvent.Value.Derivatives.Should().HaveCount(5);

        var current = await derivativeDb.CentralTransientEventCurrent.AsNoTracking()
            .SingleAsync(item => item.CentralTransientEventId == transientEvent.Id).ConfigureAwait(false);
        var admin = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "boundary-release-admin"),
            new Claim("scope", "api.admin"),
            new Claim("account_type", "User")
        ], CanonicalCredentialClaims.BearerAuthenticationType));
        var review = await new CentralTransientReviewService(
                derivativeDb,
                new CentralTransientEventVersionAppender(derivativeDb),
                TimeProvider.System)
            .ReviewAsync(
                admin,
                transientEvent.Id,
                current.RowVersion,
                $"boundary-release-review-{Guid.NewGuid():N}",
                new CentralTransientReviewRequest(
                    current.ActiveAssessmentId,
                    TransientReviewDisposition.Rejected,
                    null,
                    ["human.retention-approved"]),
                CancellationToken.None).ConfigureAwait(false);
        review.Status.Should().Be(CentralTransientReviewMutationStatus.Applied);
        await derivativeDb.CentralDerivativeJobs.Where(item =>
                item.Status == CentralDerivativeJobStatus.Waiting ||
                item.Status == CentralDerivativeJobStatus.Pending ||
                item.Status == CentralDerivativeJobStatus.RetryableFailure)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
        derivativeDb.ChangeTracker.Clear();
        var releaseCurrent = await derivativeDb.CentralTransientEventCurrent.AsNoTracking()
            .SingleAsync(item => item.CentralTransientEventId == transientEvent.Id).ConfigureAwait(false);
        var releaseService = new CentralTransientPayloadReleaseService(
            derivativeDb,
            new CentralArtifactRetentionReferences(derivativeDb),
            derivativeScope.ServiceProvider.GetRequiredService<IObjectStore>(),
            Microsoft.Extensions.Options.Options.Create(new CentralTransientPayloadReleaseOptions { Enabled = true }),
            TimeProvider.System);
        var release = await releaseService.ReleaseAsync(
            admin,
            transientEvent.Id,
            releaseCurrent.RowVersion,
            $"boundary-release-{Guid.NewGuid():N}",
            CancellationToken.None).ConfigureAwait(false);
        release.Status.Should().Be(CentralTransientPayloadReleaseStatus.Released);
        var releasedDerivative = derivativeEvent.Value.Derivatives[0];
        (await derivativeScope.ServiceProvider.GetRequiredService<ICentralTransientDerivativeRetrievalService>()
            .GetAsync(admin, transientEvent.Id, releasedDerivative.DerivativeId, CancellationToken.None)
            .ConfigureAwait(false)).Status.Should().Be(CentralTransientDerivativeLookupStatus.Gone);
    }

    [TestMethod]
    public async Task ProvisionalContextDependencies_EventuallyAppendCenteredEventVersion()
    {
        var scenario = $"transient-convergence-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            StarMaximumMagnitude = -30
        };
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 100, 104, 96, 102, 98, 103, 97, 101, 99 })
        {
            var payload = sequence == 100
                ? CreatePayload([100, 1_000, 1_000, 100])
                : CreatePayload(100);
            var sourceId = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, payload, "convergence-compatible")
                .ConfigureAwait(false);
            sources.Add(sequence, sourceId);
            await ScheduleTransientAsync(sourceId, options).ConfigureAwait(false);
        }

        Guid provisionalJobId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            provisionalJobId = await db.CentralDerivativeJobs.Where(item =>
                    item.SourceCentralArtifactId == sources[100] &&
                    item.RecipeName == CentralTransientRuntime.RecipeName)
                .Select(item => item.Id).SingleAsync().ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveAsync(provisionalJobId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
        await DisableOtherActiveJobsAsync(provisionalJobId).ConfigureAwait(false);
        await ExecuteClaimedTransientAsync(provisionalJobId, "convergence-provisional").ConfigureAwait(false);

        Guid[] dependencyJobIds;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dependencies = await db.CentralTransientContextDependencies.AsNoTracking()
                .Where(item => item.CentralDerivativeJobId == provisionalJobId)
                .OrderBy(item => item.Ordinal)
                .ToListAsync().ConfigureAwait(false);
            dependencies.Should().HaveCount(4);
            dependencies.Should().OnlyContain(item => item.RequiredCentralDerivativeJobId.HasValue);
            dependencyJobIds = dependencies.Select(item => item.RequiredCentralDerivativeJobId!.Value).ToArray();
            var duplicateSource = await db.CentralTransientValidationJobs.AsNoTracking()
                .Include(item => item.Job)
                .SingleAsync(item => item.CentralDerivativeJobId == dependencyJobIds[0]).ConfigureAwait(false);
            var duplicateJobId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            db.CentralDerivativeJobs.Add(new CentralDerivativeJob
            {
                Id = duplicateJobId,
                SourceCentralArtifactId = duplicateSource.Job!.SourceCentralArtifactId,
                TargetRole = duplicateSource.Job.TargetRole,
                TargetRecipeVersion = duplicateSource.Job.TargetRecipeVersion,
                TargetVariant = duplicateSource.Job.TargetVariant,
                RecipeName = duplicateSource.Job.RecipeName,
                RecipeOptionsJson = duplicateSource.Job.RecipeOptionsJson,
                InputSelectorJson = duplicateSource.Job.InputSelectorJson,
                RequestedRecipeIdentitySha256 = duplicateSource.Job.RequestedRecipeIdentitySha256,
                ExpectedRecipeIdentitySha256 = duplicateSource.Job.ExpectedRecipeIdentitySha256,
                RequestIdentitySha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                Status = CentralDerivativeJobStatus.TerminalFailure,
                AttemptCount = 1,
                MaxAttempts = duplicateSource.Job.MaxAttempts,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LastFailedAtUtc = now,
                LastError = "test.duplicate-terminal"
            });
            db.CentralTransientValidationJobs.Add(new CentralTransientValidationJob
            {
                CentralDerivativeJobId = duplicateJobId,
                AgentId = duplicateSource.AgentId,
                SubmissionSchemaVersion = duplicateSource.SubmissionSchemaVersion,
                SubmissionIdentitySha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                ExecutionOptionsJson = duplicateSource.ExecutionOptionsJson,
                ExecutionOptionsIdentitySha256 = duplicateSource.ExecutionOptionsIdentitySha256,
                CreatedAtUtc = now
            });
            var pinnedDependency = await db.CentralTransientContextDependencies.SingleAsync(item =>
                item.CentralDerivativeJobId == provisionalJobId && item.Ordinal == dependencies[0].Ordinal)
                .ConfigureAwait(false);
            pinnedDependency.RequiredCentralDerivativeJobId = duplicateJobId;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        foreach (var dependencyJobId in dependencyJobIds)
        {
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var hasFrozenInputs = await db.CentralDerivativeJobs.AsNoTracking()
                    .Where(item => item.Id == dependencyJobId)
                    .Select(item => item.InputSetIdentitySha256 != null)
                    .SingleAsync().ConfigureAwait(false);
                await db.CentralDerivativeJobs.Where(item => item.Id == dependencyJobId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, hasFrozenInputs
                            ? CentralDerivativeJobStatus.Pending
                            : CentralDerivativeJobStatus.Waiting)
                        .SetProperty(item => item.AvailableAtUtc,
                            hasFrozenInputs ? DateTimeOffset.UtcNow : (DateTimeOffset?)null)
                        .SetProperty(item => item.CompletedAtUtc, (DateTimeOffset?)null)
                        .SetProperty(item => item.StateReasonCode, hasFrozenInputs
                            ? null
                            : CentralDerivativeWindowReasonCodes.WaitingRequiredInput)
                        .SetProperty(item => item.LastError, (string?)null))
                    .ConfigureAwait(false);
                if (!hasFrozenInputs)
                {
                    await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                        .ResolveAsync(dependencyJobId, DateTimeOffset.UtcNow, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            await DisableOtherActiveJobsAsync(dependencyJobId).ConfigureAwait(false);
            _ = await ExecuteClaimedTransientAsync(
                dependencyJobId, $"convergence-context-{dependencyJobId:N}", expectedStatus: null)
                .ConfigureAwait(false);
        }

        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralTransientValidationJobs.AsNoTracking().CountAsync(item =>
                dependencyJobIds.Contains(item.CentralDerivativeJobId) &&
                item.CommittedAtUtc != null &&
                item.ExtractionReceipt != null).ConfigureAwait(false)).Should().Be(4);
            var scheduler = new CentralDerivativeJobScheduler(
                db,
                new CentralDerivativeRecipeCatalog(options),
                scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>());
            var retrospective = new CentralTransientRetrospectiveScheduler(
                db,
                scheduler,
                new CentralDerivativeRecipeCatalog(options),
                Options.Create(options),
                scope.ServiceProvider.GetRequiredService<CentralDerivativeWorkerTelemetry>(),
                scope.ServiceProvider.GetRequiredService<ILogger<CentralTransientRetrospectiveScheduler>>());
            await retrospective.ScheduleBatchAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            (await db.CentralTransientContextDependencies.AsNoTracking()
                .Where(item => item.CentralDerivativeJobId == provisionalJobId)
                .OrderBy(item => item.Ordinal)
                .Select(item => item.RequiredCentralDerivativeJobId!.Value)
                .ToArrayAsync().ConfigureAwait(false)).Should().Equal(dependencyJobIds);
        }

        Guid successorJobId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            successorJobId = await db.CentralTransientValidationJobs.AsNoTracking()
                .Where(item => item.ProvisionalCentralDerivativeJobId == provisionalJobId)
                .Select(item => item.CentralDerivativeJobId)
                .SingleAsync().ConfigureAwait(false);
        }
        await DisableOtherActiveJobsAsync(successorJobId).ConfigureAwait(false);
        await ExecuteClaimedTransientAsync(successorJobId, "convergence-centered").ConfigureAwait(false);

        await using var verifyScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventRecord = await verify.CentralTransientEvents.AsNoTracking()
            .Include(item => item.Versions)
            .SingleAsync(item => item.AgentId == scenario).ConfigureAwait(false);
        eventRecord.Versions.OrderBy(item => item.Version).Select(item => item.Version).Should().Equal(1, 2);
        var receipt = await verify.CentralTransientExtractionReceipts.AsNoTracking()
            .SingleAsync(item => item.CentralDerivativeJobId == successorJobId).ConfigureAwait(false);
        TransientCandidateExtractionJson.Parse(System.Text.Encoding.UTF8.GetBytes(receipt.CanonicalReceiptJson))
            .CenteredContextConverged.Should().BeTrue();
    }

    [TestMethod]
    public async Task TerminalContextOutcomesWithoutExtraction_DoNotScheduleConvergence()
    {
        var scenario = $"transient-unsettled-context-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            StarMaximumMagnitude = -30
        };
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 100, 102, 98, 101, 99 })
        {
            var sourceId = await SeedAndScheduleSourceAsync(
                scenario,
                devicePublicId,
                sequence,
                capturedBase,
                sequence == 100 ? CreatePayload([100, 1_000, 1_000, 100]) : CreatePayload(100),
                "unsettled-compatible").ConfigureAwait(false);
            sources.Add(sequence, sourceId);
            await ScheduleTransientAsync(sourceId, options).ConfigureAwait(false);
        }

        Guid provisionalJobId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            provisionalJobId = await db.CentralDerivativeJobs.Where(item =>
                    item.SourceCentralArtifactId == sources[100] &&
                    item.RecipeName == CentralTransientRuntime.RecipeName)
                .Select(item => item.Id).SingleAsync().ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveAsync(provisionalJobId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
        await DisableOtherActiveJobsAsync(provisionalJobId).ConfigureAwait(false);
        _ = await ExecuteClaimedTransientAsync(provisionalJobId, "unsettled-provisional").ConfigureAwait(false);

        await using var assertionScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dependencyJobIds = await assertionDb.CentralTransientContextDependencies.AsNoTracking()
            .Where(item => item.CentralDerivativeJobId == provisionalJobId)
            .Select(item => item.RequiredCentralDerivativeJobId!.Value)
            .ToArrayAsync().ConfigureAwait(false);
        foreach (var dependencyJobId in dependencyJobIds)
        {
            await CentralTransientValidationOutcome.RecordNeedsReviewAsync(
                assertionDb,
                dependencyJobId,
                "transient-validation.context-terminal-without-extraction",
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false);
            assertionDb.ChangeTracker.Clear();
        }
        var scheduler = new CentralDerivativeJobScheduler(
            assertionDb,
            new CentralDerivativeRecipeCatalog(options),
            assertionScope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>());

        (await scheduler.EnsureTransientContextConvergenceAsync(
            provisionalJobId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false)).Should().BeNull();
        (await assertionDb.CentralTransientValidationJobs.AsNoTracking().AnyAsync(item =>
            item.ProvisionalCentralDerivativeJobId == provisionalJobId).ConfigureAwait(false)).Should().BeFalse();
    }

    [TestMethod]
    public async Task CentralTransientWindowTimeout_FinalizesOpaqueSlotsWithStableReason()
    {
        var scenario = $"transient-timeout-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            WindowTimeout = TimeSpan.FromSeconds(5),
            StarMaximumMagnitude = -30
        };
        var centerId = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 500, capturedBase, CreatePayload(100), "timeout-compatible")
            .ConfigureAwait(false);
        await ScheduleTransientAsync(centerId, options).ConfigureAwait(false);
        var neighborId = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 498, capturedBase, CreatePayload(100), "timeout-compatible")
            .ConfigureAwait(false);
        await ScheduleTransientAsync(neighborId, options).ConfigureAwait(false);

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var waiting = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item =>
            item.SourceCentralArtifactId == centerId && item.RecipeName == CentralTransientRuntime.RecipeName)
            .ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
            .ResolveAsync(waiting.Id, waiting.ResolutionDeadlineUtc!.Value.AddTicks(1), CancellationToken.None)
            .ConfigureAwait(false);

        var terminal = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == waiting.Id)
            .ConfigureAwait(false);
        terminal.Status.Should().Be(CentralDerivativeJobStatus.Skipped);
        terminal.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.RequiredInputTimeout);
        var outcome = await db.CentralTransientValidationJobs.AsNoTracking()
            .SingleAsync(item => item.CentralDerivativeJobId == waiting.Id).ConfigureAwait(false);
        outcome.OutcomeState.Should().Be(TransientEventState.NeedsReview);
        outcome.OutcomeReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.RequiredInputTimeout);
        outcome.OutcomeEvidenceIdentitySha256.Should().HaveLength(64);
        outcome.OutcomeRecordedAtUtc.Should().NotBeNull();
        (await db.CentralTransientValidationIdentitySlots.AsNoTracking()
            .CountAsync(item => item.CentralDerivativeJobId == waiting.Id
                && item.State == CentralTransientValidationIdentitySlotState.Unused).ConfigureAwait(false)).Should().Be(32);
    }

    [TestMethod]
    public async Task MissingImmutableMaskEvidence_CompletesAsPersistedNeedsReview()
    {
        var scenario = $"transient-mask-missing-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            StarMaximumMagnitude = -30
        };
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 898, 899, 900, 901, 902 })
        {
            var sourceId = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, CreatePayload(100), "mask-compatible")
                .ConfigureAwait(false);
            sources.Add(sequence, sourceId);
            await ScheduleTransientAsync(sourceId, options).ConfigureAwait(false);
        }

        Guid jobId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            jobId = await db.CentralDerivativeJobs.Where(item => item.SourceCentralArtifactId == sources[900] &&
                    item.RecipeName == CentralTransientRuntime.RecipeName)
                .Select(item => item.Id).SingleAsync().ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveAsync(jobId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            var frameIds = await db.CentralArtifacts.Where(item => sources.Values.Contains(item.Id))
                .Select(item => item.CentralFrameId).ToArrayAsync().ConfigureAwait(false);
            await db.CentralCaptureProfiles.Where(item => frameIds.Contains(item.CentralFrameId) &&
                    item.Kind == CentralProfileKind.Mask)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    item => item.Sha256, new string('F', 64))).ConfigureAwait(false);
        }
        await DisableOtherActiveJobsAsync(jobId).ConfigureAwait(false);

        CentralDerivativeJobLease lease;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            lease = (await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("mask-needs-review", TimeSpan.FromMinutes(2), CancellationToken.None)
                .ConfigureAwait(false))!;
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            result.Status.Should().Be(ProcessingOutcomeStatus.Skipped);
            result.ReasonCode.Should().Be(CentralTransientRuntimeReasonCodes.MaskEvidenceUnavailable);
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var outcome = await db.CentralTransientValidationJobs.AsNoTracking()
                .SingleAsync(item => item.CentralDerivativeJobId == jobId).ConfigureAwait(false);
            outcome.OutcomeState.Should().Be(TransientEventState.NeedsReview);
            outcome.OutcomeReasonCode.Should().Be(CentralTransientRuntimeReasonCodes.MaskEvidenceUnavailable);
            (await db.CentralTransientExtractionReceipts.AnyAsync(item =>
                item.CentralDerivativeJobId == jobId).ConfigureAwait(false)).Should().BeFalse();
        }
    }

    [TestMethod]
    public async Task ConcurrentTransientScheduling_ConvergesToOneJobWindowAndIdentitySet()
    {
        var scenario = $"transient-concurrent-scheduling-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 98, 99, 100, 101, 102 })
        {
            sources[sequence] = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, CreatePayload(100), "concurrent-compatible")
                .ConfigureAwait(false);
        }
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            StarMaximumMagnitude = -30
        };

        const int contenders = 16;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var schedules = Enumerable.Range(0, contenders).Select(async _ =>
        {
            if (Interlocked.Increment(ref readyCount) == contenders)
            {
                ready.SetResult();
            }
            await release.Task.ConfigureAwait(false);
            await ScheduleTransientAsync(sources[100], options).ConfigureAwait(false);
        }).ToArray();
        await ready.Task.ConfigureAwait(false);
        release.SetResult();
        await Task.WhenAll(schedules).ConfigureAwait(false);

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var jobs = await db.CentralDerivativeJobs.AsNoTracking()
            .Where(item => item.SourceCentralArtifactId == sources[100]
                && item.RecipeName == CentralTransientRuntime.RecipeName)
            .Select(item => item.Id).ToArrayAsync().ConfigureAwait(false);
        jobs.Should().ContainSingle();
        (await db.CentralDerivativeJobInputs.CountAsync(item =>
            item.CentralDerivativeJobId == jobs[0]).ConfigureAwait(false)).Should().Be(5);
        (await db.CentralTransientValidationIdentitySlots.CountAsync(item =>
            item.CentralDerivativeJobId == jobs[0]).ConfigureAwait(false)).Should().Be(32);
        await Phase14ScenarioEvidence.RecordAsync(
            "central-window-freeze-commit",
            $"{scenario}-freeze",
            null,
            ["concurrent-scheduling-converged", "single-job-committed", "exact-window-inputs-committed", "identity-slots-committed"],
            [
                new Phase14EvidenceMeasurement("scheduling-contenders", contenders, "count"),
                new Phase14EvidenceMeasurement("window-inputs", 5, "count"),
                new Phase14EvidenceMeasurement("identity-slots", 32, "count")
            ]).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CentralTransientRuntime_EmitsBoundedConnectedPrivateOperationalSignals()
    {
        var scenario = $"transient-runtime-signals-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            StarMaximumMagnitude = -30
        };
        // The worker telemetry is a host singleton every test in this assembly shares, and the health assertion below
        // reads its failure axes over a LeaseDuration-wide window, so a failure any earlier test recorded in the last
        // two minutes would be attributed to this run. Retire both axes here, with the same backdating the teardown
        // below uses, so the assertion describes this test rather than the assembly's recent past. The instance must
        // stay shared: the worker resolves its collaborators from the host provider, so a dependency failure raised by
        // the graph scheduler or the submission service during this run reaches only the singleton, and a test-owned
        // instance would be blind to exactly the failures the assertion exists to catch.
        var telemetry = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<CentralDerivativeWorkerTelemetry>();
        var retiredFailure = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1));
        telemetry.RecordDependencyFailure("storage", retiredFailure);
        telemetry.RecordRenewal("failed", retiredFailure);
        using var collector = new TransientRuntimeCollector();
        using var logs = new TransientLogProvider();
        AssemblyHooks.Fixture.Factory.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 98, 99, 100, 101, 102 })
        {
            var payload = sequence == 100
                ? CreatePayload([100, 1_000, 1_000, 100])
                : CreatePayload(100);
            sources[sequence] = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, payload, "runtime-signals-compatible")
                .ConfigureAwait(false);
        }
        await ScheduleTransientAsync(sources[100], options).ConfigureAwait(false);
        Guid jobId;
        string sourceChecksum;
        string storageReference;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            jobId = await db.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.SourceCentralArtifactId == sources[100]
                    && job.RecipeName == CentralTransientRuntime.RecipeName)
                .Select(job => job.Id).SingleAsync().ConfigureAwait(false);
            var source = await db.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == sources[100])
                .ConfigureAwait(false);
            sourceChecksum = source.ChecksumSha256;
            storageReference = source.StorageReference;
        }
        await DisableOtherActiveJobsAsync(jobId).ConfigureAwait(false);

        var workerOptions = Options.Create(new CentralDerivativeWorkerOptions
        {
            Enabled = true,
            WorkerId = "issue-116-runtime-signals",
            Concurrency = 1,
            PollInterval = TimeSpan.FromMilliseconds(10),
            QueueSampleInterval = TimeSpan.FromHours(1),
            LeaseDuration = TimeSpan.FromMinutes(2),
            RenewalInterval = TimeSpan.FromSeconds(30),
            ShutdownTimeout = TimeSpan.FromSeconds(10),
            BacklogDegradedAfter = TimeSpan.FromMinutes(10)
        });
        using var worker = new CentralDerivativeWorker(
            AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            workerOptions,
            telemetry,
            TimeProvider.System,
            AssemblyHooks.Fixture.Factory.Services.GetRequiredService<ILogger<CentralDerivativeWorker>>());
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await WaitUntilAsync(async () =>
            {
                await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                    .CentralDerivativeJobs.AsNoTracking().AnyAsync(job =>
                        job.Id == jobId && job.Status == CentralDerivativeJobStatus.Completed)
                    .ConfigureAwait(false);
            }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        collector.RecordObservableInstruments();
        logs.Entries.Select(entry => entry.EventId.Id).Should().Contain([2130, 2131, 2132, 2140, 2161]);
        var metrics = collector.Metrics.ToArray();
        metrics.Select(metric => metric.Name).Should().Contain([
            "skymonitor.central.transient.outcomes",
            "skymonitor.central.transient.classifications",
            "skymonitor.central.transient.candidates",
            "skymonitor.central.derivative.attempts",
            "skymonitor.central.derivative.duration",
            "skymonitor.central.derivative.bytes",
            "skymonitor.central.derivative.window.resolutions"
        ]);
        AssertBoundedTransientMetricTags(metrics, ReadRuntimeMetricAllowlists());
        var activities = collector.Activities.ToArray();
        // Maintenance (window resolution, retrospective scheduling) runs concurrently with the claim loop, so the
        // worker may legitimately claim another job that became runnable during this one; assert on the execution
        // span of the job under test and on the stage spans connected to it.
        var execution = activities.Single(activity =>
            activity.Name == "central-derivative.execute" && activity.Kind == ActivityKind.Consumer &&
            activity.Tags.TryGetValue("job.id", out var taggedJobId) &&
            string.Equals(taggedJobId, jobId.ToString(), StringComparison.OrdinalIgnoreCase));
        foreach (var stage in new[] { "verify", "load", "detect", "converge", "persist" })
        {
            var activity = activities.FirstOrDefault(item =>
                item.Name == $"central-derivative.{stage}" && item.Kind == ActivityKind.Internal &&
                IsDescendantOf(item, execution, activities));
            Assert.IsNotNull(activity,
                $"Missing production activity for stage '{stage}'. Observed: {string.Join(',', activities.Select(item => $"{item.Name}:{item.Kind}"))}");
            Assert.AreEqual(execution.TraceId, activity.TraceId);
            Assert.IsTrue(IsDescendantOf(activity, execution, activities),
                $"Activity '{activity.Name}' was not connected to the production execute span.");
        }

        // The worker is stopped, so nothing further is written by this run. Six of the health check's axes are read
        // from tables every test in this assembly shares, and unlike the telemetry axes none of them is bounded by a
        // window: a foreign non-terminal graph execution degrades the check no matter how old it is, and a foreign
        // input pin degrades it once it passes BacklogDegradedAfter. Retire the rows this test does not own so the
        // assertion below describes this run. Ownership is this run's device, which is a fresh identifier per run and
        // is carried by the derivative outputs the worker wrote as well as by the sources seeded above, so nothing
        // this run produced is retired. An inconsistent runnable window or an unsealed graph produced by the run
        // under test therefore still fails the assertion, which is the whole point of scoping rather than truncating.
        await RetireForeignActiveStateAsync(devicePublicId).ConfigureAwait(false);

        await using (var healthScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var health = new HVO.SkyMonitor.LogicHost.HealthChecks.CentralDerivativeWorkerHealthCheck(
                healthScope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                workerOptions,
                telemetry,
                TimeProvider.System);
            try
            {
                var healthy = await health.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
                healthy.Status.Should().Be(HealthStatus.Healthy,
                    "worker health after the run should be clean; data: {0}",
                    string.Join(", ", healthy.Data.Select(pair => $"{pair.Key}={pair.Value}")));
                telemetry.RecordDependencyFailure("storage", DateTimeOffset.UtcNow);
                (await health.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false)).Status
                    .Should().Be(HealthStatus.Degraded);
            }
            finally
            {
                // Retire both axes again so this test is not itself the writer that degrades whatever runs next.
                // This has to run even when an assertion above throws. The dependency failure written to prove the
                // degraded reading is live and on the shared singleton, so an escaping exception would leave it set
                // for a full LeaseDuration and hand the next test the exact failure #753 was filed for.
                var retiredAfter = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1));
                telemetry.RecordDependencyFailure("storage", retiredAfter);
                telemetry.RecordRenewal("failed", retiredAfter);
            }
        }

        Guid[] privateEventIds;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            privateEventIds = await db.CentralTransientValidationIdentitySlots.AsNoTracking()
                .Where(slot => slot.CentralDerivativeJobId == jobId)
                .Select(slot => slot.SubmittedEventId).ToArrayAsync().ConfigureAwait(false);
        }
        var signalText = string.Join('\n',
            logs.Entries.Select(entry => entry.Message)
                .Concat(metrics.SelectMany(metric => metric.Tags.Select(tag => $"{tag.Key}={tag.Value}")))
                .Concat(activities.SelectMany(activity => activity.Tags.Select(tag => $"{tag.Key}={tag.Value}"))));
        foreach (var forbidden in new[]
        {
            sourceChecksum,
            storageReference,
            devicePublicId.ToString("D"),
            IntegrationTestFixture.MinioAccessKey
        }.Concat(privateEventIds.Select(value => value.ToString("D"))))
        {
            Assert.IsFalse(signalText.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"Central transient runtime signals leaked private value '{forbidden}'.");
        }
    }

    [TestMethod]
    public async Task RetrospectiveScheduler_UsesGenericCatalogAndAllocatesDurableSlots()
    {
        var scenario = $"transient-retrospective-{Guid.NewGuid():N}";
        var sourceId = await SeedAndScheduleSourceAsync(
            scenario,
            Guid.NewGuid(),
            700,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            CreatePayload(100),
            "retrospective-compatible",
            DateTimeOffset.UnixEpoch.AddDays(1)).ConfigureAwait(false);
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            StarMaximumMagnitude = -30
        };

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.CentralDerivativeJobs.Add(new CentralDerivativeJob
        {
            SourceCentralArtifactId = sourceId,
            TargetRole = FrameArtifactRole.Metadata,
            TargetRecipeVersion = CentralTransientRuntime.RecipeVersion,
            TargetVariant = CentralTransientRuntime.Variant,
            RecipeName = CentralTransientRuntime.RecipeName,
            RecipeOptionsJson = "{}",
            InputSelectorJson = "{}",
            RequestedRecipeIdentitySha256 = TransientCandidateExtractionFactory.ComputeRecipeIdentitySha256(
                options.Extraction.ToContract()),
            ExpectedRecipeIdentitySha256 = TransientCandidateExtractionFactory.ComputeRecipeIdentitySha256(
                options.Extraction.ToContract()),
            RequestIdentitySha256 = HashText($"{scenario}-obsolete-transient"),
            Status = CentralDerivativeJobStatus.Skipped,
            AttemptCount = 0,
            MaxAttempts = 1,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            UpdatedAtUtc = DateTimeOffset.UnixEpoch,
            CompletedAtUtc = DateTimeOffset.UnixEpoch,
            StateReasonCode = "obsolete"
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        var scheduler = new CentralDerivativeJobScheduler(
            db,
            new CentralDerivativeRecipeCatalog(options),
            scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>());
        var recipeCatalog = new CentralDerivativeRecipeCatalog(options);
        var retrospective = new CentralTransientRetrospectiveScheduler(
            db,
            scheduler,
            recipeCatalog,
            Options.Create(options),
            scope.ServiceProvider.GetRequiredService<CentralDerivativeWorkerTelemetry>(),
            scope.ServiceProvider.GetRequiredService<ILogger<CentralTransientRetrospectiveScheduler>>());

        await retrospective.ScheduleBatchAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);

        var currentExecutionIdentity = recipeCatalog.GetRequiredRecipes(FrameArtifactRole.Raw)
            .Single(item => item.RecipeName == CentralTransientRuntime.RecipeName)
            .Transient!.ExecutionOptionsIdentitySha256;
        var job = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item =>
            item.SourceCentralArtifactId == sourceId && item.RecipeName == CentralTransientRuntime.RecipeName &&
            item.TargetRecipeVersion == CentralTransientRuntime.RecipeVersion &&
            db.CentralTransientValidationJobs.Any(validation => validation.CentralDerivativeJobId == item.Id &&
                validation.ExecutionOptionsIdentitySha256 == currentExecutionIdentity))
            .ConfigureAwait(false);
        job.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
        (await db.CentralTransientValidationIdentitySlots.AsNoTracking()
            .CountAsync(item => item.CentralDerivativeJobId == job.Id).ConfigureAwait(false)).Should().Be(32);
        (await db.CentralDerivativeJobs.CountAsync(item => item.SourceCentralArtifactId == sourceId &&
            item.RecipeName == CentralTransientRuntime.RecipeName).ConfigureAwait(false)).Should().Be(2);
    }

    [TestMethod]
    public async Task CorruptTransientInput_QuarantinesJobAndFinalizesOpaqueSlots()
    {
        var scenario = $"transient-corrupt-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            StarMaximumMagnitude = -30
        };
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 100, 102, 98, 101, 99 })
        {
            var sourceId = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, CreatePayload(100), "corrupt-compatible")
                .ConfigureAwait(false);
            sources.Add(sequence, sourceId);
            await ScheduleTransientAsync(sourceId, options).ConfigureAwait(false);
        }

        Guid jobId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            jobId = await db.CentralDerivativeJobs.Where(item =>
                    item.SourceCentralArtifactId == sources[100]
                    && item.RecipeName == CentralTransientRuntime.RecipeName)
                .Select(item => item.Id).SingleAsync().ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveAsync(jobId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
        await DisableOtherActiveJobsAsync(jobId).ConfigureAwait(false);
        CentralDerivativeJobLease lease;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            lease = (await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("transient-corrupt-worker", TimeSpan.FromMinutes(2), CancellationToken.None)
                .ConfigureAwait(false))!;
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var storageReference = await db.CentralArtifacts.Where(item => item.Id == sources[99])
                .Select(item => item.StorageReference).SingleAsync().ConfigureAwait(false);
            var objectName = storageReference[$"object://{Bucket}/".Length..];
            await using var corrupt = new MemoryStream(CreatePayload(101), writable: false);
            await scope.ServiceProvider.GetRequiredService<IMinioClient>().PutObjectAsync(new PutObjectArgs()
                .WithBucket(Bucket)
                .WithObject(objectName)
                .WithStreamData(corrupt)
                .WithObjectSize(corrupt.Length)
                .WithContentType("application/x-hvo-linear-frame"), CancellationToken.None).ConfigureAwait(false);
        }

        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var execute = () => scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None);
            await execute.Should().ThrowAsync<CentralArtifactIntegrityException>().ConfigureAwait(false);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == jobId)
                .ConfigureAwait(false);
            job.Status.Should().Be(CentralDerivativeJobStatus.Quarantined);
            (await db.CentralArtifacts.AsNoTracking().Where(item => item.Id == sources[99])
                .Select(item => item.ObjectState).SingleAsync().ConfigureAwait(false))
                .Should().Be(CentralArtifactObjectState.Quarantined);
            (await db.CentralTransientValidationIdentitySlots.AsNoTracking()
                .CountAsync(item => item.CentralDerivativeJobId == jobId
                    && item.State == CentralTransientValidationIdentitySlotState.Unused).ConfigureAwait(false)).Should().Be(32);
        }
    }

    [TestMethod]
    public async Task OutOfOrderWindow_ResolvesExecutesAndPersistsExactLineage()
    {
        var scenario = $"window-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 100, 102, 98, 101, 99 })
        {
            var value = checked((ushort)((sequence - 97) * 10));
            sources[sequence] = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, CreatePayload(value), "compatible")
                .ConfigureAwait(false);
        }

        Guid jobId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.InputRequirements)
                .Include(item => item.Inputs).ThenInclude(input => input.Artifact)!.ThenInclude(artifact => artifact!.Frame)
                .AsSplitQuery()
                .SingleAsync(item => item.SourceCentralArtifactId == sources[100]
                    && item.RecipeName == BuiltInProcessingRecipes.RollingMean)
                .ConfigureAwait(false);
            job.Status.Should().Be(CentralDerivativeJobStatus.Pending);
            job.InputRequirements.OrderBy(item => item.Ordinal).Select(item => item.SequenceOffset)
                .Should().Equal(-2, -1, 0, 1, 2);
            job.Inputs.OrderBy(item => item.Ordinal).Select(item => item.CaptureSequence)
                .Should().Equal(98, 99, 100, 101, 102);
            job.InputSetIdentitySha256.Should().HaveLength(64);
            jobId = job.Id;

        }

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => NotifySourceAsync(sources[100])))
            .ConfigureAwait(false);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralDerivativeJobs.CountAsync(item => item.SourceCentralArtifactId == sources[100]
                && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false)).Should().Be(1);
            (await db.CentralDerivativeJobInputs.CountAsync(item => item.CentralDerivativeJobId == jobId)
                .ConfigureAwait(false)).Should().Be(5);
        }

        await DisableOtherActiveJobsAsync(jobId).ConfigureAwait(false);
        CentralDerivativeJobLease lease;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
            lease = (await service.ClaimNextAsync(
                "window-integration-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false))!;
            lease.JobId.Should().Be(jobId);
            lease.Inputs!.Select(input => input.CaptureSequence).Should().Equal(98, 99, 100, 101, 102);
        }

        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            result.Status.Should().Be(ProcessingOutcomeStatus.Produced);
        }

        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var completed = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.Attempts)
                .Include(item => item.ResultArtifact)!.ThenInclude(artifact => artifact!.Sources)
                .SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            completed.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            completed.ResultArtifact!.Sources.OrderBy(source => source.Ordinal).Select(source => source.ResolvedCentralArtifactId)
                .Should().Equal(new long[] { 98, 99, 100, 101, 102 }.Select(sequence => (Guid?)sources[sequence]));
            completed.ResultArtifact.ChecksumSha256.Should().Be(
                Convert.ToHexString(SHA256.HashData(CreatePayload(30))));
            completed.Attempts.Single().InputBytes.Should().Be(40);

            var retention = scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>();
            foreach (var sourceId in sources.Values)
            {
                (await retention.IsHeldAsync(sourceId, CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
            }
        }

        Guid replacementJobId;
        Guid previousResultId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            previousResultId = (await db.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == jobId).ConfigureAwait(false)).ResultCentralArtifactId!.Value;
            var existingJobId = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>()
                .ReprocessAsync(
                    jobId,
                    new CentralDerivativeReprocessRequest(
                        BuiltInProcessingRecipes.RollingMean,
                        CaptureContractJson.SerializeToElement(new RollingMeanOptions()),
                        CentralDerivativeRecipeCatalog.RollingMeanVariant,
                        Supersede: false),
                    "window-integration-test",
                    CancellationToken.None)
                .ConfigureAwait(false);
            existingJobId.Should().Be(jobId);
            var retainedJobId = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>()
                .ReprocessAsync(
                    jobId,
                    new CentralDerivativeReprocessRequest(
                        BuiltInProcessingRecipes.RollingMean,
                        CaptureContractJson.SerializeToElement(new RollingMeanOptions()),
                        "central-rolling-mean-retained-history",
                        Supersede: false),
                    "window-integration-test",
                    CancellationToken.None)
                .ConfigureAwait(false);
            var retainedJob = await db.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == retainedJobId).ConfigureAwait(false);
            retainedJob.PredecessorJobId.Should().BeNull();
            retainedJob.RetainedResultCentralArtifactId.Should().Be(previousResultId);
            (await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>()
                .IsHeldAsync(previousResultId, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            replacementJobId = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>()
                .ReprocessAsync(
                    jobId,
                    new CentralDerivativeReprocessRequest(
                        BuiltInProcessingRecipes.RollingMean,
                        CaptureContractJson.SerializeToElement(new RollingMeanOptions()),
                        "central-rolling-mean-reprocessed",
                        Supersede: true),
                    "window-integration-test",
                    CancellationToken.None)
                .ConfigureAwait(false);
            var replacement = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(job => job.Inputs)
                .SingleAsync(job => job.Id == replacementJobId).ConfigureAwait(false);
            replacement.PredecessorJobId.Should().Be(jobId);
            replacement.Inputs.OrderBy(input => input.Ordinal).Select(input => input.CaptureSequence)
                .Should().Equal(98, 99, 100, 101, 102);
            var retention = scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>();
            (await retention.IsHeldAsync(previousResultId, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
        }

        await DisableOtherActiveJobsAsync(replacementJobId).ConfigureAwait(false);
        await using (var claimScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var replacementLease = (await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("window-reprocess-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false))!;
            replacementLease.JobId.Should().Be(replacementJobId);
            await using var executionScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var replacement = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(replacementLease, CancellationToken.None).ConfigureAwait(false);
            replacement.Status.Should().Be(ProcessingOutcomeStatus.Produced);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var previous = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(job => job.ResultArtifact)
                .SingleAsync(job => job.Id == jobId).ConfigureAwait(false);
            var replacement = await db.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == replacementJobId).ConfigureAwait(false);
            previous.Status.Should().Be(CentralDerivativeJobStatus.Superseded);
            previous.SupersededByJobId.Should().Be(replacementJobId);
            previous.ResultArtifact!.ObjectState.Should().Be(CentralArtifactObjectState.Available);
            replacement.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            replacement.ResultCentralArtifactId.Should().NotBe(previousResultId);
        }
    }

    [TestMethod]
    public async Task WaitingWindow_SurvivesScopeRestartAndTimesOutWithReason()
    {
        var scenario = $"window-timeout-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var sourceId = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 500, capturedBase, CreatePayload(10), "compatible")
            .ConfigureAwait(false);
        var neighborId = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 498, capturedBase, CreatePayload(10), "compatible")
            .ConfigureAwait(false);
        Guid jobId;
        DateTimeOffset deadline;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var waiting = await db.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(item => item.SourceCentralArtifactId == sourceId
                    && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
            waiting.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
            waiting.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.WaitingRequiredInput);
            jobId = waiting.Id;
            deadline = waiting.ResolutionDeadlineUtc!.Value;
        }
        await DisableOtherActiveJobsAsync(jobId).ConfigureAwait(false);
        await using (var heldScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var retention = heldScope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>();
            (await retention.IsHeldAsync(neighborId, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
        }

        await using (var restartedScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            await restartedScope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveWaitingAsync(deadline.AddTicks(1), CancellationToken.None).ConfigureAwait(false);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var terminal = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.InputRequirements)
                .SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            terminal.Status.Should().Be(CentralDerivativeJobStatus.Skipped);
            terminal.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.RequiredInputTimeout);
            (await db.CentralDerivativeJobInputs.CountAsync(input => input.CentralDerivativeJobId == jobId)
                .ConfigureAwait(false)).Should().Be(2);
            terminal.InputRequirements.Count(item => item.ResolutionState == CentralDerivativeInputResolutionState.Missing)
                .Should().Be(3);
            var retention = scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>();
            (await retention.IsHeldAsync(sourceId, CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
            (await retention.IsHeldAsync(neighborId, CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
        }
        await Phase14ScenarioEvidence.RecordAsync(
            "central-window-timeout-commit",
            $"{scenario}-timeout",
            null,
            ["waiting-window-survived-scope-restart", "timeout-reason-committed", "resolved-input-count-preserved", "missing-input-count-preserved", "retention-holds-released"],
            [
                new Phase14EvidenceMeasurement("resolved-inputs", 2, "count"),
                new Phase14EvidenceMeasurement("missing-inputs", 3, "count")
            ]).ConfigureAwait(false);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>()
                .RequeueAsync(jobId, "window-integration-test", CancellationToken.None).ConfigureAwait(false);
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var requeued = await db.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == jobId).ConfigureAwait(false);
            requeued.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
            requeued.InputSetIdentitySha256.Should().BeNull();
            (await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("incomplete-window-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false)).Should().BeNull();
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>()
                .CancelAsync(jobId, "window-integration-test", CancellationToken.None).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task MissingRequiredInput_UsesConfiguredDeadlineOutcome()
    {
        var runJobId = Guid.Empty;
        var cases = new[]
        {
            (Outcome: CentralDerivativeWindowOutcome.Run, Status: CentralDerivativeJobStatus.Pending),
            (Outcome: CentralDerivativeWindowOutcome.Fail, Status: CentralDerivativeJobStatus.TerminalFailure),
            (Outcome: CentralDerivativeWindowOutcome.Quarantine, Status: CentralDerivativeJobStatus.Quarantined)
        };
        for (var index = 0; index < cases.Length; index++)
        {
            var scenario = $"window-outcome-{index}-{Guid.NewGuid():N}";
            var sourceId = await SeedAndScheduleSourceAsync(
                scenario,
                Guid.NewGuid(),
                1_100 + index * 100,
                DateTimeOffset.UtcNow.AddMinutes(-10),
                CreatePayload(10),
                "compatible").ConfigureAwait(false);
            await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.SingleAsync(item => item.SourceCentralArtifactId == sourceId
                && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            job.MissingInputOutcome = cases[index].Outcome;
            job.ResolutionDeadlineUtc = now.AddTicks(-1);
            await db.SaveChangesAsync().ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveAsync(job.Id, now, CancellationToken.None).ConfigureAwait(false);

            db.ChangeTracker.Clear();
            var resolved = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.InputRequirements)
                .Include(item => item.Inputs)
                .AsSplitQuery()
                .SingleAsync(item => item.Id == job.Id).ConfigureAwait(false);
            resolved.Status.Should().Be(cases[index].Status);
            resolved.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.RequiredInputTimeout);
            if (cases[index].Outcome == CentralDerivativeWindowOutcome.Run)
            {
                runJobId = resolved.Id;
                resolved.InputSetIdentitySha256.Should().HaveLength(64);
                resolved.Inputs.Should().ContainSingle();
                resolved.InputRequirements.Count(item => item.ResolutionState == CentralDerivativeInputResolutionState.Missing)
                    .Should().Be(4);
            }
        }

        await DisableOtherActiveJobsAsync(runJobId).ConfigureAwait(false);
        await using var claimScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var lease = await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
            .ClaimNextAsync("window-outcome-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false);
        lease!.JobId.Should().Be(runJobId);
        lease.Inputs.Should().ContainSingle();
        await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
            .SkipAsync(runJobId, lease.LeaseToken, "test.cleanup", CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// A frozen window minimum (graph <c>MinimumInputCount</c>) is enforced before inputs freeze for every timeout
    /// policy: a trailing window with no required positions must not run below its minimum under <c>Run</c>, and the
    /// other policies complete through their configured outcome with a stable reason.
    /// </summary>
    [TestMethod]
    public async Task MinimumInputCount_IsEnforcedBeforeFreezingForEveryTimeoutPolicy()
    {
        var cases = new[]
        {
            (Outcome: CentralDerivativeWindowOutcome.Run, Minimum: 3, Status: CentralDerivativeJobStatus.Skipped),
            (Outcome: CentralDerivativeWindowOutcome.Skip, Minimum: 3, Status: CentralDerivativeJobStatus.Skipped),
            (Outcome: CentralDerivativeWindowOutcome.Fail, Minimum: 3, Status: CentralDerivativeJobStatus.TerminalFailure),
            (Outcome: CentralDerivativeWindowOutcome.Run, Minimum: 1, Status: CentralDerivativeJobStatus.Pending)
        };
        var runJobId = Guid.Empty;
        for (var index = 0; index < cases.Length; index++)
        {
            var scenario = $"window-minimum-{index}-{Guid.NewGuid():N}";
            var sourceId = await SeedAndScheduleSourceAsync(
                scenario,
                Guid.NewGuid(),
                2_100 + index * 100,
                DateTimeOffset.UtcNow.AddMinutes(-10),
                CreatePayload(10),
                "compatible").ConfigureAwait(false);
            await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs
                .Include(item => item.InputRequirements)
                .SingleAsync(item => item.SourceCentralArtifactId == sourceId
                    && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            // A trailing window with an empty required-position set and a frozen minimum cardinality.
            foreach (var requirement in job.InputRequirements)
            {
                requirement.IsRequired = false;
            }
            job.MissingInputOutcome = cases[index].Outcome;
            job.MinimumInputCount = cases[index].Minimum;
            job.ResolutionDeadlineUtc = now.AddTicks(-1);
            await db.SaveChangesAsync().ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveAsync(job.Id, now, CancellationToken.None).ConfigureAwait(false);

            db.ChangeTracker.Clear();
            var resolved = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(item => item.InputRequirements)
                .Include(item => item.Inputs)
                .AsSplitQuery()
                .SingleAsync(item => item.Id == job.Id).ConfigureAwait(false);
            resolved.Status.Should().Be(cases[index].Status, $"case {index}: {cases[index].Outcome} minimum {cases[index].Minimum}");
            resolved.InputRequirements.Count(item => item.ResolutionState == CentralDerivativeInputResolutionState.Resolved)
                .Should().Be(1);
            if (cases[index].Status == CentralDerivativeJobStatus.Pending)
            {
                runJobId = resolved.Id;
                resolved.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.OptionalInputTimeout);
                resolved.InputSetIdentitySha256.Should().HaveLength(64);
                resolved.Inputs.Should().ContainSingle();
            }
            else
            {
                resolved.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.MinimumInputCountUnmet);
                resolved.InputSetIdentitySha256.Should().BeNull("an undersized window must never freeze");
                resolved.CompletedAtUtc.Should().NotBeNull();
                resolved.AvailableAtUtc.Should().BeNull();
            }
        }

        await DisableOtherActiveJobsAsync(runJobId).ConfigureAwait(false);
        await using var claimScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var lease = await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
            .ClaimNextAsync("window-minimum-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false);
        lease!.JobId.Should().Be(runJobId);
        await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
            .SkipAsync(runJobId, lease.LeaseToken, "test.cleanup", CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task IncompatibleRequiredPosition_FinishesWithBoundedReasonWithoutQuarantiningSource()
    {
        var scenario = $"window-incompatible-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 700, 698, 699, 701, 702 })
        {
            sources[sequence] = await SeedAndScheduleSourceAsync(
                scenario,
                devicePublicId,
                sequence,
                capturedBase,
                CreatePayload(10),
                sequence == 702 ? "different-profile" : "compatible")
                .ConfigureAwait(false);
        }

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var job = await db.CentralDerivativeJobs.AsNoTracking()
            .Include(item => item.InputRequirements)
            .SingleAsync(item => item.SourceCentralArtifactId == sources[700]
                && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
        job.Status.Should().Be(CentralDerivativeJobStatus.Skipped);
        job.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.IncompatibleInput);
        job.InputRequirements.Single(item => item.ExpectedCaptureSequence == 702).ResolutionReasonCode
            .Should().Be(CentralDerivativeWindowReasonCodes.IncompatibleProfile);
        var incompatibleSource = await db.CentralArtifacts.AsNoTracking()
            .SingleAsync(item => item.Id == sources[702]).ConfigureAwait(false);
        incompatibleSource.ObjectState.Should().Be(CentralArtifactObjectState.Available);
        incompatibleSource.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
    }

    [TestMethod]
    public async Task InvalidatedNeighbor_ReentersResolutionAndRefreezesAfterRepair()
    {
        var scenario = $"window-repair-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 900, 898, 899, 901, 902 })
        {
            sources[sequence] = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, CreatePayload(10), "compatible")
                .ConfigureAwait(false);
        }

        Guid jobId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.SingleAsync(item => item.SourceCentralArtifactId == sources[900]
                && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
            jobId = job.Id;
            var neighbor = await db.CentralArtifacts.SingleAsync(item => item.Id == sources[902]).ConfigureAwait(false);
            neighbor.ObjectState = CentralArtifactObjectState.Pending;
            neighbor.ReconstructionState = CentralReconstructionState.PendingReference;
            await ArtifactIngestService.InvalidateDependentsAsync(db, neighbor, CancellationToken.None)
                .ConfigureAwait(false);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var waiting = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == jobId)
                .ConfigureAwait(false);
            waiting.Status.Should().Be(CentralDerivativeJobStatus.Waiting);
            waiting.InputSetIdentitySha256.Should().BeNull();
            (await db.CentralDerivativeJobInputs.CountAsync(input => input.CentralDerivativeJobId == jobId)
                .ConfigureAwait(false)).Should().Be(4);

            var neighbor = await LoadSchedulableArtifactAsync(db, sources[902]).ConfigureAwait(false);
            neighbor.ObjectState = CentralArtifactObjectState.Available;
            neighbor.ReconstructionState = CentralReconstructionState.Complete;
            neighbor.StateReasonCode = null;
            await db.SaveChangesAsync().ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
                .EnsureRequiredJobsAsync(neighbor, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var repaired = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == jobId)
                .ConfigureAwait(false);
            repaired.Status.Should().Be(CentralDerivativeJobStatus.Pending);
            repaired.InputSetIdentitySha256.Should().HaveLength(64);
            (await db.CentralDerivativeJobInputs.CountAsync(input => input.CentralDerivativeJobId == jobId)
                .ConfigureAwait(false)).Should().Be(5);
        }
    }

    [TestMethod]
    public async Task PublishedWindow_InvalidationPreservesFrozenInputsAndRecoversOnlySameLineage()
    {
        var scenario = $"window-published-repair-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var sources = new Dictionary<long, Guid>();
        foreach (var sequence in new long[] { 1_200, 1_198, 1_199, 1_201, 1_202 })
        {
            sources[sequence] = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, CreatePayload(10), "compatible")
                .ConfigureAwait(false);
        }

        Guid jobId;
        string inputSetIdentity;
        Guid resultId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item =>
                item.SourceCentralArtifactId == sources[1_200]
                && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
            jobId = job.Id;
            inputSetIdentity = job.InputSetIdentitySha256!;
        }
        await DisableOtherActiveJobsAsync(jobId).ConfigureAwait(false);
        await using (var claimScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var lease = (await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("published-window-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false))!;
            await using var executionScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            _ = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var completed = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == jobId)
                .ConfigureAwait(false);
            resultId = completed.ResultCentralArtifactId!.Value;
            var neighbor = await db.CentralArtifacts.SingleAsync(item => item.Id == sources[1_202])
                .ConfigureAwait(false);
            neighbor.ObjectState = CentralArtifactObjectState.Pending;
            neighbor.ReconstructionState = CentralReconstructionState.PendingReference;
            await ArtifactIngestService.InvalidateDependentsAsync(db, neighbor, CancellationToken.None)
                .ConfigureAwait(false);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var suspended = await db.CentralDerivativeJobs.AsNoTracking().Include(item => item.Inputs)
                .SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            suspended.Status.Should().Be(CentralDerivativeJobStatus.RetryableFailure);
            suspended.InputSetIdentitySha256.Should().Be(inputSetIdentity);
            suspended.Inputs.Should().HaveCount(5);
            suspended.ResultCentralArtifactId.Should().Be(resultId);

            var neighbor = await db.CentralArtifacts.SingleAsync(item => item.Id == sources[1_202])
                .ConfigureAwait(false);
            neighbor.ObjectState = CentralArtifactObjectState.Available;
            neighbor.ReconstructionState = CentralReconstructionState.Complete;
            neighbor.StateReasonCode = null;
            var result = await db.CentralArtifacts.Include(item => item.Sources)
                .SingleAsync(item => item.Id == resultId).ConfigureAwait(false);
            result.Sources.Single(source => source.ResolvedCentralArtifactId == null).ResolvedCentralArtifactId = neighbor.Id;
            result.ReconstructionState = CentralReconstructionState.Complete;
            result.StateReasonCode = null;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        await using (var claimScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var lease = (await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("published-window-recovery", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false))!;
            lease.JobId.Should().Be(jobId);
            await using var recoveryScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var recoveredArtifactId = await recoveryScope.ServiceProvider.GetRequiredService<ICentralDerivativeOutputWriter>()
                .TryCompletePendingAsync(lease, CancellationToken.None).ConfigureAwait(false);
            recoveredArtifactId.Should().NotBeNull();
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var recovered = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            recovered.Status.Should().Be(CentralDerivativeJobStatus.Completed);
            recovered.ResultCentralArtifactId.Should().Be(resultId);
            recovered.InputSetIdentitySha256.Should().Be(inputSetIdentity);
        }
    }

    [TestMethod]
    public async Task SourcesReceivedAfterDeadline_CannotMakeWindowRunnable()
    {
        var scenario = $"window-late-{Guid.NewGuid():N}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-10);
        var centerId = await SeedAndScheduleSourceAsync(
            scenario, devicePublicId, 1_000, capturedBase, CreatePayload(10), "compatible")
            .ConfigureAwait(false);
        Guid jobId;
        DateTimeOffset deadline;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var job = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.SourceCentralArtifactId == centerId
                    && item.RecipeName == BuiltInProcessingRecipes.RollingMean).ConfigureAwait(false);
            jobId = job.Id;
            deadline = job.ResolutionDeadlineUtc!.Value;
        }
        foreach (var sequence in new long[] { 998, 999, 1001, 1002 })
        {
            _ = await SeedAndScheduleSourceAsync(
                scenario, devicePublicId, sequence, capturedBase, CreatePayload(10), "compatible", deadline.AddSeconds(1))
                .ConfigureAwait(false);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveAsync(jobId, deadline.AddSeconds(2), CancellationToken.None).ConfigureAwait(false);
            var terminal = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralDerivativeJobs.AsNoTracking().SingleAsync(item => item.Id == jobId).ConfigureAwait(false);
            terminal.Status.Should().Be(CentralDerivativeJobStatus.Skipped);
            terminal.StateReasonCode.Should().Be(CentralDerivativeWindowReasonCodes.RequiredInputTimeout);
        }
    }

    internal static async Task<Guid> SeedAndScheduleSourceAsync(
        string scenario,
        Guid devicePublicId,
        long sequence,
        DateTimeOffset capturedBase,
        byte[] payload,
        string profileSeed,
        DateTimeOffset? receivedAtUtc = null,
        int width = 2,
        int height = 2,
        CameraPixelFormat pixelFormat = CameraPixelFormat.Mono16,
        string? objectKeyOverride = null,
        bool publishPayload = true)
    {
        var capturedAtUtc = capturedBase.AddSeconds(sequence);
        var objectKey = objectKeyOverride ?? $"integration/{scenario}/{sequence:D8}.raw";
        var checksum = Convert.ToHexString(SHA256.HashData(payload));
        await using var uploadScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var minio = uploadScope.ServiceProvider.GetRequiredService<IMinioClient>();
        if (publishPayload)
        {
            if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket), CancellationToken.None)
                .ConfigureAwait(false))
            {
                await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            await using var stream = new MemoryStream(payload, writable: false);
            await minio.PutObjectAsync(new PutObjectArgs()
                    .WithBucket(Bucket)
                    .WithObject(objectKey)
                    .WithStreamData(stream)
                    .WithObjectSize(payload.Length)
                    .WithContentType("application/x-hvo-linear-frame"), CancellationToken.None)
                .ConfigureAwait(false);
        }

        var db = uploadScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registration = await db.DeviceRegistrations.Include(item => item.Observatory)
            .SingleOrDefaultAsync(item => item.DevicePublicId == devicePublicId).ConfigureAwait(false);
        if (registration is null)
        {
            var observatory = new Observatory
            {
                OwnerUserId = scenario,
                Name = $"{scenario}-observatory",
                LatitudeDegrees = 35.347,
                LongitudeDegrees = -113.878,
                ElevationMeters = 0,
                TimeZoneId = "UTC",
                CreatedAtUtc = capturedAtUtc
            };
            registration = new DeviceRegistration
            {
                DeviceId = scenario,
                DevicePublicId = devicePublicId,
                ObservatoryId = observatory.Id,
                Observatory = observatory,
                FriendlyName = scenario,
                ObservatoryName = observatory.Name,
                ObservatoryLatitudeDegrees = observatory.LatitudeDegrees,
                ObservatoryLongitudeDegrees = observatory.LongitudeDegrees,
                ObservatoryElevationMeters = observatory.ElevationMeters,
                ObservatoryTimeZoneId = observatory.TimeZoneId,
                OwnerUserId = scenario,
                OwnerDisplayName = scenario,
                VerificationCodeHash = HashText($"{scenario}-verification"),
                IssuedAtUtc = capturedAtUtc,
                Status = DeviceRegistrationStatus.Active
            };
            db.DeviceRegistrations.Add(registration);
        }
        var observatoryLocation = await ObservatoryLocationAuthority.ApplyAsync(
            db,
            registration.Observatory!,
            registration.Observatory!.LatitudeDegrees,
            registration.Observatory.LongitudeDegrees,
            registration.Observatory.ElevationMeters,
            registration.Observatory.TimeZoneId,
            registration.Observatory.AllowedDeploymentRadiusMeters,
            capturedAtUtc.AddDays(-1),
            "window-integration",
            CancellationToken.None).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var deploymentLocationId = $"{scenario}-window-location";
        var deploymentLocation = await db.DeviceDeploymentLocationVersions.SingleOrDefaultAsync(item =>
            item.RegistrationId == registration.Id && item.LocationId == deploymentLocationId && item.Version == 1)
            .ConfigureAwait(false);
        if (deploymentLocation is null)
        {
            var deploymentSnapshot = DeploymentLocationSnapshot.Create(
                deploymentLocationId,
                1,
                "window-integration",
                null,
                DateTimeOffset.UnixEpoch,
                null,
                observatoryLocation.LatitudeDegrees,
                observatoryLocation.LongitudeDegrees,
                observatoryLocation.ElevationMeters,
                observatoryLocation.TimeZoneId);
            _ = await uploadScope.ServiceProvider.GetRequiredService<IDeploymentLocationAuthorityService>()
                .ProposeAsync(
                    registration,
                    deploymentSnapshot,
                    DeploymentLocationSourceKind.Inherited,
                    "window-integration",
                    CancellationToken.None).ConfigureAwait(false);
            deploymentLocation = await db.DeviceDeploymentLocationVersions.SingleAsync(item =>
                item.RegistrationId == registration.Id && item.LocationId == deploymentLocationId && item.Version == 1)
                .ConfigureAwait(false);
        }
        var isBayer = pixelFormat == CameraPixelFormat.BayerRggb16;
        var rig = new CameraRigConfig(
            new SensorProfile(
                "window-sensor",
                width,
                height,
                5,
                isBayer ? SensorColorMode.Color : SensorColorMode.Mono,
                pixelFormat,
                isBayer ? SensorResponseMode.BayerRaw : SensorResponseMode.Monochrome,
                checked(width * 2),
                SensorRecipeVersion: "window-sensor-v1"),
            new OpticsProfile("Perspective", 0, 120, 0, LensKind.Rectilinear,
                CalibrationVersion: "window-calibration-v1"),
            new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(
                TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(900), 100, 100),
            ProfileVersion: "window-rig-v1");
        var rigSha = CameraRigProfileIdentity.ComputeSha256(rig);
        var rigProfile = await db.DeviceRigProfiles.SingleOrDefaultAsync(item =>
            item.DevicePublicId == devicePublicId && item.ProfileSha256 == rigSha).ConfigureAwait(false);
        if (rigProfile is null)
        {
            rigProfile = new DeviceRigProfile
            {
                RegistrationId = registration.Id,
                Registration = registration,
                DevicePublicId = devicePublicId,
                ObservatoryId = registration.ObservatoryId,
                Version = 1,
                ConfigHash = rigSha,
                ConfigJson = System.Text.Json.JsonSerializer.Serialize(rig),
                ProfileName = "rig",
                ProfileVersion = rig.ProfileVersion,
                ProfileSha256 = rigSha,
                CreatedAtUtc = capturedAtUtc,
                EffectiveFromUtc = capturedAtUtc
            };
            db.DeviceRigProfiles.Add(rigProfile);
        }
        var frame = new CentralFrame
        {
            RegistrationId = registration.Id,
            DevicePublicId = devicePublicId,
            ObservatoryId = registration.ObservatoryId,
            AgentId = scenario,
            FrameId = Guid.NewGuid(),
            RigProfileVersion = 1,
            DeviceRigProfileId = rigProfile.Id,
            DeviceRigProfile = rigProfile,
            RigId = $"{scenario}-rig",
            CaptureSequence = sequence,
            CapturedAtUtc = capturedAtUtc,
            FirstReceivedAtUtc = DateTimeOffset.UtcNow,
            LocationEvidenceState = CentralCaptureLocationEvidenceState.ReportedResolved,
            Location = new CentralCaptureLocation
            {
                DeviceDeploymentLocationVersionId = deploymentLocation.Id,
                DeploymentLocation = deploymentLocation,
                LocationId = deploymentLocation.LocationId,
                Version = deploymentLocation.Version,
                Source = deploymentLocation.Source,
                HorizontalAccuracyMeters = deploymentLocation.HorizontalAccuracyMeters,
                EffectiveFromUtc = deploymentLocation.EffectiveFromUtc,
                EffectiveUntilUtc = deploymentLocation.EffectiveUntilUtc
            }
        };
        frame.Timing = new CentralCaptureTiming
        {
            RequestedStartUtc = capturedAtUtc.AddSeconds(-2),
            ExposureStartedUtc = capturedAtUtc.AddSeconds(-1),
            ExposureEndedUtc = capturedAtUtc.AddMilliseconds(-100),
            ReadoutCompletedUtc = capturedAtUtc.AddMilliseconds(-50),
            DurableIngressUtc = capturedAtUtc
        };
        frame.Control = new CentralCaptureControl
        {
            RequestedExposureTicks = TimeSpan.FromMilliseconds(900).Ticks,
            EffectiveExposureTicks = TimeSpan.FromMilliseconds(900).Ticks,
            RequestedGain = 100,
            EffectiveGain = 100,
            RequestedOffset = 1,
            EffectiveOffset = 1,
            TemperatureSetpointC = -5,
            EffectiveTemperatureC = -5
        };
        var profileSha = HashText(profileSeed);
        var emptyMaskSha = CaptureContractJson.ComputeCanonicalJsonSha256(
            System.Text.Json.JsonSerializer.SerializeToElement(new { mode = "none" }));
        foreach (var kind in Enum.GetValues<CentralProfileKind>())
        {
            frame.Profiles.Add(new CentralCaptureProfile
            {
                Kind = kind,
                Name = kind switch
                {
                    CentralProfileKind.Rig => "rig",
                    CentralProfileKind.Mask => "mask",
                    _ => $"{scenario}-{kind}"
                },
                Version = kind switch
                {
                    CentralProfileKind.Rig => rig.ProfileVersion,
                    CentralProfileKind.Mask => "none-v1",
                    _ => "1"
                },
                Sha256 = kind switch
                {
                    CentralProfileKind.Rig => rigSha,
                    CentralProfileKind.Mask => emptyMaskSha,
                    _ => profileSha
                },
                DeviceRigProfileId = kind == CentralProfileKind.Rig ? rigProfile.Id : null,
                DeviceRigProfile = kind == CentralProfileKind.Rig ? rigProfile : null
            });
        }
        var rawOptions = CaptureContractJson.SerializeToElement(new { });
        var source = new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            DevicePublicId = devicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Raw,
            RecipeVersion = "window-raw-v1",
            ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
            MediaType = "application/x-hvo-linear-frame",
            ByteLength = payload.Length,
            ChecksumSha256 = checksum,
            StorageReference = $"object://{Bucket}/{objectKey}",
            ReceivedAtUtc = receivedAtUtc ?? DateTimeOffset.UtcNow,
            IdempotencyKey = HashText($"{scenario}-{sequence}"),
            SourceId = "window-integration",
            Variant = "native",
            CreatedUtc = capturedAtUtc,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete,
            ReconciledAtUtc = receivedAtUtc ?? DateTimeOffset.UtcNow,
            Layout = new CentralArtifactLayout
            {
                Width = width,
                Height = height,
                StrideBytes = checked(width * 2),
                PixelFormat = pixelFormat.ToString(),
                ByteOrder = FrameByteOrder.LittleEndian.ToString(),
                SampleDepthBits = 16,
                ContainerDepthBits = 16,
                Packing = FrameSamplePacking.ByteAligned.ToString(),
                CfaPattern = (isBayer ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None).ToString(),
                BlackLevel = isBayer ? 64 : 0,
                WhiteLevel = isBayer ? 16383 : ushort.MaxValue,
                ByteLength = payload.Length
            },
            Recipe = new CentralArtifactRecipe
            {
                Name = "raw-capture",
                SemanticVersion = "1.0.0",
                ImplementationVersion = "window-integration-v1",
                OptionsJson = CaptureContractJson.Canonicalize(rawOptions).GetRawText(),
                OptionsSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(rawOptions)
            }
        };
        db.CentralFrames.Add(frame);
        db.CentralArtifacts.Add(source);
        await db.SaveChangesAsync().ConfigureAwait(false);
        await uploadScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
            .EnsureRequiredJobsAsync(source, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        return source.Id;
    }

    private static async Task<CentralArtifact> LoadSchedulableArtifactAsync(ApplicationDbContext db, Guid sourceId)
        => await db.CentralArtifacts
            .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .SingleAsync(artifact => artifact.Id == sourceId).ConfigureAwait(false);

    internal static async Task ScheduleTransientAsync(Guid sourceId, CentralTransientOptions options)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var source = await LoadSchedulableArtifactAsync(db, sourceId).ConfigureAwait(false);
        var scheduler = new CentralDerivativeJobScheduler(
            db,
            new CentralDerivativeRecipeCatalog(options),
            scope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>());
        await scheduler.EnsureRequiredJobsAsync(source, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<CentralDerivativeExecutionResult> ExecuteClaimedTransientAsync(
        Guid expectedJobId,
        string workerId,
        ProcessingOutcomeStatus? expectedStatus = ProcessingOutcomeStatus.Produced)
    {
        CentralDerivativeJobLease lease;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            lease = (await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync(workerId, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false))!;
            lease.JobId.Should().Be(expectedJobId);
        }
        await using var executionScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var result = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
            .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        if (expectedStatus.HasValue)
        {
            result.Status.Should().Be(expectedStatus.Value);
        }
        return result;
    }

    private static async Task NotifySourceAsync(Guid sourceId)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var source = await LoadSchedulableArtifactAsync(db, sourceId).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
            .EnsureRequiredJobsAsync(source, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
    }

    private static Task DisableOtherActiveJobsAsync(Guid preservedJobId)
        => WithDbAsync(db => db.CentralDerivativeJobs.Where(job => job.Id != preservedJobId
                && (job.Status == CentralDerivativeJobStatus.Waiting
                    || job.Status == CentralDerivativeJobStatus.Pending
                    || job.Status == CentralDerivativeJobStatus.RetryableFailure))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)));

    /// <summary>
    /// Statuses in which a derivative job is finished. Every other status is one the health check still treats as an
    /// open window.
    /// </summary>
    /// <remarks>
    /// This is deliberately the terminal set rather than the active set. The health check reads five active statuses
    /// for its pin axis while <see cref="DisableOtherActiveJobsAsync"/> retires three, which is how foreign
    /// <c>Leased</c> and <c>CancelRequested</c> rows kept contributing input pins to an assertion that had already
    /// been told to ignore foreign state. Naming what is finished rather than what is open means a status added to
    /// the product later is covered here without anyone having to notice.
    /// </remarks>
    private static readonly CentralDerivativeJobStatus[] TerminalDerivativeJobStatuses =
    [
        CentralDerivativeJobStatus.Completed,
        CentralDerivativeJobStatus.TerminalFailure,
        CentralDerivativeJobStatus.Canceled,
        CentralDerivativeJobStatus.Skipped,
        CentralDerivativeJobStatus.Quarantined,
        CentralDerivativeJobStatus.Superseded
    ];

    /// <summary>
    /// Statuses in which a processing-graph execution is finished, matching the set the health check excludes.
    /// </summary>
    private static readonly CentralProcessingGraphExecutionStatus[] TerminalGraphExecutionStatuses =
    [
        CentralProcessingGraphExecutionStatus.Completed,
        CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures,
        CentralProcessingGraphExecutionStatus.Failed,
        CentralProcessingGraphExecutionStatus.Canceled,
        CentralProcessingGraphExecutionStatus.Superseded
    ];

    /// <summary>
    /// Retires every open derivative job and processing-graph execution anchored to an artifact that does not belong
    /// to <paramref name="ownedDevicePublicId"/>, so a health assertion afterwards reads the caller's own state
    /// rather than the assembly's accumulated database.
    /// </summary>
    /// <remarks>
    /// Ownership is the device, not the job and not the seeded source list. The worker legitimately creates jobs and
    /// derivative artifacts during a run, and the derivative output writer copies the source frame's
    /// device onto every output it writes, so a per-run device identifier covers the whole causal cone of the run
    /// while a list of seeded source ids would not. That keeps every row the run under test produced, so a genuine
    /// defect in that run still degrades the check; only rows anchored to another test's data are retired. The graph
    /// table is included because nothing else covers it: <see cref="DisableOtherActiveJobsAsync"/> touches derivative
    /// jobs alone, and a single foreign execution with a null <c>ExpandedAtUtc</c> reports the check Unhealthy
    /// outright.
    /// </remarks>
    private static async Task RetireForeignActiveStateAsync(Guid ownedDevicePublicId)
    {
        await WithDbAsync(async db =>
        {
            var ownedArtifactIds = db.CentralArtifacts
                .Where(artifact => artifact.DevicePublicId == ownedDevicePublicId)
                .Select(artifact => artifact.Id);
            await db.CentralDerivativeJobs
                .Where(job => !ownedArtifactIds.Contains(job.SourceCentralArtifactId)
                    && !TerminalDerivativeJobStatuses.Contains(job.Status))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
            // Superseding an execution is not a single-column write. Superseded requires a CompletedAtUtc, and
            // UpdatedAtUtc must not precede ExpandedAtUtc, so all three columns move together.
            //
            // Only a sealed execution is retired, and that limit is imposed by the schema rather than chosen. A row
            // whose ExpandedAtUtc is null is required by CK_CentralProcessingGraphExecutions_Expansion to stay
            // Pending, so retiring it means sealing it; sealing fires the frozen-count check in
            // TR_CentralProcessingGraphExecutions_IdentityImmutable, which requires the actual source, node,
            // dependency and output rows to match the four frozen counts, and those counts are themselves in the
            // trigger's immutable set. Deleting the row instead is rejected by the same trigger's first clause.
            // A partially expanded foreign execution is therefore permanently non-retirable by design, and a test
            // that meets one is looking at a genuine product state that the health check is right to report.
            var retiredAtUtc = DateTimeOffset.UtcNow;
            await db.CentralProcessingGraphExecutions
                .Where(execution => !ownedArtifactIds.Contains(execution.AnchorSourceCentralArtifactId)
                    && execution.ExpandedAtUtc != null
                    && !TerminalGraphExecutionStatuses.Contains(execution.Status))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(execution => execution.Status, CentralProcessingGraphExecutionStatus.Superseded)
                    .SetProperty(execution => execution.CompletedAtUtc, (DateTimeOffset?)retiredAtUtc)
                    .SetProperty(execution => execution.UpdatedAtUtc, retiredAtUtc))
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static async Task WithDbAsync(Func<ApplicationDbContext, Task> action)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()).ConfigureAwait(false);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await condition().ConfigureAwait(false))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for the Central transient runtime condition.");
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    private static void AssertBoundedTransientMetricTags(
        IReadOnlyCollection<RuntimeMetric> metrics,
        Dictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>> declaredMetrics)
    {
        foreach (var metric in metrics)
        {
            declaredMetrics.Should().ContainKey(metric.Name,
                $"every observed production metric must have finite labels in the runtime manifest");
            var declaredLabels = declaredMetrics[metric.Name];
            metric.Tags.Keys.Should().OnlyContain(key => declaredLabels.ContainsKey(key));
            foreach (var tag in metric.Tags)
            {
                declaredLabels[tag.Key].Should().Contain(tag.Value,
                    $"observed {metric.Name} tag {tag.Key}={tag.Value} must be declared by the runtime manifest");
            }
            metric.Tags.Values.Should().OnlyContain(value => value.Length <= 64);
        }

        foreach (var requiredLabel in new[] { "stage", "outcome", "cause", "classification", "direction" })
        {
            foreach (var metric in metrics.Where(metric => metric.Tags.ContainsKey(requiredLabel)))
            {
                declaredMetrics[metric.Name][requiredLabel].Should().Contain(metric.Tags[requiredLabel]);
            }
        }
        metrics.SelectMany(metric => metric.Tags.Select(tag => $"{tag.Key}={tag.Value}"))
            .Distinct(StringComparer.Ordinal).Count().Should().BeLessThanOrEqualTo(32);
    }

    private static Dictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>>
        ReadRuntimeMetricAllowlists()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root, "docs", "validation", "central-transient-runtime-signals.json")));
        return document.RootElement.GetProperty("metrics").EnumerateArray().ToDictionary(
            metric => metric.GetProperty("name").GetString()!,
            metric => (IReadOnlyDictionary<string, IReadOnlySet<string>>)metric.GetProperty("labels")
                .EnumerateObject().ToDictionary(
                    label => label.Name,
                    label => (IReadOnlySet<string>)label.Value.EnumerateArray()
                        .Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    private static bool IsDescendantOf(
        RuntimeActivity candidate,
        RuntimeActivity ancestor,
        IReadOnlyCollection<RuntimeActivity> activities)
    {
        var current = candidate;
        while (current.ParentSpanId != default)
        {
            if (current.ParentSpanId == ancestor.SpanId)
            {
                return true;
            }
            var parent = activities.FirstOrDefault(activity =>
                activity.TraceId == current.TraceId && activity.SpanId == current.ParentSpanId);
            if (parent is null)
            {
                return false;
            }
            current = parent;
        }
        return false;
    }

    private static IMinioClient CreateMinio(HttpMessageHandler handler)
        => new MinioClient()
            .WithEndpoint(AssemblyHooks.Fixture.MinioEndpoint)
            .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
            .WithHttpClient(new HttpClient(handler, disposeHandler: false), disposeHttpClient: true)
            .Build();

    private sealed class FailCanonicalCopyHandler(int failureNumber) : DelegatingHandler
    {
        private int copies;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put
                && request.Headers.Contains("x-amz-copy-source")
                && Interlocked.Increment(ref copies) == failureNumber)
            {
                throw new HttpRequestException("Injected canonical COPY failure.");
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ThrowBeforeCommitInterceptor(int commitNumber) : DbTransactionInterceptor
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
                throw new InvalidOperationException("Injected bundle commit rollback.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BlockAfterRetirementReadInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int blocked;

        public Task Entered => entered.Task;

        public void Release() => release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[RetentionDeletionToken]", StringComparison.Ordinal)
                && command.CommandText.Contains("[CentralArtifacts]", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }
    }

    private static async Task KillApplicationLockSessionsAsync(
        string connectionString,
        string applicationName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var sessionIds = new List<int>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT DISTINCT locks.[request_session_id]
                FROM [sys].[dm_tran_locks] AS locks
                INNER JOIN [sys].[dm_exec_sessions] AS sessions
                    ON sessions.[session_id] = locks.[request_session_id]
                WHERE locks.[resource_type] = N'APPLICATION'
                  AND locks.[request_owner_type] = N'SESSION'
                  AND locks.[request_status] = N'GRANT'
                  AND sessions.[program_name] = @applicationName;
                """;
            _ = command.Parameters.AddWithValue("@applicationName", applicationName);
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                sessionIds.Add(reader.GetInt32(0));
            }
        }
        sessionIds.Should().NotBeEmpty();
        foreach (var sessionId in sessionIds)
        {
            await using var kill = connection.CreateCommand();
            kill.CommandText = """
                DECLARE @command nvarchar(32) = N'KILL ' + CONVERT(nvarchar(11), @sessionId);
                EXEC [sys].[sp_executesql] @command;
                """;
            _ = kill.Parameters.AddWithValue("@sessionId", sessionId);
            await kill.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private static byte[] CreatePayload(ushort value)
    {
        var payload = new byte[8];
        for (var offset = 0; offset < payload.Length; offset += sizeof(ushort))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset, sizeof(ushort)), value);
        }
        return payload;
    }

    private static byte[] CreatePayload(IReadOnlyList<ushort> values)
    {
        var payload = new byte[values.Count * sizeof(ushort)];
        for (var index = 0; index < values.Count; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                payload.AsSpan(index * sizeof(ushort), sizeof(ushort)), values[index]);
        }
        return payload;
    }

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private sealed class TransientRuntimeCollector : IDisposable
    {
        private readonly MeterListener meter = new();
        private readonly ActivityListener activity;
        public ConcurrentQueue<RuntimeMetric> Metrics { get; } = new();
        public ConcurrentQueue<RuntimeActivity> Activities { get; } = new();

        public TransientRuntimeCollector()
        {
            meter.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CentralDerivativeWorkerTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Metrics.Enqueue(new RuntimeMetric(instrument.Name, value, ToTags(tags))));
            meter.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                Metrics.Enqueue(new RuntimeMetric(instrument.Name, value, ToTags(tags))));
            meter.Start();
            activity = new ActivityListener
            {
                ShouldListenTo = source => source.Name == CentralDerivativeWorkerTelemetry.ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = item => Activities.Enqueue(new RuntimeActivity(
                    item.OperationName,
                    item.Kind,
                    item.TraceId,
                    item.SpanId,
                    item.ParentSpanId,
                    item.TagObjects.ToDictionary(
                        tag => tag.Key,
                        tag => Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty,
                        StringComparer.Ordinal)))
            };
            ActivitySource.AddActivityListener(activity);
        }

        public void RecordObservableInstruments() => meter.RecordObservableInstruments();

        public void Dispose()
        {
            activity.Dispose();
            meter.Dispose();
        }

        private static Dictionary<string, string> ToTags(
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                result[tag.Key] = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
            return result;
        }
    }

    private sealed class TransientLogProvider : ILoggerProvider
    {
        public ConcurrentQueue<RuntimeLog> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new TransientLogger(categoryName, Entries);

        public void Dispose()
        {
        }
    }

    private sealed class TransientLogger(
        string category,
        ConcurrentQueue<RuntimeLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
            => category.Contains("CentralDerivative", StringComparison.Ordinal)
                || category.Contains("CentralTransient", StringComparison.Ordinal);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                entries.Enqueue(new RuntimeLog(category, logLevel, eventId, formatter(state, exception)));
            }
        }
    }

    private sealed record RuntimeMetric(
        string Name,
        double Value,
        IReadOnlyDictionary<string, string> Tags);

    private sealed record RuntimeActivity(
        string Name,
        ActivityKind Kind,
        ActivityTraceId TraceId,
        ActivitySpanId SpanId,
        ActivitySpanId ParentSpanId,
        IReadOnlyDictionary<string, string> Tags);

    private sealed record RuntimeLog(
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message);
}
