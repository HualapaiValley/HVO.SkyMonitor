using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public static class BuiltInProcessingRecipes
{
    public const string LinearNormalization = "linear-normalization";
    public const string EncodedPreview = "encoded-preview";
    public const string JpegEncoding = "jpeg-encoding";
    public const string Annotation = "annotation";
    public const string RollingMean = "rolling-mean";
    public const string ImageQuality = "image-quality";
    public const string NoOpAnalyzer = "no-op-analyzer";
    public const string CloudAssessment = "cloud-assessment";
    public const string WeatherCloudOverlay = "weather-cloud-overlay";
    public const string ReferenceCalibration = "reference-calibration";
    public const string ProjectedScene = "projected-scene";

    private static readonly Dictionary<string, ProcessingRecipeDefinition> Definitions = CreateAll()
        .Select(static recipe => recipe.Definition)
        .ToDictionary(static definition => definition.Name, StringComparer.Ordinal);

    public static bool TryGetDefinition(string recipeName, out ProcessingRecipeDefinition? definition)
    {
        if (Definitions.TryGetValue(recipeName, out var found))
        {
            definition = found;
            return true;
        }
        definition = null;
        return false;
    }

    public static ProcessingRecipeIdentity CreateRequestedIdentity(
        string recipeName,
        JsonElement options,
        ProcessingInputSelector selector)
        => CreateExecutionIdentity(recipeName, options, selector);

    public static ProcessingRecipeIdentity CreateExecutionIdentity(
        string recipeName,
        JsonElement options,
        ProcessingInputSelector selector,
        ProcessingAnnotationInput? annotation = null,
        IReadOnlyList<ProcessingAuxiliaryInput>? auxiliaryInputs = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var recipe = CreateAll().SingleOrDefault(candidate =>
            string.Equals(candidate.Definition.Name, recipeName, StringComparison.Ordinal))
            ?? throw new ArgumentException("The requested built-in recipe is unknown.", nameof(recipeName));
        var normalized = recipe.NormalizeOptions(options);
        var effective = ProcessingIdentity.BindExecutionInputs(normalized, selector, annotation, auxiliaryInputs);
        return ProcessingIdentity.CreateRecipeIdentity(recipe.Definition, effective);
    }

    public static JsonElement NormalizeOptions(string recipeName, JsonElement options)
    {
        var recipe = CreateAll().SingleOrDefault(candidate =>
            string.Equals(candidate.Definition.Name, recipeName, StringComparison.Ordinal))
            ?? throw new ArgumentException("The requested built-in recipe is unknown.", nameof(recipeName));
        return recipe.NormalizeOptions(options);
    }

    internal static IProcessingRecipe[] CreateAll() =>
    [
        new LinearNormalizationRecipe(),
        new EncodedPreviewRecipe(),
        new JpegEncodingRecipe(),
        new AnnotationRecipe(),
        new RollingMeanRecipe(),
        new ImageQualityRecipe(),
        new NoOpAnalyzerRecipe(),
        new CloudAssessmentRecipe(),
        new WeatherCloudOverlayRecipe(),
        new ReferenceCalibrationRecipe(),
        new ProjectedSceneRecipe()
    ];
}

public sealed record JpegEncodingOptions(
    int JpegQuality = JpegImageCodec.DefaultQuality,
    int? MaximumDimension = null);

internal sealed class JpegEncodingRecipe : IProcessingRecipe
{
    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.JpegEncoding, "1.0.0", "packed-jpeg-v1",
        ProcessingOperationKind.Transform);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<JpegEncodingOptions>(options);
        if (parsed.JpegQuality is < 1 or > 100 || parsed.MaximumDimension is < 1 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        return ProcessingRecipeSupport.Normalize(parsed);
    }

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        var input = ProcessingRecipeSupport.ResolveSingle(request, out var failure);
        if (input is null)
        {
            return ValueTask.FromResult(failure!);
        }
        if (!ProcessingRecipeSupport.TryValidateFrame(input, out var layout, out var layoutFailure))
        {
            return ValueTask.FromResult(layoutFailure!);
        }
        if (input.Role is not (FrameArtifactRole.Preview or FrameArtifactRole.AnnotatedPreview))
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidSelector,
                nameof(request.Input)));
        }

        var options = ProcessingRecipeSupport.ParseOptions<JpegEncodingOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        var maximumDimension = options.MaximumDimension ?? Math.Max(layout.Width, layout.Height);
        var resized = PackedImageDownsampler.Downsample(layout, input.Payload, maximumDimension);
        var resizedLayout = new ImageLayout(
            resized.Width,
            resized.Height,
            layout.PixelFormat,
            checked(resized.Width * ImageLayout.BytesPerPixel(layout.PixelFormat)));
        var payload = JpegImageCodec.EncodeToJpeg(
            resizedLayout,
            resized.Payload,
            options.JpegQuality,
            cancellationToken);
        var algorithms = new List<ProcessingAlgorithmIdentity>();
        if (resized.Width != layout.Width || resized.Height != layout.Height)
        {
            algorithms.Add(new("downsample", PackedImageDownsampler.AlgorithmVersion));
        }
        algorithms.Add(new("jpeg", JpegImageCodec.AlgorithmVersion));
        return ValueTask.FromResult(ProcessingOutcome.Produced(ProcessingRecipeSupport.CreateProduct(
            input.Role,
            request.OutputVariant,
            JpegImageCodec.MediaType,
            null,
            payload,
            identity,
            algorithms,
            [input],
            input.Integration,
            input.Compatibility)));
    }
}

