using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Replay;

internal sealed record ReplayRequestProjection(
    ReplayRequestEnvelope Metadata,
    IReadOnlyList<ReadOnlyMemory<byte>> Payloads);

internal sealed record ReplayResponseProjection(
    ReplayResponseEnvelope Metadata,
    IReadOnlyList<ReadOnlyMemory<byte>> Payloads);

internal sealed record ReplayPayloadDeclaration(int Ordinal, long Length, string Sha256);

[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Private control-flow exception never crosses the assembly boundary.")]
[SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "Private control-flow exception never crosses the assembly boundary.")]
internal sealed class ReplayJobCanceledException : Exception;

internal static class ReplayProjection
{
    internal static ReplayRequestProjection ProjectRequest(
        byte[] authenticationKey,
        ReplayRunnerJobContext context,
        ProcessingExecutionRequest request,
        LocalReplayRunnerOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payloads = new List<ReadOnlyMemory<byte>>();
        var inputs = request.Inputs?.Select(input => ProjectArtifact(input, payloads)).ToArray()
            ?? throw new LocalReplayRunnerProtocolException("Processing inputs are required.");
        var auxiliaryInputs = request.AuxiliaryInputs?.Select(input => ProjectAuxiliaryInput(input, payloads)).ToArray();
        var requestMetadata = new ReplayExecutionRequestMetadata(
            request.RecipeName,
            request.Options,
            request.Input,
            inputs,
            request.OutputVariant,
            request.Annotation,
            auxiliaryInputs,
            request.InputArtifactId);
        var requestSha256 = ReplayProtocol.ComputeSha256(
            ReplayProtocol.SerializeMetadata(requestMetadata, options.MaxMetadataBytes));
        var authorization = ReplayAuthorization.Create(authenticationKey, context, requestSha256, now);
        var metadata = new ReplayRequestEnvelope(
            ReplayProtocol.Version,
            authorization,
            requestMetadata);
        ValidateRequestEnvelope(metadata, options, now, validateAuthorizationTime: false);
        return new ReplayRequestProjection(metadata, payloads);
    }

