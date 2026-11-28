using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;

internal sealed class NightlySiloFixture(string root) : IDisposable
{
    private readonly ServiceProvider _provider = SiloInfrastructureFixture.OpenStore(root);

    internal SqliteNightlyProductStore OpenStore(IOptions<CameraAgentHostOptions> options, TimeProvider clock)
        => new(options, clock, _provider.GetRequiredService<SiloArtifactStorage>());

    internal SiloTestStore Objects => _provider.GetRequiredService<SiloTestStore>();

    internal async Task DamageAsync(Guid productId, string revision, bool missing = false)
    {
        var locator = await SiloInfrastructureFixture.ReadLocalLocatorAsync(_provider, productId, revision, CancellationToken.None)
            .ConfigureAwait(false);
        if (missing) Objects.Delete(locator);
        else Objects.Damage(locator);
    }

    public void Dispose() => _provider.Dispose();
}
