using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSkyMapUiServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task GetSkyMapAsync_WhenTheOperationsReadPolicyFails_DeniesWithoutProjectingAsync()
    {
        var projection = new RecordingProjection();
        var authorization = CreateAuthorization(out var principal, succeeded: false);
        var service = CreateService(projection, principal, authorization.Object);

        var result = await service.GetSkyMapAsync(null, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.AreEqual(0, projection.Calls);
        authorization.Verify(
            service => service.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1),
            Times.Once);
    }

    [TestMethod]
    public async Task GetSkyMapAsync_WhenAuthorized_ProjectsTheRequestedInstantAsync()
    {
        var projection = new RecordingProjection();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(projection, principal, authorization.Object);

        var result = await service.GetSkyMapAsync(Now.AddHours(-3), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.AreEqual(Now.AddHours(-3), projection.LastRequested);
        authorization.Verify(
            service => service.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1),
            Times.Once);
    }

    [TestMethod]
    [DataRow(25d, 0d)]
    [DataRow(0d, 63d)]
    public async Task GetSkyMapAsync_ForAnInstantOutsideTheAcceptedWindow_IsRejectedAsync(double futureHours, double pastDays)
    {
        var projection = new RecordingProjection();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(projection, principal, authorization.Object);

        var result = await service
            .GetSkyMapAsync(Now.AddHours(futureHours).AddDays(-pastDays), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, result.Kind);
        Assert.AreEqual(CameraAgentSkyMapInstantBounds.RejectionMessage, result.Message);
        Assert.AreEqual(0, projection.Calls);
    }

    [TestMethod]
    [DataRow(24d, 0d)]
    [DataRow(0d, 62d)]
    public async Task GetSkyMapAsync_AtTheWindowBoundary_IsAcceptedAsync(double futureHours, double pastDays)
    {
        var projection = new RecordingProjection();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(projection, principal, authorization.Object);

        var result = await service
            .GetSkyMapAsync(Now.AddHours(futureHours).AddDays(-pastDays), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.AreEqual(1, projection.Calls);
    }

    [TestMethod]
    public async Task GetSkyMapAsync_WhenTheProjectionFails_ReturnsAFixedUnavailableStateAsync()
    {
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(new ThrowingProjection(), principal, authorization.Object);

        var result = await service.GetSkyMapAsync(null, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("The sky map projection is unavailable.", result.Message);
    }

    [TestMethod]
    public async Task GetManualLocationAsync_WhenTheOperationsReadPolicyFails_DeniesWithoutReadingTheStoreAsync()
    {
        var store = new RecordingDeploymentLocationStore();
        var authorization = CreateAuthorization(out var principal, succeeded: false);
        var service = CreateService(new RecordingProjection(), principal, authorization.Object, store);

        var result = await service.GetManualLocationAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.AreEqual(0, store.Reads);
    }

    [TestMethod]
    public async Task GetManualLocationAsync_WhenAuthorized_ReturnsTheProtectedManualStateAsync()
    {
        var store = new RecordingDeploymentLocationStore();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(new RecordingProjection(), principal, authorization.Object, store);

        var result = await service.GetManualLocationAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.IsTrue(result.Value!.Supported);
        Assert.AreEqual(1, store.Reads);
    }

    [TestMethod]
    public async Task ApplyManualLocationAsync_WhenTheMutatePolicyFails_DeniesWithoutTouchingTheStoreAsync()
    {
        var store = new RecordingDeploymentLocationStore();
        var authorization = CreateAuthorization(out var principal, succeeded: false);
        var service = CreateService(new RecordingProjection(), principal, authorization.Object, store);

        var result = await ApplyAsync(service).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.IsEmpty(store.Requests);
        authorization.Verify(
            service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1),
            Times.Once);
    }

    [TestMethod]
    public async Task ApplyManualLocationAsync_WhenAuthorized_PassesTheOwnerIdAsTheRecordedActorAsync()
    {
        var store = new RecordingDeploymentLocationStore();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(new RecordingProjection(), principal, authorization.Object, store);

        var result = await ApplyAsync(service).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        var request = store.Requests.Single();
        Assert.AreEqual("owner-id", request.Actor);
        Assert.AreEqual("key-1", request.IdempotencyKey);
        Assert.AreEqual(4L, request.ExpectedVersion);
        Assert.AreEqual(2L, request.ExpectedManualSequence);
        Assert.AreEqual("America/Phoenix", request.TimeZoneId);
    }

    [TestMethod]
    public async Task ApplyManualLocationAsync_MapsRejectedCommandsToTheirOperatorResultKindAsync()
    {
        var store = new RecordingDeploymentLocationStore
        {
            Status = ManualDeploymentLocationStatus.Conflict,
            ReasonCode = ManualDeploymentLocationContract.ExpectedVersionConflictReasonCode
        };
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(new RecordingProjection(), principal, authorization.Object, store);

        var conflict = await ApplyAsync(service).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Conflict, conflict.Kind);
        Assert.Contains("Refresh before retrying", conflict.Message!, StringComparison.Ordinal);

        store.Status = ManualDeploymentLocationStatus.Invalid;
        store.ReasonCode = null;
        store.FieldPath = "location.timeZoneId";

        var invalid = await ApplyAsync(service).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, invalid.Kind);
        Assert.Contains("IANA identifier", invalid.Message!, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("manual.expectedManualSequenceConflict", "Another coordinate entry was recorded")]
    [DataRow("manual.supersededEntry", "a configuration change has since superseded")]
    [DataRow("manual.idempotencyKeyConflict", "already recorded with different coordinates")]
    public async Task ApplyManualLocationAsync_DescribesEachConflictReasonDistinctlyAsync(
        string reasonCode,
        string expected)
    {
        var store = new RecordingDeploymentLocationStore
        {
            Status = ManualDeploymentLocationStatus.Conflict,
            ReasonCode = reasonCode
        };
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(new RecordingProjection(), principal, authorization.Object, store);

        var result = await ApplyAsync(service).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Conflict, result.Kind);
        Assert.Contains(expected, result.Message!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ApplyManualLocationAsync_WhenTheStoreFails_ReturnsAFixedUnavailableStateAsync()
    {
        var store = new RecordingDeploymentLocationStore { Throw = true };
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateService(new RecordingProjection(), principal, authorization.Object, store);

        var result = await ApplyAsync(service).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("The coordinate change could not be completed.", result.Message);
    }

    [TestMethod]
    public async Task GetSiteAsync_WhenTheOperationsReadPolicyFails_DeniesWithoutReadingTheProfileAsync()
    {
        var profiles = new RecordingSiteProfileStore();
        var authorization = CreateAuthorization(out var principal, succeeded: false);
        var service = CreateSiteService(principal, authorization.Object, profiles);

        var result = await service.GetSiteAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.AreEqual(0, profiles.Reads);
        authorization.Verify(
            service => service.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1),
            Times.Once);
    }

    [TestMethod]
    public async Task GetSiteAsync_ForAStandaloneCamera_ReportsNoAssignmentAndNamesEveryActorAsync()
    {
        var profiles = new RecordingSiteProfileStore
        {
            State = ProfileState(
                new SiteProfileRevision(1, Now, "owner-id", null, "key-1", SiteProfileValues.Empty))
        };
        var locations = new RecordingDeploymentLocationStore
        {
            History =
            [
                new ManualDeploymentLocationAuditEntry(
                    1, Now, "operator-id", null, "manual-1", 3, 31.5, -110.25, 1400, "America/Phoenix"),
                new ManualDeploymentLocationAuditEntry(
                    2, Now, "removed-id", null, "manual-2", 4, 31.5, -110.25, 1400, "America/Phoenix")
            ]
        };
        var secrets = new Mock<IDeviceSecretStore>(MockBehavior.Strict);
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateSiteService(
            principal,
            authorization.Object,
            profiles,
            locations,
            new CameraAgentHostOptions
            {
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
                SiteMap = new SiteMapOptions
                {
                    TileTemplate = "https://tiles.example.test/{z}/{x}/{y}.png",
                    Attribution = "Example tiles",
                    AttributionLink = new Uri("https://tiles.example.test/terms"),
                    Zoom = 9
                }
            },
            secrets.Object,
            new Dictionary<string, ApplicationUser>(StringComparer.Ordinal)
            {
                ["owner-id"] = new() { Id = "owner-id", Email = "owner@home.lan", UserName = "owner@home.lan" },
                ["operator-id"] = new() { Id = "operator-id", UserName = "night-operator" }
            });

        var result = await service.GetSiteAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        var site = result.Value!;
        Assert.AreSame(profiles.State, site.Profile);
        Assert.AreEqual("owner@home.lan", site.OwnerLoginEmail);
        Assert.AreEqual(CameraAgentSiteAssignmentState.Standalone, site.Assignment.State);
        Assert.IsNull(site.Assignment.RegistrationName);
        Assert.AreEqual(
            new CameraAgentSiteMapSettings(
                true, "https://tiles.example.test/{z}/{x}/{y}.png", "Example tiles",
                new Uri("https://tiles.example.test/terms"), 9),
            site.Map);
        // The profile and manual-location audits share one name lookup; an account that no longer exists stays
        // unnamed rather than falling back to its identifier.
        Assert.HasCount(2, site.ActorNames);
        Assert.AreEqual("owner@home.lan", site.ActorNames["owner-id"]);
        Assert.AreEqual("night-operator", site.ActorNames["operator-id"]);
        Assert.IsFalse(site.ActorNames.ContainsKey("removed-id"));
        secrets.Verify(store => store.GetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [DataRow("registered")]
    [DataRow("not-registered")]
    [DataRow("unreadable")]
    public async Task GetSiteAsync_WithCentralIntegration_ReportsTheRegistrationAsTheCameraLastHeardItAsync(
        string scenario)
    {
        var observatory = ObservatoryLocationSnapshot.Create(
            Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, 35.347, -113.878, 520, "America/Phoenix", 1000);
        var deployment = DeploymentLocationSnapshot.Create(
            "hvo-observatory", 4, "gps-receiver", 3, DateTimeOffset.UnixEpoch, null,
            35.347, -113.878, 520, "America/Phoenix");
        var secrets = new Mock<IDeviceSecretStore>();
        var setup = secrets.Setup(store => store.GetAsync(It.IsAny<CancellationToken>()));
        switch (scenario)
        {
            case "registered":
                setup.ReturnsAsync(new DeviceSecrets(
                    Guid.NewGuid(), observatory.ObservatoryId, "  East dome  ", "token", "/heartbeat", 60,
                    Now.AddHours(-1), Now.AddHours(1), "device-key", new CentralIdentityOptions(),
                    DeploymentLocationAcknowledgment: new DeploymentLocationAcknowledgment(
                        observatory, deployment, DeploymentLocationSourceKind.Gps,
                        DeploymentLocationResolutionStatus.Acknowledged, "within-observatory-boundary", Now, Now)));
                break;
            case "not-registered":
                setup.ReturnsAsync((DeviceSecrets?)null);
                break;
            default:
                setup.ThrowsAsync(new IOException("fixture failure"));
                break;
        }
        var reconciliation = new DeploymentLocationReconciliationState
        {
            Outcome = "acknowledged",
            LastAttemptUtc = Now.AddMinutes(-5),
            LastSuccessUtc = Now.AddMinutes(-5)
        };
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateSiteService(
            principal,
            authorization.Object,
            new RecordingSiteProfileStore(),
            new RecordingDeploymentLocationStore { PendingVersion = 5 },
            new CameraAgentHostOptions(),
            secrets.Object,
            reconciliation: reconciliation);

        var result = await service.GetSiteAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        var assignment = result.Value!.Assignment;
        Assert.AreEqual("acknowledged", assignment.ReconciliationOutcome);
        Assert.AreEqual(Now.AddMinutes(-5), assignment.LastAttemptUtc);
        switch (scenario)
        {
            case "registered":
                Assert.AreEqual(CameraAgentSiteAssignmentState.Registered, assignment.State);
                Assert.AreEqual("East dome", assignment.RegistrationName);
                Assert.AreEqual(DeploymentLocationResolutionStatus.Acknowledged, assignment.Membership);
                Assert.AreEqual(4L, assignment.AcknowledgedVersion);
                Assert.AreEqual(5L, assignment.ProposedVersion);
                break;
            case "not-registered":
                Assert.AreEqual(CameraAgentSiteAssignmentState.NotRegistered, assignment.State);
                Assert.IsNull(assignment.RegistrationName);
                break;
            default:
                // An unreadable registration degrades that one fact; the rest of the page still renders.
                Assert.AreEqual(CameraAgentSiteAssignmentState.Unavailable, assignment.State);
                Assert.IsNull(assignment.Membership);
                break;
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetSiteAsync_WhenTheProfileCannotBeRead_ReturnsAFixedUnavailableStateAsync(bool storeMissing)
    {
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateSiteService(
            principal,
            authorization.Object,
            storeMissing ? null : new RecordingSiteProfileStore { Throw = true });

        var result = await service.GetSiteAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("The site profile is unavailable.", result.Message);
    }

    [TestMethod]
    public async Task SaveSiteProfileAsync_WhenTheMutatePolicyFails_DeniesWithoutTouchingTheProfileAsync()
    {
        var profiles = new RecordingSiteProfileStore();
        var authorization = CreateAuthorization(out var principal, succeeded: false);
        var service = CreateSiteService(principal, authorization.Object, profiles);

        var result = await SaveAsync(service).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.IsEmpty(profiles.Requests);
        authorization.Verify(
            service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1),
            Times.Once);
    }

    [TestMethod]
    public async Task SaveSiteProfileAsync_WhenAuthorized_RecordsTheOwnerIdAsTheActorAsync()
    {
        var profiles = new RecordingSiteProfileStore();
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateSiteService(principal, authorization.Object, profiles);

        var result = await SaveAsync(service).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.AreEqual(
            new SiteProfileRequest(SavedProfile, 3, "profile-key", "owner-id", "renamed"),
            profiles.Requests.Single());
    }

    [TestMethod]
    [DataRow(SiteProfileStatus.Conflict, SiteProfileLimits.ExpectedVersionConflictReasonCode, null, "changed since this page was read")]
    [DataRow(SiteProfileStatus.Conflict, SiteProfileLimits.IdempotencyKeyConflictReasonCode, null, "already recorded with a different profile")]
    [DataRow(SiteProfileStatus.Invalid, SiteProfileLimits.InvalidFieldReasonCode, "observatoryName", "Observatory name must be at most 80")]
    [DataRow(SiteProfileStatus.Invalid, SiteProfileLimits.InvalidFieldReasonCode, "cameraName", "Camera name must be at most 80")]
    [DataRow(SiteProfileStatus.Invalid, SiteProfileLimits.InvalidFieldReasonCode, "ownerName", "Owner name must be at most 80")]
    [DataRow(SiteProfileStatus.Invalid, SiteProfileLimits.InvalidFieldReasonCode, "ownerContact", "Owner contact must be at most 200")]
    [DataRow(SiteProfileStatus.Invalid, SiteProfileLimits.InvalidCommandReasonCode, "reason", "rejected before anything durable changed")]
    public async Task SaveSiteProfileAsync_MapsRejectedCommandsToFixedGuidanceAsync(
        SiteProfileStatus status,
        string reasonCode,
        string? fieldPath,
        string expected)
    {
        var profiles = new RecordingSiteProfileStore
        {
            Status = status,
            ReasonCode = reasonCode,
            FieldPath = fieldPath
        };
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateSiteService(principal, authorization.Object, profiles);

        var result = await SaveAsync(service).ConfigureAwait(false);

        Assert.AreEqual(
            status == SiteProfileStatus.Conflict ? OperatorUiResultKind.Conflict : OperatorUiResultKind.Invalid,
            result.Kind);
        Assert.Contains(expected, result.Message!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SaveSiteProfileAsync_WhenTheStoreFails_ReturnsAFixedUnavailableStateAsync()
    {
        var authorization = CreateAuthorization(out var principal, succeeded: true);
        var service = CreateSiteService(
            principal, authorization.Object, new RecordingSiteProfileStore { Throw = true });

        var result = await SaveAsync(service).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("The site profile change could not be completed.", result.Message);
    }

    private static readonly SiteProfileValues SavedProfile = new(
        "Hualapai Valley Observatory", "East dome", "Night Owner", "owner@home.lan");

    private static async Task<OperatorUiResult<SiteProfileResult>> SaveAsync(CameraAgentSkyMapUiService service)
        => await service.SaveSiteProfileAsync(SavedProfile, 3, "profile-key", "renamed", CancellationToken.None)
            .ConfigureAwait(false);

    private static SiteProfileState ProfileState(params SiteProfileRevision[] history)
        => new(history.Length, history.FirstOrDefault()?.Profile ?? SiteProfileValues.Empty, null, null, null, null, history);

    private static CameraAgentSkyMapUiService CreateSiteService(
        ClaimsPrincipal principal,
        IAuthorizationService authorization,
        ISiteProfileStore? profiles,
        IDeploymentLocationStore? locations = null,
        CameraAgentHostOptions? options = null,
        IDeviceSecretStore? secrets = null,
        Dictionary<string, ApplicationUser>? users = null,
        DeploymentLocationReconciliationState? reconciliation = null)
    {
        var userStore = new Mock<IUserStore<ApplicationUser>>();
        userStore
            .Setup(store => store.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
                users is not null && users.TryGetValue(id, out var user) ? user : null);
        return new(
            new StubAuthenticationStateProvider(principal),
            authorization,
            new RecordingProjection(),
            locations ?? new RecordingDeploymentLocationStore(),
            new FixedTimeProvider(Now),
            NullLogger<CameraAgentSkyMapUiService>.Instance,
            profiles,
            Options.Create(options ?? new CameraAgentHostOptions
            {
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
            }),
            Options.Create(new LocalIdentityOptions { AdminEmail = "  owner@home.lan " }),
            secrets,
            reconciliation,
            new UserManager<ApplicationUser>(
                userStore.Object, null!, null!, null!, null!, null!, null!, null!,
                NullLogger<UserManager<ApplicationUser>>.Instance));
    }

    private static async Task<OperatorUiResult<ManualDeploymentLocationResult>> ApplyAsync(
        CameraAgentSkyMapUiService service)
        => await service.ApplyManualLocationAsync(
            31.5,
            -110.25,
            1400,
            "America/Phoenix",
            4,
            2,
            "key-1",
            "relocated",
            CancellationToken.None).ConfigureAwait(false);

    private static Mock<IAuthorizationService> CreateAuthorization(out ClaimsPrincipal principal, bool succeeded)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "owner-id"),
                new Claim(
                    CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
            ],
            IdentityConstants.ApplicationScheme));
        principal = user;
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization
            .Setup(service => service.AuthorizeAsync(user, null, It.IsAny<string>()))
            .ReturnsAsync(succeeded ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return authorization;
    }

    private static CameraAgentSkyMapUiService CreateService(
        ICameraAgentSkyMapProjection projection,
        ClaimsPrincipal principal,
        IAuthorizationService authorization,
        IDeploymentLocationStore? store = null)
        => new(
            new StubAuthenticationStateProvider(principal),
            authorization,
            projection,
            store ?? new RecordingDeploymentLocationStore(),
            new FixedTimeProvider(Now),
            NullLogger<CameraAgentSkyMapUiService>.Instance);

    private sealed class RecordingDeploymentLocationStore : IDeploymentLocationStore
    {
        private static readonly ManualDeploymentLocationState State = new(
            Supported: true,
            LocationId: "hvo-observatory",
            ActiveVersion: 4,
            KnownVersion: 4,
            NextVersion: 5,
            PendingVersion: null,
            ManualSequence: 2,
            CentralAcknowledgementRequired: false,
            StagedAcknowledgementPending: false,
            CandidateAwaitingAcknowledgement: false,
            Override: null,
            OverrideSupersededAtUtc: null,
            History: []);

        internal int Reads { get; private set; }

        internal IReadOnlyList<ManualDeploymentLocationAuditEntry> History { get; init; } = [];

        internal long? PendingVersion { get; init; }

        internal List<ManualDeploymentLocationRequest> Requests { get; } = [];

        internal ManualDeploymentLocationStatus Status { get; set; } = ManualDeploymentLocationStatus.Applied;

        internal string? ReasonCode { get; set; }

        internal string? FieldPath { get; set; }

        internal bool Throw { get; set; }

        public DeploymentLocationSnapshot? Active => null;

        public ManualDeploymentLocationState Manual
        {
            get
            {
                Reads++;
                return State with { History = History, PendingVersion = PendingVersion };
            }
        }

        public ValueTask<ManualDeploymentLocationResult> ApplyManualAsync(
            ManualDeploymentLocationRequest request,
            CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new InvalidOperationException("fixture failure");
            }
            Requests.Add(request);
            return ValueTask.FromResult(
                new ManualDeploymentLocationResult(Status, ReasonCode, FieldPath, State));
        }

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public DeploymentLocationSnapshot Resolve(
            CaptureLocationProvenance provenance,
            DateTimeOffset? effectiveUtc = null)
            => throw new NotSupportedException();
    }

    private sealed class RecordingSiteProfileStore : ISiteProfileStore
    {
        internal SiteProfileState State { get; init; } = ProfileState();

        internal int Reads { get; private set; }

        internal List<SiteProfileRequest> Requests { get; } = [];

        internal SiteProfileStatus Status { get; init; } = SiteProfileStatus.Applied;

        internal string? ReasonCode { get; init; }

        internal string? FieldPath { get; init; }

        internal bool Throw { get; init; }

        public ValueTask<SiteProfileState> GetAsync(CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new InvalidDataException("fixture failure");
            }
            Reads++;
            return ValueTask.FromResult(State);
        }

        public ValueTask<SiteProfileResult> ApplyAsync(SiteProfileRequest request, CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new IOException("fixture failure");
            }
            Requests.Add(request);
            return ValueTask.FromResult(new SiteProfileResult(Status, ReasonCode, FieldPath, State));
        }
    }

    private sealed class StubAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingProjection : ICameraAgentSkyMapProjection
    {
        internal int Calls { get; private set; }

        internal DateTimeOffset? LastRequested { get; private set; }

        public ValueTask<CameraAgentSkyMapProjectionResult> ProjectAsync(
            DateTimeOffset? atUtc,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequested = atUtc;
            return ValueTask.FromResult(SkyMapTestData.Result(atUtc ?? Now));
        }
    }

    private sealed class ThrowingProjection : ICameraAgentSkyMapProjection
    {
        public ValueTask<CameraAgentSkyMapProjectionResult> ProjectAsync(
            DateTimeOffset? atUtc,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("fixture failure");
    }
}