internal static class ProcessingRecipeSupport
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static T ParseOptions<T>(JsonElement options)
    {
        if (options.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return JsonSerializer.Deserialize<T>("{}", SerializerOptions)
                ?? throw new ArgumentException("Recipe options are invalid.", nameof(options));
        }
        if (options.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Recipe options must be a JSON object.", nameof(options));
        }
        return options.Deserialize<T>(SerializerOptions)
            ?? throw new ArgumentException("Recipe options are invalid.", nameof(options));
    }

    internal static JsonElement Normalize<T>(T options) =>
        CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(options, SerializerOptions));

    internal static ProcessingArtifact? ResolveSingle(
        ProcessingExecutionRequest request,
        out ProcessingOutcome? failure)
    {
        if (!SelectorIsValid(request.Input))
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidSelector,
                nameof(request.Input));
            return null;
        }

        var matches = request.Inputs.Where(input =>
            (request.InputArtifactId is null || input.ArtifactId == request.InputArtifactId) &&
            Matches(input, request.Input)).ToArray();
        if (matches.Length == 0)
        {
            failure = ProcessingOutcome.Skipped(
                ProcessingReasonCodes.MissingInput,
                nameof(request.Input));
            return null;
        }
        if (matches.Length > 1)
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.AmbiguousInput,
                nameof(request.Input));
            return null;
        }

        failure = null;
        return matches[0];
    }

    internal static IReadOnlyList<ProcessingArtifact> ResolveMany(
        ProcessingExecutionRequest request,
        out ProcessingOutcome? failure)
    {
        if (!SelectorIsValid(request.Input))
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidSelector,
                nameof(request.Input));
            return [];
        }

        var matches = request.Inputs.Where(input =>
            (request.InputArtifactId is null || input.ArtifactId == request.InputArtifactId) &&
            Matches(input, request.Input)).ToArray();
        failure = matches.Length == 0
            ? ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingInput, nameof(request.Input))
            : null;
        return matches;
    }

    internal static ProcessingProduct CreateProduct(
        FrameArtifactRole role,
        string variant,
        string mediaType,
        FrameLayoutDescriptor? layout,
        ReadOnlyMemory<byte> payload,
        ProcessingRecipeIdentity identity,
        IReadOnlyList<ProcessingAlgorithmIdentity> algorithms,
        IReadOnlyList<ProcessingArtifact> sources,
        TimeSpan totalIntegration,
        ProcessingCompatibilityIdentity compatibility,
        ProcessingProductKind? kind = null,
        string? schemaVersion = null,
        string? contentIdentitySha256 = null)
    {
        var sourceIds = sources.Select(static source => source.ArtifactId).ToArray();
        return new ProcessingProduct(
            role,
            variant,
            ProcessingIdentity.CreateOutputIdentity(role, variant, identity.IdentitySha256, sourceIds),
            mediaType,
            layout,
            payload,
            ProcessingIdentity.ComputePayloadSha256(payload),
            identity,
            algorithms,
            sourceIds,
            totalIntegration,
            compatibility)
        {
            Kind = kind ?? (role == FrameArtifactRole.Metadata
                ? ProcessingProductKind.Metadata
                : ProcessingProductKind.PixelData),
            SchemaVersion = schemaVersion,
            ContentIdentitySha256 = contentIdentitySha256
        };
    }

    internal static bool TryValidateFrame(
        ProcessingArtifact input,
        out FrameLayoutDescriptor layout,
        out ProcessingOutcome? failure)
    {
        layout = input.Layout!;
        if (layout is null || layout.Width < 1 || layout.Height < 1)
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidLayout,
                nameof(input.Layout));
            return false;
        }
        if (!Enum.IsDefined(layout.PixelFormat))
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.UnsupportedFormat,
                nameof(input.Layout));
            return false;
        }
        var hasCompleteStoredCodeSemantics = layout.SampleDepthBits == layout.ContainerDepthBits ||
            layout.StoredCodeTransform.HasValue && layout.LevelCodeSpace.HasValue;
        var storedCodeIsSupported = layout.StoredCodeTransform switch
        {
            null => layout.SampleDepthBits == layout.ContainerDepthBits,
            FrameStoredCodeTransform.IdentityV1 or FrameStoredCodeTransform.RightAlignedV1 => true,
            FrameStoredCodeTransform.LeftShiftedV1 or FrameStoredCodeTransform.FullRangeScaledV1 =>
                layout.LevelCodeSpace == FrameLevelCodeSpace.StoredContainer,
            _ => false
        };
        var semanticsAreValid = layout.Validate().IsValid && hasCompleteStoredCodeSemantics && storedCodeIsSupported &&
            layout.PixelFormat switch
            {
                CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24 => true,
                CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 =>
                    layout.ByteOrder == FrameByteOrder.LittleEndian,
                _ => false
            };
        if (!semanticsAreValid)
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidLayout,
                nameof(input.Layout));
            return false;
        }
        if (layout.StrideBytes < checked(layout.Width * ImageLayout.BytesPerPixel(layout.PixelFormat)) ||
            layout.ByteLength != checked((long)layout.StrideBytes * layout.Height) ||
            input.Payload.Length != layout.ByteLength)
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidLayout,
                nameof(input.Layout));
            return false;
        }

        failure = null;
        return true;
    }

    internal static FrameLayoutDescriptor CreatePackedLayout(
        int width,
        int height,
        CameraPixelFormat pixelFormat)
    {
        var stride = checked(width * ImageLayout.BytesPerPixel(pixelFormat));
        return pixelFormat switch
        {
            CameraPixelFormat.Mono8 => new(width, height, stride, pixelFormat,
                FrameByteOrder.NotApplicable, 8, 8, FrameSamplePacking.ByteAligned,
                ColorFilterArrayPattern.None, null, byte.MaxValue, checked((long)stride * height)),
            CameraPixelFormat.Rgb24 => new(width, height, stride, pixelFormat,
                FrameByteOrder.NotApplicable, 8, 8, FrameSamplePacking.ByteAligned,
                ColorFilterArrayPattern.None, null, byte.MaxValue, checked((long)stride * height)),
            CameraPixelFormat.Mono16 => new(width, height, stride, pixelFormat,
                FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned,
                ColorFilterArrayPattern.None, null, ushort.MaxValue, checked((long)stride * height)),
            CameraPixelFormat.BayerRggb16 => new(width, height, stride, pixelFormat,
                FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned,
                ColorFilterArrayPattern.Rggb, null, ushort.MaxValue, checked((long)stride * height)),
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat))
        };
    }

    internal static FrameLayoutDescriptor CreatePackedLayout(FrameLayoutDescriptor source)
    {
        var stride = checked(source.Width * ImageLayout.BytesPerPixel(source.PixelFormat));
        return source with
        {
            StrideBytes = stride,
            ByteLength = checked((long)stride * source.Height)
        };
    }

    internal static byte[] PackRows(
        FrameLayoutDescriptor layout,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        var packedStride = checked(layout.Width * ImageLayout.BytesPerPixel(layout.PixelFormat));
        var output = new byte[checked(packedStride * layout.Height)];
        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.Span.Slice(y * layout.StrideBytes, packedStride)
                .CopyTo(output.AsSpan(y * packedStride, packedStride));
        }
        return output;
    }

    internal static bool SelectorIsValid(ProcessingInputSelector selector) => selector.Kind switch
    {
        ProcessingInputKind.Raw => selector.Role == FrameArtifactRole.Raw && selector.RecipeIdentitySha256 is null &&
            VariantIsValid(selector.Variant),
        ProcessingInputKind.Calibrated => selector.Role == FrameArtifactRole.Calibrated && selector.RecipeIdentitySha256 is null &&
            VariantIsValid(selector.Variant),
        ProcessingInputKind.Combined => selector.Role == FrameArtifactRole.Combined && selector.RecipeIdentitySha256 is null &&
            VariantIsValid(selector.Variant),
        ProcessingInputKind.RecipeResult => selector.Role != FrameArtifactRole.Raw &&
            !string.IsNullOrWhiteSpace(selector.Variant) &&
            IsSha256(selector.RecipeIdentitySha256),
        _ => false
    };

    internal static bool Matches(ProcessingArtifact input, ProcessingInputSelector selector) =>
        input.Role == selector.Role &&
        (selector.Variant is null || string.Equals(input.Variant, selector.Variant, StringComparison.Ordinal)) &&
        (selector.RecipeIdentitySha256 is null || string.Equals(
            input.RecipeIdentitySha256,
            selector.RecipeIdentitySha256,
            StringComparison.OrdinalIgnoreCase));

    private static bool VariantIsValid(string? variant) => variant is null || !string.IsNullOrWhiteSpace(variant);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public sealed record LinearNormalizationOptions(string Mode = "None");

