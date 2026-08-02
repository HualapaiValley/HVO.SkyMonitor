using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Threading.RateLimiting;
using Asp.Versioning;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Common.Infrastructure.Diagnostics;
using HVO.SkyMonitor.Common.Infrastructure.Filters;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.LogicHost.Components;
using HVO.SkyMonitor.LogicHost.Components.Account;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Common.Observability;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Middleware;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using Scalar.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using OpenIddict.Server.AspNetCore;
using Minio;
using Microsoft.Extensions.Options;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace HVO.SkyMonitor.LogicHost;

public sealed partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Configure shared HVO telemetry, logging, and health defaults.
        builder.AddSkyMonitorObservability();

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

        // Common configuration values reused across services
        var redisConfiguration = builder.Configuration.GetValue<string>("Redis:Configuration");
        var minioEndpoint = builder.Configuration.GetValue<string>("Minio:Endpoint");
        var smtpHost = builder.Configuration.GetValue<string>("Smtp:Host");

        var centralIdentitySection = builder.Configuration.GetSection("CentralIdentity");
        builder.Services.AddOptions<CentralIdentityOptions>()
            .Bind(centralIdentitySection);

        builder.Services.AddOptions<DeviceBootstrapSecretsOptions>()
            .Bind(builder.Configuration.GetSection("DeviceBootstrap"))
            .Validate(HasUsableDeviceBootstrapCredentials,
                "DeviceBootstrap:CentralIdentity must contain a service URL and scoped client credentials.")
            .ValidateOnStart();
        builder.Services.AddOptions<CentralDerivativeWorkerOptions>()
            .Bind(builder.Configuration.GetSection(CentralDerivativeWorkerOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.WorkerId) && options.WorkerId.Length <= 256,
                "CentralDerivativeWorker:WorkerId is required and must not exceed 256 characters.")
            .Validate(options => options.Concurrency is >= 1 and <= 32,
                "CentralDerivativeWorker:Concurrency must be between 1 and 32.")
            .Validate(options => options.PollInterval > TimeSpan.Zero
                    && options.QueueSampleInterval > TimeSpan.Zero
                    && options.LeaseDuration >= TimeSpan.FromSeconds(1)
                    && options.LeaseDuration <= TimeSpan.FromHours(1)
                    && options.RenewalInterval > TimeSpan.Zero
                    && options.RenewalInterval < options.LeaseDuration
                    && options.ShutdownTimeout > TimeSpan.Zero
                    && options.BacklogDegradedAfter > TimeSpan.Zero,
                "CentralDerivativeWorker timing values are invalid.")
            .ValidateOnStart();
        builder.Services.AddOptions<CentralTransientOptions>()
            .Bind(builder.Configuration.GetSection(CentralTransientOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddOptions<CentralTransientPayloadReleaseOptions>()
            .Bind(builder.Configuration.GetSection(CentralTransientPayloadReleaseOptions.SectionName))
            .Validate(options => options.PollInterval > TimeSpan.Zero,
                "TransientPayloadRelease:PollInterval must be positive.")
            .ValidateOnStart();
        builder.Services.AddOptions<CentralTransientNotificationOptions>()
            .Bind(builder.Configuration.GetSection(CentralTransientNotificationOptions.SectionName))
            .Validate(options => options.PollInterval > TimeSpan.Zero &&
                    options.FenceTimeout >= TimeSpan.FromSeconds(30),
                "CentralTransientNotification timing values are invalid.")
            .ValidateOnStart();
        builder.Services.Configure<HostOptions>(options =>
            options.ShutdownTimeout = builder.Configuration.GetValue(
                $"{CentralDerivativeWorkerOptions.SectionName}:ShutdownTimeout",
                TimeSpan.FromSeconds(30)));

        builder.Services.Configure<DatabaseSeedOptions>(
            builder.Configuration.GetSection(DatabaseSeedOptions.SectionName));

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
        .ConfigureApplicationPartManager(manager =>
        {
            manager.FeatureProviders.Add(new Controllers.InternalControllerFeatureProvider());
        })
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = null;
        });

        // Health checks
        builder.Services.AddSingleton(new CentralRecoveryStartupState(TimeProvider.System));
        var healthChecks = builder.Services.AddSkyMonitorHealthChecks()
            .AddDbContextCheck<ApplicationDbContext>("database", tags: ["dependency"])
            .AddCheck<CentralArtifactConsistencyHealthCheck>("artifact-consistency", tags: ["consistency"])
            .AddCheck<CentralArtifactRetentionHealthCheck>("artifact-retention", tags: ["worker"])
            .AddCheck<CentralDerivativeWorkerHealthCheck>("central-derivative-worker", tags: ["worker"])
            .AddCheck<CentralTransientLifecycleHealthCheck>("central-transient-lifecycle", tags: ["worker"])
            .AddCheck<FleetStatusHealthCheck>("fleet-status", tags: ["worker"])
            .AddCheck<EnvironmentalObservationHealthCheck>("environmental-observations", tags: ["worker"])
            .AddCheck<DeploymentLocationHealthCheck>("deployment-location", tags: ["consistency"]);
        healthChecks.AddInstalledCelestialCatalogHealthCheck();

        if (!string.IsNullOrWhiteSpace(redisConfiguration))
        {
            healthChecks.AddCheck<RedisHealthCheck>("redis", tags: ["dependency"]);
        }

        if (!string.IsNullOrWhiteSpace(minioEndpoint))
        {
            healthChecks.AddCheck<MinioHealthCheck>("minio", tags: ["dependency"]);
        }

        if (!string.IsNullOrWhiteSpace(smtpHost))
        {
            healthChecks.AddCheck<SmtpHealthCheck>("smtp", tags: ["dependency"]);
        }

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
                metrics.AddMeter("HVO.SkyMonitor.Authentication");
                metrics.AddMeter(CentralIngestTelemetry.MeterName);
                metrics.AddMeter(CentralArtifactRetrievalTelemetry.MeterName);
                metrics.AddMeter(CentralArtifactRetentionTelemetry.MeterName);
                metrics.AddMeter(CentralDerivativeWorkerTelemetry.MeterName);
                metrics.AddMeter(CentralTransientLifecycleTelemetry.MeterName);
                metrics.AddMeter(FleetStatusTelemetry.MeterName);
                metrics.AddMeter(EnvironmentalObservationTelemetry.MeterName);
                metrics.AddMeter(DeploymentLocationTelemetry.MeterName);
                metrics.AddMeter(OperatorUiTelemetry.MeterName);
                metrics.AddAspNetCoreInstrumentation();
            })
            .WithTracing(tracing => tracing
                .AddSource(CentralIngestTelemetry.ActivitySourceName)
                .AddSource(CentralTransientLifecycleTelemetry.ActivitySourceName)
                .AddSource(DeploymentLocationTelemetry.ActivitySourceName)
                .AddSource(OperatorUiTelemetry.ActivitySourceName));

        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            options.RecordException = true;
        });

        // Identity Hardening: Custom metrics for authentication
        builder.Services.AddSingleton<Meter>(sp => new Meter("HVO.SkyMonitor.Authentication", "1.0.0"));
        builder.Services.AddSingleton<AuthenticationMetrics>();

        // Identity Hardening: Rate Limiting
        builder.Services.AddRateLimiter(options =>
        {
            // Default policy for general requests
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var endpoint = context.GetEndpoint();

                // Check if endpoint has custom rate limit policy
                var policyName = endpoint?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;

                if (policyName != null)
                {
                    return RateLimitPartition.GetNoLimiter<string>("bypass");
                }

                // Global rate limit: 10,000 requests per minute per IP
                var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(ipAddress, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = builder.Configuration.GetValue<int>("RateLimiting:Global:PermitsPerMinute", 10000),
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                });
            });

            // Token endpoint rate limit: 60 requests per minute per IP
            options.AddFixedWindowLimiter("token", options =>
            {
                options.PermitLimit = builder.Configuration.GetValue<int>("RateLimiting:TokenEndpoint:PermitsPerMinute", 60);
                options.Window = TimeSpan.FromMinutes(1);
                options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                options.QueueLimit = builder.Configuration.GetValue<int>("RateLimiting:TokenEndpoint:QueueLimit", 10);
            });

            // API endpoint rate limit: 1000 requests per minute per user
            options.AddFixedWindowLimiter("api", options =>
            {
                options.PermitLimit = builder.Configuration.GetValue<int>("RateLimiting:ApiEndpoint:PermitsPerMinute", 1000);
                options.Window = TimeSpan.FromMinutes(1);
                options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                options.QueueLimit = 10;
            });

            options.OnRejected = async (context, cancellationToken) =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                var ipAddress = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var path = context.HttpContext.Request.Path.Value ?? string.Empty;
                double? retryAfterSeconds = null;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterSpan))
                {
                    retryAfterSeconds = retryAfterSpan.TotalSeconds;
                }

                Log.RateLimitExceeded(logger, ipAddress, path, retryAfterSeconds);

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                if (retryAfterSeconds.HasValue)
                {
                    context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
                }

                object? retryAfterValue = null;
                if (retryAfterSeconds.HasValue)
                {
                    retryAfterValue = retryAfterSeconds;
                }

                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    error = "too_many_requests",
                    message = "Rate limit exceeded. Please try again later.",
                    retryAfter = retryAfterValue
                }, cancellationToken).ConfigureAwait(false);
            };
        });

        // Add services to the container
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddOptions<MinioOptions>()
            .Bind(builder.Configuration.GetSection("Minio"))
            .ValidateOnStart();

        builder.Services.AddOptions<SmtpOptions>()
            .Bind(builder.Configuration.GetSection("Smtp"))
            .ValidateOnStart();

        builder.Services.AddOptions<RedisOptions>()
            .Bind(builder.Configuration.GetSection("Redis"))
            .ValidateOnStart();
        builder.Services.AddOptions<FleetStatusOptions>()
            .Bind(builder.Configuration.GetSection("FleetStatus"))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddOptions<EnvironmentalObservationOptions>()
            .Bind(builder.Configuration.GetSection("EnvironmentalObservations"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (string.IsNullOrWhiteSpace(redisConfiguration))
        {
            builder.Services.AddDistributedMemoryCache();
        }
        else
        {
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConfiguration;
                options.InstanceName = builder.Configuration.GetValue<string>("Redis:InstanceName") ?? "skymonitor:";
            });
        }

        if (!string.IsNullOrWhiteSpace(minioEndpoint))
        {
            builder.Services.AddSingleton<IMinioClient>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<MinioOptions>>().Value;
                var client = new MinioClient()
                    .WithEndpoint(options.Endpoint, options.Port)
                    .WithCredentials(options.AccessKey, options.SecretKey);

                if (options.UseSsl)
                {
                    client = client.WithSSL();
                }

                if (!string.IsNullOrWhiteSpace(options.Region))
                {
                    client = client.WithRegion(options.Region);
                }

                return client.Build();
            });

            builder.Services.AddHttpClient<MinioHealthCheck>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(5);
            });
        }

        builder.Services.AddSingleton<IEmailNotificationService, SmtpEmailNotificationService>();
        builder.Services.AddSingleton<DeploymentLocationTelemetry>();
        builder.Services.AddScoped<IObservatoryService, ObservatoryService>();
        builder.Services.AddScoped<IObservatoryMembershipService, ObservatoryMembershipService>();
        builder.Services.AddScoped<IAccountDeletionService, AccountDeletionService>();
        builder.Services.AddScoped<IObservatoryPublicationService, ObservatoryPublicationService>();
        builder.Services.AddScoped<IObservatoryInvitationService, ObservatoryInvitationService>();
        builder.Services.AddScoped<ILogicalCameraService, LogicalCameraService>();
        builder.Services.AddScoped<IPublicRecordPublicationService, PublicRecordPublicationService>();
        builder.Services.AddScoped<IPublicNetworkReadService, PublicNetworkReadService>();
        builder.Services.AddScoped<INetworkOperationsReadService, NetworkOperationsReadService>();
        builder.Services.AddScoped<ICuratedPublicPlacementService, CuratedPublicPlacementService>();
        builder.Services.AddScoped<IRegisteredUserPersonalizationService, RegisteredUserPersonalizationService>();
        builder.Services.AddScoped<INetworkOperationsMutationService, NetworkOperationsMutationService>();
        builder.Services.AddScoped<ICentralProcessingPolicyService, CentralProcessingPolicyService>();
        builder.Services.AddSingleton<OperatorUiTelemetry>();
        builder.Services.AddScoped<IDeploymentLocationAuthorityService, DeploymentLocationAuthorityService>();
        builder.Services.AddOptions<DeploymentLocationReconciliationOptions>()
            .Bind(builder.Configuration.GetSection(DeploymentLocationReconciliationOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.PollInterval > TimeSpan.Zero
                && options.LeaseDuration > TimeSpan.Zero
                && options.InitialRetryDelay > TimeSpan.Zero
                && options.MaximumRetryDelay >= options.InitialRetryDelay
                && options.BacklogDegradedAfter > TimeSpan.Zero,
                "Deployment location reconciliation durations must be positive and ordered.")
            .ValidateOnStart();
        builder.Services.AddScoped<DeploymentLocationReconciliationService>();
        builder.Services.AddScoped<IDeploymentLocationReconciliationProcessor>(provider =>
            provider.GetRequiredService<DeploymentLocationReconciliationService>());
        if (!builder.Environment.IsEnvironment("Testing"))
        {
            builder.Services.AddHostedService<DeploymentLocationReconciliationWorker>();
        }

        // This host owns only the SkyMonitor database; do not point this context at shared identity databases.
        // Database - require an explicit SQL Server connection string.
        var connectionString = builder.Configuration.GetConnectionString("skymonitordb")
            ?? builder.Configuration.GetConnectionString("DefaultConnection")
            ?? builder.Configuration["ConnectionStrings:skymonitordb"]
            ?? builder.Configuration["ConnectionStrings:DefaultConnection"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("A SkyMonitor SQL Server connection string must be configured.");
        }

        builder.Services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(connectionString));

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
        .AddRoles<IdentityRole>()
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddSignInManager()
        .AddDefaultTokenProviders();

        builder.Services.AddSingleton<IEmailSender<ApplicationUser>, SmtpIdentityEmailSender>();

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

                // Enable the resource owner password flow (for integration testing scenarios)
                options.AllowPasswordFlow();

                // Enable the refresh token flow
                options.AllowRefreshTokenFlow();

                options.RegisterScopes(
                    Scopes.Email,
                    Scopes.Profile,
                    Scopes.OpenId,
                    Scopes.OfflineAccess,
                    "api.admin",
                    "api.artifacts.read",
                    "api.camera",
                    "api.frames",
                    "api.images",
                    "api.owner.write",
                    "api.viewer",
                    "api.webhooks");

                // Register the signing and encryption credentials
                if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
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

                // Downstream services validate tokens via standard JwtBearer handlers, so emit
                // signed (non-encrypted) access tokens until we support shared decryption keys.
                options.DisableAccessTokenEncryption();

                // Register the ASP.NET Core host and configure the ASP.NET Core-specific options
                var aspNetCoreBuilder = options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableStatusCodePagesIntegration();

                if (!builder.Environment.IsProduction())
                {
                    // Allow HTTP endpoints in development and integration testing
                    aspNetCoreBuilder.DisableTransportSecurityRequirement();
                }

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
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                dataProtectionPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
                    ApiKeyAuthenticationOptions.AuthenticationScheme,
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });

            options.AddPolicy(AuthorizationPolicyNames.ApiKeyRead, policy =>
            {
                policy.AddAuthenticationSchemes(
                    IdentityConstants.ApplicationScheme,
                    ApiKeyAuthenticationOptions.AuthenticationScheme,
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
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
                    ApiKeyAuthenticationOptions.AuthenticationScheme,
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
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

            options.AddPolicy(AuthorizationPolicyNames.PlatformEditorialWrite, policy =>
            {
                policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireRole(AuthorizationRoleNames.PlatformEditor);
            });

            options.AddPolicy("OwnerLocationWrite", policy =>
            {
                policy.AddAuthenticationSchemes(
                    IdentityConstants.ApplicationScheme,
                    ApiKeyAuthenticationOptions.AuthenticationScheme,
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    var identity = CentralArtifactCredentialAccess.GetSingleCredentialIdentity(context.User);
                    if (identity is null || CentralArtifactCredentialAccess.IsSystem(context.User))
                    {
                        return false;
                    }
                    if (identity.FindFirst(ApiKeyClaims.AuthenticationType) is not null)
                    {
                        return identity.FindFirst(ApiKeyClaims.AccessLevel)?.Value == nameof(ApiKeyAccessLevel.ReadWrite);
                    }
                    return !identity.Claims.Any(claim => claim.Type == "scope")
                        || CentralArtifactCredentialAccess.HasScope(context.User, "api.owner.write")
                        || CentralArtifactCredentialAccess.HasScope(context.User, "api.admin");
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

            options.AddPolicy("DerivativeJobsRead", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context => context.User.Claims
                    .Where(claim => claim.Type == "scope")
                    .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Contains("api.admin", StringComparer.Ordinal));
            });
            options.AddPolicy("ArtifactIngest", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                    string.Equals(context.User.FindFirst("account_type")?.Value, "System", StringComparison.Ordinal)
                    && context.User.Claims
                        .Where(claim => claim.Type == "scope")
                        .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        .Contains("api.frames", StringComparer.Ordinal));
            });
            options.AddPolicy("ArtifactRetrieval", policy =>
            {
                policy.AddAuthenticationSchemes(
                    IdentityConstants.ApplicationScheme,
                    ApiKeyAuthenticationOptions.AuthenticationScheme,
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });
            options.AddPolicy("TransientEventsRead", policy =>
            {
                policy.AddAuthenticationSchemes(
                    IdentityConstants.ApplicationScheme,
                    ApiKeyAuthenticationOptions.AuthenticationScheme,
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                    CentralArtifactCredentialAccess.HasSingleCredentialIdentity(context.User) &&
                    (CentralArtifactCredentialAccess.HasOwnerCredential(context.User) ||
                     CentralArtifactCredentialAccess.HasScope(context.User, "api.admin")));
            });
            options.AddPolicy("TransientReview", policy =>
            {
                policy.AddAuthenticationSchemes(
                    IdentityConstants.ApplicationScheme,
                    ApiKeyAuthenticationOptions.AuthenticationScheme,
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    var identity = CentralArtifactCredentialAccess.GetSingleCredentialIdentity(context.User);
                    if (identity is null ||
                        string.Equals(identity.FindFirst("account_type")?.Value, "System", StringComparison.Ordinal))
                    {
                        return false;
                    }
                    var apiKeyAccess = identity.FindFirst(ApiKeyClaims.AccessLevel)?.Value;
                    return apiKeyAccess is null || apiKeyAccess == nameof(ApiKeyAccessLevel.ReadWrite);
                });
            });
            options.AddPolicy("TransientAdmin", policy =>
            {
                policy.AddAuthenticationSchemes(
                    IdentityConstants.ApplicationScheme,
                    ApiKeyAuthenticationOptions.AuthenticationScheme,
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                    CentralArtifactCredentialAccess.HasSingleCredentialIdentity(context.User) &&
                    CentralArtifactCredentialAccess.HasScope(context.User, "api.admin"));
            });
        });

        // Application services
        builder.Services.AddSingleton<IApiKeyHasher, ApiKeyHasher>();
        builder.Services.AddScoped<IApiKeyValidator, DatabaseApiKeyValidator>();
        builder.Services.AddScoped<IApiKeyAuditLogger, ApiKeyAuditLogger>();
        builder.Services.AddScoped<IApiKeyLifecycleService, ApiKeyLifecycleService>();
        builder.Services.AddScoped<IAuthenticationEventLogger, AuthenticationEventLogger>();
        builder.Services.AddScoped<IDeviceRegistrationService, DeviceRegistrationService>();
        builder.Services.AddScoped<IDeviceRegistrationEnvelopeService, DeviceRegistrationEnvelopeService>();
        builder.Services.AddScoped<IDeviceBootstrapService, DeviceBootstrapService>();
        builder.Services.AddScoped<IDeviceRegistrationReadService, DeviceRegistrationReadService>();
        builder.Services.AddScoped<IDeviceCredentialValidator, DeviceCredentialValidator>();
        builder.Services.AddScoped<IDeviceHeartbeatService, DeviceHeartbeatService>();
        builder.Services.AddSingleton<FleetStatusClassifier>();
        builder.Services.AddSingleton<FleetStatusTelemetry>();
        builder.Services.AddSingleton<FleetRetentionState>();
        builder.Services.AddHostedService<FleetStatusRetentionWorker>();
        builder.Services.AddScoped<IEnvironmentalObservationIngestService, EnvironmentalObservationIngestService>();
        builder.Services.AddScoped<IEnvironmentalObservationQueryService, EnvironmentalObservationQueryService>();
        builder.Services.AddSingleton<EnvironmentalObservationTelemetry>();
        builder.Services.AddSingleton<EnvironmentalRetentionState>();
        builder.Services.AddHostedService<EnvironmentalObservationRetentionWorker>();
        builder.Services.AddScoped<IDeviceUploadService, DeviceUploadService>();
        builder.Services.AddScoped<IArtifactIngestService, ArtifactIngestService>();
        builder.Services.AddSingleton<CentralIngestTelemetry>();
        builder.Services.AddScoped<ICentralArtifactRetrievalService, CentralArtifactRetrievalService>();
        builder.Services.AddScoped<ICentralArtifactObjectReader, CentralArtifactObjectReader>();
        builder.Services.AddScoped<ICentralArtifactRetentionReferences, CentralArtifactRetentionReferences>();
        builder.Services.AddScoped<ICentralTransientEventPersistence, CentralTransientEventPersistence>();
        builder.Services.AddScoped<ICentralTransientEventVersionAppender, CentralTransientEventVersionAppender>();
        builder.Services.AddScoped<ICentralTransientDerivativeScheduler, CentralTransientDerivativeScheduler>();
        builder.Services.AddScoped<ICentralTransientEventReadService, CentralTransientEventReadService>();
        builder.Services.AddScoped<ICentralTransientDerivativeRetrievalService, CentralTransientDerivativeRetrievalService>();
        builder.Services.AddScoped<ICentralTransientReviewService, CentralTransientReviewService>();
        builder.Services.AddScoped<ICentralTransientNotificationProcessor, CentralTransientNotificationProcessor>();
        builder.Services.AddScoped<ICentralTransientNotificationRetryService, CentralTransientNotificationRetryService>();
        builder.Services.AddScoped<ICentralTransientReprocessingService, CentralTransientReprocessingService>();
        builder.Services.AddScoped<ICentralTransientReprocessingExecutor, CentralTransientReprocessingExecutor>();
        builder.Services.AddScoped<ICentralTransientPayloadReleaseService, CentralTransientPayloadReleaseService>();
        builder.Services.AddScoped<ICentralTransientPayloadReleaseProcessor, CentralTransientPayloadReleaseService>();
        builder.Services.AddHostedService<CentralTransientNotificationWorker>();
        builder.Services.AddHostedService<CentralTransientPayloadReleaseWorker>();
        builder.Services.AddScoped<ICentralTransientSubmissionService, CentralTransientSubmissionService>();
        builder.Services.AddScoped<ICentralTransientMaskFactory, CentralTransientMaskFactory>();
        builder.Services.AddScoped<ICentralTransientDerivativeBundleFactory, CentralTransientDerivativeBundleFactory>();
        builder.Services.AddScoped<ICentralTransientDerivativeOutputWriter, CentralTransientDerivativeOutputWriter>();
        builder.Services.AddScoped<ICentralTransientDerivativeExecutor, CentralTransientDerivativeExecutor>();
        builder.Services.AddScoped<ICentralTransientValidationExecutor, CentralTransientValidationExecutor>();
        builder.Services.AddScoped<ICentralTransientRetrospectiveScheduler, CentralTransientRetrospectiveScheduler>();
        builder.Services.AddScoped<ICentralArtifactRetentionService, CentralArtifactRetentionService>();
        builder.Services.AddScoped<CentralArtifactRetentionProcessor>();
        builder.Services.AddScoped<ICentralArtifactRetentionProcessor>(provider =>
            provider.GetRequiredService<CentralArtifactRetentionProcessor>());
        builder.Services.Configure<CentralArtifactRetentionOptions>(_ => { });
        builder.Services.AddSingleton<CentralArtifactRetentionTelemetry>();
        if (!builder.Environment.IsEnvironment("Testing"))
        {
            builder.Services.AddHostedService<CentralArtifactRetentionWorker>();
        }
        builder.Services.AddSingleton<CentralArtifactRetrievalTelemetry>();
        builder.Services.AddSingleton<CentralTransientLifecycleTelemetry>();
        builder.Services.AddHostedService<CentralArtifactReconciliationService>();
        builder.Services.AddSingleton<ICentralDerivativeRecipeCatalog, CentralDerivativeRecipeCatalog>();
        builder.Services.AddScoped<ICentralClearReferenceService, CentralClearReferenceService>();
        builder.Services.AddSingleton<IProcessingRecipeExecutor, ProcessingRecipeExecutor>();
        builder.Services.AddSingleton<LogicHostRecipeExecutionAdapter>();
        builder.Services.AddScoped<ICentralDerivativeJobScheduler, CentralDerivativeJobScheduler>();
        builder.Services.AddScoped<ICentralDerivativeWindowResolver, CentralDerivativeWindowResolver>();
        builder.Services.AddScoped<ICentralDerivativeJobService, CentralDerivativeJobService>();
        builder.Services.AddScoped<ICentralDerivativeJobInputReader, CentralDerivativeJobInputReader>();
        builder.Services.AddScoped<ICentralDerivativeOutputWriter, CentralDerivativeOutputWriter>();
        builder.Services.AddScoped<ICentralDerivativeJobExecutor, CentralDerivativeJobExecutor>();
        builder.Services.AddScoped<ICentralDerivativeJobOperationsService, CentralDerivativeJobOperationsService>();
        builder.Services.AddSingleton<CentralDerivativeWorkerTelemetry>();
        builder.Services.AddHostedService<CentralDerivativeWorker>();
        builder.Services.AddScoped<IDeviceRigProfileService, DeviceRigProfileService>();
        builder.Services.AddInstalledCelestialCatalog();

        var app = builder.Build();
        _ = app.Services.GetRequiredService<CatalogSnapshotResult>();

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
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase), appBuilder =>
        {
            appBuilder.UseStatusCodePages(async statusCodeContext =>
            {
                var httpContext = statusCodeContext.HttpContext;
                var response = httpContext.Response;

                if (response.HasStarted)
                {
                    return;
                }

                var statusCode = response.StatusCode;

                if (statusCode == StatusCodes.Status401Unauthorized)
                {
                    var returnUrl = Uri.EscapeDataString(UriHelper.GetEncodedPathAndQuery(httpContext.Request));
                    var loginPath = $"/Account/Login?returnUrl={returnUrl}";

                    if (!string.Equals(httpContext.Request.Path.Value, "/Account/Login", StringComparison.OrdinalIgnoreCase))
                    {
                        response.Redirect(loginPath);
                    }

                    return;
                }

                var targetPath = statusCode == StatusCodes.Status404NotFound ? "/not-found" : "/Error";
                var targetQuery = statusCode == StatusCodes.Status404NotFound
                    ? string.Empty
                    : $"?statusCode={statusCode}";

                var currentPath = httpContext.Request.Path.Value ?? string.Empty;
                var currentQuery = httpContext.Request.QueryString.HasValue
                    ? httpContext.Request.QueryString.Value!
                    : string.Empty;

                if (string.Equals(currentPath, targetPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(currentQuery, targetQuery, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                response.Redirect(string.Concat(targetPath, targetQuery));
            });
        });

        app.UseExceptionHandler();
        app.UseHttpLogging();
        app.UseStaticFiles();

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
                Log.ApplyingMigrations(logger);
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Database.MigrateAsync().ConfigureAwait(false);
                Log.MigrationsApplied(logger);

                // Seed initial data
                Log.SeedingDatabase(logger);
                await DatabaseSeeder.SeedAsync(scope.ServiceProvider, logger).ConfigureAwait(false);
                Log.SeedingCompleted(logger);
                var backfilledObservatories = await ObservatoryLocationBackfill.RunAsync(
                    db,
                    scope.ServiceProvider.GetRequiredService<TimeProvider>(),
                    logger).ConfigureAwait(false);
                scope.ServiceProvider.GetRequiredService<DeploymentLocationTelemetry>()
                    .RecordBackfill(backfilledObservatories);
            }
            catch (Exception ex)
            {
                Log.MigrationError(logger, ex);
                throw;
            }
        }

        app.MapStaticAssets();
        app.UseRouting();

        // Identity Hardening: Rate limiting
        app.UseRateLimiter();

        app.UseAuthentication();
        app.UseMiddleware<DynamicPageCachePolicyMiddleware>();
        app.UseAuthorization();
        app.UseMiddleware<OperatorUiResponseMetricsMiddleware>();
        app.UseAntiforgery();

        // OpenAPI and Scalar
        app.MapOpenApi();
        app.MapScalarApiReference(options =>
        {
            options.Title = "HVO SkyMonitor API";
        });

        // API Controllers
        app.MapControllers();

        // Blazor
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.MapAdditionalIdentityEndpoints();

        // Prometheus metrics
        app.MapPrometheusScrapingEndpoint();

        // Default health/diagnostics endpoints
        app.MapSkyMonitorHealthEndpoints();

        await app.RunAsync().ConfigureAwait(false);
    }

    private static bool HasUsableDeviceBootstrapCredentials(DeviceBootstrapSecretsOptions options)
    {
        var identity = options.CentralIdentity;
        var credentials = identity?.ClientCredentials;
        return identity is
        {
            Mode: AuthenticationMode.ClientCredentials,
            ServiceUrl.IsAbsoluteUri: true
        } &&
        credentials is not null &&
        !string.IsNullOrWhiteSpace(credentials.ClientId) &&
        !string.IsNullOrWhiteSpace(credentials.ClientSecret) &&
        credentials.Scopes.Count > 0 &&
        credentials.Scopes.All(static scope => !string.IsNullOrWhiteSpace(scope));
    }

    private static partial class Log
    {
        private static readonly Action<ILogger, string, string, double?, Exception?> RateLimitExceededLog =
            LoggerMessage.Define<string, string, double?>(
                LogLevel.Warning,
                new EventId(1000, nameof(RateLimitExceeded)),
                "Rate limit exceeded: IP={IpAddress}, Path={Path}, RetryAfter={RetryAfter}");

        private static readonly Action<ILogger, Exception?> ApplyingMigrationsLog =
            LoggerMessage.Define(
                LogLevel.Information,
                new EventId(1001, nameof(ApplyingMigrations)),
                "Applying database migrations...");

        private static readonly Action<ILogger, Exception?> MigrationsAppliedLog =
            LoggerMessage.Define(
                LogLevel.Information,
                new EventId(1002, nameof(MigrationsApplied)),
                "Database migrations applied successfully");

        private static readonly Action<ILogger, Exception?> SeedingDatabaseLog =
            LoggerMessage.Define(
                LogLevel.Information,
                new EventId(1003, nameof(SeedingDatabase)),
                "Seeding database...");

        private static readonly Action<ILogger, Exception?> SeedingCompletedLog =
            LoggerMessage.Define(
                LogLevel.Information,
                new EventId(1004, nameof(SeedingCompleted)),
                "Database seeding completed");

        private static readonly Action<ILogger, Exception?> MigrationErrorLog =
            LoggerMessage.Define(
                LogLevel.Error,
                new EventId(1005, nameof(MigrationError)),
                "An error occurred while applying database migrations or seeding");

        public static void RateLimitExceeded(ILogger logger, string ipAddress, string path, double? retryAfterSeconds) =>
            RateLimitExceededLog(logger, ipAddress, path, retryAfterSeconds, null);

        public static void ApplyingMigrations(ILogger logger) =>
            ApplyingMigrationsLog(logger, null);

        public static void MigrationsApplied(ILogger logger) =>
            MigrationsAppliedLog(logger, null);

        public static void SeedingDatabase(ILogger logger) =>
            SeedingDatabaseLog(logger, null);

        public static void SeedingCompleted(ILogger logger) =>
            SeedingCompletedLog(logger, null);

        public static void MigrationError(ILogger logger, Exception exception) =>
            MigrationErrorLog(logger, exception);
    }
}
