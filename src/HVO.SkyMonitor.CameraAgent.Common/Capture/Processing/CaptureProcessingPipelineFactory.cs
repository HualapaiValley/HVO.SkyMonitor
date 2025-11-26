using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CaptureProcessingPipelineFactory : ICaptureProcessingPipelineFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CaptureProcessingPipelineFactory> _logger;
    private readonly Dictionary<string, CaptureProcessingStepRegistration> _registrationsByAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, CaptureProcessingStepRegistration> _registrationsByType = new();
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter()
        }
    };

    public CaptureProcessingPipelineFactory(
        IServiceProvider serviceProvider,
        IEnumerable<CaptureProcessingStepRegistration> registrations,
        ILogger<CaptureProcessingPipelineFactory> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ArgumentNullException.ThrowIfNull(registrations);

        foreach (var registration in registrations)
        {
            RegisterStep(registration);
        }
    }

    public IReadOnlyList<ICaptureProcessingStep> CreatePipeline(CameraModuleConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var configuredSteps = config.ResolveProcessingSteps();
        IReadOnlyList<CaptureProcessingStepConfig> pipelineConfig = configuredSteps;

        if (pipelineConfig.Count == 0 && _registrationsByType.Count > 0)
        {
            pipelineConfig = _registrationsByType.Values
                .OrderBy(r => r.DefaultOrder)
                .Select(r => new CaptureProcessingStepConfig(r.Alias, r.Alias, r.DefaultOrder, null))
                .ToList();
        }

        var steps = new List<ICaptureProcessingStep>(pipelineConfig.Count);
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stepConfig in pipelineConfig)
        {
            var step = CreateStep(stepConfig, identifiers);
            steps.Add(step);
        }

        _logger.CaptureProcessingPipelineBuilt(steps.Count);
        return steps;
    }

    private ICaptureProcessingStep CreateStep(CaptureProcessingStepConfig config, HashSet<string> identifiers)
    {
        var registration = ResolveRegistration(config.Type);

        var id = string.IsNullOrWhiteSpace(config.Id) ? registration.Alias : config.Id.Trim();
        if (!identifiers.Add(id))
        {
            throw new InvalidOperationException($"Duplicate capture processing step identifier '{id}'.");
        }
        var order = config.Order ?? registration.DefaultOrder;
        var metadata = new CaptureProcessingStepMetadata(
            id,
            registration.ImplementationType.FullName ?? registration.ImplementationType.Name,
            order);
        var options = CreateOptionsInstance(config.Options, registration.OptionsType);
        return (ICaptureProcessingStep)ActivatorUtilities.CreateInstance(
            _serviceProvider,
            registration.ImplementationType,
            metadata,
            options);
    }

    private void RegisterStep(CaptureProcessingStepRegistration registration)
    {
        _registrationsByAlias[registration.Alias] = registration;
        var fullName = registration.ImplementationType.FullName;
        if (!string.IsNullOrEmpty(fullName))
        {
            _registrationsByAlias.TryAdd(fullName, registration);
        }

        var assemblyQualifiedName = registration.ImplementationType.AssemblyQualifiedName;
        if (!string.IsNullOrEmpty(assemblyQualifiedName))
        {
            _registrationsByAlias.TryAdd(assemblyQualifiedName, registration);
        }

        _registrationsByType[registration.ImplementationType] = registration;
    }

    private CaptureProcessingStepRegistration ResolveRegistration(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new InvalidOperationException("Capture processing step type is required.");
        }

        if (_registrationsByAlias.TryGetValue(typeName, out var registration))
        {
            return registration;
        }

        var implementationType = TypeResolution.ResolveRequired(typeName, typeof(ICaptureProcessingStep));
        if (_registrationsByType.TryGetValue(implementationType, out registration))
        {
            _registrationsByAlias[typeName] = registration;
            return registration;
        }

        var optionsType = ProcessingStepReflection.GetOptionsType(implementationType);
        registration = new CaptureProcessingStepRegistration(
            implementationType.FullName ?? typeName,
            implementationType,
            optionsType,
            0);
        RegisterStep(registration);
        _registrationsByAlias[typeName] = registration;
        return registration;
    }

    private static object CreateOptionsInstance(JsonElement? element, Type optionsType)
    {
        if (optionsType == typeof(EmptyProcessingStepOptions))
        {
            return EmptyProcessingStepOptions.Instance;
        }

        object? options;
        if (element is null || element.Value.ValueKind == JsonValueKind.Undefined || element.Value.ValueKind == JsonValueKind.Null)
        {
            options = Activator.CreateInstance(optionsType);
        }
        else
        {
            options = JsonSerializer.Deserialize(element.Value.GetRawText(), optionsType, SerializerOptions);
        }

        options ??= Activator.CreateInstance(optionsType)
            ?? throw new InvalidOperationException($"Unable to create options type '{optionsType.Name}'.");

        Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);
        return options;
    }
}

public sealed class EmptyProcessingStepOptions
{
    public static EmptyProcessingStepOptions Instance { get; } = new();

    private EmptyProcessingStepOptions() { }
}
