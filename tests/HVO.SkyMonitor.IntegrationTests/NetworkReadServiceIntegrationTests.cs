using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class NetworkReadServiceIntegrationTests
{
    [TestMethod]
    public async Task SqlServer_PublicAndProtectedQueriesRemainBoundedAndAuthorityScoped()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var viewer = await db.Users.SingleAsync(user => user.Email == TestUsers.Viewer.Email).ConfigureAwait(false);
        var marker = Guid.NewGuid().ToString("N");
        var observatory = await services.GetRequiredService<IObservatoryService>().CreateOrUpdateAsync(
            new ObservatoryUpsertRequest(
                null,
                owner.Id,
                $"Network read {marker}",
                19.8,
                -155.4,
                4200,
                "Pacific/Honolulu",
                true)).ConfigureAwait(false);
        var publication = services.GetRequiredService<IObservatoryPublicationService>();
        _ = await publication.SetProfileAsync(
            observatory.Id,
            owner.Id,
            new ObservatoryPublicationProfileRequest(
                $"network-{marker}",
                "Network Read Station",
                "Released integration projection",
                ObservatoryProfileVisibility.Public,
                false,
                false,
                "integration-release")).ConfigureAwait(false);
        _ = await publication.SetLocationDisclosureAsync(
            observatory.Id,
            owner.Id,
            new ObservatoryLocationDisclosureRequest(
                ObservatoryLocationDisclosureLevel.Approximate,
                "US-HI",
                "Hawaii",
                19.7,
                -155.1,
                25_000,
                "integration-release")).ConfigureAwait(false);
        var cameraResult = await services.GetRequiredService<ILogicalCameraService>().CreateAsync(
            observatory.Id,
            owner.Id,
            "all-sky",
            "All Sky",
            "Released logical camera").ConfigureAwait(false);
        _ = await services.GetRequiredService<IPublicRecordPublicationService>().DecideAsync(
            observatory.Id,
            owner.Id,
            new PublicRecordSubject(PublicRecordSubjectKind.LogicalCamera, cameraResult.LogicalCameraId!.Value),
            PublicationDecisionState.Released,
            "public-camera-v1",
            "integration-release").ConfigureAwait(false);
        var registration = new DeviceRegistration
        {
            ObservatoryId = observatory.Id,
            DeviceId = $"network-agent-{marker}",
            DevicePublicId = Guid.NewGuid(),
            FriendlyName = "Network camera installation",
            ObservatoryName = observatory.Name,
            ObservatoryTimeZoneId = observatory.TimeZoneId,
            OwnerUserId = owner.Id,
            OwnerDisplayName = TestUsers.Operator.FullName,
            VerificationCodeHash = new string('A', 64),
            Status = DeviceRegistrationStatus.Active,
            IssuedAtUtc = DateTimeOffset.UtcNow,
            ActivatedAtUtc = DateTimeOffset.UtcNow
        };
        db.DeviceRegistrations.Add(registration);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var installation = await services.GetRequiredService<ILogicalCameraService>().AssignInstallationAsync(
            cameraResult.LogicalCameraId.Value,
            registration.Id,
            owner.Id,
            "integration-installation").ConfigureAwait(false);
        var frame = new CentralFrame
        {
            RegistrationId = registration.Id,
            LogicalCameraInstallationId = installation.InstallationId,
            DevicePublicId = registration.DevicePublicId.Value,
            ObservatoryId = observatory.Id,
            AgentId = registration.DeviceId,
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = DateTimeOffset.UtcNow,
            FirstReceivedAtUtc = DateTimeOffset.UtcNow,
            RigId = "rig-integration"
        };
        db.CentralFrames.Add(frame);
        db.CentralArtifacts.Add(new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            DevicePublicId = registration.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Raw,
            RecipeVersion = "raw-v1",
            ManifestSchemaVersion = "v2",
            MediaType = "application/octet-stream",
            ByteLength = 4,
            ChecksumSha256 = new string('B', 64),
            StorageReference = $"minio://skymonitor-artifacts/private-{marker}",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            IdempotencyKey = marker,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        });
        var preview = new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            DevicePublicId = registration.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Preview,
            RecipeVersion = "preview-v1",
            ManifestSchemaVersion = "v2",
            MediaType = "image/png",
            ByteLength = 4,
            ChecksumSha256 = new string('C', 64),
            StorageReference = $"minio://skymonitor-artifacts/preview-{marker}",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            IdempotencyKey = $"{marker}-preview",
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        db.CentralArtifacts.Add(preview);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var previewRelease = await services.GetRequiredService<IPublicRecordPublicationService>().DecideAsync(
            observatory.Id,
            owner.Id,
            new PublicRecordSubject(PublicRecordSubjectKind.Artifact, preview.Id),
            PublicationDecisionState.Released,
            "public-image-v1",
            "integration-release").ConfigureAwait(false);

        var publicReads = services.GetRequiredService<IPublicNetworkReadService>();
        var directory = await publicReads.ListObservatoriesAsync(50, null).ConfigureAwait(false);
        var publicDetail = await publicReads.GetObservatoryAsync($"network-{marker}").ConfigureAwait(false);
        var publicEvents = await publicReads.ListEventsAsync(25, null).ConfigureAwait(false);
        var publicImages = await publicReads.ListImagesAsync(25).ConfigureAwait(false);
        var operations = services.GetRequiredService<INetworkOperationsReadService>();
        var observatoryPage = await operations.ListObservatoriesAsync(owner.Id, 25, null).ConfigureAwait(false);
        while (observatoryPage.Items.All(item => item.ObservatoryId != observatory.Id)
            && observatoryPage.NextCursor is { } nextCursor)
        {
            observatoryPage = await operations.ListObservatoriesAsync(owner.Id, 25, nextCursor).ConfigureAwait(false);
        }
        var observatoryDetail = await operations.GetObservatoryDetailAsync(owner.Id, observatory.Id).ConfigureAwait(false);
        var cameraDetail = await operations.GetCameraAsync(owner.Id, cameraResult.LogicalCameraId.Value).ConfigureAwait(false);
        var ownerCaptures = await operations.ListCapturesAsync(
            owner.Id, observatory.Id, cameraResult.LogicalCameraId, 25, null).ConfigureAwait(false);
        var ownerCapture = await operations.GetCaptureAsync(owner.Id, frame.Id).ConfigureAwait(false);
        var viewerCapture = await operations.GetCaptureAsync(viewer.Id, frame.Id).ConfigureAwait(false);

        directory.Items.Should().ContainSingle(item => item.Slug == $"network-{marker}"
            && item.Location.DisclosureLevel == ObservatoryLocationDisclosureLevel.Approximate);
        publicDetail!.Cameras.Should().ContainSingle(item => item.Slug == "all-sky");
        publicEvents.Items.Should().NotBeNull();
        publicImages.Should().ContainSingle(item => item.PublicId == previewRelease.PublicId!.Value
            && item.OriginLabel == "Edge preview");
        observatoryPage.Items.Should().Contain(item => item.ObservatoryId == observatory.Id);
        observatoryDetail!.Cameras.Should().ContainSingle(item => item.LogicalCameraId == cameraResult.LogicalCameraId);
        cameraDetail!.Installations.Should().ContainSingle(item => item.InstallationName == registration.FriendlyName);
        ownerCaptures.Items.Should().ContainSingle(item => item.CaptureId == frame.Id);
        ownerCapture!.EffectiveRole.Should().Be(ObservatoryMembershipRole.Owner);
        ownerCapture.Artifacts.Should().ContainSingle(item => item.PublicationSubjectId == preview.Id
            && item.IsPubliclyEligible && item.IsPubliclyReleased);
        viewerCapture.Should().BeNull();

        Func<Task> rewriteInstallationInSql = () => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [CentralFrames] SET [LogicalCameraInstallationId] = NULL WHERE [Id] = {frame.Id}");
        await rewriteInstallationInSql.Should().ThrowAsync<SqlException>().WithMessage("*immutable*")
            .ConfigureAwait(false);
        frame.LogicalCameraInstallationId = null;
        Func<Task> rewriteInstallationInEf = () => db.SaveChangesAsync();
        await rewriteInstallationInEf.Should().ThrowAsync<InvalidOperationException>().WithMessage("*immutable*")
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SqlServer_EqualSortKeysPageCamerasAndInvitationsWithoutGaps()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var marker = Guid.NewGuid().ToString("N");
        var observatory = await services.GetRequiredService<IObservatoryService>().CreateOrUpdateAsync(
            new ObservatoryUpsertRequest(
                null,
                owner.Id,
                $"Paging {marker}",
                19.8,
                -155.4,
                4200,
                "Pacific/Honolulu",
                true)).ConfigureAwait(false);
        _ = await services.GetRequiredService<IObservatoryPublicationService>().SetProfileAsync(
            observatory.Id,
            owner.Id,
            new ObservatoryPublicationProfileRequest(
                $"paging-{marker}",
                "Paging Station",
                "Equal-key SQL paging fixture",
                ObservatoryProfileVisibility.Public,
                false,
                false,
                "integration-paging")).ConfigureAwait(false);
        var expiresAtUtc = DateTimeOffset.UtcNow.AddDays(1);
        for (var index = 0; index < 56; index++)
        {
            var camera = new LogicalCamera
            {
                ObservatoryId = observatory.Id,
                Slug = $"camera-{index:D2}-{marker}",
                Name = "Equal camera name",
                Description = "SQL paging fixture",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedByUserId = owner.Id
            };
            var invitee = new ApplicationUser
            {
                Id = $"paging-{index:D2}-{marker}",
                UserName = $"paging-{index:D2}-{marker}",
                Email = $"paging-{index:D2}-{marker}@example.test",
                AccountType = AccountType.User
            };
            db.AddRange(
                camera,
                invitee,
                new PublicRecordPublicationDecision
                {
                    AuthorityObservatoryId = observatory.Id,
                    SubjectKind = PublicRecordSubjectKind.LogicalCamera,
                    State = PublicationDecisionState.Released,
                    LogicalCameraId = camera.Id,
                    ProjectionSchemaVersion = "public-camera-v1",
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    ActorUserId = owner.Id,
                    ReasonCode = "integration-paging"
                },
                new ObservatoryInvitation
                {
                    ObservatoryId = observatory.Id,
                    TargetUserId = invitee.Id,
                    TargetEmailSha256 = new string('A', 64),
                    OfferedRole = ObservatoryMembershipRole.Viewer,
                    InvitedByUserId = owner.Id,
                    IssuedAtUtc = DateTimeOffset.UtcNow,
                    ExpiresAtUtc = expiresAtUtc,
                    AcceptanceTokenSha256 = new string('B', 64),
                    CanonicalSha256 = new string('C', 64)
                });
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        var operations = services.GetRequiredService<INetworkOperationsReadService>();
        var publicReads = services.GetRequiredService<IPublicNetworkReadService>();
        var invitations = services.GetRequiredService<IObservatoryInvitationService>();

        var protectedFirst = await operations.ListObservatoryCamerasAsync(
            owner.Id, observatory.Id, 50, null).ConfigureAwait(false);
        var protectedSecond = await operations.ListObservatoryCamerasAsync(
            owner.Id, observatory.Id, 50, protectedFirst.NextCursor).ConfigureAwait(false);
        var publicFirst = await publicReads.ListObservatoryCamerasAsync(
            $"paging-{marker}", 50, null).ConfigureAwait(false);
        var publicSecond = await publicReads.ListObservatoryCamerasAsync(
            $"paging-{marker}", 50, publicFirst.NextCursor).ConfigureAwait(false);
        var invitationFirst = await invitations.ListPendingAsync(
            observatory.Id, owner.Id, 50, null).ConfigureAwait(false);
        var invitationSecond = await invitations.ListPendingAsync(
            observatory.Id, owner.Id, 50, invitationFirst.NextCursor).ConfigureAwait(false);

        protectedFirst.Items.Concat(protectedSecond.Items).Select(item => item.LogicalCameraId)
            .Should().OnlyHaveUniqueItems().And.HaveCount(56);
        publicFirst.Items.Concat(publicSecond.Items).Select(item => item.Slug)
            .Should().OnlyHaveUniqueItems().And.HaveCount(56);
        invitationFirst.Items.Concat(invitationSecond.Items).Select(item => item.InvitationId)
            .Should().OnlyHaveUniqueItems().And.HaveCount(56);
    }
}
