using System.Text.Json;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed record SiloRetentionAttempt(
    string Id,
    SiloExactLocator Target,
    IReadOnlyList<SiloExpiryIntentRecord> Intents,
    IReadOnlyList<SiloReserveSurvivorRecord> Survivors,
    SiloPhysicalHeadroom Before,
    bool Issued = false,
    SiloExpiryOutcome? Outcome = null,
    DateTimeOffset? ObservedUtc = null,
    string? Evidence = null,
    SiloPhysicalAllocation? Allocation = null,
    bool DispositionCommitted = false);

internal sealed record SiloReclaimObservation(SiloPhysicalHeadroom BeforeDelete, DateTimeOffset ObservedUtc,
    SiloPhysicalAllocation? Allocation = null);

internal sealed record SiloRetentionState(
    IReadOnlyList<SiloObjectLease> Leases, SiloRetentionAttempt? Attempt = null, SiloReclaimObservation? ReclaimPending = null)
{
    private const string Name = "retention-v1.json";
    private const int MaxBytes = 4 * 1024 * 1024;

    internal static SiloRetentionState Read(PhysicalRoot root)
    {
        var path = root.Resolve(Name);
        if (!File.Exists(path)) return new([]);
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxBytes)
            throw new InvalidDataException("Silo retention state exceeds its bound.");
        try
        {
            return JsonSerializer.Deserialize<SiloRetentionState>(stream, SiloJson.Options)
                ?? throw new InvalidDataException("Silo retention state is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Silo retention state is invalid.", exception);
        }
    }

    internal async ValueTask WriteAsync(PhysicalRoot root, CancellationToken cancellationToken)
    {
        var bytes = SiloJson.Serialize(this);
        if (bytes.Length > MaxBytes)
            throw new InvalidDataException("Silo retention state exceeds its bound.");
        await AtomicPublisher.PublishAsync(root, Name, PublishMode.Replace,
            (stream, token) => stream.WriteAsync(bytes, token).AsTask(), cancellationToken).ConfigureAwait(false);
    }
}
