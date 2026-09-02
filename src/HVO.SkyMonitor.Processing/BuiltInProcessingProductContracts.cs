using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using System.Text;
using System.Text.Json;
using static HVO.SkyMonitor.Processing.BuiltInProcessingRecipes;

namespace HVO.SkyMonitor.Processing;

internal static class BuiltInProcessingProductContracts
{
    internal static ProcessingProductContract Create(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        var primary = ProcessingRecipeSupport.ResolveSingle(request, out _);
        var role = request.RecipeName switch
        {
            LinearNormalization or ReferenceCalibration => FrameArtifactRole.Calibrated,
            EncodedPreview => FrameArtifactRole.Preview,
            JpegEncoding when primary is not null => primary.Role,
            Annotation or WeatherCloudOverlay => FrameArtifactRole.AnnotatedPreview,
            RollingMean => FrameArtifactRole.Combined,
            ImageQuality or NoOpAnalyzer or CloudAssessment or ProjectedScene => FrameArtifactRole.Metadata,
            _ => throw new InvalidOperationException("The built-in recipe output contract is unavailable.")
        };
        IReadOnlyList<ProcessingArtifact> sources = request.RecipeName switch
        {
            RollingMean => RollingMeanRecipe.SelectWindow(
                ProcessingRecipeSupport.ResolveMany(request, out _),
                ProcessingRecipeSupport.ParseOptions<RollingMeanOptions>(
                    identity.Descriptor.Options.GetProperty("parameters"))),
            Annotation when primary is not null =>
                [primary, .. ResolveArtifactAuxiliaries(request)],
            CloudAssessment when primary is not null =>
                ResolveOptionalAuxiliarySources(request, primary, "clear-reference"),
            WeatherCloudOverlay when primary is not null =>
                [primary, ResolveRequiredArtifactAuxiliary(request, "assessment")],
            ReferenceCalibration when primary is not null =>
                ResolveReferenceCalibrationSources(request, primary),
            _ when primary is not null => [primary],
            _ => throw new InvalidOperationException("The built-in recipe input contract is unavailable.")
        };
        var sourceIds = sources.Select(static source => source.ArtifactId).ToArray();
        if (sourceIds.Length == 0 || sourceIds.Any(static id => id == Guid.Empty) ||
            sourceIds.Distinct().Count() != sourceIds.Length)
        {
            throw new InvalidOperationException("The built-in recipe source lineage is invalid.");
        }
        var details = CreateDetails(request, identity, primary!, sources);
        return new ProcessingProductContract(
            role,
            sourceIds,
            details.MediaType,
            details.RequiresLayout,
            details.ExactLayout,
            details.EncodedLayout,
            role == FrameArtifactRole.Metadata ? ProcessingProductKind.Metadata : ProcessingProductKind.PixelData,
            details.SchemaVersion,
            details.RequiresContentIdentity,
            details.ContentIdentitySha256,
            details.Algorithms,
            details.TotalIntegration,
            details.Compatibility,
            details.ExpectedPayloadSha256);
    }