internal sealed class LinearNormalizationRecipe : IProcessingRecipe
{
    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.LinearNormalization, "1.0.0", "linear-normalization-none-v1",
        ProcessingOperationKind.Transform);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<LinearNormalizationOptions>(options);
        if (!string.Equals(parsed.Mode, "None", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only explicit no-correction normalization is supported.", nameof(options));
        }
        return ProcessingRecipeSupport.Normalize(parsed);
    }

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        var input = ProcessingRecipeSupport.ResolveSingle(request, out var failure);
        if (input is null)
        {
            return ValueTask.FromResult(failure!);
        }
        if (!ProcessingRecipeSupport.TryValidateFrame(input, out var layout, out var layoutFailure))
        {
            return ValueTask.FromResult(layoutFailure!);
        }
        if (input.Role != FrameArtifactRole.Raw)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidSelector,
                nameof(request.Input)));
        }

        var payload = ProcessingRecipeSupport.PackRows(layout, input.Payload, cancellationToken);
        var packedLayout = ProcessingRecipeSupport.CreatePackedLayout(layout);
        var product = ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.Calibrated,
            request.OutputVariant,
            "application/x-hvo-linear-frame",
            packedLayout,
            payload,
            identity,
            [new("linear-normalization", "none-v1")],
            [input],
            input.Integration,
            input.Compatibility);
        return ValueTask.FromResult(ProcessingOutcome.Produced(product));
    }
}

