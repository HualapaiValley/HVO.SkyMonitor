using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
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
    int NightsRecorded,
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
            .Append(CultureInfo.InvariantCulture, $"{NightsRecorded} nights recorded, {PendingWindows} pending, ")
            .Append(CultureInfo.InvariantCulture, $"{FailedWindows} failed.");
        if (Note is not null)
        {
            builder.Append(' ').Append(Note);
        }
        return builder.ToString();
    }
}

/// <summary>
/// Generates keograms and star trails from published preview frames on the automation runner's schedule. Each run
/// evaluates the settled segment windows of the previous and current observing day, admits sources from captured
/// facts, and composes each window in bounded ordered parts. A night is composed from its segments once its whole
/// observing day has settled: a keogram night is assembled byte-identically to direct composition, and a star-trail
/// night is reduced through a lineage tree of rollups. Product identity is the recipe's output identity, so a re-run
/// over unchanged inputs reuses rather than recomputes, and a run is bounded by
/// <see cref="NightlyProductOptions.MaximumSegmentsPerRun"/> recipe executions, leaving the remainder pending.
/// </summary>
internal sealed class NightlyProductGenerator : IDisposable
{
    internal const string KeogramSegmentVariant = "nightly-keogram-segment-v1";
    internal const string KeogramNightVariant = "nightly-keogram-night-v1";
    internal const string StarTrailSegmentVariant = "nightly-star-trail-segment-v1";
    internal const string StarTrailRollupVariant = "nightly-star-trail-rollup-v1";
    internal const string StarTrailNightVariant = "nightly-star-trail-night-v1";
    internal const string MixedSegmentRecipesReasonCode = "nightly.mixed-segment-recipes";
    internal const string RecipeFailedReasonCode = "nightly.recipe-failed";
    private const string FingerprintSchemaVersion = "hvo-nightly-window-fingerprint-v1";

