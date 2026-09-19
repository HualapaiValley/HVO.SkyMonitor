using Amazon.S3;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;

internal static partial class ObjectStorageServiceCollectionExtensions
{
    // The provider is chosen here and nowhere else. Every consumer takes IObjectStore; the
    // AWS SDK client is registered only when the S3 provider is selected, so a Filesystem
    // deployment never constructs one. The Filesystem adapter is delivered by #585; until
    // then selecting it is a startup failure with the reason named, which is the honest
    // state rather than a fallback to S3.
    public static IServiceCollection AddObjectStorageInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IObjectStore>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<CentralObjectStorageOptions>>().Value;
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(
                "HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage");
            switch (options.Provider)
            {
                case ObjectStorageProvider.S3:
                    ObjectStorageConfigured(
                        logger,
                        "s3",
                        string.IsNullOrWhiteSpace(options.S3.ServiceEndpoint) ? "default" : "custom",
                        options.S3.CredentialMode.ToString(),
                        options.S3.AddressingStyle.ToString(),
                        options.S3.UseTls);
                    return ActivatorUtilities.CreateInstance<S3ObjectStore>(
                        provider,
                        S3ObjectStoreClientFactory.Create(options.S3));
                case ObjectStorageProvider.Filesystem:
                    throw new InvalidOperationException(
                        "ObjectStorage:Provider=Filesystem is selected but the filesystem provider is not yet delivered (#585).");
                default:
                    throw new InvalidOperationException($"Unknown ObjectStorage:Provider '{options.Provider}'.");
            }
        });
        return services;
    }

    [LoggerMessage(2180, LogLevel.Information,
        "Object storage configured: Provider={Provider} EndpointMode={EndpointMode} CredentialMode={CredentialMode} AddressingStyle={AddressingStyle} UseTls={UseTls}")]
    private static partial void ObjectStorageConfigured(
        ILogger logger,
        string provider,
        string endpointMode,
        string credentialMode,
        string addressingStyle,
        bool useTls);
}
