using System.Diagnostics;
using Asp.Versioning;
using HVO.SkyMonitor.Common.Infrastructure.Diagnostics;
using HVO.SkyMonitor.Common.Infrastructure.Filters;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Components;
using HVO.SkyMonitor.Components.Account;
using HVO.SkyMonitor.Data;
using HVO.SkyMonitor.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using Scalar.AspNetCore;
using OpenIddict.Validation.AspNetCore;

namespace HVO.SkyMonitor;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Enhanced logging with activity tracking
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

        // Add service defaults & Aspire client integrations (includes OpenTelemetry)
        builder.AddServiceDefaults();

        // Correlation ID support
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<ICorrelationIdAccessor, HttpContextCorrelationIdAccessor>();

        // Problem Details with correlation tracking
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

        // Global exception handler with HTML + API awareness
        builder.Services.AddExceptionHandler<HvoServiceExceptionHandler>();

        // HTTP logging
        builder.Services.AddHttpLogging(logging =>
        {
            logging.LoggingFields = HttpLoggingFields.RequestMethod |
                                    HttpLoggingFields.RequestPath |
                                    HttpLoggingFields.ResponseStatusCode |
                                    HttpLoggingFields.Duration;
            logging.RequestHeaders.Add(CorrelationIdMiddleware.HeaderName);
            logging.ResponseHeaders.Add(CorrelationIdMiddleware.HeaderName);
        });

        // Time provider for testability
        builder.Services.AddSingleton(TimeProvider.System);

        // API Controllers with automatic model state validation
        builder.Services.AddControllers(options =>
        {
            options.Filters.Add<ValidateModelStateAttribute>();
        })
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = null;
        });

        // Health checks
        builder.Services.AddHealthChecks()
            .AddDbContextCheck<ApplicationDbContext>("database");
        builder.Services.Configure<ApiBehaviorOptions>(options =>
        {
            options.SuppressModelStateInvalidFilter = true;
        });

        // OpenAPI and Scalar
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi();

        // API Versioning
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

        // Prometheus metrics endpoint
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddPrometheusExporter();
            });

        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            options.RecordException = true;
        });

        // Add services to the container
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Database with SQLite (temporary for Phase 0-7, will switch to PostgreSQL before Phase 8)
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "DataSource=Data/skymonitor.db;Cache=Shared";
        builder.Services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite(connectionString));

        builder.Services.AddDatabaseDeveloperPageExceptionFilter();

        // Identity
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<IdentityUserAccessor>();
        builder.Services.AddScoped<IdentityRedirectManager>();
        builder.Services.AddScoped<IdentityRevalidatingAuthenticationStateProvider>();
        builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

        builder.Services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.SignIn.RequireConfirmedAccount = true;
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddSignInManager()
        .AddDefaultTokenProviders();

        builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

        // OpenIddict Configuration (Phase 2)
        builder.Services.AddOpenIddict()
            // Register the OpenIddict core components
            .AddCore(options =>
            {
                // Configure OpenIddict to use the Entity Framework Core stores and models
                options.UseEntityFrameworkCore()
                    .UseDbContext<ApplicationDbContext>();
            })
            // Register the OpenIddict server components
            .AddServer(options =>
            {
                // Enable the authorization and token endpoints
                options.SetAuthorizationEndpointUris("/connect/authorize")
                       .SetTokenEndpointUris("/connect/token");

                // Enable the authorization code flow with PKCE
                options.AllowAuthorizationCodeFlow()
                       .RequireProofKeyForCodeExchange();

                // Enable the client credentials flow
                options.AllowClientCredentialsFlow();

                // Enable the refresh token flow
                options.AllowRefreshTokenFlow();

                // Register the signing and encryption credentials
                if (builder.Environment.IsDevelopment())
                {
                    options.AddDevelopmentEncryptionCertificate()
                           .AddDevelopmentSigningCertificate();
                }
                else
                {
                    // In production, use proper certificates from Key Vault or certificate store
                    // options.AddEncryptionCertificate(encryptionCert)
                    //        .AddSigningCertificate(signingCert);
                }

                // Register the ASP.NET Core host and configure the ASP.NET Core-specific options
                options.UseAspNetCore()
                       .EnableAuthorizationEndpointPassthrough()
                       .EnableTokenEndpointPassthrough()
                       .EnableStatusCodePagesIntegration();

                // Configure token lifetimes
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(30))
                       .SetRefreshTokenLifetime(TimeSpan.FromDays(14))
                       .SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(5));
            })
            // Register the OpenIddict validation components
            .AddValidation(options =>
            {
                // Import the configuration from the local OpenIddict server instance
                options.UseLocalServer();

                // Register the ASP.NET Core host
                options.UseAspNetCore();
            });

        // Data Protection - persist keys to avoid cookie invalidation on restart
        var dataProtectionPath = Path.Combine(builder.Environment.ContentRootPath, "DataProtection-Keys");
        Directory.CreateDirectory(dataProtectionPath);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
            .SetApplicationName("HVO.SkyMonitor");

        // Authentication
        var authenticationBuilder = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = IdentityConstants.ApplicationScheme;
            options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        });

        authenticationBuilder.AddIdentityCookies();
        authenticationBuilder.AddApiKeySupport();
        authenticationBuilder.AddPolicyScheme("Bearer", "Bearer", options =>
        {
            options.ForwardDefaultSelector = _ => OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
        });

        // Authorization policies
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthorizationPolicyNames.ApiKeyOrCookie, policy =>
            {
                policy.AddAuthenticationSchemes(
                    IdentityConstants.ApplicationScheme,
                    ApiKeyAuthenticationOptions.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });

            options.AddPolicy(AuthorizationPolicyNames.ApiKeyRead, policy =>
            {
                policy.AddAuthenticationSchemes(
                    IdentityConstants.ApplicationScheme,
                    ApiKeyAuthenticationOptions.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    var authScheme = context.User.FindFirst(ApiKeyClaims.AuthenticationType);
                    if (authScheme is null)
                    {
                        return true; // Cookie auth - allow
                    }
                    var accessLevel = context.User.FindFirst(ApiKeyClaims.AccessLevel)?.Value;
                    return accessLevel is not null &&
                        (accessLevel == ApiKeyAccessLevel.Read.ToString() ||
                         accessLevel == ApiKeyAccessLevel.ReadWrite.ToString());
                });
            });

            options.AddPolicy(AuthorizationPolicyNames.ApiKeyReadWrite, policy =>
            {
                policy.AddAuthenticationSchemes(
                    IdentityConstants.ApplicationScheme,
                    ApiKeyAuthenticationOptions.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    var authScheme = context.User.FindFirst(ApiKeyClaims.AuthenticationType);
                    if (authScheme is null)
                    {
                        return true; // Cookie auth - allow
                    }
                    var accessLevel = context.User.FindFirst(ApiKeyClaims.AccessLevel)?.Value;
                    return accessLevel == ApiKeyAccessLevel.ReadWrite.ToString();
                });
            });

            // Phase 3: Account type-based policies
            options.AddPolicy("RequireSystemAccount", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    var accountType = context.User.FindFirst("account_type")?.Value;
                    return accountType == "System";
                });
            });

            options.AddPolicy("RequireUserAccount", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    var accountType = context.User.FindFirst("account_type")?.Value;
                    return accountType == "User" || accountType == null; // null for backward compatibility
                });
            });
        });

        // Application services
        builder.Services.AddSingleton<IApiKeyHasher, ApiKeyHasher>();
        builder.Services.AddScoped<IApiKeyValidator, DatabaseApiKeyValidator>();
        builder.Services.AddScoped<IApiKeyAuditLogger, ApiKeyAuditLogger>();

        var app = builder.Build();

        // Configure the HTTP request pipeline

        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseHsts();
        }

        app.UseCorrelationId();

        // Status code pages for non-API routes only (browsers get HTML, APIs get ProblemDetails)
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), appBuilder =>
        {
            appBuilder.UseStatusCodePagesWithReExecute("/Error", "?statusCode={0}");
        });

        app.UseExceptionHandler();
        app.UseHttpLogging();

        if (app.Environment.IsDevelopment())
        {
            app.UseMigrationsEndPoint();
        }

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

                // Seed initial data
                logger.LogInformation("Seeding database...");
                await DatabaseSeeder.SeedAsync(scope.ServiceProvider, logger);
                logger.LogInformation("Database seeding completed");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred while applying database migrations or seeding");
                throw;
            }
        }

        app.MapStaticAssets();
        app.UseRouting();

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        // OpenAPI and Scalar
        app.MapOpenApi();
        app.MapScalarApiReference(options =>
        {
            options.Title = "HVO SkyMonitor API";
        });

        // API Controllers
        app.MapControllers();

        // Health checks
        app.MapHealthChecks("/health");

        // Blazor
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.MapAdditionalIdentityEndpoints();

        // Prometheus metrics
        app.MapPrometheusScrapingEndpoint();

        // Default health/diagnostics endpoints
        app.MapDefaultEndpoints();

        await app.RunAsync();
    }
}
