namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

public sealed record CameraAgentSiloArchiveReceipt(string StoreId, string Bucket, string GroupId, string RevisionId,
    long PublicationGeneration, long PublicationSequence, int PublicationIndex,
    long ReceiptGeneration, long ReceiptSequence, string ReceiptVersionId, string ReceiptSha256, DateTimeOffset? VerifiedUtc = null);

public sealed record CameraAgentSiloArchiveStatus(string State, string? Reason, string? StoreId = null, string? Bucket = null,
    bool ReplicationEnabled = false, long BacklogCount = 0, long BacklogBytes = 0, long? OldestBacklogAgeSeconds = null,
    CameraAgentSiloArchiveReceipt? VerifiedCutoff = null, CameraAgentSiloArchiveReceipt? LatestVerifiedReceipt = null);

public sealed record CameraAgentSiloArchivePublication(string State, string? Reason, CameraAgentSiloArchiveReceipt? Receipt);

public sealed record CameraAgentSiloArchiveRead(CameraAgentSiloArchiveReceipt Receipt, string ArtifactId, string ArtifactRevision);

public sealed class CameraAgentSiloArchiveContent(string state, Stream? content = null, string? sha256 = null) : IDisposable
{
    private Action? _release;

    public string State { get; } = state;
    public Stream? Content { get; } = content;
    public string? Sha256 { get; } = sha256;

    internal void HoldDownloadSlot(Action release) => _release = release;

    public void Dispose()
    {
        try
        {
            Content?.Dispose();
        }
        finally
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}

/// <summary>Optional archive operations. These never acquire the local publication or admission locks during an archive read.</summary>
public interface ICameraAgentSiloArchive
{
    CameraAgentSiloArchiveStatus Status { get; }

    ValueTask<CameraAgentSiloArchivePublication?> FindAsync(string groupId, string revisionId, CancellationToken cancellationToken);

    ValueTask<CameraAgentSiloArchiveContent> ReadAsync(CameraAgentSiloArchiveRead request, CancellationToken cancellationToken);
}
