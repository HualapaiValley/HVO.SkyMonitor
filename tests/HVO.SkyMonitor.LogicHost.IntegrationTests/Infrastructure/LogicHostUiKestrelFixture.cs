using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

internal sealed class LogicHostUiKestrelFixture : IAsyncDisposable
{
    private readonly WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory;
    private readonly HttpClient lifetimeClient;

    private LogicHostUiKestrelFixture(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        HttpClient lifetimeClient,
        Uri baseAddress)
    {
        this.factory = factory;
        this.lifetimeClient = lifetimeClient;
        BaseAddress = baseAddress;
    }

    internal Uri BaseAddress { get; }
    internal IServiceProvider Services => factory.Services;

    internal static async Task<LogicHostUiKestrelFixture> CreateAsync(
        Issue107RuntimeEvidenceCollector? evidence = null,
        bool enableArtifactResponseThrottle = false)
    {
        var factory = AssemblyHooks.Fixture.CreateKestrelFactory(
            evidence is null ? null : options =>
            {
                options.EnableSensitiveDataLogging(false);
                options.AddInterceptors(evidence);
            },
            services =>
            {
                if (evidence is not null)
                {
                    services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(evidence);
                }
                if (enableArtifactResponseThrottle)
                {
                    services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter,
                        Issue107ArtifactResponseThrottleStartupFilter>();
                }
            });
        try
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var server = factory.Services.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault()
                ?? throw new InvalidOperationException("LogicHost Kestrel did not publish a loopback address.");
            var baseAddress = new Uri(address, UriKind.Absolute);
            client.BaseAddress = baseAddress;
            return new LogicHostUiKestrelFixture(factory, client, baseAddress);
        }
        catch
        {
            await factory.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetimeClient.Dispose();
        await factory.DisposeAsync().ConfigureAwait(false);
    }
}
