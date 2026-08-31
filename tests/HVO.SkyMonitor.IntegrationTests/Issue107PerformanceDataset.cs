using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

internal sealed record Issue107PerformanceDataset(
    Guid ThousandObservatoryId,
    Guid TenThousandObservatoryId,
    Guid ThousandCaptureId,
    Guid TenThousandCaptureId,
    string DatasetSha256,
    IReadOnlyList<Guid> ObservatoryIds,
    IReadOnlyList<Guid> ReleasedPreviewIds,
    Guid PrimaryReleasedPreviewPublicId,
    string PreviewChecksumSha256,
    Guid TenThousandSparseCameraId,
    Guid W2DevicePublicId,
    Guid W2ArtifactId,
    string W2ChecksumSha256);

internal static class Issue107PerformanceDatasetSeeder
{
    internal const string Workload = "I107-P13-UI-v1";
    private const int CaptureBatchSize = 250;
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FrameArtifactRole[] ArtifactRoles =
    [
        FrameArtifactRole.Raw,
        FrameArtifactRole.Calibrated,
        FrameArtifactRole.Combined,
        FrameArtifactRole.Preview,
        FrameArtifactRole.AnnotatedPreview,
        FrameArtifactRole.Metadata
    ];
    private static readonly Lazy<ArtifactPayload[]> ArtifactPayloads = new(CreateArtifactPayloads);

    internal static async Task<Issue107PerformanceDataset> SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = await LoadUsersAsync(db).ConfigureAwait(false);
        var topology = await SeedAuthorityTopologyAsync(db, users).ConfigureAwait(false);
        await EnsureArtifactObjectsAsync(scope.ServiceProvider.GetRequiredService<IMinioClient>()).ConfigureAwait(false);
        var releasedPreviewIds = new List<Guid>(660);

        await SeedHistoryAsync(db, topology, datasetOrdinal: 0, captureCount: 1_000, releasedPreviewCount: 60,
            releasedPreviewIds).ConfigureAwait(false);
        await SeedHistoryAsync(db, topology, datasetOrdinal: 1, captureCount: 10_000, releasedPreviewCount: 600,
            releasedPreviewIds).ConfigureAwait(false);
        var releasedEvents = await SeedTransientEventsAsync(db, topology, users).ConfigureAwait(false);
        await SeedPreviewPublicationAsync(db, topology, releasedPreviewIds, users.OwnerId).ConfigureAwait(false);
        await SeedPersonalEventStateAsync(db, users, releasedEvents).ConfigureAwait(false);
        await AssertCountsAsync(db, topology, releasedPreviewIds, releasedEvents).ConfigureAwait(false);

