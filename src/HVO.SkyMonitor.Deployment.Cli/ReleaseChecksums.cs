using System.Text;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// A release's <c>SHA256SUMS</c> (install contract section 4): one line per release file, release.json included, each
/// its SHA-256 and its name as <c>sha256sum</c> writes them, <c>&lt;64 lowercase hex digits&gt;  &lt;name&gt;</c>, ending in
/// LF. Anything else is refused rather than read around, so a damaged or hand-edited list never verifies a file.
/// </summary>
internal sealed record ReleaseChecksums(IReadOnlyDictionary<string, string> Files)
{
    public const string FileName = "SHA256SUMS";

    /// <summary>The largest SHA256SUMS read. A release's own is about a kilobyte.</summary>
    public const int MaximumLength = 64 * 1024;

    /// <summary>
    /// Reads SHA256SUMS. Throws <see cref="InstallerException"/> naming what is wrong. A message names a file only once
    /// its name is known to be a release file name.
    /// </summary>
    public static ReleaseChecksums Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumLength)
        {
            throw Invalid($"it is larger than {MaximumLength} bytes");
        }
        if (bytes.IsEmpty || bytes[^1] != (byte)'\n')
        {
            throw Invalid("it does not end in a line feed");
        }
        foreach (var value in bytes)
        {
            if (value is not ((byte)'\n' or >= 0x20 and <= 0x7E))
            {
                throw Invalid("it is not lines of printable ASCII text");
            }
        }
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var line in Encoding.ASCII.GetString(bytes[..^1]).Split('\n'))
        {
            lineNumber++;
            // sha256sum's text-mode line: the digest, two spaces, the name. The binary-mode marker (" *") is not written by
            // a release and is not accepted.
            if (line.Length < 67 || line[64] != ' ' || line[65] != ' ' ||
                !ReleaseManifest.Sha256Pattern().IsMatch(line[..64]) || !ReleaseManifest.FileNamePattern().IsMatch(line[66..]))
            {
                throw Invalid($"line {lineNumber} is not a SHA-256 and a release file name");
            }
            if (!files.TryAdd(line[66..], line[..64]))
            {
                throw Invalid($"it lists {line[66..]} more than once");
            }
        }
        return new ReleaseChecksums(files);
    }

    private static InstallerException Invalid(string reason) => new($"The installer will not use {FileName}: {reason}.");
}
