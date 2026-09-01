using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Asp.Versioning;
using HVO.SkyMonitor.Common.Infrastructure.Diagnostics;
using HVO.SkyMonitor.Common.Infrastructure.Filters;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.RandomImage;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Modules.Zwo;
using HVO.SkyMonitor.CameraAgent.Extensions;
using HVO.SkyMonitor.CameraAgent.Components;
using HVO.SkyMonitor.CameraAgent.Components.Account;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Common.Observability;
using HVO.SkyMonitor.Common.Configuration;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Endpoints;

namespace HVO.SkyMonitor.CameraAgent;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        DeploymentKeyPerFile.AddConfiguredDirectory(builder.Configuration);

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
        builder.Services.AddSingleton<ICaptureAgentIdentityProvider, ProvisionedCaptureAgentIdentityProvider>();
        builder.Services.AddSingleton<IDeviceRigProfileSeeder, DeviceRigProfileSeeder>();
        builder.Services.AddHostedService<DeviceRigProfileSynchronizationService>();
        builder.Services.AddScoped<IDeviceBootstrapWorkflow, DeviceBootstrapWorkflow>();

        ApplyLocalIdentityPasswordFile(builder.Configuration);
        var localIdentitySection = builder.Configuration.GetSection("LocalIdentity");
        builder.Services.AddOptions<LocalIdentityOptions>()
            .Bind(localIdentitySection)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var localIdentitySettings = localIdentitySection.Get<LocalIdentityOptions>() ?? new LocalIdentityOptions();
        var identityDbPath = ResolveIdentityDatabasePath(localIdentitySettings.DatabasePath, builder.Environment.ContentRootPath);
        DeviceStateFilePermissions.RestrictDirectory(Path.GetDirectoryName(identityDbPath)!);
        var identityConnectionString = $"Data Source={identityDbPath}";

        var dataProtectionPath = Path.Combine(builder.Environment.ContentRootPath, "DataProtection-Keys");
        DeviceStateFilePermissions.RestrictDirectory(dataProtectionPath);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
        builder.Services.AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>();

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
        .AddClaimsPrincipalFactory<CanonicalLocalUserClaimsPrincipalFactory>()
        .AddSignInManager()
        .AddDefaultTokenProviders();

        builder.Services.AddSingleton<IEmailSender<ApplicationUser>, LoggingEmailSender>();
        builder.Services.AddSingleton<CameraAgentIdentitySeeder>();
        builder.Services.AddSingleton<CameraAgentIdentityInitialization>();
        builder.Services.AddScoped<OwnerBootstrapStateReader>();
        builder.Services.AddScoped<OwnerPasswordReplacementService>();

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

        var healthChecks = builder.Services.AddSkyMonitorHealthChecks();
        builder.Services.AddSingleton<CameraAgentOperatorTelemetry>();
        healthChecks.AddDbContextCheck<ApplicationDbContext>("identity-database", tags: ["dependency"]);
        healthChecks.AddCheck<OwnerBootstrapHealthCheck>("owner-bootstrap", tags: ["dependency"]);
        healthChecks.AddInstalledCelestialCatalogHealthCheck();
        healthChecks.AddCheck<DeploymentLocationHealthCheck>("deployment-location", tags: ["dependency"]);
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddPrometheusExporter();
                metrics.AddRuntimeInstrumentation();
                metrics.AddMeter(FleetHeartbeatTelemetry.MeterName);
                metrics.AddMeter(EnvironmentalObservationDeliveryTelemetry.MeterName);
                metrics.AddMeter(EnvironmentalAcquisitionTelemetry.InstrumentationName);
                metrics.AddMeter(TransientWorkerTelemetry.MeterName);
                metrics.AddMeter(HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureControlTelemetry.MeterName);
                metrics.AddMeter(DeploymentLocationTelemetry.MeterName);
                metrics.AddMeter(HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration.CalibrationTelemetry.MeterName);
                metrics.AddMeter(CameraAgentOperatorTelemetry.InstrumentationName);
                metrics.AddMeter(HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.CaptureProcessingTelemetry.MeterName);
            })
            .WithTracing(tracing =>
            {
                if (builder.Environment.IsEnvironment("StandaloneW6"))
                {
                    tracing.SetSampler(new AlwaysOnSampler());
                }
                tracing.AddSource(TransientWorkerTelemetry.ActivitySourceName)
                    .AddSource(DeploymentLocationTelemetry.ActivitySourceName)
                    .AddSource(EnvironmentalObservationDeliveryTelemetry.ActivitySourceName)
                    .AddSource(EnvironmentalAcquisitionTelemetry.InstrumentationName)
                    .AddSource(CameraAgentOperatorTelemetry.InstrumentationName)
                    .AddSource(HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawIngressTelemetry.ActivitySourceName)
                    .AddSource(HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureControlTelemetry.ActivitySourceName)
                    .AddSource(HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution.CaptureLaneTelemetry.ActivitySourceName)
                    .AddSource(HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.CaptureProcessingTelemetry.ActivitySourceName)
                    .AddSource(HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration.CalibrationTelemetry.ActivitySourceName);
            });

        builder.Services.AddCentralIdentityAuthentication(builder.Configuration);
        builder.Services.AddSingleton<IConfigureOptions<CentralIdentityOptions>, DeviceSecretsCentralIdentityConfigurator>();
        builder.Services.AddSkyMonitorApiClient(builder.Configuration);
        RegisterAcceptanceCentralAttemptRecorder(builder);
        builder.Services.AddSingleton<IFleetHeartbeatTransport, CameraAgentFleetHeartbeatTransport>();
        builder.Services.AddSingleton<ITransientCandidateTransport, CameraAgentTransientCandidateTransport>();
        builder.Services.AddSingleton<DeploymentLocationReconciliationState>();
        builder.Services.AddHostedService<DeploymentLocationReconciliationWorker>();
        builder.Services.AddSingleton<CameraAgentEnvironmentalObservationBridge>();
        builder.Services.AddSingleton<IEnvironmentalObservationTargetResolver>(provider =>
            provider.GetRequiredService<CameraAgentEnvironmentalObservationBridge>());
        builder.Services.AddSingleton<IEnvironmentalObservationTransport>(provider =>
            provider.GetRequiredService<CameraAgentEnvironmentalObservationBridge>());

        var authenticationBuilder = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = IdentityConstants.ApplicationScheme;
        });

        authenticationBuilder.AddIdentityCookies();

        builder.Services.ConfigureApplicationCookie(options =>
            ConfigureApplicationCookie(options, localIdentitySettings.CookieName));

        builder.Services.AddCameraAgentAuthorization();
        builder.Services.AddCameraAgentOutboxOperations();
        builder.Services.AddScoped<ICameraAgentOperatorUiService, CameraAgentOperatorUiService>();
        builder.Services.AddScoped<ICameraAgentScheduleUiService, CameraAgentScheduleUiService>();
        builder.Services.AddScoped<ICameraAgentCalibrationUiService, CameraAgentCalibrationUiService>();
        builder.Services.AddScoped<ICameraAgentEnvironmentalUiService, CameraAgentEnvironmentalUiService>();
        builder.Services.AddScoped<ICameraAgentTransientUiService, CameraAgentTransientUiService>();

        builder.Services.AddOptions<CapturePreviewOptions>()
            .Bind(builder.Configuration.GetSection("CapturePreview"))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddInstalledCelestialCatalog();
        builder.Services.AddCameraAgentInfrastructure(builder.Configuration);
        builder.Services.AddSingleton<CalibrationOperationsTokenService>();
        healthChecks.AddCheck<CameraAgentConfigurationHealthCheck>("camera-configuration", tags: ["dependency"]);
        healthChecks.AddCheck<DiskPressureHealthCheck>("disk-pressure", tags: ["dependency"]);
        healthChecks.AddCheck<RawIngressHealthCheck>("raw-ingress", tags: ["dependency"]);
        healthChecks.AddCheck<CaptureLanesHealthCheck>("capture-lanes", tags: ["dependency"]);
        healthChecks.AddCheck<CaptureProcessingHealthCheck>("capture-processing", tags: ["dependency"]);
        healthChecks.AddCheck<ArtifactOutboxHealthCheck>("artifact-outbox", tags: ["dependency"]);
        healthChecks.AddCheck<FleetHeartbeatHealthCheck>("fleet-heartbeat", tags: ["dependency"]);
        healthChecks.AddCheck<DeploymentLocationReconciliationHealthCheck>(
            "deployment-location-reconciliation", tags: ["dependency"]);
        healthChecks.AddCheck<EnvironmentalObservationDeliveryHealthCheck>("environmental-delivery", tags: ["dependency"]);
        healthChecks.AddCheck<EnvironmentalAcquisitionHealthCheck>("environmental-acquisition", tags: ["dependency"]);
        healthChecks.AddCheck<TransientWorkerHealthCheck>("transient-worker", tags: ["dependency"]);
        healthChecks.AddCheck<TransientCandidateDeliveryHealthCheck>(
            "transient-candidate-delivery", tags: ["dependency"]);
        healthChecks.AddCheck<CaptureAdmissionHealthCheck>("capture-admission", tags: ["dependency"]);
        healthChecks.AddCheck<CalibrationLibraryHealthCheck>("calibration-library", tags: ["dependency"]);
        AddCameraModules(builder.Services);

        var app = builder.Build();
        _ = app.Services.GetRequiredService<CatalogSnapshotResult>();

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

        app.UseExceptionHandler();

        app.UseHttpLogging();

        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), appBuilder =>
            {
                appBuilder.UseStatusCodePagesWithReExecute("/not-found", "?statusCode={0}");
                appBuilder.Use(async (context, next) =>
                {
                    await next(context).ConfigureAwait(false);
                    if (context.Response.HasStarted)
                    {
                        return;
                    }
                    if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
                    {
                        var returnUrl = string.Concat(context.Request.PathBase, context.Request.Path, context.Request.QueryString);
                        context.Response.Redirect($"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}");
                    }
                    else if (context.Response.StatusCode == StatusCodes.Status403Forbidden)
                    {
                        context.Response.Redirect("/Account/AccessDenied");
                    }
                });
            }
        );

        app.UseStaticFiles();
        app.MapStaticAssets();

        app.UseRouting();

        app.UseAuthentication();
        app.UseMiddleware<OwnerBootstrapGateMiddleware>();
        app.UseAuthorization();

        app.UseAntiforgery();

        app.MapOpenApi().WithDocumentPerVersion();
        app.MapScalarApiReference(options =>
        {
            options.Title = "SkyMonitor Camera Agent";
        });

        app.MapControllers();
        app.MapCameraAgentGalleryEndpoints();
        app.MapCameraAgentArtifactEndpoints();
        app.MapCameraAgentOperationsEndpoints();
        app.MapCameraAgentScheduleOperationsEndpoints();
        app.MapCameraAgentPipelineOperationsEndpoints();
        app.MapCameraAgentProcessingGraphOperationsEndpoints();
        app.MapCameraAgentCalibrationOperationsEndpoints();
        app.MapCameraAgentOutboxOperationsEndpoints();
        app.MapCameraAgentEnvironmentalOperationsEndpoints();
        app.MapCameraAgentDeploymentEndpoints();
        app.MapCameraAgentLifecycleEndpoints();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        app.MapAdditionalIdentityEndpoints();
        app.MapGet(OwnerBootstrapGateMiddleware.StatusPath, async (
                OwnerBootstrapStateReader stateReader,
                CancellationToken cancellationToken) =>
            {
                var state = await stateReader.GetStateAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(new
                {
                    state,
                    passwordChangeRequired = state is OwnerBootstrapStates.TemporaryPassword or OwnerBootstrapStates.PasswordChangeRequired
                });
            })
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OwnerBootstrapReadV1);
        app.MapGet(OwnerBootstrapGateMiddleware.VerificationPath, async (
                HttpContext context,
                IConfiguration hostConfiguration,
                OwnerBootstrapStateReader ownerBootstrapStateReader,
                HVO.SkyMonitor.CameraAgent.Common.Configuration.ICameraAgentConfigurationLoader configurationLoader,
                HVO.SkyMonitor.Catalog.Sqlite.CatalogSnapshotResult catalog,
                IOptions<HVO.SkyMonitor.CameraAgent.Common.Options.CameraAgentHostOptions> options,
                CancellationToken cancellationToken) =>
            {
                var expectedToken = hostConfiguration["InstallationVerification:Token"];
                var suppliedToken = context.Request.Headers["X-HVO-Installation-Token"].ToString();
                if (string.IsNullOrEmpty(expectedToken) || string.IsNullOrEmpty(suppliedToken) ||
                    !CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken)),
                        SHA256.HashData(Encoding.UTF8.GetBytes(suppliedToken))))
                {
                    return Results.Unauthorized();
                }
                var configuration = await configurationLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
                var ownerBootstrapState = await ownerBootstrapStateReader.GetStateAsync(cancellationToken).ConfigureAwait(false);
                await using var stream = File.OpenRead(Path.GetFullPath(options.Value.ConfigFilePath));
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(new
                {
                    agentId = configuration.AgentId,
                    ownerEmail = hostConfiguration["LocalIdentity:AdminEmail"],
                    ownerBootstrapState,
                    configurationSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(document.RootElement),
                    rigProfileSha256 = CameraRigProfileIdentity.ComputeSha256(configuration.Rig),
                    scheduleSha256 = configuration.Schedule is null
                        ? null
                        : CaptureScheduleContract.ComputeSha256(configuration.Schedule),
                    deploymentLocationId = configuration.DeploymentLocation?.LocationId,
                    deploymentLocationVersion = configuration.DeploymentLocation?.Version,
                    deploymentLocationSha256 = configuration.DeploymentLocation?.CanonicalSha256,
                    rawIngressRoot = options.Value.RawIngressRoot,
                    catalogId = catalog.CatalogId,
                    packageVersion = catalog.SnapshotVersion,
                    schemaVersion = catalog.SchemaVersion,
                    preprocessingVersion = catalog.PreprocessingVersion,
                    databaseSha256 = catalog.DatabaseSha256,
                    databaseLength = catalog.DatabaseLength,
                    rowCount = catalog.RowCount
                });
            })
            .AllowAnonymous();
        app.MapPrometheusScrapingEndpoint();

        app.MapSkyMonitorHealthEndpoints();

        var identityInitialization = app.Services.GetRequiredService<CameraAgentIdentityInitialization>();
        await identityInitialization.RunAsync(async cancellationToken =>
        {
            using (var scope = app.Services.CreateScope())
            {
                var seeder = scope.ServiceProvider.GetRequiredService<CameraAgentIdentitySeeder>();
                await seeder.InitializeAsync(cancellationToken).ConfigureAwait(false);
                DeviceStateFilePermissions.RestrictFile(identityDbPath);
            }
        }, CancellationToken.None).ConfigureAwait(false);

        await app.RunAsync().ConfigureAwait(false);
    }

    internal static void AddCameraModules(IServiceCollection services)
    {
        services.AddCameraModule<RandomImageCameraModule>("RandomImage");
        services.AddCameraModule<VirtualSkyCameraModule>("VirtualSky");
        services.AddCameraModule<ZwoAsiCameraModule>("ZwoAsi");
    }

    private static void RegisterAcceptanceCentralAttemptRecorder(WebApplicationBuilder builder)
    {
        var configuredPath = builder.Configuration["CameraAgent:AcceptanceCentralAttemptLogPath"];
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return;
        }
        if (!builder.Environment.IsEnvironment("StandaloneW6") || !string.Equals(
                builder.Configuration["CameraAgent:CentralIntegration:Mode"],
                "Disabled",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Central-attempt recording is permitted only in StandaloneW6 with central integration disabled.");
        }
        var root = Path.GetFullPath(builder.Configuration["CameraAgent:RawIngressRoot"]
            ?? throw new InvalidOperationException("CameraAgent:RawIngressRoot is required."));
        var path = Path.GetFullPath(configuredPath);
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(string.Concat("..", Path.DirectorySeparatorChar), StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("The central-attempt log must remain beneath the raw-ingress root.");
        }
        builder.Services.AddSingleton(new CameraAgentCentralHttpAttemptRecorder(path));
        builder.Services.AddSingleton<Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter,
            CameraAgentCentralHttpAttemptFilter>();
    }

    internal static void ConfigureApplicationCookie(
        CookieAuthenticationOptions options,
        string cookieName = LocalIdentityOptions.DefaultCookieName)
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.Cookie.Name = cookieName;
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
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    }

    internal static void ApplyLocalIdentityPasswordFile(ConfigurationManager configuration)
    {
        var passwordFile = configuration["LocalIdentity:AdminPasswordFile"];
        if (string.IsNullOrWhiteSpace(passwordFile))
        {
            return;
        }
        if (!Path.IsPathFullyQualified(passwordFile))
        {
            throw new InvalidOperationException("LocalIdentity:AdminPasswordFile must be an absolute path.");
        }
        var passwordFileInfo = new FileInfo(passwordFile);
        if (!passwordFileInfo.Exists || passwordFileInfo.Length is < 1 or > 4096)
        {
            throw new InvalidOperationException("LocalIdentity:AdminPasswordFile must be a non-empty regular file no larger than 4096 bytes.");
        }
        var password = File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
        if (password.Length == 0)
        {
            throw new InvalidOperationException("LocalIdentity:AdminPasswordFile is empty.");
        }
        configuration["LocalIdentity:AdminPassword"] = password;
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
