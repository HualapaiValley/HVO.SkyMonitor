using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules.NoOp;

internal sealed class NoOpCameraModule(ILogger<NoOpCameraModule> logger) : ICameraModule
{
    private readonly ILogger<NoOpCameraModule> _logger = logger;
    private CameraModuleConfig? _config;
    private NoOpCameraModuleOptions _options = new();
    private int _sequence;
    private double _reportedTemperatureC = double.NaN;

    public string Id => "NoOpCamera";

    public string DisplayName => "No-Op Camera Module";

    public string ModuleType => typeof(NoOpCameraModule).FullName ?? nameof(NoOpCameraModule);

    public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames | CameraModuleCapabilities.TemperatureControl;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _options = BindOptions(config.ModuleOptions);
        ValidateOptions(_options);
        _reportedTemperatureC = config.Rig.ControlPolicy.ResolveTemperatureSetpoint(_options.SensorTemperatureC);
        _logger.CameraModuleInitialized(DisplayName);
        return Task.CompletedTask;
    }

    public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        if (_config is null)
        {
            throw new InvalidOperationException("Camera module has not been initialized.");
        }

        var (exposure, gain) = ResolveSetpoint();
        var frame = _options.EmitFrames ? CreateFrame(exposure, gain) : null;
        var nextSetpoint = new CaptureSetpoint(exposure, gain, request.TargetInterval, _options.TargetFps);
        var result = new CaptureResult(
            frame,
            nextSetpoint,
            TimeSpan.FromMilliseconds(1),
            request.Mode,
            _options.RequiresImmediateUpload);

        return Task.FromResult(result);
    }

    private CameraFrame CreateFrame(TimeSpan exposure, double gain)
    {
        if (_config is null)
        {
            throw new InvalidOperationException("Camera module has not been initialized.");
        }

        var width = _options.Width;
        var height = _options.Height;
        var pixelCount = width * height;
        var buffer = new byte[pixelCount];
        var fill = (byte)_options.FillValue;
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(fill + (i + _sequence) % 13);
        }

        var metadata = new FrameMetadata(exposure, gain, _reportedTemperatureC, SourceId: Id);
        var frame = new CameraFrame(DateTimeOffset.UtcNow, width, height, _options.PixelFormat, buffer, metadata);
        _sequence++;
        return frame;
    }

    private static NoOpCameraModuleOptions BindOptions(JsonElement? specific)
    {
        if (specific is null || specific.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return new NoOpCameraModuleOptions();
        }

        var options = JsonSerializer.Deserialize<NoOpCameraModuleOptions>(
            specific.Value.GetRawText(),
            NoOpCameraModuleOptions.JsonSerializerOptions);
        return options ?? new NoOpCameraModuleOptions();
    }

    private static void ValidateOptions(NoOpCameraModuleOptions options)
    {
        Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);
    }

    private (TimeSpan Exposure, double Gain) ResolveSetpoint()
    {
        if (_config is null)
        {
            throw new InvalidOperationException("Camera module has not been initialized.");
        }

        var pipeline = _config.Rig.Pipeline;
        var envelope = pipeline.Envelope;

        if (envelope is not null)
        {
            var clampedExposure = ClampExposure(envelope.DayDefaults.Exposure, envelope);
            var clampedGain = ClampGain(envelope.DayDefaults.Gain, envelope);
            return (clampedExposure, clampedGain);
        }

        return (pipeline.DayExposure, pipeline.DayGain);
    }

    private static TimeSpan ClampExposure(TimeSpan value, ExposureEnvelope envelope)
    {
        var ticks = Math.Clamp(value.Ticks, envelope.MinExposure.Ticks, envelope.MaxExposure.Ticks);
        return TimeSpan.FromTicks(ticks);
    }

    private static double ClampGain(double value, ExposureEnvelope envelope)
        => Math.Clamp(value, envelope.MinGain, envelope.MaxGain);
}

public sealed class NoOpCameraModuleOptions
{
    internal static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter()
        }
    };

    [Range(16, 8192)]
    public int Width { get; init; } = 640;

    [Range(16, 8192)]
    public int Height { get; init; } = 480;

    public CameraPixelFormat PixelFormat { get; init; } = CameraPixelFormat.Mono8;

    [Range(-50, 100)]
    public double SensorTemperatureC { get; init; } = 20;

    [Range(0, 255)]
    public int FillValue { get; init; } = 64;

    public bool EmitFrames { get; init; } = true;

    [Range(0.1, 120)]
    public double? TargetFps { get; init; }
        = null;

    public bool RequiresImmediateUpload { get; init; }
}
