using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.Deployment;

internal static class SafeTreeDeletion
{
    private const uint DirectoryType = 0x4000;
    private const uint RegularFileType = 0x8000;

    public static void DeleteChild(string parentPath, string childName, uint uid, uint gid, UnixNodeIdentity? expectedIdentity = null)
    {
        ValidateName(childName);
        using var parent = NativeLinux.OpenDirectoryNoFollow(parentPath);
        var parentIdentity = NativeLinux.GetOpenNodeIdentity(parent, parentPath);
        using var child = NativeLinux.TryOpenDirectoryAt(parent, childName)
            ?? throw new InstallerException("Authenticated deletion root is not a directory.");
        var rootIdentity = NativeLinux.GetOpenNodeIdentity(child, childName);
        if (expectedIdentity is not null) EnsureSameIdentity(rootIdentity, expectedIdentity, childName);
        ValidateDirectory(rootIdentity, parentIdentity, uid, gid, childName);
        NativeLinux.MakeOpenDirectoryOwnerWritable(child);
        rootIdentity = NativeLinux.GetOpenNodeIdentity(child, childName);
        DeleteDirectoryContents(child, rootIdentity, uid, gid);
        rootIdentity = NativeLinux.GetOpenNodeIdentity(child, childName);
        Revalidate(parent, childName, rootIdentity);
        NativeLinux.UnlinkNodeAt(parent, childName, directory: true);
        NativeLinux.FlushDirectory(parentPath);
    }

    public static UnixNodeIdentity ValidateChild(string parentPath, string childName, uint uid, uint gid)
    {
        ValidateName(childName);
        using var parent = NativeLinux.OpenDirectoryNoFollow(parentPath);
        var parentIdentity = NativeLinux.GetOpenNodeIdentity(parent, parentPath);
        using var child = NativeLinux.TryOpenDirectoryAt(parent, childName)
            ?? throw new InstallerException("Authenticated deletion root is not a directory.");
        var rootIdentity = NativeLinux.GetOpenNodeIdentity(child, childName);
        ValidateDirectory(rootIdentity, parentIdentity, uid, gid, childName);
        ValidateDirectoryContents(child, rootIdentity, uid, gid);
        Revalidate(parent, childName, rootIdentity);
        return rootIdentity;
    }

    public static void RenameChild(string parentPath, string oldName, string newName, UnixNodeIdentity expectedIdentity)
    {
        ValidateName(oldName);
        ValidateName(newName);
        using var parent = NativeLinux.OpenDirectoryNoFollow(parentPath);
        Revalidate(parent, oldName, expectedIdentity);
        NativeLinux.RenameNodeAt(parent, oldName, newName);
        Revalidate(parent, newName, expectedIdentity);
        NativeLinux.FlushDirectory(parentPath);
    }

    public static IReadOnlyList<DestructiveTreeNode> CaptureChildInventory(
        string parentPath,
        string childName,
        uint uid,
        uint gid)
    {
        ValidateName(childName);
        using var parent = NativeLinux.OpenDirectoryNoFollow(parentPath);
        var parentIdentity = NativeLinux.GetOpenNodeIdentity(parent, parentPath);
        using var child = NativeLinux.TryOpenDirectoryAt(parent, childName)
            ?? throw new InstallerException("Authenticated inventory root is not a directory.");
        var rootIdentity = NativeLinux.GetOpenNodeIdentity(child, childName);
        ValidateDirectory(rootIdentity, parentIdentity, uid, gid, childName);
        var inventory = new List<DestructiveTreeNode> { ToEvidence(".", rootIdentity) };
        CaptureDirectoryInventory(child, rootIdentity, uid, gid, string.Empty, inventory);
        Revalidate(parent, childName, rootIdentity);
        return inventory;
    }

