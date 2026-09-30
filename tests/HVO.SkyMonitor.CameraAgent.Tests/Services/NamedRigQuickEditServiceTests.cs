using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

/// <summary>
/// The Camera &amp; rig "Edit active rig" command against the real named-rig and schedule stores, and the
/// supervised in-app restart that applies it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class NamedRigQuickEditServiceTests
{
    private const string NothingStaged = "Nothing was staged; the active rig and schedule are unchanged.";

    [TestMethod]
    public async Task ApplyActiveRigEdit_ChangedFlipAndMount_CopiesInstalledEquipmentAndStagesOneRevision()
    {
        using var harness = await Harness.CreateAsync().ConfigureAwait(false);
        var (active, version) = await harness.ActiveAsync().ConfigureAwait(false);

        var result = await harness.Service.ApplyActiveRigEditAsync(Harness.Request(active, version) with
        {
            HorizontalFlip = true,
            RollAdjustmentDegrees = 5
        }, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess, result.Message);
        var outcome = result.Value!;
        Assert.AreEqual(ActiveRigEditStatus.Staged, outcome.Status, outcome.Failure);
        var catalog = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var composed = catalog.Revisions.Single(revision => revision.RevisionId == outcome.ComposedRevisionId);
        var number = composed.RevisionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CollectionAssert.AreEqual(new[]
        {
            "Created optics \"Installed optics copy\" from \"Installed optics\", which is installed and read-only.",
            "Created mount \"Installed mount copy\" from \"Installed mount\", which is installed and read-only.",
            $"Composed rig revision r{number}.",
            $"Staged rig revision r{number} for restart."
        }, outcome.Recorded.ToArray());
        Assert.IsEmpty(outcome.NotDone);
        Assert.AreEqual("restart_required", outcome.Receipt!.Disposition);
        Assert.AreEqual(composed.RevisionId, outcome.Receipt.RevisionId);

        Assert.AreEqual(active.RevisionId, catalog.Selection.ActiveRevisionId);
        Assert.AreEqual(composed.RevisionId, catalog.Selection.PendingRevisionId);
        Assert.AreEqual(active.ProfileId, composed.ProfileId);
        Assert.AreEqual(active.CameraRevisionId, composed.CameraRevisionId);
        Assert.AreEqual(active.Rig.Optics with { HorizontalFlip = true }, composed.Rig.Optics);
        Assert.AreEqual(active.Rig.Orientation with { RollAdjustmentDegrees = 5 }, composed.Rig.Orientation);
        var schedule = await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(harness.InitialScheduleRevisionId, schedule.ActiveRevision.RevisionId);
        Assert.AreEqual(outcome.Receipt.ScheduleRevisionId, schedule.PendingRevision?.RevisionId);

        var equipment = (await harness.Named.GetInventoryAsync(CancellationToken.None).ConfigureAwait(false)).Equipment;
        Assert.AreEqual(1, equipment.Single(item => item.DisplayName == "Installed optics").RevisionNumber);
        Assert.AreEqual(1, equipment.Single(item => item.DisplayName == "Installed mount").RevisionNumber);
        Assert.IsFalse(equipment.Single(item => item.DisplayName == "Installed optics copy").IsInstalled);

        var again = await harness.Service.ApplyActiveRigEditAsync(Harness.Request(active, catalog.Selection.Version) with
        {
            HorizontalFlip = false
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ActiveRigEditStatus.Failed, again.Value!.Status);
        Assert.AreEqual("A rig change is already awaiting restart. Restart or cancel it before editing again.",
            again.Value.Failure);
        Assert.IsEmpty(again.Value.Recorded);
    }

    [TestMethod]
    public async Task ApplyActiveRigEdit_Unchanged_RecordsNothing()
    {
        using var harness = await Harness.CreateAsync().ConfigureAwait(false);
        var (active, version) = await harness.ActiveAsync().ConfigureAwait(false);
        var before = await harness.Named.GetInventoryAsync(CancellationToken.None).ConfigureAwait(false);
        var selection = (await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection;

        var result = await harness.Service.ApplyActiveRigEditAsync(Harness.Request(active, version), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess, result.Message);
        Assert.AreEqual(ActiveRigEditStatus.NoChanges, result.Value!.Status);
        Assert.IsEmpty(result.Value.Recorded);
        Assert.IsEmpty(result.Value.NotDone);
        Assert.IsNull(result.Value.ComposedRevisionId);
        var after = await harness.Named.GetInventoryAsync(CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(before.Equipment.ToArray(), after.Equipment.ToArray());
        var catalog = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(selection, catalog.Selection);
        Assert.HasCount(1, catalog.Revisions);
        Assert.IsNull((await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).PendingRevision);
    }

    [TestMethod]
    public async Task ApplyActiveRigEdit_PreviewRejected_StopsBeforeStagingAndNamesTheComposedRevision()
    {
        using var harness = await Harness.CreateAsync(validator: new RejectFieldOfView(123)).ConfigureAwait(false);
        var (active, version) = await harness.ActiveAsync().ConfigureAwait(false);

        var result = await harness.Service.ApplyActiveRigEditAsync(Harness.Request(active, version) with
        {
            FieldOfViewDegrees = 123
        }, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess, result.Message);
        var outcome = result.Value!;
        Assert.AreEqual(ActiveRigEditStatus.Failed, outcome.Status);
        StringAssert.StartsWith(outcome.Failure!, "Preview failed: ", StringComparison.Ordinal);
        Assert.IsNotNull(outcome.ComposedRevisionId);
        Assert.HasCount(2, outcome.Recorded);
        StringAssert.StartsWith(outcome.Recorded[0], "Created optics \"Installed optics copy\"", StringComparison.Ordinal);
        StringAssert.StartsWith(outcome.Recorded[1], "Composed rig revision r", StringComparison.Ordinal);
        CollectionAssert.AreEqual(new[] { NothingStaged }, outcome.NotDone.ToArray());
        var catalog = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(active.RevisionId, catalog.Selection.ActiveRevisionId);
        Assert.IsNull(catalog.Selection.PendingRevisionId);
        Assert.IsTrue(catalog.Revisions.Any(revision => revision.RevisionId == outcome.ComposedRevisionId));
        Assert.IsNull((await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).PendingRevision);
    }

    [TestMethod]
    public async Task ApplyActiveRigEdit_StageConflict_ReportsRecordedStepsAndLeavesNothingPending()
    {
        // The store checks a runtime that no longer holds the active schedule, as when the capture runtime
        // reloads between the page's gate check and the stage transaction.
        using var harness = await Harness.CreateAsync(detachStoreRuntime: true).ConfigureAwait(false);
        var (active, version) = await harness.ActiveAsync().ConfigureAwait(false);

        var result = await harness.Service.ApplyActiveRigEditAsync(Harness.Request(active, version) with
        {
            BoresightAzimuthDegrees = 30
        }, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess, result.Message);
        var outcome = result.Value!;
        Assert.AreEqual(ActiveRigEditStatus.Failed, outcome.Status);
        Assert.AreEqual("Rig state changed while the edit was applied.", outcome.Failure);
        Assert.HasCount(2, outcome.Recorded);
        StringAssert.StartsWith(outcome.Recorded[0], "Created mount \"Installed mount copy\"", StringComparison.Ordinal);
        StringAssert.StartsWith(outcome.Recorded[1], "Composed rig revision r", StringComparison.Ordinal);
        CollectionAssert.AreEqual(new[] { NothingStaged }, outcome.NotDone.ToArray());
        Assert.IsNull(outcome.Receipt);
        var catalog = await harness.Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(active.RevisionId, catalog.Selection.ActiveRevisionId);
        Assert.IsNull(catalog.Selection.PendingRevisionId);
        var schedule = await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(harness.InitialScheduleRevisionId, schedule.ActiveRevision.RevisionId);
        Assert.IsNull(schedule.PendingRevision);
    }

    [TestMethod]
    public async Task ApplyActiveRigEdit_PendingScheduleDraft_StopsBeforeRecordingAnything()
    {
        using var harness = await Harness.CreateAsync().ConfigureAwait(false);
        var (active, version) = await harness.ActiveAsync().ConfigureAwait(false);
        var schedule = await harness.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await harness.Schedule.StageAsync(schedule.ActiveRevision.Profile with
        {
            Schedule = Definition("draft", 2)
        }, "quick-edit-draft", schedule.Version, "owner", null, CancellationToken.None).ConfigureAwait(false);
        var before = await harness.Named.GetInventoryAsync(CancellationToken.None).ConfigureAwait(false);

        var result = await harness.Service.ApplyActiveRigEditAsync(Harness.Request(active, version) with
        {
            HorizontalFlip = true
        }, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ActiveRigEditStatus.Failed, result.Value!.Status);
        Assert.AreEqual("A capture schedule draft is pending. Apply or discard it on Schedule before editing the rig.",
            result.Value.Failure);
        Assert.IsEmpty(result.Value.Recorded);
        var after = await harness.Named.GetInventoryAsync(CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(before.Equipment.ToArray(), after.Equipment.ToArray());
    }

    [TestMethod]
    public async Task RequestRestart_ReadOnlyOperator_IsDeniedAndDoesNotStopTheHost()
    {
        using var lifetime = new CountingLifetime();
        var service = RestartService(lifetime, supervised: true, canMutate: false);

        var status = await service.GetRestartStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var result = await service.RequestRestartAsync(CancellationToken.None).ConfigureAwait(false);
        var edit = await service.ApplyActiveRigEditAsync(
            new ActiveRigEditRequest("rig", 1, true, 180, 0, 90, 0, 0), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new CameraAgentRestartStatus(true, false, false), status.Value);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, edit.Kind);
        Assert.AreEqual(0, lifetime.Stops);
    }

    [TestMethod]
    public async Task RequestRestart_Supervised_StopsTheHostOnceAfterResponding()
    {
        using var lifetime = new CountingLifetime();
        var service = RestartService(lifetime, supervised: true, canMutate: true);
        Assert.AreEqual(new CameraAgentRestartStatus(true, true, false),
            (await service.GetRestartStatusAsync(CancellationToken.None).ConfigureAwait(false)).Value);

        var first = await service.RequestRestartAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentRestartDisposition.Scheduled, first.Value);
        Assert.AreEqual(0, lifetime.Stops, "The host stops only after the request has been answered.");
        var second = await service.RequestRestartAsync(CancellationToken.None).ConfigureAwait(false);
        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var third = await service.RequestRestartAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentRestartDisposition.AlreadyRequested, second.Value);
        Assert.AreEqual(CameraAgentRestartDisposition.AlreadyRequested, third.Value);
        Assert.AreEqual(1, lifetime.Stops);
        Assert.IsTrue((await service.GetRestartStatusAsync(CancellationToken.None).ConfigureAwait(false)).Value!.Restarting);
    }

    [TestMethod]
    public async Task RequestRestart_Unsupervised_IsNotOfferedAndDoesNotStopTheHost()
    {
        using var lifetime = new CountingLifetime();
        var service = RestartService(lifetime, supervised: false, canMutate: true);

        var status = await service.GetRestartStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var result = await service.RequestRestartAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(status.Value!.Supervised);
        Assert.AreEqual(CameraAgentRestartDisposition.Unsupervised, result.Value);
        Assert.AreEqual(0, lifetime.Stops);
    }

    [TestMethod]
    public void IsSupervised_ExplicitSettingOverridesContainerDetection()
    {
        static bool Supervised(params (string Key, string Value)[] values)
            => CameraAgentNamedRigUiService.IsSupervised(new ConfigurationBuilder()
                .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
                .Build());
        const string Container = "DOTNET_RUNNING_IN_CONTAINER";
        const string Explicit = CameraAgentNamedRigUiService.SupervisedRestartKey;

        Assert.IsFalse(Supervised());
        Assert.IsTrue(Supervised((Container, "true")));
        Assert.IsTrue(Supervised((Container, "TRUE")));
        Assert.IsFalse(Supervised((Container, "false")));
        Assert.IsTrue(Supervised((Explicit, "true")));
        Assert.IsFalse(Supervised((Container, "true"), (Explicit, "false")));
        Assert.IsTrue(Supervised((Container, "false"), (Explicit, "true")));
        Assert.IsFalse(Supervised((Container, "true"), (Explicit, "maybe")));
    }

    /// <summary>The rig UI service for an owner with Operations change rights over real stores.</summary>
    internal static CameraAgentNamedRigUiService OwnerService(SqliteNamedRigProfileStore named,
        CaptureScheduleRuntimeCoordinator runtime)
    {
        var (authentication, authorization) = Authorization(canMutate: true);
        return new CameraAgentNamedRigUiService(authentication, authorization, named, runtime,
            Mock.Of<IHostApplicationLifetime>(), new ConfigurationBuilder().Build(), TimeProvider.System,
            NullLogger<CameraAgentNamedRigUiService>.Instance);
    }

    private static CameraAgentNamedRigUiService RestartService(IHostApplicationLifetime lifetime, bool supervised,
        bool canMutate)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>(CameraAgentNamedRigUiService.SupervisedRestartKey,
                    supervised ? "true" : "false")
            ])
            .Build();
        var (authentication, authorization) = Authorization(canMutate);
        return new CameraAgentNamedRigUiService(authentication, authorization, null!, null!, lifetime, configuration,
            TimeProvider.System, NullLogger<CameraAgentNamedRigUiService>.Instance)
        {
            RestartDelay = TimeSpan.FromMilliseconds(200)
        };
    }

    private static (AuthenticationStateProvider, IAuthorizationService) Authorization(bool canMutate)
    {
        var owner = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "owner-id"),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
            ],
            IdentityConstants.ApplicationScheme));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization
            .Setup(service => service.AuthorizeAsync(owner, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Success());
        authorization
            .Setup(service => service.AuthorizeAsync(owner, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1))
            .ReturnsAsync(canMutate ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return (new FixedAuthenticationStateProvider(owner), authorization.Object);
    }

    private static CameraModuleConfig Configuration()
        => new(
            new ObservatoryLocation(35, -114, 1000, "UTC"),
            new CameraModuleDescriptor("test"),
            new CameraRigConfig(
                new SensorProfile("test", 2, 2, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("Perspective", 50, 10, 0, ImageCircleRadiusPixels: 1),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 1, 10)
                {
                    Envelope = new ExposureEnvelope(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 1, 10,
                        new ExposureDefaults(TimeSpan.FromSeconds(1), 1), new ExposureDefaults(TimeSpan.FromSeconds(5), 10), 0.5)
                },
                new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                }),
            CapturePipelineConfig.Empty)
        {
            AgentId = "quick-edit-agent",
            Schedule = Definition("initial", 1),
            DeploymentLocation = DeploymentLocationSnapshot.Create("test-location", 1, "test", null,
                DateTimeOffset.UnixEpoch, null, 35, -114, 1000, "UTC")
        };

    private static CaptureScheduleDefinition Definition(string profileId, double gain)
        => new(
            "capture-schedule-v1",
            [new CaptureScheduleSetpointProfile(profileId, TimeSpan.FromSeconds(5), gain, TimeSpan.FromSeconds(10))],
            [new CaptureWeeklyScheduleWindow(
                "monday",
                DayOfWeek.Monday,
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(18, 0)),
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(6, 0), DayOffset: 1),
                profileId)]);

    /// <summary>Real schedule, runtime and named-rig stores over a temporary journal, with the UI service on top.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly List<IDisposable> _owned = [];
        private readonly string _root;

        private Harness(string root) => _root = root;

        public SqliteCaptureScheduleStore Schedule { get; private set; } = default!;
        public SqliteNamedRigProfileStore Named { get; private set; } = default!;
        public CameraAgentNamedRigUiService Service { get; private set; } = default!;
        public string InitialScheduleRevisionId { get; private set; } = default!;

        public static async Task<Harness> CreateAsync(ICameraModuleConfigurationValidator? validator = null,
            bool detachStoreRuntime = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "hvo-named-rig-quick-edit", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var harness = new Harness(root);
            try
            {
                var configuration = Configuration();
                var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
                var ingress = new JournalInitializer(root);
                var schedule = harness.Own(new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System));
                var active = await schedule.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                var telemetry = harness.Own(new CaptureControlTelemetry());
                var admission = harness.Own(new CaptureAdmissionCoordinator(ingress, options, TimeProvider.System, telemetry));
                CaptureScheduleRuntimeCoordinator Runtime() => harness.Own(new CaptureScheduleRuntimeCoordinator(schedule,
                    admission, new RawIngressState(TimeProvider.System), new CaptureLaneState(TimeProvider.System, options),
                    new EmptyPipelineFactory(), TimeProvider.System, moduleConfigurationValidator: validator));
                var runtime = Runtime();
                _ = await runtime.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                var named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule,
                    detachStoreRuntime ? Runtime() : runtime);
                _ = await named.ImportActiveAsync(active.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
                harness.Schedule = schedule;
                harness.Named = named;
                harness.InitialScheduleRevisionId = active.ActiveRevision.RevisionId;
                harness.Service = OwnerService(named, runtime);
                return harness;
            }
            catch
            {
                harness.Dispose();
                throw;
            }
        }

        public async Task<(NamedRigRevision Active, long Version)> ActiveAsync()
        {
            var catalog = await Named.GetAsync(CancellationToken.None).ConfigureAwait(false);
            return (catalog.Revisions.Single(revision => revision.RevisionId == catalog.Selection.ActiveRevisionId),
                catalog.Selection.Version);
        }

        /// <summary>An edit request that carries the active rig's current values unchanged.</summary>
        public static ActiveRigEditRequest Request(NamedRigRevision active, long version)
            => new(active.RevisionId, version, active.Rig.Optics.HorizontalFlip, active.Rig.Optics.FieldOfViewDegrees,
                active.Rig.Optics.FocalLengthMillimeters, active.Rig.Orientation.BoresightAltitudeDegrees,
                active.Rig.Orientation.BoresightAzimuthDegrees, active.Rig.Orientation.RollAdjustmentDegrees);

        private T Own<T>(T value) where T : IDisposable
        {
            _owned.Add(value);
            return value;
        }

        public void Dispose()
        {
            for (var index = _owned.Count - 1; index >= 0; index--) _owned[index].Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RejectFieldOfView(double rejected) : ICameraModuleConfigurationValidator
    {
        public void Validate(CameraModuleConfig configuration)
        {
            if (configuration.Rig.Optics.FieldOfViewDegrees == rejected)
                throw new InvalidOperationException("The camera module rejects this field of view.");
        }
    }

    private sealed class CountingLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private int _stops;

        public int Stops => Volatile.Read(ref _stops);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        // Counts without cancelling ApplicationStopping, so repeat requests exercise the one-shot latch itself.
        public void StopApplication()
        {
            Interlocked.Increment(ref _stops);
            Stopped.TrySetResult();
        }

        public void Dispose() => _stopping.Dispose();
    }

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class EmptyPipelineFactory : ICaptureProcessingPipelineFactory
    {
        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config) => new([]);

        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config) => throw new NotSupportedException();
    }

    private sealed class JournalInitializer(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
            => await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), busyTimeoutSeconds: 5)
                .InitializeAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask<RawCaptureReceipt?> AcceptAsync(CameraModuleConfig configuration,
            CaptureLoopSubmission submission, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
