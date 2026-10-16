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
            if (source.Length is < OwnerCredentialDefaults.MinimumPasswordLength or > OwnerCredentialDefaults.MaximumPasswordLength)
            {
                throw new InstallerException("The supplied password file length is invalid.");
            }
            await ValidateSuppliedPasswordAsync(source, cancellationToken).ConfigureAwait(false);
            source.Position = 0;
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

    /// <summary>
    /// Requires the supplied password, without its trailing line ending, to be UTF-8 text of at least
    /// <see cref="OwnerCredentialDefaults.MinimumPasswordLength"/> characters without control characters, which is
    /// what CameraAgent accepts as a temporary owner password.
    /// </summary>
    private static async Task ValidateSuppliedPasswordAsync(FileStream source, CancellationToken cancellationToken)
    {
        var content = new byte[source.Length];
        await source.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        string password;
        try
        {
            password = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(content)
                .TrimEnd('\r', '\n');
        }
        catch (DecoderFallbackException exception)
        {
            throw new InstallerException("The supplied password file is not valid UTF-8.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }

        if (password.Length < OwnerCredentialDefaults.MinimumPasswordLength || password.Any(char.IsControl))
        {
            throw new InstallerException("The supplied password file content is invalid.");
        }
    }

    /// <summary>
    /// Publishes <paramref name="value"/> as the owner-only credential at <paramref name="generatedPath"/> unless a
    /// credential was already published there, which is retained unchanged so a resumed installation keeps the
    /// credential its first run seeded. Used only for the documented default owner password, never for a secret.
    /// </summary>
    public static async Task<string> GetOrCreateKnownAsync(
        string generatedPath,
        string value,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (File.Exists(generatedPath))
        {
            SafeFileSystem.ValidateOwnerFile(generatedPath);
            return generatedPath;
        }

        SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(generatedPath)!);
        using var stream = new FileStream(generatedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        File.SetUnixFileMode(generatedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1024, leaveOpen: true);
        await writer.WriteLineAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Credential publication requires a flush-to-disk boundary.
        stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
        return generatedPath;
    }
}
