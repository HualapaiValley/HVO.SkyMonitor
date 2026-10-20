using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>
/// The orbital feature as <c>AddCameraAgentInfrastructure</c> registers it: one shared store behind every contract, the
/// automation adapter in the periodic registry, the store initializer ahead of the automation runner, and a named
/// CelesTrak client with no retry, redirect or logging handler.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalElementsCompositionTests : OrbitalStoreTestBase
{
    private static readonly string[] OnlyLifetimeWrapper = ["LifetimeTrackingHttpMessageHandler"];

    [TestMethod]
    public async Task InfrastructureResolvesOneSharedOrbitalGraph()
    {
        var provider = Build(CreateRoot());
        await using var lifetime = provider.ConfigureAwait(false);

        var store = provider.GetRequiredService<SqliteOrbitalElementsStore>();
        Assert.AreSame(store, provider.GetRequiredService<IOrbitalElementsStore>());
        Assert.IsInstanceOfType<OrbitalSnapshotReader>(provider.GetRequiredService<IOrbitalSnapshotReader>());
        Assert.AreSame(provider.GetRequiredService<IOrbitalSnapshotReader>(), provider.GetRequiredService<IOrbitalSnapshotReader>());
        Assert.IsInstanceOfType<CelesTrakGpClient>(provider.GetRequiredService<IOrbitalElementsProviderClient>());
        var adapter = provider.GetRequiredService<OrbitalElementsAutomationAdapter>();
        Assert.AreSame(adapter, provider.GetServices<ILocalAutomationPeriodicTaskAdapter>()
            .Single(static item => item.TaskKind == LocalAutomationTaskKind.OrbitalElementsRefresh));
        Assert.IsNotNull(provider.GetRequiredService<OrbitalElementsRefreshJob>());
        Assert.IsNotNull(provider.GetRequiredService<OrbitalElementsOperations>());
        Assert.IsNotNull(provider.GetRequiredService<OrbitalElementsHealthEvaluator>());
        Assert.IsNotNull(provider.GetRequiredService<OrbitalElementsTelemetry>());

        var descriptor = provider.GetRequiredService<ILocalAutomationTaskRegistry>().Describe()
            .Single(static item => item.TaskKind == LocalAutomationTaskKind.OrbitalElementsRefresh);
        CollectionAssert.AreEqual(new[] { CelesTrakGpSource.DatasetKey }, descriptor.Targets.ToArray());
    }

    [TestMethod]
    public async Task StoreLivesInTheReservedDirectoryUnderTheRawIngressRoot()
    {
        var root = CreateRoot();
        var provider = Build(root);
        await using var lifetime = provider.ConfigureAwait(false);
        var availability = provider.GetRequiredService<OrbitalElementsAvailability>();

        Assert.AreEqual(OrbitalElementsAvailabilityState.Available,
            await availability.EnsureAvailableAsync(CancellationToken.None).ConfigureAwait(false));

        Assert.IsTrue(File.Exists(Path.Combine(root, ".orbital-elements", SqliteOrbitalElementsStore.DatabaseFileName)));
        Assert.AreEqual(Path.Combine(Path.GetFullPath(root), ".orbital-elements"),
            OrbitalElementsServiceCollectionExtensions.CreateStoreSettings(
                new CameraAgentHostOptions { RawIngressRoot = root }).RootDirectory);
    }

    [TestMethod]
    public void StoreAndClientSettingsComeFromTheBoundOptions()
    {
        var options = new CameraAgentHostOptions
        {
            RawIngressRoot = "relative-ingress",
            OrbitalElements = new OrbitalElementsOptions
            {
                RequestTimeoutSeconds = 7,
                MaximumWireBytes = 4096,
                MaximumRetainedSnapshots = 5,
                MinimumRetentionHours = 30,
                MaximumQuarantinedPayloads = 2
            }
        };

        var store = OrbitalElementsServiceCollectionExtensions.CreateStoreSettings(options);
        var client = OrbitalElementsServiceCollectionExtensions.CreateClientSettings(options.OrbitalElements);

        Assert.IsTrue(Path.IsPathFullyQualified(store.RootDirectory));
        Assert.AreEqual(".orbital-elements", Path.GetFileName(store.RootDirectory));
        Assert.AreEqual(SqliteOrbitalElementsStore.MinimumProviderAttemptInterval, store.MinimumAttemptInterval);
        Assert.AreEqual(5, store.MaximumRetainedSnapshots);
        Assert.AreEqual(TimeSpan.FromHours(30), store.MinimumRetention);
        Assert.AreEqual(2, store.MaximumQuarantinedPayloads);
        Assert.AreEqual(TimeSpan.FromSeconds(7), client.RequestTimeout);
        Assert.AreEqual(4096, client.MaximumWireBytes);
        Assert.AreEqual(CelesTrakGpSource.MaximumRawBytes, client.MaximumRawBytes);
        Assert.AreEqual(OrbitalElementsServiceCollectionExtensions.UserAgent, client.UserAgent);
    }

    [TestMethod]
    public async Task StoreInitializerIsRegisteredBeforeTheAutomationRunner()
    {
        var services = Services(CreateRoot());
        var provider = services.BuildServiceProvider();
        await using var lifetime = provider.ConfigureAwait(false);

        var hosted = services.Where(static item => item.ServiceType == typeof(IHostedService))
            .Select(static item => item.ImplementationType)
            .ToList();
        var initializer = hosted.IndexOf(typeof(OrbitalElementsInitializationService));
        var runner = hosted.IndexOf(typeof(LocalAutomationRunnerService));

        Assert.IsGreaterThanOrEqualTo(0, initializer);
        Assert.IsGreaterThan(initializer, runner, "The store must be initialized before the runner's first sweep.");
        Assert.AreEqual(1, hosted.Count(static type => type == typeof(OrbitalElementsInitializationService)));
    }

    [TestMethod]
    public async Task NamedClientHasNoRetryRedirectCookieOrLoggingHandler()
    {
        var provider = Build(CreateRoot());
        await using var lifetime = provider.ConfigureAwait(false);
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        var handler = factory.CreateHandler(CelesTrakGpSource.HttpClientName);
        var delegating = new List<string>();
        while (handler is DelegatingHandler outer)
        {
            delegating.Add(outer.GetType().Name);
            handler = outer.InnerHandler!;
        }

        CollectionAssert.AreEqual(OnlyLifetimeWrapper, delegating,
            "Only the factory's lifetime wrapper may sit in front of the primary handler: " + string.Join(", ", delegating));
        var primary = (SocketsHttpHandler)handler;
        Assert.IsFalse(primary.AllowAutoRedirect);
        Assert.IsFalse(primary.UseCookies);
        Assert.AreEqual(System.Net.DecompressionMethods.None, primary.AutomaticDecompression);
        Assert.AreEqual(1, primary.MaxConnectionsPerServer);
        Assert.IsNull(primary.ActivityHeadersPropagator);
        Assert.AreEqual(0, primary.MaxResponseDrainSize);
    }

    [TestMethod]
    public async Task NamedClientHasAnInfiniteTimeoutAndNoDefaultHeaders()
    {
        var provider = Build(CreateRoot());
        await using var lifetime = provider.ConfigureAwait(false);

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(CelesTrakGpSource.HttpClientName);

        Assert.AreEqual(Timeout.InfiniteTimeSpan, client.Timeout, "The client's TimeProvider deadline governs the request.");
        Assert.IsNull(client.BaseAddress);
        Assert.IsEmpty(client.DefaultRequestHeaders);
    }

    [TestMethod]
    [DataRow(null, "HVO.SkyMonitor.CameraAgent")]
    [DataRow("", "HVO.SkyMonitor.CameraAgent")]
    [DataRow("   ", "HVO.SkyMonitor.CameraAgent")]
    [DataRow("+0123abcd", "HVO.SkyMonitor.CameraAgent")]
    [DataRow("1.2.3", "HVO.SkyMonitor.CameraAgent/1.2.3")]
    [DataRow("1.2.3+0123abcd", "HVO.SkyMonitor.CameraAgent/1.2.3")]
    [DataRow("1.2.3-beta.1+sha.0123abcd", "HVO.SkyMonitor.CameraAgent/1.2.3-beta.1")]
    [DataRow("1.0 beta", "HVO.SkyMonitor.CameraAgent")]
    [DataRow("1.0/2", "HVO.SkyMonitor.CameraAgent")]
    [DataRow("1.0é", "HVO.SkyMonitor.CameraAgent")]
    [DataRow("1.0\t", "HVO.SkyMonitor.CameraAgent")]
    public void UserAgentIsTheProductTokenAndAVersionWithoutBuildMetadata(string? informationalVersion, string expected)
        => Assert.AreEqual(expected, OrbitalElementsServiceCollectionExtensions.CreateUserAgent(informationalVersion));

    [TestMethod]
    public void AssemblyUserAgentIsValidAndCarriesNoSourceRevision()
    {
        var userAgent = OrbitalElementsServiceCollectionExtensions.UserAgent;

        Assert.IsTrue(CelesTrakGpClient.IsValidUserAgent(userAgent), userAgent);
        Assert.StartsWith(OrbitalElementsServiceCollectionExtensions.ProductToken, userAgent, StringComparison.Ordinal);
        Assert.DoesNotContain("+", userAgent, StringComparison.Ordinal);
    }

    private static ServiceCollection Services(string rawIngressRoot)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["CameraAgent:RawIngressRoot"] = rawIngressRoot,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
            }).Build());
        services.AddSingleton<IDeploymentLocationProtector>(new OrbitalPassThroughLocationProtector());
        return services;
    }

    private static ServiceProvider Build(string rawIngressRoot)
        => Services(rawIngressRoot).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
}

/// <summary>The host's Data Protection location protector, which the automation registry's graph needs, stood in for.</summary>
internal sealed class OrbitalPassThroughLocationProtector : IDeploymentLocationProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext;

    public byte[] Unprotect(byte[] protectedPayload) => protectedPayload;
}
