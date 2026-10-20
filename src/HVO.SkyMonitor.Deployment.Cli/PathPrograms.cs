using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>Verified release programs, retained by image so rollback needs no download folder.</summary>
internal static partial class PathPrograms
{
    internal const string RecordName = "path-programs.json";
    internal const string SystemInstaller = "/usr/local/sbin/hvo-sky-install";
    internal const UnixFileMode ProgramMode = (UnixFileMode)0x1ed;
    internal static IProcessRunner PrivilegedRunner { get; set; } = new ProcessRunner();

    internal sealed record Locations(string UserBin, string SharedBin, string MachineInstaller)
    {
        internal static Locations For(InstallationPaths paths, string productionRoot)
            => paths.ProductRoot == productionRoot
                ? new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
                    "/usr/local/bin", SystemInstaller)
                : new(Path.Combine(paths.ProductRoot, "programs", "home", ".local", "bin"),
                    Path.Combine(paths.ProductRoot, "programs", "usr", "local", "bin"),
                    Path.Combine(paths.ProductRoot, "programs", "usr", "local", "sbin", "hvo-sky-install"));
    }

    internal static string Runtime => ReleaseAcquirer.HostImageArchitecture() == "amd64" ? "linux-x64" : "linux-arm64";

    internal static async Task VerifyReleaseAsync(string folder, VerifiedRelease release, CancellationToken cancellationToken)
    {
        foreach (var name in new[] { "hvo-sky", "hvo-sky-install" })
        {
            var asset = Asset(release, name);
            var path = Path.Combine(folder, asset.Name);
            if (await HashAsync(path, cancellationToken).ConfigureAwait(false) != asset.Sha256 || new FileInfo(path).Length != asset.Size)
                throw InstallerException.Refused($"{asset.Name} does not match its SHA-256 in release.json and SHA256SUMS.");
        }
    }

    private static ReleaseAsset Asset(VerifiedRelease release, string name)
        => release.Manifest.Assets.SingleOrDefault(asset => asset.Name == $"{name}-{Runtime}" &&
               asset.Kind == (name == "hvo-sky" ? "cli" : "installer") && asset.Platform == Runtime)
           ?? throw InstallerException.Refused($"Release {release.Manifest.Tag} has no {name} for {Runtime}.");

    internal static string SelectClientFolder(Locations locations, Func<string, bool> writable)
        => writable(locations.UserBin) ? locations.UserBin
            : writable(locations.SharedBin) ? locations.SharedBin
            : throw InstallerException.Refused("Neither ~/.local/bin nor /usr/local/bin is writable without sudo for hvo-sky.");

    private static bool Writable(string folder)
    {
        SafeFileSystem.EnsureSafeExistingAncestors(folder);
        var existing = folder;
        while (!Directory.Exists(existing))
        {
            if (NativeLinux.TryGetNodeIdentity(existing) is not null) return false;
            existing = Path.GetDirectoryName(existing)!;
        }
        return NativeLinux.CanWriteAndSearch(existing);
    }

    private static async Task<PathProgramSet?> DesiredAsync(
        InstallationPaths paths, string productionRoot, string? releaseFolder, string imageId, CancellationToken cancellationToken)
    {
        var locations = Locations.For(paths, productionRoot);
        if (releaseFolder is null)
        {
            var retained = await ReadSetAsync(ImageRecord(paths, imageId), locations, cancellationToken).ConfigureAwait(false);
            if (retained is not null && retained.ImageId != imageId)
                throw new InstallerException("The retained PATH program set belongs to another image.");
            return retained;
        }
        var release = await ReleaseAcquirer.ReadAsync(releaseFolder, cancellationToken).ConfigureAwait(false);
        await VerifyReleaseAsync(releaseFolder, release, cancellationToken).ConfigureAwait(false);
        var current = await ReadSetAsync(CurrentRecord(paths), locations, cancellationToken).ConfigureAwait(false);
        var client = current?.Programs.FirstOrDefault(static program => program.Name == "hvo-sky")?.Target
                     ?? Path.Combine(SelectClientFolder(locations, Writable), "hvo-sky");
        return new(imageId, release.Manifest.Version,
        [
            new("hvo-sky", client, Asset(release, "hvo-sky").Sha256),
            new("hvo-sky-install", Path.Combine(locations.UserBin, "hvo-sky-install"), Asset(release, "hvo-sky-install").Sha256),
            new("hvo-sky-install", locations.MachineInstaller, Asset(release, "hvo-sky-install").Sha256)
        ]);
    }

    internal static async Task SettleAsync(
        InstallationPaths paths, string productionRoot, string? releaseFolder, string imageId, CancellationToken cancellationToken)
    {
        var desired = await DesiredAsync(paths, productionRoot, releaseFolder, imageId, cancellationToken).ConfigureAwait(false);
        if (desired is null)
        {
            // A legacy image has no matching release client. Keep the durable maintenance installer, but do not leave
            // a newer runtime client claiming to match the image rollback restored.
            await RemoveAsync(paths, productionRoot, purge: false, cancellationToken).ConfigureAwait(false);
            return;
        }
        using var programLock = OperationLock.Acquire(Path.Combine(paths.OperationsRoot, "path-programs.lock"), cancellationToken: cancellationToken);
        SafeFileSystem.CreateOwnerDirectory(Path.Combine(paths.DeploymentStateRoot, "programs"));
        foreach (var program in desired.Programs.DistinctBy(static program => program.Sha256))
        {
            var cached = Cached(paths, program.Sha256);
            if (await HashAsync(cached, cancellationToken).ConfigureAwait(false) != program.Sha256)
            {
                if (releaseFolder is null)
                    throw new InstallerException($"The retained {program.Name} for rollback is missing or does not match its SHA-256.");
                await WriteVerifiedAsync(Path.Combine(releaseFolder, $"{program.Name}-{Runtime}"), cached,
                    program.Sha256, executable: false, cancellationToken).ConfigureAwait(false);
            }
        }
        // Save the image-bound set before publishing the first file, so every partial publication is repeatable.
        await WriteSetAsync(ImageRecord(paths, imageId), desired, cancellationToken).ConfigureAwait(false);
        var registry = await ReadRegistryAsync(paths, cancellationToken).ConfigureAwait(false);
        var published = new List<PathProgram>();
        foreach (var program in desired.Programs)
        {
            if (program.Target == SystemInstaller)
                await PrivilegedPathInstaller.ApplyAsync(paths, program.Sha256, remove: false, null, cancellationToken).ConfigureAwait(false);
            else
                await PublishAsync(Cached(paths, program.Sha256), program.Target, program.Sha256, cancellationToken).ConfigureAwait(false);
            var installed = program with { PreviousSha256 = await HashAsync(program.Target + ".previous", cancellationToken).ConfigureAwait(false) };
            published.Add(installed);
            registry.RemoveAll(record => record.Path == installed.Target);
            registry.Add(new(installed.Name, installed.Target, desired.Version, installed.Sha256, installed.PreviousSha256));
            await SafeFileSystem.WriteJsonAtomicAsync(RegistryPath(paths), new PathProgramRegistry(registry),
                DeploymentJsonContext.Default.PathProgramRegistry, cancellationToken).ConfigureAwait(false);
        }
        await WriteSetAsync(CurrentRecord(paths), desired with { Programs = published }, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<DeploymentPlanItem>> PlanAsync(
        InstallationPaths paths, string productionRoot, string? releaseFolder, string imageId,
        LifecycleOperationKind? operation, CancellationToken cancellationToken)
    {
        var locations = Locations.For(paths, productionRoot);
        var removing = operation is LifecycleOperationKind.Uninstall or LifecycleOperationKind.Purge;
        var desired = removing
            ? await ReadSetAsync(CurrentRecord(paths), locations, cancellationToken).ConfigureAwait(false)
            : await DesiredAsync(paths, productionRoot, releaseFolder, imageId, cancellationToken).ConfigureAwait(false);
        if (desired is null && operation == LifecycleOperationKind.Rollback)
        {
            desired = await ReadSetAsync(CurrentRecord(paths), locations, cancellationToken).ConfigureAwait(false);
            removing = true;
            operation = LifecycleOperationKind.Uninstall;
        }
        if (desired is null) return [];
        var registry = await ReadRegistryAsync(paths, cancellationToken).ConfigureAwait(false);
        var items = new List<DeploymentPlanItem>();
        foreach (var owned in desired.Programs)
        {
            var latest = removing ? registry.FirstOrDefault(record => record.Path == owned.Target) : null;
            var program = latest is null ? owned : owned with { Sha256 = latest.Sha256, PreviousSha256 = latest.PreviousSha256 };
            var retained = removing && (operation == LifecycleOperationKind.Uninstall && program.Name == "hvo-sky-install" ||
                await SharedAsync(paths, program.Target, program.Name == "hvo-sky-install", cancellationToken).ConfigureAwait(false));
            var hash = await HashAsync(program.Target, cancellationToken).ConfigureAwait(false);
            var previous = await HashAsync(program.Target + ".previous", cancellationToken).ConfigureAwait(false);
            if (removing && !retained && (hash is not null && hash != program.Sha256 ||
                    previous is not null && program.PreviousSha256 is not null && previous != program.PreviousSha256))
                throw InstallerException.Refused($"Program '{program.Target}' differs from the installed record; it was preserved. Move it aside before retrying.");
            var action = removing
                ? retained || hash is null ? DeploymentPlanAction.Unchanged : DeploymentPlanAction.Remove
                : hash is null ? DeploymentPlanAction.Create
                : hash == program.Sha256 && File.GetUnixFileMode(program.Target) == ProgramMode
                    ? DeploymentPlanAction.Unchanged : DeploymentPlanAction.Change;
            items.Add(new(action, "program", program.Target,
                retained ? "retained for reinstall or another instance" : removing ? "recorded program and its .previous" :
                $"{program.Name}, release {desired.Version}; SHA256SUMS verified, 0755; replaced copy kept as .previous"));
        }
        return items;
    }

    internal static async Task RemoveAsync(
        InstallationPaths paths, string productionRoot, bool purge, CancellationToken cancellationToken, string? retainedRecord = null)
    {
        var current = await ReadSetAsync(retainedRecord ?? CurrentRecord(paths), Locations.For(paths, productionRoot), cancellationToken).ConfigureAwait(false);
        if (current is null) return;
        using var programLock = OperationLock.Acquire(Path.Combine(paths.OperationsRoot, "path-programs.lock"), cancellationToken: cancellationToken);
        var registry = await ReadRegistryAsync(paths, cancellationToken).ConfigureAwait(false);
        foreach (var owned in current.Programs)
        {
            var latest = registry.FirstOrDefault(record => record.Path == owned.Target);
            var program = latest is null ? owned : owned with { Sha256 = latest.Sha256, PreviousSha256 = latest.PreviousSha256 };
            if (!purge && program.Name == "hvo-sky-install" ||
                await SharedAsync(paths, program.Target, program.Name == "hvo-sky-install", cancellationToken).ConfigureAwait(false)) continue;
            if (program.Target == SystemInstaller)
                await PrivilegedPathInstaller.ApplyAsync(paths, program.Sha256, remove: true, program.PreviousSha256, cancellationToken).ConfigureAwait(false);
            else
            {
                await RemoveMatchingAsync(program.Target, program.Sha256, cancellationToken).ConfigureAwait(false);
                if (program.PreviousSha256 is { } previous)
                    await RemoveMatchingAsync(program.Target + ".previous", previous, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static string PurgeRecord(InstallationPaths paths, Guid operationId)
        => Path.Combine(paths.OperationsRoot, $"purge-{operationId:D}.programs.json");

    internal static async Task RetainForPurgeAsync(
        InstallationPaths paths, string productionRoot, Guid operationId, CancellationToken cancellationToken)
    {
        var current = await ReadSetAsync(CurrentRecord(paths), Locations.For(paths, productionRoot), cancellationToken).ConfigureAwait(false);
        if (current is not null)
            await WriteSetAsync(PurgeRecord(paths, operationId), current, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> SharedAsync(InstallationPaths paths, string target, bool includeUninstalled, CancellationToken cancellationToken)
    {
        foreach (var root in Directory.EnumerateDirectories(Path.Combine(paths.ProductRoot, "cameraagents")))
        {
            if (root == paths.InstanceRoot || !Guid.TryParseExact(Path.GetFileName(root), "D", out var id)) continue;
            var other = InstallationPaths.Create(paths.ProductRoot, id, ProductionCatalog.DefaultCatalogId);
            if (!File.Exists(other.ManifestPath)) continue;
            var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(other.ManifestPath, cancellationToken).ConfigureAwait(false);
            if (!includeUninstalled && manifest.LifecycleCondition != InstanceLifecycleCondition.Installed) continue;
            var record = await ReadSetAsync(CurrentRecord(other), locations: null, cancellationToken).ConfigureAwait(false);
            if (record?.Programs.Any(program => program.Target == target) == true) return true;
        }
        return false;
    }

    internal static async Task<IReadOnlyList<PathProgramRecord>> ProjectAsync(InstallationPaths paths, CancellationToken cancellationToken)
    {
        var current = await ReadSetAsync(CurrentRecord(paths), locations: null, cancellationToken).ConfigureAwait(false);
        if (current is null) return [];
        var registry = await ReadRegistryAsync(paths, cancellationToken).ConfigureAwait(false);
        var result = new List<PathProgramRecord>();
        foreach (var program in current.Programs)
        {
            var installed = registry.FirstOrDefault(record => record.Path == program.Target)
                ?? new(program.Name, program.Target, current.Version, program.Sha256, program.PreviousSha256);
            if (await HashAsync(program.Target, cancellationToken).ConfigureAwait(false) == installed.Sha256)
                result.Add(installed);
        }
        return result;
    }

    internal static async Task VerifyRollbackAsync(InstallationPaths paths, string productionRoot, string imageId, CancellationToken cancellationToken)
    {
        var desired = await DesiredAsync(paths, productionRoot, null, imageId, cancellationToken).ConfigureAwait(false);
        if (desired is null) return;
        foreach (var program in desired.Programs)
            if (await HashAsync(Cached(paths, program.Sha256), cancellationToken).ConfigureAwait(false) != program.Sha256)
                throw InstallerException.Refused($"The retained {program.Name} for rollback does not match its SHA-256.");
    }

    private static string RegistryPath(InstallationPaths paths) => Path.Combine(paths.OperationsRoot, RecordName);

    private static async Task<List<PathProgramRecord>> ReadRegistryAsync(InstallationPaths paths, CancellationToken cancellationToken)
    {
        var path = RegistryPath(paths);
        if (!File.Exists(path)) return [];
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        var registry = await JsonSerializer.DeserializeAsync(stream, DeploymentJsonContext.Default.PathProgramRegistry,
            cancellationToken).ConfigureAwait(false) ?? throw new InstallerException("The PATH program registry is empty.");
        if (registry.SchemaVersion != 1 || registry.Programs is null || registry.Programs.Any(program =>
                !ReleaseManifest.Sha256Pattern().IsMatch(program.Sha256) ||
                program.PreviousSha256 is { } previous && !ReleaseManifest.Sha256Pattern().IsMatch(previous)))
            throw new InstallerException("The PATH program registry is invalid.");
        return [.. registry.Programs];
    }

    internal static string Cached(InstallationPaths paths, string sha256) => Path.Combine(paths.DeploymentStateRoot, "programs", sha256);
    private static string CurrentRecord(InstallationPaths paths) => Path.Combine(paths.DeploymentStateRoot, RecordName);
    private static string ImageRecord(InstallationPaths paths, string imageId)
        => imageId.StartsWith("sha256:", StringComparison.Ordinal) && ReleaseManifest.Sha256Pattern().IsMatch(imageId[7..])
            ? Path.Combine(paths.DeploymentStateRoot, $"path-programs-{imageId[7..]}.json")
            : throw new InstallerException("PATH program record requires an immutable image ID.");

    private static Task WriteSetAsync(string path, PathProgramSet set, CancellationToken cancellationToken)
        => SafeFileSystem.WriteJsonAtomicAsync(path, set, DeploymentJsonContext.Default.PathProgramSet, cancellationToken);

    private static async Task<PathProgramSet?> ReadSetAsync(string path, Locations? locations, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        var set = await JsonSerializer.DeserializeAsync(stream, DeploymentJsonContext.Default.PathProgramSet, cancellationToken).ConfigureAwait(false);
        if (set is null || set.SchemaVersion != 1 || !ProductVersion.IsValid(set.Version) || set.Programs is null || set.Programs.Count != 3 ||
            set.Programs.Select(static program => program.Target).Distinct(StringComparer.Ordinal).Count() != 3 ||
            set.Programs.Count(static program => program.Name == "hvo-sky") != 1 ||
            set.Programs.Any(program => program.Name is not ("hvo-sky" or "hvo-sky-install") ||
                !ReleaseManifest.Sha256Pattern().IsMatch(program.Sha256) ||
                program.PreviousSha256 is { } previous && !ReleaseManifest.Sha256Pattern().IsMatch(previous) ||
                !Path.IsPathFullyQualified(program.Target) || Path.GetFullPath(program.Target) != program.Target ||
                locations is not null && !Allowed(program, locations)))
            throw new InstallerException("The retained PATH program record is invalid.");
        return set;
    }

    private static bool Allowed(PathProgram program, Locations locations)
        => program.Name == "hvo-sky"
            ? program.Target == Path.Combine(locations.UserBin, program.Name) || program.Target == Path.Combine(locations.SharedBin, program.Name)
            : program.Target == Path.Combine(locations.UserBin, program.Name) || program.Target == locations.MachineInstaller;
}

internal sealed record PathProgram(string Name, string Target, string Sha256, string? PreviousSha256 = null);
internal sealed record PathProgramSet(string ImageId, string Version, IReadOnlyList<PathProgram> Programs, int SchemaVersion = 1);
internal sealed record PathProgramRecord(string Name, string Path, string Version, string Sha256, string? PreviousSha256);

internal sealed record PathProgramRegistry(IReadOnlyList<PathProgramRecord> Programs, int SchemaVersion = 1);
