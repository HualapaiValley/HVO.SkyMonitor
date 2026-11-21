using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

public enum CenteredRuntimeEvidenceBasis
{
    Unavailable,
    RetainedJournal,
    ActiveAcquisition
}

/// <summary>The most recent committed exposure in this run, in attempt order.</summary>
public sealed record CenteredAcquisitionHead(
    ExposureKey Exposure, long Ordinal, DateTimeOffset ExposureStartedUtc, DateTimeOffset CommittedUtc);

/// <summary>Current journal protection for the given run as of the observation time.</summary>
public sealed record CenteredRuntimeHolds(int PendingMeteringCount, int ConsumerLeaseCount, long ConsumerLeaseBytes);

public sealed record CenteredExposureRuntimeSnapshot(
    CameraKey Camera,
    Guid? RunId,
    string? GraphRevisionId,
    string? ExecutionVersion,
    CenteredAcquisitionHead? AcquisitionHead,
    int? BufferOccupancy,
    int BufferCapacity,
    CenteredRuntimeHolds? Holds,
    DateTimeOffset ObservedUtc,
    CenteredRuntimeEvidenceBasis Basis,
    string? Reason);

/// <summary>Acquisition evidence for Operations, independent of any displayed or selected view.</summary>
public interface ICenteredExposureRuntimeReader
{
    ValueTask<CenteredExposureRuntimeSnapshot> ReadCurrentAsync(CameraKey camera, CancellationToken cancellationToken);

    ValueTask<CenteredExposureRuntimeSnapshot> ReadRunAsync(
        CameraKey camera, Guid runId, CancellationToken cancellationToken);
}
