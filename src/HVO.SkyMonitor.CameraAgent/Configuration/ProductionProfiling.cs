using HVO.SkyMonitor.CameraAgent.Common.Diagnostics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace HVO.SkyMonitor.CameraAgent.Configuration;

internal enum ProductionProfilingMode
{
    Baseline,
    Enabled,
    Disabled
}

internal static class ProductionProfiling
{
    internal const string ConfigurationKey = "Telemetry:ProductionProfiling";
    private const int MaximumMetricStreams = 512;
    private const int MaximumPointsPerStream = 64;
    private static readonly double[] DurationSeconds = [0.001, 0.005, 0.025, 0.1, 0.25, 1, 5, 15, 60, 300];
    private static readonly double[] DurationMilliseconds = DurationSeconds.Select(static value => value * 1000).ToArray();

    internal static ProductionProfilingMode ReadMode(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        null or "" or "BASELINE" => ProductionProfilingMode.Baseline,
        "ENABLED" => ProductionProfilingMode.Enabled,
        "DISABLED" => ProductionProfilingMode.Disabled,
        _ => throw new InvalidOperationException("Telemetry:ProductionProfiling must be Baseline, Enabled, or Disabled.")
    };

    internal static void ConfigureMetrics(MeterProviderBuilder metrics)
    {
        metrics.AddMeter(CameraAgentWorkloadTelemetry.MeterName, "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Components.Server.Circuits")
            .SetMaxMetricStreams(MaximumMetricStreams)
            .SetExemplarFilter(ExemplarFilterType.AlwaysOff)
            .AddView(static instrument =>
            {
                if (instrument.Meter.Name == "Microsoft.AspNetCore.Hosting")
                {
                    return instrument.Name == "http.server.request.duration"
                        ? new ExplicitBucketHistogramConfiguration { Boundaries = DurationSeconds, TagKeys = ["http.response.status_code"], CardinalityLimit = MaximumPointsPerStream }
                        : MetricStreamConfiguration.Drop;
                }
                if (instrument.Meter.Name == "Microsoft.AspNetCore.Components.Server.Circuits")
                {
                    return instrument.Name switch
                    {
                        "aspnetcore.components.circuit.active" or "aspnetcore.components.circuit.connected" =>
                            new MetricStreamConfiguration { TagKeys = [], CardinalityLimit = MaximumPointsPerStream },
                        "aspnetcore.components.circuit.duration" =>
                            new ExplicitBucketHistogramConfiguration { Boundaries = DurationSeconds, TagKeys = [], CardinalityLimit = MaximumPointsPerStream },
                        _ => MetricStreamConfiguration.Drop
                    };
                }

                if (instrument.Name.EndsWith(".duration", StringComparison.Ordinal) ||
                    instrument.Name.EndsWith(".duration_ms", StringComparison.Ordinal))
                {
                    var boundaries = instrument.Unit switch
                    {
                        "s" => DurationSeconds,
                        "ms" => DurationMilliseconds,
                        _ => null
                    };
                    if (boundaries is not null)
                    {
                        return new ExplicitBucketHistogramConfiguration
                        {
                            Boundaries = boundaries,
                            CardinalityLimit = MaximumPointsPerStream
                        };
                    }
                }

                return new MetricStreamConfiguration { CardinalityLimit = MaximumPointsPerStream };
            });
    }

    internal static Sampler CreateSampler()
    {
        // Honor local decisions. An external caller cannot force additional sampling through traceparent.
        var off = new AlwaysOffSampler();
        return new ParentBasedSampler(new TraceIdRatioBasedSampler(0.01),
            remoteParentSampled: off, remoteParentNotSampled: off);
    }
}
