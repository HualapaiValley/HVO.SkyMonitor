using System;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "MSTest test classes must be public.")]
[TestClass]
[TestCategory("Unit")]
public class SkyMonitorClientOptionsTests
{
    [TestMethod]
    public void ResolveBaseUriReturnsParsedUriWhenValid()
    {
        var options = new SkyMonitorClientOptions
        {
            BaseUrl = new Uri("https://example.com:5174/api", UriKind.Absolute)
        };

        var uri = options.ResolveBaseUri();

        Assert.AreEqual("https://example.com:5174/api", uri.ToString());
    }

    [TestMethod]
    public void TryResolveBaseUriReturnsFalseWhenInvalid()
    {
        var options = new SkyMonitorClientOptions
        {
            BaseUrl = new Uri("relative", UriKind.Relative)
        };

        var success = options.TryResolveBaseUri(out var uri);

        Assert.IsFalse(success);
        Assert.IsNull(uri);
    }

    [TestMethod]
    public void PublicBaseUriIsRequiredAndDoesNotFallBackToTransportUrl()
    {
        var options = new SkyMonitorClientOptions
        {
            BaseUrl = new Uri("http://logichost:8080", UriKind.Absolute)
        };

        Assert.IsFalse(options.TryResolvePublicBaseUri(out var missing));
        Assert.IsNull(missing);

        options.PublicBaseUrl = new Uri("https://logic.example", UriKind.Absolute);
        Assert.AreEqual("https://logic.example/", options.ResolvePublicBaseUri().ToString());
    }

    [TestMethod]
    public void ConfigurationRegistrationDisablesAutomaticRedirects()
    {
        var services = CreateServices();
        services.AddSkyMonitorApiClient(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SkyMonitor:BaseUrl"] = "https://central.test",
                ["SkyMonitor:PublicBaseUrl"] = "https://central.test"
            })
            .Build());

        AssertRedirectsDisabled(services);
    }

    [TestMethod]
    public void DelegateRegistrationDisablesAutomaticRedirects()
    {
        var services = CreateServices();
        services.AddSkyMonitorApiClient(options =>
        {
            options.BaseUrl = new Uri("https://central.test");
            options.PublicBaseUrl = new Uri("https://central.test");
        });

        AssertRedirectsDisabled(services);
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<ICentralAuthenticationService>());
        return services;
    }

    private static void AssertRedirectsDisabled(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(SkyMonitorClientOptions.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler;
        }
        Assert.IsInstanceOfType<HttpClientHandler>(handler);
        Assert.IsFalse(((HttpClientHandler)handler).AllowAutoRedirect);
    }
}
