using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Asp.Versioning;
using HVO.SkyMonitor.Common.Infrastructure.Diagnostics;
using HVO.SkyMonitor.Common.Infrastructure.Filters;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.RandomImage;
using HVO.SkyMonitor.CameraAgent.Extensions;
using HVO.SkyMonitor.CameraAgent.Components;
using HVO.SkyMonitor.CameraAgent.Components.Account;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.Common.Observability;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using Scalar.AspNetCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent;

public class Program
{
    public static async Task Main(string[] args)
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

        builder.AddSkyMonitorObservability();

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
        builder.Services.AddOptions<DeviceProvisioningOptions>()
            .Bind(builder.Configuration.GetSection("DeviceProvisioning"))
            .ValidateOnStart();
        builder.Services.AddSingleton<IDeviceIdentityStore, DeviceIdentityStore>();
        builder.Services.AddSingleton<IDeviceSecretStore, DeviceSecretStore>();
        builder.Services.AddSingleton<IDeviceRigProfileSeeder, DeviceRigProfileSeeder>();
        builder.Services.AddScoped<DeviceBootstrapWorkflow>();

        var localIdentitySection = builder.Configuration.GetSection("LocalIdentity");
        builder.Services.AddOptions<LocalIdentityOptions>()
            .Bind(localIdentitySection)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var localIdentitySettings = localIdentitySection.Get<LocalIdentityOptions>() ?? new LocalIdentityOptions();
        var identityDbPath = ResolveIdentityDatabasePath(localIdentitySettings.DatabasePath, builder.Environment.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(identityDbPath)!);
        var identityConnectionString = $"Data Source={identityDbPath}";

        var dataProtectionPath = Path.Combine(builder.Environment.ContentRootPath, "DataProtection-Keys");
        Directory.CreateDirectory(dataProtectionPath);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));

        builder.Services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseSqlite(identityConnectionString);
            options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        });
        builder.Services.AddDatabaseDeveloperPageExceptionFilter();

        builder.Services.AddControllersWithViews(options =>
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
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<IdentityUserAccessor>();
        builder.Services.AddScoped<IdentityRedirectManager>();
        builder.Services.AddScoped<IdentityRevalidatingAuthenticationStateProvider>();
        builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

        builder.Services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.SignIn.RequireConfirmedAccount = false;
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddSignInManager()
        .AddDefaultTokenProviders();

        builder.Services.AddSingleton<IEmailSender<ApplicationUser>, LoggingEmailSender>();
        builder.Services.AddSingleton<CameraAgentIdentitySeeder>();

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

        var healthChecks = builder.Services.AddSkyMonitorHealthChecks();
        healthChecks.AddDbContextCheck<ApplicationDbContext>("identity-database", tags: ["dependency"]);
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddPrometheusExporter();
            });

        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            options.RecordException = true;
        });

        builder.Services.AddCentralIdentityAuthentication(builder.Configuration);
        builder.Services.AddSingleton<IConfigureOptions<CentralIdentityOptions>, DeviceSecretsCentralIdentityConfigurator>();
        builder.Services.AddSkyMonitorApiClient(builder.Configuration);

        var authenticationBuilder = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = IdentityConstants.ApplicationScheme;
        });

        authenticationBuilder.AddIdentityCookies();

        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Account/Login";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.Cookie.Name = "CameraAgent.Auth";
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
        });

        builder.Services.AddAuthorization();

        builder.Services.AddOptions<CapturePreviewOptions>()
            .Bind(builder.Configuration.GetSection("CapturePreview"))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddCameraAgentInfrastructure(builder.Configuration);
        builder.Services.AddCameraModule<RandomImageCameraModule>("RandomImage");

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
            options.Title = "SkyMonitor Camera Agent";
        });

        app.MapControllers();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        app.MapAdditionalIdentityEndpoints();
        app.MapPrometheusScrapingEndpoint();

        app.MapSkyMonitorHealthEndpoints();

        using (var scope = app.Services.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<CameraAgentIdentitySeeder>();
            await seeder.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await app.RunAsync().ConfigureAwait(false);
    }

    private static string ResolveIdentityDatabasePath(string? configuredPath, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.Combine(contentRoot, "App_Data", "cameraagent_identity.db");
        }

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, contentRoot);
    }
}
