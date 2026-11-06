using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
public sealed class RateLimiterLifetimeTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void ShutdownDisposesBothLimitersAcrossSharedAndCyclicDelegatePaths()
    {
        using var first = CreateLimiter();
        using var second = CreateLimiter();
        var head = new MiddlewareNode(first);
        var tail = new MiddlewareNode(second);
        head.Next = tail.Invoke;
        tail.Next = head.Invoke;
        using var lifetime = new RateLimiterLifetime();
        lifetime.Capture(head.Invoke);
        lifetime.Capture(tail.Invoke);
        lifetime.CapturedCount.Should().Be(2);
        first.AttemptAcquire(new DefaultHttpContext()).IsAcquired.Should().BeTrue();
        lifetime.Dispose();
        lifetime.Dispose();
        var acquireFirst = () => first.AttemptAcquire(new DefaultHttpContext());
        var acquireSecond = () => second.AttemptAcquire(new DefaultHttpContext());
        acquireFirst.Should().Throw<ObjectDisposedException>();
        acquireSecond.Should().Throw<ObjectDisposedException>();
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task DisposedDerivedHostsReleaseTheirServiceProviders()
    {
        var references = new List<WeakReference>();
        for (var index = 0; index < 3; index++)
        {
            references.Add(await Task.Run(static () =>
            {
                using var factory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(_ => { });
                var services = factory.Services;
                services.GetRequiredService<RateLimiterLifetime>().CapturedCount.Should().Be(2,
                    "the pinned framework has a global and endpoint limiter; changes must update the lifetime guard");
                return new WeakReference(services);
            }).ConfigureAwait(false));
        }
        for (var attempt = 0; attempt < 10 && references.Any(reference => reference.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100).ConfigureAwait(false);
        }
        references.Should().OnlyContain(reference => !reference.IsAlive,
            "disposed test hosts must not remain rooted by rate-limiter timers");
    }

    private static PartitionedRateLimiter<HttpContext> CreateLimiter() =>
        PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetNoLimiter("test"));

    private sealed class MiddlewareNode(PartitionedRateLimiter<HttpContext> limiter)
    {
        private readonly PartitionedRateLimiter<HttpContext> ownedLimiter = limiter;
        public RequestDelegate? Next;
        public Task Invoke(HttpContext context)
        {
            using var lease = ownedLimiter.AttemptAcquire(context);
            return Next?.Invoke(context) ?? Task.CompletedTask;
        }
    }
}
