using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Distribution;

namespace HVO.SkyMonitor.Deployment;

internal sealed class DistributionCatalogAcquirer : IDisposable
{
    private const int MaximumRedirects = 5;
    private const int MaximumAttempts = 3;
    private const long MaximumCacheBytes = 40L * 1024 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AssetAttemptTimeout = TimeSpan.FromMinutes(30);
    private static readonly HashSet<string> CatalogFiles = new(StringComparer.Ordinal)
    {
        "manifest.json", "hyg_v42.sqlite", "LICENSE-HYG.md", "ATTRIBUTION-HYG.md"
    };

    private readonly HttpClient client;
    private readonly string cacheRoot;
    private readonly string stateRoot;
    private readonly DistributionTrustRoot trustRoot;

    public DistributionCatalogAcquirer(
        HttpMessageHandler? handler = null,
        string? cacheRoot = null,
        DistributionTrustRoot? trustRoot = null,
        string? stateRoot = null)
    {
        client = CreateClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        this.cacheRoot = cacheRoot ?? DefaultCacheRoot();
        this.stateRoot = stateRoot ?? (cacheRoot is null ? DefaultStateRoot() : Path.Combine(cacheRoot, "test-state"));
        this.trustRoot = trustRoot ?? DistributionTrustRoot.Production;
    }

    public async Task<AcquiredCatalog> AcquireAsync(InstallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CatalogManifest is null && request.CatalogIndex is null)
        {
            return new AcquiredCatalog(request.CatalogBundle!, null, null, null);
        }

