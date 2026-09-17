using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by the Manual browser suites after browser launch.")]
internal sealed class PlaywrightDiagnostics(IBrowser browser, TestContext testContext) : IAsyncDisposable
{
    private readonly List<PlaywrightDiagnosticContext> sessions = [];
    private readonly Dictionary<IBrowserContext, PlaywrightDiagnosticContext> leases = [];
    private readonly object ownershipLock = new();
    private bool completed;
    private bool disposed;
    private int nextOrdinal;

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The created session is transferred into the collector's owned session list before the lease is returned.")]
    public async Task<IBrowserContext> NewContextAsync(BrowserNewContextOptions? options = null)
    {
        var session = await PlaywrightDiagnosticContext.CreateAsync(
            browser,
            options,
            testContext,
            Interlocked.Increment(ref nextOrdinal)).ConfigureAwait(false);
        var lease = BrowserContextLease.Create(session.Context);
        var accepted = false;
        try
        {
            lock (ownershipLock)
            {
                if (!disposed && !completed)
                {
                    sessions.Add(session);
                    leases.Add(lease, session);
                    accepted = true;
                }
            }
            if (!accepted)
            {
                throw new InvalidOperationException("Browser diagnostics have already completed or disposed.");
            }
        }
        catch
        {
            lock (ownershipLock)
            {
                sessions.Remove(session);
                leases.Remove(lease);
            }
            await session.CompleteAsync().ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return lease;
    }

    public async Task ReleaseAsync(IBrowserContext lease)
    {
        PlaywrightDiagnosticContext? session;
        lock (ownershipLock)
        {
            if (!leases.TryGetValue(lease, out session))
            {
                return;
            }
            leases.Remove(lease);
            sessions.Remove(session);
        }
        await session.CompleteAsync().ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);
    }

    public async Task CompleteAsync()
    {
        if (completed)
        {
            return;
        }
        PlaywrightDiagnosticContext[] snapshot;
        lock (ownershipLock)
        {
            if (completed)
            {
                return;
            }
            snapshot = sessions.ToArray();
            foreach (var session in snapshot)
            {
                session.BeginSuccessfulCompletion();
            }
            completed = true;
        }
        foreach (var session in snapshot)
        {
            await session.CompleteAsync().ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The collector runs during assertion unwinding; one context's diagnostic failure must not prevent the remaining contexts from capture or replace the original assertion.")]
    public async ValueTask DisposeAsync()
    {
        PlaywrightDiagnosticContext[] snapshot;
        lock (ownershipLock)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            snapshot = sessions.ToArray();
            sessions.Clear();
            leases.Clear();
        }
        for (var index = snapshot.Length - 1; index >= 0; index--)
        {
            try
            {
                await snapshot[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                try
                {
                    testContext.WriteLine($"Playwright diagnostic session disposal failed: {PlaywrightDiagnosticContext.Sanitize(exception.Message)}");
                }
                catch (Exception)
                {
                    // Preserve the original browser assertion.
                }
            }
        }
    }
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "DispatchProxy constructs this type through reflection.")]
[SuppressMessage("Performance", "CA1852:Seal internal types",
    Justification = "DispatchProxy dynamically derives the generated interface proxy from this base type.")]
internal class BrowserContextLease : DispatchProxy
{
    private IBrowserContext target = null!;

