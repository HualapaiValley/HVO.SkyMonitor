using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Where the host product-root policy is read and whom every path it names must belong to. Production reads the
/// fixed root-owned file below <c>/</c>. An isolated test names a policy below a private directory it owns and
/// carries that location on the request it parses, exactly as test-root admission is carried, so no test redirects
/// the policy by mutating process state. The privileged helpers never accept a location: they always read
/// <see cref="Production"/>.
/// </summary>
/// <param name="PolicyPath">The policy file.</param>
/// <param name="TrustRoot">The directory every validated path is walked from; <c>/</c> in production.</param>
/// <param name="OwnerUid">The owner every validated path must have; root in production.</param>
/// <param name="OwnerGid">The group every validated path must have; root in production.</param>
/// <param name="DefaultProductRoot">The production product root when no policy file exists.</param>
/// <param name="LockPath">
/// The owner-only lock that serializes a policy change with privileged preparation; in production a root-only file in
/// <c>/run</c> that no other user can open, so none can hold it.
/// </param>
internal sealed record HostPolicyLocation(
    string PolicyPath,
    string TrustRoot,
    uint OwnerUid,
    uint OwnerGid,
    string DefaultProductRoot = InstallRequest.DefaultProductRoot,
    string LockPath = HostPolicyLocation.ProductionLockPath)
{
    public const string ProductionPolicyPath = "/etc/hvo/skymonitor/host.json";

    public const string ProductionLockPath = "/run/hvo-skymonitor-host-policy.lock";

    public static HostPolicyLocation Production { get; } = new(ProductionPolicyPath, "/", 0, 0);

    public bool IsProduction => Equals(Production);

    internal string OwnerDescription => IsProduction
        ? "root:root"
        : string.Create(CultureInfo.InvariantCulture, $"{OwnerUid}:{OwnerGid}");
}

/// <summary>
/// The production product root one invocation resolved: the root a valid host policy names, or
/// <see cref="InstallRequest.DefaultProductRoot"/> when no policy file exists.
/// </summary>
internal sealed record ProductionProductRoot(string Path, bool FromPolicy)
{
    public static ProductionProductRoot Default { get; } = new(InstallRequest.DefaultProductRoot, FromPolicy: false);
}

/// <summary>The outcome of writing the host policy.</summary>
internal sealed record HostPolicyChange(string Outcome, string PreviousProductRoot, string ProductRoot)
{
    public const string Updated = "updated";
    public const string Unchanged = "unchanged";
}

/// <param name="ProductRoot">The production product root.</param>
/// <param name="MountPoint">
/// The mount point (a pathname) of the filesystem that held the product root when the policy was written. When present,
/// that path must currently be a mount point and the root must resolve through the mount currently visible there, so a
/// command refuses when nothing is mounted at the path. It records no filesystem identity: a stacked or replacement
/// mount at the same path is accepted, so operators verify the backing filesystem themselves.
/// </param>
internal sealed record HostPolicyDocument(string? ProductRoot, string? MountPoint = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(HostPolicyDocument))]
internal sealed partial class HostPolicyJsonContext : JsonSerializerContext;

