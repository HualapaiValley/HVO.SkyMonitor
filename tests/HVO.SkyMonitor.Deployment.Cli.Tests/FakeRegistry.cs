using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Answers the <c>docker buildx imagetools inspect</c> calls the installer makes to read a release image from its
/// registry before pulling it, from the content a registry serves by digest (<see cref="ReleaseFixture.RegistryContent"/>):
/// <c>--raw &lt;repository&gt;@&lt;digest&gt;</c> prints that content unchanged, and
/// <c>--format {{json .Image}} &lt;repository&gt;@&lt;manifest&gt;</c> prints the configuration that manifest names.
/// </summary>
internal sealed class FakeRegistry(IReadOnlyDictionary<string, byte[]> content)
{
    /// <summary>When set, every read fails with this diagnostic, as on a host whose Docker has no buildx.</summary>
    public string? Unavailable { get; init; }

    /// <summary>The number of registry reads answered or refused.</summary>
    public int Reads { get; private set; }

    /// <summary>Whether <paramref name="arguments"/>, without the pinned <c>--host</c>, is a registry read.</summary>
    public static bool IsRead(IReadOnlyList<string> arguments)
        => arguments is ["buildx", "imagetools", "inspect", ..];

    public ProcessResult Answer(IReadOnlyList<string> arguments)
    {
        Reads++;
        if (Unavailable is { } diagnostic)
        {
            return new ProcessResult(1, string.Empty, diagnostic);
        }
        switch (arguments)
        {
            case ["buildx", "imagetools", "inspect", "--raw", var reference]:
                return Find(reference) is { } bytes
                    ? new ProcessResult(0, Encoding.UTF8.GetString(bytes), string.Empty)
                    : NotFound(reference);
            case ["buildx", "imagetools", "inspect", "--format", "{{json .Image}}", var reference]:
                if (Find(reference) is not { } manifest)
                {
                    return NotFound(reference);
                }
                using (var document = JsonDocument.Parse(manifest))
                {
                    var config = document.RootElement.GetProperty("config").GetProperty("digest").GetString()!;
                    return content.TryGetValue(config, out var configuration)
                        ? new ProcessResult(0, Encoding.UTF8.GetString(configuration) + "\n", string.Empty)
                        : NotFound(config);
                }
            default:
                throw new InvalidOperationException($"Unexpected registry read: {string.Join(' ', arguments)}");
        }
    }

    private byte[]? Find(string reference)
        => content.TryGetValue(reference[(reference.IndexOf('@', StringComparison.Ordinal) + 1)..], out var bytes) ? bytes : null;

    private static ProcessResult NotFound(string reference)
        => new(1, string.Empty, $"ERROR: {reference}: not found");
}
