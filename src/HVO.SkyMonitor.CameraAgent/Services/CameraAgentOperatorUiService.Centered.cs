using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Settings;
using Microsoft.AspNetCore.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed partial class CameraAgentOperatorUiService
{
    private const int MaximumArchiveRevisionsPerBase = 128;

    private async ValueTask<ClaimsPrincipal?> GetAuthorizedReadPrincipalAsync()
    {
        var authenticationState = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        return (await authorizationService.AuthorizeAsync(
                authenticationState.User,
                CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false)).Succeeded
            ? authenticationState.User
            : null;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The operator boundary logs internal failures and returns only fixed, sanitized states.")]
    private async ValueTask<OperatorUiResult<CameraAgentCurrentSkyView>?> TryGetCenteredCurrentSkyViewAsync(
        CenteredLiveMode mode,
        CancellationToken cancellationToken)
    {
        CenteredViewLiveContext? context;
        try
        {
            context = await centeredViewLookup!.ReadLiveContextAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent centered live context read failed.");
            return Unavailable<CameraAgentCurrentSkyView>("The current sky image is temporarily unavailable.");
        }

        if (context is null)
        {
            return null;
        }

        try
        {
            CenteredView? view = null;
            CenteredViewLiveInterval? interval = null;
            var retainedFromPriorRun = false;

            if (mode == CenteredLiveMode.Single)
            {
                view = await centeredViewIndex!.ReadLatestSingleAsync(context.Stream, cancellationToken).ConfigureAwait(false);
                if (view is null && context.State is CenteredViewLiveState.Warming or CenteredViewLiveState.Paused or CenteredViewLiveState.Stale)
                {
                    var lastLive = await centeredViewIndex.ReadLastLiveAsync(context.Stream.Camera, cancellationToken).ConfigureAwait(false);
                    if (lastLive is not null)
                    {
                        view = lastLive.View;
                        interval = lastLive.Interval;
                        retainedFromPriorRun = lastLive.View.Base.Definition.Stream != context.Stream;
                    }
                }
            }
            else
            {
                var current = await centeredViewIndex!.ReadCurrentAsync(context.Stream, cancellationToken).ConfigureAwait(false);
                if (current is not null)
                {
                    view = current.View;
                    interval = current.Interval;
                }
                else
                {
                    var lastLive = await centeredViewIndex.ReadLastLiveAsync(context.Stream.Camera, cancellationToken).ConfigureAwait(false);
                    if (lastLive is not null)
                    {
                        view = lastLive.View;
                        interval = lastLive.Interval;
                        retainedFromPriorRun = lastLive.View.Base.Definition.Stream != context.Stream;
                    }
                }
            }

            CameraAgentLayeredPresentation? presentation = null;
            var presentationStatus = CameraAgentLayeredPresentationStatus.NotRetained;
            string? presentationReason = null;
            Uri? previewUrl = null;
            if (view is not null)
            {
                previewUrl = CameraAgentCenteredViewProjection.BoundPreviewUrl(view);
                var rendered = await centeredViewPresentation!
                    .ReadAsync(view.Base.BaseId, view.Revision.RevisionId, cancellationToken)
                    .ConfigureAwait(false);
                presentationStatus = rendered.Status;
                presentationReason = rendered.Reason;
                if (rendered.Status == CameraAgentLayeredPresentationStatus.Found &&
                    CameraAgentCenteredViewProjection.IsPresentationCompatible(view, rendered.Presentation))
                {
                    presentation = rendered.Presentation;
                }
                else if (rendered.Status == CameraAgentLayeredPresentationStatus.Found)
                {
                    presentationStatus = CameraAgentLayeredPresentationStatus.Malformed;
                    presentationReason = "The centered overlay presentation does not match the exact base preview.";
                }
            }

            var observedUtc = timeProvider.GetUtcNow().ToUniversalTime();
            if (context.ObservedUtc > observedUtc)
            {
                observedUtc = context.ObservedUtc;
            }

            var displayCapture = view is null
                ? null
                : ProjectCenteredCapture(view.Base.Definition.Reference, observedUtc);
            var latestCapture = context.AdmissionHead is { } head
                ? head with { AgeSeconds = Math.Max(0, (long)(observedUtc - head.ExposureStartedUtc).TotalSeconds) }
                : displayCapture;
            var system = ProjectCenteredSystemStatus(context, observedUtc);
            var freshness = view is null
                ? CameraAgentPresentationImageFreshness.Empty
                : retainedFromPriorRun
                    ? CameraAgentPresentationImageFreshness.Historical
                    : context.State is CenteredViewLiveState.Stale or CenteredViewLiveState.Gap
                        ? CameraAgentPresentationImageFreshness.Stale
                        : CameraAgentPresentationImageFreshness.Current;

            var imagePresentation = new CameraAgentCurrentImagePresentation(
                observedUtc,
                freshness,
                system,
                latestCapture,
                displayCapture,
                IsHistoricalFallback: retainedFromPriorRun,
                SelectedStage: null,
                Stages: [],
                StructuredLayersAvailable: presentation is not null,
                HistoryBoundReached: false);

            CameraAgentCurrentSkyFacts? facts = null;
            string? factsUnavailableReason = null;
            if (displayCapture is not null)
            {
                (facts, factsUnavailableReason) = await ReadCachedFactsAsync(
                    displayCapture.CaptureId,
                    combinedArtifactId: null,
                    cancellationToken).ConfigureAwait(false);
            }

            var minimumRefresh = TimeSpan.FromSeconds(currentSkyRefresh?.Seconds ?? CurrentSkyRefreshSetting.DefaultSeconds);
            var centeredLive = new CameraAgentCenteredLiveView(
                context,
                mode,
                view,
                interval,
                presentation,
                presentationStatus,
                presentationReason,
                retainedFromPriorRun,
                previewUrl);

            return OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                new CameraAgentCurrentSkyView(imagePresentation, facts, factsUnavailableReason, CreateCurrentSkyOperations())
                {
                    ProcessingPending = false,
                    MinimumRefreshInterval = minimumRefresh,
                    Centered = centeredLive
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent centered current sky view read failed.");
            return Unavailable<CameraAgentCurrentSkyView>("The current sky image is temporarily unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Archive centered view lookups sanitize failures and never substitute another base or capture.")]
    private async ValueTask<CameraAgentCenteredArchiveView?> ReadCenteredArchiveAsync(
        Guid captureId,
        CameraAgentCenteredArchiveRequest? request,
        string? owner,
        CancellationToken cancellationToken)
    {
        if (centeredViewLookup is null || centeredViewIndex is null || centeredViewPresentation is null)
        {
            if (request?.HasExplicitTarget == true)
            {
                return new CameraAgentCenteredArchiveView(
                    captureId, [], null, null, CameraAgentLayeredPresentationStatus.Unavailable,
                    "Centered archive views are unavailable on this host.", null,
                    "The requested centered view revision or saved selection is unavailable.", null);
            }
            return null;
        }

        try
        {
            var archiveBases = await centeredViewLookup.ReadArchiveAsync(captureId, cancellationToken).ConfigureAwait(false);
            if (archiveBases.Count == 0)
            {
                if (request?.HasExplicitTarget == true)
                {
                    return new CameraAgentCenteredArchiveView(
                        captureId, [], null, null, CameraAgentLayeredPresentationStatus.NotRetained,
                        null, null,
                        "The requested centered view revision or saved personal selection was not found for this capture.",
                        null);
                }
                return null;
            }

            var summaries = new List<CameraAgentCenteredArchiveBaseSummary>(archiveBases.Count);
            foreach (var archiveBase in archiveBases)
            {
                var revisions = await centeredViewIndex
                    .ReadRevisionsAsync(archiveBase.Base.BaseId, 0, MaximumArchiveRevisionsPerBase, cancellationToken)
                    .ConfigureAwait(false);
                var history = await centeredViewIndex
                    .ReadLiveHistoryAsync(archiveBase.Base.BaseId, 0, MaximumArchiveRevisionsPerBase, cancellationToken)
                    .ConfigureAwait(false);
                summaries.Add(new(archiveBase, CameraAgentCenteredViewProjection.ClassifyRevisions(archiveBase, revisions, history)));
            }

            CenteredView? selectedView = null;
            CenteredViewSavedSelection? savedSelection = null;
            string? selectionError = null;

            if (request?.SelectionId is { } selectionId)
            {
                if (selectionId == Guid.Empty || string.IsNullOrWhiteSpace(owner))
                {
                    selectionError = "The requested saved personal selection is invalid.";
                }
                else
                {
                    var saved = await centeredViewIndex.ReadSavedAsync(owner, selectionId, cancellationToken).ConfigureAwait(false);
                    if (saved is null)
                    {
                        selectionError = "The requested saved personal selection was not found for this account.";
                    }
                    else
                    {
                        var view = await centeredViewIndex.ReadRevisionAsync(saved.BaseId, saved.RevisionId, cancellationToken).ConfigureAwait(false);
                        if (view is null || view.Base.Definition.Reference.Capture.CaptureId != captureId)
                        {
                            selectionError = "The requested saved personal selection does not belong to this capture or is no longer retained.";
                        }
                        else
                        {
                            selectedView = view;
                            savedSelection = saved;
                        }
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(request?.BaseId) || !string.IsNullOrWhiteSpace(request?.RevisionId))
            {
                if (!CenteredViewLinks.IsIdentity(request!.BaseId) || !CenteredViewLinks.IsIdentity(request.RevisionId))
                {
                    selectionError = "The requested centered view base or revision identifier is invalid.";
                }
                else
                {
                    var view = await centeredViewIndex.ReadRevisionAsync(request.BaseId!, request.RevisionId!, cancellationToken).ConfigureAwait(false);
                    if (view is null || view.Base.Definition.Reference.Capture.CaptureId != captureId)
                    {
                        selectionError = "The requested centered view revision was not found or does not belong to this capture.";
                    }
                    else
                    {
                        selectedView = view;
                    }
                }
            }
            else
            {
                var preferred = archiveBases
                    .OrderByDescending(static entry => entry.Base.Definition.Kind == CenteredViewKind.Stack && entry.Base.Definition.RecomputedFromBaseId is null)
                    .ThenBy(static entry => entry.Base.Definition.RecomputedFromBaseId is not null)
                    .First();
                selectedView = await centeredViewIndex
                    .ReadRevisionAsync(preferred.Base.BaseId, preferred.DefaultRevisionId, cancellationToken)
                    .ConfigureAwait(false);
                if (selectedView is null)
                {
                    selectionError = "The retained centered view revision is no longer available.";
                }
            }

            CameraAgentLayeredPresentation? presentation = null;
            var presentationStatus = CameraAgentLayeredPresentationStatus.NotRetained;
            string? presentationReason = null;
            Uri? previewUrl = null;
            if (selectedView is not null)
            {
                previewUrl = CameraAgentCenteredViewProjection.BoundPreviewUrl(selectedView);
                var rendered = await centeredViewPresentation
                    .ReadAsync(selectedView.Base.BaseId, selectedView.Revision.RevisionId, cancellationToken)
                    .ConfigureAwait(false);
                presentationStatus = rendered.Status;
                presentationReason = rendered.Reason;
                if (rendered.Status == CameraAgentLayeredPresentationStatus.Found &&
                    CameraAgentCenteredViewProjection.IsPresentationCompatible(selectedView, rendered.Presentation))
                {
                    presentation = rendered.Presentation;
                }
                else if (rendered.Status == CameraAgentLayeredPresentationStatus.Found)
                {
                    presentationStatus = CameraAgentLayeredPresentationStatus.Malformed;
                    presentationReason = "The centered overlay presentation does not match the exact base preview.";
                }
            }

            return new CameraAgentCenteredArchiveView(
                captureId,
                summaries.AsReadOnly(),
                selectedView,
                presentation,
                presentationStatus,
                presentationReason,
                savedSelection,
                selectionError,
                previewUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent centered archive view read failed for capture {CaptureId}.", captureId);
            return new CameraAgentCenteredArchiveView(
                captureId, [], null, null, CameraAgentLayeredPresentationStatus.Unavailable,
                "Centered archive evidence is temporarily unavailable.", null,
                "The centered view evidence for this capture is temporarily unavailable.", null);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The operator boundary logs internal failures and returns only fixed, sanitized states.")]
    public async ValueTask<OperatorUiResult<CenteredViewSavedSelection>> SaveCenteredViewSelectionAsync(
        Guid referenceCaptureId,
        string baseId,
        string revisionId,
        IReadOnlyList<string> enabledLayerIds,
        CancellationToken cancellationToken)
    {
        var actor = await GetAuthorizedActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return Denied<CenteredViewSavedSelection>();
        }
        if (centeredViewIndex is null || centeredViewPublisher is null)
        {
            return Unavailable<CenteredViewSavedSelection>("Saving a centered view selection is unavailable.");
        }
        if (referenceCaptureId == Guid.Empty ||
            !CenteredViewLinks.IsIdentity(baseId) ||
            !CenteredViewLinks.IsIdentity(revisionId) ||
            enabledLayerIds is null)
        {
            return OperatorUiResult<CenteredViewSavedSelection>.Failure(
                OperatorUiResultKind.Invalid,
                "The selected centered view or layer list is invalid.");
        }

        try
        {
            var view = await centeredViewIndex.ReadRevisionAsync(baseId, revisionId, cancellationToken).ConfigureAwait(false);
            if (view is null || view.Base.Definition.Reference.Capture.CaptureId != referenceCaptureId)
            {
                return NotFound<CenteredViewSavedSelection>("The selected centered view revision was not found for this capture.");
            }

            var saved = await centeredViewPublisher.SaveAsync(
                actor,
                Guid.NewGuid(),
                baseId,
                revisionId,
                enabledLayerIds,
                cancellationToken).ConfigureAwait(false);
            return saved is null
                ? NotFound<CenteredViewSavedSelection>("The selected centered view revision was not found.")
                : OperatorUiResult<CenteredViewSavedSelection>.Success(saved);
        }
        catch (ArgumentException)
        {
            return OperatorUiResult<CenteredViewSavedSelection>.Failure(
                OperatorUiResultKind.Invalid,
                "One or more selected layers do not belong to the exact revision.");
        }
        catch (InvalidOperationException)
        {
            return OperatorUiResult<CenteredViewSavedSelection>.Failure(
                OperatorUiResultKind.Conflict,
                "The personal selection conflicts with an existing saved selection.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent centered view selection save failed.");
            return Unavailable<CenteredViewSavedSelection>("The personal view selection could not be saved.");
        }
    }

    private static CameraAgentPresentationCapture ProjectCenteredCapture(
        CenteredViewExposure reference,
        DateTimeOffset observedUtc) =>
        new(
            reference.Capture.CaptureId,
            reference.Capture.CaptureSequence,
            reference.ExposureStartedUtc,
            Math.Max(0, (long)(observedUtc - reference.ExposureStartedUtc).TotalSeconds),
            GalleryEvidenceOrigin.Unknown);

    private static CameraAgentPresentationSystemStatus ProjectCenteredSystemStatus(
        CenteredViewLiveContext context,
        DateTimeOffset observedUtc)
    {
        var (state, message) = context.State switch
        {
            CenteredViewLiveState.Paused => (CameraAgentPresentationSystemState.Paused, "Capture is paused; retaining the last published view."),
            CenteredViewLiveState.Warming => (CameraAgentPresentationSystemState.Starting, "Centered pipeline is warming up with single exposures."),
            CenteredViewLiveState.Building => (CameraAgentPresentationSystemState.Capturing, "Building centered stack while holding the ready base."),
            CenteredViewLiveState.Ready => (CameraAgentPresentationSystemState.Capturing, "CameraAgent is capturing and publishing centered views."),
            CenteredViewLiveState.Stale => (CameraAgentPresentationSystemState.Standby, "Centered acquisition is idle; showing the last published view."),
            CenteredViewLiveState.Gap => (CameraAgentPresentationSystemState.Unavailable, "Centered admission evidence is unavailable."),
            _ => (CameraAgentPresentationSystemState.Starting, "Loading centered pipeline state.")
        };
        return new CameraAgentPresentationSystemStatus(state, message, observedUtc);
    }
}
