using Amazon.S3;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;

internal static partial class ObjectStorageServiceCollectionExtensions
{
    // The provider is chosen here and nowhere else. Every consumer takes IObjectStore; the
    // AWS SDK client is constructed only when the S3 provider is selected, so a Filesystem
    // deployment never touches the SDK.
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
                    ObjectStorageConfigured(logger, "filesystem", "local", "n/a", "n/a", false);
                    return ActivatorUtilities.CreateInstance<FilesystemObjectStore>(provider);
                default:
                    throw new InvalidOperationException($"Unknown ObjectStorage:Provider '{options.Provider}'.");
            }
        });
        // The reconciliation worker registers unconditionally and exits immediately when the
        // provider is not the filesystem one, so the service graph does not depend on options
        // being resolved at registration time.
        services.AddSingleton<FilesystemObjectReconciliationWorker>();
        services.AddSingleton<IHostedService, FilesystemObjectReconciliationHostedService>();
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
