using HVO.SkyMonitor.AgentCore;
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
    int MaxAttempts);

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
    internal const string PreviewVariant = "central-preview";
    internal const string AnnotatedPreviewVariant = "central-annotated-preview";
    internal const string ImageQualityVariant = "central-image-quality";
    private static readonly JsonElement PreviewOptions = CaptureContractJson.SerializeToElement(new EncodedPreviewOptions());
    private static readonly JsonElement AnnotationOptions = CaptureContractJson.SerializeToElement(new AnnotationRecipeOptions());
    private static readonly JsonElement ImageQualityOptions = CaptureContractJson.SerializeToElement(new { });
    private static readonly ProcessingInputSelector RawInput = ProcessingInputSelector.Raw();
    internal static readonly string PreviewRequestedRecipeIdentity = BuiltInProcessingRecipes.CreateRequestedIdentity(
        BuiltInProcessingRecipes.EncodedPreview,
        PreviewOptions,
        RawInput).IdentitySha256;
    internal static readonly string AnnotatedPreviewRequestedRecipeIdentity = BuiltInProcessingRecipes.CreateRequestedIdentity(
        BuiltInProcessingRecipes.Annotation,
        AnnotationOptions,
        RawInput).IdentitySha256;
    internal static readonly string ImageQualityRequestedRecipeIdentity = BuiltInProcessingRecipes.CreateRequestedIdentity(
        BuiltInProcessingRecipes.ImageQuality,
        ImageQualityOptions,
        RawInput).IdentitySha256;
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
            ImageQualityRequestedRecipeIdentity, DefaultMaxAttempts)
    ];

    public IReadOnlyList<CentralDerivativeRecipe> GetRequiredRecipes(FrameArtifactRole sourceRole)
        => sourceRole == FrameArtifactRole.Raw ? RawRecipes : [];
}

internal static class CentralDerivativeJobIdentity
{
    private const string Schema = "hvo-central-derivative-request-v2";

    public static string CreateRequestIdentity(
        Guid sourceDevicePublicId,
        Guid sourceArtifactId,
        CentralDerivativeRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var value = string.Join('\n',
            Schema,
            sourceDevicePublicId.ToString("N"),
            sourceArtifactId.ToString("N"),
            recipe.TargetRole.ToString(),
            recipe.TargetVariant,
            recipe.RequestedRecipeIdentitySha256.ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
