using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;

[TestClass]
[TestCategory("Unit")]
public sealed class SpaceWeatherCompositionTests
{
    [TestMethod]
    public async Task InfrastructureResolvesOneSharedGraphAndInitializesBeforeTheRunnerEvenWhenDisabled()
    {
        using var host = new SpaceWeatherTestHost(false);
        var services = new ServiceCollection(); services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["CameraAgent:RawIngressRoot"] = host.Root }).Build());
        services.AddSingleton<IDeploymentLocationProtector>(new Protector());
        var provider = services.BuildServiceProvider();
        await using var lifetime = provider.ConfigureAwait(false);
        Assert.AreSame(provider.GetRequiredService<SqliteSwpcStore>(), provider.GetRequiredService<ISwpcStore>());
        foreach (var type in new[] { typeof(SpaceWeatherRefreshJob), typeof(SpaceWeatherOperations), typeof(SpaceWeatherAvailability),
            typeof(SpaceWeatherHealthEvaluator), typeof(SpaceWeatherTelemetry), typeof(SwpcAttemptRunner) })
            Assert.IsNotNull(provider.GetRequiredService(type));
        Assert.AreEqual(1, provider.GetServices<ILocalAutomationPeriodicTaskAdapter>().Count(static adapter => adapter.TaskKind == LocalAutomationTaskKind.SpaceWeatherRefresh));
        var hosted = services.Where(static s => s.ServiceType == typeof(IHostedService)).Select(static s => s.ImplementationType).ToList();
        var initializer = hosted.IndexOf(typeof(SpaceWeatherInitializationService));
        Assert.IsTrue(initializer >= 0 && initializer < hosted.IndexOf(typeof(LocalAutomationRunnerService)));
        using var service = ActivatorUtilities.CreateInstance<SpaceWeatherInitializationService>(provider);
        await service.StartAsync(default).ConfigureAwait(false); await service.StopAsync(default).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(Path.Combine(host.Root, ".space-weather", "space-weather.db")));
        Assert.IsFalse(provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.SpaceWeather.Enabled);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(SwpcSource.HttpClientName);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, client.Timeout); Assert.IsEmpty(client.DefaultRequestHeaders);
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(SwpcSource.HttpClientName);
        var wrappers = new List<string>();
        while (handler is DelegatingHandler outer) { wrappers.Add(outer.GetType().Name); handler = outer.InnerHandler!; }
        string[] expected = ["LifetimeTrackingHttpMessageHandler"];
        CollectionAssert.AreEqual(expected, wrappers);
        var primary = (SocketsHttpHandler)handler;
        Assert.IsFalse(primary.AllowAutoRedirect); Assert.IsFalse(primary.UseCookies); Assert.AreEqual(1, primary.MaxConnectionsPerServer);
        Assert.AreEqual(TimeSpan.FromSeconds(10), primary.ConnectTimeout); Assert.IsNull(primary.ActivityHeadersPropagator);
    }

    [TestMethod]
    public void OptionsDefaultToAllCoreProductsAndRejectEmptyDuplicateAndUndefinedProducts()
    {
        var options = new SpaceWeatherOptions(); Assert.IsFalse(options.Enabled);
        CollectionAssert.AreEqual(Enum.GetValues<SwpcProduct>(), options.Products.ToArray());
        foreach (var products in new[] { Array.Empty<SwpcProduct>(), new[] { SwpcProduct.Alerts, SwpcProduct.Alerts }, new[] { (SwpcProduct)99 } })
        {
            var candidate = new SpaceWeatherOptions { Products = products };
            Assert.IsFalse(Validator.TryValidateObject(candidate, new ValidationContext(candidate), [], true));
        }
    }

    [TestMethod]
    public void OvationCannotBindAtStartup()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CameraAgent:SpaceWeather:Products:0"] = "Ovation", ["CameraAgent:RawIngressRoot"] = "ingress" }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddCameraAgentInfrastructure(config);
        using var provider = services.BuildServiceProvider();
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value);
    }

    [TestMethod]
    public void ConfiguredProductMembershipReplacesDefaultsWithoutAppending()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["CameraAgent:SpaceWeather:Products:0"] = "Scales", ["CameraAgent:RawIngressRoot"] = "ingress" }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddCameraAgentInfrastructure(config);
        using var provider = services.BuildServiceProvider();
        var products = provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.SpaceWeather.Products;
        Assert.AreEqual(1, products.Count); Assert.AreEqual(SwpcProduct.Scales, products[0]);
    }

    [TestMethod]
    [DataRow("appsettings.json")]
    [DataRow("appsettings.StandaloneW6.json")]
    [DataRow("appsettings.StandaloneProductionSmoke.json")]
    public void CommittedHostConfigurationsDisableSpaceWeather(string file)
    {
        var root = FindRoot();
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "src", "HVO.SkyMonitor.CameraAgent", file)).Build();
        var options = config.GetSection("CameraAgent:SpaceWeather").Get<SpaceWeatherOptions>();
        Assert.IsNotNull(options); Assert.IsFalse(options.Enabled);
    }

    internal static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx"))) return directory.FullName;
        throw new AssertFailedException("Repository root unavailable.");
    }
    private sealed class Protector : IDeploymentLocationProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] protectedPayload) => protectedPayload;
    }
}
