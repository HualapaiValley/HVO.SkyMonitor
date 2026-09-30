using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

/// <summary>
/// Reads and edits the operator's <c>appsettings.local.json</c>: the one file the host loads over its image settings
/// and the one file an operator edits, by hand or from the operator UI. An edit rewrites only the keys it names and
/// keeps every other key the operator wrote.
/// </summary>
/// <remarks>
/// Each read carries a version, the SHA-256 of the file's bytes, and a write names the version it was based on, so a
/// hand edit made while a page was open is never silently overwritten. Comments in a hand-edited file are accepted,
/// as the configuration loader accepts them, but a UI save rewrites the file without them.
/// </remarks>
public sealed class OperatorSettingsFile : IDisposable
{
    /// <summary>The version of a settings file that does not exist yet.</summary>
    public const string AbsentVersion = "absent";

    /// <summary>The largest settings file this host reads or writes.</summary>
    public const int MaximumFileBytes = 256 * 1024;

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly Action _reload;
    private readonly Func<IEnumerable<string>, IReadOnlyList<string>> _findOverriddenKeys;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="filePath">The absolute settings file path the host loaded.</param>
    /// <param name="reload">Reloads the host configuration from the file after a write.</param>
    /// <param name="findOverriddenKeys">Names the keys a higher-precedence configuration source supplies.</param>
    public OperatorSettingsFile(
        string filePath,
        Action reload,
        Func<IEnumerable<string>, IReadOnlyList<string>> findOverriddenKeys,
        ILogger<OperatorSettingsFile>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!Path.IsPathFullyQualified(filePath))
        {
            throw new ArgumentException("The operator settings file path must be absolute.", nameof(filePath));
        }
        FilePath = Path.GetFullPath(filePath);
        _reload = reload ?? throw new ArgumentNullException(nameof(reload));
        _findOverriddenKeys = findOverriddenKeys ?? throw new ArgumentNullException(nameof(findOverriddenKeys));
        _logger = logger ?? NullLogger<OperatorSettingsFile>.Instance;
    }

    public string FilePath { get; }

    /// <summary>Flushes the directory after the replace; tests substitute it to fail the step after publication.</summary>
    internal Action<string> SyncPublishedDirectory { get; init; } = RawIngressFileStore.SyncDirectory;

    /// <summary>The keys a configuration source with higher precedence than this file supplies.</summary>
    public IReadOnlyList<string> FindOverriddenKeys(IEnumerable<string> keys) => _findOverriddenKeys(keys);

    public async ValueTask<OperatorSettingsSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadAsync(cancellationToken).ConfigureAwait(false)).Snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sets each named key to its value, or removes it when the value is null, in one atomic replace, provided the
    /// file is still at <paramref name="expectedVersion"/>. Keys are configuration paths such as
    /// <c>CameraAgent:SkyMap:MaximumObjects</c> and match the file's existing keys without regard to case.
    /// </summary>
    public async ValueTask<OperatorSettingsWriteResult> WriteAsync(
        string expectedVersion,
        IReadOnlyDictionary<string, JsonNode?> values,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedVersion);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (bytes, current) = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.Version, expectedVersion, StringComparison.Ordinal))
            {
                return new(OperatorSettingsWriteStatus.Conflict, current);
            }
            if (current.Problem is not null || current.Root is null)
            {
                return new(OperatorSettingsWriteStatus.Unreadable, current);
            }

            var root = current.Root;
            foreach (var (key, value) in values)
            {
                if (!TrySet(root, key, value))
                {
                    return new(
                        OperatorSettingsWriteStatus.Unreadable,
                        new OperatorSettingsSnapshot(
                            current.Version,
                            current.Exists,
                            $"The setting '{key}' is inside a value that is not a JSON object."));
                }
            }

            var next = SerializeFile(root);
            if (bytes is not null && next.AsSpan().SequenceEqual(bytes))
            {
                return new(OperatorSettingsWriteStatus.Unchanged, current);
            }

            await WriteDurableAsync(next, cancellationToken).ConfigureAwait(false);
            _reload();
            OperatorSettingsLog.Written(_logger, actor, string.Join(", ", values.Keys));
            return new(OperatorSettingsWriteStatus.Applied, Parse(next));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<(byte[]? Bytes, OperatorSettingsSnapshot Snapshot)> LoadAsync(
        CancellationToken cancellationToken)
    {
        EnsureNotLink();
        if (!File.Exists(FilePath))
        {
            return (null, new OperatorSettingsSnapshot(AbsentVersion, false, null) { Root = new JsonObject(NodeOptions) });
        }

        var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[MaximumFileBytes + 1];
            var length = 0;
            int read;
            while (length < buffer.Length &&
                (read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                length += read;
            }
            var bytes = buffer.AsSpan(0, length).ToArray();
            if (length > MaximumFileBytes)
            {
                return (bytes, new OperatorSettingsSnapshot(
                    Hash(bytes), true, $"The settings file is larger than {MaximumFileBytes / 1024} KiB."));
            }
            return (bytes, Parse(bytes));
        }
    }

    private static OperatorSettingsSnapshot Parse(byte[] bytes)
    {
        var version = Hash(bytes);
        try
        {
            if (JsonNode.Parse(bytes, NodeOptions, DocumentOptions) is not JsonObject root)
            {
                return new(version, true, "The settings file must contain a single JSON object.");
            }
            // Nested objects are materialized lazily; walking them here surfaces a duplicate key now rather than
            // when a later read happens to reach it.
            Materialize(root);
            return new(version, true, null) { Root = root };
        }
        catch (JsonException exception)
        {
            return new(version, true, $"The settings file is not valid JSON (line {exception.LineNumber + 1}).");
        }
        catch (ArgumentException)
        {
            return new(version, true, "The settings file names the same key more than once.");
        }
    }

    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject item:
                foreach (var property in item)
                {
                    Materialize(property.Value);
                }
                break;
            case JsonArray items:
                foreach (var child in items)
                {
                    Materialize(child);
                }
                break;
        }
    }

    private static bool TrySet(JsonObject root, string key, JsonNode? value)
    {
        var segments = key.Split(':');
        if (segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"'{key}' is not a configuration key.", nameof(key));
        }

        var path = new List<(JsonObject Parent, string Name)>();
        var node = root;
        foreach (var segment in segments[..^1])
        {
            if (!node.TryGetPropertyValue(segment, out var child) || child is null)
            {
                if (value is null)
                {
                    return true;
                }
                child = new JsonObject(NodeOptions);
                node[segment] = child;
            }
            if (child is not JsonObject childObject)
            {
                return false;
            }
            path.Add((node, segment));
            node = childObject;
        }

        if (value is not null)
        {
            node[segments[^1]] = value;
            return true;
        }

        // Removing the last key of a section removes the section too, so clearing a value leaves no empty objects.
        node.Remove(segments[^1]);
        for (var index = path.Count - 1; index >= 0 && node.Count == 0; index--)
        {
            path[index].Parent.Remove(path[index].Name);
            node = path[index].Parent;
        }
        return true;
    }

    private static byte[] SerializeFile(JsonObject root)
    {
        var json = root.ToJsonString(WriteOptions);
        return System.Text.Encoding.UTF8.GetBytes(string.Concat(json, "\n"));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private void EnsureNotLink()
    {
        if (File.Exists(FilePath) && new FileInfo(FilePath).LinkTarget is not null)
        {
            throw new IOException("The operator settings file must not be a symbolic link.");
        }
    }

    private async ValueTask WriteDurableAsync(byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaximumFileBytes)
        {
            throw new IOException($"The settings file would be larger than {MaximumFileBytes / 1024} KiB.");
        }
        var directory = Path.GetDirectoryName(FilePath)!;
        var temporaryPath = Path.Combine(
            directory,
            string.Concat('.', Path.GetFileName(FilePath), '.', Guid.NewGuid().ToString("N"), ".tmp"));
        try
        {
            var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // FlushAsync does not provide a flush-to-disk contract.
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(temporaryPath, FilePath, overwrite: true);
            SyncPublishedDirectory(directory);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>One read of the operator settings file.</summary>
/// <param name="Version">The SHA-256 of the file's bytes, or <see cref="OperatorSettingsFile.AbsentVersion"/>.</param>
/// <param name="Problem">Why the file could not be read as settings, or null when it could.</param>
public sealed record OperatorSettingsSnapshot(string Version, bool Exists, string? Problem)
{
    /// <summary>The parsed file, or null when <see cref="Problem"/> says why it could not be parsed.</summary>
    internal JsonObject? Root { get; init; }

    /// <summary>The scalar value at a configuration key, as text, or null for a section, array, or missing key.</summary>
    public string? GetValue(string key) => Find(key) switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonValue value when value.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            => value.ToJsonString(),
        _ => null
    };

    /// <summary>Whether the file has an object at this configuration key, even an empty one.</summary>
    public bool HasSection(string key) => Find(key) is JsonObject;

    private JsonNode? Find(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        JsonNode? node = Root;
        foreach (var segment in key.Split(':'))
        {
            if (node is not JsonObject item || !item.TryGetPropertyValue(segment, out node))
            {
                return null;
            }
        }
        return node;
    }
}

public enum OperatorSettingsWriteStatus
{
    /// <summary>The file was replaced and the host configuration reloaded.</summary>
    Applied,

    /// <summary>The file already held these values; nothing was written.</summary>
    Unchanged,

    /// <summary>The file changed since the expected version was read; nothing was written.</summary>
    Conflict,

    /// <summary>The file is not valid settings JSON, so it was left for the operator to correct.</summary>
    Unreadable
}

public sealed record OperatorSettingsWriteResult(OperatorSettingsWriteStatus Status, OperatorSettingsSnapshot Snapshot);

internal static partial class OperatorSettingsLog
{
    [LoggerMessage(7611, LogLevel.Information, "Operator settings file updated by {Actor}: {Keys}")]
    internal static partial void Written(ILogger logger, string actor, string keys);
}
