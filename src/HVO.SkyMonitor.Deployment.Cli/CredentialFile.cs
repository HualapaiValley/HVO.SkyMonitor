using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.Deployment;

internal static class CredentialFile
{
    public static async Task<string> GetOrCreateAsync(
        string? suppliedPath,
        string generatedPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(generatedPath))
        {
            SafeFileSystem.ValidateOwnerFile(generatedPath);
            return generatedPath;
        }

        if (suppliedPath is not null)
        {
            await using var source = SafeFileSystem.OpenOwnerFileRead(suppliedPath, allowReadOnly: true);
            if (source.Length is < 16 or > 256)
            {
                throw new InstallerException("The supplied password file length is invalid.");
            }
            _ = await SafeFileSystem.CopyPrivateFileAsync(source, generatedPath, cancellationToken)
                .ConfigureAwait(false);
            return generatedPath;
        }

        SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(generatedPath)!);
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        var password = Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var stream = new FileStream(generatedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        File.SetUnixFileMode(generatedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1024, leaveOpen: true);
        await writer.WriteAsync(password.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync("Aa1!".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Credential publication requires a flush-to-disk boundary.
        stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
        return generatedPath;
    }
}