        try
        {
            var resolvedManifest = request.CatalogManifest is not null
                ? new ResolvedManifest(DistributionLocator.Parse(request.CatalogManifest), null, null, null)
                : await ResolveManifestFromIndexAsync(request, cancellationToken).ConfigureAwait(false);
            var manifestSource = resolvedManifest.Manifest;
            var signatureSource = resolvedManifest.Signature ?? manifestSource.AppendToName(".sig");
            var manifestBytes = await ReadMetadataAsync(
                manifestSource, DistributionVerifier.MaximumManifestBytes, request.NoDownload, cancellationToken).ConfigureAwait(false);
            var signatureBytes = await ReadMetadataAsync(
                signatureSource, DistributionVerifier.MaximumSignatureTextBytes, request.NoDownload, cancellationToken).ConfigureAwait(false);
            DistributionReleaseManifest manifest;
            try
            {
                manifest = DistributionVerifier.VerifyManifest(manifestBytes.Bytes, signatureBytes.Bytes, trustRoot);
            }
            catch (DistributionValidationException) when (!request.NoDownload && (manifestBytes.FromCache || signatureBytes.FromCache))
            {
                DeleteCachedMetadata(manifestBytes);
                DeleteCachedMetadata(signatureBytes);
                manifestBytes = await ReadMetadataAsync(
                    manifestSource, DistributionVerifier.MaximumManifestBytes, noDownload: false, cancellationToken, bypassCache: true).ConfigureAwait(false);
                signatureBytes = await ReadMetadataAsync(
                    signatureSource, DistributionVerifier.MaximumSignatureTextBytes, noDownload: false, cancellationToken, bypassCache: true).ConfigureAwait(false);
                manifest = DistributionVerifier.VerifyManifest(manifestBytes.Bytes, signatureBytes.Bytes, trustRoot);
            }
            if (resolvedManifest.Reference is { } reference &&
                (manifestBytes.Bytes.Length != reference.ManifestLength ||
                 Convert.ToHexStringLower(SHA256.HashData(manifestBytes.Bytes)) != reference.ManifestSha256))
            {
                throw new InstallerException("The catalog manifest does not match its signed index entry.");
            }
            if (manifest.ManifestKind != DistributionManifestKind.CatalogRelease)
            {
                throw new InstallerException("The signed distribution manifest is not a catalog release.");
            }
            var artifact = manifest.Artifacts.SingleOrDefault(static value => value.Role == DistributionArtifactRole.CatalogBundle)
                ?? throw new InstallerException("The signed catalog release does not identify exactly one bundle asset.");
            var assetSource = ResolveAssetSource(request, manifestSource, artifact.AssetName, resolvedManifest.Reference is null);
            var asset = await AcquireAssetAsync(assetSource, request.CatalogBundle, artifact, request.NoDownload, cancellationToken)
                .ConfigureAwait(false);
            var extractedRoot = await ExtractCatalogAsync(asset.Path, artifact, manifest.Catalog!, cancellationToken).ConfigureAwait(false);
            if (resolvedManifest.IndexState is { } indexState)
            {
                CommitIndexRollback(indexState);
            }
            var manifestHash = Convert.ToHexStringLower(SHA256.HashData(manifestBytes.Bytes));
            var evidence = new DistributionVerificationEvidence(
                manifest.ManifestKind.ToString(),
                manifest.Release.Train,
                manifest.Release.Version,
                manifest.Release.Tag,
                manifestHash,
                manifestBytes.Bytes.Length,
                manifest.Signing.KeyId,
                artifact.AssetName,
                artifact.Sha256,
                artifact.Length,
                manifestSource.Uri,
                asset.ResolvedUri,
                "verified",
                DateTimeOffset.UtcNow,
                manifest.Artifacts.Single(static value => value.Role == DistributionArtifactRole.Provenance).AssetName,
                manifest.Artifacts.Single(static value => value.Role == DistributionArtifactRole.Provenance).Sha256);
            return new AcquiredCatalog(extractedRoot, evidence, manifest.Catalog, extractedRoot);
        }
        catch (Exception exception) when (exception is DistributionValidationException or HttpRequestException or IOException or
                                           UnauthorizedAccessException or InvalidDataException or CryptographicException)
        {
            throw new InstallerException("The signed catalog distribution could not be acquired or verified.", exception);
        }
    }

    public void Dispose() => client.Dispose();

    private async Task<ResolvedManifest> ResolveManifestFromIndexAsync(
        InstallRequest request,
        CancellationToken cancellationToken)
    {
        var indexSource = DistributionLocator.Parse(request.CatalogIndex!);
        var signatureSource = indexSource.AppendToName(".sig");
        var indexBytes = await ReadMetadataAsync(
            indexSource, DistributionVerifier.MaximumManifestBytes, request.NoDownload, cancellationToken).ConfigureAwait(false);
        var signatureBytes = await ReadMetadataAsync(
            signatureSource, DistributionVerifier.MaximumSignatureTextBytes, request.NoDownload, cancellationToken).ConfigureAwait(false);
        DistributionReleaseIndex index;
        try
        {
            index = DistributionVerifier.VerifyIndex(indexBytes.Bytes, signatureBytes.Bytes, trustRoot);
        }
        catch (DistributionValidationException) when (!request.NoDownload && (indexBytes.FromCache || signatureBytes.FromCache))
        {
            DeleteCachedMetadata(indexBytes);
            DeleteCachedMetadata(signatureBytes);
            indexBytes = await ReadMetadataAsync(
                indexSource, DistributionVerifier.MaximumManifestBytes, noDownload: false, cancellationToken, bypassCache: true).ConfigureAwait(false);
            signatureBytes = await ReadMetadataAsync(
                signatureSource, DistributionVerifier.MaximumSignatureTextBytes, noDownload: false, cancellationToken, bypassCache: true).ConfigureAwait(false);
            index = DistributionVerifier.VerifyIndex(indexBytes.Bytes, signatureBytes.Bytes, trustRoot);
        }
        if (index.Train != "catalog")
        {
            throw new InstallerException("The signed release index is not for the catalog train.");
        }
        var indexState = ValidateIndexRollback(request.Channel, index, indexBytes.Bytes);
        var version = request.CatalogVersion ?? index.DefaultVersion;
        var reference = index.Releases.SingleOrDefault(release => release.Version == version)
            ?? throw new InstallerException($"Catalog version '{version}' is not present in the signed release index.");
        var manifest = ResolveIndexedAsset(request, indexSource, reference.Tag, reference.ManifestAsset);
        var signature = ResolveIndexedAsset(request, indexSource, reference.Tag, reference.SignatureAsset);
        return new ResolvedManifest(manifest, signature, reference, indexState);
    }

    private IndexRollbackState ValidateIndexRollback(DistributionChannel channel, DistributionReleaseIndex index, byte[] bytes)
    {
        SafeFileSystem.CreateOwnerDirectory(stateRoot);
        var channelName = channel switch
        {
            DistributionChannel.Local => "local",
            DistributionChannel.Stable => "stable",
            DistributionChannel.Nightly => "nightly",
            DistributionChannel.Prerelease => "prerelease",
            _ => throw new InstallerException("The distribution channel is unsupported.")
        };
        var statePath = Path.Combine(stateRoot, $"catalog-{channelName}.txt");
        using var indexLock = OperationLock.Acquire(statePath + ".lock");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (File.Exists(statePath))
        {
            using var stream = SafeFileSystem.OpenOwnerFileRead(statePath);
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
            var fields = reader.ReadToEnd().TrimEnd('\n').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 || !long.TryParse(fields[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var retainedSequence))
            {
                throw new InstallerException("The retained signed-index rollback state is invalid.");
            }
            if (index.Sequence < retainedSequence || index.Sequence == retainedSequence && hash != fields[1])
            {
                throw new InstallerException("The signed release index is older than or conflicts with retained rollback state.");
            }
        }
        return new IndexRollbackState(statePath, index.Sequence, hash);
    }

    private static void CommitIndexRollback(IndexRollbackState state)
    {
        using var indexLock = OperationLock.Acquire(state.StatePath + ".lock");
        if (File.Exists(state.StatePath))
        {
            using var stream = SafeFileSystem.OpenOwnerFileRead(state.StatePath);
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
            var fields = reader.ReadToEnd().TrimEnd('\n').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 ||
                !long.TryParse(fields[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var retainedSequence) ||
                state.Sequence < retainedSequence || state.Sequence == retainedSequence && state.Hash != fields[1])
            {
                throw new InstallerException("The signed release index conflicts with retained rollback state.");
            }
        }
        SafeFileSystem.WriteTextAtomic(
            state.StatePath,
            $"{state.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)} {state.Hash}\n");
    }

    private static DistributionLocator ResolveIndexedAsset(
        InstallRequest request,
        DistributionLocator index,
        string tag,
        string assetName)
    {
        if (request.AssetBaseUrl is { } configured)
        {
            return DistributionLocator.Parse(configured).Resolve($"{tag}/{assetName}");
        }
        if (index.LocalPath is { } localIndex)
        {
            return DistributionLocator.Parse(Path.Combine(Path.GetDirectoryName(localIndex)!, tag, assetName));
        }
        if (index.Uri.Host == "github.com")
        {
            return DistributionLocator.Parse($"https://github.com/RoySalisbury/HVO.SkyMonitor/releases/download/{tag}/{assetName}");
        }
        throw new InstallerException("A mirrored signed index requires --asset-base-url for immutable release assets.");
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "HttpClient owns and disposes the selected handler.")]
    private static HttpClient CreateClient(HttpMessageHandler? handler)
        => new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true);

    private async Task<MetadataBytes> ReadMetadataAsync(
        DistributionLocator locator,
        int maximumBytes,
        bool noDownload,
        CancellationToken cancellationToken,
        bool bypassCache = false)
    {
        if (locator.LocalPath is { } path)
        {
            await using var stream = SafeFileSystem.OpenRegularFileRead(path);
            if (stream.Length is <= 0 || stream.Length > maximumBytes)
            {
                throw new InstallerException($"Distribution metadata '{path}' exceeds its bounded size.");
            }
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return new MetadataBytes(bytes, null, false);
        }
        if (noDownload)
        {
            var cached = MetadataCachePath(locator.Uri);
            if (!File.Exists(cached))
            {
                throw new InstallerException("--no-download requires verified cached distribution metadata.");
            }
            await using var cachedStream = SafeFileSystem.OpenOwnerFileRead(cached, allowReadOnly: true);
            if (cachedStream.Length is <= 0 || cachedStream.Length > maximumBytes)
            {
                throw new InstallerException("Cached distribution metadata exceeds its bounded size.");
            }
            var cachedBytes = new byte[cachedStream.Length];
            await cachedStream.ReadExactlyAsync(cachedBytes, cancellationToken).ConfigureAwait(false);
            return new MetadataBytes(cachedBytes, cached, true);
        }
        var cachePath = MetadataCachePath(locator.Uri);
        if (!bypassCache && File.Exists(cachePath))
        {
            await using var cachedStream = SafeFileSystem.OpenOwnerFileRead(cachePath, allowReadOnly: true);
            if (cachedStream.Length is > 0 && cachedStream.Length <= maximumBytes)
            {
                var cachedBytes = new byte[cachedStream.Length];
                await cachedStream.ReadExactlyAsync(cachedBytes, cancellationToken).ConfigureAwait(false);
                return new MetadataBytes(cachedBytes, cachePath, true);
            }
            File.Delete(cachePath);
        }
        var bytesFromNetwork = await DownloadMetadataAsync(locator.Uri, maximumBytes, cancellationToken).ConfigureAwait(false);
        using (ReserveCacheQuota(bytesFromNetwork.Length, Path.GetFileName(cachePath)))
        {
            await WriteCacheFileAsync(cachePath, bytesFromNetwork, cancellationToken).ConfigureAwait(false);
        }
        return new MetadataBytes(bytesFromNetwork, cachePath, false);
    }

    private async Task<AcquiredAsset> AcquireAssetAsync(
        DistributionLocator source,
        string? operatorAsset,
        DistributionArtifact artifact,
        bool noDownload,
        CancellationToken cancellationToken)
    {
        if (operatorAsset is not null && File.Exists(operatorAsset))
        {
            await using var supplied = SafeFileSystem.OpenRegularFileRead(operatorAsset);
            await DistributionVerifier.VerifyAssetAsync(supplied, artifact, cancellationToken).ConfigureAwait(false);
            return new AcquiredAsset(operatorAsset, new Uri(operatorAsset));
        }
        if (source.LocalPath is { } localPath)
        {
            await using var local = SafeFileSystem.OpenRegularFileRead(localPath);
            await DistributionVerifier.VerifyAssetAsync(local, artifact, cancellationToken).ConfigureAwait(false);
            return new AcquiredAsset(localPath, source.Uri);
        }

        using var quotaReservation = ReserveCacheQuota(artifact.Length, artifact.Sha256, artifact.AssetName);
        var assetRoot = Path.Combine(cacheRoot, "v1", "assets", "sha256", artifact.Sha256[..2], artifact.Sha256);
        SafeFileSystem.CreateOwnerDirectory(assetRoot);
        var cached = Path.Combine(assetRoot, artifact.AssetName);
        var resolvedUriPath = cached + ".uri";
        using var cacheLock = OperationLock.Acquire(Path.Combine(assetRoot, ".acquire.lock"));
        if (File.Exists(cached))
        {
            try
            {
                await using var hit = SafeFileSystem.OpenOwnerFileRead(cached, allowReadOnly: true);
                await DistributionVerifier.VerifyAssetAsync(hit, artifact, cancellationToken).ConfigureAwait(false);
                return new AcquiredAsset(cached, ReadCachedResolvedUri(resolvedUriPath) ?? new Uri(cached));
            }
            catch (DistributionValidationException)
            {
                File.Delete(cached);
            }
        }
        if (noDownload)
        {
            throw new InstallerException($"--no-download requires verified cached asset '{artifact.AssetName}'.");
        }

        var partial = Path.Combine(assetRoot, $".{artifact.AssetName}.partial");
        var resolvedUri = await DownloadAssetAsync(source.Uri, partial, artifact, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var downloaded = SafeFileSystem.OpenOwnerFileRead(partial);
            await DistributionVerifier.VerifyAssetAsync(downloaded, artifact, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
        File.SetUnixFileMode(partial, UnixFileMode.UserRead);
        File.Move(partial, cached, overwrite: false);
        SafeFileSystem.WriteTextAtomic(resolvedUriPath, resolvedUri.AbsoluteUri + "\n");
        NativeLinux.FlushDirectory(assetRoot);
        return new AcquiredAsset(cached, resolvedUri);
    }

    private async Task<byte[]> DownloadMetadataAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);
                using var transfer = await SendWithPolicyAsync(uri, null, timeout.Token).ConfigureAwait(false);
                return await ReadBoundedAsync(transfer.Response, maximumBytes, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < MaximumAttempts && !cancellationToken.IsCancellationRequested &&
                                               exception is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<Uri> DownloadAssetAsync(
        Uri source,
        string partial,
        DistributionArtifact artifact,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long existingLength = 0;
            if (File.Exists(partial))
            {
                using var existing = SafeFileSystem.OpenOwnerFileRead(partial);
                existingLength = existing.Length;
            }
            if (existingLength < 0 || existingLength > artifact.Length)
            {
                File.Delete(partial);
                existingLength = 0;
            }
            EnsureDiskSpace(partial, artifact.Length - existingLength);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(AssetAttemptTimeout);
                using var transfer = await SendWithPolicyAsync(source, existingLength == 0 ? null : existingLength, timeout.Token)
                    .ConfigureAwait(false);
                var response = transfer.Response;
                if (existingLength > 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    File.Delete(partial);
                    existingLength = 0;
                }
                else if (existingLength > 0 &&
                         (response.StatusCode != HttpStatusCode.PartialContent ||
                          response.Content.Headers.ContentRange?.From != existingLength ||
                          response.Content.Headers.ContentRange?.Length != artifact.Length))
                {
                    File.Delete(partial);
                    throw new InstallerException("The distribution server returned an invalid resumed response.");
                }
                var responseLength = response.Content.Headers.ContentLength;
                if (responseLength is > 0 && checked(existingLength + responseLength.Value) > artifact.Length)
                {
                    File.Delete(partial);
                    throw new InstallerException("The distribution response exceeds the signed asset length.");
                }
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var output = SafeFileSystem.OpenOwnerFileAppend(partial);
                var buffer = new byte[128 * 1024];
                long total = existingLength;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    total = checked(total + read);
                    if (total > artifact.Length)
                    {
                        output.Close();
                        File.Delete(partial);
                        throw new InstallerException("The distribution response exceeds the signed asset length.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
#pragma warning disable CA1849 // Cache publication requires a flush-to-disk boundary.
                output.Flush(flushToDisk: true);
#pragma warning restore CA1849
                if (total != artifact.Length)
                {
                    throw new HttpRequestException("The distribution response ended before the signed length.");
                }
                return transfer.ResolvedUri;
            }
            catch (Exception exception) when (attempt < MaximumAttempts && exception is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new HttpRequestException("The distribution asset retry policy was exhausted.");
    }

    private async Task<HttpAcquisitionResponse> SendWithPolicyAsync(
        Uri initialUri,
        long? rangeStart,
        CancellationToken cancellationToken)
    {
        var current = initialUri;
        var original = initialUri;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            ValidateNetworkUri(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (rangeStart is not null)
            {
                request.Headers.Range = new RangeHeaderValue(rangeStart, null);
            }
            var token = Environment.GetEnvironmentVariable("HVO_GITHUB_TOKEN");
            if (!string.IsNullOrEmpty(token) && current.Host == "github.com" && current.Host == original.Host)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirect == MaximumRedirects)
                {
                    throw new HttpRequestException("The distribution redirect policy was exceeded.");
                }
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                ValidateRedirect(initialUri, current, next);
                current = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            return new HttpAcquisitionResponse(response, current);
        }
        throw new HttpRequestException("The distribution redirect policy was exceeded.");
    }

    private async Task<string> ExtractCatalogAsync(
        string archivePath,
        DistributionArtifact artifact,
        DistributionCatalogIdentity catalog,
        CancellationToken cancellationToken)
    {
        var extractionParent = Path.Combine(cacheRoot, "v1", "staging");
        SafeFileSystem.CreateOwnerDirectory(extractionParent);
        var extractionRoot = Path.Combine(extractionParent, $"catalog-{Guid.NewGuid():N}");
        var privateArchive = Path.Combine(extractionParent, $"catalog-{Guid.NewGuid():N}.tar.gz");
        SafeFileSystem.CreateOwnerDirectory(extractionRoot);
        var expectedPrefix = $"{catalog.PackageVersion}.bundle/";
        var extracted = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            _ = await SafeFileSystem.CopyPrivateFileAsync(archivePath, privateArchive, cancellationToken).ConfigureAwait(false);
            await using var archive = SafeFileSystem.OpenOwnerFileRead(privateArchive);
            await DistributionVerifier.VerifyAssetAsync(archive, artifact, cancellationToken).ConfigureAwait(false);
            archive.Position = 0;
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: false);
            using var reader = new TarReader(gzip, leaveOpen: false);
            TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (entry.EntryType != TarEntryType.RegularFile || !entry.Name.StartsWith(expectedPrefix, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The catalog archive contains an unsupported entry.");
                }
                var name = entry.Name[expectedPrefix.Length..];
                if (!CatalogFiles.Contains(name) || !extracted.Add(name) || name.Contains('/', StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The catalog archive file set is invalid.");
                }
                var maximum = name == "hyg_v42.sqlite" ? catalog.DatabaseLength : 4L * 1024 * 1024;
                if (entry.Length <= 0 || entry.Length > maximum || entry.DataStream is null)
                {
                    throw new InvalidDataException($"Catalog archive entry '{name}' exceeds its bounded size.");
                }
                var destination = Path.Combine(extractionRoot, name);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await entry.DataStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            if (!extracted.SetEquals(CatalogFiles))
            {
                throw new InvalidDataException("The catalog archive omits required files.");
            }
            await VerifyExtractedCatalogAsync(extractionRoot, catalog, cancellationToken).ConfigureAwait(false);
            return extractionRoot;
        }
        catch
        {
            Directory.Delete(extractionRoot, recursive: true);
            throw;
        }
        finally
        {
            File.Delete(privateArchive);
        }
    }

    private static async Task VerifyExtractedCatalogAsync(
        string root,
        DistributionCatalogIdentity catalog,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(root, "manifest.json");
        var databasePath = Path.Combine(root, "hyg_v42.sqlite");
        var manifestHash = await SafeFileSystem.ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false);
        var databaseHash = await SafeFileSystem.ComputeSha256Async(databasePath, cancellationToken).ConfigureAwait(false);
        if (manifestHash != catalog.BundleManifestSha256 || databaseHash != catalog.DatabaseSha256 ||
            new FileInfo(databasePath).Length != catalog.DatabaseLength)
        {
            throw new InvalidDataException("The extracted catalog does not match its signed internal identity.");
        }
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        var rootElement = document.RootElement;
        if (rootElement.GetProperty("manifestVersion").GetInt32() != catalog.ManifestVersion ||
            rootElement.GetProperty("package").GetProperty("version").GetString() != catalog.PackageVersion ||
            rootElement.GetProperty("package").GetProperty("kind").GetString() != catalog.PackageKind ||
            rootElement.GetProperty("catalog").GetProperty("id").GetString() != catalog.CatalogId ||
            rootElement.GetProperty("schemaVersion").GetString() != catalog.SchemaVersion ||
            rootElement.GetProperty("preprocessingVersion").GetString() != catalog.PreprocessingVersion ||
            rootElement.GetProperty("database").GetProperty("sha256").GetString() != catalog.DatabaseSha256 ||
            rootElement.GetProperty("database").GetProperty("length").GetInt64() != catalog.DatabaseLength ||
            rootElement.GetProperty("database").GetProperty("rowCount").GetInt64() != catalog.RowCount ||
            rootElement.GetProperty("license").GetProperty("identifier").GetString() != catalog.LicenseIdentifier ||
            rootElement.GetProperty("license").GetProperty("file").GetProperty("relativePath").GetString() != catalog.LicenseAsset ||
            rootElement.GetProperty("license").GetProperty("attribution").GetProperty("relativePath").GetString() != catalog.AttributionAsset ||
            rootElement.GetProperty("topology").GetProperty("identity").GetString() != catalog.TopologyIdentity ||
            rootElement.GetProperty("topology").GetProperty("sha256").GetString() != catalog.TopologySha256)
        {
            throw new InvalidDataException("The inner and signed outer catalog identities do not agree.");
        }
    }

    private static DistributionLocator ResolveAssetSource(
        InstallRequest request,
        DistributionLocator manifest,
        string assetName,
        bool allowConfiguredBase)
    {
        if (allowConfiguredBase && request.AssetBaseUrl is { } configured)
        {
            return DistributionLocator.Parse(configured).Resolve(assetName);
        }
        return manifest.ResolveSibling(assetName);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is not long length || length <= 0 || length > maximumBytes)
        {
            throw new HttpRequestException("Distribution metadata has a missing or invalid Content-Length.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new HttpRequestException("Distribution metadata exceeds its declared length.");
        }
        return bytes;
    }

    private static void ValidateNetworkUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath.Contains("/latest/", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("/refs/heads/", StringComparison.OrdinalIgnoreCase) || uri.Host == "raw.githubusercontent.com")
        {
            throw new HttpRequestException("The distribution URI violates immutable HTTPS policy.");
        }
    }

    private static Uri? ReadCachedResolvedUri(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var value = reader.ReadToEnd().TrimEnd('\n');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InstallerException("The cached distribution source identity is invalid.");
        }
        ValidateNetworkUri(uri);
        return uri;
    }

    private static void ValidateRedirect(Uri initial, Uri current, Uri next)
    {
        ValidateNetworkUri(next);
        var githubRedirectHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "objects.githubusercontent.com", "release-assets.githubusercontent.com", "github-releases.githubusercontent.com"
        };
        if (initial.Host != "github.com" || current.Host != "github.com" || !githubRedirectHosts.Contains(next.Host))
        {
            throw new HttpRequestException("The distribution redirect crosses an unapproved origin.");
        }
    }

    private static void EnsureDiskSpace(string path, long requiredBytes)
    {
        var cacheDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (new DriveInfo(cacheDirectory).AvailableFreeSpace < checked(requiredBytes + 64L * 1024 * 1024))
        {
            throw new InstallerException("The distribution cache does not have sufficient free space.");
        }
    }

    private OperationLock ReserveCacheQuota(long incomingBytes, string incomingHash, string? incomingAssetName = null)
    {
        if (incomingBytes > MaximumCacheBytes)
        {
            throw new InstallerException("The signed asset exceeds the distribution cache quota.");
        }
        var assetsRoot = Path.Combine(cacheRoot, "v1", "assets");
        SafeFileSystem.CreateOwnerDirectory(assetsRoot);
        var quotaLock = OperationLock.Acquire(Path.Combine(assetsRoot, ".quota.lock"));
        try
        {
            var versionRoot = Path.Combine(cacheRoot, "v1");
            var staleBefore = DateTime.UtcNow.AddDays(-7);
            foreach (var partial in Directory.EnumerateFiles(versionRoot, "*.partial", SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTimeUtc(partial) < staleBefore)
                {
                    File.Delete(partial);
                }
            }
            var shaRoot = Path.Combine(assetsRoot, "sha256");
            var assetDirectories = Directory.Exists(shaRoot)
                ? Directory.EnumerateDirectories(shaRoot, "*", SearchOption.AllDirectories)
                    .Where(static directory => Path.GetFileName(directory).Length == 64)
                    .ToArray()
                : [];
            var metadataRoot = Path.Combine(versionRoot, "metadata");
            var metadataFiles = Directory.Exists(metadataRoot)
                ? Directory.EnumerateFiles(metadataRoot, "*", SearchOption.AllDirectories).Select(static path => new FileInfo(path)).ToArray()
                : [];
            var candidates = assetDirectories.Select(static directory => CacheEvictionCandidate.ForDirectory(directory))
                .Concat(metadataFiles.Select(static file => CacheEvictionCandidate.ForFile(file)))
                .OrderBy(static candidate => candidate.LastWriteUtc)
                .ToArray();
            var retainedBytes = candidates.Sum(static candidate => candidate.Length);
            var incomingPath = incomingAssetName is null
                ? null
                : Path.Combine(assetsRoot, "sha256", incomingHash[..2], incomingHash, incomingAssetName);
            var incomingAlreadyCached = incomingPath is not null && File.Exists(incomingPath) &&
                                        new FileInfo(incomingPath).LinkTarget is null;
            var reservationBytes = incomingAlreadyCached ? 0 : incomingBytes;
            foreach (var candidate in candidates.Where(candidate => !candidate.Path.EndsWith(incomingHash, StringComparison.Ordinal)))
            {
                if (checked(retainedBytes + reservationBytes) <= MaximumCacheBytes)
                {
                    break;
                }
                retainedBytes -= candidate.Length;
                candidate.Delete();
            }
            if (checked(retainedBytes + reservationBytes) > MaximumCacheBytes)
            {
                throw new InstallerException("The bounded distribution cache cannot admit the signed asset.");
            }
            return quotaLock;
        }
        catch
        {
            quotaLock.Dispose();
            throw;
        }
    }

    private static string DefaultCacheRoot()
    {
        var configured = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
            : configured;
        return Path.Combine(root, "hvo", "skymonitor", "distribution");
    }

    private static string DefaultStateRoot()
    {
        var configured = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state")
            : configured;
        return Path.Combine(root, "hvo", "skymonitor", "distribution");
    }

    private static void DeleteCachedMetadata(MetadataBytes metadata)
    {
        if (metadata.CachePath is not null)
        {
            File.Delete(metadata.CachePath);
        }
    }

    private string MetadataCachePath(Uri uri)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        return Path.Combine(cacheRoot, "v1", "metadata", key[..2], key);
    }

    private static async Task WriteCacheFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        SafeFileSystem.CreateOwnerDirectory(directory);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Cache publication requires a flush-to-disk boundary.
            stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            stream.Close();
            try
            {
                File.Move(temporary, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Delete(temporary);
            }
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
            NativeLinux.FlushDirectory(directory);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed record MetadataBytes(byte[] Bytes, string? CachePath, bool FromCache);
    private sealed record AcquiredAsset(string Path, Uri ResolvedUri);
    private sealed record ResolvedManifest(
        DistributionLocator Manifest,
        DistributionLocator? Signature,
        DistributionReleaseReference? Reference,
        IndexRollbackState? IndexState);
    private sealed record IndexRollbackState(string StatePath, long Sequence, string Hash);
    private sealed record CacheEvictionCandidate(string Path, long Length, DateTime LastWriteUtc, bool IsDirectory)
    {
        public static CacheEvictionCandidate ForDirectory(string path)
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Select(static file => new FileInfo(file)).ToArray();
            return new CacheEvictionCandidate(
                path,
                files.Sum(static file => file.Length),
                files.Length == 0 ? Directory.GetLastWriteTimeUtc(path) : files.Max(static file => file.LastWriteTimeUtc),
                true);
        }

        public static CacheEvictionCandidate ForFile(FileInfo file)
            => new(file.FullName, file.Length, file.LastWriteTimeUtc, false);

        public void Delete()
        {
            if (IsDirectory)
            {
                Directory.Delete(Path, recursive: true);
            }
            else
            {
                File.Delete(Path);
            }
        }
    }
    private sealed record HttpAcquisitionResponse(HttpResponseMessage Response, Uri ResolvedUri) : IDisposable
    {
        public void Dispose() => Response.Dispose();
    }
}

