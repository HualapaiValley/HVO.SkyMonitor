using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Exposure;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
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
    private readonly IPlanetEphemeris? _planetEphemeris;
    private readonly CaptureControlTelemetry? _telemetry;

    public CameraModuleRunner(
        ICameraModule module,
        ICaptureHostContext hostContext,
        TimeProvider timeProvider,
        ILogger logger,
        IPlanetEphemeris? planetEphemeris = null,
        CaptureControlTelemetry? telemetry = null)
    {
        _module = module;
        _hostContext = hostContext;
        _timeProvider = timeProvider;
        _logger = logger;
        _planetEphemeris = planetEphemeris;
        _telemetry = telemetry;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Capture loop must continue after transient module failures.")]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var config = _hostContext.Configuration;
        var cadenceMode = config.Rig.Pipeline.CadenceMode;
        var targetInterval = config.Rig.Pipeline.CaptureInterval;
        var initialFailureDelay = config.Rig.Pipeline.CaptureFailureInitialDelay ?? DefaultInitialFailureDelay;
        var maximumFailureDelay = config.Rig.Pipeline.CaptureFailureMaximumDelay ?? DefaultMaximumFailureDelay;
        var exposureControl = ResolveOwnership(config.Rig.ControlPolicy?.ExposureControl, config.Rig.ControlPolicy?.AutoExposure);
        var gainControl = ResolveOwnership(config.Rig.ControlPolicy?.GainControl, config.Rig.ControlPolicy?.AutoGain);
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
            ? CreateExcludedRegions(config.Rig.ControlPolicy?.Metering?.ExcludedRegions)
            : Array.Empty<MeteringRegion>();
        var nextSetpoint = ExposureController.Initial(config.Rig.Pipeline, initialRegime);
        var captureMode = CaptureMode.Still;
        var consecutiveFailures = 0;
        long? previousStartTimestamp = null;
        DateTimeOffset? previousStartUtc = null;
        var recovering = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            CaptureSchedule schedule;
            try
            {
                schedule = await WaitForScheduleAsync(
                    cadenceMode,
                    targetInterval,
                    previousStartTimestamp,
                    previousStartUtc,
                    recovering,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
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
            var request = new CaptureRequest(schedule.RequestedStartUtc, targetInterval, captureMode, nextSetpoint);
            CaptureResult? result;
            using var cycleActivity = CaptureControlTelemetry.ActivitySource.StartActivity("capture-cycle");
            cycleActivity?.SetTag("cadence.mode", cadenceMode.ToString());
            cycleActivity?.SetTag("start.reason", schedule.StartReason.ToString());
            try
            {
                result = await _module.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
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
            var regimeChanged = regime.HasValue && previousRegime.HasValue && regime != previousRegime;
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
            targetInterval = result.NextSetpoint.NextIntervalOverride ?? targetInterval;
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
                schedule.StartReason,
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
                MonotonicStartJitter = monotonicStartJitter
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
            _telemetry?.RecordCycle(
                evidence,
                result.AcquisitionTiming,
                moduleDuration,
                meteringDuration,
                controlDecisionDuration,
                setpointDuration,
                cycleDuration,
                ingressDuration);
            _logger.CaptureControlDecision(
                cadenceMode.ToString(),
                exposureControl.ToString(),
                gainControl.ToString(),
                regime?.ToString() ?? "none",
                automatic.Reason.ToString(),
                metering?.ConsideredSampleCount ?? 0,
                metering?.ScannedBytes ?? 0);
            if (schedule.StartReason == CaptureStartReason.DeadlineOverrun)
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

    private async ValueTask<CaptureSchedule> WaitForScheduleAsync(
        CaptureCadenceMode cadenceMode,
        TimeSpan targetInterval,
        long? previousStartTimestamp,
        DateTimeOffset? previousStartUtc,
        bool recovering,
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
            await Task.Delay(
                delay < MinimumTimerDelay ? MinimumTimerDelay : delay,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
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
        var blackLevel = ResolveLevel(frame, "blackLevelAdu", 0);
        var whiteLevel = ResolveLevel(frame, "whiteLevelAdu", ushort.MaxValue);
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
        var imageCircle = policy.UseImageCircle && config.Rig.Optics.ImageCircleRadiusPixels is { } radius
            ? new MeteringImageCircle(
                config.Rig.Optics.PrincipalPointX ?? (frame.Width - 1) / 2d,
                config.Rig.Optics.PrincipalPointY ?? (frame.Height - 1) / 2d,
                radius)
            : (MeteringImageCircle?)null;
        var options = new SparseMeteringOptions(
            policy.XStride,
            policy.YStride,
            blackLevel,
            whiteLevel,
            saturationLevel,
            region,
            imageCircle,
            (BayerMeteringPhotosites)(int)policy.CfaSelection,
            config.Rig.Sensor.ByteOrder);
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

    internal static AutomaticControlOwnership ResolveOwnership(
        AutomaticControlOwnership? ownership,
        CameraFeatureDirective? legacy)
    {
        if (ownership is { } explicitOwnership && explicitOwnership != AutomaticControlOwnership.Unspecified)
        {
            return explicitOwnership;
        }
        return legacy == CameraFeatureDirective.Enabled
            ? AutomaticControlOwnership.HostMetered
            : AutomaticControlOwnership.Disabled;
    }

    private CaptureSolarRegime ResolveSolarRegime(CameraModuleConfig config, DateTimeOffset utc)
    {
        if (_planetEphemeris is null)
        {
            throw new InvalidOperationException("Host-metered capture control requires a planet ephemeris.");
        }
        var policy = config.Rig.ControlPolicy!.SolarRegimes ?? new CaptureSolarRegimePolicy();
        var classification = SolarAltitudeClassifier.Classify(
            _planetEphemeris,
            utc,
            config.Observatory.LatitudeDegrees,
            config.Observatory.LongitudeDegrees,
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

    private static ushort ResolveLevel(CameraFrame frame, string name, ushort fallback)
        => frame.Metadata.Extra?.TryGetValue(name, out var value) == true &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed) && parsed is >= ushort.MinValue and <= ushort.MaxValue
                ? checked((ushort)Math.Round(parsed, MidpointRounding.AwayFromZero))
                : fallback;

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

    private readonly record struct CaptureSchedule(DateTimeOffset RequestedStartUtc, CaptureStartReason StartReason);
}
