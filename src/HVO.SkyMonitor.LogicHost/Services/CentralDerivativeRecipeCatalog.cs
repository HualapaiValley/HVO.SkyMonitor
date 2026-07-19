using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralDerivativeRecipe(
    FrameArtifactRole SourceRole,
    FrameArtifactRole TargetRole,
    string RecipeVersion,
    string TargetVariant,
    string RecipeName,
    JsonElement Options,
    ProcessingInputSelector InputSelector,
    string RequestedRecipeIdentitySha256,
    int MaxAttempts,
    CentralDerivativeWindowDefinition? Window = null);

internal sealed record CentralDerivativeWindowDefinition(
    IReadOnlyList<CentralDerivativeWindowPosition> Positions,
    TimeSpan Timeout,
    CentralDerivativeWindowOutcome MissingInputOutcome);

internal sealed record CentralDerivativeWindowPosition(
    int SequenceOffset,
    bool IsRequired,
    ProcessingInputSelector Selector,
    CentralDerivativeCompatibilityMode CompatibilityMode = CentralDerivativeCompatibilityMode.Exact);

internal interface ICentralDerivativeRecipeCatalog
{
    IReadOnlyList<CentralDerivativeRecipe> GetRequiredRecipes(FrameArtifactRole sourceRole);
}

internal sealed class CentralDerivativeRecipeCatalog : ICentralDerivativeRecipeCatalog
{
    // These values are persisted by the existing migration; #100 will persist the requested identity alongside them.
    internal const string PreviewRecipeVersion = "central-preview-v1";
    internal const string AnnotatedPreviewRecipeVersion = "central-annotated-preview-v1";
    internal const string ImageQualityRecipeVersion = "central-image-quality-v1";
    internal const string RollingMeanRecipeVersion = "central-rolling-mean-v1";
    internal const string CloudAssessmentRecipeVersion = "central-cloud-assessment-v1";
    internal const string WeatherCloudOverlayRecipeVersion = "central-weather-cloud-overlay-v1";
    internal const string PreviewVariant = "central-preview";
    internal const string AnnotatedPreviewVariant = "central-annotated-preview";
    internal const string ImageQualityVariant = "central-image-quality";
    internal const string RollingMeanVariant = "central-rolling-mean";
    internal const string CloudAssessmentVariant = "cloud-assessment-v1";
    internal const string WeatherCloudOverlayVariant = "weather-cloud-overlay-v1";
    private static readonly JsonElement PreviewOptions = CaptureContractJson.SerializeToElement(new EncodedPreviewOptions());
    private static readonly JsonElement AnnotationOptions = CaptureContractJson.SerializeToElement(new AnnotationRecipeOptions());
    private static readonly JsonElement ImageQualityOptions = CaptureContractJson.SerializeToElement(new { });
    private static readonly JsonElement RollingMeanOptions = CaptureContractJson.SerializeToElement(new RollingMeanOptions());
    internal static readonly JsonElement CloudAssessmentOptions = CaptureContractJson.SerializeToElement(new CloudAssessmentOptions());
    internal static readonly JsonElement WeatherCloudOverlayOptions = CaptureContractJson.SerializeToElement(
        new WeatherCloudOverlayOptions());
    private static readonly ProcessingInputSelector RawInput = ProcessingInputSelector.Raw();
    internal static readonly string PreviewRequestedRecipeIdentity = BuiltInProcessingRecipes.CreateRequestedIdentity(
        BuiltInProcessingRecipes.EncodedPreview,
        PreviewOptions,
        RawInput).IdentitySha256;
    internal static readonly ProcessingInputSelector PreviewInput = ProcessingInputSelector.RecipeResult(
        FrameArtifactRole.Preview,
        PreviewVariant,
        PreviewRequestedRecipeIdentity);
    internal static readonly string AnnotatedPreviewRequestedRecipeIdentity = BuiltInProcessingRecipes.CreateRequestedIdentity(
        BuiltInProcessingRecipes.Annotation,
        AnnotationOptions,
        RawInput).IdentitySha256;
    internal static readonly string ImageQualityRequestedRecipeIdentity = BuiltInProcessingRecipes.CreateRequestedIdentity(
        BuiltInProcessingRecipes.ImageQuality,
        ImageQualityOptions,
        RawInput).IdentitySha256;
    internal static readonly string RollingMeanRequestedRecipeIdentity = BuiltInProcessingRecipes.CreateRequestedIdentity(
        BuiltInProcessingRecipes.RollingMean,
        RollingMeanOptions,
        RawInput).IdentitySha256;
    internal static readonly string CloudAssessmentRequestedRecipeIdentity = BuiltInProcessingRecipes.CreateRequestedIdentity(
        BuiltInProcessingRecipes.CloudAssessment,
        CloudAssessmentOptions,
        RawInput).IdentitySha256;
    internal static readonly string WeatherCloudOverlayRequestedRecipeIdentity = BuiltInProcessingRecipes.CreateRequestedIdentity(
        BuiltInProcessingRecipes.WeatherCloudOverlay,
        WeatherCloudOverlayOptions,
        PreviewInput).IdentitySha256;
    internal const int DefaultMaxAttempts = 5;

