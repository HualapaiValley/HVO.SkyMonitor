using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

internal sealed record StagedProjectedSceneDocument(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string StageIdentitySha256,
    [property: JsonRequired] string StageKey,
    [property: JsonRequired] string SceneId,
    [property: JsonRequired] DateTimeOffset EffectiveUtc,
    [property: JsonRequired] ObserverLocation Observer,
    [property: JsonRequired] ProjectedSceneCatalog Catalog,
    [property: JsonRequired] ProjectedSceneSelection Selection,
    [property: JsonRequired] ProjectedSceneProjection Projection,
    [property: JsonRequired] ProjectedSceneImageTransformV1 ImageTransform,
    [property: JsonRequired] HorizonPolicy HorizonPolicy,
    [property: JsonRequired] RefractionOptions Refraction,
    [property: JsonRequired] string AstronomyAlgorithmVersion,
    [property: JsonRequired] ProjectedSceneTopologyProvenance? ConstellationTopology,
    [property: JsonRequired] string? EphemerisModelVersion,
    [property: JsonRequired] IReadOnlyList<ProjectedCelestialObject> Objects,
    [property: JsonRequired] IReadOnlyList<ProjectedConstellationSegment> Segments,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RigProfileSha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FrameLayoutDescriptor? Layout = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectedSceneKind? IntendedKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StageSceneIdentitySha256 = null)
{
    internal const string CurrentSchemaVersion = "projected-scene-stage-v1";

    internal ProjectedSceneV1 Bind(ProjectedSceneSource source, ProjectedSceneKind kind)
    {
        var scene = new ProjectedSceneV1(
            ProjectedSceneV1.CurrentSchemaVersion, string.Empty, kind, EffectiveUtc, Observer, Catalog, Selection,
            Projection, ImageTransform, ProjectedSceneCoordinateConvention.ContinuousTopLeftPixelEdge,
            HorizonPolicy, Refraction, AstronomyAlgorithmVersion, ConstellationTopology, EphemerisModelVersion,
            source, Objects, Segments);
        scene = scene with { SceneIdentitySha256 = ProjectedSceneJson.ComputeIdentity(scene) };
        ProjectedSceneJson.Validate(scene);
        return scene;
    }
}

public interface IProjectedSceneStagingStore
{
    ValueTask StageAsync(string stageKey, string sceneId, VisibleScene scene, CancellationToken cancellationToken);
    ValueTask StageAsync(
        string stageKey,
        string sceneId,
        VisibleScene scene,
        CaptureProjectedSceneStageFacts facts,
        CancellationToken cancellationToken) => StageAsync(stageKey, sceneId, scene, cancellationToken);
    ValueTask DeleteAsync(string stageKey, CancellationToken cancellationToken);
    ValueTask MarkCompletedAsync(string stageKey, CancellationToken cancellationToken);
    ValueTask DeleteCompletedAsync(string stageKey, CancellationToken cancellationToken);
}

public sealed record CaptureProjectedSceneStageFacts(
    string RigProfileSha256,
    FrameLayoutDescriptor Layout,
    ProjectedSceneKind IntendedKind,
    string StageSceneIdentitySha256);

internal interface IProjectedSceneStagingReader
{
    ValueTask<StagedProjectedSceneDocument?> ReadAsync(string stageKey, CancellationToken cancellationToken);
}

internal interface IProjectedSceneStagingReconciler
{
    ValueTask<ProjectedSceneStageReconciliationResult> ReconcileAsync(
        IReadOnlySet<string> ownedStageKeys,
        CancellationToken cancellationToken);
}

internal sealed record ProjectedSceneStageReconciliationResult(int Inspected, int Deleted, int BacklogCount);

