using System.Security.Claims;
using FluentAssertions;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using static HVO.SkyMonitor.Tests.LogicHost.Services.CameraOperationsReadServiceTests;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralOperationsWorkspaceServiceTests
{
    [TestMethod]
    public async Task ExactEventRetainsBoundedReviewNotificationAndNestedEvidenceWithoutChangingCanonicalState()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Manager);
        var original = CentralWorkspaceEventHistory.Create(world.A.Event);
        var before = HVO.SkyMonitor.Processing.TransientContractJson.Serialize(original.Event);
        var events = new EventRead(original);
        var read = new CentralOperationsWorkspaceService(world.Db, world.Service(), null!, null!, null!, events, new Reviews(),
            TimeProvider.System, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var snapshot = await read.ReadAsync(Member(), world.A.Observatory, world.A.Camera, new() { View = "events", Event = world.A.Event });
        snapshot!.Event!.Summary.Should().Be(original.Summary);
        snapshot.CanReviewEvent.Should().BeTrue();
        var json = System.Text.Json.JsonSerializer.SerializeToElement(snapshot.Event);
        Console.WriteLine($"Retained event projection: {json.GetRawText().Length} characters; canonical: {before.Length} bytes; 1000 reviews and 1000 notifications.");
        json.GetRawText().Length.Should().BeLessThan(80_000, "retained event children must be bounded before reaching the renderer");
        CentralWorkspaceEventHistory.AssertBounds(json);
        var evidence = json.GetProperty("Evidence");
        evidence.GetProperty("Reviews").GetProperty("Truncated").GetBoolean().Should().BeTrue();
        evidence.GetProperty("Notifications").GetProperty("Truncated").GetBoolean().Should().BeTrue();
        evidence.GetProperty("CurrentAssessment").GetProperty("AssessmentId").GetGuid().Should().Be(original.Summary.ActiveAssessmentId);
        evidence.GetProperty("CurrentAssessment").GetProperty("ConfidenceMillionths").GetInt32().Should().Be(original.Event.Assessments[^1].ConfidenceMillionths);
        evidence.GetProperty("CurrentAssessment").GetProperty("RecipeIdentitySha256").GetString().Should().Be(original.Event.Assessments[^1].RecipeIdentitySha256);
        evidence.GetProperty("CurrentAssessment").GetProperty("Reasons").GetProperty("Truncated").GetBoolean().Should().BeTrue();
        json.GetRawText().Should().Contain(original.Event.Reviews[^1].ReviewId.ToString()).And.Contain(original.Event.Notifications[^1].NotificationId.ToString())
            .And.NotContain(original.Event.Reviews[0].ReviewId.ToString()).And.NotContain(original.Event.Notifications[0].NotificationId.ToString());
        HVO.SkyMonitor.Processing.TransientContractJson.Serialize(original.Event).Should().Equal(before);
        events.Calls.Should().Be(1, "the workspace must not drain event history");
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(25, false)]
    [DataRow(26, true)]
    public void ExactEventChildTruncationDistinguishesEmptyFullAndOverflow(int count, bool truncated)
    {
        var original = CentralWorkspaceEventHistory.Create(Guid.NewGuid());
        var detail = original with { Event = original.Event with { Reviews = original.Event.Reviews.Take(count).ToArray(), Notifications = original.Event.Notifications.Take(count).ToArray() } };
        var projection = CentralWorkspaceEventDetail.Create(detail);
        foreach (var name in new[] { "Reviews", "Notifications" })
        {
            projection.Evidence.GetProperty(name).GetProperty("Truncated").GetBoolean().Should().Be(truncated);
            projection.Evidence.GetProperty(name).GetProperty("Items").GetArrayLength().Should().Be(Math.Min(count, 25));
        }
        projection.Summary.Should().Be(original.Summary);
        projection.Evidence.GetProperty("EventIdentity").GetProperty("SourceSchemaVersion").GetString().Should().Be(original.Event.SchemaVersion);
        projection.Evidence.TryGetProperty("SchemaVersion", out _).Should().BeFalse("a display projection must not advertise itself as a canonical event");
    }

    private sealed class EventRead(CentralTransientEventDetail detail) : ICentralTransientEventReadService
    {
        internal int Calls { get; private set; }
        public Task<CentralTransientEventDetail?> GetAsync(ClaimsPrincipal principal, Guid centralTransientEventId, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult<CentralTransientEventDetail?>(detail); }
        public Task<CentralTransientEventPage> ListAsync(ClaimsPrincipal principal, int take, string? cursor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CentralTransientPublicationAuthority>> ListPublicationAuthoritiesAsync(ClaimsPrincipal principal, IReadOnlyCollection<Guid> centralTransientEventIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [TestMethod]
    [DataRow("Viewer")]
    [DataRow("Manager")]
    [DataRow("Owner")]
    public async Task RealCameraReadsComposeExactBoundedHistoryForEveryHumanRole(string roleName)
    {
        var role = Enum.Parse<ObservatoryMembershipRole>(roleName);
        await using var world = await World.CreateAsync(role);
        var read = Read(world);
        var a = world.A;
        var execution = await read.ReadAsync(Member(), a.Observatory, a.Camera, new() { Execution = a.Execution });
        execution!.Role.Should().Be(role);
        execution.Executions!.Items.Should().HaveCount(2);
        execution.Execution!.Execution.Id.Should().Be(a.Execution);
        execution.Execution.Nodes.Items.Should().HaveCount(2);
        var job = await read.ReadAsync(Member(), a.Observatory, a.Camera, new() { View = "jobs", Job = a.Job });
        job!.Job!.Job.JobId.Should().Be(a.Job);
        job.Job.Attempts.Items.Should().HaveCount(2);
        job.Job.Inputs.Items.Should().HaveCount(2);
        job.Job.Inputs.Items[1].ArtifactId.Should().BeNull("foreign lineage is concealed by the durable owner");
        var proposal = await read.ReadAsync(Member(), a.Observatory, a.Camera, new() { View = "delivery", Proposal = a.Proposal });
        proposal!.Proposal!.LogicalCameraInstallationId.Should().Be(a.RetiredInstallation.Id);
        proposal.Facts!.Items.Should().NotBeEmpty();
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { execution, job, proposal });
        json.Should().NotContain(World.LeaseToken.ToString()).And.NotContain("LeaseToken").And.NotContain("LeaseOwner");
        world.Db.ChangeTracker.Entries().Should().BeEmpty();
    }

    [TestMethod]
    public async Task CredentialAndPathSubstitutionsCannotReadCameraOrExactPrivateSubjects()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Owner);
        var read = Read(world);
        var a = world.A; var b = world.B;
        foreach (var selection in new CentralOperationsQuery[]
        {
            new(), new() { View = "jobs" }, new() { View = "delivery" }, new() { View = "catalog" },
            new() { Execution = a.Execution }, new() { View = "jobs", Job = a.Job }, new() { View = "delivery", Proposal = a.Proposal }
        })
        {
            (await read.ReadAsync(Principal("b-only"), a.Observatory, a.Camera, selection)).Should().BeNull();
            (await read.ReadAsync(Member(), a.Observatory, b.Camera, selection)).Should().BeNull();
            (await read.ReadAsync(ApiKey(a.Observatory), b.Observatory, b.Camera, selection)).Should().BeNull();
            (await read.ReadAsync(new ClaimsPrincipal(), a.Observatory, a.Camera, selection)).Should().BeNull();
        }
        foreach (var selection in new CentralOperationsQuery[] { new() { Execution = b.Execution },
            new() { View = "jobs", Job = b.Job }, new() { View = "delivery", Proposal = b.Proposal }, new() { View = "events", Event = b.Event } })
            (await read.ReadAsync(Member(), a.Observatory, a.Camera, selection)).Should().BeNull();
        world.Db.ObservatoryMemberships.RemoveRange(world.Db.ObservatoryMemberships.Where(m => m.UserId == "member"));
        await world.Db.SaveChangesAsync();
        (await read.ReadAsync(Member(), a.Observatory, a.Camera, new())).Should().BeNull();
    }

    [TestMethod]
    public async Task JobAuthorityAndStateRacesRemainWithExistingAuditedService()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Manager);
        var jobs = new Jobs();
        var mutation = Mutation(Read(world), jobs);
        var a = world.A;
        var command = new CentralWorkspaceCommand(CentralWorkspaceAction.RetryJob, new() { View = "jobs", Job = a.Job }, "request");
        (await mutation.ExecuteAsync(Member(), a.Observatory, a.Camera, command)).Should().Contain("new attempt");
        jobs.Calls.Should().ContainSingle().Which.Should().Be(("retry", "member", a.Job));
        jobs.Outcome = NetworkOperationsMutationOutcome.InvalidState;
        (await mutation.ExecuteAsync(Member(), a.Observatory, a.Camera, command)).Should().Contain("changed state");
        (await mutation.ExecuteAsync(Member(), a.Observatory, a.Camera, command with { Selection = new() { View = "jobs", Job = world.B.Job } }))
            .Should().Be(CentralOperationsWorkspaceMutationService.Denied);
        (await mutation.ExecuteAsync(ApiKey(a.Observatory), a.Observatory, a.Camera, command)).Should().Be(CentralOperationsWorkspaceMutationService.Denied);
        jobs.Calls.Should().HaveCount(2);
        world.Db.ObservatoryMemberships.Single(m => m.UserId == "member" && m.ObservatoryId == a.Observatory).Role = ObservatoryMembershipRole.Viewer;
        await world.Db.SaveChangesAsync();
        (await mutation.ExecuteAsync(Member(), a.Observatory, a.Camera, command)).Should().Be(CentralOperationsWorkspaceMutationService.Denied);
        jobs.Calls.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task ReplayAdapterUsesOnlyFrozenExactSourcesAndPreservesRequestKey()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Owner);
        var executions = new Executions();
        var mutation = Mutation(Read(world), new Jobs(), executions);
        var a = world.A;
        var original = await world.Service().GetExecutionAsync(Member(), a.Observatory, a.Camera, a.Execution, 25, null);
        var command = new CentralWorkspaceCommand(CentralWorkspaceAction.ReplayExecution, new() { Execution = a.Execution }, "same-replay-key");
        await mutation.ExecuteAsync(Member(), a.Observatory, a.Camera, command);
        await mutation.ExecuteAsync(Member(), a.Observatory, a.Camera, command);
        executions.Requests.Should().HaveCount(2);
        executions.Requests.Should().OnlyContain(r => r.Revision == original!.Execution.RevisionId && r.Key == "same-replay-key" && r.Scope == a.Observatory);
        executions.Requests[0].Sources.Should().Equal(original!.Sources.OrderBy(s => s.Ordinal).Select(s => s.ArtifactId));
        (await world.Service().GetExecutionAsync(Member(), a.Observatory, a.Camera, a.Execution, 25, null)).Should().BeEquivalentTo(original);
    }

    [TestMethod]
    public async Task ReadableExecutionCancellationKeepsForbiddenDistinctFromConcealedNotFound()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Viewer);
        var executions = new Executions { Cancellation = CentralProcessingGraphCancellationOutcome.Forbidden };
        var mutation = Mutation(Read(world), new Jobs(), executions);
        var a = world.A;
        var command = new CentralWorkspaceCommand(CentralWorkspaceAction.CancelExecution, new() { Execution = a.Execution }, "cancel");
        (await mutation.ExecuteAsync(Member(), a.Observatory, a.Camera, command)).Should().StartWith("Forbidden:");
        (await mutation.ExecuteAsync(Member(), a.Observatory, a.Camera, command with { Selection = new() { Execution = world.B.Execution } }))
            .Should().Be(CentralOperationsWorkspaceMutationService.Denied);
    }

    [TestMethod]
    public async Task EventReviewForwardsRenderedStrongPreconditionAndKeyEvenWhenReadHasAdvanced()
    {
        var eventId = Guid.NewGuid();
        var assessment = Guid.NewGuid();
        var etag = CentralTransientEventEtag.Create([0, 0, 0, 0, 0, 0, 0, 1]);
        var summary = new CentralTransientEventSummary(eventId, Guid.NewGuid(), Guid.NewGuid(), "agent", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, HVO.SkyMonitor.Processing.TransientEventState.Provisional,
            CentralTransientReviewState.NeedsReview, Guid.NewGuid(), HVO.SkyMonitor.Processing.TransientClassification.Meteor, null, 500_000,
            CentralTransientEventEtag.Create([0, 0, 0, 0, 0, 0, 0, 2]));
        var read = new SnapshotRead(new(ObservatoryMembershipRole.Manager, false, Event: new(summary, default), CanReviewEvent: true));
        var reviews = new Reviews();
        var mutation = new CentralOperationsWorkspaceMutationService(read, null!, null!, reviews, null!, null!, null!, TimeProvider.System);
        var command = new CentralWorkspaceCommand(CentralWorkspaceAction.ConfirmEvent, new() { View = "events", Event = eventId }, "same-event-request", etag, assessment);
        (await mutation.ExecuteAsync(Member(), Guid.NewGuid(), Guid.NewGuid(), command)).Should().Contain("stale If-Match");
        reviews.Version.Should().Equal([0, 0, 0, 0, 0, 0, 0, 1]);
        reviews.Request!.AssessmentId.Should().Be(assessment);
        reviews.Key.Should().Be("same-event-request");
        read.Result = read.Result with { CanReviewEvent = false };
        (await mutation.ExecuteAsync(Member(), Guid.NewGuid(), Guid.NewGuid(), command)).Should().Be(CentralOperationsWorkspaceMutationService.Denied);
        reviews.Calls.Should().Be(1);
    }

    private sealed class SnapshotRead(CentralOperationsSnapshot result) : ICentralOperationsWorkspaceService
    {
        internal CentralOperationsSnapshot Result { get; set; } = result;
        public Task<CentralOperationsSnapshot?> ReadAsync(ClaimsPrincipal principal, Guid observatory, Guid camera, CentralOperationsQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<CentralOperationsSnapshot?>(Result);
    }
    private sealed class Reviews : ICentralTransientReviewService
    {
        internal byte[]? Version { get; private set; }
        internal CentralTransientReviewRequest? Request { get; private set; }
        internal string? Key { get; private set; }
        internal int Calls { get; private set; }
        public Task<IReadOnlySet<Guid>> ListReviewableEventIdsAsync(ClaimsPrincipal principal, IReadOnlyCollection<Guid> centralTransientEventIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<Guid>>(centralTransientEventIds.ToHashSet());
        public Task<CentralTransientReviewMutationResult> ReviewAsync(ClaimsPrincipal principal, Guid centralTransientEventId, byte[] expectedRowVersion, string idempotencyKey, CentralTransientReviewRequest request, CancellationToken cancellationToken)
        { Calls++; Version = expectedRowVersion; Request = request; Key = idempotencyKey; return Task.FromResult(new CentralTransientReviewMutationResult(CentralTransientReviewMutationStatus.PreconditionFailed)); }
    }

    internal static CentralOperationsWorkspaceService Read(World world, CentralTransientEventDetail? eventDetail = null) => new(world.Db, world.Service(), null!, null!, null!,
        eventDetail is null ? null! : new EventRead(eventDetail), new Reviews(), TimeProvider.System, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
    internal static ClaimsPrincipal Member() => Principal("member");
    internal static ClaimsPrincipal Principal(string user) => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, user), new("account_type", "User")], "Identity.Application"));
    private static ClaimsPrincipal ApiKey(Guid observatory) => new(new ClaimsIdentity([
        new(ClaimTypes.NameIdentifier, "member"), new("account_type", "User"),
        new(ApiKeyClaims.AuthenticationType, ApiKeyAuthenticationOptions.AuthenticationScheme),
        new(ApiKeyClaims.AccessLevel, "Read"), new(ApiKeyClaims.ObservatoryId, observatory.ToString("D"))], ApiKeyAuthenticationOptions.AuthenticationScheme));
    private static CentralOperationsWorkspaceMutationService Mutation(ICentralOperationsWorkspaceService read, Jobs jobs, Executions? executions = null)
        => new(read, jobs, executions!, null!, null!, null!, null!, TimeProvider.System);

    private sealed class Jobs : INetworkOperationsMutationService
    {
        internal List<(string Action, string Actor, Guid Job)> Calls { get; } = [];
        internal NetworkOperationsMutationOutcome Outcome { get; set; } = NetworkOperationsMutationOutcome.Applied;
        private Task<NetworkOperationsMutationOutcome> Call(string action, string actor, Guid job)
        { Calls.Add((action, actor, job)); return Task.FromResult(Outcome); }
        public Task<NetworkOperationsMutationOutcome> CancelJobAsync(string actorUserId, Guid jobId, CancellationToken cancellationToken = default) => Call("cancel", actorUserId, jobId);
        public Task<NetworkOperationsMutationOutcome> RequeueJobAsync(string actorUserId, Guid jobId, CancellationToken cancellationToken = default) => Call("retry", actorUserId, jobId);
        public Task<NetworkOperationsMutationOutcome> ReprocessJobAsync(string actorUserId, Guid jobId, CancellationToken cancellationToken = default) => Call("successor", actorUserId, jobId);
    }
    private sealed class Executions : ICentralProcessingGraphExecutionService
    {
        internal List<(Guid Revision, Guid[] Sources, Guid? Scope, string Key)> Requests { get; } = [];
        internal CentralProcessingGraphCancellationOutcome Cancellation { get; set; }
        public Task<CentralProcessingGraphScheduleResult> ScheduleReplayAsync(Guid revisionId, IReadOnlyList<Guid> sourceArtifactIds, string actorId, Guid? observatoryScope, string idempotencyKey, string reasonCode, DateTimeOffset now, CancellationToken cancellationToken)
        { Requests.Add((revisionId, sourceArtifactIds.ToArray(), observatoryScope, idempotencyKey)); return Task.FromResult(new CentralProcessingGraphScheduleResult(CentralProcessingGraphScheduleOutcome.Existing)); }
        public Task<CentralProcessingGraphCancellationOutcome> CancelAsync(Guid executionId, string actorId, Guid? observatoryScope, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(Cancellation);
        public Task<IReadOnlyList<CentralProcessingGraphExecutionSummary>> ListAsync(string actorId, Guid? observatoryScope, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CentralProcessingGraphExecutionDetail?> GetAsync(Guid executionId, string actorId, Guid? observatoryScope, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
