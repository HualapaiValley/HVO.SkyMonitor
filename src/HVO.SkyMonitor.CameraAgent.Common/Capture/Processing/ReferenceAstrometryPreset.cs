using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Builds an optional independent astrometry branch using the installed catalog.</summary>
internal static class ReferenceAstrometryPreset
{
    internal const string GraphName = "Reference astrometry preset";
    private const double VirtualSkyDefaultMagnitude = 3.25;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly CaptureProcessingPublicationPolicy Durable = new(CaptureProcessingPersistenceMode.DurableLocal);

    internal static async ValueTask<(CapturePipelineConfig Pipeline, MeasuredAstrometryCatalogSelection Selection)> ApplyAsync(
        CapturePipelineConfig pipeline, double? referenceMagnitude, IAstrometricCatalogSource? source, CancellationToken cancellationToken)
    {
        if (referenceMagnitude is { } requested && (!double.IsFinite(requested) || requested is < -30 or > 30))
            throw new ArgumentOutOfRangeException(nameof(referenceMagnitude));
        ProcessingGraphOperationsCoordinator.EnsureExplicitPipeline(pipeline);

        var steps = pipeline.Steps.ToList();
        var prediction = Find(steps, AstrometryPredictionCaptureProcessingStep.Alias);
        var measured = Find(steps, MeasuredStellarAssociationCaptureProcessingStep.Alias);
        var solve = Find(steps, MeasuredAstrometryCaptureProcessingStep.Alias);

        var maximumEntries = AstrometricCatalogData.MaximumEntries;
        if (prediction is not null || measured is not null || solve is not null)
        {
            if (prediction is null || measured is null || solve is null ||
                prediction.Enabled == false || measured.Enabled == false || solve.Enabled == false)
                throw new InvalidOperationException("The existing astrometry branch must be complete and enabled.");

            var solveOptions = ReadSolveOptions(solve);
            if (solveOptions is not { UseAstrometryPrediction: true, Catalog: null, PreviousAccepted: null, CatalogSelection: { } priorPin })
                throw new InvalidOperationException("The preset requires an independent branch with an installed catalog pin and no explicit prior.");

            // A scope change must not silently bless a different installed catalog or an invalid prior selection.
            _ = await priorPin.ResolveAsync(source, cancellationToken).ConfigureAwait(false);
            if (referenceMagnitude is null)
            {
                var predictionOptions = (AstrometryPredictionCaptureProcessingStepOptions)CaptureProcessingPipelineFactory.CreateOptionsInstance(
                    prediction.Options, typeof(AstrometryPredictionCaptureProcessingStepOptions));
                var solver = solveOptions.Recipe.Solver ?? new();
                if (priorPin.MaximumMagnitude != predictionOptions.MaximumMagnitude ||
                    priorPin.MaximumMagnitude != solver.MaximumCatalogMagnitude)
                    throw new InvalidOperationException("The existing reference limits differ. Supply an explicit magnitude to align them.");
                return (pipeline, priorPin);
            }
            maximumEntries = priorPin.MaximumEntries;
        }

        var magnitude = referenceMagnitude ?? VirtualSkyDefaultMagnitude;
        var data = await (source ?? throw new InvalidDataException("The installed astrometry catalog adapter is unavailable."))
            .ReadAsync(magnitude, maximumEntries, cancellationToken).ConfigureAwait(false);
        if (!data.IsCompleteForRequestedMagnitude || data.Provenance is null || data.Stars.Count == 0 ||
            data.Stars.Count > maximumEntries || data.CompletenessMagnitudeLimit < magnitude)
            throw new InvalidDataException("The installed catalog does not provide a complete, nonempty selection at this magnitude.");

        var selection = new MeasuredAstrometryCatalogSelection(data.IdentitySha256, data.SelectionIdentitySha256, magnitude, maximumEntries);
        if (solve is null || prediction is null)
        {
            steps.Add(new(AstrometryPredictionCaptureProcessingStep.Alias, "AstrometryPrediction",
                DependsOn: ["$raw"], Required: false, Publication: Durable,
                Options: JsonSerializer.SerializeToElement(new { maximumMagnitude = magnitude })));
            steps.Add(new(MeasuredStellarAssociationCaptureProcessingStep.Alias, "Measured",
                DependsOn: ["AstrometryPrediction"], Required: false, Publication: Durable,
                Options: JsonSerializer.SerializeToElement(new { useAstrometryPrediction = true })));
            steps.Add(new(MeasuredAstrometryCaptureProcessingStep.Alias, "Astrometry",
                DependsOn: ["AstrometryPrediction", "Measured"], Required: false, Publication: Durable,
                Options: JsonSerializer.SerializeToElement(new
                {
                    useAstrometryPrediction = true,
                    catalogSelection = selection,
                    recipe = new { solver = new { maximumCatalogMagnitude = magnitude } }
                }, Json)));
        }
        else
        {
            var predictionOptions = ReadOptions(prediction);
            predictionOptions["maximumMagnitude"] = magnitude;
            steps[steps.IndexOf(prediction)] = prediction with { Options = JsonSerializer.SerializeToElement(predictionOptions) };

            var solveOptions = ReadOptions(solve);
            var pinOptions = solveOptions["catalogSelection"]!.AsObject();
            pinOptions["catalogIdentitySha256"] = selection.CatalogIdentitySha256;
            pinOptions["selectionIdentitySha256"] = selection.SelectionIdentitySha256;
            pinOptions["maximumMagnitude"] = magnitude;
            var recipe = (solveOptions["recipe"] ??= new JsonObject()).AsObject();
            var solver = (recipe["solver"] ??= new JsonObject()).AsObject();
            solver["maximumCatalogMagnitude"] = magnitude;
            steps[steps.IndexOf(solve)] = solve with { Options = JsonSerializer.SerializeToElement(solveOptions) };
        }

        return (pipeline with { Steps = steps }, selection);
    }

    internal static async ValueTask VerifySelectionAsync(
        CapturePipelineConfig pipeline, IAstrometricCatalogSource? source, CancellationToken cancellationToken)
    {
        var solve = Find(pipeline.Steps, MeasuredAstrometryCaptureProcessingStep.Alias);
        var selection = solve is null ? null : ReadSolveOptions(solve).CatalogSelection;
        if (selection is null)
            throw new ReferenceAstrometryCatalogConflictException();
        try
        {
            _ = await selection.ResolveAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw new ReferenceAstrometryCatalogConflictException(ReferenceAstrometryCatalogConflictException.SafeMessage, exception);
        }
    }

    private static MeasuredAstrometryCaptureOptions ReadSolveOptions(CaptureProcessingStepConfig step)
        => (MeasuredAstrometryCaptureOptions)CaptureProcessingPipelineFactory.CreateOptionsInstance(
            step.Options, typeof(MeasuredAstrometryCaptureOptions));

    private static CaptureProcessingStepConfig? Find(IReadOnlyList<CaptureProcessingStepConfig> steps, string alias)
        => steps.SingleOrDefault(step => string.Equals(step.Type, alias, StringComparison.OrdinalIgnoreCase));

    private static JsonObject ReadOptions(CaptureProcessingStepConfig step)
        => (JsonObject?)JsonNode.Parse(step.Options?.GetRawText() ?? "null", new JsonNodeOptions { PropertyNameCaseInsensitive = true })
           ?? new JsonObject();
}
