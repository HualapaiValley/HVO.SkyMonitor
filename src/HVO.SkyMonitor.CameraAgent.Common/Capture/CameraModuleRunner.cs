using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Exposure;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

internal sealed class CameraModuleRunner
{
    private static readonly TimeSpan DefaultInitialFailureDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultMaximumFailureDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumTimerDelay = TimeSpan.FromMilliseconds(1);
    private readonly ICameraModule _module;
    private readonly ICaptureHostContext _hostContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly CaptureAdmissionCoordinator? _captureAdmissionCoordinator;
    private readonly IPlanetEphemeris? _planetEphemeris;
    private readonly CaptureControlTelemetry? _telemetry;
    private readonly FleetRuntimeState? _fleetRuntimeState;
    private readonly CaptureScheduleRuntimeCoordinator? _scheduleRuntimeCoordinator;
    private readonly EnvironmentalCaptureTriggerBridge? _environmentalTriggers;

    public CameraModuleRunner(
        ICameraModule module,
        ICaptureHostContext hostContext,
        TimeProvider timeProvider,
        ILogger logger,
        IPlanetEphemeris? planetEphemeris = null,
        CaptureControlTelemetry? telemetry = null,
        FleetRuntimeState? fleetRuntimeState = null,
        CaptureScheduleRuntimeCoordinator? scheduleRuntimeCoordinator = null,
        EnvironmentalCaptureTriggerBridge? environmentalTriggers = null)
        : this(
            module, hostContext, timeProvider, logger, null, planetEphemeris, telemetry,
            fleetRuntimeState, scheduleRuntimeCoordinator, environmentalTriggers)
    {
    }

