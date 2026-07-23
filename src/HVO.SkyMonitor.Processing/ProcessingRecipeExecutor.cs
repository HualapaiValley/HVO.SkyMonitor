using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The executor boundary converts unexpected recipe failures into stable terminal outcomes.")]
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
        if (request.InputArtifactId == Guid.Empty ||
            request.InputArtifactId is { } inputArtifactId &&
            (request.Inputs.Count(input => input.ArtifactId == inputArtifactId) != 1 ||
             !ProcessingRecipeSupport.Matches(
                 request.Inputs.Single(input => input.ArtifactId == inputArtifactId),
                 request.Input)))
        {
            return ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidInput,
                nameof(request.InputArtifactId));
        }
        if (!IsValidAnnotation(request.Annotation))
        {
            return ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidAnnotation,
                nameof(request.Annotation));
        }
        if (!AreValidAuxiliaryInputs(request.AuxiliaryInputs))
        {
            return ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidInput,
                nameof(request.AuxiliaryInputs));
        }

        ProcessingRecipeIdentity identity;
        try
        {
            var normalized = recipe.NormalizeOptions(request.Options);
            var effective = ProcessingIdentity.BindExecutionInputs(
                normalized, request.Input, request.Annotation, request.AuxiliaryInputs);
            identity = ProcessingIdentity.CreateRecipeIdentity(recipe.Definition, effective);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        catch (Exception)
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
        catch (Exception)
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
            !string.IsNullOrWhiteSpace(compatibility.ProcessingProfile) &&
            (compatibility.LocationIdentitySha256 is null ||
                compatibility.LocationIdentitySha256.Length == 64 &&
                compatibility.LocationIdentitySha256.All(Uri.IsHexDigit));
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

    private static bool AreValidAuxiliaryInputs(IReadOnlyList<ProcessingAuxiliaryInput>? inputs)
    {
        if (inputs is null)
        {
            return true;
        }
        if (inputs.Count > 32 || inputs.Any(static input => input is null ||
                string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 64) ||
            inputs.Select(static input => input.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputs.Count)
        {
            return false;
        }
        foreach (var input in inputs)
        {
            if (input.Kind == ProcessingAuxiliaryInputKind.Artifact)
            {
                if (input.Selector is null || input.SchemaVersion is not null ||
                    input.IdentitySha256 is not null || !input.Payload.IsEmpty || input.ArtifactId == Guid.Empty)
                {
                    return false;
                }
            }
            else if (input.Kind == ProcessingAuxiliaryInputKind.CanonicalJson)
            {
                if (input.Selector is not null || string.IsNullOrWhiteSpace(input.SchemaVersion) ||
                    input.ArtifactId is not null ||
                    input.IdentitySha256 is not { Length: 64 } ||
                    !input.IdentitySha256.All(Uri.IsHexDigit) || input.Payload.IsEmpty ||
                    !string.Equals(
                        ProcessingIdentity.ComputePayloadSha256(input.Payload),
                        input.IdentitySha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                try
                {
                    using var document = JsonDocument.Parse(input.Payload);
                    if (HasDuplicateProperties(document.RootElement))
                    {
                        return false;
                    }
                    var canonical = JsonSerializer.SerializeToUtf8Bytes(
                        CaptureContractJson.Canonicalize(document.RootElement));
                    if (!canonical.AsSpan().SequenceEqual(input.Payload.Span))
                    {
                        return false;
                    }
                }
                catch (JsonException)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
