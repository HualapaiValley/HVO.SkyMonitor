using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Tests.Services;

/// <summary>
/// The universal conformance suite run against the filesystem provider. It needs no container,
/// so it is a Unit test: a temporary root, two precreated bucket directories, and the real
/// provider. The suite is the same one the S3 provider passes; this class declares no
/// transport-fault capabilities, so the universal assertions run and the networked ones do not.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class FilesystemObjectStoreConformanceTests
{
    private const string Bucket = "skymonitor-artifacts";
    private string _temp = null!;
    private FilesystemObjectStore _store = null!;
    private ObjectStoreConformanceSuite _suite = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temp = Path.Combine(Path.GetTempPath(), "hvo-fsos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_temp, Bucket));
        Directory.CreateDirectory(Path.Combine(_temp, "skymonitor-diagnostics"));
        _store = Create(_temp);
        _suite = new ObjectStoreConformanceSuite(
            _store,
            _ => throw new InvalidOperationException("the filesystem provider declares no injectable transport faults"),
            Bucket,
            $"conformance/{Guid.NewGuid():N}/",
            ObjectStoreConformanceCapabilities.None);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    internal static FilesystemObjectStore Create(string root)
    {
        var options = new CentralObjectStorageOptions { Provider = ObjectStorageProvider.Filesystem };
        options.Filesystem.Root = root;
        var wrapped = Options.Create(options);
        return new FilesystemObjectStore(wrapped, new ObjectStoreTelemetry(wrapped), TimeProvider.System, NullLogger<FilesystemObjectStore>.Instance);
    }

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
    public Task AmbiguousDelete_IsNotAReachableStateForThisProvider()
        => _suite.AmbiguousDeleteConvergesOnRetryAsync();
}
