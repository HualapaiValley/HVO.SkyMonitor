using Amazon.Runtime;
using Amazon.S3;
using System.Net;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minio;

namespace HVO.SkyMonitor.IntegrationTests;

internal static class ObjectStoreTestClient
{
    public static void Replace(IServiceCollection services)
    {
        services.RemoveAll<IObjectStore>();
        services.AddSingleton<IObjectStore>(provider => Create(provider.GetRequiredService<IMinioClient>()));
    }

    public static void Replace(IServiceCollection services, IObjectStore inner, HttpMessageHandler handler)
    {
        services.RemoveAll<IObjectStore>();
        services.AddSingleton<IObjectStore>(Create(inner, handler));
    }

    public static IObjectStore Create(IMinioClient client)
    {
        var config = client.Config;
        var options = new CentralObjectStorageOptions
        {
            ServiceEndpoint = config.BaseUrl,
            Region = config.Region ?? "us-east-1",
            UseTls = config.Secure,
            AddressingStyle = ObjectStorageAddressingStyle.Path,
            CredentialMode = string.IsNullOrWhiteSpace(config.SessionToken)
                ? ObjectStorageCredentialMode.Static
                : ObjectStorageCredentialMode.Session,
            AccessKey = config.AccessKey,
            SecretKey = config.SecretKey,
            SessionToken = config.SessionToken
        };
        var s3Client = S3ObjectStoreClientFactory.Create(
            options,
            new SharedHttpClientFactory(config.HttpClient));
        return Create(s3Client, options);
    }

    public static IObjectStore Create(IAmazonS3 client, CentralObjectStorageOptions? options = null)
    {
        var configuredOptions = Options.Create(options ?? new CentralObjectStorageOptions());
        return new S3ObjectStore(
            client,
            new ObjectStoreTelemetry(configuredOptions),
            TimeProvider.System,
            NullLogger<S3ObjectStore>.Instance);
    }

    public static IObjectStore Create(IObjectStore inner, HttpMessageHandler handler)
        => new HandlerObjectStore(inner, handler);

    public static bool IsNotFound(Minio.Exceptions.MinioException exception)
        => exception is Minio.Exceptions.ObjectNotFoundException
            || exception.Response?.Code is "NoSuchKey" or "NoSuchObject";

    internal sealed class SharedHttpClientFactory(HttpClient client) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => client;
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    }

    private sealed class HandlerObjectStore : IObjectStore, IDisposable
    {
        private readonly IObjectStore inner;
        private readonly HttpMessageInvoker invoker;

        public HandlerObjectStore(IObjectStore inner, HttpMessageHandler handler)
        {
            this.inner = inner;
            if (handler is DelegatingHandler delegating)
            {
                delegating.InnerHandler = new ObjectStoreHandler(inner);
            }
            invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        }

        public Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken)
            => inner.BucketExistsAsync(bucket, cancellationToken);

        public Task PutAsync(string bucket, string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
            => inner.PutAsync(bucket, key, content, contentLength, contentType, cancellationToken);

        public async Task<ObjectStoreObjectMetadata> StatAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(HttpMethod.Head, bucket, key, cancellationToken).ConfigureAwait(false);
            return await inner.StatAsync(bucket, key, cancellationToken).ConfigureAwait(false);
        }

        public async Task ReadAsync(string bucket, string key, string? generation, Func<Stream, CancellationToken, Task> reader, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(HttpMethod.Get, bucket, key, "read", cancellationToken).ConfigureAwait(false);
            await inner.ReadAsync(bucket, key, generation, reader, cancellationToken).ConfigureAwait(false);
        }

        public async Task CopyAsync(string bucket, string sourceKey, string destinationKey, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                new Uri($"http://object-store/{bucket}/{Uri.EscapeDataString(destinationKey)}"));
            request.Headers.TryAddWithoutValidation("x-amz-copy-source", Uri.EscapeDataString(sourceKey));
            using var response = await SendAsync(request, "copy", cancellationToken).ConfigureAwait(false);
        }

        public async Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(HttpMethod.Delete, bucket, key, "delete", cancellationToken).ConfigureAwait(false);
        }

        public IAsyncEnumerable<ObjectStoreItem> ListAsync(string bucket, string prefix, CancellationToken cancellationToken, string? startAfter = null)
            => inner.ListAsync(bucket, prefix, cancellationToken, startAfter);

        public void Dispose() => invoker.Dispose();

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string bucket, string key, CancellationToken cancellationToken)
            => await SendAsync(method, bucket, key, method == HttpMethod.Head ? "stat" : method == HttpMethod.Get ? "read" : "request", cancellationToken)
                .ConfigureAwait(false);

        private async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string bucket,
            string key,
            string operation,
            CancellationToken cancellationToken)
            => await SendAsync(
                new HttpRequestMessage(method, new Uri($"http://object-store/{bucket}/{Uri.EscapeDataString(key)}")),
                operation,
                cancellationToken).ConfigureAwait(false);

        private async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            string operation,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }
                var kind = response.StatusCode switch
                {
                    HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => ObjectStoreFailureKind.Authorization,
                    HttpStatusCode.NotFound => ObjectStoreFailureKind.MissingObject,
                    HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => ObjectStoreFailureKind.Timeout,
                    _ when (int)response.StatusCode >= 500 => ObjectStoreFailureKind.Transient,
                    _ => ObjectStoreFailureKind.Ambiguous
                };
                response.Dispose();
                throw new ObjectStoreException(kind, operation);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ObjectStoreException(ObjectStoreFailureKind.Timeout, operation, exception);
            }
            catch (HttpRequestException exception)
            {
                throw new ObjectStoreException(ObjectStoreFailureKind.Ambiguous, operation, exception);
            }
        }
    }

    private sealed class ObjectStoreHandler(IObjectStore inner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/', 2);
            var bucket = segments[0];
            var key = Uri.UnescapeDataString(segments[1]);
            try
            {
                if (request.Method == HttpMethod.Delete)
                {
                    await inner.DeleteAsync(bucket, key, cancellationToken).ConfigureAwait(false);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                if (request.Method == HttpMethod.Put && request.Headers.TryGetValues("x-amz-copy-source", out var sources))
                {
                    await inner.CopyAsync(bucket, Uri.UnescapeDataString(sources.Single()), key, cancellationToken)
                        .ConfigureAwait(false);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                if (request.Method == HttpMethod.Get)
                {
                    _ = await inner.StatAsync(bucket, key, cancellationToken).ConfigureAwait(false);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                _ = await inner.StatAsync(bucket, key, cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            catch (ObjectStoreException exception) when (exception.Kind == ObjectStoreFailureKind.MissingObject)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }
}
