using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

using Program = HVO.SkyMonitor.LogicHost.Program;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed partial class HybridTransientSubmissionIntegrationTests
{
    internal const string DeviceKey = "cameraagent-integration-key";
    private static readonly TransientTemporalPosition[] Positions =
    [
        TransientTemporalPosition.NMinus2,
        TransientTemporalPosition.NMinus1,
        TransientTemporalPosition.N,
        TransientTemporalPosition.NPlus1,
        TransientTemporalPosition.NPlus2
    ];

    [TestMethod]
    public async Task ModeDisabledReturnsTemporaryUnavailabilityAndAuditsReason()
    {
        var scenario = await CreateScenarioAsync().ConfigureAwait(false);
        using var factory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CentralTransient:Mode"] = "Off"
                })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var response = await SendAsync(client, scenario.DeviceId, DeviceKey, scenario.Envelope)
            .ConfigureAwait(false);

        response.StatusCode.Should().Be((HttpStatusCode)425);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        problem.RootElement.GetProperty("reasonCode").GetString()
            .Should().Be(CentralTransientSubmissionReasonCodes.ModeDisabled);
        await using var scope = factory.Services.CreateAsyncScope();
        var audits = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralTransientSubmissionAudits.AsNoTracking();
        (await audits.CountAsync(item => item.CandidateId == scenario.Envelope.CandidateId
                && item.ReasonCode == CentralTransientSubmissionReasonCodes.ModeDisabled)
            .ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task CanonicalSubmission_ConvergesDuplicatesAndPersistsAuthoritativeEvent()
    {
        var scenario = await CreateScenarioAsync(multipleCandidates: true).ConfigureAwait(false);
        using var factory = CreateHybridFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseTasks = Enumerable.Range(0, 9).Select(async _ =>
        {
            await release.Task.ConfigureAwait(false);
            return await SendAsync(client, scenario.DeviceId, DeviceKey, scenario.Envelope).ConfigureAwait(false);
        }).ToArray();
        release.SetResult();
        var responses = await Task.WhenAll(responseTasks).ConfigureAwait(false);
        try
        {
            responses.Should().ContainSingle(response => response.StatusCode == HttpStatusCode.Accepted);
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(8);
            var acceptedAcknowledgement = ParseAcknowledgement(await responses.Single(response =>
                    response.StatusCode == HttpStatusCode.Accepted).Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            acceptedAcknowledgement.Disposition.Should().Be(TransientCandidateSubmissionDisposition.Accepted);
            var duplicateAcknowledgements = await Task.WhenAll(responses.Where(response =>
                    response.StatusCode == HttpStatusCode.OK).Select(async response =>
                ParseAcknowledgement(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false))))
                .ConfigureAwait(false);
            duplicateAcknowledgements.Should().OnlyContain(acknowledgement =>
                acknowledgement.Disposition == TransientCandidateSubmissionDisposition.Duplicate
                && acknowledgement.ReceivedAtUtc == acceptedAcknowledgement.ReceivedAtUtc);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        Guid jobId;
        await using (var assertionScope = factory.Services.CreateAsyncScope())
        {
            var db = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var validation = await db.CentralTransientValidationJobs.AsNoTracking()
                .Include(item => item.IdentitySlots)
                .SingleAsync(item => item.SubmissionIdentitySha256 == scenario.Envelope.SubmissionIdentitySha256)
                .ConfigureAwait(false);
            jobId = validation.CentralDerivativeJobId;
            validation.IdentitySlots.Single(item => item.Ordinal == 0).CandidateId.Should().Be(scenario.Envelope.CandidateId);
            validation.IdentitySlots.Single(item => item.Ordinal == 0).SubmittedEventId.Should().Be(scenario.Envelope.EventId);
            (await db.CentralDerivativeJobInputs.CountAsync(item => item.CentralDerivativeJobId == jobId)
                .ConfigureAwait(false)).Should().Be(5);
            foreach (var sourceId in scenario.CentralArtifactIds)
            {
                (await assertionScope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>()
                    .IsHeldAsync(sourceId, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            }
            await db.CentralDerivativeJobs.Where(item => item.Id != jobId &&
                    (item.Status == CentralDerivativeJobStatus.Waiting || item.Status == CentralDerivativeJobStatus.Pending))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, CentralDerivativeJobStatus.Canceled)
                    .SetProperty(item => item.StateReasonCode, "integration-test-isolation"))
                .ConfigureAwait(false);
        }

        CentralDerivativeJobLease lease;
        await using (var claimScope = factory.Services.CreateAsyncScope())
        {
            lease = (await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("hybrid-integration-worker", TimeSpan.FromMinutes(2), CancellationToken.None)
                .ConfigureAwait(false))!;
        }
        lease.Should().NotBeNull();
        lease.JobId.Should().Be(jobId);
        await using (var executionScope = factory.Services.CreateAsyncScope())
        {
            var result = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            result.Status.Should().Be(ProcessingOutcomeStatus.Produced, result.ReasonCode);
        }
        await using (var persistenceScope = factory.Services.CreateAsyncScope())
        {
            var db = persistenceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var transientEvent = await db.CentralTransientEvents.AsNoTracking()
                .Include(item => item.Versions)
                .Include(item => item.Observations)
                .Include(item => item.Assessments)
                .SingleAsync(item => item.AgentId == scenario.DeviceId && item.EventId == scenario.Envelope.EventId)
                .ConfigureAwait(false);
            transientEvent.Versions.Should().ContainSingle();
            transientEvent.Assessments.Should().ContainSingle(item =>
                item.Authority == TransientAssessmentAuthority.Authoritative);
            transientEvent.Observations.Should().ContainSingle(item =>
                item.OriginatingCandidateId == scenario.Envelope.CandidateId);
            var receipt = await db.CentralTransientExtractionReceipts.AsNoTracking()
                .SingleAsync(item => item.CentralDerivativeJobId == jobId).ConfigureAwait(false);
            var extraction = TransientCandidateExtractionJson.Parse(
                System.Text.Encoding.UTF8.GetBytes(receipt.CanonicalReceiptJson));
            extraction.Candidates.Should().HaveCount(2);
            Array.FindIndex(extraction.Candidates.ToArray(),
                item => item.CandidateId == scenario.Envelope.CandidateId).Should().Be(1);
            var persisted = extraction.Candidates[1];
            persisted.EventId.Should().Be(scenario.Envelope.EventId);
            persisted.Extraction.OriginatingCandidateId.Should().Be(scenario.Envelope.CandidateId);
            persisted.Geometry!.Bounds.Should().Be(scenario.Envelope.Candidate.Geometry!.Bounds);
            persisted.Geometry.Polyline.Should().Equal(scenario.Envelope.Candidate.Geometry.Polyline);
        }
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(4)]
    public async Task FutureProfileTransition_RetiresAndReplaysWithoutCreatingValidationJob(int futureIndex)
    {
        var scenario = await CreateScenarioAsync().ConfigureAwait(false);
        Guid futureFrameId;
        string originalRigSha256;
        await using (var mutationScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = mutationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            futureFrameId = await db.CentralArtifacts.AsNoTracking()
                .Where(item => item.Id == scenario.CentralArtifactIds[futureIndex])
                .Select(item => item.CentralFrameId)
                .SingleAsync().ConfigureAwait(false);
            var rig = await db.CentralCaptureProfiles
                .SingleAsync(item => item.CentralFrameId == futureFrameId && item.Kind == CentralProfileKind.Rig)
                .ConfigureAwait(false);
            originalRigSha256 = rig.Sha256;
            rig.Sha256 = new string('A', 64);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        using var factory = CreateHybridFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var firstResponse = await SendAsync(client, scenario.DeviceId, DeviceKey, scenario.Envelope)
            .ConfigureAwait(false);
        using var replayResponse = await SendAsync(client, scenario.DeviceId, DeviceKey, scenario.Envelope)
            .ConfigureAwait(false);
        var first = ParseAcknowledgement(await firstResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        var replay = ParseAcknowledgement(await replayResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false));

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Disposition.Should().Be(TransientCandidateSubmissionDisposition.Retired);
        first.SchemaVersion.Should().Be(TransientCandidateSubmissionAcknowledgementV1.RetirementSchemaVersion);
        replay.Should().Be(first);
        await using (var restoreScope = factory.Services.CreateAsyncScope())
        {
            var restoreDb = restoreScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await restoreDb.CentralCaptureProfiles
                .Where(item => item.CentralFrameId == futureFrameId && item.Kind == CentralProfileKind.Rig)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Sha256, originalRigSha256))
                .ConfigureAwait(false);
        }
        var conflictingEnvelope = CreateEnvelope(scenario.Envelope.Candidate with
        {
            CreatedUtc = scenario.Envelope.Candidate.CreatedUtc.AddTicks(1)
        });
        using var conflictingResponse = await SendAsync(
            client, scenario.DeviceId, DeviceKey, conflictingEnvelope).ConfigureAwait(false);
        conflictingResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var assertionScope = factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await assertionDb.CentralTransientValidationJobs.CountAsync(item =>
                item.SubmissionIdentitySha256 == scenario.Envelope.SubmissionIdentitySha256)
            .ConfigureAwait(false)).Should().Be(0);
        var audits = await assertionDb.CentralTransientSubmissionAudits.AsNoTracking()
            .Where(item => item.CandidateId == scenario.Envelope.CandidateId
                && item.ReasonCode == CentralTransientSubmissionReasonCodes.ProfileTransitionRetired)
            .ToArrayAsync().ConfigureAwait(false);
        audits.Should().ContainSingle();
        audits[0].RecordedAtUtc.Should().Be(first.ReceivedAtUtc);
        (await assertionDb.CentralTransientSubmissionAudits.CountAsync(item =>
                item.CandidateId == conflictingEnvelope.CandidateId
                && item.ReasonCode == CentralTransientSubmissionReasonCodes.IdentityConflict)
            .ConfigureAwait(false)).Should().Be(1);
    }

    [TestMethod]
    public async Task PendingFutureEvidence_RemainsRetryableWithoutRetirement()
    {
        var scenario = await CreateScenarioAsync().ConfigureAwait(false);
        await using (var mutationScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = mutationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CentralArtifacts.Where(item => item.Id == scenario.CentralArtifactIds[3])
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ObjectState, CentralArtifactObjectState.Pending)
                    .SetProperty(item => item.ReconstructionState, CentralReconstructionState.PendingReference))
                .ConfigureAwait(false);
        }
        using var factory = CreateHybridFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var response = await SendAsync(client, scenario.DeviceId, DeviceKey, scenario.Envelope)
            .ConfigureAwait(false);
        await using (var restoreScope = factory.Services.CreateAsyncScope())
        {
            var restoreDb = restoreScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await restoreDb.CentralArtifacts.Where(item => item.Id == scenario.CentralArtifactIds[3])
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ObjectState, CentralArtifactObjectState.Available)
                    .SetProperty(item => item.ReconstructionState, CentralReconstructionState.Complete))
                .ConfigureAwait(false);
        }

        response.StatusCode.Should().Be((HttpStatusCode)425);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        problem.RootElement.GetProperty("reasonCode").GetString()
            .Should().Be(CentralTransientSubmissionReasonCodes.EvidenceUnavailable);
        await using var assertionScope = factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await assertionDb.CentralTransientSubmissionAudits.AnyAsync(item =>
                item.CandidateId == scenario.Envelope.CandidateId
                && item.ReasonCode == CentralTransientSubmissionReasonCodes.ProfileTransitionRetired)
            .ConfigureAwait(false)).Should().BeFalse();
    }

    [TestMethod]
    public async Task TwoCanonicalCandidates_PersistOnlyTheirSubmittedAuthoritativeEvents()
    {
        var scenario = await CreateScenarioAsync(multipleCandidates: true).ConfigureAwait(false);
        scenario.Envelopes.Should().HaveCount(2);
        using var factory = CreateHybridFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));

        foreach (var envelope in scenario.Envelopes)
        {
            using var response = await SendAsync(client, scenario.DeviceId, DeviceKey, envelope).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        var submissionIdentities = scenario.Envelopes.Select(item => item.SubmissionIdentitySha256).ToArray();
        submissionIdentities.Should().OnlyHaveUniqueItems();
        Guid[] jobIds;
        await using (var stateScope = factory.Services.CreateAsyncScope())
        {
            var db = stateScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var validations = await db.CentralTransientValidationJobs.AsNoTracking()
                .Include(item => item.Job)
                .Where(item => submissionIdentities.Contains(item.SubmissionIdentitySha256))
                .ToArrayAsync().ConfigureAwait(false);
            validations.Should().HaveCount(2);
            validations.Select(item => item.SubmissionIdentitySha256).Should().BeEquivalentTo(submissionIdentities);
            validations.Select(item => item.CentralDerivativeJobId).Should().OnlyHaveUniqueItems();
            validations.Select(item => item.Job!.RequestIdentitySha256).Should().OnlyHaveUniqueItems();
            jobIds = validations.Select(item => item.CentralDerivativeJobId).ToArray();
            await db.CentralDerivativeJobs.Where(item => !jobIds.Contains(item.Id) &&
                    (item.Status == CentralDerivativeJobStatus.Waiting || item.Status == CentralDerivativeJobStatus.Pending))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, CentralDerivativeJobStatus.Canceled)
                    .SetProperty(item => item.StateReasonCode, "integration-test-isolation"))
                .ConfigureAwait(false);
        }

        var executedJobIds = new HashSet<Guid>();
        for (var index = 0; index < jobIds.Length; index++)
        {
            CentralDerivativeJobLease lease;
            await using (var claimScope = factory.Services.CreateAsyncScope())
            {
                lease = (await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                    .ClaimNextAsync($"hybrid-two-candidate-worker-{index}", TimeSpan.FromMinutes(2), CancellationToken.None)
                    .ConfigureAwait(false))!;
            }
            lease.Should().NotBeNull();
            jobIds.Should().Contain(lease.JobId);
            executedJobIds.Add(lease.JobId).Should().BeTrue();
            await using var executionScope = factory.Services.CreateAsyncScope();
            var result = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            result.Status.Should().Be(ProcessingOutcomeStatus.Produced, result.ReasonCode);
            await executionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralDerivativeJobs
                .Where(item => !jobIds.Contains(item.Id) && item.Status == CentralDerivativeJobStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, CentralDerivativeJobStatus.Canceled)
                    .SetProperty(item => item.StateReasonCode, "integration-test-downstream-isolation"))
                .ConfigureAwait(false);
        }
        executedJobIds.Should().BeEquivalentTo(jobIds);

        await using var assertionScope = factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persistedValidations = await assertionDb.CentralTransientValidationJobs.AsNoTracking()
            .Include(item => item.IdentitySlots)
            .Include(item => item.ExtractionReceipt)
            .Where(item => jobIds.Contains(item.CentralDerivativeJobId))
            .ToArrayAsync().ConfigureAwait(false);
        var events = await assertionDb.CentralTransientEvents.AsNoTracking()
            .Include(item => item.Observations)
            .Include(item => item.Assessments)
            .Where(item => item.AgentId == scenario.DeviceId)
            .ToArrayAsync().ConfigureAwait(false);
        events.Should().HaveCount(2);
        events.Select(item => item.EventId).Should().BeEquivalentTo(
            scenario.Envelopes.Select(item => item.EventId));
        events.SelectMany(item => item.Observations).Should().HaveCount(2);

        for (var index = 0; index < scenario.Envelopes.Count; index++)
        {
            var envelope = scenario.Envelopes[index];
            var validation = persistedValidations.Single(item =>
                item.SubmissionIdentitySha256 == envelope.SubmissionIdentitySha256);
            var committedSlot = validation.IdentitySlots.Single(item =>
                item.State == CentralTransientValidationIdentitySlotState.Committed);
            committedSlot.CandidateId.Should().Be(envelope.CandidateId);
            committedSlot.SubmittedEventId.Should().Be(envelope.EventId);
            committedSlot.PersistedEventId.Should().Be(envelope.EventId);
            validation.IdentitySlots.Where(item => item.Id != committedSlot.Id).Should().OnlyContain(item =>
                item.State == CentralTransientValidationIdentitySlotState.Unused);

            var extraction = TransientCandidateExtractionJson.Parse(
                System.Text.Encoding.UTF8.GetBytes(validation.ExtractionReceipt!.CanonicalReceiptJson));
            extraction.Candidates.Should().HaveCount(2);
            Array.FindIndex(extraction.Candidates.ToArray(), item => item.CandidateId == envelope.CandidateId)
                .Should().Be(index);

            var persistedEvent = events.Single(item => item.EventId == envelope.EventId);
            persistedEvent.Observations.Should().ContainSingle(item =>
                item.OriginatingCandidateId == envelope.CandidateId);
            persistedEvent.Assessments.Should().ContainSingle(item =>
                item.Authority == TransientAssessmentAuthority.Authoritative);
        }
    }

    [TestMethod]
    public async Task RejectedSubmissions_AreBoundedQuarantinedAndDoNotMutateAcceptance()
    {
        var scenario = await CreateScenarioAsync().ConfigureAwait(false);
        using var factory = CreateHybridFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using var accepted = await SendAsync(client, scenario.DeviceId, DeviceKey, scenario.Envelope)
            .ConfigureAwait(false);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var conflictingCandidate = scenario.Envelope.Candidate with { EventId = Guid.NewGuid() };
        var conflicting = CreateEnvelope(conflictingCandidate);
        var conflicts = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => SendAsync(client, scenario.DeviceId, DeviceKey, conflicting))).ConfigureAwait(false);
        try
        {
            conflicts.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.Conflict);
        }

        finally
        {
            foreach (var response in conflicts)
            {
                response.Dispose();
            }
        }

        var conflictingEvent = CreateEnvelope(Reidentify(scenario.Envelope.Candidate) with
        {
            EventId = scenario.Envelope.EventId
        });
        using var conflictingEventResponse = await SendAsync(
            client, scenario.DeviceId, DeviceKey, conflictingEvent).ConfigureAwait(false);
        conflictingEventResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var spoofed = CreateEnvelope(Reidentify(scenario.Envelope.Candidate) with
        {
            AgentId = $"spoofed-{Guid.NewGuid():N}"
        });
        using var spoofedResponse = await SendAsync(client, scenario.DeviceId, DeviceKey, spoofed).ConfigureAwait(false);
        spoofedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var otherDevice = $"hybrid-other-{Guid.NewGuid():N}";
        await AssemblyHooks.Fixture.SeedActiveDeviceAsync(otherDevice).ConfigureAwait(false);
        var crossDeviceEnvelope = CreateEnvelope(Reidentify(scenario.Envelope.Candidate) with
        {
            AgentId = otherDevice
        });
        using var crossDevice = await SendAsync(client, otherDevice, DeviceKey, crossDeviceEnvelope).ConfigureAwait(false);
        crossDevice.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var crossDeviceBody = await crossDevice.Content.ReadAsStringAsync().ConfigureAwait(false);
        crossDeviceBody.Should().NotContain(DeviceKey).And.NotContain("minio://").And.NotContain("integration/");

        var independentDevice = await CreateScenarioAsync().ConfigureAwait(false);
        var sharedEventCandidate = Reidentify(independentDevice.Envelope.Candidate) with
        {
            EventId = scenario.Envelope.EventId
        };
        var sharedEventEnvelope = CreateEnvelope(sharedEventCandidate);
        using var sharedEventResponse = await SendAsync(
            client, independentDevice.DeviceId, DeviceKey, sharedEventEnvelope).ConfigureAwait(false);
        sharedEventResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var missingCandidateId = Guid.NewGuid();
        var missingCandidate = scenario.Envelope.Candidate with
        {
            CandidateId = missingCandidateId,
            EventId = Guid.NewGuid(),
            Extraction = scenario.Envelope.Candidate.Extraction with
            {
                OriginatingCandidateId = missingCandidateId
            },
            ContextSources = scenario.Envelope.Candidate.ContextSources.Select((source, index) => index == 0
                ? source with
                {
                    Locator = source.Locator with
                    {
                        Artifact = source.Locator.Artifact with { ArtifactId = Guid.NewGuid() }
                    }
                }
                : source).ToArray()
        };
        var missing = CreateEnvelope(missingCandidate);
        using var missingResponse = await SendAsync(client, scenario.DeviceId, DeviceKey, missing).ConfigureAwait(false);
        missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var unauthenticated = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var unauthenticatedResponse = await SendAsync(
            unauthenticated, scenario.DeviceId, DeviceKey, scenario.Envelope).ConfigureAwait(false);
        unauthenticatedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using var assertionScope = factory.Services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.CentralTransientValidationJobs.CountAsync(item =>
            item.SubmissionIdentitySha256 == scenario.Envelope.SubmissionIdentitySha256).ConfigureAwait(false)).Should().Be(1);
        (await db.CentralTransientValidationJobs.CountAsync(item =>
            item.IdentitySlots.Any(slot => slot.SubmittedEventId == scenario.Envelope.EventId)).ConfigureAwait(false))
            .Should().Be(2);
        (await db.CentralTransientSubmissionAudits.CountAsync(item =>
            item.CandidateId == conflicting.CandidateId
            && item.ReasonCode == CentralTransientSubmissionReasonCodes.IdentityConflict).ConfigureAwait(false)).Should().Be(1);
        (await db.CentralTransientSubmissionAudits.CountAsync(item =>
            item.CandidateId == conflictingEvent.CandidateId
            && item.ReasonCode == CentralTransientSubmissionReasonCodes.IdentityConflict).ConfigureAwait(false)).Should().Be(1);
        (await db.CentralTransientSubmissionAudits.CountAsync(item =>
            item.CandidateId == missing.CandidateId
             && item.ReasonCode == CentralTransientSubmissionReasonCodes.EvidenceMissing).ConfigureAwait(false)).Should().Be(1);
        (await db.CentralTransientSubmissionAudits.AnyAsync(item =>
            item.AgentId == scenario.DeviceId && item.CandidateId == spoofed.CandidateId &&
            item.ReasonCode == CentralTransientSubmissionReasonCodes.AuthenticationRejected).ConfigureAwait(false)).Should().BeTrue();
        (await db.CentralTransientSubmissionAudits.AnyAsync(item =>
            item.AgentId == otherDevice
            && item.ReasonCode == CentralTransientSubmissionReasonCodes.EvidenceMissing).ConfigureAwait(false)).Should().BeTrue();
    }

    [TestMethod]
    public async Task UnavailableCorruptAndTimedOutEvidence_ReturnBoundedDurableReasons()
    {
        var scenario = await CreateScenarioAsync().ConfigureAwait(false);
        using var factory = CreateHybridFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var firstSourceId = scenario.CentralArtifactIds[0];
        string storageReference;
        long byteLength;
        await using (var stateScope = factory.Services.CreateAsyncScope())
        {
            var db = stateScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var source = await db.CentralArtifacts.SingleAsync(item => item.Id == firstSourceId).ConfigureAwait(false);
            storageReference = source.StorageReference;
            byteLength = source.ByteLength;
            source.ObjectState = CentralArtifactObjectState.Pending;
            source.ReconstructionState = CentralReconstructionState.PendingReference;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        var unavailable = CreateEnvelope(Reidentify(scenario.Envelope.Candidate));
        using var unavailableResponse = await SendAsync(client, scenario.DeviceId, DeviceKey, unavailable)
            .ConfigureAwait(false);
        unavailableResponse.StatusCode.Should().Be((HttpStatusCode)425);

        await using (var restoreScope = factory.Services.CreateAsyncScope())
        {
            var db = restoreScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var source = await db.CentralArtifacts.SingleAsync(item => item.Id == firstSourceId).ConfigureAwait(false);
            source.ObjectState = CentralArtifactObjectState.Available;
            source.ReconstructionState = CentralReconstructionState.Complete;
            await db.SaveChangesAsync().ConfigureAwait(false);
            var objectKey = storageReference["minio://skymonitor-artifacts/".Length..];
            await using var corrupt = new MemoryStream(new byte[checked((int)byteLength)], writable: false);
            await restoreScope.ServiceProvider.GetRequiredService<IMinioClient>().PutObjectAsync(
                new PutObjectArgs()
                    .WithBucket("skymonitor-artifacts")
                    .WithObject(objectKey)
                    .WithStreamData(corrupt)
                    .WithObjectSize(byteLength)
                    .WithContentType("application/x-hvo-linear-frame"),
                CancellationToken.None).ConfigureAwait(false);
        }
        var corruptEnvelope = CreateEnvelope(Reidentify(scenario.Envelope.Candidate));
        using var corruptResponse = await SendAsync(client, scenario.DeviceId, DeviceKey, corruptEnvelope)
            .ConfigureAwait(false);
        corruptResponse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await using (var assertionScope = factory.Services.CreateAsyncScope())
        {
            var db = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralArtifacts.Where(item => item.Id == firstSourceId)
                .Select(item => item.ObjectState).SingleAsync().ConfigureAwait(false))
                .Should().Be(CentralArtifactObjectState.Quarantined);
            (await db.CentralTransientSubmissionAudits.AnyAsync(item =>
                item.CandidateId == unavailable.CandidateId
                && item.ReasonCode == CentralTransientSubmissionReasonCodes.EvidenceUnavailable).ConfigureAwait(false))
                .Should().BeTrue();
            (await db.CentralTransientSubmissionAudits.AnyAsync(item =>
                item.CandidateId == corruptEnvelope.CandidateId
                && item.ReasonCode == CentralTransientSubmissionReasonCodes.EvidenceIntegrity).ConfigureAwait(false))
                .Should().BeTrue();
        }

        var timeoutScenario = await CreateScenarioAsync().ConfigureAwait(false);
        using var timeoutFactory = CreateHybridFactory(services =>
            services.Replace(ServiceDescriptor.Scoped<ICentralArtifactObjectReader, TimeoutObjectReader>()));
        using var timeoutClient = timeoutFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        timeoutClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(timeoutClient).ConfigureAwait(false));
        using var timeoutResponse = await SendAsync(
            timeoutClient, timeoutScenario.DeviceId, DeviceKey, timeoutScenario.Envelope).ConfigureAwait(false);
        timeoutResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await timeoutResponse.Content.ReadAsStringAsync().ConfigureAwait(false))
            .Should().NotContain(DeviceKey).And.NotContain("minio://");
        await using var timeoutScope = timeoutFactory.Services.CreateAsyncScope();
        (await timeoutScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralTransientSubmissionAudits.AnyAsync(item =>
                item.CandidateId == timeoutScenario.Envelope.CandidateId
                && item.ReasonCode == CentralTransientSubmissionReasonCodes.EvidenceTimeout).ConfigureAwait(false))
            .Should().BeTrue();

        var generationScenario = await CreateScenarioAsync().ConfigureAwait(false);
        using var generationFactory = CreateHybridFactory(services =>
            services.Replace(ServiceDescriptor.Scoped<ICentralArtifactObjectReader>(provider =>
                new GenerationTimeoutObjectReader(
                    ActivatorUtilities.CreateInstance<CentralArtifactObjectReader>(provider)))));
        using var generationClient = generationFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        generationClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(generationClient).ConfigureAwait(false));
        using var generationResponse = await SendAsync(
            generationClient, generationScenario.DeviceId, DeviceKey, generationScenario.Envelope).ConfigureAwait(false);
        generationResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await using var generationScope = generationFactory.Services.CreateAsyncScope();
        (await generationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralTransientSubmissionAudits.AnyAsync(item =>
                item.CandidateId == generationScenario.Envelope.CandidateId &&
                item.ReasonCode == CentralTransientSubmissionReasonCodes.EvidenceTimeout).ConfigureAwait(false))
            .Should().BeTrue();

        var staleGenerationScenario = await CreateScenarioAsync().ConfigureAwait(false);
        using var staleGenerationFactory = CreateHybridFactory(services =>
            services.Replace(ServiceDescriptor.Scoped<ICentralArtifactObjectReader>(provider =>
                new StaleGenerationObjectReader(
                    ActivatorUtilities.CreateInstance<CentralArtifactObjectReader>(provider)))));
        using var staleGenerationClient = staleGenerationFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        staleGenerationClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(staleGenerationClient).ConfigureAwait(false));
        using var staleGenerationResponse = await SendAsync(
            staleGenerationClient,
            staleGenerationScenario.DeviceId,
            DeviceKey,
            staleGenerationScenario.Envelope).ConfigureAwait(false);
        staleGenerationResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var staleGenerationScope = staleGenerationFactory.Services.CreateAsyncScope();
        var staleGenerationDb = staleGenerationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await staleGenerationDb.CentralTransientSubmissionAudits.AnyAsync(item =>
                item.CandidateId == staleGenerationScenario.Envelope.CandidateId &&
                item.ReasonCode == CentralTransientSubmissionReasonCodes.EvidenceConflict).ConfigureAwait(false))
            .Should().BeTrue();
        (await staleGenerationDb.CentralTransientValidationJobs.AnyAsync(item =>
                item.SubmissionIdentitySha256 == staleGenerationScenario.Envelope.SubmissionIdentitySha256)
            .ConfigureAwait(false)).Should().BeFalse();

        var lockLossScenario = await CreateScenarioAsync().ConfigureAwait(false);
        using var lockLossFactory = CreateHybridFactory(services =>
            services.Replace(ServiceDescriptor.Scoped<ICentralArtifactObjectReader>(provider =>
                new LockLossObjectReader(
                    ActivatorUtilities.CreateInstance<CentralArtifactObjectReader>(provider),
                    provider.GetRequiredService<ApplicationDbContext>()))));
        using var lockLossClient = lockLossFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        lockLossClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(lockLossClient).ConfigureAwait(false));
        using var lockLossResponse = await SendAsync(
            lockLossClient, lockLossScenario.DeviceId, DeviceKey, lockLossScenario.Envelope).ConfigureAwait(false);
        lockLossResponse.StatusCode.Should().Be((HttpStatusCode)425);
        await using var lockLossScope = lockLossFactory.Services.CreateAsyncScope();
        var lockLossDb = lockLossScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await lockLossDb.CentralTransientSubmissionAudits.AnyAsync(item =>
                item.CandidateId == lockLossScenario.Envelope.CandidateId &&
                item.ReasonCode == CentralTransientSubmissionReasonCodes.EvidenceUnavailable).ConfigureAwait(false))
            .Should().BeTrue();
        (await lockLossDb.CentralTransientValidationJobs.AnyAsync(item =>
                item.SubmissionIdentitySha256 == lockLossScenario.Envelope.SubmissionIdentitySha256)
            .ConfigureAwait(false)).Should().BeFalse();

        foreach (var faultStage in Enum.GetValues<CentralTransientSubmissionFaultStage>())
        {
            var finalizationScenario = await CreateScenarioAsync().ConfigureAwait(false);
            using var finalizationFactory = CreateHybridFactory(services =>
                services.AddSingleton<ICentralTransientSubmissionFaultInjector>(
                    new ThrowingSubmissionFaultInjector(faultStage)));
            using var finalizationClient = finalizationFactory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            finalizationClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", await GetSystemTokenAsync(finalizationClient).ConfigureAwait(false));
            using var finalizationResponse = await SendAsync(
                finalizationClient,
                finalizationScenario.DeviceId,
                DeviceKey,
                finalizationScenario.Envelope).ConfigureAwait(false);
            finalizationResponse.StatusCode.Should().Be((HttpStatusCode)425);
            await using var finalizationScope = finalizationFactory.Services.CreateAsyncScope();
            var finalizationDb = finalizationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await finalizationDb.CentralTransientSubmissionAudits.AnyAsync(item =>
                    item.CandidateId == finalizationScenario.Envelope.CandidateId &&
                    item.ReasonCode == CentralTransientSubmissionReasonCodes.EvidenceUnavailable)
                .ConfigureAwait(false)).Should().BeTrue();
            (await finalizationDb.CentralTransientValidationJobs.AnyAsync(item =>
                    item.SubmissionIdentitySha256 == finalizationScenario.Envelope.SubmissionIdentitySha256)
                .ConfigureAwait(false)).Should().BeFalse();

            using var retryFactory = CreateHybridFactory();
            using var retryClient = retryFactory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            retryClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", await GetSystemTokenAsync(retryClient).ConfigureAwait(false));
            using var retryResponse = await SendAsync(
                retryClient,
                finalizationScenario.DeviceId,
                DeviceKey,
                finalizationScenario.Envelope).ConfigureAwait(false);
            retryResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
            finalizationDb.ChangeTracker.Clear();
            var retryJobId = await finalizationDb.CentralTransientValidationJobs.AsNoTracking()
                .Where(item => item.SubmissionIdentitySha256 == finalizationScenario.Envelope.SubmissionIdentitySha256)
                .Select(item => item.CentralDerivativeJobId)
                .SingleAsync().ConfigureAwait(false);
            (await finalizationDb.CentralDerivativeJobInputs.CountAsync(item =>
                item.CentralDerivativeJobId == retryJobId).ConfigureAwait(false)).Should().Be(5);
        }


        var finalFenceLossScenario = await CreateScenarioAsync().ConfigureAwait(false);
        using var finalFenceLossFactory = CreateHybridFactory(services =>
            services.AddScoped<ICentralTransientSubmissionFaultInjector>(provider =>
                new ClosingSubmissionConnectionFaultInjector(
                    provider.GetRequiredService<ApplicationDbContext>())));
        using var finalFenceLossClient = finalFenceLossFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        finalFenceLossClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(finalFenceLossClient).ConfigureAwait(false));
        using var finalFenceLossResponse = await SendAsync(
            finalFenceLossClient,
            finalFenceLossScenario.DeviceId,
            DeviceKey,
            finalFenceLossScenario.Envelope).ConfigureAwait(false);
        finalFenceLossResponse.StatusCode.Should().Be((HttpStatusCode)425);
        await using var finalFenceLossScope = finalFenceLossFactory.Services.CreateAsyncScope();
        var finalFenceLossDb = finalFenceLossScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await finalFenceLossDb.CentralTransientSubmissionAudits.AnyAsync(item =>
                item.CandidateId == finalFenceLossScenario.Envelope.CandidateId &&
                item.ReasonCode == CentralTransientSubmissionReasonCodes.EvidenceUnavailable).ConfigureAwait(false))
            .Should().BeTrue();
        (await finalFenceLossDb.CentralTransientValidationJobs.AnyAsync(item =>
                item.SubmissionIdentitySha256 == finalFenceLossScenario.Envelope.SubmissionIdentitySha256)
            .ConfigureAwait(false)).Should().BeFalse();
        using var finalFenceRetryFactory = CreateHybridFactory();
        using var finalFenceRetryClient = finalFenceRetryFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        finalFenceRetryClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(finalFenceRetryClient).ConfigureAwait(false));
        using var finalFenceRetryResponse = await SendAsync(
            finalFenceRetryClient,
            finalFenceLossScenario.DeviceId,
            DeviceKey,
            finalFenceLossScenario.Envelope).ConfigureAwait(false);
        finalFenceRetryResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        finalFenceLossDb.ChangeTracker.Clear();
        var finalFenceRetryJobId = await finalFenceLossDb.CentralTransientValidationJobs.AsNoTracking()
            .Where(item => item.SubmissionIdentitySha256 == finalFenceLossScenario.Envelope.SubmissionIdentitySha256)
            .Select(item => item.CentralDerivativeJobId)
            .SingleAsync().ConfigureAwait(false);
        (await finalFenceLossDb.CentralDerivativeJobInputs.CountAsync(item =>
            item.CentralDerivativeJobId == finalFenceRetryJobId).ConfigureAwait(false)).Should().Be(5);

        var unmatchedScenario = await CreateScenarioAsync().ConfigureAwait(false);
        var unmatchedGeometry = unmatchedScenario.Envelope.Candidate.Geometry! with
        {
            Bounds = unmatchedScenario.Envelope.Candidate.Geometry.Bounds with
            {
                X = unmatchedScenario.Envelope.Candidate.Geometry.Bounds.X + 1
            },
            Polyline = unmatchedScenario.Envelope.Candidate.Geometry.Polyline
                .Select(point => point with { X = point.X + 1 }).ToArray()
        };
        var unmatchedEnvelope = CreateEnvelope(Reidentify(unmatchedScenario.Envelope.Candidate) with
        {
            Geometry = unmatchedGeometry
        });
        using var unmatchedResponse = await SendAsync(
            client, unmatchedScenario.DeviceId, DeviceKey, unmatchedEnvelope).ConfigureAwait(false);
        unmatchedResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        Guid unmatchedJobId;
        await using (var unmatchedStateScope = factory.Services.CreateAsyncScope())
        {
            var db = unmatchedStateScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            unmatchedJobId = await db.CentralTransientValidationJobs
                .Where(item => item.SubmissionIdentitySha256 == unmatchedEnvelope.SubmissionIdentitySha256)
                .Select(item => item.CentralDerivativeJobId).SingleAsync().ConfigureAwait(false);
            await db.CentralDerivativeJobs.Where(item => item.Id != unmatchedJobId &&
                    (item.Status == CentralDerivativeJobStatus.Waiting || item.Status == CentralDerivativeJobStatus.Pending))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, CentralDerivativeJobStatus.Canceled)
                    .SetProperty(item => item.StateReasonCode, "integration-test-isolation"))
                .ConfigureAwait(false);
        }
        CentralDerivativeJobLease unmatchedLease;
        await using (var unmatchedClaimScope = factory.Services.CreateAsyncScope())
        {
            unmatchedLease = (await unmatchedClaimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("hybrid-unmatched-worker", TimeSpan.FromMinutes(2), CancellationToken.None)
                .ConfigureAwait(false))!;
        }
        unmatchedLease.JobId.Should().Be(unmatchedJobId);
        await using (var unmatchedExecutionScope = factory.Services.CreateAsyncScope())
        {
            var outcome = await unmatchedExecutionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(unmatchedLease, CancellationToken.None).ConfigureAwait(false);
            outcome.Status.Should().Be(ProcessingOutcomeStatus.Skipped);
        }
        await using var unmatchedAssertionScope = factory.Services.CreateAsyncScope();
        var unmatchedValidation = await unmatchedAssertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .CentralTransientValidationJobs.AsNoTracking().SingleAsync(item =>
                item.CentralDerivativeJobId == unmatchedJobId).ConfigureAwait(false);
        unmatchedValidation.OutcomeState.Should().Be(TransientEventState.NeedsReview);
        unmatchedValidation.OutcomeReasonCode.Should().Be(CentralTransientRuntimeReasonCodes.HybridCandidateNotFound);
    }

    internal static WebApplicationFactory<Program> CreateHybridFactory(Action<IServiceCollection>? configureServices = null)
        => AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CentralTransient:Mode"] = "Hybrid",
                    ["CentralTransient:SourceRole"] = "Raw",
                    ["CentralTransient:StarMaximumMagnitude"] = "-30"
                }));
            if (configureServices is not null)
            {
                builder.ConfigureTestServices(configureServices);
            }
        });

    internal static Task<SubmissionScenario> CreateScenarioAsync(bool multipleCandidates = false)
        => CreateScenarioAsync(8, 6, CameraPixelFormat.Mono16, multipleCandidates);

    internal static async Task<SubmissionScenario> CreateScenarioAsync(
        int width,
        int height,
        CameraPixelFormat pixelFormat,
        bool multipleCandidates = false)
    {
        var deviceId = $"hybrid-submission-{Guid.NewGuid():N}";
        await AssemblyHooks.Fixture.SeedActiveDeviceAsync(deviceId).ConfigureAwait(false);
        var activeDevice = await AssemblyHooks.Fixture.GetActiveDeviceAsync(deviceId).ConfigureAwait(false);
        var epoch = DateTimeOffset.UtcNow.AddMinutes(-10);
        var background = U16(Enumerable.Repeat((ushort)100, width * height).ToArray());
        var targetSamples = Enumerable.Repeat((ushort)100, width * height).ToArray();
        for (var x = 1; x < width - 1; x++)
        {
            targetSamples[(height / 2) * width + x] = 6_000;
            if (multipleCandidates)
            {
                targetSamples[width + x] = 6_000;
            }
        }
        var target = U16(targetSamples);
        var sourceIds = new List<Guid>(5);
        for (var index = 0; index < 5; index++)
        {
            sourceIds.Add(await CentralDerivativeWindowIntegrationTests.SeedAndScheduleSourceAsync(
                deviceId,
                activeDevice.DevicePublicId,
                index + 1,
                epoch,
                index == 2 ? target : background,
                $"{deviceId}-profile",
                width: width,
                height: height,
                pixelFormat: pixelFormat).ConfigureAwait(false));
        }

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifacts = await db.CentralArtifacts
            .Include(item => item.Layout)
            .Include(item => item.Recipe)
            .Include(item => item.Sources)
            .Include(item => item.Frame)!.ThenInclude(item => item!.Timing)
            .Include(item => item.Frame)!.ThenInclude(item => item!.Control)
            .Include(item => item.Frame)!.ThenInclude(item => item!.Profiles)
            .Where(item => sourceIds.Contains(item.Id))
            .OrderBy(item => item.Frame!.CaptureSequence)
            .ToArrayAsync().ConfigureAwait(false);
        var temporalSources = new Dictionary<TransientTemporalPosition, TransientTemporalSource>();
        for (var index = 0; index < artifacts.Length; index++)
        {
            var artifact = artifacts[index];
            var descriptor = CentralReconstructionDescriptorFactory.Create(artifact.Frame!, artifact);
            var payload = index == 2 ? target : background;
            var started = descriptor.Timing.ExposureStartedUtc;
            var ended = ProcessingArtifact.ResolveObservationEndedUtc(
                started, descriptor.Timing.ExposureEndedUtc, descriptor.Controls.EffectiveExposure);
            var processingArtifact = new ProcessingArtifact(
                artifact.ArtifactId,
                artifact.Role,
                artifact.Variant ?? string.Empty,
                ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                artifact.MediaType,
                descriptor.Layout,
                payload,
                started,
                descriptor.Controls.EffectiveExposure,
                LogicHostRecipeExecutionAdapter.CreateCompatibility(descriptor),
                descriptor.Capture.CaptureSequence,
                descriptor.Artifact.SourceArtifactIds,
                started,
                ended);
            var evidence = new TransientSourceEvidenceReferenceV1(
                TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
                artifact.ArtifactId,
                new TransientWholeArtifactLocatorV1(
                    TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                    TransientSourceLocatorKind.WholeArtifact,
                    new TransientArtifactReferenceV1(
                        artifact.ArtifactId,
                        artifact.Role,
                        artifact.Variant ?? string.Empty,
                        processingArtifact.RecipeIdentitySha256,
                        artifact.ChecksumSha256)),
                started,
                ended,
                TransientTimingQuality.Reported,
                new TransientTimingProvenanceV1("raw-ingress-manifest", ArtifactManifestV2.CurrentSchemaVersion));
            var input = TransientDetectorInputFactory.Create(
                processingArtifact,
                evidence,
                new TransientLinearLevelsV1(
                    checked((ushort)descriptor.Layout.BlackLevel!.Value),
                    checked((ushort)descriptor.Layout.WhiteLevel!.Value),
                    checked((ushort)descriptor.Layout.WhiteLevel.Value)));
            input.Validation.IsValid.Should().BeTrue(input.Validation.ReasonCode);
            var detectorInput = input.Input!;
            temporalSources.Add(Positions[index], new TransientTemporalSource(
                Positions[index],
                descriptor.Capture.CaptureSequence,
                detectorInput,
                new TransientSensitivityV1("hybrid-integration-response-v1", 1, 1),
                CreateMasks(detectorInput.Descriptor.Layout.Width, detectorInput.Descriptor.Layout.Height)));
        }
        var center = temporalSources[TransientTemporalPosition.N];
        var context = new[]
        {
            temporalSources[TransientTemporalPosition.NMinus2],
            temporalSources[TransientTemporalPosition.NMinus1]
        };
        var backgroundProduct = TransientTemporalBackgroundFactory.Create(new TransientTemporalBackgroundRequest(
            TransientTemporalBackgroundKind.CausalProvisional,
            center,
            context,
            [],
            TimeSpan.FromSeconds(30)));
        backgroundProduct.Status.Should().Be(TransientTemporalBackgroundStatus.Produced, backgroundProduct.ReasonCode);
        var candidateId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var identitySlots = Enumerable.Range(0, TransientCandidateExtractionProfiles.EdgeV1.MaximumCandidates)
            .Select(index => index == 0
                ? new TransientCandidateIdentitySlot(candidateId, eventId)
                : new TransientCandidateIdentitySlot(Guid.NewGuid(), Guid.NewGuid()))
            .ToArray();
        var sourcesByEvidence = temporalSources.Values.ToDictionary(item => item.Input.Descriptor.Source.EvidenceId);
        var extraction = TransientCandidateExtractionFactory.Create(new TransientCandidateExtractionRequest(
            deviceId,
            temporalSources.Values.Max(item => item.Input.Descriptor.Source.ObservationEndedUtc).AddTicks(1),
            center,
            backgroundProduct.Product!,
            backgroundProduct.Product!.Descriptor.Sources.Select(item => sourcesByEvidence[item.EvidenceId]).ToArray(),
            identitySlots,
            TransientCandidateExtractionProfiles.EdgeV1,
            CenteredContextConverged: false));
        extraction.Status.Should().Be(TransientCandidateExtractionStatus.Produced, extraction.ReasonCode);
        extraction.Candidates.Should().HaveCount(multipleCandidates ? 2 : 1);
        var envelopes = extraction.Candidates.Select(CreateEnvelope).ToArray();
        var envelope = multipleCandidates ? envelopes[^1] : envelopes.Single();
        return new SubmissionScenario(deviceId, sourceIds, envelope, envelopes);
    }

    internal static TransientCandidateSubmissionEnvelopeV1 CreateEnvelope(TransientCandidateV1 candidate)
    {
        var envelope = new TransientCandidateSubmissionEnvelopeV1(
            TransientCandidateSubmissionEnvelopeV1.CurrentSchemaVersion,
            candidate.CandidateId,
            candidate.EventId,
            candidate,
            candidate.Extraction.RecipeIdentitySha256,
            candidate.Provenance.ProcessingProfileIdentity,
            new string('0', 64));
        return envelope with
        {
            SubmissionIdentitySha256 = TransientCandidateDeliveryJson.ComputeSubmissionIdentitySha256(envelope)
        };
    }

    internal static TransientCandidateV1 Reidentify(TransientCandidateV1 candidate)
    {
        var candidateId = Guid.NewGuid();
        return candidate with
        {
            CandidateId = candidateId,
            EventId = Guid.NewGuid(),
            Extraction = candidate.Extraction with { OriginatingCandidateId = candidateId }
        };
    }

    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string deviceId,
        string deviceKey,
        TransientCandidateSubmissionEnvelopeV1 envelope,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/device/transient-candidates");
        request.Headers.Add("X-HVO-Device-Id", deviceId);
        request.Headers.Add("X-HVO-Device-Key", deviceKey);
        request.Headers.Add("Idempotency-Key", envelope.SubmissionIdentitySha256);
        request.Content = new ByteArrayContent(TransientCandidateDeliveryJson.Serialize(envelope));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static TransientCandidateSubmissionAcknowledgementV1 ParseAcknowledgement(byte[] payload)
        => TransientCandidateDeliveryJson.ParseAcknowledgement(payload).Value
           ?? throw new InvalidDataException("LogicHost returned an invalid acknowledgement.");

    internal static async Task<string> GetSystemTokenAsync(HttpClient client)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = TestClients.SystemCameraAgent.ClientId,
            ["client_secret"] = TestClients.SystemCameraAgent.ClientSecret,
            ["scope"] = string.Join(' ', TestClients.SystemCameraAgent.Scopes)
        });
        using var response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), content)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

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
            new ProcessingAlgorithmIdentity($"hybrid-{kind.ToString().ToUpperInvariant()}-mask", "v1"),
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

    internal sealed record SubmissionScenario(
        string DeviceId,
        IReadOnlyList<Guid> CentralArtifactIds,
        TransientCandidateSubmissionEnvelopeV1 Envelope,
        IReadOnlyList<TransientCandidateSubmissionEnvelopeV1> Envelopes);

    private sealed class TimeoutObjectReader : ICentralArtifactObjectReader
    {
        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
            => Task.FromException<CentralArtifactObjectSnapshot>(new CentralArtifactStorageException());

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken)
            => Task.FromException<bool>(new CentralArtifactStorageException());

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
            => Task.FromException(new CentralArtifactStorageException());
    }

    private sealed class GenerationTimeoutObjectReader(ICentralArtifactObjectReader inner)
        : ICentralArtifactObjectReader
    {
        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
            => inner.VerifyAsync(artifact, cancellationToken);

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken)
            => Task.FromException<bool>(new CentralArtifactStorageException());

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
            => inner.CopyToAsync(snapshot, destination, range, cancellationToken);
    }

    private sealed class StaleGenerationObjectReader(ICentralArtifactObjectReader inner)
        : ICentralArtifactObjectReader
    {
        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
            => inner.VerifyAsync(artifact, cancellationToken);

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
            => inner.CopyToAsync(snapshot, destination, range, cancellationToken);
    }

    private sealed class LockLossObjectReader(
        ICentralArtifactObjectReader inner,
        ApplicationDbContext dbContext) : ICentralArtifactObjectReader
    {
        private int generationCalls;

        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
            => inner.VerifyAsync(artifact, cancellationToken);

        public async Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken)
        {
            var isCurrent = await inner.IsCurrentGenerationAsync(artifact, storageETag, cancellationToken)
                .ConfigureAwait(false);
            if (Interlocked.Increment(ref generationCalls) == 5)
            {
                await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
            return isCurrent;
        }

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
            => inner.CopyToAsync(snapshot, destination, range, cancellationToken);
    }

    private sealed class ThrowingSubmissionFaultInjector(CentralTransientSubmissionFaultStage requestedStage)
        : ICentralTransientSubmissionFaultInjector
    {
        private int injected;

        public void ThrowIfRequested(CentralTransientSubmissionFaultStage stage)
        {
            if (stage == requestedStage && Interlocked.Exchange(ref injected, 1) == 0)
            {
                throw new CentralTransientSubmissionFaultException(stage);
            }
        }
    }

    private sealed class ClosingSubmissionConnectionFaultInjector(ApplicationDbContext dbContext)
        : ICentralTransientSubmissionFaultInjector
    {
        public void ThrowIfRequested(CentralTransientSubmissionFaultStage stage)
        {
            if (stage == CentralTransientSubmissionFaultStage.BeforeFinalFenceCheck)
            {
                dbContext.Database.GetDbConnection().Close();
            }
        }
    }
}
