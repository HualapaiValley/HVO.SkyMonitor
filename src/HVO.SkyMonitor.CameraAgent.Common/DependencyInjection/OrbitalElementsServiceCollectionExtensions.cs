using System.Reflection;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;

/// <summary>
/// Composes CameraAgent-local orbital-element acquisition: the durable store under
/// <c>{RawIngressRoot}/.orbital-elements/</c>, its fail-closed availability and initializer, the CelesTrak GP client,
/// the single-flight refresh job and its periodic automation adapter, operator operations, health evaluation,
/// telemetry, and the <see cref="IOrbitalSnapshotReader"/> consumers resolve. Called once by
/// <see cref="CameraAgentServiceCollectionExtensions.AddCameraAgentInfrastructure"/>, before the automation runner is
/// registered, so the store is initialized before the runner's first sweep.
/// </summary>
internal static class OrbitalElementsServiceCollectionExtensions
{
    /// <summary>The fixed product token sent as the provider User-Agent.</summary>
    internal const string ProductToken = "HVO.SkyMonitor.CameraAgent";

    /// <summary>The store's directory under the raw-ingress root.</summary>
    internal const string StoreDirectoryName = ".orbital-elements";

    internal static IServiceCollection AddCameraAgentOrbitalElements(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(static provider => new SqliteOrbitalElementsStore(
            CreateStoreSettings(provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<SqliteOrbitalElementsStore>>()));
        services.AddSingleton<IOrbitalElementsStore>(static provider =>
            provider.GetRequiredService<SqliteOrbitalElementsStore>());
        services.AddSingleton<OrbitalElementsAvailability>();
        services.AddSingleton<OrbitalElementsTelemetry>();

        // No retry, resilience, redirect or logging handlers: one admitted attempt is exactly one request, a non-200
        // response halts, and the client's own TimeProvider deadline governs the whole request.
        services.AddHttpClient(CelesTrakGpSource.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(CelesTrakGpSource.CreatePrimaryHandler)
            .ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers();
        services.AddSingleton<IOrbitalElementsProviderClient>(static provider => new CelesTrakGpClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(CelesTrakGpSource.HttpClientName),
            provider.GetRequiredService<TimeProvider>(),
            CreateClientSettings(provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.OrbitalElements)));

        services.AddSingleton<OrbitalElementsRefreshJob>();
        services.AddSingleton<OrbitalElementsAutomationAdapter>();
        services.AddSingleton<ILocalAutomationPeriodicTaskAdapter>(static provider =>
            provider.GetRequiredService<OrbitalElementsAutomationAdapter>());
        services.AddSingleton<OrbitalElementsOperations>();
        services.AddSingleton<OrbitalElementsHealthEvaluator>();

        // Storage is always configured, so last-known-good stays readable while refresh is disabled or halted.
        services.AddSingleton<IOrbitalSnapshotReader>(static provider => new OrbitalSnapshotReader(
            provider.GetRequiredService<SqliteOrbitalElementsStore>(), configured: true,
            provider.GetRequiredService<ILogger<OrbitalSnapshotReader>>()));

        services.AddHostedService<OrbitalElementsInitializationService>();
        return services;
    }

    internal static OrbitalElementsStoreSettings CreateStoreSettings(CameraAgentHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.OrbitalElements;
        return new OrbitalElementsStoreSettings(
            Path.Combine(Path.GetFullPath(options.RawIngressRoot), StoreDirectoryName),
            SqliteOrbitalElementsStore.MinimumProviderAttemptInterval,
            configured.MaximumRetainedSnapshots,
            TimeSpan.FromHours(configured.MinimumRetentionHours),
            configured.MaximumQuarantinedPayloads);
    }

    internal static CelesTrakGpClientSettings CreateClientSettings(OrbitalElementsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new CelesTrakGpClientSettings(
            TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
            options.MaximumWireBytes,
            CelesTrakGpSource.MaximumRawBytes,
            UserAgent);
    }

    /// <summary>
    /// <c>HVO.SkyMonitor.CameraAgent/{version}</c>, where the version is the assembly informational version without its
    /// build metadata. Falls back to the bare product token if the version is not a valid product version.
    /// </summary>
    internal static string UserAgent { get; } = CreateUserAgent(
        typeof(OrbitalElementsServiceCollectionExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static string CreateUserAgent(string? informationalVersion)
    {
        var version = informationalVersion;
        var metadata = version?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        if (metadata >= 0)
        {
            version = version![..metadata];
        }
        if (string.IsNullOrWhiteSpace(version))
        {
            return ProductToken;
        }
        var candidate = string.Concat(ProductToken, "/", version);
        return CelesTrakGpClient.IsValidUserAgent(candidate) && !version.Any(char.IsWhiteSpace) ? candidate : ProductToken;
    }
}
