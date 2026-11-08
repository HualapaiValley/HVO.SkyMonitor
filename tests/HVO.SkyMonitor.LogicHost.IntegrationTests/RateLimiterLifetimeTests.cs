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

    [TestMethod]
    [TestCategory("Unit")]
    public void ShutdownAttemptsEveryLimiterAndAggregatesFailures()
    {
        var firstError = new InvalidOperationException("first-disposal-failure");
        var lastError = new IOException("last-disposal-failure");
        var first = new DisposalLimiter(firstError);
        var middle = new DisposalLimiter(null);
        var last = new DisposalLimiter(lastError);
        var head = new MiddlewareNode(first);
        var tail = new MiddlewareNode(last);
        head.Next = new MiddlewareNode(middle) { Next = tail.Invoke }.Invoke;
        tail.Next = head.Invoke;
        using var lifetime = new RateLimiterLifetime();
        lifetime.Capture(head.Invoke);
        var dispose = lifetime.Dispose;
        dispose.Should().Throw<AggregateException>().Which.InnerExceptions.Should().Contain(firstError).And.Contain(lastError).And.HaveCount(2);
        first.Attempts.Should().Be(1);
        middle.Attempts.Should().Be(1);
        last.Attempts.Should().Be(1);
        lifetime.CapturedCount.Should().Be(0);
        lifetime.Dispose();
        first.Attempts.Should().Be(1);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(1024, false)]
    [DataRow(1025, true)]
    public void CaptureHonorsTargetBoundAndTerminatesCycles(int targetCount, bool exceedsBound)
    {
        using var limiter = CreateLimiter();
        var nodes = Enumerable.Range(0, targetCount).Select(_ => new MiddlewareNode(limiter)).ToArray();
        for (var index = 0; index < nodes.Length; index++) nodes[index].Next = nodes[(index + 1) % nodes.Length].Invoke;
        using var lifetime = new RateLimiterLifetime();
        var capture = () => lifetime.Capture(nodes[0].Invoke);
        if (exceedsBound) capture.Should().Throw<InvalidOperationException>().WithMessage("*inspection bound*");
        else capture.Should().NotThrow();
        lifetime.CapturedCount.Should().Be(1);
        limiter.AttemptAcquire(new DefaultHttpContext()).IsAcquired.Should().BeTrue();
        lifetime.Dispose();
        var acquire = () => limiter.AttemptAcquire(new DefaultHttpContext());
        acquire.Should().Throw<ObjectDisposedException>();
    }

    private sealed class DisposalLimiter(Exception? failure) : PartitionedRateLimiter<HttpContext>
    {
        internal int Attempts { get; private set; }
        public override RateLimiterStatistics? GetStatistics(HttpContext resource) => null;
        protected override RateLimitLease AttemptAcquireCore(HttpContext resource, int permitCount) => throw new NotSupportedException();
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(HttpContext resource, int permitCount, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (!disposing) return;
                Attempts++;
                if (failure is not null) throw failure;
            }
            finally { base.Dispose(disposing); }
        }
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