    internal static IBrowserContext Create(IBrowserContext target)
    {
        var lease = Create<IBrowserContext, BrowserContextLease>();
        ((BrowserContextLease)(object)lease).target = target;
        return lease;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == nameof(IAsyncDisposable.DisposeAsync) && targetMethod.GetParameters().Length == 0)
        {
            return ValueTask.CompletedTask;
        }
        try
        {
            return targetMethod.Invoke(target, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}

internal sealed partial class PlaywrightDiagnosticContext : IAsyncDisposable
{
    private readonly IBrowserContext context;
    private readonly TestContext testContext;
    private readonly string artifactPrefix;
    private readonly ConcurrentDictionary<IPage, PageEvidence> pages = new();
    private readonly object lifecycleLock = new();
    private int nextPageOrdinal;
    private bool disposed;
    private Task? completionTask;

    private PlaywrightDiagnosticContext(IBrowserContext context, TestContext testContext, string artifactPrefix)
    {
        this.context = context;
        this.testContext = testContext;
        this.artifactPrefix = artifactPrefix;
        context.Page += (_, page) => Observe(page);
        foreach (var page in context.Pages)
        {
            Observe(page);
        }
    }

    public IBrowserContext Context => context;

    public static async Task<PlaywrightDiagnosticContext> CreateAsync(
        IBrowser browser,
        BrowserNewContextOptions? options,
        TestContext testContext,
        int ordinal)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(testContext);
        var context = await browser.NewContextAsync(options).ConfigureAwait(false);
        var prefix = string.Create(
            CultureInfo.InvariantCulture,
            $"{SafeName(testContext.FullyQualifiedTestClassName)}.{SafeName(testContext.TestName)}.context-{ordinal:D4}");
        var result = new PlaywrightDiagnosticContext(context, testContext, prefix);
        try
        {
            await context.Tracing.StartAsync(new()
            {
                Screenshots = true,
                Snapshots = true,
                Sources = true
            }).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Discarding successful-run tracing is test infrastructure and must not turn a passing product assertion into a failure.")]
    public async Task CompleteAsync()
    {
        await BeginSuccessfulCompletion().ConfigureAwait(false);
    }

    internal Task BeginSuccessfulCompletion()
    {
        lock (lifecycleLock)
        {
            if (disposed)
            {
                return Task.CompletedTask;
            }
            completionTask ??= CompleteCoreAsync();
            return completionTask;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Discarding successful-run tracing is best-effort test infrastructure and must not fail a passing product test.")]
    private async Task CompleteCoreAsync()
    {
        try
        {
            await context.Tracing.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportCaptureFailure("successful trace discard", exception);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Failure-path context disposal must never replace the browser assertion that triggered capture.")]
    public async ValueTask DisposeAsync()
    {
        Task? successfulCompletion;
        lock (lifecycleLock)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            successfulCompletion = completionTask;
        }
        if (successfulCompletion is null)
        {
            try
            {
                await CaptureFailureAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ReportCaptureFailure("failure bundle", exception);
            }
            try
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ReportCaptureFailure("context disposal", exception);
            }
            return;
        }
        await successfulCompletion.ConfigureAwait(false);
        await context.DisposeAsync().ConfigureAwait(false);
    }

    private void Observe(IPage page)
    {
        var evidence = pages.GetOrAdd(
            page,
            _ => new PageEvidence(Interlocked.Increment(ref nextPageOrdinal)));
        if (!evidence.TryObserve())
        {
            return;
        }
        page.Console += (_, message) => evidence.Record("console", $"type={BoundedConsoleType(message.Type)}");
        page.PageError += (_, _) => evidence.Record("page-error", string.Empty);
        page.RequestFailed += (_, request) => evidence.Record(
            "request-failed",
            $"method={BoundedMethod(request.Method)} resource={BoundedResourceType(request.ResourceType)}");
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                evidence.Record(
                    "response-failure",
                    $"status={response.Status} method={BoundedMethod(response.Request.Method)} resource={BoundedResourceType(response.Request.ResourceType)}");
            }
        };
    }

    private async Task CaptureFailureAsync()
    {
        var directory = Path.Combine(
            testContext.ResultsDirectory ?? Path.GetTempPath(),
            "playwright-failures");
        Directory.CreateDirectory(directory);
        await CaptureAsync(
            Path.Combine(directory, $"{artifactPrefix}.trace.zip"),
            async path =>
            {
                var rawPath = Path.Combine(Path.GetTempPath(), $"hvo-playwright-{Guid.NewGuid():N}.zip");
                try
                {
                    await context.Tracing.StopAsync(new() { Path = rawPath }).ConfigureAwait(false);
                    BuildSafeTrace(path, pages.Values);
                }
                finally
                {
                    File.Delete(rawPath);
                    File.Delete($"{path}.tmp");
                }
            }).ConfigureAwait(false);

        var pageOrdinal = 0;
        foreach (var pair in pages.ToArray().OrderBy(pair => pair.Value.Ordinal))
        {
            pageOrdinal++;
            var pagePrefix = Path.Combine(directory, $"{artifactPrefix}.page-{pageOrdinal:D3}");
            await CaptureTextAsync($"{pagePrefix}.dom.html", () => CaptureSanitizedDomAsync(pair.Key))
                .ConfigureAwait(false);
            await CaptureAsync(
                $"{pagePrefix}.screenshot.png",
                path => CaptureLayoutScreenshotAsync(pair.Key, path)).ConfigureAwait(false);
            await CaptureTextAsync($"{pagePrefix}.console.log", () => Task.FromResult(string.Join('\n', pair.Value.Console)))
                .ConfigureAwait(false);
            await CaptureTextAsync($"{pagePrefix}.page-errors.log", () => Task.FromResult(string.Join('\n', pair.Value.PageErrors)))
                .ConfigureAwait(false);
            await CaptureTextAsync($"{pagePrefix}.network.log", () => Task.FromResult(string.Join('\n', pair.Value.Network)))
                .ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Each best-effort artifact is independent and must never replace the browser assertion that triggered capture.")]
    private async Task CaptureAsync(string path, Func<string, Task> capture)
    {
        try
        {
            await capture(path).ConfigureAwait(false);
            if (File.Exists(path))
            {
                testContext.AddResultFile(path);
            }
        }
        catch (Exception exception)
        {
            ReportCaptureFailure(Path.GetFileName(path), exception);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort failure diagnostics must never replace the browser assertion that triggered capture.")]
    private void ReportCaptureFailure(string artifact, Exception exception)
    {
        try
        {
            testContext.WriteLine($"Playwright diagnostic capture failed for {artifact}: {Sanitize(exception.Message)}");
        }
        catch (Exception)
        {
            // The original browser assertion remains the reported failure even when diagnostics cannot be reported.
        }
    }

    private Task CaptureTextAsync(string path, Func<Task<string>> capture) => CaptureAsync(path, async target =>
    {
        var text = Sanitize(await capture().ConfigureAwait(false));
        await File.WriteAllTextAsync(target, text, new UTF8Encoding(false)).ConfigureAwait(false);
    });

    private static Task<string> CaptureSanitizedDomAsync(IPage page) => page.EvaluateAsync<string>("""
        () => {
          const build = (element, depth) => {
            const node = document.createElement('node');
            node.setAttribute('depth', String(depth));
            node.setAttribute('children', String(element.children.length));
            node.setAttribute('attributes', String(element.attributes.length));
            for (const child of element.children) node.append(build(child, depth + 1));
            return node;
          };
          return '<!DOCTYPE html>\n' + build(document.documentElement, 0).outerHTML;
        }
        """);

    private async Task CaptureLayoutScreenshotAsync(IPage source, string path)
    {
        var layout = await source.EvaluateAsync<JsonElement>("""
        () => {
          const quantize = value => Math.max(0, Math.min(16384, Math.round(value / 16) * 16));
          const rects = [...document.querySelectorAll('body *')]
            .map(element => element.getBoundingClientRect())
            .filter(rect => rect.width > 1 && rect.height > 1)
            .slice(0, 5000)
            .map(rect => ({ left: quantize(rect.left + scrollX), top: quantize(rect.top + scrollY), width: quantize(rect.width), height: quantize(rect.height) }));
          return { width: quantize(Math.max(document.documentElement.scrollWidth, innerWidth)), height: quantize(Math.max(document.documentElement.scrollHeight, innerHeight)), rects };
        }
        """).ConfigureAwait(false);
        var html = new StringBuilder("<!DOCTYPE html><html><body style=\"margin:0;background:#111;position:relative;");
        html.Append("width:").Append(layout.GetProperty("width").GetInt32()).Append("px;height:")
            .Append(layout.GetProperty("height").GetInt32()).Append("px\">");
        foreach (var rect in layout.GetProperty("rects").EnumerateArray())
        {
            html.Append("<div style=\"position:absolute;background:#333;border:1px solid #777;box-sizing:border-box;left:")
                .Append(rect.GetProperty("left").GetInt32()).Append("px;top:")
                .Append(rect.GetProperty("top").GetInt32()).Append("px;width:")
                .Append(rect.GetProperty("width").GetInt32()).Append("px;height:")
                .Append(rect.GetProperty("height").GetInt32()).Append("px\"></div>");
        }
        html.Append("</body></html>");
        var screenshotPage = await context.NewPageAsync().ConfigureAwait(false);
        try
        {
            await screenshotPage.SetContentAsync(html.ToString()).ConfigureAwait(false);
            await screenshotPage.ScreenshotAsync(new() { Path = path, FullPage = true }).ConfigureAwait(false);
        }
        finally
        {
            await screenshotPage.CloseAsync().ConfigureAwait(false);
        }
    }

    internal static string Sanitize(string value)
    {
        var sanitized = SensitiveValueRegex().Replace(value, "$1=[REDACTED]");
        sanitized = BearerRegex().Replace(sanitized, "Bearer [REDACTED]");
        sanitized = QueryValueRegex().Replace(sanitized, "$1=[REDACTED]");
        sanitized = WindowsPathRegex().Replace(sanitized, "[PRIVATE-PATH]");
        return UnixPathRegex().Replace(sanitized, "[PRIVATE-PATH]");
    }

    private static void BuildSafeTrace(string path, IEnumerable<PageEvidence> pages)
    {
        var temporaryPath = $"{path}.tmp";
        try
        {
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("trace.json", CompressionLevel.SmallestSize);
                using var stream = entry.Open();
                using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
                writer.WriteStartObject();
                writer.WriteString("schema", "hvo-playwright-failure-trace-v1");
                writer.WriteStartArray("pages");
                foreach (var page in pages.OrderBy(page => page.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("ordinal", page.Ordinal);
                    writer.WriteNumber("consoleEvents", page.Console.Count);
                    writer.WriteNumber("pageErrors", page.PageErrors.Count);
                    writer.WriteNumber("networkFailures", page.Network.Count);
                    writer.WriteStartArray("timeline");
                    foreach (var item in page.Timeline.OrderBy(item => item.Sequence))
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("sequence", item.Sequence);
                        writer.WriteString("kind", item.Kind);
                        writer.WriteString("detail", item.Detail);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string BoundedMethod(string value) => value.ToUpperInvariant() switch
    {
        "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" => value.ToUpperInvariant(),
        _ => "OTHER"
    };

    private static string BoundedResourceType(string value) => value.ToUpperInvariant() switch
    {
        "DOCUMENT" or "STYLESHEET" or "IMAGE" or "MEDIA" or "FONT" or "SCRIPT" or "TEXTTRACK" or
        "XHR" or "FETCH" or "EVENTSOURCE" or "WEBSOCKET" or "MANIFEST" or "OTHER" => value.ToUpperInvariant(),
        _ => "OTHER"
    };

    private static string BoundedConsoleType(string value) => value.ToUpperInvariant() switch
    {
        "LOG" or "DEBUG" or "INFO" or "ERROR" or "WARNING" or "DIR" or "DIRXML" or "TABLE" or
        "TRACE" or "CLEAR" or "STARTGROUP" or "STARTGROUPCOLLAPSED" or "ENDGROUP" or "ASSERT" or
        "PROFILE" or "PROFILEEND" or "COUNT" or "TIMEEND" => value.ToUpperInvariant(),
        _ => "OTHER"
    };

    internal static string SafeName(string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
        var builder = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' ? character : '-');
        }
        return builder.ToString().Trim('-');
    }

    [GeneratedRegex("(?i)\\b(password|token|secret|authorization|cookie|antiforgery)[\\s\"']*[:=][\\s\"']*[^\\s\"'<>]+")]
    private static partial Regex SensitiveValueRegex();

    [GeneratedRegex("(?i)Bearer\\s+[^\\s\"'<>]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex("([?&][^=&#\\s]+)=[^&#\\s\"'<>]+")]
    private static partial Regex QueryValueRegex();

    [GeneratedRegex(@"[A-Za-z]:\\(?:[^\\\r\n]+\\)*[^\\\r\n]*")]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex("/(?:home|Users|private|tmp)/[^\\s\\\"'<>]+")]
    private static partial Regex UnixPathRegex();

    private sealed class PageEvidence(int ordinal)
    {
        private int observed;
        private long sequence;
        public int Ordinal { get; } = ordinal;
        public ConcurrentQueue<string> Console { get; } = new();
        public ConcurrentQueue<string> PageErrors { get; } = new();
        public ConcurrentQueue<string> Network { get; } = new();
        public ConcurrentQueue<TraceEvent> Timeline { get; } = new();
        public bool TryObserve() => Interlocked.Exchange(ref observed, 1) == 0;
        public void Record(string kind, string detail)
        {
            var item = new TraceEvent(Interlocked.Increment(ref sequence), kind, detail);
            Timeline.Enqueue(item);
            if (kind == "console") Console.Enqueue(detail);
            else if (kind == "page-error") PageErrors.Enqueue(detail);
            else Network.Enqueue(detail);
        }
    }

    private sealed record TraceEvent(long Sequence, string Kind, string Detail);
}
