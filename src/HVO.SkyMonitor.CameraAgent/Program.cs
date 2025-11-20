using System.Diagnostics;
using System.Threading.Tasks;
using Asp.Versioning;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry;
using HVO.SkyMonitor.Common.Infrastructure.Diagnostics;
using HVO.SkyMonitor.Common.Infrastructure.Filters;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.CameraAgent.Extensions;
using HVO.SkyMonitor.CameraAgent.Components;
using Microsoft.AspNetCore.Components.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using HVO.SkyMonitor.Common.Observability;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using OpenTelemetry.Extensions.Hosting;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using Scalar.AspNetCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using HVO.SkyMonitor.CameraAgent.Authentication;

namespace HVO.SkyMonitor.CameraAgent;

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

        builder.AddSkyMonitorObservability(otel =>
        {
            if (otel is OpenTelemetryBuilder concreteBuilder)
            {
                concreteBuilder.WithMetrics(metrics =>
                {
                    metrics.AddMeter(CaptureTelemetryMetricsRecorder.MeterName);
                    metrics.AddPrometheusExporter();
                })
                .UseAzureMonitor();
            }
        });

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
        builder.Services.AddCameraAgentInfrastructure(builder.Configuration);
        builder.Services.AddOptions<CapturePreviewOptions>()
            .Bind(builder.Configuration.GetSection("CapturePreview"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

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

        var sharedConnectionString = builder.Configuration.GetConnectionString("DataProtection")
            ?? builder.Configuration.GetConnectionString("skymonitordb")
            ?? builder.Configuration["ConnectionStrings:skymonitordb"]
            ?? "Host=localhost;Port=5432;Database=skymonitordb;Username=postgres;Password=postgres";

        var dataProtectionSchema = builder.Configuration.GetValue<string>("DataProtection:Schema");
        var dataProtectionTable = builder.Configuration.GetValue<string>("DataProtection:Table");

        builder.Services.AddPostgresDataProtectionKeyRepository(options =>
        {
            options.ConnectionString = sharedConnectionString;
            if (!string.IsNullOrWhiteSpace(dataProtectionSchema))
            {
                options.SchemaName = dataProtectionSchema!;
            }

            if (!string.IsNullOrWhiteSpace(dataProtectionTable))
            {
                options.TableName = dataProtectionTable!;
            }
        });

        builder.Services.AddDataProtection()
            .SetApplicationName("HVO.SkyMonitor");

        builder.Services.AddCascadingAuthenticationState();

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
        healthChecks.AddCheck<LogicHostHealthCheck>("logic-host", tags: ["dependency"]);

        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            options.RecordException = true;
        });

        // Central authentication - no local Identity
        builder.Services.AddCentralIdentityAuthentication(builder.Configuration);
        builder.Services.AddSkyMonitorApiClient(builder.Configuration);

        var interactiveClientSection = builder.Configuration.GetSection("CentralIdentity:InteractiveClient");
        builder.Services.AddOptions<InteractiveClientOptions>()
            .Bind(interactiveClientSection)
            .ValidateDataAnnotations()
            .Validate(options => options.Scopes.Count > 0, "At least one interactive scope is required.")
            .ValidateOnStart();

        const string skyMonitorApiResource = "skymonitor_api";
        var centralIdentityAuthority = builder.Configuration["CentralIdentity:ServiceUrl"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("CentralIdentity:ServiceUrl configuration is required.");
        var interactiveClientOptions = interactiveClientSection.Get<InteractiveClientOptions>()
            ?? throw new InvalidOperationException("CentralIdentity:InteractiveClient configuration is required.");
        var internalAuthorityUri = new Uri(string.Concat(centralIdentityAuthority, "/"), UriKind.Absolute);
        var publicAuthorityUri = interactiveClientOptions.PublicAuthority ?? internalAuthorityUri;

        var authenticationBuilder = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = CameraAgentAuthenticationSchemes.InteractiveCookie;
            options.DefaultChallengeScheme = CameraAgentAuthenticationSchemes.InteractiveOpenIdConnect;
        })
        .AddCookie(CameraAgentAuthenticationSchemes.InteractiveCookie, options =>
        {
            options.Cookie.Name = "CameraAgent.Auth";
            options.SlidingExpiration = true;
        })
        .AddOpenIdConnect(CameraAgentAuthenticationSchemes.InteractiveOpenIdConnect, options =>
        {
            options.Authority = internalAuthorityUri.ToString();
            options.ClientId = interactiveClientOptions.ClientId;
            options.ClientSecret = interactiveClientOptions.ClientSecret;
            options.SignInScheme = CameraAgentAuthenticationSchemes.InteractiveCookie;
            options.CallbackPath = interactiveClientOptions.CallbackPath;
            options.SignedOutCallbackPath = interactiveClientOptions.SignedOutCallbackPath;
            options.RemoteSignOutPath = interactiveClientOptions.RemoteSignOutPath;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.UsePkce = true;
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.Scope.Clear();
            foreach (var scope in interactiveClientOptions.Scopes)
            {
                options.Scope.Add(scope);
            }

            options.Events ??= new OpenIdConnectEvents();
            options.Events.OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.IssuerAddress = BuildOidcEndpoint(publicAuthorityUri, "connect/authorize");
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToIdentityProviderForSignOut = context =>
            {
                context.ProtocolMessage.IssuerAddress = BuildOidcEndpoint(publicAuthorityUri, "connect/endsession");
                return Task.CompletedTask;
            };
        });

        authenticationBuilder.AddJwtBearer(options =>
        {
            options.Audience = skyMonitorApiResource;
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        });

        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfiguration>((options, configuration) =>
            {
                var centralIdentityAuthorityValue = configuration["CentralIdentity:ServiceUrl"]?.TrimEnd('/') ?? string.Empty;
                if (string.IsNullOrWhiteSpace(centralIdentityAuthorityValue))
                {
                    return;
                }

                var issuerWithTrailingSlash = string.Concat(centralIdentityAuthorityValue, "/");
                options.Authority = issuerWithTrailingSlash;
                options.TokenValidationParameters.ValidIssuers = new[]
                {
                    issuerWithTrailingSlash,
                    centralIdentityAuthorityValue
                };
            });

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthorizationPolicyNames.ApiKeyOrCookie, policy =>
            {
                policy.AddAuthenticationSchemes(
                    JwtBearerDefaults.AuthenticationScheme,
                    CameraAgentAuthenticationSchemes.InteractiveCookie);
                policy.RequireAuthenticatedUser();
            });

            options.AddPolicy(AuthorizationPolicyNames.ApiKeyRead, policy =>
            {
                policy.AddAuthenticationSchemes(
                    JwtBearerDefaults.AuthenticationScheme,
                    CameraAgentAuthenticationSchemes.InteractiveCookie);
                policy.RequireAuthenticatedUser();
            });

            options.AddPolicy(AuthorizationPolicyNames.ApiKeyReadWrite, policy =>
            {
                policy.AddAuthenticationSchemes(
                    JwtBearerDefaults.AuthenticationScheme,
                    CameraAgentAuthenticationSchemes.InteractiveCookie);
                policy.RequireAuthenticatedUser();
            });
        });

        builder.Services.AddScoped<ISampleStatusService, SampleStatusService>();
        builder.Services.AddSingleton<CaptureTelemetryDashboardService>();

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
        app.MapPrometheusScrapingEndpoint();

        app.MapSkyMonitorHealthEndpoints();

        app.Run();
    }

    private static string BuildOidcEndpoint(Uri authority, string relativePath)
    {
        var normalizedPath = relativePath.StartsWith("/", StringComparison.Ordinal)
            ? relativePath
            : string.Concat("/", relativePath);
        return new Uri(authority, normalizedPath).ToString();
    }
}
