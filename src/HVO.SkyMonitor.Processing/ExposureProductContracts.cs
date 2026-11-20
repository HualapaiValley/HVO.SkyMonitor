using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

/// <summary>Named output within one execution of a capture.</summary>
public sealed record ExposureProductSlot(Guid ExecutionId, string Producer, string SchemaVersion, string Variant);

/// <summary>Immutable reference to one committed product.</summary>
public sealed record ExposureProductRef(
    ExposureKey Exposure,
    ExposureProductSlot Slot,
    Guid ArtifactId,
    string OutputIdentitySha256,
    string PayloadSha256,
    string? ContentIdentitySha256);

public enum ExposureProductAvailability
{
    Pending,
    Available,
    Unavailable,
    Incompatible,
    Failed,
    Expired,
    MissingEvidence
}

/// <summary>Durable settlement and its monotonically ordered publication revision.</summary>
public sealed record ExposureProductLookup(
    ExposureKey Exposure,
    ExposureProductSlot Slot,
    ExposureProductAvailability Availability,
    long? Revision,
    ExposureProductRef? Product,
    string? Reason);

/// <summary>Exact step-three result, including a disposition when qualified pixels are unavailable.</summary>
public sealed record LinearExposureLookup(
    ExposureInputV1 Exposure,
    LinearExposureDisposition Disposition,
    long? Revision,
    ExposureProductRef? Product,
    LinearInputProvenanceV1? Provenance,
    string? Reason);

/// <summary>Reads committed exposure and product evidence without borrowing the pixel payload.</summary>
public interface IExposureProductReader
{
    ValueTask<ExposureInputV1?> ReadExposureAsync(ExposureKey key, CancellationToken cancellationToken);

    ValueTask<LinearExposureLookup?> ReadLinearAsync(ExposureKey key, CancellationToken cancellationToken);

    ValueTask<ExposureProductLookup?> ReadProductAsync(
        ExposureKey key,
        ExposureProductSlot slot,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ExposureProductLookup>> ReadProductsAfterAsync(
        CameraKey camera,
        long afterRevision,
        int maximumCount,
        CancellationToken cancellationToken);
}