        var checksumInput = string.Join('\n', new[]
        {
            Workload,
            "observatories=10",
            "logical-cameras=20",
            "installations=40",
            "memberships=40",
            "invitations=10",
            "captures=1000,10000",
            "artifacts-per-capture=6",
            "jobs-per-capture=4",
            "environmental-observations-per-capture=2",
            "events-per-capture=0.05",
            "released-previews=60,600",
            "fixture-revision=3",
            string.Join(',', ArtifactPayloads.Value.Select(payload => payload.ChecksumSha256)),
            string.Join(',', topology.ObservatoryIds.Select(id => id.ToString("N")))
        });
        return new(
            topology.ObservatoryIds[0],
            topology.ObservatoryIds[1],
            Id("frame", 0),
            Id("frame", 20_000),
            Sha(checksumInput),
            topology.ObservatoryIds,
            releasedPreviewIds,
            Id("preview-public", 0),
            ArtifactPayloads.Value[3].ChecksumSha256,
            Id("camera", 3),
            topology.DevicePublicIds[3],
            Id("artifact-public", 0),
            ArtifactPayloads.Value[0].ChecksumSha256);
    }

    private static async Task<DatasetUsers> LoadUsersAsync(ApplicationDbContext db)
    {
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var secondOwner = await db.Users.SingleAsync(user => user.Email == TestUsers.Admin.Email).ConfigureAwait(false);
        var manager = await db.Users.SingleAsync(user => user.Email == TestUsers.Regular.Email).ConfigureAwait(false);
        var viewer = await db.Users.SingleAsync(user => user.Email == TestUsers.Viewer.Email).ConfigureAwait(false);
        var personalUsers = Enumerable.Range(0, 20).Select(index => new ApplicationUser
        {
            Id = $"issue-107-performance-user-{index:D2}",
            UserName = $"issue-107-performance-user-{index:D2}",
            NormalizedUserName = $"ISSUE-107-PERFORMANCE-USER-{index:D2}",
            Email = $"issue-107-performance-user-{index:D2}@example.invalid",
            NormalizedEmail = $"ISSUE-107-PERFORMANCE-USER-{index:D2}@EXAMPLE.INVALID",
            AccountType = AccountType.User,
            EmailConfirmed = true,
            SecurityStamp = Sha($"security-{index}")
        }).ToArray();
        db.Users.AddRange(personalUsers);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new(owner.Id, secondOwner.Id, manager.Id, viewer.Id, personalUsers.Select(user => user.Id).ToArray());
    }

    private static async Task<DatasetTopology> SeedAuthorityTopologyAsync(ApplicationDbContext db, DatasetUsers users)
    {
        var observatories = new List<Observatory>(10);
        var registrations = new List<DeviceRegistration>(40);
        var cameras = new List<LogicalCamera>(20);
        var installations = new List<LogicalCameraInstallation>(40);
        var activeInstallationIds = new Guid[10];
        var sparseInstallationIds = new Guid[10];
        var environmentalSourceIds = new Guid[10];
        for (var observatoryIndex = 0; observatoryIndex < 10; observatoryIndex++)
        {
            var observatoryId = Id("observatory", observatoryIndex);
            var locationId = Id("observatory-location", observatoryIndex);
            var locationSha = Sha($"location-{observatoryIndex}");
            var observatory = new Observatory
            {
                Id = observatoryId,
                OwnerUserId = users.OwnerId,
                Name = $"I107 Observatory {observatoryIndex:D2}",
                LatitudeDegrees = 18 + observatoryIndex,
                LongitudeDegrees = -160 + observatoryIndex,
                ElevationMeters = 1_000 + observatoryIndex * 100,
                TimeZoneId = "UTC",
                AllowedDeploymentRadiusMeters = 1_000,
                CurrentLocationVersion = 1,
                CurrentLocationCanonicalSha256 = locationSha,
                CreatedAtUtc = Epoch,
                IsActive = true
            };
            observatories.Add(observatory);
            db.Observatories.Add(observatory);
            db.ObservatoryLocationVersions.Add(new ObservatoryLocationVersion
            {
                Id = locationId,
                ObservatoryId = observatoryId,
                Version = 1,
                CanonicalSha256 = locationSha,
                EffectiveFromUtc = Epoch,
                LatitudeDegrees = observatory.LatitudeDegrees,
                LongitudeDegrees = observatory.LongitudeDegrees,
                ElevationMeters = observatory.ElevationMeters,
                TimeZoneId = "UTC",
                AllowedDeploymentRadiusMeters = 1_000,
                RecordedAtUtc = Epoch,
                RecordedBy = users.OwnerId
            });
            db.ObservatoryPublicationProfileVersions.Add(new ObservatoryPublicationProfileVersion
            {
                Id = Id("profile", observatoryIndex),
                ObservatoryId = observatoryId,
                Version = 1,
                PublicSlug = $"i107-observatory-{observatoryIndex:D2}",
                PublicDisplayName = $"I107 Observatory {observatoryIndex:D2}",
                PublicDescription = "Deterministic issue 107 performance observatory.",
                ProfileVisibility = observatoryIndex < 6
                    ? ObservatoryProfileVisibility.Public
                    : ObservatoryProfileVisibility.Private,
                PublishEnvironmentalSummary = observatoryIndex < 6,
                AllowAutomaticVerifiedEventInclusion = false,
                EffectiveFromUtc = Epoch,
                ActorUserId = users.OwnerId,
                ReasonCode = "performance-fixture",
                CanonicalSha256 = Sha($"profile-{observatoryIndex}")
            });
            db.ObservatoryLocationDisclosureVersions.Add(new ObservatoryLocationDisclosureVersion
            {
                Id = Id("disclosure", observatoryIndex),
                ObservatoryId = observatoryId,
                Version = 1,
                DisclosureLevel = observatoryIndex < 6
                    ? ObservatoryLocationDisclosureLevel.Approximate
                    : ObservatoryLocationDisclosureLevel.Hidden,
                RegionCode = observatoryIndex < 6 ? "TEST" : null,
                RegionLabel = observatoryIndex < 6 ? "Performance region" : null,
                PublicLatitudeDegrees = observatoryIndex < 6 ? observatory.LatitudeDegrees + 0.1 : null,
                PublicLongitudeDegrees = observatoryIndex < 6 ? observatory.LongitudeDegrees + 0.1 : null,
                PublicPrecisionMeters = observatoryIndex < 6 ? 25_000 : null,
                SourceObservatoryLocationVersionId = observatoryIndex < 6 ? locationId : null,
                EffectiveFromUtc = Epoch,
                ActorUserId = users.OwnerId,
                ReasonCode = "performance-fixture",
                CanonicalSha256 = Sha($"disclosure-{observatoryIndex}")
            });
            var roleUsers = new[]
            {
                (users.OwnerId, ObservatoryMembershipRole.Owner),
                (users.SecondOwnerId, ObservatoryMembershipRole.Owner),
                (users.ManagerId, ObservatoryMembershipRole.Manager),
                (users.ViewerId, ObservatoryMembershipRole.Viewer)
            };
            db.ObservatoryMemberships.AddRange(roleUsers.Select(item => new ObservatoryMembership
            {
                ObservatoryId = observatoryId,
                UserId = item.Item1,
                Role = item.Item2,
                AddedAtUtc = Epoch
            }));
            db.ObservatoryInvitations.Add(new ObservatoryInvitation
            {
                Id = Id("invitation", observatoryIndex),
                ObservatoryId = observatoryId,
                TargetUserId = users.PersonalUserIds[observatoryIndex],
                TargetEmailSha256 = Sha($"invite-email-{observatoryIndex}"),
                OfferedRole = ObservatoryMembershipRole.Viewer,
                InvitedByUserId = users.OwnerId,
                IssuedAtUtc = Epoch,
                ExpiresAtUtc = Epoch.AddYears(1),
                AcceptanceTokenSha256 = Sha($"invite-token-{observatoryIndex}"),
                CanonicalSha256 = Sha($"invite-{observatoryIndex}")
            });
            environmentalSourceIds[observatoryIndex] = Id("environment-source", observatoryIndex);
            db.EnvironmentalObservationSources.Add(new EnvironmentalObservationSourceRecord
            {
                Id = environmentalSourceIds[observatoryIndex],
                IdentitySha256 = Sha($"environment-source-identity-{observatoryIndex}"),
                ContentSha256 = Sha($"environment-source-content-{observatoryIndex}"),
                SiteId = observatoryId,
                Provider = "issue-107-performance",
                SourceId = $"environment-{observatoryIndex:D2}",
                Version = "1.0.0",
                Kind = EnvironmentalObservationSourceKind.Imported,
                MethodName = "deterministic-fixture",
                MethodVersion = "1.0.0",
                ParametersJson = "{}",
                ParametersSha256 = Sha("{}"),
                CreatedAtUtc = Epoch
            });

            for (var cameraIndex = 0; cameraIndex < 2; cameraIndex++)
            {
                var globalCameraIndex = observatoryIndex * 2 + cameraIndex;
                var camera = new LogicalCamera
                {
                    Id = Id("camera", globalCameraIndex),
                    ObservatoryId = observatoryId,
                    Slug = $"camera-{cameraIndex:D2}",
                    Name = $"Performance camera {cameraIndex:D2}",
                    Description = "Deterministic logical camera.",
                    CreatedAtUtc = Epoch,
                    CreatedByUserId = users.OwnerId
                };
                cameras.Add(camera);
                db.LogicalCameras.Add(camera);
                if (observatoryIndex < 6)
                {
                    db.PublicRecordPublicationDecisions.Add(new PublicRecordPublicationDecision
                    {
                        Id = Id("camera-release", globalCameraIndex),
                        PublicId = Id("camera-public", globalCameraIndex),
                        AuthorityObservatoryId = observatoryId,
                        SubjectKind = PublicRecordSubjectKind.LogicalCamera,
                        State = PublicationDecisionState.Released,
                        LogicalCameraId = camera.Id,
                        ProjectionSchemaVersion = "public-camera-v1",
                        OccurredAtUtc = Epoch.AddDays(1),
                        ActorUserId = users.OwnerId,
                        ReasonCode = "performance-fixture"
                    });
                }
                LogicalCameraInstallation? predecessor = null;
                for (var generation = 0; generation < 2; generation++)
                {
                    var registrationIndex = observatoryIndex * 4 + cameraIndex * 2 + generation;
                    var registration = new DeviceRegistration
                    {
                        Id = Id("registration", registrationIndex),
                        DeviceId = $"issue-107-agent-{registrationIndex:D2}",
                        ObservatoryId = observatoryId,
                        FriendlyName = $"Performance registration {registrationIndex:D2}",
                        ObservatoryName = observatory.Name,
                        ObservatoryLatitudeDegrees = observatory.LatitudeDegrees,
                        ObservatoryLongitudeDegrees = observatory.LongitudeDegrees,
                        ObservatoryElevationMeters = observatory.ElevationMeters,
                        ObservatoryTimeZoneId = "UTC",
                        ObservatoryLocationVersion = 1,
                        ObservatoryLocationCanonicalSha256 = locationSha,
                        LocationEvidenceState = RegistrationLocationEvidenceState.ObservatoryPinned,
                        OwnerUserId = users.OwnerId,
                        OwnerDisplayName = "Performance owner",
                        OwnerConfirmationMethod = "SelfAttested",
                        OwnerConfirmedAtUtc = Epoch,
                        Status = DeviceRegistrationStatus.Active,
                        VerificationCodeHash = Sha($"registration-{registrationIndex}"),
                        DevicePublicId = Id("device", registrationIndex),
                        IssuedAtUtc = Epoch.AddMinutes(registrationIndex),
                        ActivatedAtUtc = Epoch.AddMinutes(registrationIndex),
                        EnvelopeVersion = "v2"
                    };
                    registrations.Add(registration);
                    db.DeviceRegistrations.Add(registration);
                    var installation = new LogicalCameraInstallation
                    {
                        Id = Id("installation", registrationIndex),
                        LogicalCameraId = camera.Id,
                        RegistrationId = registration.Id,
                        InstallationPublicId = Id("installation-public", registrationIndex),
                        AssignedAtUtc = Epoch.AddMinutes(registrationIndex),
                        RetiredAtUtc = generation == 0 ? Epoch.AddDays(1) : null,
                        ReplacesInstallationId = predecessor?.Id,
                        AssignedByUserId = users.OwnerId,
                        RetiredByUserId = generation == 0 ? users.OwnerId : null,
                        AssignmentReasonCode = "performance-fixture",
                        RetirementReasonCode = generation == 0 ? "replaced" : null
                    };
                    predecessor = installation;
                    installations.Add(installation);
                    db.LogicalCameraInstallations.Add(installation);
                    if (generation == 1 && cameraIndex == 0)
                    {
                        activeInstallationIds[observatoryIndex] = installation.Id;
                    }
                    else if (generation == 1)
                    {
                        sparseInstallationIds[observatoryIndex] = installation.Id;
                    }
                }
            }
        }
        await db.SaveChangesAsync().ConfigureAwait(false);

        for (var registrationIndex = 0; registrationIndex < registrations.Count; registrationIndex++)
        {
            var registration = registrations[registrationIndex];
            var agentInstanceId = Id("agent-instance", registrationIndex);
            var bootSessionId = Id("boot-session", registrationIndex);
            db.DeviceFleetStates.Add(new DeviceFleetState
            {
                RegistrationId = registration.Id,
                AgentInstanceId = agentInstanceId,
                BootSessionId = bootSessionId,
                Sequence = 10,
                ObservedAtUtc = Epoch.AddMinutes(10),
                ReceivedAtUtc = Epoch.AddMinutes(10),
                ClockDiagnostic = FleetClockDiagnostic.WithinTolerance,
                ReportedHealth = FleetHealth.Healthy,
                SoftwareVersion = "1.0.0",
                ConfigurationSha256 = Sha($"fleet-config-{registrationIndex}"),
                StatusFingerprint = Sha($"fleet-status-{registrationIndex}"),
                CurrentPayloadSha256 = Sha($"fleet-payload-{registrationIndex}"),
                SnapshotJson = "{}"
            });
            db.DeviceHeartbeatRecords.AddRange(Enumerable.Range(1, 10).Select(sequence => new DeviceHeartbeatRecord
            {
                RegistrationId = registration.Id,
                AgentInstanceId = agentInstanceId,
                BootSessionId = bootSessionId,
                Sequence = sequence,
                ObservedAtUtc = Epoch.AddMinutes(sequence),
                ReceivedAtUtc = Epoch.AddMinutes(sequence),
                PayloadSha256 = Sha($"heartbeat-payload-{registrationIndex}-{sequence}"),
                StatusFingerprint = Sha($"heartbeat-status-{registrationIndex}-{sequence}"),
                ReportedHealth = FleetHealth.Healthy,
                ClockDiagnostic = FleetClockDiagnostic.WithinTolerance,
                AdvancedCurrent = true,
                IsSignificantSnapshot = sequence == 1,
                SnapshotJson = sequence == 1 ? "{}" : null
            }));
        }
        for (var index = 0; index < users.PersonalUserIds.Count; index++)
        {
            var userId = users.PersonalUserIds[index];
            db.RegisteredUserObservatoryFollows.Add(new RegisteredUserObservatoryFollow
            {
                UserId = userId,
                ObservatoryId = observatories[index % observatories.Count].Id,
                CreatedUtc = Epoch
            });
            db.RegisteredUserSubscriptions.Add(new RegisteredUserSubscription
            {
                Id = Id("subscription", index),
                UserId = userId,
                Kind = RegisteredUserSubscriptionKind.VerifiedEvent,
                CreatedUtc = Epoch
            });
            db.RegisteredUserNotificationPreferences.Add(new RegisteredUserNotificationPreference
            {
                UserId = userId,
                InAppEnabled = true,
                EmailEnabled = false,
                UpdatedUtc = Epoch
            });
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return new(
            observatories.Select(item => item.Id).ToArray(),
            registrations.Select(item => item.Id).ToArray(),
            registrations.Select(item => item.DevicePublicId!.Value).ToArray(),
            activeInstallationIds,
            sparseInstallationIds,
            environmentalSourceIds);
    }

    private static async Task SeedHistoryAsync(
        ApplicationDbContext db,
        DatasetTopology topology,
        int datasetOrdinal,
        int captureCount,
        int releasedPreviewCount,
        List<Guid> releasedPreviewIds)
    {
        var observatoryId = topology.ObservatoryIds[datasetOrdinal];
        var sourceId = topology.EnvironmentalSourceIds[datasetOrdinal];
        var sourceIdentity = Sha($"environment-source-identity-{datasetOrdinal}");
        for (var batchStart = 0; batchStart < captureCount; batchStart += CaptureBatchSize)
        {
            var batchEnd = Math.Min(captureCount, batchStart + CaptureBatchSize);
            for (var captureIndex = batchStart; captureIndex < batchEnd; captureIndex++)
            {
                var globalCaptureIndex = datasetOrdinal * 20_000 + captureIndex;
                var capturedAtUtc = Epoch.AddSeconds(globalCaptureIndex * 2L);
                var sparseCameraCapture = captureIndex % 20 == 0;
                var registrationIndex = datasetOrdinal * 4 + (sparseCameraCapture ? 3 : 1);
                var registrationId = topology.RegistrationIds[registrationIndex];
                var devicePublicId = topology.DevicePublicIds[registrationIndex];
                var installationId = sparseCameraCapture
                    ? topology.SparseInstallationIds[datasetOrdinal]
                    : topology.ActiveInstallationIds[datasetOrdinal];
                var frame = new CentralFrame
                {
                    Id = Id("frame", globalCaptureIndex),
                    RegistrationId = registrationId,
                    LogicalCameraInstallationId = installationId,
                    DevicePublicId = devicePublicId,
                    ObservatoryId = observatoryId,
                    AgentId = $"issue-107-agent-{registrationIndex:D2}",
                    FrameId = Id("frame-public", globalCaptureIndex),
                    CapturedAtUtc = capturedAtUtc,
                    FirstReceivedAtUtc = capturedAtUtc.AddSeconds(1),
                    RigId = $"rig-{datasetOrdinal}",
                    CaptureSequence = captureIndex + 1,
                    LocationEvidenceState = CentralCaptureLocationEvidenceState.ReportedUnresolved
                };
                var artifacts = new CentralArtifact[ArtifactRoles.Length];
                for (var artifactOrdinal = 0; artifactOrdinal < ArtifactRoles.Length; artifactOrdinal++)
                {
                    var artifactIndex = globalCaptureIndex * ArtifactRoles.Length + artifactOrdinal;
                    var role = ArtifactRoles[artifactOrdinal];
                    artifacts[artifactOrdinal] = new CentralArtifact
                    {
                        Id = Id("artifact-row", artifactIndex),
                        CentralFrameId = frame.Id,
                        ArtifactId = Id("artifact-public", artifactIndex),
                        DevicePublicId = devicePublicId,
                        Role = role,
                        RecipeVersion = "issue-107-v1",
                        ManifestSchemaVersion = "v2",
                        MediaType = ArtifactPayloads.Value[artifactOrdinal].MediaType,
                        ByteLength = ArtifactPayloads.Value[artifactOrdinal].Bytes.LongLength,
                        ChecksumSha256 = ArtifactPayloads.Value[artifactOrdinal].ChecksumSha256,
                        StorageReference = $"s3://skymonitor-artifacts/issue-107/{artifactOrdinal:D2}.bin",
                        ReceivedAtUtc = capturedAtUtc.AddSeconds(1),
                        IdempotencyKey = Sha($"artifact-idempotency-{artifactIndex}"),
                        Variant = $"variant-{artifactOrdinal}",
                        CreatedUtc = capturedAtUtc,
                        ObjectState = CentralArtifactObjectState.Available,
                        ReconstructionState = CentralReconstructionState.Complete
                    };
                    frame.Artifacts.Add(artifacts[artifactOrdinal]);
                }
                db.CentralFrames.Add(frame);
                for (var jobOrdinal = 0; jobOrdinal < 4; jobOrdinal++)
                {
                    var jobIndex = globalCaptureIndex * 4 + jobOrdinal;
                    db.CentralDerivativeJobs.Add(new CentralDerivativeJob
                    {
                        Id = Id("job", jobIndex),
                        SourceCentralArtifactId = artifacts[0].Id,
                        ResultCentralArtifactId = artifacts[jobOrdinal + 1].Id,
                        TargetRole = artifacts[jobOrdinal + 1].Role,
                        TargetRecipeVersion = "issue-107-v1",
                        TargetVariant = $"variant-{jobOrdinal + 1}",
                        RecipeName = $"issue-107-recipe-{jobOrdinal}",
                        RecipeOptionsJson = "{}",
                        InputSelectorJson = "{}",
                        RequestedRecipeIdentitySha256 = Sha($"recipe-{jobOrdinal}"),
                        ExpectedRecipeIdentitySha256 = Sha($"recipe-{jobOrdinal}"),
                        RequestIdentitySha256 = Sha($"job-request-{jobIndex}"),
                        Status = CentralDerivativeJobStatus.Completed,
                        AttemptCount = 1,
                        MaxAttempts = 3,
                        CreatedAtUtc = capturedAtUtc,
                        UpdatedAtUtc = capturedAtUtc.AddSeconds(1),
                        CompletedAtUtc = capturedAtUtc.AddSeconds(1),
                        StateReasonCode = "completed"
                    });
                }
                for (var observationOrdinal = 0; observationOrdinal < 2; observationOrdinal++)
                {
                    var observationIndex = globalCaptureIndex * 2 + observationOrdinal;
                    db.EnvironmentalObservations.Add(new EnvironmentalObservationRecord
                    {
                        Id = Id("environment-row", observationIndex),
                        SourceRecordId = sourceId,
                        SiteId = observatoryId,
                        RigId = frame.RigId,
                        SourceKind = EnvironmentalObservationSourceKind.Imported,
                        SourceIdentitySha256 = sourceIdentity,
                        ObservationId = Id("environment-observation", observationIndex),
                        SchemaVersion = "environmental-observation-v1",
                        Kind = observationOrdinal == 0
                            ? EnvironmentalObservationKind.CloudCover
                            : EnvironmentalObservationKind.RelativeHumidity,
                        Unit = observationOrdinal == 0
                            ? EnvironmentalObservationUnit.Fraction
                            : EnvironmentalObservationUnit.Percent,
                        NumericValue = observationOrdinal == 0 ? 0.2 : 45,
                        Quality = EnvironmentalObservationQuality.Good,
                        ObservedAtUtc = capturedAtUtc,
                        ObservedFromUtc = capturedAtUtc.AddMinutes(-1),
                        ObservedThroughUtc = capturedAtUtc,
                        ValidFromUtc = capturedAtUtc.AddMinutes(-1),
                        ValidThroughUtc = capturedAtUtc.AddMinutes(5),
                        StaleAfterUtc = capturedAtUtc.AddMinutes(2),
                        ReceivedAtUtc = capturedAtUtc.AddSeconds(1),
                        ClockDiagnostic = EnvironmentalClockDiagnostic.WithinTolerance,
                        PayloadSha256 = Sha($"environment-payload-{observationIndex}")
                    });
                }
                if (captureIndex < releasedPreviewCount)
                {
                    releasedPreviewIds.Add(artifacts[3].Id);
                }
            }
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }

    private static async Task SeedPreviewPublicationAsync(
        ApplicationDbContext db,
        DatasetTopology topology,
        List<Guid> previewIds,
        string ownerId)
    {
        for (var index = 0; index < previewIds.Count; index++)
        {
            db.PublicRecordPublicationDecisions.Add(new PublicRecordPublicationDecision
            {
                Id = Id("preview-release", index),
                PublicId = Id("preview-public", index),
                AuthorityObservatoryId = index < 60 ? topology.ObservatoryIds[0] : topology.ObservatoryIds[1],
                SubjectKind = PublicRecordSubjectKind.Artifact,
                State = PublicationDecisionState.Released,
                CentralArtifactId = previewIds[index],
                ProjectionSchemaVersion = "public-image-v1",
                OccurredAtUtc = Epoch.AddDays(2),
                ActorUserId = ownerId,
                ReasonCode = "performance-fixture"
            });
            if ((index + 1) % 200 == 0)
            {
                await db.SaveChangesAsync().ConfigureAwait(false);
                db.ChangeTracker.Clear();
            }
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    private static async Task<List<ReleasedEvent>> SeedTransientEventsAsync(
        ApplicationDbContext db,
        DatasetTopology topology,
        DatasetUsers users)
    {
        var template = CentralTransientPersistenceFixture.Create().Event;
        var released = new List<ReleasedEvent>(110);
        for (var datasetOrdinal = 0; datasetOrdinal < 2; datasetOrdinal++)
        {
            var eventCount = datasetOrdinal == 0 ? 50 : 500;
            var releasedCount = datasetOrdinal == 0 ? 10 : 100;
            var observatoryId = topology.ObservatoryIds[datasetOrdinal];
            for (var eventIndex = 0; eventIndex < eventCount; eventIndex++)
            {
                var globalEventIndex = datasetOrdinal * 1_000 + eventIndex;
                var globalCaptureIndex = datasetOrdinal * 20_000 + eventIndex * 20;
                var observedAtUtc = Epoch.AddSeconds(globalCaptureIndex * 2L);
                var centralEventId = Id("transient-row", globalEventIndex);
                var eventId = Id("transient-public", globalEventIndex);
                var eventVersionId = Id("transient-version", globalEventIndex);
                var observationId = Id("transient-observation", globalEventIndex);
                var evidenceId = Id("transient-evidence", globalEventIndex);
                var assessmentId = Id("transient-assessment", globalEventIndex);
                var reviewId = Id("transient-review", globalEventIndex);
                var sourceArtifactRowId = Id("artifact-row", globalCaptureIndex * ArtifactRoles.Length);
                var sourceArtifactId = Id("artifact-public", globalCaptureIndex * ArtifactRoles.Length);
                var sourceChecksum = ArtifactPayloads.Value[0].ChecksumSha256;
                var source = template.Observations[0].Source with
                {
                    EvidenceId = evidenceId,
                    Locator = template.Observations[0].Source.Locator with
                    {
                        Artifact = template.Observations[0].Source.Locator.Artifact with
                        {
                            ArtifactId = sourceArtifactId,
                            Role = FrameArtifactRole.Raw,
                            Variant = "variant-0",
                            RecipeIdentitySha256 = Sha("recipe-0"),
                            ChecksumSha256 = sourceChecksum
                        }
                    },
                    ObservationStartedUtc = observedAtUtc,
                    ObservationEndedUtc = observedAtUtc.AddSeconds(1)
                };
                var observation = template.Observations[0] with
                {
                    ObservationId = observationId,
                    Ordinal = 0,
                    Source = source,
                    BackgroundArtifacts = [],
                    Extraction = template.Observations[0].Extraction with { OriginatingCandidateId = null },
                    Geometry = template.Observations[0].Geometry with { SourceEvidenceId = evidenceId },
                    Features = template.Observations[0].Features with { SourceEvidenceId = evidenceId }
                };
                var assessment = template.Assessments[0] with
                {
                    AssessmentId = assessmentId,
                    CreatedUtc = observedAtUtc.AddSeconds(2),
                    Authority = TransientAssessmentAuthority.Authoritative,
                    Classification = TransientClassification.Meteor,
                    MeteorSeverity = TransientMeteorSeverity.Meteor,
                    ConfidenceMillionths = 900_000,
                    Reasons = template.Assessments[0].Reasons.Select(reason => reason with
                    {
                        ObservationIds = [observationId]
                    }).ToArray(),
                    EvidenceObservationIds = [observationId],
                    SupersedesAssessmentId = null
                };
                var review = new TransientReviewV1(
                    reviewId,
                    observedAtUtc.AddSeconds(3),
                    users.OwnerId,
                    TransientReviewDisposition.Confirmed,
                    assessmentId,
                    null,
                    ["performance-fixture"],
                    null);
                var transientEvent = template with
                {
                    EventId = eventId,
                    EventVersionId = eventVersionId,
                    Version = 1,
                    PreviousEventVersionId = null,
                    PreviousVersionCreatedUtc = null,
                    AgentId = $"issue-107-agent-{datasetOrdinal * 4 + 1:D2}",
                    State = TransientEventState.Validated,
                    EventCreatedUtc = observedAtUtc.AddSeconds(2),
                    VersionCreatedUtc = observedAtUtc.AddSeconds(3),
                    FirstObservedUtc = observedAtUtc,
                    LastObservedUtc = observedAtUtc.AddSeconds(1),
                    Observations = [observation],
                    Assessments = [assessment],
                    Reviews = [review],
                    Notifications = [],
                    Derivatives = []
                };
                var canonicalBytes = TransientContractJson.Serialize(transientEvent);
                var eventRecord = new CentralTransientEventRecord
                {
                    Id = centralEventId,
                    AgentId = transientEvent.AgentId,
                    EventId = eventId,
                    EventCreatedUtc = transientEvent.EventCreatedUtc
                };
                var observationRecord = new CentralTransientObservationRecord
                {
                    ObservationId = observationId,
                    CentralTransientEventId = centralEventId,
                    DetectorInputIdentitySha256 = observation.Provenance.DetectorInputIdentitySha256,
                    CalibrationIdentity = observation.Provenance.CalibrationIdentity,
                    MaskIdentity = observation.Provenance.MaskIdentity,
                    ProcessingProfileIdentity = observation.Provenance.ProcessingProfileIdentity,
                    OriginatingCandidateId = observation.Extraction.OriginatingCandidateId,
                    ExtractionProducerSchemaVersion = observation.Extraction.Producer.SchemaVersion,
                    ExtractionProducerKind = observation.Extraction.Producer.Kind,
                    ExtractionProducerName = observation.Extraction.Producer.Name,
                    ExtractionProducerVersion = observation.Extraction.Producer.Version,
                    ExtractionRecipeIdentitySha256 = observation.Extraction.RecipeIdentitySha256,
                    ExtractionReceiptIdentitySha256 = Sha($"extraction-receipt-{globalEventIndex}"),
                    GeometryJson = JsonSerializer.Serialize(observation.Geometry),
                    FeaturesJson = JsonSerializer.Serialize(observation.Features),
                    SourceReferenceId = observationId,
                    Source = new CentralTransientObservationSourceReference
                    {
                        ObservationId = observationId,
                        CentralArtifactId = sourceArtifactRowId,
                        EvidenceSchemaVersion = source.SchemaVersion,
                        EvidenceId = evidenceId,
                        LocatorSchemaVersion = source.Locator.SchemaVersion,
                        LocatorKind = source.Locator.Kind,
                        ArtifactId = sourceArtifactId,
                        ArtifactRole = FrameArtifactRole.Raw,
                        ArtifactVariant = "variant-0",
                        ArtifactRecipeIdentitySha256 = Sha("recipe-0"),
                        ArtifactChecksumSha256 = sourceChecksum,
                        ObservationStartedUtc = source.ObservationStartedUtc,
                        ObservationEndedUtc = source.ObservationEndedUtc,
                        TimingQuality = source.TimingQuality,
                        TimingProvenanceSource = source.TimingProvenance.Source,
                        TimingProvenanceVersion = source.TimingProvenance.Version
                    }
                };
                var assessmentRecord = new CentralTransientAssessmentRecord
                {
                    AssessmentId = assessmentId,
                    CentralTransientEventId = centralEventId,
                    CreatedUtc = assessment.CreatedUtc,
                    Authority = assessment.Authority,
                    Classification = assessment.Classification,
                    MeteorSeverity = assessment.MeteorSeverity,
                    ConfidenceMillionths = assessment.ConfidenceMillionths,
                    ProducerSchemaVersion = assessment.Producer.SchemaVersion,
                    ProducerKind = assessment.Producer.Kind,
                    ProducerName = assessment.Producer.Name,
                    ProducerVersion = assessment.Producer.Version,
                    RecipeIdentitySha256 = assessment.RecipeIdentitySha256,
                    ReceiptSchemaVersion = "transient-assessment-receipt-v1",
                    ExecutionIdentitySha256 = Sha($"assessment-execution-{globalEventIndex}"),
                    OptionsIdentitySha256 = Sha("assessment-options"),
                    CanonicalReceiptJson = "{}",
                    CanonicalReceiptSha256 = Sha("{}"),
                    CanonicalReceiptByteLength = 2
                };
                var versionRecord = new CentralTransientEventVersionRecord
                {
                    EventVersionId = eventVersionId,
                    CentralTransientEventId = centralEventId,
                    Version = 1,
                    State = transientEvent.State,
                    VersionCreatedUtc = transientEvent.VersionCreatedUtc,
                    FirstObservedUtc = transientEvent.FirstObservedUtc,
                    LastObservedUtc = transientEvent.LastObservedUtc,
                    SchemaVersion = transientEvent.SchemaVersion,
                    CanonicalEventJson = Encoding.UTF8.GetString(canonicalBytes),
                    CanonicalEventSha256 = Convert.ToHexString(SHA256.HashData(canonicalBytes)),
                    CanonicalEventByteLength = canonicalBytes.Length
                };
                var reviewRecord = new CentralTransientReviewRecord
                {
                    ReviewId = reviewId,
                    CentralTransientEventId = centralEventId,
                    CreatedUtc = review.CreatedUtc,
                    ReviewerIdentity = users.OwnerId,
                    Disposition = TransientReviewDisposition.Confirmed,
                    AssessmentId = assessmentId,
                    ReasonCodesJson = "[\"performance-fixture\"]"
                };
                eventRecord.Observations.Add(observationRecord);
                eventRecord.Assessments.Add(assessmentRecord);
                eventRecord.Versions.Add(versionRecord);
                eventRecord.Reviews.Add(reviewRecord);
                eventRecord.Current = new CentralTransientEventCurrent
                {
                    CentralTransientEventId = centralEventId,
                    LatestEventVersionId = eventVersionId,
                    LatestVersion = 1,
                    ActiveAssessmentId = assessmentId,
                    LatestReviewId = reviewId,
                    ReviewState = CentralTransientReviewState.Reviewed,
                    EffectiveClassification = assessment.Classification,
                    EffectiveMeteorSeverity = assessment.MeteorSeverity,
                    EffectiveConfidenceMillionths = assessment.ConfidenceMillionths,
                    UpdatedUtc = transientEvent.VersionCreatedUtc
                };
                versionRecord.Observations.Add(new CentralTransientEventVersionObservation
                {
                    CentralTransientEventId = centralEventId,
                    EventVersionId = eventVersionId,
                    Ordinal = 0,
                    ObservationId = observationId
                });
                versionRecord.Assessments.Add(new CentralTransientEventVersionAssessment
                {
                    CentralTransientEventId = centralEventId,
                    EventVersionId = eventVersionId,
                    Ordinal = 0,
                    AssessmentId = assessmentId
                });
                versionRecord.Reviews.Add(new CentralTransientEventVersionReview
                {
                    CentralTransientEventId = centralEventId,
                    EventVersionId = eventVersionId,
                    Ordinal = 0,
                    ReviewId = reviewId
                });
                assessmentRecord.EvidenceObservations.Add(new CentralTransientAssessmentObservation
                {
                    CentralTransientEventId = centralEventId,
                    AssessmentId = assessmentId,
                    Ordinal = 0,
                    ObservationId = observationId
                });
                db.CentralTransientEvents.Add(eventRecord);
                if (eventIndex < releasedCount)
                {
                    var publicId = Id("event-public-record", globalEventIndex);
                    db.PublicRecordPublicationDecisions.Add(new PublicRecordPublicationDecision
                    {
                        Id = Id("event-release", globalEventIndex),
                        PublicId = publicId,
                        AuthorityObservatoryId = observatoryId,
                        SubjectKind = PublicRecordSubjectKind.TransientEvent,
                        State = PublicationDecisionState.Released,
                        CentralTransientEventId = centralEventId,
                        SourceEventVersionId = eventVersionId,
                        ProjectionSchemaVersion = "public-event-v1",
                        OccurredAtUtc = transientEvent.VersionCreatedUtc.AddSeconds(1),
                        ActorUserId = users.OwnerId,
                        ReasonCode = "performance-fixture"
                    });
                    released.Add(new(centralEventId, publicId));
                }
                if ((eventIndex + 1) % 50 == 0 || eventIndex + 1 == eventCount)
                {
                    await db.SaveChangesAsync().ConfigureAwait(false);
                    db.ChangeTracker.Clear();
                }
            }
        }
        return released;
    }

    private static async Task SeedPersonalEventStateAsync(
        ApplicationDbContext db,
        DatasetUsers users,
        IReadOnlyList<ReleasedEvent> releasedEvents)
    {
        for (var index = 0; index < users.PersonalUserIds.Count; index++)
        {
            var userId = users.PersonalUserIds[index];
            var bookmarked = releasedEvents[index];
            db.RegisteredUserTransientEventBookmarks.Add(new RegisteredUserTransientEventBookmark
            {
                UserId = userId,
                CentralTransientEventId = bookmarked.CentralTransientEventId,
                CreatedUtc = Epoch
            });
            for (var notificationOrdinal = 0; notificationOrdinal < 2; notificationOrdinal++)
            {
                var released = releasedEvents[index * 2 + notificationOrdinal];
                db.RegisteredUserNotifications.Add(new RegisteredUserNotification
                {
                    Id = Id("user-notification", index * 2 + notificationOrdinal),
                    UserId = userId,
                    Kind = RegisteredUserNotificationKind.VerifiedEventReleased,
                    CentralTransientEventId = released.CentralTransientEventId,
                    PublicRecordId = released.PublicId,
                    Title = "Verified event released",
                    CreatedUtc = Epoch,
                    DeduplicationKey = $"issue-107-event-{index:D2}-{notificationOrdinal:D2}"
                });
            }
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    private static async Task AssertCountsAsync(
        ApplicationDbContext db,
        DatasetTopology topology,
        IReadOnlyCollection<Guid> previewIds,
        IReadOnlyCollection<ReleasedEvent> releasedEvents)
    {
        var observatoryIds = topology.ObservatoryIds.ToArray();
        var frameIds = db.CentralFrames.Where(frame => observatoryIds.Contains(frame.ObservatoryId)).Select(frame => frame.Id);
        var artifactIds = db.CentralArtifacts.Where(artifact => frameIds.Contains(artifact.CentralFrameId)).Select(item => item.Id);
        (await db.CentralFrames.CountAsync(frame => observatoryIds.Contains(frame.ObservatoryId)).ConfigureAwait(false))
            .Should().Be(11_000);
        (await db.CentralFrames.CountAsync(frame => frame.ObservatoryId == topology.ObservatoryIds[0])
            .ConfigureAwait(false)).Should().Be(1_000);
        (await db.CentralFrames.CountAsync(frame => frame.ObservatoryId == topology.ObservatoryIds[1])
            .ConfigureAwait(false)).Should().Be(10_000);
        (await db.CentralArtifacts.CountAsync(artifact => frameIds.Contains(artifact.CentralFrameId)).ConfigureAwait(false))
            .Should().Be(66_000);
        (await db.CentralArtifacts.CountAsync(artifact => artifact.Frame!.ObservatoryId == topology.ObservatoryIds[0])
            .ConfigureAwait(false)).Should().Be(6_000);
        (await db.CentralArtifacts.CountAsync(artifact => artifact.Frame!.ObservatoryId == topology.ObservatoryIds[1])
            .ConfigureAwait(false)).Should().Be(60_000);
        (await db.CentralDerivativeJobs.CountAsync(job => artifactIds.Contains(job.SourceCentralArtifactId))
            .ConfigureAwait(false)).Should().Be(44_000);
        (await db.CentralDerivativeJobs.CountAsync(job => job.SourceArtifact!.Frame!.ObservatoryId == topology.ObservatoryIds[0])
            .ConfigureAwait(false)).Should().Be(4_000);
        (await db.CentralDerivativeJobs.CountAsync(job => job.SourceArtifact!.Frame!.ObservatoryId == topology.ObservatoryIds[1])
            .ConfigureAwait(false)).Should().Be(40_000);
        (await db.EnvironmentalObservations.CountAsync(item => observatoryIds.Contains(item.SiteId)).ConfigureAwait(false))
            .Should().Be(22_000);
        (await db.EnvironmentalObservations.CountAsync(item => item.SiteId == topology.ObservatoryIds[0])
            .ConfigureAwait(false)).Should().Be(2_000);
        (await db.EnvironmentalObservations.CountAsync(item => item.SiteId == topology.ObservatoryIds[1])
            .ConfigureAwait(false)).Should().Be(20_000);
        (await db.Observatories.CountAsync(item => observatoryIds.Contains(item.Id)).ConfigureAwait(false)).Should().Be(10);
        (await db.LogicalCameras.CountAsync(item => observatoryIds.Contains(item.ObservatoryId)).ConfigureAwait(false))
            .Should().Be(20);
        (await db.LogicalCameraInstallations.CountAsync(item => observatoryIds.Contains(item.LogicalCamera!.ObservatoryId))
            .ConfigureAwait(false)).Should().Be(40);
        (await db.ObservatoryMemberships.CountAsync(item => observatoryIds.Contains(item.ObservatoryId)).ConfigureAwait(false))
            .Should().Be(40);
        (await db.ObservatoryInvitations.CountAsync(item => observatoryIds.Contains(item.ObservatoryId)).ConfigureAwait(false))
            .Should().Be(10);
        (await db.DeviceHeartbeatRecords.CountAsync(item => topology.RegistrationIds.Contains(item.RegistrationId))
            .ConfigureAwait(false)).Should().Be(400);
        (await db.CentralTransientEvents.CountAsync(item =>
                item.AgentId.StartsWith("issue-107-agent-")).ConfigureAwait(false))
            .Should().Be(550);
        (await db.RegisteredUserObservatoryFollows.CountAsync(item => observatoryIds.Contains(item.ObservatoryId))
            .ConfigureAwait(false)).Should().Be(20);
        (await db.RegisteredUserTransientEventBookmarks.CountAsync(item =>
                releasedEvents.Select(released => released.CentralTransientEventId).Contains(item.CentralTransientEventId))
            .ConfigureAwait(false)).Should().Be(20);
        (await db.RegisteredUserSubscriptions.CountAsync(item => item.UserId.StartsWith("issue-107-performance-user-"))
            .ConfigureAwait(false)).Should().Be(20);
        (await db.RegisteredUserNotifications.CountAsync(item => item.UserId.StartsWith("issue-107-performance-user-"))
            .ConfigureAwait(false)).Should().Be(40);
        previewIds.Should().HaveCount(660);
        releasedEvents.Should().HaveCount(110);
        ArtifactPayloads.Value[3].Bytes.LongLength.Should().Be(611_814);
        ArtifactPayloads.Value[3].ChecksumSha256.Should()
            .Be("2932749E6479187E574BC339D62C120C5CA728DBDF60DFA4F27C39B878C19849");
        (await db.PublicRecordPublicationDecisions.CountAsync(item =>
                item.SubjectKind == PublicRecordSubjectKind.Artifact
                && previewIds.Contains(item.CentralArtifactId!.Value)).ConfigureAwait(false)).Should().Be(660);
        (await db.PublicRecordPublicationDecisions.CountAsync(item =>
                item.SubjectKind == PublicRecordSubjectKind.TransientEvent
                && releasedEvents.Select(released => released.CentralTransientEventId)
                    .Contains(item.CentralTransientEventId!.Value)).ConfigureAwait(false)).Should().Be(110);
    }

    private static async Task EnsureArtifactObjectsAsync(IMinioClient minio)
    {
        const string bucket = "skymonitor-artifacts";
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), CancellationToken.None)
                .ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), CancellationToken.None)
                .ConfigureAwait(false);
        }
        for (var ordinal = 0; ordinal < ArtifactPayloads.Value.Length; ordinal++)
        {
            var payload = ArtifactPayloads.Value[ordinal];
            await using var stream = new MemoryStream(payload.Bytes, writable: false);
            await minio.PutObjectAsync(new PutObjectArgs()
                    .WithBucket(bucket)
                    .WithObject($"issue-107/{ordinal:D2}.bin")
                    .WithStreamData(stream)
                    .WithObjectSize(payload.Bytes.LongLength)
                    .WithContentType(payload.MediaType), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static ArtifactPayload[] CreateArtifactPayloads()
    {
        var raw = CreateCanonicalFrame(3096, 2080, CameraPixelFormat.BayerRggb16);
        var calibrated = CreateCanonicalFrame(1936, 1216, CameraPixelFormat.Mono16);
        var combined = CreatePayload(1024, 2);
        var encodedPreview = EncodePreview(calibrated);
        var metadata = Encoding.UTF8.GetBytes("{\"schema\":\"issue-107-performance-metadata-v1\"}");
        return new[]
        {
            CreateArtifactPayload(raw, "application/x-hvo-frame"),
            CreateArtifactPayload(calibrated, "application/x-hvo-frame"),
            CreateArtifactPayload(combined, "application/x-hvo-frame"),
            CreateArtifactPayload(encodedPreview, "image/jpeg"),
            CreateArtifactPayload(encodedPreview, "image/jpeg"),
            CreateArtifactPayload(metadata, "application/json")
        };
    }

    private static byte[] CreateCanonicalFrame(int width, int height, CameraPixelFormat format)
    {
        var stride = checked(width * 2);
        var payload = GC.AllocateUninitializedArray<byte>(checked(stride * height));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = format == CameraPixelFormat.Mono16
                    ? (ushort)((2025L + 257L * (y * (long)width + x)) & 0xFFFF)
                    : (ushort)((64 + 2025 + 31 * x + 17 * y + 997 * ((y & 1) * 2 + (x & 1))) & 0x3FFF);
                var offset = y * stride + x * 2;
                payload[offset] = (byte)value;
                payload[offset + 1] = (byte)(value >> 8);
            }
        }
        return payload;
    }

    private static byte[] EncodePreview(byte[] payload)
    {
        var layout = new FrameLayoutDescriptor(
            1936,
            1216,
            1936 * 2,
            CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian,
            16,
            16,
            FrameSamplePacking.ByteAligned,
            ColorFilterArrayPattern.None,
            0,
            ushort.MaxValue,
            payload.Length);
        var artifact = new ProcessingArtifact(
            Id("w1-source", 0),
            FrameArtifactRole.Raw,
            "source",
            new string('0', 64),
            "application/x-hvo-frame",
            layout,
            payload,
            new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(20),
            new ProcessingCompatibilityIdentity(
                "W1-rig", "north-up", "none", "full", "W1-sensor", "night", "pipeline-v1"));
        var request = new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new { }),
            ProcessingInputSelector.Raw("source"),
            [artifact],
            "jpeg");
        var outcome = new ProcessingRecipeExecutor().ExecuteAsync(request).AsTask().GetAwaiter().GetResult();
        if (outcome.Status != ProcessingOutcomeStatus.Produced || outcome.Products.Count != 1)
        {
            throw new InvalidOperationException($"Canonical W1 preview encoding failed: {outcome.ReasonCode}");
        }
        return outcome.Products[0].Payload.ToArray();
    }

    private static byte[] CreatePayload(int length, int seed)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index + seed) % 251);
        }
        return bytes;
    }

    private static ArtifactPayload CreateArtifactPayload(byte[] bytes, string mediaType)
        => new(bytes, mediaType, Convert.ToHexString(SHA256.HashData(bytes)));

    private static Guid Id(string kind, long index)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{Workload}:{kind}:{index}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string Sha(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record DatasetUsers(
        string OwnerId,
        string SecondOwnerId,
        string ManagerId,
        string ViewerId,
        IReadOnlyList<string> PersonalUserIds);

    private sealed record DatasetTopology(
        IReadOnlyList<Guid> ObservatoryIds,
        IReadOnlyList<Guid> RegistrationIds,
        IReadOnlyList<Guid> DevicePublicIds,
        IReadOnlyList<Guid> ActiveInstallationIds,
        IReadOnlyList<Guid> SparseInstallationIds,
        IReadOnlyList<Guid> EnvironmentalSourceIds);

    private sealed record ReleasedEvent(Guid CentralTransientEventId, Guid PublicId);

    private sealed record ArtifactPayload(byte[] Bytes, string MediaType, string ChecksumSha256);
}
