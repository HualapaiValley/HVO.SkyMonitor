using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

// Separate from schema-1 install.json: released installers disallow unmapped JSON members.
internal sealed record InstallProgramInstance(Guid InstanceId, IReadOnlyList<PathProgramRecord>? Programs);
internal sealed record InstallProgramRecord(int SchemaVersion, string Product, string ProductRoot, IReadOnlyList<InstallProgramInstance> Instances);

internal static partial class InstallRecordStore
{
    internal const string ProgramsFileName = "install-programs.json";

    internal static string ProgramsPath(InstallRecordLocation location)
        => Path.Combine(Path.GetDirectoryName(location.Path)!, ProgramsFileName);

    internal static async Task<bool> ProgramsDifferAsync(
        InstallRecordLocation location, InstallationPaths paths, CancellationToken cancellationToken)
    {
        var desired = new InstallProgramInstance(Guid.Parse(Path.GetFileName(paths.InstanceRoot)),
            await PathPrograms.ProjectAsync(paths, cancellationToken).ConfigureAwait(false));
        var record = await ReadProgramsAsync(location, cancellationToken).ConfigureAwait(false);
        return !ProgramsMatch(record, desired);
    }

    private static bool ProgramsMatch(InstallProgramRecord? record, InstallProgramInstance change)
    {
        var existing = record?.Instances.FirstOrDefault(instance => instance.InstanceId == change.InstanceId);
        return change.Programs is null || change.Programs.Count == 0
            ? existing is null : existing?.Programs?.SequenceEqual(change.Programs) == true;
    }

    private static InstallProgramRecord ApplyPrograms(
        InstallRecordLocation location, InstallProgramRecord? record, InstallProgramInstance change)
    {
        var instances = (record?.Instances ?? []).Where(instance => instance.InstanceId != change.InstanceId).ToList();
        if (change.Programs is { Count: > 0 }) instances.Add(change);
        return new(1, InstallRecord.ProductName, location.ProductRoot, [.. instances.OrderBy(instance => instance.InstanceId)]);
    }

    private static async Task<DeploymentPlanAction> SettleProgramsAsync(
        InstallRecordLocation location, InstallProgramInstance change, CancellationToken cancellationToken)
    {
        var record = await ReadProgramsAsync(location, cancellationToken).ConfigureAwait(false);
        if (ProgramsMatch(record, change)) return DeploymentPlanAction.Unchanged;
        var action = change.Programs is null || change.Programs.Count == 0 ? DeploymentPlanAction.Remove
            : record?.Instances.Any(instance => instance.InstanceId == change.InstanceId) == true
                ? DeploymentPlanAction.Change : DeploymentPlanAction.Create;
        if (location.Trusted is not { } trusted)
        {
            await SafeFileSystem.WriteJsonAtomicAsync(ProgramsPath(location), ApplyPrograms(location, record, change),
                InstallRecordJsonContext.Default.InstallProgramRecord, cancellationToken).ConfigureAwait(false);
        }
        else if (!trusted.IsProduction || NativeLinux.getuid() == 0)
        {
            ApplyTrusted(trusted, location.ProductRoot, new(null, null, change));
        }
        else
        {
            var result = await PrivilegedRunner.RunAsync("sudo", HelperArguments(location.ProductRoot, new(null, null, change)),
                cancellationToken).ConfigureAwait(false);
            PrivilegedHelperErrors.ThrowIfFailed(result, "The PATH program sidecar could not be updated");
            if (!ProgramsMatch(await ReadProgramsAsync(location, cancellationToken).ConfigureAwait(false), change))
                throw new InstallerException("The PATH program sidecar does not hold the change after the privileged update.");
        }
        return action;
    }

    private static string ApplyProgramsTrusted(InstallRecordLocation location, InstallProgramInstance change)
    {
        // ApplyTrusted already holds the host-policy lock and has rechecked its product root.
        var record = ReadProgramsAsync(location, CancellationToken.None).GetAwaiter().GetResult();
        if (ProgramsMatch(record, change)) return HostPolicyChange.Unchanged;
        var next = ApplyPrograms(location, record, change);
        HostProductRootPolicy.WriteTrustedFile(location.Trusted!, ProgramsPath(location),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(next, InstallRecordJsonContext.Default.InstallProgramRecord) + "\n"),
            "The PATH program sidecar");
        return HostPolicyChange.Updated;
    }

    internal static async Task<InstallProgramRecord?> ReadProgramsAsync(
        InstallRecordLocation location, CancellationToken cancellationToken)
    {
        var path = ProgramsPath(location);
        byte[]? bytes;
        if (location.Trusted is { } trusted)
            bytes = HostProductRootPolicy.TryReadTrustedFile(trusted, path, MaximumRecordBytes, "PATH program sidecar");
        else
        {
            if (!File.Exists(path)) return null;
            await using var input = SafeFileSystem.OpenOwnerFileRead(path);
            if (input.Length > MaximumRecordBytes) throw new InstallerException("The PATH program sidecar is too large.");
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }
        if (bytes is null) return null;
        try
        {
            var record = JsonSerializer.Deserialize(bytes, InstallRecordJsonContext.Default.InstallProgramRecord);
            if (record is null || record.SchemaVersion != 1 || record.Product != InstallRecord.ProductName ||
                record.ProductRoot != location.ProductRoot || record.Instances is null ||
                record.Instances.Any(instance => instance is null) ||
                record.Instances.Select(instance => instance.InstanceId).Distinct().Count() != record.Instances.Count ||
                record.Instances.Any(instance => ProgramEntryProblem(instance) is not null || instance.Programs is null))
                throw new InstallerException("The PATH program sidecar is invalid; it was preserved.");
            return record;
        }
        catch (JsonException exception)
        {
            throw new InstallerException("The PATH program sidecar is invalid; it was preserved.", exception);
        }
    }

    private static string? ProgramEntryProblem(InstallProgramInstance? entry)
        => entry is null || entry.InstanceId == Guid.Empty ||
           entry.Programs is { } programs && (programs.Count > 3 || programs.Any(program =>
               program is null || program.Name is not ("hvo-sky" or "hvo-sky-install") || !Path.IsPathFullyQualified(program.Path) || program.Path.Any(char.IsControl) ||
               Path.GetFullPath(program.Path) != program.Path || !ProductVersion.IsValid(program.Version) || program.Sha256 is null ||
               !ReleaseManifest.Sha256Pattern().IsMatch(program.Sha256) || program.PendingSha256 is not null ||
               program.PreviousSha256 is { } previous && !ReleaseManifest.Sha256Pattern().IsMatch(previous)))
            ? "Invalid PATH program sidecar instance." : null;
}
