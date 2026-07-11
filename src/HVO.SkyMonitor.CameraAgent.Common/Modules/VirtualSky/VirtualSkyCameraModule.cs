using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using System.Diagnostics;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

/// <summary>Deterministic virtual Mono16 or RGB24 camera using the shared imaging pipeline.</summary>
public sealed class VirtualSkyCameraModule(TimeProvider timeProvider) : ICameraModule
{
    private CameraModuleConfig? _config;
    private VirtualSkyCameraModuleOptions _options = new();

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string DisplayName => "Virtual Sky Camera";
    public string ModuleType => "VirtualSky";
    public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

    public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Rig.Sensor.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.Rgb24))
        {
            throw new NotSupportedException("The initial virtual sky camera profile supports Mono16 or RGB24 output.");
        }

        _config = config;
        if (config.ModuleOptions is { } options)
        {
            _options = JsonSerializer.Deserialize<VirtualSkyCameraModuleOptions>(options.GetRawText()) ?? new VirtualSkyCameraModuleOptions();
        }
        return Task.CompletedTask;
    }

    public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = _config ?? throw new InvalidOperationException("Module has not been initialized.");
        if (request.Mode != CaptureMode.Still)
        {
            throw new NotSupportedException("Virtual sky camera supports still captures only.");
        }

        var setpoint = request.RequestedSetpoint ?? new CaptureSetpoint(config.Rig.Pipeline.NightExposure, config.Rig.Pipeline.NightGain, null, null);
        var sensor = config.Rig.Sensor;
        var layout = new ImageLayout(sensor.WidthPixels, sensor.HeightPixels, sensor.PixelFormat,
            checked(sensor.WidthPixels * ImageLayout.BytesPerPixel(sensor.PixelFormat)));
        var start = timeProvider.GetTimestamp();
        var pixelData = sensor.PixelFormat switch
        {
            CameraPixelFormat.Mono16 => DeterministicStarFieldRenderer.RenderMono16(
                layout, request.RequestedStartUtc, _options.Seed, _options.StarCount, setpoint.Exposure.TotalSeconds, setpoint.Gain),
            CameraPixelFormat.Rgb24 => Rgb24GradientRenderer.Render(layout, setpoint.Exposure.TotalSeconds, setpoint.Gain),
            _ => throw new UnreachableException()
        };
        var frame = new CameraFrame(
            request.RequestedStartUtc.ToUniversalTime(), sensor.WidthPixels, sensor.HeightPixels, sensor.PixelFormat,
            pixelData,
            new FrameMetadata(setpoint.Exposure, setpoint.Gain, double.NaN, "VirtualSky"));
        return Task.FromResult(new CaptureResult(frame, setpoint, timeProvider.GetElapsedTime(start), request.Mode, false));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Virtual scene settings used by the configuration-selected camera module.</summary>
public sealed class VirtualSkyCameraModuleOptions
{
    /// <summary>Deterministic seed for synthetic star positions and brightness.</summary>
    public int Seed { get; init; } = 2025;

    /// <summary>Number of point stars rendered into Mono16 frames.</summary>
    public int StarCount { get; init; } = 32;
}
