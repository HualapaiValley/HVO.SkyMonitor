using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

/// <summary>What one scheduled run of a nightly product kind did.</summary>
internal sealed record NightlyProductRunReport(
    NightlyProductKind Kind,
    bool Ready,
    int WindowsEvaluated,
    int WindowsUnchanged,
    int ProductsPublished,
    int ProductsReused,
    int PendingWindows,
    int FailedWindows,
    int FinalsRecorded,
    string? Note)
{
    /// <summary>A bounded operator-facing summary of the run.</summary>
    internal string Describe()
    {
        if (!Ready)
        {
            return Note ?? "Nightly product generation is not ready.";
        }
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{NightlyProductContract.TargetFor(Kind)}: ")
            .Append(CultureInfo.InvariantCulture, $"{WindowsEvaluated} windows evaluated, {WindowsUnchanged} unchanged, ")
            .Append(CultureInfo.InvariantCulture, $"{ProductsPublished} products published, {ProductsReused} reused, ")
            .Append(CultureInfo.InvariantCulture, $"{FinalsRecorded} final windows recorded, {PendingWindows} pending, ")
            .Append(CultureInfo.InvariantCulture, $"{FailedWindows} failed.");
        if (Note is not null)
        {
            builder.Append(' ').Append(Note);
        }
        return builder.ToString();
    }
}

/// <summary>
/// Generates one retained, settled hourly or sunrise-day occurrence from published fixed previews. Admission uses
/// captured exposure, recipe, rig and location facts. Keograms cover the complete planned UTC period; star trails
/// reduce only actual dark-night frames through ordered parts and bounded rollups. The immutable occurrence binds
/// recipe identity and publication, so unchanged retries verify and reuse the same output files. A window exceeding
/// <see cref="NightlyProductOptions.MaximumSegmentsPerRun"/> recipe executions is rejected before source restoration.
/// </summary>
internal sealed class NightlyProductGenerator : IDisposable
{
    internal const string KeogramSegmentVariant = "still-keogram-part-v2";
    internal const string KeogramFinalVariant = "still-keogram-final-v2";
    internal const string StarTrailSegmentVariant = "still-star-trail-part-v2";
    internal const string StarTrailRollupVariant = "still-star-trail-rollup-v2";
    internal const string StarTrailFinalVariant = "still-star-trail-final-v2";
    internal const string MixedSegmentRecipesReasonCode = "nightly.mixed-segment-recipes";
    internal const string RecipeFailedReasonCode = "nightly.recipe-failed";
    private const string FingerprintSchemaVersion = "hvo-still-window-fingerprint-v3";

    private readonly CameraAgentHostOptions _options;
    private readonly ICameraAgentConfigurationAccessor _configuration;
    private readonly INightlyProductSourceReader _sources;
    private readonly SqliteNightlyProductStore _store;
    private readonly IPlanetEphemeris _ephemeris;
    private readonly Func<IDeploymentLocationStore?> _locationStore;
    private readonly TimeProvider _timeProvider;
    private readonly ProcessingRecipeExecutor _executor = new();
    private readonly SemaphoreSlim _run = new(1, 1);

    public NightlyProductGenerator(
        IOptions<CameraAgentHostOptions> options,
        ICameraAgentConfigurationAccessor configuration,
        INightlyProductSourceReader sources,
        SqliteNightlyProductStore store,
        IPlanetEphemeris ephemeris,
        Func<IDeploymentLocationStore?> locationStore,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _configuration = configuration;
        _sources = sources;
        _store = store;
        _ephemeris = ephemeris;
        _locationStore = locationStore;
        _timeProvider = timeProvider;
    }

    public void Dispose() => _run.Dispose();