    internal static bool PayloadMatches(
        ProcessingExecutionRequest request,
        ProcessingProductContract contract,
        ReadOnlyMemory<byte> payload,
        string? contentIdentitySha256,
        string recipeIdentitySha256,
        IReadOnlyList<ProcessingAlgorithmIdentity> algorithms,
        CancellationToken cancellationToken)
    {
        if (contract.ExpectedPayloadSha256 is not null && !string.Equals(
            ProcessingIdentity.ComputePayloadSha256(payload),
            contract.ExpectedPayloadSha256,
            StringComparison.Ordinal))
        {
            return false;
        }
        if (contract.EncodedLayout is { } encodedLayout)
        {
            try
            {
                var info = JpegImageCodec.InspectJpeg(payload);
                if (info.Width != encodedLayout.Width || info.Height != encodedLayout.Height ||
                    info.PixelFormat != encodedLayout.PixelFormat)
                {
                    return false;
                }
                var decoded = JpegImageCodec.DecodeJpeg(payload, cancellationToken);
                if (decoded.Width != encodedLayout.Width || decoded.Height != encodedLayout.Height ||
                    decoded.PixelFormat != encodedLayout.PixelFormat)
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
            {
                return false;
            }
        }
        if (string.Equals(request.RecipeName, ImageQuality, StringComparison.Ordinal))
        {
            try
            {
                var statistics = JsonSerializer.Deserialize<ImageStatisticsResult>(
                    payload.Span,
                    ProcessingRecipeSupport.SerializerOptions);
                var input = ProcessingRecipeSupport.ResolveSingle(request, out _);
                var layout = input?.Layout;
                if (statistics is null || input is null || layout is null)
                {
                    return false;
                }
                var pixelCount = checked((long)layout.Width * layout.Height);
                var channelCount = layout.PixelFormat == CameraPixelFormat.Rgb24 ? 3 : 1;
                var sampleCount = checked(pixelCount * channelCount);
                var formatMaximum = layout.PixelFormat is CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24
                    ? byte.MaxValue
                    : ushort.MaxValue;
                var saturationLevel = layout.WhiteLevel is { } whiteLevel
                    ? checked((ushort)Math.Round(whiteLevel))
                    : (ushort)formatMaximum;
                var count = checked((ulong)sampleCount);
                var minimumSum = statistics.Minimum == statistics.Maximum
                    ? checked((ulong)statistics.Minimum * count)
                    : checked((ulong)statistics.Minimum * (count - 1) + statistics.Maximum);
                var maximumSum = statistics.Minimum == statistics.Maximum
                    ? minimumSum
                    : checked((ulong)statistics.Maximum * (count - 1) + statistics.Minimum);
                var minimumSquare = checked((ulong)statistics.Minimum * statistics.Minimum);
                var maximumSquare = checked((ulong)statistics.Maximum * statistics.Maximum);
                var minimumSumOfSquares = statistics.Minimum == statistics.Maximum
                    ? checked(minimumSquare * count)
                    : checked(minimumSquare * (count - 1) + maximumSquare);
                var maximumSumOfSquares = statistics.Minimum == statistics.Maximum
                    ? minimumSumOfSquares
                    : checked(maximumSquare * (count - 1) + minimumSquare);
                if (statistics.PixelCount != pixelCount || statistics.ChannelCount != channelCount ||
                    statistics.SampleCount != sampleCount || statistics.Minimum > statistics.Maximum ||
                    statistics.Maximum > formatMaximum || statistics.Sum < minimumSum || statistics.Sum > maximumSum ||
                    statistics.SumOfSquares < minimumSumOfSquares || statistics.SumOfSquares > maximumSumOfSquares ||
                    statistics.ZeroCount is < 0 || statistics.ZeroCount > sampleCount ||
                    statistics.SaturatedCount is < 0 || statistics.SaturatedCount > sampleCount ||
                    (statistics.Minimum == 0) != (statistics.ZeroCount > 0) ||
                    (statistics.Maximum < saturationLevel || statistics.Minimum > saturationLevel) &&
                    statistics.SaturatedCount != 0 ||
                    (statistics.Minimum == saturationLevel || statistics.Maximum == saturationLevel) &&
                    statistics.SaturatedCount == 0 ||
                    statistics.Minimum == statistics.Maximum && statistics.Minimum == saturationLevel &&
                    statistics.SaturatedCount != sampleCount ||
                    !string.Equals(
                        statistics.AlgorithmVersion,
                        ImageStatisticsCalculator.AlgorithmVersion,
                        StringComparison.Ordinal))
                {
                    return false;
                }
                var canonical = Encoding.UTF8.GetBytes(CaptureContractJson.Canonicalize(
                    JsonSerializer.SerializeToElement(statistics, ProcessingRecipeSupport.SerializerOptions)).GetRawText());
                if (!payload.Span.SequenceEqual(canonical))
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is JsonException or OverflowException)
            {
                return false;
            }
        }
        if (string.Equals(request.RecipeName, CloudAssessment, StringComparison.Ordinal))
        {
            var parsed = CloudAssessmentJson.Parse(payload);
            if (!parsed.Validation.IsValid || parsed.Assessment is not { } assessment ||
                !string.Equals(
                    assessment.AssessmentIdentitySha256,
                    contentIdentitySha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    assessment.RecipeIdentitySha256,
                    recipeIdentitySha256,
                    StringComparison.Ordinal) ||
                !assessment.Algorithms.SequenceEqual(algorithms))
            {
                return false;
            }
        }
        return true;
    }

    private static ProductDetails CreateDetails(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        ProcessingArtifact primary,
        IReadOnlyList<ProcessingArtifact> sources) => request.RecipeName switch
        {
            LinearNormalization => new(
                "application/x-hvo-linear-frame",
                true,
                ProcessingRecipeSupport.CreatePackedLayout(primary.Layout!),
                null,
                false,
                null,
                [new("linear-normalization", "none-v1")],
                primary.Integration,
                primary.Compatibility,
                null,
                null),
            EncodedPreview => CreateEncodedPreviewDetails(identity, primary),
            JpegEncoding => CreateJpegDetails(identity, primary),
            Annotation => CreateAnnotationDetails(identity, primary),
            RollingMean => new(
                "application/x-hvo-linear-frame",
                true,
                ProcessingRecipeSupport.CreatePackedLayout(sources[0].Layout!),
                null,
                false,
                null,
                [new("linear-mean", Linear16ArithmeticMean.AlgorithmVersion)],
                TimeSpan.FromTicks(sources.Sum(static source => source.Integration.Ticks)),
                sources[^1].Compatibility,
                null,
                null),
            ImageQuality => MetadataDetails(
                "application/json",
                [new("image-statistics", ImageStatisticsCalculator.AlgorithmVersion)],
                primary),
            NoOpAnalyzer => MetadataDetails(
                "application/json",
                [new("no-op", "v1")],
                primary,
                ProcessingIdentity.ComputePayloadSha256("{\"status\":\"ok\"}"u8.ToArray())),
            CloudAssessment => CreateCloudAssessmentDetails(primary, sources),
            WeatherCloudOverlay => CreateWeatherCloudOverlayDetails(identity, primary),
            ReferenceCalibration => CreateReferenceCalibrationDetails(request, primary),
            ProjectedScene => CreateProjectedSceneDetails(request, primary),
            _ => throw new InvalidOperationException("The built-in recipe output details are unavailable.")
        };

    private static ProductDetails CreateEncodedPreviewDetails(
        ProcessingRecipeIdentity identity,
        ProcessingArtifact primary)
    {
        var options = ProcessingRecipeSupport.ParseOptions<EncodedPreviewOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        var layout = primary.Layout!;
        var algorithms = new List<ProcessingAlgorithmIdentity>();
        var format = layout.PixelFormat;
        switch (layout.PixelFormat)
        {
            case CameraPixelFormat.Mono16:
                format = CameraPixelFormat.Mono8;
                algorithms.Add(new("display-stretch", Mono16DisplayStretch.AlgorithmVersion));
                break;
            case CameraPixelFormat.BayerRggb16:
                format = CameraPixelFormat.Rgb24;
                algorithms.Add(new("display-stretch", Mono16DisplayStretch.AlgorithmVersion));
                algorithms.Add(new("rggb-demosaic", BayerRggb16Demosaicer.AlgorithmVersion));
                break;
            case CameraPixelFormat.Mono8:
            case CameraPixelFormat.Rgb24:
                algorithms.Add(new("row-packing", "packed-copy-v1"));
                break;
            default:
                throw new InvalidOperationException("The encoded-preview output format is unavailable.");
        }
        var packed = string.Equals(options.OutputEncoding, "Packed", StringComparison.Ordinal);
        if (!packed)
        {
            algorithms.Add(new("jpeg", JpegImageCodec.AlgorithmVersion));
        }
        return PixelDetails(
            packed ? "application/x-hvo-packed-image" : JpegImageCodec.MediaType,
            packed,
            packed ? ProcessingRecipeSupport.CreatePackedLayout(layout.Width, layout.Height, format) : null,
            algorithms,
            primary,
            packed ? null : ProcessingRecipeSupport.CreatePackedLayout(layout.Width, layout.Height, format));
    }

    private static ProductDetails CreateJpegDetails(
        ProcessingRecipeIdentity identity,
        ProcessingArtifact primary)
    {
        var options = ProcessingRecipeSupport.ParseOptions<JpegEncodingOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        var layout = primary.Layout!;
        var maximumDimension = options.MaximumDimension ?? Math.Max(layout.Width, layout.Height);
        var algorithms = new List<ProcessingAlgorithmIdentity>();
        var scale = Math.Min(1d, Math.Min(
            (double)maximumDimension / layout.Width,
            (double)maximumDimension / layout.Height));
        var width = Math.Max(1, (int)Math.Floor(layout.Width * scale));
        var height = Math.Max(1, (int)Math.Floor(layout.Height * scale));
        if (layout.PixelFormat == CameraPixelFormat.BayerRggb16 && scale < 1)
        {
            width -= width > 1 ? width % 2 : 0;
            height -= height > 1 ? height % 2 : 0;
        }
        if (width != layout.Width || height != layout.Height)
        {
            algorithms.Add(new("downsample", PackedImageDownsampler.AlgorithmVersion));
        }
        algorithms.Add(new("jpeg", JpegImageCodec.AlgorithmVersion));
        return PixelDetails(
            JpegImageCodec.MediaType,
            false,
            null,
            algorithms,
            primary,
            ProcessingRecipeSupport.CreatePackedLayout(width, height, layout.PixelFormat));
    }

    private static ProductDetails CreateAnnotationDetails(
        ProcessingRecipeIdentity identity,
        ProcessingArtifact primary)
    {
        var options = ProcessingRecipeSupport.ParseOptions<AnnotationRecipeOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        var algorithms = CreateDisplayAlgorithms(primary, out var exactLayout);
        algorithms.Add(new("annotation-renderer", AnnotationRenderer.AlgorithmVersion));
        var packed = string.Equals(options.OutputEncoding, "Packed", StringComparison.Ordinal);
        if (!packed)
        {
            algorithms.Add(new("jpeg", JpegImageCodec.AlgorithmVersion));
        }
        return PixelDetails(
            packed ? "application/x-hvo-packed-image" : JpegImageCodec.MediaType,
            packed,
            packed ? exactLayout : null,
            algorithms,
            primary,
            packed ? null : exactLayout);
    }

    private static ProductDetails CreateCloudAssessmentDetails(
        ProcessingArtifact primary,
        IReadOnlyList<ProcessingArtifact> sources)
    {
        var algorithms = new List<ProcessingAlgorithmIdentity> { CloudAssessmentRecipe.PolicyAlgorithm };
        if (sources.Count == 2 && primary.Layout is { } layout &&
            CloudAssessmentRecipe.InputsAreCompatible(primary, layout, sources[1]) &&
            CloudAssessmentRecipe.CreateCalibration(layout, primary.Compatibility) is not null)
        {
            algorithms.Add(CloudAssessmentRecipe.TransmissionAlgorithm);
        }
        return new ProductDetails(
            StructuredProcessingProductContracts.CloudAssessmentMediaType,
            false,
            null,
            CloudAssessmentV1.CurrentSchemaVersion,
            true,
            null,
            algorithms,
            TimeSpan.FromTicks(sources.Sum(static source => source.Integration.Ticks)),
            primary.Compatibility,
            null,
            null);
    }

    private static ProductDetails CreateWeatherCloudOverlayDetails(
        ProcessingRecipeIdentity identity,
        ProcessingArtifact primary)
    {
        var options = ProcessingRecipeSupport.ParseOptions<WeatherCloudOverlayOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        var algorithms = CreatePackedDisplayAlgorithms(primary, out var exactLayout);
        algorithms.Add(new("weather-cloud-overlay", WeatherCloudOverlayRenderer.AlgorithmVersion));
        var packed = string.Equals(options.OutputEncoding, "Packed", StringComparison.Ordinal);
        if (!packed)
        {
            algorithms.Add(new("jpeg", JpegImageCodec.AlgorithmVersion));
        }
        return PixelDetails(
            packed ? "application/x-hvo-packed-image" : JpegImageCodec.MediaType,
            packed,
            packed ? exactLayout : null,
            algorithms,
            primary,
            packed ? null : exactLayout);
    }

    private static ProductDetails CreateReferenceCalibrationDetails(
        ProcessingExecutionRequest request,
        ProcessingArtifact primary)
    {
        var profileInput = request.AuxiliaryInputs!.Single(input =>
            string.Equals(input.Name, "calibration-profile", StringComparison.OrdinalIgnoreCase));
        var profile = ReferenceCalibrationProfileJson.Parse(profileInput.Payload.Span)
            ?? throw new InvalidOperationException("The reference calibration profile contract is unavailable.");
        var defect = profile.References.Single(reference =>
            string.Equals(reference.Kind, CalibrationReferenceKinds.Defect, StringComparison.Ordinal));
        return new ProductDetails(
            "application/x-hvo-linear-frame",
            true,
            CalibrationMasterBuilder.CreateNormalizedLayout(primary.Layout!),
            null,
            false,
            null,
            [
                new("calibration-normalization", CalibrationMasterBuilder.NormalizationAlgorithmVersion),
                new("linear16-reference-calibration", Linear16ReferenceCalibration.AlgorithmVersion)
            ],
            primary.Integration,
            primary.Compatibility with
            {
                Calibration = profileInput.IdentitySha256!,
                Mask = defect.PayloadSha256
            },
            null,
            null);
    }

    private static ProductDetails CreateProjectedSceneDetails(
        ProcessingExecutionRequest request,
        ProcessingArtifact primary)
    {
        var scene = request.AuxiliaryInputs!.Single(input =>
            string.Equals(input.Name, ProjectedSceneRecipe.AuxiliaryInputName, StringComparison.Ordinal));
        return new ProductDetails(
            ProjectedSceneRecipe.MediaType,
            false,
            null,
            scene.SchemaVersion,
            true,
            scene.IdentitySha256,
            [new("projected-scene-contract", "1.0.0")],
            primary.Integration,
            primary.Compatibility,
            null,
            ProcessingIdentity.ComputePayloadSha256(scene.Payload));
    }

    private static ProductDetails MetadataDetails(
        string mediaType,
        IReadOnlyList<ProcessingAlgorithmIdentity> algorithms,
        ProcessingArtifact primary,
        string? expectedPayloadSha256 = null) => new(
            mediaType,
            false,
            null,
            null,
            false,
            null,
            algorithms,
            primary.Integration,
            primary.Compatibility,
            null,
            expectedPayloadSha256);

    private static ProductDetails PixelDetails(
        string mediaType,
        bool requiresLayout,
        FrameLayoutDescriptor? exactLayout,
        IReadOnlyList<ProcessingAlgorithmIdentity> algorithms,
        ProcessingArtifact primary,
        FrameLayoutDescriptor? encodedLayout = null) => new(
            mediaType,
            requiresLayout,
            exactLayout,
            null,
            false,
            null,
            algorithms,
            primary.Integration,
            primary.Compatibility,
            encodedLayout,
            null);

    private static List<ProcessingAlgorithmIdentity> CreateDisplayAlgorithms(
        ProcessingArtifact primary,
        out FrameLayoutDescriptor? exactLayout)
    {
        if (string.Equals(primary.MediaType, JpegImageCodec.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            var info = JpegImageCodec.InspectJpeg(primary.Payload);
            exactLayout = ProcessingRecipeSupport.CreatePackedLayout(info.Width, info.Height, info.PixelFormat);
            return [new("jpeg-decode", JpegImageCodec.AlgorithmVersion)];
        }
        var layout = primary.Layout!;
        var algorithms = new List<ProcessingAlgorithmIdentity>();
        var format = layout.PixelFormat;
        switch (layout.PixelFormat)
        {
            case CameraPixelFormat.Mono16:
                format = CameraPixelFormat.Mono8;
                algorithms.Add(new("display-stretch", Mono16DisplayStretch.AlgorithmVersion));
                break;
            case CameraPixelFormat.BayerRggb16:
                format = CameraPixelFormat.Rgb24;
                algorithms.Add(new("display-stretch", Mono16DisplayStretch.AlgorithmVersion));
                algorithms.Add(new("rggb-demosaic", BayerRggb16Demosaicer.AlgorithmVersion));
                break;
            case CameraPixelFormat.Mono8:
            case CameraPixelFormat.Rgb24:
                algorithms.Add(new("row-packing", "packed-copy-v1"));
                break;
            default:
                throw new InvalidOperationException("The annotation display contract is unavailable.");
        }
        exactLayout = ProcessingRecipeSupport.CreatePackedLayout(layout.Width, layout.Height, format);
        return algorithms;
    }

    private static List<ProcessingAlgorithmIdentity> CreatePackedDisplayAlgorithms(
        ProcessingArtifact primary,
        out FrameLayoutDescriptor? exactLayout)
    {
        if (string.Equals(primary.MediaType, JpegImageCodec.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            var info = JpegImageCodec.InspectJpeg(primary.Payload);
            exactLayout = ProcessingRecipeSupport.CreatePackedLayout(info.Width, info.Height, info.PixelFormat);
            return [new("jpeg-decode", JpegImageCodec.AlgorithmVersion)];
        }
        var layout = primary.Layout!;
        exactLayout = ProcessingRecipeSupport.CreatePackedLayout(layout.Width, layout.Height, layout.PixelFormat);
        return [new("row-packing", "packed-copy-v1")];
    }

    private sealed record ProductDetails(
        string MediaType,
        bool RequiresLayout,
        FrameLayoutDescriptor? ExactLayout,
        string? SchemaVersion,
        bool RequiresContentIdentity,
        string? ContentIdentitySha256,
        IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms,
        TimeSpan TotalIntegration,
        ProcessingCompatibilityIdentity Compatibility,
        FrameLayoutDescriptor? EncodedLayout,
        string? ExpectedPayloadSha256);

    private static ProcessingArtifact[] ResolveArtifactAuxiliaries(ProcessingExecutionRequest request) =>
        (request.AuxiliaryInputs ?? [])
            .Where(static auxiliary => auxiliary.Kind == ProcessingAuxiliaryInputKind.Artifact)
            .Select(auxiliary => ResolveRequiredArtifactAuxiliary(request, auxiliary.Name))
            .ToArray();

    private static IReadOnlyList<ProcessingArtifact> ResolveOptionalAuxiliarySources(
        ProcessingExecutionRequest request,
        ProcessingArtifact primary,
        string name)
    {
        var auxiliary = request.AuxiliaryInputs?.SingleOrDefault(input =>
            string.Equals(input.Name, name, StringComparison.Ordinal));
        if (auxiliary is null)
        {
            return [primary];
        }
        if (auxiliary.Kind != ProcessingAuxiliaryInputKind.Artifact || auxiliary.Selector is null)
        {
            throw new InvalidOperationException("The built-in recipe auxiliary input contract is unavailable.");
        }
        var matches = request.Inputs.Where(input =>
            input.ArtifactId != primary.ArtifactId &&
            (auxiliary.ArtifactId is null || input.ArtifactId == auxiliary.ArtifactId) &&
            ProcessingRecipeSupport.Matches(input, auxiliary.Selector)).ToArray();
        return matches.Length switch
        {
            0 => [primary],
            1 => [primary, matches[0]],
            _ => throw new InvalidOperationException("The built-in recipe auxiliary source is ambiguous.")
        };
    }

    private static ProcessingArtifact ResolveRequiredArtifactAuxiliary(
        ProcessingExecutionRequest request,
        string name,
        Guid? excludedArtifactId = null)
    {
        var auxiliary = request.AuxiliaryInputs?.SingleOrDefault(input =>
            string.Equals(input.Name, name, StringComparison.Ordinal));
        if (auxiliary?.Kind != ProcessingAuxiliaryInputKind.Artifact || auxiliary.Selector is null)
        {
            throw new InvalidOperationException("The built-in recipe auxiliary input contract is unavailable.");
        }
        var matches = request.Inputs.Where(input =>
            input.ArtifactId != excludedArtifactId &&
            (auxiliary.ArtifactId is null || input.ArtifactId == auxiliary.ArtifactId) &&
            ProcessingRecipeSupport.Matches(input, auxiliary.Selector)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException("The built-in recipe auxiliary source is ambiguous or unavailable.");
    }

    private static IReadOnlyList<ProcessingArtifact> ResolveReferenceCalibrationSources(
        ProcessingExecutionRequest request,
        ProcessingArtifact primary)
    {
        var profileInput = request.AuxiliaryInputs?.SingleOrDefault(input =>
            string.Equals(input.Name, "calibration-profile", StringComparison.OrdinalIgnoreCase));
        var profile = profileInput?.Kind == ProcessingAuxiliaryInputKind.CanonicalJson
            ? ReferenceCalibrationProfileJson.Parse(profileInput.Payload.Span)
            : null;
        if (profile?.References is not { Count: 4 })
        {
            throw new InvalidOperationException("The reference calibration source contract is unavailable.");
        }
        var references = profile.References.ToDictionary(static reference => reference.Kind, StringComparer.Ordinal);
        return
        [
            primary,
            ResolveReference(CalibrationReferenceKinds.Bias),
            ResolveReference(CalibrationReferenceKinds.Dark),
            ResolveReference(CalibrationReferenceKinds.Flat),
            ResolveReference(CalibrationReferenceKinds.Defect)
        ];

        ProcessingArtifact ResolveReference(string kind)
        {
            if (!references.TryGetValue(kind, out var descriptor))
            {
                throw new InvalidOperationException("The reference calibration source contract is incomplete.");
            }
            var auxiliary = request.AuxiliaryInputs?.SingleOrDefault(input =>
                string.Equals(input.Name, $"{kind}-reference", StringComparison.OrdinalIgnoreCase));
            if (auxiliary?.Kind != ProcessingAuxiliaryInputKind.Artifact ||
                auxiliary.ArtifactId != descriptor.ArtifactId)
            {
                throw new InvalidOperationException("A reference calibration auxiliary source is unavailable.");
            }
            var matches = request.Inputs.Where(input => input.ArtifactId == descriptor.ArtifactId).ToArray();
            return matches.Length == 1
                ? matches[0]
                : throw new InvalidOperationException("A reference calibration source is ambiguous or unavailable.");
        }
    }
}
