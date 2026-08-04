using System.Collections.Frozen;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Modules.Zwo;

[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The gate remains valid so repeated concurrent DisposeAsync calls are idempotent.")]
[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Native cleanup is best-effort and must not hide the primary capture or initialization failure.")]
public sealed class ZwoAsiCameraModule :
    ICameraModule,
    ICameraSetpointController,
    ICameraModuleConfigurationPreflight
{
    private const string SupportedSdkMajorMinor = "1.41";
    private static readonly FrozenDictionary<string, SupportedProfile> SupportedProfiles = new[]
    {
        new SupportedProfile("ASI676MC", 3552, 3552, 2.0, 12, 7104),
        new SupportedProfile("ASI178MC", 3096, 2080, 2.4, 14, 6192)
    }.ToFrozenDictionary(profile => profile.Model, StringComparer.Ordinal);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly TimeProvider _timeProvider;
    private readonly Func<string, IAsiNativeApi> _nativeApiFactory;
    private readonly Func<string, string?> _environmentVariableResolver;
    private readonly SemaphoreSlim _nativeGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private IAsiNativeApi? _native;
    private CameraModuleConfig? _configuration;
    private ZwoAsiCameraModuleOptions? _options;
    private AsiCameraInfo? _camera;
    private Dictionary<AsiControlType, AsiControlCaps>? _controls;
    private ResolvedSensorReadout? _readout;
    private int _cameraId = -1;
    private bool _cameraOpen;
    private bool _exposureActive;
    private bool _disposed;
    private int _successfulCapturesInSession;
    private TimeSpan _effectiveExposure;
    private double _effectiveGain;
    private double _effectiveOffset;
    private DateTimeOffset? _setpointAppliedUtc;

    public ZwoAsiCameraModule(TimeProvider timeProvider)
        : this(timeProvider, path => AsiNativeApi.Load(path), Environment.GetEnvironmentVariable)
    {
    }

    internal ZwoAsiCameraModule(
        TimeProvider timeProvider,
        Func<string, IAsiNativeApi> nativeApiFactory,
        Func<string, string?> environmentVariableResolver)
    {
        _timeProvider = timeProvider;
        _nativeApiFactory = nativeApiFactory;
        _environmentVariableResolver = environmentVariableResolver;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    public string DisplayName => "ZWO ASI Camera";
    public string ModuleType => "ZwoAsi";
    public CameraModuleCapabilities Capabilities =>
        CameraModuleCapabilities.StillFrames |
        CameraModuleCapabilities.AdaptiveExposure |
        CameraModuleCapabilities.AdaptiveGain;

    void ICameraModuleConfigurationPreflight.ValidateConfiguration(CameraModuleConfig configuration)
        => _ = BindAndValidate(configuration);

    public async Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var validated = BindAndValidate(config);
        ValidatePlatform();

        await _nativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CleanupNative();
            cancellationToken.ThrowIfCancellationRequested();

            var runtime = ResolveRuntimeConfiguration(validated.Options);
            try
            {
                try
                {
                    _native = _nativeApiFactory(runtime.LibraryPath);
                }
                catch
                {
                    throw new InvalidOperationException("The configured ASI SDK library could not be loaded or bound.");
                }
                ValidateSdkVersion(_native.GetSdkVersion());
                var selected = SelectCamera(_native, runtime.Serial, validated.Profile.Model, cancellationToken);
                _cameraId = selected.CameraId;
                try
                {
                    _native.OpenCamera(_cameraId);
                    _cameraOpen = true;
                    _native.InitializeCamera(_cameraId);
                }
                catch (AsiException)
                {
                    throw new InvalidOperationException("The configured ASI camera is not connected or accessible.");
                }
                cancellationToken.ThrowIfCancellationRequested();

                var controls = _native.GetControlCapabilities(_cameraId)
                    .ToDictionary(control => control.Type);
                ValidateCameraAndControls(config, validated.Options, validated.Profile, selected, validated.Readout, controls);

                _configuration = config;
                _options = validated.Options;
                _camera = selected;
                _readout = validated.Readout;
                _controls = controls;

                var initial = config.Rig.Pipeline.Envelope?.DayDefaults is { } defaults
                    ? new CaptureSetpoint(defaults.Exposure, defaults.Gain, null, null)
                    : new CaptureSetpoint(config.Rig.Pipeline.DayExposure, config.Rig.Pipeline.DayGain, null, null);
                ApplySetpointCore(initial);
                SetAndRequireReadback(AsiControlType.Offset, validated.Options.Offset);
                SetAndRequireReadback(AsiControlType.BandwidthOverload, validated.Options.UsbBandwidth);
                SetOptionalBooleanControl(AsiControlType.HighSpeedMode, false);
                SetOptionalBooleanControl(AsiControlType.Flip, false);
                SetOptionalBooleanControl(AsiControlType.MonoBin, validated.Options.MonoBin);

                var profile = validated.Readout.Profile;
                var layout = validated.Readout.Layout;
                _native.SetRoiFormat(_cameraId, layout.Width, layout.Height, profile.BinX, AsiImageType.Raw16);
                _native.SetStartPosition(_cameraId, profile.Roi.X / profile.BinX, profile.Roi.Y / profile.BinY);
                SetOptionalBooleanControl(AsiControlType.HardwareBin, validated.Options.HardwareBin);
                ValidateRoiReadback(validated.Readout);
            }
            catch
            {
                CleanupNative();
                throw;
            }
        }
        finally
        {
            _nativeGate.Release();
        }
    }

    public async ValueTask<DateTimeOffset> ApplySetpointAsync(
        CaptureSetpoint setpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setpoint);
        await _nativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            return ApplySetpointCore(setpoint);
        }
        finally
        {
            _nativeGate.Release();
        }
    }

    public async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode != CaptureMode.Still)
        {
            throw new NotSupportedException("The ZWO ASI module supports still captures only.");
        }

        using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        var captureToken = captureCancellation.Token;
        await _nativeGate.WaitAsync(captureToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var native = _native!;
            var readout = _readout!;
            var options = _options!;
            if (options.MaximumCapturesPerSession > 0 &&
                _successfulCapturesInSession >= options.MaximumCapturesPerSession)
            {
                RestartCameraSession();
                native = _native!;
            }
            if (request.RequestedSetpoint is { } requestedSetpoint)
            {
                ApplySetpointCore(requestedSetpoint);
            }

            var exposureStartedUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            var captureStartedTimestamp = _timeProvider.GetTimestamp();
            native.StartExposure(_cameraId, dark: false);
            _exposureActive = true;
            var timeout = AddChecked(_effectiveExposure, options.CaptureTimeoutMargin);
            DateTimeOffset exposureEndedUtc = default;
            long exposureEndedTimestamp = default;
            byte[] pixels;
            try
            {
                while (true)
                {
                    captureToken.ThrowIfCancellationRequested();
                    if (_timeProvider.GetElapsedTime(captureStartedTimestamp) >= timeout)
                    {
                        throw new TimeoutException("The ASI still exposure exceeded its configured monotonic deadline.");
                    }

                    var status = native.GetExposureStatus(_cameraId);
                    if (status == AsiExposureStatus.Success)
                    {
                        exposureEndedTimestamp = _timeProvider.GetTimestamp();
                        exposureEndedUtc = _timeProvider.GetUtcNow().ToUniversalTime();
                        break;
                    }
                    if (status != AsiExposureStatus.Working)
                    {
                        throw new InvalidOperationException($"ASI still exposure failed with status {status}.");
                    }

                    await Task.Delay(options.PollInterval, _timeProvider, captureToken).ConfigureAwait(false);
                }

                captureToken.ThrowIfCancellationRequested();
                if (_timeProvider.GetElapsedTime(captureStartedTimestamp) >= timeout)
                {
                    throw new TimeoutException("The ASI still exposure exceeded its configured monotonic deadline before readout.");
                }

                var byteCount = checked((int)readout.Layout.ByteLength);
                pixels = GC.AllocateUninitializedArray<byte>(byteCount);
                unsafe
                {
                    fixed (byte* pointer = pixels)
                    {
                        // The SDK call is synchronous; the native gate and frame buffer remain owned until it returns.
                        native.GetDataAfterExposure(_cameraId, (IntPtr)pointer, byteCount);
                    }
                }
                _exposureActive = false;
                _successfulCapturesInSession++;
            }
            catch
            {
                AbortActiveExposureAfterFailure();
                throw;
            }
            var readoutCompletedUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            var processingLatency = _timeProvider.GetElapsedTime(exposureEndedTimestamp);
            var temperatureC = ReadTemperature();
            var frame = new CameraFrame(
                exposureStartedUtc,
                readout.Layout.Width,
                readout.Layout.Height,
                readout.Layout.PixelFormat,
                pixels,
                new FrameMetadata(
                    _effectiveExposure,
                    _effectiveGain,
                    temperatureC,
                    SourceId: _camera!.Model,
                    Offset: _effectiveOffset),
                readout.Layout.StrideBytes)
            {
                Layout = readout.Layout
            };
            var nextSetpoint = new CaptureSetpoint(_effectiveExposure, _effectiveGain, null, null);
            return new CaptureResult(frame, nextSetpoint, processingLatency, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    exposureStartedUtc,
                    exposureEndedUtc,
                    readoutCompletedUtc)
                {
                    SetpointAppliedUtc = _setpointAppliedUtc
                }
            };
        }
        finally
        {
            _nativeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeCancellation.CancelAsync().ConfigureAwait(false);
        await _nativeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            CleanupNative(throwCloseFailure: true);
        }
        finally
        {
            _nativeGate.Release();
        }
    }

    private static ValidatedConfiguration BindAndValidate(CameraModuleConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.ModuleOptions is not { } element || element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("ZWO ASI module options are required.", nameof(configuration));
        }

        var options = element.Deserialize<ZwoAsiCameraModuleOptions>(SerializerOptions)
            ?? throw new ArgumentException("ZWO ASI module options are invalid.", nameof(configuration));
        if (!IsValidEnvironmentVariableName(options.LibraryPathEnvironmentVariable))
        {
            throw new ArgumentException("The ASI SDK library-path environment-variable name is required.", nameof(configuration));
        }
        if (string.IsNullOrWhiteSpace(options.ExpectedModel) || options.ExpectedModel.Length > 63)
        {
            throw new ArgumentException("The expected ASI camera model is required and must fit the SDK model field.", nameof(configuration));
        }
        if (!SupportedProfiles.TryGetValue(options.ExpectedModel, out var supportedProfile))
        {
            throw new NotSupportedException("The configured expected ASI camera model is not supported.");
        }
        if (!IsValidEnvironmentVariableName(options.CameraSerialEnvironmentVariable))
        {
            throw new ArgumentException("The ASI camera-serial environment-variable name is required.", nameof(configuration));
        }
        if (options.Offset < 0 || options.UsbBandwidth is < 0 or > 100 ||
            options.MaximumCapturesPerSession is < 0 or > 1_000_000 ||
            options.PollInterval <= TimeSpan.Zero || options.PollInterval > TimeSpan.FromSeconds(1) ||
            options.CaptureTimeoutMargin < TimeSpan.Zero || options.CaptureTimeoutMargin > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(configuration), "ASI control and timing options are outside supported bounds.");
        }
        if (options.MonoBin || options.HardwareBin)
        {
            throw new NotSupportedException("The first supported ZWO ASI mode requires mono-bin and hardware-bin to remain disabled.");
        }

        var readout = configuration.Rig.Readout is { } profile
            ? SensorReadoutResolver.Resolve(configuration.Rig.Sensor, profile)
            : throw new ArgumentException("The ZWO ASI module requires an explicit physical sensor readout.", nameof(configuration));
        ValidateConfiguredProfile(configuration.Rig.Sensor, readout, supportedProfile);
        return new ValidatedConfiguration(options, readout, supportedProfile);
    }

    private static void ValidateConfiguredProfile(
        SensorProfile sensor,
        ResolvedSensorReadout readout,
        SupportedProfile supportedProfile)
    {
        var profile = readout.Profile;
        if (sensor.WidthPixels != supportedProfile.Width || sensor.HeightPixels != supportedProfile.Height ||
            Math.Abs(sensor.PixelSizeMicrons - supportedProfile.PixelSizeMicrons) > 0.0001 ||
            sensor.ColorMode != SensorColorMode.Color || sensor.PixelFormat != CameraPixelFormat.BayerRggb16 ||
            sensor.ResponseMode != SensorResponseMode.BayerRaw || sensor.SimulationResponse is not null ||
            sensor.StrideBytes != supportedProfile.StrideBytes || sensor.ByteOrder != SampleByteOrder.LittleEndian ||
            profile.Roi != new SensorCrop(0, 0, supportedProfile.Width, supportedProfile.Height) ||
            profile.BinX != 1 || profile.BinY != 1 ||
            profile.BinningAlgorithm != FrameBinningAlgorithm.IdentityV1 ||
            profile.PixelFormat != CameraPixelFormat.BayerRggb16 ||
            profile.SampleDepthBits != supportedProfile.SampleDepthBits ||
            profile.ContainerDepthBits != 16 || profile.Packing != FrameSamplePacking.ByteAligned ||
            profile.ByteOrder != SampleByteOrder.LittleEndian || profile.CfaPattern != ColorFilterArrayPattern.Rggb ||
            profile.CfaOriginX != 0 || profile.CfaOriginY != 0 ||
            profile.StrideBytes != supportedProfile.StrideBytes ||
            profile.StoredCodeTransform != FrameStoredCodeTransform.OpaqueContainerV1 ||
            profile.LevelCodeSpace != FrameLevelCodeSpace.StoredContainer ||
            profile.BlackLevel != 0 || profile.WhiteLevel != 65535)
        {
            throw new NotSupportedException(
                $"The supported {supportedProfile.Model} mode is its full-frame bin-1 RAW16 BayerRggb16 profile with an opaque stored-container mapping.");
        }
    }

    private static void ValidatePlatform()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The ZWO ASI module currently supports Linux only.");
        }
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new PlatformNotSupportedException("The ZWO ASI module requires a Linux x64 or ARM64 LP64 process.");
        }
        AsiAbi.Validate();
    }

    private static void ValidateSdkVersion(string value)
    {
        var numbers = new List<int>(2);
        var current = 0;
        var hasDigits = false;
        foreach (var character in value)
        {
            if (char.IsAsciiDigit(character))
            {
                current = checked(current * 10 + character - '0');
                hasDigits = true;
            }
            else if (hasDigits)
            {
                numbers.Add(current);
                current = 0;
                hasDigits = false;
                if (numbers.Count == 2)
                {
                    break;
                }
            }
        }
        if (hasDigits && numbers.Count < 2)
        {
            numbers.Add(current);
        }
        if (numbers.Count < 2 || numbers[0] != 1 || numbers[1] != 41)
        {
            throw new NotSupportedException($"The ZWO ASI module requires SDK V{SupportedSdkMajorMinor} major/minor compatibility.");
        }
    }

    private static AsiCameraInfo SelectCamera(
        IAsiNativeApi native,
        ReadOnlySpan<byte> expectedSerial,
        string expectedModel,
        CancellationToken cancellationToken)
    {
        var count = native.GetCameraCount();
        if (count is < 0 or > 256)
        {
            throw new InvalidOperationException("The ASI SDK returned an invalid connected-camera count.");
        }
        var matches = new List<AsiCameraInfo>();
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AsiCameraInfo camera;
            try
            {
                camera = native.GetCameraProperty(index);
            }
            catch (AsiException)
            {
                continue;
            }
            if (!string.Equals(NormalizeNativeModel(camera.Model), expectedModel, StringComparison.Ordinal))
            {
                continue;
            }

            var opened = false;
            var identityRead = false;
            var serialMatches = false;
            var closed = false;
            try
            {
                native.OpenCamera(camera.CameraId);
                opened = true;
                serialMatches = native.GetSerialNumber(camera.CameraId).AsSpan().SequenceEqual(expectedSerial);
                identityRead = true;
            }
            catch (AsiException)
            {
                // Inventory entries that cannot safely identify themselves are not selection candidates.
            }
            finally
            {
                if (opened)
                {
                    try
                    {
                        native.CloseCamera(camera.CameraId);
                        closed = true;
                    }
                    catch (AsiException)
                    {
                        // An entry that cannot close cleanly is not safe to select.
                    }
                }
            }

            if (opened && !closed)
            {
                throw new InvalidOperationException("The configured ASI camera is not connected.");
            }

            if (identityRead && closed && serialMatches)
            {
                matches.Add(camera);
            }
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException("The configured ASI camera is not connected.");
        }
        if (matches.Count > 1)
        {
            throw new InvalidOperationException("The configured ASI camera identity is ambiguous.");
        }
        if (!string.Equals(NormalizeNativeModel(matches[0].Model), expectedModel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The selected ASI camera does not match the configured expected model.");
        }
        return matches[0];
    }

    private static void ValidateCameraAndControls(
        CameraModuleConfig configuration,
        ZwoAsiCameraModuleOptions options,
        SupportedProfile supportedProfile,
        AsiCameraInfo camera,
        ResolvedSensorReadout readout,
        Dictionary<AsiControlType, AsiControlCaps> controls)
    {
        if (!string.Equals(NormalizeNativeModel(camera.Model), supportedProfile.Model, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The selected ASI camera does not match the validated physical profile.");
        }
        if (camera.MaximumWidth != supportedProfile.Width || camera.MaximumHeight != supportedProfile.Height ||
            Math.Abs(camera.PixelSizeMicrons - supportedProfile.PixelSizeMicrons) > 0.0001 ||
            camera.BitDepth != supportedProfile.SampleDepthBits)
        {
            throw new InvalidOperationException("The selected ASI camera geometry or ADC depth does not match the configured rig.");
        }
        if (!camera.IsColorCamera || camera.BayerPattern != AsiBayerPattern.Rg ||
            readout.Layout.CfaPattern != ColorFilterArrayPattern.Rggb)
        {
            throw new InvalidOperationException("The selected ASI camera color/CFA layout does not match the configured rig.");
        }
        if (!camera.SupportedBins.Contains(readout.Profile.BinX) || !camera.SupportedImageTypes.Contains(AsiImageType.Raw16))
        {
            throw new NotSupportedException("The selected ASI camera does not advertise the configured bin and RAW16 mode.");
        }
        foreach (var required in new[]
                 {
                     AsiControlType.Exposure, AsiControlType.Gain, AsiControlType.Offset,
                     AsiControlType.BandwidthOverload
                 })
        {
            if (!controls.TryGetValue(required, out var capability) || !capability.IsWritable)
            {
                throw new NotSupportedException($"The selected ASI camera lacks required writable control {required}.");
            }
        }
        ValidateControlRange(controls[AsiControlType.Offset], options.Offset);
        ValidateControlRange(controls[AsiControlType.BandwidthOverload], options.UsbBandwidth);
        ValidateConfiguredSetpoints(configuration.Rig.Pipeline, controls);
    }

    private DateTimeOffset ApplySetpointCore(CaptureSetpoint setpoint)
    {
        if (setpoint.Exposure <= TimeSpan.Zero || !double.IsFinite(setpoint.Gain) ||
            setpoint.Gain < 0 || setpoint.Gain != Math.Truncate(setpoint.Gain) ||
            setpoint.TargetFps is { } fps && (!double.IsFinite(fps) || fps <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(setpoint), "ASI exposure and gain setpoints must be positive finite values in native control units.");
        }
        var exposureMicroseconds = ToExposureMicroseconds(setpoint.Exposure, nameof(setpoint));
        var gain = ToGain(setpoint.Gain, nameof(setpoint));
        var exposureCaps = GetWritableControl(AsiControlType.Exposure);
        var gainCaps = GetWritableControl(AsiControlType.Gain);
        ValidateControlRange(exposureCaps, exposureMicroseconds);
        ValidateControlRange(gainCaps, gain);

        _native!.SetControlValue(_cameraId, AsiControlType.Exposure, exposureMicroseconds, automatic: false);
        var actualExposure = _native.GetControlValue(_cameraId, AsiControlType.Exposure);
        _native.SetControlValue(_cameraId, AsiControlType.Gain, gain, automatic: false);
        var actualGain = _native.GetControlValue(_cameraId, AsiControlType.Gain);
        if (actualExposure.Automatic || actualGain.Automatic)
        {
            throw new InvalidOperationException("The ASI camera retained automatic exposure or gain after a manual setpoint.");
        }
        ValidateControlRange(exposureCaps, actualExposure.Value);
        ValidateControlRange(gainCaps, actualGain.Value);
        _effectiveExposure = TimeSpan.FromTicks(checked(actualExposure.Value * 10));
        _effectiveGain = actualGain.Value;
        _setpointAppliedUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        return _setpointAppliedUtc.Value;
    }

    private void SetAndRequireReadback(AsiControlType type, long value)
    {
        var caps = GetWritableControl(type);
        ValidateControlRange(caps, value);
        _native!.SetControlValue(_cameraId, type, value, automatic: false);
        var actual = _native.GetControlValue(_cameraId, type);
        if (actual.Automatic || actual.Value != value)
        {
            throw new InvalidOperationException($"ASI control {type} did not retain the configured manual value.");
        }
        if (type == AsiControlType.Offset)
        {
            _effectiveOffset = actual.Value;
        }
    }

    private void SetOptionalBooleanControl(AsiControlType type, bool enabled)
    {
        if (_controls!.TryGetValue(type, out var caps) && caps.IsWritable)
        {
            SetAndRequireReadback(type, enabled ? 1 : 0);
        }
        else if (enabled)
        {
            throw new NotSupportedException($"The selected ASI camera lacks required control {type}.");
        }
    }

    private void ValidateRoiReadback(ResolvedSensorReadout readout)
    {
        var roi = _native!.GetRoiFormat(_cameraId);
        var position = _native.GetStartPosition(_cameraId);
        if (roi.Width != readout.Layout.Width || roi.Height != readout.Layout.Height ||
            roi.Bin != readout.Profile.BinX || roi.ImageType != AsiImageType.Raw16 ||
            position.X != readout.Profile.Roi.X / readout.Profile.BinX ||
            position.Y != readout.Profile.Roi.Y / readout.Profile.BinY)
        {
            throw new InvalidOperationException("The ASI camera did not retain the configured ROI, bin, format, or start position.");
        }
    }

    private double ReadTemperature()
    {
        if (_controls!.ContainsKey(AsiControlType.Temperature))
        {
            var temperature = _native!.GetControlValue(_cameraId, AsiControlType.Temperature);
            return temperature.Value / 10d;
        }
        return double.NaN;
    }

    private AsiControlCaps GetWritableControl(AsiControlType type)
    {
        if (_controls is null || !_controls.TryGetValue(type, out var caps) || !caps.IsWritable)
        {
            throw new NotSupportedException($"The selected ASI camera lacks required writable control {type}.");
        }
        return caps;
    }

    private static void ValidateControlRange(AsiControlCaps caps, long value)
    {
        if (value < caps.Minimum || value > caps.Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"ASI control {caps.Type} is outside the camera capability range.");
        }
    }

    private void StopActiveExposure()
    {
        if (!_exposureActive || _native is null || !_cameraOpen)
        {
            return;
        }
        _native.StopExposure(_cameraId);
        _exposureActive = false;
    }

    private void AbortActiveExposureAfterFailure()
    {
        try
        {
            StopActiveExposure();
        }
        catch
        {
            CleanupNative(skipExposureStop: true);
        }
    }

    private void RestartCameraSession()
    {
        var native = _native!;
        var config = _configuration!;
        var options = _options!;
        var camera = _camera!;
        var readout = _readout!;
        var setpoint = new CaptureSetpoint(_effectiveExposure, _effectiveGain, null, null);
        try
        {
            native.CloseCamera(_cameraId);
            _cameraOpen = false;
            native.OpenCamera(_cameraId);
            _cameraOpen = true;
            native.InitializeCamera(_cameraId);
            var controls = native.GetControlCapabilities(_cameraId).ToDictionary(control => control.Type);
            ValidateCameraAndControls(
                config, options, SupportedProfiles[options.ExpectedModel], camera, readout, controls);
            _controls = controls;
            ApplySetpointCore(setpoint);
            SetAndRequireReadback(AsiControlType.Offset, options.Offset);
            SetAndRequireReadback(AsiControlType.BandwidthOverload, options.UsbBandwidth);
            SetOptionalBooleanControl(AsiControlType.HighSpeedMode, false);
            SetOptionalBooleanControl(AsiControlType.Flip, false);
            SetOptionalBooleanControl(AsiControlType.MonoBin, options.MonoBin);
            var profile = readout.Profile;
            var layout = readout.Layout;
            native.SetRoiFormat(_cameraId, layout.Width, layout.Height, profile.BinX, AsiImageType.Raw16);
            native.SetStartPosition(_cameraId, profile.Roi.X / profile.BinX, profile.Roi.Y / profile.BinY);
            SetOptionalBooleanControl(AsiControlType.HardwareBin, options.HardwareBin);
            ValidateRoiReadback(readout);
            _successfulCapturesInSession = 0;
        }
        catch (Exception exception)
        {
            CleanupNative(skipExposureStop: true);
            throw new InvalidOperationException("The ASI camera session could not be recycled safely.", exception);
        }
    }

    private void CleanupNative(bool skipExposureStop = false, bool throwCloseFailure = false)
    {
        var native = _native;
        if (native is null)
        {
            ResetState();
            return;
        }
        if (!skipExposureStop)
        {
            try
            {
                StopActiveExposure();
            }
            catch
            {
                // Closing and unloading are still required after a failed stop.
            }
        }
        Exception? closeException = null;
        try
        {
            if (_cameraOpen)
            {
                native.CloseCamera(_cameraId);
                _cameraOpen = false;
            }
        }
        catch (Exception exception)
        {
            // Native disposal and managed reset remain best-effort and idempotent.
            closeException = exception;
        }
        try
        {
            native.Dispose();
        }
        catch
        {
            // The managed session must still be invalidated if unloading fails.
        }
        finally
        {
            ResetState();
        }
        if (throwCloseFailure && closeException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeException).Throw();
        }
    }

    private void ResetState()
    {
        _native = null;
        _configuration = null;
        _options = null;
        _camera = null;
        _controls = null;
        _readout = null;
        _cameraId = -1;
        _cameraOpen = false;
        _exposureActive = false;
        _successfulCapturesInSession = 0;
        _effectiveExposure = default;
        _effectiveGain = default;
        _effectiveOffset = default;
        _setpointAppliedUtc = null;
    }

    private void EnsureInitialized()
    {
        ThrowIfDisposed();
        if (_native is null || !_cameraOpen || _configuration is null)
        {
            throw new InvalidOperationException("The ZWO ASI module has not been initialized.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static TimeSpan AddChecked(TimeSpan left, TimeSpan right)
        => TimeSpan.FromTicks(checked(left.Ticks + right.Ticks));

    private static bool TryParseSerial(string value, out byte[] serial)
    {
        serial = Array.Empty<byte>();
        if (value.Length != 16)
        {
            return false;
        }
        try
        {
            serial = Convert.FromHexString(value);
            return serial.Length == 8;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private ResolvedRuntimeConfiguration ResolveRuntimeConfiguration(ZwoAsiCameraModuleOptions options)
    {
        string? libraryPath;
        string? cameraSerial;
        try
        {
            libraryPath = _environmentVariableResolver(options.LibraryPathEnvironmentVariable);
            cameraSerial = _environmentVariableResolver(options.CameraSerialEnvironmentVariable);
        }
        catch
        {
            throw new InvalidOperationException("The configured ASI runtime environment could not be resolved.");
        }
        if (string.IsNullOrWhiteSpace(libraryPath) || !Path.IsPathFullyQualified(libraryPath))
        {
            throw new InvalidOperationException("The configured ASI SDK library environment variable must contain an absolute path.");
        }
        if (cameraSerial is null || !TryParseSerial(cameraSerial, out var serial))
        {
            throw new InvalidOperationException("The configured ASI camera serial environment variable must contain exactly 16 hexadecimal characters.");
        }
        return new ResolvedRuntimeConfiguration(libraryPath, serial);
    }

    private static void ValidateConfiguredSetpoints(
        PipelineExposureProfile pipeline,
        Dictionary<AsiControlType, AsiControlCaps> controls)
    {
        var exposure = controls[AsiControlType.Exposure];
        var gain = controls[AsiControlType.Gain];
        ValidateConfiguredSetpoint(pipeline.DayExposure, pipeline.DayGain, exposure, gain);
        ValidateConfiguredSetpoint(pipeline.NightExposure, pipeline.NightGain, exposure, gain);
        if (pipeline.Envelope is not { } envelope)
        {
            return;
        }

        ValidateConfiguredSetpoint(envelope.MinExposure, envelope.MinGain, exposure, gain);
        ValidateConfiguredSetpoint(envelope.MaxExposure, envelope.MaxGain, exposure, gain);
        ValidateConfiguredSetpoint(envelope.DayDefaults.Exposure, envelope.DayDefaults.Gain, exposure, gain);
        ValidateConfiguredSetpoint(envelope.NightDefaults.Exposure, envelope.NightDefaults.Gain, exposure, gain);
        if (envelope.TwilightDefaults is { } twilight)
        {
            ValidateConfiguredSetpoint(twilight.Exposure, twilight.Gain, exposure, gain);
        }
    }

    private static void ValidateConfiguredSetpoint(
        TimeSpan exposure,
        double gain,
        AsiControlCaps exposureCaps,
        AsiControlCaps gainCaps)
    {
        ValidateControlRange(exposureCaps, ToExposureMicroseconds(exposure, nameof(exposure)));
        ValidateControlRange(gainCaps, ToGain(gain, nameof(gain)));
    }

    private static long ToExposureMicroseconds(TimeSpan exposure, string parameterName)
    {
        var microseconds = exposure.TotalMicroseconds;
        if (exposure <= TimeSpan.Zero || !double.IsFinite(microseconds) || microseconds > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        var rounded = checked((long)Math.Round(microseconds, MidpointRounding.AwayFromZero));
        return rounded > 0 ? rounded : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static long ToGain(double gain, string parameterName)
    {
        if (!double.IsFinite(gain) || gain < 0 || gain != Math.Truncate(gain) || gain > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        return checked((long)gain);
    }

    private static bool IsValidEnvironmentVariableName(string value)
        => !string.IsNullOrWhiteSpace(value) &&
            !value.Contains('=', StringComparison.Ordinal) &&
            !value.Contains('\0', StringComparison.Ordinal);

    private static string NormalizeNativeModel(string value)
        => value.StartsWith("ZWO ", StringComparison.Ordinal) ? value[4..] : value;

    private sealed record ValidatedConfiguration(
        ZwoAsiCameraModuleOptions Options,
        ResolvedSensorReadout Readout,
        SupportedProfile Profile);

    private sealed record SupportedProfile(
        string Model,
        int Width,
        int Height,
        double PixelSizeMicrons,
        int SampleDepthBits,
        int StrideBytes);

    private sealed record ResolvedRuntimeConfiguration(string LibraryPath, byte[] Serial);
}
