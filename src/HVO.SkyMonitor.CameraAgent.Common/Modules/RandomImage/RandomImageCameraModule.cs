using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules.RandomImage;

public sealed class RandomImageCameraModule(TimeProvider timeProvider) : ICameraModule
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly TimeProvider _timeProvider = timeProvider;
    private RandomImageCameraModuleOptions _options = new();
    private CameraModuleConfig? _config;
    private RandomNumberGenerator? _random;
    private ExposureEnvelope? _envelope;
    private TimeSpan _currentExposure = TimeSpan.FromMilliseconds(500);
    private double _currentGain = 1.0d;
    private double _reportedTemperatureC = double.NaN;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string DisplayName => "Random Image Camera";

    public string ModuleType => ModuleTypes.RandomImage;

    public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames | CameraModuleCapabilities.TemperatureControl;

    public ValueTask DisposeAsync()
    {
        _random?.Dispose();
        return ValueTask.CompletedTask;
    }

    public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        var moduleOptions = config.ModuleOptions;
        _options = moduleOptions.HasValue
            ? moduleOptions.Value.Deserialize<RandomImageCameraModuleOptions>(SerializerOptions) ?? new RandomImageCameraModuleOptions()
            : new RandomImageCameraModuleOptions();
        var pipeline = config.Rig.Pipeline;
        _envelope = pipeline.Envelope;
        InitializeSetpoint(pipeline);
        _reportedTemperatureC = config.Rig.ControlPolicy.ResolveTemperatureSetpoint(double.NaN);
        _random = RandomNumberGenerator.Create();
        return Task.CompletedTask;
    }

    public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_config is null || _random is null)
        {
            throw new InvalidOperationException("Module has not been initialized.");
        }

        if (request.Mode is not CaptureMode.Still)
        {
            throw new NotSupportedException("Random image module supports still captures only.");
        }

        var width = _config.Rig.Sensor.WidthPixels;
        var height = _config.Rig.Sensor.HeightPixels;
        var buffer = new byte[width * height];

        var captureStart = _timeProvider.GetTimestamp();

        switch (_options.Pattern.ToUpperInvariant())
        {
            case "GRADIENT":
                FillGradient(buffer, width, height);
                break;
            default:
                _random.GetBytes(buffer);
                break;
        }

        var metadata = new FrameMetadata(
            Exposure: _currentExposure,
            Gain: _currentGain,
            TemperatureC: _reportedTemperatureC,
            SourceId: "RandomImage",
            Extra: null);

        var frame = new CameraFrame(
            TimestampUtc: _timeProvider.GetUtcNow(),
            Width: width,
            Height: height,
            PixelFormat: _config.Rig.Sensor.PixelFormat,
            PixelData: buffer,
            Metadata: metadata);

        var nextSetpoint = new CaptureSetpoint(
            Exposure: _currentExposure,
            Gain: _currentGain,
            NextIntervalOverride: null,
            TargetFps: null);

        var processingLatency = _timeProvider.GetElapsedTime(captureStart);

        var result = new CaptureResult(
            Frame: frame,
            NextSetpoint: nextSetpoint,
            ProcessingLatency: processingLatency,
            Mode: request.Mode,
            RequiresImmediateUpload: false);

        return Task.FromResult(result);
    }

    private void InitializeSetpoint(PipelineExposureProfile pipeline)
    {
        if (_envelope is { } envelope)
        {
            _currentExposure = ClampExposure(envelope.DayDefaults.Exposure, envelope);
            _currentGain = ClampGain(envelope.DayDefaults.Gain, envelope);
            return;
        }

        _currentExposure = pipeline.DayExposure;
        _currentGain = pipeline.DayGain;
    }

    private static TimeSpan ClampExposure(TimeSpan value, ExposureEnvelope envelope)
    {
        var clampedTicks = Math.Clamp(value.Ticks, envelope.MinExposure.Ticks, envelope.MaxExposure.Ticks);
        return TimeSpan.FromTicks(clampedTicks);
    }

    private static double ClampGain(double value, ExposureEnvelope envelope)
        => Math.Clamp(value, envelope.MinGain, envelope.MaxGain);

    private static void FillGradient(Span<byte> buffer, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var rowFactor = (byte)(255.0 * y / Math.Max(1, height - 1));
            for (var x = 0; x < width; x++)
            {
                buffer[y * width + x] = (byte)((rowFactor + x) % 256);
            }
        }
    }

    private static class ModuleTypes
    {
        public const string RandomImage = "RandomImage";
    }
}