    private readonly CameraAgentHostOptions _options;
    private readonly ICameraAgentConfigurationAccessor _configuration;
    private readonly IObservingDayCalendarProvider _calendars;
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
        IObservingDayCalendarProvider calendars,
        INightlyProductSourceReader sources,
        SqliteNightlyProductStore store,
        IPlanetEphemeris ephemeris,
        Func<IDeploymentLocationStore?> locationStore,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _configuration = configuration;
        _calendars = calendars;
        _sources = sources;
        _store = store;
        _ephemeris = ephemeris;
        _locationStore = locationStore;
        _timeProvider = timeProvider;
    }

    public void Dispose() => _run.Dispose();

    /// <summary>Runs one scheduled occurrence. Runs of either kind are serialized within the process.</summary>
    internal async ValueTask<NightlyProductRunReport> RunAsync(NightlyProductKind kind, CancellationToken cancellationToken)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(kind, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _run.Release();
        }
    }

    private async ValueTask<NightlyProductRunReport> RunCoreAsync(NightlyProductKind kind, CancellationToken cancellationToken)
    {
        var options = _options.NightlyProducts;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.SourceNodeId))
        {
            return NotReady(kind, "Nightly product generation is disabled in this CameraAgent's startup configuration.");
        }
        if (!_configuration.IsConfigured)
        {
            return NotReady(kind, "The camera configuration has not been loaded yet.");
        }
        var configuration = await _configuration.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var context = new RunContext(kind, options, configuration, _timeProvider.GetUtcNow().ToUniversalTime());
        var settle = TimeSpan.FromSeconds(options.SettleSeconds);
        foreach (var day in NightlyProductWindowPlanner.ResolveDays(_calendars.Current, context.NowUtc))
        {
            var windows = NightlyProductWindowPlanner.SegmentWindows(day, options.SegmentMinutes);
            var complete = true;
            foreach (var window in windows)
            {
                if (!NightlyProductWindowPlanner.IsDue(window.EndUtc, settle, context.NowUtc))
                {
                    complete = false;
                    break;
                }
                complete &= await EvaluateSegmentAsync(context, window, cancellationToken).ConfigureAwait(false);
            }
            if (complete && NightlyProductWindowPlanner.IsDue(day.EndUtc, settle, context.NowUtc))
            {
                await EvaluateNightAsync(context, day, windows, cancellationToken).ConfigureAwait(false);
            }
        }
        return new NightlyProductRunReport(
            kind, Ready: true, context.WindowsEvaluated, context.WindowsUnchanged, context.Published, context.Reused,
            context.PendingWindows, context.FailedWindows, context.NightsRecorded,
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
            if (candidates.Count > NightlyProductContract.MaximumWindowCandidates)
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
                context.MaximumSolarAltitudeDegrees,
                candidate => Locate(candidate, context.Configuration),
                _ephemeris,
                unsupported);
            if (admission.Admitted.Count == 0)
            {
                return await RecordUnlessUnchangedAsync(
                    context, NightlyProductScope.Segment, window, NightlyProductWindowDisposition.NoSources,
                    null, candidateCount, [], admission.Exclusions, cancellationToken).ConfigureAwait(false);
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
            var parts = Partition(admission.Admitted, context.Options.MaximumSegmentSources);
            for (var ordinal = 0; ordinal < parts.Count; ordinal++)
            {
                var part = parts[ordinal];
                var result = await ProduceAsync(
                    context,
                    NightlyProductScope.Segment,
                    window,
                    ordinal,
                    context.SegmentRecipe,
                    context.RecipeOptions,
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
            context.FailedWindows++;
            return false;
        }
    }

    /// <summary>Composes a settled observing day from the current products of its segment windows.</summary>
    private async ValueTask EvaluateNightAsync(
        RunContext context,
        ObservingDay day,
        IReadOnlyList<NightlyProductWindow> windows,
        CancellationToken cancellationToken)
    {
        var night = new NightlyProductWindow(day.Date, day.StartUtc, day.EndUtc);
        try
        {
            var segments = new List<NightlyProductDetail>();
            foreach (var window in windows)
            {
                var state = await _store.ReadWindowAsync(
                    context.Kind, NightlyProductScope.Segment, window.StartUtc, cancellationToken).ConfigureAwait(false);
                if (state is null)
                {
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
                    context, NightlyProductScope.Night, night, NightlyProductWindowDisposition.NoSources, null, 0, [],
                    empty, cancellationToken).ConfigureAwait(false);
                context.NightsRecorded++;
                return;
            }
            if (context.Kind == NightlyProductKind.Keogram && context.Geometry is null)
            {
                reason = NightlyProductContract.GeometryUnavailableReasonCode;
            }
            else if (context.Kind == NightlyProductKind.Keogram && segments.Count > NightlyProductRecipeLimits.MaximumSourceCount)
            {
                reason = NightlyProductContract.NightSegmentBoundReasonCode;
            }
            else if (segments.Select(static segment => (segment.Variant, segment.RecipeIdentitySha256)).Distinct().Count() > 1)
            {
                reason = MixedSegmentRecipesReasonCode;
            }
            var identities = segments.Select(static segment => segment.OutputIdentitySha256).ToArray();
            if (reason is not null)
            {
                await RecordUnlessUnchangedAsync(
                    context, NightlyProductScope.Night, night, NightlyProductWindowDisposition.Rejected, reason,
                    segments.Count, identities, empty, cancellationToken).ConfigureAwait(false);
                context.NightsRecorded++;
                return;
            }

            var fingerprint = Fingerprint(context, NightlyProductScope.Night, identities, segments.Count, empty, null);
            if (await IsUnchangedAsync(context, NightlyProductScope.Night, night.StartUtc, fingerprint, cancellationToken)
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
                NightlyProductScope.Night,
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
            context.NightsRecorded++;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            context.FailedWindows++;
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
            NightlyProductScope.Night,
            night,
            0,
            BuiltInProcessingRecipes.KeogramAssembly,
            KeogramNightVariant,
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
        var fanIn = Math.Max(2, context.Options.MaximumSegmentSources);
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
            context, NightlyProductScope.Night, night, 0, BuiltInProcessingRecipes.StarTrail, StarTrailNightVariant,
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
                lineage, context.Options.RenditionJpegQuality),
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
        foreach (var candidate in admitted
            .OrderBy(static candidate => candidate.ExposureStartedUtc)
            .ThenBy(static candidate => candidate.ArtifactId))
        {
            if (current.Count == maximumSources ||
                current.Count > 0 &&
                (!string.Equals(current[0].Variant, candidate.Variant, StringComparison.Ordinal) ||
                 !string.Equals(current[0].RecipeIdentitySha256, candidate.RecipeIdentitySha256, StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add(current);
                current = [];
            }
            current.Add(candidate);
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
        var existing = await _store.ReadWindowAsync(context.Kind, scope, windowStartUtc, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null && string.Equals(existing.FingerprintSha256, fingerprint, StringComparison.Ordinal))
        {
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
        await _store.RecordWindowAsync(
            new NightlyProductWindowStatus(
                context.Kind, scope, window.ObservingDate, window.StartUtc, window.EndUtc, disposition, reasonCode,
                candidateCount, admittedCount, exclusions, context.NowUtc),
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
            maximumSolarAltitudeDegrees = context.MaximumSolarAltitudeDegrees,
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
            DateTimeOffset nowUtc)
        {
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
                MaximumSolarAltitudeDegrees = options.KeogramMaximumSolarAltitudeDegrees;
                SegmentRecipe = BuiltInProcessingRecipes.Keogram;
                SegmentVariant = KeogramSegmentVariant;
                RecipeOptions = JsonSerializer.SerializeToElement(KeogramOptions);
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
                MaximumSolarAltitudeDegrees = options.StarTrailMaximumSolarAltitudeDegrees;
                SegmentRecipe = BuiltInProcessingRecipes.StarTrail;
                SegmentVariant = StarTrailSegmentVariant;
                RecipeOptions = StarTrailRecipeOptions;
            }
        }

        internal NightlyProductKind Kind { get; }
        internal NightlyProductOptions Options { get; }
        internal CameraModuleConfig Configuration { get; }
        internal DateTimeOffset NowUtc { get; }
        internal string RigProfileSha256 { get; }
        internal double MaximumSolarAltitudeDegrees { get; }
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
        internal int NightsRecorded { get; set; }
    }
}
