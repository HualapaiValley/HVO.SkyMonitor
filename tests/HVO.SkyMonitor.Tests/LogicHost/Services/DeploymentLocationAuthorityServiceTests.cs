using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class DeploymentLocationAuthorityServiceTests
{
    [TestMethod]
    public async Task ProposeAsync_UnconfiguredBoundaryPersistsIdempotentPendingProposal()
    {
        await using var context = CreateContext();
        var registration = await SeedRegistrationAsync(context, allowedRadiusMeters: null);
        var service = new DeploymentLocationAuthorityService(context, new FixedTimeProvider(ProposalUtc));
        var deployment = CreateDeployment();

        var first = await service.ProposeAsync(
            registration, deployment, DeploymentLocationSourceKind.Manual, "bootstrap:test");
        await context.SaveChangesAsync();
        var second = await service.ProposeAsync(
            registration, deployment, DeploymentLocationSourceKind.Manual, "bootstrap:test");

        first.Status.Should().Be(DeploymentLocationResolutionStatus.Pending);
        first.ReasonCode.Should().Be("boundary-unconfigured");
        second.Should().Be(first);
        registration.LocationEvidenceState.Should().Be(RegistrationLocationEvidenceState.DeploymentPending);
        (await context.DeviceDeploymentLocationVersions.CountAsync()).Should().Be(1);
        (await context.DeploymentLocationResolutionAudits.CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task ProposeAsync_WithinConfiguredBoundaryAcknowledges()
    {
        await using var context = CreateContext();
        var registration = await SeedRegistrationAsync(context, allowedRadiusMeters: 1000);
        var service = new DeploymentLocationAuthorityService(context, new FixedTimeProvider(ProposalUtc));

        var result = await service.ProposeAsync(
            registration, CreateDeployment(), DeploymentLocationSourceKind.Gps, "bootstrap:test");

        result.Status.Should().Be(DeploymentLocationResolutionStatus.Acknowledged);
        result.ReasonCode.Should().Be("within-observatory-boundary");
        result.ResolvedAtUtc.Should().Be(ProposalUtc);
        registration.LocationEvidenceState.Should().Be(RegistrationLocationEvidenceState.DeploymentAcknowledged);
    }

    [TestMethod]
    public async Task ProposeAsync_InheritedElevationMismatchRequiresOwnerResolution()
    {
        await using var context = CreateContext();
        var registration = await SeedRegistrationAsync(context, allowedRadiusMeters: 1000);
        var service = new DeploymentLocationAuthorityService(context, new FixedTimeProvider(ProposalUtc));
        var deployment = CreateDeployment() with { ElevationMeters = 521 };
        deployment = deployment with
        {
            CanonicalSha256 = DeploymentLocationSnapshot.Create(
                deployment.LocationId,
                deployment.Version,
                deployment.Source,
                deployment.HorizontalAccuracyMeters,
                deployment.EffectiveFromUtc,
                deployment.EffectiveUntilUtc,
                deployment.LatitudeDegrees,
                deployment.LongitudeDegrees,
                deployment.ElevationMeters,
                deployment.TimeZoneId).CanonicalSha256
        };

        var result = await service.ProposeAsync(
            registration, deployment, DeploymentLocationSourceKind.Inherited, "bootstrap:test");

        result.Status.Should().Be(DeploymentLocationResolutionStatus.Pending);
        result.ReasonCode.Should().Be("inherited-observatory-mismatch");
        registration.LocationEvidenceState.Should().Be(RegistrationLocationEvidenceState.DeploymentPending);
    }

    [TestMethod]
    public async Task ProposeAsync_RejectsUndefinedSourceKind()
    {
        await using var context = CreateContext();
        var registration = await SeedRegistrationAsync(context, allowedRadiusMeters: 1000);
        var service = new DeploymentLocationAuthorityService(context, new FixedTimeProvider(ProposalUtc));

        Func<Task> action = () => service.ProposeAsync(
            registration, CreateDeployment(), (DeploymentLocationSourceKind)99, "bootstrap:test");

        await action.Should().ThrowAsync<DeviceRegistrationException>();
        context.DeviceDeploymentLocationVersions.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(-1, false)]
    [DataRow(0, true)]
    [DataRow(119_999, true)]
    [DataRow(120_000, false)]
    public void AppliesAt_UsesHalfOpenDeploymentInterval(int offsetMilliseconds, bool expected)
    {
        var effectiveFromUtc = ProposalUtc.AddMinutes(1);
        var deployment = new DeviceDeploymentLocationVersion
        {
            EffectiveFromUtc = effectiveFromUtc,
            EffectiveUntilUtc = effectiveFromUtc.AddMinutes(2)
        };

        DeploymentLocationAuthorityService.AppliesAt(
            deployment, effectiveFromUtc.AddMilliseconds(offsetMilliseconds)).Should().Be(expected);
    }

    [TestMethod]
    public async Task ResolveAsync_EnforcesOwnerAndRetainsAudit()
    {
        var databaseName = Guid.NewGuid().ToString();
        Guid proposalId;
        Guid concurrencyToken;
        await using (var setupContext = CreateContext(databaseName))
        {
            var registration = await SeedRegistrationAsync(setupContext, allowedRadiusMeters: null);
            var setupService = new DeploymentLocationAuthorityService(setupContext, new FixedTimeProvider(ProposalUtc));
            _ = await setupService.ProposeAsync(
                registration, CreateDeployment(), DeploymentLocationSourceKind.Manual, "bootstrap:test");
            await setupContext.SaveChangesAsync();
            var proposal = await setupContext.DeviceDeploymentLocationVersions.SingleAsync();
            proposalId = proposal.Id;
            concurrencyToken = proposal.ConcurrencyToken;
        }
        await using var context = CreateContext(databaseName);
        var service = new DeploymentLocationAuthorityService(context, new FixedTimeProvider(ProposalUtc));

        var foreign = await service.ResolveAsync(
            proposalId,
            "foreign-owner",
            DeploymentLocationResolutionStatus.Acknowledged,
            "approved",
            concurrencyToken);

        var resolved = await service.ResolveAsync(
            proposalId,
            "owner",
            DeploymentLocationResolutionStatus.Acknowledged,
            "owner-approved",
            concurrencyToken);

        foreign.Status.Should().Be(DeploymentLocationMutationStatus.NotFound);
        resolved.Status.Should().Be(DeploymentLocationMutationStatus.Applied);
        resolved.Proposal!.ReasonCode.Should().Be("owner-approved");
        (await context.DeploymentLocationResolutionAudits.CountAsync()).Should().Be(2);
    }

    [TestMethod]
    public async Task ResolveAsync_WithExpectedTokenRejectsStaleMutationAndRotatesOnSuccess()
    {
        await using var context = CreateContext();
        var registration = await SeedRegistrationAsync(context, allowedRadiusMeters: null);
        var service = new DeploymentLocationAuthorityService(context, new FixedTimeProvider(ProposalUtc));
        _ = await service.ProposeAsync(
            registration, CreateDeployment(), DeploymentLocationSourceKind.Manual, "bootstrap:test");
        await context.SaveChangesAsync();
        var proposal = await context.DeviceDeploymentLocationVersions.SingleAsync();
        var originalToken = proposal.ConcurrencyToken;

        var stale = await service.ResolveAsync(
            proposal.Id,
            "owner",
            DeploymentLocationResolutionStatus.Acknowledged,
            "owner-approved",
            Guid.NewGuid());
        var applied = await service.ResolveAsync(
            proposal.Id,
            "owner",
            DeploymentLocationResolutionStatus.Acknowledged,
            "owner-approved",
            originalToken);
        var invalidTransition = await service.ResolveAsync(
            proposal.Id,
            "owner",
            DeploymentLocationResolutionStatus.Rejected,
            "owner-rejected",
            applied.Proposal!.ConcurrencyToken);

        stale.Status.Should().Be(DeploymentLocationMutationStatus.PreconditionFailed);
        applied.Status.Should().Be(DeploymentLocationMutationStatus.Applied);
        applied.Proposal!.ConcurrencyToken.Should().NotBe(originalToken);
        invalidTransition.Status.Should().Be(DeploymentLocationMutationStatus.InvalidTransition);
        (await context.DeploymentLocationResolutionAudits.CountAsync()).Should().Be(2);
    }

    [TestMethod]
    public async Task ListAndGetAsync_FilterAtOwnerQueryBoundary()
    {
        await using var context = CreateContext();
        var owner = await SeedRegistrationAsync(context, allowedRadiusMeters: null);
        var foreign = await SeedRegistrationAsync(context, allowedRadiusMeters: null, "foreign", "camera-foreign");
        var service = new DeploymentLocationAuthorityService(context, new FixedTimeProvider(ProposalUtc));
        _ = await service.ProposeAsync(owner, CreateDeployment(), DeploymentLocationSourceKind.Manual, "bootstrap:test");
        _ = await service.ProposeAsync(foreign, CreateDeployment(), DeploymentLocationSourceKind.Manual, "bootstrap:test");
        await context.SaveChangesAsync();
        var foreignProposalId = foreign.DeploymentLocations.Single().Id;

        var proposals = await service.ListAsync(
            "owner", DeploymentLocationResolutionStatus.Pending, 100);
        var hidden = await service.GetAsync(foreignProposalId, "owner");

        proposals.Proposals.Should().ContainSingle(item => item.RegistrationId == owner.Id);
        hidden.Should().BeNull();
    }

    [TestMethod]
    public async Task ViewerCanReadProposalButCannotResolveIt()
    {
        await using var context = CreateContext();
        var registration = await SeedRegistrationAsync(context, allowedRadiusMeters: null);
        context.Users.Add(new ApplicationUser
        {
            Id = "viewer",
            UserName = "viewer",
            AccountType = AccountType.User
        });
        context.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            ObservatoryId = registration.ObservatoryId,
            UserId = "viewer",
            Role = ObservatoryMembershipRole.Viewer,
            AddedAtUtc = ProposalUtc
        });
        await context.SaveChangesAsync();
        var service = new DeploymentLocationAuthorityService(context, new FixedTimeProvider(ProposalUtc));
        _ = await service.ProposeAsync(
            registration, CreateDeployment(), DeploymentLocationSourceKind.Manual, "bootstrap:test");
        await context.SaveChangesAsync();
        var proposal = await context.DeviceDeploymentLocationVersions.SingleAsync();

        var page = await service.ListAsync("viewer", DeploymentLocationResolutionStatus.Pending, 100);
        var detail = await service.GetAsync(proposal.Id, "viewer");
        var resolution = await service.ResolveAsync(
            proposal.Id,
            "viewer",
            DeploymentLocationResolutionStatus.Acknowledged,
            "viewer-approved",
            proposal.ConcurrencyToken);

        page.Proposals.Should().ContainSingle(item => item.Id == proposal.Id);
        detail.Should().NotBeNull();
        resolution.Status.Should().Be(DeploymentLocationMutationStatus.NotFound);
    }

    [TestMethod]
    public async Task DelayedOlderProposal_DoesNotOverwriteCurrentRegistrationEvidence()
    {
        await using var context = CreateContext();
        var registration = await SeedRegistrationAsync(context, allowedRadiusMeters: 1000);
        var service = new DeploymentLocationAuthorityService(context, new FixedTimeProvider(ProposalUtc));
        var current = DeploymentLocationSnapshot.Create(
            "camera-hualapai", 2, "gps", 2, ProposalUtc, null,
            35.347, -113.878, 520, "America/Phoenix");
        var delayed = DeploymentLocationSnapshot.Create(
            "camera-hualapai", 1, "manual", 2, ProposalUtc.AddDays(-1), ProposalUtc,
            40, -113.878, 520, "America/Phoenix");

        var currentResult = await service.ProposeAsync(
            registration, current, DeploymentLocationSourceKind.Gps, "bootstrap:test");
        var delayedResult = await service.ProposeAsync(
            registration, delayed, DeploymentLocationSourceKind.Manual, "active-device-reconciliation");

        currentResult.Status.Should().Be(DeploymentLocationResolutionStatus.Acknowledged);
        delayedResult.Status.Should().Be(DeploymentLocationResolutionStatus.Pending);
        registration.LocationEvidenceState.Should().Be(RegistrationLocationEvidenceState.DeploymentAcknowledged);
    }

    [TestMethod]
    public async Task ProposeAsync_RejectsConflictingSourceClassificationAndHistoryOrder()
    {
        await using var context = CreateContext();
        var registration = await SeedRegistrationAsync(context, allowedRadiusMeters: 1000);
        var service = new DeploymentLocationAuthorityService(context, new FixedTimeProvider(ProposalUtc));
        var current = DeploymentLocationSnapshot.Create(
            "camera-hualapai", 2, "gps", 2, ProposalUtc, null,
            35.347, -113.878, 520, "America/Phoenix");
        await service.ProposeAsync(
            registration, current, DeploymentLocationSourceKind.Gps, "bootstrap:test");

        Func<Task> conflictingSource = () => service.ProposeAsync(
            registration, current, DeploymentLocationSourceKind.Manual, "bootstrap:test");
        var contradictoryOlder = DeploymentLocationSnapshot.Create(
            "camera-hualapai", 1, "manual", 2, ProposalUtc.AddMinutes(1), null,
            35.347, -113.878, 520, "America/Phoenix");
        Func<Task> contradictoryHistory = () => service.ProposeAsync(
            registration, contradictoryOlder, DeploymentLocationSourceKind.Manual, "bootstrap:test");

        await conflictingSource.Should().ThrowAsync<DeviceRegistrationException>();
        await contradictoryHistory.Should().ThrowAsync<DeviceRegistrationException>();
        (await context.DeviceDeploymentLocationVersions.CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task NewerCorrectedCandidate_ClosesOlderPendingProposal()
    {
        await using var context = CreateContext();
        var registration = await SeedRegistrationAsync(context, allowedRadiusMeters: null);
        var service = new DeploymentLocationAuthorityService(context, new FixedTimeProvider(ProposalUtc));
        var first = DeploymentLocationSnapshot.Create(
            "camera-hualapai", 2, "manual", 2, ProposalUtc, null,
            35.4, -113.878, 520, "America/Phoenix");
        var corrected = DeploymentLocationSnapshot.Create(
            "camera-hualapai", 3, "manual-corrected", 1, ProposalUtc.AddMilliseconds(1), null,
            35.347, -113.878, 520, "America/Phoenix");

        var pending = await service.ProposeAsync(
            registration, first, DeploymentLocationSourceKind.Manual, "active-device-reconciliation");
        var replacement = await service.ProposeAsync(
            registration, corrected, DeploymentLocationSourceKind.Gps, "active-device-reconciliation");

        pending.Status.Should().Be(DeploymentLocationResolutionStatus.Pending);
        replacement.Status.Should().Be(DeploymentLocationResolutionStatus.Pending);
        var rows = await context.DeviceDeploymentLocationVersions.OrderBy(item => item.Version).ToArrayAsync();
        rows[0].Status.Should().Be(DeploymentLocationResolutionStatus.Rejected);
        rows[0].ReasonCode.Should().Be("deployment-version-superseded");
        rows[1].Status.Should().Be(DeploymentLocationResolutionStatus.Pending);
        registration.LocationEvidenceState.Should().Be(RegistrationLocationEvidenceState.DeploymentPending);
    }

    private static readonly DateTimeOffset ProposalUtc = new(2026, 7, 24, 2, 0, 0, TimeSpan.Zero);

    private static DeploymentLocationSnapshot CreateDeployment()
        => DeploymentLocationSnapshot.Create(
            "camera-hualapai",
            1,
            "operator-pinned",
            5,
            DateTimeOffset.UnixEpoch,
            null,
            35.3471,
            -113.878,
            521,
            "America/Phoenix");

    private static async Task<DeviceRegistration> SeedRegistrationAsync(
        ApplicationDbContext context,
        double? allowedRadiusMeters,
        string ownerUserId = "owner",
        string deviceId = "camera-test")
    {
        var observatory = new Observatory
        {
            OwnerUserId = ownerUserId,
            Name = "Hualapai",
            LatitudeDegrees = 35.347,
            LongitudeDegrees = -113.878,
            ElevationMeters = 520,
            TimeZoneId = "America/Phoenix",
            AllowedDeploymentRadiusMeters = allowedRadiusMeters,
            CreatedAtUtc = ProposalUtc.AddDays(-1),
            IsActive = true
        };
        var registration = new DeviceRegistration
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            DeviceId = deviceId,
            FriendlyName = "Camera Test",
            ObservatoryName = observatory.Name,
            ObservatoryTimeZoneId = observatory.TimeZoneId,
            OwnerUserId = observatory.OwnerUserId,
            OwnerDisplayName = "Owner",
            VerificationCodeHash = new string('A', 64),
            DevicePublicId = Guid.NewGuid(),
            Status = DeviceRegistrationStatus.Active,
            IssuedAtUtc = ProposalUtc.AddMinutes(-1)
        };
        if (!context.Users.Local.Any(user => user.Id == ownerUserId))
        {
            context.Users.Add(new ApplicationUser
            {
                Id = ownerUserId,
                UserName = ownerUserId,
                AccountType = AccountType.User
            });
        }
        context.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            UserId = ownerUserId,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = ProposalUtc.AddDays(-1)
        });
        context.AddRange(observatory, registration);
        await context.SaveChangesAsync();
        return registration;
    }

    private static ApplicationDbContext CreateContext(string? databaseName = null)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .Options);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
