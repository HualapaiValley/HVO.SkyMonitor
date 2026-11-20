using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CenteredExposureInputLease(
    SqliteCaptureProcessingStore store,
    Guid owner,
    LinearExposureInputV1 input,
    DateTimeOffset expiresUtc) : IAsyncDisposable
{
    internal LinearExposureInputV1 Input { get; } = input;

    internal DateTimeOffset ExpiresUtc { get; } = expiresUtc;

    public ValueTask DisposeAsync() => new(store.ReleaseCenteredInputHoldAsync(owner));
}

internal sealed class CenteredExposureInputCustody(
    RawCaptureIngress ingress,
    CenteredExposureProductReader reader,
    SqliteCaptureProcessingStore store,
    CaptureProcessingPersistence persistence,
    TimeProvider clock)
{
    internal async Task<CenteredExposureInputLease?> OpenLinearAsync(
        ExposureProductRef expected, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(duration));

        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var rawGate = RawIngressLifecycleLock.ForRoot(persistence.StorageRoot);
        var storageGate = StorageLifecycleLock.ForRoot(persistence.StorageRoot);
        var owner = Guid.NewGuid();
        var expires = clock.GetUtcNow().Add(duration);
        await rawGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await storageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var lookup = await reader.ReadLinearCoreAsync(expected.Exposure, cancellationToken).ConfigureAwait(false);
                if (lookup?.Product != expected || lookup.Provenance is not { } provenance)
                    return null;

                var linear = await persistence.RestoreCenteredLinearAsync(expected, cancellationToken).ConfigureAwait(false);
                var raw = await ingress.ReadCenteredRawAsync(expected.Exposure, cancellationToken).ConfigureAwait(false);
                if (raw is null || !ingress.CenteredFrameFilesExist(raw.Manifest))
                    return null;

                var bytes = checked(lookup.Exposure.Raw.Layout.ByteLength + linear.Payload.Length);
                if (!await store.TryHoldCenteredInputAsync(owner, expected, bytes, expires, cancellationToken)
                    .ConfigureAwait(false))
                    return null;
                return new(store, owner, new(lookup.Exposure, linear, provenance), expires);
            }
            finally
            {
                storageGate.Release();
            }
        }
        finally
        {
            rawGate.Release();
        }
    }
}