internal sealed class ProjectedSceneStagingStore :
    IProjectedSceneStagingStore,
    IProjectedSceneStagingReader,
    IProjectedSceneStagingReconciler,
    IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly ProjectedSceneSource ValidationSource = new(
        new Guid("11111111-1111-1111-1111-111111111111"),
        new Guid("22222222-2222-2222-2222-222222222222"),
        new string('A', 64));
    private readonly string _root;
    private readonly string _durableRoot;
    private readonly string _cursorPath;
    private readonly ProjectedSceneStagingOptions _options;
    private readonly ProjectedSceneStageLifecycleCoordinator _lifecycle;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProjectedSceneStagingStore(
        IOptions<CameraAgentHostOptions> options,
        ProjectedSceneStageLifecycleCoordinator? lifecycle = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _durableRoot = Path.GetFullPath(options.Value.RawIngressRoot);
        _root = Path.GetFullPath(Path.Combine(_durableRoot, "staging", "projected-scenes"));
        _cursorPath = Path.Combine(_durableRoot, "journal", "projected-scene-stage.cursor");
        _options = options.Value.ProjectedSceneStaging;
        _lifecycle = lifecycle ?? new ProjectedSceneStageLifecycleCoordinator();
    }

    public async ValueTask StageAsync(
        string stageKey,
        string sceneId,
        VisibleScene scene,
        CancellationToken cancellationToken)
    {
        ValidateKey(stageKey, nameof(stageKey));
        ValidateKey(sceneId);
        ArgumentNullException.ThrowIfNull(scene);
        await StageCoreDocumentAsync(stageKey, sceneId, scene, facts: null, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask StageAsync(
        string stageKey,
        string sceneId,
        VisibleScene scene,
        CaptureProjectedSceneStageFacts facts,
        CancellationToken cancellationToken)
        => StageCoreDocumentAsync(stageKey, sceneId, scene, facts ?? throw new ArgumentNullException(nameof(facts)), cancellationToken);

    private async ValueTask StageCoreDocumentAsync(
        string stageKey,
        string sceneId,
        VisibleScene scene,
        CaptureProjectedSceneStageFacts? facts,
        CancellationToken cancellationToken)
    {
        ValidateKey(stageKey, nameof(stageKey));
        ValidateKey(sceneId);
        ArgumentNullException.ThrowIfNull(scene);
        if (facts is not null)
        {
            ValidateKey(facts.RigProfileSha256, nameof(facts));
            ValidateKey(facts.StageSceneIdentitySha256, nameof(facts));
            ArgumentNullException.ThrowIfNull(facts.Layout);
            if (!Enum.IsDefined(facts.IntendedKind)) throw new ArgumentOutOfRangeException(nameof(facts));
        }
        var projected = ProjectedSceneJson.Create(
            facts?.IntendedKind ?? ProjectedSceneKind.VirtualRenderAuthoritative,
            scene,
            ProjectedSceneImageTransformV1.Identity(scene.Request.Projection.WidthPixels, scene.Request.Projection.HeightPixels),
            ValidationSource,
            scene.Request.ProjectionVersion,
            scene.Request.ProjectionVersion);
        var document = new StagedProjectedSceneDocument(
            StagedProjectedSceneDocument.CurrentSchemaVersion, string.Empty, stageKey, sceneId, projected.EffectiveUtc,
            projected.Observer, projected.Catalog, projected.Selection, projected.Projection, projected.ImageTransform,
            projected.HorizonPolicy, projected.Refraction, projected.AstronomyAlgorithmVersion,
            projected.ConstellationTopology, projected.EphemerisModelVersion, projected.Objects, projected.Segments,
            facts?.RigProfileSha256, facts?.Layout, facts?.IntendedKind, facts?.StageSceneIdentitySha256);
        document = document with { StageIdentitySha256 = ComputeIdentity(document) };
        var bytes = Serialize(document);

        var registered = await _lifecycle.RegisterPendingAsync(stageKey, cancellationToken).ConfigureAwait(false);

        try
        {
            await StageCoreAsync(stageKey, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (registered) await _lifecycle.ResolvePendingAsync(stageKey).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask StageCoreAsync(string stageKey, byte[] bytes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsurePhysicalDirectory();
            var path = GetPath(stageKey);
            var existing = await ReadBoundedFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.AsSpan().SequenceEqual(bytes)) return;
                throw new InvalidDataException("Projected-scene stage conflicts with existing capture geometry.");
            }
            var files = new DirectoryInfo(_root).EnumerateFiles("*.json", SearchOption.TopDirectoryOnly).ToArray();
            if (files.Length >= _options.MaximumFileCount || files.Sum(static file => file.Length) + bytes.Length > _options.MaximumTotalBytes)
                throw new IOException("Projected-scene staging capacity is exhausted.");
            var temporary = Path.Combine(_root, $".{stageKey}.{Guid.NewGuid():N}.tmp");
            if (OperatingSystem.IsLinux())
            {
                await PublishLinuxAsync(Path.GetFileName(temporary), $"{stageKey}.json", bytes, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            var published = false;
            try
            {
                var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                try
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    RandomAccess.FlushToDisk(stream.SafeFileHandle);
                }
                finally
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                try
                {
                    File.Move(temporary, path);
                    published = true;
                }
                catch (IOException) when (File.Exists(path))
                {
                    var concurrentlyPublished = await ReadBoundedFileAsync(path, cancellationToken).ConfigureAwait(false)
                        ?? throw new IOException("Projected-scene stage disappeared during conflict authentication.");
                    if (!concurrentlyPublished.AsSpan().SequenceEqual(bytes))
                    {
                        throw new InvalidDataException(
                            "Projected-scene stage conflicts with existing capture geometry.");
                    }
                }
                RawIngressFileStore.SyncDirectoryHierarchy(_durableRoot, _root);
            }
            catch
            {
                TryDeleteTemporaryPath(temporary);
                if (published)
                {
                    TryDeleteTemporaryPath(path);
                    try
                    {
                        RawIngressFileStore.SyncDirectoryHierarchy(_durableRoot, _root);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // Reconciliation removes a published stage when rollback durability cannot be confirmed.
                    }
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<StagedProjectedSceneDocument?> ReadAsync(string stageKey, CancellationToken cancellationToken)
    {
        ValidateKey(stageKey, nameof(stageKey));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPath(stageKey);
            var bytes = await ReadBoundedFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return null;
            StagedProjectedSceneDocument document;
            try
            {
                document = JsonSerializer.Deserialize<StagedProjectedSceneDocument>(bytes, SerializerOptions)
                    ?? throw new InvalidDataException("Projected-scene stage is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Projected-scene stage is invalid.", exception);
            }
            if (!bytes.AsSpan().SequenceEqual(Serialize(document)) ||
                document.SchemaVersion != StagedProjectedSceneDocument.CurrentSchemaVersion ||
                document.StageKey != stageKey ||
                !string.Equals(document.StageIdentitySha256, ComputeIdentity(document), StringComparison.Ordinal))
                throw new InvalidDataException("Projected-scene stage identity is invalid.");
            ValidateSemanticIdentity(document);
            return document;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DeleteAsync(string stageKey, CancellationToken cancellationToken)
    {
        ValidateKey(stageKey, nameof(stageKey));
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    using var directory = OpenLinuxStageDirectory(create: false);
                    if (directory is not null && LinuxUnlink(directory, $"{stageKey}.json"))
                        RandomAccess.FlushToDisk(directory);
                }
                else
                {
                    var path = GetPath(stageKey);
                    if (File.Exists(path))
                    {
                        // Non-Linux platforms retain strict reparse-point checks, but cannot make check/open atomic.
                        EnsureAncestorChainHasNoLinks(Path.GetDirectoryName(path)!);
                        EnsurePhysicalFile(path);
                        File.Delete(path);
                        RawIngressFileStore.SyncDirectory(_root);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            await _lifecycle.ResolvePendingAsync(stageKey).ConfigureAwait(false);
        }
    }

    public async ValueTask MarkCompletedAsync(string stageKey, CancellationToken cancellationToken)
    {
        ValidateKey(stageKey, nameof(stageKey));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var activeName = $"{stageKey}.json";
            var completedName = $"{stageKey}.completed.json";
            if (OperatingSystem.IsLinux())
            {
                using var directory = OpenLinuxStageDirectory(create: false);
                if (directory is null) return;
                using var active = LinuxOpenAtFile(directory, activeName);
                if (active is null)
                {
                    using var completed = LinuxOpenAtFile(directory, completedName);
                    if (completed is not null) EnsureLinuxRegularFile(completed);
                    return;
                }
                if (!TryRenameNoReplace(directory, activeName, completedName, out var exists))
                {
                    if (!exists) throw new IOException("Projected-scene completion marker could not be published.");
                    using var completed = LinuxOpenAtFile(directory, completedName)
                        ?? throw new IOException("Projected-scene completion marker disappeared.");
                    var activeBytes = await ReadBoundedHandleAsync(active, cancellationToken).ConfigureAwait(false);
                    var completedBytes = await ReadBoundedHandleAsync(completed, cancellationToken).ConfigureAwait(false);
                    if (!activeBytes.AsSpan().SequenceEqual(completedBytes))
                        throw new InvalidDataException("Projected-scene completion marker conflicts with active evidence.");
                    _ = LinuxUnlink(directory, activeName);
                }
                RandomAccess.FlushToDisk(directory);
                return;
            }

            var activePath = Path.Combine(_root, activeName);
            var completedPath = Path.Combine(_root, completedName);
            EnsureAncestorChainHasNoLinks(_root);
            if (!File.Exists(activePath))
            {
                if (File.Exists(completedPath)) EnsurePhysicalFile(completedPath);
                return;
            }
            EnsurePhysicalFile(activePath);
            try
            {
                File.Move(activePath, completedPath);
            }
            catch (IOException) when (File.Exists(completedPath))
            {
                EnsurePhysicalFile(completedPath);
                var activeBytes = await ReadBoundedFileAsync(activePath, cancellationToken).ConfigureAwait(false);
                var completedBytes = await ReadBoundedFileAsync(completedPath, cancellationToken).ConfigureAwait(false);
                if (activeBytes is null || completedBytes is null || !activeBytes.AsSpan().SequenceEqual(completedBytes))
                    throw new InvalidDataException("Projected-scene completion marker conflicts with active evidence.");
                File.Delete(activePath);
            }
            RawIngressFileStore.SyncDirectory(_root);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DeleteCompletedAsync(string stageKey, CancellationToken cancellationToken)
    {
        ValidateKey(stageKey, nameof(stageKey));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var name = $"{stageKey}.completed.json";
            if (OperatingSystem.IsLinux())
            {
                using var directory = OpenLinuxStageDirectory(create: false);
                if (directory is not null && LinuxUnlink(directory, name)) RandomAccess.FlushToDisk(directory);
                return;
            }
            var path = Path.Combine(_root, name);
            EnsureAncestorChainHasNoLinks(_root);
            if (!File.Exists(path)) return;
            EnsurePhysicalFile(path);
            File.Delete(path);
            RawIngressFileStore.SyncDirectory(_root);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ProjectedSceneStageReconciliationResult> ReconcileAsync(
        IReadOnlySet<string> ownedStageKeys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownedStageKeys);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_root)) return new(0, 0, 0);
            EnsurePhysicalDirectory();
            const int hardGlobalMaximum = 65_536;
            var entries = Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .Take(hardGlobalMaximum + 1)
                .ToArray();
            var overflow = entries.Length > hardGlobalMaximum;
            if (overflow) entries = entries[..hardGlobalMaximum];
            if (entries.Length == 0)
            {
                WriteCursor(new ReconciliationCursor(null, 0));
                return new(0, 0, 0);
            }
            var cursor = ReadCursor();
            var remaining = cursor is { RemainingInspections: > 0 }
                ? cursor.RemainingInspections
                : entries.Length;
            var start = cursor?.LastName is null ? 0 : Array.FindIndex(entries, path =>
                string.CompareOrdinal(Path.GetFileName(path), cursor.LastName) > 0);
            if (start < 0) start = 0;
            var inspected = 0;
            var deleted = 0;
            string? lastName = null;
            var passLimit = Math.Min(_options.MaximumReconciliationEntries, remaining);
            for (var offset = 0; offset < entries.Length && inspected < passLimit; offset++)
            {
                var index = (start + offset) % entries.Length;
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entries[index]);
                lastName = name;
                inspected++;
                if (IsCompletedMarker(name))
                {
                    DeleteEntry(name);
                    deleted++;
                    continue;
                }
                if (name.EndsWith(".tmp", StringComparison.Ordinal) && name.StartsWith('.'))
                {
                    DeleteEntry(name);
                    deleted++;
                    continue;
                }
                if (!name.EndsWith(".json", StringComparison.Ordinal)) continue;
                var stageKey = name[..^5];
                if (stageKey.Length != 64 || stageKey.Any(static value => !Uri.IsHexDigit(value)))
                {
                    DeleteEntry(name);
                    deleted++;
                    continue;
                }
                if (ownedStageKeys.Contains(stageKey))
                    continue;
                try
                {
                    var bytes = await ReadBoundedFileAsync(entries[index], cancellationToken).ConfigureAwait(false);
                    if (bytes is null) continue;
                    var document = JsonSerializer.Deserialize<StagedProjectedSceneDocument>(bytes, SerializerOptions);
                    if (document is null || document.StageKey != stageKey ||
                        document.SchemaVersion != StagedProjectedSceneDocument.CurrentSchemaVersion ||
                        !bytes.AsSpan().SequenceEqual(Serialize(document)) ||
                        !string.Equals(document.StageIdentitySha256, ComputeIdentity(document), StringComparison.Ordinal))
                        throw new InvalidDataException("Projected-scene stage identity is invalid.");
                    ValidateSemanticIdentity(document);
                    DeleteEntry(name);
                    deleted++;
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
                {
                    DeleteEntry(name);
                    deleted++;
                }
            }
            remaining = Math.Max(0, remaining - inspected);
            WriteCursor(new ReconciliationCursor(lastName, remaining));
            var backlog = overflow && remaining == 0 ? 1 : remaining;
            return new(inspected, deleted, backlog);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsCompletedMarker(string name)
    {
        const string suffix = ".completed.json";
        if (!name.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var key = name[..^suffix.Length];
        return key.Length == 64 && key.All(Uri.IsHexDigit);
    }

    private ReconciliationCursor? ReadCursor()
    {
        try
        {
            if (!File.Exists(_cursorPath)) return null;
            EnsureAncestorChainHasNoLinks(Path.GetDirectoryName(_cursorPath)!);
            var value = File.ReadAllBytes(_cursorPath);
            if (value.Length is < 2 or > 512) return null;
            var cursor = JsonSerializer.Deserialize<ReconciliationCursor>(value, SerializerOptions);
            return cursor is { RemainingInspections: >= 0 } &&
                (cursor.LastName is null || cursor.LastName.Length is > 0 and <= 128 && !cursor.LastName.Any(char.IsControl))
                ? cursor
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteCursor(ReconciliationCursor value)
    {
        var directory = Path.GetDirectoryName(_cursorPath)!;
        Directory.CreateDirectory(directory);
        EnsureAncestorChainHasNoLinks(directory);
        var temporary = $"{_cursorPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions));
            RawIngressFileStore.SyncFile(_durableRoot, temporary);
            File.Move(temporary, _cursorPath, overwrite: true);
            RawIngressFileStore.SyncDirectory(directory);
        }
        finally
        {
            TryDeleteTemporaryPath(temporary);
        }
    }

    private sealed record ReconciliationCursor(string? LastName, int RemainingInspections);

    private static void TryDeleteTemporaryPath(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static byte[] Serialize(StagedProjectedSceneDocument document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(document, SerializerOptions)));
        if (bytes.Length > ProjectedSceneJson.MaximumPayloadBytes) throw new ArgumentException("Projected-scene stage exceeds 4 MiB.", nameof(document));
        return bytes;
    }

    private static string ComputeIdentity(StagedProjectedSceneDocument document) =>
        CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(
            document with { StageIdentitySha256 = string.Empty }, SerializerOptions));

    private static void ValidateSemanticIdentity(StagedProjectedSceneDocument document)
    {
        var semantic = document.Bind(
            ValidationSource,
            document.IntendedKind ?? ProjectedSceneKind.VirtualRenderAuthoritative);
        if (document.StageSceneIdentitySha256 is { } identity &&
            !string.Equals(identity, semantic.SceneIdentitySha256, StringComparison.Ordinal))
            throw new InvalidDataException("Projected-scene stage semantic identity is invalid.");
    }

    private string GetPath(string stageKey) => Path.Combine(_root, $"{stageKey}.json");

    private void EnsurePhysicalDirectory()
    {
        var existed = Directory.Exists(_root);
        EnsureAncestorChainHasNoLinks(_root);
        Directory.CreateDirectory(_root);
        EnsureAncestorChainHasNoLinks(_root);
        if (!existed) RawIngressFileStore.SyncDirectoryHierarchy(_durableRoot, _root);
    }

    private static void EnsurePhysicalFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Projected-scene stage cannot be a link.");
    }

    private static void ValidateKey(string value, string parameterName = "sceneId")
    {
        if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Stage and scene identities must be SHA-256 values.", parameterName);
    }

    internal static void EnsureAncestorChainHasNoLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Projected-scene staging path cannot contain links.");
    }

    private async ValueTask<byte[]?> ReadBoundedFileAsync(string path, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            var fileName = Path.GetFileName(path);
            try
            {
                using var sceneHandle = OpenLinuxStageDirectory(create: false);
                if (sceneHandle is null) return null;
                using var fileHandle = LinuxOpenAtFile(sceneHandle, fileName);
                if (fileHandle is null) return null;
                return await ReadBoundedHandleAsync(fileHandle, cancellationToken).ConfigureAwait(false);
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }
        try
        {
            EnsureAncestorChainHasNoLinks(Path.GetDirectoryName(path)!);
            EnsurePhysicalFile(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        try
        {
            using var stream = File.Open(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            if (stream.Length is < 1 or > ProjectedSceneJson.MaximumPayloadBytes)
                throw new InvalidDataException("Projected-scene stage exceeds its payload bound.");
            var length = checked((int)stream.Length);
            var bytes = GC.AllocateUninitializedArray<byte>(length);
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (stream.Length != length || stream.ReadByte() != -1)
                throw new InvalidDataException("Projected-scene stage changed while it was read.");
            return bytes;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static async ValueTask<byte[]> ReadBoundedHandleAsync(
        SafeFileHandle handle,
        CancellationToken cancellationToken)
    {
        EnsureLinuxRegularFile(handle);
        var lengthValue = RandomAccess.GetLength(handle);
        if (lengthValue is < 1 or > ProjectedSceneJson.MaximumPayloadBytes)
            throw new InvalidDataException("Projected-scene stage exceeds its payload bound.");
        var length = checked((int)lengthValue);
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        var offset = 0;
        while (offset < length)
        {
            var read = await RandomAccess.ReadAsync(handle, bytes.AsMemory(offset), offset, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Projected-scene stage ended during bounded read.");
            offset += read;
        }
        if (RandomAccess.GetLength(handle) != length)
            throw new InvalidDataException("Projected-scene stage changed while it was read.");
        return bytes;
    }

    private void DeleteEntry(string name)
    {
        if (OperatingSystem.IsLinux())
        {
            using var directory = OpenLinuxStageDirectory(create: false);
            if (directory is not null && LinuxUnlink(directory, name)) RandomAccess.FlushToDisk(directory);
            return;
        }
        var path = Path.Combine(_root, name);
        EnsureAncestorChainHasNoLinks(Path.GetDirectoryName(path)!);
        EnsurePhysicalFile(path);
        File.Delete(path);
        RawIngressFileStore.SyncDirectory(_root);
    }

    private SafeFileHandle? OpenLinuxStageDirectory(bool create)
    {
        if (create) EnsurePhysicalDirectory();
        try
        {
            var root = LinuxOpenAbsoluteDirectoryComponents(_durableRoot);
            try
            {
                var staging = LinuxOpenAtDirectory(root, "staging");
                try
                {
                    return LinuxOpenAtDirectory(staging, "projected-scenes");
                }
                finally
                {
                    staging.Dispose();
                }
            }
            finally
            {
                root.Dispose();
            }
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static SafeFileHandle LinuxOpenDirectory(string path)
        => LinuxOpen(path, StagingOpenFlags.ReadOnly | LinuxDirectoryFlag() |
            LinuxNoFollowFlag() | StagingOpenFlags.CloseOnExec, directory: true);

    private static SafeFileHandle LinuxOpenAbsoluteDirectoryComponents(string path)
    {
        path = Path.GetFullPath(path);
        var current = LinuxOpenDirectory(Path.GetPathRoot(path)!);
        try
        {
            foreach (var component in path[Path.GetPathRoot(path)!.Length..]
                         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = LinuxOpenAtDirectory(current, component);
                current.Dispose();
                current = next;
            }
            var result = current;
            current = null!;
            return result;
        }
        finally
        {
            current?.Dispose();
        }
    }

    private static SafeFileHandle LinuxOpenAtDirectory(SafeFileHandle parent, string name)
        => LinuxOpenAt(parent, name, StagingOpenFlags.ReadOnly | LinuxDirectoryFlag() |
            LinuxNoFollowFlag() | StagingOpenFlags.CloseOnExec, directory: true)!;

    private static StagingOpenFlags LinuxDirectoryFlag()
        => (StagingOpenFlags)Storage.LinuxOpenFlags.Directory;

    private static StagingOpenFlags LinuxNoFollowFlag()
        => (StagingOpenFlags)Storage.LinuxOpenFlags.NoFollow;

    private static SafeFileHandle? LinuxOpenAtFile(SafeFileHandle parent, string name)
        => LinuxOpenAt(parent, name, StagingOpenFlags.ReadOnly | LinuxNoFollowFlag() |
            StagingOpenFlags.CloseOnExec, directory: false);

    private async ValueTask PublishLinuxAsync(
        string temporaryName,
        string finalName,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        using var directory = OpenLinuxStageDirectory(create: true)!;
        var published = false;
        SafeFileHandle temporary = LinuxOpenAt(
            directory,
            temporaryName,
            StagingOpenFlags.WriteOnly | StagingOpenFlags.Create | StagingOpenFlags.Exclusive |
                LinuxNoFollowFlag() | StagingOpenFlags.CloseOnExec,
            directory: false,
            mode: 0x180)
            ?? throw new IOException("Projected-scene temporary stage could not be created.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RandomAccess.Write(temporary, bytes, 0);
            RandomAccess.FlushToDisk(temporary);
            temporary.Dispose();
            if (!TryRenameNoReplace(directory, temporaryName, finalName, out var exists))
            {
                if (!exists)
                    throw new IOException("Projected-scene stage could not be published safely.");
                using var existing = LinuxOpenAtFile(directory, finalName)
                    ?? throw new IOException("Projected-scene stage disappeared during conflict authentication.");
                var existingBytes = await ReadBoundedHandleAsync(existing, cancellationToken).ConfigureAwait(false);
                if (!existingBytes.AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException("Projected-scene stage conflicts with existing capture geometry.");
            }
            else
            {
                published = true;
            }
            RandomAccess.FlushToDisk(directory);
        }
        catch
        {
            if (published)
            {
                try
                {
                    _ = LinuxUnlink(directory, finalName);
                    RandomAccess.FlushToDisk(directory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Reconciliation removes a published stage when rollback durability cannot be confirmed.
                }
            }
            throw;
        }
        finally
        {
            temporary.Dispose();
            try
            {
                _ = LinuxUnlink(directory, temporaryName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The primary publication failure or cancellation remains authoritative; reconciliation removes the temp.
            }
        }
    }

    private static SafeFileHandle LinuxOpen(string path, StagingOpenFlags flags, bool directory)
    {
        var descriptor = Open(path, (int)flags);
        if (descriptor >= 0) return new SafeFileHandle((nint)descriptor, ownsHandle: true);
        throw LinuxOpenException(directory);
    }

    private static SafeFileHandle? LinuxOpenAt(
        SafeFileHandle parent,
        string name,
        StagingOpenFlags flags,
        bool directory,
        int mode = 0)
    {
        var descriptor = OpenAt(parent, name, (int)flags, mode);
        if (descriptor >= 0) return new SafeFileHandle((nint)descriptor, ownsHandle: true);
        var error = Marshal.GetLastPInvokeError();
        if (!directory && error == 2) return null;
        throw LinuxOpenException(directory, error);
    }

    private static Exception LinuxOpenException(bool directory, int? error = null)
    {
        var nativeError = error ?? Marshal.GetLastPInvokeError();
        return nativeError == 2
            ? new DirectoryNotFoundException("Projected-scene staging directory is unavailable.")
            : nativeError is 40 or 20
                ? new UnauthorizedAccessException("Projected-scene staging path contains a link or non-directory component.")
                : new IOException("Projected-scene staging evidence could not be opened safely.", new Win32Exception(nativeError));
    }

    private static void EnsureLinuxRegularFile(SafeFileHandle handle)
    {
        if (StatX(handle, string.Empty, 0x1000, 0x1, out var stat) != 0 || (stat.Mode & 0xF000) != 0x8000)
            throw new InvalidDataException("Projected-scene stage is not a regular file.");
    }

    private static bool LinuxUnlink(SafeFileHandle directory, string name)
    {
        if (UnlinkAt(directory, name, 0) == 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == 2) return false;
        throw new IOException("Projected-scene stage could not be removed safely.", new Win32Exception(error));
    }

    private static bool TryRenameNoReplace(
        SafeFileHandle directory,
        string temporaryName,
        string finalName,
        out bool exists)
    {
        const uint renameNoReplace = 1;
        int result;
        try
        {
            result = RenameAt2(directory, temporaryName, directory, finalName, renameNoReplace);
        }
        catch (EntryPointNotFoundException)
        {
            result = -1;
            Marshal.SetLastPInvokeError(38);
        }
        if (result == 0)
        {
            exists = false;
            return true;
        }
        var error = Marshal.GetLastPInvokeError();
        if (error == 17)
        {
            exists = true;
            return false;
        }
        if (error is not (38 or 22 or 95))
            throw new IOException("Projected-scene no-replace publication failed.", new Win32Exception(error));
        if (LinkAt(directory, temporaryName, directory, finalName, 0) == 0)
        {
            _ = LinuxUnlink(directory, temporaryName);
            exists = false;
            return true;
        }
        error = Marshal.GetLastPInvokeError();
        if (error == 17)
        {
            exists = true;
            return false;
        }
        throw new IOException("Projected-scene no-replace fallback failed.", new Win32Exception(error));
    }

    [Flags]
    private enum StagingOpenFlags
    {
        ReadOnly = 0,
        WriteOnly = 1,
        Create = 0x40,
        Exclusive = 0x80,
        CloseOnExec = 0x80000
    }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct LinuxStatX
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
    }

#pragma warning disable SYSLIB1054
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "openat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int OpenAt(SafeFileHandle directory, string path, int flags, int mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "unlinkat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int UnlinkAt(SafeFileHandle directory, string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "renameat2", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int RenameAt2(
        SafeFileHandle oldDirectory,
        string oldPath,
        SafeFileHandle newDirectory,
        string newPath,
        uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "linkat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int LinkAt(
        SafeFileHandle oldDirectory,
        string oldPath,
        SafeFileHandle newDirectory,
        string newPath,
        int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int StatX(SafeFileHandle directory, string path, int flags, uint mask, out LinuxStatX stat);
#pragma warning restore SYSLIB1054

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public void Dispose() => _gate.Dispose();
}