internal sealed class AcquiredCatalog(
    string bundlePath,
    DistributionVerificationEvidence? evidence,
    DistributionCatalogIdentity? signedIdentity,
    string? temporaryRoot) : IDisposable
{
    public string BundlePath { get; } = bundlePath;
    public DistributionVerificationEvidence? Evidence { get; } = evidence;
    public DistributionCatalogIdentity? SignedIdentity { get; } = signedIdentity;

    public void Dispose()
    {
        if (temporaryRoot is not null && Directory.Exists(temporaryRoot))
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }
}

internal sealed record DistributionLocator(Uri Uri, string? LocalPath)
{
    public static DistributionLocator Parse(string value)
    {
        if (Path.IsPathFullyQualified(value))
        {
            var path = Path.GetFullPath(value);
            return new DistributionLocator(new Uri(path), path);
        }
        var uri = new Uri(value, UriKind.Absolute);
        return new DistributionLocator(uri, null);
    }

    public DistributionLocator AppendToName(string suffix)
        => LocalPath is { } path
            ? Parse(path + suffix)
            : new DistributionLocator(new Uri(Uri.AbsoluteUri + suffix), null);

    public DistributionLocator ResolveSibling(string name)
        => LocalPath is { } path
            ? Parse(Path.Combine(Path.GetDirectoryName(path)!, name))
            : new DistributionLocator(new Uri(Uri, name), null);

    public DistributionLocator Resolve(string name)
    {
        if (LocalPath is { } path)
        {
            return Parse(Path.Combine(path, name));
        }
        var baseUri = Uri.AbsoluteUri.EndsWith('/') ? Uri : new Uri(Uri.AbsoluteUri + "/");
        return new DistributionLocator(new Uri(baseUri, name), null);
    }
}
