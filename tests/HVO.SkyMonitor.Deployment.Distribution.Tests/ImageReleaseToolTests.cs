using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class ImageReleaseToolTests
{
    /// <summary>
    /// Runs the release tool and returns its exit code with everything it reported, so a negative test asserts on
    /// the rule it names rather than on any failure the tool can produce.
    /// </summary>
    private static async Task<(int ExitCode, string Diagnostics)> RunAsync(string[] arguments)
    {
        var original = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            var exitCode = await ReleaseTool.Program.Main(arguments);
            return (exitCode, captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private static async Task AssertRejectedAsync(string[] arguments, string expected)
    {
        var (exitCode, diagnostics) = await RunAsync(arguments);
        Assert.AreEqual(1, exitCode, diagnostics);
        StringAssert.Contains(diagnostics, expected, StringComparison.Ordinal);
    }

    private const string Revision = ImageReleaseFixture.Revision;
    private const string MinimumRevision = ImageReleaseFixture.MinimumRevision;
    private const string IndexDigest = ImageReleaseFixture.IndexDigest;
    private const string Repository = ImageReleaseFixture.Repository;
    private static readonly string[] ExpectedArchitectures = ["amd64", "arm64"];

    [TestMethod]
    public async Task CreateImage_SignedRelease_BindsEveryPlatformDigestAndCompatibilityToTheArchiveBytes()
    {
        using var fixture = ImageReleaseFixture.Create();
        var release = Path.Combine(fixture.Root, "release");

        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.CreateArguments(release)));

        var manifestPath = Path.Combine(release, "image-manifest.json");
        var signaturePath = manifestPath + ".sig";
        Assert.AreEqual(0, await ReleaseTool.Program.Main(
            ["sign-local", "--manifest", manifestPath, "--private-key", fixture.PrivateKey, "--signature", signaturePath]));
        Assert.AreEqual(0, await ReleaseTool.Program.Main([
            "verify", "--manifest", manifestPath, "--signature", signaturePath, "--asset-root", release,
            "--public-key", fixture.PublicKey
        ]));

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var signatureBytes = await File.ReadAllBytesAsync(signaturePath);
        var manifest = DistributionVerifier.VerifyManifest(manifestBytes, signatureBytes, fixture.TrustRoot);
        Assert.AreEqual(DistributionManifestKind.ImageRelease, manifest.ManifestKind);
        Assert.AreEqual("image", manifest.Release.Train);
        Assert.AreEqual("image-v1.2.3", manifest.Release.Tag);
        var image = manifest.Images.Single();
        Assert.AreEqual("CameraAgent", image.Component);
        Assert.AreEqual(Repository, image.Repository);
        Assert.AreEqual(IndexDigest, image.ManifestDigest);
        Assert.AreEqual("cameraagent-state-v2", image.Compatibility.StateContract);
        Assert.AreEqual(MinimumRevision, image.Compatibility.MinimumCompatibleRevision);
        Assert.AreEqual(12, image.Compatibility.RawIngressSchema);
        Assert.AreEqual(2, image.Compatibility.CatalogManifestVersion);
        Assert.AreEqual("local-replay-runner-v1", image.Compatibility.ReplayRunnerContract);
        CollectionAssert.AreEqual(
            ExpectedArchitectures,
            image.Platforms.Select(static platform => platform.Architecture).Order(StringComparer.Ordinal).ToArray());
        foreach (var platform in image.Platforms)
        {
            Assert.AreEqual("linux", platform.OperatingSystem);
            Assert.AreEqual(fixture.ManifestDigestFor(platform.Architecture), platform.ManifestDigest);
            Assert.AreEqual(fixture.ImageIdFor(platform.Architecture), platform.OfflineArchiveImageId);
            Assert.AreEqual($"cameraagent-image-v1.2.3-linux-{platform.Architecture}.tar", platform.OfflineArchiveAsset);
            Assert.IsTrue(File.Exists(Path.Combine(release, platform.OfflineArchiveAsset!)));
        }
        Assert.AreEqual(1, manifest.Artifacts.Count(static artifact => artifact.Role == DistributionArtifactRole.VulnerabilityScan));
        Assert.AreEqual(2, manifest.Artifacts.Count(static artifact => artifact.Role == DistributionArtifactRole.ImageArchive));
    }

    [TestMethod]
    public async Task CreateImage_ThenIndex_PublishesTheImageTrainWithAVersionedTag()
    {
        using var fixture = ImageReleaseFixture.Create();
        var release = Path.Combine(fixture.Root, "release");
        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.CreateArguments(release)));
        var manifestPath = Path.Combine(release, "image-manifest.json");
        var manifestSignature = manifestPath + ".sig";
        Assert.AreEqual(0, await ReleaseTool.Program.Main(
            ["sign-local", "--manifest", manifestPath, "--private-key", fixture.PrivateKey, "--signature", manifestSignature]));
        var indexRoot = Path.Combine(fixture.Root, "index");

        Assert.AreEqual(0, await ReleaseTool.Program.Main([
            "create-index", "--train", "image", "--sequence", "1", "--created-utc", "2026-08-24T04:29:18Z",
            "--manifest", manifestPath, "--manifest-signature", manifestSignature, "--public-key", fixture.PublicKey,
            "--signing-key-id", fixture.KeyId, "--output", indexRoot
        ]));

        var indexPath = Path.Combine(indexRoot, "image-release-index.json");
        var indexSignature = indexPath + ".sig";
        Assert.AreEqual(0, await ReleaseTool.Program.Main([
            "sign-local", "--manifest", indexPath, "--private-key", fixture.PrivateKey, "--signature", indexSignature,
            "--metadata-kind", "index"
        ]));
        var indexBytes = await File.ReadAllBytesAsync(indexPath);
        var indexSignatureBytes = await File.ReadAllBytesAsync(indexSignature);
        var index = DistributionVerifier.VerifyIndex(indexBytes, indexSignatureBytes, fixture.TrustRoot);
        Assert.AreEqual("image", index.Train);
        Assert.AreEqual("image-v1.2.3", index.Releases.Single().Tag);
    }

    [TestMethod]
    public async Task CreateImage_ArchitectureThatContradictsTheArchive_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--linux-arm64") + 1] = fixture.Amd64Archive;

        await AssertRejectedAsync(arguments, "but must contain linux/arm64");
    }

    [TestMethod]
    public async Task CreateImage_PlatformsThatDeclareDifferentCompatibility_AreRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var labels = ImageReleaseFixture.DefaultLabels(Revision);
        labels["io.hvo.skymonitor.catalog-contract"] = "hyg-v42-production-p3-s3";
        var divergent = fixture.WriteArchive("divergent-arm64", "arm64", labels);
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--linux-arm64") + 1] = divergent;

        await AssertRejectedAsync(arguments, "declares different labels than");
    }

    [TestMethod]
    public async Task CreateImage_ImageMissingACompatibilityLabel_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var labels = ImageReleaseFixture.DefaultLabels(Revision);
        labels.Remove("io.hvo.skymonitor.raw-ingress-schema");
        var incomplete = fixture.WriteArchive("incomplete-amd64", "amd64", labels);
        var incompleteArm = fixture.WriteArchive("incomplete-arm64", "arm64", labels);
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--linux-amd64") + 1] = incomplete;
        arguments[Array.IndexOf(arguments, "--linux-arm64") + 1] = incompleteArm;

        await AssertRejectedAsync(arguments, "omits the required label 'io.hvo.skymonitor.raw-ingress-schema'");
    }

    [TestMethod]
    public async Task CreateImage_ImageBuiltFromAnotherRevision_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var labels = ImageReleaseFixture.DefaultLabels(new string('c', 40));
        var amd64 = fixture.WriteArchive("other-amd64", "amd64", labels);
        var arm64 = fixture.WriteArchive("other-arm64", "arm64", labels);
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--linux-amd64") + 1] = amd64;
        arguments[Array.IndexOf(arguments, "--linux-arm64") + 1] = arm64;

        await AssertRejectedAsync(arguments, "revision label does not match --revision");
    }

    [TestMethod]
    public async Task CreateImage_ScanReportWithACriticalFinding_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var report = fixture.WriteScanReport("critical-scan.json", critical: 1, fixture.AllImageIds);
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--scan-report") + 1] = report;

        await AssertRejectedAsync(arguments, "must record zero critical findings");
    }

    [TestMethod]
    public async Task CreateImage_ScanReportThatOmitsAPublishedImage_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var report = fixture.WriteScanReport("partial-scan.json", critical: 0, [fixture.AllImageIds[0]]);
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--scan-report") + 1] = report;

        await AssertRejectedAsync(arguments, "does not cover exactly the published images");
    }

    [TestMethod]
    public async Task Verify_SignedPlatformIdentityThatTheArchiveDoesNotProduce_Fails()
    {
        using var fixture = ImageReleaseFixture.Create();
        var release = Path.Combine(fixture.Root, "release");
        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.CreateArguments(release)));
        var manifestPath = Path.Combine(release, "image-manifest.json");
        var text = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(
            manifestPath,
            text.Replace(fixture.ImageIdFor("arm64"), "sha256:" + new string('9', 64), StringComparison.Ordinal),
            new UTF8Encoding(false));
        var signaturePath = manifestPath + ".sig";
        Assert.AreEqual(0, await ReleaseTool.Program.Main(
            ["sign-local", "--manifest", manifestPath, "--private-key", fixture.PrivateKey, "--signature", signaturePath]));

        await AssertRejectedAsync(
            [
                "verify", "--manifest", manifestPath, "--signature", signaturePath, "--asset-root", release,
                "--public-key", fixture.PublicKey
            ],
            "does not match its signed platform identity");
    }

    [TestMethod]
    public async Task Verify_ImageArchiveWhoseBytesChanged_Fails()
    {
        using var fixture = ImageReleaseFixture.Create();
        var release = Path.Combine(fixture.Root, "release");
        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.CreateArguments(release)));
        var manifestPath = Path.Combine(release, "image-manifest.json");
        var signaturePath = manifestPath + ".sig";
        Assert.AreEqual(0, await ReleaseTool.Program.Main(
            ["sign-local", "--manifest", manifestPath, "--private-key", fixture.PrivateKey, "--signature", signaturePath]));
        var archive = Path.Combine(release, "cameraagent-image-v1.2.3-linux-arm64.tar");
        File.Copy(fixture.Amd64Archive, archive, overwrite: true);

        await AssertRejectedAsync(
            [
                "verify", "--manifest", manifestPath, "--signature", signaturePath, "--asset-root", release,
                "--public-key", fixture.PublicKey
            ],
            "does not match its signed identity");
    }

    [TestMethod]
    public async Task Inspect_ArchiveWithARewrittenConfigurationBlob_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var tampered = fixture.WriteTamperedArchive("tampered-amd64");
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--linux-amd64") + 1] = tampered;

        await AssertRejectedAsync(arguments, "does not match its content address");
    }

    [TestMethod]
    public async Task CreateImage_ComponentInventories_ArePublishedAndBoundToTheirOwnPlatform()
    {
        using var fixture = ImageReleaseFixture.Create();
        var release = Path.Combine(fixture.Root, "release");

        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.CreateArguments(release)));

        var manifestPath = Path.Combine(release, "image-manifest.json");
        var signaturePath = manifestPath + ".sig";
        Assert.AreEqual(0, await ReleaseTool.Program.Main(
            ["sign-local", "--manifest", manifestPath, "--private-key", fixture.PrivateKey, "--signature", signaturePath]));
        Assert.AreEqual(0, await ReleaseTool.Program.Main([
            "verify", "--manifest", manifestPath, "--signature", signaturePath, "--asset-root", release,
            "--public-key", fixture.PublicKey
        ]));

        var signedBytes = await File.ReadAllBytesAsync(manifestPath);
        var signatureBytes = await File.ReadAllBytesAsync(signaturePath);
        var manifest = DistributionVerifier.VerifyManifest(signedBytes, signatureBytes, fixture.TrustRoot);
        Assert.AreEqual(DistributionSchemaVersions.ReleaseManifestWithComponentSboms, manifest.SchemaVersion);
        var inventories = manifest.Artifacts
            .Where(static artifact => artifact.Role == DistributionArtifactRole.ComponentSbom)
            .ToArray();
        Assert.AreEqual(2, inventories.Length);
        foreach (var platform in manifest.Images.Single().Platforms)
        {
            var asset = $"image-components-linux-{platform.Architecture}.spdx.json";
            Assert.AreEqual(asset, platform.ComponentSbomAsset);
            var inventory = inventories.Single(artifact => artifact.AssetName == asset);
            Assert.AreEqual("linux", inventory.OperatingSystem);
            Assert.AreEqual(platform.Architecture, inventory.Architecture);
            Assert.AreEqual("application/spdx+json", inventory.MediaType);
            // The published inventory must name the image ID this platform's archive actually loads.
            var inventoryBytes = await File.ReadAllBytesAsync(Path.Combine(release, asset));
            using var document = JsonDocument.Parse(inventoryBytes);
            Assert.IsTrue(document.RootElement.GetProperty("packages").EnumerateArray().Any(package =>
                package.TryGetProperty("annotations", out var annotations) &&
                annotations.EnumerateArray().Any(annotation =>
                    annotation.GetProperty("comment").GetString() ==
                        $"ImageID: {fixture.ImageIdFor(platform.Architecture)}")));
        }
    }

    [TestMethod]
    public async Task CreateImage_InventoryThatDescribesTheOtherArchitecture_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--component-sbom-amd64") + 1] = fixture.ComponentInventoryFor("arm64");

        await AssertRejectedAsync(arguments, "does not name the published image");
    }

    [TestMethod]
    public async Task CreateImage_InventoryThatIsNotSpdx23_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var wrongFormat = fixture.WriteComponentInventory(
            "cyclonedx.spdx.json",
            fixture.ImageIdFor("amd64"),
            ImageReleaseFixture.InventoryComponents,
            spdxVersion: "SPDX-2.2");
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--component-sbom-amd64") + 1] = wrongFormat;

        await AssertRejectedAsync(arguments, "is not an SPDX 2.3 document");
    }

    [TestMethod]
    public async Task CreateImage_InventoryWithoutAUsableComponentList_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var empty = fixture.WriteComponentInventory("empty.spdx.json", fixture.ImageIdFor("arm64"), components: 0);
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--component-sbom-arm64") + 1] = empty;

        await AssertRejectedAsync(arguments, "which cannot describe a published CameraAgent image");
    }

    [TestMethod]
    public async Task CreateImage_PublishableVersionWithoutComponentInventories_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var arguments = fixture.DryRunArguments(Path.Combine(fixture.Root, "release")).ToList();
        arguments[arguments.IndexOf("--version") + 1] = "1.2.3";

        await AssertRejectedAsync(
            [.. arguments],
            "A published image release requires a component inventory for every published platform");
    }

    [TestMethod]
    public async Task CreateImage_OneInventoryWithoutTheOther_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release")).ToList();
        var index = arguments.IndexOf("--component-sbom-arm64");
        arguments.RemoveRange(index, 2);

        await AssertRejectedAsync(
            [.. arguments],
            "must be supplied for every published platform or for none");
    }

    /// <summary>
    /// An unscanned dry run carries no inventory, so it publishes the release-manifest shape released before
    /// inventories existed. The verifier must still accept it, which is what keeps a version 1 release verifiable.
    /// </summary>
    [TestMethod]
    public async Task CreateImage_UnscannedDryRun_PublishesTheVersion1ShapeThatStillVerifies()
    {
        using var fixture = ImageReleaseFixture.Create();
        var release = Path.Combine(fixture.Root, "dryrun");

        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.DryRunArguments(release)));

        var manifestPath = Path.Combine(release, "image-manifest.json");
        var signaturePath = manifestPath + ".sig";
        Assert.AreEqual(0, await ReleaseTool.Program.Main(
            ["sign-local", "--manifest", manifestPath, "--private-key", fixture.PrivateKey, "--signature", signaturePath]));
        Assert.AreEqual(0, await ReleaseTool.Program.Main([
            "verify", "--manifest", manifestPath, "--signature", signaturePath, "--asset-root", release,
            "--public-key", fixture.PublicKey
        ]));

        var signedBytes = await File.ReadAllBytesAsync(manifestPath);
        var signatureBytes = await File.ReadAllBytesAsync(signaturePath);
        var manifest = DistributionVerifier.VerifyManifest(signedBytes, signatureBytes, fixture.TrustRoot);
        Assert.AreEqual(DistributionSchemaVersions.ReleaseManifest, manifest.SchemaVersion);
        Assert.IsFalse(manifest.Artifacts.Any(static artifact => artifact.Role == DistributionArtifactRole.ComponentSbom));
        Assert.IsTrue(manifest.Images.Single().Platforms.All(static platform => platform.ComponentSbomAsset is null));
    }

    /// <summary>
    /// Verification re-derives the inventory's own subject claim from the published bytes, so a release whose
    /// inventory was replaced and re-signed with a document describing another image still fails.
    /// </summary>
    [TestMethod]
    public async Task Verify_ComponentInventoryThatDescribesAnotherImage_Fails()
    {
        using var fixture = ImageReleaseFixture.Create();
        var release = Path.Combine(fixture.Root, "release");
        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.CreateArguments(release)));

        const string asset = "image-components-linux-amd64.spdx.json";
        var substituted = fixture.WriteComponentInventory(
            "substituted.spdx.json", fixture.ImageIdFor("arm64"), ImageReleaseFixture.InventoryComponents);
        var assetPath = Path.Combine(release, asset);
        File.Copy(substituted, assetPath, overwrite: true);

        var manifestPath = Path.Combine(release, "image-manifest.json");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var manifest = JsonNode.Parse(manifestBytes)!;
        var bytes = await File.ReadAllBytesAsync(assetPath);
        foreach (var artifact in manifest["artifacts"]!.AsArray())
        {
            if (artifact!["assetName"]!.GetValue<string>() == asset)
            {
                artifact["length"] = bytes.Length;
                artifact["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            }
        }
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(), new UTF8Encoding(false));
        var signaturePath = manifestPath + ".sig";
        Assert.AreEqual(0, await ReleaseTool.Program.Main(
            ["sign-local", "--manifest", manifestPath, "--private-key", fixture.PrivateKey, "--signature", signaturePath]));

        await AssertRejectedAsync(
            [
                "verify", "--manifest", manifestPath, "--signature", signaturePath, "--asset-root", release,
                "--public-key", fixture.PublicKey
            ],
            "does not name the published image");
    }

    /// <summary>
    /// An inventory that keeps its own correct subject but also carries a claim for another image must be
    /// rejected. Counting only the expected claim would let an arm64 inventory be signed as the amd64 one.
    /// </summary>
    [TestMethod]
    public async Task CreateImage_InventoryThatAlsoClaimsAnotherImage_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var smuggled = fixture.WriteComponentInventory(
            "two-subjects.spdx.json",
            fixture.ImageIdFor("amd64"),
            ImageReleaseFixture.InventoryComponents,
            additionalImageId: fixture.ImageIdFor("arm64"));
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--component-sbom-amd64") + 1] = smuggled;

        await AssertRejectedAsync(arguments, "as its single subject");
    }

    /// <summary>SPDX 2.3 clause 8.4 makes a SHA-1 checksum mandatory on every file the document declares.</summary>
    [TestMethod]
    public async Task CreateImage_InventoryWithAFileMissingItsSha1Checksum_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var withFiles = fixture.WriteComponentInventory(
            "files-without-sha1.spdx.json",
            fixture.ImageIdFor("arm64"),
            ImageReleaseFixture.InventoryComponents,
            includeFileWithoutSha1: true);
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--component-sbom-arm64") + 1] = withFiles;

        await AssertRejectedAsync(arguments, "without the SHA-1 checksum SPDX 2.3 requires");
    }

    [TestMethod]
    public async Task CreateImage_InventoryThatIsNotValidJson_NamesThePlatformAndTheFile()
    {
        using var fixture = ImageReleaseFixture.Create();
        var malformed = Path.Combine(fixture.Root, "malformed.spdx.json");
        await File.WriteAllTextAsync(malformed, "{ \"spdxVersion\": ", new UTF8Encoding(false));
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--component-sbom-amd64") + 1] = malformed;

        await AssertRejectedAsync(arguments, $"The linux/amd64 component inventory '{malformed}' is not valid JSON.");
    }

    /// <summary>Pins the component floor itself, so the boundary is a decision rather than an accident.</summary>
    [TestMethod]
    public async Task CreateImage_InventoryOneComponentBelowTheFloor_IsRejectedAndAtTheFloorIsAccepted()
    {
        using var fixture = ImageReleaseFixture.Create();
        var below = fixture.WriteComponentInventory("below.spdx.json", fixture.ImageIdFor("amd64"), components: 31);
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--component-sbom-amd64") + 1] = below;

        await AssertRejectedAsync(arguments, "records 31 components");

        var atFloor = fixture.WriteComponentInventory("at-floor.spdx.json", fixture.ImageIdFor("amd64"), components: 32);
        var accepted = fixture.CreateArguments(Path.Combine(fixture.Root, "accepted"));
        accepted[Array.IndexOf(accepted, "--component-sbom-amd64") + 1] = atFloor;

        Assert.AreEqual(0, await ReleaseTool.Program.Main(accepted));
    }

    /// <summary>
    /// The scan report is produced outside the tool, so a member present with the wrong JSON type is ordinary
    /// malformed input. It must fail as a release error rather than as an unhandled exception from GetString().
    /// </summary>
    [TestMethod]
    public async Task CreateImage_ScanReportWithANonStringMember_FailsAsAReleaseError()
    {
        using var fixture = ImageReleaseFixture.Create();
        var malformed = Path.Combine(fixture.Root, "typed-scan.json");
        var report = await File.ReadAllTextAsync(fixture.ScanReport);
        await File.WriteAllTextAsync(
            malformed,
            report.Replace("\"scanner\":\"trivy\"", "\"scanner\":123", StringComparison.Ordinal),
            new UTF8Encoding(false));
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--scan-report") + 1] = malformed;

        await AssertRejectedAsync(arguments, "does not declare a scanner and scan time");
    }

    [TestMethod]
    public async Task CreateImage_ScanReportWithANonObjectSubject_FailsAsAReleaseError()
    {
        using var fixture = ImageReleaseFixture.Create();
        var malformed = Path.Combine(fixture.Root, "subject-scan.json");
        var report = await File.ReadAllTextAsync(fixture.ScanReport);
        var opening = report.IndexOf("\"subjects\":[", StringComparison.Ordinal);
        Assert.IsTrue(opening >= 0, report);
        await File.WriteAllTextAsync(
            malformed,
            report.Insert(opening + "\"subjects\":[".Length, "\"not-an-object\","),
            new UTF8Encoding(false));
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--scan-report") + 1] = malformed;

        await AssertRejectedAsync(arguments, "lists an invalid scanned subject");
    }

    /// <summary>
    /// A present-but-non-array <c>files</c> member must be refused rather than treated as absent, or a malformed
    /// document would skip the per-file rule entirely by declaring files in the wrong shape.
    /// </summary>
    [TestMethod]
    public async Task CreateImage_InventoryWhoseFilesMemberIsNotAnArray_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var malformed = fixture.WriteComponentInventory(
            "files-not-array.spdx.json",
            fixture.ImageIdFor("amd64"),
            ImageReleaseFixture.InventoryComponents,
            filesNotAnArray: true);
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--component-sbom-amd64") + 1] = malformed;

        await AssertRejectedAsync(arguments, "declares a 'files' member that is not an array");
    }

    /// <summary>
    /// A subject object with no usable image ID must be refused, not filtered out: filtering would let junk sit
    /// alongside the correct entries and still satisfy the "covers exactly the published images" rule.
    /// </summary>
    [TestMethod]
    public async Task CreateImage_ScanReportSubjectWithoutAUsableImageId_IsRejected()
    {
        using var fixture = ImageReleaseFixture.Create();
        var malformed = Path.Combine(fixture.Root, "empty-subject-scan.json");
        var report = await File.ReadAllTextAsync(fixture.ScanReport);
        var opening = report.IndexOf("\"subjects\":[", StringComparison.Ordinal);
        Assert.IsTrue(opening >= 0, report);
        await File.WriteAllTextAsync(
            malformed,
            report.Insert(opening + "\"subjects\":[".Length, "{\"architecture\":\"amd64\"},"),
            new UTF8Encoding(false));
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, "release"));
        arguments[Array.IndexOf(arguments, "--scan-report") + 1] = malformed;

        await AssertRejectedAsync(arguments, "lists an invalid scanned subject");
    }

    /// <summary>
    /// The signature covers the exact manifest bytes, so identical inputs must produce identical bytes. The
    /// component inventories are gathered into a dictionary before they reach the artifact list, and dictionary
    /// enumeration order is not a documented guarantee, so this pins the ordering rather than trusting it.
    /// </summary>
    [TestMethod]
    public async Task CreateImage_IdenticalInputs_ProduceIdenticalSignedManifestBytes()
    {
        using var fixture = ImageReleaseFixture.Create();
        var first = Path.Combine(fixture.Root, "first");
        var second = Path.Combine(fixture.Root, "second");

        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.CreateArguments(first)));
        Assert.AreEqual(0, await ReleaseTool.Program.Main(fixture.CreateArguments(second)));

        var firstNames = Directory.GetFiles(first).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(
            firstNames,
            Directory.GetFiles(second).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        foreach (var name in firstNames)
        {
            CollectionAssert.AreEqual(
                await File.ReadAllBytesAsync(Path.Combine(first, name!)),
                await File.ReadAllBytesAsync(Path.Combine(second, name!)),
                name);
        }

        // The component inventories must sit in a stable, data-derived position in the signed artifact list.
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(first, "image-manifest.json"));
        using var document = JsonDocument.Parse(manifestBytes);
        var inventories = document.RootElement.GetProperty("artifacts").EnumerateArray()
            .Where(static artifact => artifact.GetProperty("role").GetString() == "ComponentSbom")
            .Select(static artifact => artifact.GetProperty("architecture").GetString())
            .ToArray();
        CollectionAssert.AreEqual(ExpectedArchitectures, inventories);
    }
}
