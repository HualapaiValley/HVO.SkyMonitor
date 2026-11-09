using System.Text.Json;
using HVO.SkyMonitor.Deployment.ReleaseTool;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class ReleaseCatalogBoundaryTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(32)]
    public void CatalogManifestReadHonorsTheByteBoundAndNeverReadsPastOneExtraByte(int extraBytes)
    {
        var bytes = new byte[Program.MaximumCatalogManifestLength + extraBytes];
        bytes.AsSpan().Fill(0x20);
        using var stream = new MemoryStream(bytes, writable: false);

        if (extraBytes == 0)
        {
            CollectionAssert.AreEqual(bytes, Program.ReadCatalogManifest(stream));
        }
        else
        {
            var failure = Assert.ThrowsExactly<ReleaseToolException>(() => Program.ReadCatalogManifest(stream));
            StringAssert.Contains(failure.Message, "exceeds its maximum length", StringComparison.Ordinal);
        }
        Assert.AreEqual((long)Math.Min(bytes.Length, Program.MaximumCatalogManifestLength + 1), stream.Position);
    }

    [TestMethod]
    public async Task InvalidCatalogBundleIsStagedAndRefusedWithoutPublishingOrChangingItsInputs()
    {
        using var fixture = OciReleaseFixture.Create();
        var bundle = Directory.CreateDirectory(Path.Combine(fixture.Root, "catalog-bundle")).FullName;
        var specification = ReleaseCatalogs.Specifications.All.First();
        foreach (var name in ReleaseCatalogs.OrderedFiles(specification))
        {
            await File.WriteAllTextAsync(Path.Combine(bundle, name), "synthetic invalid catalog input");
        }
        // Registry identity and exact file membership cannot make an unsupported manifest trustworthy.
        await File.WriteAllTextAsync(Path.Combine(bundle, "manifest.json"), JsonSerializer.Serialize(new
        {
            manifestVersion = 0,
            package = new { version = specification.PackageVersionPrefix + "1" },
            catalog = new { id = specification.CatalogId }
        }));
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(bundle))
        {
            originals.Add(Path.GetFileName(path), await File.ReadAllBytesAsync(path));
        }
        var output = Path.Combine(fixture.Root, "release");
        using var error = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            Assert.AreEqual(1, await Program.Main(fixture.Arguments(output, "--catalog-bundle", bundle)));
        }
        finally
        {
            Console.SetError(previousError);
        }

        StringAssert.Contains(error.ToString(), "The catalog bundle does not match its approved specification:", StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(output), "A refused catalog must leave no partial release assets.");
        CollectionAssert.AreEquivalent(originals.Keys.ToArray(), Directory.GetFiles(bundle).Select(Path.GetFileName).ToArray());
        foreach (var (name, bytes) in originals)
        {
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(bundle, name)), name);
        }
    }
}
