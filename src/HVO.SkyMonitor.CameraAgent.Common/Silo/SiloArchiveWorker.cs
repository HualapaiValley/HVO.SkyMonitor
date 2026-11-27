using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed partial class SiloArchiveWorker(IConfiguration configuration, IServiceProvider services, TimeProvider clock,
    ILogger<SiloArchiveWorker> logger) : BackgroundService, ICameraAgentSiloArchive
{
    private const int DiscoverLimit = 64;
    private const int PrefixLimit = 16;
    private const int VerifyLimit = 4;
    private static readonly TimeSpan AttemptLimit = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ReadLimit = TimeSpan.FromMinutes(2);
    private int _downloadInProgress;
    private SiloArchiveIndex? _index;
    private CameraAgentSiloArchiveStatus _status = new("disabled", null);

    private IConfigurationSection Settings => configuration.GetSection("CameraAgent:SiloArchive");

    public CameraAgentSiloArchiveStatus Status => Volatile.Read(ref _status);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consecutiveFaults = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var result = await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                consecutiveFaults = result.State is "blocked" or "failed" ? Math.Min(consecutiveFaults + 1, 5) : 0;
                await Task.Delay(TimeSpan.FromSeconds(consecutiveFaults == 0 ? 2 : 2 << consecutiveFaults), clock, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The optional archive worker reports isolated failures without terminating local capture.")]
    internal async ValueTask<CameraAgentSiloArchiveStatus> RunCycleAsync(CancellationToken stoppingToken)
    {
        var enabled = false;
        string? storeId = null;
        string? bucket = null;
        SiloArchiveIndex? index = null;
        string? state = null;
        string? reason = null;
        try
        {
            var settings = Settings;
            enabled = settings.GetValue("ReplicationEnabled", false);
            if (!settings.Exists())
            {
                Volatile.Write(ref _index, null);
                return Publish(new("disabled", null));
            }
            using var archive = SiloArchiveStore.Create(settings, clock);
            storeId = archive.Marker.StoreId;
            bucket = archive.Marker.Bucket;
            var source = services.GetService<SiloInstalledStorage>();
            if (!enabled)
            {
                if (source is not null && source.Binding.BindingId == archive.Marker.SourceBindingId)
                    index = await TryOpenIndexAsync(source, archive.Marker, create: false, stoppingToken).ConfigureAwait(false);
                var disabled = index is null ? null : await index.ReadBacklogAsync(stoppingToken).ConfigureAwait(false);
                return Publish(new("disabled", null, storeId, bucket, VerifiedCutoff: disabled?.Cutoff, LatestVerifiedReceipt: disabled?.Latest));
            }
            if (source is null || source.Binding.BindingId != archive.Marker.SourceBindingId ||
                source.Binding.Bucket == archive.Marker.Bucket)
                throw new SiloArchiveException("blocked", "archive-source-binding-mismatch");
            if (!source.BindingVerified)
                throw new SiloArchiveException("pending", "archive-local-publication-index-pending");

            index = await TryOpenIndexAsync(source, archive.Marker, create: true, stoppingToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(index);
            await index.DiscoverAsync(source.Index, DiscoverLimit, stoppingToken).ConfigureAwait(false);
            await archive.VerifyAsync(stoppingToken).ConfigureAwait(false);
            var verification = new SiloArchiveVerification(archive, clock);
            var due = await index.ReadDueAsync(clock.GetUtcNow(), VerifyLimit, stoppingToken).ConfigureAwait(false);
            foreach (var work in due)
                await VerifyAttemptAsync(source, archive, verification, index, work, stoppingToken).ConfigureAwait(false);

            if (await AdvancePrefixAsync(source, archive, index, stoppingToken).ConfigureAwait(false) is { } prefix)
                (state, reason) = (prefix.State, prefix.Reason);
        }
        catch (SiloArchiveException exception)
        {
            (state, reason) = (exception.State, exception.Reason);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException &&
            !stoppingToken.IsCancellationRequested)
        {
            (state, reason) = ("failed", "archive-configuration-or-evidence-invalid");
        }
        catch (Exception) when (!stoppingToken.IsCancellationRequested)
        {
            (state, reason) = ("blocked", "archive-verification-unavailable");
        }

        SiloArchiveBacklog? backlog = null;
        try
        {
            if (index is not null)
                backlog = await index.ReadBacklogAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception) when (!stoppingToken.IsCancellationRequested)
        {
            return Publish(Status with { State = "failed", Reason = "archive-work-index-unavailable" });
        }

        state = state is "blocked" or "failed" ? state
            : backlog?.Failed > 0 ? "failed"
            : backlog?.Blocked > 0 ? "blocked"
            : state == "pending" || backlog?.Count > 0 ? "pending"
            : "reachable";
        reason ??= state switch { "failed" => "archive-receipt-verification-failed", "blocked" => "archive-replica-backlog-blocked", _ => null };
        return Publish(new(state, reason, storeId, bucket, enabled, backlog?.Count ?? 0, backlog?.Bytes ?? 0,
            backlog?.Oldest is { } oldest ? Math.Max(0, (long)(clock.GetUtcNow() - oldest).TotalSeconds) : null,
            backlog?.Cutoff, backlog?.Latest));
    }

    private async ValueTask<SiloArchiveIndex?> TryOpenIndexAsync(SiloInstalledStorage source, SiloArchiveMarker marker,
        bool create, CancellationToken cancellationToken)
    {
        var path = source.PhysicalState.Resolve(marker.StoreId + ".db");
        var current = Volatile.Read(ref _index);
        var exists = File.Exists(path);
        if (current is not null && current.Matches(marker) && exists) return current;
        Volatile.Write(ref _index, null);
        if (!create && !exists) return null;
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            source.PhysicalState.Verify(path + suffix, "silo-archive-index");
        var index = new SiloArchiveIndex(path, marker);
        await index.InitializeAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _index, index);
        return index;
    }

    private CameraAgentSiloArchiveStatus Publish(CameraAgentSiloArchiveStatus status)
    {
        var previous = Interlocked.Exchange(ref _status, status);
        if (previous.State != status.State || previous.Reason != status.Reason)
            ReportState(logger, status.State, status.Reason);
        return status;
    }

    private async ValueTask VerifyAttemptAsync(SiloInstalledStorage source, SiloArchiveStore archive, SiloArchiveVerification verification,
        SiloArchiveIndex index, SiloArchiveWork work, CancellationToken stoppingToken)
    {
        var position = work.Publication.Position;
        var operation = FormattableString.Invariant($"archive-copy/{archive.Marker.StoreId}/{position.Generation}/{position.Sequence}/{work.Publication.Index}");
        var state = "reachable";
        string? reason = null;
        SiloCommittedSegment? receipt = null;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            deadline.CancelAfter(AttemptLimit);
            try
            {
                if (await source.State.ReadPendingAsync(deadline.Token).ConfigureAwait(false) is { } pending && pending.OperationId == operation)
                    await source.ArchiveJournal.ReconcilePendingAsync(deadline.Token).ConfigureAwait(false);
                if (await source.State.ReadOperationAsync(operation, deadline.Token).ConfigureAwait(false) is { } previous)
                {
                    receipt = new(previous.Position, previous.Sha256, previous.VersionId);
                }
                else
                {
                    var committed = await source.State.ReadCommittedSegmentAsync(position, deadline.Token).ConfigureAwait(false)
                        ?? throw new SiloArchiveException("failed", "archive-source-commit-missing");
                    var records = await verification.VerifyPublicationAsync(work.Publication, committed, source.Index, deadline.Token)
                        .ConfigureAwait(false);
                    receipt = await source.ArchiveJournal.AppendAsync(records, operation, deadline.Token).ConfigureAwait(false);
                }
                await verification.ReadReceiptAsync(receipt, work.Publication, verifyCopies: true, deadline.Token).ConfigureAwait(false);
            }
            catch (SiloArchiveException exception)
            {
                (state, reason) = (exception.State, exception.Reason);
                if (reason == "archive-copy-policy-expired")
                    state = "policy-expired";
                if (state == "pending" && work.Attempts >= 5)
                    state = "blocked";
            }
            catch (Exception exception) when (exception is IOException ||
                exception is OperationCanceledException && !stoppingToken.IsCancellationRequested)
            {
                (state, reason) = ("blocked", "archive-attempt-unavailable");
            }
            catch (Exception exception) when (exception is SiloDataException or SiloConflictException or JsonException)
            {
                (state, reason) = ("failed", "archive-attempt-evidence-invalid");
            }
        }
        stoppingToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        var retry = now + (state == "reachable" ? TimeSpan.FromDays(1) : TimeSpan.FromSeconds(2 << Math.Min(work.Attempts, 7)));
        await index.CompleteAttemptAsync(work, state, reason, retry, state == "reachable" ? receipt : null, now, stoppingToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<SiloArchiveException?> AdvancePrefixAsync(SiloInstalledStorage source, SiloArchiveStore archive,
        SiloArchiveIndex index, CancellationToken stoppingToken)
    {
        var (sequence, sha) = await index.ReadPrefixAsync(stoppingToken).ConfigureAwait(false);
        var head = await source.State.ReadHeadAsync(stoppingToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(AttemptLimit);
        try
        {
            for (var count = 0; count < PrefixLimit && sequence < head.Sequence && head.Generation == 1; count++)
            {
                var next = new SiloJournalPosition(1, sequence + 1);
                var committed = await source.State.ReadCommittedSegmentAsync(next, deadline.Token).ConfigureAwait(false)
                    ?? throw new SiloArchiveException("failed", "archive-source-commit-missing");
                var journal = await archive.ReadSegmentAsync(committed, deadline.Token).ConfigureAwait(false);
                if (journal.Previous != (sequence > 0 ? new SiloJournalPosition(1, sequence) : null) || journal.PreviousSha256 != sha)
                    throw new SiloArchiveException("failed", "archive-journal-chain-mismatch");
                foreach (var anchor in journal.Records.OfType<SiloAnchorRecord>())
                {
                    var identity = new SiloObjectIdentity(SiloIdentityKind.Manifest, "checkpoint", "1", anchor.CheckpointSha256, anchor.CheckpointLength);
                    var proof = await archive.ProbeAsync(identity, archive.ReplicaOf(anchor.Checkpoint), captureBytes: false, deadline.Token)
                        .ConfigureAwait(false);
                    if (proof.Status != SiloProbeStatus.Verified)
                        throw new SiloArchiveException(proof.Status == SiloProbeStatus.Damaged ? "failed" : "pending",
                            "archive-checkpoint-not-verified");
                }
                sequence = next.Sequence;
                sha = committed.Sha256;
                await index.SavePrefixAsync(sequence, sha, deadline.Token).ConfigureAwait(false);
            }
            return head.Generation > 1 ? new("blocked", "archive-unsupported-writer-generation")
                : sequence < head.Sequence ? new("pending", "archive-journal-verification-pending") : null;
        }
        catch (SiloArchiveException exception)
        {
            return exception;
        }
    }

    public ValueTask<CameraAgentSiloArchivePublication?> FindAsync(string groupId, string revisionId, CancellationToken cancellationToken) =>
        Volatile.Read(ref _index) is { } index ? index.FindAsync(groupId, revisionId, cancellationToken)
            : ValueTask.FromResult<CameraAgentSiloArchivePublication?>(null);

    [SuppressMessage("Reliability", "CA2000", Justification = "The response owns the verified scratch stream; failures dispose it in finally.")]
    public async ValueTask<CameraAgentSiloArchiveContent> ReadAsync(CameraAgentSiloArchiveRead request, CancellationToken cancellationToken)
    {
        if (!Settings.Exists()) return new("disabled");
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _downloadInProgress, 1, 0) != 0)
            return new("blocked");
        FileStream? scratch = null;
        var releaseSlot = true;
        try
        {
            var receipt = request.Receipt;
            if (receipt is null || !SiloHash.IsValid(receipt.ReceiptSha256) || !SiloText.IsToken(receipt.GroupId, 128) ||
                !SiloText.IsToken(receipt.RevisionId, 128) || string.IsNullOrWhiteSpace(request.ArtifactId) ||
                string.IsNullOrWhiteSpace(request.ArtifactRevision))
                return new("invalid-request");
            SiloText.RequireText(receipt.ReceiptVersionId, nameof(receipt.ReceiptVersionId), 1024);
            using var archive = SiloArchiveStore.Create(Settings, clock);
            if (receipt.StoreId != archive.Marker.StoreId || receipt.Bucket != archive.Marker.Bucket)
                return new("invalid-request");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ReadLimit);
            await archive.VerifyAsync(deadline.Token).ConfigureAwait(false);
            var verification = new SiloArchiveVerification(archive, clock);
            var copies = await verification.ReadReceiptAsync(
                new(new(receipt.ReceiptGeneration, receipt.ReceiptSequence), receipt.ReceiptSha256, receipt.ReceiptVersionId),
                new(receipt.GroupId, receipt.RevisionId, new(receipt.PublicationGeneration, receipt.PublicationSequence), receipt.PublicationIndex),
                verifyCopies: false, deadline.Token).ConfigureAwait(false);
            var selected = copies.SingleOrDefault(copy => copy.Identity.Kind == SiloIdentityKind.Member &&
                copy.Identity.Id == request.ArtifactId && copy.Identity.Revision == request.ArtifactRevision);
            if (selected is null) return new("missing");

            var rawRoot = services.GetService<IOptions<CameraAgentHostOptions>>()?.Value.RawIngressRoot;
            var scratchRoot = Settings["ReadScratchRoot"] ?? rawRoot;
            if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(scratchRoot) || !Path.IsPathFullyQualified(scratchRoot))
                return new("blocked");
            var maximumBytes = Settings.GetValue("MaximumReadBytes", 512L * 1024 * 1024);
            if (maximumBytes is < 1 or > SiloS3ObjectStore.MaximumPutBytes || selected.Identity.Length > maximumBytes)
                return new("blocked");
            var physical = PhysicalRoot.Open(scratchRoot);
            scratch = new FileStream(physical.Resolve($"hvo-archive-{Guid.NewGuid():N}.tmp"),
                new FileStreamOptions
                {
                    Access = FileAccess.ReadWrite,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.Delete,
                    BufferSize = 64 * 1024,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
            File.Delete(scratch.Name);
            await verification.CopyAsync(selected, scratch, deadline.Token).ConfigureAwait(false);
            scratch.Position = 0;
            var result = new CameraAgentSiloArchiveContent("reachable", scratch, selected.Identity.Sha256);
            result.HoldDownloadSlot(() => Volatile.Write(ref _downloadInProgress, 0));
            releaseSlot = false;
            scratch = null;
            return result;
        }
        catch (SiloArchiveException exception)
        {
            return new(exception.Reason == "archive-copy-policy-expired" ? "policy-expired" :
                exception.State == "pending" ? "missing" : exception.State);
        }
        catch (ArgumentException)
        {
            return new("invalid-request");
        }
        catch (Exception exception) when (exception is SiloDataException or JsonException)
        {
            return new("failed");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new("blocked");
        }
        finally
        {
            if (scratch is not null) await scratch.DisposeAsync().ConfigureAwait(false);
            if (releaseSlot) Volatile.Write(ref _downloadInProgress, 0);
        }
    }

    [LoggerMessage(7395, LogLevel.Information, "CameraAgent Silo archive {State} ({Reason})")]
    private static partial void ReportState(ILogger logger, string state, string? reason);
}
