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
    private readonly ConcurrentDictionary<string, State> states = new(StringComparer.Ordinal);

    internal static string RequestIdentity(OnlineWeatherSourceOptions options)
        => HVO.SkyMonitor.AgentCore.CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            options.Provider,
            Station = options.Provider == WeatherProvider.Nws ? options.StationId.ToUpperInvariant() : null,
            Endpoint = options.Provider == WeatherProvider.Hvo ? options.HvoEndpoint?.AbsoluteUri : null
        });

    private static string ScheduleId(OnlineWeatherSourceOptions options) => "__weather-provider-" + RequestIdentity(options);

    public async ValueTask<WeatherProviderSample> ReadAsync(OnlineWeatherSourceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var state = states.GetOrAdd(RequestIdentity(options), _ => new State(options));
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var updateSchedule = false;
        DateTimeOffset? persistedSchedule = null;
        try
        {
            if (!state.Initialized)
            {
                if (scheduleStore is not null && root is not null)
                {
                    var prior = await scheduleStore.ReadSourceStatesAsync(root, cancellationToken).ConfigureAwait(false);
                    state.NextAttempt = prior.SingleOrDefault(source => source.SourceId == state.Schedule.Id)?.NextPollUtc ?? DateTimeOffset.MinValue;
                }
                state.Initialized = true;
            }
            var now = timeProvider.GetUtcNow();
            if (now < state.NextAttempt) return state.Current ?? new(default, default, state.Reason);
            var uri = options.Provider == WeatherProvider.Nws
                ? new Uri($"https://api.weather.gov/stations/{options.StationId}/observations/latest") : options.HvoEndpoint!;
            var previous = state.Current;
            var validators = state.Validators;
            var interval = TimeSpan.FromSeconds(options.Provider == WeatherProvider.Nws ? 300 : 60);
            state.NextAttempt = now.Add(interval);
            state.Current = null;
            state.Validators = null;
            state.Reason = "provider-request-incomplete";
            // Record minimum spacing before sending: cancellation must not expose an old cache or permit another kind to retry immediately.
            await SaveScheduleAsync(state, cancellationToken).ConfigureAwait(false);
            persistedSchedule = state.NextAttempt;
            updateSchedule = true;
            var response = await transport.FetchAsync(uri, 65_536, "application/json", validators, cancellationToken).ConfigureAwait(false);
            if (response.Failure != SwpcTransportFailure.None)
                return Fail(state, response.Failure == SwpcTransportFailure.Timeout ? "provider-timeout" : "provider-network-unavailable");
            if (response.StatusCode == 429 || response.StatusCode >= 500 && response.RetryAfter is not null)
            {
                var retry = SwpcRetryAfter.Resolve(response.RetryAfter, now);
                state.NextAttempt = now.Add(retry > interval ? retry : interval);
                if (response.StatusCode == 429) return Fail(state, "provider-rate-limited");
            }
            if (response.StatusCode == 304)
            {
                if (previous is null || !response.ValidatorsSent) return Fail(state, "provider-unusable-cache");
                state.Validators = new(response.EntityTag ?? validators?.EntityTag, response.LastModified ?? validators?.LastModified);
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
                if (updateSchedule && state.NextAttempt != persistedSchedule)
                    await SaveScheduleAsync(state, CancellationToken.None).ConfigureAwait(false);
            }
            finally { state.Gate.Release(); }
        }
    }
    private async ValueTask SaveScheduleAsync(State state, CancellationToken cancellationToken)
    {
        if (scheduleStore is not null && root is not null)
            await scheduleStore.UpdateSourceScheduleAsync(root, state.Schedule, state.NextAttempt, cancellationToken).ConfigureAwait(false);
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
    private sealed class State(OnlineWeatherSourceOptions options)
    {
        internal EnvironmentalSourceDescriptor Schedule { get; } = new(ScheduleId(options), "OnlineWeatherProviderSchedule",
            HVO.SkyMonitor.Processing.EnvironmentalObservationKind.AirTemperature, false, [EnvironmentalAcquisitionTrigger.Periodic],
            DateTimeOffset.UnixEpoch, options.Provider == WeatherProvider.Nws ? 300 : 60, 1, 7200, 5400, null,
            JsonSerializer.SerializeToElement(new { }));
        internal bool Initialized { get; set; }
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal DateTimeOffset NextAttempt { get; set; }
        internal WeatherProviderSample? Current { get; set; }
        internal string Reason { get; set; } = "provider-unavailable";
        internal SwpcRequestValidators? Validators { get; set; }
    }
}
