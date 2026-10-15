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
    string? ChecksumSha256, int ExaminedCaptures, string? NextCursor);

internal interface ILogicalCameraArchiveReadService
{
    Task<CameraArchivePage> ListCapturesAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId,
        CameraArchiveFilter filter, int take, string? cursor, CancellationToken cancellationToken = default);
    Task<CameraCurrentSelection?> SelectCurrentAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId,
        string? cursor, CancellationToken cancellationToken = default);
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
        var rows = await Project(Window(owner, observatoryId, cameraId, filter, position, CurrentCaptureLimit + 1))
            .Take(CurrentCaptureLimit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var more = rows.Count > CurrentCaptureLimit;
        if (more) rows.RemoveAt(rows.Count - 1);
        var examined = 0;
        foreach (var capture in rows)
        {
            examined++;
            // One deterministic retained Preview per capture. Annotation and publication are optional.
            // SQL eligibility is not a content lease; verify through the existing object reader below.
            var candidate = await db.CentralArtifacts.AsNoTracking()
                .Where(artifact => artifact.CentralFrameId == capture.CaptureId &&
                    artifact.Role == FrameArtifactRole.Preview &&
                    artifact.ObjectState == CentralArtifactObjectState.Available &&
                    artifact.ReconstructionState == CentralReconstructionState.Complete &&
                    (artifact.MediaType == "image/jpeg" || artifact.MediaType == "image/png") &&
                    artifact.ByteLength > 0 && artifact.ByteLength <= MaximumPreviewBytes)
                .OrderByDescending(artifact => artifact.ReceivedAtUtc).ThenByDescending(artifact => artifact.ArtifactId)
                .Select(artifact => new { artifact.DevicePublicId, artifact.ArtifactId })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (candidate is null) continue;
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
                if (!await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false)) return null;
                return new("dependency-unavailable", null, null, null, null, examined, cursor);
            }
            if (!await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false)) return null;
            if (!await db.CentralArtifacts.AsNoTracking().AnyAsync(item => item.Id == artifact.Id &&
                item.ObjectState == CentralArtifactObjectState.Available &&
                item.ReconstructionState == CentralReconstructionState.Complete, cancellationToken).ConfigureAwait(false)) continue;
            return new("found", capture, artifact.ArtifactId,
                FormattableString.Invariant($"/api/v1.0/devices/{artifact.DevicePublicId:D}/artifacts/{artifact.ArtifactId:D}/content"),
                artifact.ChecksumSha256, examined, null);
        }
        if (!await CanReadAsync(owner, observatoryId, cameraId, cancellationToken).ConfigureAwait(false)) return null;
        return new(more ? "continue" : "no-result", null, null, null, null, examined,
            more ? Encode(rows[^1], owner, observatoryId, cameraId, "current", filter) : null);
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
        var from = Parameter("@from", SqlDbType.DateTimeOffset, filter.FromUtc);
        var until = Parameter("@until", SqlDbType.DateTimeOffset, filter.UntilUtc);
        var boundaryTime = Parameter("@boundaryTime", SqlDbType.DateTimeOffset, cursor?.CapturedAtUtc);
        var boundaryId = Parameter("@boundaryId", SqlDbType.UniqueIdentifier, cursor?.CaptureId);
        if (filter.Product is not null || filter.ObjectState is not null || filter.ReconstructionState is not null || filter.Text is not null)
            return FilteredWindow(owner, observatoryId, cameraId, filter, cursor);
        return db.CentralFrames.FromSqlInterpolated($"""
            SELECT candidate.*
            FROM [LogicalCameraInstallations] AS installation
            INNER JOIN [LogicalCameras] AS camera ON camera.[Id] = installation.[LogicalCameraId]
            CROSS APPLY (
                SELECT TOP ({limit}) frame.*
                FROM [CentralFrames] AS frame
                    WITH (INDEX([IX_CentralFrames_LogicalCameraInstallationId_CapturedAtUtc_Id]))
                WHERE frame.[LogicalCameraInstallationId] = installation.[Id]
                    AND frame.[ObservatoryId] = {observatoryId}
                    AND ({from} IS NULL OR frame.[CapturedAtUtc] >= {from})
                    AND ({until} IS NULL OR frame.[CapturedAtUtc] < {until})
                    AND ({boundaryTime} IS NULL OR frame.[CapturedAtUtc] < {boundaryTime}
                        OR frame.[CapturedAtUtc] = {boundaryTime} AND frame.[Id] < {boundaryId})
                ORDER BY frame.[CapturedAtUtc] DESC, frame.[Id] DESC
            ) AS candidate
            WHERE camera.[Id] = {cameraId} AND camera.[ObservatoryId] = {observatoryId}
            """).AsNoTracking().Where(frame => ObservatoryMembershipAccess.ForUser(db, owner)
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

    private string Encode(CameraArchiveCapture last, string owner, Guid observatory, Guid camera, string purpose, CameraArchiveFilter filter)
        => _cursorProtector.Protect(JsonSerializer.Serialize(new ArchiveCursor(owner, observatory, camera, purpose,
            filter, last.CapturedAtUtc, last.CaptureId)));

    private ArchiveCursor? Decode(string? cursor, string owner, Guid observatory, Guid camera, string purpose, CameraArchiveFilter filter)
    {
        if (cursor is null) return null;
        try
        {
            if (cursor.Length > 4096) throw new FormatException();
            var value = JsonSerializer.Deserialize<ArchiveCursor>(_cursorProtector.Unprotect(cursor));
            if (value is null || value.Owner != owner || value.Observatory != observatory || value.Camera != camera ||
                value.Purpose != purpose || value.Filter != filter || value.CaptureId == Guid.Empty) throw new FormatException();
            return value;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            throw new ArgumentException("The continuation is invalid for this request.", nameof(cursor));
        }
    }

    private sealed record ArchiveCursor(string Owner, Guid Observatory, Guid Camera, string Purpose,
        CameraArchiveFilter Filter, DateTimeOffset CapturedAtUtc, Guid CaptureId);
}
