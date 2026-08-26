using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum CentralLayeredPresentationStatus
{
    Found,
    Unavailable,
    Malformed,
    TooLarge,
    DependencyUnavailable
}

internal sealed record CentralLayeredPresentation(
    Guid CaptureId,
    Guid DevicePublicId,
    Guid BaseArtifactId,
    string BaseMediaType,
    string ManifestIdentitySha256,
    string PresentationIdentitySha256,
    string SvgChecksumSha256,
    int WidthPixels,
    int HeightPixels,
    IReadOnlyList<GroupedSvgPresentationLayer> Layers,
    byte[] Svg)
{
    public string BaseContentPath => FormattableString.Invariant(
        $"/api/v1.0/captures/{CaptureId:D}/presentation/base");

    public string SvgPath => FormattableString.Invariant(
        $"/api/v1.0/captures/{CaptureId:D}/presentation.svg");
}

internal sealed record CentralLayeredPresentationResult(
    CentralLayeredPresentationStatus Status,
    CentralLayeredPresentation? Presentation = null);

internal interface ICentralLayeredPresentationService
{
    Task<CentralLayeredPresentationResult> GetAsync(
        Guid captureId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}

internal sealed class CentralLayeredPresentationService(
    ApplicationDbContext dbContext,
    ICentralArtifactObjectReader objectReader,
    IDistributedCache distributedCache,
    CentralLayeredPresentationCache memoryCache,
    CentralPresentationTelemetry telemetry,
    TimeProvider timeProvider) : ICentralLayeredPresentationService
{
    private const int MaximumSourcePayloadBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan DistributedCacheTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CentralLayeredPresentationResult> GetAsync(
        Guid captureId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (captureId == Guid.Empty || !CentralArtifactCredentialAccess.HasOwnerCredential(principal) ||
            CentralArtifactCredentialAccess.GetOwnerId(principal) is not { } ownerId)
        {
            return new(CentralLayeredPresentationStatus.Unavailable);
        }

        var observatories = ObservatoryMembershipAccess.ForUser(dbContext, ownerId)
            .Select(static membership => membership.ObservatoryId);
        var observatoryScope = CentralArtifactCredentialAccess.GetObservatoryScope(principal);
        var manifests = await dbContext.CentralArtifacts.AsNoTracking()
            .Include(static artifact => artifact.Frame)
            .Include(static artifact => artifact.StructuredProduct)
            .Where(artifact => artifact.CentralFrameId == captureId &&
                observatories.Contains(artifact.Frame!.ObservatoryId) &&
                (observatoryScope == null || artifact.Frame.ObservatoryId == observatoryScope) &&
                artifact.ObjectState == CentralArtifactObjectState.Available &&
                artifact.ReconstructionState == CentralReconstructionState.Complete &&
                artifact.MediaType == PresentationProcessingProducts.ManifestMediaType &&
                artifact.StructuredProduct != null &&
                artifact.StructuredProduct.ProductSchemaVersion == OverlayManifestV1.CurrentSchemaVersion)
            .OrderByDescending(static artifact => artifact.CreatedUtc)
            .ThenByDescending(static artifact => artifact.ArtifactId)
            .Take(1)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (manifests.Length == 0)
        {
            return new(CentralLayeredPresentationStatus.Unavailable);
        }
        var manifestArtifact = manifests[0];
        var cacheKey = CreateCacheKey(captureId, manifestArtifact.ArtifactId, manifestArtifact.ChecksumSha256);
        if (memoryCache.TryGet(cacheKey, out var memoryHit))
        {
            if (IsValidCached(memoryHit, captureId, manifestArtifact) &&
                await MatchesAuthoritativeInputsAsync(
                    memoryHit, manifestArtifact, cancellationToken).ConfigureAwait(false))
            {
                telemetry.RecordCache("memory", "hit");
                return new(CentralLayeredPresentationStatus.Found, memoryHit);
            }
            memoryCache.Remove(cacheKey);
            telemetry.RecordCache("memory", "evicted");
        }
        telemetry.RecordCache("memory", "miss");
        var distributed = await ReadDistributedAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (distributed is { Length: > 0 and <= GroupedSvgPresentationRenderer.MaximumSvgBytes * 2 })
        {
            try
            {
                var cached = JsonSerializer.Deserialize<CentralLayeredPresentation>(distributed, CacheJsonOptions);
                if (cached is not null && IsValidCached(cached, captureId, manifestArtifact) &&
                    await MatchesAuthoritativeInputsAsync(cached, manifestArtifact, cancellationToken).ConfigureAwait(false))
                {
                    memoryCache.Add(cacheKey, cached);
                    telemetry.RecordCache("distributed", "hit");
                    return new(CentralLayeredPresentationStatus.Found, cached);
                }
            }
            catch (JsonException)
            {
                await RemoveDistributedAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            }
        }
        telemetry.RecordCache("distributed", "miss");

        return await memoryCache.RunSingleFlightAsync(
            cacheKey,
            captureId,
            manifestArtifact,
            cancellationToken).ConfigureAwait(false);
    }

    internal static string CreateCacheKey(Guid captureId, Guid manifestArtifactId, string manifestChecksumSha256) =>
        $"central-presentation-v2:{GroupedSvgPresentationRenderer.RendererVersion}:" +
        $"{captureId:D}:{manifestArtifactId:D}:{manifestChecksumSha256}";

    internal async Task<CentralLayeredPresentationResult> BuildAsync(
        Guid captureId,
        CentralArtifact manifestArtifact,
        string cacheKey)
    {
        var started = timeProvider.GetTimestamp();
        using var generationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var generationToken = generationTimeout.Token;
        CentralLayeredPresentationResult Fail(CentralLayeredPresentationStatus status, string outcome)
        {
            telemetry.RecordGeneration(outcome, 0, timeProvider.GetElapsedTime(started));
            return new(status);
        }
        try
        {
            var manifestBytes = await ReadBytesAsync(
                    manifestArtifact, LayeredPresentationJson.MaximumPayloadBytes, generationToken)
                .ConfigureAwait(false);
            var parsedManifest = LayeredPresentationJson.ParseManifest(manifestBytes);
            if (!parsedManifest.IsValid || parsedManifest.Document is not { } manifest ||
                !string.Equals(manifest.ManifestIdentitySha256,
                    manifestArtifact.StructuredProduct!.ContentIdentitySha256, StringComparison.Ordinal))
            {
                return Fail(CentralLayeredPresentationStatus.Malformed, "malformed");
            }

            var referencedIds = manifest.Layers.Select(static layer => layer.SourceProduct.ArtifactId)
                .Append(manifest.BaseProduct.ArtifactId)
                .ToArray();
            var referenced = await dbContext.CentralArtifacts.AsNoTracking()
                .Include(static artifact => artifact.Frame)
                .Include(static artifact => artifact.Layout)
                .Where(artifact => artifact.CentralFrameId == captureId &&
                    referencedIds.Contains(artifact.ArtifactId) &&
                    artifact.ObjectState == CentralArtifactObjectState.Available &&
                    artifact.ReconstructionState == CentralReconstructionState.Complete)
                .ToDictionaryAsync(static artifact => artifact.ArtifactId, generationToken)
                .ConfigureAwait(false);
            if (referenced.Count != referencedIds.Distinct().Count() ||
                !referenced.TryGetValue(manifest.BaseProduct.ArtifactId, out var baseArtifact) ||
                baseArtifact.Role != FrameArtifactRole.Preview ||
                !string.Equals(baseArtifact.MediaType, manifest.BaseProduct.MediaType, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(baseArtifact.ChecksumSha256,
                    manifest.BaseProduct.ProductIdentitySha256, StringComparison.OrdinalIgnoreCase) ||
                baseArtifact.Layout is not { } baseLayout ||
                baseLayout.Width != manifest.BaseProduct.Compatibility.WidthPixels ||
                baseLayout.Height != manifest.BaseProduct.Compatibility.HeightPixels ||
                !string.Equals(PresentationProcessingProducts.ComputeLayoutIdentity(
                        CentralReconstructionDescriptorFactory.CreateLayout(baseLayout)),
                    manifest.BaseProduct.Compatibility.LayoutIdentitySha256, StringComparison.Ordinal))
            {
                return Fail(CentralLayeredPresentationStatus.Malformed, "malformed");
            }

            var payloads = new List<HVO.SkyMonitor.Imaging.PresentationLayerPayloadV1>(manifest.Layers.Count);
            var sourceBytes = 0;
            foreach (var layer in manifest.Layers)
            {
                if (!referenced.TryGetValue(layer.SourceProduct.ArtifactId, out var artifact) ||
                    !string.Equals(artifact.MediaType, PresentationLayerPayloadJson.MediaType, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(CentralLayeredPresentationStatus.Malformed, "malformed");
                }
                var remaining = MaximumSourcePayloadBytes - sourceBytes;
                if (remaining <= 0 || artifact.ByteLength > remaining)
                {
                    return Fail(CentralLayeredPresentationStatus.TooLarge, "too-large");
                }
                var bytes = await ReadBytesAsync(artifact, remaining, generationToken).ConfigureAwait(false);
                sourceBytes = checked(sourceBytes + bytes.Length);
                var payload = PresentationLayerPayloadJson.Parse(bytes).Payload;
                if (payload is null ||
                    !string.Equals(payload.ContentIdentitySha256,
                        layer.SourceProduct.ProductIdentitySha256, StringComparison.Ordinal) ||
                    payload.WidthPixels != manifest.BaseProduct.Compatibility.WidthPixels ||
                    payload.HeightPixels != manifest.BaseProduct.Compatibility.HeightPixels)
                {
                    return Fail(CentralLayeredPresentationStatus.Malformed, "malformed");
                }
                payloads.Add(payload);
            }

            var rendered = GroupedSvgPresentationRenderer.Render(manifest, payloads, baseArtifact.ChecksumSha256);
            var presentation = new CentralLayeredPresentation(
                captureId,
                baseArtifact.Frame!.DevicePublicId,
                baseArtifact.ArtifactId,
                baseArtifact.MediaType,
                manifest.ManifestIdentitySha256,
                rendered.PresentationIdentitySha256,
                rendered.SvgChecksumSha256,
                rendered.WidthPixels,
                rendered.HeightPixels,
                rendered.Layers,
                rendered.Svg.ToArray());
            memoryCache.Add(cacheKey, presentation);
            var serialized = JsonSerializer.SerializeToUtf8Bytes(presentation, CacheJsonOptions);
            await WriteDistributedAsync(cacheKey, serialized, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7)
            }).ConfigureAwait(false);
            telemetry.RecordGeneration("completed", presentation.Svg.Length, timeProvider.GetElapsedTime(started));
            return new(CentralLayeredPresentationStatus.Found, presentation);
        }
        catch (Exception exception) when (exception is CentralArtifactMissingException or CentralArtifactStorageException)
        {
            telemetry.RecordGeneration("unavailable", 0, timeProvider.GetElapsedTime(started));
            return new(CentralLayeredPresentationStatus.DependencyUnavailable);
        }
        catch (OperationCanceledException) when (generationTimeout.IsCancellationRequested)
        {
            telemetry.RecordGeneration("timeout", 0, timeProvider.GetElapsedTime(started));
            return new(CentralLayeredPresentationStatus.DependencyUnavailable);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or
            InvalidOperationException or OverflowException or CentralArtifactIntegrityException)
        {
            telemetry.RecordGeneration("failed", 0, timeProvider.GetElapsedTime(started));
            return new(exception is InvalidDataException
                ? CentralLayeredPresentationStatus.TooLarge
                : CentralLayeredPresentationStatus.Malformed);
        }
    }

    private async Task<byte[]> ReadBytesAsync(
        CentralArtifact artifact,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (artifact.ByteLength is < 1 || artifact.ByteLength > maximumBytes || artifact.ByteLength > int.MaxValue)
        {
            throw new InvalidDataException("The presentation product exceeds its payload bound.");
        }
        var snapshot = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream(checked((int)artifact.ByteLength));
        await objectReader.CopyToAsync(snapshot, destination, null, cancellationToken).ConfigureAwait(false);
        return destination.ToArray();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The disposable presentation cache is best-effort and authoritative products remain in SQL and object storage.")]
    private async Task<byte[]?> ReadDistributedAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DistributedCacheTimeout);
            return await distributedCache.GetAsync(key, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The disposable presentation cache is best-effort and authoritative products remain in SQL and object storage.")]
    private async Task WriteDistributedAsync(
        string key,
        byte[] value,
        DistributedCacheEntryOptions options)
    {
        try
        {
            using var timeout = new CancellationTokenSource(DistributedCacheTimeout);
            await distributedCache.SetAsync(key, value, options, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Corrupt disposable cache entries may be ignored when the cache itself is unavailable.")]
    private async Task RemoveDistributedAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DistributedCacheTimeout);
            await distributedCache.RemoveAsync(key, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool IsValidCached(
        CentralLayeredPresentation cached,
        Guid captureId,
        CentralArtifact manifestArtifact)
    {
        if (cached.CaptureId != captureId || cached.BaseArtifactId == Guid.Empty ||
            cached.Svg.Length is < 1 or > GroupedSvgPresentationRenderer.MaximumSvgBytes ||
            cached.Layers.Count > LayeredPresentationJson.MaximumLayerCount ||
            !string.Equals(cached.ManifestIdentitySha256,
                manifestArtifact.StructuredProduct!.ContentIdentitySha256, StringComparison.Ordinal) ||
            !string.Equals(cached.SvgChecksumSha256,
                Convert.ToHexString(SHA256.HashData(cached.Svg)), StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            using var text = new StringReader(System.Text.Encoding.UTF8.GetString(cached.Svg));
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            XNamespace svg = "http://www.w3.org/2000/svg";
            if (document.Root?.Name != svg + "svg" ||
                document.Root.Attribute("data-presentation-identity")?.Value != cached.PresentationIdentitySha256)
            {
                return false;
            }
            var allowedElements = new HashSet<string>(StringComparer.Ordinal)
            {
                "svg", "g", "line", "ellipse", "rect", "circle", "text", "path"
            };
            if (document.Root.DescendantsAndSelf().Any(element =>
                    element.Name.Namespace != svg || !allowedElements.Contains(element.Name.LocalName) ||
                    element.Attributes().Any(attribute =>
                        !attribute.IsNamespaceDeclaration &&
                        (attribute.Name.Namespace != XNamespace.None ||
                         attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                         attribute.Name.LocalName.Contains("href", StringComparison.OrdinalIgnoreCase) ||
                         attribute.Name.LocalName == "style" &&
                         attribute.Value is not ("mix-blend-mode:multiply" or "mix-blend-mode:screen" or
                              "mix-blend-mode:lighten")))))
            {
                return false;
            }
            var groups = document.Root.Elements(svg + "g").ToArray();
            return groups.Length == cached.Layers.Count && groups.Select(group =>
                    group.Attribute("data-layer-identity")?.Value)
                .SequenceEqual(cached.Layers.Select(static layer => layer.IdentitySha256));
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<bool> MatchesAuthoritativeInputsAsync(
        CentralLayeredPresentation cached,
        CentralArtifact manifestArtifact,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await ReadBytesAsync(
                    manifestArtifact, LayeredPresentationJson.MaximumPayloadBytes, cancellationToken)
                .ConfigureAwait(false);
            var parsed = LayeredPresentationJson.ParseManifest(bytes);
            if (!parsed.IsValid || parsed.Document is not { } manifest ||
                manifest.ManifestIdentitySha256 != cached.ManifestIdentitySha256 ||
                manifest.BaseProduct.ArtifactId != cached.BaseArtifactId ||
                manifest.BaseProduct.MediaType != cached.BaseMediaType ||
                manifest.BaseProduct.Compatibility.WidthPixels != cached.WidthPixels ||
                manifest.BaseProduct.Compatibility.HeightPixels != cached.HeightPixels ||
                manifest.Layers.Count != cached.Layers.Count)
            {
                return false;
            }
            var referencedIds = manifest.Layers.Select(static layer => layer.SourceProduct.ArtifactId)
                .Append(manifest.BaseProduct.ArtifactId)
                .Distinct()
                .ToArray();
            var artifacts = await dbContext.CentralArtifacts.AsNoTracking()
                .Include(static artifact => artifact.Layout)
                .Include(static artifact => artifact.StructuredProduct)
                .Where(artifact => artifact.CentralFrameId == cached.CaptureId &&
                    referencedIds.Contains(artifact.ArtifactId) &&
                    artifact.ObjectState == CentralArtifactObjectState.Available &&
                    artifact.ReconstructionState == CentralReconstructionState.Complete)
                .ToDictionaryAsync(static artifact => artifact.ArtifactId, cancellationToken).ConfigureAwait(false);
            if (artifacts.Count != referencedIds.Length ||
                !artifacts.TryGetValue(manifest.BaseProduct.ArtifactId, out var baseArtifact) ||
                baseArtifact.Role != FrameArtifactRole.Preview ||
                baseArtifact.MediaType != manifest.BaseProduct.MediaType ||
                !string.Equals(baseArtifact.ChecksumSha256,
                    manifest.BaseProduct.ProductIdentitySha256, StringComparison.OrdinalIgnoreCase) ||
                baseArtifact.Layout is not { } baseLayout ||
                PresentationProcessingProducts.ComputeLayoutIdentity(
                    CentralReconstructionDescriptorFactory.CreateLayout(baseLayout)) !=
                manifest.BaseProduct.Compatibility.LayoutIdentitySha256 ||
                manifest.Layers.Any(layer =>
                    !artifacts.TryGetValue(layer.SourceProduct.ArtifactId, out var artifact) ||
                    artifact.MediaType != layer.SourceProduct.MediaType ||
                    artifact.StructuredProduct?.ProductSchemaVersion != PresentationLayerPayloadV1.CurrentSchemaVersion ||
                    artifact.StructuredProduct.ContentIdentitySha256 != layer.SourceProduct.ProductIdentitySha256))
            {
                return false;
            }
            return GroupedSvgPresentationRenderer.ComputeIdentity(manifest, baseArtifact.ChecksumSha256) ==
                cached.PresentationIdentitySha256 &&
                manifest.Layers.Zip(cached.Layers).All(pair =>
                    pair.First.LayerIdentitySha256 == pair.Second.IdentitySha256 &&
                    pair.First.LayerKind == pair.Second.Kind &&
                    pair.First.ZOrder == pair.Second.ZOrder &&
                    pair.First.EnabledByDefault == pair.Second.EnabledByDefault &&
                    pair.First.OpacityMillionths == pair.Second.OpacityMillionths &&
                    pair.First.RendererVersion == pair.Second.RendererVersion &&
                    pair.First.StyleVersion == pair.Second.StyleVersion);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or
            InvalidOperationException or OverflowException or CentralArtifactIntegrityException or
            CentralArtifactMissingException or CentralArtifactStorageException)
        {
            return false;
        }
    }
}

internal sealed class CentralLayeredPresentationCache(
    IServiceScopeFactory scopeFactory,
    CentralPresentationTelemetry telemetry,
    CentralPresentationGenerationGate generationGate)
{
    private const int MaximumEntries = 64;
    private const long MaximumBytes = 16L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<CentralLayeredPresentationResult>>> _flights =
        new(StringComparer.Ordinal);
    private long _bytes;
    private long _sequence;

    public bool TryGet(string key, out CentralLayeredPresentation presentation)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Sequence = ++_sequence;
                presentation = entry.Presentation;
                return true;
            }
        }
        presentation = null!;
        return false;
    }

    public void Add(string key, CentralLayeredPresentation presentation)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(key) || presentation.Svg.LongLength > MaximumBytes)
            {
                return;
            }
            while (_entries.Count >= MaximumEntries || _bytes + presentation.Svg.LongLength > MaximumBytes)
            {
                var oldest = _entries.MinBy(static pair => pair.Value.Sequence);
                if (oldest.Key is null)
                {
                    break;
                }
                _entries.Remove(oldest.Key);
                _bytes -= oldest.Value.Presentation.Svg.LongLength;
                telemetry.RecordCache("memory", "evicted");
            }
            _entries.Add(key, new(presentation, ++_sequence));
            _bytes += presentation.Svg.LongLength;
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (_entries.Remove(key, out var entry))
            {
                _bytes -= entry.Presentation.Svg.LongLength;
            }
        }
    }

    public async Task<CentralLayeredPresentationResult> RunSingleFlightAsync(
        string key,
        Guid captureId,
        CentralArtifact manifestArtifact,
        CancellationToken cancellationToken)
    {
        var flight = _flights.GetOrAdd(key, _ => new(
            () => RunAsync(captureId, manifestArtifact, key),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var task = flight.Value;
        _ = task.ContinueWith(
            static (_, state) =>
            {
                var removal = ((CentralLayeredPresentationCache Cache, string Key,
                    Lazy<Task<CentralLayeredPresentationResult>> Flight))state!;
                removal.Cache._flights.TryRemove(new(removal.Key, removal.Flight));
            },
            (this, key, flight),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<CentralLayeredPresentationResult> RunAsync(
        Guid captureId,
        CentralArtifact manifestArtifact,
        string key)
    {
        using var lease = await generationGate.TryEnterAsync().ConfigureAwait(false);
        if (lease is null)
        {
            telemetry.RecordGeneration("overloaded", 0, TimeSpan.Zero);
            return new(CentralLayeredPresentationStatus.DependencyUnavailable);
        }
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CentralLayeredPresentationService>()
            .BuildAsync(captureId, manifestArtifact, key).ConfigureAwait(false);
    }

    private sealed class CacheEntry(CentralLayeredPresentation presentation, long sequence)
    {
        public CentralLayeredPresentation Presentation { get; } = presentation;
        public long Sequence { get; set; } = sequence;
    }
}

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification =
    "This process-lifetime gate must remain usable until detached presentation flights finish during shutdown.")]
internal sealed class CentralPresentationGenerationGate
{
    internal const int MaximumConcurrent = 2;
    internal const int MaximumAdmitted = 18;
    private readonly SemaphoreSlim _semaphore = new(MaximumConcurrent, MaximumConcurrent);
    private int _admitted;

    public async ValueTask<IDisposable?> TryEnterAsync()
    {
        if (Interlocked.Increment(ref _admitted) > MaximumAdmitted)
        {
            Interlocked.Decrement(ref _admitted);
            return null;
        }

        try
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            return new Lease(this);
        }
        catch
        {
            Interlocked.Decrement(ref _admitted);
            throw;
        }
    }

    private sealed class Lease(CentralPresentationGenerationGate owner) : IDisposable
    {
        private CentralPresentationGenerationGate? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }
            Interlocked.Decrement(ref owner._admitted);
            owner._semaphore.Release();
        }
    }
}
