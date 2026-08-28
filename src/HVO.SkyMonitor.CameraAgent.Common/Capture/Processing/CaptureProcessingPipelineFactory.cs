using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Reflection;
using HVO.SkyMonitor.Processing;
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
    private readonly Dictionary<string, CaptureProcessingStepRegistration> _registrationsByCanonicalAlias = new(StringComparer.OrdinalIgnoreCase);
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
            if (_registrationsByCanonicalAlias.TryGetValue(registration.Alias, out var existing) &&
                existing != registration)
            {
                throw new InvalidOperationException(
                    $"Capture processing step alias '{registration.Alias}' has conflicting registrations.");
            }
            RegisterStep(registration);
            _registrationsByCanonicalAlias[registration.Alias] = registration;
        }
    }

    public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var pipeline = config.Pipeline;
        var schemaVersion = pipeline.SchemaVersion;
        var desired = pipeline.Steps.Select(static step => new CaptureProcessingPlanNode(
            string.IsNullOrWhiteSpace(step.Id) ? step.Type : step.Id.Trim(),
            step.Type,
            step.Enabled != false,
            step.Required,
            step.Order,
            step.Options,
            step.DependsOn?.ToArray(),
            null,
            null,
            null,
            step.Publication)).ToArray();
        var graph = CreateGraph(config);
        try
        {
            var configuredById = desired.ToDictionary(static node => node.Id, StringComparer.OrdinalIgnoreCase);
            var effective = graph.Nodes.Select(node => new CaptureProcessingPlanNode(
                node.Id,
                node.Alias ?? configuredById.GetValueOrDefault(node.Id)?.Alias ?? node.Step.GetType().Name,
                true,
                node.Required,
                node.EffectiveOrder,
                node.EffectiveOptions,
                node.DeclaredDependencies ?? node.Dependencies,
                node.RecipeName,
                node.OutputRole,
                node.OutputVariant,
                node.Publication)).ToArray();
            var projectedDesired = desired.Select(static node => node with
            {
                Options = RedactOptions(node.Options)
            }).ToArray();
            var projectedEffective = effective.Select(static node => node with
            {
                Options = RedactOptions(node.Options)
            }).ToArray();
            return new CaptureProcessingPlanPreview(
                schemaVersion,
                pipeline.DependencyPolicy,
                CaptureContractJson.ComputeCanonicalJsonSha256(CaptureContractJson.SerializeToElement(new
                {
                    SchemaVersion = schemaVersion,
                    pipeline.DependencyPolicy,
                    Nodes = desired
                })),
                CaptureContractJson.ComputeCanonicalJsonSha256(CaptureContractJson.SerializeToElement(new
                {
                    SchemaVersion = schemaVersion,
                    pipeline.DependencyPolicy,
                    Nodes = graph.Nodes.Select(static node => node.PlanSha256).ToArray()
                })),
                projectedDesired,
                projectedEffective);
        }
        finally
        {
            graph.DisposeSteps();
        }
    }

    public CaptureProcessingGraph CreateGraph(CameraModuleConfig config)
    {
        var stopwatch = Stopwatch.StartNew();
        using var activity = CaptureProcessingTelemetry.ActivitySource.StartActivity("processing-graph.validate");
        ArgumentNullException.ThrowIfNull(config);
        var explicitV2 = config.Pipeline is
        {
            SchemaVersion: CapturePipelineSchemaVersions.ExplicitV2,
            DependencyPolicy: CapturePipelineDependencyPolicy.RejectEnabledDependent
        };
        var legacyV1 = config.Pipeline is
        {
            SchemaVersion: CapturePipelineSchemaVersions.LegacyV1,
            DependencyPolicy: CapturePipelineDependencyPolicy.LegacyInference
        };
        if (config.Pipeline.SchemaVersion is not
            (CapturePipelineSchemaVersions.LegacyV1 or CapturePipelineSchemaVersions.ExplicitV2))
        {
            throw new InvalidOperationException(
                $"Unsupported capture pipeline schema '{config.Pipeline.SchemaVersion}'.");
        }
        if (!explicitV2 && !legacyV1)
        {
            throw new InvalidOperationException(
                $"Capture pipeline schema '{config.Pipeline.SchemaVersion}' cannot use dependency policy '{config.Pipeline.DependencyPolicy}'.");
        }
        var configuredSteps = config.Pipeline.Steps;
        var effectiveLayout = config.Rig.Readout is null
            ? null
            : SensorReadoutResolver.Resolve(config.Rig.Sensor, config.Rig.Readout).Layout;
        IReadOnlyList<CaptureProcessingStepConfig> pipelineConfig = configuredSteps;
        if (legacyV1 && configuredSteps.Any(static step => step.Enabled is not null))
        {
            throw new InvalidOperationException("The top-level enabled field is supported only by capture pipeline v2.");
        }
        if (legacyV1 && configuredSteps.Any(static step => step.Publication is not null))
        {
            throw new InvalidOperationException("Per-step publication policy is supported only by capture pipeline v2.");
        }
        if (explicitV2)
        {
            ValidateExplicitConfiguration(configuredSteps);
            pipelineConfig = configuredSteps.Where(static step => step.Enabled != false).ToArray();
        }
        if (legacyV1 && pipelineConfig.Count == 0 && _registrationsByType.Count > 0)
        {
            var effectivePixelFormat = effectiveLayout?.PixelFormat ?? config.Rig.Sensor.PixelFormat;
            pipelineConfig = _registrationsByType.Values
                .Where(registration => registration.AutoInclude &&
                    (effectivePixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 ||
                     registration.ImplementationType != typeof(CalibrationCaptureProcessingStep) &&
                     registration.ImplementationType != typeof(RollingCombinationCaptureProcessingStep)))
                .OrderBy(static registration => registration.DefaultOrder)
                .Select(static registration => new CaptureProcessingStepConfig(
                    registration.Alias,
                    registration.Alias,
                    registration.DefaultOrder,
                    null,
                    string.Equals(registration.Alias, "Annotation", StringComparison.OrdinalIgnoreCase)
                        ? ["Preview"]
                        : null))
                .ToArray();
        }

        var configured = new List<(CaptureProcessingStepConfig Config, ICaptureProcessingStep Step)>(pipelineConfig.Count);
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var stepConfig in pipelineConfig)
            {
                var step = CreateStep(stepConfig, identifiers, explicitV2, out var effectiveConfig);
                if (explicitV2 && effectiveConfig.Publication is not null &&
                    step is not ICaptureProcessingGraphStep)
                {
                    (step as IDisposable)?.Dispose();
                    throw new InvalidOperationException(
                        $"Capture pipeline v2 step '{step.Name}' cannot publish because it produces no artifact.");
                }
                if (step is ICaptureProcessingGraphStep { Enabled: false })
                {
                    (step as IDisposable)?.Dispose();
                    if (explicitV2)
                    {
                        throw new InvalidOperationException(
                            $"Capture processing step '{step.Name}' must use the top-level enabled field instead of an options-level enabled value.");
                    }
                    continue;
                }
                configured.Add((effectiveConfig, step));
            }

            if (legacyV1)
            {
                InferLegacyDependencies(configured);
            }

            var nodesById = configured.ToDictionary(
                static item => item.Step.Name,
                StringComparer.OrdinalIgnoreCase);
            foreach (var item in configured)
            {
                foreach (var dependency in item.Config.DependsOn ?? [])
                {
                    if (explicitV2 && IsRawDependency(dependency))
                    {
                        continue;
                    }
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

            ValidateOutputs(config, effectiveLayout, configured, nodesById, explicitV2);
            ValidateStoragePolicies(configured, nodesById, explicitV2);
            var nodes = TopologicalSort(configured, nodesById, explicitV2);
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

    private void ValidateExplicitConfiguration(IReadOnlyList<CaptureProcessingStepConfig> configuredSteps)
    {
        var byId = new Dictionary<string, CaptureProcessingStepConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in configuredSteps)
        {
            if (string.IsNullOrWhiteSpace(step.Type) || !_registrationsByCanonicalAlias.ContainsKey(step.Type))
            {
                throw new InvalidOperationException(
                    $"Capture pipeline v2 step type '{step.Type}' is not a registered stable alias.");
            }
            var id = string.IsNullOrWhiteSpace(step.Id) ? step.Type : step.Id.Trim();
            if (IsRawDependency(id))
            {
                throw new InvalidOperationException("'$raw' is reserved and cannot be used as a processing step identifier.");
            }
            if (!byId.TryAdd(id, step))
            {
                throw new InvalidOperationException($"Duplicate capture processing step identifier '{id}'.");
            }
            if (step.Options is { ValueKind: JsonValueKind.Object } options &&
                options.EnumerateObject().Any(static property => string.Equals(
                    property.Name, "enabled", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Capture pipeline v2 step '{id}' must use the top-level enabled field, not options.enabled.");
            }
            if (step.Publication is { } publication)
            {
                if (!Enum.IsDefined(publication.Persistence))
                {
                    throw new InvalidOperationException(
                        $"Capture pipeline v2 step '{id}' publication persistence mode is unsupported.");
                }
            }
        }

        foreach (var (id, step) in byId.Where(static item => item.Value.Enabled != false))
        {
            if (step.DependsOn is null || step.DependsOn.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Capture pipeline v2 step '{id}' must declare an explicit dependency, including '$raw' for a raw input.");
            }
            foreach (var dependency in step.DependsOn)
            {
                if (IsRawDependency(dependency))
                {
                    continue;
                }
                if (!byId.TryGetValue(dependency, out var producer))
                {
                    throw new InvalidOperationException(
                        $"Capture processing step '{id}' depends on missing step '{dependency}'.");
                }
                if (producer.Enabled == false)
                {
                    throw new InvalidOperationException(
                        $"Capture pipeline v2 step '{id}' depends on disabled step '{dependency}'.");
                }
            }
        }
    }

    private static void ValidateStoragePolicies(
        IReadOnlyList<(CaptureProcessingStepConfig Config, ICaptureProcessingStep Step)> configured,
        Dictionary<string, (CaptureProcessingStepConfig Config, ICaptureProcessingStep Step)> nodesById,
        bool explicitV2)
    {
        if (!explicitV2)
        {
            return;
        }
        if (configured.Any(static item =>
                item.Config.Publication?.Persistence == CaptureProcessingPersistenceMode.DurableLocal) &&
            !configured.Any(static item => item.Step is NoOpFileStorageProcessingStep))
        {
            throw new InvalidOperationException(
                "Durable processing outputs require an enabled Storage step to define retention ownership.");
        }
        foreach (var storage in configured.Where(static item => item.Step is NoOpFileStorageProcessingStep))
        {
            var options = ((NoOpFileStorageProcessingStep)storage.Step).ConfiguredOptions;
            var dependencyIds = storage.Config.DependsOn ?? [];
            var producerDependencies = dependencyIds
                .Where(nodesById.ContainsKey)
                .Select(id => (Id: id, Step: nodesById[id].Step as ICaptureProcessingGraphStep))
                .Where(static target => target.Step is not null)
                .ToArray();
            var producerOutputs = producerDependencies
                .SelectMany(static target => GetOutputs(target.Step!)
                    .Select(output => (target.Id, Step: target.Step!, Output: output)))
                .ToArray();
            foreach (var policy in options.Policies ?? [])
            {
                var targets = producerOutputs
                    .Where(target => policy.StepId is null || string.Equals(policy.StepId, target.Id, StringComparison.OrdinalIgnoreCase))
                    .Where(target => policy.Role is null || policy.Role == target.Output.Role)
                    .Where(target => policy.Variant is null || string.Equals(policy.Variant, target.Output.Variant, StringComparison.Ordinal))
                    .Where(target => policy.RecipeName is null || string.Equals(policy.RecipeName, target.Output.RecipeName, StringComparison.Ordinal))
                    .ToArray();
                var selectsRaw = dependencyIds.Any(IsRawDependency) &&
                    policy.StepId is null &&
                    (policy.Role is null or FrameArtifactRole.Raw) &&
                    policy.Variant is null && policy.RecipeName is null;
                if (targets.Length == 0 && !selectsRaw)
                {
                    throw new InvalidOperationException(
                        $"Storage policy does not select a declared producer dependency for step '{storage.Step.Name}'.");
                }
            }
            foreach (var target in producerOutputs.Where(target =>
                         target.Output.Role == FrameArtifactRole.Metadata ||
                         target.Step is JpegEncodingCaptureProcessingStep))
            {
                var policy = (options.Policies ?? [])
                    .Where(candidate => candidate.StepId is null || string.Equals(candidate.StepId, target.Id, StringComparison.OrdinalIgnoreCase))
                    .Where(candidate => candidate.Role is null || candidate.Role == target.Output.Role)
                    .Where(candidate => candidate.Variant is null || string.Equals(candidate.Variant, target.Output.Variant, StringComparison.Ordinal))
                    .Where(candidate => candidate.RecipeName is null || string.Equals(candidate.RecipeName, target.Output.RecipeName, StringComparison.Ordinal))
                    .OrderByDescending(static candidate =>
                        (candidate.StepId is null ? 0 : 1) + (candidate.Role is null ? 0 : 1) +
                        (candidate.Variant is null ? 0 : 1) + (candidate.RecipeName is null ? 0 : 1))
                    .FirstOrDefault();
                if (policy?.QueueForUpload ?? options.QueueForUpload)
                {
                    throw new InvalidOperationException(
                        $"Storage upload policy for step '{target.Id}' output '{target.Output.Variant}' cannot target a layoutless metadata or JPEG product.");
                }
            }
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
        FrameLayoutDescriptor? effectiveLayout,
        List<(CaptureProcessingStepConfig Config, ICaptureProcessingStep Step)> configured,
        Dictionary<string, (CaptureProcessingStepConfig Config, ICaptureProcessingStep Step)> nodesById,
        bool explicitV2)
    {
        var outputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in configured)
        {
            if (explicitV2 && item.Step is ICaptureProcessingArtifactConsumer consumer)
            {
                var consumerDependencies = item.Config.DependsOn ?? [];
                if ((consumerDependencies.Any(IsRawDependency) &&
                     !consumer.AcceptedDependencyRoles.Contains(FrameArtifactRole.Raw)) ||
                    consumerDependencies
                        .Where(static dependency => !IsRawDependency(dependency))
                        .Select(dependency => nodesById[dependency].Step)
                        .Any(dependency => dependency is not ICaptureProcessingGraphStep producer ||
                            !consumer.AcceptedDependencyRoles.Contains(producer.OutputRole)))
                {
                    throw new InvalidOperationException(
                        $"Capture processing step '{item.Step.Name}' declares an unsupported artifact dependency.");
                }
                continue;
            }
            if (explicitV2 && item.Step is ICaptureProcessingOutcomeConsumer)
            {
                if ((item.Config.DependsOn ?? []).Any(IsRawDependency))
                {
                    throw new InvalidOperationException(
                        $"Capture processing step '{item.Step.Name}' declares an unsupported outcome dependency.");
                }
                continue;
            }
            if (item.Step is not ICaptureProcessingGraphStep graphStep)
            {
                continue;
            }
            var advertisedOutputs = GetOutputs(graphStep);
            if (advertisedOutputs.Any(output => !outputs.Add($"{output.Role}\0{output.Variant}")))
            {
                throw new InvalidOperationException(
                    $"Capture processing graph declares duplicate output {graphStep.OutputRole}/{graphStep.OutputVariant} from recipe '{graphStep.RecipeName}'.");
            }

            var declaredDependencies = item.Config.DependsOn ?? [];
            var usesRaw = explicitV2 && declaredDependencies.Any(IsRawDependency);
            var dependencies = explicitV2
                ? declaredDependencies.Where(static dependency => !IsRawDependency(dependency)).ToArray()
                : declaredDependencies.ToArray();
            var effectiveFormat = effectiveLayout?.PixelFormat ?? config.Rig.Sensor.PixelFormat;
            if (RequiresLinear16(graphStep.RecipeName) &&
                effectiveFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
            {
                throw new InvalidOperationException(
                    $"Capture processing step '{item.Step.Name}' requires a linear 16-bit input, but the configured readout emits {effectiveFormat}.");
            }
            if (effectiveLayout is not null && dependencies.Length == 0 &&
                (!ProcessingStoredCodeIsSupported(effectiveLayout) ||
                 effectiveLayout.ContainerDepthBits > 8 && effectiveLayout.ByteOrder != FrameByteOrder.LittleEndian))
            {
                throw new InvalidOperationException(
                    $"Capture processing step '{item.Step.Name}' does not support the configured raw readout layout.");
            }
            if (graphStep.AcceptedInputRoles.Count == 0)
            {
                continue;
            }
            if (usesRaw)
            {
                if (dependencies.Length != 0 || !graphStep.AcceptedInputRoles.Contains(FrameArtifactRole.Raw))
                {
                    throw new InvalidOperationException(
                        $"Capture processing step '{item.Step.Name}' declares an incompatible '$raw' dependency.");
                }
                continue;
            }
            if (dependencies.Length == 0)
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
            if (item.Step is IRequiredCaptureProcessingDependencies required)
            {
                if (!TryAssignRequiredDependencies(matching, required.DependencyRequirements, out _))
                {
                    throw new InvalidOperationException(
                        $"Capture processing step '{item.Step.Name}' does not have its required dependency inputs.");
                }
                continue;
            }
            if (item.Step is ICompoundCaptureProcessingGraphStep compound)
            {
                if (matching.Length != producers.Length ||
                    matching.Length != compound.RequiredDependencyRoleGroups.Count ||
                    compound.RequiredDependencyRoleGroups.Any(group =>
                        matching.Count(dependency => group.Contains(dependency.OutputRole)) != 1) ||
                    compound.RequiredDependencyRecipes.Any(requirement =>
                        matching.Count(dependency => dependency.OutputRole == requirement.Key &&
                            requirement.Value.Contains(dependency.RecipeName)) != 1))
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

    private static bool TryAssignRequiredDependencies(
        ICaptureProcessingGraphStep[] dependencies,
        IReadOnlyList<CaptureProcessingDependencyRequirement> requirements,
        out IReadOnlySet<int> optionalDependencyIndexes)
    {
        optionalDependencyIndexes = new HashSet<int>();
        if (requirements.Any(static requirement => requirement.Roles is null || requirement.Roles.Count == 0 ||
                requirement.Variant is { Length: 0 or > 128 }) ||
            dependencies.Any(dependency => GetOutputs(dependency).Count == 0))
        {
            return false;
        }
        var products = dependencies.SelectMany((dependency, dependencyIndex) => GetOutputs(dependency)
            .Select(output => (output, dependencyIndex))).ToArray();
        var candidates = requirements.Select(requirement => products
            .Select((product, index) => (product, index))
            .Where(item => requirement.Roles.Contains(item.product.output.Role) &&
                (requirement.RecipeNames is null || requirement.RecipeNames.Contains(item.product.output.RecipeName)) &&
                (requirement.SchemaVersions is null || item.product.output.SchemaVersion is { } schemaVersion &&
                    requirement.SchemaVersions.Contains(schemaVersion)) &&
                (requirement.Variant is null || string.Equals(requirement.Variant, item.product.output.Variant, StringComparison.Ordinal)))
            .Select(static item => item.index)
            .ToArray()).ToArray();
        if (requirements.Select((requirement, index) => (requirement, index))
            .Any(item => item.requirement.Required && candidates[item.index].Length == 0))
        {
            return false;
        }
        var assignedProducts = new bool[products.Length];
        var selectedProducts = new int?[requirements.Count];
        if (!Assign(0)) return false;
        var usedDependencies = selectedProducts.Where(static value => value.HasValue)
            .Select(value => products[value!.Value].dependencyIndex).ToHashSet();
        if (usedDependencies.Count != dependencies.Length) return false;
        var requiredDependencies = requirements.Select((requirement, index) => (requirement, index))
            .Where(item => item.requirement.Required && selectedProducts[item.index].HasValue)
            .Select(item => products[selectedProducts[item.index]!.Value].dependencyIndex).ToHashSet();
        optionalDependencyIndexes = usedDependencies.Where(index => !requiredDependencies.Contains(index)).ToHashSet();
        return true;

        bool Assign(int requirementIndex)
        {
            if (requirementIndex == requirements.Count)
            {
                return true;
            }
            foreach (var dependencyIndex in candidates[requirementIndex])
            {
                if (assignedProducts[dependencyIndex])
                {
                    continue;
                }
                assignedProducts[dependencyIndex] = true;
                selectedProducts[requirementIndex] = dependencyIndex;
                if (Assign(requirementIndex + 1))
                {
                    return true;
                }
                selectedProducts[requirementIndex] = null;
                assignedProducts[dependencyIndex] = false;
            }
            if (!requirements[requirementIndex].Required && Assign(requirementIndex + 1))
            {
                return true;
            }
            return false;
        }
    }

    private static IReadOnlyList<CaptureProcessingOutputDescriptor> GetOutputs(ICaptureProcessingGraphStep step)
        => step is IMultiOutputCaptureProcessingGraphStep multi
            ? multi.Outputs
            : [new(step.OutputRole, step.OutputVariant, step.RecipeName, step.OutputSchemaVersion)];

    private static bool RequiresLinear16(string recipeName)
        => recipeName is HVO.SkyMonitor.Processing.BuiltInProcessingRecipes.RollingMean or
            HVO.SkyMonitor.Processing.BuiltInProcessingRecipes.ReferenceCalibration or
            HVO.SkyMonitor.Processing.BuiltInProcessingRecipes.CloudAssessment;

    private static bool ProcessingStoredCodeIsSupported(FrameLayoutDescriptor layout)
        => layout.StoredCodeTransform switch
        {
            FrameStoredCodeTransform.IdentityV1 or FrameStoredCodeTransform.RightAlignedV1 => true,
            FrameStoredCodeTransform.LeftShiftedV1 or FrameStoredCodeTransform.FullRangeScaledV1 =>
                layout.LevelCodeSpace == FrameLevelCodeSpace.StoredContainer,
            _ => false
        };

    private static bool IsRawDependency(string dependency)
        => string.Equals(dependency, "$raw", StringComparison.OrdinalIgnoreCase);

    private static List<CaptureProcessingGraphNode> TopologicalSort(
        List<(CaptureProcessingStepConfig Config, ICaptureProcessingStep Step)> configured,
        Dictionary<string, (CaptureProcessingStepConfig Config, ICaptureProcessingStep Step)> nodesById,
        bool explicitV2)
    {
        var remainingDependencies = configured.ToDictionary(
            static item => item.Step.Name,
            item => new HashSet<string>(
                explicitV2
                    ? (item.Config.DependsOn ?? []).Where(static dependency => !IsRawDependency(dependency))
                    : item.Config.DependsOn ?? [],
                StringComparer.OrdinalIgnoreCase),
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
                var dependencies = explicitV2
                    ? item.Config.DependsOn?.Where(static dependency => !IsRawDependency(dependency)).ToArray() ?? []
                    : item.Config.DependsOn?.ToArray() ?? [];
                IReadOnlySet<string>? optionalDependencies = null;
                if (item.Step is IRequiredCaptureProcessingDependencies required && graphStep is not null)
                {
                    var producerSteps = dependencies.Select(dependency =>
                        (ICaptureProcessingGraphStep)nodesById[dependency].Step).ToArray();
                    if (!TryAssignRequiredDependencies(producerSteps, required.DependencyRequirements, out var optionalIndexes))
                        throw new InvalidOperationException($"Capture processing step '{item.Step.Name}' dependency assignment changed during ordering.");
                    optionalDependencies = optionalIndexes.Select(index => dependencies[index]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                ordered.Add(new CaptureProcessingGraphNode(
                    item.Step.Name,
                    item.Step,
                    dependencies,
                    item.Config.Required,
                    graphStep?.RecipeName,
                    graphStep?.OutputRole,
                    graphStep?.OutputVariant,
                    ComputeNodePlanSha256(
                        item.Config,
                        item.Step,
                        graphStep,
                        item.Config.DependsOn?.ToArray() ?? dependencies),
                    item.Config.Type,
                    item.Step.Order,
                    item.Config.Options,
                    item.Config.DependsOn?.ToArray(),
                    item.Config.Publication,
                    optionalDependencies));
                remainingDependencies.Remove(item.Step.Name);
                foreach (var unresolved in remainingDependencies.Values)
                {
                    unresolved.Remove(item.Step.Name);
                }
            }
        }
        return ordered;
    }

    private static string ComputeNodePlanSha256(
        CaptureProcessingStepConfig config,
        ICaptureProcessingStep step,
        ICaptureProcessingGraphStep? graphStep,
        IReadOnlyList<string> dependencies)
    {
        var outputs = graphStep is null ? [] : GetOutputs(graphStep).Select(static output => new
        {
            output.Role,
            output.Variant,
            output.RecipeName,
            output.SchemaVersion
        }).ToArray();
        var dependencyRequirements = step is IRequiredCaptureProcessingDependencies required
            ? required.DependencyRequirements.Select(static requirement => new
            {
                Roles = requirement.Roles.Order().ToArray(),
                RecipeNames = requirement.RecipeNames?.Order(StringComparer.Ordinal).ToArray(),
                SchemaVersions = requirement.SchemaVersions?.Order(StringComparer.Ordinal).ToArray(),
                requirement.Variant,
                requirement.Required
            }).ToArray()
            : null;
        var plan = config.Publication is null
            ? JsonSerializer.SerializeToElement(new
            {
                config.Type,
                id = step.Name,
                order = step.Order,
                dependencies,
                config.Required,
                config.Options,
                recipe = graphStep?.RecipeName,
                recipeIdentitySchema = graphStep is null ? null : ProcessingIdentity.BoundInputSchemaVersion,
                outputRole = graphStep?.OutputRole,
                outputVariant = graphStep?.OutputVariant,
                outputSchemaVersion = graphStep?.OutputSchemaVersion,
                outputs,
                dependencyRequirements
            })
            : CaptureContractJson.SerializeToElement(new
            {
                config.Type,
                id = step.Name,
                order = step.Order,
                dependencies,
                config.Required,
                config.Options,
                recipe = graphStep?.RecipeName,
                recipeIdentitySchema = graphStep is null ? null : ProcessingIdentity.BoundInputSchemaVersion,
                outputRole = graphStep?.OutputRole,
                outputVariant = graphStep?.OutputVariant,
                outputSchemaVersion = graphStep?.OutputSchemaVersion,
                outputs,
                dependencyRequirements,
                config.Publication
            });
        return CaptureContractJson.ComputeCanonicalJsonSha256(plan);
    }

    private ICaptureProcessingStep CreateStep(
        CaptureProcessingStepConfig config,
        HashSet<string> identifiers,
        bool aliasesOnly,
        out CaptureProcessingStepConfig effectiveConfig)
    {
        var registration = ResolveRegistration(config.Type, aliasesOnly);

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
        var effectiveOptions = SerializeEffectiveOptions(options, aliasesOnly);
        effectiveConfig = aliasesOnly
            ? config with
            {
                Type = registration.Alias,
                Id = id,
                Order = order,
                Options = effectiveOptions,
                Enabled = config.Enabled != false
            }
            : config;
        return (ICaptureProcessingStep)ActivatorUtilities.CreateInstance(
            _serviceProvider,
            registration.ImplementationType,
            metadata,
            options);
    }

    private static JsonElement SerializeEffectiveOptions(object options, bool omitLegacyEnabled)
    {
        var serialized = CaptureContractJson.SerializeToElement(options);
        if (!omitLegacyEnabled || serialized.ValueKind is not JsonValueKind.Object)
        {
            return serialized;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in serialized.EnumerateObject())
            {
                if (!string.Equals(property.Name, "enabled", StringComparison.OrdinalIgnoreCase))
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement? RedactOptions(JsonElement? options)
    {
        if (options is null)
        {
            return null;
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteElement(options.Value, writer);
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();

        static void WriteElement(JsonElement element, Utf8JsonWriter writer)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveName(property.Name))
                    {
                        writer.WriteStringValue("[redacted]");
                    }
                    else
                    {
                        WriteElement(property.Value, writer);
                    }
                }
                writer.WriteEndObject();
                return;
            }
            if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(item, writer);
                }
                writer.WriteEndArray();
                return;
            }
            element.WriteTo(writer);
        }

        static bool IsSensitiveName(string name)
            => name.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("root", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("endpoint", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("url", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("uri", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("key", StringComparison.OrdinalIgnoreCase);
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

    private CaptureProcessingStepRegistration ResolveRegistration(string typeName, bool aliasesOnly)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new InvalidOperationException("Capture processing step type is required.");
        }

        var registrations = aliasesOnly ? _registrationsByCanonicalAlias : _registrationsByAlias;
        if (registrations.TryGetValue(typeName, out var registration))
        {
            return registration;
        }

        if (aliasesOnly)
        {
            throw new InvalidOperationException(
                $"Capture pipeline v2 step type '{typeName}' is not a registered stable alias.");
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
