using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Conformance;

/// <summary>Projects only public store facts into the fixture contract's vocabulary.</summary>
internal static class SwpcScenarioView
{
    /// <summary>Uses the supplied scenario time and sites for all lifecycle and relevance projections.</summary>
    internal static JsonObject Observe(SwpcStoreView view, JsonNode scenario, DateTimeOffset at)
    {
        var notices = new JsonObject();
        foreach (var group in view.Notices.Groups)
        {
            var window = SwpcNoticeLifecycle.Evaluate(group, view.Notices.Groups, at);
            var ack = SwpcGroupBuilder.Acknowledgement(group, view.Notices.Acknowledgements);
            var notice = JsonSerializer.SerializeToNode(new
            {
                family = group.Family.Token(),
                level = group.Level,
                state = window.State.Token(),
                fromUtc = Utc(window.FromUtc),
                untilUtc = Utc(window.UntilUtc),
                endReason = window.EndReason?.Token(),
                mayBeOutOfDate = window.State is SwpcNoticeState.Pending or SwpcNoticeState.Active
                    && view.Products[SwpcProduct.Alerts].Freshness != SwpcFreshness.Fresh,
                latest = group.Latest.Identity,
                versions = group.Versions.Length,
                corrected = group.Versions.Any(v => v.Message.Header.Corrected),
                orphan = group.Orphan,
                acknowledged = ack is null ? null : new { @operator = ack.Operator, atUtc = Utc(ack.AtUtc) }
            })!.AsObject();
            var relevance = new JsonObject();
            foreach (var site in scenario["sites"]!.AsArray().Select(s => s!))
            {
                var location = site["location"];
                var phi = location is null ? (double?)null : GeomagneticLatitude.Compute(
                    location["latitudeDeg"]!.GetValue<double>(), location["longitudeDeg"]!.GetValue<double>());
                relevance[site["siteId"]!.GetValue<string>()] = SpaceWeatherRelevance.Categorize(group.Family,
                    group.Latest.Code, group.Level, group.LatestNonCancel?.Header.StatedLatitude, phi).Category.Token();
            }
            notice["relevance"] = relevance;
            if (group.Family == SpaceWeatherFamily.Watch && group.LatestNonCancel is { } nc)
                notice["days"] = JsonSerializer.SerializeToNode(nc.Header.Days?.Select(d => new
                {
                    dateUtc = d.DateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    level = d.Level
                }));
            var zone = TimeZoneInfo.FindSystemTimeZoneById(scenario["sites"]![0]!["timeZoneId"]!.GetValue<string>());
            notice["local"] = JsonSerializer.SerializeToNode(new
            {
                fromLocal = Local(window.FromUtc, zone),
                untilLocal = Local(window.UntilUtc, zone)
            });
            notices[group.Identity] = notice;
        }
        return new JsonObject
        {
            ["notices"] = notices,
            ["products"] = new JsonObject(view.Products.Select(p =>
                KeyValuePair.Create<string, JsonNode?>(p.Key.Token(), Product(view, p.Key, at)))),
            ["originalRecords"] = JsonSerializer.SerializeToNode(view.Notices.Versions
                .GroupBy(v => v.Version.Message.Identity).ToDictionary(g => g.Key,
                    g => g.OrderBy(v => v.Version.IngestSequence).Select(v => v.Version.Message.OriginalRecordJson).ToArray(),
                    StringComparer.Ordinal))
        };
    }

