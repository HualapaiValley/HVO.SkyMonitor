using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class SiloRetentionEndpoints
{
    internal static void MapSiloRetentionEndpoints(this RouteGroupBuilder lifecycle, Func<HttpContext, IConfiguration, bool> authorize)
    {
        var routes = lifecycle.MapGroup("/silo/retention");
        routes.MapPut("/policy", (CameraAgentSiloRetentionPolicy policy, HttpContext context, IConfiguration configuration,
            CancellationToken cancellationToken) =>
            ExecuteAsync(context, configuration, authorize, async retention =>
            {
                await retention.SetPolicyAsync(policy, cancellationToken).ConfigureAwait(false);
                return Results.NoContent();
            }));
        routes.MapPut("/pin", (CameraAgentSiloPin pin, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
            ExecuteAsync(context, configuration, authorize, async retention =>
            {
                await retention.SetPinAsync(pin, cancellationToken).ConfigureAwait(false);
                return Results.NoContent();
            }));
        routes.MapGet("/dry-run", (DateTimeOffset asOfUtc, bool underPressure, HttpContext context, IConfiguration configuration,
            CancellationToken cancellationToken) =>
            ExecuteAsync(context, configuration, authorize, async retention =>
                Results.Ok(await retention.DryRunAsync(asOfUtc, underPressure, cancellationToken).ConfigureAwait(false))));
    }

    private static async Task<IResult> ExecuteAsync(HttpContext context, IConfiguration configuration,
        Func<HttpContext, IConfiguration, bool> authorize, Func<ICameraAgentSiloRetention, ValueTask<IResult>> action)
    {
        if (!authorize(context, configuration)) return Results.Unauthorized();
        context.Response.Headers.CacheControl = "no-store";
        var retention = context.RequestServices.GetService<ICameraAgentSiloRetention>();
        if (retention is null) return Results.NotFound();
        if (!retention.Ready) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        try
        {
            return await action(retention).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest();
        }
        catch (InvalidOperationException)
        {
            return Results.Conflict();
        }
        catch (IOException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
