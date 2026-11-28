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
        if (!IsValidUtf16(descriptor.Name) ||
            !IsValidUtf16(descriptor.SemanticVersion) ||
            !IsValidUtf16(descriptor.ImplementationVersion))
        {
            throw new ArgumentException("Recipe descriptor contains malformed UTF-16.", nameof(descriptor));
        }
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
        if (!IsValidUtf16(variant))
        {
            throw new ArgumentException("Output variant contains malformed UTF-16.", nameof(variant));
        }
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

    internal static bool IsValidUtf16(string? value)
    {
        if (value is null)
        {
            return true;
        }
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (!char.IsSurrogate(ch))
            {
                continue;
            }
            if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
                continue;
            }
            return false;
        }
        return true;
    }

    internal static bool IsValidAnnotationUtf16(ProcessingAnnotationInput annotation)
    {
        if (annotation.Objects is { } objects &&
            objects.Any(static item => item is not null &&
                (!IsValidUtf16(item.Id) || !IsValidUtf16(item.DisplayName))))
        {
            return false;
        }
        if (annotation.Segments is { } segments &&
            segments.Any(static item => item is not null && !IsValidUtf16(item.ConstellationId)))
        {
            return false;
        }
        if (annotation.MetadataOverlay is { } metadata &&
            (!ValidLines(metadata.TopLeft) || !ValidLines(metadata.TopRight) ||
             !ValidLines(metadata.BottomLeft) || !ValidLines(metadata.BottomRight)))
        {
            return false;
        }
        return true;

        static bool ValidLines(IReadOnlyList<string>? lines) =>
            lines is null || lines.All(static line => IsValidUtf16(line));
    }

    internal static JsonElement BindExecutionInputs(
        JsonElement normalizedOptions,
        ProcessingInputSelector selector,
        ProcessingAnnotationInput? annotation,
        IReadOnlyList<ProcessingAuxiliaryInput>? auxiliaryInputs = null,
        BayerPattern? deliveredCfaPhase = null)
    {
        if (!IsValidUtf16(selector.Variant) || !IsValidUtf16(selector.RecipeIdentitySha256))
        {
            throw new ArgumentException("Input selector contains malformed UTF-16.", nameof(selector));
        }
        if (annotation is not null && !IsValidAnnotationUtf16(annotation))
        {
            throw new ArgumentException("Annotation contains malformed UTF-16.", nameof(annotation));
        }
        if (auxiliaryInputs is not null && auxiliaryInputs.Any(static input => input is not null &&
            (!IsValidUtf16(input.Name) ||
             !IsValidUtf16(input.SchemaVersion) ||
             input.Selector is { } auxSelector &&
                 (!IsValidUtf16(auxSelector.Variant) || !IsValidUtf16(auxSelector.RecipeIdentitySha256)))))
        {
            throw new ArgumentException("Auxiliary input contains malformed UTF-16.", nameof(auxiliaryInputs));
        }
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
