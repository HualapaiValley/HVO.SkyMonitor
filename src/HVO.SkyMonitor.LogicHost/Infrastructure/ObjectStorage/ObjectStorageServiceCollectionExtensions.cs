using Amazon.S3;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;

internal static partial class ObjectStorageServiceCollectionExtensions
{
    public static IServiceCollection AddObjectStorageInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IAmazonS3>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<CentralObjectStorageOptions>>().Value;
            ObjectStorageConfigured(
                provider.GetRequiredService<ILoggerFactory>().CreateLogger(
                    "HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage"),
                string.IsNullOrWhiteSpace(options.ServiceEndpoint) ? "default" : "custom",
                options.CredentialMode.ToString(),
                options.AddressingStyle.ToString(),
                options.UseTls);
            return S3ObjectStoreClientFactory.Create(options);
        });
        services.AddSingleton<IObjectStore, S3ObjectStore>();
        return services;
    }

    [LoggerMessage(2180, LogLevel.Information,
        "Object storage configured: EndpointMode={EndpointMode} CredentialMode={CredentialMode} AddressingStyle={AddressingStyle} UseTls={UseTls}")]
    private static partial void ObjectStorageConfigured(
        ILogger logger,
        string endpointMode,
        string credentialMode,
        string addressingStyle,
        bool useTls);
}
