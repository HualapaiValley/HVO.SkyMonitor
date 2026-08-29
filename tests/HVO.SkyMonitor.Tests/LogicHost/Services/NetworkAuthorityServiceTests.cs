using FluentAssertions;
using System.Globalization;
using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class NetworkAuthorityServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 2, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task PublicationProfileAndLocation_AreVersionedIndependentOwnerDecisions()
    {
        await using var context = CreateContext();
        var data = Seed(context);
        var service = new ObservatoryPublicationService(context, new FixedTimeProvider());

        var profile = await service.SetProfileAsync(
            data.Observatory.Id,
            data.Owner.Id,
            new ObservatoryPublicationProfileRequest(
                "  SUMMIT-NORTH  ",
                "Summit North",
                "Public station description",
                ObservatoryProfileVisibility.Public,
                true,
                false,
                "owner-approved"));
        var replay = await service.SetProfileAsync(
            data.Observatory.Id,
            data.Owner.Id,
            new ObservatoryPublicationProfileRequest(
                "summit-north",
                "Summit North",
                "Public station description",
                ObservatoryProfileVisibility.Public,
                true,
                false,
                "owner-approved"));
        var denied = await service.SetProfileAsync(
            data.Observatory.Id,
            data.Target.Id,
            new ObservatoryPublicationProfileRequest(
                "summit-north",
                "Changed",
                "",
                ObservatoryProfileVisibility.Private,
                false,
                false,
                "unauthorized"));
        var invalidExact = await service.SetLocationDisclosureAsync(
            data.Observatory.Id,
            data.Owner.Id,
            new ObservatoryLocationDisclosureRequest(
                ObservatoryLocationDisclosureLevel.Exact,
                null,
                null,
                0,
                0,
                0,
                "wrong-exact-location"));
        var approximate = await service.SetLocationDisclosureAsync(
            data.Observatory.Id,
            data.Owner.Id,
            new ObservatoryLocationDisclosureRequest(
                ObservatoryLocationDisclosureLevel.Approximate,
                "US-HI",
                "Hawaii",
                19.7,
                -155.1,
                25_000,
                "owner-approved"));

        profile.Should().Be(new ObservatoryPublicationMutationResult(
            ObservatoryPublicationMutationOutcome.Applied, 1));
        replay.Should().Be(new ObservatoryPublicationMutationResult(
            ObservatoryPublicationMutationOutcome.Unchanged, 1));
        denied.Outcome.Should().Be(ObservatoryPublicationMutationOutcome.NotFoundOrDenied);
        invalidExact.Outcome.Should().Be(ObservatoryPublicationMutationOutcome.Invalid);
        approximate.Should().Be(new ObservatoryPublicationMutationResult(
            ObservatoryPublicationMutationOutcome.Applied, 1));
        var storedProfile = await context.ObservatoryPublicationProfileVersions.SingleAsync();
        storedProfile.PublicSlug.Should().Be("summit-north");
        storedProfile.CanonicalSha256.Should().MatchRegex("^[0-9A-F]{64}$");
        var disclosure = await context.ObservatoryLocationDisclosureVersions.SingleAsync();
        disclosure.SourceObservatoryLocationVersionId.Should().Be(data.Location.Id);
        disclosure.PublicPrecisionMeters.Should().Be(25_000);
    }

    [TestMethod]
    public async Task LogicalCamera_ReplacementRetainsImmutableInstallationHistory()
    {
        await using var context = CreateContext();
        var data = Seed(context);
        var firstRegistration = AddRegistration(context, data.Observatory, "first");
        var secondRegistration = AddRegistration(context, data.Observatory, "second");
        await context.SaveChangesAsync();
        var service = new LogicalCameraService(context, new FixedTimeProvider());

        var created = await service.CreateAsync(
            data.Observatory.Id, data.Owner.Id, "all-sky", "All Sky", "North dome");
        var first = await service.AssignInstallationAsync(
            created.LogicalCameraId!.Value, firstRegistration.Id, data.Owner.Id, "initial-installation");
        var replacement = await service.AssignInstallationAsync(
            created.LogicalCameraId.Value, secondRegistration.Id, data.Owner.Id, "cameraagent-replacement");

        created.Outcome.Should().Be(LogicalCameraMutationOutcome.Applied);
        first.Outcome.Should().Be(LogicalCameraMutationOutcome.Applied);
        replacement.Outcome.Should().Be(LogicalCameraMutationOutcome.Applied);
        var installations = await context.LogicalCameraInstallations.OrderBy(item => item.AssignedAtUtc)
            .ThenBy(item => item.Id).ToArrayAsync();
        installations.Should().HaveCount(2);
        installations.Should().ContainSingle(item => item.RegistrationId == firstRegistration.Id
            && item.RetiredAtUtc == Now && item.RetirementReasonCode == "installation-replaced");
        installations.Should().ContainSingle(item => item.RegistrationId == secondRegistration.Id
            && item.RetiredAtUtc == null && item.ReplacesInstallationId == first.InstallationId);

        installations[0].AssignmentReasonCode = "changed";
        Func<Task> mutateHistory = () => context.SaveChangesAsync();
        await mutateHistory.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*terminal retirement*");
    }

    [TestMethod]
    public async Task Invitation_AcceptanceCreatesMembershipAndImmutableDispositionWithoutPlaintextToken()
    {
        await using var context = CreateContext();
        var data = Seed(context);
        var service = new ObservatoryInvitationService(context, new FixedTimeProvider());

        var issued = await service.IssueAsync(
            data.Observatory.Id,
            data.Owner.Id,
            data.Target.Id,
            ObservatoryMembershipRole.Viewer,
            TimeSpan.FromDays(2));
        var wrongToken = await service.AcceptAsync(
            issued.InvitationId!.Value, data.Target.Id, "wrong-token");
        var accepted = await service.AcceptAsync(
            issued.InvitationId.Value, data.Target.Id, issued.AcceptanceToken!);

        issued.Outcome.Should().Be(ObservatoryInvitationMutationOutcome.Applied);
        issued.AcceptanceToken.Should().NotBeNullOrWhiteSpace();
        wrongToken.Should().Be(ObservatoryInvitationMutationOutcome.NotFoundOrDenied);
        accepted.Should().Be(ObservatoryInvitationMutationOutcome.Applied);
        var invitation = await context.ObservatoryInvitations.SingleAsync();
        invitation.AcceptanceTokenSha256.Should().MatchRegex("^[0-9A-F]{64}$")
            .And.NotBe(issued.AcceptanceToken);
        (await context.ObservatoryMemberships.SingleAsync(item => item.UserId == data.Target.Id))
            .Role.Should().Be(ObservatoryMembershipRole.Viewer);
        (await context.ObservatoryMembershipAudits.SingleAsync(item => item.TargetUserId == data.Target.Id))
            .ReasonCode.Should().Be("invitation-accepted");
        (await context.ObservatoryInvitationDispositions.SingleAsync()).Action
            .Should().Be(ObservatoryInvitationDispositionAction.Accepted);
    }

    [TestMethod]
    public async Task PublicationDecision_AppendsReleaseAndWithdrawalWithoutMutatingHistory()
    {
        await using var context = CreateContext();
        var data = Seed(context);
        var cameras = new LogicalCameraService(context, new FixedTimeProvider());
        var created = await cameras.CreateAsync(
            data.Observatory.Id, data.Owner.Id, "public-camera", "Public Camera", "Released camera");
        var service = new PublicRecordPublicationService(context, new FixedTimeProvider());
        var subject = new PublicRecordSubject(
            PublicRecordSubjectKind.LogicalCamera, created.LogicalCameraId!.Value);

        var released = await service.DecideAsync(
            data.Observatory.Id,
            data.Owner.Id,
            subject,
            PublicationDecisionState.Released,
            "public-camera-v1",
            "owner-released");
        var replay = await service.DecideAsync(
            data.Observatory.Id,
            data.Owner.Id,
            subject,
            PublicationDecisionState.Released,
            "public-camera-v1",
            "owner-released");
        var withdrawn = await service.DecideAsync(
            data.Observatory.Id,
            data.Owner.Id,
            subject,
            PublicationDecisionState.Withdrawn,
            "public-camera-v1",
            "owner-withdrew");

        released.Outcome.Should().Be(PublicRecordPublicationOutcome.Applied);
        replay.Should().Be(released with { Outcome = PublicRecordPublicationOutcome.Unchanged });
        withdrawn.Outcome.Should().Be(PublicRecordPublicationOutcome.Applied);
        var decisions = await context.PublicRecordPublicationDecisions.OrderBy(item => item.OccurredAtUtc)
            .ThenBy(item => item.Id).ToArrayAsync();
        decisions.Should().HaveCount(2);
        decisions.Should().ContainSingle(item => item.State == PublicationDecisionState.Withdrawn
            && item.SupersedesDecisionId == released.DecisionId);

        decisions[0].ReasonCode = "changed";
        Func<Task> mutate = () => context.SaveChangesAsync();
        await mutate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*immutable*");
    }

    [TestMethod]
    public async Task EventRelease_NotifiesEnabledSubscribersOnce()
    {
        await using var context = CreateContext();
        var data = Seed(context);
        var disabledSubscriber = new ApplicationUser
        {
            Id = "disabled-subscriber",
            UserName = "disabled-subscriber",
            AccountType = AccountType.User
        };
        context.Users.Add(disabledSubscriber);
        context.RegisteredUserSubscriptions.AddRange(
            new RegisteredUserSubscription
            {
                UserId = data.Target.Id,
                Kind = RegisteredUserSubscriptionKind.VerifiedEvent,
                CreatedUtc = Now
            },
            new RegisteredUserSubscription
            {
                UserId = disabledSubscriber.Id,
                Kind = RegisteredUserSubscriptionKind.VerifiedEvent,
                CreatedUtc = Now
            });
        context.RegisteredUserNotificationPreferences.Add(new RegisteredUserNotificationPreference
        {
            UserId = disabledSubscriber.Id,
            InAppEnabled = false,
            UpdatedUtc = Now
        });
        var registration = AddRegistration(context, data.Observatory, "event-release");
        var frame = new CentralFrame
        {
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId!.Value,
            ObservatoryId = data.Observatory.Id,
            AgentId = registration.DeviceId,
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = Now,
            FirstReceivedAtUtc = Now,
            RigId = "event-rig"
        };
        var artifact = new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            DevicePublicId = registration.DevicePublicId!.Value,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Raw,
            RecipeVersion = "raw-v1",
            ManifestSchemaVersion = "v2",
            MediaType = "application/octet-stream",
            ByteLength = 4,
            ChecksumSha256 = new string('D', 64),
            StorageReference = "private-event-object",
            ReceivedAtUtc = Now,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        var eventRecord = new CentralTransientEventRecord
        {
            AgentId = registration.DeviceId,
            EventId = Guid.NewGuid(),
            EventCreatedUtc = Now
        };
        var version = new CentralTransientEventVersionRecord
        {
            EventVersionId = Guid.NewGuid(),
            Event = eventRecord,
            CentralTransientEventId = eventRecord.Id,
            Version = 1,
            State = TransientEventState.Validated,
            VersionCreatedUtc = Now,
            FirstObservedUtc = Now,
            LastObservedUtc = Now,
            SchemaVersion = "transient-event-v1",
            CanonicalEventJson = "{}",
            CanonicalEventSha256 = new string('E', 64),
            CanonicalEventByteLength = 2
        };
        var assessment = new CentralTransientAssessmentRecord
        {
            AssessmentId = Guid.NewGuid(),
            Event = eventRecord,
            CentralTransientEventId = eventRecord.Id,
            CreatedUtc = Now,
            Authority = TransientAssessmentAuthority.Authoritative,
            Classification = TransientClassification.Meteor,
            MeteorSeverity = TransientMeteorSeverity.Meteor,
            ConfidenceMillionths = 900_000
        };
        var current = new CentralTransientEventCurrent
        {
            CentralTransientEventId = eventRecord.Id,
            Event = eventRecord,
            LatestEventVersionId = version.EventVersionId,
            LatestEventVersion = version,
            LatestVersion = 1,
            ActiveAssessmentId = assessment.AssessmentId,
            ActiveAssessment = assessment,
            ReviewState = CentralTransientReviewState.Reviewed,
            EffectiveClassification = TransientClassification.Meteor,
            EffectiveMeteorSeverity = TransientMeteorSeverity.Meteor,
            EffectiveConfidenceMillionths = 900_000,
            UpdatedUtc = Now
        };
        var observation = new CentralTransientObservationRecord
        {
            ObservationId = Guid.NewGuid(),
            Event = eventRecord,
            CentralTransientEventId = eventRecord.Id,
            SourceReferenceId = Guid.NewGuid()
        };
        var source = new CentralTransientObservationSourceReference
        {
            Observation = observation,
            ObservationId = observation.ObservationId,
            Artifact = artifact,
            CentralArtifactId = artifact.Id
        };
        context.AddRange(frame, artifact, eventRecord, version, assessment, current, observation, source);
        await context.SaveChangesAsync();
        var service = new PublicRecordPublicationService(context, new FixedTimeProvider());
        var subject = new PublicRecordSubject(
            PublicRecordSubjectKind.TransientEvent,
            eventRecord.Id,
            version.EventVersionId);

        assessment.Authority = TransientAssessmentAuthority.Provisional;
        await context.SaveChangesAsync();
        var provisional = await service.DecideAsync(
            data.Observatory.Id,
            data.Owner.Id,
            subject,
            PublicationDecisionState.Released,
            "public-event-v1",
            "owner-released");
        assessment.Authority = TransientAssessmentAuthority.Authoritative;
        await context.SaveChangesAsync();
        var release = await service.DecideAsync(
            data.Observatory.Id,
            data.Owner.Id,
            subject,
            PublicationDecisionState.Released,
            "public-event-v1",
            "owner-released");
        var replay = await service.DecideAsync(
            data.Observatory.Id,
            data.Owner.Id,
            subject,
            PublicationDecisionState.Released,
            "public-event-v1",
            "owner-released");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, data.Owner.Id), new Claim("account_type", "User")],
            IdentityConstants.ApplicationScheme));
        var releaseDecision = await context.PublicRecordPublicationDecisions
            .SingleAsync(item => item.CentralTransientEventId == eventRecord.Id);
        releaseDecision.AuthorityObservatoryId.Should().Be(data.Observatory.Id);
        releaseDecision.SourceEventVersionId.Should().Be(version.EventVersionId);
        releaseDecision.State.Should().Be(PublicationDecisionState.Released);
        var readService = new CentralTransientEventReadService(context);
        var releasedAuthorities = await readService.ListPublicationAuthoritiesAsync(
            principal, [eventRecord.Id], CancellationToken.None);
        var withdrawal = await service.DecideAsync(
            data.Observatory.Id,
            data.Owner.Id,
            subject,
            PublicationDecisionState.Withdrawn,
            "public-event-v1",
            "owner-withdrew");
        var withdrawnAuthorities = await readService.ListPublicationAuthoritiesAsync(
            principal, [eventRecord.Id], CancellationToken.None);

        provisional.Outcome.Should().Be(PublicRecordPublicationOutcome.NotFoundOrDenied);
        release.Outcome.Should().Be(PublicRecordPublicationOutcome.Applied);
        replay.Outcome.Should().Be(PublicRecordPublicationOutcome.Unchanged);
        releasedAuthorities.Should().ContainSingle();
        releasedAuthorities[0].ObservatoryId.Should().Be(data.Observatory.Id);
        releasedAuthorities[0].CurrentState.Should().Be(PublicationDecisionState.Released);
        withdrawal.Outcome.Should().Be(PublicRecordPublicationOutcome.Applied);
        withdrawnAuthorities.Should().ContainSingle();
        withdrawnAuthorities[0].ObservatoryId.Should().Be(data.Observatory.Id);
        withdrawnAuthorities[0].CurrentState.Should().Be(PublicationDecisionState.Withdrawn);
        var notification = await context.RegisteredUserNotifications.SingleAsync();
        notification.UserId.Should().Be(data.Target.Id);
        notification.PublicRecordId.Should().Be(release.PublicId!.Value);
        notification.DeduplicationKey.Should().Be($"verified-event:{release.PublicId.Value:N}");
    }

    [TestMethod]
    public async Task PublicProjection_ContainsOnlyReleasedAllowlistedDataAndHidesStaleCoordinates()
    {
        await using var context = CreateContext();
        var data = Seed(context);
        var publication = new ObservatoryPublicationService(context, new FixedTimeProvider());
        _ = await publication.SetProfileAsync(
            data.Observatory.Id,
            data.Owner.Id,
            new ObservatoryPublicationProfileRequest(
                "summit",
                "Summit Station",
                "Released profile",
                ObservatoryProfileVisibility.Public,
                false,
                false,
                "owner-approved"));
        _ = await publication.SetLocationDisclosureAsync(
            data.Observatory.Id,
            data.Owner.Id,
            new ObservatoryLocationDisclosureRequest(
                ObservatoryLocationDisclosureLevel.Approximate,
                "US-HI",
                "Hawaii",
                19.7,
                -155.1,
                25_000,
                "owner-approved"));
        var cameras = new LogicalCameraService(context, new FixedTimeProvider());
        var releasedCamera = await cameras.CreateAsync(
            data.Observatory.Id, data.Owner.Id, "released", "Released Camera", "Visible");
        _ = await cameras.CreateAsync(
            data.Observatory.Id, data.Owner.Id, "private", "Private Camera", "Hidden");
        var decisions = new PublicRecordPublicationService(context, new FixedTimeProvider());
        _ = await decisions.DecideAsync(
            data.Observatory.Id,
            data.Owner.Id,
            new PublicRecordSubject(PublicRecordSubjectKind.LogicalCamera, releasedCamera.LogicalCameraId!.Value),
            PublicationDecisionState.Released,
            "public-camera-v1",
            "owner-released");
        var reads = new PublicNetworkReadService(context);

        var page = await reads.ListObservatoriesAsync(25, null);
        var detail = await reads.GetObservatoryAsync("summit");

        page.Items.Should().ContainSingle();
        detail.Should().NotBeNull();
        detail!.Cameras.Should().ContainSingle(item => item.Slug == "released");
        detail.Cameras.Should().NotContain(item => item.Slug == "private");
        var json = JsonSerializer.Serialize(detail);
        json.Contains(data.Observatory.Id.ToString(), StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        json.Should().NotContain(data.Owner.Id).And.NotContain("155.4");

        data.Location.SupersededAtUtc = Now.AddMinutes(1);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var stale = await reads.GetObservatoryAsync("summit");
        stale!.Observatory.Location.DisclosureLevel.Should().Be(ObservatoryLocationDisclosureLevel.Hidden);
        stale.Observatory.Location.LatitudeDegrees.Should().BeNull();
        stale.Observatory.Location.LongitudeDegrees.Should().BeNull();
    }

    [TestMethod]
    public async Task PublicProjection_FailsClosedForPersistedExactCoordinates()
    {
        await using var context = CreateContext();
        var data = Seed(context);
        context.ObservatoryPublicationProfileVersions.Add(new ObservatoryPublicationProfileVersion
        {
            ObservatoryId = data.Observatory.Id,
            Version = 1,
            PublicSlug = "exact-record",
            PublicDisplayName = "Exact Record",
            ProfileVisibility = ObservatoryProfileVisibility.Public,
            EffectiveFromUtc = Now,
            ActorUserId = data.Owner.Id,
            ReasonCode = "direct-persistence-test",
            CanonicalSha256 = new string('B', 64)
        });
        context.ObservatoryLocationDisclosureVersions.Add(new ObservatoryLocationDisclosureVersion
        {
            ObservatoryId = data.Observatory.Id,
            Version = 1,
            DisclosureLevel = ObservatoryLocationDisclosureLevel.Exact,
            PublicLatitudeDegrees = 19.8,
            PublicLongitudeDegrees = -155.4,
            PublicPrecisionMeters = 1,
            SourceObservatoryLocationVersionId = data.Location.Id,
            EffectiveFromUtc = Now,
            ActorUserId = data.Owner.Id,
            ReasonCode = "direct-persistence-test",
            CanonicalSha256 = new string('C', 64)
        });
        await context.SaveChangesAsync();

        var projection = (await new PublicNetworkReadService(context)
            .GetObservatoryAsync("exact-record"))!.Observatory.Location;

        projection.DisclosureLevel.Should().Be(ObservatoryLocationDisclosureLevel.Hidden);
        projection.LatitudeDegrees.Should().BeNull();
        projection.LongitudeDegrees.Should().BeNull();
        projection.PrecisionMeters.Should().BeNull();
    }

    [TestMethod]
    public async Task ApproximateLocation_RejectsNonFinitePrecisionAndClampsBoundaryRounding()
    {
        await using var context = CreateContext();
        var data = Seed(context);
        data.Location.LatitudeDegrees = 90;
        data.Location.LongitudeDegrees = 180;
        await context.SaveChangesAsync();
        var service = new ObservatoryPublicationService(context, new FixedTimeProvider());

        var invalid = await service.SetLocationDisclosureAsync(
            data.Observatory.Id,
            data.Owner.Id,
            new ObservatoryLocationDisclosureRequest(
                ObservatoryLocationDisclosureLevel.Approximate,
                "BOUNDARY",
                "Boundary",
                90,
                180,
                double.PositiveInfinity,
                "invalid-precision"));
        var valid = await service.SetLocationDisclosureAsync(
            data.Observatory.Id,
            data.Owner.Id,
            new ObservatoryLocationDisclosureRequest(
                ObservatoryLocationDisclosureLevel.Approximate,
                "BOUNDARY",
                "Boundary",
                90,
                180,
                25_000,
                "boundary-rounding"));

        invalid.Outcome.Should().Be(ObservatoryPublicationMutationOutcome.Invalid);
        valid.Outcome.Should().Be(ObservatoryPublicationMutationOutcome.Applied);
        var disclosure = await context.ObservatoryLocationDisclosureVersions.SingleAsync();
        disclosure.PublicLatitudeDegrees.Should().BeInRange(-90, 90);
        disclosure.PublicLongitudeDegrees.Should().BeInRange(-180, 180);
    }

    [TestMethod]
    public async Task OperationsReads_AreBoundedMembershipScopedAndOmitStorageReferences()
    {
        await using var context = CreateContext();
        var data = Seed(context);
        var registration = AddRegistration(context, data.Observatory, "operations");
        var frame = new CentralFrame
        {
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId!.Value,
            ObservatoryId = data.Observatory.Id,
            AgentId = registration.DeviceId,
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = Now,
            FirstReceivedAtUtc = Now,
            RigId = "rig-1",
            CaptureSequence = 42
        };
        var artifact = new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            DevicePublicId = registration.DevicePublicId!.Value,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Raw,
            RecipeVersion = "raw-v1",
            ManifestSchemaVersion = "v2",
            MediaType = "application/octet-stream",
            ByteLength = 4,
            ChecksumSha256 = new string('B', 64),
            StorageReference = "minio://skymonitor-artifacts/private-object-key",
            ReceivedAtUtc = Now,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        var preview = new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            DevicePublicId = registration.DevicePublicId!.Value,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Preview,
            RecipeVersion = "preview-v1",
            ManifestSchemaVersion = "v2",
            MediaType = "image/png",
            ByteLength = 4,
            ChecksumSha256 = new string('C', 64),
            StorageReference = "minio://skymonitor-artifacts/private-preview-key",
            ReceivedAtUtc = Now,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        var activeImage = new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            DevicePublicId = registration.DevicePublicId!.Value,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.AnnotatedPreview,
            RecipeVersion = "svg-v1",
            ManifestSchemaVersion = "v2",
            MediaType = "image/svg+xml",
            ByteLength = 4,
            ChecksumSha256 = new string('D', 64),
            StorageReference = "minio://skymonitor-artifacts/private-active-image-key",
            ReceivedAtUtc = Now,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        frame.Timing = new CentralCaptureTiming
        {
            CentralFrameId = frame.Id,
            RequestedStartUtc = Now.AddSeconds(-2),
            ExposureStartedUtc = Now.AddSeconds(-1),
            ExposureEndedUtc = Now,
            ReadoutCompletedUtc = Now.AddMilliseconds(100),
            DurableIngressUtc = Now.AddMilliseconds(200)
        };
        frame.Control = new CentralCaptureControl
        {
            CentralFrameId = frame.Id,
            RequestedExposureTicks = TimeSpan.FromSeconds(1).Ticks,
            EffectiveExposureTicks = TimeSpan.FromSeconds(1).Ticks,
            RequestedGain = 100,
            EffectiveGain = 100
        };
        frame.Profiles.Add(new CentralCaptureProfile
        {
            CentralFrameId = frame.Id,
            Kind = CentralProfileKind.Rig,
            Name = "all-sky-rig",
            Version = "7",
            Sha256 = new string('E', 64)
        });
        preview.Sources.Add(new CentralArtifactSource
        {
            CentralArtifactId = preview.Id,
            Ordinal = 0,
            SourceArtifactId = artifact.ArtifactId,
            ResolvedCentralArtifactId = artifact.Id,
            ResolvedArtifact = artifact
        });
        context.AddRange(frame, artifact, preview, activeImage);
        await context.SaveChangesAsync();
        var movedObservatory = new Observatory
        {
            OwnerUserId = data.Owner.Id,
            Name = "Moved registration authority",
            TimeZoneId = "UTC",
            CreatedAtUtc = Now,
            IsActive = true
        };
        context.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            Observatory = movedObservatory,
            ObservatoryId = movedObservatory.Id,
            UserId = data.Owner.Id,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = Now
        });
        registration.Observatory = movedObservatory;
        registration.ObservatoryId = movedObservatory.Id;
        await context.SaveChangesAsync();
        var publication = new PublicRecordPublicationService(context, new FixedTimeProvider());
        var movedAuthorityRelease = await publication.DecideAsync(
            movedObservatory.Id,
            data.Owner.Id,
            new PublicRecordSubject(PublicRecordSubjectKind.Artifact, preview.Id),
            PublicationDecisionState.Released,
            "public-image-v1",
            "owner-release");
        var rawRelease = await publication.DecideAsync(
            data.Observatory.Id,
            data.Owner.Id,
            new PublicRecordSubject(PublicRecordSubjectKind.Artifact, artifact.Id),
            PublicationDecisionState.Released,
            "public-image-v1",
            "owner-release");
        var previewRelease = await publication.DecideAsync(
            data.Observatory.Id,
            data.Owner.Id,
            new PublicRecordSubject(PublicRecordSubjectKind.Artifact, preview.Id),
            PublicationDecisionState.Released,
            "public-image-v1",
            "owner-release");
        var activeImageRelease = await publication.DecideAsync(
            data.Observatory.Id,
            data.Owner.Id,
            new PublicRecordSubject(PublicRecordSubjectKind.Artifact, activeImage.Id),
            PublicationDecisionState.Released,
            "public-image-v1",
            "owner-release");
        var service = new NetworkOperationsReadService(context);

        var ownerPage = await service.ListCapturesAsync(data.Owner.Id, null, null, 25, null);
        var targetPage = await service.ListCapturesAsync(data.Target.Id, null, null, 25, null);
        var detail = await service.GetCaptureAsync(data.Owner.Id, frame.Id);
        var trace = await service.GetCaptureTraceAsync(data.Owner.Id, frame.Id);
        var hidden = await service.GetCaptureAsync(data.Target.Id, frame.Id);

        ownerPage.Items.Should().ContainSingle(item => item.CaptureId == frame.Id && item.HasRaw);
        ownerPage.NextCursor.Should().BeNull();
        targetPage.Items.Should().BeEmpty();
        rawRelease.Outcome.Should().Be(PublicRecordPublicationOutcome.NotFoundOrDenied);
        movedAuthorityRelease.Outcome.Should().Be(PublicRecordPublicationOutcome.NotFoundOrDenied);
        previewRelease.Outcome.Should().Be(PublicRecordPublicationOutcome.Applied);
        activeImageRelease.Outcome.Should().Be(PublicRecordPublicationOutcome.NotFoundOrDenied);
        detail.Should().NotBeNull();
        detail!.EffectiveRole.Should().Be(ObservatoryMembershipRole.Owner);
        detail.Provenance.EffectiveGain.Should().Be(100);
        detail.Provenance.Profiles.Should().ContainSingle(profile => profile.Version == "7");
        trace!.ArtifactLineage.Should().ContainSingle(source =>
            source.ArtifactId == preview.ArtifactId
            && source.Ordinal == 0
            && source.ResolvedArtifactId == artifact.ArtifactId);
        hidden.Should().BeNull();
        var json = JsonSerializer.Serialize(detail);
        json.Should().NotContain("private-object-key").And.NotContain("minio://");
        detail.Artifacts.Should().ContainSingle(item =>
            item.ArtifactId == artifact.ArtifactId
            && !item.IsPubliclyEligible
            && !item.IsPubliclyReleased
            && item.ContentPath.EndsWith($"/{artifact.ArtifactId:D}/content", StringComparison.Ordinal));
        detail.Artifacts.Should().ContainSingle(item =>
            item.ArtifactId == preview.ArtifactId
            && item.PublicationSubjectId == preview.Id
            && item.IsPubliclyEligible
            && item.IsPubliclyReleased);
    }

    [TestMethod]
    public async Task ProcessingPolicy_VersionsOwnerOverridesAndResolvesFutureRecipes()
    {
        await using var context = CreateContext();
        var data = Seed(context);
        var catalog = new CentralDerivativeRecipeCatalog(new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Hybrid
        });
        using var telemetry = new OperatorUiTelemetry();
        var service = new CentralProcessingPolicyService(
            context,
            catalog,
            new FixedTimeProvider(),
            NullLogger<CentralProcessingPolicyService>.Instance,
            telemetry);

        var applied = await service.SetAsync(
            data.Observatory.Id,
            data.Owner.Id,
            new CentralProcessingPolicyRequest(620_000, false, "owner-policy"));
        var denied = await service.SetAsync(
            data.Observatory.Id,
            data.Target.Id,
            new CentralProcessingPolicyRequest(700_000, null, "denied-policy"));
        var summary = await service.GetAsync(data.Observatory.Id, data.Owner.Id);
        var recipes = await service.ResolveRequiredRecipesAsync(
            data.Observatory.Id, FrameArtifactRole.Raw, CancellationToken.None);
        var transient = await service.ResolveTransientRecipeAsync(
            data.Observatory.Id, FrameArtifactRole.Raw, CancellationToken.None);
        var restored = await service.SetAsync(
            data.Observatory.Id,
            data.Owner.Id,
            new CentralProcessingPolicyRequest(null, true, "restore-policy"));

        applied.Should().Be(CentralProcessingPolicyMutationOutcome.Applied);
        denied.Should().Be(CentralProcessingPolicyMutationOutcome.NotFoundOrDenied);
        summary.Should().BeEquivalentTo(new
        {
            EffectiveRole = ObservatoryMembershipRole.Owner,
            EffectiveCloudTransmissionThresholdMillionths = 620_000,
            EffectiveCentralValidationEnabled = false,
            Version = (int?)1
        });
        var cloud = recipes.Single(recipe => recipe.RecipeName == BuiltInProcessingRecipes.CloudAssessment);
        cloud.RequestedRecipeIdentitySha256.Should().NotBe(CentralDerivativeRecipeCatalog.CloudAssessmentRequestedRecipeIdentity);
        cloud.Options.GetRawText().Should().Contain("620000");
        transient.Should().BeNull();
        restored.Should().Be(CentralProcessingPolicyMutationOutcome.Applied);
        (await service.ResolveTransientRecipeAsync(
            data.Observatory.Id, FrameArtifactRole.Raw, CancellationToken.None)).Should().NotBeNull();
        var history = await context.CentralProcessingOverrideVersions.OrderBy(item => item.Version).ToArrayAsync();
        history.Should().HaveCount(2);
        history[0].SupersededAtUtc.Should().NotBeNull();
        history[1].CloudTransmissionThresholdMillionths.Should().BeNull();
    }

    [TestMethod]
    public async Task CaptureTrace_RedactsResolvedLineageFromAnotherObservatory()
    {
        await using var context = CreateContext();
        var data = Seed(context);
        var foreignObservatory = new Observatory
        {
            OwnerUserId = "foreign-owner",
            Name = "Foreign",
            TimeZoneId = "UTC",
            CreatedAtUtc = Now,
            IsActive = true
        };
        var ownFrame = new CentralFrame
        {
            RegistrationId = Guid.NewGuid(),
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = data.Observatory.Id,
            AgentId = "own-agent",
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = Now,
            FirstReceivedAtUtc = Now
        };
        var foreignFrame = new CentralFrame
        {
            RegistrationId = Guid.NewGuid(),
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = foreignObservatory.Id,
            AgentId = "foreign-agent",
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = Now,
            FirstReceivedAtUtc = Now
        };
        var foreignArtifact = new CentralArtifact
        {
            Frame = foreignFrame,
            CentralFrameId = foreignFrame.Id,
            ArtifactId = Guid.NewGuid(),
            DevicePublicId = foreignFrame.DevicePublicId,
            Role = FrameArtifactRole.Raw,
            RecipeVersion = "raw-v1",
            ManifestSchemaVersion = "v2",
            MediaType = "application/octet-stream",
            ByteLength = 4,
            ChecksumSha256 = new string('F', 64),
            StorageReference = "foreign-private-key",
            ReceivedAtUtc = Now,
            IdempotencyKey = "foreign-artifact",
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        var ownArtifact = new CentralArtifact
        {
            Frame = ownFrame,
            CentralFrameId = ownFrame.Id,
            ArtifactId = Guid.NewGuid(),
            DevicePublicId = ownFrame.DevicePublicId,
            Role = FrameArtifactRole.Preview,
            RecipeVersion = "preview-v1",
            ManifestSchemaVersion = "v2",
            MediaType = "image/png",
            ByteLength = 4,
            ChecksumSha256 = new string('E', 64),
            StorageReference = "own-private-key",
            ReceivedAtUtc = Now,
            IdempotencyKey = "own-artifact",
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        for (var ordinal = 0; ordinal < 201; ordinal++)
        {
            ownArtifact.Sources.Add(new CentralArtifactSource
            {
                CentralArtifactId = ownArtifact.Id,
                Ordinal = ordinal,
                SourceArtifactId = ordinal == 0 ? foreignArtifact.ArtifactId : Guid.NewGuid(),
                ResolvedCentralArtifactId = ordinal == 0 ? foreignArtifact.Id : null,
                ResolvedArtifact = ordinal == 0 ? foreignArtifact : null
            });
        }
        var foreignJob = new CentralDerivativeJob
        {
            SourceCentralArtifactId = foreignArtifact.Id,
            SourceArtifact = foreignArtifact,
            TargetRole = FrameArtifactRole.AnnotatedPreview,
            TargetRecipeVersion = "foreign-v1",
            TargetVariant = "foreign",
            RecipeName = "foreign",
            RequestedRecipeIdentitySha256 = new string('A', 64),
            RequestIdentitySha256 = new string('B', 64),
            Status = CentralDerivativeJobStatus.Pending,
            MaxAttempts = 1,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now
        };
        var requirement = new CentralDerivativeJobInputRequirement
        {
            CentralDerivativeJobId = foreignJob.Id,
            Ordinal = 0,
            BindingName = "foreign-window",
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            IsRequired = true,
            CompatibilityMode = CentralDerivativeCompatibilityMode.None,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved,
            ResolvedAtUtc = Now
        };
        var input = new CentralDerivativeJobInput
        {
            CentralDerivativeJobId = foreignJob.Id,
            CentralDerivativeJobInputRequirementId = requirement.Id,
            Requirement = requirement,
            Ordinal = 0,
            CentralArtifactId = ownArtifact.Id,
            Artifact = ownArtifact,
            CompatibilitySha256 = new string('C', 64),
            ByteLength = ownArtifact.ByteLength,
            SelectedAtUtc = Now
        };
        requirement.Input = input;
        foreignJob.InputRequirements.Add(requirement);
        foreignJob.Inputs.Add(input);
        var transientEvent = new CentralTransientEventRecord
        {
            AgentId = "own-agent",
            EventId = Guid.NewGuid(),
            EventCreatedUtc = Now
        };
        var observationId = Guid.NewGuid();
        var observation = new CentralTransientObservationRecord
        {
            ObservationId = observationId,
            CentralTransientEventId = transientEvent.Id,
            Event = transientEvent,
            SourceReferenceId = observationId
        };
        observation.Source = new CentralTransientObservationSourceReference
        {
            ObservationId = observationId,
            Observation = observation,
            CentralArtifactId = ownArtifact.Id,
            Artifact = ownArtifact,
            ArtifactId = ownArtifact.ArtifactId,
            ArtifactRole = ownArtifact.Role,
            ArtifactChecksumSha256 = ownArtifact.ChecksumSha256,
            ObservationStartedUtc = Now,
            ObservationEndedUtc = Now
        };
        observation.Backgrounds.Add(new CentralTransientObservationBackgroundReference
        {
            ObservationId = observationId,
            Observation = observation,
            Ordinal = 0,
            CentralArtifactId = foreignArtifact.Id,
            Artifact = foreignArtifact,
            ArtifactId = foreignArtifact.ArtifactId,
            ArtifactRole = foreignArtifact.Role,
            ArtifactChecksumSha256 = foreignArtifact.ChecksumSha256
        });
        transientEvent.Observations.Add(observation);
        context.AddRange(
            foreignObservatory,
            ownFrame,
            foreignFrame,
            ownArtifact,
            foreignArtifact,
            foreignJob,
            transientEvent);
        await context.SaveChangesAsync();

        var trace = await new NetworkOperationsReadService(context)
            .GetCaptureTraceAsync(data.Owner.Id, ownFrame.Id);

        trace.Should().NotBeNull();
        trace!.ArtifactLineage.Should().HaveCount(200).And.OnlyContain(source =>
            source.SourceArtifactId == null
            && source.ResolvedArtifactId == null
            && source.ResolvedChecksumSha256 == null);
        trace.Jobs.Should().BeEmpty();
        trace.Events.Should().BeEmpty();
        trace.TraceIsTruncated.Should().BeTrue();
    }

    [TestMethod]
    public async Task CameraLists_KeysetPageBeyondFiftyWithoutGapsOrDuplicates()
    {
        await using var context = CreateContext();
        var data = Seed(context);
        context.ObservatoryPublicationProfileVersions.Add(new ObservatoryPublicationProfileVersion
        {
            ObservatoryId = data.Observatory.Id,
            Version = 1,
            PublicSlug = "paged-station",
            PublicDisplayName = "Paged Station",
            PublicDescription = "Paging fixture",
            ProfileVisibility = ObservatoryProfileVisibility.Public,
            EffectiveFromUtc = Now,
            ActorUserId = data.Owner.Id,
            ReasonCode = "test",
            CanonicalSha256 = new string('A', 64)
        });
        for (var index = 0; index < 56; index++)
        {
            var camera = new LogicalCamera
            {
                ObservatoryId = data.Observatory.Id,
                Slug = $"camera-{index:D2}",
                Name = $"Camera {index:D2}",
                Description = "Paging fixture",
                CreatedAtUtc = Now.AddSeconds(index),
                CreatedByUserId = data.Owner.Id
            };
            context.LogicalCameras.Add(camera);
            context.PublicRecordPublicationDecisions.Add(new PublicRecordPublicationDecision
            {
                AuthorityObservatoryId = data.Observatory.Id,
                SubjectKind = PublicRecordSubjectKind.LogicalCamera,
                State = PublicationDecisionState.Released,
                LogicalCameraId = camera.Id,
                ProjectionSchemaVersion = "public-camera-v1",
                OccurredAtUtc = Now.AddSeconds(index),
                ActorUserId = data.Owner.Id,
                ReasonCode = "test"
            });
        }
        await context.SaveChangesAsync();
        var operations = new NetworkOperationsReadService(context);
        var publicReads = new PublicNetworkReadService(context);

        var operationsFirst = await operations.ListObservatoryCamerasAsync(
            data.Owner.Id, data.Observatory.Id, 50, null);
        var operationsSecond = await operations.ListObservatoryCamerasAsync(
            data.Owner.Id, data.Observatory.Id, 50, operationsFirst.NextCursor);
        var publicFirst = await publicReads.ListObservatoryCamerasAsync("paged-station", 50, null);
        var publicSecond = await publicReads.ListObservatoryCamerasAsync(
            "paged-station", 50, publicFirst.NextCursor);

        operationsFirst.Items.Should().HaveCount(50);
        operationsSecond.Items.Should().HaveCount(6);
        operationsFirst.Items.Concat(operationsSecond.Items).Select(item => item.LogicalCameraId)
            .Should().OnlyHaveUniqueItems().And.HaveCount(56);
        publicFirst.Items.Should().HaveCount(50);
        publicSecond.Items.Should().HaveCount(6);
        publicFirst.Items.Concat(publicSecond.Items).Select(item => item.Slug)
            .Should().OnlyHaveUniqueItems().And.HaveCount(56);
    }

    [TestMethod]
    public async Task PendingInvitations_KeysetPageBeyondFiftyWithoutGapsOrDuplicates()
    {
        await using var context = CreateContext();
        var data = Seed(context);
        for (var index = 0; index < 56; index++)
        {
            var user = new ApplicationUser
            {
                Id = $"invitee-{index:D2}",
                UserName = $"invitee-{index:D2}",
                Email = $"invitee-{index:D2}@example.test",
                AccountType = AccountType.User
            };
            context.Users.Add(user);
            context.ObservatoryInvitations.Add(new ObservatoryInvitation
            {
                ObservatoryId = data.Observatory.Id,
                TargetUserId = user.Id,
                TargetEmailSha256 = new string('A', 64),
                OfferedRole = ObservatoryMembershipRole.Viewer,
                InvitedByUserId = data.Owner.Id,
                IssuedAtUtc = Now,
                ExpiresAtUtc = Now.AddDays(1).AddSeconds(index),
                AcceptanceTokenSha256 = new string('B', 64),
                CanonicalSha256 = new string('C', 64)
            });
        }
        await context.SaveChangesAsync();
        var service = new ObservatoryInvitationService(context, new FixedTimeProvider());

        var first = await service.ListPendingAsync(data.Observatory.Id, data.Owner.Id, 50, null);
        var second = await service.ListPendingAsync(
            data.Observatory.Id, data.Owner.Id, 50, first.NextCursor);

        first.Items.Should().HaveCount(50);
        second.Items.Should().HaveCount(6);
        first.Items.Concat(second.Items).Select(item => item.InvitationId)
            .Should().OnlyHaveUniqueItems().And.HaveCount(56);
    }

    [TestMethod]
    public async Task ProtectedChildCollections_KeysetPageBeyondFiftyWithoutGapsOrDuplicates()
    {
        await using var context = CreateContext();
        var data = Seed(context);
        var camera = new LogicalCamera
        {
            ObservatoryId = data.Observatory.Id,
            Slug = "history-camera",
            Name = "History Camera",
            CreatedAtUtc = Now,
            CreatedByUserId = data.Owner.Id
        };
        context.LogicalCameras.Add(camera);
        var frame = new CentralFrame
        {
            RegistrationId = Guid.NewGuid(),
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = data.Observatory.Id,
            AgentId = "paging-agent",
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = Now,
            FirstReceivedAtUtc = Now
        };
        context.CentralFrames.Add(frame);
        for (var index = 0; index < 56; index++)
        {
            var registration = AddRegistration(context, data.Observatory, $"paged-{index:D2}");
            camera.Installations.Add(new LogicalCameraInstallation
            {
                LogicalCameraId = camera.Id,
                Registration = registration,
                RegistrationId = registration.Id,
                InstallationPublicId = Guid.NewGuid(),
                AssignedAtUtc = Now.AddMinutes(index),
                RetiredAtUtc = Now.AddMinutes(index).AddSeconds(30),
                AssignedByUserId = data.Owner.Id,
                RetiredByUserId = data.Owner.Id,
                AssignmentReasonCode = "paging-fixture",
                RetirementReasonCode = "paging-fixture"
            });
            context.CentralArtifacts.Add(new CentralArtifact
            {
                CentralFrameId = frame.Id,
                ArtifactId = Guid.NewGuid(),
                DevicePublicId = frame.DevicePublicId,
                Role = FrameArtifactRole.Metadata,
                RecipeVersion = "paging-v1",
                ManifestSchemaVersion = "v2",
                MediaType = "application/json",
                ByteLength = 2,
                ChecksumSha256 = index.ToString("X64", CultureInfo.InvariantCulture),
                StorageReference = $"private-{index:D2}",
                ReceivedAtUtc = Now.AddSeconds(index),
                IdempotencyKey = $"paging-{index:D2}",
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete
            });
        }
        await context.SaveChangesAsync();
        var service = new NetworkOperationsReadService(context);

        var registrationsFirst = await service.ListObservatoryRegistrationsAsync(
            data.Owner.Id, data.Observatory.Id, 50, null);
        var registrationsSecond = await service.ListObservatoryRegistrationsAsync(
            data.Owner.Id, data.Observatory.Id, 50, registrationsFirst.NextCursor);
        var installationsFirst = await service.ListCameraInstallationsAsync(data.Owner.Id, camera.Id, 50, null);
        var installationsSecond = await service.ListCameraInstallationsAsync(
            data.Owner.Id, camera.Id, 50, installationsFirst.NextCursor);
        var artifactsFirst = await service.ListCaptureArtifactsAsync(data.Owner.Id, frame.Id, 50, null);
        var artifactsSecond = await service.ListCaptureArtifactsAsync(
            data.Owner.Id, frame.Id, 50, artifactsFirst.NextCursor);

        registrationsFirst.Items.Concat(registrationsSecond.Items).Select(item => item.RegistrationId)
            .Should().OnlyHaveUniqueItems().And.HaveCount(56);
        installationsFirst.Items.Concat(installationsSecond.Items).Select(item => item.InstallationPublicId)
            .Should().OnlyHaveUniqueItems().And.HaveCount(56);
        artifactsFirst.Items.Concat(artifactsSecond.Items).Select(item => item.ArtifactId)
            .Should().OnlyHaveUniqueItems().And.HaveCount(56);
    }

    private static ApplicationDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static TestData Seed(ApplicationDbContext context)
    {
        var owner = new ApplicationUser
        {
            Id = "owner",
            UserName = "owner",
            Email = "owner@example.test",
            AccountType = AccountType.User
        };
        var target = new ApplicationUser
        {
            Id = "target",
            UserName = "target",
            Email = "target@example.test",
            AccountType = AccountType.User
        };
        var observatory = new Observatory
        {
            OwnerUserId = owner.Id,
            Name = "Summit",
            LatitudeDegrees = 19.8,
            LongitudeDegrees = -155.4,
            ElevationMeters = 4200,
            TimeZoneId = "Pacific/Honolulu",
            CreatedAtUtc = Now,
            IsActive = true
        };
        var location = new ObservatoryLocationVersion
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            Version = 1,
            CanonicalSha256 = new string('A', 64),
            EffectiveFromUtc = Now,
            LatitudeDegrees = observatory.LatitudeDegrees,
            LongitudeDegrees = observatory.LongitudeDegrees,
            ElevationMeters = observatory.ElevationMeters,
            TimeZoneId = observatory.TimeZoneId,
            RecordedAtUtc = Now,
            RecordedBy = owner.Id
        };
        context.AddRange(owner, target, observatory, location);
        context.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            UserId = owner.Id,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = Now
        });
        context.SaveChanges();
        return new(owner, target, observatory, location);
    }

    private static DeviceRegistration AddRegistration(
        ApplicationDbContext context,
        Observatory observatory,
        string suffix)
    {
        var registration = new DeviceRegistration
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            DeviceId = $"agent-{suffix}",
            DevicePublicId = Guid.NewGuid(),
            FriendlyName = $"Camera {suffix}",
            ObservatoryName = observatory.Name,
            ObservatoryTimeZoneId = observatory.TimeZoneId,
            OwnerUserId = observatory.OwnerUserId,
            OwnerDisplayName = observatory.OwnerUserId,
            VerificationCodeHash = new string('A', 64),
            Status = DeviceRegistrationStatus.Active,
            IssuedAtUtc = Now,
            ActivatedAtUtc = Now
        };
        context.DeviceRegistrations.Add(registration);
        return registration;
    }

    private sealed record TestData(
        ApplicationUser Owner,
        ApplicationUser Target,
        Observatory Observatory,
        ObservatoryLocationVersion Location);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
