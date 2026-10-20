namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>Crash and disk-full boundaries of orbital publication and retention, for deterministic tests.</summary>
internal enum OrbitalStorageFaultPoint
{
    AfterRaw,
    AfterPayload,
    AfterManifest,
    BeforeActivationCommit,
    AfterTombstone
}

/// <summary>
/// Throwing an <see cref="IOException"/> simulates a storage failure the store must survive and record; throwing
/// anything else simulates a process crash at that boundary and propagates without any recording.
/// </summary>
internal interface IOrbitalStorageFaultInjector
{
    void Inject(OrbitalStorageFaultPoint point);
}

internal sealed class NullOrbitalStorageFaultInjector : IOrbitalStorageFaultInjector
{
    public static NullOrbitalStorageFaultInjector Instance { get; } = new();

    public void Inject(OrbitalStorageFaultPoint point)
    {
    }
}
