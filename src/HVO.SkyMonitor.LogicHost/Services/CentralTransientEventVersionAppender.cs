using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralTransientEventVersionAppender
{
    Task<CentralTransientEventAppendResult> AppendCanonicalAsync(
        CentralTransientEventAppendRequest request,
        CancellationToken cancellationToken);

    Task<CentralTransientEventAppendResult> AppendGeneratedAsync(
        Guid centralTransientEventId,
        Func<TransientEventV1, TransientEventV1> createNext,
        bool resetReviewState,
        CancellationToken cancellationToken);
}

internal sealed record CentralTransientEventAppendRequest(
    Guid CentralTransientEventId,
    TransientEventV1 Event,
    string CanonicalJson,
    string CanonicalSha256,
    int CanonicalByteLength,
    bool IsNewEvent = false);

internal sealed record CentralTransientEventAppendResult(
    TransientEventV1? Previous,
    TransientEventV1 Event,
    CentralTransientEventVersionRecord Version,
    CentralTransientEventCurrent Current);

internal sealed class CentralTransientEventVersionAppender(ApplicationDbContext dbContext)
    : ICentralTransientEventVersionAppender
{
    public async Task<CentralTransientEventAppendResult> AppendCanonicalAsync(
        CentralTransientEventAppendRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Transient event versions must be appended inside a transaction.");
        }

        TransientEventV1? previous = null;
        if (request.IsNewEvent)
        {
            if (request.Event.Version != 1 || request.Event.PreviousEventVersionId is not null ||
                request.Event.PreviousVersionCreatedUtc is not null)
            {
                throw InvalidHistory("A new event must begin with version one and no predecessor.");
            }
        }
        else
        {
            await AcquireEventLockAsync(request.CentralTransientEventId, cancellationToken).ConfigureAwait(false);
            previous = await LoadLatestAsync(request.CentralTransientEventId, cancellationToken).ConfigureAwait(false);
            ValidateAppend(previous, request.Event);
        }

        ValidateCanonical(request);
        return await PersistAsync(request, previous, resetReviewState: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CentralTransientEventAppendResult> AppendGeneratedAsync(
        Guid centralTransientEventId,
        Func<TransientEventV1, TransientEventV1> createNext,
        bool resetReviewState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createNext);
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Transient event versions must be appended inside a transaction.");
        }

        await AcquireEventLockAsync(centralTransientEventId, cancellationToken).ConfigureAwait(false);
        var previous = await LoadLatestAsync(centralTransientEventId, cancellationToken).ConfigureAwait(false);
        var next = createNext(previous);
        ValidateAppend(previous, next);
        var canonical = TransientContractJson.Serialize(next);
        var request = new CentralTransientEventAppendRequest(
            centralTransientEventId,
            next,
            Encoding.UTF8.GetString(canonical),
            Convert.ToHexString(SHA256.HashData(canonical)),
            canonical.Length);
        return await PersistAsync(request, previous, resetReviewState, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CentralTransientEventAppendResult> PersistAsync(
        CentralTransientEventAppendRequest request,
        TransientEventV1? previous,
        bool resetReviewState,
        CancellationToken cancellationToken)
    {
        var transientEvent = request.Event;
        var eventRecord = request.IsNewEvent
            ? dbContext.ChangeTracker.Entries<CentralTransientEventRecord>()
                .Select(entry => entry.Entity)
                .SingleOrDefault(item => item.Id == request.CentralTransientEventId)
            : await dbContext.CentralTransientEvents.SingleOrDefaultAsync(
                item => item.Id == request.CentralTransientEventId, cancellationToken).ConfigureAwait(false);
        if (eventRecord is null || eventRecord.EventId != transientEvent.EventId ||
            !string.Equals(eventRecord.AgentId, transientEvent.AgentId, StringComparison.Ordinal) ||
            eventRecord.EventCreatedUtc != transientEvent.EventCreatedUtc)
        {
            throw InvalidHistory("Event identity and creation time are immutable across versions.");
        }

        var reviewCreatedUtc = await dbContext.CentralTransientReviews.AsNoTracking()
            .Where(item => item.CentralTransientEventId == eventRecord.Id)
            .ToDictionaryAsync(item => item.ReviewId, item => item.CreatedUtc, cancellationToken)
            .ConfigureAwait(false);
        foreach (var review in transientEvent.Reviews.Where(item => !reviewCreatedUtc.ContainsKey(item.ReviewId)))
        {
            DateTimeOffset? supersededCreatedUtc = null;
            if (review.SupersedesReviewId is { } supersedesReviewId)
            {
                if (!reviewCreatedUtc.TryGetValue(supersedesReviewId, out var predecessorCreatedUtc))
                {
                    throw InvalidHistory("A review predecessor must be committed earlier in the same event.");
                }
                supersededCreatedUtc = predecessorCreatedUtc;
            }

            dbContext.CentralTransientReviews.Add(new CentralTransientReviewRecord
            {
                ReviewId = review.ReviewId,
                CentralTransientEventId = eventRecord.Id,
                CreatedUtc = review.CreatedUtc,
                ReviewerIdentity = review.ReviewerIdentity,
                Disposition = review.Disposition,
                AssessmentId = review.AssessmentId,
                OverrideClassification = review.Override?.Classification,
                OverrideMeteorSeverity = review.Override?.MeteorSeverity,
                OverrideConfidenceMillionths = review.Override?.ConfidenceMillionths,
                ReasonCodesJson = CanonicalJson(review.ReasonCodes),
                SupersedesReviewId = review.SupersedesReviewId,
                SupersedesReviewCreatedUtc = supersededCreatedUtc
            });
            reviewCreatedUtc.Add(review.ReviewId, review.CreatedUtc);
        }

        var notificationCreatedUtc = await dbContext.CentralTransientNotifications.AsNoTracking()
            .Where(item => item.CentralTransientEventId == eventRecord.Id)
            .ToDictionaryAsync(item => item.NotificationId, item => item.CreatedUtc, cancellationToken)
            .ConfigureAwait(false);
        foreach (var notification in transientEvent.Notifications.Where(item =>
                     !notificationCreatedUtc.ContainsKey(item.NotificationId)))
        {
            DateTimeOffset? supersededCreatedUtc = null;
            if (notification.SupersedesNotificationId is { } supersedesNotificationId)
            {
                if (!notificationCreatedUtc.TryGetValue(supersedesNotificationId, out var predecessorCreatedUtc))
                {
                    throw InvalidHistory("A notification predecessor must be committed earlier in the same event.");
                }
                supersededCreatedUtc = predecessorCreatedUtc;
            }
            dbContext.CentralTransientNotifications.Add(new CentralTransientNotificationRecord
            {
                NotificationId = notification.NotificationId,
                CentralTransientEventId = eventRecord.Id,
                CreatedUtc = notification.CreatedUtc,
                Channel = notification.Channel,
                State = notification.State,
                AssessmentId = notification.AssessmentId,
                ReasonCode = notification.ReasonCode,
                SupersedesNotificationId = notification.SupersedesNotificationId,
                SupersedesNotificationCreatedUtc = supersededCreatedUtc
            });
            notificationCreatedUtc.Add(notification.NotificationId, notification.CreatedUtc);
        }

        await ValidateDerivativesAsync(eventRecord.Id, transientEvent.Derivatives, cancellationToken)
            .ConfigureAwait(false);

        var version = new CentralTransientEventVersionRecord
        {
            EventVersionId = transientEvent.EventVersionId,
            CentralTransientEventId = eventRecord.Id,
            Version = transientEvent.Version,
            PreviousVersionNumber = transientEvent.Version == 1 ? null : transientEvent.Version - 1,
            PreviousEventVersionId = transientEvent.PreviousEventVersionId,
            PreviousVersionCreatedUtc = transientEvent.PreviousVersionCreatedUtc,
            State = transientEvent.State,
            VersionCreatedUtc = transientEvent.VersionCreatedUtc,
            FirstObservedUtc = transientEvent.FirstObservedUtc,
            LastObservedUtc = transientEvent.LastObservedUtc,
            SchemaVersion = transientEvent.SchemaVersion,
            CanonicalEventJson = request.CanonicalJson,
            CanonicalEventSha256 = request.CanonicalSha256,
            CanonicalEventByteLength = request.CanonicalByteLength
        };
        dbContext.CentralTransientEventVersions.Add(version);
        dbContext.AddRange(transientEvent.Observations.Select(observation =>
            new CentralTransientEventVersionObservation
            {
                CentralTransientEventId = eventRecord.Id,
                EventVersionId = transientEvent.EventVersionId,
                Ordinal = observation.Ordinal,
                ObservationId = observation.ObservationId
            }));
        dbContext.AddRange(transientEvent.Assessments.Select((assessment, ordinal) =>
            new CentralTransientEventVersionAssessment
            {
                CentralTransientEventId = eventRecord.Id,
                EventVersionId = transientEvent.EventVersionId,
                Ordinal = ordinal,
                AssessmentId = assessment.AssessmentId
            }));
        dbContext.AddRange(transientEvent.Reviews.Select((review, ordinal) =>
            new CentralTransientEventVersionReview
            {
                CentralTransientEventId = eventRecord.Id,
                EventVersionId = transientEvent.EventVersionId,
                Ordinal = ordinal,
                ReviewId = review.ReviewId
            }));
        dbContext.AddRange(transientEvent.Notifications.Select((notification, ordinal) =>
            new CentralTransientEventVersionNotification
            {
                CentralTransientEventId = eventRecord.Id,
                EventVersionId = transientEvent.EventVersionId,
                Ordinal = ordinal,
                NotificationId = notification.NotificationId
            }));
        dbContext.AddRange(transientEvent.Derivatives.Select((derivative, ordinal) =>
            new CentralTransientEventVersionDerivative
            {
                CentralTransientEventId = eventRecord.Id,
                EventVersionId = transientEvent.EventVersionId,
                Ordinal = ordinal,
                DerivativeId = derivative.DerivativeId
            }));

        var current = await dbContext.CentralTransientEventCurrent.SingleOrDefaultAsync(
                item => item.CentralTransientEventId == eventRecord.Id, cancellationToken)
            .ConfigureAwait(false) ?? new CentralTransientEventCurrent
            {
                CentralTransientEventId = eventRecord.Id
            };
        if (dbContext.Entry(current).State == EntityState.Detached)
        {
            dbContext.CentralTransientEventCurrent.Add(current);
        }
        ApplyCurrent(current, transientEvent, resetReviewState);
        return new(previous, transientEvent, version, current);
    }

    private async Task AcquireEventLockAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var acquired = await dbContext.Database.SqlQuery<int>(
                $"SELECT CAST(1 AS int) AS [Value] FROM [CentralTransientEvents] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {eventId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (acquired != 1)
        {
            throw InvalidHistory("Transient event was not found.");
        }
    }

    private async Task ValidateDerivativesAsync(
        Guid eventId,
        IReadOnlyList<TransientDerivativeV1> derivatives,
        CancellationToken cancellationToken)
    {
        if (derivatives.Count == 0)
        {
            return;
        }
        var ids = derivatives.Select(item => item.DerivativeId).ToArray();
        var persisted = await dbContext.CentralTransientDerivatives.AsNoTracking()
            .Include(item => item.Sources)
            .Include(item => item.DerivativeJob)
            .Include(item => item.OutputIntent)
            .Where(item => item.CentralTransientEventId == eventId && ids.Contains(item.DerivativeId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var tracked = dbContext.CentralTransientDerivatives.Local
            .Where(item => item.CentralTransientEventId == eventId && ids.Contains(item.DerivativeId))
            .ToArray();
        var records = persisted.Concat(tracked)
            .GroupBy(item => item.DerivativeId)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var derivative in derivatives)
        {
            if (!records.TryGetValue(derivative.DerivativeId, out var record) ||
                record.DerivativeJob?.CommittedAtUtc is null ||
                record.OutputIntent?.CommittedAtUtc is null ||
                record.CreatedUtc != derivative.CreatedUtc || record.Kind != derivative.Kind ||
                record.ArtifactId != derivative.Artifact.ArtifactId ||
                record.ArtifactRole != derivative.Artifact.Role ||
                !string.Equals(record.ArtifactVariant, derivative.Artifact.Variant, StringComparison.Ordinal) ||
                !string.Equals(record.ArtifactChecksumSha256, derivative.Artifact.ChecksumSha256, StringComparison.Ordinal) ||
                !string.Equals(record.RecipeIdentitySha256, derivative.RecipeIdentitySha256, StringComparison.Ordinal) ||
                !record.Sources.OrderBy(item => item.Ordinal).Select(item => item.EvidenceId)
                    .SequenceEqual(derivative.OrderedSourceEvidenceIds) ||
                !string.Equals(record.LimitationsJson, CanonicalJson(derivative.Limitations), StringComparison.Ordinal))
            {
                throw InvalidHistory("Event derivative history does not match committed event-owned evidence.");
            }
        }
    }

    private async Task<TransientEventV1> LoadLatestAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var latest = await dbContext.CentralTransientEventVersions.AsNoTracking()
            .Where(item => item.CentralTransientEventId == eventId)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw InvalidHistory("Transient event has no committed version.");
        var parsed = TransientContractJson.ParseEvent(Encoding.UTF8.GetBytes(latest.CanonicalEventJson));
        return parsed.Value is not null && parsed.Validation.IsValid
            ? parsed.Value
            : throw InvalidHistory("Previously committed canonical event history is invalid.");
    }

    private static void ValidateAppend(TransientEventV1 previous, TransientEventV1 incoming)
    {
        if (incoming.Version != previous.Version + 1 ||
            incoming.PreviousEventVersionId != previous.EventVersionId ||
            incoming.PreviousVersionCreatedUtc != previous.VersionCreatedUtc ||
            incoming.EventId != previous.EventId ||
            !string.Equals(incoming.AgentId, previous.AgentId, StringComparison.Ordinal) ||
            incoming.EventCreatedUtc != previous.EventCreatedUtc ||
            !IsPrefix(previous.Observations, incoming.Observations) ||
            !IsPrefix(previous.Assessments, incoming.Assessments) ||
            !IsPrefix(previous.Reviews, incoming.Reviews) ||
            !IsPrefix(previous.Notifications, incoming.Notifications) ||
            !IsPrefix(previous.Derivatives, incoming.Derivatives))
        {
            throw InvalidHistory("Event versions must append without altering or removing prior history.");
        }
    }

    private static void ValidateCanonical(CentralTransientEventAppendRequest request)
    {
        var canonical = TransientContractJson.Serialize(request.Event);
        var sha256 = Convert.ToHexString(SHA256.HashData(canonical));
        if (request.CanonicalByteLength != canonical.Length ||
            !string.Equals(request.CanonicalJson, Encoding.UTF8.GetString(canonical), StringComparison.Ordinal) ||
            !string.Equals(request.CanonicalSha256, sha256, StringComparison.Ordinal))
        {
            throw new CentralTransientPersistenceException(
                CentralTransientPersistenceReasonCodes.InvalidCanonicalPayload,
                "Event payload is not the exact canonical Processing representation.");
        }
    }

    private static void ApplyCurrent(
        CentralTransientEventCurrent current,
        TransientEventV1 transientEvent,
        bool resetReviewState)
    {
        if (transientEvent.Assessments.Count == 0)
        {
            throw InvalidHistory("A persisted transient event must retain an active assessment.");
        }
        var assessment = transientEvent.Assessments[^1];
        var review = resetReviewState
            ? null
            : transientEvent.Reviews.LastOrDefault(item => item.AssessmentId == assessment.AssessmentId);
        current.LatestEventVersionId = transientEvent.EventVersionId;
        current.LatestVersion = transientEvent.Version;
        current.ActiveAssessmentId = assessment.AssessmentId;
        current.LatestReviewId = review?.ReviewId;
        current.ReviewState = review?.Disposition switch
        {
            TransientReviewDisposition.Confirmed => CentralTransientReviewState.Reviewed,
            TransientReviewDisposition.Overridden => CentralTransientReviewState.Overridden,
            TransientReviewDisposition.Rejected => CentralTransientReviewState.Rejected,
            _ => CentralTransientReviewState.NeedsReview
        };
        var effective = CentralTransientReviewProjection.Resolve(
            assessment.Classification,
            assessment.MeteorSeverity,
            assessment.ConfidenceMillionths,
            review?.Override);
        current.EffectiveClassification = effective.Classification;
        current.EffectiveMeteorSeverity = effective.MeteorSeverity;
        current.EffectiveConfidenceMillionths = effective.ConfidenceMillionths;
        current.UpdatedUtc = transientEvent.VersionCreatedUtc;
    }

    private static bool IsPrefix<T>(IReadOnlyList<T> prefix, IReadOnlyList<T> values)
        => prefix.Count <= values.Count && prefix.Select((item, index) =>
            string.Equals(CanonicalJson(item), CanonicalJson(values[index]), StringComparison.Ordinal)).All(equal => equal);

    private static string CanonicalJson<T>(T value)
        => CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(value)).GetRawText();

    private static CentralTransientPersistenceException InvalidHistory(string message)
        => new(CentralTransientPersistenceReasonCodes.InvalidHistory, message);
}
