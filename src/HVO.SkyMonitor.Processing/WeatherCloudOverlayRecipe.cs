using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public sealed record WeatherCloudOverlayOptions(
    int LineThickness = 1,
    bool DrawLabels = true,
    int MaximumLabelCharacters = 32,
    int JpegQuality = JpegImageCodec.DefaultQuality,
    string OutputEncoding = "Packed");

internal sealed class WeatherCloudOverlayRecipe : IProcessingRecipe
{
    private static readonly JsonSerializerOptions EnvironmentSerializerOptions = CreateEnvironmentSerializerOptions();

    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.WeatherCloudOverlay,
        "1.0.0",
        "weather-cloud-overlay-v1",
        ProcessingOperationKind.Transform);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<WeatherCloudOverlayOptions>(options);
        if (parsed.LineThickness is < 1 or > 8 || parsed.MaximumLabelCharacters is < 0 or > 128 ||
            parsed.JpegQuality is < 1 or > 100 || parsed.OutputEncoding is not ("Packed" or "Jpeg"))
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
        var preview = ProcessingRecipeSupport.ResolveSingle(request, out var failure);
        if (preview is null)
        {
            return ValueTask.FromResult(failure!);
        }
        if (preview.Role != FrameArtifactRole.Preview)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidSelector,
                nameof(request.Input)));
        }
        var assessmentArtifact = ResolveAssessment(request, out failure);
        if (assessmentArtifact is null)
        {
            return ValueTask.FromResult(failure!);
        }
        var parsedAssessment = CloudAssessmentJson.Parse(assessmentArtifact.Payload);
        if (!parsedAssessment.Validation.IsValid || parsedAssessment.Assessment is not { } assessment)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidInput,
                nameof(request.AuxiliaryInputs)));
        }
        if (!string.Equals(
                assessmentArtifact.MediaType,
                "application/vnd.hvo.cloud-assessment+json",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                assessment.RecipeIdentitySha256,
                assessmentArtifact.RecipeIdentitySha256,
                StringComparison.Ordinal))
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.IncompatibleInput,
                nameof(request.AuxiliaryInputs)));
        }
        var environment = ResolveEnvironment(request, out failure);
        if (environment is null)
        {
            return ValueTask.FromResult(failure!);
        }
        var environmentInput = request.AuxiliaryInputs!.Single(input =>
            string.Equals(input.Name, "environment", StringComparison.Ordinal));
        if (!string.Equals(
            assessment.Environment.InputIdentitySha256,
            environmentInput.IdentitySha256,
            StringComparison.OrdinalIgnoreCase) ||
            (assessment.Environment with { InputIdentitySha256 = null }) !=
                (environment with { InputIdentitySha256 = null }))
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.IncompatibleInput,
                nameof(request.AuxiliaryInputs)));
        }

        var options = ProcessingRecipeSupport.ParseOptions<WeatherCloudOverlayOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        if (!TryCreateDisplay(preview, cancellationToken, out var display, out var algorithms, out failure))
        {
            return ValueTask.FromResult(failure!);
        }
        var tileCount = checked(assessment.Grid.Columns * assessment.Grid.Rows);
        var mask = assessment.Mask?.Bits.ToArray() ?? new byte[(tileCount + 7) / 8];
        var labels = CreateLabels(assessment, environment);
        var overlay = WeatherCloudOverlayRenderer.Render(
            display.Layout,
            display.Pixels,
            assessment.Grid.Columns,
            assessment.Grid.Rows,
            mask,
            labels,
            new WeatherCloudOverlayRenderOptions(
                options.LineThickness,
                DrawLabels: options.DrawLabels,
                MaximumLabelCharacters: options.MaximumLabelCharacters),
            cancellationToken);
        algorithms.Add(new ProcessingAlgorithmIdentity("weather-cloud-overlay", overlay.AlgorithmVersion));
        ReadOnlyMemory<byte> payload;
        string mediaType;
        FrameLayoutDescriptor? outputLayout;
        if (options.OutputEncoding == "Jpeg")
        {
            payload = JpegImageCodec.EncodeToJpeg(display.Layout, overlay.Pixels, options.JpegQuality, cancellationToken);
            mediaType = JpegImageCodec.MediaType;
            outputLayout = null;
            algorithms.Add(new ProcessingAlgorithmIdentity("jpeg", JpegImageCodec.AlgorithmVersion));
        }
        else
        {
            payload = overlay.Pixels;
            mediaType = "application/x-hvo-packed-image";
            outputLayout = ProcessingRecipeSupport.CreatePackedLayout(
                display.Layout.Width,
                display.Layout.Height,
                display.Layout.PixelFormat);
        }
        return ValueTask.FromResult(ProcessingOutcome.Produced(ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.AnnotatedPreview,
            request.OutputVariant,
            mediaType,
            outputLayout,
            payload,
            identity,
            algorithms,
            [preview, assessmentArtifact],
            preview.Integration,
            preview.Compatibility)));
    }

    private static ProcessingArtifact? ResolveAssessment(
        ProcessingExecutionRequest request,
        out ProcessingOutcome? failure)
    {
        var auxiliary = request.AuxiliaryInputs?.SingleOrDefault(input =>
            string.Equals(input.Name, "assessment", StringComparison.Ordinal));
        if (auxiliary?.Kind != ProcessingAuxiliaryInputKind.Artifact || auxiliary.Selector is null ||
            !ProcessingRecipeSupport.SelectorIsValid(auxiliary.Selector))
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidSelector,
                nameof(request.AuxiliaryInputs));
            return null;
        }
        var matches = request.Inputs.Where(input =>
            (auxiliary.ArtifactId is null || input.ArtifactId == auxiliary.ArtifactId) &&
            ProcessingRecipeSupport.Matches(input, auxiliary.Selector)).ToArray();
        if (matches.Length != 1 || matches[0].Role != FrameArtifactRole.Metadata || matches[0].Layout is not null)
        {
            failure = ProcessingOutcome.TerminalFailure(
                matches.Length > 1 ? ProcessingReasonCodes.AmbiguousInput : ProcessingReasonCodes.MissingInput,
                nameof(request.AuxiliaryInputs));
            return null;
        }
        failure = null;
        return matches[0];
    }

    private static CloudAssessmentEnvironmentV1? ResolveEnvironment(
        ProcessingExecutionRequest request,
        out ProcessingOutcome? failure)
    {
        var auxiliary = request.AuxiliaryInputs?.SingleOrDefault(input =>
            string.Equals(input.Name, "environment", StringComparison.Ordinal));
        if (auxiliary?.Kind != ProcessingAuxiliaryInputKind.CanonicalJson ||
            !string.Equals(auxiliary.SchemaVersion, CloudAssessmentEnvironmentV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidInput,
                nameof(request.AuxiliaryInputs));
            return null;
        }
        try
        {
            var environment = JsonSerializer.Deserialize<CloudAssessmentEnvironmentV1>(
                auxiliary.Payload.Span,
                EnvironmentSerializerOptions);
            if (environment is not null && EnvironmentIsValid(environment))
            {
                failure = null;
                return environment;
            }
        }
        catch (JsonException)
        {
        }
        failure = ProcessingOutcome.TerminalFailure(
            ProcessingReasonCodes.InvalidInput,
            nameof(request.AuxiliaryInputs));
        return null;
    }

    private static bool EnvironmentIsValid(CloudAssessmentEnvironmentV1 environment)
        => string.Equals(
               environment.SchemaVersion,
               CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
               StringComparison.Ordinal) &&
           Enum.IsDefined(environment.PrecipitationStatus) &&
           (environment.PrecipitationStatus is
                EnvironmentalObservationMatchStatus.Missing or EnvironmentalObservationMatchStatus.Contradictory
               ? environment.PrecipitationObservationId is null &&
                 environment.PrecipitationContentSha256 is null && !environment.PrecipitationDetected
               : environment.PrecipitationObservationId is not null &&
                 environment.PrecipitationContentSha256 is { Length: 64 } checksum && checksum.All(Uri.IsHexDigit));

    private static bool TryCreateDisplay(
        ProcessingArtifact preview,
        CancellationToken cancellationToken,
        out DisplayInput display,
        out List<ProcessingAlgorithmIdentity> algorithms,
        out ProcessingOutcome? failure)
    {
        algorithms = [];
        if (string.Equals(preview.MediaType, JpegImageCodec.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            if (preview.Layout is not null)
            {
                display = default;
                failure = ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidLayout, nameof(preview.Layout));
                return false;
            }
            var decoded = JpegImageCodec.DecodeJpeg(preview.Payload, cancellationToken);
            display = new DisplayInput(
                new ImageLayout(
                    decoded.Width,
                    decoded.Height,
                    decoded.PixelFormat,
                    checked(decoded.Width * ImageLayout.BytesPerPixel(decoded.PixelFormat))),
                decoded.PixelData);
            algorithms.Add(new ProcessingAlgorithmIdentity("jpeg-decode", JpegImageCodec.AlgorithmVersion));
            failure = null;
            return true;
        }
        if (!ProcessingRecipeSupport.TryValidateFrame(preview, out var layout, out failure) ||
            layout.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24))
        {
            display = default;
            failure ??= ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.UnsupportedFormat,
                nameof(preview.Layout));
            return false;
        }
        var pixels = ProcessingRecipeSupport.PackRows(layout, preview.Payload, cancellationToken);
        display = new DisplayInput(
            new ImageLayout(
                layout.Width,
                layout.Height,
                layout.PixelFormat,
                checked(layout.Width * ImageLayout.BytesPerPixel(layout.PixelFormat))),
            pixels);
        algorithms.Add(new ProcessingAlgorithmIdentity("row-packing", "packed-copy-v1"));
        failure = null;
        return true;
    }

    private static string[] CreateLabels(
        CloudAssessmentV1 assessment,
        CloudAssessmentEnvironmentV1 environment)
    {
        var coverage = assessment.CoverageMillionths is { } value
            ? string.Create(CultureInfo.InvariantCulture, $"Cloud {value / 10_000}.{value % 10_000 / 1_000}%")
            : "Cloud unavailable";
        return
        [
            coverage,
            $"Quality {assessment.Quality}",
            $"Precipitation {environment.PrecipitationStatus}"
        ];
    }

    private static JsonSerializerOptions CreateEnvironmentSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }

    private readonly record struct DisplayInput(ImageLayout Layout, ReadOnlyMemory<byte> Pixels);
}
