using System.Diagnostics.Metrics;
using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class AuthenticationMetricsTests
{
    [TestMethod]
    public void RecordTokenRequest_UsesOnlyBoundedLabels()
    {
        using var meter = new Meter("authentication-metrics-tests");
        using var listener = new MeterListener();
        KeyValuePair<string, object?>[]? observedTags = null;

        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter == meter && instrument.Name == "auth.token_requests")
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => observedTags = tags.ToArray());
        listener.Start();

        var metrics = new AuthenticationMetrics(meter);
        metrics.RecordTokenRequest("client_credentials", success: true, durationMs: 12.5);

        Assert.IsNotNull(observedTags);
        CollectionAssert.AreEquivalent(
            new[] { "grant_type", "result" },
            observedTags.Select(static tag => tag.Key).ToArray());
    }

    [TestMethod]
    public void RecordApiKeyAuthentication_UsesOnlyBoundedLabels()
    {
        using var meter = new Meter("api-key-metrics-tests");
        using var listener = new MeterListener();
        KeyValuePair<string, object?>[]? observedTags = null;

        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter == meter && instrument.Name == "auth.apikey_authentication")
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => observedTags = tags.ToArray());
        listener.Start();

        var metrics = new AuthenticationMetrics(meter);
        metrics.RecordApiKeyAuthentication(success: true, accessLevel: "Read");

        Assert.IsNotNull(observedTags);
        CollectionAssert.AreEquivalent(
            new[] { "result", "access_level" },
            observedTags.Select(static tag => tag.Key).ToArray());
    }
}
