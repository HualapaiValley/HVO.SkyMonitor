using System.Net;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcResponseOwnershipTests
{
    [TestMethod]
    public void ConstructorOwnsAnExactCopyOfTheCallerSlice()
    {
        byte[] caller = [99, 1, 2, 3, 99];
        var response = new SwpcHttpResponse(SwpcProduct.Alerts, SwpcTestData.Now, SwpcTestData.Now,
            SwpcTransportFailure.None, 200, body: caller.AsMemory(1, 3));
        caller.AsSpan().Fill(42);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, response.Body.ToArray());
        Assert.IsTrue(MemoryMarshal.TryGetArray(response.Body, out var storage));
        Assert.AreEqual(3, storage.Array!.Length);
        Assert.AreEqual(0, storage.Offset);
        Assert.AreNotSame(caller, storage.Array);
    }

    [TestMethod]
    public void WithCopyKeepsOwnedBodyAndTransportFactsConsistent()
    {
        byte[] caller = [1, 2, 3];
        var original = new SwpcHttpResponse(SwpcProduct.Scales, SwpcTestData.Now, SwpcTestData.Now,
            SwpcTransportFailure.None, 200, validatorsSent: true, mediaType: "application/json", body: caller);
        var copy = original with { };
        caller.AsSpan().Clear();
        Assert.AreEqual(original, copy);
        Assert.AreNotSame(original, copy);
        Assert.AreEqual(original.Body, copy.Body, "Record copies share the already owned read-only memory.");
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, copy.Body.ToArray());
        Assert.AreEqual(200, copy.StatusCode);
        Assert.AreEqual(SwpcTransportFailure.None, copy.Failure);
        Assert.IsTrue(copy.ValidatorsSent);
        Assert.IsFalse(copy.Oversize);
        Assert.IsFalse(copy.BodyUndecodable);
    }

    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 7)]
    [DataRow(false, 65536)]
    [DataRow(true, 0)]
    [DataRow(true, 7)]
    [DataRow(true, 65536)]
    public async Task ClientRetainsOnlyTheExactDecodedLength(bool gzip, int length)
    {
        var decoded = Enumerable.Repeat((byte)42, length).ToArray();
        var content = new SwpcContent(gzip ? SwpcTestData.Gzip(decoded) : decoded);
        if (gzip) content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
        using var handler = new SwpcStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var response = await new SwpcClient(http, new ManualTimeProvider(SwpcTestData.Now), SwpcSource.UserAgent)
            .FetchAsync(SwpcProduct.Scales, null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcTransportFailure.None, response.Failure);
        Assert.IsFalse(response.BodyUndecodable);
        Assert.AreEqual(length, response.Body.Length);
        Assert.IsTrue(MemoryMarshal.TryGetArray(response.Body, out var storage));
        Assert.AreEqual(length, storage.Array!.Length, "The cap+1 scratch array must not escape.");
        Assert.AreEqual(0, storage.Offset);
        CollectionAssert.AreEqual(decoded, response.Body.ToArray());
    }
}