    internal static IReadOnlyList<ReplayPayloadDeclaration> ValidateRequestEnvelope(
        ReplayRequestEnvelope envelope,
        LocalReplayRunnerOptions options,
        DateTimeOffset now,
        bool validateAuthorizationTime)
    {
        if (envelope.ProtocolVersion != ReplayProtocol.Version || envelope.Authorization is null || envelope.Request is null)
        {
            throw new LocalReplayRunnerProtocolException("Replay request protocol version or required metadata is invalid.");
        }
        if (validateAuthorizationTime)
        {
            ReplayProtocol.ValidateJobContext(envelope.Authorization.Context, now);
        }

        var request = envelope.Request;
        if (!ReplayProtocol.IsBoundedIdentifier(request.RecipeName) ||
            !BuiltInProcessingRecipes.TryGetDefinition(request.RecipeName, out _) ||
            !ReplayProtocol.IsBoundedIdentifier(request.OutputVariant) ||
            request.Options.ValueKind is not (System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Null) ||
            request.Input is null || !Enum.IsDefined(request.Input.Kind) || !Enum.IsDefined(request.Input.Role) ||
            !OptionalIdentifier(request.Input.Variant) || !OptionalSemanticSha256(request.Input.RecipeIdentitySha256) ||
            request.Inputs is null || request.Inputs.Count is < 1 or > ReplayProtocolLimits.MaximumInputs ||
            request.InputArtifactId == Guid.Empty)
        {
            throw new LocalReplayRunnerProtocolException("Replay processing request metadata is invalid or exceeds protocol bounds.");
        }

        ValidateAnnotation(request.Annotation);
        var declarations = new List<ReplayPayloadDeclaration>();
        var artifactIds = new HashSet<Guid>();
        foreach (var artifact in request.Inputs)
        {
            ValidateArtifact(artifact, declarations, artifactIds);
        }
        if (request.AuxiliaryInputs is { } auxiliaryInputs)
        {
            if (auxiliaryInputs.Count > ReplayProtocolLimits.MaximumAuxiliaryInputs ||
                auxiliaryInputs.Select(static input => input.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != auxiliaryInputs.Count)
            {
                throw new LocalReplayRunnerProtocolException("Replay auxiliary input list is invalid or oversized.");
            }
            foreach (var auxiliary in auxiliaryInputs)
            {
                ValidateAuxiliaryInput(auxiliary, declarations);
            }
        }
        ValidateDeclarations(declarations, options.MaxTotalTransferBytes);
        return declarations.OrderBy(static declaration => declaration.Ordinal).ToArray();
    }

    internal static ProcessingExecutionRequest ReconstructRequest(
        ReplayExecutionRequestMetadata metadata,
        IReadOnlyList<byte[]> payloads)
    {
        var inputs = metadata.Inputs.Select(input => new ProcessingArtifact(
            input.ArtifactId,
            input.Role,
            input.Variant,
            input.RecipeIdentitySha256,
            input.MediaType,
            input.Layout,
            GetPayload(input.PayloadOrdinal, payloads),
            input.CreatedUtc,
            input.Integration,
            input.Compatibility,
            input.CaptureSequence,
            input.SourceArtifactIds,
            input.ObservationStartedUtc,
            input.ObservationEndedUtc,
            input.Conditions)
        {
            ProductKind = input.ProductKind,
            SchemaVersion = input.SchemaVersion,
            ContentIdentitySha256 = input.ContentIdentitySha256,
            CaptureId = input.CaptureId,
            DescriptorIdentitySha256 = input.DescriptorIdentitySha256
        }).ToArray();
        var auxiliaryInputs = metadata.AuxiliaryInputs?.Select(input => new ProcessingAuxiliaryInput(
            input.Name,
            input.Kind,
            input.Selector,
            input.SchemaVersion,
            input.IdentitySha256,
            GetPayload(input.PayloadOrdinal, payloads),
            input.ArtifactId)
        {
            ChecksumSha256 = input.ChecksumSha256
        }).ToArray();
        return new ProcessingExecutionRequest(
            metadata.RecipeName,
            metadata.Options,
            metadata.Input,
            inputs,
            metadata.OutputVariant,
            metadata.Annotation,
            auxiliaryInputs,
            metadata.InputArtifactId);
    }

    internal static ReplayResponseProjection ProjectResponse(
        ReplayRunnerJobContext context,
        ProcessingExecutionRequest request,
        ProcessingOutcome outcome,
        LocalReplayRunnerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var payloads = new List<ReadOnlyMemory<byte>>();
        var products = outcome.Products?.Select(product => ProjectProduct(product, payloads)).ToArray()
            ?? throw new LocalReplayRunnerProtocolException("Processing outcome products are required.");
        var envelope = new ReplayResponseEnvelope(
            ReplayProtocol.Version,
            CreateCorrelation(context),
            outcome.Status,
            outcome.ReasonCode,
            outcome.Field,
            products);
        ValidateOutcomeMetadata(envelope, context, request, payloads, options.MaxTotalTransferBytes, cancellationToken);
        return new ReplayResponseProjection(envelope, payloads);
    }

    internal static ProcessingOutcome ReconstructOutcome(
        ReplayResponseEnvelope envelope,
        IReadOnlyList<byte[]> payloads,
        ReplayRunnerJobContext context,
        ProcessingExecutionRequest request,
        long maximumTransferBytes,
        CancellationToken cancellationToken = default)
    {
        var memories = payloads.Select(static payload => (ReadOnlyMemory<byte>)payload).ToArray();
        ValidateOutcomeMetadata(envelope, context, request, memories, maximumTransferBytes, cancellationToken);
        var products = envelope.Products.Select(product => new ProcessingProduct(
            product.Role,
            product.Variant,
            product.OutputIdentitySha256,
            product.MediaType,
            product.Layout,
            GetPayload(product.PayloadOrdinal, payloads),
            product.ChecksumSha256,
            product.Recipe,
            product.Algorithms,
            product.SourceArtifactIds,
            product.TotalIntegration,
            product.Compatibility)
        {
            Kind = product.Kind,
            SchemaVersion = product.SchemaVersion,
            ContentIdentitySha256 = product.ContentIdentitySha256
        }).ToArray();
        return new ProcessingOutcome(envelope.Status, envelope.ReasonCode, envelope.Field, products);
    }

    internal static ReplayResponseCorrelation CreateCorrelation(ReplayRunnerJobContext context) => new(
        context.JobId,
        context.ReplayExecutionId,
        context.GraphRevisionId,
        context.LocalPlanIdentity,
        context.NodeId,
        context.DurableAttempt,
        context.DurableClaim);

    internal static void ValidatePayload(
        ReplayPayloadDeclaration declaration,
        ReplayFrame frame)
    {
        if (frame.Type == ReplayFrameType.Cancel)
        {
            ReplayProtocol.RequireEmptyControlFrame(frame, ReplayFrameType.Cancel);
            throw new ReplayJobCanceledException();
        }
        if (frame.Type is not (ReplayFrameType.RequestPayload or ReplayFrameType.ResponsePayload) ||
            frame.Ordinal != declaration.Ordinal || frame.Payload.LongLength != declaration.Length ||
            !string.Equals(ReplayProtocol.ComputeSha256(frame.Payload), declaration.Sha256, StringComparison.Ordinal))
        {
            throw new LocalReplayRunnerProtocolException("Replay binary payload does not match its ordinal, length, or SHA-256 declaration.");
        }
    }

    internal static IReadOnlyList<ReplayPayloadDeclaration> GetResponseDeclarations(
        ReplayResponseEnvelope envelope,
        long maximumTransferBytes)
    {
        if (envelope.Products is null || envelope.Products.Count > ReplayProtocolLimits.MaximumProducts)
        {
            throw new LocalReplayRunnerOutputValidationException("Replay response product list is invalid or oversized.");
        }
        var declarations = new List<ReplayPayloadDeclaration>();
        foreach (var product in envelope.Products)
        {
            AddDeclaration(product.PayloadOrdinal, product.PayloadLength, product.PayloadSha256, declarations, output: true);
        }
        try
        {
            ValidateDeclarations(declarations, maximumTransferBytes);
        }
        catch (LocalReplayRunnerProtocolException exception)
        {
            throw new LocalReplayRunnerOutputValidationException(exception.Message, exception);
        }
        return declarations.OrderBy(static declaration => declaration.Ordinal).ToArray();
    }

    private static ReplayArtifactMetadata ProjectArtifact(
        ProcessingArtifact artifact,
        List<ReadOnlyMemory<byte>> payloads)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var ordinal = AddPayload(artifact.Payload, payloads);
        return new ReplayArtifactMetadata(
            artifact.ArtifactId,
            artifact.Role,
            artifact.Variant,
            artifact.RecipeIdentitySha256,
            artifact.MediaType,
            artifact.Layout,
            ordinal,
            artifact.Payload.Length,
            ReplayProtocol.ComputeSha256(artifact.Payload),
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

    private static ReplayAuxiliaryInputMetadata ProjectAuxiliaryInput(
        ProcessingAuxiliaryInput input,
        List<ReadOnlyMemory<byte>> payloads)
    {
        ArgumentNullException.ThrowIfNull(input);
        var ordinal = AddPayload(input.Payload, payloads);
        return new ReplayAuxiliaryInputMetadata(
            input.Name,
            input.Kind,
            input.Selector,
            input.SchemaVersion,
            input.IdentitySha256,
            ordinal,
            input.Payload.Length,
            ReplayProtocol.ComputeSha256(input.Payload),
            input.ArtifactId,
            input.ChecksumSha256);
    }

    private static ReplayProductMetadata ProjectProduct(
        ProcessingProduct product,
        List<ReadOnlyMemory<byte>> payloads)
    {
        ArgumentNullException.ThrowIfNull(product);
        var ordinal = AddPayload(product.Payload, payloads);
        return new ReplayProductMetadata(
            product.Role,
            product.Variant,
            product.OutputIdentitySha256,
            product.MediaType,
            product.Layout,
            ordinal,
            product.Payload.Length,
            ReplayProtocol.ComputeSha256(product.Payload),
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

    private static int? AddPayload(ReadOnlyMemory<byte> payload, List<ReadOnlyMemory<byte>> payloads)
    {
        if (payload.IsEmpty)
        {
            return null;
        }
        var ordinal = payloads.Count;
        payloads.Add(payload);
        return ordinal;
    }

    private static void ValidateArtifact(
        ReplayArtifactMetadata artifact,
        List<ReplayPayloadDeclaration> declarations,
        HashSet<Guid> artifactIds)
    {
        if (artifact is null || artifact.ArtifactId == Guid.Empty || !artifactIds.Add(artifact.ArtifactId) ||
            !Enum.IsDefined(artifact.Role) || !Enum.IsDefined(artifact.ProductKind) ||
            !ReplayProtocol.IsBoundedIdentifier(artifact.Variant) ||
            !ReplayProtocol.IsSha256(artifact.RecipeIdentitySha256) ||
            !ReplayProtocol.IsBoundedIdentifier(artifact.MediaType) ||
            artifact.CreatedUtc.Offset != TimeSpan.Zero || artifact.Integration < TimeSpan.Zero ||
            artifact.Compatibility is null || !OptionalIdentifier(artifact.SchemaVersion) ||
            !OptionalSemanticSha256(artifact.ContentIdentitySha256) || artifact.CaptureId == Guid.Empty ||
            !OptionalSemanticSha256(artifact.DescriptorIdentitySha256))
        {
            throw new LocalReplayRunnerProtocolException("Replay artifact metadata is invalid.");
        }
        ValidateCompatibility(artifact.Compatibility);
        ValidateSourceIds(artifact.SourceArtifactIds, allowNull: true, output: false);
        if (artifact.ObservationStartedUtc is { } started && started.Offset != TimeSpan.Zero ||
            artifact.ObservationEndedUtc is { } ended && ended.Offset != TimeSpan.Zero ||
            artifact.ObservationStartedUtc is { } observationStarted &&
            artifact.ObservationEndedUtc is { } observationEnded && observationEnded < observationStarted ||
            artifact.Conditions is { } conditions &&
            (!double.IsFinite(conditions.Gain) || conditions.Gain < 0 ||
             conditions.Offset is { } offset && !double.IsFinite(offset) ||
             conditions.TemperatureC is { } temperature && !double.IsFinite(temperature)))
        {
            throw new LocalReplayRunnerProtocolException("Replay artifact timing or capture conditions are invalid.");
        }
        if (artifact.Layout is { } layout &&
            (!layout.Validate().IsValid || artifact.PayloadLength != 0 && layout.ByteLength != artifact.PayloadLength))
        {
            throw new LocalReplayRunnerProtocolException("Replay artifact frame layout does not match its payload length.");
        }
        AddDeclaration(artifact.PayloadOrdinal, artifact.PayloadLength, artifact.PayloadSha256, declarations, output: false);
    }

    private static void ValidateAuxiliaryInput(
        ReplayAuxiliaryInputMetadata input,
        List<ReplayPayloadDeclaration> declarations)
    {
        if (input is null || !ReplayProtocol.IsBoundedIdentifier(input.Name) || !Enum.IsDefined(input.Kind) ||
            !OptionalIdentifier(input.SchemaVersion) || !OptionalSemanticSha256(input.IdentitySha256) ||
            !OptionalSemanticSha256(input.ChecksumSha256) || input.ArtifactId == Guid.Empty)
        {
            throw new LocalReplayRunnerProtocolException("Replay auxiliary input metadata is invalid.");
        }
        if (input.Selector is { } selector &&
            (!Enum.IsDefined(selector.Kind) || !Enum.IsDefined(selector.Role) ||
             !OptionalIdentifier(selector.Variant) || !OptionalSemanticSha256(selector.RecipeIdentitySha256)))
        {
            throw new LocalReplayRunnerProtocolException("Replay auxiliary selector metadata is invalid.");
        }
        AddDeclaration(input.PayloadOrdinal, input.PayloadLength, input.PayloadSha256, declarations, output: false);
    }

    private static void ValidateAnnotation(ProcessingAnnotationInput? annotation)
    {
        if (annotation is null)
        {
            return;
        }
        if (annotation.Objects is null || annotation.Objects.Count > ReplayProtocolLimits.MaximumAnnotationItems ||
            annotation.Segments is null || annotation.Segments.Count > ReplayProtocolLimits.MaximumAnnotationItems ||
            !ReplayProtocol.IsSha256(annotation.ProvenanceSha256) ||
            annotation.Objects.Any(static item => item is null || !ReplayProtocol.IsBoundedIdentifier(item.Id) ||
                item.DisplayName is null || item.DisplayName.Length > ReplayProtocolLimits.MaximumIdentifierLength) ||
            annotation.Segments.Any(static item => item is null || !ReplayProtocol.IsBoundedIdentifier(item.ConstellationId)) ||
            annotation.MetadataOverlay is { } metadata &&
            new[] { metadata.TopLeft, metadata.TopRight, metadata.BottomLeft, metadata.BottomRight }
                .Any(static lines => lines is null || lines.Count > 8 ||
                    lines.Any(static line => line is null || line.Length > 64 || line.Any(char.IsControl))))
        {
            throw new LocalReplayRunnerProtocolException("Replay annotation metadata is invalid or oversized.");
        }
    }

    private static void ValidateOutcomeMetadata(
        ReplayResponseEnvelope envelope,
        ReplayRunnerJobContext context,
        ProcessingExecutionRequest request,
        IReadOnlyList<ReadOnlyMemory<byte>> payloads,
        long maximumTransferBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            if (envelope.ProtocolVersion != ReplayProtocol.Version || envelope.Correlation != CreateCorrelation(context) ||
                !Enum.IsDefined(envelope.Status) || envelope.Products is null ||
                envelope.Products.Count > ReplayProtocolLimits.MaximumProducts)
            {
                throw new LocalReplayRunnerOutputValidationException("Replay response metadata or correlation is invalid.");
            }
            if (envelope.Status == ProcessingOutcomeStatus.Produced)
            {
                if (envelope.Products.Count != 1 || envelope.ReasonCode is not null || envelope.Field is not null)
                {
                    throw new LocalReplayRunnerOutputValidationException("A produced replay outcome must contain exactly one product and no failure reason.");
                }
            }
            else if (envelope.Products.Count != 0 || !ReplayProtocol.IsBoundedIdentifier(envelope.ReasonCode) ||
                !OptionalIdentifier(envelope.Field))
            {
                throw new LocalReplayRunnerOutputValidationException("A non-produced replay outcome must contain a bounded reason and no products.");
            }

            var declarations = GetResponseDeclarations(envelope, maximumTransferBytes);
            if (declarations.Count != payloads.Count)
            {
                throw new LocalReplayRunnerOutputValidationException("Replay response payload count does not match its declarations.");
            }
            foreach (var declaration in declarations)
            {
                var payload = payloads[declaration.Ordinal];
                if (payload.Length != declaration.Length ||
                    !string.Equals(ReplayProtocol.ComputeSha256(payload), declaration.Sha256, StringComparison.Ordinal))
                {
                    throw new LocalReplayRunnerOutputValidationException("Replay response payload length or SHA-256 is invalid.");
                }
            }

            ProcessingRecipeIdentity? expectedRecipe = null;
            if (envelope.Products.Count > 0)
            {
                expectedRecipe = BuiltInProcessingRecipes.CreateExecutionIdentity(
                    request.RecipeName,
                    request.Options,
                    request.Input,
                    request.Annotation,
                    request.AuxiliaryInputs);
            }
            var expectedProduct = envelope.Products.Count > 0
                ? BuiltInProcessingRecipes.CreateProductContract(request, expectedRecipe!)
                : null;
            foreach (var product in envelope.Products)
            {
                ValidateProduct(product, payloads, request, expectedProduct!, expectedRecipe!, cancellationToken);
            }
        }
        catch (LocalReplayRunnerOutputValidationException)
        {
            throw;
        }
        catch (LocalReplayRunnerProtocolException exception)
        {
            throw new LocalReplayRunnerOutputValidationException(exception.Message, exception);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new LocalReplayRunnerOutputValidationException("Replay response failed output contract validation.", exception);
        }
    }

    private static void ValidateProduct(
        ReplayProductMetadata product,
        IReadOnlyList<ReadOnlyMemory<byte>> payloads,
        ProcessingExecutionRequest request,
        ProcessingProductContract expectedProduct,
        ProcessingRecipeIdentity expectedRecipe,
        CancellationToken cancellationToken)
    {
        if (product is null || !Enum.IsDefined(product.Role) || !Enum.IsDefined(product.Kind) ||
            product.Role != expectedProduct.Role ||
            product.Kind != expectedProduct.Kind ||
            !ReplayProtocol.IsBoundedIdentifier(product.Variant) ||
            !string.Equals(product.Variant, request.OutputVariant, StringComparison.Ordinal) ||
            !ReplayProtocol.IsUppercaseSha256(product.OutputIdentitySha256) ||
            !ReplayProtocol.IsBoundedIdentifier(product.MediaType) ||
            !string.Equals(product.MediaType, expectedProduct.MediaType, StringComparison.Ordinal) ||
            !ReplayProtocol.IsUppercaseSha256(product.PayloadSha256) ||
            !ReplayProtocol.IsUppercaseSha256(product.ChecksumSha256) ||
            product.Recipe?.Descriptor is null ||
            !ReplayProtocol.IsUppercaseSha256(product.Recipe.IdentitySha256) ||
            product.Algorithms is null || product.Algorithms.Count > ReplayProtocolLimits.MaximumAlgorithms ||
            product.Algorithms.Any(static algorithm => algorithm is null ||
                !ReplayProtocol.IsBoundedIdentifier(algorithm.Name) || !ReplayProtocol.IsBoundedIdentifier(algorithm.Version)) ||
            !product.Algorithms.SequenceEqual(expectedProduct.Algorithms) ||
            product.TotalIntegration != expectedProduct.TotalIntegration ||
            product.Compatibility != expectedProduct.Compatibility ||
            !OptionalIdentifier(product.SchemaVersion) || !OptionalSha256(product.ContentIdentitySha256) ||
            !string.Equals(product.SchemaVersion, expectedProduct.SchemaVersion, StringComparison.Ordinal) ||
            expectedProduct.RequiresContentIdentity != (product.ContentIdentitySha256 is not null) ||
            expectedProduct.ContentIdentitySha256 is not null && !string.Equals(
                product.ContentIdentitySha256,
                expectedProduct.ContentIdentitySha256,
                StringComparison.Ordinal))
        {
            throw new LocalReplayRunnerOutputValidationException("Replay product metadata is invalid or oversized.");
        }
        ValidateCompatibility(product.Compatibility);
        ValidateSourceIds(product.SourceArtifactIds, allowNull: false, output: true);
        if (!product.SourceArtifactIds.SequenceEqual(expectedProduct.SourceArtifactIds))
        {
            throw new LocalReplayRunnerOutputValidationException("Replay product lineage does not match the ordered recipe source contract.");
        }

        var payload = product.PayloadOrdinal is { } ordinal ? payloads[ordinal] : ReadOnlyMemory<byte>.Empty;
        if (expectedProduct.RequiresLayout != (product.Layout is not null) ||
            expectedProduct.ExactLayout is not null && product.Layout != expectedProduct.ExactLayout ||
            product.Layout is { } layout && (!layout.Validate().IsValid || layout.ByteLength != payload.Length) ||
            product.PayloadLength != payload.Length ||
            !string.Equals(product.ChecksumSha256, ReplayProtocol.ComputeSha256(payload), StringComparison.Ordinal) ||
            !string.Equals(product.PayloadSha256, product.ChecksumSha256, StringComparison.Ordinal))
        {
            throw new LocalReplayRunnerOutputValidationException("Replay product layout, payload length, or checksum is invalid.");
        }

        var descriptor = product.Recipe.Descriptor;
        if (!BuiltInProcessingRecipes.TryGetDefinition(request.RecipeName, out var definition) || definition is null ||
            !string.Equals(descriptor.Name, definition.Name, StringComparison.Ordinal) ||
            !string.Equals(descriptor.SemanticVersion, definition.SemanticVersion, StringComparison.Ordinal) ||
            !string.Equals(descriptor.ImplementationVersion, definition.ImplementationVersion, StringComparison.Ordinal) ||
            !ReplayProtocol.IsUppercaseSha256(descriptor.OptionsSha256) ||
            !string.Equals(descriptor.OptionsSha256, CaptureContractJson.ComputeCanonicalJsonSha256(descriptor.Options), StringComparison.Ordinal) ||
            !string.Equals(product.Recipe.IdentitySha256, expectedRecipe.IdentitySha256, StringComparison.Ordinal) ||
            !string.Equals(product.Recipe.IdentitySha256, ProcessingIdentity.CreateRecipeIdentity(descriptor).IdentitySha256, StringComparison.Ordinal))
        {
            throw new LocalReplayRunnerOutputValidationException("Replay product recipe name, version, or identity is invalid.");
        }

        var expectedOutputIdentity = ProcessingIdentity.CreateOutputIdentity(
            product.Role,
            product.Variant,
            product.Recipe.IdentitySha256,
            product.SourceArtifactIds);
        if (!string.Equals(product.OutputIdentitySha256, expectedOutputIdentity, StringComparison.Ordinal))
        {
            throw new LocalReplayRunnerOutputValidationException("Replay product output identity is invalid.");
        }

        if (!BuiltInProcessingRecipes.ProductPayloadMatchesContract(
            request,
            expectedProduct,
            payload,
            product.ContentIdentitySha256,
            product.Recipe.IdentitySha256,
            product.Algorithms,
            cancellationToken))
        {
            throw new LocalReplayRunnerOutputValidationException(
                "Replay product payload does not match its recipe contract.");
        }
    }

    private static void ValidateCompatibility(ProcessingCompatibilityIdentity compatibility)
    {
        if (!ReplayProtocol.IsBoundedIdentifier(compatibility.Rig) ||
            !ReplayProtocol.IsBoundedIdentifier(compatibility.Orientation) ||
            !ReplayProtocol.IsBoundedIdentifier(compatibility.Calibration) ||
            !ReplayProtocol.IsBoundedIdentifier(compatibility.Mask) ||
            !ReplayProtocol.IsBoundedIdentifier(compatibility.Sensor) ||
            !ReplayProtocol.IsBoundedIdentifier(compatibility.SetpointRegime) ||
            !ReplayProtocol.IsBoundedIdentifier(compatibility.ProcessingProfile) ||
            !OptionalSemanticSha256(compatibility.LocationIdentitySha256))
        {
            throw new LocalReplayRunnerProtocolException("Replay compatibility identity is invalid.");
        }
    }

    private static void ValidateSourceIds(IReadOnlyList<Guid>? sourceIds, bool allowNull, bool output)
    {
        if (sourceIds is null)
        {
            if (allowNull)
            {
                return;
            }
            ThrowLineage(output);
        }
        if (sourceIds!.Count > ReplayProtocolLimits.MaximumSourceIds ||
            sourceIds.Any(static id => id == Guid.Empty) || sourceIds.Distinct().Count() != sourceIds.Count)
        {
            ThrowLineage(output);
        }
    }

    private static void ThrowLineage(bool output)
    {
        if (output)
        {
            throw new LocalReplayRunnerOutputValidationException("Replay product lineage is invalid or oversized.");
        }
        throw new LocalReplayRunnerProtocolException("Replay artifact lineage is invalid or oversized.");
    }

    private static void AddDeclaration(
        int? ordinal,
        long length,
        string sha256,
        List<ReplayPayloadDeclaration> declarations,
        bool output)
    {
        if (length < 0 || length > Array.MaxLength || !ReplayProtocol.IsUppercaseSha256(sha256) ||
            (length == 0) != (ordinal is null) || ordinal is < 0)
        {
            if (output)
            {
                throw new LocalReplayRunnerOutputValidationException("Replay product payload declaration is invalid.");
            }
            throw new LocalReplayRunnerProtocolException("Replay input payload declaration is invalid.");
        }
        if (ordinal is { } value)
        {
            declarations.Add(new ReplayPayloadDeclaration(value, length, sha256));
        }
        else if (!string.Equals(sha256, ReplayProtocol.ComputeSha256(ReadOnlyMemory<byte>.Empty), StringComparison.Ordinal))
        {
            if (output)
            {
                throw new LocalReplayRunnerOutputValidationException("Replay empty product checksum is invalid.");
            }
            throw new LocalReplayRunnerProtocolException("Replay empty input checksum is invalid.");
        }
    }

    private static void ValidateDeclarations(List<ReplayPayloadDeclaration> declarations, long maximumTransferBytes)
    {
        var ordered = declarations.OrderBy(static declaration => declaration.Ordinal).ToArray();
        long total = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].Ordinal != index)
            {
                throw new LocalReplayRunnerProtocolException("Replay payload ordinals must be unique and contiguous.");
            }
            total = checked(total + ordered[index].Length);
            if (total > maximumTransferBytes)
            {
                throw new LocalReplayRunnerProtocolException("Replay payload transfer exceeds the configured limit.");
            }
        }
    }

    private static ReadOnlyMemory<byte> GetPayload(int? ordinal, IReadOnlyList<byte[]> payloads) =>
        ordinal is { } value ? payloads[value] : ReadOnlyMemory<byte>.Empty;

    private static bool OptionalIdentifier(string? value) => value is null || ReplayProtocol.IsBoundedIdentifier(value);

    private static bool OptionalSha256(string? value) => value is null || ReplayProtocol.IsUppercaseSha256(value);

    private static bool OptionalSemanticSha256(string? value) => value is null || ReplayProtocol.IsSha256(value);
}
