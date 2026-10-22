using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraOperationsReadServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 6, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow((int)ObservatoryMembershipRole.Viewer)]
    [DataRow((int)ObservatoryMembershipRole.Manager)]
    [DataRow((int)ObservatoryMembershipRole.Owner)]
    public async Task EveryMemberRoleReadsEveryCameraCollection(int role)
    {
        await using var world = await World.CreateAsync((ObservatoryMembershipRole)role).ConfigureAwait(false);
        var service = world.Service();
        var a = world.A;
        Assert.HasCount(3, (await service.ListAssignmentsAsync(Member(), a.Observatory, a.Camera,
            CentralProcessingGraphTargetHost.Central, 50, null).ConfigureAwait(false)).Items);
        Assert.HasCount(2, (await service.ListExecutionsAsync(Member(), a.Observatory, a.Camera, 50, null).ConfigureAwait(false)).Items);
        Assert.IsNotNull(await service.GetExecutionAsync(Member(), a.Observatory, a.Camera, a.Execution, 50, null).ConfigureAwait(false));
        Assert.HasCount(2, (await service.ListJobsAsync(Member(), a.Observatory, a.Camera, 50, null).ConfigureAwait(false)).Items);
        Assert.IsNotNull(await service.GetJobAsync(Member(), a.Observatory, a.Camera, a.Job, 50).ConfigureAwait(false));
        Assert.HasCount(2, (await service.ListProposalsAsync(Member(), a.Observatory, a.Camera, 50, null).ConfigureAwait(false)).Items);
        Assert.IsNotNull(await service.ListProposalFactsAsync(Member(), a.Observatory, a.Camera, a.Proposal, 50, null).ConfigureAwait(false));
        Assert.HasCount(1, (await service.ListEventAssociationsAsync(Member(), a.Observatory, a.Camera, 50, null).ConfigureAwait(false)).Items);
        Assert.IsFalse(world.Db.ChangeTracker.Entries().Any());
    }

    [TestMethod]
    public async Task OtherObservatoryRemovedMemberAndInvalidCredentialsSeeNothing()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Owner).ConfigureAwait(false);
        var service = world.Service();
        var a = world.A;
        var b = world.B;
        var denied = new List<(ClaimsPrincipal Principal, Guid Observatory, Guid Camera, Guid Execution, Guid Job, Guid Proposal)>
        {
            // A member of B only, and a member of both reading B's identities through A's route.
            (Principal("b-only"), a.Observatory, a.Camera, a.Execution, a.Job, a.Proposal),
            (Member(), a.Observatory, b.Camera, b.Execution, b.Job, b.Proposal),
            (Member(), b.Observatory, a.Camera, a.Execution, a.Job, a.Proposal),
            // An A-scoped key of a user who is also a member of B cannot read B.
            (ApiKey("member", a.Observatory), b.Observatory, b.Camera, b.Execution, b.Job, b.Proposal),
            (new ClaimsPrincipal(), a.Observatory, a.Camera, a.Execution, a.Job, a.Proposal),
            (SystemBearer(), a.Observatory, a.Camera, a.Execution, a.Job, a.Proposal),
            (new ClaimsPrincipal([Member().Identities.Single(), Principal("b-only").Identities.Single()]),
                a.Observatory, a.Camera, a.Execution, a.Job, a.Proposal)
        };
        foreach (var (principal, observatory, camera, execution, job, proposal) in denied)
        {
            await AssertDeniedAsync(service, principal, observatory, camera, execution, job, proposal).ConfigureAwait(false);
        }
        // B's exact identities substituted into A's own readable route are still denied.
        Assert.IsNull(await service.GetExecutionAsync(Member(), a.Observatory, a.Camera, b.Execution, 50, null).ConfigureAwait(false));
        Assert.IsNull(await service.GetJobAsync(Member(), a.Observatory, a.Camera, b.Job, 50).ConfigureAwait(false));
        Assert.IsNull(await service.ListJobAttemptsAsync(Member(), a.Observatory, a.Camera, b.Job, 50, null).ConfigureAwait(false));
        Assert.IsNull(await service.ListJobInputsAsync(Member(), a.Observatory, a.Camera, b.Job, 50, null).ConfigureAwait(false));
        Assert.IsNull(await service.ListJobOutputsAsync(Member(), a.Observatory, a.Camera, b.Job, 50, null).ConfigureAwait(false));
        Assert.IsNull(await service.ListProposalFactsAsync(Member(), a.Observatory, a.Camera, b.Proposal, 50, null).ConfigureAwait(false));
        Assert.IsNotNull(await service.GetJobAsync(ApiKey("member", a.Observatory), a.Observatory, a.Camera, a.Job, 5).ConfigureAwait(false));
        Assert.IsNotNull(await service.GetJobAsync(Member(), b.Observatory, b.Camera, b.Job, 5).ConfigureAwait(false));

        world.Db.ObservatoryMemberships.RemoveRange(world.Db.ObservatoryMemberships.Where(item => item.UserId == "member"));
        await world.Db.SaveChangesAsync().ConfigureAwait(false);
        world.Db.ChangeTracker.Clear();
        await AssertDeniedAsync(service, Member(), a.Observatory, a.Camera, a.Execution, a.Job, a.Proposal).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task GlobalDefinitionsAndAssignmentsGrantNoPrivateExecutionAuthority()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Owner).ConfigureAwait(false);
        var service = world.Service();
        var a = world.A;
        // "b-only" can read the global default assignment through B, but nothing of A's camera history.
        var bAssignments = await service.ListAssignmentsAsync(Principal("b-only"), world.B.Observatory, world.B.Camera,
            CentralProcessingGraphTargetHost.Central, 50, null).ConfigureAwait(false);
        Assert.IsTrue(bAssignments.Items.Any(item => item.Scope == CentralProcessingGraphAssignmentScope.GlobalDefault));
        Assert.IsFalse(bAssignments.Items.Any(item => item.ObservatoryId == a.Observatory));
        Assert.IsEmpty((await service.ListExecutionsAsync(Principal("b-only"), a.Observatory, a.Camera, 50, null).ConfigureAwait(false)).Items);
        Assert.IsNull(await service.GetExecutionAsync(Principal("b-only"), a.Observatory, a.Camera, a.Execution, 50, null).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task AssignmentHistoryIncludesCameraObservatoryAndGlobalRowsWithoutDefinitionsOrOtherScopes()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        var a = world.A;
        var page = await world.Service().ListAssignmentsAsync(Member(), a.Observatory, a.Camera,
            CentralProcessingGraphTargetHost.Central, 50, null).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(
            new[] { CentralProcessingGraphAssignmentScope.LogicalCamera, CentralProcessingGraphAssignmentScope.Observatory,
                CentralProcessingGraphAssignmentScope.GlobalDefault },
            page.Items.Select(item => item.Scope).ToArray());
        Assert.IsTrue(page.Items.All(item => item.TargetHost == CentralProcessingGraphTargetHost.Central));
        Assert.IsFalse(page.Items.Any(item => item.LogicalCameraId == a.OtherCamera));
        Assert.IsTrue(page.Items.All(item => item.RevisionLifecycle == CentralProcessingGraphLifecycle.Published));
        Assert.IsTrue(page.Items.SequenceEqual(page.Items.OrderByDescending(item => item.EffectiveFromUtc).ThenByDescending(item => item.Id)));
        var json = JsonSerializer.Serialize(page);
        Assert.DoesNotContain("DefinitionJson", json);
        Assert.DoesNotContain("ActorUserId", json);
        var edge = await world.Service().ListAssignmentsAsync(Member(), a.Observatory, a.Camera,
            CentralProcessingGraphTargetHost.Edge, 50, null).ConfigureAwait(false);
        Assert.HasCount(1, edge.Items);
        Assert.AreEqual(CentralProcessingGraphTargetHost.Edge, edge.Items[0].TargetHost);
    }

    [TestMethod]
    public async Task ExecutionHistorySpansReplacedInstallationsAndPagesEqualTimestampsWithConcurrentInserts()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        var a = world.A;
        for (var index = 0; index < 7; index++)
        {
            if (index % 2 == 0) world.AddExecution(a.RetiredInstallation, a.Revision, a.Anchor, a.CameraEntity, Now);
            else world.AddExecution(a.CurrentInstallation, a.Revision, a.CurrentAnchor, a.CameraEntity, Now);
        }
        world.AddExecution(world.B.CurrentInstallation, world.B.Revision, world.B.CurrentAnchor, world.B.CameraEntity, Now);
        await world.Db.SaveChangesAsync().ConfigureAwait(false);
        var service = world.Service();
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await service.ListExecutionsAsync(Member(), a.Observatory, a.Camera, 2, cursor).ConfigureAwait(false);
            Assert.IsLessThanOrEqualTo(2, page.Items.Count);
            seen.AddRange(page.Items.Select(item => item.Id));
            cursor = page.NextCursor;
            if (pages++ == 0)
            {
                // A newer insert after the first page cannot reappear behind the cursor; an older one is still reached.
                world.AddExecution(a.CurrentInstallation, a.Revision, a.CurrentAnchor, a.CameraEntity, Now.AddHours(1));
                world.AddExecution(a.RetiredInstallation, a.Revision, a.Anchor, a.CameraEntity, Now.AddHours(-1));
                await world.Db.SaveChangesAsync().ConfigureAwait(false);
            }
        }
        while (cursor is not null);
        var expected = await world.Db.CentralProcessingGraphExecutions
            .Where(item => item.LogicalCameraId == a.Camera && item.CreatedAtUtc <= Now)
            .OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id)
            .Select(item => item.Id).ToListAsync().ConfigureAwait(false);
        CollectionAssert.AreEqual(expected, seen);
        Assert.AreEqual(seen.Count, seen.Distinct().Count());
        Assert.IsTrue(seen.Contains(a.Execution));
        Assert.IsFalse(seen.Contains(world.B.Execution));
    }

    [TestMethod]
    public async Task ContinuationsAreBoundToCallerCameraCollectionSubjectAndFilter()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Owner).ConfigureAwait(false);
        var a = world.A;
        for (var index = 0; index < 3; index++)
        {
            world.AddExecution(a.CurrentInstallation, a.Revision, a.CurrentAnchor, a.CameraEntity, Now.AddMinutes(index));
        }
        world.Db.Add(new ObservatoryMembership
        {
            UserId = "b-only",
            ObservatoryId = a.Observatory,
            Role = ObservatoryMembershipRole.Viewer,
            AddedAtUtc = Now
        });
        await world.Db.SaveChangesAsync().ConfigureAwait(false);
        var service = world.Service();
        var executions = (await service.ListExecutionsAsync(Member(), a.Observatory, a.Camera, 1, null).ConfigureAwait(false)).NextCursor;
        var assignments = (await service.ListAssignmentsAsync(Member(), a.Observatory, a.Camera,
            CentralProcessingGraphTargetHost.Central, 1, null).ConfigureAwait(false)).NextCursor;
        Assert.IsNotNull(executions);
        Assert.IsNotNull(assignments);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.ListExecutionsAsync(Principal("b-only"), a.Observatory, a.Camera, 1, executions)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.ListExecutionsAsync(Member(), world.B.Observatory, world.B.Camera, 1, executions)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.ListProposalsAsync(Member(), a.Observatory, a.Camera, 1, executions)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ListAssignmentsAsync(Member(), a.Observatory,
            a.Camera, CentralProcessingGraphTargetHost.Edge, 1, assignments)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.ListExecutionsAsync(Member(), a.Observatory, a.Camera, 1, executions[..^2] + "AA")).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.ListExecutionsAsync(Member(), a.Observatory, a.Camera, 1, new string('x', 5000))).ConfigureAwait(false);
        var nodes = (await service.GetExecutionAsync(Member(), a.Observatory, a.Camera, a.Execution, 1, null).ConfigureAwait(false))!
            .Nodes.NextCursor;
        Assert.IsNotNull(nodes);
        var otherExecution = world.Db.CentralProcessingGraphExecutions.First(item => item.LogicalCameraId == a.Camera && item.Id != a.Execution).Id;
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.GetExecutionAsync(Member(), a.Observatory, a.Camera, otherExecution, 1, nodes)).ConfigureAwait(false);
        // A denied caller learns nothing about cursor validity.
        Assert.IsNull(await service.GetExecutionAsync(Principal("nobody"), a.Observatory, a.Camera, a.Execution, 1, "garbage").ConfigureAwait(false));
        Assert.IsEmpty((await service.ListJobsAsync(Principal("nobody"), a.Observatory, a.Camera, 1, "garbage").ConfigureAwait(false)).Items);
        foreach (var take in new[] { 0, -1, 51 })
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
                service.ListExecutionsAsync(Member(), a.Observatory, a.Camera, take, null)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ExecutionInspectorReturnsFrozenIdentitiesSourcesAndBoundedNodePages()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        var a = world.A;
        var service = world.Service();
        var first = await service.GetExecutionAsync(Member(), a.Observatory, a.Camera, a.Execution, 1, null).ConfigureAwait(false);
        Assert.IsNotNull(first);
        Assert.AreEqual(a.Execution, first.Execution.Id);
        Assert.AreEqual(a.RetiredInstallation.Id, first.LogicalCameraInstallationId);
        Assert.AreEqual(new string('d', 64), first.DefinitionIdentitySha256);
        Assert.HasCount(1, first.Sources);
        Assert.AreEqual(a.AnchorArtifactId, first.Sources[0].ArtifactId);
        Assert.HasCount(1, first.Nodes.Items);
        Assert.AreEqual(0, first.Nodes.Items[0].Ordinal);
        Assert.AreEqual(2, first.Nodes.Items[0].InputCount);
        Assert.AreEqual(2, first.Nodes.Items[0].ResolvedInputCount);
        Assert.AreEqual(1, first.Nodes.Items[0].RequiredInputCount);
        Assert.AreEqual(1, first.Nodes.Items[0].OutputCount);
        Assert.AreEqual(1, first.Nodes.Items[0].BoundOutputCount);
        var second = await service.GetExecutionAsync(Member(), a.Observatory, a.Camera, a.Execution, 1, first.Nodes.NextCursor).ConfigureAwait(false);
        Assert.HasCount(1, second!.Nodes.Items);
        Assert.AreEqual(1, second.Nodes.Items[0].Ordinal);
        Assert.IsNull(second.Nodes.NextCursor);
        Assert.IsNull(await service.GetExecutionAsync(Member(), a.Observatory, a.OtherCamera, a.Execution, 1, null).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task JobInspectorProjectsExactLineageWithoutLeaseOrCrossObservatoryIdentity()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        var a = world.A;
        var service = world.Service();
        var inspector = await service.GetJobAsync(Member(), a.Observatory, a.Camera, a.Job, 1).ConfigureAwait(false);
        Assert.IsNotNull(inspector);
        Assert.AreEqual(a.Execution, inspector.Job.GraphExecutionId);
        Assert.AreEqual(a.AnchorArtifactId, inspector.Job.SourceArtifactId);
        Assert.AreEqual(a.RetiredInstallation.Id, inspector.Job.LogicalCameraInstallationId);
        Assert.AreEqual(a.ResultArtifactId, inspector.Job.ResultArtifactId);
        Assert.HasCount(1, inspector.Attempts.Items);
        Assert.IsNotNull(inspector.Attempts.NextCursor);
        Assert.HasCount(1, inspector.Inputs.Items);
        Assert.AreEqual(a.AnchorArtifactId, inspector.Inputs.Items[0].ArtifactId);
        Assert.AreEqual(new string('c', 64), inspector.Inputs.Items[0].ArtifactChecksumSha256);
        Assert.HasCount(1, inspector.Outputs.Items);
        Assert.AreEqual(a.ResultArtifactId, inspector.Outputs.Items[0].ArtifactId);
        var json = JsonSerializer.Serialize(inspector);
        Assert.DoesNotContain(World.LeaseToken.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lease-owner", json);
        Assert.DoesNotContain("LeaseToken", json);
        Assert.DoesNotContain("LeaseExpires", json);
        Assert.DoesNotContain("secret error text", json);

        var attempts = await service.ListJobAttemptsAsync(Member(), a.Observatory, a.Camera, a.Job, 1, inspector.Attempts.NextCursor).ConfigureAwait(false);
        Assert.AreEqual(2, attempts!.Items.Single().AttemptNumber);
        Assert.IsNull(attempts.NextCursor);
        var inputs = await service.ListJobInputsAsync(Member(), a.Observatory, a.Camera, a.Job, 1, inspector.Inputs.NextCursor).ConfigureAwait(false);
        var crossObservatory = inputs!.Items.Single();
        Assert.AreEqual(CentralDerivativeInputResolutionState.Resolved, crossObservatory.ResolutionState);
        Assert.IsNull(crossObservatory.ArtifactId);
        Assert.IsNull(crossObservatory.ArtifactChecksumSha256);
        Assert.IsNull(crossObservatory.ArtifactCaptureId);
        var outputs = await service.ListJobOutputsAsync(Member(), a.Observatory, a.Camera, a.Job, 1, null).ConfigureAwait(false);
        Assert.AreEqual(inspector.Outputs.Items.Single(), outputs!.Items.Single());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ListJobInputsAsync(
            Member(), a.Observatory, a.Camera, a.Job, 1, inspector.Attempts.NextCursor)).ConfigureAwait(false);
        Assert.IsNull(await service.ListJobAttemptsAsync(Member(), a.Observatory, a.Camera, world.B.Job, 1, null).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task JobHistoryWalksOnlyBoundedCameraCapturesAndReachesEveryJobOnce()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        var a = world.A;
        // 120 sparse captures with equal timestamps in pairs; only every tenth has jobs, three at an equal time.
        for (var index = 0; index < 120; index++)
        {
            var installation = index % 3 == 0 ? a.RetiredInstallation : a.CurrentInstallation;
            var capture = world.AddCapture(installation, a.CameraEntity, Now.AddMinutes(-10 - index / 2));
            if (index % 10 == 0)
            {
                var artifact = world.AddArtifact(capture, FrameArtifactRole.Raw);
                for (var job = 0; job < 3; job++) world.AddJob(artifact, Now.AddMinutes(-index / 2));
            }
        }
        var bCapture = world.AddCapture(world.B.CurrentInstallation, world.B.CameraEntity, Now.AddMinutes(-11));
        world.AddJob(world.AddArtifact(bCapture, FrameArtifactRole.Raw), Now);
        await world.Db.SaveChangesAsync().ConfigureAwait(false);
        var service = world.Service();
        var seen = new List<Guid>();
        string? cursor = null;
        var requests = 0;
        do
        {
            var page = await service.ListJobsAsync(Member(), a.Observatory, a.Camera, 4, cursor).ConfigureAwait(false);
            Assert.IsLessThanOrEqualTo(4, page.Items.Count);
            Assert.IsLessThanOrEqualTo(CameraOperationsReadService.CaptureExaminationLimit + 1, page.ExaminedCaptures);
            seen.AddRange(page.Items.Select(item => item.JobId));
            cursor = page.NextCursor;
            requests++;
        }
        while (cursor is not null && requests < 100);
        var expected = await world.Db.CentralDerivativeJobs
            .Where(job => job.SourceArtifact!.Frame!.LogicalCameraInstallation!.LogicalCameraId == a.Camera)
            .OrderByDescending(job => job.SourceArtifact!.Frame!.CapturedAtUtc).ThenByDescending(job => job.SourceArtifact!.CentralFrameId)
            .ThenByDescending(job => job.CreatedAtUtc).ThenByDescending(job => job.Id)
            .Select(job => job.Id).ToListAsync().ConfigureAwait(false);
        Assert.HasCount(38, expected);
        CollectionAssert.AreEqual(expected, seen);
        Assert.IsLessThan(100, requests);
    }

    [TestMethod]
    public async Task ProposalHistoryReportsStoredFactsAndLabelsDerivedStagedWithoutTimestamp()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        var a = world.A;
        var service = world.Service();
        var page = await service.ListProposalsAsync(Member(), a.Observatory, a.Camera, 50, null).ConfigureAwait(false);
        Assert.HasCount(2, page.Items);
        var staged = page.Items.Single(item => item.Proposal.Id == a.Proposal);
        Assert.AreEqual("Accepted", staged.TerminalFact!.Kind);
        Assert.AreEqual("Retrieved", staged.LatestFact!.Kind);
        Assert.IsNull(staged.LatestLifecycleFact);
        Assert.AreEqual(CameraOperationsReadService.DerivedStagedStatus, staged.DerivedStatus!.Status);
        Assert.StartsWith("derived:", staged.DerivedStatus.Basis);
        Assert.DoesNotContain("At", JsonSerializer.Serialize(staged.DerivedStatus));
        var superseded = page.Items.Single(item => item.Proposal.Id != a.Proposal);
        Assert.AreEqual(a.CurrentInstallation.Id, superseded.Proposal.LogicalCameraInstallationId);
        Assert.AreEqual(a.RetiredInstallation.Id, staged.Proposal.LogicalCameraInstallationId);
        Assert.AreEqual("Superseded", superseded.TerminalFact!.Kind);
        Assert.AreEqual("LogicHost", superseded.TerminalFact.Source);
        Assert.AreEqual("effective-delivery-tuple-changed", superseded.TerminalFact.ReasonCode);
        Assert.IsNull(superseded.DerivedStatus);

        // The derivation follows the latest stored lifecycle fact: activation clears it, a rollback leaves the accepted
        // revision inactive (staged again), and a later re-activation clears it once more.
        foreach (var (kind, minutes, expectStaged) in new[] { ("Activated", 30, false), ("RolledBack", 40, true), ("Activated", 50, false) })
        {
            world.AddFact(a.ProposalEntity, kind, Now.AddMinutes(minutes));
            await world.Db.SaveChangesAsync().ConfigureAwait(false);
            var current = (await world.Service().ListProposalsAsync(Member(), a.Observatory, a.Camera, 50, null).ConfigureAwait(false))
                .Items.Single(item => item.Proposal.Id == a.Proposal);
            Assert.AreEqual(kind, current.LatestLifecycleFact!.Kind);
            Assert.AreEqual(kind, current.LatestFact!.Kind);
            Assert.AreEqual("Accepted", current.TerminalFact!.Kind);
            Assert.AreEqual(expectStaged, current.DerivedStatus is not null, kind);
        }
    }

    [TestMethod]
    public async Task ProposalFactsContinueBeyondTheFirstPageInStableOrder()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        var a = world.A;
        for (var index = 0; index < 70; index++) world.AddFact(a.ProposalEntity, "Retrieved", Now.AddMinutes(5));
        await world.Db.SaveChangesAsync().ConfigureAwait(false);
        var service = world.Service();
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await service.ListProposalFactsAsync(Member(), a.Observatory, a.Camera, a.Proposal, 50, cursor).ConfigureAwait(false);
            seen.AddRange(page!.Items.Select(item => item.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        var expected = await world.Db.CentralProcessingGraphDeliveryFacts.Where(fact => fact.ProposalId == a.Proposal)
            .OrderByDescending(fact => fact.RecordedAtUtc).ThenByDescending(fact => fact.Id)
            .Select(fact => fact.Id).ToListAsync().ConfigureAwait(false);
        Assert.HasCount(72, expected);
        CollectionAssert.AreEqual(expected, seen);
        Assert.IsNull(await service.ListProposalFactsAsync(Member(), a.Observatory, a.Camera, world.B.Proposal, 50, null).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task EventAssociationsRequireEveryContributorWithinMembershipAndCredentialScope()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        var a = world.A;
        // A second event uses A's capture as its source and B's capture as background.
        var shared = world.AddEvent(a.EventCapture, world.B.EventCapture, Now.AddMinutes(2));
        await world.Db.SaveChangesAsync().ConfigureAwait(false);
        var service = world.Service();
        var both = await service.ListEventAssociationsAsync(Member(), a.Observatory, a.Camera, 50, null).ConfigureAwait(false);
        Assert.HasCount(2, both.Items);
        Assert.IsTrue(both.Items.All(item => item.CaptureId == a.EventCapture.Id && item.CaptureIsObservationSource));
        CollectionAssert.AreEqual(both.Items.OrderByDescending(item => item.CentralTransientEventId).ToArray(), both.Items.ToArray());
        var scoped = await service.ListEventAssociationsAsync(ApiKey("member", a.Observatory), a.Observatory, a.Camera, 50, null).ConfigureAwait(false);
        Assert.AreEqual(a.Event, scoped.Items.Single().CentralTransientEventId);
        world.Db.Add(new ObservatoryMembership { UserId = "b-only", ObservatoryId = a.Observatory, Role = ObservatoryMembershipRole.Viewer, AddedAtUtc = Now });
        await world.Db.SaveChangesAsync().ConfigureAwait(false);
        var bothMember = await service.ListEventAssociationsAsync(Principal("b-only"), a.Observatory, a.Camera, 50, null).ConfigureAwait(false);
        Assert.HasCount(2, bothMember.Items);
        var background = await service.ListEventAssociationsAsync(Member(), world.B.Observatory, world.B.Camera, 50, null).ConfigureAwait(false);
        var backgroundOnly = background.Items.Single(item => item.CentralTransientEventId == shared);
        Assert.IsTrue(backgroundOnly.CaptureIsBackground);
        Assert.IsFalse(backgroundOnly.CaptureIsObservationSource);
        Assert.IsEmpty((await service.ListEventAssociationsAsync(Principal("b-only"), world.A.Observatory, world.B.Camera, 50, null).ConfigureAwait(false)).Items);
    }

    [TestMethod]
    public async Task EventAssociationsPageWithinOneCaptureAndAcrossCaptures()
    {
        await using var world = await World.CreateAsync(ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        var a = world.A;
        for (var index = 0; index < 4; index++) world.AddEvent(a.EventCapture, null, Now.AddMinutes(index));
        var older = world.AddCapture(a.CurrentInstallation, a.CameraEntity, a.EventCapture.CapturedAtUtc.AddMinutes(-1));
        world.AddEvent(older, null, Now);
        await world.Db.SaveChangesAsync().ConfigureAwait(false);
        var service = world.Service();
        var seen = new List<(Guid, Guid)>();
        string? cursor = null;
        do
        {
            var page = await service.ListEventAssociationsAsync(Member(), a.Observatory, a.Camera, 2, cursor).ConfigureAwait(false);
            seen.AddRange(page.Items.Select(item => (item.CaptureId, item.CentralTransientEventId)));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        Assert.HasCount(6, seen);
        Assert.AreEqual(6, seen.Distinct().Count());
        Assert.IsTrue(seen.Take(5).All(item => item.Item1 == a.EventCapture.Id));
        Assert.AreEqual(older.Id, seen[^1].Item1);
    }

    [TestMethod]
    public void SqlServerTranslationKeepsPerInstallationSeeksAndUnionedAssignmentBranches()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=localhost;Database=camera-operations-model;Integrated Security=true;TrustServerCertificate=true")
            .Options);
        var observatory = Guid.NewGuid();
        var camera = Guid.NewGuid();
        Guid[] installations = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        foreach (var sql in new[]
        {
            CameraOperationsReadService.ExecutionHistory(db, installations, observatory, camera, Now, Guid.NewGuid(), 26)!.ToQueryString(),
            CameraOperationsReadService.CaptureWindow(db, installations, observatory, Now, Guid.NewGuid(), 51)!.ToQueryString(),
            CameraOperationsReadService.ProposalHistory(db, installations
                .Select(id => new CameraOperationsReadService.InstallationKey(id, Guid.NewGuid(), Guid.NewGuid())).ToArray(),
                Now, Guid.NewGuid(), 26)!.ToQueryString()
        })
        {
            // One TOP seek per retained installation, never a ROW_NUMBER over the camera's complete history.
            StringAssert.Contains(sql, "UNION ALL");
            Assert.AreEqual(installations.Length + 1, sql.Split("TOP(").Length - 1, sql);
            Assert.DoesNotContain("ROW_NUMBER", sql);
            // The keyset is anchored with <= so the seek bound is a range, not a residual OR over the whole history.
            StringAssert.Contains(sql, "<= @");
        }
        Assert.IsNull(CameraOperationsReadService.ExecutionHistory(db, [], observatory, camera, null, null, 26));
        var assignments = CameraOperationsReadService.AssignmentHistory(
            db, observatory, camera, CentralProcessingGraphTargetHost.Central, Now, Guid.NewGuid()).Take(26).ToQueryString();
        StringAssert.Contains(assignments, "UNION ALL");
        // Proposal and fact reads pin their existing indexes so rows read never follow the network's history.
        var facts = CameraOperationsReadService.PageFacts(db, [Guid.NewGuid(), Guid.NewGuid()]).ToQueryString();
        Assert.DoesNotContain("ROW_NUMBER", facts);
        StringAssert.Contains(facts, "FORCESEEK([IX_CentralProcessingGraphDeliveryFacts_ProposalId_RecordedAtUtc_Id]([ProposalId]))");
        StringAssert.Contains(CameraOperationsReadService.ProposalFacts(db, Guid.NewGuid()).Take(51).ToQueryString(),
            "FORCESEEK([IX_CentralProcessingGraphDeliveryFacts_ProposalId_RecordedAtUtc_Id]([ProposalId]))");
        var proposals = CameraOperationsReadService.ProposalHistory(db, installations
            .Select(id => new CameraOperationsReadService.InstallationKey(id, Guid.NewGuid(), Guid.NewGuid())).ToArray(),
            Now, Guid.NewGuid(), 26)!.ToQueryString();
        Assert.AreEqual(installations.Length, proposals.Split("FORCESEEK(").Length - 1, proposals);
        var events = new CameraOperationsReadService(db, new EphemeralDataProtectionProvider())
            .EventAssociations("member", null, observatory, camera, [Guid.NewGuid()], Guid.NewGuid()).Take(3).ToQueryString();
        StringAssert.Contains(events, "UNION");
        StringAssert.Contains(events, "CentralTransientObservationBackgrounds");
    }

    private static async Task AssertDeniedAsync(
        CameraOperationsReadService service, ClaimsPrincipal principal, Guid observatory, Guid camera, Guid execution,
        Guid job, Guid proposal)
    {
        Assert.IsEmpty((await service.ListAssignmentsAsync(principal, observatory, camera, CentralProcessingGraphTargetHost.Central, 50, null).ConfigureAwait(false)).Items);
        Assert.IsEmpty((await service.ListExecutionsAsync(principal, observatory, camera, 50, null).ConfigureAwait(false)).Items);
        Assert.IsNull(await service.GetExecutionAsync(principal, observatory, camera, execution, 50, null).ConfigureAwait(false));
        var jobs = await service.ListJobsAsync(principal, observatory, camera, 50, null).ConfigureAwait(false);
        Assert.IsEmpty(jobs.Items);
        Assert.AreEqual(0, jobs.ExaminedCaptures);
        Assert.IsNull(jobs.NextCursor);
        Assert.IsNull(await service.GetJobAsync(principal, observatory, camera, job, 50).ConfigureAwait(false));
        Assert.IsNull(await service.ListJobAttemptsAsync(principal, observatory, camera, job, 50, null).ConfigureAwait(false));
        Assert.IsNull(await service.ListJobInputsAsync(principal, observatory, camera, job, 50, null).ConfigureAwait(false));
        Assert.IsNull(await service.ListJobOutputsAsync(principal, observatory, camera, job, 50, null).ConfigureAwait(false));
        Assert.IsEmpty((await service.ListProposalsAsync(principal, observatory, camera, 50, null).ConfigureAwait(false)).Items);
        Assert.IsNull(await service.ListProposalFactsAsync(principal, observatory, camera, proposal, 50, null).ConfigureAwait(false));
        Assert.IsEmpty((await service.ListEventAssociationsAsync(principal, observatory, camera, 50, null).ConfigureAwait(false)).Items);
    }

    private static ClaimsPrincipal Member() => Principal("member");

    private static ClaimsPrincipal Principal(string actor)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor), new Claim("account_type", "User")],
            IdentityConstants.ApplicationScheme));

    private static ClaimsPrincipal ApiKey(string actor, Guid observatory)
        => new(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, actor), new Claim("account_type", "User"),
            new Claim(ApiKeyClaims.AuthenticationType, ApiKeyAuthenticationOptions.AuthenticationScheme),
            new Claim(ApiKeyClaims.AccessLevel, nameof(ApiKeyAccessLevel.Read)),
            new Claim(ApiKeyClaims.ObservatoryId, observatory.ToString("D"))], ApiKeyAuthenticationOptions.AuthenticationScheme));

    private static ClaimsPrincipal SystemBearer()
        => new(new ClaimsIdentity([new Claim("sub", "member"), new Claim("account_type", "System"), new Claim("scope", "api.admin")],
            CanonicalCredentialClaims.BearerAuthenticationType));

    internal sealed record Site(
        Guid Observatory,
        Guid Camera,
        LogicalCamera CameraEntity,
        Guid OtherCamera,
        LogicalCameraInstallation RetiredInstallation,
        LogicalCameraInstallation CurrentInstallation,
        CentralProcessingGraphRevision Revision,
        CentralArtifact Anchor,
        CentralArtifact CurrentAnchor,
        Guid AnchorArtifactId,
        Guid ResultArtifactId,
        Guid Execution,
        Guid Job,
        CentralProcessingGraphDeliveryProposal ProposalEntity,
        Guid Proposal,
        CentralFrame EventCapture,
        Guid Event);

    /// <summary>
    /// Two observatories. "member" belongs to both; "b-only" belongs to B. Each observatory has one camera with a
    /// retired and a replacement installation, an execution with two nodes, a job with two attempts, two inputs
    /// (the second resolved from the other observatory), one bound output, two proposals and one event.
    /// </summary>
    internal sealed class World : IAsyncDisposable
    {
        internal static readonly Guid LeaseToken = Guid.NewGuid();
        private int _sequence;

        private World(ApplicationDbContext db) => Db = db;

        public ApplicationDbContext Db { get; }
        public Site A { get; private set; } = null!;
        public Site B { get; private set; } = null!;

        public static async Task<World> CreateAsync(ObservatoryMembershipRole role)
        {
            var world = new World(new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options));
            var member = new ApplicationUser { Id = "member", AccountType = AccountType.User };
            var bOnly = new ApplicationUser { Id = "b-only", AccountType = AccountType.User };
            var nobody = new ApplicationUser { Id = "nobody", AccountType = AccountType.User };
            world.Db.AddRange(member, bOnly, nobody);
            var a = world.AddSite("A", [(member, role)]);
            var b = world.AddSite("B", [(member, ObservatoryMembershipRole.Viewer), (bOnly, ObservatoryMembershipRole.Viewer)]);
            world.A = a;
            world.B = b;
            var crossA = world.AddCrossObservatoryRequirement(a, b);
            var crossB = world.AddCrossObservatoryRequirement(b, a);
            var globalRevision = world.AddRevision();
            world.Db.Add(new CentralProcessingGraphAssignment
            {
                RevisionId = globalRevision.Id,
                Revision = globalRevision,
                TargetHost = CentralProcessingGraphTargetHost.Central,
                Scope = CentralProcessingGraphAssignmentScope.GlobalDefault,
                EffectiveFromUtc = Now.AddDays(-3),
                CreatedAtUtc = Now.AddDays(-3),
                ActorUserId = "platform",
                ReasonCode = "global"
            });
            // Production seals graph topology at expansion and only then selects concrete inputs.
            await world.Db.SaveChangesAsync().ConfigureAwait(false);
            foreach (var execution in world.Db.ChangeTracker.Entries<CentralProcessingGraphExecution>()
                .Where(entry => entry.Entity.Id == a.Execution || entry.Entity.Id == b.Execution))
            {
                execution.Entity.ExpandedAtUtc = execution.Entity.CreatedAtUtc;
            }
            await world.Db.SaveChangesAsync().ConfigureAwait(false);
            world.SelectInput(a, a.Anchor, 0);
            world.SelectInput(a, b.Anchor, crossA);
            world.SelectInput(b, b.Anchor, 0);
            world.SelectInput(b, a.Anchor, crossB);
            foreach (var output in world.Db.ChangeTracker.Entries<CentralDerivativeJobOutput>())
            {
                output.Entity.ResultCentralArtifactId = output.Entity.Job!.ResultCentralArtifactId;
                output.Entity.ResultOutputIdentitySha256 = world.Hash();
                output.Entity.BoundAtUtc = Now.AddHours(-3);
            }
            await world.Db.SaveChangesAsync().ConfigureAwait(false);
            world.Db.ChangeTracker.Clear();
            return world;
        }

        public CameraOperationsReadService Service()
        {
            Db.ChangeTracker.Clear();
            return new(Db, new EphemeralDataProtectionProvider());
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public CentralProcessingGraphExecution AddExecution(
            LogicalCameraInstallation installation, CentralProcessingGraphRevision revision, CentralArtifact anchor,
            LogicalCamera camera, DateTimeOffset createdAt)
        {
            var execution = new CentralProcessingGraphExecution
            {
                ExecutionClass = CentralProcessingGraphExecutionClass.Replay,
                Status = CentralProcessingGraphExecutionStatus.Pending,
                RequestIdentitySha256 = Hash(),
                RevisionId = revision.Id,
                DefinitionIdentitySha256 = new string('d', 64),
                FrozenDefinitionJson = "{}",
                CentralPlanIdentitySha256 = new string('e', 64),
                FrozenCentralPlanJson = "{}",
                ExpectedSourceCount = 1,
                ExpectedNodeCount = 2,
                ObservatoryId = camera.ObservatoryId,
                LogicalCameraId = camera.Id,
                LogicalCameraInstallationId = installation.Id,
                InstallationPublicId = installation.InstallationPublicId,
                AnchorSourceCentralArtifactId = anchor.Id,
                AnchorSourceArtifactId = anchor.ArtifactId,
                AnchorSourceChecksumSha256 = anchor.ChecksumSha256,
                Trigger = CentralProcessingGraphTrigger.Replay,
                ActorId = "member",
                IdempotencyKey = $"key-{++_sequence}",
                ReasonCode = "test",
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = createdAt
            };
            Db.Add(execution);
            return execution;
        }

        public CentralFrame AddCapture(LogicalCameraInstallation installation, LogicalCamera camera, DateTimeOffset capturedAt)
        {
            var frame = new CentralFrame
            {
                LogicalCameraInstallationId = installation.Id,
                RegistrationId = installation.RegistrationId,
                ObservatoryId = camera.ObservatoryId,
                FrameId = Guid.NewGuid(),
                AgentId = "agent",
                CapturedAtUtc = capturedAt,
                FirstReceivedAtUtc = capturedAt
            };
            Db.Add(frame);
            return frame;
        }

        public CentralArtifact AddArtifact(CentralFrame frame, FrameArtifactRole role)
        {
            var artifact = new CentralArtifact
            {
                CentralFrameId = frame.Id,
                ArtifactId = Guid.NewGuid(),
                Role = role,
                ChecksumSha256 = new string('c', 64),
                IdempotencyKey = Hash(),
                ReceivedAtUtc = frame.CapturedAtUtc
            };
            Db.Add(artifact);
            return artifact;
        }

        public CentralDerivativeJob AddJob(CentralArtifact source, DateTimeOffset createdAt)
        {
            var job = new CentralDerivativeJob
            {
                SourceCentralArtifactId = source.Id,
                TargetRole = FrameArtifactRole.Preview,
                TargetVariant = "default",
                TargetRecipeVersion = "1",
                RecipeName = "recipe",
                RequestIdentitySha256 = Hash(),
                Status = CentralDerivativeJobStatus.Pending,
                MaxAttempts = 3,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = createdAt
            };
            Db.Add(job);
            return job;
        }

        public void AddFact(CentralProcessingGraphDeliveryProposal proposal, string kind, DateTimeOffset recordedAt)
            => Db.Add(new CentralProcessingGraphDeliveryFact
            {
                ProposalId = proposal.Id,
                Kind = kind,
                Source = "CameraAgent",
                OccurredAtUtc = recordedAt,
                RecordedAtUtc = recordedAt
            });

        public Guid AddEvent(CentralFrame sourceCapture, CentralFrame? backgroundCapture, DateTimeOffset createdAt)
        {
            var record = new CentralTransientEventRecord { AgentId = "agent", EventId = Guid.NewGuid(), EventCreatedUtc = createdAt };
            var version = new CentralTransientEventVersionRecord
            {
                EventVersionId = Guid.NewGuid(),
                CentralTransientEventId = record.Id,
                Version = 1,
                State = TransientEventState.Provisional,
                VersionCreatedUtc = createdAt,
                FirstObservedUtc = createdAt,
                LastObservedUtc = createdAt
            };
            var observationId = Guid.NewGuid();
            var sourceArtifact = AddArtifact(sourceCapture, FrameArtifactRole.Raw);
            Db.AddRange(record, version,
                new CentralTransientObservationSourceReference
                {
                    ObservationId = observationId,
                    CentralArtifactId = sourceArtifact.Id,
                    ArtifactId = sourceArtifact.ArtifactId
                },
                new CentralTransientObservationRecord
                {
                    ObservationId = observationId,
                    CentralTransientEventId = record.Id,
                    SourceReferenceId = observationId
                },
                new CentralTransientEventCurrent
                {
                    CentralTransientEventId = record.Id,
                    LatestEventVersionId = version.EventVersionId,
                    LatestVersion = 1,
                    ActiveAssessmentId = Guid.NewGuid(),
                    ReviewState = CentralTransientReviewState.NeedsReview,
                    EffectiveClassification = TransientClassification.Meteor,
                    UpdatedUtc = createdAt
                });
            if (backgroundCapture is not null)
            {
                var background = AddArtifact(backgroundCapture, FrameArtifactRole.Raw);
                Db.Add(new CentralTransientObservationBackgroundReference
                {
                    ObservationId = observationId,
                    CentralArtifactId = background.Id,
                    ArtifactId = background.ArtifactId
                });
            }
            return record.Id;
        }

        private Site AddSite(string name, IReadOnlyList<(ApplicationUser User, ObservatoryMembershipRole Role)> members)
        {
            var observatory = new Observatory { OwnerUserId = "member", Name = name, TimeZoneId = "UTC" };
            var camera = new LogicalCamera { Observatory = observatory, ObservatoryId = observatory.Id, Name = name, Slug = "camera-" + name };
            var other = new LogicalCamera { Observatory = observatory, ObservatoryId = observatory.Id, Name = name + "2", Slug = "camera-" + name + "-2" };
            Db.AddRange(observatory, camera, other);
            foreach (var (user, role) in members)
            {
                Db.Add(new ObservatoryMembership { UserId = user.Id, ObservatoryId = observatory.Id, Role = role, AddedAtUtc = Now });
            }
            var retired = AddInstallation(camera, Now.AddDays(-2), Now.AddDays(-1), null);
            var current = AddInstallation(camera, Now.AddDays(-1), null, retired.Id);
            var otherInstallation = AddInstallation(other, Now.AddDays(-2), null, null);
            var revision = AddRevision();
            foreach (var (scope, host, cameraId) in new (CentralProcessingGraphAssignmentScope, CentralProcessingGraphTargetHost, Guid?)[]
            {
                (CentralProcessingGraphAssignmentScope.LogicalCamera, CentralProcessingGraphTargetHost.Central, camera.Id),
                (CentralProcessingGraphAssignmentScope.LogicalCamera, CentralProcessingGraphTargetHost.Edge, camera.Id),
                (CentralProcessingGraphAssignmentScope.Observatory, CentralProcessingGraphTargetHost.Central, null),
                (CentralProcessingGraphAssignmentScope.LogicalCamera, CentralProcessingGraphTargetHost.Central, other.Id)
            })
            {
                Db.Add(new CentralProcessingGraphAssignment
                {
                    RevisionId = revision.Id,
                    Revision = revision,
                    TargetHost = host,
                    Scope = scope,
                    ObservatoryId = observatory.Id,
                    LogicalCameraId = cameraId,
                    EffectiveFromUtc = Now.AddDays(-1),
                    CreatedAtUtc = Now.AddDays(-1),
                    ActorUserId = "member",
                    ReasonCode = "scoped"
                });
            }

            var capture = AddCapture(retired, camera, Now.AddHours(-5));
            var anchor = AddArtifact(capture, FrameArtifactRole.Raw);
            var resultCapture = AddCapture(retired, camera, Now.AddHours(-5).AddSeconds(1));
            var result = AddArtifact(resultCapture, FrameArtifactRole.Preview);
            var execution = AddExecution(retired, revision, anchor, camera, Now.AddHours(-4));
            Db.Add(new CentralProcessingGraphExecutionSource
            {
                Execution = execution,
                ExecutionId = execution.Id,
                Ordinal = 0,
                SourceId = "raw",
                CentralArtifactId = anchor.Id,
                ArtifactId = anchor.ArtifactId,
                ArtifactChecksumSha256 = anchor.ChecksumSha256,
                ArtifactByteLength = 10,
                SelectionEvidenceJson = "{}",
                SelectionEvidenceSha256 = Hash(),
                SelectedAtUtc = Now.AddHours(-4)
            });
            CentralDerivativeJob? firstNode = null;
            for (var ordinal = 0; ordinal < 2; ordinal++)
            {
                var node = AddJob(anchor, Now.AddHours(-4));
                node.GraphExecution = execution;
                node.GraphExecutionId = execution.Id;
                node.GraphNodeId = $"node-{ordinal}";
                node.GraphNodeOrdinal = ordinal;
                node.GraphFailurePolicy = ProcessingGraphNodeFailurePolicy.Required;
                node.LeaseOwner = "lease-owner";
                node.LeaseToken = LeaseToken;
                node.LeaseExpiresAtUtc = Now;
                node.LastError = "secret error text";
                firstNode ??= node;
            }
            firstNode!.ResultCentralArtifactId = result.Id;
            firstNode.ResultArtifact = result;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                Db.Add(new CentralDerivativeJobAttempt
                {
                    CentralDerivativeJobId = firstNode.Id,
                    AttemptNumber = attempt,
                    WorkerId = "worker",
                    LeaseAcquiredAtUtc = Now.AddHours(-3),
                    LeaseExpiresAtUtc = Now.AddHours(-2),
                    Outcome = CentralDerivativeAttemptOutcome.RetryableFailure
                });
            }
            var resolved = new CentralDerivativeJobInputRequirement
            {
                CentralDerivativeJobId = firstNode.Id,
                Job = firstNode,
                Ordinal = 0,
                BindingName = "primary",
                SourceKind = CentralDerivativeInputSourceKind.Artifact,
                IsRequired = true,
                ResolutionState = CentralDerivativeInputResolutionState.Resolved,
                ExpectedCentralArtifactId = anchor.Id,
                ResolvedAtUtc = Now.AddHours(-4)
            };
            Db.Add(resolved);
            Db.Add(new CentralDerivativeJobOutput
            {
                CentralDerivativeJobId = firstNode.Id,
                Job = firstNode,
                Ordinal = 0,
                Role = FrameArtifactRole.Preview,
                Variant = "default",
                ProductKind = ProcessingProductKind.PixelData,
                ContractJson = "{}",
                ContractIdentitySha256 = Hash()
            });
            // An execution's anchor capture always belongs to the execution's own installation.
            var currentAnchor = AddArtifact(AddCapture(current, camera, Now.AddHours(-3)), FrameArtifactRole.Raw);
            var otherExecution = AddExecution(current, revision, currentAnchor, camera, Now.AddHours(-3));
            var otherCapture = AddCapture(otherInstallation, other, Now.AddHours(-1));
            AddExecution(otherInstallation, revision, AddArtifact(otherCapture, FrameArtifactRole.Raw), other, Now);
            _ = otherExecution;

            var staged = AddProposal(retired, Now.AddHours(-6));
            AddFact(staged, "Accepted", Now.AddHours(-5));
            AddFact(staged, "Retrieved", Now.AddHours(-4));
            var superseded = AddProposal(current, Now.AddHours(-2));
            Db.Add(new CentralProcessingGraphDeliveryFact
            {
                ProposalId = superseded.Id,
                Kind = "Superseded",
                Source = "LogicHost",
                OccurredAtUtc = Now.AddHours(-1),
                RecordedAtUtc = Now.AddHours(-1),
                ReasonCode = "effective-delivery-tuple-changed"
            });
            AddProposal(otherInstallation, Now);

            var eventCapture = AddCapture(current, camera, Now.AddHours(-2));
            var eventId = AddEvent(eventCapture, null, Now.AddHours(-2));
            return new(observatory.Id, camera.Id, camera, other.Id, retired, current, revision, anchor, currentAnchor, anchor.ArtifactId,
                result.ArtifactId, execution.Id, firstNode.Id, staged, staged.Id, eventCapture, eventId);
        }

        public int AddCrossObservatoryRequirement(Site site, Site other)
        {
            var job = Db.ChangeTracker.Entries<CentralDerivativeJob>().Single(entry => entry.Entity.Id == site.Job).Entity;
            Db.Add(new CentralDerivativeJobInputRequirement
            {
                CentralDerivativeJobId = job.Id,
                Job = job,
                Ordinal = 1,
                BindingName = "auxiliary",
                SourceKind = CentralDerivativeInputSourceKind.Artifact,
                IsRequired = false,
                ResolutionState = CentralDerivativeInputResolutionState.Resolved,
                ExpectedCentralArtifactId = other.Anchor.Id,
                ResolvedAtUtc = Now.AddHours(-4)
            });
            return 1;
        }

        public void SelectInput(Site site, CentralArtifact artifact, int ordinal)
        {
            var requirement = Db.ChangeTracker.Entries<CentralDerivativeJobInputRequirement>()
                .Single(entry => entry.Entity.CentralDerivativeJobId == site.Job && entry.Entity.Ordinal == ordinal).Entity;
            Db.Add(new CentralDerivativeJobInput
            {
                CentralDerivativeJobId = site.Job,
                CentralDerivativeJobInputRequirementId = requirement.Id,
                Requirement = requirement,
                Ordinal = ordinal,
                CentralArtifactId = artifact.Id,
                ByteLength = 10,
                SelectedAtUtc = Now.AddHours(-4)
            });
        }

        private LogicalCameraInstallation AddInstallation(
            LogicalCamera camera, DateTimeOffset assigned, DateTimeOffset? retired, Guid? replaces)
        {
            var registration = new DeviceRegistration { ObservatoryId = camera.ObservatoryId, DeviceId = $"device-{++_sequence}" };
            var installation = new LogicalCameraInstallation
            {
                LogicalCamera = camera,
                LogicalCameraId = camera.Id,
                Registration = registration,
                RegistrationId = registration.Id,
                InstallationPublicId = Guid.NewGuid(),
                AssignedAtUtc = assigned,
                RetiredAtUtc = retired,
                ReplacesInstallationId = replaces,
                AssignedByUserId = "member",
                AssignmentReasonCode = "test"
            };
            Db.AddRange(registration, installation);
            return installation;
        }

        private CentralProcessingGraphRevision AddRevision()
        {
            var revision = new CentralProcessingGraphRevision
            {
                Name = $"graph-{++_sequence}",
                Revision = "1",
                DefinitionJson = "{}",
                DefinitionIdentitySha256 = Hash(),
                PortablePlanIdentitySha256 = Hash(),
                CreatedAtUtc = Now.AddDays(-10),
                PublishedAtUtc = Now.AddDays(-9),
                CreatedByUserId = "platform"
            };
            Db.Add(revision);
            return revision;
        }

        private CentralProcessingGraphDeliveryProposal AddProposal(LogicalCameraInstallation installation, DateTimeOffset issuedAt)
        {
            var proposal = new CentralProcessingGraphDeliveryProposal
            {
                LogicalCameraInstallationId = installation.Id,
                RegistrationId = installation.RegistrationId,
                InstallationPublicId = installation.InstallationPublicId,
                CapabilitySnapshotSha256 = Hash(),
                IssuedAtUtc = issuedAt,
                ExpiresAtUtc = issuedAt.AddHours(1)
            };
            Db.Add(proposal);
            return proposal;
        }

        internal string Hash() => (++_sequence).ToString("D64", System.Globalization.CultureInfo.InvariantCulture);
    }
}
