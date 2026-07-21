using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CentralTransientEventPersistenceIntegrationTests
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
            using var telemetry = new CentralArtifactRetrievalTelemetry();
            var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
            if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket("skymonitor-artifacts"))
                    .ConfigureAwait(false))
            {
                await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket("skymonitor-artifacts"))
                    .ConfigureAwait(false);
            }
            var releaseService = new CentralArtifactRetentionService(
                releaseContext,
                new CentralArtifactRetentionReferences(releaseContext),
                minio,
                TimeProvider.System,
                telemetry);
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

    private static async Task<SeededDatabase> SeedAsync(
        ApplicationDbContext db,
        CentralTransientPersistenceFixture fixture,
        bool mismatchFirstObservation = false,
        Guid? provisionalJobId = null)
    {
        var artifacts = new List<CentralArtifact>();
        var sourceOrdinal = 0;
        var eventCreatedUtc = fixture.Events.Count > 0
            ? fixture.Event.EventCreatedUtc
            : fixture.Extraction.OrderedSources.Max(item => item.Source.ObservationEndedUtc).AddSeconds(1);
        foreach (var source in fixture.Extraction.OrderedSources)
        {
            var reference = source.Source.Locator.Artifact;
            var frame = new CentralFrame
            {
                RegistrationId = Guid.NewGuid(),
                DevicePublicId = Guid.NewGuid(),
                ObservatoryId = Guid.NewGuid(),
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
                StorageReference = $"minio://skymonitor-artifacts/{Guid.NewGuid():N}.bin",
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
        job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs);
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

    private sealed record SeededDatabase(Guid JobId, IReadOnlyList<CentralArtifact> Artifacts);

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
