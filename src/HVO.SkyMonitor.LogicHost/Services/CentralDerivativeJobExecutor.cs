using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

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
    ICentralDerivativeJobScheduler jobScheduler,
    ICentralTransientValidationExecutor transientExecutor,
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
        if (string.Equals(lease.RecipeName, CentralTransientRuntime.RecipeName, StringComparison.Ordinal))
        {
            return await transientExecutor.ExecuteAsync(lease, cancellationToken).ConfigureAwait(false);
        }
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
        var canonicalInputs = CreateCanonicalInputs(lease);
        if (canonicalInputs is null)
        {
            const string reason = "processing.canonical-input-integrity-failed";
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
            await jobScheduler.EnsureRequiredJobsAsync(
                lease.SourceDevicePublicId,
                recoveredArtifactId.Value,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
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
                canonicalInputs,
                cancellationToken: cancellationToken).ConfigureAwait(false);
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
                if (RequiresBoundExpectedIdentity(lease) && !string.Equals(
                        outcome.Products[0].Recipe.IdentitySha256,
                        lease.ExpectedRecipeIdentitySha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    const string reason = "processing.recipe-identity-mismatch";
                    await jobService.FailAsync(
                        lease.JobId, lease.LeaseToken, reason, retryable: false, cancellationToken).ConfigureAwait(false);
                    RecordPinRelease(lease, "terminal");
                    return new CentralDerivativeExecutionResult(ProcessingOutcomeStatus.TerminalFailure, null, reason);
                }
                var publishStarted = timeProvider.GetTimestamp();
                Guid artifactId;
                using (telemetry.StartStage("publish", lease.RecipeName))
                {
                    artifactId = await outputWriter.PersistAsync(
                        lease, outcome.Products[0], input.ByteLength, duration, cancellationToken).ConfigureAwait(false);
                }
                await jobScheduler.EnsureRequiredJobsAsync(
                    lease.SourceDevicePublicId,
                    artifactId,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
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

    private static List<ProcessingAuxiliaryInput>? CreateCanonicalInputs(CentralDerivativeJobLease lease)
    {
        if (lease.CanonicalInputs is not { Count: > 0 })
        {
            return [];
        }
        var inputs = new List<ProcessingAuxiliaryInput>(lease.CanonicalInputs.Count);
        foreach (var input in lease.CanonicalInputs.OrderBy(item => item.Ordinal))
        {
            var payload = Encoding.UTF8.GetBytes(input.CanonicalJson);
            if (payload.Length != input.ByteLength ||
                !string.Equals(
                    ProcessingIdentity.ComputePayloadSha256(payload),
                    input.IdentitySha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            inputs.Add(new ProcessingAuxiliaryInput(
                input.BindingName,
                ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: input.SchemaVersion,
                IdentitySha256: input.IdentitySha256,
                Payload: payload));
        }
        return inputs;
    }

    internal static bool RequiresBoundExpectedIdentity(CentralDerivativeJobLease lease)
        => RequiresBoundExpectedIdentity(
            lease.RequestedRecipeIdentitySha256,
            lease.ExpectedRecipeIdentitySha256);

    internal static bool RequiresBoundExpectedIdentity(string requestedIdentity, string? expectedIdentity)
        => !string.IsNullOrWhiteSpace(expectedIdentity)
            && !string.Equals(
                expectedIdentity,
                requestedIdentity,
                StringComparison.OrdinalIgnoreCase);

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
