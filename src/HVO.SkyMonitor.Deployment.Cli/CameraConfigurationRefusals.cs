namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The refusals of a re-run whose answers or camera configuration the installer cannot adopt (Refs #1779): answers that
/// differ from the installed instance's, a configuration it cannot explain, or newer defaults the running image does not
/// run. Each is composed from fixed prose and formatted fields
/// (<c>DiagnosticFormatting.md</c>), so the top-level writer prints it whole, naming the file or record and the remedy,
/// instead of cutting it at its 256-character limit. Every refusal here has changed nothing.
/// </summary>
internal static class CameraConfigurationRefusals
{
    /// <summary>
    /// A change description as the refusal prints it: its arrows in words, since every field of a composed message is
    /// escaped as JSON text, which would print an arrow as its escape. The plan names the change exactly.
    /// </summary>
    private static string Plain(string change) => change.Replace("→", "to", StringComparison.Ordinal);

    /// <summary>
    /// The re-run's answers differ from those the instance was installed with: every differing option, each with its
    /// installed and requested value, however many there are.
    /// </summary>
    public static InstallerException DifferentAnswers(
        Guid instanceId, IReadOnlyList<(string Option, string Installed, string Requested)> differences)
    {
        ArgumentNullException.ThrowIfNull(differences);
        var changed = differences.Select(static difference =>
            difference.Option + " (" + CameraAgentStateDiagnostics.Reason(difference.Installed) + ", not " +
            CameraAgentStateDiagnostics.Reason(difference.Requested) + ")");
        return InstallerException.RefusedFormatted(
            $"Instance {instanceId:D} is installed with a different {string.Join(", ", changed)}. A re-run may change only --cpus and --memory; give the answers it was installed with.");
    }

    /// <summary>
    /// A record describes the instance's rig profile or schedule otherwise than its camera configuration does: each
    /// differing field, with the recorded value and the configuration's.
    /// </summary>
    public static InstallerException RigOrScheduleDisagrees(
        Guid instanceId, string recordPath, string configurationPath, IReadOnlyList<(string Field, string Recorded, string Configured)> differences)
    {
        ArgumentNullException.ThrowIfNull(differences);
        var changed = differences.Select(static difference =>
            difference.Field + " " + CameraAgentStateDiagnostics.Reason(difference.Recorded) + ", not " +
            CameraAgentStateDiagnostics.Reason(difference.Configured));
        return InstallerException.RefusedFormatted(
            $"Instance {instanceId:D} cannot be re-run: {CameraAgentStateDiagnostics.Reason(recordPath)} describes its rig profile and schedule otherwise than its camera configuration, {CameraAgentStateDiagnostics.Reason(configurationPath)}, does: {string.Join(", ", changed)}. A re-run does not rewrite a configuration it cannot explain. Restore the instance's records from a backup, or re-run it with the installer that last installed or re-ran it.");
    }

    /// <summary>The recorded configuration identity is not one any configuration-default generation writes.</summary>
    public static InstallerException UnknownIdentity(
        Guid instanceId, string manifestPath, string configurationPath, string configurationSha256, string known)
        => InstallerException.RefusedFormatted(
            $"Instance {instanceId:D} cannot be re-run: its records ({CameraAgentStateDiagnostics.Reason(manifestPath)}) bind its camera configuration, {CameraAgentStateDiagnostics.Reason(configurationPath)}, to identity {CameraAgentStateDiagnostics.Reason(configurationSha256)}, which this installer does not write for the installed answers under any configuration defaults it knows ({CameraAgentStateDiagnostics.Reason(known)}). A re-run does not rewrite a configuration it cannot explain. A newer installer may have written it: re-run the instance with that installer. Otherwise restore the instance's records and configuration from a backup.");

    /// <summary>Adopting a generation would move the rig profile or schedule identity, which a re-run never does.</summary>
    public static InstallerException MovesRigOrSchedule(Guid instanceId, string generation)
        => InstallerException.RefusedFormatted(
            $"Instance {instanceId:D} cannot be re-run: adopting the installer's configuration defaults {CameraAgentStateDiagnostics.Reason(generation)} would change its rig profile or schedule identity. A re-run does not rewrite a configuration it cannot explain. Re-run it with the installer that last installed or re-ran it.");

    /// <summary>The manifest and the result record configuration identities no interrupted re-run leaves behind.</summary>
    public static InstallerException RecordsDisagree(
        Guid instanceId, string configurationPath, string manifestPath, string manifestSha256, string resultPath, string resultSha256)
        => InstallerException.RefusedFormatted(
            $"Instance {instanceId:D} cannot be re-run: its records disagree about its camera configuration, {CameraAgentStateDiagnostics.Reason(configurationPath)}: {CameraAgentStateDiagnostics.Reason(manifestPath)} records identity {CameraAgentStateDiagnostics.Reason(manifestSha256)} and {CameraAgentStateDiagnostics.Reason(resultPath)} records {CameraAgentStateDiagnostics.Reason(resultSha256)}, which no interrupted re-run leaves behind. A re-run does not rewrite a configuration it cannot explain. Restore the instance's records from a backup, or re-run it with the installer that last installed or re-ran it.");

    /// <summary>camera-module.json holds a configuration other than the recorded one or one an interrupted re-run wrote.</summary>
    public static InstallerException EditedConfiguration(Guid instanceId, string configurationPath, string fileSha256, string recordedSha256)
        => InstallerException.RefusedFormatted(
            $"Instance {instanceId:D} cannot be re-run: its camera configuration, {CameraAgentStateDiagnostics.Reason(configurationPath)}, has been edited: its canonical identity {CameraAgentStateDiagnostics.Reason(fileSha256)} is not the recorded {CameraAgentStateDiagnostics.Reason(recordedSha256)}. A re-run does not rewrite a configuration it cannot explain. Move the edited camera-module.json aside and re-run the same install, which writes the configuration the records describe; change the camera through its settings, such as camera.skyLighting, not this file.");

    /// <summary>camera-module.json cannot be read, or is not JSON.</summary>
    public static InstallerException UnreadableConfiguration(Guid instanceId, string configurationPath)
        => InstallerException.RefusedFormatted(
            $"Instance {instanceId:D} cannot be re-run: its camera configuration, {CameraAgentStateDiagnostics.Reason(configurationPath)}, cannot be read as the JSON configuration its records describe. A re-run does not rewrite a configuration it cannot explain. Move the edited camera-module.json aside and re-run the same install, which writes the configuration the records describe; change the camera through its settings, such as camera.skyLighting, not this file.");

    /// <summary>
    /// Adopting newer defaults needs an image that runs them, and the running image does not declare it. A re-run keeps the
    /// image, so the operator upgrades it first.
    /// </summary>
    public static InstallerException ImageCannotAdopt(
        Guid instanceId, string change, string required, string imageId, string declared, string program)
        => InstallerException.RefusedFormatted(
            $"Instance {instanceId:D} would adopt the installer's new configuration defaults ({CameraAgentStateDiagnostics.Reason(Plain(change))}), which need a CameraAgent image that declares {CameraAgentStateDiagnostics.Reason(CameraConfigurationDefaults.ImageLabel)} {CameraAgentStateDiagnostics.Reason(required)} or later; the running image {CameraAgentStateDiagnostics.Reason(imageId)} declares {CameraAgentStateDiagnostics.Reason(declared)}. A re-run keeps the image: upgrade the instance first with '{CameraAgentStateDiagnostics.Reason(program)} cameraagent upgrade --instance-id {instanceId:D}' to a release whose image declares it, then re-run.");
}
