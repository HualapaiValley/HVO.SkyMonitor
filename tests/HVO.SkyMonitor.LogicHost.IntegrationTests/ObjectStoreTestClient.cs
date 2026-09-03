using Amazon.Runtime;
using Amazon.S3;
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

    public static bool IsNotFound(Minio.Exceptions.MinioException exception)
        => exception is Minio.Exceptions.ObjectNotFoundException
            || exception.Response?.Code is "NoSuchKey" or "NoSuchObject";

    internal sealed class SharedHttpClientFactory(HttpClient client) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => client;
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    }
}
