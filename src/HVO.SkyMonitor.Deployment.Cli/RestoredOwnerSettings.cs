using System.Text;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The owner settings a restore keeps from the live instance (#1353 PR-1369-R2 F14). At every start CameraAgent's identity
/// seeder makes the account <c>LocalIdentity__AdminEmail</c> names the site owner, demoting any other, and creates that
/// account from the temporary owner password when it is missing. These settings grant authority as surely as the Identity
/// database does, so restoring the backup's would give back authority the live instance has since moved, whatever
/// <see cref="RestoredCredentials"/> kept revoked. The restore keeps the live files, and the owner email the live records
/// name, which the reinstall verifies the running instance against.
/// </summary>
internal static class RestoredOwnerSettings
{
    /// <summary>The setting the seeder makes the site owner by.</summary>
    public const string AdminEmailSetting = "LocalIdentity__AdminEmail";

    /// <summary>The setting that names the sign-in name of a site owner the seeder creates.</summary>
    public const string AdminUserNameSetting = "LocalIdentity__AdminUserName";

    /// <summary>The files the seeder reads to grant authority, relative to the instance root.</summary>
    public static IReadOnlyList<string> Files { get; } =
    [
        $"config/secrets/{AdminEmailSetting}",
        $"config/secrets/{AdminUserNameSetting}",
        "config/secrets/LocalIdentity__AdminPassword",
        "config/secrets/LocalIdentity__AdminPasswordFile",
        "config/secrets/LocalIdentity__AllowMissingAdminPassword",
        "config/owner-bootstrap/temporary-password"
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Makes each of <see cref="Files"/> under <paramref name="instanceRoot"/> what it is under <paramref name="live"/>, the
    /// live instance the swap kept aside: copied where it differs or is missing, removed where the live instance has none.
    /// Returns the name of each one it changed, never its content. Run again, it changes nothing more.
    /// </summary>
    public static IReadOnlyList<string> KeepLive(string instanceRoot, string live)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceRoot);
        ArgumentException.ThrowIfNullOrEmpty(live);
        var changed = new List<string>();
        foreach (var relative in Files)
        {
            var restored = Path.Combine(instanceRoot, relative);
            var kept = Path.Combine(live, relative);
            var name = relative.StartsWith("config/secrets/", StringComparison.Ordinal) ? Path.GetFileName(relative) : relative["config/".Length..];
            if (File.Exists(kept))
            {
                var value = Read(kept);
                if (File.Exists(restored) && string.Equals(Read(restored), value, StringComparison.Ordinal)) continue;
                var directory = Path.GetDirectoryName(restored)!;
                if (!Directory.Exists(directory))
                {
                    SafeFileSystem.CreateOwnerDirectory(directory);
                    NativeLinux.FlushDirectory(Path.GetDirectoryName(directory)!);
                }
                SafeFileSystem.WriteTextAtomic(restored, value);
                changed.Add(name);
            }
            else if (File.Exists(restored))
            {
                SafeFileSystem.ValidateOwnerFile(restored, allowReadOnly: true);
                File.Delete(restored);
                NativeLinux.FlushDirectory(Path.GetDirectoryName(restored)!);
                changed.Add($"{name} (removed, as the live instance has none)");
            }
        }
        return changed;
    }

    /// <summary>
    /// The value of the setting <paramref name="name"/> under <paramref name="instanceRoot"/>'s <c>config/secrets</c>, as
    /// CameraAgent reads it, or <see langword="null"/> when there is none.
    /// </summary>
    public static string? Setting(string instanceRoot, string name)
    {
        var path = Path.Combine(instanceRoot, "config", "secrets", name);
        return File.Exists(path) ? Read(path).TrimEnd('\r', '\n') : null;
    }

    private static string Read(string path)
    {
        using var stream = SafeFileSystem.OpenOwnerFileRead(path, allowReadOnly: true);
        using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }
}
