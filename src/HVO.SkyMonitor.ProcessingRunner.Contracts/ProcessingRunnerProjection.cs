using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.ProcessingRunner.Contracts;

/// <summary>
/// Projects shared processing contracts to protocol metadata and back. Both directions are used by LogicHost and
/// the runner so a claim reconstructs the exact <see cref="ProcessingExecutionRequest"/> the in-process path builds.
/// </summary>
public static class ProcessingRunnerProjection
{
    public static ProcessingRunnerArtifactMetadata ProjectArtifact(
        ProcessingArtifact artifact,
        Guid devicePublicId,
        string contentPath,
        long payloadLength,
        string payloadSha256)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadSha256);
        return new ProcessingRunnerArtifactMetadata(
            artifact.ArtifactId,
            devicePublicId,
            contentPath,
            payloadLength,
            payloadSha256,
            artifact.Role,
            artifact.Variant,
            artifact.RecipeIdentitySha256,
            artifact.MediaType,
            artifact.Layout,
            artifact.CreatedUtc,
            artifact.Integration,
            artifact.Compatibility,
            artifact.CaptureSequence,
            artifact.SourceArtifactIds,
            artifact.ObservationStartedUtc,
            artifact.ObservationEndedUtc,
            artifact.Conditions,
            artifact.ProductKind,
            artifact.SchemaVersion,
            artifact.ContentIdentitySha256,
            artifact.CaptureId,
            artifact.DescriptorIdentitySha256);
    }

    public static ProcessingArtifact ReconstructArtifact(
        ProcessingRunnerArtifactMetadata metadata,
        ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new ProcessingArtifact(
            metadata.ArtifactId,
            metadata.Role,
            metadata.Variant,
            metadata.RecipeIdentitySha256,
            metadata.MediaType,
            metadata.Layout,
            payload,
            metadata.CreatedUtc,
            metadata.Integration,
            metadata.Compatibility,
            metadata.CaptureSequence,
            metadata.SourceArtifactIds,
            metadata.ObservationStartedUtc,
            metadata.ObservationEndedUtc,
            metadata.Conditions)
        {
            ProductKind = metadata.ProductKind,
            SchemaVersion = metadata.SchemaVersion,
            ContentIdentitySha256 = metadata.ContentIdentitySha256,
            CaptureId = metadata.CaptureId,
            DescriptorIdentitySha256 = metadata.DescriptorIdentitySha256
        };
    }

    public static ProcessingRunnerAuxiliaryInputMetadata ProjectAuxiliaryInput(ProcessingAuxiliaryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string? canonicalJson = null;
        if (input.Kind == ProcessingAuxiliaryInputKind.CanonicalJson)
        {
            canonicalJson = Encoding.UTF8.GetString(input.Payload.Span);
        }
        else if (!input.Payload.IsEmpty)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.InvalidCompletion,
                "Artifact auxiliary inputs travel by reference, never as inline payloads.");
        }
        return new ProcessingRunnerAuxiliaryInputMetadata(
            input.Name,
            input.Kind,
            input.Selector,
            input.SchemaVersion,
            input.IdentitySha256,
            canonicalJson,
            input.ArtifactId,
            input.ChecksumSha256);
    }

    public static ProcessingAuxiliaryInput ReconstructAuxiliaryInput(ProcessingRunnerAuxiliaryInputMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ReadOnlyMemory<byte> payload = default;
        if (metadata.Kind == ProcessingAuxiliaryInputKind.CanonicalJson)
        {
            var bytes = Encoding.UTF8.GetBytes(metadata.CanonicalJson ?? string.Empty);
            if (metadata.IdentitySha256 is not null
                && !ProcessingRunnerProtocol.ChecksumEquals(ProcessingIdentity.ComputePayloadSha256(bytes), metadata.IdentitySha256))
            {
                throw new ProcessingRunnerProtocolException(
                    ProcessingRunnerReasonCodes.PayloadChecksumMismatch,
                    $"Canonical auxiliary input '{metadata.Name}' does not match its declared identity.");
            }
            payload = bytes;
        }
        return new ProcessingAuxiliaryInput(
            metadata.Name,
            metadata.Kind,
            metadata.Selector,
            metadata.SchemaVersion,
            metadata.IdentitySha256,
            payload,
            metadata.ArtifactId)
        {
            ChecksumSha256 = metadata.ChecksumSha256
        };
    }

    /// <summary>Rebuilds the execution request from a claim and the verified input payloads, in claim order.</summary>
    public static ProcessingExecutionRequest ReconstructRequest(
        ProcessingRunnerClaim claim,
        IReadOnlyList<ReadOnlyMemory<byte>> payloads)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(payloads);
        if (payloads.Count != claim.Inputs.Count)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.InvalidCompletion, "Every claimed input requires exactly one payload.");
        }
        var inputs = new ProcessingArtifact[claim.Inputs.Count];
        for (var index = 0; index < inputs.Length; index++)
        {
            var metadata = claim.Inputs[index];
            var payload = payloads[index];
            VerifyPayload(payload.Span, metadata.PayloadLength, metadata.PayloadSha256, $"input {metadata.ArtifactId:D}");
            inputs[index] = ReconstructArtifact(metadata, payload);
        }
        var auxiliaryInputs = claim.AuxiliaryInputs?.Select(ReconstructAuxiliaryInput).ToArray();
        return new ProcessingExecutionRequest(
            claim.RecipeName,
            claim.Options,
            claim.Input,
            inputs,
            claim.OutputVariant,
            claim.Annotation,
            auxiliaryInputs,
            claim.InputArtifactId);
    }

    public static ProcessingRunnerProductMetadata ProjectProduct(ProcessingProduct product, int? payloadOrdinal)
    {
        ArgumentNullException.ThrowIfNull(product);
        return new ProcessingRunnerProductMetadata(
            product.Role,
            product.Variant,
            product.OutputIdentitySha256,
            product.MediaType,
            product.Layout,
            payloadOrdinal,
            product.Payload.Length,
            ProcessingRunnerProtocol.ComputeSha256(product.Payload.Span),
            product.ChecksumSha256,
            product.Recipe,
            product.Algorithms,
            product.SourceArtifactIds,
            product.TotalIntegration,
            product.Compatibility,
            product.Kind,
            product.SchemaVersion,
            product.ContentIdentitySha256);
    }

    /// <summary>
    /// Rebuilds a product and re-derives its identities: the recipe identity from its descriptor and the output identity
    /// from role, variant, recipe identity, and ordered sources. A runner-supplied identity that does not match its
    /// provenance is rejected before anything durable can be keyed by it.
    /// </summary>
    public static ProcessingProduct ReconstructProduct(
        ProcessingRunnerProductMetadata metadata,
        ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        VerifyPayload(payload.Span, metadata.PayloadLength, metadata.PayloadSha256, $"product {metadata.OutputIdentitySha256}");
        var derivedRecipe = ProcessingIdentity.CreateRecipeIdentity(metadata.Recipe.Descriptor);
        if (!ProcessingRunnerProtocol.ChecksumEquals(derivedRecipe.IdentitySha256, metadata.Recipe.IdentitySha256))
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.RecipeIdentityMismatch,
                "The product recipe identity does not match its recipe descriptor.");
        }
        var derivedOutput = ProcessingIdentity.CreateOutputIdentity(
            metadata.Role, metadata.Variant, metadata.Recipe.IdentitySha256, metadata.SourceArtifactIds);
        if (!ProcessingRunnerProtocol.ChecksumEquals(derivedOutput, metadata.OutputIdentitySha256))
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.OutputIdentityMismatch,
                "The product output identity does not match its role, variant, recipe, and sources.");
        }
        return new ProcessingProduct(
            metadata.Role,
            metadata.Variant,
            metadata.OutputIdentitySha256,
            metadata.MediaType,
            metadata.Layout,
            payload,
            metadata.ChecksumSha256,
            metadata.Recipe,
            metadata.Algorithms,
            metadata.SourceArtifactIds,
            metadata.TotalIntegration,
            metadata.Compatibility)
        {
            Kind = metadata.Kind,
            SchemaVersion = metadata.SchemaVersion,
            ContentIdentitySha256 = metadata.ContentIdentitySha256
        };
    }

    /// <summary>Projects an outcome to a completion request plus the ordered payload parts it references.</summary>
    public static (ProcessingRunnerCompletionRequest Request, IReadOnlyList<ReadOnlyMemory<byte>> Payloads) ProjectOutcome(
        Guid leaseToken,
        ProcessingOutcome outcome,
        long inputBytes,
        TimeSpan executionDuration)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Products.Count > ProcessingRunnerProtocol.MaximumProductCount)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.TransferTooLarge, "The outcome declares too many products.");
        }
        var payloads = new List<ReadOnlyMemory<byte>>();
        var products = new List<ProcessingRunnerProductMetadata>(outcome.Products.Count);
        foreach (var product in outcome.Products)
        {
            int? ordinal = null;
            if (!product.Payload.IsEmpty)
            {
                ordinal = payloads.Count;
                payloads.Add(product.Payload);
            }
            products.Add(ProjectProduct(product, ordinal));
        }
        return (new ProcessingRunnerCompletionRequest(
            leaseToken,
            outcome.Status,
            outcome.ReasonCode,
            outcome.Field,
            products,
            inputBytes,
            executionDuration), payloads);
    }

    public static ProcessingOutcome ReconstructOutcome(
        ProcessingRunnerCompletionRequest request,
        IReadOnlyList<ReadOnlyMemory<byte>> payloads)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payloads);
        var products = new ProcessingProduct[request.Products.Count];
        for (var index = 0; index < products.Length; index++)
        {
            var metadata = request.Products[index];
            ReadOnlyMemory<byte> payload = default;
            if (metadata.PayloadOrdinal is { } ordinal)
            {
                if (ordinal < 0 || ordinal >= payloads.Count)
                {
                    throw new ProcessingRunnerProtocolException(
                        ProcessingRunnerReasonCodes.InvalidCompletion, "A product references a missing payload part.");
                }
                payload = payloads[ordinal];
            }
            products[index] = ReconstructProduct(metadata, payload);
        }
        return new ProcessingOutcome(request.Status, request.ReasonCode, request.Field, products);
    }

    public static void VerifyPayload(ReadOnlySpan<byte> payload, long expectedLength, string expectedSha256, string subject)
    {
        if (payload.Length != expectedLength)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.PayloadLengthMismatch,
                $"The {subject} payload length {payload.Length} does not match the declared length {expectedLength}.");
        }
        if (!ProcessingRunnerProtocol.ChecksumEquals(ProcessingRunnerProtocol.ComputeSha256(payload), expectedSha256))
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.PayloadChecksumMismatch,
                $"The {subject} payload does not match its declared SHA-256.");
        }
    }

    public static byte[] SerializeMetadata<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ProcessingRunnerProtocol.SerializerOptions);
        if (bytes.Length > ProcessingRunnerProtocol.MaximumMetadataBytes)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.TransferTooLarge, "Protocol metadata exceeds the maximum size.");
        }
        return bytes;
    }

    public static T DeserializeMetadata<T>(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > ProcessingRunnerProtocol.MaximumMetadataBytes)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.TransferTooLarge, "Protocol metadata exceeds the maximum size.");
        }
        try
        {
            return JsonSerializer.Deserialize<T>(utf8Json, ProcessingRunnerProtocol.SerializerOptions)
                ?? throw new ProcessingRunnerProtocolException(
                    ProcessingRunnerReasonCodes.InvalidCompletion, "Protocol metadata is empty.");
        }
        catch (JsonException exception)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.InvalidCompletion, "Protocol metadata is malformed.", exception);
        }
    }
}