    /// <summary>Maps durable policy, freshness, last-known-good facts and time-dependent Kp relations.</summary>
    internal static JsonNode Product(SwpcStoreView view, SwpcProduct product, DateTimeOffset at)
    {
        var stored = view.Products[product];
        var checkpoint = stored.Checkpoint;
        var halt = view.ProviderHalt ?? stored.ProductHalt;
        var result = JsonSerializer.SerializeToNode(new
        {
            lastOutcome = stored.LastOutcome is { } outcome ? SwpcPolicy.OutcomeToken(outcome) : null,
            freshness = stored.Freshness switch
            {
                SwpcFreshness.None => "none",
                SwpcFreshness.Fresh => "fresh",
                SwpcFreshness.Stale => "stale",
                SwpcFreshness.Outdated => "outdated",
                _ => throw new ArgumentOutOfRangeException(nameof(view))
            },
            lkgSha256 = stored.LastKnownGood?.Sha256,
            lastSuccessUtc = Utc(stored.LastSuccessUtc),
            nextAttemptNotBeforeUtc = Utc(checkpoint.NextAttemptNotBeforeUtc),
            retryNotBeforeUtc = Utc(checkpoint.RetryNotBeforeUtc),
            consecutiveTransientFailures = checkpoint.ConsecutiveTransientFailures,
            consecutiveRateLimited = checkpoint.ConsecutiveRateLimited,
            consecutiveRejected = checkpoint.ConsecutiveRejected,
            recordDriftOpen = checkpoint.RecordDriftStampUtc is not null,
            recordDriftStampUtc = Utc(checkpoint.RecordDriftStampUtc),
            halt = halt is null ? null : new
            {
                scope = view.ProviderHalt is null ? "product" : "provider",
                reason = halt.ReasonCode,
                sinceUtc = Utc(halt.SinceUtc)
            },
            validators = new { etag = stored.Validators.ETag, lastModified = stored.Validators.LastModified }
        })!.AsObject();
        result["facts"] = Facts(stored.LastKnownGood?.Parsed);
        if (stored.LastKnownGood?.Parsed is SwpcKpBody kp)
            result["kpBinRelations"] = JsonSerializer.SerializeToNode(kp.Value.Records.ToDictionary(
                b => Utc(b.StartUtc)!, b => b.RelationAt(at), StringComparer.Ordinal));
        return result;
    }

    /// <summary>Preserves source strings and verbatim labels from typed last-known-good bodies.</summary>
    private static JsonNode? Facts(SwpcParsedBody? body) => body switch
    {
        null => null,
        SwpcAlertsBody alerts => JsonSerializer.SerializeToNode(new
        {
            records = alerts.Value.RecordCount,
            acceptedRecords = alerts.Value.Records.Length
        }),
        SwpcScalesBody scales => JsonSerializer.SerializeToNode(scales.Value.Records.ToDictionary(r => r.Window, r => new
        {
            stampUtc = Utc(r.StampUtc),
            rScale = r.RScale,
            rMinorProbPct = r.RMinorProbPct,
            rMajorProbPct = r.RMajorProbPct,
            sScale = r.SScale,
            sProbPct = r.SProbPct,
            gScale = r.GScale
        }, StringComparer.Ordinal)),
        SwpcKpBody kp => JsonSerializer.SerializeToNode(new
        {
            rowCount = kp.Value.RecordCount,
            firstBinStartUtc = Utc(kp.Value.Records.Min(b => b.StartUtc)),
            lastBinStartUtc = Utc(kp.Value.Records.Max(b => b.StartUtc)),
            bins = kp.Value.Records.ToDictionary(b => Utc(b.StartUtc)!, b => new
            {
                kp = b.KpText,
                label = b.ObservedKind,
                noaaScale = b.NoaaScale
            }, StringComparer.Ordinal)
        }),
        SwpcThreeDayBody forecast => Forecast(forecast.Value.Records.Single()),
        _ => throw new ArgumentOutOfRangeException(nameof(body))
    };

    /// <summary>Names forecast columns by UTC date rather than the printed table year.</summary>
    private static JsonNode Forecast(IssuedForecast forecast) => JsonSerializer.SerializeToNode(new
    {
        issuedUtc = Utc(forecast.IssuedUtc),
        kpBreakdownBins = forecast.KpBreakdown.Count,
        kpBreakdown = forecast.KpBreakdown.ToDictionary(p => Utc(p.Key)!, p => new { kp = p.Value.KpText, noaaScale = p.Value.NoaaScale }, StringComparer.Ordinal),
        radiationProbabilities = forecast.RadiationProbabilities.ToDictionary(p => p.Key,
            p => p.Value.ToDictionary(d => d.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d => d.Value, StringComparer.Ordinal), StringComparer.Ordinal),
        radioBlackoutProbabilities = forecast.RadioBlackoutProbabilities.ToDictionary(p => p.Key,
            p => p.Value.ToDictionary(d => d.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d => d.Value, StringComparer.Ordinal), StringComparer.Ordinal)
    })!;

    internal static string? Utc(DateTimeOffset? at) => at?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    private static string Local(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone)
        .ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}
