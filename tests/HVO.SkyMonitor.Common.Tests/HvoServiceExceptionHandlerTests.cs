using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Common.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class HvoServiceExceptionHandlerTests
{
    [TestMethod]
    [DataRow(null, false)]
    [DataRow("application/json", true)]
    public async Task JsonFailureIsSafeAndRetainsDiagnostics(string? accept, bool useActivity)
    {
        using var services = CreateServices();
        using var body = new MemoryStream();
        using var activity = useActivity ? new Activity("exception-response-test") : null;
        activity?.SetIdFormat(ActivityIdFormat.W3C);
        activity?.Start();
        var context = new DefaultHttpContext { RequestServices = services, TraceIdentifier = "request-trace" };
        context.Response.Body = body;
        context.Request.Headers.Accept = accept;
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "operator-correlation";
        var logger = new RecordingLogger();
        var handler = CreateHandler(services, logger);
        var exception = new InvalidOperationException(
            "Private path /var/lib/hvo/private.json and credential=secret-sentinel",
            new IOException("inner-secret-sentinel"));
        var middleware = new CorrelationIdMiddleware(
            async httpContext => Assert.IsTrue(await handler.TryHandleAsync(httpContext, exception, CancellationToken.None)),
            NullLogger<CorrelationIdMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        var payload = Encoding.UTF8.GetString(body.ToArray());
        Assert.IsFalse(payload.Contains("private.json", StringComparison.Ordinal));
        Assert.IsFalse(payload.Contains("secret-sentinel", StringComparison.Ordinal));
        Assert.IsFalse(payload.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.AreEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        using var document = JsonDocument.Parse(payload);
        var problem = document.RootElement;
        Assert.AreEqual(500, problem.GetProperty("status").GetInt32());
        Assert.AreEqual("An unexpected error occurred while processing the request.", problem.GetProperty("title").GetString());
        Assert.IsFalse(problem.TryGetProperty("detail", out _));
        Assert.AreEqual("operator-correlation", problem.GetProperty("correlationId").GetString());
        Assert.AreEqual(activity?.TraceId.ToString() ?? "request-trace", problem.GetProperty("traceId").GetString());
        Assert.AreEqual("safe-extension", problem.GetProperty("support").GetString());
        Assert.AreEqual("operator-correlation", context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
        Assert.AreSame(exception, logger.Exception);
        Assert.AreEqual(LogLevel.Error, logger.Level);
    }

    [TestMethod]
    [DataRow("correlation /?&+", "/Error?correlationId=correlation%20%2F%3F%26%2B")]
    [DataRow("", "/Error")]
    public async Task HtmlFailureRedirectsWithoutExceptionDetails(string correlationId, string expectedLocation)
    {
        using var services = CreateServices();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services, TraceIdentifier = correlationId };
        context.Request.Headers.Accept = "text/html";
        context.Response.Body = body;
        var logger = new RecordingLogger();
        var handler = CreateHandler(services, logger);
        var exception = new InvalidOperationException("private-secret-sentinel");

        Assert.IsTrue(await handler.TryHandleAsync(context, exception, CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.AreEqual(expectedLocation, context.Response.Headers.Location.ToString());
        Assert.AreEqual(0L, body.Length);
        Assert.AreSame(exception, logger.Exception);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["support"] = "safe-extension");
        return services.BuildServiceProvider();
    }

    private static HvoServiceExceptionHandler CreateHandler(IServiceProvider services, RecordingLogger logger) =>
        new(logger, services.GetRequiredService<ProblemDetailsFactory>(), services.GetRequiredService<IOptions<ProblemDetailsOptions>>());

    private sealed class RecordingLogger : ILogger<HvoServiceExceptionHandler>
    {
        public Exception? Exception { get; private set; }

        public LogLevel Level { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Exception = exception;
            Level = logLevel;
        }
    }
}
