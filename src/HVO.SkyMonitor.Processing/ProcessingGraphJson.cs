using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

public static class ProcessingGraphJson
{
    public const int MaximumDocumentBytes = 2_097_152;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] SerializeCanonical(ProcessingGraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (ExceedsMaximumDocumentBytes(definition))
        {
            throw new ArgumentException(
                $"Processing graph JSON exceeds {MaximumDocumentBytes} bytes.", nameof(definition));
        }
        return JsonSerializer.SerializeToUtf8Bytes(CreateCanonicalDefinition(definition));
    }

    public static string ComputeDefinitionIdentity(ProcessingGraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (ExceedsMaximumDocumentBytes(definition))
        {
            throw new ArgumentException(
                $"Processing graph JSON exceeds {MaximumDocumentBytes} bytes.", nameof(definition));
        }
        return CaptureContractJson.ComputeCanonicalJsonSha256(CreateCanonicalDefinition(definition));
    }

    public static ProcessingGraphParseResult Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumDocumentBytes)
        {
            return Failure(
                ProcessingGraphReasonCodes.LimitExceeded,
                "$",
                $"Processing graph JSON exceeds {MaximumDocumentBytes} bytes.");
        }
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            if (TryFindDuplicateProperty(document.RootElement, "$", out var duplicatePath))
            {
                return Failure(
                    ProcessingGraphReasonCodes.InvalidJson,
                    duplicatePath,
                    "Processing graph JSON contains a duplicate property.");
            }
            var definition = JsonSerializer.Deserialize<ProcessingGraphDefinition>(utf8Json.Span, SerializerOptions);
            if (definition is null)
            {
                return Failure(ProcessingGraphReasonCodes.InvalidJson, "$", "Processing graph JSON is empty.");
            }
            return string.Equals(definition.SchemaVersion, ProcessingGraphSchemaVersions.Current, StringComparison.Ordinal)
                ? new(definition, [])
                : Failure(
                    ProcessingGraphReasonCodes.UnsupportedSchema,
                    "schemaVersion",
                    $"Processing graph schema '{definition.SchemaVersion}' is unsupported.");
        }
        catch (JsonException exception)
        {
            return Failure(
                ProcessingGraphReasonCodes.InvalidJson,
                exception.Path ?? "$",
                "Processing graph JSON is invalid.");
        }
    }

    internal static JsonElement CreateCanonicalDefinition(ProcessingGraphDefinition definition)
        => CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(new
        {
            schemaVersion = definition.SchemaVersion,
            name = definition.Name,
            revision = definition.Revision,
            sources = definition.Sources
                .OrderBy(static source => source.Id, StringComparer.Ordinal)
                .Select(CanonicalSource).ToArray(),
            nodes = definition.Nodes
                .OrderBy(static node => node.Id, StringComparer.Ordinal)
                .Select(CanonicalNode).ToArray()
        }));

    internal static bool ExceedsMaximumDocumentBytes(ProcessingGraphDefinition definition)
    {
        long length = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = definition.SchemaVersion,
            name = definition.Name,
            revision = definition.Revision,
            sources = Array.Empty<object>(),
            nodes = Array.Empty<object>()
        }).Length;
        if (AddArray(definition.Sources.Select(CanonicalSource)) ||
            AddArray(definition.Nodes.Select(CanonicalNode)))
        {
            return true;
        }
        return length > MaximumDocumentBytes;

        bool AddArray(IEnumerable<object> values)
        {
            var first = true;
            foreach (var value in values)
            {
                length += JsonSerializer.SerializeToUtf8Bytes(value).Length;
                if (!first)
                {
                    length++;
                }
                first = false;
                if (length > MaximumDocumentBytes)
                {
                    return true;
                }
            }
            return false;
        }
    }

    private static object CanonicalSource(ProcessingGraphSourceDefinition source) => new
    {
        id = source.Id,
        outputs = source.Outputs.Select(CanonicalOutput).ToArray()
    };

    private static object CanonicalNode(ProcessingGraphNodeDefinition node) => new
    {
        id = node.Id,
        stepAlias = node.StepAlias,
        stepVersion = node.StepVersion,
        operationKind = node.OperationKind.ToString(),
        enabled = node.Enabled,
        failurePolicy = node.FailurePolicy.ToString(),
        order = node.Order,
        effectiveOptions = CaptureContractJson.Canonicalize(node.EffectiveOptions),
        dependencies = node.Dependencies.Select(static dependency => new
        {
            producerId = dependency.ProducerId,
            kind = dependency.Kind.ToString(),
            required = dependency.Required
        }).ToArray(),
        inputs = node.Inputs.Select(CanonicalInput).ToArray(),
        outputs = node.Outputs.Select(CanonicalOutput).ToArray(),
        window = CanonicalWindow(node.Window),
        capabilityLabels = SortDistinct(node.CapabilityLabels),
        hostApplicability = SortDistinct(node.HostApplicability)
    };

    internal static object CanonicalInput(ProcessingGraphInputContract input) => new
    {
        roles = input.Roles.Distinct().Order().Select(static role => role.ToString()).ToArray(),
        productKinds = input.ProductKinds.Distinct().Order().Select(static kind => kind.ToString()).ToArray(),
        variants = SortDistinct(input.Variants),
        recipeNames = SortDistinct(input.RecipeNames),
        schemaVersions = SortDistinct(input.SchemaVersions),
        required = input.Required,
        input.BindingName,
        bindingKind = input.BindingKind.ToString()
    };

    internal static object CanonicalOutput(ProcessingGraphProductContract output) => new
    {
        role = output.Role.ToString(),
        output.Variant,
        productKind = output.ProductKind.ToString(),
        recipe = output.Recipe is null ? null : new
        {
            output.Recipe.Name,
            output.Recipe.SemanticVersion,
            output.Recipe.ImplementationVersion,
            operationKind = output.Recipe.OperationKind.ToString()
        },
        output.SchemaVersion,
        algorithms = (output.Algorithms.IsDefault ? [] : output.Algorithms)
            .Select(static algorithm => new { algorithm.Name, algorithm.Version }).ToArray()
    };

    internal static object? CanonicalWindow(ProcessingGraphWindowRequirement? window) => window is null ? null : new
    {
        kind = window.Kind.ToString(),
        window.MinimumInputCount,
        window.MaximumInputCount,
        requiredPositions = window.RequiredPositions.ToArray(),
        compatibilityLabels = SortDistinct(window.CompatibilityLabels)
    };

    internal static string[] SortDistinct(IEnumerable<string> values) => values
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static bool TryFindDuplicateProperty(JsonElement element, string path, out string duplicatePath)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = $"{path}.{property.Name}";
                if (!names.Add(property.Name))
                {
                    duplicatePath = propertyPath;
                    return true;
                }
                if (TryFindDuplicateProperty(property.Value, propertyPath, out duplicatePath))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindDuplicateProperty(item, $"{path}[{index}]", out duplicatePath))
                {
                    return true;
                }
                index++;
            }
        }
        duplicatePath = string.Empty;
        return false;
    }

    private static ProcessingGraphParseResult Failure(string code, string path, string message) =>
        new(null, [new(code, path, message)]);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
