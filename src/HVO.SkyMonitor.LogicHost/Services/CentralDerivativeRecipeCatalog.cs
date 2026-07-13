using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralDerivativeRecipe(
    FrameArtifactRole SourceRole,
    FrameArtifactRole TargetRole,
    string RecipeVersion,
    int MaxAttempts);

internal interface ICentralDerivativeRecipeCatalog
{
    IReadOnlyList<CentralDerivativeRecipe> GetRequiredRecipes(FrameArtifactRole sourceRole);
}

internal sealed class CentralDerivativeRecipeCatalog : ICentralDerivativeRecipeCatalog
{
    internal const string PreviewRecipeVersion = "central-preview-v1";
    internal const string AnnotatedPreviewRecipeVersion = "central-annotated-preview-v1";
    internal const int DefaultMaxAttempts = 5;

    private static readonly IReadOnlyList<CentralDerivativeRecipe> RawRecipes =
    [
        new(FrameArtifactRole.Raw, FrameArtifactRole.Preview, PreviewRecipeVersion, DefaultMaxAttempts),
        new(FrameArtifactRole.Raw, FrameArtifactRole.AnnotatedPreview, AnnotatedPreviewRecipeVersion, DefaultMaxAttempts)
    ];

    public IReadOnlyList<CentralDerivativeRecipe> GetRequiredRecipes(FrameArtifactRole sourceRole)
        => sourceRole == FrameArtifactRole.Raw ? RawRecipes : [];
}