public sealed record EncodedPreviewOptions(
    double BlackPercentile = 0.5,
    double WhitePercentile = 0.9999,
    double AsinhStrength = 4,
    int JpegQuality = JpegImageCodec.DefaultQuality,
    string OutputEncoding = "Jpeg");

internal sealed class EncodedPreviewRecipe : IProcessingRecipe
{
    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.EncodedPreview, "1.0.0", "encoded-preview-v1",
        ProcessingOperationKind.Transform);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<EncodedPreviewOptions>(options);
        _ = CreateStretch(parsed);
        if (parsed.JpegQuality is < 1 or > 100 ||
            parsed.OutputEncoding is not ("Jpeg" or "Packed"))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        return ProcessingRecipeSupport.Normalize(parsed);
    }

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        var input = ProcessingRecipeSupport.ResolveSingle(request, out var failure);
        if (input is null)
        {
            return ValueTask.FromResult(failure!);
        }
        if (!ProcessingRecipeSupport.TryValidateFrame(input, out var layout, out var layoutFailure))
        {
            return ValueTask.FromResult(layoutFailure!);
        }
        if (input.Role is not (FrameArtifactRole.Raw or FrameArtifactRole.Calibrated or FrameArtifactRole.Combined))
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidSelector,
                nameof(request.Input)));
        }

        var options = ProcessingRecipeSupport.ParseOptions<EncodedPreviewOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        byte[] display;
        CameraPixelFormat displayFormat;
        var algorithms = new List<ProcessingAlgorithmIdentity>();
        switch (layout.PixelFormat)
        {
            case CameraPixelFormat.Mono16:
                display = Mono16DisplayStretch.Apply(
                    layout.Width, layout.Height, input.Payload, cancellationToken, layout.StrideBytes, CreateStretch(options));
                displayFormat = CameraPixelFormat.Mono8;
                algorithms.Add(new("display-stretch", Mono16DisplayStretch.AlgorithmVersion));
                break;
            case CameraPixelFormat.BayerRggb16:
                display = BayerRggb16Demosaicer.DemosaicToRgb24(
                    layout.Width, layout.Height, input.Payload, cancellationToken, layout.StrideBytes, CreateStretch(options));
                displayFormat = CameraPixelFormat.Rgb24;
                algorithms.Add(new("display-stretch", Mono16DisplayStretch.AlgorithmVersion));
                algorithms.Add(new("rggb-demosaic", BayerRggb16Demosaicer.AlgorithmVersion));
                break;
            case CameraPixelFormat.Rgb24:
            case CameraPixelFormat.Mono8:
                display = ProcessingRecipeSupport.PackRows(layout, input.Payload, cancellationToken);
                displayFormat = layout.PixelFormat;
                algorithms.Add(new("row-packing", "packed-copy-v1"));
                break;
            default:
                return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                    ProcessingReasonCodes.UnsupportedFormat,
                    nameof(input.Layout.PixelFormat)));
        }

        var displayLayout = new ImageLayout(
            layout.Width,
            layout.Height,
            displayFormat,
            checked(layout.Width * ImageLayout.BytesPerPixel(displayFormat)));
        ReadOnlyMemory<byte> output;
        string mediaType;
        FrameLayoutDescriptor? outputLayout;
        if (options.OutputEncoding == "Packed")
        {
            output = display;
            mediaType = "application/x-hvo-packed-image";
            outputLayout = ProcessingRecipeSupport.CreatePackedLayout(
                displayLayout.Width, displayLayout.Height, displayLayout.PixelFormat);
        }
        else
        {
            output = JpegImageCodec.EncodeToJpeg(
                displayLayout, display, options.JpegQuality, cancellationToken);
            mediaType = JpegImageCodec.MediaType;
            outputLayout = null;
            algorithms.Add(new("jpeg", JpegImageCodec.AlgorithmVersion));
        }
        var product = ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.Preview,
            request.OutputVariant,
            mediaType,
            outputLayout,
            output,
            identity,
            algorithms,
            [input],
            input.Integration,
            input.Compatibility);
        return ValueTask.FromResult(ProcessingOutcome.Produced(product));
    }

    private static Mono16DisplayStretchOptions CreateStretch(EncodedPreviewOptions options)
    {
        var stretch = new Mono16DisplayStretchOptions(
            options.BlackPercentile,
            options.WhitePercentile,
            options.AsinhStrength);
        stretch.Validate();
        return stretch;
    }
}

