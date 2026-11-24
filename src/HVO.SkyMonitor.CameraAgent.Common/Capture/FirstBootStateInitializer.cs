using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

/// <summary>
/// Persists a Silo-layout instance's first-boot pause before its capture service ever starts (#1966; cutover contract
/// 8.1). The installer runs CameraAgent once in this mode, with the instance's own state mounts and settings but no web
/// host, camera module or capture loop: it creates the raw-ingress journal, whose schema transaction writes the pause,
/// and reports the durable status. A retry over the state it left is idempotent; any other existing state is refused.
/// </summary>
public static class FirstBootStateInitializer
{
    public const string Argument = "--hvo-initialize-first-boot-state";

    public static async Task<FirstBootPauseStatus> InitializeAsync(
        string rawIngressRoot,
        int busyTimeoutSeconds,
        FirstBootPauseIdentity seed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var journalDirectory = Path.Combine(Path.GetFullPath(rawIngressRoot), "journal");
        Directory.CreateDirectory(journalDirectory);
        var databasePath = Path.Combine(journalDirectory, "raw-ingress.db");
        await new SqliteRawCaptureJournal(databasePath, busyTimeoutSeconds, firstBootPause: seed)
            .InitializeAsync(cancellationToken).ConfigureAwait(false);
        var status = await new SqliteCaptureControlStore(databasePath, busyTimeoutSeconds, TimeProvider.System)
            .ReadFirstBootPauseAsync(seed, cancellationToken).ConfigureAwait(false);
        if (!status.Seeded || status.Resumed || status.State != "paused" || status.Version != 1 || !status.InForce)
            throw new InvalidDataException("The capture state is not the new instance's first-boot pause.");
        return status;
    }
}
