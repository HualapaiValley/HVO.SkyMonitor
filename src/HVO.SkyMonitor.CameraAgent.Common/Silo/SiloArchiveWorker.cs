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
    ILogger<SiloArchiveWorker> logger) : BackgroundService, ICameraAgentSiloArchive, ISiloRetentionArchive
{
    private const int DiscoverLimit = 64;
    private const int PrefixLimit = 16;
    private const int VerifyLimit = 4;
    private static readonly TimeSpan AttemptLimit = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ReadLimit = TimeSpan.FromMinutes(2);
    private int _downloadInProgress;
    private PrefixRetry? _prefixRetry;

    private sealed record PrefixRetry(SiloArchiveMarker Marker, long Sequence, int Attempts, DateTimeOffset Next, SiloArchiveException Fault);
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
            if (!enabled && !HasArchiveConfiguration(settings))
            {
                Volatile.Write(ref _index, null);
                _prefixRetry = null;
                return Publish(new("disabled", null));
            }
            var marker = SiloArchiveStore.ReadMarker(settings);
            (storeId, bucket) = (marker.StoreId, marker.Bucket);
            var source = services.GetService<SiloInstalledStorage>();
            var bound = source is not null && source.Binding.BindingId == marker.SourceBindingId &&
                source.Binding.Bucket != marker.Bucket;
            if (!bound) Volatile.Write(ref _index, null);
            if (!enabled)
            {
                _prefixRetry = null;
                if (bound)
                    index = await TryOpenIndexAsync(source!, marker, create: false, stoppingToken).ConfigureAwait(false);
                var disabled = index is null ? null : await index.ReadBacklogAsync(stoppingToken).ConfigureAwait(false);
                return Publish(new("disabled", null, storeId, bucket, VerifiedCutoff: disabled?.Cutoff, LatestVerifiedReceipt: disabled?.Latest));
            }
            if (!bound || source is null)
                throw new SiloArchiveException("blocked", "archive-source-binding-mismatch");

            var bindingVerified = source.BindingVerified;
            index = await TryOpenIndexAsync(source, marker, create: bindingVerified, stoppingToken).ConfigureAwait(false);
            if (!bindingVerified)
                throw new SiloArchiveException("pending", "archive-local-publication-index-pending");

            ArgumentNullException.ThrowIfNull(index);
            using var archive = SiloArchiveStore.Create(settings, marker, clock);
            await index.DiscoverAsync(source.Index, DiscoverLimit, stoppingToken).ConfigureAwait(false);
            await archive.VerifyAsync(stoppingToken).ConfigureAwait(false);
            var verification = new SiloArchiveVerification(archive, clock);
            var due = await index.ReadDueAsync(clock.GetUtcNow(), VerifyLimit, stoppingToken).ConfigureAwait(false);
            foreach (var work in due)
            {
                var advanced = await source.Publisher.WithPublicationLockAsync(async () =>
                {
                    if (source.Retention.ReadUnsettledTarget() is not null) return false;
                    await VerifyAttemptAsync(source, archive, verification, index, work, stoppingToken).ConfigureAwait(false);
                    return true;
                }, stoppingToken).ConfigureAwait(false);
                if (!advanced) break;
            }

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

    private static bool HasArchiveConfiguration(IConfigurationSection settings) =>
        !string.IsNullOrEmpty(settings["Endpoint"]) || !string.IsNullOrEmpty(settings["StoreId"]) ||
        !string.IsNullOrEmpty(settings["Bucket"]) || !string.IsNullOrEmpty(settings["SourceBindingId"]) ||
        !string.IsNullOrEmpty(settings["AccessKey"]) || !string.IsNullOrEmpty(settings["SecretKey"]) ||
        !string.IsNullOrEmpty(settings["AuthorityPem"]);

    public async ValueTask<bool> ProtectAsync(SiloObjectIdentity identity, SiloExactLocator survivor, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = SiloArchiveStore.Create(Settings, clock);
            await archive.VerifyAsync(cancellationToken).ConfigureAwait(false);
            if (!await archive.HasLegalHoldAsync(survivor, cancellationToken).ConfigureAwait(false) &&
                !await archive.TryPlaceLegalHoldAsync(Settings, survivor, cancellationToken).ConfigureAwait(false)) return false;
            return await VerifyProtectedAsync(archive, identity, survivor, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SiloArchiveException or SiloStoreUnavailableException or ArgumentException)
        {
            return false;
        }
    }

    public async ValueTask<bool> ConfirmProtectedAsync(SiloObjectIdentity identity, SiloExactLocator survivor, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = SiloArchiveStore.Create(Settings, clock);
            return await VerifyProtectedAsync(archive, identity, survivor, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SiloArchiveException or SiloStoreUnavailableException or ArgumentException)
        {
            return false;
        }
    }

    private static async ValueTask<bool> VerifyProtectedAsync(SiloArchiveStore archive, SiloObjectIdentity identity,
        SiloExactLocator survivor, CancellationToken cancellationToken) =>
        await archive.HasLegalHoldAsync(survivor, cancellationToken).ConfigureAwait(false) &&
        !await archive.WasExpiredAsync(survivor, cancellationToken).ConfigureAwait(false) &&
        (await archive.ProbeAsync(identity, survivor, captureBytes: false, cancellationToken).ConfigureAwait(false)).Status == SiloProbeStatus.Verified;

    public async ValueTask<bool> IsAuthorityExpiredAsync(SiloExactLocator locator, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = SiloArchiveStore.Create(Settings, clock);
            if (locator.StoreId != archive.Marker.StoreId || locator.Bucket != archive.Marker.Bucket ||
                !await archive.WasExpiredAsync(locator, cancellationToken).ConfigureAwait(false)) return false;
            using var open = await archive.Objects.OpenVersionAsync(locator.Key, locator.VersionId, cancellationToken).ConfigureAwait(false);
            return open.Status == SiloOpenStatus.NoSuchVersion;
        }
        catch (Exception exception) when (exception is SiloArchiveException or SiloStoreUnavailableException or ArgumentException)
        {
            return false;
        }
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
        var state = "reachable";
        string? reason = null;
        SiloCommittedSegment? receipt = null;
        var awaitingRefresh = false;
        long? expirySequence = null;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            deadline.CancelAfter(AttemptLimit);
            try
            {
                var prefix = (await index.ReadPrefixAsync(deadline.Token).ConfigureAwait(false)).Sequence;
                var position = work.Publication.Position;
                var committed = await source.State.ReadCommittedSegmentAsync(position, deadline.Token).ConfigureAwait(false)
                    ?? throw new SiloArchiveException("failed", "archive-source-commit-missing");
                var evidence = await verification.VerifyPublicationAsync(work.Publication, committed, source.Index, prefix, deadline.Token)
                    .ConfigureAwait(false);
                if (work.Receipt is { } previous)
                    await RecordArchiveDispositionsAsync(source, archive, verification, work.Publication, previous, evidence, deadline.Token)
                        .ConfigureAwait(false);
                if (evidence.Records.Count == 0)
                {
                    state = "policy-expired";
                    expirySequence = evidence.LocalExpirySequence;
                }
                else
                {
                    bool current;
                    (receipt, awaitingRefresh, current) = await VerifyReceiptAsync(source, archive, verification, work, evidence, prefix, deadline.Token)
                        .ConfigureAwait(false);
                    if (!current)
                        (state, reason) = ("pending", "archive-receipt-refresh-pending");
                }
            }
            catch (SiloArchiveException exception)
            {
                (state, reason) = (exception.State, exception.Reason);
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
        var resetAttempts = state == "reachable" && !awaitingRefresh;
        var retry = now + (resetAttempts
            ? TimeSpan.FromDays(1)
            : TimeSpan.FromSeconds(2 << Math.Min(work.Attempts, 7)));
        await index.CompleteAttemptAsync(work, state, reason, retry, receipt, now, resetAttempts, stoppingToken, expirySequence)
            .ConfigureAwait(false);
    }

    private async ValueTask RecordArchiveDispositionsAsync(SiloInstalledStorage source, SiloArchiveStore archive,
        SiloArchiveVerification verification, SiloPublicationRef publication, SiloCommittedSegment previous,
        SiloArchivePublicationEvidence current, CancellationToken cancellationToken)
    {
        var historical = await verification.ReadReceiptAsync(previous, publication, verifyCopies: false, cancellationToken, current)
            .ConfigureAwait(false);
        foreach (var copy in historical.Copies.Where(copy => !current.Copies.TryGetValue(copy.Identity.Role, out var verified) ||
            verified.Locator != copy.Locator).DistinctBy(static copy => copy.Locator))
        {
            var exact = copy.Locator;
            if (!await archive.WasExpiredAsync(exact, cancellationToken).ConfigureAwait(false)) continue;
            using var observed = await archive.Objects.OpenVersionAsync(exact.Key, exact.VersionId, cancellationToken).ConfigureAwait(false);
            if (observed.Status != SiloOpenStatus.NoSuchVersion) continue;

            var intentId = SiloHash.Sha256(SiloJson.Serialize(exact));
            var operation = "archive-disposition:" + intentId;
            await source.ArchiveJournal.ReconcilePendingAsync(cancellationToken).ConfigureAwait(false);
            if (await source.State.ReadOperationAsync(operation, cancellationToken).ConfigureAwait(false) is not null) continue;
            await source.ArchiveJournal.AppendAsync(
                [
                    new SiloExpiryIntentRecord(intentId, SiloExpiryScope.Copy, copy.Identity, "archive-custody-observed",
                        [new(exact, SiloArchiveStore.Custody)], "archive-owner-expiry", LastCopy: false),
                    new SiloExpiryDispositionRecord(intentId, exact, SiloExpiryOutcome.AlreadyAbsent, clock.GetUtcNow(),
                        "archive-custody-and-exact-absence")
                ], operation, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<(SiloCommittedSegment? Receipt, bool AwaitingRefresh, bool Current)> VerifyReceiptAsync(
        SiloInstalledStorage source, SiloArchiveStore archive, SiloArchiveVerification verification, SiloArchiveWork work,
        SiloArchivePublicationEvidence evidence, long prefix, CancellationToken cancellationToken)
    {
        var position = work.Publication.Position;
        var previous = work.Receipt;
        var operation = FormattableString.Invariant($"archive-copy/{archive.Marker.StoreId}/{position.Generation}/{position.Sequence}/{work.Publication.Index}");
        if (previous is not null) operation += "/" + previous.Sha256;
        if (await source.State.ReadPendingAsync(cancellationToken).ConfigureAwait(false) is { } pending && pending.OperationId == operation)
            await source.ArchiveJournal.ReconcilePendingAsync(cancellationToken).ConfigureAwait(false);

        var recorded = await source.State.ReadOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        SiloCommittedSegment receipt;
        SiloArchiveReceiptEvidence? historical = null;
        if (recorded is not null)
        {
            receipt = new(recorded.Position, recorded.Sha256, recorded.VersionId);
        }
        else
        {
            if (previous is not null)
            {
                historical = await verification.ReadReceiptAsync(previous, work.Publication, verifyCopies: false, cancellationToken, evidence)
                    .ConfigureAwait(false);
                if (!NeedsReceiptRefresh(evidence, historical)) return (previous, false, true);
            }
            receipt = await source.ArchiveJournal.AppendAsync(evidence.Records, operation, cancellationToken).ConfigureAwait(false);
        }

        if (previous is not null && (receipt.Position.Generation != 1 || receipt.Position.Sequence > prefix))
        {
            historical ??= await verification.ReadReceiptAsync(previous, work.Publication, verifyCopies: false, cancellationToken, evidence)
                .ConfigureAwait(false);
            // The previous receipt can support the cutoff while its replacement replicates if its copies still verify.
            return CopiesAreCurrent(evidence, historical) ? (previous, true, true) : (null, true, false);
        }
        var candidate = await verification.ReadReceiptAsync(receipt, work.Publication, verifyCopies: false, cancellationToken, evidence)
            .ConfigureAwait(false);
        // A recorded replacement can age while waiting for the prefix. Keep its position for the next refresh.
        var needsRefresh = NeedsReceiptRefresh(evidence, candidate);
        return (receipt, needsRefresh, !needsRefresh);
    }

    private static bool NeedsReceiptRefresh(SiloArchivePublicationEvidence current, SiloArchiveReceiptEvidence historical)
    {
        var receipt = (SiloArchiveReceiptRecord)current.Records[0];
        var unverified = receipt.UnverifiedMembers ?? [];
        return current.Copies.Count != historical.Copies.Count || !CopiesAreCurrent(current, historical) ||
            unverified.Count != historical.Unverified.Count ||
            unverified.Any(member => historical.Unverified.GetValueOrDefault(member.Role) != member.State);
    }

    private static bool CopiesAreCurrent(SiloArchivePublicationEvidence current, SiloArchiveReceiptEvidence historical) =>
        historical.Copies.All(copy => current.Copies.TryGetValue(copy.Identity.Role, out var verified) &&
            verified.Locator == copy.Locator && verified.Identity == copy.Identity);

    private async ValueTask<SiloArchiveException?> AdvancePrefixAsync(SiloInstalledStorage source, SiloArchiveStore archive,
        SiloArchiveIndex index, CancellationToken stoppingToken)
    {
        var (sequence, sha) = await index.ReadPrefixAsync(stoppingToken).ConfigureAwait(false);
        if (_prefixRetry is { } retry && retry.Marker == archive.Marker && retry.Sequence == sequence &&
            clock.GetUtcNow() < retry.Next)
            return retry.Fault;
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
                        throw new SiloArchiveException(proof.Status switch
                        {
                            SiloProbeStatus.Damaged => "failed",
                            SiloProbeStatus.Unavailable => "blocked",
                            _ => "pending"
                        }, "archive-checkpoint-not-verified");
                }
                sequence = next.Sequence;
                sha = committed.Sha256;
                await index.SavePrefixAsync(sequence, sha, deadline.Token).ConfigureAwait(false);
            }
            _prefixRetry = null;
            return head.Generation > 1 ? new("blocked", "archive-unsupported-writer-generation")
                : sequence < head.Sequence ? new("pending", "archive-journal-verification-pending") : null;
        }
        catch (SiloArchiveException exception)
        {
            return DeferPrefix(archive.Marker, sequence, exception);
        }
        catch (Exception exception) when (exception is IOException ||
            exception is OperationCanceledException && !stoppingToken.IsCancellationRequested)
        {
            stoppingToken.ThrowIfCancellationRequested();
            return DeferPrefix(archive.Marker, sequence, new("blocked", "archive-journal-unavailable"));
        }
    }

    private SiloArchiveException DeferPrefix(SiloArchiveMarker marker, long sequence, SiloArchiveException fault)
    {
        var attempts = _prefixRetry is { } retry && retry.Marker == marker && retry.Sequence == sequence
            ? Math.Min(retry.Attempts + 1, 7) : 0;
        if (fault.State == "pending" && attempts >= 5)
            fault = new("blocked", fault.Reason);
        _prefixRetry = new(marker, sequence, attempts, clock.GetUtcNow() + TimeSpan.FromSeconds(2 << attempts), fault);
        return fault;
    }

    public ValueTask<CameraAgentSiloArchivePublication?> FindAsync(string groupId, string revisionId, CancellationToken cancellationToken) =>
        Volatile.Read(ref _index) is { } index ? index.FindAsync(groupId, revisionId, cancellationToken)
            : ValueTask.FromResult<CameraAgentSiloArchivePublication?>(null);

    [SuppressMessage("Reliability", "CA2000", Justification = "The response owns the verified scratch stream; failures dispose it in finally.")]
    public async ValueTask<CameraAgentSiloArchiveContent> ReadAsync(CameraAgentSiloArchiveRead request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var receipt = request.Receipt;
        if (receipt is null || !SiloHash.IsValid(receipt.ReceiptSha256) || !SiloText.IsToken(receipt.GroupId, 128) ||
            !SiloText.IsToken(receipt.RevisionId, 128) || !SiloText.IsToken(receipt.StoreId, 128) ||
            !SiloText.IsToken(receipt.Bucket, 63) || !SiloText.IsText(receipt.ReceiptVersionId, 1024) ||
            !SiloText.IsText(request.ArtifactId, 256) || !SiloText.IsText(request.ArtifactRevision, 256) ||
            receipt.PublicationGeneration < 1 || receipt.PublicationSequence < 1 || receipt.PublicationIndex < 0 ||
            receipt.ReceiptGeneration < 1 || receipt.ReceiptSequence < 1 ||
            new SiloJournalPosition(receipt.ReceiptGeneration, receipt.ReceiptSequence) <=
                new SiloJournalPosition(receipt.PublicationGeneration, receipt.PublicationSequence))
            return new("invalid-request");

        var settings = Settings;
        if (Interlocked.CompareExchange(ref _downloadInProgress, 1, 0) != 0)
            return new("blocked");
        FileStream? scratch = null;
        var releaseSlot = true;
        try
        {
            if (!HasArchiveConfiguration(settings) && !settings.GetValue("ReplicationEnabled", false))
                return new("disabled");
            var marker = SiloArchiveStore.ReadMarker(settings);
            if (receipt.StoreId != marker.StoreId || receipt.Bucket != marker.Bucket)
                return new("invalid-request");

            var maximumBytes = settings.GetValue("MaximumReadBytes", 512L * 1024 * 1024);
            if (maximumBytes is < 1 or > SiloS3ObjectStore.MaximumPutBytes)
                return new("failed");
            using var archive = SiloArchiveStore.Create(settings, marker, clock);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ReadLimit);
            await archive.VerifyAsync(deadline.Token).ConfigureAwait(false);
            var verification = new SiloArchiveVerification(archive, clock);
            var evidence = await verification.ReadReceiptAsync(
                new(new(receipt.ReceiptGeneration, receipt.ReceiptSequence), receipt.ReceiptSha256, receipt.ReceiptVersionId),
                new(receipt.GroupId, receipt.RevisionId, new(receipt.PublicationGeneration, receipt.PublicationSequence), receipt.PublicationIndex),
                verifyCopies: false, deadline.Token).ConfigureAwait(false);
            var selected = evidence.Copies.SingleOrDefault(copy => copy.Identity.Kind == SiloIdentityKind.Member &&
                copy.Identity.Id == request.ArtifactId && copy.Identity.Revision == request.ArtifactRevision);
            if (selected is null)
                return new(evidence.Unverified.GetValueOrDefault(new(SiloIdentityKind.Member, request.ArtifactId, request.ArtifactRevision), "missing"));

            var rawRoot = services.GetService<IOptions<CameraAgentHostOptions>>()?.Value.RawIngressRoot;
            var scratchRoot = settings["ReadScratchRoot"] ?? rawRoot;
            if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(scratchRoot) || !Path.IsPathFullyQualified(scratchRoot))
                return new("blocked");
            if (selected.Identity.Length > maximumBytes)
                return new("blocked");
            var physical = PhysicalRoot.Open(scratchRoot);
            PhysicalRoot.Open(Path.GetPathRoot(physical.Path)!).Verify(physical.Path, "silo-archive-read-scratch");
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
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException or JsonException)
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
            try
            {
                if (scratch is not null) await scratch.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (releaseSlot) Volatile.Write(ref _downloadInProgress, 0);
            }
        }
    }

    [LoggerMessage(7395, LogLevel.Information, "CameraAgent Silo archive {State} ({Reason})")]
    private static partial void ReportState(ILogger logger, string state, string? reason);
}
