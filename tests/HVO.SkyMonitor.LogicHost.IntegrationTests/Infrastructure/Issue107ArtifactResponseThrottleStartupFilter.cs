using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

internal sealed class Issue107ArtifactResponseThrottleStartupFilter : IStartupFilter
{
    internal const string HeaderName = "X-HVO-Test-Throttle";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => application =>
        {
            application.Use(async (context, nextMiddleware) =>
            {
                if (!context.Request.Headers.ContainsKey(HeaderName))
                {
                    await nextMiddleware(context).ConfigureAwait(false);
                    return;
                }
                var originalBody = context.Response.Body;
                await using var throttledBody = new ThrottledWriteStream(originalBody);
                context.Response.Body = throttledBody;
                try
                {
                    await nextMiddleware(context).ConfigureAwait(false);
                }
                finally
                {
                    context.Response.Body = originalBody;
                }
            });
            next(application);
        };

    private sealed class ThrottledWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            // The host owns the original response stream.
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => base.DisposeAsync();
    }
}
