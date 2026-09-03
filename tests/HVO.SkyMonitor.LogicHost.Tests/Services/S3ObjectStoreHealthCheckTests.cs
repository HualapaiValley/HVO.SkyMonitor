using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class S3ObjectStoreHealthCheckTests
{
    [TestMethod]
    public async Task AuthenticatedRequiredBuckets_AreHealthy()
    {
        var store = CreateStore();
        var healthCheck = CreateHealthCheck(store);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(HealthStatus.Healthy, result.Status);
        Assert.AreEqual("both-required-buckets-authenticated", result.Data["Reason"]);
    }

    [TestMethod]
    public async Task RetryableOutage_BecomesUnhealthyAndRecovers()
    {
        var store = CreateStore();
        store.FailNext("bucket-exists", ObjectStoreFailureKind.Transient);
        store.FailNext("bucket-exists", ObjectStoreFailureKind.Transient);
        store.FailNext("bucket-exists", ObjectStoreFailureKind.Transient);
        var healthCheck = CreateHealthCheck(store);
        var context = new HealthCheckContext();

        Assert.AreEqual(
            HealthStatus.Degraded,
            (await healthCheck.CheckHealthAsync(context).ConfigureAwait(false)).Status);
        Assert.AreEqual(
            HealthStatus.Degraded,
            (await healthCheck.CheckHealthAsync(context).ConfigureAwait(false)).Status);
        Assert.AreEqual(
            HealthStatus.Unhealthy,
            (await healthCheck.CheckHealthAsync(context).ConfigureAwait(false)).Status);
        Assert.AreEqual(
            HealthStatus.Healthy,
            (await healthCheck.CheckHealthAsync(context).ConfigureAwait(false)).Status);
    }

    [TestMethod]
    public async Task MissingOrDeniedBucket_IsImmediatelyUnhealthy()
    {
        var missingStore = new DeterministicObjectStore();
        missingStore.AddBucket(CentralObjectStorageOptions.DefaultArtifactBucket);
        Assert.AreEqual(
            HealthStatus.Unhealthy,
            (await CreateHealthCheck(missingStore).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false)).Status);

        var deniedStore = CreateStore();
        deniedStore.FailNext("bucket-exists", ObjectStoreFailureKind.Authorization);
        var denied = await CreateHealthCheck(deniedStore).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Unhealthy, denied.Status);
        Assert.IsNull(denied.Exception);
        Assert.DoesNotContain(CentralObjectStorageOptions.DefaultArtifactBucket, denied.Description ?? string.Empty);
    }

    private static DeterministicObjectStore CreateStore()
    {
        var store = new DeterministicObjectStore();
        store.AddBucket(CentralObjectStorageOptions.DefaultArtifactBucket);
        store.AddBucket(CentralObjectStorageOptions.DefaultDiagnosticsBucket);
        return store;
    }

    private static ObjectStoreHealthCheck CreateHealthCheck(IObjectStore store)
        => new(
            store,
            Options.Create(new CentralObjectStorageOptions()),
            NullLogger<ObjectStoreHealthCheck>.Instance);
}
