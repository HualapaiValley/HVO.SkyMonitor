using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.LogicHost.Tests.Services;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class FilesystemObjectStoreFaultTests
{
    private const string Bucket = "skymonitor-artifacts";
    private string _temp = null!;
    private FilesystemObjectStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temp = Path.Combine(Path.GetTempPath(), "hvo-fsos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_temp, Bucket));
        _store = FilesystemObjectStoreConformanceTests.Create(_temp);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    private string DescriptorPath(string key)
        => Path.Combine(_temp, FilesystemObjectLayout.DescriptorRelativePath(Bucket, FilesystemObjectLayout.KeyHash(key)));

    private string DataPath(string key, string generation)
        => Path.Combine(_temp, FilesystemObjectLayout.DataRelativePath(Bucket, FilesystemObjectLayout.KeyHash(key), generation));

    private static readonly CancellationToken None = CancellationToken.None;

    // ---- key mapping: no key segment is a physical segment ----

    [TestMethod]
    [DataRow("../../etc/passwd")]
    [DataRow("a/../../b")]
    [DataRow("/absolute/key")]
    [DataRow("C:\\windows\\key")]
    [DataRow("key with spaces/and/Slashes")]
    [DataRow("CON")]
    [DataRow("..")]
    [DataRow("nul.txt")]
    [DataRow("MixedCase/Key")]
    [DataRow("mixedcase/key")]
    public async Task AnyKeyMapsToAnOpaqueNameInsideTheBucket(string key)
    {
        await _store.PutAsync(Bucket, key, Bytes("x"), 1, "text/plain", None);
        var descriptor = DescriptorPath(key);
        Assert.IsTrue(File.Exists(descriptor));
        Assert.IsTrue(descriptor.StartsWith(Path.Combine(_temp, Bucket) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        var physicalName = Path.GetFileName(descriptor);
        Assert.IsTrue(physicalName.EndsWith(FilesystemObjectLayout.DescriptorSuffix, StringComparison.Ordinal), physicalName);
        var stem = physicalName[..^FilesystemObjectLayout.DescriptorSuffix.Length];
        Assert.HasCount(64, stem);
        Assert.IsTrue(stem.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')), "the physical stem is lowercase hex and nothing from the key survives: " + stem);
        var stat = await _store.StatAsync(Bucket, key, None);
        Assert.AreEqual(key, stat.Key, "the exact logical key round-trips through the descriptor");
    }

    [TestMethod]
    public async Task CaseVariantKeysAreDistinctObjects()
    {
        await _store.PutAsync(Bucket, "Key", Bytes("upper"), 5, "text/plain", None);
        await _store.PutAsync(Bucket, "key", Bytes("lower"), 5, "text/plain", None);
        Assert.AreEqual("upper", await ReadAll("Key"));
        Assert.AreEqual("lower", await ReadAll("key"));
    }

    // ---- descriptor is the commit point ----

    [TestMethod]
    public async Task DescriptorMismatchIsCorruptStateNotServed()
    {
        await _store.PutAsync(Bucket, "k", Bytes("payload"), 7, "text/plain", None);
        var path = DescriptorPath("k");
        var json = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllBytesAsync(path));
        var tampered = json.GetRawText().Replace("\"key\":\"k\"", "\"key\":\"other\"", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, tampered);

        var fault = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => _store.StatAsync(Bucket, "k", None));
        Assert.AreEqual(ObjectStoreFailureKind.CorruptState, fault.Kind);
        Assert.IsTrue(fault.IsTerminal);
    }

    [TestMethod]
    public async Task DescriptorWithoutDataIsCorruptState()
    {
        await _store.PutAsync(Bucket, "k", Bytes("payload"), 7, "text/plain", None);
        var stat = await _store.StatAsync(Bucket, "k", None);
        File.Delete(DataPath("k", stat.Generation));
        var fault = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => _store.ReadAsync(Bucket, "k", null, (_, _) => Task.CompletedTask, None));
        Assert.AreEqual(ObjectStoreFailureKind.CorruptState, fault.Kind);
    }

    [TestMethod]
    public async Task DataLengthContradictingDescriptorIsCorruptState()
    {
        await _store.PutAsync(Bucket, "k", Bytes("payload"), 7, "text/plain", None);
        var stat = await _store.StatAsync(Bucket, "k", None);
        await File.AppendAllTextAsync(DataPath("k", stat.Generation), "extra");
        var fault = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => _store.ReadAsync(Bucket, "k", null, (_, _) => Task.CompletedTask, None));
        Assert.AreEqual(ObjectStoreFailureKind.CorruptState, fault.Kind);
    }

    [TestMethod]
    public async Task MalformedDescriptorIsCorruptStateAndInvisibleToListing()
    {
        await _store.PutAsync(Bucket, "good", Bytes("x"), 1, "text/plain", None);
        await _store.PutAsync(Bucket, "bad", Bytes("x"), 1, "text/plain", None);
        await File.WriteAllTextAsync(DescriptorPath("bad"), "{not json");
        var fault = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => _store.StatAsync(Bucket, "bad", None));
        Assert.AreEqual(ObjectStoreFailureKind.CorruptState, fault.Kind);
        var listed = new List<string>();
        await foreach (var item in _store.ListAsync(Bucket, "", None))
        {
            listed.Add(item.Key);
        }
        CollectionAssert.AreEqual(new[] { "good" }, listed, "a malformed descriptor is not a listable object; it is reconciliation's problem");
    }

    // ---- publication crash points ----

    [TestMethod]
    public async Task ShortInputNeverCommits()
    {
        var fault = await Assert.ThrowsExactlyAsync<ObjectStoreException>(
            () => _store.PutAsync(Bucket, "k", Bytes("abc"), 10, "text/plain", None));
        Assert.AreEqual(ObjectStoreFailureKind.Unsupported, fault.Kind);
        Assert.IsFalse(File.Exists(DescriptorPath("k")));
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(_temp, Bucket), "*.tmp", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task LongInputNeverCommits()
    {
        var fault = await Assert.ThrowsExactlyAsync<ObjectStoreException>(
            () => _store.PutAsync(Bucket, "k", Bytes("abcdef"), 3, "text/plain", None));
        Assert.AreEqual(ObjectStoreFailureKind.Unsupported, fault.Kind);
        Assert.IsFalse(File.Exists(DescriptorPath("k")));
    }

    [TestMethod]
    public async Task WriterFailureMidStreamLeavesPriorGenerationIntact()
    {
        await _store.PutAsync(Bucket, "k", Bytes("v1"), 2, "text/plain", None);
        var before = await _store.StatAsync(Bucket, "k", None);
        using var failing = new FailAfterStream(Encoding.UTF8.GetBytes("v2-longer"), failAfter: 4);
        await Assert.ThrowsAsync<ObjectStoreException>(() => _store.PutAsync(Bucket, "k", failing, 9, "text/plain", None));
        var after = await _store.StatAsync(Bucket, "k", None);
        Assert.AreEqual(before.Generation, after.Generation, "a failed replacement must not touch the committed generation");
        Assert.AreEqual("v1", await ReadAll("k"));
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(_temp, Bucket), "*.tmp", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task CancellationDuringWriteLeavesPriorGenerationIntact()
    {
        await _store.PutAsync(Bucket, "k", Bytes("v1"), 2, "text/plain", None);
        using var cts = new CancellationTokenSource();
        using var cancelling = new CancelAfterStream(new byte[200_000], cts, cancelAfter: 100_000);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => _store.PutAsync(Bucket, "k", cancelling, 200_000, "application/octet-stream", cts.Token));
        Assert.AreEqual("v1", await ReadAll("k"));
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(_temp, Bucket), "*.tmp", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task ReplacementIsGenerationImmutable_OldGenerationReadIsPrecondition()
    {
        await _store.PutAsync(Bucket, "k", Bytes("v1"), 2, "text/plain", None);
        var g1 = (await _store.StatAsync(Bucket, "k", None)).Generation;
        await _store.PutAsync(Bucket, "k", Bytes("v2"), 2, "text/plain", None);
        var g2 = (await _store.StatAsync(Bucket, "k", None)).Generation;
        Assert.AreNotEqual(g1, g2);
        Assert.IsTrue(File.Exists(DataPath("k", g1)), "the retired generation's bytes stay until reclamation, so an open reader keeps them");
        var fault = await Assert.ThrowsExactlyAsync<ObjectStoreException>(
            () => _store.ReadAsync(Bucket, "k", g1, (_, _) => Task.CompletedTask, None));
        Assert.AreEqual(ObjectStoreFailureKind.Precondition, fault.Kind);
        Assert.AreEqual("v2", await ReadAll("k", g2));
    }

    [TestMethod]
    public async Task OpenReaderSurvivesReplacementAndDelete()
    {
        var payload = new string('a', 300_000);
        await _store.PutAsync(Bucket, "k", Bytes(payload), payload.Length, "text/plain", None);
        var readBack = new StringBuilder();
        await _store.ReadAsync(Bucket, "k", null, async (stream, ct) =>
        {
            var buffer = new byte[64 * 1024];
            var first = await stream.ReadAsync(buffer, ct);
            readBack.Append(Encoding.UTF8.GetString(buffer, 0, first));
            // While the reader holds the data file open, the key is replaced and then deleted.
            await _store.PutAsync(Bucket, "k", Bytes("replaced"), 8, "text/plain", ct);
            await _store.DeleteAsync(Bucket, "k", ct);
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                readBack.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }, None);
        Assert.AreEqual(payload, readBack.ToString(), "the reader sees the generation it opened, complete");
        var gone = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => _store.StatAsync(Bucket, "k", None));
        Assert.AreEqual(ObjectStoreFailureKind.MissingObject, gone.Kind);
    }

    // ---- delete / copy semantics ----

    [TestMethod]
    public async Task DeleteIsIdempotentAndRetiresDataForReclamation()
    {
        await _store.PutAsync(Bucket, "k", Bytes("v1"), 2, "text/plain", None);
        var g = (await _store.StatAsync(Bucket, "k", None)).Generation;
        await _store.DeleteAsync(Bucket, "k", None);
        await _store.DeleteAsync(Bucket, "k", None);
        Assert.IsFalse(File.Exists(DescriptorPath("k")));
        Assert.IsTrue(File.Exists(DataPath("k", g)), "data is retired, not removed, by a request");
    }

    [TestMethod]
    public async Task CopyProducesAnIndependentGenerationAndVerifiesTheSourceDigest()
    {
        await _store.PutAsync(Bucket, "src", Bytes("payload"), 7, "text/plain", None);
        await _store.CopyAsync(Bucket, "src", "dst", None);
        var src = await _store.StatAsync(Bucket, "src", None);
        var dst = await _store.StatAsync(Bucket, "dst", None);
        Assert.AreNotEqual(src.Generation, dst.Generation);
        Assert.AreEqual("payload", await ReadAll("dst"));
        await _store.DeleteAsync(Bucket, "src", None);
        if (OperatingSystem.IsLinux())
        {
            File.Delete(DataPath("src", src.Generation));
        }
        Assert.AreEqual("payload", await ReadAll("dst"), "unlinking the source object does not remove the destination object");

        // Corrupt the source bytes under an intact descriptor: copy must refuse, not propagate.
        await _store.PutAsync(Bucket, "src2", Bytes("payload"), 7, "text/plain", None);
        var g = (await _store.StatAsync(Bucket, "src2", None)).Generation;
        await File.WriteAllTextAsync(DataPath("src2", g), "PAYLOAD");
        var fault = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => _store.CopyAsync(Bucket, "src2", "dst2", None));
        Assert.AreEqual(ObjectStoreFailureKind.CorruptState, fault.Kind);
        Assert.IsFalse(File.Exists(DescriptorPath("dst2")));
    }

    [TestMethod]
    public async Task CopyFallsBackToStreamingWhenHardLinksAreUnavailable()
    {
        await _store.PutAsync(Bucket, "src", Bytes("payload"), 7, "text/plain", None);
        _store.DisableHardLinksForTest = true;
        await _store.CopyAsync(Bucket, "src", "dst", None);
        var src = await _store.StatAsync(Bucket, "src", None);
        var dst = await _store.StatAsync(Bucket, "dst", None);
        await File.WriteAllTextAsync(DataPath("src", src.Generation), "PAYLOAD");
        Assert.AreEqual("payload", await ReadAll("dst"), "fallback writes an independent data file");
        Assert.AreNotEqual(src.Generation, dst.Generation);
    }

    // ---- buckets and containment ----

    [TestMethod]
    public async Task MissingBucketIsMissingBucketNotCreated()
    {
        Assert.IsFalse(await _store.BucketExistsAsync("absent", None));
        var fault = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => _store.PutAsync("absent", "k", Bytes("x"), 1, "text/plain", None));
        Assert.AreEqual(ObjectStoreFailureKind.MissingBucket, fault.Kind);
        Assert.IsFalse(Directory.Exists(Path.Combine(_temp, "absent")), "the provider never creates a bucket");
    }

    [TestMethod]
    public async Task ASymbolicLinkBucketIsNotABucket()
    {
        var outside = Path.Combine(Path.GetTempPath(), "hvo-fsos-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_temp, "linked-bucket"), outside);
            Assert.IsFalse(await _store.BucketExistsAsync("linked-bucket", None));
            var fault = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => _store.PutAsync("linked-bucket", "k", Bytes("x"), 1, "text/plain", None));
            Assert.AreEqual(ObjectStoreFailureKind.MissingBucket, fault.Kind);
            Assert.IsEmpty(Directory.GetFileSystemEntries(outside), "nothing is written through the link");
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [TestMethod]
    public void FaultMappingIsExhaustiveAndNeverCarriesThePath()
    {
        var secret = "/srv/objects/skymonitor-artifacts/ab/cd/secret";
        foreach (var kind in Enum.GetValues<FileSystemFaultKind>())
        {
            var mapped = FilesystemObjectStore.Map("op", new FileSystemFaultException(kind, "op", secret));
            Assert.IsFalse(mapped.Message.Contains(secret, StringComparison.Ordinal), $"{kind} leaked the path");
        }
        Assert.AreEqual(ObjectStoreFailureKind.Capacity, FilesystemObjectStore.Map("put", new FileSystemFaultException(FileSystemFaultKind.NoSpace, "put", secret)).Kind);
        Assert.AreEqual(ObjectStoreFailureKind.Unsupported, FilesystemObjectStore.Map("put", new FileSystemFaultException(FileSystemFaultKind.Containment, "put", secret)).Kind);
        Assert.AreEqual(ObjectStoreFailureKind.Authorization, FilesystemObjectStore.Map("put", new FileSystemFaultException(FileSystemFaultKind.PermissionDenied, "put", secret)).Kind);
        Assert.AreEqual(ObjectStoreFailureKind.Canceled, FilesystemObjectStore.Map("put", new FileSystemFaultException(FileSystemFaultKind.Cancelled, "put", secret)).Kind);
    }

    [TestMethod]
    public async Task OversizedDeclaredLengthIsRefusedBeforeReading()
    {
        using var probe = new FailAfterStream(new byte[1], failAfter: 0);
        var fault = await Assert.ThrowsExactlyAsync<ObjectStoreException>(
            () => _store.PutAsync(Bucket, "k", probe, IObjectStore.MaximumPutContentLength + 1, "text/plain", None));
        Assert.AreEqual(ObjectStoreFailureKind.Unsupported, fault.Kind);
        Assert.AreEqual(0, probe.BytesRead, "the size check precedes any read");
    }

    [TestMethod]
    public async Task ConcurrentPutsOnOneKeyConvergeToOneCommittedGeneration()
    {
        var puts = Enumerable.Range(0, 16).Select(i =>
            _store.PutAsync(Bucket, "k", Bytes($"v{i:D2}"), 3, "text/plain", None));
        await Task.WhenAll(puts);
        var stat = await _store.StatAsync(Bucket, "k", None);
        var content = await ReadAll("k");
        Assert.IsTrue(content.StartsWith('v') && content.Length == 3, content);
        Assert.AreEqual(3, stat.ContentLength);
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(_temp, Bucket), "*.tmp", SearchOption.AllDirectories));
        var descriptors = Directory.GetFiles(Path.Combine(_temp, Bucket), "*" + FilesystemObjectLayout.DescriptorSuffix, SearchOption.AllDirectories);
        Assert.HasCount(1, descriptors);
    }

    private async Task<string> ReadAll(string key, string? generation = null)
    {
        var sb = new StringBuilder();
        await _store.ReadAsync(Bucket, key, generation, async (stream, ct) =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            sb.Append(await reader.ReadToEndAsync(ct));
        }, None);
        return sb.ToString();
    }

    private sealed class FailAfterStream(byte[] content, int failAfter) : Stream
    {
        private int _position;
        public int BytesRead => _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= failAfter)
            {
                throw new IOException("simulated input failure");
            }
            var n = Math.Min(count, Math.Min(content.Length - _position, failAfter - _position));
            Array.Copy(content, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }

    private sealed class CancelAfterStream(byte[] content, CancellationTokenSource cts, int cancelAfter) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= cancelAfter)
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }
            var n = Math.Min(count, content.Length - _position);
            Array.Copy(content, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
