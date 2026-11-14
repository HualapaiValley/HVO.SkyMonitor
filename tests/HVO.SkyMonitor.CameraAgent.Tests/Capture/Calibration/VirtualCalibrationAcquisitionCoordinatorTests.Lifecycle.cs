using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Focus;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

public sealed partial class VirtualCalibrationAcquisitionCoordinatorTests
{
    private static readonly Guid LifecycleWorker = Guid.NewGuid();
    private static readonly JsonSerializerOptions LifecycleJson = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow(CameraAgentControlPhase.Draining)]
    [DataRow(CameraAgentControlPhase.Restarting)]
    [DataRow(CameraAgentControlPhase.Verifying)]
    public async Task LifecycleFence_ManualPreviewAndScheduledCaptureRefuseNewWorkAndRecover(CameraAgentControlPhase phase)
    {
        var root = CreateRoot();
        try
        {
            using var controls = LifecycleControls(root);
            using var fixture = await Fixture.CreateAsync(root, lifecycleControls: controls).ConfigureAwait(false);
            using var ownership = new CameraModuleOwnership();
            var module = await PublishFocusAsync(ownership).ConfigureAwait(false);
            await using var moduleLifetime = module.ConfigureAwait(false);
            var source = new CameraModuleManualFocusPreviewSource(ownership, fixture.Admission, TimeProvider.System);
            var receipt = await ClaimLifecycleAsync(controls, phase, fixture.Admission.Snapshot.Version).ConfigureAwait(false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var scheduled = fixture.Admission.EnterAsync(deadline.Token).AsTask();
            Assert.IsFalse(scheduled.IsCompleted, "The existing scheduled-capture fence must still hold.");
            var refusal = await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(() =>
                source.AcquireAsync(new(TimeSpan.FromMilliseconds(50), 20), null, deadline.Token)).ConfigureAwait(false);
            StringAssert.Contains(refusal.Message, "installer lifecycle", StringComparison.Ordinal);
            Assert.AreEqual(0, module.Previews);
            await EndLifecycleAsync(controls, receipt).ConfigureAwait(false);
            using var admitted = await scheduled.WaitAsync(deadline.Token).ConfigureAwait(false);
            admitted.MarkNoPublicationRequired();
            admitted.Dispose();
            _ = await source.AcquireAsync(new(TimeSpan.FromMilliseconds(50), 20), null, deadline.Token).ConfigureAwait(false);
            Assert.AreEqual(1, module.Previews);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow(CameraAgentControlPhase.Draining)]
    [DataRow(CameraAgentControlPhase.Restarting)]
    [DataRow(CameraAgentControlPhase.Verifying)]
    public async Task LifecycleFence_FocusUiRefusesBeforeCreatingSession(CameraAgentControlPhase phase)
    {
        var root = CreateRoot();
        try
        {
            using var controls = LifecycleControls(root);
            using var fixture = await Fixture.CreateAsync(root, lifecycleControls: controls).ConfigureAwait(false);
            using var ownership = new CameraModuleOwnership();
            var module = await PublishFocusAsync(ownership).ConfigureAwait(false);
            await using var moduleLifetime = module.ConfigureAwait(false);
            var source = new CameraModuleManualFocusPreviewSource(ownership, fixture.Admission, TimeProvider.System);
            using var focusStore = new ManualFocusSessionStore(root);
            using var focus = new ManualFocusSessionCoordinator(source, TimeProvider.System);
            var service = new CameraAgentFocusUiService(new LifecycleAuthentication(), new LifecycleAuthorization(), focus,
                focusStore, NullLogger<CameraAgentFocusUiService>.Instance);
            try
            {
                var receipt = await ClaimLifecycleAsync(controls, phase, fixture.Admission.Snapshot.Version).ConfigureAwait(false);
                var refused = await service.StartAsync(new(new(TimeSpan.FromMilliseconds(50), 20)), CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(OperatorUiResultKind.Conflict, refused.Kind);
                Assert.AreEqual(ManualFocusSessionState.Idle, focus.Snapshot.State);
                Assert.AreEqual(0, module.Previews);
                await EndLifecycleAsync(controls, receipt).ConfigureAwait(false);
                var accepted = await service.StartAsync(new(new(TimeSpan.FromMilliseconds(50), 20)), CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(OperatorUiResultKind.Success, accepted.Kind, accepted.Message);
            }
            finally { await ((IHostedService)focus).StopAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow(CameraAgentControlPhase.Draining)]
    [DataRow(CameraAgentControlPhase.Restarting)]
    [DataRow(CameraAgentControlPhase.Verifying)]
    public async Task LifecycleFence_CalibrationAcquisitionRefusesBeforePlanningAndRecovers(CameraAgentControlPhase phase)
    {
        var root = CreateRoot();
        try
        {
            using var controls = LifecycleControls(root);
            using var fixture = await Fixture.CreateAsync(root, lifecycleControls: controls).ConfigureAwait(false);
            var receipt = await ClaimLifecycleAsync(controls, phase, fixture.Admission.Snapshot.Version).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(() =>
                fixture.Coordinator.AcquireAsync(Request("lifecycle-calibration"), CancellationToken.None)).ConfigureAwait(false);
            using (var db = await OpenAsync(root).ConfigureAwait(false))
                Assert.AreEqual(0L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM calibration_acquisition_jobs;").ConfigureAwait(false));
            await EndLifecycleAsync(controls, receipt).ConfigureAwait(false);
            var completed = await fixture.Coordinator.AcquireAsync(Request("lifecycle-calibration"), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CalibrationAcquisitionStates.Published, completed.State);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow(CameraAgentControlPhase.Draining, false)]
    [DataRow(CameraAgentControlPhase.Restarting, false)]
    [DataRow(CameraAgentControlPhase.Verifying, false)]
    [DataRow(CameraAgentControlPhase.Draining, true)]
    [DataRow(CameraAgentControlPhase.Restarting, true)]
    [DataRow(CameraAgentControlPhase.Verifying, true)]
    public async Task LifecycleFence_CalibrationUiAndApiReturnExistingConflict(CameraAgentControlPhase phase, bool api)
    {
        var root = CreateRoot();
        try
        {
            using var controls = LifecycleControls(root);
            using var fixture = await Fixture.CreateAsync(root, lifecycleControls: controls).ConfigureAwait(false);
            var receipt = await ClaimLifecycleAsync(controls, phase, fixture.Admission.Snapshot.Version).ConfigureAwait(false);
            var operations = new CalibrationLibraryOperationsCoordinator(fixture.Store, fixture.Admission);
            var request = Request("lifecycle-surface");
            if (api)
            {
                Assert.AreEqual(StatusCodes.Status409Conflict, await AcquireThroughApiAsync(fixture, operations, request).ConfigureAwait(false));
                await EndLifecycleAsync(controls, receipt).ConfigureAwait(false);
                Assert.AreEqual(StatusCodes.Status200OK, await AcquireThroughApiAsync(fixture, operations, request).ConfigureAwait(false));
            }
            else
            {
                var service = new CameraAgentCalibrationUiService(new LifecycleAuthentication(), new LifecycleAuthorization(),
                    fixture.Store, fixture.Coordinator, operations,
                    new CalibrationOperationsTokenService(new EphemeralDataProtectionProvider(), TimeSpan.FromMinutes(5)),
                    NullLogger<CameraAgentCalibrationUiService>.Instance);
                var command = new CalibrationUiAcquisitionRequest(request.Gain, request.Offset, request.TemperatureC,
                    request.BiasExposure, request.DarkExposure, request.FlatExposure, request.DefectExposure,
                    request.ApplicableLightExposure, request.EffectiveFromUtc, request.EffectiveUntilUtc, request.SourceModel, request.Reason);
                var refused = await service.AcquireAsync(command, 0, request.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(OperatorUiResultKind.Conflict, refused.Kind, refused.Message);
                await EndLifecycleAsync(controls, receipt).ConfigureAwait(false);
                var accepted = await service.AcquireAsync(command, 0, request.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(OperatorUiResultKind.Success, accepted.Kind, accepted.Message);
            }
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow(CameraAgentControlPhase.Draining, false)]
    [DataRow(CameraAgentControlPhase.Restarting, false)]
    [DataRow(CameraAgentControlPhase.Verifying, false)]
    [DataRow(CameraAgentControlPhase.Draining, true)]
    [DataRow(CameraAgentControlPhase.Restarting, true)]
    [DataRow(CameraAgentControlPhase.Verifying, true)]
    public async Task LifecycleFence_CalibrationActivationAndRollbackRefuseAtSharedBoundary(CameraAgentControlPhase phase, bool rollback)
    {
        var root = CreateRoot();
        try
        {
            using var controls = LifecycleControls(root);
            using var fixture = await Fixture.CreateAsync(root, lifecycleControls: controls).ConfigureAwait(false);
            var first = await fixture.Coordinator.AcquireAsync(Request("lifecycle-first"), CancellationToken.None).ConfigureAwait(false);
            var second = await fixture.Coordinator.AcquireAsync(Request("lifecycle-second") with
            { SourceModel = new() { Seed = 209 } }, CancellationToken.None).ConfigureAwait(false);
            var operations = new CalibrationLibraryOperationsCoordinator(fixture.Store, fixture.Admission);
            _ = await operations.ActivateAsync(first.BundleId!, "activate-first", 0, "operator", "test", CancellationToken.None).ConfigureAwait(false);
            var state = await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await operations.ActivateAsync(second.BundleId!, "activate-second", state.Version, "operator", "test", CancellationToken.None).ConfigureAwait(false);
            state = await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
            var receipt = await ClaimLifecycleAsync(controls, phase, fixture.Admission.Snapshot.Version).ConfigureAwait(false);
            Task Command() => rollback
                ? operations.RollbackAsync(first.BundleId!, "lifecycle-select", state.Version, "operator", "test", CancellationToken.None)
                : operations.ActivateAsync(first.BundleId!, "lifecycle-select", state.Version, "operator", "test", CancellationToken.None);
            await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(Command).ConfigureAwait(false);
            Assert.AreEqual(state.Version, (await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Version);
            await EndLifecycleAsync(controls, receipt).ConfigureAwait(false);
            await Command().ConfigureAwait(false);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task LifecycleFence_ClaimDuringBoundaryDrainRefusesQueuedManualWork()
    {
        var root = CreateRoot();
        try
        {
            using var controls = LifecycleControls(root);
            using var fixture = await Fixture.CreateAsync(root, lifecycleControls: controls).ConfigureAwait(false);
            using var exposure = await fixture.Admission.EnterAsync(CancellationToken.None).ConfigureAwait(false);
            var invoked = false;
            var boundary = fixture.Admission.ExecuteCaptureBoundaryAsync(_ =>
            { invoked = true; return Task.FromResult(true); }, CancellationToken.None);
            Assert.IsFalse(boundary.IsCompleted);
            var receipt = await ClaimLifecycleAsync(controls, CameraAgentControlPhase.Draining, fixture.Admission.Snapshot.Version).ConfigureAwait(false);
            exposure.MarkPublished();
            exposure.Dispose();
            await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(() => boundary).ConfigureAwait(false);
            Assert.IsFalse(invoked);
            await EndLifecycleAsync(controls, receipt).ConfigureAwait(false);
            Assert.IsTrue(await fixture.Admission.ExecuteCaptureBoundaryAsync(_ => Task.FromResult(true), CancellationToken.None).ConfigureAwait(false));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task LifecycleFence_InFlightPreviewCompletesWithoutCancellationAndPauseWaits()
    {
        var root = CreateRoot();
        try
        {
            using var controls = LifecycleControls(root);
            using var fixture = await Fixture.CreateAsync(root, lifecycleControls: controls).ConfigureAwait(false);
            using var ownership = new CameraModuleOwnership();
            var module = await PublishFocusAsync(ownership).ConfigureAwait(false);
            await using var moduleLifetime = module.ConfigureAwait(false);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            module.BeforePreview = (_, token) => release.Task.WaitAsync(token);
            var source = new CameraModuleManualFocusPreviewSource(ownership, fixture.Admission, TimeProvider.System);
            var preview = source.AcquireAsync(new(TimeSpan.FromMilliseconds(50), 20), null, CancellationToken.None);
            await module.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Task<CaptureControlCommandResult>? pause = null;
            try
            {
                var receipt = await ClaimLifecycleAsync(controls, CameraAgentControlPhase.Draining, fixture.Admission.Snapshot.Version).ConfigureAwait(false);
                pause = fixture.Admission.PauseAsync("lifecycle-owned-pause", fixture.Admission.Snapshot.Version,
                    "operator", "test", CancellationToken.None);
                Assert.IsFalse(pause.IsCompleted);
                Assert.IsFalse(preview.IsCompleted);
                release.SetResult();
                await preview.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await pause.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreEqual(0, module.CancelledPreviews);
                await EndLifecycleAsync(controls, receipt).ConfigureAwait(false);
                _ = await source.AcquireAsync(new(TimeSpan.FromMilliseconds(50), 20), null, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Admission.Snapshot.State);
            }
            finally
            {
                release.TrySetResult();
                if (pause is not null) await pause.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow(CameraAgentControlPhase.Draining)]
    [DataRow(CameraAgentControlPhase.Restarting)]
    public async Task LifecycleFence_RecoveryServiceDefersPendingJobWithoutConsumingAttempts(CameraAgentControlPhase phase)
    {
        var root = CreateRoot();
        try
        {
            using var controls = LifecycleControls(root);
            using (var interrupted = await Fixture.CreateAsync(root,
                new OneShotCancellationFaultInjector(CalibrationPublicationFaultPoint.BeforePayloadWrite)).ConfigureAwait(false))
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                    interrupted.Coordinator.AcquireAsync(Request("lifecycle-interrupted"), CancellationToken.None)).ConfigureAwait(false);
            using var fixture = await Fixture.CreateAsync(root, lifecycleControls: controls).ConfigureAwait(false);
            var pending = await fixture.Store.ReadPendingAcquisitionJobAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(pending);
            var receipt = await ClaimLifecycleAsync(controls, phase, fixture.Admission.Snapshot.Version).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(() => fixture.Coordinator.ResumePendingAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(pending.AttemptCount,
                (await fixture.Store.ReadPendingAcquisitionJobAsync(CancellationToken.None).ConfigureAwait(false))!.AttemptCount);
            using var recovery = new VirtualCalibrationAcquisitionRecoveryService(fixture.Coordinator);
            await recovery.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                Assert.IsFalse(recovery.ExecuteTask!.IsCompleted);
                await EndLifecycleAsync(controls, receipt).ConfigureAwait(false);
                await recovery.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Assert.IsNull(await fixture.Store.ReadPendingAcquisitionJobAsync(CancellationToken.None).ConfigureAwait(false));
            }
            finally { await recovery.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task LifecycleFence_InFlightCalibrationCaptureCompletesAndPauseWaits()
    {
        var root = CreateRoot();
        var blocker = new BlockingFaultInjector(CalibrationPublicationFaultPoint.BeforePayloadWrite);
        try
        {
            using var controls = LifecycleControls(root);
            using var fixture = await Fixture.CreateAsync(root, blocker, lifecycleControls: controls).ConfigureAwait(false);
            var acquisition = Task.Run(() => fixture.Coordinator.AcquireAsync(Request("lifecycle-in-flight"), CancellationToken.None));
            try
            {
                await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var receipt = await ClaimLifecycleAsync(controls, CameraAgentControlPhase.Draining, fixture.Admission.Snapshot.Version).ConfigureAwait(false);
                var pause = fixture.Admission.PauseAsync("pause-in-flight-calibration", fixture.Admission.Snapshot.Version,
                    "operator", "test", CancellationToken.None);
                Assert.IsFalse(pause.IsCompleted);
                Assert.IsFalse(acquisition.IsCompleted);
                blocker.Release.Set();
                Assert.AreEqual(CalibrationAcquisitionStates.Published, (await acquisition.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)).State);
                await pause.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Admission.Snapshot.State);
                await EndLifecycleAsync(controls, receipt).ConfigureAwait(false);
            }
            finally { blocker.Release.Set(); await acquisition.ConfigureAwait(false); }
        }
        finally { blocker.Release.Dispose(); DeleteRoot(root); }
    }

    [TestMethod]
    public async Task LifecycleFence_ArmedStopRemainsFencedUntilNewProcessLoadsTerminalState()
    {
        var root = CreateRoot();
        try
        {
            using (var controls = LifecycleControls(root))
            using (var fixture = await Fixture.CreateAsync(root, lifecycleControls: controls).ConfigureAwait(false))
            {
                await fixture.Admission.PauseAsync("pause-before-stop", fixture.Admission.Snapshot.Version,
                    "operator", "test", CancellationToken.None).ConfigureAwait(false);
                var version = fixture.Admission.Snapshot.Version;
                var receipt = await ClaimLifecycleAsync(controls, CameraAgentControlPhase.Restarting, version).ConfigureAwait(false);
                receipt = receipt with { RestartRequested = true, PausedVersion = version };
                await controls.ExchangeAsync(new(LifecycleWorker, receipt), CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(await controls.ArmRestartAsync(new(LifecycleWorker, receipt.Request.OperationId, version), version, CancellationToken.None).ConfigureAwait(false));
                await EndLifecycleAsync(controls, receipt).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(() =>
                    fixture.Admission.ExecuteCaptureBoundaryAsync(_ => Task.FromResult(true), CancellationToken.None)).ConfigureAwait(false);
                Assert.IsTrue(controls.StopPending);
            }
            using var restartedControls = LifecycleControls(root);
            using var restarted = await Fixture.CreateAsync(root, lifecycleControls: restartedControls).ConfigureAwait(false);
            Assert.IsFalse(restartedControls.AdmissionHeld);
            Assert.IsTrue(await restarted.Admission.ExecuteCaptureBoundaryAsync(_ => Task.FromResult(true), CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(CaptureAdmissionState.Paused, restarted.Admission.Snapshot.State);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow(CameraAgentControlPhase.Draining)]
    [DataRow(CameraAgentControlPhase.Restarting)]
    [DataRow(CameraAgentControlPhase.Verifying)]
    public async Task LifecycleFence_ConfigurationPublicationRemainsAvailableWithoutAdmittingCapture(CameraAgentControlPhase phase)
    {
        var root = CreateRoot();
        try
        {
            using var controls = LifecycleControls(root);
            using var fixture = await Fixture.CreateAsync(root, lifecycleControls: controls).ConfigureAwait(false);
            _ = await ClaimLifecycleAsync(controls, phase, fixture.Admission.Snapshot.Version).ConfigureAwait(false);
            Assert.IsTrue(await fixture.Admission.ExecuteConfigurationBoundaryAsync(_ => Task.FromResult(true), CancellationToken.None).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(() =>
                fixture.Admission.ExecuteCaptureBoundaryAsync(_ => Task.FromResult(true), CancellationToken.None)).ConfigureAwait(false);
        }
        finally { DeleteRoot(root); }
    }

    private static CameraAgentControlStore LifecycleControls(string root) => new(
        Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }), TimeProvider.System);

    private static async Task<CameraAgentControlReceipt> ClaimLifecycleAsync(CameraAgentControlStore controls,
        CameraAgentControlPhase phase, long version)
    {
        var worker = LifecycleWorker;
        await controls.ExchangeAsync(new(worker), CancellationToken.None).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var queued = await controls.SubmitAsync(new(Guid.NewGuid(), CameraAgentControlKind.Restart,
            "operator", "lifecycle test", version, now), CancellationToken.None).ConfigureAwait(false);
        var events = queued.Events.ToList();
        foreach (var next in new[] { CameraAgentControlPhase.Draining, CameraAgentControlPhase.Restarting, CameraAgentControlPhase.Verifying })
        {
            events.Add(new(next, now, "lifecycle.test"));
            if (next == phase) break;
        }
        var receipt = queued with { Phase = phase, Events = events };
        await controls.ExchangeAsync(new(worker, receipt), CancellationToken.None).ConfigureAwait(false);
        return receipt;
    }

    private static async Task EndLifecycleAsync(CameraAgentControlStore controls, CameraAgentControlReceipt receipt)
    {
        // The same registered worker publishes the terminal boundary without arming a real stop.
        await controls.ExchangeAsync(new(LifecycleWorker, receipt with
        { Phase = CameraAgentControlPhase.Failed, Events = [.. receipt.Events, new(CameraAgentControlPhase.Failed, DateTimeOffset.UtcNow, "lifecycle.test-ended")] }), CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<ScriptedPreviewModule> PublishFocusAsync(CameraModuleOwnership ownership)
    {
        var inner = FocusTestModules.Create();
        var config = FocusTestModules.Config();
        await inner.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var module = new ScriptedPreviewModule(inner);
        ownership.Publish(module, config);
        return module;
    }

    private static readonly ClaimsPrincipal LifecycleUser = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "operator"), new Claim(CanonicalCredentialClaims.AccountTypeClaim,
            CanonicalCredentialClaims.UserAccountType)], IdentityConstants.ApplicationScheme));
    private sealed class LifecycleAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(LifecycleUser));
    }
    private sealed class LifecycleAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(AuthorizationResult.Success());
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());
    }

    private static async Task<int> AcquireThroughApiAsync(Fixture fixture, CalibrationLibraryOperationsCoordinator operations,
        VirtualCalibrationAcquisitionRequestV1 request)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(fixture.Store);
        builder.Services.AddSingleton(fixture.Coordinator);
        builder.Services.AddSingleton(operations);
        builder.Services.AddSingleton(new CalibrationOperationsTokenService(new EphemeralDataProtectionProvider(), TimeSpan.FromMinutes(5)));
        var app = builder.Build();
        await using var appLifetime = app.ConfigureAwait(false);
        app.MapCameraAgentCalibrationOperationsEndpoints();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == "/api/v1/operations/calibration/acquisitions");
        var context = new DefaultHttpContext { RequestServices = app.Services, User = LifecycleUser };
        context.Request.Method = "POST";
        context.Request.Headers["Idempotency-Key"] = request.IdempotencyKey;
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ExpectedVersion = 0,
            request.Gain,
            request.Offset,
            request.TemperatureC,
            request.BiasExposure,
            request.DarkExposure,
            request.FlatExposure,
            request.DefectExposure,
            request.ApplicableLightExposure,
            request.EffectiveFromUtc,
            request.EffectiveUntilUtc,
            request.SourceModel,
            request.Reason
        }, LifecycleJson);
        using var input = new MemoryStream(body);
        using var output = new MemoryStream();
        context.Request.Body = input;
        context.Request.ContentLength = body.Length;
        context.Request.ContentType = "application/json";
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new LifecycleBody());
        context.Response.Body = output;
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        return context.Response.StatusCode;
    }
    private sealed class LifecycleBody : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
