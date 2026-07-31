namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class OperatorUiResponseMetricsMiddleware(
    RequestDelegate next,
    OperatorUiTelemetry telemetry)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var operation = OperationFor(context.Request);
        if (operation is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }
        var originalBody = context.Response.Body;
        await using var countingBody = new CountingWriteStream(originalBody);
        context.Response.Body = countingBody;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
            telemetry.RecordResponseBytes(
                operation,
                context.User.Identity?.IsAuthenticated is true ? "member" : "visitor",
                $"{context.Response.StatusCode / 100}xx",
                countingBody.BytesWritten);
        }
    }

    private static string? OperationFor(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method)) return null;
        var path = request.Path;
        if (path == "/") return "public-home";
        if (path.StartsWithSegments("/observatories", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/events", StringComparison.OrdinalIgnoreCase))
        {
            return "public-page";
        }
        if (path.StartsWithSegments("/app", StringComparison.OrdinalIgnoreCase)) return "operations-page";
        if (path.StartsWithSegments("/api/v1.0/public/artifacts", StringComparison.OrdinalIgnoreCase))
        {
            return "public-artifact";
        }
        return null;
    }

    private sealed class CountingWriteStream(Stream inner) : Stream
    {
        internal long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
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
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }
        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            BytesWritten += count;
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            BytesWritten += buffer.Length;
        }
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesWritten += buffer.Length;
        }
    }
}
