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
        var lookup = await TryPinAsync().ConfigureAwait(false);
        if (lookup is null) return null;
        var provenance = lookup.Provenance!;

        var handedOff = false;
        try
        {
            var linear = await persistence.RestoreCenteredLinearAsync(expected, cancellationToken).ConfigureAwait(false);
            if (clock.GetUtcNow() >= expires) return null;
            if (linear.DescriptorIdentitySha256 != provenance.LinearDescriptorSha256)
                throw new InvalidDataException("The retained linear descriptor differs from its centered product provenance.");

            var lease = new CenteredExposureInputLease(store, owner, new(lookup.Exposure, linear, provenance), expires);
            handedOff = true;
            return lease;
        }
        finally
        {
            if (!handedOff) await store.ReleaseCenteredInputHoldAsync(owner).ConfigureAwait(false);
        }

        async Task<LinearExposureLookup?> TryPinAsync()
        {
            // The two lifecycle gates protect only identity and pin acquisition, never a full-frame read.
            await rawGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await storageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var candidate = await reader.ReadLinearCoreAsync(expected.Exposure, cancellationToken).ConfigureAwait(false);
                    if (candidate?.Product != expected ||
                        candidate.Disposition is not (LinearExposureDisposition.Calibrated or LinearExposureDisposition.QualifiedNormalizationOnly) ||
                        candidate.Provenance is null)
                        return null;

                    var output = await store.ReadOutputByArtifactIdAsync(expected.ArtifactId, cancellationToken).ConfigureAwait(false);
                    if (output?.Descriptor is not { } descriptor || output.AvailabilityState != "Available")
                        return null;
                    if (!ingress.CenteredFrameFilesExist(candidate.Exposure.Raw))
                        return null;

                    var bytes = checked(candidate.Exposure.Raw.Layout.ByteLength + descriptor.Layout.ByteLength);
                    return await store.TryHoldCenteredInputAsync(owner, expected, bytes, expires, cancellationToken)
                        .ConfigureAwait(false) ? candidate : null;
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
}
