using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed partial class CentralTransientEventPersistenceIntegrationTests
{
    [TestMethod]
    public async Task AppendPersistsVerifiedCanonicalHistoryAndFinalizesEverySlot()
    {
        await using var database = CreateDatabase("Append");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            var service = new CentralTransientEventPersistence(database.Context);

            var commit = await service.AppendAsync(fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var adopted = await service.AppendAsync(fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            adopted.Should().BeEquivalentTo(commit, options => options.WithStrictOrdering());
            database.Context.ChangeTracker.Clear();

            commit.EventVersionIds.Should().Equal(fixture.Event.EventVersionId);
            commit.AssessmentIds.Should().Equal(fixture.Assessment.Assessment.AssessmentId);
            var version = await database.Context.CentralTransientEventVersions.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            version.CanonicalEventSha256.Should().Be(fixture.Request.Events.Single().Sha256);
            version.CanonicalEventByteLength.Should().Be(fixture.Request.Events.Single().ByteLength);
            var storedObservation = await database.Context.CentralTransientObservations.AsNoTracking()
                .Include(item => item.Source).SingleAsync().ConfigureAwait(false);
            storedObservation.DetectorInputIdentitySha256.Should()
                .Be(fixture.Event.Observations.Single().Provenance.DetectorInputIdentitySha256);
            storedObservation.ExtractionReceiptIdentitySha256.Should().Be(fixture.Extraction.ExtractionIdentitySha256);
            storedObservation.Source!.ArtifactId.Should().Be(fixture.Event.Observations.Single().Source.Locator.Artifact.ArtifactId);
            var storedAssessment = await database.Context.CentralTransientAssessments.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            storedAssessment.ExecutionIdentitySha256.Should().Be(fixture.Assessment.ExecutionIdentitySha256);
            storedAssessment.RecipeIdentitySha256.Should().Be(fixture.Assessment.Assessment.RecipeIdentitySha256);
            storedAssessment.ProducerName.Should().Be(fixture.Assessment.Assessment.Producer.Name);
            var current = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            current.LatestEventVersionId.Should().Be(fixture.Event.EventVersionId);
            current.ActiveAssessmentId.Should().Be(fixture.Assessment.Assessment.AssessmentId);
            current.ReviewState.Should().Be(CentralTransientReviewState.NeedsReview);
            current.RowVersion.Should().HaveCount(8);
            var storedExtraction = await database.Context.CentralTransientExtractionReceipts.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            storedExtraction.CanonicalReceiptSha256.Should().Be(fixture.Request.ExtractionReceipt.Sha256);
            storedExtraction.CanonicalReceiptByteLength.Should().Be(fixture.Request.ExtractionReceipt.ByteLength);
            var slots = await database.Context.CentralTransientValidationIdentitySlots.AsNoTracking()
                .OrderBy(item => item.Ordinal).ToListAsync().ConfigureAwait(false);
            slots.Select(item => item.State).Should().Equal(
                CentralTransientValidationIdentitySlotState.Committed,
                CentralTransientValidationIdentitySlotState.Unused);
            slots[0].Should().Match<CentralTransientValidationIdentitySlot>(item =>
                item.PersistedObservationId == item.ObservationId
                && item.PersistedAssessmentId == item.AssessmentId
                && item.PersistedEventId == fixture.Event.EventId);
            (await database.Context.CentralTransientExtractionSources.CountAsync().ConfigureAwait(false))
                .Should().Be(fixture.Extraction.OrderedSources.Count);

            var retention = new CentralArtifactRetentionReferences(database.Context);
            foreach (var artifact in seeded.Artifacts)
            {
                (await retention.IsHeldAsync(artifact.Id, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            }

            var update = async () => await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralTransientEventVersions]
                SET [CanonicalEventSha256] = {new string('F', 64)}
                WHERE [EventVersionId] = {fixture.Event.EventVersionId};
                """).ConfigureAwait(false);
            await update.Should().ThrowAsync<SqlException>().ConfigureAwait(false);
            var delete = async () => await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM [CentralTransientEvents] WHERE [EventId] = {fixture.Event.EventId};
                """).ConfigureAwait(false);
            await delete.Should().ThrowAsync<SqlException>().ConfigureAwait(false);
            var mutateSlot = async () => await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralTransientValidationIdentitySlots]
                SET [State] = N'Unused', [CentralTransientEventId] = NULL,
                    [PersistedEventVersionId] = NULL, [PersistedObservationId] = NULL, [PersistedAssessmentId] = NULL
                WHERE [CentralDerivativeJobId] = {seeded.JobId} AND [Ordinal] = 0;
                """).ConfigureAwait(false);
            await mutateSlot.Should().ThrowAsync<SqlException>().ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ReviewMutation_AppendsAuditAndVersionWithReplayBeforeEtag()
    {
        await using var database = CreateDatabase("ReviewMutation");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            var persistence = new CentralTransientEventPersistence(database.Context);
            _ = await persistence.AppendAsync(fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            var current = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            var principal = CreateOwnerPrincipal("reviewer-1", admin: true);
            var reviewService = new CentralTransientReviewService(
                database.Context,
                new CentralTransientEventVersionAppender(database.Context),
                TimeProvider.System);
            var request = new CentralTransientReviewRequest(
                fixture.Assessment.Assessment.AssessmentId,
                TransientReviewDisposition.Confirmed,
                null,
                ["human.confirmed"]);

            var applied = await reviewService.ReviewAsync(
                principal,
                current.CentralTransientEventId,
                current.RowVersion,
                "review-key-1",
                request,
                CancellationToken.None).ConfigureAwait(false);
            applied.Status.Should().Be(CentralTransientReviewMutationStatus.Applied);
            applied.Response!.Replayed.Should().BeFalse();
            applied.Response.ReviewState.Should().Be(CentralTransientReviewState.Reviewed);
            database.Context.ChangeTracker.Clear();

            var replayed = await reviewService.ReviewAsync(
                principal,
                current.CentralTransientEventId,
                current.RowVersion,
                "review-key-1",
                request,
                CancellationToken.None).ConfigureAwait(false);
            replayed.Status.Should().Be(CentralTransientReviewMutationStatus.Applied);
            replayed.Response.Should().Be(applied.Response with { Replayed = true });

            var conflict = await reviewService.ReviewAsync(
                principal,
                current.CentralTransientEventId,
                current.RowVersion,
                "review-key-1",
                request with { ReasonCodes = ["human.changed"] },
                CancellationToken.None).ConfigureAwait(false);
            conflict.Status.Should().Be(CentralTransientReviewMutationStatus.IdempotencyConflict);

            var stale = await reviewService.ReviewAsync(
                principal,
                current.CentralTransientEventId,
                current.RowVersion,
                "review-key-2",
                request,
                CancellationToken.None).ConfigureAwait(false);
            stale.Status.Should().Be(CentralTransientReviewMutationStatus.PreconditionFailed);

            (await database.Context.CentralTransientReviews.AsNoTracking().CountAsync().ConfigureAwait(false))
                .Should().Be(1);
            (await database.Context.CentralTransientReviewMutations.AsNoTracking().CountAsync().ConfigureAwait(false))
                .Should().Be(1);
            (await database.Context.CentralTransientEventVersions.AsNoTracking().CountAsync().ConfigureAwait(false))
                .Should().Be(2);
            var latest = await database.Context.CentralTransientEventVersions.AsNoTracking()
                .OrderByDescending(item => item.Version).FirstAsync().ConfigureAwait(false);
            var parsed = TransientContractJson.ParseEvent(Encoding.UTF8.GetBytes(latest.CanonicalEventJson));
            parsed.Validation.IsValid.Should().BeTrue();
            parsed.Value!.Assessments.Should().BeEquivalentTo(fixture.Event.Assessments);
            parsed.Value.Reviews.Should().ContainSingle(item => item.ReviewId == applied.Response.ReviewId);

            await CentralTransientValidationOutcome.RecordNeedsReviewAsync(
                database.Context,
                seeded.JobId,
                "transient-review.source-invalidated",
                latest.VersionCreatedUtc.AddSeconds(1),
                CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var invalidated = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            invalidated.ReviewState.Should().Be(CentralTransientReviewState.NeedsReview);
            invalidated.LatestReviewId.Should().BeNull();
            invalidated.EffectiveClassification.Should().Be(fixture.Assessment.Assessment.Classification);
            (await database.Context.CentralTransientReviews.CountAsync().ConfigureAwait(false)).Should().Be(1);
            (await database.Context.CentralTransientEventVersions.CountAsync().ConfigureAwait(false)).Should().Be(3);

            var mutateReview = async () => await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralTransientReviews]
                SET [ReviewerIdentity] = {"mutated"}
                WHERE [ReviewId] = {applied.Response.ReviewId};
                """).ConfigureAwait(false);
            await mutateReview.Should().ThrowAsync<SqlException>().ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task NotificationRetry_AppendsPendingAttemptWithReplayBeforeEtag()
    {
        await using var database = CreateDatabase("NotificationRetry");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            var persistence = new CentralTransientEventPersistence(database.Context);
            _ = await persistence.AppendAsync(fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var seededCurrent = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);

            var reviewId = Guid.NewGuid();
            var failedNotificationId = Guid.NewGuid();
            var failedDispatchId = Guid.NewGuid();
            await using (var transaction = await database.Context.Database.BeginTransactionAsync().ConfigureAwait(false))
            {
                var appender = new CentralTransientEventVersionAppender(database.Context);
                var appended = await appender.AppendGeneratedAsync(seededCurrent.CentralTransientEventId, previous =>
                {
                    var createdUtc = previous.VersionCreatedUtc.AddTicks(1);
                    return previous with
                    {
                        EventVersionId = Guid.NewGuid(),
                        Version = previous.Version + 1,
                        PreviousEventVersionId = previous.EventVersionId,
                        PreviousVersionCreatedUtc = previous.VersionCreatedUtc,
                        VersionCreatedUtc = createdUtc,
                        Reviews = previous.Reviews.Append(new TransientReviewV1(
                            reviewId,
                            createdUtc,
                            "reviewer-1",
                            TransientReviewDisposition.Confirmed,
                            fixture.Assessment.Assessment.AssessmentId,
                            null,
                            ["human.confirmed"],
                            null)).ToArray(),
                        Notifications = previous.Notifications.Append(new TransientNotificationV1(
                            failedNotificationId,
                            createdUtc,
                            "email",
                            TransientNotificationState.Failed,
                            fixture.Assessment.Assessment.AssessmentId,
                            "notification.dispatch-failed",
                            null)).ToArray()
                    };
                }, resetReviewState: false, CancellationToken.None).ConfigureAwait(false);
                database.Context.CentralTransientNotificationDispatches.Add(new()
                {
                    DispatchId = failedDispatchId,
                    CentralTransientEventId = appended.Current.CentralTransientEventId,
                    AssessmentId = fixture.Assessment.Assessment.AssessmentId,
                    ReviewId = reviewId,
                    InitialNotificationId = failedNotificationId,
                    LatestNotificationId = failedNotificationId,
                    Channel = "email",
                    Recipient = "retry@example.test",
                    RecipientIdentitySha256 = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes("RETRY@EXAMPLE.TEST"))),
                    State = CentralTransientNotificationDispatchState.Failed,
                    CreatedUtc = appended.Event.VersionCreatedUtc,
                    CompletedUtc = appended.Event.VersionCreatedUtc,
                    ReasonCode = "notification.dispatch-failed"
                });
                await database.Context.SaveChangesAsync().ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
            }
            database.Context.ChangeTracker.Clear();

            var current = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            var service = new CentralTransientNotificationRetryService(
                database.Context,
                new CentralTransientEventVersionAppender(database.Context),
                TimeProvider.System);
            var principal = CreateOwnerPrincipal("notification-admin", admin: true);
            var scheduled = await service.RetryAsync(
                principal,
                current.CentralTransientEventId,
                failedNotificationId,
                current.RowVersion,
                "notification-retry-1",
                CancellationToken.None).ConfigureAwait(false);

            scheduled.Status.Should().Be(CentralTransientNotificationRetryStatus.Scheduled);
            scheduled.Response!.Replayed.Should().BeFalse();
            database.Context.ChangeTracker.Clear();
            var retry = await database.Context.CentralTransientNotificationDispatches.AsNoTracking().SingleAsync(item =>
                item.DispatchId == scheduled.Response.DispatchId).ConfigureAwait(false);
            retry.State.Should().Be(CentralTransientNotificationDispatchState.Pending);
            retry.SupersedesDispatchId.Should().Be(failedDispatchId);
            retry.Recipient.Should().Be("retry@example.test");
            var pending = await database.Context.CentralTransientNotifications.AsNoTracking().SingleAsync(item =>
                item.NotificationId == scheduled.Response.NotificationId).ConfigureAwait(false);
            pending.State.Should().Be(TransientNotificationState.Pending);
            pending.SupersedesNotificationId.Should().Be(failedNotificationId);

            var replayed = await service.RetryAsync(
                principal,
                current.CentralTransientEventId,
                failedNotificationId,
                current.RowVersion,
                "notification-retry-1",
                CancellationToken.None).ConfigureAwait(false);
            replayed.Status.Should().Be(CentralTransientNotificationRetryStatus.Scheduled);
            replayed.Response.Should().Be(scheduled.Response with { Replayed = true });

            var conflict = await service.RetryAsync(
                principal,
                current.CentralTransientEventId,
                retry.LatestNotificationId,
                current.RowVersion,
                "notification-retry-1",
                CancellationToken.None).ConfigureAwait(false);
            conflict.Status.Should().Be(CentralTransientNotificationRetryStatus.IdempotencyConflict);

            var retryCurrent = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            var duplicateSuccessor = await service.RetryAsync(
                principal,
                current.CentralTransientEventId,
                failedNotificationId,
                retryCurrent.RowVersion,
                $"notification-retry-{Guid.NewGuid():N}",
                CancellationToken.None).ConfigureAwait(false);
            duplicateSuccessor.Status.Should().Be(CentralTransientNotificationRetryStatus.Ineligible);

            database.Context.ChangeTracker.Clear();
            var fenced = await database.Context.CentralTransientNotificationDispatches.SingleAsync(item =>
                item.DispatchId == retry.DispatchId).ConfigureAwait(false);
            var clock = new MutableTimeProvider(
                DateTimeOffset.UtcNow > fenced.CreatedUtc ? DateTimeOffset.UtcNow : fenced.CreatedUtc.AddTicks(1));
            fenced.State = CentralTransientNotificationDispatchState.Fenced;
            fenced.FencedUtc = clock.GetUtcNow();
            await database.Context.SaveChangesAsync().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var email = new RecordingEmailNotificationService();
            var notificationOptions = Microsoft.Extensions.Options.Options.Create(
                new CentralTransientNotificationOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(1),
                    FenceTimeout = TimeSpan.FromMinutes(1)
                });
            var processor = new CentralTransientNotificationProcessor(
                database.Context,
                new CentralTransientEventVersionAppender(database.Context),
                email,
                clock,
                notificationOptions);
            (await processor.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();
            email.SendCount.Should().Be(0);

            clock.Advance(TimeSpan.FromMinutes(2));
            (await processor.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            email.SendCount.Should().Be(0);
            database.Context.ChangeTracker.Clear();
            var recovered = await database.Context.CentralTransientNotificationDispatches.AsNoTracking().SingleAsync(
                item => item.DispatchId == retry.DispatchId).ConfigureAwait(false);
            recovered.State.Should().Be(CentralTransientNotificationDispatchState.Failed);
            recovered.ReasonCode.Should().Be("notification.ambiguous-post-fence");
            var recoveredCurrent = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            var ambiguousRetry = await service.RetryAsync(
                principal,
                recovered.CentralTransientEventId,
                recovered.LatestNotificationId,
                recoveredCurrent.RowVersion,
                $"ambiguous-retry-{Guid.NewGuid():N}",
                CancellationToken.None).ConfigureAwait(false);
            ambiguousRetry.Status.Should().Be(CentralTransientNotificationRetryStatus.Ineligible);
            var insertFenced = async () => await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralTransientNotificationDispatches]
                    ([DispatchId], [CentralTransientEventId], [AssessmentId], [ReviewId],
                     [InitialNotificationId], [LatestNotificationId], [Channel], [Recipient],
                     [RecipientIdentitySha256], [State], [CreatedUtc], [FencedUtc])
                VALUES
                    ({Guid.NewGuid()}, {current.CentralTransientEventId}, {retry.AssessmentId}, {retry.ReviewId},
                     {retry.InitialNotificationId}, {retry.LatestNotificationId}, N'email', {"invalid@example.test"},
                     {new string('F', 64)}, N'Fenced', {retry.CreatedUtc}, {clock.GetUtcNow()});
                """).ConfigureAwait(false);
            await insertFenced.Should().ThrowAsync<SqlException>().ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ValidationWorker_CannotAppendForgedHumanReview()
    {
        await using var database = CreateDatabase("ForgedReview");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            var forgedReview = new TransientReviewV1(
                Guid.NewGuid(),
                fixture.Event.VersionCreatedUtc,
                "forged-reviewer",
                TransientReviewDisposition.Confirmed,
                fixture.Assessment.Assessment.AssessmentId,
                null,
                ["human.forged"],
                null);
            var forgedEvent = fixture.Event with { Reviews = [forgedReview] };
            var forgedBytes = TransientContractJson.Serialize(forgedEvent);

            var exception = await CaptureAppendAsync(
                new CentralTransientEventPersistence(database.Context),
                fixture.Request with
                {
                    CentralDerivativeJobId = seeded.JobId,
                    Events = [CentralTransientPersistenceFixture.CanonicalPayload(forgedBytes)]
                }).ConfigureAwait(false);

            exception.Should().NotBeNull();
            exception!.ReasonCode.Should().Be(CentralTransientPersistenceReasonCodes.InvalidIdentityBinding);
            (await database.Context.CentralTransientEvents.CountAsync().ConfigureAwait(false)).Should().Be(0);
            (await database.Context.CentralTransientReviews.CountAsync().ConfigureAwait(false)).Should().Be(0);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ValidationWorker_CannotAppendForgedEventDerivative()
    {
        await using var database = CreateDatabase("ForgedDerivative");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            var source = fixture.Event.Observations.Single().Source;
            var forgedDerivative = new TransientDerivativeV1(
                Guid.NewGuid(),
                fixture.Event.VersionCreatedUtc,
                TransientDerivativeKind.Reconstruction,
                new TransientArtifactReferenceV1(
                    Guid.NewGuid(),
                    FrameArtifactRole.Combined,
                    "event-reconstruction",
                    new string('A', 64),
                    new string('B', 64)),
                new string('A', 64),
                [source.EvidenceId],
                [
                    TransientDerivativeLimitation.IntraExposureTimingUnavailable,
                    TransientDerivativeLimitation.SaturatedPhotometryUnrecoverable
                ]);
            var forgedEvent = fixture.Event with { Derivatives = [forgedDerivative] };
            var forgedBytes = TransientContractJson.Serialize(forgedEvent);

            var exception = await CaptureAppendAsync(
                new CentralTransientEventPersistence(database.Context),
                fixture.Request with
                {
                    CentralDerivativeJobId = seeded.JobId,
                    Events = [CentralTransientPersistenceFixture.CanonicalPayload(forgedBytes)]
                }).ConfigureAwait(false);

            exception.Should().NotBeNull();
            exception!.ReasonCode.Should().Be(CentralTransientPersistenceReasonCodes.InvalidIdentityBinding);
            (await database.Context.CentralTransientEvents.CountAsync().ConfigureAwait(false)).Should().Be(0);
            (await database.Context.CentralTransientDerivatives.CountAsync().ConfigureAwait(false)).Should().Be(0);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task UncommittedEventDerivative_CannotAppendCanonicalVersion()
    {
        await using var database = CreateDatabase("CommittedDerivative");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            _ = await new CentralTransientEventPersistence(database.Context).AppendAsync(fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            var eventRecord = await database.Context.CentralTransientEvents.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            var sourceVersion = await database.Context.CentralTransientEventVersions.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            var observation = fixture.Event.Observations.Single();
            var sourceArtifact = seeded.Artifacts.Single(item =>
                item.ArtifactId == observation.Source.Locator.Artifact.ArtifactId);
            var now = sourceVersion.VersionCreatedUtc.AddSeconds(1);
            var bundleJob = new CentralDerivativeJob
            {
                SourceCentralArtifactId = sourceArtifact.Id,
                TargetRole = FrameArtifactRole.Combined,
                TargetRecipeVersion = "event-reconstruction-v1",
                TargetVariant = "event-reconstruction",
                RecipeName = "central-transient-derivative-bundle",
                RecipeOptionsJson = "{}",
                InputSelectorJson = "{}",
                RequestedRecipeIdentitySha256 = new string('C', 64),
                ExpectedRecipeIdentitySha256 = new string('C', 64),
                RequestIdentitySha256 = new string('D', 64),
                Status = CentralDerivativeJobStatus.Completed,
                MaxAttempts = 3,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CompletedAtUtc = now
            };
            var derivativeJob = new CentralTransientDerivativeJob
            {
                CentralDerivativeJobId = bundleJob.Id,
                Job = bundleJob,
                CentralTransientEventId = eventRecord.Id,
                SourceEventVersionId = sourceVersion.EventVersionId,
                RequestIdentitySha256 = bundleJob.RequestIdentitySha256,
                ProducerSchemaVersion = "transient-derivative-producer-v1",
                ProducerName = "hvo.linear16-transient-reconstruction",
                ProducerVersion = Linear16TransientReconstruction.AlgorithmVersion,
                RecipeIdentitySha256 = bundleJob.RequestedRecipeIdentitySha256,
                OptionsIdentitySha256 = new string('E', 64),
                CanonicalRequestJson = "{}",
                CanonicalRequestSha256 = ProcessingIdentity.ComputePayloadSha256(Encoding.UTF8.GetBytes("{}")),
                CanonicalRequestByteLength = 2,
                ExpectedOutputCount = 5,
                CreatedAtUtc = now,
                CommittedAtUtc = null
            };
            var derivativeId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var outputIdentity = new string('F', 64);
            var checksum = new string('A', 64);
            var intent = new CentralTransientDerivativeOutputIntent
            {
                CentralDerivativeJobId = bundleJob.Id,
                DerivativeJob = derivativeJob,
                CentralTransientEventId = eventRecord.Id,
                Kind = TransientDerivativeKind.Reconstruction,
                DerivativeId = derivativeId,
                ArtifactId = artifactId,
                ArtifactRole = FrameArtifactRole.Combined,
                ArtifactVariant = "event-reconstruction",
                MediaType = "application/x-hvo-frame",
                ByteLength = 2,
                ChecksumSha256 = checksum,
                OutputIdentitySha256 = outputIdentity,
                StorageReference = $"object://skymonitor-artifacts/events/{outputIdentity}.bin",
                StorageETag = "fixture-etag",
                ObjectState = CentralArtifactObjectState.Available,
                CreatedAtUtc = now,
                ObjectVerifiedAtUtc = now,
                CommittedAtUtc = null
            };
            derivativeJob.OutputIntents.Add(intent);
            var limitations = new[]
            {
                TransientDerivativeLimitation.IntraExposureTimingUnavailable,
                TransientDerivativeLimitation.SaturatedPhotometryUnrecoverable
            };
            var derivative = new CentralTransientDerivativeRecord
            {
                DerivativeId = derivativeId,
                CentralTransientEventId = eventRecord.Id,
                SourceEventVersionId = sourceVersion.EventVersionId,
                CentralDerivativeJobId = bundleJob.Id,
                DerivativeJob = derivativeJob,
                OutputIntentId = intent.Id,
                OutputIntent = intent,
                CreatedUtc = now,
                Kind = intent.Kind,
                ArtifactId = artifactId,
                ArtifactRole = intent.ArtifactRole,
                ArtifactVariant = intent.ArtifactVariant,
                MediaType = intent.MediaType,
                ByteLength = intent.ByteLength,
                ArtifactChecksumSha256 = checksum,
                RecipeIdentitySha256 = derivativeJob.RecipeIdentitySha256,
                OptionsIdentitySha256 = derivativeJob.OptionsIdentitySha256,
                OutputIdentitySha256 = outputIdentity,
                LimitationsJson = CaptureContractJson.Canonicalize(
                    CaptureContractJson.SerializeToElement(limitations)).GetRawText(),
                AssessmentId = fixture.Assessment.Assessment.AssessmentId
            };
            derivative.Sources.Add(new CentralTransientDerivativeSourceReference
            {
                DerivativeId = derivativeId,
                CentralTransientEventId = eventRecord.Id,
                Ordinal = 0,
                ObservationId = observation.ObservationId,
                EvidenceId = observation.Source.EvidenceId,
                CentralArtifactId = sourceArtifact.Id,
                ArtifactId = sourceArtifact.ArtifactId,
                ArtifactChecksumSha256 = sourceArtifact.ChecksumSha256,
                ObservationStartedUtc = observation.Source.ObservationStartedUtc,
                ObservationEndedUtc = observation.Source.ObservationEndedUtc
            });
            for (var ordinal = 0; ordinal < observation.BackgroundArtifacts.Count; ordinal++)
            {
                var background = observation.BackgroundArtifacts[ordinal];
                var centralBackground = seeded.Artifacts.Single(item => item.ArtifactId == background.ArtifactId);
                derivative.Backgrounds.Add(new CentralTransientDerivativeBackgroundReference
                {
                    DerivativeId = derivativeId,
                    CentralTransientEventId = eventRecord.Id,
                    ObservationOrdinal = 0,
                    BackgroundOrdinal = ordinal,
                    ObservationId = observation.ObservationId,
                    CentralArtifactId = centralBackground.Id,
                    ArtifactId = background.ArtifactId,
                    ArtifactChecksumSha256 = background.ChecksumSha256
                });
            }
            database.Context.CentralDerivativeJobs.Add(bundleJob);
            database.Context.CentralTransientDerivativeJobs.Add(derivativeJob);
            database.Context.CentralTransientDerivatives.Add(derivative);
            await database.Context.SaveChangesAsync().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            await using var transaction = await database.Context.Database.BeginTransactionAsync().ConfigureAwait(false);
            var appender = new CentralTransientEventVersionAppender(database.Context);
            var append = async () => await appender.AppendGeneratedAsync(eventRecord.Id, previous => previous with
            {
                EventVersionId = Guid.NewGuid(),
                Version = previous.Version + 1,
                PreviousEventVersionId = previous.EventVersionId,
                PreviousVersionCreatedUtc = previous.VersionCreatedUtc,
                VersionCreatedUtc = now,
                Derivatives = previous.Derivatives.Append(new TransientDerivativeV1(
                        derivativeId,
                        now,
                        TransientDerivativeKind.Reconstruction,
                        new TransientArtifactReferenceV1(
                            artifactId,
                            intent.ArtifactRole,
                            intent.ArtifactVariant,
                            derivativeJob.RecipeIdentitySha256,
                            checksum),
                        derivativeJob.RecipeIdentitySha256,
                        [observation.Source.EvidenceId],
                        limitations)).ToArray()
            }, resetReviewState: false, CancellationToken.None).ConfigureAwait(false);
            await append.Should().ThrowAsync<CentralTransientPersistenceException>().ConfigureAwait(false);
            await transaction.RollbackAsync().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            (await database.Context.Set<CentralTransientEventVersionDerivative>().CountAsync().ConfigureAwait(false))
                .Should().Be(0);
            var persistedIntent = await database.Context.CentralTransientDerivativeOutputIntents.SingleAsync()
                .ConfigureAwait(false);
            persistedIntent.CommittedAtUtc = now;
            await database.Context.SaveChangesAsync().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var persistedJob = await database.Context.CentralTransientDerivativeJobs.SingleAsync()
                .ConfigureAwait(false);
            persistedJob.CommittedAtUtc = now;
            var incompleteCommit = async () => await database.Context.SaveChangesAsync().ConfigureAwait(false);
            await incompleteCommit.Should().ThrowAsync<DbUpdateException>().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var mutate = async () => await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralTransientDerivatives]
                SET [ArtifactChecksumSha256] = {new string('0', 64)}
                WHERE [DerivativeId] = {derivativeId};
                """).ConfigureAwait(false);
            await mutate.Should().ThrowAsync<SqlException>().ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task EventDerivativeScheduler_FreezesExactInputsAndConvergesRetries()
    {
        await using var database = CreateDatabase("DerivativeSchedule");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            var scheduler = new CentralTransientDerivativeScheduler(database.Context);
            var persistence = new CentralTransientEventPersistence(
                database.Context,
                new CentralTransientEventVersionAppender(database.Context),
                scheduler);
            _ = await persistence.AppendAsync(fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var eventVersionId = await database.Context.CentralTransientEventVersions.AsNoTracking()
                .Select(item => item.EventVersionId).SingleAsync().ConfigureAwait(false);

            await scheduler.EnsureScheduledAsync([eventVersionId], CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            var derivativeJob = await database.Context.CentralTransientDerivativeJobs.AsNoTracking()
                .Include(item => item.Job)!.ThenInclude(item => item!.Inputs)
                .Include(item => item.Job)!.ThenInclude(item => item!.CanonicalInputs)
                .SingleAsync().ConfigureAwait(false);
            derivativeJob.SourceEventVersionId.Should().Be(eventVersionId);
            derivativeJob.Job!.Status.Should().Be(CentralDerivativeJobStatus.Pending);
            derivativeJob.Job.AvailableAtUtc.Should().Be(derivativeJob.CreatedAtUtc);
            derivativeJob.Job.RecipeName.Should().Be(CentralTransientDerivativeRuntime.RecipeName);
            derivativeJob.Job.Inputs.Should().HaveCount(fixture.Extraction.OrderedSources.Count);
            derivativeJob.Job.CanonicalInputs.Should().ContainSingle(item =>
                item.SchemaVersion == CentralTransientDerivativeRuntime.RequestSchemaVersion &&
                item.IdentitySha256 == derivativeJob.CanonicalRequestSha256);
            derivativeJob.CanonicalRequestJson.Should().NotContain("object://");
            derivativeJob.CanonicalRequestJson.Should().NotContain("StorageReference");
            derivativeJob.CanonicalRequestJson.Should()
                .Contain(fixture.Event.Observations.Single().Provenance.MaskIdentity);
            derivativeJob.CanonicalRequestJson.Should()
                .Contain(fixture.Extraction.ExtractionIdentitySha256);
            using (var telemetry = new CentralDerivativeWorkerTelemetry())
            {
                var resolver = new CentralDerivativeWindowResolver(
                    database.Context,
                    telemetry,
                    TimeProvider.System,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<CentralDerivativeWindowResolver>.Instance);
                await resolver.ResolveWaitingAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            }
            database.Context.ChangeTracker.Clear();
            (await database.Context.CentralDerivativeJobs.AsNoTracking().SingleAsync(item =>
                item.RecipeName == CentralTransientDerivativeRuntime.RecipeName).ConfigureAwait(false)).Status
                .Should().Be(CentralDerivativeJobStatus.Pending);
            (await database.Context.CentralTransientDerivativeJobs.CountAsync().ConfigureAwait(false)).Should().Be(1);

            await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                DISABLE TRIGGER [TR_CentralTransientDerivativeJobs_CommittedImmutable]
                    ON [CentralTransientDerivativeJobs];
                UPDATE [CentralTransientDerivativeJobs]
                SET [CommittedAtUtc] = {DateTimeOffset.UtcNow}
                WHERE [CentralDerivativeJobId] = {derivativeJob.CentralDerivativeJobId};
                ENABLE TRIGGER [TR_CentralTransientDerivativeJobs_CommittedImmutable]
                    ON [CentralTransientDerivativeJobs];
                """).ConfigureAwait(false);
            var insertAfterCommit = async () => await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralTransientDerivativeOutputIntents]
                    ([Id], [CentralDerivativeJobId], [CentralTransientEventId], [Kind], [DerivativeId],
                     [ArtifactId], [ArtifactRole], [ArtifactVariant], [MediaType], [ByteLength],
                     [ChecksumSha256], [OutputIdentitySha256], [StorageReference], [ObjectState],
                     [CreatedAtUtc])
                VALUES
                    ({Guid.NewGuid()}, {derivativeJob.CentralDerivativeJobId}, {derivativeJob.CentralTransientEventId},
                     N'Reconstruction', {Guid.NewGuid()}, {Guid.NewGuid()}, N'Combined', N'event-reconstruction',
                     N'application/x-hvo-frame', 2, {new string('A', 64)}, {new string('B', 64)},
                     N'object://skymonitor-artifacts/events/closed.bin', N'Pending', {DateTimeOffset.UtcNow});
                """).ConfigureAwait(false);
            await insertAfterCommit.Should().ThrowAsync<SqlException>().ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ConcurrentEventDerivativeScheduling_ConvergesToOneFrozenJob()
    {
        await using var database = CreateDatabase("DerivativeScheduleConcurrent");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            _ = await new CentralTransientEventPersistence(database.Context).AppendAsync(fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var eventVersionId = await database.Context.CentralTransientEventVersions.AsNoTracking()
                .Select(item => item.EventVersionId).SingleAsync().ConfigureAwait(false);

            await using var firstContext = CreateContext(database.ConnectionString);
            await using var secondContext = CreateContext(database.ConnectionString);
            await Task.WhenAll(
                new CentralTransientDerivativeScheduler(firstContext)
                    .EnsureScheduledAsync([eventVersionId], CancellationToken.None),
                new CentralTransientDerivativeScheduler(secondContext)
                    .EnsureScheduledAsync([eventVersionId], CancellationToken.None)).ConfigureAwait(false);

            database.Context.ChangeTracker.Clear();
            (await database.Context.CentralTransientDerivativeJobs.CountAsync().ConfigureAwait(false)).Should().Be(1);
            (await database.Context.CentralDerivativeJobs.CountAsync(item =>
                item.RecipeName == CentralTransientDerivativeRuntime.RecipeName).ConfigureAwait(false)).Should().Be(1);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task EventDerivativeOutputIntent_CannotCrossEventOwnership()
    {
        await using var database = CreateDatabase("DerivativeIntentOwnership");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.CreateMultiCandidate();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            _ = await new CentralTransientEventPersistence(database.Context).AppendAsync(fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var eventVersions = await database.Context.CentralTransientEventVersions.AsNoTracking()
                .OrderBy(item => item.CentralTransientEventId)
                .Select(item => new { item.CentralTransientEventId, item.EventVersionId })
                .ToArrayAsync().ConfigureAwait(false);
            await new CentralTransientDerivativeScheduler(database.Context)
                .EnsureScheduledAsync(eventVersions.Select(item => item.EventVersionId).ToArray(), CancellationToken.None)
                .ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            var firstJob = await database.Context.CentralTransientDerivativeJobs.AsNoTracking()
                .OrderBy(item => item.CentralTransientEventId).FirstAsync().ConfigureAwait(false);
            var otherEventId = eventVersions.Single(item =>
                item.CentralTransientEventId != firstJob.CentralTransientEventId).CentralTransientEventId;
            database.Context.CentralTransientDerivativeOutputIntents.Add(new CentralTransientDerivativeOutputIntent
            {
                CentralDerivativeJobId = firstJob.CentralDerivativeJobId,
                CentralTransientEventId = otherEventId,
                Kind = TransientDerivativeKind.Reconstruction,
                DerivativeId = Guid.NewGuid(),
                ArtifactId = Guid.NewGuid(),
                ArtifactRole = FrameArtifactRole.Combined,
                ArtifactVariant = "event-reconstruction",
                MediaType = "application/x-hvo-frame",
                ByteLength = 2,
                ChecksumSha256 = new string('A', 64),
                OutputIdentitySha256 = new string('B', 64),
                StorageReference = "object://skymonitor-artifacts/events/cross-owner.bin",
                ObjectState = CentralArtifactObjectState.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });

            var save = async () => await database.Context.SaveChangesAsync().ConfigureAwait(false);
            await save.Should().ThrowAsync<DbUpdateException>().ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task EventReprocessing_AppendsAssessmentResetsReviewAndReplaysBeforeEtag()
    {
        await using var database = CreateDatabase("EventReprocessing");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            _ = await new CentralTransientEventPersistence(database.Context).AppendAsync(fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var current = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            var principal = CreateOwnerPrincipal("reprocessing-admin", admin: true);
            var options = fixture.AssessmentOptions with { FireballMinimumIntegratedSignalAdu = 1 };
            var request = new CentralTransientReprocessingRequest(options);
            var idempotencyKey = $"reprocess-{Guid.NewGuid():N}";
            var service = new CentralTransientReprocessingService(database.Context, TimeProvider.System);

            var scheduled = await service.ScheduleAsync(
                principal,
                current.CentralTransientEventId,
                current.RowVersion,
                idempotencyKey,
                request,
                CancellationToken.None).ConfigureAwait(false);
            scheduled.Status.Should().Be(CentralTransientReprocessingStatus.Scheduled);
            scheduled.Response!.Replayed.Should().BeFalse();
            database.Context.ChangeTracker.Clear();
            var equivalentPrincipal = CreateOwnerPrincipal("other-reprocessing-admin", admin: true);
            var equivalentKey = $"equivalent-{Guid.NewGuid():N}";
            var converged = await service.ScheduleAsync(
                equivalentPrincipal,
                current.CentralTransientEventId,
                current.RowVersion,
                equivalentKey,
                request,
                CancellationToken.None).ConfigureAwait(false);
            converged.Status.Should().Be(CentralTransientReprocessingStatus.Scheduled);
            converged.Response!.Replayed.Should().BeTrue();
            converged.Response.JobId.Should().Be(scheduled.Response.JobId);
            (await database.Context.CentralTransientReprocessingJobs.CountAsync().ConfigureAwait(false)).Should().Be(1);
            (await database.Context.CentralTransientReprocessingRequests.CountAsync().ConfigureAwait(false)).Should().Be(2);
            var overlapping = await service.ScheduleAsync(
                principal,
                current.CentralTransientEventId,
                current.RowVersion,
                $"overlap-{Guid.NewGuid():N}",
                request with { Options = options with { FireballMinimumIntegratedSignalAdu = 2 } },
                CancellationToken.None).ConfigureAwait(false);
            overlapping.Status.Should().Be(CentralTransientReprocessingStatus.Ineligible);
            database.Context.ChangeTracker.Clear();
            var lease = await new CentralDerivativeJobService(database.Context, TimeProvider.System)
                .ClaimNextAsync("reprocessing-worker", TimeSpan.FromMinutes(2), CancellationToken.None)
                .ConfigureAwait(false);
            lease.Should().NotBeNull();
            lease!.RecipeName.Should().Be(CentralTransientReprocessingRuntime.RecipeName);
            database.Context.ChangeTracker.Clear();
            var objectReader = new RecordingArtifactObjectReader();
            var scheduler = new ThrowOnceTransientDerivativeScheduler(
                new CentralTransientDerivativeScheduler(database.Context));
            var executor = new CentralTransientReprocessingExecutor(
                database.Context,
                new CentralTransientEventVersionAppender(database.Context),
                scheduler,
                new CentralDerivativeJobService(database.Context, TimeProvider.System),
                objectReader,
                TimeProvider.System);
            var staleLease = async () => await executor.ExecuteAsync(
                lease with { LeaseToken = Guid.NewGuid() }, CancellationToken.None).ConfigureAwait(false);
            await staleLease.Should().ThrowAsync<CentralDerivativeJobStateException>().ConfigureAwait(false);
            (await database.Context.CentralTransientAssessments.CountAsync().ConfigureAwait(false)).Should().Be(1);
            objectReader.VerifiedArtifactIds.Clear();
            var interrupted = async () => await executor.ExecuteAsync(lease, CancellationToken.None)
                .ConfigureAwait(false);
            await interrupted.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var result = await executor.ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            result.Status.Should().Be(ProcessingOutcomeStatus.Produced);
            result.ReasonCode.Should().Be("transient-reprocessing.output-adopted");
            objectReader.VerifiedArtifactIds.Should().BeEquivalentTo(seeded.Artifacts.Select(item => item.Id));
            database.Context.ChangeTracker.Clear();

            (await database.Context.CentralTransientAssessments.CountAsync().ConfigureAwait(false)).Should().Be(2);
            (await database.Context.CentralTransientEventVersions.CountAsync().ConfigureAwait(false)).Should().Be(2);
            var updated = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            updated.ActiveAssessmentId.Should().NotBe(current.ActiveAssessmentId);
            updated.ReviewState.Should().Be(CentralTransientReviewState.NeedsReview);
            (await database.Context.CentralTransientDerivativeJobs.CountAsync(item =>
                item.SourceEventVersionId == updated.LatestEventVersionId).ConfigureAwait(false)).Should().Be(1);

            var replay = await service.ScheduleAsync(
                principal,
                current.CentralTransientEventId,
                current.RowVersion,
                idempotencyKey,
                request,
                CancellationToken.None).ConfigureAwait(false);
            replay.Status.Should().Be(CentralTransientReprocessingStatus.Scheduled);
            replay.Response!.Replayed.Should().BeTrue();
            replay.Response.JobId.Should().Be(scheduled.Response.JobId);
            var convergedReplay = await service.ScheduleAsync(
                equivalentPrincipal,
                current.CentralTransientEventId,
                current.RowVersion,
                equivalentKey,
                request,
                CancellationToken.None).ConfigureAwait(false);
            convergedReplay.Status.Should().Be(CentralTransientReprocessingStatus.Scheduled);
            convergedReplay.Response!.JobId.Should().Be(scheduled.Response.JobId);
            convergedReplay.Response.Replayed.Should().BeTrue();
            var conflict = await service.ScheduleAsync(
                principal,
                current.CentralTransientEventId,
                current.RowVersion,
                idempotencyKey,
                request with { Options = options with { FireballMinimumIntegratedSignalAdu = 2 } },
                CancellationToken.None).ConfigureAwait(false);
            conflict.Status.Should().Be(CentralTransientReprocessingStatus.IdempotencyConflict);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task EventPayloadRelease_IsOptInTerminalAndIdempotent()
    {
        await using var database = CreateDatabase("EventPayloadRelease");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            _ = await new CentralTransientEventPersistence(database.Context).AppendAsync(fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            await database.Context.CentralDerivativeJobs.Where(item => item.Id == seeded.JobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, CentralDerivativeJobStatus.Completed)
                    .SetProperty(item => item.CompletedAtUtc, DateTimeOffset.UtcNow)
                    .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var current = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            var principal = CreateOwnerPrincipal("release-admin", admin: true);
            var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IObjectStore>();
            var disabled = new CentralTransientPayloadReleaseService(
                database.Context,
                new CentralArtifactRetentionReferences(database.Context),
                minio,
                Microsoft.Extensions.Options.Options.Create(new CentralTransientPayloadReleaseOptions()),
                TimeProvider.System);
            (await disabled.ReleaseAsync(
                principal, current.CentralTransientEventId, current.RowVersion, "disabled", CancellationToken.None)
                .ConfigureAwait(false)).Status.Should().Be(CentralTransientPayloadReleaseStatus.Disabled);

            var reviewed = await new CentralTransientReviewService(
                    database.Context,
                    new CentralTransientEventVersionAppender(database.Context),
                    TimeProvider.System)
                .ReviewAsync(
                    principal,
                    current.CentralTransientEventId,
                    current.RowVersion,
                    $"release-review-{Guid.NewGuid():N}",
                    new CentralTransientReviewRequest(
                        current.ActiveAssessmentId,
                        TransientReviewDisposition.Confirmed,
                        null,
                        ["human.release-approved"]),
                    CancellationToken.None).ConfigureAwait(false);
            reviewed.Status.Should().Be(CentralTransientReviewMutationStatus.Applied);
            var sharedArtifactId = await database.Context.CentralTransientObservationSources.AsNoTracking()
                .Select(item => item.CentralArtifactId).FirstAsync().ConfigureAwait(false);
            var sharedEventId = Guid.NewGuid();
            var sharedObservationId = Guid.NewGuid();
            await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralTransientEvents] ([Id], [AgentId], [EventId], [EventCreatedUtc])
                VALUES ({sharedEventId}, {"shared-evidence-agent"}, {Guid.NewGuid()}, {DateTimeOffset.UtcNow});

                INSERT INTO [CentralTransientObservationSources]
                    ([ObservationId], [CentralArtifactId], [EvidenceSchemaVersion], [EvidenceId],
                     [LocatorSchemaVersion], [LocatorKind], [ArtifactId], [ArtifactRole], [ArtifactVariant],
                     [ArtifactRecipeIdentitySha256], [ArtifactChecksumSha256], [ObservationStartedUtc],
                     [ObservationEndedUtc], [TimingQuality], [TimingProvenanceSource], [TimingProvenanceVersion])
                SELECT TOP(1) {sharedObservationId}, [CentralArtifactId], [EvidenceSchemaVersion], [EvidenceId],
                     [LocatorSchemaVersion], [LocatorKind], [ArtifactId], [ArtifactRole], [ArtifactVariant],
                     [ArtifactRecipeIdentitySha256], [ArtifactChecksumSha256], [ObservationStartedUtc],
                     [ObservationEndedUtc], [TimingQuality], [TimingProvenanceSource], [TimingProvenanceVersion]
                FROM [CentralTransientObservationSources]
                WHERE [CentralArtifactId] = {sharedArtifactId};

                INSERT INTO [CentralTransientObservations]
                    ([ObservationId], [CentralTransientEventId], [DetectorInputIdentitySha256],
                     [CalibrationIdentity], [MaskIdentity], [ProcessingProfileIdentity], [OriginatingCandidateId],
                     [ExtractionProducerSchemaVersion], [ExtractionProducerKind], [ExtractionProducerName],
                     [ExtractionProducerVersion], [ExtractionRecipeIdentitySha256], [ExtractionReceiptIdentitySha256],
                     [GeometryJson], [FeaturesJson], [SourceReferenceId])
                SELECT TOP(1) {sharedObservationId}, {sharedEventId}, [DetectorInputIdentitySha256],
                     [CalibrationIdentity], [MaskIdentity], [ProcessingProfileIdentity], NULL,
                     [ExtractionProducerSchemaVersion], [ExtractionProducerKind], [ExtractionProducerName],
                     [ExtractionProducerVersion], [ExtractionRecipeIdentitySha256], [ExtractionReceiptIdentitySha256],
                     [GeometryJson], [FeaturesJson], {sharedObservationId}
                FROM [CentralTransientObservations] AS observation
                WHERE EXISTS (
                    SELECT 1 FROM [CentralTransientObservationSources] AS source
                    WHERE source.[ObservationId] = observation.[ObservationId]
                      AND source.[CentralArtifactId] = {sharedArtifactId});
                """).ConfigureAwait(false);
            var recoveryRelease = new CentralTransientPayloadRelease
            {
                CentralTransientEventId = sharedEventId,
                ActorIdentity = "release-recovery",
                IdempotencyKey = $"release-recovery-{Guid.NewGuid():N}",
                CanonicalRequestSha256 = new string('A', 64),
                State = CentralTransientPayloadReleaseState.Pending,
                CreatedUtc = DateTimeOffset.UtcNow
            };
            recoveryRelease.Items.Add(new CentralTransientPayloadReleaseItem
            {
                ReleaseId = recoveryRelease.ReleaseId,
                Ordinal = 0,
                Kind = CentralTransientPayloadReleaseItemKind.SourceArtifact,
                RecordId = sharedArtifactId
            });
            database.Context.CentralTransientPayloadReleases.Add(recoveryRelease);
            await database.Context.SaveChangesAsync().ConfigureAwait(false);
            await database.Context.CentralDerivativeJobs.Where(item =>
                    item.RecipeName == CentralTransientDerivativeRuntime.RecipeName)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var reviewedCurrent = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            var enabled = new CentralTransientPayloadReleaseService(
                database.Context,
                new CentralArtifactRetentionReferences(database.Context),
                minio,
                Microsoft.Extensions.Options.Options.Create(new CentralTransientPayloadReleaseOptions
                {
                    Enabled = true,
                    InitialRetryDelay = TimeSpan.FromMilliseconds(1),
                    MaximumRetryDelay = TimeSpan.FromMilliseconds(10),
                    MaximumRetryCount = 5
                }),
                TimeProvider.System);
            (await enabled.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            var preservedLateHold = await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                .SingleAsync(item => item.ReleaseId == recoveryRelease.ReleaseId).ConfigureAwait(false);
            preservedLateHold.Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.PreservedHeld);
            preservedLateHold.ReleasedUtc.Should().NotBeNull();
            var key = $"release-{Guid.NewGuid():N}";
            var released = await enabled.ReleaseAsync(
                principal,
                current.CentralTransientEventId,
                reviewedCurrent.RowVersion,
                key,
                CancellationToken.None).ConfigureAwait(false);
            for (var attempt = 0;
                 attempt < 10 && released.Status == CentralTransientPayloadReleaseStatus.Accepted;
                 attempt++)
            {
                await Task.Delay(20).ConfigureAwait(false);
                _ = await enabled.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false);
                released = await enabled.ReleaseAsync(
                    principal,
                    current.CentralTransientEventId,
                    reviewedCurrent.RowVersion,
                    key,
                    CancellationToken.None).ConfigureAwait(false);
            }
            var failureReasons = released.Status == CentralTransientPayloadReleaseStatus.Failed
                ? await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                    .Where(item => item.ReleaseId == released.Response!.ReleaseId && item.FailureReasonCode != null)
                    .Select(item => item.FailureReasonCode!).ToArrayAsync().ConfigureAwait(false)
                : [];
            released.Status.Should().Be(
                CentralTransientPayloadReleaseStatus.Released,
                string.Join(',', failureReasons));
            released.Response!.State.Should().Be(CentralTransientPayloadReleaseState.Completed);
            released.Response.ETag.Should().NotBe(reviewed.Response!.ETag);
            released.Response.ReleasedPayloadCount.Should().Be(seeded.Artifacts.Count - 1);
            var durableItems = await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                .Where(item => item.ReleaseId == released.Response.ReleaseId)
                .OrderBy(item => item.Ordinal).ToArrayAsync().ConfigureAwait(false);
            durableItems.Should().HaveCount(seeded.Artifacts.Count);
            durableItems.Select(item => item.RecordId).Should()
                .Equal(seeded.Artifacts.Select(item => item.Id).Order());
            durableItems.Should().OnlyContain(item =>
                item.Outcome != CentralTransientPayloadReleaseItemOutcome.Pending &&
                item.ReleasedUtc.HasValue &&
                item.ReservationToken == null &&
                item.RetryAtUtc == null &&
                item.StorageReference != null &&
                item.TargetRowVersion != null && item.TargetRowVersion.Length == 8 &&
                item.TargetGeneration.HasValue && item.RowVersion.Length == 8);
            durableItems.Count(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Released)
                .Should().Be(seeded.Artifacts.Count - 1);
            durableItems.Count(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.PreservedHeld)
                .Should().Be(1);
            var terminalAudit = durableItems.Select(item => new
            {
                item.ReleaseId,
                item.Ordinal,
                item.Kind,
                item.RecordId,
                item.Outcome,
                item.ReleasedUtc,
                item.RequestedAtUtc,
                item.StorageReference,
                TargetRowVersion = Convert.ToHexString(item.TargetRowVersion!),
                item.TargetGeneration,
                item.RetryCount,
                RowVersion = Convert.ToHexString(item.RowVersion)
            }).ToArray();
            (await database.Context.CentralArtifacts.CountAsync(item =>
                item.ObjectState == CentralArtifactObjectState.Expired).ConfigureAwait(false))
                .Should().Be(seeded.Artifacts.Count - 1);
            (await database.Context.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == sharedArtifactId)
                .ConfigureAwait(false)).ObjectState.Should().Be(CentralArtifactObjectState.Available);
            (await database.Context.CentralTransientEvents.CountAsync().ConfigureAwait(false)).Should().Be(2);
            (await database.Context.CentralTransientEventVersions.CountAsync().ConfigureAwait(false)).Should().Be(2);

            var replay = await enabled.ReleaseAsync(
                principal,
                current.CentralTransientEventId,
                current.RowVersion,
                key,
                CancellationToken.None).ConfigureAwait(false);
            replay.Status.Should().Be(CentralTransientPayloadReleaseStatus.Released);
            replay.Response!.Replayed.Should().BeTrue();
            replay.Response.ReleaseId.Should().Be(released.Response.ReleaseId);
            database.Context.ChangeTracker.Clear();
            var replayItems = await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                .Where(item => item.ReleaseId == released.Response.ReleaseId)
                .OrderBy(item => item.Ordinal).ToArrayAsync().ConfigureAwait(false);
            var replayAudit = replayItems.Select(item => new
            {
                item.ReleaseId,
                item.Ordinal,
                item.Kind,
                item.RecordId,
                item.Outcome,
                item.ReleasedUtc,
                item.RequestedAtUtc,
                item.StorageReference,
                TargetRowVersion = Convert.ToHexString(item.TargetRowVersion!),
                item.TargetGeneration,
                item.RetryCount,
                RowVersion = Convert.ToHexString(item.RowVersion)
            }).ToArray();
            replayAudit.Should().BeEquivalentTo(terminalAudit, options => options.WithStrictOrdering());
            CentralTransientEventEtag.TryParse(released.Response.ETag, out var releasedRowVersion).Should().BeTrue();
            var secondRelease = await enabled.ReleaseAsync(
                principal,
                current.CentralTransientEventId,
                releasedRowVersion,
                $"second-release-{Guid.NewGuid():N}",
                CancellationToken.None).ConfigureAwait(false);
            secondRelease.Status.Should().Be(CentralTransientPayloadReleaseStatus.Ineligible);
            var blockedReprocessing = await new CentralTransientReprocessingService(
                    database.Context, TimeProvider.System)
                .ScheduleAsync(
                    principal,
                    current.CentralTransientEventId,
                    releasedRowVersion,
                    $"released-reprocess-{Guid.NewGuid():N}",
                    new CentralTransientReprocessingRequest(fixture.AssessmentOptions),
                    CancellationToken.None).ConfigureAwait(false);
            blockedReprocessing.Status.Should().Be(CentralTransientReprocessingStatus.Ineligible);
            var appendReleaseItem = async () => await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralTransientPayloadReleaseItems]
                    ([ReleaseId], [Ordinal], [Kind], [RecordId], [Outcome], [ReleasedUtc])
                VALUES ({released.Response.ReleaseId}, {999}, N'SourceArtifact', {Guid.NewGuid()}, N'Pending', NULL);
                """).ConfigureAwait(false);
            await appendReleaseItem.Should().ThrowAsync<SqlException>().ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadReleaseProcessor_ResumesDurablePendingRelease()
    {
        await using var database = CreateDatabase("PendingPayloadRelease");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            _ = await new CentralTransientEventPersistence(database.Context).AppendAsync(fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var current = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            var source = seeded.Artifacts[0];
            var release = new CentralTransientPayloadRelease
            {
                CentralTransientEventId = current.CentralTransientEventId,
                ActorIdentity = "release-recovery-admin",
                IdempotencyKey = $"recovery-{Guid.NewGuid():N}",
                CanonicalRequestSha256 = new string('A', 64),
                State = CentralTransientPayloadReleaseState.Pending,
                CreatedUtc = DateTimeOffset.UtcNow
            };
            release.Items.Add(new CentralTransientPayloadReleaseItem
            {
                ReleaseId = release.ReleaseId,
                Ordinal = 0,
                Kind = CentralTransientPayloadReleaseItemKind.SourceArtifact,
                RecordId = source.Id
            });
            database.Context.CentralTransientPayloadReleases.Add(release);
            await database.Context.SaveChangesAsync().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IObjectStore>();

            var processor = new CentralTransientPayloadReleaseService(
                database.Context,
                new CentralArtifactRetentionReferences(database.Context),
                minio,
                Microsoft.Extensions.Options.Options.Create(
                    new CentralTransientPayloadReleaseOptions { Enabled = true }),
                TimeProvider.System);
            (await processor.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            database.Context.ChangeTracker.Clear();

            var completed = await database.Context.CentralTransientPayloadReleases.AsNoTracking()
                .Include(item => item.Items).SingleAsync(item => item.ReleaseId == release.ReleaseId)
                .ConfigureAwait(false);
            completed.State.Should().Be(
                CentralTransientPayloadReleaseState.Completed,
                "normalized items were {0}",
                string.Join(", ", completed.Items.OrderBy(item => item.Ordinal).Select(item =>
                    $"{item.Ordinal}:{item.Kind}:{item.Outcome}:retry={item.RetryCount}:failure={item.FailureReasonCode}")));
            completed.Items.Should().HaveCount(5);
            completed.Items.Should().OnlyContain(item => item.ReleasedUtc.HasValue);
            (await database.Context.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == source.Id)
                .ConfigureAwait(false)).ObjectState.Should().Be(CentralArtifactObjectState.Expired);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ConcurrentReviews_FromOnePredecessorCommitExactlyOneSuccessor()
    {
        await using var database = CreateDatabase("ConcurrentReview");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            _ = await new CentralTransientEventPersistence(database.Context).AppendAsync(fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var current = await database.Context.CentralTransientEventCurrent.AsNoTracking().SingleAsync()
                .ConfigureAwait(false);
            var principal = CreateOwnerPrincipal("reviewer-concurrent", admin: true);
            var request = new CentralTransientReviewRequest(
                current.ActiveAssessmentId,
                TransientReviewDisposition.Confirmed,
                null,
                ["human.confirmed"]);

            await using var firstContext = CreateContext(database.ConnectionString);
            await using var secondContext = CreateContext(database.ConnectionString);
            var firstService = new CentralTransientReviewService(
                firstContext, new CentralTransientEventVersionAppender(firstContext), TimeProvider.System);
            var secondService = new CentralTransientReviewService(
                secondContext, new CentralTransientEventVersionAppender(secondContext), TimeProvider.System);
            var results = await Task.WhenAll(
                firstService.ReviewAsync(principal, current.CentralTransientEventId, current.RowVersion,
                    "concurrent-1", request, CancellationToken.None),
                secondService.ReviewAsync(principal, current.CentralTransientEventId, current.RowVersion,
                    "concurrent-2", request, CancellationToken.None)).ConfigureAwait(false);

            results.Select(item => item.Status).Should().BeEquivalentTo([
                CentralTransientReviewMutationStatus.Applied,
                CentralTransientReviewMutationStatus.PreconditionFailed
            ]);
            database.Context.ChangeTracker.Clear();
            (await database.Context.CentralTransientReviews.CountAsync().ConfigureAwait(false)).Should().Be(1);
            (await database.Context.CentralTransientEventVersions.CountAsync().ConfigureAwait(false)).Should().Be(2);
            (await database.Context.CentralTransientReviewMutations.CountAsync().ConfigureAwait(false)).Should().Be(1);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task MixedOwnerEvidence_IsHiddenFromEveryIndividualOwner()
    {
        await using var database = CreateDatabase("MixedOwner");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var firstObservatory = new Observatory
            {
                OwnerUserId = "owner-1",
                Name = "Owner one",
                TimeZoneId = "UTC",
                CreatedAtUtc = now,
                IsActive = true
            };
            var secondObservatory = new Observatory
            {
                OwnerUserId = "owner-2",
                Name = "Owner two",
                TimeZoneId = "UTC",
                CreatedAtUtc = now,
                IsActive = true
            };
            var firstRegistration = CreateRegistration(firstObservatory, "owner-1", now);
            var secondRegistration = CreateRegistration(secondObservatory, "owner-2", now);
            database.Context.DeviceRegistrations.AddRange(firstRegistration, secondRegistration);
            await database.Context.SaveChangesAsync().ConfigureAwait(false);

            var fixture = CentralTransientPersistenceFixture.Create();
            var registrationIds = Enumerable.Range(0, fixture.Extraction.OrderedSources.Count)
                .Select(index => index % 2 == 0 ? firstRegistration.Id : secondRegistration.Id)
                .ToArray();
            var seeded = await SeedAsync(database.Context, fixture, registrationIds: registrationIds)
                .ConfigureAwait(false);
            _ = await new CentralTransientEventPersistence(database.Context).AppendAsync(fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var eventId = await database.Context.CentralTransientEventCurrent.AsNoTracking()
                .Select(item => item.CentralTransientEventId).SingleAsync().ConfigureAwait(false);
            var readService = new CentralTransientEventReadService(database.Context);

            (await readService.GetAsync(CreateOwnerPrincipal("owner-1"), eventId, CancellationToken.None)
                .ConfigureAwait(false)).Should().BeNull();
            (await readService.GetAsync(CreateOwnerPrincipal("owner-2"), eventId, CancellationToken.None)
                .ConfigureAwait(false)).Should().BeNull();
            (await readService.GetAsync(CreateOwnerPrincipal("admin", admin: true), eventId, CancellationToken.None)
                .ConfigureAwait(false)).Should().NotBeNull();
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CenteredSuccessorNoCandidate_AppendsRejectedProvisionalEventVersion()
    {
        await using var database = CreateDatabase("CenteredNoCandidate");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var provisional = CentralTransientPersistenceFixture.Create();
            var provisionalSeed = await SeedAsync(database.Context, provisional).ConfigureAwait(false);
            var service = new CentralTransientEventPersistence(database.Context);
            _ = await service.AppendAsync(provisional.Request with
            {
                CentralDerivativeJobId = provisionalSeed.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            var centeredNoCandidate = CentralTransientPersistenceFixture.CreateNoCandidate();
            var successorSeed = await SeedAsync(
                database.Context,
                centeredNoCandidate,
                provisionalJobId: provisionalSeed.JobId).ConfigureAwait(false);
            var commit = await service.AppendAsync(centeredNoCandidate.Request with
            {
                CentralDerivativeJobId = successorSeed.JobId
            }, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            var versions = await database.Context.CentralTransientEventVersions.AsNoTracking()
                .OrderBy(item => item.Version)
                .ToListAsync().ConfigureAwait(false);
            versions.Select(item => item.Version).Should().Equal(1, 2);
            versions[^1].State.Should().Be(TransientEventState.Rejected);
            commit.EventVersionIds.Should().Equal(versions[^1].EventVersionId);
            var rejected = TransientContractJson.ParseEvent(Encoding.UTF8.GetBytes(versions[^1].CanonicalEventJson));
            rejected.Validation.IsValid.Should().BeTrue();
            rejected.Value!.PreviousEventVersionId.Should().Be(versions[0].EventVersionId);
            rejected.Value.Observations.Should().BeEquivalentTo(provisional.Event.Observations);

            var successor = await database.Context.CentralTransientValidationJobs.AsNoTracking()
                .Include(item => item.ExtractionReceipt)
                .SingleAsync(item => item.CentralDerivativeJobId == successorSeed.JobId).ConfigureAwait(false);
            successor.OutcomeState.Should().Be(TransientEventState.Rejected);
            successor.OutcomeReasonCode.Should().Be(TransientCandidateExtractionReasonCodes.NoCandidate);
            successor.ExtractionReceipt.Should().NotBeNull();
            using var evidence = JsonDocument.Parse(successor.OutcomeEvidenceJson!);
            evidence.RootElement.GetProperty("extractionReceiptSha256").GetString()
                .Should().Be(successor.ExtractionReceipt!.CanonicalReceiptSha256);
            evidence.RootElement.GetProperty("events")[0].GetProperty("eventVersionId").GetGuid()
                .Should().Be(versions[^1].EventVersionId);
            evidence.RootElement.GetProperty("events")[0].GetProperty("state").GetString()
                .Should().Be(nameof(TransientEventState.Rejected));
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow("canonical-hash")]
    [DataRow("slot")]
    [DataRow("cross-agent")]
    [DataRow("artifact-role")]
    [DataRow("job-recipe")]
    [DataRow("job-options")]
    [DataRow("job-input-set")]
    [DataRow("job-canonical-input")]
    public async Task AppendRejectsUnverifiedPayloadIdentityOrArtifactLineageAtomically(string scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        await using var database = CreateDatabase($"Reject{scenario.Replace("-", "_", StringComparison.Ordinal)}");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture, scenario == "slot").ConfigureAwait(false);
            var request = fixture.Request with { CentralDerivativeJobId = seeded.JobId };
            if (scenario == "canonical-hash")
            {
                request = request with
                {
                    Events = [request.Events.Single() with { Sha256 = new string('0', 64) }]
                };
            }
            else if (scenario is "cross-agent" or "artifact-role")
            {
                var artifact = await database.Context.CentralArtifacts.Include(item => item.Frame)
                    .SingleAsync(item => item.ArtifactId == fixture.Extraction.OrderedSources[2].Source.Locator.Artifact.ArtifactId)
                    .ConfigureAwait(false);
                if (scenario == "cross-agent")
                {
                    artifact.Frame!.AgentId = "other-agent";
                }
                else
                {
                    artifact.Role = FrameArtifactRole.Preview;
                }
                await database.Context.SaveChangesAsync().ConfigureAwait(false);
            }
            else
            {
                var job = await database.Context.CentralDerivativeJobs.SingleAsync(item => item.Id == seeded.JobId)
                    .ConfigureAwait(false);
                if (scenario == "job-recipe")
                {
                    job.ExpectedRecipeIdentitySha256 = new string('0', 64);
                }
                else if (scenario == "job-options")
                {
                    job.RecipeOptionsJson = "{}";
                }
                else if (scenario == "job-input-set")
                {
                    job.InputSetIdentitySha256 = new string('0', 64);
                }
                else if (scenario == "job-canonical-input")
                {
                    var canonicalInput = await database.Context.CentralDerivativeJobCanonicalInputs
                        .SingleAsync(item => item.CentralDerivativeJobId == seeded.JobId).ConfigureAwait(false);
                    canonicalInput.IdentitySha256 = new string('0', 64);
                }
                await database.Context.SaveChangesAsync().ConfigureAwait(false);
            }
            database.Context.ChangeTracker.Clear();
            var service = new CentralTransientEventPersistence(database.Context);

            var append = async () => await service.AppendAsync(request, CancellationToken.None).ConfigureAwait(false);
            var exception = await append.Should().ThrowAsync<CentralTransientPersistenceException>().ConfigureAwait(false);
            exception.Which.ReasonCode.Should().Be(scenario switch
            {
                "canonical-hash" => CentralTransientPersistenceReasonCodes.InvalidCanonicalPayload,
                "slot" => CentralTransientPersistenceReasonCodes.InvalidIdentityBinding,
                "job-recipe" or "job-options" or "job-input-set" or "job-canonical-input" =>
                    CentralTransientPersistenceReasonCodes.JobInputConflict,
                _ => CentralTransientPersistenceReasonCodes.InvalidArtifactLineage
            });
            database.Context.ChangeTracker.Clear();
            (await database.Context.CentralTransientEvents.CountAsync().ConfigureAwait(false)).Should().Be(0);
            (await database.Context.CentralTransientExtractionReceipts.CountAsync().ConfigureAwait(false)).Should().Be(0);
            (await database.Context.CentralTransientValidationIdentitySlots.AsNoTracking()
                .CountAsync(item => item.State == CentralTransientValidationIdentitySlotState.Reserved)
                .ConfigureAwait(false)).Should().Be(2);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ConflictingCommittedReplayFailsClosed()
    {
        await using var database = CreateDatabase("ReplayConflict");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            var request = fixture.Request with { CentralDerivativeJobId = seeded.JobId };
            var service = new CentralTransientEventPersistence(database.Context);
            _ = await service.AppendAsync(request, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var conflictingEvent = fixture.Event with
            {
                State = fixture.Event.State == TransientEventState.Pending
                    ? TransientEventState.NeedsReview
                    : TransientEventState.Pending
            };
            var conflictingBytes = TransientContractJson.Serialize(conflictingEvent);
            var conflictingRequest = request with
            {
                Events = [CentralTransientPersistenceFixture.CanonicalPayload(conflictingBytes)]
            };

            var replay = async () => await service.AppendAsync(conflictingRequest, CancellationToken.None)
                .ConfigureAwait(false);
            var exception = await replay.Should().ThrowAsync<CentralTransientPersistenceException>().ConfigureAwait(false);
            exception.Which.ReasonCode.Should().Be(CentralTransientPersistenceReasonCodes.ReplayConflict);
            (await database.Context.CentralTransientEventVersions.CountAsync().ConfigureAwait(false)).Should().Be(1);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ReorderedExactReplayReturnsCommitIdsInDurableSlotOrder()
    {
        await using var database = CreateDatabase("ReorderedReplay");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.CreateMultiCandidate();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            var request = fixture.Request with { CentralDerivativeJobId = seeded.JobId };
            var service = new CentralTransientEventPersistence(database.Context);
            var committed = await service.AppendAsync(request, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var reordered = request with
            {
                Events = request.Events.Reverse().ToArray(),
                AssessmentReceipts = request.AssessmentReceipts.Reverse().ToArray()
            };

            var adopted = await service.AppendAsync(reordered, CancellationToken.None).ConfigureAwait(false);

            committed.EventVersionIds.Should().Equal(fixture.Events.Select(item => item.EventVersionId));
            committed.AssessmentIds.Should().Equal(fixture.Assessments.Select(item => item.Assessment.AssessmentId));
            adopted.Should().BeEquivalentTo(committed, options => options.WithStrictOrdering());
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow("event")]
    [DataRow("assessment")]
    public async Task DuplicateCommittedReplayIdentityFailsAsReplayConflict(string duplicate)
    {
        ArgumentNullException.ThrowIfNull(duplicate);
        await using var database = CreateDatabase($"Duplicate{duplicate}");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.CreateMultiCandidate();
            var seeded = await SeedAsync(database.Context, fixture).ConfigureAwait(false);
            var request = fixture.Request with { CentralDerivativeJobId = seeded.JobId };
            var service = new CentralTransientEventPersistence(database.Context);
            _ = await service.AppendAsync(request, CancellationToken.None).ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();
            var duplicateRequest = duplicate == "event"
                ? request with { Events = [request.Events[0], request.Events[0]] }
                : request with
                {
                    AssessmentReceipts = [request.AssessmentReceipts[0], request.AssessmentReceipts[0]]
                };

            var replay = async () => await service.AppendAsync(duplicateRequest, CancellationToken.None)
                .ConfigureAwait(false);

            var exception = await replay.Should().ThrowAsync<CentralTransientPersistenceException>().ConfigureAwait(false);
            exception.Which.ReasonCode.Should().Be(CentralTransientPersistenceReasonCodes.ReplayConflict);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ConcurrentExactReplayConvergesOnOneCommittedOutput()
    {
        await using var setup = CreateDatabase("ConcurrentReplay");
        try
        {
            await setup.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(setup.Context, fixture).ConfigureAwait(false);
            setup.Context.ChangeTracker.Clear();
            await using var firstContext = CreateContext(setup.ConnectionString);
            await using var secondContext = CreateContext(setup.ConnectionString);
            var request = fixture.Request with { CentralDerivativeJobId = seeded.JobId };

            var commits = await Task.WhenAll(
                new CentralTransientEventPersistence(firstContext).AppendAsync(request, CancellationToken.None),
                new CentralTransientEventPersistence(secondContext).AppendAsync(request, CancellationToken.None))
                .ConfigureAwait(false);

            commits[1].Should().BeEquivalentTo(commits[0], options => options.WithStrictOrdering());
            await using var verify = CreateContext(setup.ConnectionString);
            (await verify.CentralTransientEventVersions.CountAsync().ConfigureAwait(false)).Should().Be(1);
            (await verify.CentralTransientAssessments.CountAsync().ConfigureAwait(false)).Should().Be(1);
            (await verify.CentralTransientExtractionReceipts.CountAsync().ConfigureAwait(false)).Should().Be(1);
        }
        finally
        {
            await setup.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task AnotherValidSameAgentWindowIsRejectedWhenJobSelectedDifferentInputs()
    {
        await using var database = CreateDatabase("OtherWindow");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var selected = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(database.Context, selected).ConfigureAwait(false);
            var otherWindow = CentralTransientPersistenceFixture.Create(selected.Slots);
            var request = otherWindow.Request with { CentralDerivativeJobId = seeded.JobId };
            var service = new CentralTransientEventPersistence(database.Context);

            var append = async () => await service.AppendAsync(request, CancellationToken.None).ConfigureAwait(false);
            var exception = await append.Should().ThrowAsync<CentralTransientPersistenceException>().ConfigureAwait(false);
            exception.Which.ReasonCode.Should().Be(CentralTransientPersistenceReasonCodes.JobInputConflict);
            (await database.Context.CentralTransientEvents.CountAsync().ConfigureAwait(false)).Should().Be(0);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task AppendAndReleaseRaceCannotCommitEvidenceAfterArtifactRelease()
    {
        await using var setup = CreateDatabase("RetentionRace");
        try
        {
            await setup.Context.Database.MigrateAsync().ConfigureAwait(false);
            var fixture = CentralTransientPersistenceFixture.Create();
            var seeded = await SeedAsync(setup.Context, fixture).ConfigureAwait(false);
            setup.Context.ChangeTracker.Clear();
            var completedJob = await setup.Context.CentralDerivativeJobs.SingleAsync(item => item.Id == seeded.JobId)
                .ConfigureAwait(false);
            completedJob.Status = CentralDerivativeJobStatus.Completed;
            completedJob.CompletedAtUtc = fixture.Event.VersionCreatedUtc;
            completedJob.UpdatedAtUtc = fixture.Event.VersionCreatedUtc;
            await setup.Context.SaveChangesAsync().ConfigureAwait(false);
            setup.Context.ChangeTracker.Clear();
            await using var appendContext = CreateContext(setup.ConnectionString);
            await using var releaseContext = CreateContext(setup.ConnectionString);
            var appendService = new CentralTransientEventPersistence(appendContext);
            using var telemetry = new CentralArtifactRetentionTelemetry();
            var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IObjectStore>();
            var retentionReferences = new CentralArtifactRetentionReferences(releaseContext);
            var retentionProcessor = new CentralArtifactRetentionProcessor(
                releaseContext,
                retentionReferences,
                minio,
                TimeProvider.System,
                telemetry,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CentralArtifactRetentionProcessor>.Instance);
            var releaseService = new CentralArtifactRetentionService(
                releaseContext,
                retentionReferences,
                retentionProcessor,
                TimeProvider.System,
                telemetry,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CentralArtifactRetentionService>.Instance);
            var target = seeded.Artifacts.Single(item =>
                item.ArtifactId == fixture.Extraction.OrderedSources[0].Source.Locator.Artifact.ArtifactId);
            (await setup.Context.CentralDerivativeJobs.AsNoTracking()
                .Where(item => item.Id == seeded.JobId).Select(item => item.Status).SingleAsync().ConfigureAwait(false))
                .Should().Be(CentralDerivativeJobStatus.Completed);
            (await setup.Context.CentralClearReferenceDesignations.AsNoTracking()
                .CountAsync(item => item.CentralArtifactId == target.Id).ConfigureAwait(false)).Should().Be(0);
            (await setup.Context.CentralTransientExtractionSources.AsNoTracking()
                .CountAsync(item => item.CentralArtifactId == target.Id).ConfigureAwait(false)).Should().Be(0);
            (await new CentralArtifactRetentionReferences(setup.Context)
                .IsHeldAsync(target.Id, CancellationToken.None).ConfigureAwait(false)).Should().BeFalse();

            var appendTask = CaptureAppendAsync(appendService, fixture.Request with
            {
                CentralDerivativeJobId = seeded.JobId
            });
            var releaseTask = releaseService.ReleaseAsync(target.Id, CancellationToken.None);
            var appendResult = await appendTask.ConfigureAwait(false);
            var releaseResult = await releaseTask.ConfigureAwait(false);

            if (appendResult is null)
            {
                releaseResult.Should().Be(CentralArtifactRetentionResult.Held);
            }
            else
            {
                appendResult.ReasonCode.Should().Be(CentralTransientPersistenceReasonCodes.InvalidArtifactLineage);
                releaseResult.Should().Be(CentralArtifactRetentionResult.Released);
            }
            await using var verify = CreateContext(setup.ConnectionString);
            var eventCount = await verify.CentralTransientEvents.CountAsync().ConfigureAwait(false);
            var state = await verify.CentralArtifacts.Where(item => item.Id == target.Id)
                .Select(item => item.ObjectState).SingleAsync().ConfigureAwait(false);
            ((eventCount == 1 && state == CentralArtifactObjectState.Available) ||
             (eventCount == 0 && state == CentralArtifactObjectState.Expired)).Should().BeTrue();
        }
        finally
        {
            await setup.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static async Task<CentralTransientPersistenceException?> CaptureAppendAsync(
        CentralTransientEventPersistence service,
        CentralTransientPersistenceRequest request)
    {
        try
        {
            _ = await service.AppendAsync(request, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (CentralTransientPersistenceException exception)
        {
            return exception;
        }
    }

    private static DeviceRegistration CreateRegistration(
        Observatory observatory,
        string ownerId,
        DateTimeOffset now)
        => new()
        {
            DeviceId = $"mixed-owner-{Guid.NewGuid():N}",
            ObservatoryId = observatory.Id,
            Observatory = observatory,
            FriendlyName = "Mixed owner fixture",
            ObservatoryName = observatory.Name,
            ObservatoryTimeZoneId = observatory.TimeZoneId,
            OwnerUserId = ownerId,
            OwnerDisplayName = ownerId,
            OwnerConfirmationMethod = "SelfAttested",
            OwnerConfirmedAtUtc = now,
            Status = DeviceRegistrationStatus.Active,
            VerificationCodeHash = DeviceRegistrationService.ComputeSha256("ABCDE"),
            DevicePublicId = Guid.NewGuid(),
            IssuedAtUtc = now,
            ActivatedAtUtc = now
        };

    private static ClaimsPrincipal CreateOwnerPrincipal(string ownerId, bool admin = false)
        => new(new ClaimsIdentity([
            new Claim("sub", ownerId),
            new Claim("account_type", "User"),
            new Claim("scope", admin ? "api.viewer api.admin" : "api.viewer")
        ], CanonicalCredentialClaims.BearerAuthenticationType));

    internal static async Task<SeededDatabase> SeedAsync(
        ApplicationDbContext db,
        CentralTransientPersistenceFixture fixture,
        bool mismatchFirstObservation = false,
        Guid? provisionalJobId = null,
        Guid? registrationId = null,
        IReadOnlyList<Guid>? registrationIds = null)
    {
        var artifacts = new List<CentralArtifact>();
        var sourceOrdinal = 0;
        var suppliedRegistrationIds = registrationIds ?? (registrationId is { } id ? [id] : []);
        var observatoriesByRegistration = await db.DeviceRegistrations.AsNoTracking()
            .Where(item => suppliedRegistrationIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => item.ObservatoryId).ConfigureAwait(false);
        var eventCreatedUtc = fixture.Events.Count > 0
            ? fixture.Event.EventCreatedUtc
            : fixture.Extraction.OrderedSources.Max(item => item.Source.ObservationEndedUtc).AddSeconds(1);
        foreach (var source in fixture.Extraction.OrderedSources)
        {
            var reference = source.Source.Locator.Artifact;
            var sourceRegistrationId = registrationIds is not null
                ? registrationIds[sourceOrdinal]
                : registrationId ?? Guid.NewGuid();
            var frame = new CentralFrame
            {
                RegistrationId = sourceRegistrationId,
                DevicePublicId = Guid.NewGuid(),
                ObservatoryId = observatoriesByRegistration.GetValueOrDefault(sourceRegistrationId, Guid.NewGuid()),
                AgentId = fixture.AgentId,
                FrameId = Guid.NewGuid(),
                CapturedAtUtc = source.Source.ObservationStartedUtc,
                FirstReceivedAtUtc = source.Source.ObservationEndedUtc,
                CaptureSequence = 100 + sourceOrdinal
            };
            var artifact = new CentralArtifact
            {
                CentralFrameId = frame.Id,
                ArtifactId = reference.ArtifactId,
                DevicePublicId = frame.DevicePublicId,
                Role = reference.Role,
                RecipeVersion = ProcessingConformanceFixture.SourceRecipe.SemanticVersion,
                ManifestSchemaVersion = "v2",
                MediaType = "application/x-hvo-frame",
                ByteLength = fixture.Payloads[reference.ArtifactId].Length,
                ChecksumSha256 = reference.ChecksumSha256,
                StorageReference = $"object://skymonitor-artifacts/{Guid.NewGuid():N}.bin",
                ReceivedAtUtc = source.Source.ObservationEndedUtc,
                IdempotencyKey = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                Variant = reference.Variant,
                CreatedUtc = source.Source.ObservationEndedUtc,
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete,
                Recipe = new CentralArtifactRecipe
                {
                    Name = ProcessingConformanceFixture.SourceRecipe.Name,
                    SemanticVersion = ProcessingConformanceFixture.SourceRecipe.SemanticVersion,
                    ImplementationVersion = ProcessingConformanceFixture.SourceRecipe.ImplementationVersion,
                    OptionsJson = CaptureContractJson.Canonicalize(ProcessingConformanceFixture.SourceRecipe.Options).GetRawText(),
                    OptionsSha256 = ProcessingConformanceFixture.SourceRecipe.OptionsSha256
                }
            };
            frame.Artifacts.Add(artifact);
            db.CentralFrames.Add(frame);
            artifacts.Add(artifact);
            sourceOrdinal++;
        }
        var center = artifacts.Single(item =>
            item.ArtifactId == fixture.Extraction.OrderedSources[2].Source.Locator.Artifact.ArtifactId);
        var extractionRecipeIdentity = TransientCandidateExtractionFactory.ComputeRecipeIdentitySha256(
            fixture.Extraction.Options);
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = center.Id,
            TargetRole = FrameArtifactRole.Metadata,
            TargetRecipeVersion = TransientCandidateExtractionFactory.ProducerVersion,
            TargetVariant = "transient-validation",
            RecipeName = "transient-validation",
            RecipeOptionsJson = CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(fixture.Extraction.Options)).GetRawText(),
            InputSelectorJson = "{}",
            RequestedRecipeIdentitySha256 = extractionRecipeIdentity,
            ExpectedRecipeIdentitySha256 = extractionRecipeIdentity,
            RequestIdentitySha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
            Status = CentralDerivativeJobStatus.Leased,
            AttemptCount = 1,
            MaxAttempts = 3,
            CreatedAtUtc = eventCreatedUtc,
            UpdatedAtUtc = eventCreatedUtc
        };
        var compatibilityJson = "{}";
        var compatibilitySha256 = ProcessingIdentity.ComputePayloadSha256(Encoding.UTF8.GetBytes(compatibilityJson));
        for (var ordinal = 0; ordinal < artifacts.Count; ordinal++)
        {
            var artifact = artifacts[ordinal];
            var requirement = new CentralDerivativeJobInputRequirement
            {
                CentralDerivativeJobId = job.Id,
                Ordinal = ordinal,
                BindingName = $"source-{ordinal}",
                SourceKind = CentralDerivativeInputSourceKind.Artifact,
                IsRequired = true,
                SelectorJson = "{}",
                CompatibilityMode = CentralDerivativeCompatibilityMode.Exact,
                ExpectedAgentId = fixture.AgentId,
                ExpectedCaptureSequence = artifact.Frame!.CaptureSequence,
                ExpectedCentralArtifactId = artifact.Id,
                ResolutionState = CentralDerivativeInputResolutionState.Resolved,
                ResolvedAtUtc = eventCreatedUtc
            };
            var input = new CentralDerivativeJobInput
            {
                CentralDerivativeJobId = job.Id,
                CentralDerivativeJobInputRequirementId = requirement.Id,
                Ordinal = ordinal,
                CentralArtifactId = artifact.Id,
                Artifact = artifact,
                CaptureSequence = artifact.Frame!.CaptureSequence,
                CompatibilityJson = compatibilityJson,
                CompatibilitySha256 = compatibilitySha256,
                ByteLength = artifact.ByteLength,
                SelectedAtUtc = eventCreatedUtc
            };
            requirement.Input = input;
            job.InputRequirements.Add(requirement);
            job.Inputs.Add(input);
        }
        var optionsSchema = "transient-candidate-extraction-options-v1";
        var optionsJson = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(new
        {
            schema = optionsSchema,
            options = fixture.Extraction.Options
        })).GetRawText();
        var optionsRequirement = new CentralDerivativeJobInputRequirement
        {
            CentralDerivativeJobId = job.Id,
            Ordinal = artifacts.Count,
            BindingName = "extraction-options",
            SourceKind = CentralDerivativeInputSourceKind.Canonical,
            IsRequired = true,
            SelectorJson = "{}",
            CompatibilityMode = CentralDerivativeCompatibilityMode.None,
            ExpectedAgentId = fixture.AgentId,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved,
            ResolvedAtUtc = eventCreatedUtc
        };
        job.InputRequirements.Add(optionsRequirement);
        job.CanonicalInputs.Add(new CentralDerivativeJobCanonicalInput
        {
            CentralDerivativeJobId = job.Id,
            CentralDerivativeJobInputRequirementId = optionsRequirement.Id,
            Requirement = optionsRequirement,
            Ordinal = optionsRequirement.Ordinal,
            SchemaVersion = optionsSchema,
            IdentitySha256 = fixture.Extraction.OptionsIdentitySha256,
            CanonicalJson = optionsJson,
            ByteLength = Encoding.UTF8.GetByteCount(optionsJson),
            SelectedAtUtc = eventCreatedUtc
        });
        job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(
            job.Inputs, job.CanonicalInputs);
        var executionOptions = CentralTransientExecutionOptionsJson.Serialize(new CentralTransientExecutionOptionsV1(
            CentralTransientExecutionOptionsV1.CurrentSchemaVersion,
            fixture.Extraction.Options,
            fixture.AssessmentOptions,
            TimeSpan.FromMinutes(5).Ticks,
            TimeSpan.FromSeconds(20).Ticks,
            TimeSpan.FromSeconds(5).Ticks,
            8,
            0.85,
            6.5,
            2_000,
            1,
            CentralTransientMaskPolicyV1.ProfileBoundProjectedStarsV1));
        var validationJob = new CentralTransientValidationJob
        {
            CentralDerivativeJobId = job.Id,
            AgentId = fixture.AgentId,
            SubmissionSchemaVersion = "central-transient-validation-submission-v1",
            SubmissionIdentitySha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
            ExecutionOptionsJson = executionOptions.Json,
            ExecutionOptionsIdentitySha256 = executionOptions.Sha256,
            ProvisionalCentralDerivativeJobId = provisionalJobId,
            CreatedAtUtc = eventCreatedUtc
        };
        foreach (var slot in fixture.Slots)
        {
            validationJob.IdentitySlots.Add(new CentralTransientValidationIdentitySlot
            {
                Ordinal = slot.Ordinal,
                AgentId = fixture.AgentId,
                State = CentralTransientValidationIdentitySlotState.Reserved,
                SubmittedEventId = slot.EventId,
                CandidateId = slot.CandidateId,
                ObservationId = mismatchFirstObservation && slot.Ordinal == 0 ? Guid.NewGuid() : slot.ObservationId,
                AssessmentId = slot.AssessmentId
            });
        }
        db.CentralDerivativeJobs.Add(job);
        db.CentralTransientValidationJobs.Add(validationJob);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new(job.Id, artifacts);
    }

    private static MigrationDatabase CreateDatabase(string scenario)
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorTransientPersistence{scenario}_{Guid.NewGuid():N}"
        };
        return new MigrationDatabase(CreateContext(builder.ConnectionString), builder.ConnectionString);
    }

    private static ApplicationDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);

    internal sealed record SeededDatabase(Guid JobId, IReadOnlyList<CentralArtifact> Artifacts);

    private sealed class RecordingArtifactObjectReader : ICentralArtifactObjectReader
    {
        public List<Guid> VerifiedArtifactIds { get; } = [];

        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
        {
            VerifiedArtifactIds.Add(artifact.Id);
            return Task.FromResult(new CentralArtifactObjectSnapshot(
                artifact.StorageReference,
                "test-etag",
                artifact.ByteLength));
        }

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowOnceTransientDerivativeScheduler(ICentralTransientDerivativeScheduler inner)
        : ICentralTransientDerivativeScheduler
    {
        private bool thrown;

        public Task EnsureScheduledAsync(
            IReadOnlyList<Guid> eventVersionIds,
            CancellationToken cancellationToken)
        {
            if (!thrown)
            {
                thrown = true;
                throw new InvalidOperationException("Injected post-commit scheduling interruption.");
            }
            return inner.EnsureScheduledAsync(eventVersionIds, cancellationToken);
        }
    }

    private sealed class RecordingEmailNotificationService : IEmailNotificationService
    {
        public int SendCount { get; private set; }

        public Task SendAsync(
            string recipient,
            string subject,
            string body,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }

    private sealed class MigrationDatabase(ApplicationDbContext context, string connectionString) : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; } = context;
        public string ConnectionString { get; } = connectionString;
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}

internal sealed record CentralTransientPersistenceFixture(
    TransientCandidateExtractionDescriptorV1 Extraction,
    IReadOnlyList<TransientAssessmentExecutionDescriptorV1> Assessments,
    IReadOnlyList<TransientEventV1> Events,
    IReadOnlyList<FixtureIdentitySlot> Slots,
    IReadOnlyDictionary<Guid, byte[]> Payloads,
    CentralTransientPersistenceRequest Request,
    TransientDeterministicAssessmentOptionsV1 AssessmentOptions,
    string AgentId)
{
    public TransientAssessmentExecutionDescriptorV1 Assessment => Assessments[0];
    public TransientEventV1 Event => Events[0];

    public static CentralTransientPersistenceFixture Create(IReadOnlyList<FixtureIdentitySlot>? reservedSlots = null)
        => Create(reservedSlots, candidateCount: 1);

    public static CentralTransientPersistenceFixture CreateMultiCandidate()
        => Create(reservedSlots: null, candidateCount: 2);

    public static CentralTransientPersistenceFixture CreateNoCandidate(
        IReadOnlyList<FixtureIdentitySlot>? reservedSlots = null)
        => Create(reservedSlots, candidateCount: 0);

    private static CentralTransientPersistenceFixture Create(
        IReadOnlyList<FixtureIdentitySlot>? reservedSlots,
        int candidateCount)
    {
        const string agentId = "central-persistence-agent";
        var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(ProcessingConformanceFixture.SourceRecipe).IdentitySha256;
        var positions = new[]
        {
            TransientTemporalPosition.NMinus2,
            TransientTemporalPosition.NMinus1,
            TransientTemporalPosition.N,
            TransientTemporalPosition.NPlus1,
            TransientTemporalPosition.NPlus2
        };
        var payloads = new Dictionary<Guid, byte[]>();
        var sources = new Dictionary<TransientTemporalPosition, TransientTemporalSource>();
        var epoch = ProcessingConformanceFixture.CapturedUtc;
        var width = candidateCount == 1 ? 2 : 5;
        const int height = 2;
        var layout = ProcessingConformanceFixture.Layout with
        {
            Width = width,
            Height = height,
            StrideBytes = width * 2,
            ByteLength = width * height * 2
        };
        foreach (var position in positions)
        {
            var index = Array.IndexOf(positions, position);
            var samples = Enumerable.Repeat((ushort)100, width * height).ToArray();
            if (position == TransientTemporalPosition.N && candidateCount > 0)
            {
                if (candidateCount == 1)
                {
                    samples[1] = 1_000;
                    samples[2] = 1_000;
                }
                else
                {
                    samples[0] = 1_000;
                    samples[width - 1] = 1_000;
                }
            }
            var payload = U16(samples);
            var artifactId = Guid.NewGuid();
            payloads.Add(artifactId, payload);
            var started = epoch.AddSeconds(index * 2);
            var ended = started.AddSeconds(1);
            var artifact = new ProcessingArtifact(
                artifactId,
                FrameArtifactRole.Raw,
                "source",
                recipeIdentity,
                "application/x-hvo-frame",
                layout,
                payload,
                ended,
                TimeSpan.FromSeconds(1),
                ProcessingConformanceFixture.Compatibility,
                100 + index,
                ObservationStartedUtc: started,
                ObservationEndedUtc: ended);
            var evidence = new TransientSourceEvidenceReferenceV1(
                TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
                Guid.NewGuid(),
                new TransientWholeArtifactLocatorV1(
                    TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                    TransientSourceLocatorKind.WholeArtifact,
                    new TransientArtifactReferenceV1(
                        artifactId,
                        artifact.Role,
                        artifact.Variant,
                        artifact.RecipeIdentitySha256,
                        Convert.ToHexString(SHA256.HashData(payload)))),
                started,
                ended,
                TransientTimingQuality.Reported,
                new TransientTimingProvenanceV1("integration-fixture", "v1"));
            var input = TransientDetectorInputFactory.Create(
                artifact, evidence, new TransientLinearLevelsV1(0, ushort.MaxValue, ushort.MaxValue));
            if (!input.Validation.IsValid)
            {
                throw new InvalidOperationException(input.Validation.ReasonCode);
            }
            sources.Add(position, new TransientTemporalSource(
                position,
                100 + index,
                input.Input!,
                new TransientSensitivityV1("fixture-response-v1", 1, 1),
                CreateMasks(width, height)));
        }
        var background = TransientTemporalBackgroundFactory.Create(new TransientTemporalBackgroundRequest(
            TransientTemporalBackgroundKind.CenteredFinal,
            sources[TransientTemporalPosition.N],
            positions.Where(position => position != TransientTemporalPosition.N).Select(position => sources[position]).ToArray(),
            [],
            TimeSpan.FromSeconds(20)));
        if (background.Status != TransientTemporalBackgroundStatus.Produced)
        {
            throw new InvalidOperationException(background.ReasonCode);
        }
        var slotSeeds = reservedSlots?.ToArray() ??
        [
            new FixtureIdentitySlot(0, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            new FixtureIdentitySlot(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        ];
        var candidateSlots = slotSeeds.Select(item =>
            new TransientCandidateIdentitySlot(item.CandidateId, item.EventId)).ToArray();
        var extractionOptions = new TransientCandidateExtractionOptionsV1(20, 1, 20, 2, 2, 4, 16, 1, 0.8);
        var byEvidence = sources.Values.ToDictionary(item => item.Input.Descriptor.Source.EvidenceId);
        var createdUtc = sources.Values.Max(item => item.Input.Descriptor.Source.ObservationEndedUtc).AddSeconds(1);
        var extraction = TransientCandidateExtractionFactory.Create(new TransientCandidateExtractionRequest(
            agentId,
            createdUtc,
            sources[TransientTemporalPosition.N],
            background.Product!,
            background.Product!.Descriptor.Sources.Select(item => byEvidence[item.EvidenceId]).ToArray(),
            candidateSlots,
            extractionOptions,
            CenteredContextConverged: true));
        if (extraction.Status != (candidateCount == 0
                ? TransientCandidateExtractionStatus.NoCandidate
                : TransientCandidateExtractionStatus.Produced) ||
            extraction.Candidates.Count != candidateCount)
        {
            throw new InvalidOperationException(
                extraction.ReasonCode ?? $"Fixture did not produce exactly {candidateCount} candidates.");
        }
        var assessments = new List<TransientAssessmentExecutionDescriptorV1>(candidateCount);
        var transientEvents = new List<TransientEventV1>(candidateCount);
        var assessmentOptions = new TransientDeterministicAssessmentOptionsV1(
            5, 3, 4, 3, 1.8, 0.5, 5, 1_000, 3, 3, 3, 100, 20, 2);
        for (var ordinal = 0; ordinal < candidateCount; ordinal++)
        {
            var promoted = TransientObservationFactory.CreateAssessmentObservation(new TransientObservationPromotionRequest(
                extraction.Candidates[ordinal].CandidateId,
                slotSeeds[ordinal].ObservationId,
                0,
                extraction.Descriptor!));
            var assessment = TransientAssessmentFactory.Create(new TransientAssessmentExecutionRequest(
                extraction.Candidates[ordinal].EventId,
                slotSeeds[ordinal].AssessmentId,
                createdUtc.AddSeconds(1),
                TransientAssessmentAuthority.Authoritative,
                [promoted],
                assessmentOptions,
                []));
            if (assessment.Status != TransientAssessmentExecutionStatus.Produced)
            {
                throw new InvalidOperationException($"{assessment.ReasonCode}:{assessment.Field}");
            }
            var producedAssessment = assessment.Descriptor!.Assessment;
            var state = producedAssessment.Classification switch
            {
                TransientClassification.Meteor or TransientClassification.Satellite or TransientClassification.Aircraft =>
                    TransientEventState.Validated,
                TransientClassification.SensorArtifact or TransientClassification.EnvironmentalArtifact =>
                    TransientEventState.Rejected,
                _ => TransientEventState.NeedsReview
            };
            assessments.Add(assessment.Descriptor);
            transientEvents.Add(new TransientEventV1(
                TransientEventV1.CurrentSchemaVersion,
                extraction.Candidates[ordinal].EventId,
                Guid.NewGuid(),
                1,
                null,
                null,
                agentId,
                state,
                createdUtc,
                createdUtc.AddSeconds(2),
                promoted.Observation.Source.ObservationStartedUtc,
                promoted.Observation.Source.ObservationEndedUtc,
                [promoted.Observation],
                [producedAssessment],
                [],
                [],
                []));
        }
        var slots = slotSeeds;
        var extractionBytes = TransientCandidateExtractionJson.Serialize(extraction.Descriptor!);
        var request = new CentralTransientPersistenceRequest(
            Guid.Empty,
            CanonicalPayload(extractionBytes),
            transientEvents.Select(item => CanonicalPayload(TransientContractJson.Serialize(item))).ToArray(),
            assessments.Select(item => CanonicalPayload(TransientAssessmentJson.Serialize(item))).ToArray());
        return new(
            extraction.Descriptor!, assessments, transientEvents, slots, payloads, request, assessmentOptions, agentId);
    }

    internal static CentralTransientCanonicalPayload CanonicalPayload(byte[] bytes)
        => new(bytes, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);

    private static TransientDetectorMask[] CreateMasks(int width, int height)
    {
        var empty = Linear16MaskOperations.Empty(width, height);
        return new[]
        {
            TransientDetectorMaskKind.Sky,
            TransientDetectorMaskKind.ImageCircle,
            TransientDetectorMaskKind.Horizon,
            TransientDetectorMaskKind.Obstruction,
            TransientDetectorMaskKind.BadPixel,
            TransientDetectorMaskKind.Star
        }.Select(kind => TransientDetectorMask.Create(
            kind,
            new ProcessingAlgorithmIdentity($"fixture-{kind.ToString().ToUpperInvariant()}-mask", "v1"),
            empty)).ToArray();
    }

    private static byte[] U16(ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++)
        {
            bytes[index * 2] = (byte)values[index];
            bytes[index * 2 + 1] = (byte)(values[index] >> 8);
        }
        return bytes;
    }
}

internal sealed record FixtureIdentitySlot(
    int Ordinal,
    Guid EventId,
    Guid CandidateId,
    Guid ObservationId,
    Guid AssessmentId);
