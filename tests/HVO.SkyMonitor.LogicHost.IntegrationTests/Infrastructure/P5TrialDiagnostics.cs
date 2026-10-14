using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

internal static class P5TrialDiagnostics
{
    internal const int MaximumSamples = 16_384;
    internal const int MaximumBytes = 4 * 1024 * 1024;
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task AssertPlateauAsync(
        bool passed,
        string message,
        Func<Task<object>> createRecord,
        string directory,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var retentionFailure = passed ? null : await TryRecordAsync(
            createRecord, directory, fileName, cancellationToken).ConfigureAwait(false);
        try
        {
            Assert.IsTrue(passed, message);
        }
        catch (AssertFailedException assertion) when (retentionFailure is not null)
        {
            throw new AssertFailedException(assertion.Message, new AggregateException(assertion, retentionFailure));
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Diagnostic failure must not replace the original trial failure, including cancellation.")]
    internal static async Task<Exception?> TryRecordAsync(
        Func<Task<object>> createRecord,
        string directory,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await WriteAsync(directory, fileName, await createRecord().ConfigureAwait(false), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception failure)
        {
            return failure;
        }
    }

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Durable flushing and atomic rename occur only after resource sampling stops.")]
    internal static async Task WriteAsync(
        string directory,
        string fileName,
        object record,
        int maximumBytes = MaximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName)
            || !fileName.EndsWith(".json", StringComparison.Ordinal))
        {
            throw new ArgumentException("Diagnostic filename must be a single JSON filename.", nameof(fileName));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var fullDirectory = Path.GetFullPath(directory);
        for (var ancestor = new DirectoryInfo(fullDirectory); ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Diagnostic directory must not traverse a symbolic link or reparse point.");
            }
        }
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Private P5 diagnostics require owner-only Unix file modes.");
        }
        Directory.CreateDirectory(fullDirectory, DirectoryMode);
        if (File.GetUnixFileMode(fullDirectory) != DirectoryMode)
        {
            throw new IOException("Diagnostic directory must already be owner-only.");
        }
        var destination = Path.Combine(fullDirectory, fileName);
        var staging = Path.Combine(fullDirectory, $".{Guid.NewGuid():N}.pending");
        var created = false;
        try
        {
            await using (var file = new FileStream(staging, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                created = true;
                using var bounded = new BoundedWriteStream(file, maximumBytes);
                await JsonSerializer.SerializeAsync(bounded, record, record.GetType(), JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, destination, overwrite: false);
        }
        finally
        {
            if (created && File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }

    private sealed class BoundedWriteStream(Stream inner, int maximumBytes) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Reserve(buffer.Length);
            inner.Write(buffer);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reserve(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        private void Reserve(int count)
        {
            if (count > maximumBytes - _written)
            {
                throw new IOException("P5 diagnostic record exceeds its declared byte limit.");
            }
            _written += count;
        }
    }
}
