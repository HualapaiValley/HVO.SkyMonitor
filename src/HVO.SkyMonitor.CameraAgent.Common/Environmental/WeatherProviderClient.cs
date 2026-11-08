using System.Collections.Concurrent;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public enum WeatherProvider { Nws, Hvo }
public sealed record OnlineWeatherSourceOptions(WeatherProvider Provider, string StationId,
    Uri? HvoEndpoint = null, bool HvoStationConfirmed = false, double? StationDistanceKilometers = null, double? StationElevationMeters = null);
public sealed record WeatherProviderSample(ReadOnlyMemory<byte> Body, DateTimeOffset SampledAtUtc, string Reason);

/// <summary>One bounded fetch per station interval, shared across reading kinds. Failed fetches invalidate availability.</summary>
public sealed class WeatherProviderClient(HttpClient httpClient, TimeProvider timeProvider,
    IEnvironmentalAcquisitionStateStore? scheduleStore = null, string? root = null) : IDisposable
{
    private readonly BoundedHttpClient transport = new(httpClient, timeProvider, SwpcSource.UserAgent);
    private readonly ConcurrentDictionary<OnlineWeatherSourceOptions, State> states = new();

    internal void Register(OnlineWeatherSourceOptions options, EnvironmentalSourceDescriptor descriptor)
    {
        var state = states.GetOrAdd(options, static _ => new());
        state.Sources.TryAdd(descriptor.Id, descriptor);
    }

    public async ValueTask<WeatherProviderSample> ReadAsync(OnlineWeatherSourceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var state = states.GetOrAdd(options, static _ => new());
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var updateSchedule = false;
        try
        {
            if (!state.Initialized)
            {
                if (scheduleStore is not null && root is not null)
                {
                    var prior = await scheduleStore.ReadSourceStatesAsync(root, cancellationToken).ConfigureAwait(false);
                    state.NextAttempt = prior.Where(source => state.Sources.ContainsKey(source.SourceId))
                        .Select(source => source.NextPollUtc ?? DateTimeOffset.MinValue).DefaultIfEmpty().Max();
                }
                state.Initialized = true;
            }
            var now = timeProvider.GetUtcNow();
            if (now < state.NextAttempt) return state.Current ?? new(default, default, state.Reason);
            var uri = options.Provider == WeatherProvider.Nws
                ? new Uri($"https://api.weather.gov/stations/{options.StationId}/observations/latest") : options.HvoEndpoint!;
            var response = await transport.FetchAsync(uri, 65_536, "application/json", state.Validators, cancellationToken).ConfigureAwait(false);
            var interval = options.Provider == WeatherProvider.Nws ? 300 : 60;
            state.NextAttempt = now.AddSeconds(interval);
            updateSchedule = true;
            var previous = state.Current;
            state.Current = null;
            if (response.Failure != SwpcTransportFailure.None)
                return Fail(state, response.Failure == SwpcTransportFailure.Timeout ? "provider-timeout" : "provider-network-unavailable");
            if (response.StatusCode == 429)
            {
                state.NextAttempt = now.Add(SwpcRetryAfter.Resolve(response.RetryAfter, now));
                return Fail(state, "provider-rate-limited");
            }
            if (response.StatusCode == 304)
            {
                if (previous is null || state.Validators is null) return Fail(state, "provider-unusable-cache");
                return state.Current = previous;
            }
            if (response.StatusCode != 200) return Fail(state, "provider-http-failure");
            if (response.Oversize || response.BodyUndecodable ||
                !string.Equals(response.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(response.MediaType, "application/geo+json", StringComparison.OrdinalIgnoreCase))
                return Fail(state, "provider-invalid-body");
            try
            {
                using var json = EnvironmentalPayloadJson.Parse(response.Body);
                var root = json.RootElement;
                var sampled = options.Provider == WeatherProvider.Nws
                    ? root.GetProperty("properties").GetProperty("timestamp").GetDateTimeOffset().ToUniversalTime()
                    : DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("dateTime").GetInt64());
                if (sampled == default || sampled > now.AddSeconds(120)) return Fail(state, "provider-clock-skew");
                state.Validators = new(response.EntityTag, response.LastModified);
                state.Reason = "produced";
                return state.Current = new(response.Body, sampled, "produced");
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException or KeyNotFoundException)
            { return Fail(state, "provider-malformed"); }
        }
        finally
        {
            try
            {
                if (updateSchedule && scheduleStore is not null && root is not null)
                    foreach (var source in state.Sources.Values)
                        await scheduleStore.UpdateSourceScheduleAsync(root, source, state.NextAttempt, cancellationToken).ConfigureAwait(false);
            }
            finally { state.Gate.Release(); }
        }
    }
    private static WeatherProviderSample Fail(State state, string reason)
    {
        state.Reason = reason;
        state.Validators = null;
        return new(default, default, reason);
    }
    public void Dispose()
    {
        httpClient.Dispose();
        foreach (var state in states.Values) state.Gate.Dispose();
    }
    private sealed class State
    {
        internal ConcurrentDictionary<string, EnvironmentalSourceDescriptor> Sources { get; } = new(StringComparer.Ordinal);
        internal bool Initialized { get; set; }
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal DateTimeOffset NextAttempt { get; set; }
        internal WeatherProviderSample? Current { get; set; }
        internal string Reason { get; set; } = "provider-unavailable";
        internal SwpcRequestValidators? Validators { get; set; }
    }
}