/// <summary>
/// The root-owned host policy <c>/etc/hvo/skymonitor/host.json</c> (<c>{"productRoot": "/srv/data/hvo-skymonitor"}</c>)
/// that relocates the production product root to another filesystem without a bind mount. Its absence leaves the
/// default <c>/var/lib/hvo/skymonitor</c> in force. When present it is validated strictly and fails closed: the file
/// and every directory from <c>/</c> to it, and every component of the product root it names, must be
/// <c>root:root</c> and not writable by group or other users; no component may be a symbolic link; the root must be an
/// absolute normalized path of portable segments that already exists; nothing may be mounted beneath it, so the whole
/// root lives on one mount; and when the policy records the mount point that held the root, that path must currently be
/// a mount point the root resolves through (a pathname check, not filesystem authentication). The command line reads it once and carries the answer on the request, and each narrow
/// <c>sudo</c> helper reads and validates it again for itself instead of trusting its arguments. A policy change and a
/// privileged preparation hold the same root-only lock, so an instance can never be created under a root while the
/// policy moves away from it.
/// </summary>
internal static class HostProductRootPolicy
{
    internal const int MaximumPolicyBytes = 4096;
    internal const int MaximumProductRootLength = 512;
    internal const string MountInfoPath = "/proc/self/mountinfo";
    private const uint DirectoryType = 0x4000;
    private const uint RegularFileType = 0x8000;
    private const uint SymbolicLinkType = 0xA000;
    private const UnixFileMode SharedDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode PolicyFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode GroupOrOtherAccess =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Reads the policy at <paramref name="location"/> once. An absent file (or an absent directory on its path)
    /// resolves the default product root; any other condition that is not a valid policy is refused.
    /// </summary>
    public static ProductionProductRoot Resolve(HostPolicyLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var document = TryReadPolicy(location);
        if (document is null)
        {
            return location.IsProduction
                ? ProductionProductRoot.Default
                : new ProductionProductRoot(location.DefaultProductRoot, FromPolicy: false);
        }
        var productRoot = document.ProductRoot;
        if ((ProductRootTextError(productRoot) ?? MountPointTextError(document.MountPoint, productRoot!)) is { } error)
        {
            throw new InstallerException($"Host policy '{location.PolicyPath}' is invalid: {error}");
        }
        ValidateProductRoot(location, productRoot!, document.MountPoint);
        return new ProductionProductRoot(productRoot!, FromPolicy: true);
    }

