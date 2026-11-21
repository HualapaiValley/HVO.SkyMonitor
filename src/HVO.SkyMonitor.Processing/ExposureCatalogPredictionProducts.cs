using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing;

/// <summary>Produces and authenticates the independent, delivered-image catalog prediction for a centered exposure.</summary>
public static class ExposureCatalogPredictionProducts
{
    public const string StepAlias = "ExposureCatalogPrediction";
    public const string Variant = "exposure-catalog-prediction-v1";

    public static ProcessingExecutionRequest Request(ProcessingArtifact raw, ProjectedSceneV1 scene)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(scene);
        return Request(raw, scene, ProjectedSceneJson.Serialize(scene));
    }

    private static ProcessingExecutionRequest Request(ProcessingArtifact raw, ProjectedSceneV1 scene, ReadOnlyMemory<byte> payload)
        => new(BuiltInProcessingRecipes.ProjectedScene, JsonSerializer.SerializeToElement(new Dictionary<string, object>()),
            ProcessingInputSelector.Raw(raw.Variant), [raw], Variant, AuxiliaryInputs:
            [
                new(ProjectedSceneRecipe.AuxiliaryInputName, ProcessingAuxiliaryInputKind.CanonicalJson,
                    SchemaVersion: scene.SchemaVersion, IdentitySha256: scene.SceneIdentitySha256, Payload: payload)
                {
                    ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(payload)
                }
            ], InputArtifactId: raw.ArtifactId);

    internal static ProjectedSceneV1 Read(ProcessingArtifact artifact, ProcessingArtifact raw, ExposureMeasurementInputV1 expected)
    {
        if (artifact is not { Role: FrameArtifactRole.Metadata, ProductKind: ProcessingProductKind.Metadata, Variant: Variant, Layout: null } ||
            artifact.MediaType != ProjectedSceneRecipe.MediaType || artifact.CaptureId != expected.Raw.CaptureId ||
            artifact.SourceArtifactIds is not [var parent] || parent != expected.Raw.ArtifactId ||
            ProjectedSceneJson.Parse(artifact.Payload).Scene is not { } scene ||
            artifact.SchemaVersion != scene.SchemaVersion ||
            !ExposureMeasurementProducts.Same(artifact.ContentIdentitySha256, scene.SceneIdentitySha256))
            throw new InvalidDataException("Invalid exposure catalog prediction envelope.");

        var request = Request(raw, scene, artifact.Payload);
        var recipe = BuiltInProcessingRecipes.CreateExecutionIdentity(request.RecipeName, request.Options, request.Input,
            auxiliaryInputs: request.AuxiliaryInputs);
        if (artifact.RecipeDescriptor is not { } descriptor ||
            recipe.IdentitySha256 != ProcessingIdentity.CreateRecipeIdentity(descriptor).IdentitySha256 ||
            !ExposureMeasurementProducts.Same(recipe.IdentitySha256, artifact.RecipeIdentitySha256) ||
            artifact.ArtifactId != ProcessingIdentity.CreateArtifactId(ProcessingIdentity.CreateOutputIdentity(
                artifact.Role, Variant, recipe.IdentitySha256, artifact.SourceArtifactIds)))
            throw new InvalidDataException("Invalid exposure catalog prediction identity.");

        ValidateProjection(scene, expected);
        return scene;
    }

    internal static void ValidateProjection(ProjectedSceneV1 prediction, ExposureMeasurementInputV1 expected)
    {
        ProjectedSceneJson.Validate(prediction);
        var raw = expected.Raw;
        if (prediction.Kind != ProjectedSceneKind.Predicted || prediction.Source.CaptureId != raw.CaptureId ||
            prediction.Source.ArtifactId != raw.ArtifactId ||
            !ExposureMeasurementProducts.Same(prediction.Source.ArtifactIdentitySha256, raw.DescriptorSha256) ||
            !ExposureMeasurementProducts.Same(prediction.SceneIdentitySha256, ProjectedSceneJson.ComputeIdentity(prediction)) ||
            expected.ApertureProjection is not { } projection || projection != ProjectedSceneJson.ToProjectionContext(prediction.Projection) ||
            expected.OpticalCalibrationVersion != prediction.Projection.CalibrationVersion ||
            expected.OpticalCalibrationVersion != prediction.Projection.AlgorithmVersion ||
            prediction.CoordinateConvention != ProjectedSceneCoordinateConvention.ContinuousTopLeftPixelEdge ||
            prediction.ImageTransform != ProjectedSceneImageTransformV1.Identity(raw.Layout.Width, raw.Layout.Height))
            throw new InvalidDataException("Prediction differs from the measured exposure or delivered geometry.");
    }
}
