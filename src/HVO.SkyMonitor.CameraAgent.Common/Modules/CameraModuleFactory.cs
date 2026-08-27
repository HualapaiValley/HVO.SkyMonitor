using System.Linq;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules;

public sealed class CameraModuleFactory(
    IServiceProvider serviceProvider,
    IEnumerable<CameraModuleRegistration> registrations,
    ILogger<CameraModuleFactory> logger) : ICameraModuleFactory, ICameraModuleConfigurationValidator
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly Dictionary<string, Type> _registrationMap = registrations
        .GroupBy(r => r.ModuleType, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Last().ImplementationType, StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<CameraModuleFactory> _logger = logger;

    public ICameraModule Create(string moduleType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleType);
        var implementationType = ResolveModuleType(moduleType);
        var module = (ICameraModule)ActivatorUtilities.CreateInstance(_serviceProvider, implementationType);
        _logger.CameraModuleCreated(moduleType, implementationType.FullName ?? implementationType.Name);
        return module;
    }

    public void Validate(CameraModuleConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var module = Create(configuration.ModuleType);
        try
        {
            if (module is ICameraModuleConfigurationPreflight preflight)
            {
                preflight.ValidateConfiguration(configuration);
            }
            var controlPolicy = configuration.Rig.ControlPolicy ?? throw new InvalidOperationException(
                "Camera control ownership must be configured explicitly.");
            var automaticControl = controlPolicy.ExposureControl != AutomaticControlOwnership.Disabled ||
                controlPolicy.GainControl != AutomaticControlOwnership.Disabled;
            if (automaticControl && module is not ICameraSetpointController)
            {
                throw new InvalidOperationException(
                    "Automatic camera control requires a module that supports capture setpoints.");
            }
        }
        finally
        {
            module.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private Type ResolveModuleType(string moduleType)
    {
        if (_registrationMap.TryGetValue(moduleType, out var implementationType))
        {
            return implementationType;
        }

        implementationType = TypeResolution.ResolveRequired(moduleType, typeof(ICameraModule));
        _registrationMap[moduleType] = implementationType;
        var fullName = implementationType.FullName;
        if (!string.IsNullOrEmpty(fullName))
        {
            _registrationMap[fullName] = implementationType;
        }

        return implementationType;
    }
}
