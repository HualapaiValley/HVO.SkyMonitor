using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using System.Text.Json;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralDerivativeRecipe(
    FrameArtifactRole SourceRole,
    FrameArtifactRole TargetRole,
    string RecipeVersion,
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
    internal static readonly string PreviewRequestedRecipeIdentity = BuiltInProcessingRecipes.CreateRequestedIdentity(
        BuiltInProcessingRecipes.EncodedPreview,
        JsonSerializer.SerializeToElement(new EncodedPreviewOptions()),
        ProcessingInputSelector.Raw()).IdentitySha256;
    internal static readonly string AnnotatedPreviewRequestedRecipeIdentity = BuiltInProcessingRecipes.CreateRequestedIdentity(
        BuiltInProcessingRecipes.Annotation,
        JsonSerializer.SerializeToElement(new AnnotationRecipeOptions()),
        ProcessingInputSelector.Raw()).IdentitySha256;
    internal const int DefaultMaxAttempts = 5;

    private static readonly IReadOnlyList<CentralDerivativeRecipe> RawRecipes =
    [
        new(FrameArtifactRole.Raw, FrameArtifactRole.Preview, PreviewRecipeVersion,
            PreviewRequestedRecipeIdentity, DefaultMaxAttempts),
        new(FrameArtifactRole.Raw, FrameArtifactRole.AnnotatedPreview, AnnotatedPreviewRecipeVersion,
            AnnotatedPreviewRequestedRecipeIdentity, DefaultMaxAttempts)
    ];

    public IReadOnlyList<CentralDerivativeRecipe> GetRequiredRecipes(FrameArtifactRole sourceRole)
        => sourceRole == FrameArtifactRole.Raw ? RawRecipes : [];
}
