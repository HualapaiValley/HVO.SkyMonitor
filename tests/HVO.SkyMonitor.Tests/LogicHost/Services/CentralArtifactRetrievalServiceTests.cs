using System.Security.Claims;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralArtifactRetrievalServiceTests
{
    [TestMethod]
    public async Task FindAsync_OwnerAndActiveWorkerCanReadExactArtifact()
    {
        await using var db = CreateContext();
        var data = Seed(db);
        var leaseToken = Guid.NewGuid();
        db.CentralDerivativeJobs.Add(new CentralDerivativeJob
        {
            SourceCentralArtifactId = data.Artifact.Id,
            Status = CentralDerivativeJobStatus.Leased,
            LeaseOwner = "worker-1",
            LeaseToken = leaseToken,
            LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1),
            MaxAttempts = 3,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        using var telemetry = new CentralArtifactRetrievalTelemetry();
        var service = new CentralArtifactRetrievalService(
            db, TimeProvider.System, telemetry, NullLogger<CentralArtifactRetrievalService>.Instance);

        var owner = Principal(new Claim(ClaimTypes.NameIdentifier, "owner-1"));
        var ownerResult = await service.FindAsync(data.DevicePublicId, data.Artifact.ArtifactId, owner, null, CancellationToken.None);
        var worker = Principal(
            new Claim("sub", "worker-1"),
            new Claim("account_type", "System"),
            new Claim("scope", "api.artifacts.read"));
        var workerResult = await service.FindAsync(data.DevicePublicId, data.Artifact.ArtifactId, worker,
            new CentralArtifactWorkerAccess(db.CentralDerivativeJobs.Single().Id, "worker-1", leaseToken), CancellationToken.None);

        ownerResult.Status.Should().Be(CentralArtifactLookupStatus.Found);
        workerResult.Status.Should().Be(CentralArtifactLookupStatus.Found);
    }

    [TestMethod]
    public async Task FindAsync_CrossOwnerAndUnboundSystemCallerAreIndistinguishableFromMissing()
    {
        await using var db = CreateContext();
        var data = Seed(db);
        await db.SaveChangesAsync();
        using var telemetry = new CentralArtifactRetrievalTelemetry();
        var service = new CentralArtifactRetrievalService(
            db, TimeProvider.System, telemetry, NullLogger<CentralArtifactRetrievalService>.Instance);

        var otherOwner = Principal(new Claim(ClaimTypes.NameIdentifier, "owner-2"));
        var system = Principal(new Claim("account_type", "System"), new Claim("scope", "api.artifacts.read"));

        (await service.FindAsync(data.DevicePublicId, data.Artifact.ArtifactId, otherOwner, null, CancellationToken.None))
            .Status.Should().Be(CentralArtifactLookupStatus.NotFound);
        (await service.FindAsync(data.DevicePublicId, data.Artifact.ArtifactId, system, null, CancellationToken.None))
            .Status.Should().Be(CentralArtifactLookupStatus.NotFound);
    }

    [TestMethod]
    public async Task DownloadAuthorization_RecordsShortLivedGrantAndRechecksMembership()
    {
        var now = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        await using var db = CreateContext();
        var data = Seed(db);
        var membership = db.ObservatoryMemberships.Local.Single();
        membership.Role = ObservatoryMembershipRole.Viewer;
        await db.SaveChangesAsync();
        using var telemetry = new CentralArtifactRetrievalTelemetry();
        var service = new CentralArtifactRetrievalService(
            db, new FixedTimeProvider(now), telemetry, NullLogger<CentralArtifactRetrievalService>.Instance);
        var viewer = Principal(new Claim(ClaimTypes.NameIdentifier, "owner-1"));
        var range = new CentralArtifactByteRange(1, 2);

        var grant = await service.IssueDownloadAuthorizationAsync(
            data.Artifact, viewer, range, CancellationToken.None);

        grant.Should().NotBeNull();
        (await service.ValidateDownloadAuthorizationAsync(
            data.Artifact, viewer, range, grant!.AuthorizationId, grant.Token, CancellationToken.None)).Should().BeTrue();
        var audit = await db.CentralArtifactDownloadAuthorizations.SingleAsync();
        audit.Should().Match<CentralArtifactDownloadAuthorization>(item =>
            item.CentralArtifactId == data.Artifact.Id
            && item.ObservatoryId == membership.ObservatoryId
            && item.ActorUserId == "owner-1"
            && item.MembershipRole == ObservatoryMembershipRole.Viewer
            && item.RangeStart == 1
            && item.RangeEnd == 2
            && item.IssuedAtUtc == now
            && item.ExpiresAtUtc == now.AddMinutes(1)
            && item.TokenSha256.Length == 64
            && item.TokenSha256 != grant.Token);

        db.ObservatoryMemberships.Remove(membership);
        await db.SaveChangesAsync();
        (await service.ValidateDownloadAuthorizationAsync(
            data.Artifact, viewer, range, grant.AuthorizationId, grant.Token, CancellationToken.None)).Should().BeFalse();
        (await service.IssueDownloadAuthorizationAsync(data.Artifact, viewer, null, CancellationToken.None))
            .Should().BeNull();
        (await db.CentralArtifactDownloadAuthorizations.CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task DownloadAuthorization_IsRequiredForEveryHumanRoleButNotSystemWorkers()
    {
        await using var db = CreateContext();
        var data = Seed(db);
        using var telemetry = new CentralArtifactRetrievalTelemetry();
        var service = new CentralArtifactRetrievalService(
            db, TimeProvider.System, telemetry, NullLogger<CentralArtifactRetrievalService>.Instance);
        var human = Principal(new Claim(ClaimTypes.NameIdentifier, "owner-1"));
        var system = Principal(new Claim("account_type", "System"), new Claim("scope", "api.artifacts.read"));

        (await service.RequiresDownloadAuthorizationAsync(data.Artifact, human, CancellationToken.None))
            .Should().BeTrue();
        (await service.RequiresDownloadAuthorizationAsync(data.Artifact, system, CancellationToken.None))
            .Should().BeFalse();
    }

    [TestMethod]
    [DataRow((int)CentralDerivativeJobStatus.Pending, true)]
    [DataRow((int)CentralDerivativeJobStatus.Leased, true)]
    [DataRow((int)CentralDerivativeJobStatus.RetryableFailure, true)]
    [DataRow((int)CentralDerivativeJobStatus.Completed, false)]
    [DataRow((int)CentralDerivativeJobStatus.TerminalFailure, false)]
    public async Task RetentionReference_TracksOnlyActiveJobStates(int statusValue, bool expected)
    {
        await using var db = CreateContext();
        var data = Seed(db);
        db.CentralDerivativeJobs.Add(new CentralDerivativeJob
        {
            SourceCentralArtifactId = data.Artifact.Id,
            Status = (CentralDerivativeJobStatus)statusValue,
            MaxAttempts = 3,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var references = new CentralArtifactRetentionReferences(db);

        (await references.IsHeldAsync(data.Artifact.Id, CancellationToken.None)).Should().Be(expected);
    }

    [TestMethod]
    public async Task RetentionReference_CurrentPublicReleaseHoldsUntilWithdrawal()
    {
        await using var db = CreateContext();
        var data = Seed(db);
        await db.SaveChangesAsync();
        var observatoryId = await db.DeviceRegistrations.Where(item => item.Id == data.Artifact.Frame!.RegistrationId)
            .Select(item => item.ObservatoryId).SingleAsync();
        var release = new PublicRecordPublicationDecision
        {
            AuthorityObservatoryId = observatoryId,
            SubjectKind = PublicRecordSubjectKind.Artifact,
            State = PublicationDecisionState.Released,
            CentralArtifactId = data.Artifact.Id,
            ProjectionSchemaVersion = "artifact-v1",
            OccurredAtUtc = DateTimeOffset.UtcNow,
            ActorUserId = "owner-1",
            ReasonCode = "owner-released"
        };
        db.PublicRecordPublicationDecisions.Add(release);
        await db.SaveChangesAsync();
        var references = new CentralArtifactRetentionReferences(db);

        (await references.IsHeldAsync(data.Artifact.Id, CancellationToken.None)).Should().BeTrue();

        db.PublicRecordPublicationDecisions.Add(new PublicRecordPublicationDecision
        {
            AuthorityObservatoryId = observatoryId,
            SubjectKind = PublicRecordSubjectKind.Artifact,
            State = PublicationDecisionState.Withdrawn,
            CentralArtifactId = data.Artifact.Id,
            ProjectionSchemaVersion = "artifact-v1",
            OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(1),
            ActorUserId = "owner-1",
            ReasonCode = "owner-withdrew",
            SupersedesDecisionId = release.Id
        });
        await db.SaveChangesAsync();

        (await references.IsHeldAsync(data.Artifact.Id, CancellationToken.None)).Should().BeFalse();
    }

    private static ClaimsPrincipal Principal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "Test"));

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static (Guid DevicePublicId, CentralArtifact Artifact) Seed(ApplicationDbContext db)
    {
        var devicePublicId = Guid.NewGuid();
        var observatory = new Observatory
        {
            OwnerUserId = "owner-1",
            Name = "Observatory",
            TimeZoneId = "UTC",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsActive = true
        };
        var registration = new DeviceRegistration
        {
            DeviceId = "agent-1",
            DevicePublicId = devicePublicId,
            OwnerUserId = "owner-1",
            OwnerDisplayName = "Owner",
            FriendlyName = "Camera",
            ObservatoryName = "Observatory",
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            VerificationCodeHash = "hash",
            IssuedAtUtc = DateTimeOffset.UtcNow,
            Status = DeviceRegistrationStatus.Active
        };
        var frame = new CentralFrame
        {
            RegistrationId = registration.Id,
            DevicePublicId = devicePublicId,
            ObservatoryId = registration.ObservatoryId,
            AgentId = registration.DeviceId,
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = DateTimeOffset.UtcNow,
            FirstReceivedAtUtc = DateTimeOffset.UtcNow,
            RigId = "rig-1"
        };
        var artifact = new CentralArtifact
        {
            CentralFrameId = frame.Id,
            Frame = frame,
            DevicePublicId = devicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Raw,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete,
            StorageReference = "minio://skymonitor-artifacts/test",
            MediaType = "application/octet-stream",
            ByteLength = 4,
            ChecksumSha256 = new string('A', 64),
            RecipeVersion = "raw-v1",
            ManifestSchemaVersion = "v2",
            IdempotencyKey = Guid.NewGuid().ToString(),
            ReceivedAtUtc = DateTimeOffset.UtcNow
        };
        db.Users.Add(new ApplicationUser
        {
            Id = "owner-1",
            UserName = "owner-1",
            AccountType = AccountType.User
        });
        db.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            UserId = "owner-1",
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = DateTimeOffset.UtcNow
        });
        db.DeviceRegistrations.Add(registration);
        db.CentralFrames.Add(frame);
        db.CentralArtifacts.Add(artifact);
        return (devicePublicId, artifact);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