public sealed record AnnotationRecipeOptions(
    int MarkRadius = 6,
    byte MarkerValue = 144,
    bool DrawLabels = true,
    int LabelScale = 1,
    bool DrawImageCircle = false,
    bool DrawCardinalDirections = false,
    byte ImageCircleValue = 96,
    byte CardinalValue = byte.MaxValue,
    int CardinalScale = 2,
    byte ConstellationLineValue = 160,
    byte ConstellationLineRed = 96,
    byte ConstellationLineGreen = 160,
    byte ConstellationLineBlue = byte.MaxValue,
    int ConstellationLineThickness = 1,
    double ConstellationLineOpacity = 0.8,
    int JpegQuality = JpegImageCodec.DefaultQuality,
    double BlackPercentile = 0.5,
    double WhitePercentile = 0.9999,
    double AsinhStrength = 4,
    string OutputEncoding = "Jpeg");

internal sealed class AnnotationRecipe : IProcessingRecipe
{
    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.Annotation, "1.0.0", "projected-annotation-v3",
        ProcessingOperationKind.Transform);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<AnnotationRecipeOptions>(options);
        CreateAnnotationOptions(parsed).ValidateForProcessing();
        _ = CreateStretch(parsed);
        if (parsed.JpegQuality is < 1 or > 100 ||
            parsed.OutputEncoding is not ("Jpeg" or "Packed"))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        return ProcessingRecipeSupport.Normalize(parsed);
    }

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        if (request.Annotation is null || string.IsNullOrWhiteSpace(request.Annotation.ProvenanceSha256))
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.MissingAnnotation,
                nameof(request.Annotation)));
        }
        if (request.Input.Kind == ProcessingInputKind.RecipeResult &&
            request.Input.Role != FrameArtifactRole.Preview)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidSelector,
                nameof(request.Input)));
        }
        var input = ProcessingRecipeSupport.ResolveSingle(request, out var failure);
        if (input is null)
        {
            return ValueTask.FromResult(failure!);
        }
        var auxiliarySources = (request.AuxiliaryInputs ?? [])
            .Where(static auxiliary => auxiliary.Kind == ProcessingAuxiliaryInputKind.Artifact)
            .Select(auxiliary => request.Inputs.SingleOrDefault(candidate =>
                candidate.ArtifactId == auxiliary.ArtifactId &&
                ProcessingRecipeSupport.Matches(candidate, auxiliary.Selector!)))
            .ToArray();
        if (auxiliarySources.Any(static source => source is null))
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidInput,
                nameof(request.AuxiliaryInputs)));
        }

        var options = ProcessingRecipeSupport.ParseOptions<AnnotationRecipeOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        if (!TryCreateDisplay(input, options, cancellationToken, out var display, out var format, out var algorithms, out var displayFailure))
        {
            return ValueTask.FromResult(displayFailure!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var annotationOptions = CreateAnnotationOptions(options);
        var annotation = format == CameraPixelFormat.Rgb24
            ? AnnotationRenderer.AnnotateRgb24WithSegments(
                display.PixelData, display.Width, display.Height, request.Annotation.Objects,
                request.Annotation.Segments, request.Annotation.Transform, annotationOptions,
                request.Annotation.ProjectionOverlay, request.Annotation.MetadataOverlay, cancellationToken)
            : AnnotationRenderer.AnnotateMono8WithSegments(
                display.PixelData, display.Width, display.Height, request.Annotation.Objects,
                request.Annotation.Segments, request.Annotation.Transform, annotationOptions,
                request.Annotation.ProjectionOverlay, request.Annotation.MetadataOverlay, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var packedStride = checked(display.Width * ImageLayout.BytesPerPixel(format));
        algorithms.Add(new("annotation-renderer", AnnotationRenderer.AlgorithmVersion));
        ReadOnlyMemory<byte> output;
        string mediaType;
        FrameLayoutDescriptor? outputLayout;
        if (options.OutputEncoding == "Packed")
        {
            output = annotation.Pixels;
            mediaType = "application/x-hvo-packed-image";
            outputLayout = ProcessingRecipeSupport.CreatePackedLayout(display.Width, display.Height, format);
        }
        else
        {
            output = JpegImageCodec.EncodeToJpeg(
                new ImageLayout(display.Width, display.Height, format, packedStride),
                annotation.Pixels,
                options.JpegQuality,
                cancellationToken);
            mediaType = JpegImageCodec.MediaType;
            outputLayout = null;
            algorithms.Add(new("jpeg", JpegImageCodec.AlgorithmVersion));
        }
        var product = ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.AnnotatedPreview,
            request.OutputVariant,
            mediaType,
            outputLayout,
            output,
            identity,
            algorithms,
            [input, .. auxiliarySources!],
            input.Integration,
            input.Compatibility);
        return ValueTask.FromResult(ProcessingOutcome.Produced(product));
    }

    private static bool TryCreateDisplay(
        ProcessingArtifact input,
        AnnotationRecipeOptions options,
        CancellationToken cancellationToken,
        out DecodedImage display,
        out CameraPixelFormat format,
        out List<ProcessingAlgorithmIdentity> algorithms,
        out ProcessingOutcome? failure)
    {
        algorithms = [];
        if (string.Equals(input.MediaType, JpegImageCodec.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            if (input.Role != FrameArtifactRole.Preview || input.Layout is not null)
            {
                display = null!;
                format = default;
                failure = ProcessingOutcome.TerminalFailure(
                    ProcessingReasonCodes.InvalidLayout,
                    nameof(input.Layout));
                return false;
            }
            display = JpegImageCodec.DecodeJpeg(input.Payload, cancellationToken);
            format = display.PixelFormat;
            algorithms.Add(new("jpeg-decode", JpegImageCodec.AlgorithmVersion));
            failure = null;
            return true;
        }
        if (!ProcessingRecipeSupport.TryValidateFrame(input, out var layout, out failure))
        {
            display = null!;
            format = default;
            return false;
        }

        var stretch = CreateStretch(options);
        byte[] pixels;
        switch (layout.PixelFormat)
        {
            case CameraPixelFormat.Mono16:
                pixels = Mono16DisplayStretch.Apply(
                    layout.Width, layout.Height, input.Payload, cancellationToken, layout.StrideBytes, stretch);
                format = CameraPixelFormat.Mono8;
                algorithms.Add(new("display-stretch", Mono16DisplayStretch.AlgorithmVersion));
                break;
            case CameraPixelFormat.BayerRggb16:
                pixels = BayerRggb16Demosaicer.DemosaicToRgb24(
                    layout.Width, layout.Height, input.Payload, cancellationToken, layout.StrideBytes, stretch);
                format = CameraPixelFormat.Rgb24;
                algorithms.Add(new("display-stretch", Mono16DisplayStretch.AlgorithmVersion));
                algorithms.Add(new("rggb-demosaic", BayerRggb16Demosaicer.AlgorithmVersion));
                break;
            case CameraPixelFormat.Mono8:
            case CameraPixelFormat.Rgb24:
                pixels = ProcessingRecipeSupport.PackRows(layout, input.Payload, cancellationToken);
                format = layout.PixelFormat;
                algorithms.Add(new("row-packing", "packed-copy-v1"));
                break;
            default:
                display = null!;
                failure = ProcessingOutcome.TerminalFailure(
                    ProcessingReasonCodes.UnsupportedFormat,
                    nameof(input.Layout.PixelFormat));
                format = default;
                return false;
        }

        display = new DecodedImage(
            layout.Width,
            layout.Height,
            format,
            checked(layout.Width * ImageLayout.BytesPerPixel(format)),
            pixels,
            "application/x-hvo-packed-image",
            "processing-display-v1");
        failure = null;
        return true;
    }

    private static AnnotationOptions CreateAnnotationOptions(AnnotationRecipeOptions options) => new()
    {
        MarkRadius = options.MarkRadius,
        MarkerValue = options.MarkerValue,
        DrawLabels = options.DrawLabels,
        LabelScale = options.LabelScale,
        DrawImageCircle = options.DrawImageCircle,
        DrawCardinalDirections = options.DrawCardinalDirections,
        ImageCircleValue = options.ImageCircleValue,
        CardinalValue = options.CardinalValue,
        CardinalScale = options.CardinalScale,
        ConstellationLineValue = options.ConstellationLineValue,
        ConstellationLineRed = options.ConstellationLineRed,
        ConstellationLineGreen = options.ConstellationLineGreen,
        ConstellationLineBlue = options.ConstellationLineBlue,
        ConstellationLineThickness = options.ConstellationLineThickness,
        ConstellationLineOpacity = options.ConstellationLineOpacity
    };

    private static Mono16DisplayStretchOptions CreateStretch(AnnotationRecipeOptions options)
    {
        var stretch = new Mono16DisplayStretchOptions(
            options.BlackPercentile, options.WhitePercentile, options.AsinhStrength);
        stretch.Validate();
        return stretch;
    }
}

public sealed record RollingMeanOptions(
    int MaximumFrameCount = 5,
    double? MaximumIntegrationMilliseconds = null,
    double? MaximumAgeMilliseconds = null);

internal sealed class RollingMeanRecipe : IProcessingRecipe
{
    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.RollingMean, "1.0.0", Linear16ArithmeticMean.AlgorithmVersion,
        ProcessingOperationKind.Window);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<RollingMeanOptions>(options);
        if (parsed.MaximumFrameCount is < 1 or > 100 ||
            parsed.MaximumIntegrationMilliseconds is <= 0 ||
            parsed.MaximumAgeMilliseconds is <= 0 ||
            parsed.MaximumIntegrationMilliseconds is { } integration && !double.IsFinite(integration) ||
            parsed.MaximumAgeMilliseconds is { } age && !double.IsFinite(age))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        return ProcessingRecipeSupport.Normalize(parsed);
    }

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        var candidates = ProcessingRecipeSupport.ResolveMany(request, out var failure);
        if (candidates.Count == 0)
        {
            return ValueTask.FromResult(failure!);
        }
        if (request.Input.Kind == ProcessingInputKind.RecipeResult ||
            request.Input.Role is not (FrameArtifactRole.Raw or FrameArtifactRole.Calibrated or FrameArtifactRole.Combined))
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidSelector,
                nameof(request.Input)));
        }
        if (candidates.Select(static source => source.ArtifactId).Any(static id => id == Guid.Empty) ||
            candidates.Select(static source => source.ArtifactId).Distinct().Count() != candidates.Count)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidLineage,
                nameof(request.Inputs)));
        }
        var sequenced = candidates.Count(source => source.CaptureSequence.HasValue);
        if (sequenced is not 0 && sequenced != candidates.Count)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidLineage,
                nameof(request.Inputs)));
        }
        for (var index = 1; index < candidates.Count; index++)
        {
            if (sequenced == candidates.Count
                    ? candidates[index].CaptureSequence <= candidates[index - 1].CaptureSequence
                    : candidates[index].CreatedUtc < candidates[index - 1].CreatedUtc)
            {
                return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                    ProcessingReasonCodes.InvalidLineage,
                    nameof(request.Inputs)));
            }
        }

        var options = ProcessingRecipeSupport.ParseOptions<RollingMeanOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        var selected = SelectWindow(candidates, options);
        if (!TryValidateCompatibility(selected, out var layouts))
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.IncompatibleInput,
                nameof(request.Inputs)));
        }

        var mean = Linear16ArithmeticMean.Compute(
            selected.Select((source, index) => new Linear16Frame(
                layouts[index].Width,
                layouts[index].Height,
                layouts[index].StrideBytes,
                layouts[index].PixelFormat,
                source.Payload)).ToArray(),
            cancellationToken);
        var totalIntegration = TimeSpan.FromTicks(selected.Sum(static source => source.Integration.Ticks));
        var product = ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.Combined,
            request.OutputVariant,
            "application/x-hvo-linear-frame",
            ProcessingRecipeSupport.CreatePackedLayout(layouts[0]),
            mean.PixelData,
            identity,
            [new("linear-mean", Linear16ArithmeticMean.AlgorithmVersion)],
            selected,
            totalIntegration,
            selected[^1].Compatibility);
        return ValueTask.FromResult(ProcessingOutcome.Produced(product));
    }

    private static List<ProcessingArtifact> SelectWindow(
        IReadOnlyList<ProcessingArtifact> candidates,
        RollingMeanOptions options)
    {
        var selected = new List<ProcessingArtifact>();
        var newest = candidates[^1].CreatedUtc;
        var integration = TimeSpan.Zero;
        for (var index = candidates.Count - 1; index >= 0 && selected.Count < options.MaximumFrameCount; index--)
        {
            var candidate = candidates[index];
            if (options.MaximumAgeMilliseconds is { } maximumAge &&
                (newest - candidate.CreatedUtc).TotalMilliseconds > maximumAge)
            {
                break;
            }
            if (options.MaximumIntegrationMilliseconds is { } maximumIntegration && selected.Count > 0 &&
                (integration + candidate.Integration).TotalMilliseconds > maximumIntegration)
            {
                break;
            }
            selected.Add(candidate);
            integration += candidate.Integration;
        }
        selected.Reverse();
        return selected;
    }

    private static bool TryValidateCompatibility(
        List<ProcessingArtifact> selected,
        out IReadOnlyList<FrameLayoutDescriptor> layouts)
    {
        var values = new List<FrameLayoutDescriptor>(selected.Count);
        foreach (var source in selected)
        {
            if (!ProcessingRecipeSupport.TryValidateFrame(source, out var layout, out _) ||
                layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
            {
                layouts = [];
                return false;
            }
            values.Add(layout);
        }

        var firstLayout = values[0];
        var firstCompatibility = selected[0].Compatibility;
        var firstRecipeIdentity = selected[0].RecipeIdentitySha256;
        if (values.Any(layout => layout.Width != firstLayout.Width || layout.Height != firstLayout.Height ||
                layout.StrideBytes != firstLayout.StrideBytes || layout.PixelFormat != firstLayout.PixelFormat ||
                layout.ByteOrder != firstLayout.ByteOrder ||
                layout.SampleDepthBits != firstLayout.SampleDepthBits || layout.ContainerDepthBits != firstLayout.ContainerDepthBits ||
                layout.Packing != firstLayout.Packing || layout.CfaPattern != firstLayout.CfaPattern ||
                layout.BlackLevel != firstLayout.BlackLevel || layout.WhiteLevel != firstLayout.WhiteLevel ||
                layout.StoredCodeTransform != firstLayout.StoredCodeTransform ||
                layout.LevelCodeSpace != firstLayout.LevelCodeSpace || layout.Readout != firstLayout.Readout) ||
            selected.Any(source => source.Compatibility != firstCompatibility
                || !string.Equals(source.RecipeIdentitySha256, firstRecipeIdentity, StringComparison.OrdinalIgnoreCase)))
        {
            layouts = [];
            return false;
        }

        layouts = values;
        return true;
    }
}

