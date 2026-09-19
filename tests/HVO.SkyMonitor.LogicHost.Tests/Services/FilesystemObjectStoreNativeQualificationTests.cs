using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Tests.Services;

/// <summary>Manual real-mount probes for the exact #586 qualification campaign.</summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class FilesystemObjectStoreNativeQualificationTests
{
    [TestMethod]
    public async Task PutReportsConfiguredFailureKind()
    {
        var expected = Enum.Parse<ObjectStoreFailureKind>(Require("HVO_QUALIFICATION_EXPECTED_KIND"), ignoreCase: false);
        using var store = OpenStore();
        var bytes = int.Parse(Environment.GetEnvironmentVariable("HVO_QUALIFICATION_BYTES") ?? "4194304", System.Globalization.CultureInfo.InvariantCulture);
        using var content = new RepeatingStream(bytes);
        var fault = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => store.PutAsync(
            CentralObjectStorageOptions.DefaultArtifactBucket,
            "qualification/" + Guid.NewGuid().ToString("N"),
            content,
            bytes,
            "application/octet-stream",
            CancellationToken.None));
        Assert.AreEqual(expected, fault.Kind);
        Assert.AreEqual(expected == ObjectStoreFailureKind.Capacity, fault.RequiresOperator);
    }

    [TestMethod]
    public async Task PutSucceedsAfterFaultRecovery()
    {
        using var store = OpenStore();
        using var content = new RepeatingStream(4096);
        await store.PutAsync(
            CentralObjectStorageOptions.DefaultArtifactBucket,
            "qualification/recovered-" + Guid.NewGuid().ToString("N"),
            content,
            4096,
            "application/octet-stream",
            CancellationToken.None);
    }

    private static FilesystemObjectStore OpenStore()
    {
        var options = new CentralObjectStorageOptions { Provider = ObjectStorageProvider.Filesystem };
        options.Filesystem.Root = Require("HVO_QUALIFICATION_ROOT");
        var wrapped = Options.Create(options);
        return new FilesystemObjectStore(
            wrapped,
            new ObjectStoreTelemetry(wrapped),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
    }

    private static string Require(string name)
        => Environment.GetEnvironmentVariable(name)
            ?? throw new AssertInconclusiveException($"{name} is required for the manual qualification probe.");

    private sealed class RepeatingStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - _position);
            if (read <= 0) return 0;
            buffer.AsSpan(offset, read).Fill(0x5a);
            _position += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, length - _position);
            if (read <= 0) return ValueTask.FromResult(0);
            buffer.Span[..read].Fill(0x5a);
            _position += read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
