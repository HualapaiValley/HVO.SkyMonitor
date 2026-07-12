using System;
using System.Collections.Generic;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

public sealed class FileCameraAgentConfigurationLoader(
    IOptions<CameraAgentHostOptions> options,
    ILogger<FileCameraAgentConfigurationLoader> logger) : ICameraAgentConfigurationLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter()
        }
    };

    private readonly CameraAgentHostOptions _options = options.Value;
    private readonly ILogger<FileCameraAgentConfigurationLoader> _logger = logger;

    public async Task<CameraModuleConfig> LoadAsync(CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(_options.ConfigFilePath);
        if (!File.Exists(path))
        {
            _logger.ConfigurationFileMissing(path);
            throw new FileNotFoundException("Camera agent configuration file was not found.", path);
        }

        using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<CameraModuleDocument>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            _logger.ConfigurationFileInvalid(path);
            throw new InvalidOperationException("Camera agent configuration is invalid.");
        }

        var config = new CameraModuleConfig(
            Observatory: _options.Observatory,
            Module: document.Module,
            Rig: document.Rig,
            ProcessingSteps: document.ProcessingSteps,
            Pipeline: document.Pipeline,
            AgentId: string.IsNullOrWhiteSpace(_options.AgentId) ? document.AgentId : _options.AgentId);

        ValidateConfig(config);
        _logger.ConfigurationLoaded(path);
        return config;
    }

    private static void ValidateConfig(CameraModuleConfig config)
    {
        if (config.Module is null || string.IsNullOrWhiteSpace(config.Module.Type))
        {
            throw new InvalidOperationException("Camera module type must be specified.");
        }

        if (string.IsNullOrWhiteSpace(config.AgentId))
        {
            throw new InvalidOperationException("AgentId must be specified and match the registered device identity.");
        }

        if (config.Rig.Sensor.WidthPixels <= 0 || config.Rig.Sensor.HeightPixels <= 0)
        {
            throw new InvalidOperationException("Sensor resolution must be greater than zero.");
        }

        if (config.Rig.Pipeline.CaptureInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("CaptureInterval must be greater than zero.");
        }
        var initialBackoff = config.Rig.Pipeline.CaptureFailureInitialDelay ?? TimeSpan.FromMilliseconds(250);
        var maximumBackoff = config.Rig.Pipeline.CaptureFailureMaximumDelay ?? TimeSpan.FromSeconds(30);
        if (initialBackoff <= TimeSpan.Zero || maximumBackoff < initialBackoff)
        {
            throw new InvalidOperationException(
                "Capture failure backoff delays must be positive and the maximum must not be less than the initial delay.");
        }
    }
}

internal sealed record CameraModuleDocument(
    string AgentId,
    CameraModuleDescriptor Module,
    CameraRigConfig Rig,
    IReadOnlyList<CaptureProcessingStepConfig>? ProcessingSteps = null,
    CapturePipelineConfig? Pipeline = null);
