using System.Net;
using System.Net.Sockets;
using System.Text;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class S3ObjectStoreConformanceTests
{
    private const string Bucket = "skymonitor-artifacts";
    private ObjectStoreConformanceSuite _suite = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        var services = AssemblyHooks.Fixture.Factory.Services;
        var minio = services.GetRequiredService<IMinioClient>();
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket)).ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket)).ConfigureAwait(false);
        }
        _suite = new ObjectStoreConformanceSuite(
            services.GetRequiredService<IObjectStore>(),
            CreateFaultedStore,
            Bucket,
            $"issue-504/conformance/{Guid.NewGuid():N}/");
    }

    [TestCleanup]
    public Task CleanupAsync() => _suite.CleanupAsync();

    [TestMethod]
    public Task MaximumStreamingPublicationConditionalReadAndDelete_Conform()
        => _suite.MaximumStreamingPublicationConditionalReadAndDeleteAsync();

    [TestMethod]
    public Task Listing_IsCompleteOrdinalAndCrossesProviderPages()
        => _suite.ListingIsCompleteOrdinalAndCrossesProviderPagesAsync();

    [TestMethod]
    public Task Failures_AreClassifiedAndCallerFailuresArePreserved()
        => _suite.FailuresAreClassifiedAndCallerFailuresArePreservedAsync();

    [TestMethod]
    public Task AmbiguousDelete_ConvergesOnRetry()
        => _suite.AmbiguousDeleteConvergesOnRetryAsync();

    private static IObjectStore CreateFaultedStore(ObjectStoreConformanceFault fault)
    {
        var endpoint = AssemblyHooks.Fixture.MinioEndpoint;
        if (fault == ObjectStoreConformanceFault.Unavailable)
        {
            endpoint = $"127.0.0.1:{GetUnusedPort()}";
        }

        var options = new CentralObjectStorageOptions
        {
            ServiceEndpoint = endpoint,
            Region = "us-east-1",
            UseTls = false,
            AddressingStyle = ObjectStorageAddressingStyle.Path,
            CredentialMode = ObjectStorageCredentialMode.Static,
            AccessKey = IntegrationTestFixture.MinioAccessKey,
            SecretKey = IntegrationTestFixture.MinioSecretKey
        };
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
