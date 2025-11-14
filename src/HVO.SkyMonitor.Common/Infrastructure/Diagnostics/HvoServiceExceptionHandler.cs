using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace HVO.SkyMonitor.Common.Infrastructure.Diagnostics;

public sealed class HvoServiceExceptionHandler : IExceptionHandler
{
    private readonly ILogger<HvoServiceExceptionHandler> _logger;
    private readonly ProblemDetailsFactory _problemDetailsFactory;
    private readonly IOptions<ProblemDetailsOptions> _problemDetailsOptions;

    public HvoServiceExceptionHandler(
        ILogger<HvoServiceExceptionHandler> logger,
        ProblemDetailsFactory problemDetailsFactory,
        IOptions<ProblemDetailsOptions> problemDetailsOptions)
    {
        _logger = logger;
        _problemDetailsFactory = problemDetailsFactory;
        _problemDetailsOptions = problemDetailsOptions;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var correlationId = CorrelationIdMiddleware.GetCorrelationId(httpContext);

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId ?? string.Empty
        }))
        {
            _logger.LogError(exception, "Unhandled exception executing request.");
        }

        if (AcceptsHtml(httpContext))
        {
            var target = string.IsNullOrWhiteSpace(correlationId)
                ? "/Error"
                : $"/Error?correlationId={UrlEncoder.Default.Encode(correlationId)}";

            httpContext.Response.Redirect(target);
            return true;
        }

        var problemDetails = _problemDetailsFactory.CreateProblemDetails(
            httpContext,
            statusCode: StatusCodes.Status500InternalServerError,
            title: "An unexpected error occurred while processing the request.",
            detail: exception.Message);

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            problemDetails.Extensions["correlationId"] = correlationId;
        }

        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
        problemDetails.Extensions["traceId"] = traceId;

        var customize = _problemDetailsOptions.Value.CustomizeProblemDetails;
        customize?.Invoke(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        });

        httpContext.Response.StatusCode = problemDetails.Status ?? StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken: cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static bool AcceptsHtml(HttpContext httpContext)
    {
        var acceptHeaders = httpContext.Request.GetTypedHeaders().Accept;
        if (acceptHeaders is null)
        {
            return false;
        }

        foreach (var mediaType in acceptHeaders)
        {
            if (mediaType.MediaType.HasValue &&
                string.Equals(mediaType.MediaType.Value, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
