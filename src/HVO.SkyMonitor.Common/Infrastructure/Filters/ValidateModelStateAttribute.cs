using System.Diagnostics;
using HVO.SkyMonitor.Common.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HVO.SkyMonitor.Common.Infrastructure.Filters;

public sealed class ValidateModelStateAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ModelState.IsValid)
        {
            return;
        }

        var problemDetails = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "The request could not be processed due to validation errors.",
            Detail = "Review the errors and try again.",
            Instance = context.HttpContext.Request.Path
        };

        var correlationId = CorrelationIdMiddleware.GetCorrelationId(context.HttpContext);
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            problemDetails.Extensions["correlationId"] = correlationId;
        }

        problemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;

        context.Result = new ObjectResult(problemDetails)
        {
            StatusCode = problemDetails.Status
        };
    }
}
