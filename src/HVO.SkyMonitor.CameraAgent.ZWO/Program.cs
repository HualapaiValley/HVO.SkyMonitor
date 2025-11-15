using System.Diagnostics;
using Asp.Versioning;
using HVO.SkyMonitor.Common.Infrastructure.Diagnostics;
using HVO.SkyMonitor.Common.Infrastructure.Filters;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.CameraAgent;
using HVO.SkyMonitor.CameraAgent.Extensions;
using HVO.SkyMonitor.CameraAgent.ZWO.Components;
using HVO.SkyMonitor.CameraAgent.ZWO.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using Scalar.AspNetCore;

namespace HVO.SkyMonitor.CameraAgent.ZWO;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();
        builder.Logging.AddDebug();
        builder.Logging.Configure(options =>
        {
            options.ActivityTrackingOptions = ActivityTrackingOptions.SpanId |
                ActivityTrackingOptions.TraceId |
                ActivityTrackingOptions.ParentId |
                ActivityTrackingOptions.Baggage |
                ActivityTrackingOptions.Tags;
        });

        builder.AddServiceDefaults();

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<ICorrelationIdAccessor, HttpContextCorrelationIdAccessor>();

        builder.Services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                var correlationId = CorrelationIdMiddleware.GetCorrelationId(context.HttpContext);
                if (!string.IsNullOrWhiteSpace(correlationId))
                {
                    context.ProblemDetails.Extensions["correlationId"] = correlationId;
                }

                context.ProblemDetails.Extensions["traceId"] =
                    Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
            };
        });

        builder.Services.AddExceptionHandler<HvoServiceExceptionHandler>();

        builder.Services.AddHttpLogging(logging =>
        {
            logging.LoggingFields = HttpLoggingFields.RequestMethod |
                                    HttpLoggingFields.RequestPath |
                                    HttpLoggingFields.ResponseStatusCode |
                                    HttpLoggingFields.Duration;
            logging.RequestHeaders.Add(CorrelationIdMiddleware.HeaderName);
            logging.ResponseHeaders.Add(CorrelationIdMiddleware.HeaderName);
        });

        builder.Services.AddSingleton(TimeProvider.System);

        builder.Services.AddControllers(options =>
        {
            options.Filters.Add<ValidateModelStateAttribute>();
        });
        builder.Services.Configure<ApiBehaviorOptions>(options =>
        {
            options.SuppressModelStateInvalidFilter = true;
        });
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ReportApiVersions = true;
            })
            .AddApiExplorer(options =>
                {
                    options.GroupNameFormat = "'v'VVV";
                    options.SubstituteApiVersionInUrl = true;
                });

        builder.Services.AddHealthChecks();
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddPrometheusExporter();
            });

        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            options.RecordException = true;
        });

        // Central Identity authentication for outbound calls
        builder.Services.AddCentralIdentityAuthentication(builder.Configuration);
        builder.Services.AddSkyMonitorApiClient(builder.Configuration);

        // JWT Bearer authentication for inbound API calls
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                var centralIdentityUrl = builder.Configuration["CentralIdentity:ServiceUrl"];
                options.Authority = centralIdentityUrl;
                options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateAudience = false,
                    ValidateIssuer = true,
                    ValidIssuer = centralIdentityUrl
                };
                options.RequireHttpsMetadata = false; // Development only
            });

        builder.Services.AddAuthorization();

        builder.Services.AddSingleton<IApiKeyHasher, ApiKeyHasher>();
        builder.Services.AddScoped<ISampleStatusService, SampleStatusService>();

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseHsts();
        }

        app.UseCorrelationId();

        app.UseExceptionHandler();

        app.UseHttpLogging();

        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), appBuilder =>
            {
                appBuilder.UseStatusCodePagesWithReExecute("/not-found", "?statusCode={0}");
            }
        );

        app.UseStaticFiles();

        app.UseRouting();

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseAntiforgery();

        app.MapOpenApi();
        app.MapScalarApiReference(options =>
        {
            options.Title = "SkyMonitor Camera Agent ZWO";
        });

        app.MapControllers();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        app.MapPrometheusScrapingEndpoint();

        app.MapDefaultEndpoints();

        app.Run();
    }
}
