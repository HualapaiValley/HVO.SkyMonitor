using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;

[TestClass]
[TestCategory("Unit")]
public sealed class SpaceWeatherHealthTests
{
    [TestMethod]
    public async Task SimultaneousHealthReasonsKeepTheDocumentedPrecedence()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        var productHalt = true; var providerHalt = true; var drift = true;
        host.Store.HealthTransform = health =>
        {
            var product = health.Products[SwpcProduct.Alerts];
            return health with
            {
                ProviderHalt = providerHalt ? new("forbidden", host.Clock.GetUtcNow()) : null,
                Products = health.Products.SetItem(SwpcProduct.Alerts, product with
                {
                    ProductHalt = productHalt ? new("not-found", host.Clock.GetUtcNow()) : null,
                    Checkpoint = product.Checkpoint with { ConsecutiveRejected = drift ? 3 : 0 }
                }),
                RawBodyBytes = 512L * 1024 * 1024 + 1
            };
        };
        Assert.AreEqual("space-weather.provider-halted", (await host.Health.EvaluateAsync(default).ConfigureAwait(false)).Description);
        providerHalt = false;
        Assert.AreEqual("space-weather.product-halted", (await host.Health.EvaluateAsync(default).ConfigureAwait(false)).Description);
        productHalt = false;
        Assert.AreEqual("space-weather.format-drift", (await host.Health.EvaluateAsync(default).ConfigureAwait(false)).Description);
        drift = false;
        Assert.AreEqual("space-weather.store-oversized", (await host.Health.EvaluateAsync(default).ConfigureAwait(false)).Description);
    }

    [TestMethod]
    [DataRow("provider-halted")]
    [DataRow("product-halted")]
    [DataRow("alerts-outdated")]
    [DataRow("format-drift")]
    [DataRow("record-drift")]
    [DataRow("store-oversized")]
    public async Task EveryBoundedHealthReasonIsDegraded(string reason)
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        host.Store.HealthTransform = health =>
        {
            var product = health.Products[SwpcProduct.Alerts];
            product = reason switch
            {
                "product-halted" => product with { ProductHalt = new("not-found", host.Clock.GetUtcNow()) },
                "alerts-outdated" => product with { Freshness = SwpcFreshness.Outdated },
                "format-drift" => product with { Checkpoint = product.Checkpoint with { ConsecutiveRejected = 3 } },
                "record-drift" => product with { Checkpoint = product.Checkpoint with { RecordDriftStampUtc = host.Clock.GetUtcNow() } },
                _ => product
            };
            return health with
            {
                Products = health.Products.SetItem(SwpcProduct.Alerts, product),
                ProviderHalt = reason == "provider-halted" ? new("forbidden", host.Clock.GetUtcNow()) : null,
                RawBodyBytes = reason == "store-oversized" ? 512L * 1024 * 1024 + 1 : 0
            };
        };
        var result = await host.Health.EvaluateAsync(default).ConfigureAwait(false);
        Assert.IsTrue(result.Degraded); Assert.AreEqual("space-weather." + reason, result.Description);
    }

    [TestMethod]
    public async Task DisabledIsHealthyUnlessStorageIsUnavailable()
    {
        using var host = new SpaceWeatherTestHost(false); await host.InitializeAsync().ConfigureAwait(false);
        var healthy = await host.Health.EvaluateAsync(default).ConfigureAwait(false);
        Assert.IsFalse(healthy.Degraded); Assert.AreEqual("disabled", healthy.Description);
        host.Store.ReadFailure = new SwpcStorageException("hidden");
        host.Availability.RecordFailure(host.Store.ReadFailure); host.Sqlite.Dispose();
        var unavailable = await host.Health.EvaluateAsync(default).ConfigureAwait(false);
        Assert.IsTrue(unavailable.Degraded); Assert.AreEqual("space-weather.store-unavailable", unavailable.Description);
    }

    [TestMethod]
    public async Task FirstSuccessOverdueUsesTheEarliestRetainedAttemptForThatConfiguredProduct()
    {
        using var host = new SpaceWeatherTestHost(products: [SwpcProduct.Alerts]); await host.InitializeAsync().ConfigureAwait(false);
        Assert.IsFalse((await host.Health.EvaluateAsync(default).ConfigureAwait(false)).Degraded);
        host.Client.Status = 503; await host.RunAsync().ConfigureAwait(false);
        host.Clock.Advance(TimeSpan.FromSeconds(3599)); Assert.IsFalse((await host.Health.EvaluateAsync(default).ConfigureAwait(false)).Degraded);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual("space-weather.first-success-overdue", (await host.Health.EvaluateAsync(default).ConfigureAwait(false)).Description);
        host.Clock.Advance(TimeSpan.FromDays(36));
        Assert.IsFalse((await host.Health.EvaluateAsync(default).ConfigureAwait(false)).Degraded, "Pruned history cannot support a first-attempt age claim.");
    }
}
