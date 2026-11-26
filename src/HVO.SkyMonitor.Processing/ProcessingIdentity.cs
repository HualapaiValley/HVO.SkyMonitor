using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public static class ProcessingIdentity
{
    public const string BoundInputSchemaVersion = "hvo-processing-bound-inputs-v2";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static ProcessingRecipeIdentity CreateRecipeIdentity(
        ProcessingRecipeDefinition definition,
        JsonElement effectiveOptions)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var descriptor = RecipeIdentityDescriptor.Create(
            definition.Name,
            definition.SemanticVersion,
            definition.ImplementationVersion,
            effectiveOptions);
        return CreateRecipeIdentity(descriptor) with { OperationKind = definition.OperationKind };
    }

    public static ProcessingRecipeIdentity CreateRecipeIdentity(RecipeIdentityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var envelope = JsonSerializer.SerializeToElement(new
        {
            schema = "hvo-processing-recipe-v1",
            name = descriptor.Name,
            semanticVersion = descriptor.SemanticVersion,
            implementationVersion = descriptor.ImplementationVersion,
            options = descriptor.Options
        }, SerializerOptions);
        return new ProcessingRecipeIdentity(
            descriptor,
            CaptureContractJson.ComputeCanonicalJsonSha256(envelope));
    }

    public static string CreateOutputIdentity(
        FrameArtifactRole role,
        string variant,
        string recipeIdentitySha256,
        IReadOnlyList<Guid> sourceArtifactIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variant);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeIdentitySha256);
        ArgumentNullException.ThrowIfNull(sourceArtifactIds);
        if (recipeIdentitySha256.Length != 64 || recipeIdentitySha256.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Recipe identity must be a SHA-256 value.", nameof(recipeIdentitySha256));
        }
        var envelope = JsonSerializer.SerializeToElement(new
        {
            schema = "hvo-processing-output-v1",
            role = role.ToString(),
            variant,
            recipeIdentitySha256 = recipeIdentitySha256.ToUpperInvariant(),
            sourceArtifactIds
        }, SerializerOptions);
        return CaptureContractJson.ComputeCanonicalJsonSha256(envelope);
    }

    public static string ComputePayloadSha256(ReadOnlyMemory<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload.Span));

    public static Guid CreateArtifactId(string outputIdentitySha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputIdentitySha256);
        if (outputIdentitySha256.Length != SHA256.HashSizeInBytes * 2
            || outputIdentitySha256.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Output identity must be a SHA-256 value.", nameof(outputIdentitySha256));
        }
        var bytes = Convert.FromHexString(outputIdentitySha256);
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16));
    }

    internal static JsonElement BindExecutionInputs(
        JsonElement normalizedOptions,
        ProcessingInputSelector selector,
        ProcessingAnnotationInput? annotation,
        IReadOnlyList<ProcessingAuxiliaryInput>? auxiliaryInputs = null,
        BayerPattern? deliveredCfaPhase = null)
    {
        var annotationIdentity = annotation is null
            ? null
            : CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(new
            {
                ProvenanceSha256 = annotation.ProvenanceSha256.ToUpperInvariant(),
                annotation.Transform,
                annotation.Objects,
                annotation.Segments,
                annotation.ProjectionOverlay,
                annotation.MetadataOverlay
            }, SerializerOptions));
        var inputBinding = new
        {
            kind = selector.Kind.ToString(),
            role = selector.Role.ToString(),
            selector.Variant,
            recipeIdentitySha256 = selector.RecipeIdentitySha256?.ToUpperInvariant()
        };
        var boundAuxiliaries = (auxiliaryInputs ?? []).OrderBy(static input => input.Name, StringComparer.Ordinal).Select(static input => new
        {
            input.Name,
            kind = input.Kind.ToString(),
            selector = input.Selector is null ? null : new
            {
                kind = input.Selector.Kind.ToString(),
                role = input.Selector.Role.ToString(),
                input.Selector.Variant,
                recipeIdentitySha256 = input.Selector.RecipeIdentitySha256?.ToUpperInvariant()
            },
            input.SchemaVersion,
            identitySha256 = input.IdentitySha256?.ToUpperInvariant()
        }).ToArray();
        var envelope = deliveredCfaPhase is { } shiftedCfa && shiftedCfa != BayerPattern.Rggb
            ? JsonSerializer.SerializeToElement(new
            {
                schema = BoundInputSchemaVersion,
                input = inputBinding,
                deliveredCfa = shiftedCfa.ToString(),
                parameters = normalizedOptions,
                annotationIdentitySha256 = annotationIdentity,
                auxiliaryInputs = boundAuxiliaries
            }, SerializerOptions)
            : JsonSerializer.SerializeToElement(new
            {
                schema = BoundInputSchemaVersion,
                input = inputBinding,
                parameters = normalizedOptions,
                annotationIdentitySha256 = annotationIdentity,
                auxiliaryInputs = boundAuxiliaries
            }, SerializerOptions);
        return CaptureContractJson.Canonicalize(envelope);
    }
}
