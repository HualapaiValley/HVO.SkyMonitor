using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

public static class ProcessingGraphCompiler
{
    public const int MaximumSources = 64;
    public const int MaximumNodes = 1024;
    public const int MaximumContractsPerNode = 64;
    public const int MaximumOptionsBytes = 65_536;

    private const int MaximumIdentityLength = 128;
    private const int MaximumBindingSearchStates = 100_000;

    public static ProcessingGraphCompilationResult Compile(
        ProcessingGraphDefinition definition,
        ProcessingGraphCompilationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        context ??= ProcessingGraphCompilationContext.Portable;
        var diagnostics = new List<ProcessingGraphDiagnostic>();
        ValidateDefinition(definition, context, diagnostics);
        if (diagnostics.Count > 0)
        {
            return Failure(diagnostics);
        }

        var enabledNodes = definition.Nodes.Where(static node => node.Enabled).ToArray();
        var nodesById = definition.Nodes.ToDictionary(static node => node.Id, StringComparer.Ordinal);
        var sourcesById = definition.Sources.ToDictionary(static source => source.Id, StringComparer.Ordinal);
        var remainingDependencies = enabledNodes.ToDictionary(
            static node => node.Id,
            node => node.Dependencies
                .Where(dependency => nodesById.ContainsKey(dependency.ProducerId))
                .Select(static dependency => dependency.ProducerId)
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var ordered = new List<ProcessingGraphNodeDefinition>(enabledNodes.Length);
        while (ordered.Count < enabledNodes.Length)
        {
            var ready = enabledNodes
                .Where(node => remainingDependencies.TryGetValue(node.Id, out var dependencies) && dependencies.Count == 0)
                .OrderBy(static node => node.Order)
                .ThenBy(static node => node.Id, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
            {
                var cycle = remainingDependencies.Keys.Order(StringComparer.Ordinal).ToArray();
                diagnostics.Add(new(
                    ProcessingGraphReasonCodes.Cycle,
                    "nodes",
                    $"Processing graph contains a dependency cycle involving: {string.Join(", ", cycle)}."));
                return Failure(diagnostics);
            }

            foreach (var node in ready)
            {
                ordered.Add(node);
                remainingDependencies.Remove(node.Id);
                foreach (var unresolved in remainingDependencies.Values)
                {
                    unresolved.Remove(node.Id);
                }
            }
        }

        var sourceIdentities = definition.Sources.ToDictionary(
            static source => source.Id,
            source => CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                schema = "hvo-processing-graph-source-v1",
                source.Id,
                outputs = source.Outputs.Select(ProcessingGraphJson.CanonicalOutput).ToArray()
            }),
            StringComparer.Ordinal);
        var planNodes = ImmutableArray.CreateBuilder<ProcessingGraphPlanNode>(ordered.Count);
        var nodeIdentities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in ordered)
        {
            var products = new List<(
                string ProducerId,
                int OutputIndex,
                ProcessingGraphProductContract Output,
                ProcessingGraphDependencyKind Kind,
                bool Required)>();
            foreach (var dependency in node.Dependencies.Where(static dependency => IsProductDependency(dependency.Kind)))
            {
                var outputs = sourcesById.TryGetValue(dependency.ProducerId, out var source)
                    ? source.Outputs
                    : nodesById[dependency.ProducerId].Outputs;
                products.AddRange(outputs.Select((output, outputIndex) =>
                    (dependency.ProducerId, outputIndex, output, dependency.Kind, dependency.Required)));
            }

            var bindings = BindInputs(node, products, diagnostics);
            if (bindings.IsDefault)
            {
                return Failure(diagnostics);
            }
            var dependencyIdentities = node.Dependencies.Select(dependency => new
            {
                dependency.ProducerId,
                kind = dependency.Kind.ToString(),
                dependency.Required,
                producerIdentitySha256 = sourceIdentities.GetValueOrDefault(dependency.ProducerId)
                    ?? nodeIdentities[dependency.ProducerId]
            }).ToArray();
            var identity = CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                schema = "hvo-processing-graph-node-plan-v1",
                node.Id,
                node.StepAlias,
                node.StepVersion,
                operationKind = node.OperationKind.ToString(),
                failurePolicy = node.FailurePolicy.ToString(),
                node.Order,
                effectiveOptions = CaptureContractJson.Canonicalize(node.EffectiveOptions),
                dependencies = dependencyIdentities,
                inputs = node.Inputs.Select(ProcessingGraphJson.CanonicalInput).ToArray(),
                outputs = node.Outputs.Select(ProcessingGraphJson.CanonicalOutput).ToArray(),
                window = ProcessingGraphJson.CanonicalWindow(node.Window),
                capabilityLabels = ProcessingGraphJson.SortDistinct(node.CapabilityLabels),
                hostApplicability = ProcessingGraphJson.SortDistinct(node.HostApplicability)
            });
            nodeIdentities.Add(node.Id, identity);
            planNodes.Add(new(node, identity, bindings));
        }

        if (diagnostics.Count > 0)
        {
            return Failure(diagnostics);
        }

        var canonicalDefinition = ProcessingGraphJson.CreateCanonicalDefinition(definition);
        var definitionIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(canonicalDefinition);
        var planIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "hvo-processing-graph-plan-v1",
            sources = definition.Sources.OrderBy(static source => source.Id, StringComparer.Ordinal)
                .Select(source => sourceIdentities[source.Id]).ToArray(),
            nodes = planNodes.Select(static node => node.IdentitySha256).ToArray()
        });
        return new(new(
            definitionIdentity,
            planIdentity,
            canonicalDefinition,
            definition.Sources.OrderBy(static source => source.Id, StringComparer.Ordinal).ToImmutableArray(),
            planNodes.MoveToImmutable()), []);
    }

    private static void ValidateDefinition(
        ProcessingGraphDefinition definition,
        ProcessingGraphCompilationContext context,
        List<ProcessingGraphDiagnostic> diagnostics)
    {
        if (!string.Equals(definition.SchemaVersion, ProcessingGraphSchemaVersions.Current, StringComparison.Ordinal))
        {
            Add(diagnostics, ProcessingGraphReasonCodes.UnsupportedSchema, "schemaVersion",
                $"Processing graph schema '{definition.SchemaVersion}' is unsupported.");
        }
        ValidateIdentity(definition.Name, "name", diagnostics);
        ValidateIdentity(definition.Revision, "revision", diagnostics);
        if (definition.Sources.IsDefault || definition.Nodes.IsDefault)
        {
            Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, "$", "Processing graph collections are required.");
            return;
        }
        if (!ValidateShape(definition, diagnostics))
        {
            return;
        }
        if (definition.Sources.Length > MaximumSources)
        {
            Add(diagnostics, ProcessingGraphReasonCodes.LimitExceeded, "sources",
                $"Processing graph exceeds the {MaximumSources} source limit.");
        }
        if (definition.Nodes.Length > MaximumNodes)
        {
            Add(diagnostics, ProcessingGraphReasonCodes.LimitExceeded, "nodes",
                $"Processing graph exceeds the {MaximumNodes} node limit.");
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        for (var sourceIndex = 0; sourceIndex < definition.Sources.Length; sourceIndex++)
        {
            var source = definition.Sources[sourceIndex];
            var path = $"sources[{sourceIndex}]";
            ValidateIdentity(source.Id, $"{path}.id", diagnostics);
            if (!identifiers.Add(source.Id))
            {
                Add(diagnostics, ProcessingGraphReasonCodes.DuplicateIdentifier, $"{path}.id",
                    $"Processing graph identifier '{source.Id}' is duplicated.");
            }
            ValidateOutputs(source.Outputs, $"{path}.outputs", diagnostics);
        }

        var nodesById = new Dictionary<string, ProcessingGraphNodeDefinition>(StringComparer.Ordinal);
        for (var nodeIndex = 0; nodeIndex < definition.Nodes.Length; nodeIndex++)
        {
            var node = definition.Nodes[nodeIndex];
            var path = $"nodes[{nodeIndex}]";
            ValidateIdentity(node.Id, $"{path}.id", diagnostics);
            ValidateIdentity(node.StepAlias, $"{path}.stepAlias", diagnostics);
            ValidateIdentity(node.StepVersion, $"{path}.stepVersion", diagnostics);
            if (!Enum.IsDefined(node.OperationKind))
            {
                Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, $"{path}.operationKind",
                    "Processing graph node operation kind is invalid.");
            }
            if (!Enum.IsDefined(node.FailurePolicy))
            {
                Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, $"{path}.failurePolicy",
                    "Processing graph node failure policy is invalid.");
            }
            if (ValidIdentity(node.Id) && (!identifiers.Add(node.Id) || !nodesById.TryAdd(node.Id, node)))
            {
                Add(diagnostics, ProcessingGraphReasonCodes.DuplicateIdentifier, $"{path}.id",
                    $"Processing graph identifier '{node.Id}' is duplicated.");
            }
            if (node.EffectiveOptions.ValueKind != JsonValueKind.Object)
            {
                Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, $"{path}.effectiveOptions",
                    $"Processing graph node '{node.Id}' options must be a JSON object.");
            }
            else if (JsonSerializer.SerializeToUtf8Bytes(node.EffectiveOptions).Length > MaximumOptionsBytes)
            {
                Add(diagnostics, ProcessingGraphReasonCodes.LimitExceeded, $"{path}.effectiveOptions",
                    $"Processing graph node '{node.Id}' options exceed {MaximumOptionsBytes} bytes.");
            }
            ValidateCount(node.Dependencies, $"{path}.dependencies", diagnostics);
            ValidateCount(node.Inputs, $"{path}.inputs", diagnostics);
            ValidateCount(node.Outputs, $"{path}.outputs", diagnostics);
            for (var dependencyIndex = 0; dependencyIndex < node.Dependencies.Length; dependencyIndex++)
            {
                var dependency = node.Dependencies[dependencyIndex];
                ValidateIdentity(dependency.ProducerId, $"{path}.dependencies[{dependencyIndex}].producerId", diagnostics);
                if (!Enum.IsDefined(dependency.Kind))
                {
                    Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode,
                        $"{path}.dependencies[{dependencyIndex}].kind",
                        "Processing graph dependency kind is invalid.");
                }
            }
            ValidateOutputs(node.Outputs, $"{path}.outputs", diagnostics);
            ValidateInputs(node.Inputs, $"{path}.inputs", diagnostics);
            ValidateWindow(node.Window, $"{path}.window", diagnostics);
            if ((node.OperationKind == ProcessingOperationKind.Window) != (node.Window is not null))
            {
                Add(diagnostics, ProcessingGraphReasonCodes.InvalidWindow, $"{path}.window",
                    "Processing graph window operations must declare exactly one window requirement.");
            }
            ValidateLabels(node.CapabilityLabels, $"{path}.capabilityLabels", diagnostics);
            ValidateLabels(node.HostApplicability, $"{path}.hostApplicability", diagnostics);
        }

        if (diagnostics.Count == 0 && ProcessingGraphJson.ExceedsMaximumDocumentBytes(definition))
        {
            Add(diagnostics, ProcessingGraphReasonCodes.LimitExceeded, "$",
                $"Processing graph JSON exceeds {ProcessingGraphJson.MaximumDocumentBytes} bytes.");
        }
        if (diagnostics.Count > 0)
        {
            return;
        }

        var sourceIds = definition.Sources.Select(static source => source.Id).ToHashSet(StringComparer.Ordinal);
        var effectiveOutputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in definition.Sources)
        {
            AddOutputKeys(source.Id, source.Outputs, effectiveOutputs, diagnostics);
        }
        for (var nodeIndex = 0; nodeIndex < definition.Nodes.Length; nodeIndex++)
        {
            var node = definition.Nodes[nodeIndex];
            if (!node.Enabled)
            {
                continue;
            }
            var path = $"nodes[{nodeIndex}]";
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in node.Dependencies)
            {
                if (!dependencies.Add(dependency.ProducerId))
                {
                    Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, $"{path}.dependencies",
                        $"Processing graph node '{node.Id}' repeats dependency '{dependency.ProducerId}'.");
                }
                if (string.Equals(node.Id, dependency.ProducerId, StringComparison.Ordinal))
                {
                    Add(diagnostics, ProcessingGraphReasonCodes.Cycle, $"{path}.dependencies",
                        $"Processing graph node '{node.Id}' cannot depend on itself.");
                }
                else if (!sourceIds.Contains(dependency.ProducerId))
                {
                    if (!nodesById.TryGetValue(dependency.ProducerId, out var producer))
                    {
                        Add(diagnostics, ProcessingGraphReasonCodes.MissingProducer, $"{path}.dependencies",
                            $"Processing graph node '{node.Id}' depends on missing producer '{dependency.ProducerId}'.");
                    }
                    else if (!producer.Enabled)
                    {
                        Add(diagnostics, ProcessingGraphReasonCodes.DisabledProducer, $"{path}.dependencies",
                            $"Processing graph node '{node.Id}' depends on disabled producer '{dependency.ProducerId}'.");
                    }
                }
            }
            AddOutputKeys(node.Id, node.Outputs, effectiveOutputs, diagnostics);
            ValidateContext(node, context, path, diagnostics);
        }
    }

    private static bool ValidateShape(
        ProcessingGraphDefinition definition,
        List<ProcessingGraphDiagnostic> diagnostics)
    {
        var valid = true;
        for (var sourceIndex = 0; sourceIndex < definition.Sources.Length; sourceIndex++)
        {
            var source = definition.Sources[sourceIndex];
            if (source is null)
            {
                Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, $"sources[{sourceIndex}]",
                    "Processing graph source cannot be null.");
                valid = false;
                continue;
            }
            valid &= ValidateArrayShape(source.Outputs, $"sources[{sourceIndex}].outputs", diagnostics);
        }
        for (var nodeIndex = 0; nodeIndex < definition.Nodes.Length; nodeIndex++)
        {
            var node = definition.Nodes[nodeIndex];
            if (node is null)
            {
                Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, $"nodes[{nodeIndex}]",
                    "Processing graph node cannot be null.");
                valid = false;
                continue;
            }
            valid &= ValidateArrayShape(node.Dependencies, $"nodes[{nodeIndex}].dependencies", diagnostics);
            valid &= ValidateArrayShape(node.Inputs, $"nodes[{nodeIndex}].inputs", diagnostics);
            valid &= ValidateArrayShape(node.Outputs, $"nodes[{nodeIndex}].outputs", diagnostics);
            if (node.CapabilityLabels.IsDefault || node.HostApplicability.IsDefault)
            {
                Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, $"nodes[{nodeIndex}]",
                    "Processing graph node label collections are required.");
                valid = false;
            }
            if (node.Window is { } window &&
                (window.RequiredPositions.IsDefault || window.CompatibilityLabels.IsDefault))
            {
                Add(diagnostics, ProcessingGraphReasonCodes.InvalidWindow, $"nodes[{nodeIndex}].window",
                    "Processing graph window collections are required.");
                valid = false;
            }
            foreach (var input in node.Inputs.IsDefault ? [] : node.Inputs)
            {
                if (input is null || input.Roles.IsDefault || input.ProductKinds.IsDefault ||
                    input.Variants.IsDefault || input.RecipeNames.IsDefault || input.SchemaVersions.IsDefault)
                {
                    Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, $"nodes[{nodeIndex}].inputs",
                        "Processing graph input and selector collections cannot be null or missing.");
                    valid = false;
                }
            }
        }
        return valid;
    }

    private static bool ValidateArrayShape<T>(
        ImmutableArray<T> values,
        string path,
        List<ProcessingGraphDiagnostic> diagnostics)
    {
        if (values.IsDefault || values.Length > MaximumContractsPerNode || values.Any(static value => value is null))
        {
            Add(diagnostics, ProcessingGraphReasonCodes.LimitExceeded, path,
                $"Processing graph collection is missing, contains null, or exceeds {MaximumContractsPerNode} entries.");
            return false;
        }
        return true;
    }

    private static ImmutableArray<ProcessingGraphInputBinding> BindInputs(
        ProcessingGraphNodeDefinition node,
        List<(
            string ProducerId,
            int OutputIndex,
            ProcessingGraphProductContract Output,
            ProcessingGraphDependencyKind Kind,
            bool Required)> products,
        List<ProcessingGraphDiagnostic> diagnostics)
    {
        var artifactDependencies = node.Dependencies
            .Where(static dependency => IsProductDependency(dependency.Kind))
            .ToArray();
        if (artifactDependencies.Length == 0 && node.Inputs.Length == 0)
        {
            return [];
        }

        var candidates = node.Inputs.Select(input => products
            .Select((product, productIndex) => (product, productIndex))
            .Where(candidate => Matches(input, candidate.product.Output) &&
                InputKindMatchesDependency(input.BindingKind, candidate.product.Kind) &&
                (!input.Required || candidate.product.Required))
            .Select(static candidate => candidate.productIndex)
            .ToArray()).ToArray();
        var missingRequiredInput = node.Inputs.Select((input, inputIndex) => (input, inputIndex))
            .FirstOrDefault(item => item.input.Required && candidates[item.inputIndex].Length == 0);
        if (missingRequiredInput.input is not null)
        {
            var optionalMatch = products.Any(candidate => !candidate.Required &&
                Matches(missingRequiredInput.input, candidate.Output) &&
                InputKindMatchesDependency(missingRequiredInput.input.BindingKind, candidate.Kind));
            if (optionalMatch)
            {
                Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, $"nodes[{node.Id}].dependencies",
                    $"Processing graph node '{node.Id}' cannot bind required input '{missingRequiredInput.input.BindingName}' to an optional producer.");
                return default;
            }
            Add(diagnostics, ProcessingGraphReasonCodes.IncompatibleInput, $"nodes[{node.Id}].inputs",
                $"Processing graph node '{node.Id}' does not have all required compatible inputs.");
            return default;
        }

        var assignedProducts = new bool[products.Count];
        var selectedProducts = new int?[node.Inputs.Length];
        var solutions = new List<int?[]>(2);
        var searchStates = 0;
        var searchLimitExceeded = false;
        Assign(0);
        if (searchLimitExceeded)
        {
            Add(diagnostics, ProcessingGraphReasonCodes.LimitExceeded, $"nodes[{node.Id}].inputs",
                $"Processing graph node '{node.Id}' input binding exceeds the bounded search limit.");
            return default;
        }
        if (solutions.Count == 0)
        {
            Add(diagnostics, ProcessingGraphReasonCodes.IncompatibleInput, $"nodes[{node.Id}].inputs",
                $"Processing graph node '{node.Id}' cannot bind its declared artifact dependencies.");
            return default;
        }
        if (solutions.Count > 1)
        {
            Add(diagnostics, ProcessingGraphReasonCodes.AmbiguousInput, $"nodes[{node.Id}].inputs",
                $"Processing graph node '{node.Id}' has ambiguous compatible producers.");
            return default;
        }

        var solution = solutions[0];
        var usedProducerIds = solution.Where(static selection => selection.HasValue)
            .Select(selection => products[selection!.Value].ProducerId)
            .ToHashSet(StringComparer.Ordinal);
        var unboundRequired = artifactDependencies
            .Where(static dependency => dependency.Required)
            .FirstOrDefault(dependency => !usedProducerIds.Contains(dependency.ProducerId));
        if (unboundRequired is not null)
        {
            Add(diagnostics, ProcessingGraphReasonCodes.IncompatibleInput, $"nodes[{node.Id}].dependencies",
                $"Processing graph node '{node.Id}' does not consume required producer '{unboundRequired.ProducerId}'.");
            return default;
        }

        var contradictoryBinding = solution.Select((selection, inputIndex) => (selection, inputIndex))
            .Where(static item => item.selection.HasValue)
            .Select(item => new
            {
                Input = node.Inputs[item.inputIndex],
                Dependency = products[item.selection!.Value]
            })
            .FirstOrDefault(static item => item.Input.Required && !item.Dependency.Required);
        if (contradictoryBinding is not null)
        {
            Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, $"nodes[{node.Id}].dependencies",
                $"Processing graph node '{node.Id}' cannot bind a required input to optional producer '{contradictoryBinding.Dependency.ProducerId}'.");
            return default;
        }

        return solution.Select((selection, inputIndex) => (selection, inputIndex))
            .Where(static item => item.selection.HasValue)
            .Select(item => new ProcessingGraphInputBinding(
                item.inputIndex,
                node.Inputs[item.inputIndex].BindingName,
                node.Inputs[item.inputIndex].BindingKind,
                products[item.selection!.Value].ProducerId,
                products[item.selection.Value].OutputIndex,
                node.Inputs[item.inputIndex].Required))
            .ToImmutableArray();

        void Assign(int inputIndex)
        {
            searchStates++;
            if (searchStates > MaximumBindingSearchStates)
            {
                searchLimitExceeded = true;
                return;
            }
            if (searchLimitExceeded || solutions.Count > 1)
            {
                return;
            }
            if (inputIndex == node.Inputs.Length)
            {
                var used = selectedProducts.Where(static value => value.HasValue)
                    .Select(value => products[value!.Value].ProducerId)
                    .ToHashSet(StringComparer.Ordinal);
                if (artifactDependencies.Where(static dependency => dependency.Required)
                    .All(dependency => used.Contains(dependency.ProducerId)))
                {
                    solutions.Add((int?[])selectedProducts.Clone());
                }
                return;
            }
            var solutionCountBeforeCandidates = solutions.Count;
            foreach (var productIndex in candidates[inputIndex])
            {
                if (assignedProducts[productIndex])
                {
                    continue;
                }
                assignedProducts[productIndex] = true;
                selectedProducts[inputIndex] = productIndex;
                Assign(inputIndex + 1);
                selectedProducts[inputIndex] = null;
                assignedProducts[productIndex] = false;
            }
            if (!node.Inputs[inputIndex].Required && solutions.Count == solutionCountBeforeCandidates)
            {
                Assign(inputIndex + 1);
            }
        }
    }

    private static bool Matches(ProcessingGraphInputContract input, ProcessingGraphProductContract output) =>
        (input.Roles.IsEmpty || input.Roles.Contains(output.Role)) &&
        (input.ProductKinds.IsEmpty || input.ProductKinds.Contains(output.ProductKind)) &&
        (input.Variants.IsEmpty || input.Variants.Contains(output.Variant, StringComparer.Ordinal)) &&
        (input.RecipeNames.IsEmpty || output.Recipe is not null && input.RecipeNames.Contains(output.Recipe.Name, StringComparer.Ordinal)) &&
        (input.SchemaVersions.IsEmpty || output.SchemaVersion is not null && input.SchemaVersions.Contains(output.SchemaVersion, StringComparer.Ordinal));

    private static bool IsProductDependency(ProcessingGraphDependencyKind kind) => kind is
        ProcessingGraphDependencyKind.Artifact or
        ProcessingGraphDependencyKind.CanonicalJson or
        ProcessingGraphDependencyKind.Annotation;

    private static bool InputKindMatchesDependency(
        ProcessingGraphInputBindingKind inputKind,
        ProcessingGraphDependencyKind dependencyKind) => inputKind switch
        {
            ProcessingGraphInputBindingKind.PrimaryArtifact or ProcessingGraphInputBindingKind.AuxiliaryArtifact =>
                dependencyKind == ProcessingGraphDependencyKind.Artifact,
            ProcessingGraphInputBindingKind.CanonicalJson => dependencyKind == ProcessingGraphDependencyKind.CanonicalJson,
            ProcessingGraphInputBindingKind.Annotation => dependencyKind == ProcessingGraphDependencyKind.Annotation,
            _ => false
        };

    private static void ValidateOutputs(
        ImmutableArray<ProcessingGraphProductContract> outputs,
        string path,
        List<ProcessingGraphDiagnostic> diagnostics)
    {
        if (outputs.IsDefault)
        {
            Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, path, "Processing graph outputs are required.");
            return;
        }
        for (var index = 0; index < outputs.Length; index++)
        {
            var output = outputs[index];
            if (!Enum.IsDefined(output.Role) || !Enum.IsDefined(output.ProductKind) ||
                !ValidIdentity(output.Variant) || output.SchemaVersion is not null && !ValidIdentity(output.SchemaVersion) ||
                output.Recipe is { } recipe && (!ValidIdentity(recipe.Name) || !ValidIdentity(recipe.SemanticVersion) ||
                    !ValidIdentity(recipe.ImplementationVersion) || !Enum.IsDefined(recipe.OperationKind)) ||
                !output.Algorithms.IsDefault && (output.Algorithms.Length > MaximumContractsPerNode ||
                    output.Algorithms.Any(static algorithm => algorithm is null ||
                        !ValidIdentity(algorithm.Name) || !ValidIdentity(algorithm.Version))))
            {
                Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, $"{path}[{index}]",
                    "Processing graph output contract is invalid.");
            }
        }
    }

    private static void ValidateInputs(
        ImmutableArray<ProcessingGraphInputContract> inputs,
        string path,
        List<ProcessingGraphDiagnostic> diagnostics)
    {
        if (inputs.IsDefault)
        {
            Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, path, "Processing graph inputs are required.");
            return;
        }
        var bindingNames = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < inputs.Length; index++)
        {
            var input = inputs[index];
            if (input.Roles.IsDefault || input.ProductKinds.IsDefault || input.Variants.IsDefault ||
                input.RecipeNames.IsDefault || input.SchemaVersions.IsDefault ||
                input.Roles.Length > MaximumContractsPerNode || input.ProductKinds.Length > MaximumContractsPerNode ||
                input.Variants.Length > MaximumContractsPerNode || input.RecipeNames.Length > MaximumContractsPerNode ||
                input.SchemaVersions.Length > MaximumContractsPerNode ||
                input.Roles.Any(static role => !Enum.IsDefined(role)) ||
                input.ProductKinds.Any(static kind => !Enum.IsDefined(kind)) ||
                input.Variants.Any(static value => !ValidIdentity(value)) ||
                input.RecipeNames.Any(static value => !ValidIdentity(value)) ||
                input.SchemaVersions.Any(static value => !ValidIdentity(value)) ||
                !ValidIdentity(input.BindingName) || !Enum.IsDefined(input.BindingKind) ||
                !bindingNames.Add(input.BindingName) ||
                input.Roles.IsEmpty && input.ProductKinds.IsEmpty && input.Variants.IsEmpty &&
                    input.RecipeNames.IsEmpty && input.SchemaVersions.IsEmpty)
            {
                Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, $"{path}[{index}]",
                    "Processing graph input contract is invalid.");
            }
        }
    }

    private static void ValidateWindow(
        ProcessingGraphWindowRequirement? window,
        string path,
        List<ProcessingGraphDiagnostic> diagnostics)
    {
        if (window is null)
        {
            return;
        }
        var positionsValid = !window.RequiredPositions.IsDefault && window.RequiredPositions.Distinct().Count() == window.RequiredPositions.Length;
        var shapeValid = Enum.IsDefined(window.Kind) && window.MinimumInputCount > 0 &&
            window.MaximumInputCount >= window.MinimumInputCount && window.MaximumInputCount <= 4096 &&
            positionsValid && window.RequiredPositions.Length <= MaximumContractsPerNode &&
            !window.CompatibilityLabels.IsDefault && window.CompatibilityLabels.Length <= MaximumContractsPerNode;
        var modeValid = window.Kind switch
        {
            ProcessingGraphWindowKind.Trailing => window.RequiredPositions.IsEmpty,
            ProcessingGraphWindowKind.Centered => !window.RequiredPositions.IsEmpty && window.RequiredPositions.Contains(0) &&
                window.MinimumInputCount >= window.RequiredPositions.Length,
            _ => false
        };
        if (!shapeValid || !modeValid || window.CompatibilityLabels.IsEmpty ||
            window.CompatibilityLabels.Distinct(StringComparer.Ordinal).Count() != window.CompatibilityLabels.Length ||
            window.CompatibilityLabels.Any(static label => !ValidIdentity(label)))
        {
            Add(diagnostics, ProcessingGraphReasonCodes.InvalidWindow, path,
                "Processing graph window contract is invalid or incompatible with its mode.");
        }
    }

    private static void ValidateContext(
        ProcessingGraphNodeDefinition node,
        ProcessingGraphCompilationContext context,
        string path,
        List<ProcessingGraphDiagnostic> diagnostics)
    {
        if (context.Host is null)
        {
            return;
        }
        if (!node.HostApplicability.IsEmpty &&
            !node.HostApplicability.Contains(context.Host, StringComparer.Ordinal))
        {
            Add(diagnostics, ProcessingGraphReasonCodes.HostInapplicable, $"{path}.hostApplicability",
                $"Processing graph node '{node.Id}' does not apply to host '{context.Host}'.");
        }
        var available = context.AvailableCapabilities.IsDefault
            ? ImmutableArray<string>.Empty
            : context.AvailableCapabilities;
        var missing = node.CapabilityLabels
            .Where(capability => !available.Contains(capability, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            Add(diagnostics, ProcessingGraphReasonCodes.MissingCapability, $"{path}.capabilityLabels",
                $"Processing graph node '{node.Id}' requires unavailable capabilities: {string.Join(", ", missing)}.");
        }
    }

    private static void AddOutputKeys(
        string producerId,
        ImmutableArray<ProcessingGraphProductContract> outputs,
        HashSet<string> keys,
        List<ProcessingGraphDiagnostic> diagnostics)
    {
        foreach (var output in outputs)
        {
            var key = $"{output.Role}\0{output.Variant}";
            if (!keys.Add(key))
            {
                Add(diagnostics, ProcessingGraphReasonCodes.DuplicateOutput, $"outputs[{producerId}]",
                    $"Processing graph declares duplicate output {output.Role}/{output.Variant} at producer '{producerId}'.");
            }
        }
    }

    private static void ValidateLabels(
        ImmutableArray<string> labels,
        string path,
        List<ProcessingGraphDiagnostic> diagnostics)
    {
        if (labels.IsDefault || labels.Length > MaximumContractsPerNode ||
            labels.Any(static label => !ValidIdentity(label)) ||
            labels.Distinct(StringComparer.Ordinal).Count() != labels.Length)
        {
            Add(diagnostics, ProcessingGraphReasonCodes.InvalidNode, path,
                "Processing graph labels must be unique bounded non-empty values.");
        }
    }

    private static void ValidateCount<T>(
        ImmutableArray<T> values,
        string path,
        List<ProcessingGraphDiagnostic> diagnostics)
    {
        if (values.IsDefault || values.Length > MaximumContractsPerNode)
        {
            Add(diagnostics, ProcessingGraphReasonCodes.LimitExceeded, path,
                $"Processing graph collection is missing or exceeds {MaximumContractsPerNode} entries.");
        }
    }

    private static void ValidateIdentity(
        string value,
        string path,
        List<ProcessingGraphDiagnostic> diagnostics)
    {
        if (!ValidIdentity(value))
        {
            Add(diagnostics, ProcessingGraphReasonCodes.InvalidIdentity, path,
                "Processing graph identity must be a trimmed bounded non-empty value without control characters.");
        }
    }

    private static bool ValidIdentity(string? value) => value is { Length: > 0 and <= MaximumIdentityLength } &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) && !value.Any(char.IsControl);

    private static void Add(
        List<ProcessingGraphDiagnostic> diagnostics,
        string code,
        string path,
        string message) => diagnostics.Add(new(code, path, message));

    private static ProcessingGraphCompilationResult Failure(IEnumerable<ProcessingGraphDiagnostic> diagnostics) =>
        new(null, diagnostics
            .OrderBy(static diagnostic => diagnostic.Path, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToImmutableArray());
}
