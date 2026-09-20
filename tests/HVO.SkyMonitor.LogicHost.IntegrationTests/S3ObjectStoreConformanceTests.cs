using System.Net;
using System.Net.Sockets;
using System.Text;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Conformance coverage for the retained AWS SDK S3 adapter. The supported LogicHost deployment
/// uses the filesystem provider, so this suite runs only against an operator-provided endpoint.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class S3ObjectStoreConformanceTests
{
    private const string Bucket = "skymonitor-artifacts";
    private ObjectStoreConformanceSuite? _suite;

    [TestInitialize]
    public void Initialize()
    {
        if (IntegrationTestFixture.TryGetExternalS3Endpoint() is null)
        {
            Assert.Inconclusive(
                $"Set {IntegrationTestFixture.ExternalS3EndpointVariable} to run the S3 adapter conformance suite.");
        }
        _suite = new ObjectStoreConformanceSuite(
            CreateStore(),
            CreateFaultedStore,
            Bucket,
            $"issue-504/conformance/{Guid.NewGuid():N}/");
    }

    [TestCleanup]
    public Task CleanupAsync() => _suite?.CleanupAsync() ?? Task.CompletedTask;

    [TestMethod]
    public Task MaximumStreamingPublicationConditionalReadAndDelete_Conform()
        => _suite!.MaximumStreamingPublicationConditionalReadAndDeleteAsync();

    [TestMethod]
    public Task Listing_IsCompleteOrdinalAndCrossesProviderPages()
        => _suite!.ListingIsCompleteOrdinalAndCrossesProviderPagesAsync();

    [TestMethod]
    public Task Failures_AreClassifiedAndCallerFailuresArePreserved()
        => _suite!.FailuresAreClassifiedAndCallerFailuresArePreservedAsync();

    [TestMethod]
    public Task AmbiguousDelete_ConvergesOnRetry()
        => _suite!.AmbiguousDeleteConvergesOnRetryAsync();

    private static IObjectStore CreateStore()
        => ObjectStoreTestClient.Create(
            S3ObjectStoreClientFactory.Create(CreateOptions(IntegrationTestFixture.ExternalS3Endpoint)),
            CreateOptions(IntegrationTestFixture.ExternalS3Endpoint));

    private static CentralObjectStorageOptions CreateOptions(string endpoint)
        => new()
        {
            ServiceEndpoint = endpoint,
            Region = "us-east-1",
            UseTls = false,
            AddressingStyle = ObjectStorageAddressingStyle.Path,
            CredentialMode = ObjectStorageCredentialMode.Static,
            AccessKey = IntegrationTestFixture.ExternalS3AccessKey,
            SecretKey = IntegrationTestFixture.ExternalS3SecretKey
        };

    private static IObjectStore CreateFaultedStore(ObjectStoreConformanceFault fault)
    {
        var endpoint = IntegrationTestFixture.ExternalS3Endpoint;
        if (fault == ObjectStoreConformanceFault.Unavailable)
        {
            endpoint = $"127.0.0.1:{GetUnusedPort()}";
        }

        var options = CreateOptions(endpoint);
        if (fault is not ObjectStoreConformanceFault.Unavailable)
        {
            var handler = new ConformanceFaultHandler(fault) { InnerHandler = new SocketsHttpHandler() };
            var httpClient = new HttpClient(handler, disposeHandler: true);
            var client = S3ObjectStoreClientFactory.Create(
                options,
                new ObjectStoreTestClient.SharedHttpClientFactory(httpClient));
            return ObjectStoreTestClient.Create(client, options);
        }
        return ObjectStoreTestClient.Create(S3ObjectStoreClientFactory.Create(options), options);
    }

    private static int GetUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class ConformanceFaultHandler(ObjectStoreConformanceFault fault) : DelegatingHandler
    {
        private int _ambiguousDeletePending = 1;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (fault == ObjectStoreConformanceFault.Timeout)
            {
                throw new TaskCanceledException("Injected object-storage timeout.");
            }
            if (fault is ObjectStoreConformanceFault.Authentication
                or ObjectStoreConformanceFault.Authorization
                or ObjectStoreConformanceFault.Throttled)
            {
                var (status, code) = fault switch
                {
                    ObjectStoreConformanceFault.Authentication =>
                        (HttpStatusCode.Unauthorized, "InvalidAccessKeyId"),
                    ObjectStoreConformanceFault.Authorization =>
                        (HttpStatusCode.Forbidden, "AccessDenied"),
                    _ => (HttpStatusCode.ServiceUnavailable, "SlowDown")
                };
                return new HttpResponseMessage(status)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        $"<Error><Code>{code}</Code><Message>Injected conformance failure</Message></Error>",
                        Encoding.UTF8,
                        "application/xml")
                };
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (fault == ObjectStoreConformanceFault.AmbiguousDelete
                && request.Method == HttpMethod.Delete
                && Interlocked.Exchange(ref _ambiguousDeletePending, 0) == 1)
            {
                response.Dispose();
                throw new HttpRequestException("Injected response loss after delete completion.");
            }
            return response;
        }
    }

}
