using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Policy;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;

/// <summary>External opt-in probe: exactly one GET per core product; no body is logged or persisted.</summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class SwpcLiveProbeTests
{
    [TestMethod]
    public async Task CoreProductsReturnAcceptedBodiesThroughProductionClientComposition()
    {
        if (Environment.GetEnvironmentVariable("HVO_ISSUE1398_LIVE_PROBE") != "1") Assert.Inconclusive("Live SWPC probe is opt-in.");
        var services = new ServiceCollection(); services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().Build());
        var provider = services.BuildServiceProvider(); await using var lifetime = provider.ConfigureAwait(false);
        var client = provider.GetRequiredService<ISwpcClient>(); var clock = provider.GetRequiredService<TimeProvider>();
        var view = SwpcPolicyTestStoreDouble.EmptyView();
        foreach (var product in Enum.GetValues<SwpcProduct>())
        {
            var start = SwpcPolicy.Admit(view, product, SwpcAttemptTrigger.Manual, "manual-live-probe", Guid.NewGuid(), clock.GetUtcNow()).Start!;
            var response = await client.FetchAsync(product, null, default).ConfigureAwait(false);
            Assert.AreEqual(200, response.StatusCode); Assert.AreEqual(SwpcSource.MediaType(product), response.MediaType);
            var classified = SwpcPolicy.Classify(start, view.Products[product], response, clock.GetUtcNow());
            Assert.AreEqual(SwpcFetchOutcome.Updated, classified.Outcome);
        }
    }
}
