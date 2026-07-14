using System.Text.Json;

namespace HVO.SkyMonitor.Processing;

public sealed class ProcessingRecipeExecutor : IProcessingRecipeExecutor
{
    private readonly Dictionary<string, IProcessingRecipe> _recipes;

    public ProcessingRecipeExecutor(IEnumerable<IProcessingRecipe>? recipes = null)
    {
        var selected = recipes?.ToArray();
        if (selected is null || selected.Length == 0)
        {
            selected = BuiltInProcessingRecipes.CreateAll();
        }
        _recipes = selected.ToDictionary(
            static recipe => recipe.Definition.Name,
            StringComparer.Ordinal);
    }

    public async ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.RecipeName) ||
            !_recipes.TryGetValue(request.RecipeName, out var recipe))
        {
            return ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.UnknownRecipe,
                nameof(request.RecipeName));
        }
        if (request.Inputs is null || request.Input is null ||
            string.IsNullOrWhiteSpace(request.OutputVariant))
        {
            return ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidOptions,
                nameof(request));
        }
        if (request.Inputs.Any(static input => !IsValidInput(input)))
        {
            return ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidInput,
                nameof(request.Inputs));
        }
        if (!IsValidAnnotation(request.Annotation))
        {
            return ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidAnnotation,
                nameof(request.Annotation));
        }

        ProcessingRecipeIdentity identity;
        try
        {
            var normalized = recipe.NormalizeOptions(request.Options);
            var effective = ProcessingIdentity.BindExecutionInputs(
                normalized, request.Input, request.Annotation);
            identity = ProcessingIdentity.CreateRecipeIdentity(recipe.Definition, effective);
        }
        catch (JsonException)
        {
            return ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidOptions,
                nameof(request.Options));
        }
        catch (ArgumentException)
        {
            return ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidOptions,
                nameof(request.Options));
        }
        catch (InvalidOperationException)
        {
            return ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.ExecutionFailed,
                nameof(request.Options));
        }

        try
        {
            return await recipe.ExecuteAsync(request, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidLayout,
                nameof(request.Inputs));
        }
        catch (OverflowException)
        {
            return ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidLayout,
                nameof(request.Inputs));
        }
        catch (InvalidOperationException)
        {
            return ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.ExecutionFailed);
        }
    }

    private static bool IsValidInput(ProcessingArtifact? input)
    {
        if (input is null || input.ArtifactId == Guid.Empty || !Enum.IsDefined(input.Role) ||
            string.IsNullOrWhiteSpace(input.Variant) || string.IsNullOrWhiteSpace(input.MediaType) ||
            input.RecipeIdentitySha256 is not { Length: 64 } ||
            !input.RecipeIdentitySha256.All(Uri.IsHexDigit) ||
            input.CreatedUtc.Offset != TimeSpan.Zero || input.Integration < TimeSpan.Zero ||
            input.Compatibility is not { } compatibility)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(compatibility.Rig) &&
            !string.IsNullOrWhiteSpace(compatibility.Orientation) &&
            !string.IsNullOrWhiteSpace(compatibility.Calibration) &&
            !string.IsNullOrWhiteSpace(compatibility.Mask) &&
            !string.IsNullOrWhiteSpace(compatibility.Sensor) &&
            !string.IsNullOrWhiteSpace(compatibility.SetpointRegime) &&
            !string.IsNullOrWhiteSpace(compatibility.ProcessingProfile);
    }

    private static bool IsValidAnnotation(ProcessingAnnotationInput? annotation)
    {
        if (annotation is null)
        {
            return true;
        }
        if (annotation.ProvenanceSha256 is not { Length: 64 } ||
            !annotation.ProvenanceSha256.All(Uri.IsHexDigit) ||
            annotation.Objects is null || annotation.Segments is null ||
            !IsFiniteNonZero(annotation.Transform.ScaleX) ||
            !IsFiniteNonZero(annotation.Transform.ScaleY) ||
            !double.IsFinite(annotation.Transform.OffsetX) ||
            !double.IsFinite(annotation.Transform.OffsetY) ||
            annotation.Objects.Any(static item => item is null ||
                string.IsNullOrWhiteSpace(item.Id) || item.DisplayName is null ||
                !IsFinite(item.Pixel.X, item.Pixel.Y)) ||
            annotation.Segments.Any(static item => item is null ||
                string.IsNullOrWhiteSpace(item.ConstellationId) ||
                !IsFinite(item.FromPixel.X, item.FromPixel.Y) ||
                !IsFinite(item.ToPixel.X, item.ToPixel.Y)))
        {
            return false;
        }

        var overlay = annotation.ProjectionOverlay;
        return overlay is null ||
            double.IsFinite(overlay.ImageCircleRadius) && overlay.ImageCircleRadius > 0 &&
            IsFinite(overlay.Center.X, overlay.Center.Y) &&
            IsFinite(overlay.North.X, overlay.North.Y) &&
            IsFinite(overlay.East.X, overlay.East.Y) &&
            IsFinite(overlay.South.X, overlay.South.Y) &&
            IsFinite(overlay.West.X, overlay.West.Y);

        static bool IsFinite(double x, double y) => double.IsFinite(x) && double.IsFinite(y);
        static bool IsFiniteNonZero(double value) => double.IsFinite(value) && value != 0;
    }
}
