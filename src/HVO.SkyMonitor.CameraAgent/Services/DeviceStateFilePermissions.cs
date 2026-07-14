namespace HVO.SkyMonitor.CameraAgent.Services;

internal static class DeviceStateFilePermissions
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead |
        UnixFileMode.UserWrite;

    public static void RestrictDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, DirectoryMode);
        }
    }

    public static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, FileMode);
        }
    }
}
