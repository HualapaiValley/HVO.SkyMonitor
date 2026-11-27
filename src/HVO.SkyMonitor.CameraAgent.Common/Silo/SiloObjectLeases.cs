namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed record SiloObjectLease(string Id, IReadOnlyList<SiloExactLocator> Versions, DateTimeOffset ExpiresUtc);

/// <summary>Restore and replay can lease the whole batch of exact paths they are about to read.</summary>
internal interface ISiloObjectLeases
{
    ValueTask<SiloObjectLease?> TryAcquireAsync(IReadOnlyList<SiloExactLocator> versions, TimeSpan duration, CancellationToken cancellationToken);

    ValueTask<bool> RevalidateAsync(SiloObjectLease lease, CancellationToken cancellationToken);

    ValueTask ReleaseAsync(SiloObjectLease lease, CancellationToken cancellationToken);
}

internal interface ISiloVersionRemoval
{
    /// <returns>False only for an authoritative refusal. An uncertain outcome throws.</returns>
    ValueTask<bool> DeleteVersionAsync(SiloExactLocator locator, CancellationToken cancellationToken);
}

internal interface ISiloRetentionArchive
{
    ValueTask<bool> ProtectAsync(SiloObjectIdentity identity, SiloExactLocator survivor, CancellationToken cancellationToken);

    ValueTask<bool> ConfirmProtectedAsync(SiloObjectIdentity identity, SiloExactLocator survivor, CancellationToken cancellationToken);

    ValueTask<bool> IsAuthorityExpiredAsync(SiloExactLocator locator, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}