    public static UnixNodeIdentity ValidateRemainingChild(
        string parentPath,
        string childName,
        uint uid,
        uint gid,
        IReadOnlyList<DestructiveTreeNode> expected)
    {
        var current = CaptureChildInventory(parentPath, childName, uid, gid);
        var expectedByPath = expected.ToDictionary(static node => node.RelativePath, StringComparer.Ordinal);
        foreach (var node in current)
        {
            if (!expectedByPath.TryGetValue(node.RelativePath, out var retained) || !IsRetainedIdentity(node, retained))
                throw new InstallerException("Authenticated deletion recovery found an added or replaced tree entry.");
        }
        var root = current.Single(static node => node.RelativePath == ".");
        return new UnixNodeIdentity(
            root.Uid, root.Gid, (UnixFileMode)root.Mode, root.Type, root.LinkCount, root.Inode,
            root.DeviceMajor, root.DeviceMinor, root.MountId);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Every nullable child directory handle is disposed by the using declaration before the loop advances.")]
    private static void DeleteDirectoryContents(
        SafeFileHandle directory,
        UnixNodeIdentity rootIdentity,
        uint uid,
        uint gid)
    {
        var descriptorPath = $"/proc/self/fd/{directory.DangerousGetHandle().ToInt64()}";
        var names = Directory.EnumerateFileSystemEntries(descriptorPath)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrEmpty(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var name in names)
        {
            using var childDirectory = NativeLinux.TryOpenDirectoryAt(directory, name!);
            if (childDirectory is not null)
            {
                var identity = NativeLinux.GetOpenNodeIdentity(childDirectory, name!);
                ValidateDirectory(identity, rootIdentity, uid, gid, name!);
                Revalidate(directory, name!, identity);
                NativeLinux.MakeOpenDirectoryOwnerWritable(childDirectory);
                DeleteDirectoryContents(childDirectory, rootIdentity, uid, gid);
                identity = NativeLinux.GetOpenNodeIdentity(childDirectory, name!);
                Revalidate(directory, name!, identity);
                NativeLinux.UnlinkNodeAt(directory, name!, directory: true);
                continue;
            }
            using var child = NativeLinux.OpenNodeAt(directory, name!);
            var fileIdentity = NativeLinux.GetOpenNodeIdentity(child, name!);
            if (fileIdentity.Type != RegularFileType || fileIdentity.Uid != uid || fileIdentity.Gid != gid ||
                fileIdentity.LinkCount != 1 || !SameFileSystem(fileIdentity, rootIdentity))
            {
                throw new InstallerException("Authenticated deletion rejected a special, linked, foreign, or mounted entry.");
            }
            Revalidate(directory, name!, fileIdentity);
            NativeLinux.UnlinkNodeAt(directory, name!, directory: false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Every nullable child directory handle is disposed by the using declaration before the loop advances.")]
    private static void ValidateDirectoryContents(
        SafeFileHandle directory,
        UnixNodeIdentity rootIdentity,
        uint uid,
        uint gid)
    {
        var descriptorPath = $"/proc/self/fd/{directory.DangerousGetHandle().ToInt64()}";
        foreach (var name in Directory.EnumerateFileSystemEntries(descriptorPath)
                     .Select(Path.GetFileName)
                     .Where(static name => !string.IsNullOrEmpty(name))
                     .Order(StringComparer.Ordinal))
        {
            using var childDirectory = NativeLinux.TryOpenDirectoryAt(directory, name!);
            if (childDirectory is not null)
            {
                var identity = NativeLinux.GetOpenNodeIdentity(childDirectory, name!);
                ValidateDirectory(identity, rootIdentity, uid, gid, name!);
                ValidateDirectoryContents(childDirectory, rootIdentity, uid, gid);
                Revalidate(directory, name!, identity);
                continue;
            }
            using var child = NativeLinux.OpenNodeAt(directory, name!);
            var fileIdentity = NativeLinux.GetOpenNodeIdentity(child, name!);
            if (fileIdentity.Type != RegularFileType || fileIdentity.Uid != uid || fileIdentity.Gid != gid ||
                fileIdentity.LinkCount != 1 || !SameFileSystem(fileIdentity, rootIdentity))
            {
                throw new InstallerException("Authenticated deletion rejected a special, linked, foreign, or mounted entry.");
            }
            Revalidate(directory, name!, fileIdentity);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Every nullable child directory handle is disposed by the using declaration before the loop advances.")]
    private static void CaptureDirectoryInventory(
        SafeFileHandle directory,
        UnixNodeIdentity rootIdentity,
        uint uid,
        uint gid,
        string relativeRoot,
        ICollection<DestructiveTreeNode> inventory)
    {
        var descriptorPath = $"/proc/self/fd/{directory.DangerousGetHandle().ToInt64()}";
        foreach (var name in Directory.EnumerateFileSystemEntries(descriptorPath)
                     .Select(Path.GetFileName)
                     .Where(static name => !string.IsNullOrEmpty(name))
                     .Order(StringComparer.Ordinal))
        {
            var relativePath = string.IsNullOrEmpty(relativeRoot) ? name! : Path.Combine(relativeRoot, name!);
            using var childDirectory = NativeLinux.TryOpenDirectoryAt(directory, name!);
            if (childDirectory is not null)
            {
                var identity = NativeLinux.GetOpenNodeIdentity(childDirectory, name!);
                ValidateDirectory(identity, rootIdentity, uid, gid, name!);
                inventory.Add(ToEvidence(relativePath, identity));
                CaptureDirectoryInventory(childDirectory, rootIdentity, uid, gid, relativePath, inventory);
                Revalidate(directory, name!, identity);
                continue;
            }
            using var child = NativeLinux.OpenNodeAt(directory, name!);
            var fileIdentity = NativeLinux.GetOpenNodeIdentity(child, name!);
            if (fileIdentity.Type != RegularFileType || fileIdentity.Uid != uid || fileIdentity.Gid != gid ||
                fileIdentity.LinkCount != 1 || !SameFileSystem(fileIdentity, rootIdentity))
                throw new InstallerException("Authenticated inventory rejected a special, linked, foreign, or mounted entry.");
            inventory.Add(ToEvidence(relativePath, fileIdentity));
            Revalidate(directory, name!, fileIdentity);
        }
    }

    private static DestructiveTreeNode ToEvidence(string relativePath, UnixNodeIdentity identity)
        => new(relativePath, identity.Uid, identity.Gid, (int)identity.Mode, identity.Type, identity.LinkCount,
            identity.Inode, identity.DeviceMajor, identity.DeviceMinor, identity.MountId);

    private static bool IsRetainedIdentity(DestructiveTreeNode current, DestructiveTreeNode expected)
        => current.RelativePath == expected.RelativePath && current.Uid == expected.Uid && current.Gid == expected.Gid &&
           current.Type == expected.Type && current.Inode == expected.Inode && current.DeviceMajor == expected.DeviceMajor &&
           current.DeviceMinor == expected.DeviceMinor &&
           (current.Type == DirectoryType
               ? current.LinkCount <= expected.LinkCount &&
                 (current.Mode == expected.Mode || current.Mode == (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
               : current.LinkCount == expected.LinkCount && current.Mode == expected.Mode);

    private static void ValidateName(string childName)
    {
        if (string.IsNullOrEmpty(childName) || childName is "." or ".." ||
            childName.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            childName.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InstallerException("Authenticated deletion received an invalid child name.");
        }
    }

    private static void ValidateDirectory(
        UnixNodeIdentity identity,
        UnixNodeIdentity rootIdentity,
        uint uid,
        uint gid,
        string name)
    {
        if (identity.Type != DirectoryType || identity.Uid != uid || identity.Gid != gid ||
            (identity.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0 ||
            identity.LinkCount < 2 || !SameFileSystem(identity, rootIdentity))
        {
            throw new InstallerException($"Authenticated deletion rejected directory '{name}'.");
        }
    }

    private static void Revalidate(SafeFileHandle parent, string name, UnixNodeIdentity expected)
    {
        var actual = NativeLinux.GetNodeIdentityAt(parent, name);
        EnsureSameIdentity(actual, expected, name);
    }

    private static void EnsureSameIdentity(UnixNodeIdentity actual, UnixNodeIdentity expected, string name)
    {
        if (actual.Uid != expected.Uid || actual.Gid != expected.Gid || actual.Mode != expected.Mode ||
            actual.Type != expected.Type || actual.LinkCount != expected.LinkCount || actual.Inode != expected.Inode ||
            !SameFileSystem(actual, expected))
            throw new InstallerException($"Entry '{name}' changed during authenticated deletion.");
    }

    private static bool SameFileSystem(UnixNodeIdentity value, UnixNodeIdentity root)
        => value.DeviceMajor == root.DeviceMajor && value.DeviceMinor == root.DeviceMinor && value.MountId == root.MountId;
}
