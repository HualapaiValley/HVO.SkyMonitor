using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace HVO.SkyMonitor.LogicHost.TestInfrastructure;

// ASP.NET Core 10's rate-limiting middleware does not dispose its endpoint or global
// partitioned limiter. Their timers otherwise retain every stopped test host.
// Keep this pinned-framework workaround in tests; do not replace rate-limit behavior.
internal sealed class RateLimiterLifetime : IDisposable
{
    private readonly HashSet<PartitionedRateLimiter<HttpContext>> limiters = new(ReferenceEqualityComparer.Instance);
    internal int CapturedCount => limiters.Count;

    internal void Capture(RequestDelegate pipeline)
    {
        var pending = new Stack<Delegate>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        pending.Push(pipeline);
        while (pending.TryPop(out var callback))
        {
            foreach (var invocation in callback.GetInvocationList())
            {
                if (invocation.Target is not { } target || !visited.Add(target)) continue;
                if (visited.Count > 1024) throw new InvalidOperationException("Test middleware graph exceeds the lifetime inspection bound.");
                for (var type = target.GetType(); type is not null; type = type.BaseType)
                {
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (typeof(PartitionedRateLimiter<HttpContext>).IsAssignableFrom(field.FieldType)
                            && field.GetValue(target) is PartitionedRateLimiter<HttpContext> limiter)
                            limiters.Add(limiter);
                        else if (typeof(Delegate).IsAssignableFrom(field.FieldType)
                            && field.GetValue(target) is Delegate next)
                            pending.Push(next);
                    }
                }
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Attempt shutdown of every owned limiter, then propagate all disposal failures.")]
    public void Dispose()
    {
        List<Exception>? errors = null;
        foreach (var limiter in limiters)
        {
            try { limiter.Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        limiters.Clear();
        if (errors is not null) throw new AggregateException("Test rate-limiter shutdown failed.", errors);
    }
}

internal sealed class RateLimiterLifetimeStartupFilter(RateLimiterLifetime lifetime) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(pipeline =>
        {
            lifetime.Capture(pipeline);
            return pipeline;
        });
        next(app);
    };
}