internal sealed class ImageQualityRecipe : IProcessingRecipe
{
    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.ImageQuality, "1.0.0", ImageStatisticsCalculator.AlgorithmVersion,
        ProcessingOperationKind.Analyzer);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<Dictionary<string, JsonElement>>(options);
        if (parsed.Count != 0)
        {
            throw new ArgumentException("Image quality v1 has no options.", nameof(options));
        }
        return ProcessingRecipeSupport.Normalize(parsed);
    }

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        var input = ProcessingRecipeSupport.ResolveSingle(request, out var failure);
        if (input is null)
        {
            return ValueTask.FromResult(failure!);
        }
        if (!ProcessingRecipeSupport.TryValidateFrame(input, out var layout, out var layoutFailure))
        {
            return ValueTask.FromResult(layoutFailure!);
        }

        var statistics = ImageStatisticsCalculator.Calculate(
            layout.Width,
            layout.Height,
            layout.StrideBytes,
            layout.PixelFormat,
            input.Payload,
            layout.WhiteLevel is { } whiteLevel ? checked((ushort)Math.Round(whiteLevel)) : null,
            cancellationToken);
        var json = CaptureContractJson.Canonicalize(
            JsonSerializer.SerializeToElement(statistics, ProcessingRecipeSupport.SerializerOptions));
        var payload = JsonSerializer.SerializeToUtf8Bytes(json);
        var product = ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.Metadata,
            request.OutputVariant,
            "application/json",
            null,
            payload,
            identity,
            [new("image-statistics", ImageStatisticsCalculator.AlgorithmVersion)],
            [input],
            input.Integration,
            input.Compatibility);
        return ValueTask.FromResult(ProcessingOutcome.Produced(product));
    }
}

