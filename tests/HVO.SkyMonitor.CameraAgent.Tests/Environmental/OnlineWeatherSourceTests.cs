using System.Net;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Unit")]
public sealed class OnlineWeatherSourceTests
{
    private const string Nws = """{"properties":{"timestamp":"2026-11-08T06:00:00Z","temperature":{"value":20,"unitCode":"wmoUnit:degC"},"relativeHumidity":{"value":40,"unitCode":"wmoUnit:percent"},"barometricPressure":{"value":90000,"unitCode":"wmoUnit:Pa"},"windSpeed":{"value":36,"unitCode":"wmoUnit:km_h-1"},"windDirection":{"value":180,"unitCode":"wmoUnit:degree_(angle)"}}}""";
    private const string Hvo = """{"dateTime":1794117600,"outTemp":68,"outHumidity":40,"windSpeed":10,"windDir":180,"barometer":30.0}""";

    [TestMethod]
    public async Task CanceledProviderAttemptInvalidatesCacheAndPreservesMinimumSpacing()
    {
        var clock = new Clock();
        using var handler = new CancelingHandler();
        using var client = new WeatherProviderClient(new HttpClient(handler), clock);
        var source = Create(client);
        Assert.IsNotNull((await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false)).Fact);
        clock.Now = clock.Now.AddSeconds(300);
        using (var cancellation = new CancellationTokenSource(30))
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context() with { ObservedAtUtc = clock.Now }, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        clock.Now = clock.Now.AddSeconds(1);
        var result = await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context() with { ObservedAtUtc = clock.Now }, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(result.Fact);
        Assert.AreEqual(2, handler.Calls);
    }

    private sealed class CancelingHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++Calls == 2) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return Response(200, Nws);
        }
    }

    [TestMethod]
    public async Task RetryAfterNeverShortensTheProviderMinimumInterval()
    {
        var clock = new Clock();
        using var handler = new FakeHandler(_ =>
        {
            var response = Response(429);
            response.Headers.Remove("Retry-After");
            response.Headers.TryAddWithoutValidation("Retry-After", "1");
            return response;
        });
        using var client = new WeatherProviderClient(new HttpClient(handler), clock);
        var temperature = Create(client);
        var humidity = Create(client, kind: EnvironmentalObservationKind.RelativeHumidity);
        await temperature.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
        clock.Now = clock.Now.AddSeconds(60);
        await humidity.AcquireAsync(EnvironmentalHardwareAdapterTests.Context() with { ObservedAtUtc = clock.Now }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow(WeatherProvider.Nws, EnvironmentalObservationKind.AirTemperature, 20)]
    [DataRow(WeatherProvider.Nws, EnvironmentalObservationKind.WindSpeed, 10)]
    [DataRow(WeatherProvider.Hvo, EnvironmentalObservationKind.AirTemperature, 20)]
    [DataRow(WeatherProvider.Hvo, EnvironmentalObservationKind.WindSpeed, 4.4704)]
    [DataRow(WeatherProvider.Hvo, EnvironmentalObservationKind.SeaLevelPressure, 101591.2578)]
    public async Task ConvertsProviderFieldsWithImportedProvenance(WeatherProvider provider, EnvironmentalObservationKind kind, double expected)
    {
        using var handler = new FakeHandler(_ => Response(200, provider == WeatherProvider.Nws ? Nws : Hvo));
        using var client = new WeatherProviderClient(new HttpClient(handler), new Clock());
        var source = Create(client, provider, kind);
        var result = await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalSourceAcquisitionOutcome.Produced, result.Outcome, result.Reason);
        Assert.AreEqual(expected, result.Fact!.Value.NumericValue!.Value, 0.00001);
        Assert.AreEqual(EnvironmentalObservationSourceKind.Imported, result.Fact.Source.Kind);
        Assert.AreEqual(EnvironmentalHardwareAdapterTests.Epoch, result.Fact.ObservedAtUtc);
        Assert.IsFalse(Encoding.UTF8.GetString(EnvironmentalObservationFactJson.Serialize(result.Fact)).Contains("https://", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ConditionalFetchRetainsProviderTimeAndDoesNotFetchPerKindOrExposure()
    {
        var clock = new Clock();
        using var handler = new FakeHandler(call => call == 1 ? Response(200, Nws) : Response(304));
        using var client = new WeatherProviderClient(new HttpClient(handler), clock);
        var source = Create(client);
        var first = await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
        var other = await Create(client, kind: EnvironmentalObservationKind.RelativeHumidity).AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, handler.Calls);
        Assert.IsNotNull(other.Fact);
        clock.Now = clock.Now.AddSeconds(300);
        var second = await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context() with { ObservedAtUtc = clock.Now }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.Fact!.ObservedAtUtc, second.Fact!.ObservedAtUtc);
        Assert.AreEqual(2, handler.Calls);
        Assert.IsTrue(handler.Conditional);
    }

    [TestMethod]
    [DataRow(429, "provider-rate-limited")]
    [DataRow(500, "provider-http-failure")]
    [DataRow(503, "provider-http-failure")]
    public async Task FailureInvalidatesCurrentReadingAndBackoffIsRespected(int status, string reason)
    {
        var clock = new Clock();
        using var handler = new FakeHandler(call => call == 1 ? Response(200, Nws) : Response(status));
        using var client = new WeatherProviderClient(new HttpClient(handler), clock);
        var source = Create(client);
        Assert.IsNotNull((await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false)).Fact);
        clock.Now = clock.Now.AddSeconds(300);
        var failed = await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context() with { ObservedAtUtc = clock.Now }, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(failed.Fact);
        Assert.AreEqual(reason, failed.Reason);
        Assert.IsNull((await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context() with { ObservedAtUtc = clock.Now.AddSeconds(1) }, CancellationToken.None).ConfigureAwait(false)).Fact);
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    [DataRow("malformed")]
    [DataRow("oversized")]
    [DataRow("stale")]
    [DataRow("future")]
    [DataRow("network")]
    [DataRow("timeout")]
    [DataRow("missing")]
    [DataRow("wrong-unit")]
    public async Task RejectsInvalidResponsesWithoutCurrentOrDefaultValues(string scenario)
    {
        using var handler = new FakeHandler(_ => scenario switch
        {
            "network" => throw new HttpRequestException("redaction-host-and-key"),
            "timeout" => throw new TaskCanceledException("redaction-host-and-key", new TimeoutException()),
            "malformed" => Response(200, "not-json"),
            "oversized" => Response(200, new string('x', 65537)),
            "stale" => Response(200, Nws.Replace("06:00:00", "03:00:00", StringComparison.Ordinal)),
            "future" => Response(200, Nws.Replace("06:00:00", "07:00:00", StringComparison.Ordinal)),
            "missing" => Response(200, Nws.Replace("20,", "null,", StringComparison.Ordinal)),
            "wrong-unit" => Response(200, Nws.Replace("wmoUnit:degC", "kelvin", StringComparison.Ordinal)),
            _ => Response(500)
        });
        using var client = new WeatherProviderClient(new HttpClient(handler), new Clock());
        var result = await Create(client).AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(result.Fact);
        Assert.IsFalse(result.Reason.Contains("redaction", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HvoMissingFieldsAndSeaLevelPressureNeverBecomeStationPressure()
    {
        using var handler = new FakeHandler(_ => Response(200, Hvo));
        using var client = new WeatherProviderClient(new HttpClient(handler), new Clock());
        foreach (var kind in new[] { EnvironmentalObservationKind.AtmosphericPressure, EnvironmentalObservationKind.WindGust, EnvironmentalObservationKind.PrecipitationRate, EnvironmentalObservationKind.CloudCover })
            Assert.IsNull((await Create(client, WeatherProvider.Hvo, kind).AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false)).Fact);
    }

    [TestMethod]
    public void OnlineProviderRejectsCaptureTriggersAndUnconfirmedHvoProfile()
    {
        using var handler = new FakeHandler(_ => Response(200, Nws));
        using var client = new WeatherProviderClient(new HttpClient(handler), new Clock());
        Assert.ThrowsExactly<ArgumentException>(() => new OnlineWeatherSource(EnvironmentalHardwareAdapterTests.Descriptor("OnlineWeather", EnvironmentalObservationKind.AirTemperature), new(WeatherProvider.Nws, "KIGM"), client));
        Assert.ThrowsExactly<ArgumentException>(() => Create(client, WeatherProvider.Hvo, confirmed: false));
    }

    internal static OnlineWeatherSource Create(WeatherProviderClient client, WeatherProvider provider = WeatherProvider.Nws,
        EnvironmentalObservationKind kind = EnvironmentalObservationKind.AirTemperature, bool confirmed = true)
    {
        var descriptor = EnvironmentalHardwareAdapterTests.Descriptor("OnlineWeather", kind) with
        {
            Triggers = [EnvironmentalAcquisitionTrigger.Periodic],
            PeriodSeconds = provider == WeatherProvider.Nws ? 300 : 60,
            StaleAfterSeconds = provider == WeatherProvider.Nws ? 5400 : 300,
            ValidForSeconds = provider == WeatherProvider.Nws ? 7200 : 900
        };
        return new(descriptor, new(provider, "KIGM", provider == WeatherProvider.Hvo ? new Uri("https://weather.example/current.json") : null, confirmed), client);
    }
    internal sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = EnvironmentalHardwareAdapterTests.Epoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal sealed class FakeHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal bool Conditional { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Conditional = request.Headers.Contains("If-None-Match");
            return Task.FromResult(respond(++Calls));
        }
    }
    internal static HttpResponseMessage Response(int code, string body = "")
    {
        var response = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("ETag", "\"fixture-v1\"");
        response.Headers.TryAddWithoutValidation("Retry-After", "600");
        return response;
    }
}
