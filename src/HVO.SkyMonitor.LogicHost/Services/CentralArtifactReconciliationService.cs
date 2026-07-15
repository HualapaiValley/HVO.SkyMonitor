using System.Security.Cryptography;
using System.Data.Common;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using System.Net;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralArtifactReconciliationService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    CentralIngestTelemetry telemetry,
    ILogger<CentralArtifactReconciliationService> logger) : BackgroundService
{
    internal static readonly TimeSpan StagingObjectGracePeriod = TimeSpan.FromMinutes(15);
    internal const int MaximumStagingObjectsPerCycle = 1000;
    private const string Bucket = "skymonitor-artifacts";
    private const string BucketPrefix = "minio://skymonitor-artifacts/";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is DbException or DbUpdateConcurrencyException or MinioException
                or HttpRequestException or IOException or InvalidOperationException)
            {
                logger.LogError(exception, "Central artifact reconciliation cycle failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = CentralIngestTelemetry.StartActivity("central-artifact.reconcile");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
        await CleanupStagingObjectsAsync(minio, cancellationToken).ConfigureAwait(false);
        var artifactIds = await db.CentralArtifacts.AsNoTracking()
            .Where(artifact => artifact.ObjectState == CentralArtifactObjectState.Pending
                || artifact.ReconstructionState == CentralReconstructionState.PendingReference)
            .OrderBy(artifact => artifact.ReconciledAtUtc ?? artifact.ReceivedAtUtc)
            .Take(100)
            .Select(artifact => artifact.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var artifactId in artifactIds)
        {
            try
            {
                await ReconcileOneAsync(artifactId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is DbException or DbUpdateConcurrencyException or MinioException
                or HttpRequestException or IOException or InvalidOperationException)
            {
                logger.LogError(exception, "Central artifact reconciliation failed for one durable record");
            }
        }
        var pendingObjects = await db.CentralArtifacts
            .Where(artifact => artifact.ObjectState == CentralArtifactObjectState.Pending)
            .GroupBy(static _ => 1)
            .Select(group => new BacklogSnapshot(
                group.LongCount(), group.Sum(artifact => artifact.ByteLength), group.Min(artifact => artifact.ReceivedAtUtc)))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var pendingReferences = await db.CentralArtifacts
            .Where(artifact => artifact.ReconstructionState == CentralReconstructionState.PendingReference)
            .GroupBy(static _ => 1)
            .Select(group => new BacklogSnapshot(
                group.LongCount(), group.Sum(artifact => artifact.ByteLength), group.Min(artifact => artifact.ReceivedAtUtc)))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var quarantined = await db.CentralArtifacts
            .Where(artifact => artifact.ObjectState == CentralArtifactObjectState.Quarantined
                || artifact.ReconstructionState == CentralReconstructionState.Quarantined)
            .GroupBy(static _ => 1)
            .Select(group => new BacklogSnapshot(
                group.LongCount(), group.Sum(artifact => artifact.ByteLength), group.Min(artifact => artifact.ReceivedAtUtc)))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordBacklog(
            pendingObjects?.Count ?? 0,
            pendingObjects?.Bytes ?? 0,
            GetAgeSeconds(pendingObjects?.OldestAtUtc),
            pendingReferences?.Count ?? 0,
            pendingReferences?.Bytes ?? 0,
            GetAgeSeconds(pendingReferences?.OldestAtUtc),
            quarantined?.Count ?? 0,
            quarantined?.Bytes ?? 0,
            GetAgeSeconds(quarantined?.OldestAtUtc));
        telemetry.RecordReconciliationDuration("completed", timeProvider.GetElapsedTime(started));
    }

    private async Task CleanupStagingObjectsAsync(IMinioClient minio, CancellationToken cancellationToken)
    {
        if (!await minio.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(Bucket), cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var cutoffUtc = timeProvider.GetUtcNow() - StagingObjectGracePeriod;
        var scanned = 0L;
        var retained = 0L;
        var deleted = 0L;
        var failed = 0L;
        var deletedBytes = 0L;
        var args = new ListObjectsArgs().WithBucket(Bucket).WithPrefix("staging/").WithRecursive(true);
        await foreach (var item in minio.ListObjectsEnumAsync(args, cancellationToken).ConfigureAwait(false))
        {
            if (scanned >= MaximumStagingObjectsPerCycle)
            {
                break;
            }
            scanned++;
            var lastModifiedUtc = item.LastModifiedDateTime;
            // In-progress PUTs are not listable. The grace period additionally protects a completed
            // staging PUT while its request is copying the object to the final key.
            if (!lastModifiedUtc.HasValue || new DateTimeOffset(lastModifiedUtc.Value.ToUniversalTime()) > cutoffUtc)
            {
                retained++;
                continue;
            }
            try
            {
                await minio.RemoveObjectAsync(new RemoveObjectArgs()
                    .WithBucket(Bucket)
                    .WithObject(item.Key), cancellationToken).ConfigureAwait(false);
                deleted++;
                deletedBytes += checked((long)item.Size);
            }
            catch (Exception exception) when (exception is MinioException or HttpRequestException or IOException)
            {
                failed++;
                logger.LogWarning("Failed to remove a stale MinIO staging object");
            }
        }
        telemetry.RecordStagingCleanup("scanned", scanned);
        telemetry.RecordStagingCleanup("retained", retained);
        telemetry.RecordStagingCleanup("deleted", deleted, deletedBytes);
        telemetry.RecordStagingCleanup("failed", failed);
    }

    private async Task ReconcileOneAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
        var scheduler = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>();
        var artifact = await db.CentralArtifacts
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Timing)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Profiles)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .Include(item => item.Sources)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.Id == artifactId, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            return;
        }
        var reconciledAtUtc = timeProvider.GetUtcNow();
        if (artifact.ObjectState == CentralArtifactObjectState.Pending)
        {
            await ArtifactIngestService.InvalidateDependentsAsync(db, artifact, cancellationToken).ConfigureAwait(false);
            var verification = await VerifyAsync(minio, artifact, cancellationToken).ConfigureAwait(false);
            telemetry.RecordChecksum("reconciliation", verification switch
            {
                null => "matched",
                "missing" => "missing",
                "object.length-mismatch" => "length-mismatch",
                _ => "checksum-mismatch"
            });
            if (verification == "missing")
            {
                artifact.ReconciledAtUtc = reconciledAtUtc;
                artifact.StateReasonCode = "object.missing";
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (verification is not null)
            {
                artifact.ObjectState = CentralArtifactObjectState.Quarantined;
                artifact.ReconstructionState = CentralReconstructionState.Quarantined;
                artifact.StateReasonCode = verification;
                artifact.ReconciledAtUtc = reconciledAtUtc;
                await ArtifactIngestService.InvalidateDependentsAsync(db, artifact, cancellationToken).ConfigureAwait(false);
                telemetry.RecordQuarantine(verification);
                telemetry.RecordReconciled("quarantined");
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            artifact.ObjectState = CentralArtifactObjectState.Available;
        }
        if (artifact.ReconstructionState == CentralReconstructionState.PendingReference)
        {
            await ResolveReferencesAsync(db, artifact, cancellationToken).ConfigureAwait(false);
            artifact.ReconciledAtUtc = reconciledAtUtc;
        }
        else if (artifact.ObjectState == CentralArtifactObjectState.Available
            && artifact.ReconstructionState == CentralReconstructionState.Quarantined)
        {
            await ResolveReferencesAsync(db, artifact, cancellationToken).ConfigureAwait(false);
        }
        if (artifact.ObjectState != CentralArtifactObjectState.Available
            || artifact.ReconstructionState != CentralReconstructionState.Complete)
        {
            await ArtifactIngestService.InvalidateDependentsAsync(db, artifact, cancellationToken).ConfigureAwait(false);
        }
        if (artifact.ObjectState == CentralArtifactObjectState.Available
            && artifact.ReconstructionState == CentralReconstructionState.Complete)
        {
            artifact.ReconciledAtUtc = reconciledAtUtc;
            artifact.StateReasonCode = null;
            if (artifact.ManifestSchemaVersion == HVO.SkyMonitor.AgentCore.ArtifactUploadManifest.CurrentSchemaVersion
                || artifact.Role == HVO.SkyMonitor.AgentCore.FrameArtifactRole.Raw)
            {
                await scheduler.EnsureRequiredJobsAsync(artifact, reconciledAtUtc, cancellationToken).ConfigureAwait(false);
            }
            telemetry.RecordReconciled("completed");
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private long GetAgeSeconds(DateTimeOffset? oldestAtUtc)
        => oldestAtUtc.HasValue
            ? Math.Max(0, (long)(timeProvider.GetUtcNow() - oldestAtUtc.Value).TotalSeconds)
            : 0;

    private sealed record BacklogSnapshot(long Count, long Bytes, DateTimeOffset OldestAtUtc);

    private static async Task ResolveReferencesAsync(
        ApplicationDbContext db,
        CentralArtifact artifact,
        CancellationToken cancellationToken)
    {
        var frame = artifact.Frame!;
        var unavailableSource = false;
        if (!frame.DeviceRigProfileId.HasValue)
        {
            var rig = frame.Profiles.Single(profile => profile.Kind == CentralProfileKind.Rig);
            var profile = await HistoricalRigProfileResolver.ResolveAsync(
                db,
                frame.DevicePublicId,
                new(rig.Name, rig.Version, rig.Sha256),
                frame.Timing!.ExposureStartedUtc,
                cancellationToken).ConfigureAwait(false);
            if (profile is not null)
            {
                rig.DeviceRigProfileId = profile.Id;
                rig.DeviceRigProfile = profile;
                frame.DeviceRigProfileId = profile.Id;
                frame.DeviceRigProfile = profile;
                frame.RigProfileVersion = profile.Version;
            }
        }

        foreach (var source in artifact.Sources.Where(source => source.ResolvedCentralArtifactId != null))
        {
            var usable = await db.CentralArtifacts.AnyAsync(candidate =>
                candidate.Id == source.ResolvedCentralArtifactId
                && candidate.ObjectState == CentralArtifactObjectState.Available
                && candidate.ReconstructionState == CentralReconstructionState.Complete,
                cancellationToken).ConfigureAwait(false);
            if (!usable)
            {
                source.ResolvedCentralArtifactId = null;
                source.ResolvedArtifact = null;
                unavailableSource = true;
            }
        }
        foreach (var source in artifact.Sources.Where(source => source.ResolvedCentralArtifactId == null))
        {
            var resolved = await db.CentralArtifacts.FirstOrDefaultAsync(candidate =>
                candidate.ArtifactId == source.SourceArtifactId
                && candidate.DevicePublicId == frame.DevicePublicId
                && candidate.ObjectState == CentralArtifactObjectState.Available
                && candidate.ReconstructionState == CentralReconstructionState.Complete,
                cancellationToken).ConfigureAwait(false);
            if (resolved is not null)
            {
                source.ResolvedCentralArtifactId = resolved.Id;
                source.ResolvedArtifact = resolved;
            }
        }
        if (!frame.DeviceRigProfileId.HasValue)
        {
            artifact.ReconstructionState = CentralReconstructionState.PendingReference;
            artifact.StateReasonCode = "profile.rig-not-found";
            return;
        }
        if (artifact.Sources.Any(source => source.ResolvedCentralArtifactId == null))
        {
            artifact.ReconstructionState = CentralReconstructionState.PendingReference;
            artifact.StateReasonCode = unavailableSource
                || artifact.StateReasonCode == "lineage.source-unavailable"
                    ? "lineage.source-unavailable"
                    : "lineage.source-not-found";
            return;
        }
        artifact.ReconstructionState = CentralReconstructionState.Complete;
        artifact.StateReasonCode = null;
    }

    private static async Task<string?> VerifyAsync(
        IMinioClient minio,
        CentralArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!artifact.StorageReference.StartsWith(BucketPrefix, StringComparison.Ordinal))
        {
            return "object.reference-invalid";
        }
        long length = 0;
        byte[]? checksum = null;
        try
        {
            await minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(Bucket)
                .WithObject(artifact.StorageReference[BucketPrefix.Length..])
                .WithCallbackStream(stream =>
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = stream.Read(buffer)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        length += read;
                    }
                    checksum = hash.GetHashAndReset();
                }), cancellationToken).ConfigureAwait(false);
        }
        catch (MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
        {
            return "missing";
        }
        if (length != artifact.ByteLength)
        {
            return "object.length-mismatch";
        }
        return string.Equals(Convert.ToHexString(checksum!), artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : "object.checksum-mismatch";
    }
}

internal static class MinioObjectVerification
{
    public static bool IsNotFound(MinioException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is ObjectNotFoundException)
        {
            return true;
        }

        var errorCode = exception.Response?.Code;
        return errorCode is "NoSuchKey" or "NoSuchObject"
            && exception.ServerResponse?.StatusCode == HttpStatusCode.NotFound;
    }
}
