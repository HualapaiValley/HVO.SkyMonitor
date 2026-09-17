using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
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
    private bool completed;
    private int nextOrdinal;

    public async Task<IBrowserContext> NewContextAsync(BrowserNewContextOptions? options = null)
    {
        var session = await PlaywrightDiagnosticContext.CreateAsync(
            browser,
            options,
            testContext,
            Interlocked.Increment(ref nextOrdinal)).ConfigureAwait(false);
        sessions.Add(session);
        var lease = BrowserContextLease.Create(session.Context);
        leases.Add(lease, session);
        return lease;
    }

    public async Task ReleaseAsync(IBrowserContext lease)
    {
        if (!leases.TryGetValue(lease, out var session))
        {
            return;
        }
        await session.CompleteAsync().ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);
        leases.Remove(lease);
        sessions.Remove(session);
    }

    public async Task CompleteAsync()
    {
        if (completed)
        {
            return;
        }
        completed = true;
        foreach (var session in sessions)
        {
            await session.CompleteAsync().ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The collector runs during assertion unwinding; one context's diagnostic failure must not prevent the remaining contexts from capture or replace the original assertion.")]
    public async ValueTask DisposeAsync()
    {
        for (var index = sessions.Count - 1; index >= 0; index--)
        {
            try
            {
                await sessions[index].DisposeAsync().ConfigureAwait(false);
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
    private int nextPageOrdinal;
    private bool completed;
    private bool disposed;

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
        if (completed)
        {
            return;
        }
        completed = true;
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
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (!completed)
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
        await context.DisposeAsync().ConfigureAwait(false);
    }

    private void Observe(IPage page)
    {
        var evidence = pages.GetOrAdd(
            page,
            _ => new PageEvidence(Interlocked.Increment(ref nextPageOrdinal)));
        page.Console += (_, message) => evidence.Console.Enqueue($"{message.Type}: {message.Text}");
        page.PageError += (_, error) => evidence.PageErrors.Enqueue(error);
        page.RequestFailed += (_, request) => evidence.Network.Enqueue(
            $"request-failed {request.Method} {request.ResourceType} {SanitizeUrl(request.Url)} {request.Failure}");
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                evidence.Network.Enqueue(
                    $"response {response.Status} {response.Request.Method} {response.Request.ResourceType} {SanitizeUrl(response.Url)}");
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
                await context.Tracing.StopAsync(new() { Path = path }).ConfigureAwait(false);
                SanitizeTrace(path);
            }).ConfigureAwait(false);

        var pageOrdinal = 0;
        foreach (var pair in pages.OrderBy(pair => pair.Value.Ordinal))
        {
            pageOrdinal++;
            var pagePrefix = Path.Combine(directory, $"{artifactPrefix}.page-{pageOrdinal:D3}");
            await CaptureAsync(
                $"{pagePrefix}.screenshot.png",
                path => pair.Key.ScreenshotAsync(new()
                {
                    Path = path,
                    FullPage = true,
                    Mask =
                    [
                        pair.Key.Locator("input"),
                        pair.Key.Locator("textarea"),
                        pair.Key.Locator("[data-sensitive]")
                    ]
                })).ConfigureAwait(false);
            await CaptureTextAsync($"{pagePrefix}.dom.html", () => CaptureSanitizedDomAsync(pair.Key))
                .ConfigureAwait(false);
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
          const root = document.documentElement.cloneNode(true);
          root.querySelectorAll('script').forEach(element => element.remove());
          root.querySelectorAll('input').forEach(element => {
            if (element.hasAttribute('value') || !['checkbox', 'radio'].includes((element.getAttribute('type') || '').toLowerCase())) {
              element.setAttribute('value', '[REDACTED]');
            }
            element.removeAttribute('checked');
          });
          root.querySelectorAll('textarea').forEach(element => { element.textContent = '[REDACTED]'; });
          root.querySelectorAll('option').forEach(element => element.removeAttribute('selected'));
          root.querySelectorAll('[href], [src], [action]').forEach(element => {
            for (const name of ['href', 'src', 'action']) {
              const value = element.getAttribute(name);
              if (!value) continue;
              try {
                const url = new URL(value, document.baseURI);
                url.search = '';
                url.hash = '';
                element.setAttribute(name, url.toString());
              } catch { element.setAttribute(name, '[REDACTED-URL]'); }
            }
          });
          return '<!DOCTYPE html>\n' + root.outerHTML;
        }
        """);

    internal static string Sanitize(string value)
    {
        var sanitized = SensitiveValueRegex().Replace(value, "$1=[REDACTED]");
        sanitized = BearerRegex().Replace(sanitized, "Bearer [REDACTED]");
        sanitized = QueryValueRegex().Replace(sanitized, "$1=[REDACTED]");
        sanitized = WindowsPathRegex().Replace(sanitized, "[PRIVATE-PATH]");
        return UnixPathRegex().Replace(sanitized, "[PRIVATE-PATH]");
    }

    internal static void SanitizeTrace(string path)
    {
        var temporaryPath = $"{path}.sanitized";
        using (var source = ZipFile.OpenRead(path))
        using (var target = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                // Playwright stores request/response bodies and other opaque payloads here. Sidecar screenshots are
                // retained explicitly; bodies, cookies and source payloads are excluded from the durable trace.
                if (entry.FullName.StartsWith("resources/", StringComparison.Ordinal))
                {
                    continue;
                }
                var output = target.CreateEntry(entry.FullName, CompressionLevel.SmallestSize);
                using var inputStream = entry.Open();
                using var reader = new StreamReader(inputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                using var outputStream = output.Open();
                using var writer = new StreamWriter(outputStream, new UTF8Encoding(false));
                writer.Write(SanitizeTraceText(reader.ReadToEnd()));
            }
        }
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string SanitizeTraceText(string value)
    {
        var output = new StringBuilder(value.Length);
        using var reader = new StringReader(value);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                var node = JsonNode.Parse(line);
                if (node is null)
                {
                    output.AppendLine();
                    continue;
                }
                if (node["type"]?.GetValue<string>() is "frame-snapshot" or "screencast-frame")
                {
                    continue;
                }
                SanitizeJson(node);
                output.AppendLine(node.ToJsonString());
            }
            catch (JsonException)
            {
                output.AppendLine(Sanitize(line));
            }
        }
        return output.ToString();
    }

    private static void SanitizeJson(JsonNode node)
    {
        if (node is JsonObject objectNode)
        {
            foreach (var property in objectNode.ToArray())
            {
                if (property.Key is "headers" or "cookies")
                {
                    objectNode[property.Key] = new JsonArray();
                    continue;
                }
                if (property.Key is "params" or "result" or "postData" or "body" or "html"
                    || property.Key == "text" && objectNode.ContainsKey("mimeType"))
                {
                    objectNode[property.Key] = "[REDACTED]";
                    continue;
                }
                if (SensitivePropertyNameRegex().IsMatch(property.Key))
                {
                    objectNode[property.Key] = "[REDACTED]";
                    continue;
                }
                if (property.Key == "queryString" && property.Value is JsonArray query)
                {
                    foreach (var item in query.OfType<JsonObject>())
                    {
                        item["value"] = "[REDACTED]";
                    }
                    continue;
                }
                if (property.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    objectNode[property.Key] = Sanitize(text);
                }
                else if (property.Value is not null)
                {
                    SanitizeJson(property.Value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array.Where(item => item is not null))
            {
                SanitizeJson(item!);
            }
        }
    }

    internal static string SanitizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return "[INVALID-URL]";
        }
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}{uri.AbsolutePath}";
    }

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

    [GeneratedRegex("(?i)password|token|secret|authorization|cookie|antiforgery")]
    private static partial Regex SensitivePropertyNameRegex();

    [GeneratedRegex(@"[A-Za-z]:\\(?:[^\\\r\n]+\\)*[^\\\r\n]*")]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex("/(?:home|Users|private|tmp)/[^\\s\\\"'<>]+")]
    private static partial Regex UnixPathRegex();

    private sealed class PageEvidence(int ordinal)
    {
        public int Ordinal { get; } = ordinal;
        public ConcurrentQueue<string> Console { get; } = new();
        public ConcurrentQueue<string> PageErrors { get; } = new();
        public ConcurrentQueue<string> Network { get; } = new();
    }
}