internal sealed class NoOpAnalyzerRecipe : IProcessingRecipe
{
    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.NoOpAnalyzer, "1.0.0", "no-op-analyzer-v1",
        ProcessingOperationKind.Analyzer);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<Dictionary<string, JsonElement>>(options);
        if (parsed.Count != 0)
        {
            throw new ArgumentException("No-op analyzer v1 has no options.", nameof(options));
        }
        return ProcessingRecipeSupport.Normalize(parsed);
    }

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = ProcessingRecipeSupport.ResolveSingle(request, out var failure);
        if (input is null)
        {
            return ValueTask.FromResult(failure!);
        }

        ReadOnlyMemory<byte> payload = "{\"status\":\"ok\"}"u8.ToArray();
        var product = ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.Metadata,
            request.OutputVariant,
            "application/json",
            null,
            payload,
            identity,
            [new("no-op", "v1")],
            [input],
            input.Integration,
            input.Compatibility);
        return ValueTask.FromResult(ProcessingOutcome.Produced(product));
    }
}

internal static class AnnotationOptionsValidation
{
    internal static void ValidateForProcessing(this AnnotationOptions options)
    {
        if (options.MarkRadius is < 0 or > 32 || options.LabelScale is < 1 or > 8 ||
            options.CardinalScale is < 1 or > 8 || options.ConstellationLineThickness is < 1 or > 8 ||
            !double.IsFinite(options.ConstellationLineOpacity) || options.ConstellationLineOpacity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}