    private static readonly IReadOnlyList<CentralDerivativeRecipe> RawRecipes =
    [
        new(FrameArtifactRole.Raw, FrameArtifactRole.Preview, PreviewRecipeVersion, PreviewVariant,
            BuiltInProcessingRecipes.EncodedPreview, PreviewOptions, RawInput,
            PreviewRequestedRecipeIdentity, DefaultMaxAttempts),
        new(FrameArtifactRole.Raw, FrameArtifactRole.AnnotatedPreview, AnnotatedPreviewRecipeVersion, AnnotatedPreviewVariant,
            BuiltInProcessingRecipes.Annotation, AnnotationOptions, RawInput,
            AnnotatedPreviewRequestedRecipeIdentity, DefaultMaxAttempts),
        new(FrameArtifactRole.Raw, FrameArtifactRole.Metadata, ImageQualityRecipeVersion, ImageQualityVariant,
            BuiltInProcessingRecipes.ImageQuality, ImageQualityOptions, RawInput,
            ImageQualityRequestedRecipeIdentity, DefaultMaxAttempts),
        new(FrameArtifactRole.Raw, FrameArtifactRole.Metadata, CloudAssessmentRecipeVersion, CloudAssessmentVariant,
            BuiltInProcessingRecipes.CloudAssessment, CloudAssessmentOptions, RawInput,
            CloudAssessmentRequestedRecipeIdentity, DefaultMaxAttempts),
        new(FrameArtifactRole.Raw, FrameArtifactRole.Combined, RollingMeanRecipeVersion, RollingMeanVariant,
            BuiltInProcessingRecipes.RollingMean, RollingMeanOptions, RawInput,
            RollingMeanRequestedRecipeIdentity, DefaultMaxAttempts,
            new CentralDerivativeWindowDefinition(
                new[] { -2, -1, 0, 1, 2 }.Select(offset => new CentralDerivativeWindowPosition(
                    offset, IsRequired: true, RawInput)).ToArray(),
                TimeSpan.FromMinutes(5),
                CentralDerivativeWindowOutcome.Skip))
    ];

    internal static readonly CentralDerivativeRecipe WeatherCloudOverlayRecipe = new(
        FrameArtifactRole.Preview,
        FrameArtifactRole.AnnotatedPreview,
        WeatherCloudOverlayRecipeVersion,
        WeatherCloudOverlayVariant,
        BuiltInProcessingRecipes.WeatherCloudOverlay,
        WeatherCloudOverlayOptions,
        PreviewInput,
        WeatherCloudOverlayRequestedRecipeIdentity,
        DefaultMaxAttempts);

    public IReadOnlyList<CentralDerivativeRecipe> GetRequiredRecipes(FrameArtifactRole sourceRole)
        => sourceRole == FrameArtifactRole.Raw ? RawRecipes : [];
}

internal static class CentralDerivativeJobIdentity
{
    private const string Schema = "hvo-central-derivative-request-v2";
    private const string WindowSchema = "hvo-central-derivative-window-request-v1";

    public static string CreateRequestIdentity(
        Guid sourceDevicePublicId,
        Guid sourceArtifactId,
        CentralDerivativeRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.Window is not null)
        {
            var positions = OrderWindowPositions(recipe.Window.Positions).Select((position, ordinal) => string.Join('|',
                ordinal,
                position.SequenceOffset,
                position.IsRequired,
                position.CompatibilityMode,
                CreateSelectorIdentity(position.Selector)));
            var windowValue = string.Join('\n',
                WindowSchema,
                sourceDevicePublicId.ToString("N"),
                sourceArtifactId.ToString("N"),
                recipe.TargetRole.ToString(),
                recipe.TargetVariant,
                recipe.RecipeVersion,
                recipe.RequestedRecipeIdentitySha256.ToUpperInvariant(),
                recipe.Window.Timeout.Ticks,
                recipe.Window.MissingInputOutcome.ToString(),
                string.Join('\n', positions));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(windowValue)));
        }
        var value = string.Join('\n',
            Schema,
            sourceDevicePublicId.ToString("N"),
            sourceArtifactId.ToString("N"),
            recipe.TargetRole.ToString(),
            recipe.TargetVariant,
            recipe.RequestedRecipeIdentitySha256.ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    internal static IOrderedEnumerable<CentralDerivativeWindowPosition> OrderWindowPositions(
        IEnumerable<CentralDerivativeWindowPosition> positions)
        => positions.OrderBy(position => position.SequenceOffset)
            .ThenBy(position => CreateSelectorIdentity(position.Selector), StringComparer.Ordinal)
            .ThenByDescending(position => position.IsRequired)
            .ThenBy(position => position.CompatibilityMode);

    private static string CreateSelectorIdentity(ProcessingInputSelector selector)
        => CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(selector)).GetRawText();

    public static string CreateReprocessRequestIdentity(
        Guid predecessorJobId,
        Guid sourceDevicePublicId,
        Guid sourceArtifactId,
        CentralDerivativeRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var value = string.Join('\n',
            "hvo-central-derivative-reprocess-v1",
            predecessorJobId.ToString("N"),
            sourceDevicePublicId.ToString("N"),
            sourceArtifactId.ToString("N"),
            recipe.TargetRole.ToString(),
            recipe.TargetVariant,
            recipe.RequestedRecipeIdentitySha256.ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
