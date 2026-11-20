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
        ValidateMagnitude(referenceMagnitude);
        var basis = ReadBasis(pipeline);
        if (basis is not null)
        {
            var priorPin = basis.Pin;
            // A scope change must not silently bless a different installed catalog or an invalid prior selection.
            _ = await priorPin.ResolveAsync(source, cancellationToken).ConfigureAwait(false);
            if (referenceMagnitude is null)
            {
                if (!HasCoherentDepth(basis))
                    throw new InvalidOperationException("The existing reference limits differ. Supply an explicit magnitude to align them.");
                return (pipeline, priorPin);
            }
        }

        var maximumEntries = basis?.Pin.MaximumEntries ?? AstrometricCatalogData.MaximumEntries;
        var magnitude = referenceMagnitude ?? VirtualSkyDefaultMagnitude;
        var data = await (source ?? throw new InvalidDataException("The installed astrometry catalog adapter is unavailable."))
            .ReadAsync(magnitude, maximumEntries, cancellationToken).ConfigureAwait(false);
        if (!data.IsCompleteForRequestedMagnitude || data.Provenance is null || data.Stars.Count == 0 ||
            data.Stars.Count > maximumEntries || data.CompletenessMagnitudeLimit < magnitude)
            throw new InvalidDataException("The installed catalog does not provide a complete, nonempty selection at this magnitude.");

        var selection = new MeasuredAstrometryCatalogSelection(data.IdentitySha256, data.SelectionIdentitySha256, magnitude, maximumEntries);
        return (ApplySelection(pipeline, basis, selection), selection);
    }

    internal static void ValidateMagnitude(double? referenceMagnitude)
    {
        if (referenceMagnitude is { } requested && (!double.IsFinite(requested) || requested is < -30 or > 30))
            throw new ArgumentOutOfRangeException(nameof(referenceMagnitude));
    }

    internal static bool MatchesLegacyCreation(
        CapturePipelineConfig basisPipeline, double? referenceMagnitude, CapturePipelineConfig committedPipeline,
        MeasuredAstrometryCatalogSelection selection)
    {
        // Legacy receipts fingerprinted the resulting revision. Rebuild the expected pipeline with the
        // recorded selection so those receipts can still be checked without the original catalog adapter.
        var basis = ReadBasis(basisPipeline);
        var magnitude = referenceMagnitude ?? basis?.Pin.MaximumMagnitude ?? VirtualSkyDefaultMagnitude;
        var maximumEntries = basis?.Pin.MaximumEntries ?? AstrometricCatalogData.MaximumEntries;
        if (selection.MaximumMagnitude != magnitude || selection.MaximumEntries != maximumEntries)
            return false;

        CapturePipelineConfig expected;
        if (referenceMagnitude is null && basis is not null)
        {
            if (basis.Pin != selection || !HasCoherentDepth(basis))
                return false;
            expected = basisPipeline;
        }
        else
        {
            expected = ApplySelection(basisPipeline, basis, selection);
        }
        return CaptureContractJson.ComputeCanonicalJsonSha256(expected) ==
            CaptureContractJson.ComputeCanonicalJsonSha256(committedPipeline);
    }

    private static ExistingBranch? ReadBasis(CapturePipelineConfig pipeline)
    {
        ProcessingGraphOperationsCoordinator.EnsureExplicitPipeline(pipeline);
        var solve = FindSolver(pipeline);
        if (solve is null && !pipeline.Steps.Any(static step =>
                HasAlias(step, AstrometryPredictionCaptureProcessingStep.Alias) ||
                HasAlias(step, MeasuredStellarAssociationCaptureProcessingStep.Alias) ||
                HasAlias(step, MeasuredAstrometryCaptureProcessingStep.Alias)))
            return null;
        if (solve?.DependsOn is not { Count: 2 } dependencies)
            throw new InvalidOperationException("The existing astrometry branch must be complete and enabled.");

        // A graph may publish other associations for its canonical scene. Only the solver's inputs define this branch.
        var prediction = FindDependency(pipeline.Steps, dependencies, AstrometryPredictionCaptureProcessingStep.Alias);
        var measured = FindDependency(pipeline.Steps, dependencies, MeasuredStellarAssociationCaptureProcessingStep.Alias);
        if (prediction is null || measured?.DependsOn is not { Count: 1 } measuredInputs ||
            !string.Equals(measuredInputs[0], NodeId(prediction), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The existing astrometry branch must be complete and enabled.");
        var options = ReadSolveOptions(solve);
        if (options is not { UseAstrometryPrediction: true, Catalog: null, PreviousAccepted: null, CatalogSelection: not null })
            throw new InvalidOperationException("The preset requires an independent branch with an installed catalog pin and no explicit prior.");
        return new(prediction, solve, options);
    }

    private static bool HasCoherentDepth(ExistingBranch basis)
    {
        var prediction = (AstrometryPredictionCaptureProcessingStepOptions)CaptureProcessingPipelineFactory.CreateOptionsInstance(
            basis.Prediction.Options, typeof(AstrometryPredictionCaptureProcessingStepOptions));
        return basis.Pin.MaximumMagnitude == prediction.MaximumMagnitude &&
            basis.Pin.MaximumMagnitude == (basis.Options.Recipe.Solver ?? new()).MaximumCatalogMagnitude;
    }

    private static CapturePipelineConfig ApplySelection(
        CapturePipelineConfig pipeline, ExistingBranch? basis, MeasuredAstrometryCatalogSelection selection)
    {
        var steps = pipeline.Steps.ToList();
        var magnitude = selection.MaximumMagnitude;
        if (basis is null)
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
            var (prediction, solve, _) = basis;
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

        return pipeline with { Steps = steps };
    }

    internal static MeasuredAstrometryCatalogSelection ReadSelection(CapturePipelineConfig pipeline)
    {
        var solve = FindSolver(pipeline);
        var selection = solve is null ? null : ReadSolveOptions(solve).CatalogSelection;
        return selection ?? throw new ReferenceAstrometryCatalogConflictException();
    }

    internal static async ValueTask VerifySelectionAsync(
        CapturePipelineConfig pipeline, IAstrometricCatalogSource? source, CancellationToken cancellationToken)
    {
        var selection = ReadSelection(pipeline);
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

    internal static CaptureProcessingStepConfig? FindSolver(CapturePipelineConfig pipeline)
        => pipeline.Steps.SingleOrDefault(static step => step.Enabled != false &&
            HasAlias(step, MeasuredAstrometryCaptureProcessingStep.Alias));

    private static CaptureProcessingStepConfig? FindDependency(
        IReadOnlyList<CaptureProcessingStepConfig> steps, IReadOnlyList<string> dependencies, string alias)
        => steps.SingleOrDefault(step => step.Enabled != false && HasAlias(step, alias) &&
            dependencies.Contains(NodeId(step), StringComparer.OrdinalIgnoreCase));

    private static bool HasAlias(CaptureProcessingStepConfig step, string alias)
        => string.Equals(step.Type, alias, StringComparison.OrdinalIgnoreCase);

    private static string NodeId(CaptureProcessingStepConfig step)
        => string.IsNullOrWhiteSpace(step.Id) ? step.Type : step.Id.Trim();

    private static JsonObject ReadOptions(CaptureProcessingStepConfig step)
        => (JsonObject?)JsonNode.Parse(step.Options?.GetRawText() ?? "null", new JsonNodeOptions { PropertyNameCaseInsensitive = true })
           ?? new JsonObject();

    private sealed record ExistingBranch(
        CaptureProcessingStepConfig Prediction, CaptureProcessingStepConfig Solve, MeasuredAstrometryCaptureOptions Options)
    {
        internal MeasuredAstrometryCatalogSelection Pin => Options.CatalogSelection!;
    }
}
