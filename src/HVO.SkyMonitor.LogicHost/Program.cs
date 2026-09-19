using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
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
using HVO.SkyMonitor.LogicHost.Services.Elastic;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Common.Observability;
using HVO.SkyMonitor.Common.Configuration;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Hosting;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
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
using Microsoft.Extensions.Options;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace HVO.SkyMonitor.LogicHost;

public sealed partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        var command = LogicHostCommandParser.Parse(args);
        var builder = WebApplication.CreateBuilder(command.ForwardedArguments.ToArray());
        DeploymentKeyPerFile.AddConfiguredDirectory(builder.Configuration);

        if (command.Mode is LogicHostHostMode.ObjectStoreBackup or LogicHostHostMode.ObjectStoreRestore or LogicHostHostMode.ObjectStoreVerify)
        {
            // Offline object-store maintenance: no services, no database, no listener. Runs
            // against the same configuration the runtime would use so the root and bucket
            // names cannot drift from what the host serves.
            return await RunObjectStoreMaintenanceAsync(command, builder.Configuration).ConfigureAwait(false);
        }

        var reverseProxy = builder.Configuration.GetSection(DeploymentReverseProxyOptions.SectionName).Get<DeploymentReverseProxyOptions>() ?? new();
        if (reverseProxy.Enabled)
        {
            if (reverseProxy.TrustedProxies.Count == 0 || reverseProxy.TrustedProxies.Any(static value => !IPAddress.TryParse(value, out _)))
            {
                throw new InvalidOperationException("ReverseProxy:TrustedProxies must contain valid explicit IP addresses when forwarded headers are enabled.");
            }
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = 1;
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
                foreach (var proxy in reverseProxy.TrustedProxies)
                {
                    options.KnownProxies.Add(IPAddress.Parse(proxy));
                }
            });
        }

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
        var objectStorageConfigured = builder.Configuration
            .GetSection(CentralObjectStorageOptions.SectionName).Exists();
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
        builder.Services.AddOptions<CentralProcessingEntitlementOptions>()
            .Bind(builder.Configuration.GetSection(CentralProcessingEntitlementOptions.SectionName))
            .Validate(options => options.Validate(out _), "ProcessingEntitlements configuration is invalid.")
            .ValidateOnStart();
        builder.Services.AddOptions<CentralElasticProviderOptions>()
            .Bind(builder.Configuration.GetSection(CentralElasticProviderOptions.SectionName))
            .Validate(options => options.Validate(out _), "ElasticProviders configuration is invalid.")
            .Validate<IOptions<CentralProcessingRunnerOptions>>(
                (options, runners) => options.ValidateRunnerProtocol(runners.Value.Enabled, out _),
                "ElasticProviders:Enabled requires ProcessingRunners:Enabled=true; provisioned instances register through the runner protocol.")
            .ValidateOnStart();
        builder.Services.AddOptions<CentralProcessingRunnerOptions>()
            .Bind(builder.Configuration.GetSection(CentralProcessingRunnerOptions.SectionName))
            .Validate(options => options.Validate(out _), "ProcessingRunners configuration is invalid.")
            .ValidateOnStart();
        builder.Services.AddOptions<CentralTransientOptions>()
            .Bind(builder.Configuration.GetSection(CentralTransientOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddOptions<CentralTransientPayloadReleaseOptions>()
            .Bind(builder.Configuration.GetSection(CentralTransientPayloadReleaseOptions.SectionName))
            .Validate(options => options.PollInterval > TimeSpan.Zero &&
                    options.ReservationLeaseTimeout >= TimeSpan.FromSeconds(1) &&
                    options.InitialRetryDelay > TimeSpan.Zero &&
                    options.MaximumRetryDelay >= options.InitialRetryDelay &&
                    options.MaximumRetryCount > 0,
                "TransientPayloadRelease timing values are invalid.")
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
            .AddCheck<CentralProcessingRunnerHealthCheck>("processing-runners", tags: ["worker"])
            .AddCheck<CentralElasticProviderHealthCheck>("elastic-providers", tags: ["worker"])
            .AddCheck<CentralProcessingEntitlementHealthCheck>("processing-entitlements", tags: ["worker"])
            .AddCheck<ProcessingGraphCatalogHealthCheck>("processing-graph-catalog", tags: ["consistency"])
            .AddCheck<CentralTransientLifecycleHealthCheck>("central-transient-lifecycle", tags: ["worker"])
            .AddCheck<FleetStatusHealthCheck>("fleet-status", tags: ["worker"])
            .AddCheck<EnvironmentalObservationHealthCheck>("environmental-observations", tags: ["worker"])
            .AddCheck<DeploymentLocationHealthCheck>("deployment-location", tags: ["consistency"]);
        healthChecks.AddInstalledCelestialCatalogHealthCheck();

        if (!string.IsNullOrWhiteSpace(redisConfiguration))
        {
            healthChecks.AddCheck<RedisHealthCheck>("redis", tags: ["dependency"]);
        }

        if (objectStorageConfigured)
        {
            healthChecks.AddCheck<ObjectStoreHealthCheck>("object-store", tags: ["dependency"]);
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

        // API Versioning
        builder.Services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
        })
        .AddMvc()
        .AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        })
        .AddOpenApi();

        // Prometheus metrics endpoint
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddPrometheusExporter();
                metrics.AddMeter("HVO.SkyMonitor.Authentication");
                metrics.AddMeter(CentralIngestTelemetry.MeterName);
                metrics.AddMeter(CentralArtifactRetrievalTelemetry.MeterName);
                metrics.AddMeter(CentralPresentationTelemetry.MeterName);
                metrics.AddMeter(CentralArtifactRetentionTelemetry.MeterName);
                metrics.AddMeter(CentralDerivativeWorkerTelemetry.MeterName);
                metrics.AddMeter(CentralProcessingRunnerTelemetry.MeterName);
                metrics.AddMeter(ElasticProviderTelemetry.MeterName);
                metrics.AddMeter(CentralProcessingFairnessTelemetry.MeterName);
                metrics.AddMeter(CentralTransientLifecycleTelemetry.MeterName);
                metrics.AddMeter(FleetStatusTelemetry.MeterName);
                metrics.AddMeter(EnvironmentalObservationTelemetry.MeterName);
                metrics.AddMeter(DeploymentLocationTelemetry.MeterName);
                metrics.AddMeter(OperatorUiTelemetry.MeterName);
                metrics.AddMeter(ObjectStoreTelemetry.MeterName);
                metrics.AddMeter(ProcessingGraphCatalogTelemetry.MeterName);
                metrics.AddAspNetCoreInstrumentation();
            })
            .WithTracing(tracing => tracing
                .AddSource(CentralIngestTelemetry.ActivitySourceName)
                .AddSource(CentralTransientLifecycleTelemetry.ActivitySourceName)
                .AddSource(DeploymentLocationTelemetry.ActivitySourceName)
                .AddSource(OperatorUiTelemetry.ActivitySourceName)
                .AddSource(ObjectStoreTelemetry.ActivitySourceName)
                .AddSource(ProcessingGraphCatalogTelemetry.ActivitySourceName));

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

        builder.Services.AddOptions<CentralObjectStorageOptions>()
            .Bind(builder.Configuration.GetSection(CentralObjectStorageOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => !string.Equals(options.ArtifactBucket, options.DiagnosticsBucket, StringComparison.Ordinal),
                "Artifact and diagnostics buckets must be distinct.")
            .Validate(HasValidObjectStorageEndpoint,
                "ObjectStorage:ServiceEndpoint must be a host name with an optional port and no URI scheme.")
            .Validate(HasValidObjectStorageCredentials,
                "ObjectStorage credentials do not match the configured CredentialMode.")
            .Validate(HasExclusiveProviderSettings,
                "ObjectStorage:Provider selects one provider; settings for the other provider must not be present.")
            .Validate(HasValidFilesystemRoot,
                "ObjectStorage:Filesystem:Root must be an absolute path when ObjectStorage:Provider is Filesystem.")
            .ValidateOnStart();
        builder.Services.AddSingleton<CentralObjectStorageNames>();
        builder.Services.AddSingleton<ObjectStoreTelemetry>();

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

        if (objectStorageConfigured)
        {
            builder.Services.AddObjectStorageInfrastructure();
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
        builder.Services.AddSingleton<ProcessingGraphCatalogTelemetry>();
        builder.Services.AddSingleton<ICentralProcessingGraphNodeRegistry, CentralProcessingGraphNodeRegistry>();
        builder.Services.AddScoped<ProcessingGraphBacklogSampler>();
        builder.Services.AddHostedService<ProcessingGraphBacklogWorker>();
        builder.Services.AddScoped<ProcessingGraphCatalogService>();
        builder.Services.AddScoped<IProcessingGraphCatalogService>(provider =>
            provider.GetRequiredService<ProcessingGraphCatalogService>());
        builder.Services.AddScoped<IProcessingGraphDeliveryService, ProcessingGraphDeliveryService>();
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
        var connectionPurpose = command.Mode == LogicHostHostMode.DatabaseInitialize
            ? LogicHostSqlConnectionPurpose.DatabaseInitialization
            : LogicHostSqlConnectionPurpose.Runtime;
        var sqlProfile = LogicHostSqlConnectionProfiles.Resolve(
            builder.Configuration,
            builder.Environment,
            connectionPurpose);

        builder.Services.AddSingleton<CentralProcessingUsageInterceptor>();
        builder.Services.AddDbContext<ApplicationDbContext>((services, options) =>
            options.UseSqlServer(sqlProfile.ConnectionString)
                .AddInterceptors(services.GetRequiredService<CentralProcessingUsageInterceptor>()));
        builder.Services.AddScoped<DatabaseInitializer>();
        builder.Services.AddScoped<DatabaseRuntimeValidator>();

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
        .AddClaimsPrincipalFactory<CanonicalUserClaimsPrincipalFactory>()
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
                    "api.runner",
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
                    var configured = builder.Configuration.GetSection(OpenIddictCertificateOptions.SectionName)
                        .Get<OpenIddictCertificateOptions>() ?? throw new InvalidOperationException(
                            "Production OpenIddict certificate configuration is required.");
                    var signing = OpenIddictCertificateOptions.Load(
                        configured.SigningPath,
                        configured.SigningPassword,
                        X509KeyUsageFlags.DigitalSignature);
                    var encryption = OpenIddictCertificateOptions.Load(
                        configured.EncryptionPath,
                        configured.EncryptionPassword,
                        X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment);
                    options.AddSigningCertificate(signing).AddEncryptionCertificate(encryption);
                }

                // Downstream services validate tokens via standard JwtBearer handlers, so emit
                // signed (non-encrypted) access tokens until we support shared decryption keys.
                options.DisableAccessTokenEncryption();

                // Register the ASP.NET Core host and configure the ASP.NET Core-specific options
                var aspNetCoreBuilder = options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableStatusCodePagesIntegration();

                if (DeploymentTransportSecurity.AllowsInsecureOpenIddictTransport(
                        builder.Environment.IsProduction(),
                        builder.Configuration["Deployment:Mode"]))
                {
                    // Isolated deployments explicitly permit local-network HTTP; persistent production never does.
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
                AddCredentialAuthenticationSchemes(policy);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context => HasCookieOrApiKeyCredential(context.User));
            });

            options.AddPolicy(AuthorizationPolicyNames.ApiKeyRead, policy =>
            {
                AddCredentialAuthenticationSchemes(policy);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context => HasReadCredential(context.User));
            });

            options.AddPolicy(AuthorizationPolicyNames.ApiKeyReadWrite, policy =>
            {
                AddCredentialAuthenticationSchemes(policy);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context => HasWriteCredential(context.User));
            });

            options.AddPolicy(AuthorizationPolicyNames.CanonicalBearer, policy =>
            {
                AddCredentialAuthenticationSchemes(policy);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context => HasCanonicalBearerCredential(context.User));
            });

            options.AddPolicy(AuthorizationPolicyNames.BearerAdmin, policy =>
            {
                AddCredentialAuthenticationSchemes(policy);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context => HasBearerAdminCredential(context.User));
            });

            options.AddPolicy(AuthorizationPolicyNames.InteractiveUser, policy =>
            {
                policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                    CentralArtifactCredentialAccess.GetOwnerId(context.User) is not null);
            });

            options.AddPolicy(AuthorizationPolicyNames.PlatformEditorialWrite, policy =>
            {
                policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                    CentralArtifactCredentialAccess.HasSingleCredentialIdentity(context.User));
                policy.RequireRole(AuthorizationRoleNames.PlatformEditor);
            });

            options.AddPolicy("OwnerLocationWrite", policy =>
            {
                AddCredentialAuthenticationSchemes(policy);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    var identity = CentralArtifactCredentialAccess.GetSingleCredentialIdentity(context.User);
                    if (identity is null || CentralArtifactCredentialAccess.IsSystem(context.User))
                    {
                        return false;
                    }
                    if (CentralArtifactCredentialAccess.IsApiKey(identity))
                    {
                        return CentralArtifactCredentialAccess.GetApiKeyAccessLevel(context.User)
                            == nameof(ApiKeyAccessLevel.ReadWrite);
                    }
                    return CanonicalCredentialClaims.IsCookie(identity)
                        || HasBearerWriteScope(context.User);
                });
            });

            // Phase 3: Account type-based policies
            options.AddPolicy("RequireSystemAccount", policy =>
            {
                AddCredentialAuthenticationSchemes(policy);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    return CentralArtifactCredentialAccess.IsSystem(context.User);
                });
            });

            options.AddPolicy("RequireUserAccount", policy =>
            {
                AddCredentialAuthenticationSchemes(policy);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    var identity = CentralArtifactCredentialAccess.GetSingleCredentialIdentity(context.User);
                    return identity is not null && !CentralArtifactCredentialAccess.IsSystem(context.User);
                });
            });

            options.AddPolicy("DerivativeJobsRead", policy =>
            {
                AddCredentialAuthenticationSchemes(policy);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                    CentralArtifactCredentialAccess.HasSingleCredentialIdentity(context.User)
                    && CentralArtifactCredentialAccess.HasScope(context.User, "api.admin"));
            });
            options.AddPolicy("ArtifactIngest", policy =>
            {
                AddCredentialAuthenticationSchemes(policy);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                    CentralArtifactCredentialAccess.IsSystem(context.User)
                    && CentralArtifactCredentialAccess.HasScope(context.User, "api.frames"));
            });
            options.AddPolicy("ArtifactRetrieval", policy =>
            {
                AddCredentialAuthenticationSchemes(policy);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context => HasArtifactRetrievalCredential(context.User));
            });
            options.AddPolicy("ProcessingRunner", policy =>
            {
                AddCredentialAuthenticationSchemes(policy);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                    CentralArtifactCredentialAccess.IsSystem(context.User)
                    && CentralArtifactCredentialAccess.HasScope(context.User, "api.runner"));
            });
            options.AddPolicy("TransientEventsRead", policy =>
            {
                AddCredentialAuthenticationSchemes(policy);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                    CentralArtifactCredentialAccess.HasSingleCredentialIdentity(context.User) &&
                    (CentralArtifactCredentialAccess.HasOwnerCredential(context.User) ||
                     CentralArtifactCredentialAccess.HasScope(context.User, "api.admin")));
            });
            options.AddPolicy("TransientReview", policy =>
            {
                AddCredentialAuthenticationSchemes(policy);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                {
                    var identity = CentralArtifactCredentialAccess.GetSingleCredentialIdentity(context.User);
                    if (identity is null || CentralArtifactCredentialAccess.IsSystem(context.User))
                    {
                        return false;
                    }
                    return CanonicalCredentialClaims.IsCookie(identity)
                        || CentralArtifactCredentialAccess.IsApiKey(identity)
                            && CentralArtifactCredentialAccess.GetApiKeyAccessLevel(context.User)
                                == nameof(ApiKeyAccessLevel.ReadWrite)
                        || CanonicalCredentialClaims.IsBearer(identity)
                            && HasBearerWriteScope(context.User);
                });
            });
            options.AddPolicy("TransientAdmin", policy =>
            {
                AddCredentialAuthenticationSchemes(policy);
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
        builder.Services.AddScoped<IArtifactIngestService, ArtifactIngestService>();
        builder.Services.AddSingleton<CentralIngestTelemetry>();
        builder.Services.AddScoped<ICentralArtifactRetrievalService, CentralArtifactRetrievalService>();
        builder.Services.AddScoped<ICentralArtifactObjectReader, CentralArtifactObjectReader>();
        builder.Services.AddSingleton<CentralPresentationGenerationGate>();
        builder.Services.AddSingleton<CentralLayeredPresentationCache>();
        builder.Services.AddSingleton<CentralPresentationTelemetry>();
        builder.Services.AddSingleton<CentralPresentationMaterializationGate>();
        builder.Services.AddScoped<CentralLayeredPresentationService>();
        builder.Services.AddScoped<ICentralLayeredPresentationService>(provider =>
            provider.GetRequiredService<CentralLayeredPresentationService>());
        builder.Services.AddScoped<ICentralPresentationMaterializer, CentralPresentationMaterializer>();
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
        builder.Services.AddScoped<ICentralProcessingGraphScheduler, CentralProcessingGraphScheduler>();
        builder.Services.AddScoped<ICentralProcessingGraphExecutionService, CentralProcessingGraphExecutionService>();
        builder.Services.AddScoped<ICentralDerivativeJobScheduler, CentralDerivativeJobScheduler>();
        builder.Services.AddScoped<ICentralDerivativeWindowResolver, CentralDerivativeWindowResolver>();
        builder.Services.AddScoped<CentralDerivativeJobService>();
        builder.Services.AddScoped<ICentralDerivativeJobService>(
            provider => provider.GetRequiredService<CentralDerivativeJobService>());
        builder.Services.AddScoped<ICentralDerivativeRunnerLeaseService>(
            provider => provider.GetRequiredService<CentralDerivativeJobService>());
        builder.Services.AddScoped<CentralDerivativeJobInputReader>();
        builder.Services.AddScoped<ICentralDerivativeJobInputReader>(
            provider => provider.GetRequiredService<CentralDerivativeJobInputReader>());
        builder.Services.AddScoped<ICentralDerivativeJobInputDescriber>(
            provider => provider.GetRequiredService<CentralDerivativeJobInputReader>());
        builder.Services.AddScoped<ICentralDerivativeOutputWriter, CentralDerivativeOutputWriter>();
        builder.Services.AddScoped<CentralDerivativeJobExecutor>();
        builder.Services.AddScoped<ICentralDerivativeJobExecutor>(
            provider => provider.GetRequiredService<CentralDerivativeJobExecutor>());
        builder.Services.AddScoped<ICentralDerivativeExecutionPipeline>(
            provider => provider.GetRequiredService<CentralDerivativeJobExecutor>());
        builder.Services.AddSingleton<CentralProcessingRunnerTelemetry>();
        builder.Services.AddSingleton<ElasticProviderTelemetry>();
        builder.Services.AddSingleton<LocalProcessElasticRunnerProvider>();
        builder.Services.AddSingleton<IElasticRunnerProvider>(services =>
            services.GetRequiredService<IOptions<CentralElasticProviderOptions>>().Value is { Enabled: true, Provider: CentralElasticProviderKind.LocalProcess }
                ? services.GetRequiredService<LocalProcessElasticRunnerProvider>()
                : new NullElasticRunnerProvider());
        builder.Services.AddSingleton<IElasticArtifactAccessAdapter>(services =>
        {
            var settings = services.GetRequiredService<IOptions<CentralElasticProviderOptions>>().Value;
            var address = settings.LocalProcess.LogicHostUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : new Uri("https://localhost/");
            return new LeaseScopedArtifactAccessAdapter(address);
        });
        builder.Services.AddHostedService<ElasticRunnerAutoscaler>();
        builder.Services.AddSingleton<CentralProcessingFairnessTelemetry>();
        builder.Services.AddScoped<ICentralProcessingRunnerRegistry, CentralProcessingRunnerRegistry>();
        builder.Services.AddScoped<ICentralProcessingRunnerJobService, CentralProcessingRunnerJobService>();
        builder.Services.AddScoped<ICentralDerivativeJobOperationsService, CentralDerivativeJobOperationsService>();
        builder.Services.AddSingleton<CentralDerivativeWorkerTelemetry>();
        builder.Services.AddSingleton<CentralProcessingGraphConvergenceSignal>();
        builder.Services.AddHostedService<CentralDerivativeWorker>();
        builder.Services.AddScoped<IDeviceRigProfileService, DeviceRigProfileService>();
        builder.Services.AddInstalledCelestialCatalog();

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            try
            {
                if (command.Mode == LogicHostHostMode.DatabaseInitialize ||
                    app.Environment.IsDevelopment() ||
                    app.Environment.IsEnvironment("Testing"))
                {
                    var result = await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>()
                        .RunAsync(CancellationToken.None).ConfigureAwait(false);
                    Log.DatabaseInitializationCompleted(
                        logger,
                        result.AttemptId,
                        result.TargetMigrationId,
                        result.Elapsed.TotalMilliseconds);
                }
                else
                {
                    await scope.ServiceProvider.GetRequiredService<DatabaseRuntimeValidator>()
                        .ValidateAsync(CancellationToken.None).ConfigureAwait(false);
                    Log.DatabaseRuntimeValidated(logger);
                }
            }
            catch (Exception exception)
            {
                Log.DatabasePreparationFailed(logger, exception);
                throw;
            }
        }

        if (command.Mode == LogicHostHostMode.DatabaseInitialize)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            return 0;
        }
        _ = app.Services.GetRequiredService<CatalogSnapshotResult>();

        // Configure the HTTP request pipeline

        if (reverseProxy.Enabled)
        {
            app.UseForwardedHeaders();
        }

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

        app.MapStaticAssets();
        app.UseRouting();

        // Identity Hardening: Rate limiting
        app.UseRateLimiter();

        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            if (HasCookieWithCompetingCredential(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        app.UseMiddleware<DynamicPageCachePolicyMiddleware>();
        app.UseAuthorization();
        app.UseMiddleware<OperatorUiResponseMetricsMiddleware>();
        app.UseAntiforgery();

        // OpenAPI and Scalar
        app.MapOpenApi().WithDocumentPerVersion();
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
        return 0;
    }

    private static async Task<int> RunObjectStoreMaintenanceAsync(LogicHostCommand command, ConfigurationManager configuration)
    {
        var options = configuration.GetSection(CentralObjectStorageOptions.SectionName).Get<CentralObjectStorageOptions>() ?? new();
        if (options.Provider != ObjectStorageProvider.Filesystem || !HasValidFilesystemRoot(options))
        {
            await Console.Error.WriteLineAsync("Object-store backup, restore and verify apply only when ObjectStorage:Provider is Filesystem with a valid ObjectStorage:Filesystem:Root; the S3 provider's backup belongs to the object-storage service's own runbook.").ConfigureAwait(false);
            return 2;
        }
        var buckets = new[] { options.ArtifactBucket, options.DiagnosticsBucket };
        var path = Path.GetFullPath(command.Path!);
        var root = Path.GetFullPath(options.Filesystem.Root!);
        if (path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || string.Equals(path, root, StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync("The backup path must be outside the object-store root.").ConfigureAwait(false);
            return 2;
        }
        try
        {
            switch (command.Mode)
            {
                case LogicHostHostMode.ObjectStoreBackup:
                    {
                        var inventory = await FilesystemObjectBackup.BackupAsync(root, buckets, path, TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
                        await Console.Out.WriteLineAsync($"object-store-backup ok objects={inventory.ObjectCount} bytes={inventory.TotalBytes} buckets={string.Join(",", inventory.Buckets)} path={path}").ConfigureAwait(false);
                        return 0;
                    }
                case LogicHostHostMode.ObjectStoreRestore:
                    {
                        var inventory = await FilesystemObjectBackup.RestoreAsync(path, root, buckets, CancellationToken.None).ConfigureAwait(false);
                        var mismatches = await FilesystemObjectBackup.VerifyAsync(path, root, CancellationToken.None).ConfigureAwait(false);
                        await Console.Out.WriteLineAsync($"object-store-restore ok objects={inventory.ObjectCount} bytes={inventory.TotalBytes} verifiedMismatches={mismatches}").ConfigureAwait(false);
                        return mismatches == 0 ? 0 : 1;
                    }
                default:
                    {
                        var mismatches = await FilesystemObjectBackup.VerifyAsync(path, root, CancellationToken.None).ConfigureAwait(false);
                        await Console.Out.WriteLineAsync($"object-store-verify {(mismatches == 0 ? "ok" : "MISMATCH")} mismatches={mismatches}").ConfigureAwait(false);
                        return mismatches == 0 ? 0 : 1;
                    }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or HVO.SkyMonitor.Storage.FileSystem.FileSystemFaultException)
        {
            await Console.Error.WriteLineAsync($"object-store maintenance failed: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
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

    private static bool HasCookieOrApiKeyCredential(System.Security.Claims.ClaimsPrincipal principal)
        => CentralArtifactCredentialAccess.GetSingleCredentialIdentity(principal) is { } identity
            && (CanonicalCredentialClaims.IsCookie(identity) || CanonicalCredentialClaims.IsApiKey(identity));

    private static void AddCredentialAuthenticationSchemes(AuthorizationPolicyBuilder policy)
        => policy.AddAuthenticationSchemes(
            IdentityConstants.ApplicationScheme,
            ApiKeyAuthenticationOptions.AuthenticationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

    private static bool HasCookieWithCompetingCredential(HttpContext context)
        => context.User.Identities.Any(CanonicalCredentialClaims.IsCookie)
            && (context.Request.Headers.ContainsKey(ApiKeyAuthenticationOptions.HeaderName)
                || context.Request.Headers.Authorization.Any(static value =>
                    value is not null && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)));

    private static bool HasReadCredential(System.Security.Claims.ClaimsPrincipal principal)
    {
        var identity = CentralArtifactCredentialAccess.GetSingleCredentialIdentity(principal);
        if (identity is null)
        {
            return false;
        }

        if (CanonicalCredentialClaims.IsCookie(identity))
        {
            return true;
        }
        if (CanonicalCredentialClaims.IsApiKey(identity))
        {
            return CentralArtifactCredentialAccess.GetApiKeyAccessLevel(principal) is
                nameof(ApiKeyAccessLevel.Read) or nameof(ApiKeyAccessLevel.ReadWrite);
        }
        return HasBearerReadScope(principal);
    }

    private static bool HasWriteCredential(System.Security.Claims.ClaimsPrincipal principal)
    {
        var identity = CentralArtifactCredentialAccess.GetSingleCredentialIdentity(principal);
        if (identity is null || CentralArtifactCredentialAccess.IsSystem(principal))
        {
            return false;
        }

        return CanonicalCredentialClaims.IsCookie(identity)
            || CanonicalCredentialClaims.IsApiKey(identity)
                && CentralArtifactCredentialAccess.GetApiKeyAccessLevel(principal)
                    == nameof(ApiKeyAccessLevel.ReadWrite)
            || CanonicalCredentialClaims.IsBearer(identity) && HasBearerWriteScope(principal);
    }

    private static bool HasCanonicalBearerCredential(System.Security.Claims.ClaimsPrincipal principal)
        => CentralArtifactCredentialAccess.GetSingleCredentialIdentity(principal) is { } identity
            && CanonicalCredentialClaims.IsBearer(identity);

    private static bool HasBearerAdminCredential(System.Security.Claims.ClaimsPrincipal principal)
        => CentralArtifactCredentialAccess.GetSingleCredentialIdentity(principal) is { } identity
            && CanonicalCredentialClaims.IsBearer(identity)
            && CentralArtifactCredentialAccess.HasScope(principal, "api.admin");

    private static bool HasArtifactRetrievalCredential(System.Security.Claims.ClaimsPrincipal principal)
    {
        var identity = CentralArtifactCredentialAccess.GetSingleCredentialIdentity(principal);
        return identity is not null
            && (CanonicalCredentialClaims.IsCookie(identity)
                || CanonicalCredentialClaims.IsApiKey(identity)
                || CanonicalCredentialClaims.IsBearer(identity)
                    && (HasBearerReadScope(principal)
                        || CentralArtifactCredentialAccess.HasScope(principal, "api.artifacts.read")));
    }

    private static bool HasBearerReadScope(System.Security.Claims.ClaimsPrincipal principal)
        => CentralArtifactCredentialAccess.HasScope(principal, "api.viewer")
            || CentralArtifactCredentialAccess.HasScope(principal, "api.admin");

    private static bool HasBearerWriteScope(System.Security.Claims.ClaimsPrincipal principal)
        => CentralArtifactCredentialAccess.HasScope(principal, "api.owner.write")
            || CentralArtifactCredentialAccess.HasScope(principal, "api.admin");

    internal static bool HasValidObjectStorageEndpoint(CentralObjectStorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ServiceEndpoint))
        {
            return true;
        }
        return !options.ServiceEndpoint.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(
                $"{(options.UseTls ? "https" : "http")}://{options.ServiceEndpoint}",
                UriKind.Absolute,
                out var endpoint)
            && string.IsNullOrEmpty(endpoint.UserInfo)
            && endpoint.AbsolutePath == "/"
            && string.IsNullOrEmpty(endpoint.Query)
            && string.IsNullOrEmpty(endpoint.Fragment);
    }

    // Provider settings are mutually exclusive and fail closed: a deployment that names one
    // provider while carrying the other's settings is half-migrated, and starting it would
    // silently serve the wrong provider. The S3 group is only checked when Filesystem is
    // selected, because S3 is the default and its defaults are indistinguishable from
    // "unset"; a Filesystem root under an S3 selection is always a contradiction.
    internal static bool HasExclusiveProviderSettings(CentralObjectStorageOptions options)
        => options.Provider switch
        {
            ObjectStorageProvider.Filesystem => !options.HasS3Settings,
            ObjectStorageProvider.S3 => !options.HasFilesystemSettings,
            _ => false
        };

    internal static bool HasValidFilesystemRoot(CentralObjectStorageOptions options)
        => options.Provider != ObjectStorageProvider.Filesystem
            || (!string.IsNullOrWhiteSpace(options.Filesystem.Root)
                && Path.IsPathRooted(options.Filesystem.Root)
                && options.Filesystem.Root == Path.GetFullPath(options.Filesystem.Root));

    internal static bool HasValidObjectStorageCredentials(CentralObjectStorageOptions options)
        => options.CredentialMode switch
        {
            ObjectStorageCredentialMode.DefaultChain => string.IsNullOrWhiteSpace(options.AccessKey)
                && string.IsNullOrWhiteSpace(options.SecretKey)
                && string.IsNullOrWhiteSpace(options.SessionToken),
            ObjectStorageCredentialMode.Static => !string.IsNullOrWhiteSpace(options.AccessKey)
                && !string.IsNullOrWhiteSpace(options.SecretKey)
                && string.IsNullOrWhiteSpace(options.SessionToken),
            ObjectStorageCredentialMode.Session => !string.IsNullOrWhiteSpace(options.AccessKey)
                && !string.IsNullOrWhiteSpace(options.SecretKey)
                && !string.IsNullOrWhiteSpace(options.SessionToken),
            _ => false
        };

    private static partial class Log
    {
        private static readonly Action<ILogger, string, string, double?, Exception?> RateLimitExceededLog =
            LoggerMessage.Define<string, string, double?>(
                LogLevel.Warning,
                new EventId(1000, nameof(RateLimitExceeded)),
                "Rate limit exceeded: IP={IpAddress}, Path={Path}, RetryAfter={RetryAfter}");

        private static readonly Action<ILogger, Guid, string, double, Exception?> DatabaseInitializationCompletedLog =
            LoggerMessage.Define<Guid, string, double>(
                LogLevel.Information,
                new EventId(1001, nameof(DatabaseInitializationCompleted)),
                "Database initialization completed: AttemptId={AttemptId}, TargetMigrationId={TargetMigrationId}, ElapsedMilliseconds={ElapsedMilliseconds}");

        private static readonly Action<ILogger, Exception?> DatabaseRuntimeValidatedLog =
            LoggerMessage.Define(
                LogLevel.Information,
                new EventId(1002, nameof(DatabaseRuntimeValidated)),
                "Database runtime state validated successfully");

        private static readonly Action<ILogger, Exception?> DatabasePreparationFailedLog =
            LoggerMessage.Define(
                LogLevel.Critical,
                new EventId(1003, nameof(DatabasePreparationFailed)),
                "Database initialization or runtime validation failed");

        public static void RateLimitExceeded(ILogger logger, string ipAddress, string path, double? retryAfterSeconds) =>
            RateLimitExceededLog(logger, ipAddress, path, retryAfterSeconds, null);

        public static void DatabaseInitializationCompleted(
            ILogger logger,
            Guid attemptId,
            string targetMigrationId,
            double elapsedMilliseconds) =>
            DatabaseInitializationCompletedLog(logger, attemptId, targetMigrationId, elapsedMilliseconds, null);

        public static void DatabaseRuntimeValidated(ILogger logger) =>
            DatabaseRuntimeValidatedLog(logger, null);

        public static void DatabasePreparationFailed(ILogger logger, Exception exception) =>
            DatabasePreparationFailedLog(logger, exception);
    }
}
