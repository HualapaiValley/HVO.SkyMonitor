using System.Globalization;
using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace HVO.SkyMonitor.LogicHost.Services;

/// <summary>Capture-time interval [FromUtc, UntilUtc); product/state predicates match the same artifact.</summary>
internal sealed record CameraArchiveFilter(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? UntilUtc = null,
    FrameArtifactRole? Product = null,
    CentralArtifactObjectState? ObjectState = null,
    CentralReconstructionState? ReconstructionState = null,
    string? Text = null);

internal sealed record CameraArchiveCapture(
    Guid CaptureId, Guid FrameId, Guid InstallationId, DateTimeOffset CapturedAtUtc,
    DateTimeOffset FirstReceivedAtUtc, long? CaptureSequence);

internal sealed record CameraArchivePage(IReadOnlyList<CameraArchiveCapture> Items, string? NextCursor);

internal sealed record CameraCurrentSelection(
    string Status, CameraArchiveCapture? Capture, Guid? ArtifactId, string? ContentPath,
    string? ChecksumSha256, int ExaminedCaptures, int ExaminedArtifacts, string? NextCursor);

internal interface ILogicalCameraArchiveReadService
{
    Task<CameraArchivePage> ListCapturesAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId,
        CameraArchiveFilter filter, int take, string? cursor, CancellationToken cancellationToken = default);
    Task<CameraCurrentSelection?> SelectCurrentAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId,
        string? cursor, CancellationToken cancellationToken = default);
    Task<CameraCurrentSelection?> RevalidateCurrentAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId,
        CameraCurrentSelection selection, CancellationToken cancellationToken = default);
    Task<OperationsCaptureDetail?> GetCaptureAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId,
        Guid captureId, CancellationToken cancellationToken = default);
}

