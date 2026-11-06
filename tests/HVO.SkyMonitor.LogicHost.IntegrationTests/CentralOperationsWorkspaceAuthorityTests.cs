using FluentAssertions;
using System.Security.Claims;
using System.Text;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CentralOperationsWorkspaceAuthorityTests
{
    [TestMethod]
    public async Task ScopedJobRecoveryUsesRealAuthorityFencesAndPreservesSameJobAttemptHistory()
    {
        var data = await CameraOperationsSeed.CreateAsync(2, 1, 1, 1);
        await data.AddMemberAsync(TestUsers.Viewer.Email, ObservatoryMembershipRole.Manager);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var manager = await db.Users.Where(u => u.Email == TestUsers.Viewer.Email).Select(u => u.Id).SingleAsync();
        var principal = CameraOperationsSeed.Principal(manager);
        var read = services.GetRequiredService<ICentralOperationsWorkspaceService>();
        var mutations = services.GetRequiredService<ICentralOperationsWorkspaceMutationService>();
        var selection = new CentralOperationsQuery { View = "jobs", Job = data.InspectedJob };
        var original = (await read.ReadAsync(principal, data.Observatory, data.Camera, selection))!.Job!;
        var cancel = new CentralWorkspaceCommand(CentralWorkspaceAction.CancelJob, selection, "cancel-job");
        (await mutations.ExecuteAsync(principal, data.Observatory, data.OtherCamera, cancel)).Should().Be(CentralOperationsWorkspaceMutationService.Denied);
        (await mutations.ExecuteAsync(principal, data.Observatory, data.Camera, cancel)).Should().Contain("cancellation was recorded");
        db.ChangeTracker.Clear();
        var canceled = await db.CentralDerivativeJobs.AsNoTracking().SingleAsync(j => j.Id == data.InspectedJob);
        canceled.Status.Should().Be(CentralDerivativeJobStatus.Canceled);
        canceled.CancellationRequestedBy.Should().Be("user:" + manager);
        (await mutations.ExecuteAsync(principal, data.Observatory, data.Camera,
            cancel with { Action = CentralWorkspaceAction.RetryJob })).Should().Contain("same job for a new attempt");
        db.ChangeTracker.Clear();
        var requeued = await read.ReadAsync(principal, data.Observatory, data.Camera, selection);
        requeued!.Job!.Job.JobId.Should().Be(original.Job.JobId);
        requeued.Job.Attempts.Should().BeEquivalentTo(original.Attempts);
        requeued.Job.Job.AttemptCount.Should().Be(original.Job.AttemptCount);
        requeued.Job.Job.MaxAttempts.Should().BeGreaterThan(original.Job.MaxAttempts);
        // The rendered affordance can race a terminal transition. The durable operation rejects it.
        await db.CentralDerivativeJobs.Where(j => j.Id == data.InspectedJob).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, CentralDerivativeJobStatus.Completed));
        (await mutations.ExecuteAsync(principal, data.Observatory, data.Camera, cancel)).Should().Contain("changed state");
        await db.ObservatoryMemberships.Where(m => m.UserId == manager && m.ObservatoryId == data.Observatory).ExecuteDeleteAsync();
        (await mutations.ExecuteAsync(principal, data.Observatory, data.Camera, cancel)).Should().Be(CentralOperationsWorkspaceMutationService.Denied);
        (await db.CentralDerivativeJobAttempts.CountAsync(a => a.CentralDerivativeJobId == data.InspectedJob)).Should().Be(2);
    }

    [TestMethod]
    public async Task ArtifactPublicationIsExactAuditedAndRejectsForeignIdentitiesAndContinuationReuse()
    {
        var data = await CameraOperationsSeed.CreateAsync(2, 1, 1, 1);
        var foreign = await CameraOperationsSeed.CreateAsync(2, 1, 1, 1);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var source = await db.CentralArtifacts.Include(a => a.Frame).SingleAsync(a => a.ArtifactId == data.InspectedJobSourceArtifactId);
        var previews = new List<CentralArtifact>();
        for (var i = 0; i < 27; i++)
        {
            var preview = new CentralArtifact
            {
                CentralFrameId = source.CentralFrameId,
                DevicePublicId = source.DevicePublicId,
                ArtifactId = Guid.NewGuid(),
                Role = FrameArtifactRole.Preview,
                Variant = $"preview-{i}",
                RecipeVersion = "preview-v1",
                ManifestSchemaVersion = "v2",
                MediaType = "image/png",
                ByteLength = 1,
                ChecksumSha256 = new string('a', 64),
                StorageReference = $"object://skymonitor-artifacts/workspace-{Guid.NewGuid():N}",
                ReceivedAtUtc = DateTimeOffset.UtcNow,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete
            };
            db.Add(preview); previews.Add(preview);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var read = services.GetRequiredService<ICentralOperationsWorkspaceService>();
        var mutations = services.GetRequiredService<ICentralOperationsWorkspaceMutationService>();
        var principal = CameraOperationsSeed.Principal(data.OwnerId);
        var selection = new CentralOperationsQuery { View = "artifacts", Capture = source.CentralFrameId };
        var page = await read.ReadAsync(principal, data.Observatory, data.Camera, selection);
        page!.Artifacts!.Items.Should().HaveCount(25);
        page.Artifacts.NextCursor.Should().NotBeNull();
        var artifact = page.Artifacts.Items.First(a => a.IsPubliclyEligible);
        var command = new CentralWorkspaceCommand(CentralWorkspaceAction.ReleaseArtifact, selection, "release-artifact", Artifact: artifact.ArtifactId);
        (await mutations.ExecuteAsync(principal, data.Observatory, data.Camera, command with { Artifact = foreign.InspectedJobSourceArtifactId }))
            .Should().Be(CentralOperationsWorkspaceMutationService.Denied);
        (await mutations.ExecuteAsync(principal, data.Observatory, data.OtherCamera, command)).Should().Be(CentralOperationsWorkspaceMutationService.Denied);
        (await mutations.ExecuteAsync(principal, data.Observatory, data.Camera, command)).Should().Contain("exact artifact was recorded");
        var decisions = await db.PublicRecordPublicationDecisions.AsNoTracking().Where(d => d.AuthorityObservatoryId == data.Observatory && d.SubjectKind == PublicRecordSubjectKind.Artifact).ToArrayAsync();
        decisions.Should().ContainSingle().Which.CentralArtifactId.Should().Be(artifact.PublicationSubjectId);
        decisions[0].ActorUserId.Should().Be(data.OwnerId);
        (await db.CentralArtifacts.AsNoTracking().Where(a => previews.Select(p => p.Id).Contains(a.Id)).Select(a => a.ChecksumSha256).Distinct().SingleAsync())
            .Should().Be(new string('a', 64));
        var foreignCapture = await db.CentralDerivativeJobs.AsNoTracking().Where(j => j.Id == foreign.InspectedJob).Select(j => j.SourceArtifact!.CentralFrameId).SingleAsync();
        await FluentActions.Awaiting(() => read.ReadAsync(principal, foreign.Observatory, foreign.Camera,
            selection with { Capture = foreignCapture, Page = page.Artifacts.NextCursor })).Should().ThrowAsync<ArgumentException>();
        // Same observatory, another exact capture must also reject the protected UI continuation.
        var otherCapture = await db.CentralFrames.AsNoTracking().Where(f => f.LogicalCameraInstallation!.LogicalCameraId == data.Camera && f.Id != source.CentralFrameId).Select(f => f.Id).FirstAsync();
        await FluentActions.Awaiting(() => read.ReadAsync(principal, data.Observatory, data.Camera,
            selection with { Capture = otherCapture, Page = page.Artifacts.NextCursor })).Should().ThrowAsync<ArgumentException>();
    }

    [TestMethod]
    public async Task PolicyMutationRequiresOwnerAndGlobalCatalogAuthorityNeverGrantsPrivateScope()
    {
        var data = await CameraOperationsSeed.CreateAsync(2, 1, 1, 1);
        await data.AddMemberAsync(TestUsers.Viewer.Email, ObservatoryMembershipRole.Manager);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var read = services.GetRequiredService<ICentralOperationsWorkspaceService>();
        var mutations = services.GetRequiredService<ICentralOperationsWorkspaceMutationService>();
        var managerId = await db.Users.Where(u => u.Email == TestUsers.Viewer.Email).Select(u => u.Id).SingleAsync();
        var command = new CentralWorkspaceCommand(CentralWorkspaceAction.SavePolicy, new() { View = "policy" }, "policy", Policy: new(600_000, null, "ignored-client-reason"));
        (await mutations.ExecuteAsync(CameraOperationsSeed.Principal(managerId), data.Observatory, data.Camera, command)).Should().Be(CentralOperationsWorkspaceMutationService.Denied);
        (await mutations.ExecuteAsync(CameraOperationsSeed.Principal(data.OwnerId), data.Observatory, data.Camera, command)).Should().Contain("future central jobs");
        var version = await db.CentralProcessingOverrideVersions.AsNoTracking().SingleAsync(p => p.ObservatoryId == data.Observatory);
        version.ActorUserId.Should().Be(data.OwnerId);
        version.ReasonCode.Should().Be("central-workspace-policy");
        var revision = await db.CentralProcessingGraphRevisions.AsNoTracking().Select(r => r.Id).FirstAsync();
        var retire = new CentralWorkspaceCommand(CentralWorkspaceAction.RetireRevision, new() { View = "catalog", Revision = revision }, "retire");
        (await mutations.ExecuteAsync(CameraOperationsSeed.Principal(data.OwnerId), data.Observatory, data.Camera, retire)).Should().Be(CentralOperationsWorkspaceMutationService.Denied);
        var definitionJson = await db.CentralProcessingGraphRevisions.Where(r => r.Id == revision).Select(r => r.DefinitionJson).SingleAsync();
        var definition = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(definitionJson)).Definition! with { Name = "workspace-" + Guid.NewGuid().ToString("N") };
        var catalog = services.GetRequiredService<IProcessingGraphCatalogService>();
        var created = await catalog.CreateRevisionAsync(definition, data.OwnerId, true, CancellationToken.None);
        created.Outcome.Should().Be(CentralProcessingGraphMutationOutcome.Applied);
        var editor = CameraOperationsSeed.Principal(data.OwnerId);
        ((ClaimsIdentity)editor.Identity!).AddClaim(new Claim(ClaimTypes.Role, AuthorizationRoleNames.PlatformEditor));
        var exactRevision = retire with { Selection = new() { View = "catalog", Revision = created.Value!.Id } };
        (await mutations.ExecuteAsync(editor, data.Observatory, data.Camera, exactRevision with { Action = CentralWorkspaceAction.PublishRevision })).Should().Contain("Applied");
        (await mutations.ExecuteAsync(editor, data.Observatory, data.Camera, exactRevision)).Should().Contain("Applied");
        (await catalog.GetRevisionAsync(created.Value.Id, CancellationToken.None))!.Lifecycle.Should().Be(CentralProcessingGraphLifecycle.Retired);
        var adminId = await db.Users.Where(u => u.Email == TestUsers.Admin.Email).Select(u => u.Id).SingleAsync();
        (await read.ReadAsync(CameraOperationsSeed.Principal(adminId), data.Observatory, data.Camera, new())).Should().BeNull();
    }
    [TestMethod]
    public async Task EventReviewUsesAllContributorAuthorityAndExactPreconditionsThroughProductionServices()
    {
        var data = await CameraOperationsSeed.CreateAsync(2, 1, 1, 1);
        await data.AddMemberAsync(TestUsers.Viewer.Email, ObservatoryMembershipRole.Manager);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var registration = await db.LogicalCameraInstallations.Where(i => i.Id == data.CurrentInstallation).Select(i => i.RegistrationId).SingleAsync();
        var fixture = CentralTransientPersistenceFixture.Create();
        var seeded = await CentralTransientEventPersistenceIntegrationTests.SeedAsync(db, fixture,
            registrationId: registration, logicalCameraInstallationId: data.CurrentInstallation);
        await new CentralTransientEventPersistence(db).AppendAsync(fixture.Request with { CentralDerivativeJobId = seeded.JobId }, CancellationToken.None);
        db.ChangeTracker.Clear();
        var eventId = await db.CentralTransientEvents.Where(e => e.EventId == fixture.Event.EventId).Select(e => e.Id).SingleAsync();
        var managerId = await db.Users.Where(u => u.Email == TestUsers.Viewer.Email).Select(u => u.Id).SingleAsync();
        var manager = CameraOperationsSeed.Principal(managerId);
        var read = services.GetRequiredService<ICentralOperationsWorkspaceService>();
        var mutations = services.GetRequiredService<ICentralOperationsWorkspaceMutationService>();
        var query = new CentralOperationsQuery { View = "events", Event = eventId };
        var snapshot = await read.ReadAsync(manager, data.Observatory, data.Camera, query);
        snapshot!.CanReviewEvent.Should().BeTrue();
        var summary = snapshot.Event!.Summary;
        var command = new CentralWorkspaceCommand(CentralWorkspaceAction.ConfirmEvent, query, "workspace-review-exact", summary.ETag, summary.ActiveAssessmentId);
        (await mutations.ExecuteAsync(manager, data.Observatory, data.OtherCamera, command)).Should().Be(CentralOperationsWorkspaceMutationService.Denied);
        (await mutations.ExecuteAsync(manager, data.Observatory, data.Camera, command)).Should().Contain("exact assessment");
        (await mutations.ExecuteAsync(manager, data.Observatory, data.Camera, command)).Should().Contain("exact assessment");
        (await mutations.ExecuteAsync(manager, data.Observatory, data.Camera,
            command with { Action = CentralWorkspaceAction.RejectEvent, IdempotencyKey = "workspace-review-stale" })).Should().Contain("stale If-Match");
        var foreign = await CameraOperationsSeed.CreateAsync(2, 1, 1, 1);
        var foreignRegistration = await db.LogicalCameraInstallations.Where(i => i.Id == foreign.CurrentInstallation).Select(i => i.RegistrationId).SingleAsync();
        var cross = CentralTransientPersistenceFixture.Create();
        var sourceRegistrations = cross.Extraction.OrderedSources.Select((_, index) => index == 2 ? registration : foreignRegistration).ToArray();
        var sourceInstallations = cross.Extraction.OrderedSources.Select((_, index) => index == 2 ? data.CurrentInstallation : foreign.CurrentInstallation).ToArray();
        var crossSeed = await CentralTransientEventPersistenceIntegrationTests.SeedAsync(db, cross,
            registrationIds: sourceRegistrations, logicalCameraInstallationIds: sourceInstallations);
        await new CentralTransientEventPersistence(db).AppendAsync(cross.Request with { CentralDerivativeJobId = crossSeed.JobId }, CancellationToken.None);
        db.ChangeTracker.Clear();
        var crossId = await db.CentralTransientEvents.Where(e => e.EventId == cross.Event.EventId).Select(e => e.Id).SingleAsync();
        var crossQuery = new CentralOperationsQuery { View = "events", Event = crossId };
        (await read.ReadAsync(manager, data.Observatory, data.Camera, crossQuery)).Should().BeNull("the manager cannot read the other contributor observatory");
        var both = await read.ReadAsync(CameraOperationsSeed.Principal(data.OwnerId), data.Observatory, data.Camera, crossQuery);
        both!.Event!.Summary.CentralTransientEventId.Should().Be(crossId);
        (await mutations.ExecuteAsync(manager, data.Observatory, data.Camera,
            command with { Selection = crossQuery })).Should().Be(CentralOperationsWorkspaceMutationService.Denied);
        await db.ObservatoryMemberships.Where(m => m.UserId == managerId && m.ObservatoryId == data.Observatory)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ObservatoryMembershipRole.Viewer));
        var viewer = await read.ReadAsync(manager, data.Observatory, data.Camera, query);
        viewer!.CanReviewEvent.Should().BeFalse();
        (await mutations.ExecuteAsync(manager, data.Observatory, data.Camera, command)).Should().Be(CentralOperationsWorkspaceMutationService.Denied);
    }

}
