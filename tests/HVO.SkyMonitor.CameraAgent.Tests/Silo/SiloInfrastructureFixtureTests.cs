namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Silo")]
public sealed class SiloInfrastructureFixtureTests
{
    [TestMethod]
    public async Task ParallelRegistrationPreservesBucketBeforeDirectoryCreationAndOnReopen()
    {
        var parent = Directory.CreateTempSubdirectory("hvo-silo-fixture-");
        try
        {
            var root = Path.Combine(parent.FullName, "original");
            var otherRoot = Path.Combine(parent.FullName, "concurrent");
            var bucket = SiloInfrastructureFixture.GetOrCreateBucket(root);
            var instanceId = bucket.Instance;
            Assert.IsFalse(Directory.Exists(root));

            var otherBucket = await Task.Factory.StartNew(() => SiloInfrastructureFixture.GetOrCreateBucket(otherRoot),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
            Assert.AreNotEqual(instanceId, otherBucket.Instance);
            Assert.IsFalse(Directory.Exists(root));

            Directory.CreateDirectory(root);
            var reopened = SiloInfrastructureFixture.GetOrCreateBucket(root);
            Assert.AreSame(bucket, reopened);
            Assert.AreEqual(instanceId, reopened.Instance);
            Assert.AreSame(bucket, SiloInfrastructureFixture.GetOrCreateBucket(root + Path.DirectorySeparatorChar));
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task ParallelRegistrationPreservesUnopenedProductStoreAcrossRootAliases()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-silo-fixture-{Guid.NewGuid():N}");
        var provider = SiloInfrastructureFixture.ProductStore(root);

        _ = await Task.Run(() => SiloInfrastructureFixture.ProductStore(root + "-other")).ConfigureAwait(false);

        Assert.IsFalse(Directory.Exists(root));
        Assert.AreSame(provider, SiloInfrastructureFixture.ProductStore(root + Path.DirectorySeparatorChar));
    }
}
