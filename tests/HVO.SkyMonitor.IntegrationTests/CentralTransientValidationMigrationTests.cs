using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CentralTransientValidationMigrationTests
{
    private const string PreviousMigration = "20260718213753_AddCentralCloudProcessing";
    private const string ShaA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ShaB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string ShaC = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";

    [TestMethod]
    public async Task CleanAndRepeatedMigrationProduceCurrentTransientSchema()
    {
        await using var database = CreateDatabase("Clean");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);

            (await database.Context.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
            await AssertSchemaAsync(database.Context).ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PreviousLatestUpgradePreservesPopulatedWorkerWindowState()
    {
        await using var database = CreateDatabase("Upgrade");
        try
        {
            var migrator = database.Context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);
            var source = AddSourceArtifact(database.Context, "upgrade-agent", DateTimeOffset.UnixEpoch);
            var job = AddDerivativeJob(database.Context, source, DateTimeOffset.UnixEpoch);
            job.InputRequirements.Add(new CentralDerivativeJobInputRequirement
            {
                Ordinal = 0,
                BindingName = "center",
                SourceKind = CentralDerivativeInputSourceKind.Artifact,
                SequenceOffset = 0,
                IsRequired = true,
                SelectorJson = "{}",
                CompatibilityMode = CentralDerivativeCompatibilityMode.Exact,
                ExpectedAgentId = "upgrade-agent",
                ExpectedCentralArtifactId = source.Id,
                ResolutionState = CentralDerivativeInputResolutionState.Resolved,
                ResolvedAtUtc = DateTimeOffset.UnixEpoch
            });
            await database.Context.SaveChangesAsync().ConfigureAwait(false);

            await migrator.MigrateAsync().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            (await database.Context.CentralDerivativeJobs.CountAsync(item => item.Id == job.Id).ConfigureAwait(false))
                .Should().Be(1);
            (await database.Context.CentralDerivativeJobInputRequirements
                .CountAsync(item => item.CentralDerivativeJobId == job.Id).ConfigureAwait(false)).Should().Be(1);
            (await database.Context.CentralTransientEvents.CountAsync().ConfigureAwait(false)).Should().Be(0);
            (await database.Context.CentralTransientValidationJobs.CountAsync().ConfigureAwait(false)).Should().Be(0);
            await AssertSchemaAsync(database.Context).ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task DownAndUpRollbackPreservesExistingWorkerRows()
    {
        await using var database = CreateDatabase("Rollback");
        try
        {
            var migrator = database.Context.GetService<IMigrator>();
            await migrator.MigrateAsync().ConfigureAwait(false);
            var source = AddSourceArtifact(database.Context, "rollback-agent", DateTimeOffset.UnixEpoch);
            var job = AddDerivativeJob(database.Context, source, DateTimeOffset.UnixEpoch);
            await database.Context.SaveChangesAsync().ConfigureAwait(false);

            await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);
            var transientTableCount = await database.Context.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                FROM [sys].[tables]
                WHERE [name] LIKE N'CentralTransient%'
                """).SingleAsync().ConfigureAwait(false);
            transientTableCount.Should().Be(0);
            var workerCount = await database.Context.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value] FROM [CentralDerivativeJobs] WHERE [Id] = {job.Id}
                """).SingleAsync().ConfigureAwait(false);
            workerCount.Should().Be(1);

            await migrator.MigrateAsync().ConfigureAwait(false);
            await AssertSchemaAsync(database.Context).ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ValidationJobPersistsOpaqueIdentitySlotsAgainstGenericJobState()
    {
        await using var database = CreateDatabase("JobIdentity");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UnixEpoch.AddMinutes(30);
            var source = AddSourceArtifact(database.Context, "job-agent", now);
            var derivativeJob = AddDerivativeJob(database.Context, source, now);
            var slot = new CentralTransientValidationIdentitySlot
            {
                Ordinal = 0,
                EventId = Guid.NewGuid(),
                CandidateId = Guid.NewGuid(),
                ObservationId = Guid.NewGuid(),
                AssessmentId = Guid.NewGuid()
            };
            var validationJob = new CentralTransientValidationJob
            {
                CentralDerivativeJobId = derivativeJob.Id,
                AgentId = "job-agent",
                SubmissionSchemaVersion = "central-transient-validation-submission-v1",
                SubmissionIdentitySha256 = ShaC,
                CreatedAtUtc = now
            };
            validationJob.IdentitySlots.Add(slot);
            database.Context.CentralTransientValidationJobs.Add(validationJob);
            await database.Context.SaveChangesAsync().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            var persisted = await database.Context.CentralTransientValidationJobs.AsNoTracking()
                .Include(item => item.IdentitySlots)
                .SingleAsync(item => item.CentralDerivativeJobId == derivativeJob.Id).ConfigureAwait(false);
            persisted.SubmissionIdentitySha256.Should().Be(ShaC);
            persisted.IdentitySlots.Single().Should().Match<CentralTransientValidationIdentitySlot>(item =>
                item.EventId == slot.EventId
                && item.CandidateId == slot.CandidateId
                && item.ObservationId == slot.ObservationId
                && item.AssessmentId == slot.AssessmentId);
            (await database.Context.CentralDerivativeJobs.AsNoTracking()
                .Where(item => item.Id == derivativeJob.Id)
                .Select(item => item.Status).SingleAsync().ConfigureAwait(false)).Should().Be(CentralDerivativeJobStatus.Waiting);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task VersionHistoryOrderedEvidenceAndRetentionReferencesRemainVisible()
    {
        await using var database = CreateDatabase("History");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UnixEpoch.AddHours(1);
            var source = AddSourceArtifact(database.Context, "history-agent", now);
            var background = AddSourceArtifact(database.Context, "history-agent", now.AddSeconds(-1));
            var eventRecord = CreateEvent("history-agent", now, source, background);
            var firstVersion = CreateVersion(eventRecord, 1, now.AddSeconds(1));
            var firstAssessment = CreateAssessment(eventRecord, now, Guid.NewGuid(), ShaA, null);
            eventRecord.Assessments.Add(firstAssessment);
            eventRecord.Versions.Add(firstVersion);
            database.Context.CentralTransientEvents.Add(eventRecord);
            await database.Context.SaveChangesAsync().ConfigureAwait(false);
            database.Context.AddRange(
                new CentralTransientEventVersionObservation
                {
                    CentralTransientEventId = eventRecord.Id,
                    EventVersionId = firstVersion.EventVersionId,
                    ObservationId = eventRecord.Observations.Single().ObservationId,
                    Ordinal = 0
                },
                new CentralTransientEventVersionAssessment
                {
                    CentralTransientEventId = eventRecord.Id,
                    EventVersionId = firstVersion.EventVersionId,
                    AssessmentId = firstAssessment.AssessmentId,
                    Ordinal = 0
                },
                new CentralTransientAssessmentObservation
                {
                    CentralTransientEventId = eventRecord.Id,
                    AssessmentId = firstAssessment.AssessmentId,
                    ObservationId = eventRecord.Observations.Single().ObservationId,
                    Ordinal = 0
                });
            await database.Context.SaveChangesAsync().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            var secondAssessment = CreateAssessment(eventRecord, now.AddSeconds(2), Guid.NewGuid(), ShaB,
                firstAssessment.AssessmentId);
            var secondVersion = CreateVersion(eventRecord, 2, now.AddSeconds(3), firstVersion);
            database.Context.CentralTransientAssessments.Add(secondAssessment);
            database.Context.CentralTransientEventVersions.Add(secondVersion);
            await database.Context.SaveChangesAsync().ConfigureAwait(false);
            database.Context.AddRange(
                new CentralTransientAssessmentObservation
                {
                    CentralTransientEventId = eventRecord.Id,
                    AssessmentId = secondAssessment.AssessmentId,
                    ObservationId = eventRecord.Observations.Single().ObservationId,
                    Ordinal = 0
                },
                new CentralTransientEventVersionObservation
                {
                    CentralTransientEventId = eventRecord.Id,
                    EventVersionId = secondVersion.EventVersionId,
                    ObservationId = eventRecord.Observations.Single().ObservationId,
                    Ordinal = 0
                },
                new CentralTransientEventVersionAssessment
                {
                    CentralTransientEventId = eventRecord.Id,
                    EventVersionId = secondVersion.EventVersionId,
                    AssessmentId = firstAssessment.AssessmentId,
                    Ordinal = 0
                },
                new CentralTransientEventVersionAssessment
                {
                    CentralTransientEventId = eventRecord.Id,
                    EventVersionId = secondVersion.EventVersionId,
                    AssessmentId = secondAssessment.AssessmentId,
                    Ordinal = 1
                });
            await database.Context.SaveChangesAsync().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            var versions = await database.Context.CentralTransientEventVersions.AsNoTracking()
                .Where(item => item.CentralTransientEventId == eventRecord.Id)
                .OrderBy(item => item.Version).ToListAsync().ConfigureAwait(false);
            versions.Select(item => item.EventVersionId).Should().Equal(firstVersion.EventVersionId, secondVersion.EventVersionId);
            versions[1].PreviousEventVersionId.Should().Be(firstVersion.EventVersionId);
            (await database.Context.CentralTransientAssessments.AsNoTracking()
                .CountAsync(item => item.CentralTransientEventId == eventRecord.Id).ConfigureAwait(false)).Should().Be(2);
            var assessmentOrder = await database.Context.Set<CentralTransientEventVersionAssessment>().AsNoTracking()
                .Where(item => item.EventVersionId == secondVersion.EventVersionId)
                .OrderBy(item => item.Ordinal).Select(item => item.AssessmentId).ToListAsync().ConfigureAwait(false);
            assessmentOrder.Should().Equal(firstAssessment.AssessmentId, secondAssessment.AssessmentId);

            var retention = new CentralArtifactRetentionReferences(database.Context);
            (await retention.IsHeldAsync(source.Id, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            (await retention.IsHeldAsync(background.Id, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();

            database.Context.CentralArtifacts.Remove(await database.Context.CentralArtifacts
                .SingleAsync(item => item.Id == source.Id).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<DbUpdateException>(async () =>
                await database.Context.SaveChangesAsync().ConfigureAwait(false)).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            (await database.Context.CentralTransientEventVersions.CountAsync(
                item => item.CentralTransientEventId == eventRecord.Id).ConfigureAwait(false)).Should().Be(2);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task DatabaseRejectsDuplicateIdentityForkAndCrossEventPredecessor()
    {
        await using var database = CreateDatabase("Constraints");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UnixEpoch.AddHours(2);
            var source = AddSourceArtifact(database.Context, "constraint-agent", now);
            var background = AddSourceArtifact(database.Context, "constraint-agent", now.AddSeconds(-1));
            var firstEvent = CreateEvent("constraint-agent", now, source, background);
            var firstVersion = CreateVersion(firstEvent, 1, now.AddSeconds(1));
            var assessment = CreateAssessment(firstEvent, now, Guid.NewGuid(), ShaA, null);
            firstEvent.Versions.Add(firstVersion);
            firstEvent.Assessments.Add(assessment);
            database.Context.CentralTransientEvents.Add(firstEvent);
            await database.Context.SaveChangesAsync().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            database.Context.CentralTransientEvents.Add(new CentralTransientEventRecord
            {
                AgentId = firstEvent.AgentId,
                EventId = firstEvent.EventId,
                EventCreatedUtc = now
            });
            await AssertSaveRejectedAsync(database.Context).ConfigureAwait(false);

            database.Context.CentralTransientAssessments.Add(CreateAssessment(firstEvent, now.AddSeconds(1),
                Guid.NewGuid(), ShaA, null));
            await AssertSaveRejectedAsync(database.Context).ConfigureAwait(false);

            var secondVersion = CreateVersion(firstEvent, 2, now.AddSeconds(2), firstVersion);
            database.Context.CentralTransientEventVersions.Add(secondVersion);
            await database.Context.SaveChangesAsync().ConfigureAwait(false);
            database.Context.CentralTransientEventVersions.Add(CreateVersion(firstEvent, 3, now.AddSeconds(3), firstVersion));
            await AssertSaveRejectedAsync(database.Context).ConfigureAwait(false);

            var secondEvent = new CentralTransientEventRecord
            {
                AgentId = "other-agent",
                EventId = Guid.NewGuid(),
                EventCreatedUtc = now
            };
            database.Context.CentralTransientEvents.Add(secondEvent);
            await database.Context.SaveChangesAsync().ConfigureAwait(false);
            database.Context.CentralTransientEventVersions.Add(new CentralTransientEventVersionRecord
            {
                EventVersionId = Guid.NewGuid(),
                CentralTransientEventId = secondEvent.Id,
                Version = 2,
                PreviousVersionNumber = 1,
                PreviousEventVersionId = firstVersion.EventVersionId,
                PreviousVersionCreatedUtc = firstVersion.VersionCreatedUtc,
                State = TransientEventState.NeedsReview,
                VersionCreatedUtc = now.AddSeconds(4),
                FirstObservedUtc = now.AddSeconds(-1),
                LastObservedUtc = now,
                SchemaVersion = TransientEventV1.CurrentSchemaVersion,
                CanonicalEventJson = "{}",
                CanonicalEventSha256 = ShaB,
                CanonicalEventByteLength = 2
            });
            await AssertSaveRejectedAsync(database.Context).ConfigureAwait(false);

            var selfVersionId = Guid.NewGuid();
            database.Context.CentralTransientEventVersions.Add(new CentralTransientEventVersionRecord
            {
                EventVersionId = selfVersionId,
                CentralTransientEventId = firstEvent.Id,
                Version = 3,
                PreviousVersionNumber = 2,
                PreviousEventVersionId = selfVersionId,
                PreviousVersionCreatedUtc = secondVersion.VersionCreatedUtc,
                State = TransientEventState.NeedsReview,
                VersionCreatedUtc = now.AddSeconds(4),
                FirstObservedUtc = now.AddSeconds(-1),
                LastObservedUtc = now,
                SchemaVersion = TransientEventV1.CurrentSchemaVersion,
                CanonicalEventJson = "{}",
                CanonicalEventSha256 = ShaB,
                CanonicalEventByteLength = 2
            });
            await AssertSaveRejectedAsync(database.Context).ConfigureAwait(false);

            database.Context.CentralTransientEventVersions.Add(new CentralTransientEventVersionRecord
            {
                EventVersionId = Guid.NewGuid(),
                CentralTransientEventId = firstEvent.Id,
                Version = 3,
                PreviousVersionNumber = 2,
                PreviousEventVersionId = secondVersion.EventVersionId,
                PreviousVersionCreatedUtc = secondVersion.VersionCreatedUtc.AddTicks(1),
                State = TransientEventState.NeedsReview,
                VersionCreatedUtc = now.AddSeconds(4),
                FirstObservedUtc = now.AddSeconds(-1),
                LastObservedUtc = now,
                SchemaVersion = TransientEventV1.CurrentSchemaVersion,
                CanonicalEventJson = "{}",
                CanonicalEventSha256 = ShaB,
                CanonicalEventByteLength = 2
            });
            await AssertSaveRejectedAsync(database.Context).ConfigureAwait(false);

            var selfAssessmentId = Guid.NewGuid();
            database.Context.CentralTransientAssessments.Add(CreateAssessment(
                firstEvent, now.AddSeconds(4), selfAssessmentId, ShaB, selfAssessmentId));
            await AssertSaveRejectedAsync(database.Context).ConfigureAwait(false);

            var cycleFirstId = Guid.NewGuid();
            var cycleSecondId = Guid.NewGuid();
            database.Context.CentralTransientAssessments.AddRange(
                CreateAssessment(firstEvent, now.AddSeconds(6), cycleFirstId, ShaB, cycleSecondId),
                CreateAssessment(firstEvent, now.AddSeconds(7), cycleSecondId, ShaC, cycleFirstId));
            await AssertSaveRejectedAsync(database.Context).ConfigureAwait(false);

            var missingSource = CreateEvent("constraint-agent", now, source, background).Observations.Single();
            missingSource.CentralTransientEventId = firstEvent.Id;
            missingSource.Source = null;
            missingSource.SourceReferenceId = Guid.NewGuid();
            database.Context.CentralTransientObservations.Add(missingSource);
            await AssertSaveRejectedAsync(database.Context).ConfigureAwait(false);

            var partialJob = AddDerivativeJob(database.Context, source, now.AddSeconds(5));
            partialJob.RequestIdentitySha256 = ShaC;
            var partialValidation = new CentralTransientValidationJob
            {
                CentralDerivativeJobId = partialJob.Id,
                AgentId = "constraint-agent",
                SubmissionSchemaVersion = "central-transient-validation-submission-v1",
                SubmissionIdentitySha256 = ShaA,
                CreatedAtUtc = now.AddSeconds(5)
            };
            partialValidation.IdentitySlots.Add(new CentralTransientValidationIdentitySlot
            {
                Ordinal = 0,
                State = CentralTransientValidationIdentitySlotState.Committed,
                EventId = Guid.NewGuid(),
                CandidateId = Guid.NewGuid(),
                ObservationId = Guid.NewGuid(),
                AssessmentId = Guid.NewGuid()
            });
            database.Context.CentralTransientValidationJobs.Add(partialValidation);
            await AssertSaveRejectedAsync(database.Context).ConfigureAwait(false);

            database.Context.CentralTransientEvents.Remove(await database.Context.CentralTransientEvents
                .SingleAsync(item => item.Id == firstEvent.Id).ConfigureAwait(false));
            await AssertSaveRejectedAsync(database.Context).ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static CentralTransientEventRecord CreateEvent(
        string agentId,
        DateTimeOffset now,
        CentralArtifact source,
        CentralArtifact background)
    {
        var record = new CentralTransientEventRecord
        {
            AgentId = agentId,
            EventId = Guid.NewGuid(),
            EventCreatedUtc = now
        };
        var observation = new CentralTransientObservationRecord
        {
            ObservationId = Guid.NewGuid(),
            CentralTransientEventId = record.Id,
            DetectorInputIdentitySha256 = ShaA,
            CalibrationIdentity = "calibration-v1",
            MaskIdentity = "mask-v1",
            ProcessingProfileIdentity = "processing-v1",
            OriginatingCandidateId = Guid.NewGuid(),
            ExtractionProducerSchemaVersion = TransientExtractionProducerV1.CurrentSchemaVersion,
            ExtractionProducerKind = TransientExtractionProducerKind.DeterministicAlgorithm,
            ExtractionProducerName = "extractor",
            ExtractionProducerVersion = "v1",
            ExtractionRecipeIdentitySha256 = ShaB,
            ExtractionReceiptIdentitySha256 = ShaC,
            GeometryJson = "{}",
            FeaturesJson = "{}",
            Source = new CentralTransientObservationSourceReference
            {
                CentralArtifactId = source.Id,
                EvidenceSchemaVersion = TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
                EvidenceId = Guid.NewGuid(),
                LocatorSchemaVersion = TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                LocatorKind = TransientSourceLocatorKind.WholeArtifact,
                ArtifactId = source.ArtifactId,
                ArtifactRole = source.Role,
                ArtifactVariant = source.Variant!,
                ArtifactRecipeIdentitySha256 = ShaA,
                ArtifactChecksumSha256 = source.ChecksumSha256,
                ObservationStartedUtc = now.AddSeconds(-1),
                ObservationEndedUtc = now,
                TimingQuality = TransientTimingQuality.Reported,
                TimingProvenanceSource = "camera",
                TimingProvenanceVersion = "v1"
            },
            SourceReferenceId = Guid.Empty
        };
        observation.Source!.ObservationId = observation.ObservationId;
        observation.SourceReferenceId = observation.ObservationId;
        observation.Backgrounds.Add(new CentralTransientObservationBackgroundReference
        {
            Ordinal = 0,
            CentralArtifactId = background.Id,
            ArtifactId = background.ArtifactId,
            ArtifactRole = background.Role,
            ArtifactVariant = background.Variant!,
            ArtifactRecipeIdentitySha256 = ShaA,
            ArtifactChecksumSha256 = background.ChecksumSha256
        });
        record.Observations.Add(observation);
        return record;
    }

    private static CentralTransientEventVersionRecord CreateVersion(
        CentralTransientEventRecord eventRecord,
        int version,
        DateTimeOffset createdUtc,
        CentralTransientEventVersionRecord? previous = null)
        => new()
        {
            EventVersionId = Guid.NewGuid(),
            CentralTransientEventId = eventRecord.Id,
            Version = version,
            PreviousVersionNumber = previous is null ? null : version - 1,
            PreviousEventVersionId = previous?.EventVersionId,
            PreviousVersionCreatedUtc = previous?.VersionCreatedUtc,
            State = version == 1 ? TransientEventState.Pending : TransientEventState.Validated,
            VersionCreatedUtc = createdUtc,
            FirstObservedUtc = eventRecord.EventCreatedUtc.AddSeconds(-1),
            LastObservedUtc = eventRecord.EventCreatedUtc,
            SchemaVersion = TransientEventV1.CurrentSchemaVersion,
            CanonicalEventJson = "{}",
            CanonicalEventSha256 = version == 1 ? ShaA : ShaB,
            CanonicalEventByteLength = 2
        };

    private static CentralTransientAssessmentRecord CreateAssessment(
        CentralTransientEventRecord eventRecord,
        DateTimeOffset createdUtc,
        Guid assessmentId,
        string executionIdentity,
        Guid? supersedes)
        => new()
        {
            AssessmentId = assessmentId,
            CentralTransientEventId = eventRecord.Id,
            CreatedUtc = createdUtc,
            Authority = TransientAssessmentAuthority.Authoritative,
            Classification = TransientClassification.Meteor,
            MeteorSeverity = TransientMeteorSeverity.Meteor,
            ConfidenceMillionths = 900_000,
            SupersedesAssessmentId = supersedes,
            SupersedesAssessmentCreatedUtc = supersedes is null ? null : createdUtc.AddSeconds(-2),
            ProducerSchemaVersion = TransientAssessmentProducerV1.CurrentSchemaVersion,
            ProducerKind = TransientAssessmentProducerKind.DeterministicAlgorithm,
            ProducerName = "assessor",
            ProducerVersion = "v1",
            RecipeIdentitySha256 = ShaC,
            ReceiptSchemaVersion = TransientAssessmentExecutionDescriptorV1.CurrentSchemaVersion,
            ExecutionIdentitySha256 = executionIdentity,
            OptionsIdentitySha256 = ShaB,
            CanonicalReceiptJson = "{}",
            CanonicalReceiptSha256 = executionIdentity,
            CanonicalReceiptByteLength = 2
        };

    private static CentralArtifact AddSourceArtifact(ApplicationDbContext db, string agentId, DateTimeOffset now)
    {
        var frame = new CentralFrame
        {
            RegistrationId = Guid.NewGuid(),
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = Guid.NewGuid(),
            AgentId = agentId,
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = now,
            FirstReceivedAtUtc = now
        };
        var artifact = new CentralArtifact
        {
            CentralFrameId = frame.Id,
            ArtifactId = Guid.NewGuid(),
            DevicePublicId = frame.DevicePublicId,
            Role = FrameArtifactRole.Raw,
            RecipeVersion = "raw-v1",
            ManifestSchemaVersion = "v2",
            MediaType = "application/octet-stream",
            ByteLength = 2,
            ChecksumSha256 = ShaA,
            StorageReference = $"minio://skymonitor-artifacts/{Guid.NewGuid():N}.bin",
            ReceivedAtUtc = now,
            IdempotencyKey = Guid.NewGuid().ToString("N").PadRight(64, '0'),
            Variant = "raw",
            CreatedUtc = now,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        frame.Artifacts.Add(artifact);
        db.CentralFrames.Add(frame);
        return artifact;
    }

    private static CentralDerivativeJob AddDerivativeJob(ApplicationDbContext db, CentralArtifact source, DateTimeOffset now)
    {
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = source.Id,
            TargetRole = FrameArtifactRole.Metadata,
            TargetRecipeVersion = "transient-validation-v1",
            TargetVariant = "transient-validation",
            RecipeName = "transient-validation",
            RecipeOptionsJson = "{}",
            InputSelectorJson = "{}",
            RequestedRecipeIdentitySha256 = ShaA,
            ExpectedRecipeIdentitySha256 = ShaA,
            RequestIdentitySha256 = ShaB,
            Status = CentralDerivativeJobStatus.Waiting,
            AttemptCount = 0,
            MaxAttempts = 3,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.CentralDerivativeJobs.Add(job);
        return job;
    }

    private static async Task AssertSaveRejectedAsync(ApplicationDbContext db)
    {
        await Assert.ThrowsExactlyAsync<DbUpdateException>(async () =>
            await db.SaveChangesAsync().ConfigureAwait(false)).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    private static async Task AssertSchemaAsync(ApplicationDbContext db)
    {
        var tableCount = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS [Value]
            FROM [sys].[tables]
            WHERE [name] IN
                (N'CentralTransientEvents', N'CentralTransientEventVersions', N'CentralTransientObservations',
                 N'CentralTransientObservationSources', N'CentralTransientObservationBackgrounds',
                 N'CentralTransientAssessments', N'CentralTransientEventVersionObservations',
                 N'CentralTransientEventVersionAssessments', N'CentralTransientAssessmentObservations',
                 N'CentralTransientValidationJobs', N'CentralTransientValidationIdentitySlots',
                 N'CentralTransientExtractionReceipts', N'CentralTransientExtractionSources')
            """).SingleAsync().ConfigureAwait(false);
        tableCount.Should().Be(13);
        var constraints = await db.Database.SqlQuery<string>($"""
            SELECT [name] AS [Value]
            FROM [sys].[check_constraints]
            WHERE [parent_object_id] IN
                (OBJECT_ID(N'[CentralTransientEventVersions]'), OBJECT_ID(N'[CentralTransientAssessments]'),
                 OBJECT_ID(N'[CentralTransientValidationJobs]'))
            """).ToListAsync().ConfigureAwait(false);
        constraints.Should().Contain([
            "CK_CentralTransientEventVersions_Predecessor",
            "CK_CentralTransientEventVersions_ObservedInterval",
            "CK_CentralTransientAssessments_Confidence",
            "CK_CentralTransientAssessments_Predecessor"
        ]);
        var triggerCount = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS [Value]
            FROM [sys].[triggers]
            WHERE [name] LIKE N'TR_CentralTransient%'
            """).SingleAsync().ConfigureAwait(false);
        triggerCount.Should().Be(13);
    }

    private static MigrationDatabase CreateDatabase(string scenario)
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorTransient{scenario}_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new MigrationDatabase(new ApplicationDbContext(options));
    }

    private sealed class MigrationDatabase(ApplicationDbContext context) : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; } = context;
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}
