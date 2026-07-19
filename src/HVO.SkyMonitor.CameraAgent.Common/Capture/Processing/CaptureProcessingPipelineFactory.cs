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
using System.Diagnostics;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CaptureProcessingPipelineFactory : ICaptureProcessingPipelineFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CaptureProcessingPipelineFactory> _logger;
    private readonly CaptureProcessingTelemetry _telemetry;
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
        ILogger<CaptureProcessingPipelineFactory> logger,
        CaptureProcessingTelemetry telemetry)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));

        ArgumentNullException.ThrowIfNull(registrations);

        foreach (var registration in registrations)
        {
            RegisterStep(registration);
        }
    }

    public IReadOnlyList<ICaptureProcessingStep> CreatePipeline(CameraModuleConfig config)
        => CreateGraph(config).Nodes.Select(static node => node.Step).ToArray();

    public CaptureProcessingGraph CreateGraph(CameraModuleConfig config)
    {
        var stopwatch = Stopwatch.StartNew();
        using var activity = CaptureProcessingTelemetry.ActivitySource.StartActivity("processing-graph.validate");
        ArgumentNullException.ThrowIfNull(config);
        var configuredSteps = config.ResolveProcessingSteps();
        IReadOnlyList<CaptureProcessingStepConfig> pipelineConfig = configuredSteps;

        if (pipelineConfig.Count == 0 && _registrationsByType.Count > 0)
        {
            pipelineConfig = _registrationsByType.Values
                .Where(registration => registration.AutoInclude &&
                    (config.Rig.Sensor.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 ||
                     registration.ImplementationType != typeof(CalibrationCaptureProcessingStep) &&
                     registration.ImplementationType != typeof(RollingCombinationCaptureProcessingStep)))
                .OrderBy(r => r.DefaultOrder)
                .Select(r => new CaptureProcessingStepConfig(
                    r.Alias,
                    r.Alias,
                    r.DefaultOrder,
                    null,
                    string.Equals(r.Alias, "Annotation", StringComparison.OrdinalIgnoreCase) ? ["Preview"] : null))
                .ToList();
        }

        var configured = new List<(CaptureProcessingStepConfig Config, ICaptureProcessingStep Step)>(pipelineConfig.Count);
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var stepConfig in pipelineConfig)
            {
                var step = CreateStep(stepConfig, identifiers);
                if (step is ICaptureProcessingGraphStep { Enabled: false })
                {
                    (step as IDisposable)?.Dispose();
                    continue;
                }
                configured.Add((stepConfig, step));
            }

            InferLegacyDependencies(configured);

            var nodesById = configured.ToDictionary(
                static item => item.Step.Name,
                StringComparer.OrdinalIgnoreCase);
            foreach (var item in configured)
            {
                foreach (var dependency in item.Config.DependsOn ?? [])
                {
                    if (!nodesById.ContainsKey(dependency))
                    {
                        throw new InvalidOperationException(
                            $"Capture processing step '{item.Step.Name}' depends on missing step '{dependency}'.");
                    }
                    if (string.Equals(item.Step.Name, dependency, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Capture processing step '{item.Step.Name}' cannot depend on itself.");
                    }
                }
            }

            ValidateOutputs(config, configured, nodesById);
            var nodes = TopologicalSort(configured, nodesById);
            stopwatch.Stop();
            _telemetry.RecordValidation(nodes.Count, stopwatch.Elapsed);
            activity?.SetStatus(ActivityStatusCode.Ok);
            _logger.CaptureProcessingGraphValidated(nodes.Count);
            _logger.CaptureProcessingPipelineBuilt(nodes.Count);
            return new CaptureProcessingGraph(nodes);
        }
        catch
        {
            foreach (var disposable in configured.Select(static item => item.Step).OfType<IDisposable>())
            {
                disposable.Dispose();
            }
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    private static void InferLegacyDependencies(
        List<(CaptureProcessingStepConfig Config, ICaptureProcessingStep Step)> configured)
    {
        var legacyOrdered = configured
            .OrderBy(static item => item.Step.Order)
            .ThenBy(static item => item.Step.Name, StringComparer.Ordinal)
            .ToArray();
        for (var legacyIndex = 0; legacyIndex < legacyOrdered.Length; legacyIndex++)
        {
            var item = legacyOrdered[legacyIndex];
            if (item.Config.DependsOn is not null || item.Step is not ICaptureProcessingGraphStep graphStep)
            {
                continue;
            }
            var producer = legacyOrdered
                .Take(legacyIndex)
                .Where(candidate => candidate.Step is ICaptureProcessingGraphStep candidateGraph &&
                    graphStep.AcceptedInputRoles.Contains(candidateGraph.OutputRole))
                .OrderByDescending(static candidate => candidate.Step.Order)
                .ThenByDescending(static candidate => candidate.Step.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (producer.Step is not null)
            {
                var configuredIndex = configured.FindIndex(candidate => string.Equals(
                    candidate.Step.Name, item.Step.Name, StringComparison.OrdinalIgnoreCase));
                configured[configuredIndex] = (item.Config with { DependsOn = [producer.Step.Name] }, item.Step);
            }
        }
    }

    private static void ValidateOutputs(
        CameraModuleConfig config,
        List<(CaptureProcessingStepConfig Config, ICaptureProcessingStep Step)> configured,
        Dictionary<string, (CaptureProcessingStepConfig Config, ICaptureProcessingStep Step)> nodesById)
    {
        var outputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in configured)
        {
            if (item.Step is not ICaptureProcessingGraphStep graphStep)
            {
                continue;
            }
            var outputKey = $"{graphStep.OutputRole}\0{graphStep.OutputVariant}";
            if (!outputs.Add(outputKey))
            {
                throw new InvalidOperationException(
                    $"Capture processing graph declares duplicate output {graphStep.OutputRole}/{graphStep.OutputVariant} from recipe '{graphStep.RecipeName}'.");
            }

            var dependencies = item.Config.DependsOn ?? [];
            if (string.Equals(graphStep.RecipeName, HVO.SkyMonitor.Processing.BuiltInProcessingRecipes.RollingMean, StringComparison.Ordinal) &&
                config.Rig.Sensor.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
            {
                throw new InvalidOperationException(
                    $"Capture processing step '{item.Step.Name}' requires a linear 16-bit sensor input.");
            }
            if (graphStep.AcceptedInputRoles.Count == 0)
            {
                continue;
            }
            if (dependencies.Count == 0)
            {
                if (!graphStep.AcceptedInputRoles.Contains(FrameArtifactRole.Raw))
                {
                    throw new InvalidOperationException(
                        $"Capture processing step '{item.Step.Name}' requires one explicit producing dependency.");
                }
                continue;
            }
            var producers = dependencies.Select(dependency => nodesById[dependency].Step).ToArray();
            if (producers.Any(static dependency => dependency is not ICaptureProcessingGraphStep))
            {
                throw new InvalidOperationException(
                    $"Capture processing step '{item.Step.Name}' depends on a step that does not produce an artifact.");
            }
            var matching = producers
                .Cast<ICaptureProcessingGraphStep>()
                .Where(dependency => graphStep.AcceptedInputRoles.Contains(dependency.OutputRole))
                .ToArray();
            if (item.Step is ICompoundCaptureProcessingGraphStep compound)
            {
                var roles = matching.Select(static dependency => dependency.OutputRole).ToHashSet();
                if (matching.Length != producers.Length || matching.Length != compound.RequiredDependencyRoles.Count ||
                    !compound.RequiredDependencyRoles.SetEquals(roles))
                {
                    throw new InvalidOperationException(
                        $"Capture processing step '{item.Step.Name}' does not have its required compound inputs.");
                }
                continue;
            }
            if (matching.Length != 1 || matching.Length != producers.Length)
            {
                throw new InvalidOperationException(
                    $"Capture processing step '{item.Step.Name}' must have exactly one unambiguous compatible producer.");
            }
        }
    }

    private static List<CaptureProcessingGraphNode> TopologicalSort(
        List<(CaptureProcessingStepConfig Config, ICaptureProcessingStep Step)> configured,
        Dictionary<string, (CaptureProcessingStepConfig Config, ICaptureProcessingStep Step)> nodesById)
    {
        var remainingDependencies = configured.ToDictionary(
            static item => item.Step.Name,
            static item => new HashSet<string>(item.Config.DependsOn ?? [], StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var ordered = new List<CaptureProcessingGraphNode>(configured.Count);
        while (ordered.Count < configured.Count)
        {
            var ready = configured
                .Where(item => remainingDependencies.ContainsKey(item.Step.Name) && remainingDependencies[item.Step.Name].Count == 0)
                .OrderBy(static item => item.Step.Order)
                .ThenBy(static item => item.Step.Name, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
            {
                var cycle = string.Join(", ", remainingDependencies.Keys.Order(StringComparer.Ordinal));
                throw new InvalidOperationException($"Capture processing graph contains a dependency cycle involving: {cycle}.");
            }
            foreach (var item in ready)
            {
                var graphStep = item.Step as ICaptureProcessingGraphStep;
                var dependencies = item.Config.DependsOn?.ToArray() ?? [];
                ordered.Add(new CaptureProcessingGraphNode(
                    item.Step.Name,
                    item.Step,
                    dependencies,
                    item.Config.Required,
                    graphStep?.RecipeName,
                    graphStep?.OutputRole,
                    graphStep?.OutputVariant,
                    CaptureContractJson.ComputeCanonicalJsonSha256(
                        JsonSerializer.SerializeToElement(new
                        {
                            item.Config.Type,
                            id = item.Step.Name,
                            order = item.Step.Order,
                            dependencies,
                            item.Config.Required,
                            item.Config.Options,
                            recipe = graphStep?.RecipeName,
                            outputRole = graphStep?.OutputRole,
                            outputVariant = graphStep?.OutputVariant
                        }))));
                remainingDependencies.Remove(item.Step.Name);
                foreach (var unresolved in remainingDependencies.Values)
                {
                    unresolved.Remove(item.Step.Name);
                }
            }
        }
        return ordered;
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