    /// <summary>Runs one scheduled occurrence. Runs of either kind are serialized within the process.</summary>
    internal async ValueTask<NightlyProductRunReport> RunAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(occurrence, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _run.Release();
        }
    }

    private async ValueTask<NightlyProductRunReport> RunCoreAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (!occurrence.IsValid() || occurrence.SourceWindow is not { } sourceWindow ||
            !NightlyProductPreset.TryParseTarget(occurrence.Definition.TaskTarget, out var kind))
        {
            throw new ArgumentException("A still producer requires a valid retained source-window occurrence.", nameof(occurrence));
        }
        var expectedSelection = kind == NightlyProductKind.Keogram ? LocalAutomationSourceSelection.AllActualSources
            : LocalAutomationSourceSelection.DarkNightActualSources;
        if (sourceWindow.Policy.Selection != expectedSelection)
            throw new ArgumentException("The source selection does not match the still product kind.", nameof(occurrence));
        var options = _options.NightlyProducts;
        if (!options.Enabled || !NightlyProductPreset.Matches(occurrence.Definition.TaskTarget, kind, options))
        {
            return NotReady(kind, "The retained producer preset is unavailable; current settings cannot replace it.");
        }
        if (!sourceWindow.IsEligibleForFinal(_timeProvider.GetUtcNow()))
        {
            return NotReady(kind, "The retained source window has not settled.");
        }
        if (!_configuration.IsConfigured)
        {
            return NotReady(kind, "The camera configuration has not been loaded yet.");
        }
        var configuration = await _configuration.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(options.RigProfileSha256, RigProjectionContextFactory.CreateProfileHashSha256(configuration.Rig),
                StringComparison.OrdinalIgnoreCase))
        {
            return NotReady(kind, "The retained producer rig is unavailable; current geometry cannot replace it.");
        }
        var context = new RunContext(kind, options, configuration, _timeProvider.GetUtcNow().ToUniversalTime(), occurrence);
        var window = new NightlyProductWindow(sourceWindow.ReportingPeriod.ReportDate, sourceWindow.StartUtc, sourceWindow.EndUtc);
        if (await EvaluateSegmentAsync(context, window, cancellationToken).ConfigureAwait(false))
        {
            await EvaluateNightAsync(context, window, [window], cancellationToken).ConfigureAwait(false);
        }
        return new NightlyProductRunReport(
            kind, Ready: true, context.WindowsEvaluated, context.WindowsUnchanged, context.Published, context.Reused,
            context.PendingWindows, context.FailedWindows, context.FinalsRecorded,
            context.GeometryUnavailable ? "The rig projection could not produce keogram geometry." : null);
    }

    /// <summary>Evaluates one settled segment window. Returns whether the window has a recorded current evaluation.</summary>
    private async ValueTask<bool> EvaluateSegmentAsync(
        RunContext context,
        NightlyProductWindow window,
        CancellationToken cancellationToken)
    {
        context.WindowsEvaluated++;
        try
        {
            var unsupported = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var candidates = await _sources.ReadCandidatesAsync(
                context.Options.SourceNodeId!,
                window.StartUtc,
                window.EndUtc,
                NightlyProductContract.MaximumWindowCandidates,
                reason => unsupported[reason] = unsupported.GetValueOrDefault(reason) + 1,
                cancellationToken).ConfigureAwait(false);
            var candidateCount = candidates.Count + unsupported.Values.Sum();
            if (candidateCount > NightlyProductContract.MaximumWindowCandidates)
            {
                // Never truncated: the window is rejected and names the bound it exceeded.
                return await RecordUnlessUnchangedAsync(
                    context, NightlyProductScope.Segment, window, NightlyProductWindowDisposition.Rejected,
                    NightlyProductContract.WindowSourceBoundReasonCode, candidateCount, [], unsupported,
                    cancellationToken).ConfigureAwait(false);
            }
            if (context.Kind == NightlyProductKind.Keogram && context.Geometry is null)
            {
                return await RecordUnlessUnchangedAsync(
                    context, NightlyProductScope.Segment, window, NightlyProductWindowDisposition.Rejected,
                    NightlyProductContract.GeometryUnavailableReasonCode, candidateCount, [], unsupported,
                    cancellationToken).ConfigureAwait(false);
            }

            var admission = NightlyProductAdmission.Admit(
                candidates,
                context.RigProfileSha256,
                context.Occurrence.SourceWindow!,
                context.Options.SourceRecipeIdentitySha256!,
                candidate => Locate(candidate, context.Configuration),
                _ephemeris,
                unsupported);
            if (admission.Admitted.Any(static candidate => candidate.PayloadBytes <= 0 ||
                    candidate.PayloadBytes > NightlyProductRecipeLimits.MaximumSourceBytes))
            {
                return await RecordUnlessUnchangedAsync(context, NightlyProductScope.Segment, window,
                    NightlyProductWindowDisposition.Rejected, "nightly.source-byte-bound", candidateCount, [],
                    admission.Exclusions, cancellationToken).ConfigureAwait(false);
            }
            if (admission.Admitted.Count == 0)
            {
                return await RecordUnlessUnchangedAsync(
                    context, NightlyProductScope.Segment, window, NightlyProductWindowDisposition.NoSources,
                    null, candidateCount, [], admission.Exclusions, cancellationToken).ConfigureAwait(false);
            }

            if (context.Kind == NightlyProductKind.StarTrail && admission.Admitted.Count > 1 &&
                (context.Options.MaximumSegmentSources < 2 ||
                 admission.Admitted.Max(static source => source.PayloadBytes) > NightlyProductRecipeLimits.MaximumSourceBytes / 2))
            {
                return await RecordUnlessUnchangedAsync(context, NightlyProductScope.Segment, window,
                    NightlyProductWindowDisposition.Rejected, "nightly.rollup-bound", candidateCount, [],
                    admission.Exclusions, cancellationToken).ConfigureAwait(false);
            }

            var parts = Partition(admission.Admitted, context.Options.MaximumSegmentSources);
            if (context.Kind == NightlyProductKind.Keogram && KeogramPlanRejection(context, parts) is { } planReason)
            {
                return await RecordUnlessUnchangedAsync(context, NightlyProductScope.Segment, window,
                    NightlyProductWindowDisposition.Rejected, planReason, candidateCount, [], admission.Exclusions,
                    cancellationToken).ConfigureAwait(false);
            }

            var admittedIdentities = admission.Admitted.Select(static candidate => candidate.OutputIdentitySha256).ToArray();
            var fingerprint = Fingerprint(
                context, NightlyProductScope.Segment, admittedIdentities, candidateCount, admission.Exclusions, null);
            if (await IsUnchangedAsync(context, NightlyProductScope.Segment, window.StartUtc, fingerprint, cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }

            var byArtifact = admission.Admitted.ToDictionary(static candidate => candidate.ArtifactId);
            var productIds = new List<Guid>();
            var requiredExecutions = parts.Count + 1;
            if (context.Kind == NightlyProductKind.StarTrail)
            {
                var count = parts.Count;
                var fanIn = Math.Max(2, Math.Min(context.Options.MaximumSegmentSources,
                    (int)(NightlyProductRecipeLimits.MaximumSourceBytes / admission.Admitted.Max(static source => source.PayloadBytes))));
                while (count > fanIn)
                {
                    count = (count + fanIn - 1) / fanIn;
                    requiredExecutions += count;
                }
            }
            if (requiredExecutions > context.Options.MaximumSegmentsPerRun)
            {
                context.FailedWindows = 1;
                return await RecordAsync(context, NightlyProductScope.Segment, window,
                    NightlyProductWindowDisposition.Rejected, NightlyProductContract.ExecutionBoundReasonCode,
                    candidateCount, admission.Admitted.Count, admission.Exclusions, fingerprint, [], cancellationToken)
                    .ConfigureAwait(false);
            }
            for (var ordinal = 0; ordinal < parts.Count; ordinal++)
            {
                var part = parts[ordinal];
                var result = await ProduceAsync(
                    context,
                    NightlyProductScope.Segment,
                    window,
                    ordinal,
                    context.SegmentRecipe,
                    context.SegmentRecipeOptions,
                    context.SegmentVariant,
                    ProcessingInputSelector.RecipeResult(
                        FrameArtifactRole.Preview, part[0].Variant, part[0].RecipeIdentitySha256),
                    context.SegmentAuxiliaryInputs,
                    [.. part.Select(static candidate => (candidate.ArtifactId, candidate.ExposureStartedUtc))],
                    token => _sources.RestoreAsync(part, token),
                    id => (NightlyProductSourceKind.PreviewFrame, byArtifact[id].OutputIdentitySha256, byArtifact[id].CaptureId),
                    cancellationToken).ConfigureAwait(false);
                if (result.Pending)
                {
                    context.PendingWindows++;
                    return false;
                }
                if (result.RejectionReasonCode is { } reason)
                {
                    context.FailedWindows = 1;
                    return await RecordAsync(
                        context, NightlyProductScope.Segment, window, NightlyProductWindowDisposition.Rejected, reason,
                        candidateCount, admission.Admitted.Count, admission.Exclusions, fingerprint, [],
                        cancellationToken).ConfigureAwait(false);
                }
                productIds.Add(result.ProductId!.Value);
            }
            return await RecordAsync(
                context, NightlyProductScope.Segment, window, NightlyProductWindowDisposition.Produced, null,
                candidateCount, admission.Admitted.Count, admission.Exclusions, fingerprint, productIds,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            // A source that cannot be verified fails this window only. It stays unrecorded, so the next run retries
            // it and the night waits for it rather than composing around it.
            context.FailedWindows = 1;
            return false;
        }
    }

    private static string? KeogramPlanRejection(RunContext context, IReadOnlyList<IReadOnlyList<NightlyProductCandidate>> parts)
    {
        var geometry = context.Geometry!;
        int plannedWidth;
        try
        {
            plannedWidth = new PlannedKeogramAxis(context.Occurrence.SourceWindow!.StartUtc,
                context.Occurrence.SourceWindow.EndUtc, TimeSpan.FromSeconds(context.Options.KeogramColumnSeconds))
                .Width(context.Options.KeogramMaximumColumnCount);
        }
        catch (ArgumentException) { return "nightly.keogram-column-bound"; }
        var first = parts[0][0].Layout;
        if (first is null || first.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24) ||
            parts.SelectMany(static part => part).Any(source => source.Layout is not { } layout ||
                layout.Width != geometry.Projection.WidthPixels || layout.Height != geometry.Projection.HeightPixels ||
                layout.PixelFormat != first.PixelFormat || layout.ByteLength != source.PayloadBytes))
            return "nightly.keogram-layout-mismatch";
        var columnBytes = (long)geometry.SampleCount * ImageLayout.BytesPerPixel(first.PixelFormat);
        if (plannedWidth * columnBytes > NightlyProductRecipeLimits.MaximumSourceBytes)
            return "nightly.keogram-output-byte-bound";
        var assemblyBytes = 0L;
        foreach (var part in parts)
        {
            int width;
            try
            {
                width = KeogramComposer.ComputeTimeAxis([.. part.Select(static source => source.ExposureStartedUtc)],
                    context.KeogramOptions.MaximumGapSeconds, context.KeogramOptions.MaximumGapColumnCount,
                    context.KeogramOptions.MaximumColumnCount).Width;
            }
            catch (ArgumentException) { return "nightly.keogram-column-bound"; }
            var bytes = width * columnBytes;
            if (bytes > NightlyProductRecipeLimits.MaximumSourceBytes) return "nightly.keogram-output-byte-bound";
            assemblyBytes += bytes;
        }
        return assemblyBytes > NightlyProductRecipeLimits.MaximumSourceBytes ? "nightly.keogram-assembly-byte-bound" : null;
    }

    /// <summary>Composes a settled observing day from the current products of its segment windows.</summary>
    private async ValueTask EvaluateNightAsync(
        RunContext context,
        NightlyProductWindow day,
        IReadOnlyList<NightlyProductWindow> windows,
        CancellationToken cancellationToken)
    {
        var night = new NightlyProductWindow(day.ObservingDate, day.StartUtc, day.EndUtc);
        try
        {
            var segments = new List<NightlyProductDetail>();
            foreach (var window in windows)
            {
                var state = await _store.ReadWindowAsync(
                    context.Kind, NightlyProductScope.Segment, window.StartUtc, cancellationToken, context.Occurrence.IdentitySha256).ConfigureAwait(false);
                if (state is null)
                {
                    return;
                }
                if (state.Status.Disposition == NightlyProductWindowDisposition.Rejected)
                {
                    await RecordUnlessUnchangedAsync(context, NightlyProductScope.Final, night,
                        NightlyProductWindowDisposition.Rejected, state.Status.ReasonCode, state.Status.CandidateCount,
                        [state.FingerprintSha256], state.Status.Exclusions, cancellationToken).ConfigureAwait(false);
                    context.FailedWindows = 1;
                    context.FinalsRecorded++;
                    return;
                }
                foreach (var productId in state.ProductIds)
                {
                    segments.Add(await _store.GetAsync(productId, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException("A current nightly segment is not published."));
                }
            }
            var empty = new SortedDictionary<string, int>(StringComparer.Ordinal);
            string? reason = null;
            if (segments.Count == 0)
            {
                await RecordUnlessUnchangedAsync(
                    context, NightlyProductScope.Final, night, NightlyProductWindowDisposition.NoSources, null, 0, [],
                    empty, cancellationToken).ConfigureAwait(false);
                context.FinalsRecorded++;
                return;
            }
            if (context.Kind == NightlyProductKind.Keogram && context.Geometry is null)
            {
                reason = NightlyProductContract.GeometryUnavailableReasonCode;
            }
            else if (context.Kind == NightlyProductKind.Keogram && segments.Count > NightlyProductRecipeLimits.MaximumSourceCount)
            {
                reason = NightlyProductContract.FinalSegmentBoundReasonCode;
            }
            else if (segments.Select(static segment => (segment.Variant, segment.RecipeIdentitySha256)).Distinct().Count() > 1)
            {
                reason = MixedSegmentRecipesReasonCode;
            }
            var identities = segments.Select(static segment => segment.OutputIdentitySha256).ToArray();
            if (reason is not null)
            {
                context.FailedWindows = 1;
                await RecordUnlessUnchangedAsync(
                    context, NightlyProductScope.Final, night, NightlyProductWindowDisposition.Rejected, reason,
                    segments.Count, identities, empty, cancellationToken).ConfigureAwait(false);
                context.FinalsRecorded++;
                return;
            }

            var fingerprint = Fingerprint(context, NightlyProductScope.Final, identities, segments.Count, empty, null);
            if (await IsUnchangedAsync(context, NightlyProductScope.Final, night.StartUtc, fingerprint, cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }
            var outcome = context.Kind == NightlyProductKind.Keogram
                ? await ProduceKeogramNightAsync(context, night, segments, cancellationToken).ConfigureAwait(false)
                : await ProduceStarTrailNightAsync(context, night, segments, cancellationToken).ConfigureAwait(false);
            if (outcome.Pending)
            {
                context.PendingWindows++;
                return;
            }
            await RecordAsync(
                context,
                NightlyProductScope.Final,
                night,
                outcome.RejectionReasonCode is null
                    ? NightlyProductWindowDisposition.Produced
                    : NightlyProductWindowDisposition.Rejected,
                outcome.RejectionReasonCode,
                segments.Count,
                segments.Count,
                empty,
                fingerprint,
                outcome.RejectionReasonCode is null ? outcome.ProductIds : [],
                cancellationToken).ConfigureAwait(false);
            context.FinalsRecorded++;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            context.FailedWindows = 1;
        }
    }

    private async ValueTask<NightOutcome> ProduceKeogramNightAsync(
        RunContext context,
        NightlyProductWindow night,
        IReadOnlyList<NightlyProductDetail> segments,
        CancellationToken cancellationToken)
    {
        // The axes are rebuilt from each segment's exact lineage frame times under the same options it was composed
        // with, so assembly re-lays exactly the columns the segment holds.
        var axes = new KeogramSegmentAxesV1(
            KeogramSegmentAxesV1.CurrentSchemaVersion,
            [.. segments.Select(segment => KeogramSegmentAxesJson.CreateSegment(
                segment.Summary.ProductId,
                [.. segment.Sources.Select(static source => source.ObservationStartedUtc)],
                context.KeogramOptions))]);
        var result = await ProduceFromProductsAsync(
            context,
            NightlyProductScope.Final,
            night,
            0,
            BuiltInProcessingRecipes.KeogramAssembly,
            KeogramFinalVariant,
            [KeogramGeometryJson.CreateAuxiliaryInput(context.Geometry!), KeogramSegmentAxesJson.CreateAuxiliaryInput(axes)],
            segments,
            cancellationToken).ConfigureAwait(false);
        return result.Pending
            ? NightOutcome.PendingOutcome
            : new NightOutcome(false, result.RejectionReasonCode, result.ProductId is { } id ? [id] : []);
    }

    private async ValueTask<NightOutcome> ProduceStarTrailNightAsync(
        RunContext context,
        NightlyProductWindow night,
        IReadOnlyList<NightlyProductDetail> segments,
        CancellationToken cancellationToken)
    {
        // Lighten is associative, so reducing in bounded rollups yields the pixels of one direct reduction while
        // every intermediate keeps its own published lineage.
        var fanIn = Math.Max(2, Math.Min(context.Options.MaximumSegmentSources,
            (int)(NightlyProductRecipeLimits.MaximumSourceBytes / segments.Max(static product => product.PayloadBytes))));
        var products = new List<Guid>();
        IReadOnlyList<NightlyProductDetail> level = segments;
        var rollupOrdinal = 0;
        while (level.Count > fanIn)
        {
            var next = new List<NightlyProductDetail>();
            foreach (var chunk in level.Chunk(fanIn))
            {
                var rollup = await ProduceFromProductsAsync(
                    context, NightlyProductScope.Rollup, night, rollupOrdinal++, BuiltInProcessingRecipes.StarTrail,
                    StarTrailRollupVariant, null, chunk, cancellationToken).ConfigureAwait(false);
                if (rollup.Pending)
                {
                    return NightOutcome.PendingOutcome;
                }
                if (rollup.RejectionReasonCode is { } reason)
                {
                    return new NightOutcome(false, reason, []);
                }
                products.Add(rollup.ProductId!.Value);
                next.Add(await _store.GetAsync(rollup.ProductId.Value, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("A published star-trail rollup could not be read back."));
            }
            level = next;
        }
        var final = await ProduceFromProductsAsync(
            context, NightlyProductScope.Final, night, 0, BuiltInProcessingRecipes.StarTrail, StarTrailFinalVariant,
            null, level, cancellationToken).ConfigureAwait(false);
        if (final.Pending)
        {
            return NightOutcome.PendingOutcome;
        }
        if (final.RejectionReasonCode is { } finalReason)
        {
            return new NightOutcome(false, finalReason, []);
        }
        products.Add(final.ProductId!.Value);
        return new NightOutcome(false, null, products);
    }

    private ValueTask<ProduceResult> ProduceFromProductsAsync(
        RunContext context,
        NightlyProductScope scope,
        NightlyProductWindow window,
        int ordinal,
        string recipeName,
        string variant,
        IReadOnlyList<ProcessingAuxiliaryInput>? auxiliaryInputs,
        IReadOnlyList<NightlyProductDetail> inputs,
        CancellationToken cancellationToken)
    {
        if (inputs.Sum(static input => input.PayloadBytes) > NightlyProductRecipeLimits.MaximumSourceBytes)
            return ValueTask.FromResult(ProduceResult.Rejected("nightly.source-byte-bound"));
        var byId = inputs.ToDictionary(static input => input.Summary.ProductId);
        return ProduceAsync(
            context,
            scope,
            window,
            ordinal,
            recipeName,
            recipeName == BuiltInProcessingRecipes.StarTrail ? context.StarTrailRecipeOptions : context.RecipeOptions,
            variant,
            ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, inputs[0].Variant, inputs[0].RecipeIdentitySha256),
            auxiliaryInputs,
            [.. inputs.Select(static input => (input.Summary.ProductId, input.Sources[0].ObservationStartedUtc))],
            async token =>
            {
                var artifacts = new List<ProcessingArtifact>(inputs.Count);
                foreach (var input in inputs)
                {
                    artifacts.Add(ToArtifact(
                        await _store.ReadStoredProductAsync(input.Summary.ProductId, token).ConfigureAwait(false)));
                }
                return artifacts;
            },
            id => (NightlyProductSourceKind.NightlyProduct, byId[id].OutputIdentitySha256, null),
            cancellationToken);
    }

    /// <summary>
    /// Produces one part. Its output identity is computed before any payload is restored, so a part already published
    /// is reused without recomputation, and a recomputed product whose identity differs from the prediction is a
    /// lineage defect rather than a new product.
    /// </summary>
    private async ValueTask<ProduceResult> ProduceAsync(
        RunContext context,
        NightlyProductScope scope,
        NightlyProductWindow window,
        int ordinal,
        string recipeName,
        JsonElement recipeOptions,
        string variant,
        ProcessingInputSelector selector,
        IReadOnlyList<ProcessingAuxiliaryInput>? auxiliaryInputs,
        IReadOnlyList<(Guid Id, DateTimeOffset ObservationStartedUtc)> sources,
        Func<CancellationToken, ValueTask<IReadOnlyList<ProcessingArtifact>>> restore,
        Func<Guid, (NightlyProductSourceKind Kind, string OutputIdentitySha256, Guid? CaptureId)> describe,
        CancellationToken cancellationToken)
    {
        auxiliaryInputs = [.. auxiliaryInputs ?? [], NightlyProductPreset.BindOccurrence(context.Occurrence)];
        var recipeIdentity = BuiltInProcessingRecipes.CreateExecutionIdentity(
            recipeName, recipeOptions, selector, annotation: null, auxiliaryInputs).IdentitySha256;
        // The recipes order sources by observation start then artifact identity; the same order predicts the identity.
        Guid[] orderedIds = [.. sources
            .OrderBy(static source => source.ObservationStartedUtc)
            .ThenBy(static source => source.Id)
            .Select(static source => source.Id)];
        var predicted = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Preview, variant, recipeIdentity, orderedIds);
        if (await _store.FindByOutputIdentityAsync(predicted, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            await _store.VerifyPublicationAsync(existing.Summary.ProductId, cancellationToken).ConfigureAwait(false);
            context.Reused++;
            return ProduceResult.Produced(existing.Summary.ProductId);
        }
        if (context.RemainingExecutions == 0)
        {
            return ProduceResult.PendingResult;
        }
        context.RemainingExecutions--;

        var inputs = await restore(cancellationToken).ConfigureAwait(false);
        var outcome = await _executor.ExecuteAsync(
            new ProcessingExecutionRequest(recipeName, recipeOptions, selector, inputs, variant, null, auxiliaryInputs),
            cancellationToken).ConfigureAwait(false);
        if (outcome.Status != ProcessingOutcomeStatus.Produced || outcome.Products.Count != 1)
        {
            return ProduceResult.Rejected(outcome.ReasonCode ?? RecipeFailedReasonCode);
        }
        var product = outcome.Products[0];
        if (!string.Equals(product.OutputIdentitySha256, predicted, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A nightly product's output identity differs from its predicted lineage.");
        }
        var byId = inputs.ToDictionary(static input => input.ArtifactId);
        var lineage = product.SourceArtifactIds.Select((id, index) =>
        {
            var facts = describe(id);
            var input = byId[id];
            return new NightlyProductSource(
                index, facts.Kind, id, facts.OutputIdentitySha256, facts.CaptureId,
                (input.ObservationStartedUtc ?? input.CreatedUtc).ToUniversalTime());
        }).ToArray();
        var detail = await _store.PublishAsync(
            new NightlyProductPublication(
                context.Kind, scope, window.ObservingDate, window.StartUtc, window.EndUtc, ordinal, recipeName, product,
                lineage, context.Options.RenditionJpegQuality)
            { Occurrence = context.Occurrence },
            cancellationToken).ConfigureAwait(false);
        context.Published++;
        return ProduceResult.Produced(detail.Summary.ProductId);
    }

    /// <summary>
    /// Splits admitted sources, in recipe order, into parts that share one input selector and hold at most
    /// <paramref name="maximumSources"/> sources each.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<NightlyProductCandidate>> Partition(
        IReadOnlyList<NightlyProductCandidate> admitted,
        int maximumSources)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSources, 1);
        var parts = new List<IReadOnlyList<NightlyProductCandidate>>();
        var current = new List<NightlyProductCandidate>();
        long currentBytes = 0;
        foreach (var candidate in admitted
            .OrderBy(static candidate => candidate.ExposureStartedUtc)
            .ThenBy(static candidate => candidate.ArtifactId))
        {
            if (current.Count == maximumSources || currentBytes + candidate.PayloadBytes > NightlyProductRecipeLimits.MaximumSourceBytes ||
                current.Count > 0 &&
                (!string.Equals(current[0].Variant, candidate.Variant, StringComparison.Ordinal) ||
                 !string.Equals(current[0].RecipeIdentitySha256, candidate.RecipeIdentitySha256, StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add(current);
                current = [];
                currentBytes = 0;
            }
            current.Add(candidate);
            currentBytes += candidate.PayloadBytes;
        }
        if (current.Count > 0)
        {
            parts.Add(current);
        }
        return parts;
    }

    private ObservatoryLocation? Locate(NightlyProductCandidate candidate, CameraModuleConfig configuration)
    {
        if (candidate.Location is { } provenance)
        {
            if (_locationStore() is not { } store)
            {
                return null;
            }
            try
            {
                return store.Resolve(provenance, candidate.ExposureStartedUtc).ToObservatoryLocation();
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                return null;
            }
        }
        // A capture without location provenance is located by the static observatory only when this CameraAgent has
        // never used a versioned deployment location; otherwise its location is unknown, not assumed.
        return configuration.DeploymentLocation is null && !configuration.DeploymentLocationRedacted
            ? configuration.Observatory
            : null;
    }

    private async ValueTask<bool> IsUnchangedAsync(
        RunContext context,
        NightlyProductScope scope,
        DateTimeOffset windowStartUtc,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var existing = await _store.ReadWindowAsync(context.Kind, scope, windowStartUtc, cancellationToken, context.Occurrence.IdentitySha256)
            .ConfigureAwait(false);
        if (existing is not null && string.Equals(existing.FingerprintSha256, fingerprint, StringComparison.Ordinal))
        {
            foreach (var productId in existing.ProductIds)
                await _store.VerifyPublicationAsync(productId, cancellationToken).ConfigureAwait(false);
            context.WindowsUnchanged++;
            return true;
        }
        return false;
    }

    private async ValueTask<bool> RecordUnlessUnchangedAsync(
        RunContext context,
        NightlyProductScope scope,
        NightlyProductWindow window,
        NightlyProductWindowDisposition disposition,
        string? reasonCode,
        int candidateCount,
        IReadOnlyList<string> inputs,
        IReadOnlyDictionary<string, int> exclusions,
        CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(context, scope, inputs, candidateCount, exclusions, reasonCode);
        if (await IsUnchangedAsync(context, scope, window.StartUtc, fingerprint, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        return await RecordAsync(
            context, scope, window, disposition, reasonCode, candidateCount, 0, exclusions, fingerprint, [],
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> RecordAsync(
        RunContext context,
        NightlyProductScope scope,
        NightlyProductWindow window,
        NightlyProductWindowDisposition disposition,
        string? reasonCode,
        int candidateCount,
        int admittedCount,
        IReadOnlyDictionary<string, int> exclusions,
        string fingerprint,
        IReadOnlyList<Guid> productIds,
        CancellationToken cancellationToken)
    {
        if (disposition == NightlyProductWindowDisposition.Rejected) context.FailedWindows = 1;
        await _store.RecordWindowAsync(
            new NightlyProductWindowStatus(
                context.Kind, scope, window.ObservingDate, window.StartUtc, window.EndUtc, disposition, reasonCode,
                candidateCount, admittedCount, exclusions, context.NowUtc)
            { Occurrence = context.Occurrence },
            fingerprint,
            productIds,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The identity of everything a window's evaluation depends on: the kind's effective options and geometry, the
    /// rig, the admission thresholds, the ordered admitted inputs, and the exclusion counts. An unchanged fingerprint
    /// means the recorded evaluation is still current.
    /// </summary>
    private static string Fingerprint(
        RunContext context,
        NightlyProductScope scope,
        IReadOnlyList<string> inputs,
        int candidateCount,
        IReadOnlyDictionary<string, int> exclusions,
        string? reasonCode) =>
        CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schemaVersion = FingerprintSchemaVersion,
            kind = context.Kind.ToString(),
            scope = scope.ToString(),
            rigProfileSha256 = context.RigProfileSha256,
            geometryIdentitySha256 = context.Geometry is { } geometry
                ? KeogramGeometryJson.ComputeIdentitySha256(geometry)
                : null,
            recipeOptions = context.RecipeOptions,
            maximumSegmentSources = context.Options.MaximumSegmentSources,
            occurrenceIdentitySha256 = context.Occurrence.IdentitySha256,
            renditionJpegQuality = context.Options.RenditionJpegQuality,
            inputs,
            candidateCount,
            exclusions = new SortedDictionary<string, int>(exclusions.ToDictionary(), StringComparer.Ordinal),
            reasonCode
        });

    private static ProcessingArtifact ToArtifact(NightlyStoredProduct stored) => new(
        stored.Detail.Summary.ProductId,
        FrameArtifactRole.Preview,
        stored.Detail.Variant,
        stored.Detail.RecipeIdentitySha256,
        JournalNightlyProductSourceReader.PackedImageMediaType,
        stored.Layout,
        stored.Payload,
        stored.Detail.Summary.CreatedUtc,
        stored.Detail.Summary.TotalIntegration,
        stored.Compatibility,
        SourceArtifactIds: [.. stored.Detail.Sources.Select(static source => source.ArtifactId)],
        ObservationStartedUtc: stored.Detail.Sources[0].ObservationStartedUtc);

    private static NightlyProductRunReport NotReady(NightlyProductKind kind, string note) =>
        new(kind, Ready: false, 0, 0, 0, 0, 0, 0, 0, note);

    private sealed record ProduceResult(bool Pending, string? RejectionReasonCode, Guid? ProductId)
    {
        internal static readonly ProduceResult PendingResult = new(true, null, null);

        internal static ProduceResult Produced(Guid productId) => new(false, null, productId);

        internal static ProduceResult Rejected(string reasonCode) => new(false, reasonCode, null);
    }

    private sealed record NightOutcome(bool Pending, string? RejectionReasonCode, IReadOnlyList<Guid> ProductIds)
    {
        internal static readonly NightOutcome PendingOutcome = new(true, null, []);
    }

    /// <summary>The fixed inputs and running counters of one run.</summary>
    private sealed class RunContext
    {
        internal RunContext(
            NightlyProductKind kind,
            NightlyProductOptions options,
            CameraModuleConfig configuration,
            DateTimeOffset nowUtc,
            LocalAutomationOccurrence occurrence)
        {
            Occurrence = occurrence;
            Kind = kind;
            Options = options;
            Configuration = configuration;
            NowUtc = nowUtc;
            RemainingExecutions = options.MaximumSegmentsPerRun;
            RigProfileSha256 = RigProjectionContextFactory.CreateProfileHashSha256(configuration.Rig);
            KeogramOptions = new KeogramRecipeOptions(
                options.KeogramMaximumGapSeconds, options.KeogramMaximumGapColumnCount, options.KeogramMaximumColumnCount);
            StarTrailRecipeOptions = JsonSerializer.SerializeToElement(
                new StarTrailRecipeOptions(NightlyProductRecipeLimits.MaximumSourceCount));
            if (kind == NightlyProductKind.Keogram)
            {
                SegmentRecipe = BuiltInProcessingRecipes.Keogram;
                SegmentVariant = KeogramSegmentVariant;
                SegmentRecipeOptions = JsonSerializer.SerializeToElement(KeogramOptions);
                RecipeOptions = JsonSerializer.SerializeToElement(KeogramOptions with
                {
                    PlannedAxis = new PlannedKeogramAxis(occurrence.SourceWindow!.StartUtc, occurrence.SourceWindow.EndUtc,
                        TimeSpan.FromSeconds(options.KeogramColumnSeconds))
                });
                try
                {
                    Geometry = KeogramGeometryV1.Create(RigProjectionContextFactory.Create(configuration.Rig), RigProfileSha256);
                    SegmentAuxiliaryInputs = [KeogramGeometryJson.CreateAuxiliaryInput(Geometry)];
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    GeometryUnavailable = true;
                }
            }
            else
            {
                SegmentRecipe = BuiltInProcessingRecipes.StarTrail;
                SegmentVariant = StarTrailSegmentVariant;
                SegmentRecipeOptions = StarTrailRecipeOptions;
                RecipeOptions = StarTrailRecipeOptions;
            }
        }

        internal NightlyProductKind Kind { get; }
        internal NightlyProductOptions Options { get; }
        internal CameraModuleConfig Configuration { get; }
        internal DateTimeOffset NowUtc { get; }
        internal string RigProfileSha256 { get; }
        internal LocalAutomationOccurrence Occurrence { get; }
        internal JsonElement SegmentRecipeOptions { get; }
        internal KeogramRecipeOptions KeogramOptions { get; }
        internal JsonElement StarTrailRecipeOptions { get; }
        internal JsonElement RecipeOptions { get; }
        internal string SegmentRecipe { get; }
        internal string SegmentVariant { get; }
        internal KeogramGeometryV1? Geometry { get; }
        internal IReadOnlyList<ProcessingAuxiliaryInput>? SegmentAuxiliaryInputs { get; }
        internal bool GeometryUnavailable { get; }
        internal int RemainingExecutions { get; set; }
        internal int WindowsEvaluated { get; set; }
        internal int WindowsUnchanged { get; set; }
        internal int Published { get; set; }
        internal int Reused { get; set; }
        internal int PendingWindows { get; set; }
        internal int FailedWindows { get; set; }
        internal int FinalsRecorded { get; set; }
    }
}
