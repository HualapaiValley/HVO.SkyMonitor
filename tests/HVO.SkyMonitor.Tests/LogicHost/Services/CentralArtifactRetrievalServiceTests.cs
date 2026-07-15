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
        var registration = new DeviceRegistration
        {
            DeviceId = "agent-1",
            DevicePublicId = devicePublicId,
            OwnerUserId = "owner-1",
            OwnerDisplayName = "Owner",
            FriendlyName = "Camera",
            ObservatoryName = "Observatory",
            ObservatoryId = Guid.NewGuid(),
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
        db.DeviceRegistrations.Add(registration);
        db.CentralFrames.Add(frame);
        db.CentralArtifacts.Add(artifact);
        return (devicePublicId, artifact);
    }
}