    public CameraModuleRunner(
        ICameraModule module,
        ICaptureHostContext hostContext,
        TimeProvider timeProvider,
        ILogger logger,
        CaptureAdmissionCoordinator? captureAdmissionCoordinator,
        IPlanetEphemeris? planetEphemeris = null,
        CaptureControlTelemetry? telemetry = null,
        FleetRuntimeState? fleetRuntimeState = null,
        CaptureScheduleRuntimeCoordinator? scheduleRuntimeCoordinator = null,
        EnvironmentalCaptureTriggerBridge? environmentalTriggers = null)
    {
        _module = module;
        _hostContext = hostContext;
        _timeProvider = timeProvider;
        _logger = logger;
        _captureAdmissionCoordinator = captureAdmissionCoordinator;
        _planetEphemeris = planetEphemeris;
        _telemetry = telemetry;
        _fleetRuntimeState = fleetRuntimeState;
        _scheduleRuntimeCoordinator = scheduleRuntimeCoordinator;
        _environmentalTriggers = environmentalTriggers;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Capture loop must continue after transient module failures.")]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var config = _hostContext.Configuration;
        var cadenceMode = config.Rig.Pipeline.CadenceMode;
        var targetInterval = config.Rig.Pipeline.CaptureInterval;
        var initialFailureDelay = config.Rig.Pipeline.CaptureFailureInitialDelay ?? DefaultInitialFailureDelay;
        var maximumFailureDelay = config.Rig.Pipeline.CaptureFailureMaximumDelay ?? DefaultMaximumFailureDelay;
        var controlPolicy = config.Rig.ControlPolicy ?? throw new InvalidOperationException(
            "Camera control ownership must be configured explicitly.");
        var exposureControl = controlPolicy.ExposureControl;
        var gainControl = controlPolicy.GainControl;
        var hostMetered = exposureControl == AutomaticControlOwnership.HostMetered ||
            gainControl == AutomaticControlOwnership.HostMetered;
        var automaticControlEnabled = exposureControl != AutomaticControlOwnership.Disabled ||
            gainControl != AutomaticControlOwnership.Disabled;
        var setpointController = automaticControlEnabled
            ? _module as ICameraSetpointController ?? throw new InvalidOperationException(
                "Automatic camera control requires a module that can apply setpoints before ingress.")
            : null;
        var initialRegime = hostMetered ? ResolveSolarRegime(config, UtcNow()) : CaptureSolarRegime.Night;
        CaptureSolarRegime? previousRegime = hostMetered ? initialRegime : null;
        var excludedRegions = hostMetered
            ? CreateExcludedRegions(controlPolicy.Metering?.ExcludedRegions)
            : Array.Empty<MeteringRegion>();
        var nextSetpoint = ExposureController.Initial(config.Rig.Pipeline, initialRegime);
        var captureMode = CaptureMode.Still;
        var consecutiveFailures = 0;
        long? previousStartTimestamp = null;
        DateTimeOffset? previousStartUtc = null;
        var recovering = false;
        TimeSpan? moduleDelayOverride = null;
        string? activeProfileKey = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            CaptureScheduleGrant? scheduleGrant = null;
            if (_scheduleRuntimeCoordinator is not null)
            {
                try
                {
                    scheduleGrant = await _scheduleRuntimeCoordinator.WaitForGrantAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                var profileChanged = activeProfileKey is not null && !string.Equals(
                    activeProfileKey, scheduleGrant.ProfileKey, StringComparison.Ordinal);
                if (scheduleGrant.AdmissionWasInterrupted || profileChanged)
                {
                    previousStartTimestamp = null;
                    previousStartUtc = null;
                    recovering = false;
                    moduleDelayOverride = null;
                }
                cadenceMode = scheduleGrant.Profile.CadenceMode;
                targetInterval = cadenceMode == CaptureCadenceMode.MinimumStartInterval
                    ? Max(scheduleGrant.Profile.CaptureInterval, moduleDelayOverride ?? TimeSpan.Zero)
                    : scheduleGrant.Profile.CaptureInterval;
                if (!string.Equals(activeProfileKey, scheduleGrant.ProfileKey, StringComparison.Ordinal))
                {
                    if (scheduleGrant.Decision.Reason != CaptureScheduleAdmissionReason.LegacyCompatibility)
                    {
                        nextSetpoint = ExposureController.Initial(scheduleGrant.Profile);
                    }
                    activeProfileKey = scheduleGrant.ProfileKey;
                }
            }
            CaptureSchedule? schedule;
            try
            {
                schedule = await WaitForScheduleAsync(
                    cadenceMode,
                    targetInterval,
                    previousStartTimestamp,
                    previousStartUtc,
                    recovering,
                    scheduleGrant?.Decision.NextTransitionUtc,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            if (schedule is null)
            {
                previousStartTimestamp = null;
                previousStartUtc = null;
                recovering = false;
                continue;
            }
            var effectiveSchedule = schedule.Value;

            CaptureAdmissionCoordinator.CaptureAdmissionLease admission = default;
            try
            {
                if (_captureAdmissionCoordinator is not null)
                {
                    admission = await _captureAdmissionCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
                }
                if (_scheduleRuntimeCoordinator is not null && scheduleGrant is not null)
                {
                    var confirmed = await _scheduleRuntimeCoordinator.ConfirmGrantAsync(
                        scheduleGrant,
                        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                        cancellationToken).ConfigureAwait(false);
                    if (confirmed is null)
                    {
                        admission.MarkNoPublicationRequired();
                        admission.Dispose();
                        previousStartTimestamp = null;
                        previousStartUtc = null;
                        recovering = false;
                        continue;
                    }
                    scheduleGrant = confirmed;
                    if (confirmed.AdmissionWasInterrupted)
                    {
                        previousStartTimestamp = null;
                        previousStartUtc = null;
                        recovering = false;
                        moduleDelayOverride = null;
                        targetInterval = confirmed.Profile.CaptureInterval;
                        effectiveSchedule = new CaptureSchedule(UtcNow(), CaptureStartReason.Initial);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                admission.MarkNoPublicationRequired();
                admission.Dispose();
                break;
            }
            catch
            {
                admission.MarkNoPublicationRequired();
                admission.Dispose();
                throw;
            }

            if (_scheduleRuntimeCoordinator is not null)
            {
                moduleDelayOverride = null;
            }
            var priorStartTimestamp = previousStartTimestamp;
            var moduleCallStartedUtc = UtcNow();
            var moduleCallStartedTimestamp = _timeProvider.GetTimestamp();
            var monotonicStartJitter = cadenceMode == CaptureCadenceMode.MinimumStartInterval &&
                priorStartTimestamp.HasValue
                    ? Max(
                        TimeSpan.Zero,
                        _timeProvider.GetElapsedTime(priorStartTimestamp.Value, moduleCallStartedTimestamp) -
                        targetInterval)
                    : TimeSpan.Zero;
            previousStartTimestamp = moduleCallStartedTimestamp;
            previousStartUtc = moduleCallStartedUtc;
            var request = new CaptureRequest(effectiveSchedule.RequestedStartUtc, targetInterval, captureMode, nextSetpoint);
            CaptureResult? result;
            using var cycleActivity = CaptureControlTelemetry.ActivitySource.StartActivity("capture-cycle");
            cycleActivity?.SetTag("cadence.mode", cadenceMode.ToString());
            cycleActivity?.SetTag("start.reason", effectiveSchedule.StartReason.ToString());
            try
            {
                if (_environmentalTriggers is not null)
                {
                    await _environmentalTriggers.BeforeCaptureAsync(moduleCallStartedUtc, cancellationToken)
                        .ConfigureAwait(false);
                }
                result = await _module.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                admission.MarkNoPublicationRequired();
                admission.Dispose();
                break;
            }
            catch (Exception ex)
            {
                admission.MarkNoPublicationRequired();
                admission.Dispose();
                _fleetRuntimeState?.CaptureFailed(ex.GetType().Name);
                cycleActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "module-failure");
                consecutiveFailures++;
                recovering = true;
                if (ShouldLogFailure(consecutiveFailures, initialFailureDelay, maximumFailureDelay))
                {
                    _logger.CaptureLoopFailed(ex);
                }
                if (!await DelayAfterFailureAsync(
                        consecutiveFailures, initialFailureDelay, maximumFailureDelay, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
                continue;
            }

            if (result is null)
            {
                admission.MarkNoPublicationRequired();
                admission.Dispose();
                _fleetRuntimeState?.CaptureFailed("null-result");
                cycleActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "null-result");
                consecutiveFailures++;
                recovering = true;
                if (ShouldLogFailure(consecutiveFailures, initialFailureDelay, maximumFailureDelay))
                {
                    _logger.CaptureReturnedNull(consecutiveFailures);
                }
                if (!await DelayAfterFailureAsync(
                        consecutiveFailures, initialFailureDelay, maximumFailureDelay, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
                continue;
            }
            using var acceptedAdmission = admission;
            if (consecutiveFailures > 0)
            {
                _logger.CaptureRecovered(consecutiveFailures);
                consecutiveFailures = 0;
            }
            recovering = false;
            var moduleDuration = _timeProvider.GetElapsedTime(
                moduleCallStartedTimestamp,
                _timeProvider.GetTimestamp());

            var readoutCompletedUtc = result.AcquisitionTiming?.ReadoutCompletedUtc.ToUniversalTime() ?? UtcNow();
            var active = ResolveActiveSetpoint(request.RequestedSetpoint!, result);
            var regime = hostMetered ? ResolveSolarRegime(config, readoutCompletedUtc) : (CaptureSolarRegime?)null;
            var regimeChanged = (scheduleGrant is null ||
                    scheduleGrant.Decision.Reason == CaptureScheduleAdmissionReason.LegacyCompatibility) &&
                regime.HasValue && previousRegime.HasValue && regime != previousRegime;
            var meteringStartedTimestamp = _timeProvider.GetTimestamp();
            var metering = hostMetered ? Measure(config, result.Frame, readoutCompletedUtc, excludedRegions) : null;
            var meteringDuration = hostMetered
                ? _timeProvider.GetElapsedTime(meteringStartedTimestamp, _timeProvider.GetTimestamp())
                : TimeSpan.Zero;
            if (metering is not null)
            {
                _telemetry?.RecordMetering(metering, meteringDuration);
            }
            var decisionStartedUtc = Max(UtcNow(), metering?.CompletedUtc ?? readoutCompletedUtc);
            var controlStartedTimestamp = _timeProvider.GetTimestamp();
            var controlDecisionDuration = TimeSpan.Zero;
            var setpointDuration = TimeSpan.Zero;
            ExposureDecision automatic;
            DateTimeOffset decisionCompletedUtc;
            DateTimeOffset? setpointAppliedUtc = null;
            Exception? setpointFailure = null;
            using (var controlActivity = CaptureControlTelemetry.ActivitySource.StartActivity("capture-control"))
            {
                automatic = Decide(
                    config.Rig.Pipeline,
                    active,
                    result.NextSetpoint,
                    exposureControl,
                    gainControl,
                    regime,
                    metering,
                    regimeChanged);
                nextSetpoint = automatic.Setpoint with
                {
                    NextIntervalOverride = result.NextSetpoint.NextIntervalOverride,
                    TargetFps = result.NextSetpoint.TargetFps
                };
                decisionCompletedUtc = Max(UtcNow(), decisionStartedUtc);
                controlDecisionDuration = _timeProvider.GetElapsedTime(
                    controlStartedTimestamp,
                    _timeProvider.GetTimestamp());
                if (setpointController is not null && RequiresSetpointApplication(active, nextSetpoint))
                {
                    var setpointStartedTimestamp = _timeProvider.GetTimestamp();
                    try
                    {
                        var applied = (await setpointController.ApplySetpointAsync(
                            nextSetpoint,
                            cancellationToken).ConfigureAwait(false)).ToUniversalTime();
                        if (applied < decisionCompletedUtc)
                        {
                            throw new InvalidOperationException(
                                "The camera module reported setpoint application before the host decision completed.");
                        }
                        setpointAppliedUtc = applied;
                    }
                    catch (Exception exception)
                    {
                        setpointFailure = exception;
                        automatic = automatic with
                        {
                            Reason = CaptureControlDecisionReason.SetpointApplicationFailed
                        };
                        controlActivity?.SetStatus(
                            System.Diagnostics.ActivityStatusCode.Error,
                            exception.GetType().Name);
                    }
                    setpointDuration = _timeProvider.GetElapsedTime(
                        setpointStartedTimestamp,
                        _timeProvider.GetTimestamp());
                }
                controlActivity?.SetTag("decision.reason", automatic.Reason.ToString());
                controlActivity?.SetTag("solar.regime", regime?.ToString() ?? "none");
                if (setpointFailure is null)
                {
                    controlActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                }
            }
            if (_scheduleRuntimeCoordinator is null)
            {
                targetInterval = result.NextSetpoint.NextIntervalOverride ?? targetInterval;
            }
            else
            {
                moduleDelayOverride = result.NextSetpoint.NextIntervalOverride;
            }
            captureMode = result.Mode;
            previousRegime = regime;

            var observedGap = priorStartTimestamp.HasValue
                ? _timeProvider.GetElapsedTime(priorStartTimestamp.Value, moduleCallStartedTimestamp)
                : (TimeSpan?)null;
            var ingressHandoffStartedUtc = Max(
                UtcNow(),
                setpointAppliedUtc ?? decisionCompletedUtc);
            var evidence = new CaptureCycleEvidence(
                cadenceMode,
                effectiveSchedule.StartReason,
                exposureControl,
                gainControl,
                regime,
                moduleCallStartedUtc,
                observedGap,
                metering,
                new CaptureControlDecisionEvidence(
                    decisionStartedUtc,
                    decisionCompletedUtc,
                    active.Exposure,
                    active.Gain,
                    nextSetpoint.Exposure,
                    nextSetpoint.Gain,
                    automatic.Reason,
                    setpointAppliedUtc,
                    active.TargetFps,
                    nextSetpoint.TargetFps),
                ingressHandoffStartedUtc)
            {
                MonotonicStartJitter = monotonicStartJitter,
                ScheduleAdmission = scheduleGrant?.Evidence
            };

            var elapsedBeforeIngress = _timeProvider.GetElapsedTime(
                moduleCallStartedTimestamp,
                _timeProvider.GetTimestamp());
            var submission = new CaptureLoopSubmission(
                request,
                result,
                moduleCallStartedUtc,
                targetInterval,
                elapsedBeforeIngress)
            {
                CycleEvidence = evidence
            };

            var ingressStartedTimestamp = _timeProvider.GetTimestamp();
            using (var ingressActivity = CaptureControlTelemetry.ActivitySource.StartActivity("capture-ingress-handoff"))
            {
                try
                {
                    await _hostContext.PublishAsync(
                        submission,
                        setpointFailure is null ? cancellationToken : CancellationToken.None).ConfigureAwait(false);
                    acceptedAdmission.MarkPublished();
                    ingressActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                }
                catch (Exception exception)
                {
                    ingressActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, exception.GetType().Name);
                    throw;
                }
            }
            var ingressDuration = _timeProvider.GetElapsedTime(ingressStartedTimestamp, _timeProvider.GetTimestamp());
            var cycleDuration = _timeProvider.GetElapsedTime(moduleCallStartedTimestamp, _timeProvider.GetTimestamp());
            _fleetRuntimeState?.CaptureSucceeded(result, moduleDuration, ingressDuration);
            _telemetry?.RecordCycle(
                evidence,
                result.AcquisitionTiming,
                moduleDuration,
                meteringDuration,
                controlDecisionDuration,
                setpointDuration,
                cycleDuration,
                ingressDuration);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.CaptureControlDecision(
                    cadenceMode.ToString(),
                    exposureControl.ToString(),
                    gainControl.ToString(),
                    regime?.ToString() ?? "none",
                    automatic.Reason.ToString(),
                    metering?.ConsideredSampleCount ?? 0,
                    metering?.ScannedBytes ?? 0);
            }
            if (effectiveSchedule.StartReason == CaptureStartReason.DeadlineOverrun)
            {
                _logger.CaptureDeadlineOverrun(monotonicStartJitter.TotalMilliseconds);
            }
            cycleActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            if (setpointFailure is not null)
            {
                cycleActivity?.SetStatus(
                    System.Diagnostics.ActivityStatusCode.Error,
                    "setpoint-application-failed");
                ExceptionDispatchInfo.Capture(setpointFailure).Throw();
            }
        }
    }

    private async ValueTask<CaptureSchedule?> WaitForScheduleAsync(
        CaptureCadenceMode cadenceMode,
        TimeSpan targetInterval,
        long? previousStartTimestamp,
        DateTimeOffset? previousStartUtc,
        bool recovering,
        DateTimeOffset? reevaluateUtc,
        CancellationToken cancellationToken)
    {
        if (!previousStartTimestamp.HasValue || !previousStartUtc.HasValue)
        {
            return new CaptureSchedule(UtcNow(), CaptureStartReason.Initial);
        }
        if (cadenceMode == CaptureCadenceMode.Continuous)
        {
            return new CaptureSchedule(
                UtcNow(),
                recovering ? CaptureStartReason.FailureRecovery : CaptureStartReason.ContinuousReady);
        }

        var requestedStartUtc = previousStartUtc.Value + targetInterval;
        var elapsed = _timeProvider.GetElapsedTime(previousStartTimestamp.Value, _timeProvider.GetTimestamp());
        var delay = targetInterval - elapsed;
        var waited = false;
        while (delay > TimeSpan.Zero)
        {
            var timerDelay = delay;
            if (reevaluateUtc is { } transition)
            {
                var transitionDelay = transition - UtcNow();
                if (transitionDelay <= TimeSpan.Zero)
                {
                    return null;
                }
                timerDelay = Min(timerDelay, transitionDelay);
            }
            await Task.Delay(
                timerDelay < MinimumTimerDelay ? MinimumTimerDelay : timerDelay,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
            if (reevaluateUtc is { } nextTransition && UtcNow() >= nextTransition)
            {
                return null;
            }
            waited = true;
            elapsed = _timeProvider.GetElapsedTime(previousStartTimestamp.Value, _timeProvider.GetTimestamp());
            delay = targetInterval - elapsed;
        }
        if (waited)
        {
            return new CaptureSchedule(
                requestedStartUtc,
                recovering ? CaptureStartReason.FailureRecovery : CaptureStartReason.DeadlineReached);
        }
        return new CaptureSchedule(
            requestedStartUtc,
            recovering ? CaptureStartReason.FailureRecovery : CaptureStartReason.DeadlineOverrun);
    }

    private CaptureMeteringEvidence Measure(
        CameraModuleConfig config,
        CameraFrame? frame,
        DateTimeOffset readoutCompletedUtc,
        ReadOnlySpan<MeteringRegion> excludedRegions)
    {
        var startedUtc = Max(UtcNow(), readoutCompletedUtc);
        using var activity = CaptureControlTelemetry.ActivitySource.StartActivity("capture-meter");
        if (frame is null || frame.PixelData.IsEmpty)
        {
            activity?.SetTag("metering.outcome", CaptureMeteringOutcome.NoFrame.ToString());
            return EmptyMetering(startedUtc, CaptureMeteringOutcome.NoFrame);
        }
        if (frame.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            _logger.CaptureMeteringUnavailable(frame.PixelFormat.ToString(), "unsupported-format");
            activity?.SetTag("metering.outcome", CaptureMeteringOutcome.UnsupportedFormat.ToString());
            return EmptyMetering(startedUtc, CaptureMeteringOutcome.UnsupportedFormat);
        }

        var policy = config.Rig.ControlPolicy!.Metering ?? new CaptureMeteringPolicy();
        var (blackLevel, whiteLevel) = ResolveStoredLevels(frame);
        if (whiteLevel <= blackLevel)
        {
            blackLevel = 0;
            whiteLevel = ushort.MaxValue;
        }
        var saturationLevel = (ushort)Math.Clamp(
            Math.Round(blackLevel + (whiteLevel - blackLevel) * policy.SaturationFraction),
            blackLevel + 1,
            whiteLevel);
        var region = policy.Region is { } crop
            ? new MeteringRegion(crop.X, crop.Y, crop.Width, crop.Height)
            : (MeteringRegion?)null;
        var imageCircle = policy.UseImageCircle ? ResolveMeteringImageCircle(config, frame) : null;
        var byteOrder = frame.Layout?.ByteOrder switch
        {
            FrameByteOrder.BigEndian => SampleByteOrder.BigEndian,
            FrameByteOrder.LittleEndian => SampleByteOrder.LittleEndian,
            _ => config.Rig.Sensor.ByteOrder
        };
        var options = new SparseMeteringOptions(
            policy.XStride,
            policy.YStride,
            blackLevel,
            whiteLevel,
            saturationLevel,
            region,
            imageCircle,
            (BayerMeteringPhotosites)(int)policy.CfaSelection,
            byteOrder);
        var result = SparseLinear16Meter.Measure(
            new ImageLayout(
                frame.Width,
                frame.Height,
                frame.PixelFormat,
                frame.StrideBytes ?? checked(frame.Width * 2)),
            frame.PixelData.Span,
            options,
            excludedRegions: excludedRegions);
        var completedUtc = Max(UtcNow(), startedUtc);
        var outcome = result.HasMeasurement
            ? CaptureMeteringOutcome.Measured
            : result.SaturatedSampleCount > 0
                ? CaptureMeteringOutcome.SaturationRejected
                : CaptureMeteringOutcome.NoEligibleSamples;
        var evidence = new CaptureMeteringEvidence(
            startedUtc,
            completedUtc,
            result.ConsideredSampleCount,
            result.AcceptedSampleCount,
            result.SaturatedSampleCount,
            result.ScannedBytes,
            result.HasMeasurement ? result.NormalizedMean : null,
            outcome);
        activity?.SetTag("metering.outcome", outcome.ToString());
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        return evidence;
    }

    private CaptureMeteringEvidence EmptyMetering(DateTimeOffset startedUtc, CaptureMeteringOutcome outcome)
    {
        var evidence = new CaptureMeteringEvidence(startedUtc, Max(UtcNow(), startedUtc), 0, 0, 0, 0, null, outcome);
        return evidence;
    }

    private static ExposureDecision Decide(
        PipelineExposureProfile profile,
        CaptureSetpoint active,
        CaptureSetpoint moduleNext,
        AutomaticControlOwnership exposureControl,
        AutomaticControlOwnership gainControl,
        CaptureSolarRegime? regime,
        CaptureMeteringEvidence? metering,
        bool regimeChanged)
    {
        var hostMetered = exposureControl == AutomaticControlOwnership.HostMetered ||
            gainControl == AutomaticControlOwnership.HostMetered;
        if (!hostMetered)
        {
            if (exposureControl == AutomaticControlOwnership.Disabled &&
                gainControl == AutomaticControlOwnership.Disabled)
            {
                return new ExposureDecision(active, CaptureControlDecisionReason.Disabled);
            }
            return new ExposureDecision(
                active with
                {
                    Exposure = exposureControl == AutomaticControlOwnership.CameraNative
                        ? moduleNext.Exposure
                        : active.Exposure,
                    Gain = gainControl == AutomaticControlOwnership.CameraNative
                        ? moduleNext.Gain
                        : active.Gain
                },
                CaptureControlDecisionReason.CameraNative);
        }

        var hostDecision = ExposureController.Next(
            profile,
            active,
            metering?.NormalizedLevel,
            regime!.Value,
            exposureControl == AutomaticControlOwnership.HostMetered,
            gainControl == AutomaticControlOwnership.HostMetered,
            metering?.Outcome == CaptureMeteringOutcome.SaturationRejected,
            regimeChanged);
        return hostDecision with
        {
            Setpoint = hostDecision.Setpoint with
            {
                Exposure = exposureControl == AutomaticControlOwnership.HostMetered
                    ? hostDecision.Setpoint.Exposure
                    : active.Exposure,
                Gain = gainControl == AutomaticControlOwnership.HostMetered
                    ? hostDecision.Setpoint.Gain
                    : active.Gain
            }
        };
    }

    private static bool RequiresSetpointApplication(CaptureSetpoint active, CaptureSetpoint decided)
        => active.Exposure != decided.Exposure || active.Gain != decided.Gain ||
            active.TargetFps != decided.TargetFps;

    private static MeteringRegion[] CreateExcludedRegions(IReadOnlyList<SensorCrop>? excludedRegions)
    {
        if (excludedRegions is null || excludedRegions.Count == 0)
        {
            return Array.Empty<MeteringRegion>();
        }

        var result = new MeteringRegion[excludedRegions.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var region = excludedRegions[index];
            result[index] = new MeteringRegion(region.X, region.Y, region.Width, region.Height);
        }
        return result;
    }

    private CaptureSolarRegime ResolveSolarRegime(CameraModuleConfig config, DateTimeOffset utc)
    {
        if (_planetEphemeris is null)
        {
            throw new InvalidOperationException("Host-metered capture control requires a planet ephemeris.");
        }
        var policy = config.Rig.ControlPolicy!.SolarRegimes ?? new CaptureSolarRegimePolicy();
        var observatory = config.ResolveObservatory(utc);
        var classification = SolarAltitudeClassifier.Classify(
            _planetEphemeris,
            utc,
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            policy.DayAltitudeThresholdDegrees,
            policy.NightAltitudeThresholdDegrees);
        return classification.Regime switch
        {
            SolarAltitudeRegime.Day => CaptureSolarRegime.Day,
            SolarAltitudeRegime.Twilight => CaptureSolarRegime.Twilight,
            SolarAltitudeRegime.Night => CaptureSolarRegime.Night,
            _ => throw new InvalidOperationException("The solar regime is invalid.")
        };
    }

    private static CaptureSetpoint ResolveActiveSetpoint(CaptureSetpoint requested, CaptureResult result)
        => result.Frame is { } frame
            ? requested with { Exposure = frame.Metadata.Exposure, Gain = frame.Metadata.Gain }
            : requested;

    private static ushort ResolveLevel(CameraFrame frame, string name, ushort fallback, int maximum)
        => frame.Metadata.Extra?.TryGetValue(name, out var value) == true &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed) && parsed >= ushort.MinValue && parsed <= maximum
                ? checked((ushort)Math.Round(parsed, MidpointRounding.AwayFromZero))
                : fallback;

    private static (ushort Black, ushort White) ResolveStoredLevels(CameraFrame frame)
    {
        var layout = frame.Layout;
        var metadataMaximum = layout?.LevelCodeSpace == FrameLevelCodeSpace.NativeSample
            ? (1 << layout.SampleDepthBits) - 1
            : layout is null ? ushort.MaxValue : (1 << layout.ContainerDepthBits) - 1;
        var black = layout?.BlackLevel ?? ResolveLevel(frame, "blackLevelAdu", 0, metadataMaximum);
        var white = layout?.WhiteLevel ?? ResolveLevel(
            frame, "whiteLevelAdu", checked((ushort)metadataMaximum), metadataMaximum);
        if (layout?.LevelCodeSpace == FrameLevelCodeSpace.NativeSample)
        {
            var sampleMaximum = Math.Pow(2, layout.SampleDepthBits) - 1;
            var containerMaximum = Math.Pow(2, layout.ContainerDepthBits) - 1;
            var scale = layout.StoredCodeTransform switch
            {
                FrameStoredCodeTransform.LeftShiftedV1 => Math.Pow(2, layout.ContainerDepthBits - layout.SampleDepthBits),
                FrameStoredCodeTransform.FullRangeScaledV1 => containerMaximum / sampleMaximum,
                _ => 1
            };
            black *= scale;
            white *= scale;
        }
        return (
            checked((ushort)Math.Round(Math.Clamp(black, ushort.MinValue, ushort.MaxValue), MidpointRounding.AwayFromZero)),
            checked((ushort)Math.Round(Math.Clamp(white, ushort.MinValue, ushort.MaxValue), MidpointRounding.AwayFromZero)));
    }

    private static MeteringImageCircle? ResolveMeteringImageCircle(CameraModuleConfig config, CameraFrame frame)
    {
        if (config.Rig.Optics.ImageCircleRadiusPixels is not { } nativeRadius)
        {
            return null;
        }
        if (frame.Layout?.Readout is not { } readout)
        {
            return new MeteringImageCircle(
                config.Rig.Optics.PrincipalPointX ?? (frame.Width - 1) / 2d,
                config.Rig.Optics.PrincipalPointY ?? (frame.Height - 1) / 2d,
                nativeRadius);
        }
        if (readout.BinX != readout.BinY)
        {
            return null;
        }
        var nativePrincipalX = config.Rig.Optics.PrincipalPointX ?? readout.NativeWidth / 2d;
        var nativePrincipalY = config.Rig.Optics.PrincipalPointY ?? readout.NativeHeight / 2d;
        return new MeteringImageCircle(
            (nativePrincipalX - readout.RoiX) / readout.BinX,
            (nativePrincipalY - readout.RoiY) / readout.BinY,
            nativeRadius / readout.BinX);
    }

    private async Task<bool> DelayAfterFailureAsync(
        int consecutiveFailures,
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        CancellationToken cancellationToken)
    {
        var delay = CalculateFailureDelay(consecutiveFailures, initialDelay, maximumDelay);
        if (ShouldLogFailure(consecutiveFailures, initialDelay, maximumDelay))
        {
            _logger.CaptureFailureBackoff(consecutiveFailures, delay.TotalMilliseconds);
        }
        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    internal static TimeSpan CalculateFailureDelay(int consecutiveFailures)
        => CalculateFailureDelay(consecutiveFailures, DefaultInitialFailureDelay, DefaultMaximumFailureDelay);

    internal static TimeSpan CalculateFailureDelay(
        int consecutiveFailures,
        TimeSpan initialDelay,
        TimeSpan maximumDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(consecutiveFailures, 1);
        var exponent = consecutiveFailures - 1d;
        var milliseconds = initialDelay.TotalMilliseconds * Math.Pow(2, exponent);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, maximumDelay.TotalMilliseconds));
    }

    private static bool ShouldLogFailure(int failures, TimeSpan initialDelay, TimeSpan maximumDelay)
        => failures == 1 || CalculateFailureDelay(failures, initialDelay, maximumDelay) !=
            CalculateFailureDelay(failures - 1, initialDelay, maximumDelay);

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private readonly record struct CaptureSchedule(DateTimeOffset RequestedStartUtc, CaptureStartReason StartReason);
}
