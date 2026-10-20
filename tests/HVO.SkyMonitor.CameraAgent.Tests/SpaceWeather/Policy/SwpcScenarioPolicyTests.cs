using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Policy;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcScenarioPolicyTests
{
    public TestContext TestContext { get; set; } = null!;
    internal static string Pack => Path.Combine(AppContext.BaseDirectory, "Fixtures", "space-weather", "swpc-v1");

    [TestMethod]
    [DataRow("s01-duplicate-refetch.json")]
    [DataRow("s02-same-identity-revised-content.json")]
    [DataRow("s03-correction-reissue.json")]
    [DataRow("s04-continuation-self-reference.json")]
    [DataRow("s05-extension-chain-expiry.json")]
    [DataRow("s06-extension-before-root.json")]
    [DataRow("s07-watch-supersession-and-cancel.json")]
    [DataRow("s08-watch-replacement-and-escalation.json")]
    [DataRow("s09-synthetic-cancel-warning.json")]
    [DataRow("s10-restart-ack-rolling-window.json")]
    [DataRow("s11-stale-cdn-out-of-order.json")]
    [DataRow("s12-malformed-bodies.json")]
    [DataRow("s13-outage-backoff-halts.json")]
    [DataRow("s14-year-rollover-and-local-day.json")]
    [DataRow("s15-missing-scale-and-location.json")]
    [DataRow("s16-kp-estimated-future-bin.json")]
    [DataRow("s17-three-day-issue-versus-last-modified.json")]
    [DataRow("s18-ovation-site-band.json")]
    [DataRow("s19-early-resume-spacing.json")]
    [DataRow("s20-record-level-drift.json")]
    [DataRow("s21-pending-short-window-and-cap.json")]
    [DataRow("s22-gone-client-error-network.json")]
    [DataRow("s23-retry-after-forms.json")]
    [DataRow("s24-json-nonfinite.json")]
    [DataRow("s25-year-bounds.json")]
    [DataRow("s26-original-record-verbatim.json")]
    [DataRow("s27-original-record-outer-whitespace.json")]
    public async Task RunnerMatchesEveryPolicyExpectation(string file)
    {
        var scenario = JsonNode.Parse(File.ReadAllText(Path.Combine(Pack, "scenarios", file)))!;
        using var store = new SwpcPolicyTestStoreDouble();
        var clock = new ManualTimeProvider(SwpcTestData.Now);
        var client = new PolicyStubClient();
        var runner = new SwpcAttemptRunner(store, client, clock, SwpcJitter.None);
        var stepsAsserted = 0;
        var policySteps = 0;
        var ovationSteps = 0;
        var excludedKeys = 0;
        var keysAsserted = 0;
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            foreach (var step in scenario["steps"]!.AsArray().Select(s => s!))
            {
                var kind = step["kind"]!.GetValue<string>();
                var where = file + ":step:" + step["step"];
                if (step["product"]?.GetValue<string>() == "ovation")
                {
                    var before = store.View;
                    // All six excluded exchanges are 200s: they cannot halt the provider.
                    Assert.AreEqual(200, step["response"]!["status"]!.GetValue<int>(), where);
                    Assert.IsTrue(step["expect"]!["outcome"]!.GetValue<string>() is "updated" or "rejected", where);
                    ovationSteps++;
                    Skip("OVATION U3");
                    Assert.AreEqual(before.ProviderHalt, store.View.ProviderHalt, where);
                    Assert.AreSame(before.Notices, store.View.Notices, where);
                    Assert.AreEqual(before.Revision, store.View.Revision, where);
                    continue;
                }
                if (kind is "acknowledge" or "evaluate") { Skip(kind + " (no policy effect)"); continue; }
                if (kind == "restart")
                {
                    runner.Dispose();
                    runner = new(store, client, clock, SwpcJitter.None);
                    Skip("restart (runner reconstructed; S3 state assertions excluded)");
                    continue;
                }
                policySteps++;
                var at = SwpcTestData.Time(step["atUtc"]!.GetValue<string>());
                clock.Advance(at - clock.GetUtcNow());
                var expect = step["expect"] ?? new JsonObject();
                var asserted = 0;
                if (kind == "fetch")
                {
                    var product = Product(step["product"]!.GetValue<string>());
                    var trigger = step["trigger"]?.GetValue<string>() == "manual" ? SwpcAttemptTrigger.Manual : SwpcAttemptTrigger.Scheduled;
                    client.LastValidators = null;
                    client.Response = step["response"] is { } response ? Response(product, response, at) : null;
                    var result = await runner.RunAsync(product, trigger, "scenario-" + step["step"]).ConfigureAwait(false);
                    Assert.AreNotEqual(SwpcRefusal.NotDue, result.Refusal, where + ": scheduler defect");
                    if (expect.AsObject().ContainsKey("refused")) Check(expect["refused"], JsonValue.Create(Refusal(result.Refusal)), where + ".refused");
                    if (expect.AsObject().ContainsKey("outcome")) Check(expect["outcome"], JsonValue.Create(result.Outcome is { } o ? SwpcPolicy.OutcomeToken(o) : null), where + ".outcome");
                    if (expect.AsObject().ContainsKey("request")) Check(expect["request"], client.LastValidators is { } sent
                        ? JsonSerializer.SerializeToNode(new { ifNoneMatch = sent.EntityTag, ifModifiedSince = sent.LastModified }) : null, where + ".request");
                    if (expect["product"] is { } expectedProduct) CheckProduct(expectedProduct, product, where + ".product");
                    if (expect["attention"] is JsonArray expectedAttention)
                        Check(new JsonArray(expectedAttention.Where(e => e!["group"]!.GetValue<string>().StartsWith("provider/", StringComparison.Ordinal)).Select(e => e!.DeepClone()).ToArray()),
                            JsonSerializer.SerializeToNode(result.Attention.Where(e => e.Group.StartsWith("provider/", StringComparison.Ordinal))
                                .Select(e => new { group = e.Group, reason = e.Reason, level = e.Level })), where + ".providerAttention");
                    if (expect["diagnostics"] is JsonArray expectedDiagnostics)
                        Check(new JsonArray(expectedDiagnostics.Where(d => OwnedDiagnostic(d!.GetValue<string>())).Select(d => d!.DeepClone()).ToArray()),
                            JsonSerializer.SerializeToNode(result.Diagnostics.Where(OwnedDiagnostic)), where + ".diagnostics");
                }
                else if (kind == "resume")
                {
                    var result = await runner.ResumeAsync(step["scope"]!.GetValue<string>() == "provider" ? null : Product(step["product"]!.GetValue<string>()), Guid.NewGuid()).ConfigureAwait(false);
                    Assert.IsFalse(result.NotHalted, where);
                }
                else Assert.Fail(where + ": unknown step kind");
                if (expect["products"] is JsonObject products)
                    foreach (var (token, productExpectation) in products)
                        if (token != "ovation") CheckProduct(productExpectation!, Product(token), where + ".products." + token);
                if (asserted > 0) stepsAsserted++;
                else Skip("policy executed without scoped expectation");
                keysAsserted += asserted;

                void CheckProduct(JsonNode expected, SwpcProduct product, string path)
                {
                    var actual = ProductView(store.View, product);
                    foreach (var (key, value) in expected.AsObject())
                    {
                        if (actual.AsObject().ContainsKey(key)) Check(value, actual[key], path + "." + key);
                        else
                        {
                            Assert.IsTrue(key is "facts" or "freshness" or "kpBinRelations", path + "." + key + ": unexpected excluded policy key");
                            excludedKeys++;
                        }
                    }
                }
                void Check(JsonNode? expected, JsonNode? actual, string path)
                {
                    asserted += Subset(expected, actual, path);
                }
            }
            if (policySteps > 0) Assert.IsTrue(keysAsserted > 0, file + ": policy coverage must be nonzero");
            Assert.AreEqual(file.StartsWith("s18-", StringComparison.Ordinal) ? 2
                : file.StartsWith("s24-", StringComparison.Ordinal) ? 4 : 0, ovationSteps, file);
            TestContext.WriteLine("COVERAGE {0}: steps={1}; keys={2}; skipped={3}; excludedProductKeys={4}; reasons={5}", file,
                stepsAsserted, keysAsserted, skipped.Values.Sum(), excludedKeys, string.Join("; ", skipped.Select(p => p.Key + "=" + p.Value.ToString(CultureInfo.InvariantCulture))));
        }
        finally { runner.Dispose(); }
        void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
    }

    internal static SwpcProduct Product(string token) => token switch
    {
        "alerts" => SwpcProduct.Alerts,
        "scales" => SwpcProduct.Scales,
        "kp-forecast" => SwpcProduct.KpForecast,
        "3-day-forecast" => SwpcProduct.ThreeDayForecast,
        _ => throw new ArgumentOutOfRangeException(nameof(token))
    };

    internal static SwpcHttpResponse Response(SwpcProduct product, JsonNode node, DateTimeOffset at)
    {
        if (node["outcome"] is { } failure)
            return new(product, at, at, failure.GetValue<string>() == "timeout" ? SwpcTransportFailure.Timeout : SwpcTransportFailure.NetworkError, null);
        var status = node["status"]!.GetValue<int>();
        var bytes = status == 200 ? Body(node) : [];
        var oversize = bytes.Length > SwpcSource.MaximumDecodedBytes(product);
        return new(product, at, at, SwpcTransportFailure.None, status, mediaType: node["contentType"]?.GetValue<string>(),
            entityTag: node["etag"]?.GetValue<string>(), lastModified: node["lastModified"]?.GetValue<string>(),
            retryAfter: node["retryAfter"]?.GetValue<string>(), body: oversize ? default : bytes, oversize: oversize);
    }

    internal static byte[] Body(JsonNode response)
    {
        if (response["body"] is { } body) return Encoding.UTF8.GetBytes(body.GetValue<string>());
        if (response["bodyBase64"] is { } base64) return Convert.FromBase64String(base64.GetValue<string>());
        if (response["bodyFile"] is { } file)
        {
            var path = Path.GetFullPath(Path.Combine(Pack, file.GetValue<string>()));
            Assert.IsTrue(path.StartsWith(Pack + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            return File.ReadAllBytes(path);
        }
        if (response["bodySynthesis"] is not { } synthesis) return [];
        if (synthesis["kind"]!.GetValue<string>() == "padded-empty-array")
            return Encoding.ASCII.GetBytes("[" + new string(' ', synthesis["totalBytes"]!.GetValue<int>() - 2) + "]");
        // Match fixtures.py encode_alerts: compact ensure_ascii JSON, with escaped solidus.
        var record = synthesis["record"]!.ToJsonString(new JsonSerializerOptions { WriteIndented = false })
            .Replace("/", "\\/", StringComparison.Ordinal);
        return Encoding.ASCII.GetBytes("[" + string.Join(',', Enumerable.Repeat(record, synthesis["count"]!.GetValue<int>())) + "]");
    }

    private static JsonNode ProductView(SwpcStoreView view, SwpcProduct product)
    {
        var stored = view.Products[product];
        var c = stored.Checkpoint;
        var halt = view.ProviderHalt ?? stored.ProductHalt;
        return JsonSerializer.SerializeToNode(new
        {
            lastOutcome = stored.LastOutcome is { } o ? SwpcPolicy.OutcomeToken(o) : null,
            lkgSha256 = stored.LastKnownGood?.Sha256,
            lastSuccessUtc = Utc(stored.LastSuccessUtc),
            nextAttemptNotBeforeUtc = Utc(c.NextAttemptNotBeforeUtc),
            retryNotBeforeUtc = Utc(c.RetryNotBeforeUtc),
            consecutiveTransientFailures = c.ConsecutiveTransientFailures,
            consecutiveRateLimited = c.ConsecutiveRateLimited,
            consecutiveRejected = c.ConsecutiveRejected,
            recordDriftOpen = c.RecordDriftStampUtc is not null,
            recordDriftStampUtc = Utc(c.RecordDriftStampUtc),
            halt = halt is null ? null : new { scope = view.ProviderHalt is null ? "product" : "provider", reason = halt.ReasonCode, sinceUtc = Utc(halt.SinceUtc) },
            validators = new { etag = stored.Validators.ETag, lastModified = stored.Validators.LastModified }
        })!;
    }

    private static string? Utc(DateTimeOffset? at) => at?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    private static bool OwnedDiagnostic(string code) => code.StartsWith("body-rejected:", StringComparison.Ordinal) || code == "stale-snapshot";
    private static string? Refusal(SwpcRefusal? refusal) => refusal switch
    {
        null => null,
        SwpcRefusal.Halted => "halted",
        SwpcRefusal.Backoff => "backoff",
        SwpcRefusal.MinInterval => "min-interval",
        SwpcRefusal.NotDue => "not-due",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal))
    };

    private static int Subset(JsonNode? expected, JsonNode? actual, string where)
    {
        if (expected is JsonObject obj)
        {
            Assert.IsInstanceOfType<JsonObject>(actual, where);
            var keys = 0;
            foreach (var (key, value) in obj) { Assert.IsTrue(actual.AsObject().ContainsKey(key), where); keys += Subset(value, actual[key], where + "." + key); }
            return Math.Max(keys, 1);
        }
        if (expected is JsonArray array)
        {
            Assert.IsInstanceOfType<JsonArray>(actual, where);
            Assert.AreEqual(array.Count, actual.AsArray().Count, where);
            var keys = 0;
            for (var i = 0; i < array.Count; i++) keys += Subset(array[i], actual[i], where + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
            return Math.Max(keys, 1);
        }
        else Assert.IsTrue(JsonNode.DeepEquals(expected, actual), where + ": expected=" + expected + "; actual=" + actual);
        return 1;
    }
}

internal sealed class PolicyStubClient : ISwpcClient
{
    public SwpcHttpResponse? Response { get; set; }
    public SwpcRequestValidators? LastValidators { get; set; }
    public int Calls { get; private set; }
    public Func<SwpcProduct, SwpcRequestValidators?, CancellationToken, ValueTask<SwpcHttpResponse>>? Fetch { get; set; }
    public ValueTask<SwpcHttpResponse> FetchAsync(SwpcProduct product, SwpcRequestValidators? validators, CancellationToken cancellationToken)
    {
        Calls++;
        LastValidators = validators;
        if (Fetch is not null) return Fetch(product, validators, cancellationToken);
        var r = Response ?? throw new AssertFailedException("A refused request sent an exchange.");
        return ValueTask.FromResult(new SwpcHttpResponse(product, r.StartedUtc, r.CompletedUtc, r.Failure, r.StatusCode,
            validators?.EntityTag is not null || validators?.LastModified is not null, r.MediaType, r.EntityTag,
            r.LastModified, r.RetryAfter, r.Body, r.Oversize, r.BodyUndecodable));
    }
}
