using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// What an upgrade shows before it changes anything (install contract section 6): the upgrade notes of every release
/// after the installed one up to the target, taken from the target's release.json, and the refusal of a target older
/// than what is installed, since only <c>rollback</c> goes back.
/// </summary>
internal static class UpgradeNotes
{
    /// <summary>
    /// The release version of the image the instance runs, which an upgrade compares its target with, or
    /// <see langword="null"/> when nothing records one. Today the record is the release record an install or upgrade
    /// from a signed image release keeps beside the deployment state, withdrawn when the instance rolls back or moves to
    /// an image no release named; #1309 replaces it with a record of the release.json version it installs. Only a
    /// record of the running image, naming a SemVer version, counts.
    /// </summary>
    public static async Task<string?> InstalledVersionAsync(
        InstallationPaths paths,
        ImageInstallationIdentity installedImage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(installedImage);
        var path = Path.Combine(paths.DeploymentStateRoot, AcquiredImage.EvidenceFileName);
        if (!File.Exists(path))
        {
            return null;
        }
        CameraAgentImageReleaseEvidence? record;
        try
        {
            await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
            record = await JsonSerializer.DeserializeAsync(
                    stream, DeploymentJsonContext.Default.CameraAgentImageReleaseEvidence, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InstallerException("The retained image release record is invalid JSON.", exception);
        }
        return record?.ImageId == installedImage.ImageId && record.Distribution?.ReleaseVersion is { } version &&
               ProductVersion.IsValid(version)
            ? version
            : null;
    }

    /// <summary>
    /// The notes an upgrade from <paramref name="installedVersion"/> to <paramref name="target"/> shows, oldest first:
    /// each note newer than the installed version and no newer than the release the target belongs to, so a release
    /// candidate shows its release's notes, as RoofController's installer does. When the installed version is not
    /// recorded every note up to the target is shown, and the older-target check cannot be made.
    /// </summary>
    /// <exception cref="InstallerException">The target is older than the installed version.</exception>
    public static LifecycleUpgradeNotes Plan(string? installedVersion, ReleaseManifest target, Guid instanceId)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (installedVersion is not null && ProductVersion.IsOlder(target.Version, installedVersion))
        {
            throw new InstallerException(
                $"{installedVersion} is installed, which is newer than the upgrade target {target.Version}; an upgrade never goes back. " +
                $"To return to the previous image, run 'hvo-sky-install cameraagent rollback --instance-id {instanceId:D}'.");
        }
        var release = ProductVersion.Release(target.Version);
        var notes = target.UpgradeNotes
            .Where(note => (installedVersion is null || ProductVersion.IsOlder(installedVersion, note.Version)) &&
                           !ProductVersion.IsOlder(release, note.Version))
            .OrderBy(static note => note.Version, Comparer<string>.Create(ProductVersion.Compare))
            .ToArray();
        return new LifecycleUpgradeNotes(installedVersion, target.Version, notes);
    }

    /// <summary>
    /// Shows the notes before the upgrade starts: rendered on standard output, ahead of the result. With
    /// <c>--json</c> standard output carries only the one result, so they are written to standard error as one
    /// <see cref="LifecycleUpgradeNotesEvent"/> line instead, which a failed upgrade's error object follows.
    /// </summary>
    public static Task ShowAsync(LifecycleUpgradeNotes plan, bool json, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        return json ? error.WriteLineAsync(Event(plan)) : output.WriteLineAsync(Render(plan));
    }

    /// <summary>The notes as the one-line event a <c>--json</c> upgrade writes to standard error.</summary>
    public static string Event(LifecycleUpgradeNotes plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var line = new ArrayBufferWriter<byte>();
        // The context indents the documents it writes itself; a writer given to it keeps its own unindented form.
        using (var writer = new Utf8JsonWriter(line))
        {
            JsonSerializer.Serialize(
                writer,
                new LifecycleUpgradeNotesEvent(DeploymentSchemaVersions.LifecycleEvent, LifecycleUpgradeNotesEvent.Name, plan),
                DeploymentJsonContext.Default.LifecycleUpgradeNotesEvent);
        }
        return Encoding.UTF8.GetString(line.WrittenSpan);
    }

    /// <summary>The notes as an operator reads them before the upgrade starts.</summary>
    public static string Render(LifecycleUpgradeNotes plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var text = new StringBuilder();
        text.AppendLine(plan.InstalledVersion is null
            ? string.Create(CultureInfo.InvariantCulture,
                $"Upgrading to {plan.TargetVersion}. The installed version is not recorded, so the upgrade notes of every release up to {plan.TargetVersion} are shown.")
            : string.Create(CultureInfo.InvariantCulture, $"Upgrading from {plan.InstalledVersion} to {plan.TargetVersion}."));
        if (plan.Notes.Count == 0)
        {
            text.AppendLine("No upgrade notes apply.");
        }
        foreach (var note in plan.Notes)
        {
            text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Upgrade notes for {note.Version}:");
            // The same policy that admits release text escapes unsafe scalars in directly constructed notes too.
            foreach (var line in UpgradeNoteText.EscapeForTerminal(note.Text).Split('\n'))
            {
                text.AppendLine(line.Length == 0 ? line : "  " + line);
            }
        }
        return text.ToString();
    }
}
