using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing;

/// <summary>
/// Explicit configured-rig capture recipe. Hosts must resolve/pin evidence before constructing it; registration,
/// acquisition, publication and reference retention are separate responsibilities. No pixel interpretation.
/// </summary>
public sealed class CaptureOrbitalPredictionRecipe : IProcessingRecipe
{
    public const string Name = "capture-orbital-prediction";
    public const string AlgorithmVersion = "capture-orbital-prediction-v1";
    private readonly CaptureOrbitalPredictionInput input;
    private readonly OrbitalPredictionEngine engine;
    public ProcessingRecipeDefinition Definition { get; } = new(Name, "1.0.0", AlgorithmVersion, ProcessingOperationKind.Analyzer);

    public CaptureOrbitalPredictionRecipe(CaptureOrbitalPredictionInput input, OrbitalTimeData timeData, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        input.ValidateTime(timeData);
        this.input = input;
        engine = new(timeData, timeProvider);
    }

    public JsonElement NormalizeOptions(JsonElement options)
    {
        CaptureOrbitalPredictionInput.Require(options.ValueKind == JsonValueKind.Object && !options.EnumerateObject().Any(),
            "capture-prediction-options-must-be-empty");
        // All effective settings and source identities enter the ordinary Processing recipe hash.
        using var document = JsonDocument.Parse(input.SerializePlan());
        return document.RootElement.Clone();
    }

    public async ValueTask<ProcessingOutcome> ExecuteAsync(ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        var source = ProcessingRecipeSupport.ResolveSingle(request, out var failure);
        if (source is null) return failure!;
        if (request.Annotation is not null || request.AuxiliaryInputs is { Count: > 0 } || !input.Matches(source))
            return ProcessingOutcome.TerminalFailure("capture-orbital-input-mismatch");
        var expectedIdentity = ProcessingIdentity.CreateRecipeIdentity(Definition,
            ProcessingIdentity.BindExecutionInputs(NormalizeOptions(JsonSerializer.SerializeToElement(new { })), request.Input, null));
        if (identity.IdentitySha256 != expectedIdentity.IdentitySha256)
            return ProcessingOutcome.TerminalFailure("capture-orbital-recipe-mismatch");
        var result = input.Query is { } query
            ? await engine.PredictExposureAsync(query, cancellationToken).ConfigureAwait(false) : null;
        var output = new CaptureOrbitalPredictionV1(CaptureOrbitalPredictionJson.SchemaVersion, input.IdentitySha256,
            input.Plan, "Unknown", result?.Status ?? OrbitalExecutionStatus.Unavailable,
            result?.Reason ?? OrbitalPredictionReason.SelectionWithheld, result?.EvaluatedObjects ?? 0,
            result?.PropagationEvaluations ?? 0, result?.Products ?? [], result?.Failures ?? []);
        var payload = CaptureOrbitalPredictionJson.Serialize(output, input);
        cancellationToken.ThrowIfCancellationRequested();
        return ProcessingOutcome.Produced(ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata,
            request.OutputVariant, CaptureOrbitalPredictionJson.MediaType, null, payload, identity,
            [new(Name, AlgorithmVersion), new(OrbitalPredictionModel.Propagator.Name, OrbitalPredictionModel.Propagator.Version),
                new("teme-conversion", OrbitalTimeData.ConversionVersion), new("orbital-selection", OrbitalSelectionPolicy.CurrentVersion)],
            [source], source.Integration, source.Compatibility, ProcessingProductKind.Metadata,
            CaptureOrbitalPredictionJson.SchemaVersion, ProcessingIdentity.ComputePayloadSha256(payload)));
    }
}