    /// <summary>
    /// Returns why <paramref name="value"/> cannot be a policy product root, or <see langword="null"/> when its text is
    /// acceptable. Only the text is judged here; ownership, links, existence, and mounts are judged on the host.
    /// </summary>
    public static string? ProductRootTextError(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "productRoot is required.";
        }
        if (value.Length > MaximumProductRootLength)
        {
            return $"productRoot must be at most {MaximumProductRootLength} characters.";
        }
        if (value.Any(char.IsControl))
        {
            return "productRoot must not contain control characters.";
        }
        if (!Path.IsPathFullyQualified(value) || value == "/" || value.EndsWith('/') ||
            value.Contains("//", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFullPath(value), value, StringComparison.Ordinal))
        {
            return "productRoot must be an absolute normalized path other than /.";
        }
        if (!value[1..].Split('/').All(IsPortableSegment))
        {
            return "productRoot segments may contain only ASCII letters, digits, '.', '_' and '-', and must not start with '-'.";
        }
        return null;
    }

    /// <summary>
    /// A segment the generated Compose environment file and bind-mount syntax carry literally. Compose interpolates
    /// <c>$</c>, treats <c> #</c> as a comment, and splits a volume on <c>:</c>, so a root naming any of them, whitespace,
    /// quotes, or anything outside this set could become a different bind source or an unusable deployment.
    /// </summary>
    private static bool IsPortableSegment(string segment)
        => segment.Length > 0 && segment[0] != '-' &&
           segment.All(static character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static string? MountPointTextError(string? mountPoint, string productRoot)
    {
        if (mountPoint is null)
        {
            return null;
        }
        if (mountPoint != "/" && ProductRootTextError(mountPoint) is not null)
        {
            return "mountPoint must be an absolute normalized path of portable segments.";
        }
        return mountPoint == "/" || productRoot == mountPoint || productRoot.StartsWith(mountPoint + "/", StringComparison.Ordinal)
            ? null
            : "mountPoint must be the product root or one of its ancestors.";
    }

    /// <summary>
    /// Requires an existing product root whose every component from the trust root is a directory, not a link, owned
    /// by the policy owner, and not writable by group or other users, with nothing mounted beneath it.
    /// </summary>
    public static void ValidateProductRoot(HostPolicyLocation location, string productRoot, string? mountPoint = null)
        => _ = AuthenticateProductRoot(location, productRoot, mountPoint);

    /// <summary>Validates the product root and returns the ID of the mount it resolves through.</summary>
    private static ulong AuthenticateProductRoot(HostPolicyLocation location, string productRoot, string? mountPoint)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(productRoot);
        var components = ComponentsBelowTrustRoot(location, productRoot, "The host policy product root");
        using var directory = WalkTrustedDirectories(location, components, productRoot, createMissing: false, missingIsAbsence: false)
            ?? throw new InstallerException($"The host policy product root '{productRoot}' does not exist.");
        var mountId = NativeLinux.GetOpenNodeIdentity(directory, productRoot).MountId;
        var mountInfo = ReadMountInfo();
        EnsureSingleMount(productRoot, mountInfo);
        if (mountPoint is not null)
        {
            EnsureMountedOn(productRoot, mountId, mountPoint, mountInfo);
        }
        return mountId;
    }

    /// <summary>
    /// Requires <paramref name="mountPoint"/> to be a current mount point and the product root to resolve through the
    /// mount currently visible there (equal mount IDs). When nothing is mounted at the path, the mount-point directory,
    /// and possibly a same-named directory beneath it, remain on the parent filesystem; this refuses that instead of
    /// writing beneath the empty mount point. It checks the pathname only: an underlying stacked mount or a different
    /// filesystem mounted at the same path is accepted.
    /// </summary>
    internal static void EnsureMountedOn(string productRoot, ulong rootMountId, string mountPoint, string mountInfo)
    {
        ArgumentNullException.ThrowIfNull(mountInfo);
        var mounts = ParseMountInfo(mountInfo).Where(mount => mount.MountPoint == mountPoint).ToArray();
        if (mounts.Length == 0)
        {
            throw new InstallerException(
                $"The host policy product root '{productRoot}' requires its filesystem mounted at '{mountPoint}', but nothing is mounted there; mount it before using SkyMonitor.");
        }
        if (!mounts.Any(mount => mount.MountId == rootMountId))
        {
            throw new InstallerException(
                $"The host policy product root '{productRoot}' is not on the filesystem mounted at '{mountPoint}'.");
        }
    }

    /// <summary>The mount point of the mount with <paramref name="mountId"/>, as the kernel reports it in mountinfo.</summary>
    internal static string MountPointOf(ulong mountId, string mountInfo)
    {
        foreach (var mount in ParseMountInfo(mountInfo))
        {
            if (mount.MountId == mountId)
            {
                return mount.MountPoint;
            }
        }
        throw new InstallerException("The mount that holds the product root is not in the host mount table.");
    }

    /// <summary>
    /// Refuses a product root with anything mounted beneath it. A mount exactly at the root is accepted: the whole
    /// root then lives on that one mount, which is what the purge namespace and every rename beneath it require.
    /// </summary>
    internal static void EnsureSingleMount(string productRoot, string mountInfo)
    {
        ArgumentNullException.ThrowIfNull(productRoot);
        ArgumentNullException.ThrowIfNull(mountInfo);
        var prefix = productRoot + "/";
        foreach (var (_, mountPoint) in ParseMountInfo(mountInfo))
        {
            if (mountPoint.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InstallerException(
                    $"The host policy product root '{productRoot}' must live on one mount, but '{mountPoint}' is mounted beneath it.");
            }
        }
    }

    private static List<(ulong MountId, string MountPoint)> ParseMountInfo(string mountInfo)
    {
        var mounts = new List<(ulong MountId, string MountPoint)>();
        foreach (var line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ');
            if (fields.Length < 5 ||
                !ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var mountId))
            {
                throw new InstallerException("The host mount table could not be read.");
            }
            mounts.Add((mountId, UnescapeMountPath(fields[4])));
        }
        return mounts;
    }

    /// <summary>
    /// Writes the policy so it names <paramref name="requestedRoot"/>. Refuses to move a production root that already
    /// holds CameraAgent or LogicHost instances, creates only the final product-root directory when its validated
    /// parent exists, and publishes the file atomically. The caller must already hold the policy owner's authority.
    /// </summary>
    public static HostPolicyChange Apply(HostPolicyLocation location, string requestedRoot)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (ProductRootTextError(requestedRoot) is { } error)
        {
            throw new InstallerException($"The requested product root is invalid: {error}");
        }
        // Resolution, the empty-root check, and publication happen under the lock every privileged preparation holds
        // while it resolves the policy and creates an instance, so no instance can appear under the old root between
        // the check and the moment the policy stops naming it.
        using var policyLock = AcquireLock(location);
        var current = Resolve(location);
        if (!string.Equals(current.Path, requestedRoot, StringComparison.Ordinal))
        {
            EnsureHoldsNoInstances(current.Path);
        }

        var components = ComponentsBelowTrustRoot(location, requestedRoot, "The requested product root");
        using (WalkTrustedDirectories(location, components, requestedRoot, createMissing: true, missingIsAbsence: false, createOnlyLast: true))
        {
        }
        var mountPoint = MountPointOf(AuthenticateProductRoot(location, requestedRoot, mountPoint: null), ReadMountInfo());
        if (current.FromPolicy && string.Equals(current.Path, requestedRoot, StringComparison.Ordinal) &&
            string.Equals(TryReadPolicy(location)?.MountPoint, mountPoint, StringComparison.Ordinal))
        {
            return new HostPolicyChange(HostPolicyChange.Unchanged, current.Path, requestedRoot);
        }
        WritePolicy(location, requestedRoot, mountPoint);

        var written = Resolve(location);
        if (!written.FromPolicy || !string.Equals(written.Path, requestedRoot, StringComparison.Ordinal) ||
            !string.Equals(TryReadPolicy(location)?.MountPoint, mountPoint, StringComparison.Ordinal))
        {
            throw new InstallerException("The written host policy does not resolve the requested product root.");
        }
        return new HostPolicyChange(HostPolicyChange.Updated, current.Path, requestedRoot);
    }

    /// <summary>
    /// Takes the owner-only host-policy lock, waiting up to <paramref name="timeout"/>. The lock file must be a regular
    /// file with one link, owned by the policy owner, and inaccessible to group and other users, so an unprivileged user
    /// can neither hold it nor replace it. It is an <c>flock</c> on its own open file description, so it also excludes
    /// a second holder in the same process.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned lock owns the authenticated handle; it is disposed on every failure path.")]
    public static IDisposable AcquireLock(HostPolicyLocation location, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        var components = ComponentsBelowTrustRoot(location, location.LockPath, "The host policy lock");
        using var parent = WalkTrustedDirectories(
                location, components[..^1], location.LockPath, createMissing: false, missingIsAbsence: false)
            ?? throw new InstallerException($"The host policy lock directory for '{location.LockPath}' does not exist.");
        var handle = NativeLinux.OpenLockFileAt(parent, components[^1], location.LockPath);
        try
        {
            var identity = NativeLinux.GetOpenNodeIdentity(handle, location.LockPath);
            if (identity.Type != RegularFileType || identity.LinkCount != 1 ||
                identity.Uid != location.OwnerUid || identity.Gid != location.OwnerGid ||
                (identity.Mode & GroupOrOtherAccess) != 0)
            {
                throw new InstallerException(
                    $"Host policy lock '{location.LockPath}' must be a regular file owned by {location.OwnerDescription} with mode 0600.");
            }
            var deadline = DateTimeOffset.UtcNow + (timeout ?? DefaultLockTimeout);
            while (!NativeLinux.TryLockExclusive(handle, location.LockPath))
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new InstallerException(
                        $"Host policy lock '{location.LockPath}' is busy; retry once the other installation or host policy change completes.");
                }
                Thread.Sleep(TimeSpan.FromMilliseconds(50));
            }
            return new HostPolicyLock(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private sealed class HostPolicyLock(SafeFileHandle handle) : IDisposable
    {
        public void Dispose()
        {
            NativeLinux.Unlock(handle);
            handle.Dispose();
        }
    }

    /// <summary>
    /// Refuses when <paramref name="productRoot"/> holds any CameraAgent or LogicHost instance entry, including a purge
    /// tombstone. Moving such a root would orphan every instance, whose manifests and Compose files name it.
    /// </summary>
    internal static void EnsureHoldsNoInstances(string productRoot)
    {
        foreach (var name in new[] { "cameraagents", "logichosts" })
        {
            var path = Path.Combine(productRoot, name);
            var identity = NativeLinux.TryGetNodeIdentity(path);
            if (identity is null)
            {
                continue;
            }
            if (identity.Type != DirectoryType || Directory.EnumerateFileSystemEntries(path).Any())
            {
                throw new InstallerException(
                    $"The current product root '{productRoot}' already holds instances under '{path}'; the product root cannot be moved while instances depend on it.");
            }
        }
    }

    private static HostPolicyDocument? TryReadPolicy(HostPolicyLocation location)
    {
        var policyPath = location.PolicyPath;
        if (!Path.IsPathFullyQualified(policyPath) || !string.Equals(Path.GetFullPath(policyPath), policyPath, StringComparison.Ordinal))
        {
            throw new InstallerException($"Host policy path '{policyPath}' must be absolute and normalized.");
        }
        var components = ComponentsBelowTrustRoot(location, policyPath, "The host policy");
        using var parent = WalkTrustedDirectories(location, components[..^1], Path.GetDirectoryName(policyPath)!, createMissing: false, missingIsAbsence: true);
        if (parent is null)
        {
            return null;
        }
        var name = components[^1];
        var observed = NativeLinux.TryGetNodeIdentityAt(parent, name);
        if (observed is null)
        {
            return null;
        }
        if (observed.Type == SymbolicLinkType)
        {
            throw new InstallerException($"Host policy '{policyPath}' must not be a symbolic link.");
        }
        if (observed.Type != RegularFileType)
        {
            throw new InstallerException($"Host policy '{policyPath}' must be a regular file.");
        }
        using var handle = NativeLinux.OpenNodeAt(parent, name);
        var opened = NativeLinux.GetOpenNodeIdentity(handle, policyPath);
        if (opened.Type != RegularFileType || opened.Inode != observed.Inode ||
            opened.DeviceMajor != observed.DeviceMajor || opened.DeviceMinor != observed.DeviceMinor)
        {
            throw new InstallerException($"Host policy '{policyPath}' changed while it was read.");
        }
        if (opened.LinkCount != 1)
        {
            throw new InstallerException($"Host policy '{policyPath}' must not have hard links.");
        }
        EnsureTrusted(location, opened, policyPath);
        return Deserialize(handle, policyPath);
    }

    private static HostPolicyDocument Deserialize(SafeFileHandle handle, string policyPath)
    {
        var buffer = new byte[MaximumPolicyBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = RandomAccess.Read(handle, buffer.AsSpan(length), length)) > 0)
        {
            length += read;
        }
        if (length > MaximumPolicyBytes)
        {
            throw new InstallerException($"Host policy '{policyPath}' exceeds {MaximumPolicyBytes} bytes.");
        }
        try
        {
            return JsonSerializer.Deserialize(
                       buffer.AsSpan(0, length),
                       HostPolicyJsonContext.Default.HostPolicyDocument)
                   ?? throw new InstallerException($"Host policy '{policyPath}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new InstallerException(
                $"Host policy '{policyPath}' must be a JSON object with one productRoot string and at most one mountPoint string.", exception);
        }
    }

    private static void WritePolicy(HostPolicyLocation location, string productRoot, string mountPoint)
    {
        var policyPath = location.PolicyPath;
        var directory = Path.GetDirectoryName(policyPath)!;
        var components = ComponentsBelowTrustRoot(location, policyPath, "The host policy");
        using (WalkTrustedDirectories(location, components[..^1], directory, createMissing: true, missingIsAbsence: false))
        {
        }
        var content = JsonSerializer.Serialize(
            new HostPolicyDocument(productRoot, mountPoint), HostPolicyJsonContext.Default.HostPolicyDocument) + "\n";
        var temporary = Path.Combine(directory, $".{Path.GetFileName(policyPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                NativeLinux.ChangeOwner(temporary, location.OwnerUid, location.OwnerGid);
                File.SetUnixFileMode(temporary, PolicyFileMode);
                stream.Write(Encoding.UTF8.GetBytes(content));
#pragma warning disable CA1849 // Atomic policy publication requires a flush-to-disk boundary.
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
            File.Move(temporary, policyPath, overwrite: true);
            NativeLinux.FlushDirectory(directory);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// Opens <paramref name="components"/> below the trust root one at a time without following links, requiring
    /// every opened directory (the trust root included) to be owned by the policy owner and not writable by group or
    /// other users. Returns the final directory, or <see langword="null"/> when a component is absent and
    /// <paramref name="missingIsAbsence"/> is set. When <paramref name="createMissing"/> is set an absent component is
    /// created with the policy owner and mode <c>0755</c>; <paramref name="createOnlyLast"/> limits that to the final
    /// component.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each superseded component handle is disposed as the walk advances, and the current handle is disposed on failure or returned to the caller.")]
    private static SafeFileHandle? WalkTrustedDirectories(
        HostPolicyLocation location,
        string[] components,
        string displayPath,
        bool createMissing,
        bool missingIsAbsence,
        bool createOnlyLast = false)
    {
        var current = NativeLinux.OpenDirectoryNoFollow(location.TrustRoot);
        try
        {
            EnsureTrusted(location, NativeLinux.GetOpenNodeIdentity(current, location.TrustRoot), location.TrustRoot);
            var walked = location.TrustRoot;
            for (var index = 0; index < components.Length; index++)
            {
                var name = components[index];
                walked = Path.Combine(walked, name);
                var observed = NativeLinux.TryGetNodeIdentityAt(current, name);
                if (observed is null)
                {
                    if (missingIsAbsence)
                    {
                        current.Dispose();
                        return null;
                    }
                    if (!createMissing || createOnlyLast && index != components.Length - 1)
                    {
                        throw new InstallerException(
                            $"'{walked}' does not exist; create it owned by {location.OwnerDescription} or mount its filesystem before using '{displayPath}'.");
                    }
                    CreateTrustedDirectory(location, walked);
                    observed = NativeLinux.TryGetNodeIdentityAt(current, name)
                        ?? throw new InstallerException($"'{walked}' could not be created.");
                }
                if (observed.Type == SymbolicLinkType)
                {
                    throw new InstallerException($"'{walked}' must not be a symbolic link.");
                }
                if (observed.Type != DirectoryType)
                {
                    throw new InstallerException($"'{walked}' must be a directory.");
                }
                var next = NativeLinux.TryOpenDirectoryAt(current, name)
                    ?? throw new InstallerException($"'{walked}' could not be opened without following links.");
                current.Dispose();
                current = next;
                var opened = NativeLinux.GetOpenNodeIdentity(current, walked);
                if (opened.Inode != observed.Inode || opened.DeviceMajor != observed.DeviceMajor ||
                    opened.DeviceMinor != observed.DeviceMinor)
                {
                    throw new InstallerException($"'{walked}' changed while it was authenticated.");
                }
                EnsureTrusted(location, opened, walked);
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static void CreateTrustedDirectory(HostPolicyLocation location, string path)
    {
        Directory.CreateDirectory(path);
        NativeLinux.ChangeOwner(path, location.OwnerUid, location.OwnerGid);
        File.SetUnixFileMode(path, SharedDirectoryMode);
    }

    private static void EnsureTrusted(HostPolicyLocation location, UnixNodeIdentity identity, string path)
    {
        if (identity.Uid != location.OwnerUid || identity.Gid != location.OwnerGid ||
            (identity.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InstallerException(
                $"'{path}' must be owned by {location.OwnerDescription} and not writable by group or other users.");
        }
    }

    private static string[] ComponentsBelowTrustRoot(HostPolicyLocation location, string path, string description)
    {
        var trustRoot = location.TrustRoot;
        if (!Path.IsPathFullyQualified(trustRoot) ||
            !string.Equals(Path.GetFullPath(trustRoot), trustRoot, StringComparison.Ordinal) ||
            trustRoot != "/" && trustRoot.EndsWith('/'))
        {
            throw new InstallerException($"Host policy trust root '{trustRoot}' must be absolute and normalized.");
        }
        var prefix = trustRoot == "/" ? "/" : trustRoot + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length == prefix.Length)
        {
            throw new InstallerException($"{description} '{path}' must be below '{trustRoot}'.");
        }
        return path[prefix.Length..].Split('/');
    }

    private static string ReadMountInfo()
    {
        try
        {
            return File.ReadAllText(MountInfoPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallerException("The host mount table could not be read.", exception);
        }
    }

    /// <summary>Decodes the octal escapes (<c>\040</c>, <c>\011</c>, <c>\012</c>, <c>\134</c>) the kernel writes in mountinfo paths.</summary>
    internal static string UnescapeMountPath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && IsOctal(value, index + 1) && IsOctal(value, index + 2) && IsOctal(value, index + 3))
            {
                builder.Append((char)(((value[index + 1] - '0') << 6) | ((value[index + 2] - '0') << 3) | (value[index + 3] - '0')));
                index += 3;
            }
            else
            {
                builder.Append(value[index]);
            }
        }
        return builder.ToString();
    }

    private static bool IsOctal(string value, int index) => index < value.Length && value[index] is >= '0' and <= '7';
}
