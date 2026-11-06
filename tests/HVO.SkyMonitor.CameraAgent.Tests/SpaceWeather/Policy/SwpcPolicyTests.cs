using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Policy;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcPolicyTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AdmissionPreservesValidatorsAndCrashCheckpoint(bool laterExisting)
    {
        var t = SwpcTestData.Now;
        var view = SwpcPolicyTestStoreDouble.EmptyView();
        var stored = view.Products[SwpcProduct.Alerts];
        stored = stored with
        {
            Validators = new("etag", "modified"),
            Checkpoint = stored.Checkpoint with
            { NextAttemptNotBeforeUtc = t.AddSeconds(laterExisting ? 600 : -1), InBackoff = false, ConsecutiveRejected = 7 }
        };
        view = view with { Products = view.Products.SetItem(SwpcProduct.Alerts, stored) };
        var result = SwpcPolicy.Admit(view, SwpcProduct.Alerts, SwpcAttemptTrigger.Manual, "run", Guid.NewGuid(), t);
        Assert.IsNull(result.Refusal);
        Assert.AreSame(stored.Validators, result.Start!.SentValidators);
        Assert.AreEqual(t, result.Start.StartedUtc);
        Assert.AreEqual(stored.Checkpoint with { NextAttemptNotBeforeUtc = t.AddSeconds(laterExisting ? 600 : 120) }, result.Start.CheckpointAfterStart);
    }

    [TestMethod]
    [DataRow(true, true, true, SwpcAttemptTrigger.Manual, SwpcRefusal.Halted)]
    [DataRow(false, true, true, SwpcAttemptTrigger.Manual, SwpcRefusal.Halted)]
    [DataRow(false, false, true, SwpcAttemptTrigger.Manual, SwpcRefusal.Backoff)]
    [DataRow(false, false, false, SwpcAttemptTrigger.Manual, SwpcRefusal.MinInterval)]
    [DataRow(false, false, true, SwpcAttemptTrigger.Scheduled, SwpcRefusal.NotDue)]
    [DataRow(true, false, true, SwpcAttemptTrigger.Scheduled, SwpcRefusal.Halted)]
    public void AdmissionFirstMatchingRuleWins(bool provider, bool product, bool backoff, SwpcAttemptTrigger trigger, SwpcRefusal expected)
    {
        var t = SwpcTestData.Now;
        var view = SwpcPolicyTestStoreDouble.EmptyView();
        var stored = view.Products[SwpcProduct.Alerts] with
        {
            LastAttemptUtc = t.AddSeconds(-10),
            ProductHalt = product ? new("not-found", t) : null,
            Checkpoint = new(t.AddSeconds(100), null, backoff, 0, 0, 0, null)
        };
        view = view with { ProviderHalt = provider ? new("forbidden", t) : null, Products = view.Products.SetItem(SwpcProduct.Alerts, stored) };
        var result = SwpcPolicy.Admit(view, SwpcProduct.Alerts, trigger, "run", Guid.NewGuid(), t);
        Assert.AreEqual(expected, result.Refusal);
        Assert.IsNull(result.Start);
    }

    [TestMethod]
    public void FirstAttemptAndExactBoundariesAreAdmittedAndBackoffEnds()
    {
        var view = SwpcPolicyTestStoreDouble.EmptyView();
        var t = SwpcTestData.Now;
        foreach (var trigger in Enum.GetValues<SwpcAttemptTrigger>())
            Assert.IsNotNull(SwpcPolicy.Admit(view, SwpcProduct.Alerts, trigger, "first", Guid.NewGuid(), t).Start);
        var stored = view.Products[SwpcProduct.Alerts] with { LastAttemptUtc = t.AddSeconds(-60), Checkpoint = new(t, null, true, 1, 2, 3, t) };
        view = view with { Products = view.Products.SetItem(SwpcProduct.Alerts, stored) };
        var start = SwpcPolicy.Admit(view, SwpcProduct.Alerts, SwpcAttemptTrigger.Manual, "boundary", Guid.NewGuid(), t).Start!;
        Assert.AreEqual(stored.Checkpoint with { InBackoff = false, NextAttemptNotBeforeUtc = t.AddSeconds(120) }, start.CheckpointAfterStart);
    }

    [TestMethod]
    [DataRow(199, false, SwpcFetchOutcome.TransientFailure, "unexpected-status")]
    [DataRow(200, false, SwpcFetchOutcome.Updated, "updated")]
    [DataRow(201, false, SwpcFetchOutcome.TransientFailure, "unexpected-status")]
    [DataRow(299, false, SwpcFetchOutcome.TransientFailure, "unexpected-status")]
    [DataRow(300, false, SwpcFetchOutcome.Halted, "endpoint-moved")]
    [DataRow(301, false, SwpcFetchOutcome.Halted, "endpoint-moved")]
    [DataRow(304, false, SwpcFetchOutcome.TransientFailure, "not-modified-unsolicited")]
    [DataRow(304, true, SwpcFetchOutcome.NotModified, "not-modified")]
    [DataRow(399, false, SwpcFetchOutcome.Halted, "endpoint-moved")]
    [DataRow(400, false, SwpcFetchOutcome.Halted, "client-error")]
    [DataRow(403, false, SwpcFetchOutcome.Halted, "forbidden")]
    [DataRow(404, false, SwpcFetchOutcome.Halted, "not-found")]
    [DataRow(410, false, SwpcFetchOutcome.Halted, "gone")]
    [DataRow(418, false, SwpcFetchOutcome.Halted, "client-error")]
    [DataRow(429, false, SwpcFetchOutcome.RateLimited, "rate-limited")]
    [DataRow(499, false, SwpcFetchOutcome.Halted, "client-error")]
    [DataRow(500, false, SwpcFetchOutcome.TransientFailure, "server-error")]
    [DataRow(599, false, SwpcFetchOutcome.TransientFailure, "server-error")]
    public void EveryResponseRowAndStatusBoundary(int status, bool validators, SwpcFetchOutcome outcome, string reason)
    {
        var response = PolicyData.Response(status, validators);
        var completion = PolicyData.Classify(response);
        Assert.AreEqual(outcome, completion.Outcome);
        Assert.AreEqual(reason, completion.ReasonCode);
        Assert.AreEqual(status, completion.HttpStatus);
        Assert.AreEqual(response.CompletedUtc, completion.RetrievedUtc);
        Assert.AreEqual(status == 200 ? response.Body.Length : 0, completion.BytesRead);
        Assert.AreEqual(status == 200, completion.Candidate is not null);
        Assert.AreEqual(outcome == SwpcFetchOutcome.Updated ? SwpcValidatorOutcome.Replaced
            : outcome == SwpcFetchOutcome.NotModified ? SwpcValidatorOutcome.NotModified
            : validators ? SwpcValidatorOutcome.Preserved : SwpcValidatorOutcome.NotSent, completion.ValidatorOutcome);
    }

    [TestMethod]
    [DataRow(SwpcTransportFailure.Timeout, "timeout")]
    [DataRow(SwpcTransportFailure.NetworkError, "network-error")]
    public void TransportFailuresHaveNoRetrievalOrStatus(SwpcTransportFailure failure, string reason)
    {
        var t = SwpcTestData.Now;
        var c = PolicyData.Classify(new(SwpcProduct.Alerts, t, t.AddSeconds(10), failure, null));
        Assert.AreEqual(SwpcFetchOutcome.TransientFailure, c.Outcome);
        Assert.AreEqual(reason, c.ReasonCode);
        Assert.IsNull(c.HttpStatus);
        Assert.IsNull(c.RetrievedUtc);
        Assert.IsNull(c.Candidate);
        Assert.AreEqual(0L, c.BytesRead);
    }

    [TestMethod]
    [DataRow("oversize")]
    [DataRow("content-type")]
    [DataRow("encoding")]
    [DataRow("malformed-json")]
    [DataRow("schema")]
    [DataRow("suspect-empty")]
    [DataRow("too-many-records")]
    [DataRow("no-valid-records")]
    public void EveryBodyRejectionIsReportedWithoutReplacingValidators(string reason)
    {
        byte[] bytes = reason switch
        {
            "encoding" => [0xff],
            "malformed-json" => "["u8.ToArray(),
            "schema" => "{}"u8.ToArray(),
            "suspect-empty" => "[]"u8.ToArray(),
            "too-many-records" => Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("{}", 2001)) + "]"),
            "no-valid-records" => "[{}]"u8.ToArray(),
            _ => []
        };
        var t = SwpcTestData.Now;
        var response = new SwpcHttpResponse(SwpcProduct.Alerts, t, t, SwpcTransportFailure.None, 200,
            true, reason == "content-type" ? "text/html" : "application/json", body: bytes, oversize: reason == "oversize");
        var c = PolicyData.Classify(response);
        Assert.AreEqual(SwpcFetchOutcome.Rejected, c.Outcome);
        Assert.AreEqual("body-rejected:" + reason, c.ReasonCode);
        Assert.AreEqual(SwpcValidatorOutcome.Preserved, c.ValidatorOutcome);
        Assert.IsNull(c.Candidate);
    }

    [TestMethod]
    [DataRow(SwpcProduct.Alerts)]
    [DataRow(SwpcProduct.Scales)]
    [DataRow(SwpcProduct.KpForecast)]
    [DataRow(SwpcProduct.ThreeDayForecast)]
    public void UndecodableBodyUsesS1EmptyProbeForBothT1Mappings(SwpcProduct product)
    {
        var t = SwpcTestData.Now;
        var media = SwpcSource.MediaType(product);
        foreach (var value in new[] { media, " " + media.ToUpperInvariant() + "; charset=utf-8 ", "text/html", null })
        {
            var r = new SwpcHttpResponse(product, t, t, SwpcTransportFailure.None, 200, mediaType: value, bodyUndecodable: true);
            var c = PolicyData.Classify(r);
            Assert.AreEqual("body-rejected:" + (value is null or "text/html" ? "content-type" : "encoding"), c.ReasonCode);
            Assert.AreEqual(0L, c.BytesRead);
        }
    }

    [TestMethod]
    public void EnvelopeRejectionOrderPrecedesParsingAndUtf8()
    {
        var t = SwpcTestData.Now;
        var oversize = new SwpcHttpResponse(SwpcProduct.Alerts, t, t, SwpcTransportFailure.None,
            200, mediaType: "text/html", oversize: true);
        Assert.AreEqual("body-rejected:oversize", PolicyData.Classify(oversize).ReasonCode);
        var invalidUtf8 = new SwpcHttpResponse(SwpcProduct.Alerts, t, t, SwpcTransportFailure.None,
            200, mediaType: "text/html", body: new byte[] { 0xff });
        Assert.AreEqual("body-rejected:content-type", PolicyData.Classify(invalidUtf8).ReasonCode);
    }

    [TestMethod]
    public void CandidateRetainsExactProvenanceAndUsesSharedComparison()
    {
        var r = PolicyData.Response(200, true);
        var c = PolicyData.Classify(r);
        var candidate = c.Candidate!;
        var parsed = SwpcAlertsParser.Parse(r.Body.Span, r.MediaType);
        Assert.AreEqual("/products/alerts.json", candidate.CanonicalPath);
        Assert.AreEqual(r.Product, candidate.Product);
        Assert.AreEqual(r.StartedUtc, candidate.FetchStartedUtc);
        Assert.AreEqual(r.CompletedUtc, candidate.RetrievedUtc);
        Assert.AreEqual(200, candidate.HttpStatus);
        Assert.AreEqual(r.MediaType, candidate.MediaType);
        Assert.AreEqual(r.Body.Length, candidate.DecodedByteCount);
        CollectionAssert.AreEqual(r.Body.ToArray(), candidate.DecodedBody.ToArray());
        Assert.AreEqual(parsed.SnapshotSha256, candidate.Sha256);
        Assert.AreEqual(parsed.StampUtc, candidate.InFileStampUtc);
        Assert.AreEqual(new SwpcValidators(r.EntityTag, r.LastModified), candidate.ResponseValidators);
        Assert.IsInstanceOfType<SwpcAlertsBody>(candidate.Parsed);
        foreach (var (lkg, expected) in new[] { (candidate, SwpcFetchOutcome.Unchanged),
            (candidate with { Sha256 = "different", InFileStampUtc = candidate.InFileStampUtc!.Value.AddMinutes(1) }, SwpcFetchOutcome.StaleSnapshot),
            (candidate with { Sha256 = "different", InFileStampUtc = null }, SwpcFetchOutcome.Updated) })
        {
            var next = PolicyData.Classify(r, PolicyData.Stored(r.Product) with { LastKnownGood = lkg });
            Assert.AreEqual(expected, next.Outcome);
            Assert.AreEqual(SwpcSnapshotComparison.Classify(lkg, candidate), next.Outcome);
            Assert.AreEqual(expected == SwpcFetchOutcome.StaleSnapshot ? SwpcValidatorOutcome.Preserved : SwpcValidatorOutcome.Replaced, next.ValidatorOutcome);
        }
    }

    [TestMethod]
    [DataRow(SwpcProduct.Alerts, "alerts.json", "/products/alerts.json")]
    [DataRow(SwpcProduct.Scales, "noaa-scales.json", "/products/noaa-scales.json")]
    [DataRow(SwpcProduct.KpForecast, "noaa-planetary-k-index-forecast.json", "/products/noaa-planetary-k-index-forecast.json")]
    [DataRow(SwpcProduct.ThreeDayForecast, "3-day-forecast.txt", "/text/3-day-forecast.txt")]
    public void EveryCoreProductBuildsItsMatchingTypedCandidate(SwpcProduct product, string file, string canonicalPath)
    {
        var bytes = File.ReadAllBytes(Path.Combine(SwpcScenarioPolicyTests.Pack, "capture-20261019T1857Z", file));
        var t = SwpcTestData.Now;
        var response = new SwpcHttpResponse(product, t, t.AddSeconds(1), SwpcTransportFailure.None, 200,
            mediaType: SwpcSource.MediaType(product), body: bytes);
        var completion = PolicyData.Classify(response);
        Assert.AreEqual(SwpcFetchOutcome.Updated, completion.Outcome);
        var candidate = completion.Candidate!;
        Assert.AreEqual(product, candidate.Product);
        Assert.AreEqual(canonicalPath, candidate.CanonicalPath);
        Assert.AreEqual(product switch
        {
            SwpcProduct.Alerts => typeof(SwpcAlertsBody),
            SwpcProduct.Scales => typeof(SwpcScalesBody),
            SwpcProduct.KpForecast => typeof(SwpcKpBody),
            _ => typeof(SwpcThreeDayBody)
        }, candidate.Parsed.GetType());
        CollectionAssert.AreEqual(bytes, candidate.DecodedBody.ToArray());
    }

    [TestMethod]
    public void ThirdRateLimitClassifiesAsHalted()
    {
        var stored = PolicyData.Stored() with { Checkpoint = new(null, null, false, 7, 2, 8, null) };
        var c = PolicyData.Classify(PolicyData.Response(429), stored);
        Assert.AreEqual(SwpcFetchOutcome.Halted, c.Outcome);
        Assert.AreEqual("rate-limited", c.ReasonCode);
    }
}

