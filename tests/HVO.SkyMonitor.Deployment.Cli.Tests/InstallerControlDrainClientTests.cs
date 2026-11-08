using System.Net;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class InstallerControlDrainClientTests
{
    [TestMethod]
    [DataRow("rawIngress")]
    [DataRow("captureLanes")]
    [DataRow("captureProcessing")]
    [DataRow("artifactOutbox")]
    public async Task ControlDrainWaitsForEveryAcceptedQueueAndSendsStableExpectedVersionCommand(string pendingSection)
    {
        var operationId = Guid.NewGuid();
        using var handler = new DrainHandler(pendingSection, operationId);
        var client = new CameraAgentLifecycleClient(new Uri("http://virtual-cameraagent"), handler,
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(1)));
        var boundary = await client.PauseControlAndDrainAsync(operationId, 7, "installation-test-token", CancellationToken.None);
        Assert.AreEqual(2, handler.StateReads, "An unleased but queued capture or delivery is not a completed control drain.");
        Assert.AreEqual(0L, boundary.RawPending + boundary.LanePending + boundary.ProcessingPending + boundary.OutboxPending);
    }

    private sealed class DrainHandler(string pendingSection, Guid operationId) : HttpMessageHandler
    {
        internal int StateReads;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("installation-test-token", request.Headers.GetValues("X-HVO-Installation-Token").Single());
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.AreEqual(operationId, body.RootElement.GetProperty("operationId").GetGuid());
                Assert.AreEqual(7L, body.RootElement.GetProperty("expectedVersion").GetInt64());
                return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            StateReads++;
            object Section(string name) => new { freshness = "fresh", value = new { availability = "Healthy", pendingCount = StateReads == 1 && name == pendingSection ? 1 : 0, leasedCount = 0 } };
            var state = new
            {
                captureControl = new { value = new { state = "Paused", version = 9L, isInitialized = true } },
                captureSequence = 42L, rawIngress = Section("rawIngress"), captureLanes = Section("captureLanes"),
                captureProcessing = Section("captureProcessing"), artifactOutbox = Section("artifactOutbox")
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(state), Encoding.UTF8, "application/json") };
        }
    }
}
