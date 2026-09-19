using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using HVO.SkyMonitor.LogicHost.Configuration;

namespace HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;

internal static class S3ObjectStoreClientFactory
{
    // This factory reads only the S3 transport group: it is the one place the AWS SDK meets
    // configuration, and it must not see the neutral or filesystem surface.
    public static IAmazonS3 Create(CentralObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create(options.S3, null);
    }

    public static IAmazonS3 Create(S3ObjectStorageOptions options)
        => Create(options, null);

    internal static IAmazonS3 Create(CentralObjectStorageOptions options, HttpClientFactory? httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create(options.S3, httpClientFactory);
    }

    internal static IAmazonS3 Create(S3ObjectStorageOptions options, HttpClientFactory? httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        var config = CreateConfig(options);
        config.HttpClientFactory = httpClientFactory;
        return options.CredentialMode switch
        {
            ObjectStorageCredentialMode.DefaultChain => new AmazonS3Client(config),
            ObjectStorageCredentialMode.Static => new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKey!, options.SecretKey!), config),
            ObjectStorageCredentialMode.Session => new AmazonS3Client(
                new SessionAWSCredentials(options.AccessKey!, options.SecretKey!, options.SessionToken!), config),
            _ => throw new InvalidOperationException("Object-storage credential mode is unsupported.")
        };
    }

    internal static AmazonS3Config CreateConfig(CentralObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CreateConfig(options.S3);
    }

    internal static AmazonS3Config CreateConfig(S3ObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var hasCustomEndpoint = !string.IsNullOrWhiteSpace(options.ServiceEndpoint);
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.AddressingStyle == ObjectStorageAddressingStyle.Path,
            MaxErrorRetry = 0,
            RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region)
        };
        if (hasCustomEndpoint)
        {
            config.ServiceURL = $"{(options.UseTls ? "https" : "http")}://{options.ServiceEndpoint}";
            config.AuthenticationRegion = options.Region;
        }
        return config;
    }
}
