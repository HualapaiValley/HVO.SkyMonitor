using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentCentralHttpAttemptRecorderTests
{
    [TestMethod]
    public async Task FilterRecordsOnlyNamedCentralClientAttempts()
    {
        var root = Directory.CreateTempSubdirectory("hvo-central-attempt-").FullName;
        try
        {
            var path = Path.Combine(root, "attempts.log");
            var recorder = new CameraAgentCentralHttpAttemptRecorder(path);
            var services = new ServiceCollection();
            services.AddSingleton(recorder);
            services.AddSingleton<IHttpMessageHandlerBuilderFilter, CameraAgentCentralHttpAttemptFilter>();
            services.AddHttpClient(SkyMonitorClientOptions.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(static () => new SuccessHandler());
            services.AddHttpClient("unrelated")
                .ConfigurePrimaryHttpMessageHandler(static () => new SuccessHandler());
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IHttpClientFactory>();

            using var centralResponse = await factory.CreateClient(SkyMonitorClientOptions.HttpClientName)
                .GetAsync(new Uri("http://central.invalid/")).ConfigureAwait(false);
            using var unrelatedResponse = await factory.CreateClient("unrelated")
                .GetAsync(new Uri("http://unrelated.invalid/")).ConfigureAwait(false);

            centralResponse.EnsureSuccessStatusCode();
            unrelatedResponse.EnsureSuccessStatusCode();
            CollectionAssert.AreEqual(
                new[]
                {
                    CameraAgentCentralHttpAttemptRecorder.ReadyRecord,
                    $"{CameraAgentCentralHttpAttemptRecorder.AttemptRecord} {SkyMonitorClientOptions.HttpClientName}"
                },
                await File.ReadAllLinesAsync(path).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ReinitializationPreservesAttemptsFromEarlierProcessLifetime()
    {
        var root = Directory.CreateTempSubdirectory("hvo-central-attempt-").FullName;
        try
        {
            var path = Path.Combine(root, "attempts.log");
            var first = new CameraAgentCentralHttpAttemptRecorder(path);
            first.Record(SkyMonitorClientOptions.HttpClientName);

            _ = new CameraAgentCentralHttpAttemptRecorder(path);

            CollectionAssert.AreEqual(
                new[]
                {
                    CameraAgentCentralHttpAttemptRecorder.ReadyRecord,
                    $"{CameraAgentCentralHttpAttemptRecorder.AttemptRecord} {SkyMonitorClientOptions.HttpClientName}",
                    CameraAgentCentralHttpAttemptRecorder.ReadyRecord
                },
                File.ReadAllLines(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SuccessHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
    }
}
