using System.Diagnostics;
using Asp.Versioning;
using HVO.SkyMonitor.CameraAgent.ZWO.Components;
using HVO.SkyMonitor.CameraAgent.ZWO.Components.Account;
using HVO.SkyMonitor.CameraAgent.ZWO.Data;
using HVO.SkyMonitor.Common.Infrastructure.Diagnostics;
using HVO.SkyMonitor.Common.Infrastructure.Filters;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using Scalar.AspNetCore;

namespace HVO.SkyMonitor.CameraAgent.ZWO;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Ensure SQLite database directory exists
        var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "data");
        Directory.CreateDirectory(dataDirectory);

        // Update SQLite connection string to use data directory
        var sqliteConnection = builder.Configuration.GetConnectionString("SqliteConnection");
        if (!string.IsNullOrEmpty(sqliteConnection) && !sqliteConnection.Contains("Data Source=/"))
        {
            // Replace relative path with absolute path in data directory
            var dbFileName = Path.GetFileName(sqliteConnection.Replace("Data Source=", "").Split(';')[0]);
            sqliteConnection = $"Data Source={Path.Combine(dataDirectory, dbFileName)};Cache=Shared";
        }

        // Enhanced logging with JSON console formatting and activity tracking
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });

        builder.Logging.Configure(options =>
        {
            options.ActivityTrackingOptions = ActivityTrackingOptions.SpanId
                | ActivityTrackingOptions.TraceId
                | ActivityTrackingOptions.ParentId
                | ActivityTrackingOptions.Baggage
                | ActivityTrackingOptions.Tags;
        });

        // Add service defaults & Aspire client integrations
        builder.AddServiceDefaults();

        // Correlation ID support for distributed tracing
        builder.Services.AddSingleton<ICorrelationIdAccessor, HttpContextCorrelationIdAccessor>();

        // Problem Details with correlation tracking
        builder.Services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions["traceId"] =
                    Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;

                var correlationId = context.HttpContext.Items["CorrelationId"] as string;
                if (!string.IsNullOrWhiteSpace(correlationId))
                {
                    context.ProblemDetails.Extensions["correlationId"] = correlationId;
                }
            };
        });

        // Global exception handler
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

        // HTTP logging with correlation header
        builder.Services.AddHttpLogging(options =>
        {
            options.LoggingFields = HttpLoggingFields.RequestMethod
                | HttpLoggingFields.RequestPath
                | HttpLoggingFields.ResponseStatusCode
                | HttpLoggingFields.Duration;
            options.RequestHeaders.Add(CorrelationIdMiddleware.HeaderName);
        });

        // Time provider for testability
        builder.Services.AddSingleton(TimeProvider.System);

        // API Controllers with automatic model state validation
        builder.Services.AddControllers(options =>
        {
            options.Filters.Add<ValidateModelStateAttribute>();
        });

        // Health checks
        builder.Services.AddHealthChecks()
            .AddDbContextCheck<ApplicationDbContext>("database");

        // OpenAPI support
        builder.Services.AddOpenApi();

        // API Versioning
        builder.Services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new Asp.Versioning.UrlSegmentApiVersionReader();
        }).AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        });

        // Prometheus metrics
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddPrometheusExporter();
            });

        // SQLite database with Aspire integration - using data directory for persistence
        builder.Configuration["ConnectionStrings:SqliteConnection"] = sqliteConnection;
        builder.AddSqliteDbContext<ApplicationDbContext>("SqliteConnection");

        builder.Services.AddDatabaseDeveloperPageExceptionFilter();

        // Identity with cookie authentication
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<IdentityUserAccessor>();
        builder.Services.AddScoped<IdentityRedirectManager>();
        builder.Services.AddScoped<IdentityRevalidatingAuthenticationStateProvider>();
        builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

        builder.Services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.SignIn.RequireConfirmedAccount = true;
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddSignInManager()
        .AddDefaultTokenProviders();

        builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

        // Data Protection - persist keys to avoid cookie invalidation on restart
        var dataProtectionPath = Path.Combine(builder.Environment.ContentRootPath, "DataProtection-Keys");
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
            .SetApplicationName("HVO.SkyMonitor.CameraAgent.ZWO");

        // Authentication: Identity cookies only
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = IdentityConstants.ApplicationScheme;
            options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        })
        .AddIdentityCookies();

        // Authorization
        builder.Services.AddAuthorization();

        // Blazor components
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Scalar API documentation
        builder.Services.AddEndpointsApiExplorer();

        var app = builder.Build();

        // Configure the HTTP request pipeline
        if (!app.Environment.IsDevelopment())
        {
            // Use error page for non-API routes
            app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), appBuilder =>
            {
                appBuilder.UseExceptionHandler("/Error");
            });
            app.UseHsts();
        }

        // Correlation ID middleware (first to ensure all requests have correlation IDs)
        app.UseCorrelationId();

        // Status code pages for non-API routes only (browsers get HTML, APIs get ProblemDetails)
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), appBuilder =>
        {
            appBuilder.UseStatusCodePagesWithReExecute("/Error", "?statusCode={0}");
        });

        // Exception handler
        app.UseExceptionHandler();

        // HTTP logging
        app.UseHttpLogging();

        // Apply database migrations automatically on startup
        using (var scope = app.Services.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            try
            {
                logger.LogInformation("Applying database migrations...");
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Database.Migrate();
                logger.LogInformation("Database migrations applied successfully");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred while applying database migrations");
                throw;
            }
        }

        // Static files and antiforgery
        app.UseStaticFiles();
        app.UseAntiforgery();

        // Authentication & Authorization
        app.UseAuthentication();
        app.UseAuthorization();

        // Map OpenAPI and Scalar
        app.MapOpenApi();
        app.MapScalarApiReference(options =>
        {
            options.Title = "Camera Agent ZWO API";
            options.Theme = ScalarTheme.Mars;
        });

        // Map API controllers
        app.MapControllers();

        // Health checks
        app.MapHealthChecks("/health");

        // Map Blazor components
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        // Map Prometheus metrics endpoint
        app.MapPrometheusScrapingEndpoint();

        // Map default endpoints (health checks, etc.)
        app.MapDefaultEndpoints();

        app.Run();
    }
}
