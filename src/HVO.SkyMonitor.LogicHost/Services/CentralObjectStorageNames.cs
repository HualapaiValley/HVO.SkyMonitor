using HVO.SkyMonitor.LogicHost.Configuration;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralObjectStorageNames
{
    private readonly CentralObjectStorageOptions _options;

    public CentralObjectStorageNames(IOptions<CentralObjectStorageOptions>? options = null)
    {
        _options = options?.Value ?? new CentralObjectStorageOptions();
    }

    public string ArtifactBucket => _options.ArtifactBucket;

    public string ArtifactPrefix => _options.ArtifactPrefix;
}
