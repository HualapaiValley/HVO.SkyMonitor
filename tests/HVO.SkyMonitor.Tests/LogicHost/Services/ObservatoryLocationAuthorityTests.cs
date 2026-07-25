using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class ObservatoryLocationAuthorityTests
{
    [TestMethod]
    public void RepresentationHash_ChangesWhenAuthorityGenerationChanges()
    {
        var observatory = new Observatory
        {
            Name = "Hash generation",
            LatitudeDegrees = 35.347,
            LongitudeDegrees = -113.878,
            ElevationMeters = 520,
            TimeZoneId = "America/Phoenix",
            CurrentLocationVersion = 1,
            CurrentLocationCanonicalSha256 = new string('A', 64),
            IsActive = true
        };
        var original = ObservatoryService.CreateRepresentationSha256(observatory);

        observatory.CurrentLocationVersion = 3;
        observatory.CurrentLocationCanonicalSha256 = new string('B', 64);

        ObservatoryService.CreateRepresentationSha256(observatory).Should().NotBe(original);
    }

    [TestMethod]
    public void AppliesAt_DoesNotFabricateAuthorityBeforeFirstRecordedVersion()
    {
        var effectiveFrom = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        var version = new ObservatoryLocationVersion
        {
            Version = 1,
            EffectiveFromUtc = effectiveFrom
        };

        ObservatoryLocationAuthority.AppliesAt(version, effectiveFrom.AddTicks(-1)).Should().BeFalse();
        ObservatoryLocationAuthority.AppliesAt(version, effectiveFrom).Should().BeTrue();
    }

    [TestMethod]
    public async Task CreateAndLocationUpdate_AppendImmutableVersions()
    {
        await using var context = CreateContext();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero));
        var deploymentAuthority = new DeploymentLocationAuthorityService(context, clock);
        var service = new ObservatoryService(context, clock, deploymentAuthority);
        var created = await service.CreateOrUpdateAsync(new ObservatoryUpsertRequest(
            null, "owner", "Hualapai", 35.347, -113.878, 520, "America/Phoenix", true, 2500));

        created.CurrentLocationVersion.Should().Be(1);
        created.CurrentLocationCanonicalSha256.Should().HaveLength(64);
        (await context.ObservatoryLocationVersions.CountAsync()).Should().Be(1);

        clock.Advance(TimeSpan.FromMinutes(1));
        await service.CreateOrUpdateAsync(new ObservatoryUpsertRequest(
            created.Id, "owner", "Hualapai Valley", 35.347, -113.878, 520, "America/Phoenix", true, 2500));
        (await context.ObservatoryLocationVersions.CountAsync()).Should().Be(1);
        var registration = new DeviceRegistration
        {
            Observatory = created,
            ObservatoryId = created.Id,
            DeviceId = "observatory-move-camera",
            FriendlyName = "Move camera",
            ObservatoryName = created.Name,
            ObservatoryTimeZoneId = created.TimeZoneId,
            OwnerUserId = "owner",
            OwnerDisplayName = "Owner",
            VerificationCodeHash = new string('A', 64),
            DevicePublicId = Guid.NewGuid(),
            Status = DeviceRegistrationStatus.Active,
            IssuedAtUtc = clock.GetUtcNow()
        };
        context.DeviceRegistrations.Add(registration);
        await context.SaveChangesAsync();
        _ = await deploymentAuthority.ProposeAsync(
            registration,
            DeploymentLocationSnapshot.Create(
                "move-camera", 1, "gps", 2, DateTimeOffset.UnixEpoch, null,
                35.347, -113.878, 520, "America/Phoenix"),
            DeploymentLocationSourceKind.Gps,
            "bootstrap:test");
        var deployment = await context.DeviceDeploymentLocationVersions.SingleAsync();
        var frame = new CentralFrame
        {
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId!.Value,
            ObservatoryId = created.Id,
            AgentId = registration.DeviceId,
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = clock.GetUtcNow(),
            FirstReceivedAtUtc = clock.GetUtcNow(),
            LocationEvidenceState = CentralCaptureLocationEvidenceState.ReportedResolved
        };
        frame.Location = new CentralCaptureLocation
        {
            CentralFrame = frame,
            CentralFrameId = frame.Id,
            DeploymentLocation = deployment,
            DeviceDeploymentLocationVersionId = deployment.Id,
            LocationId = deployment.LocationId,
            Version = deployment.Version,
            Source = deployment.Source,
            HorizontalAccuracyMeters = deployment.HorizontalAccuracyMeters,
            EffectiveFromUtc = deployment.EffectiveFromUtc,
            EffectiveUntilUtc = deployment.EffectiveUntilUtc
        };
        context.CentralFrames.Add(frame);
        await context.SaveChangesAsync();
        registration.LocationEvidenceState.Should().Be(RegistrationLocationEvidenceState.DeploymentAcknowledged);
        var pendingCandidate = DeploymentLocationSnapshot.Create(
            "move-camera", 2, "manual-candidate", 2, clock.GetUtcNow(), null,
            40, -113.878, 520, "America/Phoenix");
        var candidateResult = await deploymentAuthority.ProposeAsync(
            registration, pendingCandidate, DeploymentLocationSourceKind.Manual, "active-device-reconciliation");
        candidateResult.Status.Should().Be(DeploymentLocationResolutionStatus.Pending);

        clock.Advance(TimeSpan.FromMinutes(1));
        var moved = await service.CreateOrUpdateAsync(new ObservatoryUpsertRequest(
            created.Id, "owner", "Hualapai Valley", 35.348, -113.878, 520, "America/Phoenix", true, 2500));
        var versions = await context.ObservatoryLocationVersions.OrderBy(item => item.Version).ToArrayAsync();

        moved.CurrentLocationVersion.Should().Be(2);
        versions.Should().HaveCount(2);
        versions[0].SupersededAtUtc.Should().Be(clock.GetUtcNow());
        versions[1].SupersededAtUtc.Should().BeNull();
        versions[0].CanonicalSha256.Should().NotBe(versions[1].CanonicalSha256);
        var evaluations = await context.DeviceDeploymentLocationVersions
            .OrderBy(item => item.ObservatoryLocationVersionNumber)
            .ToArrayAsync();
        evaluations.Should().HaveCount(3);
        var activeEvaluation = evaluations.Single(item =>
            item.ObservatoryLocationVersionNumber == 1 && item.Version == 1);
        var rejectedCandidate = evaluations.Single(item =>
            item.ObservatoryLocationVersionNumber == 1 && item.Version == 2);
        var currentEvaluation = evaluations.Single(item => item.ObservatoryLocationVersionNumber == 2);
        activeEvaluation.Status.Should().Be(DeploymentLocationResolutionStatus.Acknowledged);
        rejectedCandidate.Status.Should().Be(DeploymentLocationResolutionStatus.Rejected);
        rejectedCandidate.ReasonCode.Should().Be("observatory-version-superseded");
        currentEvaluation.Version.Should().Be(1);
        currentEvaluation.Status.Should().Be(DeploymentLocationResolutionStatus.Pending);
        currentEvaluation.ReasonCode.Should().Be("observatory-location-changed");
        frame.Location!.DeviceDeploymentLocationVersionId.Should().Be(activeEvaluation.Id);
        registration.LocationEvidenceState.Should().Be(RegistrationLocationEvidenceState.DeploymentPending);
        frame.LocationEvidenceState.Should().Be(CentralCaptureLocationEvidenceState.ReportedResolved);
        (await context.DeploymentLocationResolutionAudits.CountAsync()).Should().Be(4);

        clock.Advance(TimeSpan.FromMinutes(1));
        var movedAgain = await service.CreateOrUpdateAsync(new ObservatoryUpsertRequest(
            created.Id, "owner", "Hualapai Valley", 35.349, -113.878, 520,
            "America/Phoenix", true, 2500));
        var latestEvaluation = await context.DeviceDeploymentLocationVersions.SingleAsync(item =>
            item.ObservatoryLocationVersionNumber == 3);
        movedAgain.CurrentLocationVersion.Should().Be(3);
        latestEvaluation.Status.Should().Be(DeploymentLocationResolutionStatus.Pending);
        latestEvaluation.ReasonCode.Should().Be("observatory-location-changed");
        latestEvaluation.Version.Should().Be(1);
        (await context.DeviceDeploymentLocationVersions.CountAsync()).Should().Be(4);

        var staleResolution = await deploymentAuthority.ResolveAsync(
            currentEvaluation.Id,
            "owner",
            DeploymentLocationResolutionStatus.Rejected,
            "stale-decision",
            currentEvaluation.ConcurrencyToken);
        staleResolution.Status.Should().Be(DeploymentLocationMutationStatus.StaleAuthority);
        var currentResolution = await deploymentAuthority.ResolveAsync(
            latestEvaluation.Id,
            "owner",
            DeploymentLocationResolutionStatus.Acknowledged,
            "current-decision",
            latestEvaluation.ConcurrencyToken);
        currentResolution.Status.Should().Be(DeploymentLocationMutationStatus.Applied);
        frame.Location.DeviceDeploymentLocationVersionId.Should().Be(activeEvaluation.Id);
        frame.LocationEvidenceState.Should().Be(CentralCaptureLocationEvidenceState.ReportedResolved);
        (await context.DeviceDeploymentLocationVersions.CountAsync(item =>
            item.Status == DeploymentLocationResolutionStatus.Pending)).Should().Be(0);
    }

    [TestMethod]
    public async Task Backfill_NormalizesWindowsTimeZoneAndLeavesInvalidLegacyLocationForOwnerRepair()
    {
        await using var context = CreateContext();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero));
        var windows = new Observatory
        {
            OwnerUserId = "owner",
            Name = "Windows time zone",
            LatitudeDegrees = 35,
            LongitudeDegrees = -113,
            ElevationMeters = 500,
            TimeZoneId = "Pacific Standard Time",
            CreatedAtUtc = clock.GetUtcNow(),
            IsActive = true
        };
        var invalid = new Observatory
        {
            OwnerUserId = "owner",
            Name = "Invalid time zone",
            LatitudeDegrees = 35,
            LongitudeDegrees = -113,
            ElevationMeters = 500,
            TimeZoneId = "Not/A-Time-Zone",
            CreatedAtUtc = clock.GetUtcNow(),
            IsActive = true
        };
        context.Observatories.AddRange(windows, invalid);
        await context.SaveChangesAsync();

        var count = await ObservatoryLocationBackfill.RunAsync(context, clock);

        count.Should().Be(1);
        var normalizedWindows = await context.Observatories.SingleAsync(item => item.Id == windows.Id);
        var invalidLegacy = await context.Observatories.SingleAsync(item => item.Id == invalid.Id);
        normalizedWindows.TimeZoneId.Should().Be("America/Los_Angeles");
        normalizedWindows.CurrentLocationVersion.Should().Be(1);
        invalidLegacy.CurrentLocationVersion.Should().BeNull();

        var service = new ObservatoryService(
            context,
            clock,
            new DeploymentLocationAuthorityService(context, clock));
        var repaired = await service.CreateOrUpdateAsync(new ObservatoryUpsertRequest(
            invalidLegacy.Id, "owner", invalidLegacy.Name, 35, -113, 500, "UTC", true));
        repaired.CurrentLocationVersion.Should().Be(1);
        repaired.TimeZoneId.Should().Be("UTC");
    }

    [TestMethod]
    public async Task Create_RejectsInvalidPortableLocation()
    {
        await using var context = CreateContext();
        var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = new ObservatoryService(
            context,
            clock,
            new DeploymentLocationAuthorityService(context, clock));

        Func<Task> invalidTimeZone = () => service.CreateOrUpdateAsync(new ObservatoryUpsertRequest(
            null, "owner", "Invalid", 0, 0, 0, "Pacific Standard Time", true));
        Func<Task> invalidRadius = () => service.CreateOrUpdateAsync(new ObservatoryUpsertRequest(
            null, "owner", "Invalid", 0, 0, 0, "UTC", true, -1));

        await invalidTimeZone.Should().ThrowAsync<ArgumentException>();
        await invalidRadius.Should().ThrowAsync<ArgumentException>();
        context.Observatories.Should().BeEmpty();
    }

    private static ApplicationDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan duration) => now += duration;
    }
}
