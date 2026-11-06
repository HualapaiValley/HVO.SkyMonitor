using System.Globalization;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>The fake host's Linux adapter. It uses the same descriptor identity and no-replace rename primitives as the installer.</summary>
internal sealed class DeploymentFakeFiles : IDeploymentFiles
{
    private readonly Dictionary<string, SafeFileHandle> handles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> offsets = new(StringComparer.Ordinal);
    public string Call(string operation, params string[] args)
    {
        var path = args.ElementAtOrDefault(0) ?? "";
        switch (operation)
        {
            case "replace": File.Move(path, args[1], overwrite: true); return "moved";
            case "create":
                var created = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var id = handles.Count.ToString(CultureInfo.InvariantCulture); handles.Add(id, created); offsets.Add(id, 0); return id;
            case "append-bytes":
                var bytes = Convert.FromBase64String(args[1]); RandomAccess.Write(handles[path], bytes, offsets[path]); offsets[path] += bytes.Length; return "written";
            case "stat": return Metadata(NativeLinux.TryGetNodeIdentity(path));
            case "fstat": return Metadata(NativeLinux.GetOpenNodeIdentity(handles[path], path));
            case "open":
                var handle = NativeLinux.OpenReadOnlyNoFollow(path); var key = handles.Count.ToString(CultureInfo.InvariantCulture); handles.Add(key, handle); return key;
            case "lock": if (!NativeLinux.TryLockExclusive(handles[path], "fixture")) throw SplitHostJson.Error("host-primitive-failed"); return "locked";
            case "path": return $"/proc/{Environment.ProcessId}/fd/{handles[path].DangerousGetHandle()}";
            case "read":
                using (var buffer = new MemoryStream())
                {
                    var data = new byte[8192]; long offset = 0;
                    while (buffer.Length <= 1024 * 1024) { var count = RandomAccess.Read(handles[path], data, offset); if (count == 0) break; buffer.Write(data, 0, count); offset += count; }
                    return Convert.ToBase64String(buffer.ToArray());
                }
            case "list": return Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Concat(Directory.EnumerateFileSystemEntries(path).Take(10000).Select(p => Path.GetFileName(p) + "\0"))));
            case "mounts": return Convert.ToBase64String(File.ReadAllBytes("/proc/self/mountinfo"));
            case "move":
                using (var parent = NativeLinux.OpenDirectoryNoFollow(Path.GetDirectoryName(path)! + "/.")) NativeLinux.RenameNodeAt(parent, Path.GetFileName(path), Path.GetFileName(args[1])); return "moved";
            case "unlink":
            case "rmdir":
                using (var parent = NativeLinux.OpenDirectoryNoFollow(Path.GetDirectoryName(path)! + "/.")) NativeLinux.UnlinkNodeAt(parent, Path.GetFileName(path), operation == "rmdir"); return "removed";
            case "mkdir": Directory.CreateDirectory(path, SafeFileSystem.OwnerDirectoryMode); return "created";
            case "chmod700": File.SetUnixFileMode(path, SafeFileSystem.OwnerDirectoryMode); return "secured";
            case "write": DeploymentFakeHost.Write(path, args[1]); return "written";
            case "sync":
                if (Directory.Exists(path)) NativeLinux.FlushDirectory(path + "/.");
                else
                {
                    var opened = handles.Values.SingleOrDefault(file => path == $"/proc/{Environment.ProcessId}/fd/{file.DangerousGetHandle()}");
                    if (opened is not null) RandomAccess.FlushToDisk(opened);
                    else { using var descriptor = NativeLinux.OpenReadOnlyNoFollow(path); RandomAccess.FlushToDisk(descriptor); }
                }
                return "synced";
            default: throw new InvalidOperationException("Unexpected host primitive " + operation);
        }
    }
    private static string Metadata(UnixNodeIdentity? value) => value is null ? "absent" : string.Create(CultureInfo.InvariantCulture, $"{value.Uid} {Convert.ToString((int)value.Mode, 8)} {value.Type:x} {value.LinkCount} {value.DeviceMajor}:{value.DeviceMinor} {value.Inode}");
    public void Dispose() { foreach (var handle in handles.Values) handle.Dispose(); }
}