/// <summary>Bounded, credential-scoped reads over retained installation membership; no durable selection policy.</summary>
internal sealed class LogicalCameraArchiveReadService(
    ApplicationDbContext db,
    IDataProtectionProvider protection,
    INetworkOperationsReadService operations,
    ICentralArtifactRetrievalService retrieval,
    ICentralArtifactObjectReader objects) : ILogicalCameraArchiveReadService
{
    internal const int CurrentCaptureLimit = 5;
    internal const int CurrentArtifactLimit = 5;
    internal const long MaximumPreviewBytes = 32 * 1024 * 1024;
    private readonly IDataProtector _cursorProtector = protection.CreateProtector("logical-camera-archive-v1");

    public async Task<CameraArchivePage> ListCapturesAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, CameraArchiveFilter filter,
        int take, string? cursor, CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(take));
        filter = Normalize(filter);
        var owner = Owner(principal, observatoryId);
        if (owner is null || !await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false))
            return new([], null);
        var position = Decode(cursor, owner, observatoryId, cameraId, "archive", filter);
        var rows = await Project(Window(owner, observatoryId, cameraId, filter, position, take + 1)).Take(take + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        // Recheck authority after materialization, including membership removal between the two reads.
        if (!await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false))
            return new([], null);
        return new(rows, more ? Encode(rows[^1], owner, observatoryId, cameraId, "archive", filter) : null);
    }

    public async Task<CameraCurrentSelection?> SelectCurrentAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, string? cursor,
        CancellationToken cancellationToken = default)
    {
        var owner = Owner(principal, observatoryId);
        if (owner is null || !await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false))
            return null;
        var filter = new CameraArchiveFilter();
        var position = Decode(cursor, owner, observatoryId, cameraId, "current", filter);
        var rows = new List<CameraArchiveCapture>(CurrentCaptureLimit + 1);
        if (position?.Preview is not null)
        {
            var anchor = await Project(Captures(owner, observatoryId, cameraId)
                .Where(frame => frame.Id == position.CaptureId && frame.CapturedAtUtc == position.CapturedAtUtc))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (anchor is not null) rows.Add(anchor);
        }
        var windowSize = CurrentCaptureLimit + 1 - rows.Count;
        rows.AddRange(await Project(Window(owner, observatoryId, cameraId, filter, position, windowSize))
            .Take(windowSize).ToListAsync(cancellationToken).ConfigureAwait(false));
        var more = rows.Count > CurrentCaptureLimit;
        if (more) rows.RemoveAt(rows.Count - 1);
        var examined = 0;
        var examinedArtifacts = 0;
        async Task<CameraCurrentSelection?> FinishAsync(string status, string? continuation)
            => await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false)
                ? new(status, null, null, null, null, examined, examinedArtifacts, continuation) : null;
        foreach (var capture in rows)
        {
            examined++;
            // Keep capture ordering authoritative while trying bounded, deterministic Preview siblings.
            var candidatesQuery = db.CentralArtifacts.AsNoTracking()
                .Where(artifact => artifact.CentralFrameId == capture.CaptureId &&
                    artifact.Role == FrameArtifactRole.Preview &&
                    artifact.ObjectState == CentralArtifactObjectState.Available &&
                    artifact.ReconstructionState == CentralReconstructionState.Complete &&
                    (artifact.MediaType == "image/jpeg" || artifact.MediaType == "image/png") &&
                    artifact.ByteLength > 0 && artifact.ByteLength <= MaximumPreviewBytes);
            if (position?.CaptureId == capture.CaptureId && position.Preview is { } preview)
                candidatesQuery = candidatesQuery.Where(artifact => artifact.ReceivedAtUtc < preview.ReceivedAtUtc ||
                    artifact.ReceivedAtUtc == preview.ReceivedAtUtc && artifact.ArtifactId.CompareTo(preview.ArtifactId) < 0);
            var remaining = CurrentArtifactLimit - examinedArtifacts;
            var candidates = await candidatesQuery.OrderByDescending(artifact => artifact.ReceivedAtUtc)
                .ThenByDescending(artifact => artifact.ArtifactId)
                .Select(artifact => new { artifact.DevicePublicId, artifact.ArtifactId, artifact.ReceivedAtUtc })
                .Take(remaining + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
            var morePreviews = candidates.Count > remaining;
            if (morePreviews) candidates.RemoveAt(candidates.Count - 1);
            foreach (var candidate in candidates)
            {
                examinedArtifacts++;
                var lookup = await retrieval.FindAsync(candidate.DevicePublicId, candidate.ArtifactId, principal, null,
                    cancellationToken).ConfigureAwait(false);
                if (lookup.Status != CentralArtifactLookupStatus.Found || lookup.Artifact is not { } artifact) continue;
                try
                {
                    var snapshot = await objects.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
                    if (!await objects.IsCurrentGenerationAsync(artifact, snapshot.StorageETag, cancellationToken).ConfigureAwait(false))
                        continue;
                }
                catch (Exception exception) when (exception is CentralArtifactMissingException or CentralArtifactIntegrityException)
                {
                    // Read-only selection does not create another repair/reconciliation authority.
                    continue;
                }
                catch (CentralArtifactStorageException)
                {
                    return await FinishAsync("dependency-unavailable", cursor).ConfigureAwait(false);
                }
                if (!await db.CentralArtifacts.AsNoTracking().AnyAsync(item => item.Id == artifact.Id &&
                    item.ObjectState == CentralArtifactObjectState.Available &&
                    item.ReconstructionState == CentralReconstructionState.Complete, cancellationToken).ConfigureAwait(false)) continue;
                if (!await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false)) return null;
                return new("found", capture, artifact.ArtifactId,
                    FormattableString.Invariant($"/api/v1.0/devices/{artifact.DevicePublicId:D}/artifacts/{artifact.ArtifactId:D}/content"),
                    artifact.ChecksumSha256, examined, examinedArtifacts, null);
            }
            if (morePreviews)
            {
                var last = candidates[^1];
                return await FinishAsync("continue", Encode(capture, owner, observatoryId, cameraId, "current", filter,
                    new(last.ReceivedAtUtc, last.ArtifactId))).ConfigureAwait(false);
            }
            if (examinedArtifacts == CurrentArtifactLimit)
            {
                var moreCaptures = more || examined < rows.Count;
                return await FinishAsync(moreCaptures ? "continue" : "no-result", moreCaptures
                    ? Encode(capture, owner, observatoryId, cameraId, "current", filter) : null).ConfigureAwait(false);
            }
        }
        return await FinishAsync(more ? "continue" : "no-result",
            more ? Encode(rows[^1], owner, observatoryId, cameraId, "current", filter) : null).ConfigureAwait(false);
    }

    /// <summary>Rechecks one selected Preview or a continuation's authority without searching another page or capture.</summary>
    public async Task<CameraCurrentSelection?> RevalidateCurrentAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, CameraCurrentSelection selection,
        CancellationToken cancellationToken = default)
    {
        var owner = Owner(principal, observatoryId);
        if (owner is null || !await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false))
            return null;
        _ = Decode(selection.NextCursor, owner, observatoryId, cameraId, "current", new());
        if (selection.Capture is not { } selected)
            return await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false)
                ? selection with { ExaminedCaptures = 0, ExaminedArtifacts = 0 } : null;

        var capture = await Project(Captures(owner, observatoryId, cameraId).Where(frame => frame.Id == selected.CaptureId))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var unavailable = selection with { Status = "unavailable", ContentPath = null, ExaminedCaptures = 1, ExaminedArtifacts = 0 };
        async Task<CameraCurrentSelection?> FinishAsync(CameraCurrentSelection result)
            => await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false) ? result : null;
        if (capture is null || selection.ArtifactId is not { } artifactId)
            return await FinishAsync(unavailable).ConfigureAwait(false);
        var candidate = await db.CentralArtifacts.AsNoTracking().Where(artifact => artifact.CentralFrameId == capture.CaptureId &&
                artifact.ArtifactId == artifactId && artifact.Role == FrameArtifactRole.Preview &&
                artifact.ObjectState == CentralArtifactObjectState.Available && artifact.ReconstructionState == CentralReconstructionState.Complete &&
                (artifact.MediaType == "image/jpeg" || artifact.MediaType == "image/png") &&
                artifact.ByteLength > 0 && artifact.ByteLength <= MaximumPreviewBytes)
            .Select(artifact => new { artifact.DevicePublicId }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (candidate is null) return await FinishAsync(unavailable).ConfigureAwait(false);
        unavailable = unavailable with { ExaminedArtifacts = 1 };
        var lookup = await retrieval.FindAsync(candidate.DevicePublicId, artifactId, principal, null, cancellationToken).ConfigureAwait(false);
        if (lookup.Status != CentralArtifactLookupStatus.Found || lookup.Artifact is not { } artifact ||
            artifact.ChecksumSha256 != selection.ChecksumSha256)
            return await FinishAsync(unavailable).ConfigureAwait(false);
        try
        {
            var snapshot = await objects.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
            if (!await objects.IsCurrentGenerationAsync(artifact, snapshot.StorageETag, cancellationToken).ConfigureAwait(false))
                return await FinishAsync(unavailable).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CentralArtifactMissingException or CentralArtifactIntegrityException)
        {
            return await FinishAsync(unavailable).ConfigureAwait(false);
        }
        catch (CentralArtifactStorageException)
        {
            return await FinishAsync(unavailable with { Status = "dependency-unavailable" }).ConfigureAwait(false);
        }
        if (!await db.CentralArtifacts.AsNoTracking().AnyAsync(item => item.Id == artifact.Id &&
                item.ObjectState == CentralArtifactObjectState.Available && item.ReconstructionState == CentralReconstructionState.Complete,
                cancellationToken).ConfigureAwait(false)) return await FinishAsync(unavailable).ConfigureAwait(false);
        return await FinishAsync(selection with
        {
            Status = "found", Capture = capture, ExaminedCaptures = 1, ExaminedArtifacts = 1,
            ContentPath = FormattableString.Invariant($"/api/v1.0/devices/{artifact.DevicePublicId:D}/artifacts/{artifactId:D}/content")
        }).ConfigureAwait(false);
    }

    public async Task<OperationsCaptureDetail?> GetCaptureAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid captureId,
        CancellationToken cancellationToken = default)
    {
        var owner = Owner(principal, observatoryId);
        if (owner is null || !await Captures(owner, observatoryId, cameraId)
            .AnyAsync(frame => frame.Id == captureId, cancellationToken).ConfigureAwait(false)) return null;
        var detail = await operations.GetCaptureAsync(owner, captureId, cancellationToken).ConfigureAwait(false);
        return await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false) ? detail : null;
    }

    private static string? Owner(ClaimsPrincipal principal, Guid observatoryId)
        => CentralArtifactCredentialAccess.HasOwnerCredential(principal) &&
           CentralArtifactCredentialAccess.IsObservatoryAllowed(principal, observatoryId)
            ? CentralArtifactCredentialAccess.GetOwnerId(principal) : null;

    private Task<bool> CanReadAsync(string owner, Guid observatoryId, Guid cameraId, CancellationToken token)
        => db.LogicalCameras.AsNoTracking().AnyAsync(camera => camera.Id == cameraId &&
            camera.ObservatoryId == observatoryId && ObservatoryMembershipAccess.ForUser(db, owner)
                .Any(membership => membership.ObservatoryId == observatoryId), token);

    private IQueryable<CentralFrame> Captures(string owner, Guid observatoryId, Guid cameraId)
        => db.CentralFrames.AsNoTracking().Where(frame => frame.ObservatoryId == observatoryId &&
            frame.LogicalCameraInstallation != null && frame.LogicalCameraInstallation.LogicalCameraId == cameraId &&
            frame.LogicalCameraInstallation.LogicalCamera!.ObservatoryId == observatoryId &&
            ObservatoryMembershipAccess.ForUser(db, owner).Any(membership => membership.ObservatoryId == observatoryId));

    // SQL APPLY preserves a bounded index seek per retained installation before the global merge.
    // LINQ's per-group Take can become ROW_NUMBER over the complete history instead.
    private IQueryable<CentralFrame> Window(string owner, Guid observatoryId, Guid cameraId,
        CameraArchiveFilter filter, ArchiveCursor? cursor, int limit)
    {
        if (filter.Product is not null || filter.ObjectState is not null || filter.ReconstructionState is not null || filter.Text is not null)
            return FilteredWindow(owner, observatoryId, cameraId, filter, cursor);
        var parameters = new List<object>
        {
            Parameter("@observatory", SqlDbType.UniqueIdentifier, observatoryId),
            Parameter("@camera", SqlDbType.UniqueIdentifier, cameraId),
            Parameter("@limit", SqlDbType.Int, limit)
        };
        // Only fixed SQL fragments are composed. Every caller value stays a typed parameter.
        // Nullable OR predicates turn deep keysets into residual scans on cached plans.
        var dates = "";
        if (filter.FromUtc is { } from)
        {
            dates += " AND frame.[CapturedAtUtc] >= @from";
            parameters.Add(Parameter("@from", SqlDbType.DateTimeOffset, from));
        }
        if (filter.UntilUtc is { } until)
        {
            dates += " AND frame.[CapturedAtUtc] < @until";
            parameters.Add(Parameter("@until", SqlDbType.DateTimeOffset, until));
        }
        string Seek(string boundary) => $"""
            SELECT TOP (@limit) frame.*
            FROM [CentralFrames] AS frame
                WITH (INDEX([IX_CentralFrames_LogicalCameraInstallationId_CapturedAtUtc_Id]))
            WHERE frame.[LogicalCameraInstallationId] = installation.[Id]
                AND frame.[ObservatoryId] = @observatory {dates} {boundary}
            ORDER BY frame.[CapturedAtUtc] DESC, frame.[Id] DESC
            """;
        var window = Seek("");
        if (cursor is not null)
        {
            parameters.Add(Parameter("@boundaryTime", SqlDbType.DateTimeOffset, cursor.CapturedAtUtc));
            parameters.Add(Parameter("@boundaryId", SqlDbType.UniqueIdentifier, cursor.CaptureId));
            // Two disjoint index ranges also bound the equal-timestamp case before merging.
            window = $"""
                SELECT TOP (@limit) bounded.* FROM (
                    SELECT older.* FROM ({Seek("AND frame.[CapturedAtUtc] < @boundaryTime")}) AS older
                    UNION ALL
                    SELECT sameTime.* FROM ({Seek("AND frame.[CapturedAtUtc] = @boundaryTime AND frame.[Id] < @boundaryId")}) AS sameTime
                ) AS bounded
                ORDER BY bounded.[CapturedAtUtc] DESC, bounded.[Id] DESC
                """;
        }
        var sql = $"""
            SELECT candidate.*
            FROM [LogicalCameraInstallations] AS installation
            INNER JOIN [LogicalCameras] AS camera ON camera.[Id] = installation.[LogicalCameraId]
            CROSS APPLY ({window}) AS candidate
            WHERE camera.[Id] = @camera AND camera.[ObservatoryId] = @observatory
            """;
        return db.CentralFrames.FromSqlRaw(sql, parameters.ToArray()).AsNoTracking()
            .Where(frame => ObservatoryMembershipAccess.ForUser(db, owner)
                .Any(membership => membership.ObservatoryId == frame.ObservatoryId));
    }

    // Selective artifact predicates need a global semi-join plan. Applying an installation row goal
    // here forces an artifact probe for every historical capture when few records match.
    private IQueryable<CentralFrame> FilteredWindow(string owner, Guid observatoryId, Guid cameraId,
        CameraArchiveFilter filter, ArchiveCursor? cursor)
    {
        var query = db.CentralFrames.FromSqlInterpolated($"""
            SELECT frame.* FROM [CentralFrames] AS frame
                WITH (INDEX([IX_CentralFrames_LogicalCameraInstallationId_CapturedAtUtc_Id]))
            INNER JOIN [LogicalCameraInstallations] AS installation ON installation.[Id] = frame.[LogicalCameraInstallationId]
            INNER JOIN [LogicalCameras] AS camera ON camera.[Id] = installation.[LogicalCameraId]
            WHERE camera.[Id] = {cameraId} AND camera.[ObservatoryId] = {observatoryId}
                AND frame.[ObservatoryId] = {observatoryId}
            """).AsNoTracking().Where(frame => ObservatoryMembershipAccess.ForUser(db, owner)
                .Any(membership => membership.ObservatoryId == frame.ObservatoryId));
        if (filter.FromUtc is { } from) query = query.Where(frame => frame.CapturedAtUtc >= from);
        if (filter.UntilUtc is { } until) query = query.Where(frame => frame.CapturedAtUtc < until);
        if (cursor is not null) query = query.Where(frame => frame.CapturedAtUtc < cursor.CapturedAtUtc ||
            frame.CapturedAtUtc == cursor.CapturedAtUtc && frame.Id.CompareTo(cursor.CaptureId) < 0);
        if (filter.Product is not null || filter.ObjectState is not null || filter.ReconstructionState is not null)
            query = query.Where(frame => frame.Artifacts.Any(artifact =>
                (filter.Product == null || artifact.Role == filter.Product) &&
                (filter.ObjectState == null || artifact.ObjectState == filter.ObjectState) &&
                (filter.ReconstructionState == null || artifact.ReconstructionState == filter.ReconstructionState)));
        if (filter.Text is { } text)
        {
            if (Guid.TryParse(text, out var identity)) query = query.Where(frame => frame.Id == identity || frame.FrameId == identity);
            else
            {
                var sequence = long.Parse(text, CultureInfo.InvariantCulture);
                query = query.Where(frame => frame.CaptureSequence == sequence);
            }
        }
        return query;
    }

    private static SqlParameter Parameter(string name, SqlDbType type, object? value)
        => new(name, type) { Value = value ?? DBNull.Value };

    private static IQueryable<CameraArchiveCapture> Project(IQueryable<CentralFrame> query)
        => query.OrderByDescending(frame => frame.CapturedAtUtc).ThenByDescending(frame => frame.Id)
            .Select(frame => new CameraArchiveCapture(frame.Id, frame.FrameId, frame.LogicalCameraInstallationId!.Value,
                frame.CapturedAtUtc, frame.FirstReceivedAtUtc, frame.CaptureSequence));

    internal static CameraArchiveFilter Normalize(CameraArchiveFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.FromUtc >= filter.UntilUtc ||
            filter.Product is { } product && !Enum.IsDefined(product) ||
            filter.ObjectState is { } state && !Enum.IsDefined(state) ||
            filter.ReconstructionState is { } reconstruction && !Enum.IsDefined(reconstruction))
            throw new ArgumentException("The archive filter is invalid.", nameof(filter));
        var text = string.IsNullOrWhiteSpace(filter.Text) ? null : filter.Text.Trim();
        if (text is not null)
        {
            if (text.Length <= 64 && Guid.TryParse(text, out var identity)) text = identity.ToString("D");
            else if (text.Length <= 20 && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
                text = sequence.ToString(CultureInfo.InvariantCulture);
            else throw new ArgumentException("Text must be an exact capture/frame UUID or non-negative capture sequence.", nameof(filter));
        }
        return filter with { FromUtc = filter.FromUtc?.ToUniversalTime(), UntilUtc = filter.UntilUtc?.ToUniversalTime(), Text = text };
    }

    private string Encode(CameraArchiveCapture last, string owner, Guid observatory, Guid camera, string purpose, CameraArchiveFilter filter, PreviewPosition? preview = null)
        => _cursorProtector.Protect(JsonSerializer.Serialize(new ArchiveCursor(owner, observatory, camera, purpose,
            filter, last.CapturedAtUtc, last.CaptureId, preview)));

    private ArchiveCursor? Decode(string? cursor, string owner, Guid observatory, Guid camera, string purpose, CameraArchiveFilter filter)
    {
        if (cursor is null) return null;
        try
        {
            if (cursor.Length > 4096) throw new FormatException();
            var value = JsonSerializer.Deserialize<ArchiveCursor>(_cursorProtector.Unprotect(cursor));
            if (value is null || value.Owner != owner || value.Observatory != observatory || value.Camera != camera ||
                value.Purpose != purpose || value.Filter != filter || value.CaptureId == Guid.Empty ||
                value.Preview is { } preview && (purpose != "current" || preview.ArtifactId == Guid.Empty)) throw new FormatException();
            return value;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            throw new ArgumentException("The continuation is invalid for this request.", nameof(cursor));
        }
    }

    private sealed record ArchiveCursor(string Owner, Guid Observatory, Guid Camera, string Purpose,
        CameraArchiveFilter Filter, DateTimeOffset CapturedAtUtc, Guid CaptureId, PreviewPosition? Preview = null);

    private sealed record PreviewPosition(DateTimeOffset ReceivedAtUtc, Guid ArtifactId);
}