internal static class PolicyData
{
    public static SwpcStoredProduct Stored(SwpcProduct product = SwpcProduct.Alerts) => SwpcPolicyTestStoreDouble.EmptyView().Products[product];
    public static byte[] Alerts => Encoding.UTF8.GetBytes("[{\"product_id\":\"TEST\",\"issue_datetime\":\"2026-02-03 01:00:00.000\",\"message\":\"Space Weather Message Code: ALTZZZ\\nSerial Number: 1\\nIssue Time: 2026 Feb 03 0100 UTC\\nALERT: Test\\n\"}]");
    public static SwpcHttpResponse Response(int status = 200, bool validators = false, bool completedAtStart = false) => new(SwpcProduct.Alerts,
        SwpcTestData.Now, completedAtStart ? SwpcTestData.Now : SwpcTestData.Now.AddSeconds(10), SwpcTransportFailure.None, status, validators,
        "application/json; charset=utf-8", "new-etag", "new-modified", body: status == 200 ? Alerts : default);
    public static SwpcAttemptStart Start(SwpcProduct product = SwpcProduct.Alerts) => new(Guid.NewGuid(), 0, product,
        "test", SwpcAttemptTrigger.Scheduled, SwpcTestData.Now, new(null, null), Stored(product).Checkpoint);
    public static SwpcAttemptCompletion Classify(SwpcHttpResponse response, SwpcStoredProduct? stored = null) =>
        SwpcPolicy.Classify(Start(response.Product), stored ?? Stored(response.Product), response, response.CompletedUtc.AddSeconds(5));
}
