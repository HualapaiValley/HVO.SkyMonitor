using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralDerivativeExecutionResult(
    ProcessingOutcomeStatus Status,
    Guid? ArtifactId,
    string? ReasonCode);

internal interface ICentralDerivativeJobExecutor
{
    Task<CentralDerivativeExecutionResult> ExecuteAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken);
}

internal sealed class CentralDerivativeJobExecutor(
    ICentralDerivativeJobInputReader inputReader,
    LogicHostRecipeExecutionAdapter recipeAdapter,
    ICentralDerivativeOutputWriter outputWriter,
    ICentralDerivativeJobService jobService,
    CentralDerivativeWorkerTelemetry telemetry,
    TimeProvider timeProvider) : ICentralDerivativeJobExecutor
{
    private const double MaximumLabelMagnitude = 2.5;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public async Task<CentralDerivativeExecutionResult> ExecuteAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        using var optionsDocument = JsonDocument.Parse(lease.RecipeOptionsJson);
        var selector = JsonSerializer.Deserialize<ProcessingInputSelector>(lease.InputSelectorJson, SerializerOptions)
            ?? throw new CentralDerivativeJobStateException("The derivative input selector is invalid.");
        var currentRequestedIdentity = BuiltInProcessingRecipes.CreateRequestedIdentity(
            lease.RecipeName,
            optionsDocument.RootElement,
            selector).IdentitySha256;
        if (!string.Equals(
            currentRequestedIdentity,
            lease.RequestedRecipeIdentitySha256,
            StringComparison.OrdinalIgnoreCase))
        {
            const string reason = "processing.recipe-identity-mismatch";
            await jobService.FailAsync(
                lease.JobId, lease.LeaseToken, reason, retryable: false, cancellationToken).ConfigureAwait(false);
            RecordPinRelease(lease, "terminal");
            return new CentralDerivativeExecutionResult(ProcessingOutcomeStatus.TerminalFailure, null, reason);
        }
        Guid? recoveredArtifactId;
        var recoveryStarted = timeProvider.GetTimestamp();
        using (telemetry.StartStage("recover", lease.RecipeName))
        {
            recoveredArtifactId = await outputWriter.TryCompletePendingAsync(lease, cancellationToken)
                .ConfigureAwait(false);
        }
        if (recoveredArtifactId.HasValue)
        {
            telemetry.RecordStage(
                "recover", lease.RecipeName, "adopted", timeProvider.GetElapsedTime(recoveryStarted));
            telemetry.RecordRecovery("adopted");
            return new CentralDerivativeExecutionResult(
                ProcessingOutcomeStatus.Produced, recoveredArtifactId, "derivative.output-recovered");
        }
        var input = await inputReader.ReadAsync(lease, cancellationToken).ConfigureAwait(false);
        telemetry.RecordStage(
            "recover", lease.RecipeName, "empty", timeProvider.GetElapsedTime(recoveryStarted));
        var annotation = string.Equals(lease.RecipeName, BuiltInProcessingRecipes.Annotation, StringComparison.Ordinal)
            ? CreateAnnotation(lease.SceneProvenanceJson)
            : null;
        if (string.Equals(lease.RecipeName, BuiltInProcessingRecipes.Annotation, StringComparison.Ordinal)
            && annotation is null)
        {
            await jobService.SkipAsync(
                lease.JobId, lease.LeaseToken, ProcessingReasonCodes.MissingAnnotation, cancellationToken)
                .ConfigureAwait(false);
            RecordPinRelease(lease, "skipped");
            return new CentralDerivativeExecutionResult(
                ProcessingOutcomeStatus.Skipped, null, ProcessingReasonCodes.MissingAnnotation);
        }

        var started = timeProvider.GetTimestamp();
        ProcessingOutcome outcome;
        using (telemetry.StartStage("execute", lease.RecipeName))
        {
            outcome = await recipeAdapter.ExecuteAsync(
                input.ProcessingInputs,
                lease.RecipeName,
                optionsDocument.RootElement.Clone(),
                selector,
                lease.TargetVariant,
                annotation,
                cancellationToken).ConfigureAwait(false);
        }
        var duration = timeProvider.GetElapsedTime(started);
        telemetry.RecordStage("execute", lease.RecipeName, GetOutcome(outcome.Status), duration);
        switch (outcome.Status)
        {
            case ProcessingOutcomeStatus.Produced:
                if (outcome.Products.Count != 1)
                {
                    throw new CentralDerivativeJobStateException("A derivative job must produce exactly one product.");
                }
                var publishStarted = timeProvider.GetTimestamp();
                Guid artifactId;
                using (telemetry.StartStage("publish", lease.RecipeName))
                {
                    artifactId = await outputWriter.PersistAsync(
                        lease, outcome.Products[0], input.ByteLength, duration, cancellationToken).ConfigureAwait(false);
                }
                telemetry.RecordStage(
                    "publish",
                    lease.RecipeName,
                    "completed",
                    timeProvider.GetElapsedTime(publishStarted),
                    outcome.Products[0].Payload.Length);
                return new CentralDerivativeExecutionResult(outcome.Status, artifactId, null);
            case ProcessingOutcomeStatus.Skipped:
                await jobService.SkipAsync(
                    lease.JobId, lease.LeaseToken, outcome.ReasonCode!, cancellationToken).ConfigureAwait(false);
                RecordPinRelease(lease, "skipped");
                return new CentralDerivativeExecutionResult(outcome.Status, null, outcome.ReasonCode);
            case ProcessingOutcomeStatus.RetryableFailure:
                await jobService.FailAsync(
                    lease.JobId, lease.LeaseToken, outcome.ReasonCode!, retryable: true, cancellationToken)
                    .ConfigureAwait(false);
                return new CentralDerivativeExecutionResult(outcome.Status, null, outcome.ReasonCode);
            case ProcessingOutcomeStatus.TerminalFailure:
                await jobService.FailAsync(
                    lease.JobId, lease.LeaseToken, outcome.ReasonCode!, retryable: false, cancellationToken)
                    .ConfigureAwait(false);
                RecordPinRelease(lease, "terminal");
                return new CentralDerivativeExecutionResult(outcome.Status, null, outcome.ReasonCode);
            default:
                throw new InvalidOperationException("The processing outcome status is unsupported.");
        }
    }

    private static ProcessingAnnotationInput? CreateAnnotation(string? sceneProvenanceJson)
    {
        if (string.IsNullOrWhiteSpace(sceneProvenanceJson))
        {
            return null;
        }
        var provenance = JsonSerializer.Deserialize<SceneProvenance>(sceneProvenanceJson, SerializerOptions);
        if (provenance?.Objects is null && provenance?.Segments is null)
        {
            return null;
        }
        var objects = provenance.Objects?.Select(item =>
        {
            var annotate = ShouldAnnotate(item);
            return new ProjectedAnnotationObject(
                item.Id,
                item.DisplayName,
                new PixelPoint(item.PixelX, item.PixelY),
                annotate,
                annotate);
        }).ToArray() ?? [];
        var segments = provenance.Segments?.Select(item => new ProjectedAnnotationSegment(
            item.ConstellationId,
            new PixelPoint(item.FromPixelX, item.FromPixelY),
            new PixelPoint(item.ToPixelX, item.ToPixelY))).ToArray() ?? [];
        var identity = CaptureContractJson.ComputeCanonicalJsonSha256(
            CaptureContractJson.SerializeToElement(new { provenance.SceneId, objects, segments }));
        return new ProcessingAnnotationInput(
            objects,
            segments,
            new PreviewTransform(1, 1),
            ProjectionOverlay: null,
            identity);
    }

    private void RecordPinRelease(CentralDerivativeJobLease lease, string outcome)
    {
        if (lease.Inputs is not { Count: > 1 })
        {
            return;
        }
        var selectedAtUtc = lease.Inputs.Min(input => input.SelectedAtUtc);
        if (selectedAtUtc != default)
        {
            telemetry.RecordWindowPinDuration(lease.RecipeName, timeProvider.GetUtcNow() - selectedAtUtc, outcome);
        }
    }

    private static bool ShouldAnnotate(ProjectedObjectProvenance item)
        => !string.IsNullOrWhiteSpace(item.DisplayName)
            && !string.Equals(item.Id, item.DisplayName, StringComparison.Ordinal)
            && (item.Id.StartsWith("solar-system:", StringComparison.Ordinal)
                || item.Magnitude <= MaximumLabelMagnitude);

    private static string GetOutcome(ProcessingOutcomeStatus status) => status switch
    {
        ProcessingOutcomeStatus.Produced => "produced",
        ProcessingOutcomeStatus.Skipped => "skipped",
        ProcessingOutcomeStatus.RetryableFailure => "retryable",
        ProcessingOutcomeStatus.TerminalFailure => "terminal",
        _ => "other"
    };

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
