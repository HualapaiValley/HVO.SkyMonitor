using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraModuleCadenceTests
{
    private static readonly DateTimeOffset StartUtc = new(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task RunAsync_ContinuousStartsAfterReadoutAndSynchronousHandoffWithoutWaitingForBackgroundWork()
    {
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var backgroundGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? backgroundWork = null;
        var module = new ScriptedCameraModule(timeProvider, (call, request) =>
        {
            if (call == 1)
            {
                timeProvider.Advance(TimeSpan.FromSeconds(3));
            }

            return CreateResult(request);
        });
        var context = new RecordingHostContext(
            CreateConfig(CaptureCadenceMode.Continuous, TimeSpan.FromSeconds(30)),
            timeProvider,
            cancellation,
            publishTarget: 2,
            onPublish: (publish, _) =>
            {
                if (publish == 1)
                {
                    timeProvider.Advance(TimeSpan.FromSeconds(2));
                    backgroundWork = WaitForGateAsync(backgroundGate.Task);
                }
            });
        var runner = CreateRunner(module, context, timeProvider);

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(0, timeProvider.TimerCreationCount);
        Assert.AreEqual(StartUtc, module.Calls[0].StartedUtc);
        Assert.AreEqual(StartUtc + TimeSpan.FromSeconds(5), module.Calls[1].StartedUtc);
        Assert.IsNotNull(backgroundWork);
        Assert.IsFalse(backgroundWork.IsCompleted);

        var firstEvidence = RequiredEvidence(context.Submissions[0]);
        Assert.AreEqual(CaptureCadenceMode.Continuous, firstEvidence.CadenceMode);
        Assert.AreEqual(CaptureStartReason.Initial, firstEvidence.StartReason);
        Assert.AreEqual(StartUtc, firstEvidence.ModuleCallStartedUtc);
        Assert.AreEqual(StartUtc + TimeSpan.FromSeconds(3), firstEvidence.IngressHandoffStartedUtc);
        AssertEvidenceOrder(firstEvidence);

        var second = context.Submissions[1];
        var secondEvidence = RequiredEvidence(second);
        Assert.AreEqual(StartUtc + TimeSpan.FromSeconds(5), second.Request.RequestedStartUtc);
        Assert.AreEqual(CaptureStartReason.ContinuousReady, secondEvidence.StartReason);
        Assert.AreEqual(StartUtc + TimeSpan.FromSeconds(5), secondEvidence.ModuleCallStartedUtc);
        Assert.AreEqual(TimeSpan.FromSeconds(5), secondEvidence.ObservedInterExposureGap);

        backgroundGate.SetResult();
        await backgroundWork.ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RunAsync_MinimumStartIntervalAnchorsRequestedDeadlineAndActualCallToPriorStart()
    {
        var interval = TimeSpan.FromSeconds(10);
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var module = new ScriptedCameraModule(timeProvider, (_, request) => CreateResult(request));
        var context = new RecordingHostContext(
            CreateConfig(CaptureCadenceMode.MinimumStartInterval, interval),
            timeProvider,
            cancellation,
            publishTarget: 2);
        var runner = CreateRunner(module, context, timeProvider);

        var run = runner.RunAsync(cancellation.Token);
        await timeProvider.WaitForTimerCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(1, module.Calls.Count);
        timeProvider.Advance(interval);
        await run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(1, timeProvider.TimerCreationCount);
        Assert.AreEqual(StartUtc, module.Calls[0].StartedUtc);
        Assert.AreEqual(StartUtc + interval, module.Calls[1].StartedUtc);
        Assert.AreEqual(StartUtc + interval, module.Calls[1].Request.RequestedStartUtc);

        var evidence = RequiredEvidence(context.Submissions[1]);
        Assert.AreEqual(CaptureStartReason.DeadlineReached, evidence.StartReason);
        Assert.AreEqual(StartUtc + interval, evidence.ModuleCallStartedUtc);
        Assert.AreEqual(interval, evidence.ObservedInterExposureGap);
        AssertEvidenceOrder(evidence);
    }

    [TestMethod]
    public async Task RunAsync_MinimumStartIntervalFailureRecoveryWaitsForBackoffAndRemainingStartInterval()
    {
        var interval = TimeSpan.FromSeconds(10);
        var backoff = TimeSpan.FromSeconds(2);
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var module = new ScriptedCameraModule(timeProvider, (call, request) =>
        {
            if (call == 1)
            {
                throw new InvalidOperationException("Deterministic fast capture failure.");
            }

            return CreateResult(request);
        });
        var context = new RecordingHostContext(
            CreateConfig(
                CaptureCadenceMode.MinimumStartInterval,
                interval,
                failureInitialDelay: backoff,
                failureMaximumDelay: backoff),
            timeProvider,
            cancellation,
            publishTarget: 1);
        var runner = CreateRunner(module, context, timeProvider);

        var run = runner.RunAsync(cancellation.Token);
        await timeProvider.WaitForTimerCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(1, module.Calls.Count);
        timeProvider.Advance(backoff);
        await timeProvider.WaitForTimerCountAsync(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(1, module.Calls.Count);
        timeProvider.Advance(interval - backoff - TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, module.Calls.Count);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(2, timeProvider.TimerCreationCount);
        Assert.AreEqual(StartUtc + interval, module.Calls[1].StartedUtc);
        Assert.AreEqual(StartUtc + interval, module.Calls[1].Request.RequestedStartUtc);
        var evidence = RequiredEvidence(context.Submissions[0]);
        Assert.AreEqual(CaptureStartReason.FailureRecovery, evidence.StartReason);
        Assert.AreEqual(StartUtc + interval, evidence.ModuleCallStartedUtc);
        AssertEvidenceOrder(evidence);
    }

    [TestMethod]
    public async Task RunAsync_OverrunSkipsTimerAndPreservesPriorActualStartDeadline()
    {
        var interval = TimeSpan.FromSeconds(10);
        var overrun = TimeSpan.FromSeconds(3);
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var module = new ScriptedCameraModule(timeProvider, (call, request) =>
        {
            if (call == 1)
            {
                timeProvider.Advance(interval + overrun);
            }

            return CreateResult(request);
        });
        var context = new RecordingHostContext(
            CreateConfig(CaptureCadenceMode.MinimumStartInterval, interval),
            timeProvider,
            cancellation,
            publishTarget: 2);
        var runner = CreateRunner(module, context, timeProvider);

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(0, timeProvider.TimerCreationCount);
        Assert.AreEqual(StartUtc + interval, module.Calls[1].Request.RequestedStartUtc);
        Assert.AreEqual(StartUtc + interval + overrun, module.Calls[1].StartedUtc);

        var evidence = RequiredEvidence(context.Submissions[1]);
        Assert.AreEqual(CaptureStartReason.DeadlineOverrun, evidence.StartReason);
        Assert.AreEqual(StartUtc + interval + overrun, evidence.ModuleCallStartedUtc);
        Assert.AreEqual(interval + overrun, evidence.ObservedInterExposureGap);
        AssertEvidenceOrder(evidence);
    }

    [TestMethod]
    [DataRow(CaptureCadenceMode.Continuous, 3, 2, 5, 0, CaptureStartReason.ContinuousReady)]
    [DataRow(CaptureCadenceMode.MinimumStartInterval, 13, 0, 13, 3, CaptureStartReason.DeadlineOverrun)]
    public async Task RunAsync_ActualHostStartJitterRemainsTruthfulForSimulatedExposureTimestamps(
        CaptureCadenceMode cadenceMode,
        int firstModuleSeconds,
        int firstIngressSeconds,
        int expectedSecondHostStartSeconds,
        int expectedJitterSeconds,
        CaptureStartReason expectedReason)
    {
        var interval = TimeSpan.FromSeconds(10);
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var module = new ScriptedCameraModule(timeProvider, (call, request) =>
        {
            var simulatedExposureStart = StartUtc - TimeSpan.FromMinutes(1) +
                TimeSpan.FromSeconds((call - 1) * 10);
            if (call == 1)
            {
                timeProvider.Advance(TimeSpan.FromSeconds(firstModuleSeconds));
            }

            return CreateResult(
                request,
                acquisitionTiming: new CaptureAcquisitionTiming(
                    simulatedExposureStart,
                    simulatedExposureStart,
                    simulatedExposureStart));
        });
        var context = new RecordingHostContext(
            CreateConfig(cadenceMode, interval),
            timeProvider,
            cancellation,
            publishTarget: 2,
            onPublish: (publish, _) =>
            {
                if (publish == 1)
                {
                    timeProvider.Advance(TimeSpan.FromSeconds(firstIngressSeconds));
                }
            });
        var runner = CreateRunner(module, context, timeProvider);

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var expectedHostStart = StartUtc + TimeSpan.FromSeconds(expectedSecondHostStartSeconds);
        var second = context.Submissions[1];
        var evidence = RequiredEvidence(second);
        Assert.AreEqual(expectedHostStart, module.Calls[1].StartedUtc);
        Assert.AreEqual(expectedHostStart, evidence.ModuleCallStartedUtc);
        Assert.AreEqual(TimeSpan.FromSeconds(expectedJitterSeconds),
            evidence.ModuleCallStartedUtc - second.Request.RequestedStartUtc);
        Assert.AreEqual(TimeSpan.FromSeconds(expectedJitterSeconds), evidence.MonotonicStartJitter);
        Assert.AreEqual(TimeSpan.FromSeconds(expectedSecondHostStartSeconds), evidence.ObservedInterExposureGap);
        Assert.AreEqual(expectedReason, evidence.StartReason);
        AssertEvidenceOrder(evidence);
    }

    [TestMethod]
    public async Task RunAsync_DisabledControlsDoNotMeterAndPreserveEffectiveActiveControls()
    {
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var effectiveExposure = TimeSpan.FromSeconds(7);
        const double effectiveGain = 70;
        var moduleNext = new CaptureSetpoint(TimeSpan.FromSeconds(1), 2, null, null);
        var module = new ScriptedCameraModule(timeProvider, (call, request) =>
        {
            var frame = call == 1
                ? CreatePaddedFrame(CameraPixelFormat.Mono16, effectiveExposure, effectiveGain, timeProvider.GetUtcNow())
                : null;
            return CreateResult(request, frame, moduleNext);
        });
        var context = new RecordingHostContext(
            CreateConfig(
                CaptureCadenceMode.Continuous,
                TimeSpan.FromSeconds(1),
                AutomaticControlOwnership.Disabled,
                AutomaticControlOwnership.Disabled),
            timeProvider,
            cancellation,
            publishTarget: 2);
        var runner = CreateRunner(module, context, timeProvider);

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var evidence = RequiredEvidence(context.Submissions[0]);
        Assert.IsNull(evidence.Metering);
        Assert.AreEqual(0L, evidence.Metering?.ConsideredSampleCount ?? 0L);
        Assert.AreEqual(0L, evidence.Metering?.ScannedBytes ?? 0L);
        Assert.AreEqual(AutomaticControlOwnership.Disabled, evidence.ExposureControl);
        Assert.AreEqual(AutomaticControlOwnership.Disabled, evidence.GainControl);
        Assert.AreEqual(CaptureControlDecisionReason.Disabled, evidence.Decision.Reason);
        Assert.AreEqual(effectiveExposure, evidence.Decision.ActiveExposure);
        Assert.AreEqual(effectiveGain, evidence.Decision.ActiveGain);
        Assert.AreEqual(effectiveExposure, evidence.Decision.DecidedExposure);
        Assert.AreEqual(effectiveGain, evidence.Decision.DecidedGain);
        Assert.AreEqual(effectiveExposure, module.Calls[1].Request.RequestedSetpoint!.Exposure);
        Assert.AreEqual(effectiveGain, module.Calls[1].Request.RequestedSetpoint!.Gain);
        AssertEvidenceOrder(evidence);
    }

    [TestMethod]
    public async Task RunAsync_SetpointFailurePublishesCompletedFrameBeforePropagating()
    {
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var frame = CreatePaddedFrame(
            CameraPixelFormat.Mono16,
            TimeSpan.FromSeconds(4),
            40,
            StartUtc);
        var module = new ScriptedCameraModule(
            timeProvider,
            (_, request) => CreateResult(request, frame),
            setpointException: new IOException("setpoint failed"));
        var context = new RecordingHostContext(
            CreateHostMeteredConfig(CameraPixelFormat.Mono16),
            timeProvider,
            cancellation,
            publishTarget: 2);
        var runner = CreateRunner(module, context, timeProvider, new ZenithSunEphemeris());

        var exception = await Assert.ThrowsExactlyAsync<IOException>(
            () => runner.RunAsync(cancellation.Token)).ConfigureAwait(false);

        Assert.AreEqual("setpoint failed", exception.Message);
        Assert.HasCount(1, context.Submissions);
        Assert.AreSame(frame, context.Submissions[0].Result.Frame);
        var evidence = RequiredEvidence(context.Submissions[0]);
        Assert.AreEqual(CaptureControlDecisionReason.SetpointApplicationFailed, evidence.Decision.Reason);
        Assert.IsNull(evidence.Decision.SetpointAppliedUtc);
        AssertEvidenceOrder(evidence);
    }

    [TestMethod]
    public async Task RunAsync_CameraNativeSetpointFailurePublishesCompletedFrameBeforePropagating()
    {
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var frame = CreatePaddedFrame(
            CameraPixelFormat.BayerRggb16,
            TimeSpan.FromSeconds(4),
            40,
            StartUtc);
        var moduleNext = new CaptureSetpoint(TimeSpan.FromSeconds(9), 90, null, 12);
        var module = new ScriptedCameraModule(
            timeProvider,
            (_, request) => CreateResult(request, frame, moduleNext),
            setpointException: new IOException("camera-native setpoint failed"));
        var context = new RecordingHostContext(
            CreateConfig(
                CaptureCadenceMode.Continuous,
                TimeSpan.FromSeconds(1),
                AutomaticControlOwnership.CameraNative,
                AutomaticControlOwnership.Disabled,
                CameraPixelFormat.BayerRggb16),
            timeProvider,
            cancellation,
            publishTarget: 2);
        var runner = CreateRunner(module, context, timeProvider, new RejectingPlanetEphemeris());

        var exception = await Assert.ThrowsExactlyAsync<IOException>(
            () => runner.RunAsync(cancellation.Token)).ConfigureAwait(false);

        Assert.AreEqual("camera-native setpoint failed", exception.Message);
        Assert.HasCount(1, context.Submissions);
        Assert.AreSame(frame, context.Submissions[0].Result.Frame);
        var evidence = RequiredEvidence(context.Submissions[0]);
        Assert.AreEqual(AutomaticControlOwnership.CameraNative, evidence.ExposureControl);
        Assert.AreEqual(AutomaticControlOwnership.Disabled, evidence.GainControl);
        Assert.AreNotEqual(evidence.Decision.ActiveExposure, evidence.Decision.DecidedExposure);
        Assert.AreEqual(moduleNext.Exposure, evidence.Decision.DecidedExposure);
        Assert.AreEqual(evidence.Decision.ActiveGain, evidence.Decision.DecidedGain);
        Assert.AreEqual(CaptureControlDecisionReason.SetpointApplicationFailed, evidence.Decision.Reason);
        Assert.IsNull(evidence.Decision.SetpointAppliedUtc);
        AssertEvidenceOrder(evidence);
    }

    [TestMethod]
    public async Task RunAsync_TargetFpsOnlySetpointFailurePublishesCompletedFrameAndEvidenceBeforePropagating()
    {
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var frame = CreatePaddedFrame(
            CameraPixelFormat.BayerRggb16,
            TimeSpan.FromSeconds(2),
            20,
            StartUtc);
        var moduleNext = new CaptureSetpoint(frame.Metadata.Exposure, frame.Metadata.Gain, null, 12.5);
        var module = new ScriptedCameraModule(
            timeProvider,
            (_, request) => CreateResult(request, frame, moduleNext),
            setpointException: new IOException("target FPS setpoint failed"));
        var context = new RecordingHostContext(
            CreateConfig(
                CaptureCadenceMode.Continuous,
                TimeSpan.FromSeconds(1),
                AutomaticControlOwnership.CameraNative,
                AutomaticControlOwnership.Disabled,
                CameraPixelFormat.BayerRggb16),
            timeProvider,
            cancellation,
            publishTarget: 2);
        var runner = CreateRunner(module, context, timeProvider, new RejectingPlanetEphemeris());

        var exception = await Assert.ThrowsExactlyAsync<IOException>(
            () => runner.RunAsync(cancellation.Token)).ConfigureAwait(false);

        Assert.AreEqual("target FPS setpoint failed", exception.Message);
        Assert.HasCount(1, context.Submissions);
        Assert.AreSame(frame, context.Submissions[0].Result.Frame);
        var evidence = RequiredEvidence(context.Submissions[0]);
        Assert.AreEqual(AutomaticControlOwnership.CameraNative, evidence.ExposureControl);
        Assert.AreEqual(AutomaticControlOwnership.Disabled, evidence.GainControl);
        Assert.AreEqual(evidence.Decision.ActiveExposure, evidence.Decision.DecidedExposure);
        Assert.AreEqual(evidence.Decision.ActiveGain, evidence.Decision.DecidedGain);
        Assert.IsNull(evidence.Decision.ActiveTargetFps);
        Assert.AreEqual(moduleNext.TargetFps, evidence.Decision.DecidedTargetFps);
        Assert.AreEqual(CaptureControlDecisionReason.SetpointApplicationFailed, evidence.Decision.Reason);
        Assert.IsNull(evidence.Decision.SetpointAppliedUtc);
        AssertEvidenceOrder(evidence);
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task RunAsync_HostMeteredPaddedLinear16UsesSparseEvidenceSolarRegimeAndEffectiveMetadata(
        CameraPixelFormat pixelFormat)
    {
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var frameExposure = TimeSpan.FromSeconds(4);
        const double frameGain = 40;
        CameraFrame? firstFrame = null;
        var module = new ScriptedCameraModule(timeProvider, (call, request) =>
        {
            if (call != 1)
            {
                return CreateResult(request);
            }

            var exposureStarted = timeProvider.GetUtcNow();
            timeProvider.Advance(TimeSpan.FromSeconds(3));
            firstFrame = CreatePaddedFrame(pixelFormat, frameExposure, frameGain, exposureStarted);
            var timing = new CaptureAcquisitionTiming(
                exposureStarted,
                exposureStarted + TimeSpan.FromSeconds(2),
                exposureStarted + TimeSpan.FromSeconds(3));
            return CreateResult(request, firstFrame, acquisitionTiming: timing);
        }, setpointApplicationDelay: TimeSpan.FromMilliseconds(400));
        var context = new RecordingHostContext(
            CreateHostMeteredConfig(pixelFormat),
            timeProvider,
            cancellation,
            publishTarget: 2,
            onPublish: (publish, _) =>
            {
                if (publish == 1)
                {
                    Assert.AreEqual(1, module.Applications.Count);
                }
            });
        var ephemeris = new ZenithSunEphemeris();
        var runner = CreateRunner(module, context, timeProvider, ephemeris);

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var evidence = RequiredEvidence(context.Submissions[0]);
        var metering = evidence.Metering;
        Assert.IsNotNull(metering);
        Assert.AreEqual(CaptureMeteringOutcome.Measured, metering.Outcome);
        Assert.IsTrue(metering.ConsideredSampleCount > 0);
        Assert.AreEqual(metering.ConsideredSampleCount, metering.AcceptedSampleCount);
        Assert.IsTrue(metering.ScannedBytes > 0);
        Assert.IsNotNull(firstFrame);
        Assert.IsTrue(metering.ScannedBytes < firstFrame.PixelData.Length);
        Assert.AreEqual(CaptureSolarRegime.Day, evidence.SolarRegime);
        Assert.AreEqual(3, ephemeris.SunRequestCount);
        Assert.AreEqual(frameExposure, evidence.Decision.ActiveExposure);
        Assert.AreEqual(frameGain, evidence.Decision.ActiveGain);
        Assert.AreEqual(TimeSpan.FromSeconds(8), evidence.Decision.DecidedExposure);
        Assert.AreEqual(frameGain, evidence.Decision.DecidedGain);
        Assert.AreEqual(CaptureControlDecisionReason.ExposureAdjusted, evidence.Decision.Reason);
        Assert.AreEqual(TimeSpan.FromSeconds(8), module.Calls[1].Request.RequestedSetpoint!.Exposure);
        Assert.AreEqual(frameGain, module.Calls[1].Request.RequestedSetpoint!.Gain);
        Assert.AreEqual(1, module.Applications.Count);
        var application = module.Applications[0];
        Assert.AreEqual(TimeSpan.FromSeconds(8), application.Setpoint.Exposure);
        Assert.AreEqual(frameGain, application.Setpoint.Gain);
        Assert.IsTrue(module.Calls[0].Order < application.Order);
        Assert.AreEqual(evidence.Decision.CompletedUtc, application.StartedUtc);
        Assert.AreEqual(application.AppliedUtc, evidence.Decision.SetpointAppliedUtc);
        Assert.IsTrue(evidence.Decision.CompletedUtc < evidence.Decision.SetpointAppliedUtc);
        Assert.AreEqual(StartUtc, evidence.ModuleCallStartedUtc);
        Assert.AreEqual(StartUtc + TimeSpan.FromSeconds(3), metering.StartedUtc);
        Assert.AreEqual(StartUtc + TimeSpan.FromSeconds(3.4), evidence.IngressHandoffStartedUtc);
        AssertEvidenceOrder(evidence);
    }

    [TestMethod]
    public async Task RunAsync_HostMeteredSolarRegimeUsesDeploymentLocationInsteadOfLegacyObservatory()
    {
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var module = new ScriptedCameraModule(
            timeProvider,
            (_, request) => CreateResult(request));
        var location = DeploymentLocationSnapshot.Create(
            "siding-spring-synthetic",
            1,
            "test",
            null,
            DateTimeOffset.UnixEpoch,
            null,
            -31.2733,
            149.0700,
            1165,
            "Australia/Sydney");
        var configuration = CreateHostMeteredConfig(CameraPixelFormat.Mono16) with
        {
            Observatory = new ObservatoryLocation(0, 0, 0, "UTC"),
            DeploymentLocation = location
        };
        var context = new RecordingHostContext(
            configuration,
            timeProvider,
            cancellation,
            publishTarget: 1);
        var runner = CreateRunner(module, context, timeProvider, new ZenithSunEphemeris());

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(CaptureSolarRegime.Night, RequiredEvidence(context.Submissions.Single()).SolarRegime);
    }

    [TestMethod]
    public async Task RunAsync_HostMetersBigEndianMono16UsingConfiguredSensorByteOrder()
    {
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var module = new ScriptedCameraModule(timeProvider, (call, request) =>
        {
            var frame = call == 1
                ? CreatePaddedFrame(
                    CameraPixelFormat.Mono16,
                    TimeSpan.FromSeconds(4),
                    40,
                    timeProvider.GetUtcNow(),
                    SampleByteOrder.BigEndian)
                : null;
            return CreateResult(request, frame);
        });
        var context = new RecordingHostContext(
            CreateHostMeteredConfig(CameraPixelFormat.Mono16, SampleByteOrder.BigEndian),
            timeProvider,
            cancellation,
            publishTarget: 2);
        var runner = CreateRunner(module, context, timeProvider, new ZenithSunEphemeris());

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var evidence = RequiredEvidence(context.Submissions[0]);
        var metering = evidence.Metering;
        Assert.IsNotNull(metering);
        Assert.AreEqual(CaptureMeteringOutcome.Measured, metering.Outcome);
        Assert.AreEqual(16L, metering.ConsideredSampleCount);
        Assert.AreEqual(16L, metering.AcceptedSampleCount);
        Assert.AreEqual(32L, metering.ScannedBytes);
        Assert.AreEqual(0.1, metering.NormalizedLevel!.Value, 1e-12);
        Assert.AreEqual(CaptureControlDecisionReason.ExposureAdjusted, evidence.Decision.Reason);
        Assert.AreEqual(TimeSpan.FromSeconds(8), evidence.Decision.DecidedExposure);
        AssertEvidenceOrder(evidence);
    }

    [TestMethod]
    public async Task RunAsync_HostMeteringRoundsFractionalAsi174BlackLevelInsteadOfFallingBack()
    {
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var module = new ScriptedCameraModule(timeProvider, (call, request) =>
        {
            var frame = call == 1
                ? CreatePaddedFrame(
                    CameraPixelFormat.Mono16,
                    TimeSpan.FromSeconds(4),
                    40,
                    timeProvider.GetUtcNow(),
                    blackLevelAdu: "64.5")
                : null;
            return CreateResult(request, frame);
        });
        var context = new RecordingHostContext(
            CreateHostMeteredConfig(CameraPixelFormat.Mono16),
            timeProvider,
            cancellation,
            publishTarget: 2);
        var runner = CreateRunner(module, context, timeProvider, new ZenithSunEphemeris());

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var metering = RequiredEvidence(context.Submissions[0]).Metering;
        Assert.IsNotNull(metering);
        Assert.AreEqual(CaptureMeteringOutcome.Measured, metering.Outcome);
        Assert.AreEqual((1000d - 65) / (10000 - 65), metering.NormalizedLevel!.Value, 1e-12);
        Assert.AreNotEqual(0.1, metering.NormalizedLevel.Value, 1e-12,
            "Fractional black-level metadata must not use the zero fallback.");
    }

    [TestMethod]
    public async Task RunAsync_ConfiguredExcludedRegionsSkipBrightSamplesBeforeScanning()
    {
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var module = new ScriptedCameraModule(timeProvider, (call, request) =>
        {
            var frame = call == 1
                ? CreatePaddedFrame(
                    CameraPixelFormat.Mono16,
                    TimeSpan.FromSeconds(4),
                    40,
                    timeProvider.GetUtcNow(),
                    sampleAt: static (x, _) => x < 4 ? (ushort)9000 : (ushort)1000)
                : null;
            return CreateResult(request, frame);
        });
        var context = new RecordingHostContext(
            CreateHostMeteredConfig(
                CameraPixelFormat.Mono16,
                excludedRegions: [new SensorCrop(0, 0, 4, 8)]),
            timeProvider,
            cancellation,
            publishTarget: 2);
        var runner = CreateRunner(module, context, timeProvider, new ZenithSunEphemeris());

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var metering = RequiredEvidence(context.Submissions[0]).Metering;
        Assert.IsNotNull(metering);
        Assert.AreEqual(CaptureMeteringOutcome.Measured, metering.Outcome);
        Assert.AreEqual(8L, metering.ConsideredSampleCount);
        Assert.AreEqual(8L, metering.AcceptedSampleCount);
        Assert.AreEqual(0L, metering.SaturatedSampleCount);
        Assert.AreEqual(16L, metering.ScannedBytes);
        Assert.IsTrue(metering.ScannedBytes < 32L, "The eight masked samples must not be read.");
        Assert.AreEqual(0.1, metering.NormalizedLevel!.Value, 1e-12,
            "Bright samples inside the configured exclusion must not affect the mean.");
    }

    [TestMethod]
    public async Task RunAsync_CameraNativeUsesModuleNextSetpointWithoutHostMetering()
    {
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var moduleNext = new CaptureSetpoint(TimeSpan.FromSeconds(9), 90, null, 12);
        var module = new ScriptedCameraModule(timeProvider, (call, request) =>
        {
            var frame = call == 1
                ? CreatePaddedFrame(
                    CameraPixelFormat.BayerRggb16,
                    TimeSpan.FromSeconds(4),
                    40,
                    timeProvider.GetUtcNow())
                 : null;
            return CreateResult(request, frame, moduleNext);
        }, setpointApplicationDelay: TimeSpan.FromMilliseconds(250));
        var context = new RecordingHostContext(
            CreateConfig(
                CaptureCadenceMode.Continuous,
                TimeSpan.FromSeconds(1),
                AutomaticControlOwnership.CameraNative,
                AutomaticControlOwnership.CameraNative,
                CameraPixelFormat.BayerRggb16),
            timeProvider,
            cancellation,
            publishTarget: 2,
            onPublish: (publish, _) =>
            {
                if (publish == 1)
                {
                    Assert.AreEqual(1, module.Applications.Count);
                }
            });
        var ephemeris = new RejectingPlanetEphemeris();
        var runner = CreateRunner(module, context, timeProvider, ephemeris);

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var evidence = RequiredEvidence(context.Submissions[0]);
        Assert.IsNull(evidence.Metering);
        Assert.IsNull(evidence.SolarRegime);
        Assert.AreEqual(CaptureControlDecisionReason.CameraNative, evidence.Decision.Reason);
        Assert.AreEqual(moduleNext.Exposure, evidence.Decision.DecidedExposure);
        Assert.AreEqual(moduleNext.Gain, evidence.Decision.DecidedGain);
        Assert.AreEqual(moduleNext.Exposure, module.Calls[1].Request.RequestedSetpoint!.Exposure);
        Assert.AreEqual(moduleNext.Gain, module.Calls[1].Request.RequestedSetpoint!.Gain);
        Assert.AreEqual(moduleNext.TargetFps, module.Calls[1].Request.RequestedSetpoint!.TargetFps);
        Assert.AreEqual(1, module.Applications.Count);
        var application = module.Applications[0];
        Assert.AreEqual(moduleNext, application.Setpoint);
        Assert.IsTrue(module.Calls[0].Order < application.Order);
        Assert.AreEqual(evidence.Decision.CompletedUtc, application.StartedUtc);
        Assert.AreEqual(application.AppliedUtc, evidence.Decision.SetpointAppliedUtc);
        Assert.IsTrue(evidence.Decision.CompletedUtc < evidence.Decision.SetpointAppliedUtc);
        Assert.AreEqual(0, ephemeris.RequestCount);
        AssertEvidenceOrder(evidence);
    }

    [TestMethod]
    public async Task RunAsync_DayTwilightNightSequenceUsesRegimeDefaultsAndReasonCodesTransitions()
    {
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var module = new ScriptedCameraModule(timeProvider, (_, request) => CreateResult(request));
        var context = new RecordingHostContext(
            CreateSolarTransitionConfig(),
            timeProvider,
            cancellation,
            publishTarget: 2);
        var ephemeris = new DeclinationSequenceEphemeris(30, 0, -30);
        var runner = CreateRunner(module, context, timeProvider, ephemeris);

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.AreEqual(3, ephemeris.RequestCount);
        Assert.AreEqual(2, module.Applications.Count);
        var twilight = RequiredEvidence(context.Submissions[0]);
        Assert.AreEqual(CaptureSolarRegime.Twilight, twilight.SolarRegime);
        Assert.AreEqual(CaptureControlDecisionReason.SolarRegimeChanged, twilight.Decision.Reason);
        Assert.AreEqual(TimeSpan.FromSeconds(6), twilight.Decision.DecidedExposure);
        Assert.AreEqual(50d, twilight.Decision.DecidedGain);
        Assert.AreEqual(twilight.Decision.DecidedExposure, module.Applications[0].Setpoint.Exposure);
        Assert.AreEqual(twilight.Decision.DecidedGain, module.Applications[0].Setpoint.Gain);

        var night = RequiredEvidence(context.Submissions[1]);
        Assert.AreEqual(CaptureSolarRegime.Night, night.SolarRegime);
        Assert.AreEqual(CaptureControlDecisionReason.SolarRegimeChanged, night.Decision.Reason);
        Assert.AreEqual(TimeSpan.FromSeconds(10), night.Decision.DecidedExposure);
        Assert.AreEqual(80d, night.Decision.DecidedGain);
        Assert.AreEqual(TimeSpan.FromSeconds(6), context.Submissions[1].Request.RequestedSetpoint!.Exposure);
        Assert.AreEqual(50d, context.Submissions[1].Request.RequestedSetpoint!.Gain);
        Assert.AreEqual(night.Decision.DecidedExposure, module.Applications[1].Setpoint.Exposure);
        Assert.AreEqual(night.Decision.DecidedGain, module.Applications[1].Setpoint.Gain);
        AssertEvidenceOrder(twilight);
        AssertEvidenceOrder(night);
    }

    private static CameraModuleRunner CreateRunner(
        ICameraModule module,
        ICaptureHostContext context,
        TimeProvider timeProvider,
        IPlanetEphemeris? ephemeris = null)
        => new(module, context, timeProvider, NullLogger.Instance, ephemeris);

    private static CaptureResult CreateResult(
        CaptureRequest request,
        CameraFrame? frame = null,
        CaptureSetpoint? nextSetpoint = null,
        CaptureAcquisitionTiming? acquisitionTiming = null)
        => new(
            frame,
            nextSetpoint ?? request.RequestedSetpoint!,
            TimeSpan.Zero,
            CaptureMode.Still,
            false)
        {
            AcquisitionTiming = acquisitionTiming
        };

    private static CameraModuleConfig CreateConfig(
        CaptureCadenceMode cadenceMode,
        TimeSpan interval,
        AutomaticControlOwnership exposureControl = AutomaticControlOwnership.Disabled,
        AutomaticControlOwnership gainControl = AutomaticControlOwnership.Disabled,
        CameraPixelFormat pixelFormat = CameraPixelFormat.Mono16,
        TimeSpan? failureInitialDelay = null,
        TimeSpan? failureMaximumDelay = null)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("CadenceTest"),
            new CameraRigConfig(
                new SensorProfile(
                    "CadenceTest",
                    8,
                    8,
                    1,
                    pixelFormat == CameraPixelFormat.BayerRggb16 ? SensorColorMode.Color : SensorColorMode.Mono,
                    pixelFormat,
                    StrideBytes: 20),
                new OpticsProfile("EquidistantFisheye", 1, 180, 0, ImageCircleRadiusPixels: 4),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    interval,
                    TimeSpan.FromSeconds(2),
                     TimeSpan.FromSeconds(3),
                     20,
                     30,
                     CaptureFailureInitialDelay: failureInitialDelay,
                     CaptureFailureMaximumDelay: failureMaximumDelay,
                     CadenceMode: cadenceMode),
                new CameraControlPolicy
                {
                    ExposureControl = exposureControl,
                    GainControl = gainControl,
                    Metering = new CaptureMeteringPolicy
                    {
                        XStride = 2,
                        YStride = 2,
                        UseImageCircle = false,
                        CfaSelection = CaptureMeteringCfaSelection.Green
                    }
                }));

    private static CameraModuleConfig CreateHostMeteredConfig(
        CameraPixelFormat pixelFormat,
        SampleByteOrder byteOrder = SampleByteOrder.LittleEndian,
        IReadOnlyList<SensorCrop>? excludedRegions = null)
    {
        var envelope = new ExposureEnvelope(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(16),
            1,
            100,
            new ExposureDefaults(TimeSpan.FromSeconds(2), 20),
            new ExposureDefaults(TimeSpan.FromSeconds(10), 80),
            0.5,
            Hysteresis: 0.01,
            AdjustmentFactor: 2,
            GainStep: 10);
        return new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("CadenceTest"),
            new CameraRigConfig(
                new SensorProfile(
                    "CadenceTest",
                    8,
                    8,
                    1,
                     pixelFormat == CameraPixelFormat.BayerRggb16 ? SensorColorMode.Color : SensorColorMode.Mono,
                     pixelFormat,
                     StrideBytes: 20,
                     ByteOrder: byteOrder),
                new OpticsProfile("EquidistantFisheye", 1, 180, 0, ImageCircleRadiusPixels: 4),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(10),
                    20,
                    80,
                    envelope,
                    CadenceMode: CaptureCadenceMode.Continuous),
                new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.HostMetered,
                    GainControl = AutomaticControlOwnership.Disabled,
                    Metering = new CaptureMeteringPolicy
                    {
                        XStride = 2,
                        YStride = 2,
                        UseImageCircle = false,
                        SaturationFraction = 0.98,
                        CfaSelection = CaptureMeteringCfaSelection.Green,
                        ExcludedRegions = excludedRegions
                    },
                    SolarRegimes = new CaptureSolarRegimePolicy
                    {
                        DayAltitudeThresholdDegrees = 0,
                        NightAltitudeThresholdDegrees = -12
                    }
                }));
    }

    private static CameraModuleConfig CreateSolarTransitionConfig()
    {
        var envelope = new ExposureEnvelope(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(16),
            1,
            100,
            new ExposureDefaults(TimeSpan.FromSeconds(2), 20),
            new ExposureDefaults(TimeSpan.FromSeconds(10), 80),
            0.5,
            TwilightDefaults: new ExposureDefaults(TimeSpan.FromSeconds(6), 50));
        return new CameraModuleConfig(
            new ObservatoryLocation(90, 0, 0, "UTC"),
            new CameraModuleDescriptor("CadenceTest"),
            new CameraRigConfig(
                new SensorProfile(
                    "CadenceTest",
                    8,
                    8,
                    1,
                    SensorColorMode.Mono,
                    CameraPixelFormat.Mono16,
                    StrideBytes: 20),
                new OpticsProfile("EquidistantFisheye", 1, 180, 0, ImageCircleRadiusPixels: 4),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(10),
                    20,
                    80,
                    envelope,
                    CadenceMode: CaptureCadenceMode.Continuous),
                new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.HostMetered,
                    GainControl = AutomaticControlOwnership.HostMetered,
                    Metering = new CaptureMeteringPolicy { UseImageCircle = false },
                    SolarRegimes = new CaptureSolarRegimePolicy
                    {
                        DayAltitudeThresholdDegrees = 20,
                        NightAltitudeThresholdDegrees = -20
                    }
                }));
    }

    private static CameraFrame CreatePaddedFrame(
        CameraPixelFormat pixelFormat,
        TimeSpan exposure,
        double gain,
        DateTimeOffset timestampUtc,
        SampleByteOrder byteOrder = SampleByteOrder.LittleEndian,
        Func<int, int, ushort>? sampleAt = null,
        string blackLevelAdu = "0")
    {
        const int width = 8;
        const int height = 8;
        const int stride = 20;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)0xFF);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sample = sampleAt?.Invoke(x, y) ?? 1000;
                var offset = y * stride + x * 2;
                pixels[offset] = byteOrder == SampleByteOrder.LittleEndian
                    ? (byte)(sample & byte.MaxValue)
                    : (byte)(sample >> 8);
                pixels[offset + 1] = byteOrder == SampleByteOrder.LittleEndian
                    ? (byte)(sample >> 8)
                    : (byte)(sample & byte.MaxValue);
            }
        }

        return new CameraFrame(
            timestampUtc,
            width,
            height,
            pixelFormat,
            pixels,
            new FrameMetadata(
                exposure,
                gain,
                0,
                Extra: new Dictionary<string, string>
                {
                    ["blackLevelAdu"] = blackLevelAdu,
                    ["whiteLevelAdu"] = "10000"
                }),
            stride);
    }

    private static CaptureCycleEvidence RequiredEvidence(CaptureLoopSubmission submission)
    {
        Assert.IsNotNull(submission.CycleEvidence);
        return submission.CycleEvidence;
    }

    private static async Task WaitForGateAsync(Task gate)
        => await gate.ConfigureAwait(false);

    private static void AssertEvidenceOrder(CaptureCycleEvidence evidence)
    {
        Assert.IsTrue(evidence.ModuleCallStartedUtc <= evidence.Decision.StartedUtc);
        Assert.IsTrue(evidence.Decision.StartedUtc <= evidence.Decision.CompletedUtc);
        Assert.IsTrue(evidence.Decision.CompletedUtc <= evidence.IngressHandoffStartedUtc);
        if (evidence.Metering is { } metering)
        {
            Assert.IsTrue(evidence.ModuleCallStartedUtc <= metering.StartedUtc);
            Assert.IsTrue(metering.StartedUtc <= metering.CompletedUtc);
            Assert.IsTrue(metering.CompletedUtc <= evidence.Decision.StartedUtc);
        }
        if (evidence.Decision.SetpointAppliedUtc is { } setpointAppliedUtc)
        {
            Assert.IsTrue(evidence.Decision.CompletedUtc <= setpointAppliedUtc);
            Assert.IsTrue(setpointAppliedUtc <= evidence.IngressHandoffStartedUtc);
        }
    }

    private sealed record ModuleCall(
        CaptureRequest Request,
        DateTimeOffset StartedUtc,
        long StartedTimestamp,
        int Order);

    private sealed record SetpointApplication(
        CaptureSetpoint Setpoint,
        DateTimeOffset StartedUtc,
        DateTimeOffset AppliedUtc,
        int Order);

    private sealed class ScriptedCameraModule(
        ManualTimeProvider timeProvider,
        Func<int, CaptureRequest, CaptureResult> script,
        TimeSpan? setpointApplicationDelay = null,
        Exception? setpointException = null) : ICameraModule, ICameraSetpointController
    {
        private readonly List<SetpointApplication> _applications = [];
        private readonly List<ModuleCall> _calls = [];
        private int _operationOrder;

        public List<ModuleCall> Calls => _calls;
        public List<SetpointApplication> Applications => _applications;
        public string Id => "cadence-test";
        public string DisplayName => "Cadence Test";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            _calls.Add(new ModuleCall(
                request,
                timeProvider.GetUtcNow(),
                timeProvider.GetTimestamp(),
                ++_operationOrder));
            return Task.FromResult(script(_calls.Count, request));
        }

        public ValueTask<DateTimeOffset> ApplySetpointAsync(
            CaptureSetpoint setpoint,
            CancellationToken cancellationToken)
        {
            var startedUtc = timeProvider.GetUtcNow();
            var order = ++_operationOrder;
            if (setpointException is not null)
            {
                return ValueTask.FromException<DateTimeOffset>(setpointException);
            }
            timeProvider.Advance(setpointApplicationDelay ?? TimeSpan.Zero);
            var appliedUtc = timeProvider.GetUtcNow();
            _applications.Add(new SetpointApplication(setpoint, startedUtc, appliedUtc, order));
            return ValueTask.FromResult(appliedUtc);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingHostContext(
        CameraModuleConfig configuration,
        ManualTimeProvider timeProvider,
        CancellationTokenSource cancellation,
        int publishTarget,
        Action<int, CaptureLoopSubmission>? onPublish = null) : ICaptureHostContext
    {
        private readonly List<CaptureLoopSubmission> _submissions = [];

        public CameraModuleConfig Configuration => configuration;
        public List<CaptureLoopSubmission> Submissions => _submissions;

        public ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
        {
            Assert.AreEqual(timeProvider.GetUtcNow(), submission.CycleEvidence!.IngressHandoffStartedUtc);
            _submissions.Add(submission);
            onPublish?.Invoke(_submissions.Count, submission);
            if (_submissions.Count == publishTarget)
            {
                return new ValueTask(cancellation.CancelAsync());
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ZenithSunEphemeris : IPlanetEphemeris
    {
        public string ModelVersion => "zenith-sun-test-v1";
        public int SunRequestCount { get; private set; }

        public SolarSystemPosition GetPosition(SolarSystemBody body, DateTimeOffset utc)
        {
            Assert.AreEqual(SolarSystemBody.Sun, body);
            SunRequestCount++;
            return new SolarSystemPosition(new EquatorialPoint(18.697374558, 0), -26.74);
        }
    }

    private sealed class DeclinationSequenceEphemeris(params double[] declinations) : IPlanetEphemeris
    {
        public string ModelVersion => "declination-sequence-test-v1";
        public int RequestCount { get; private set; }

        public SolarSystemPosition GetPosition(SolarSystemBody body, DateTimeOffset utc)
        {
            Assert.AreEqual(SolarSystemBody.Sun, body);
            Assert.IsTrue(RequestCount < declinations.Length, "The runner requested an unexpected ephemeris sample.");
            return new SolarSystemPosition(new EquatorialPoint(0, declinations[RequestCount++]), -26.74);
        }
    }

    private sealed class RejectingPlanetEphemeris : IPlanetEphemeris
    {
        public string ModelVersion => "rejecting-test-v1";
        public int RequestCount { get; private set; }

        public SolarSystemPosition GetPosition(SolarSystemBody body, DateTimeOffset utc)
        {
            RequestCount++;
            throw new AssertFailedException("Camera-native control must not request solar ephemeris data.");
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly List<(int Count, TaskCompletionSource Completion)> _timerWaiters = [];
        private long _timestamp;
        private long _utcTicks = startUtc.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public int TimerCreationCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return new DateTimeOffset(_utcTicks, TimeSpan.Zero);
            }
        }

        public override long GetTimestamp()
        {
            lock (_sync)
            {
                return _timestamp;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            ManualTimer timer;
            List<TaskCompletionSource> completed;
            lock (_sync)
            {
                TimerCreationCount++;
                timer = new ManualTimer(this, callback, state, Deadline(dueTime), PeriodTicks(period));
                _timers.Add(timer);
                completed = _timerWaiters
                    .Where(waiter => waiter.Count <= TimerCreationCount)
                    .Select(waiter => waiter.Completion)
                    .ToList();
                _timerWaiters.RemoveAll(waiter => waiter.Count <= TimerCreationCount);
            }

            foreach (var completion in completed)
            {
                completion.TrySetResult();
            }

            return timer;
        }

        public Task WaitForTimerCountAsync(int count)
        {
            lock (_sync)
            {
                if (TimerCreationCount >= count)
                {
                    return Task.CompletedTask;
                }

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _timerWaiters.Add((count, completion));
                return completion.Task;
            }
        }

        public void Advance(TimeSpan amount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);
            List<ManualTimer> due;
            lock (_sync)
            {
                _utcTicks = checked(_utcTicks + amount.Ticks);
                _timestamp = checked(_timestamp + amount.Ticks);
                due = _timers.Where(timer => timer.PrepareToFire(_timestamp)).ToList();
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private long Deadline(TimeSpan dueTime)
            => dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(_timestamp + dueTime.Ticks);

        private static long PeriodTicks(TimeSpan period)
            => period == Timeout.InfiniteTimeSpan ? long.MaxValue : period.Ticks;

        private void Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                timer.ChangeCore(Deadline(dueTime), PeriodTicks(period));
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_sync)
            {
                timer.DisposeCore();
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            long deadline,
            long periodTicks) : ITimer
        {
            private bool _disposed;
            private long _deadline = deadline;
            private long _periodTicks = periodTicks;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.Change(this, dueTime, period);
                return !_disposed;
            }

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool PrepareToFire(long timestamp)
            {
                if (_disposed || timestamp < _deadline)
                {
                    return false;
                }

                _deadline = _periodTicks == long.MaxValue
                    ? long.MaxValue
                    : checked(timestamp + _periodTicks);
                return true;
            }

            public void Fire() => callback(state);

            public void ChangeCore(long nextDeadline, long nextPeriodTicks)
            {
                if (_disposed)
                {
                    return;
                }

                _deadline = nextDeadline;
                _periodTicks = nextPeriodTicks;
            }

            public void DisposeCore() => _disposed = true;
        }
    }
}
