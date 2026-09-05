using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Distribution;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed partial class DistributionVerifierTests
{
    [TestMethod]
    public void VerifyManifest_ExactSignedBytes_AreAccepted()
    {
        using var fixture = SigningFixture.Create();
        var bytes = fixture.ManifestBytes();

        var result = DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot);

        Assert.AreEqual("1.0.0", result.Release.Version);
        Assert.AreEqual(DistributionManifestKind.InstallerRelease, result.ManifestKind);
    }

    [TestMethod]
    public void VerifyManifest_TamperedBytes_AreRejected()
    {
        using var fixture = SigningFixture.Create();
        var original = fixture.ManifestBytes();
        var tampered = original.ToArray();
        tampered[^2] ^= 1;

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(tampered, fixture.Sign(original), fixture.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_UntrustedKey_IsRejected()
    {
        using var trusted = SigningFixture.Create();
        using var untrusted = SigningFixture.Create();
        var bytes = trusted.ManifestBytes();

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, untrusted.Sign(bytes), trusted.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_NoncanonicalSignatureText_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var bytes = fixture.ManifestBytes();
        var signature = fixture.Sign(bytes);
        byte[] noncanonical = [.. signature.AsSpan(0, 4), (byte)' ', .. signature.AsSpan(4)];

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, noncanonical, fixture.TrustRoot));
    }

    [TestMethod]
    [DataRow("\"schemaVersion\":1,")]
    [DataRow("\"unknown\":true,")]
    public void VerifyManifest_DuplicateOrUnknownMembers_AreRejected(string injectedMember)
    {
        using var fixture = SigningFixture.Create();
        var json = Encoding.UTF8.GetString(fixture.ManifestBytes()).Insert(1, injectedMember);
        var bytes = Encoding.UTF8.GetBytes(json);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_UnsafeAssetName_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var manifest = fixture.Manifest() with
        {
            Artifacts = fixture.Manifest().Artifacts
                .Select((artifact, index) => index == 0 ? artifact with { AssetName = "../installer" } : artifact)
                .ToArray()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DistributionJsonContext.Default.DistributionReleaseManifest);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public async Task VerifyAssetAsync_WrongLengthOrHash_IsRejected()
    {
        var bytes = "asset"u8.ToArray();
        var artifact = new DistributionArtifact(
            DistributionArtifactRole.Installer,
            "installer.tar.gz",
            "application/gzip",
            bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        await using var valid = new MemoryStream(bytes);
        await DistributionVerifier.VerifyAssetAsync(valid, artifact, CancellationToken.None);

        await using var invalid = new MemoryStream("other"u8.ToArray());
        await Assert.ThrowsExactlyAsync<DistributionValidationException>(
            () => DistributionVerifier.VerifyAssetAsync(invalid, artifact, CancellationToken.None));
    }

    [TestMethod]
    public async Task VerifyAssetAsync_OversizedSignedLength_IsRejectedBeforeReading()
    {
        var artifact = new DistributionArtifact(
            DistributionArtifactRole.Installer,
            "installer.tar.gz",
            "application/gzip",
            DistributionVerifier.MaximumInstallerBytes + 1,
            new string('a', 64));
        await using var stream = new MemoryStream();

        await Assert.ThrowsExactlyAsync<DistributionValidationException>(
            () => DistributionVerifier.VerifyAssetAsync(stream, artifact, CancellationToken.None));
    }

    [TestMethod]
    public void VerifyManifest_MissingRequiredArchitecture_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var manifest = fixture.Manifest() with
        {
            Artifacts = fixture.Manifest().Artifacts
                .Where(static artifact => artifact.Architecture != "arm64")
                .ToArray()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DistributionJsonContext.Default.DistributionReleaseManifest);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_DuplicateEvidenceRole_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var manifest = fixture.Manifest();
        var artifacts = manifest.Artifacts.Append(new DistributionArtifact(
            DistributionArtifactRole.Sbom,
            "second-sbom.spdx.json",
            "application/spdx+json",
            1,
            new string('d', 64))).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest with { Artifacts = artifacts }, DistributionJsonContext.Default.DistributionReleaseManifest);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_NewerProductionCatalogRevision_IsAccepted()
    {
        using var fixture = SigningFixture.Create();
        var manifest = fixture.CatalogManifest("hyg-v4.2-p3-s2-r2");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DistributionJsonContext.Default.DistributionReleaseManifest);

        var result = DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot);

        Assert.AreEqual("hyg-v4.2-p3-s2-r2", result.Catalog?.PackageVersion);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public void VerifyManifest_NoncanonicalCatalogManifestVersion_IsRejected(int manifestVersion)
    {
        using var fixture = SigningFixture.Create();
        var manifest = fixture.CatalogManifest("hyg-v4.2-p3-s2-r1");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest with { Catalog = manifest.Catalog! with { ManifestVersion = manifestVersion } },
            DistributionJsonContext.Default.DistributionReleaseManifest);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    [DataRow("hyg-v4.2-p3-s2-r0")]
    [DataRow("other-v1")]
    public void VerifyManifest_InvalidProductionCatalogVersion_IsRejected(string version)
    {
        using var fixture = SigningFixture.Create();
        var manifest = fixture.CatalogManifest(version);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DistributionJsonContext.Default.DistributionReleaseManifest);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public void IsCanonicalKeyId_HistoricalFingerprint_IsAccepted()
    {
        Assert.IsTrue(DistributionTrustRoot.IsCanonicalKeyId($"p256-sha256:{new string('f', 64)}"));
        Assert.IsFalse(DistributionTrustRoot.IsCanonicalKeyId($"p256-sha256:{new string('F', 64)}"));
    }

    [TestMethod]
    public void VerifyIndex_SignedDefaultVersion_IsAccepted()
    {
        using var fixture = SigningFixture.Create();
        var index = new DistributionReleaseIndex(
            DistributionSchemaVersions.ReleaseIndex,
            "release-index",
            "installer",
            1,
            DateTimeOffset.Parse("2026-08-24T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            "1.0.0",
            new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, fixture.TrustRoot.KeyId),
            [new DistributionReleaseReference(
                "1.0.0",
                "installer-v1.0.0",
                "release-manifest.json",
                1024,
                new string('a', 64),
                "release-manifest.json.sig")]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(index, DistributionJsonContext.Default.DistributionReleaseIndex);

        var result = DistributionVerifier.VerifyIndex(bytes, fixture.Sign(bytes), fixture.TrustRoot);

        Assert.AreEqual(1, result.Sequence);
    }

    [TestMethod]
    public void VerifyManifest_ImageReleaseWithPerPlatformComponentInventories_IsAccepted()
    {
        using var fixture = SigningFixture.Create();
        var bytes = fixture.ImageManifestBytes(
            DistributionSchemaVersions.ReleaseManifestWithComponentSboms, withInventories: true);

        var manifest = DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot);

        Assert.AreEqual(DistributionSchemaVersions.ReleaseManifestWithComponentSboms, manifest.SchemaVersion);
        Assert.AreEqual(
            2,
            manifest.Artifacts.Count(static artifact => artifact.Role == DistributionArtifactRole.ComponentSbom));
        foreach (var platform in manifest.Images.Single().Platforms)
        {
            Assert.AreEqual($"image-components-linux-{platform.Architecture}.spdx.json", platform.ComponentSbomAsset);
        }
    }

    /// <summary>
    /// The shape published before component inventories existed stays verifiable unchanged, so a release signed
    /// under version 1 is not invalidated by the addition.
    /// </summary>
    [TestMethod]
    public void VerifyManifest_ImageReleaseWithoutComponentInventories_RemainsAccepted()
    {
        using var fixture = SigningFixture.Create();
        var bytes = fixture.ImageManifestBytes(DistributionSchemaVersions.ReleaseManifest);

        var manifest = DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot);

        Assert.AreEqual(DistributionSchemaVersions.ReleaseManifest, manifest.SchemaVersion);
        Assert.IsFalse(manifest.Artifacts.Any(static artifact => artifact.Role == DistributionArtifactRole.ComponentSbom));
        Assert.IsTrue(manifest.Images.Single().Platforms.All(static platform => platform.ComponentSbomAsset is null));
    }

    [TestMethod]
    public void VerifyManifest_Version1ThatDeclaresAComponentInventory_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var bytes = fixture.ImageManifestBytes(DistributionSchemaVersions.ReleaseManifest, withInventories: true);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_ComponentInventoryVersionWithoutInventories_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var bytes = fixture.ImageManifestBytes(DistributionSchemaVersions.ReleaseManifestWithComponentSboms);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    /// <summary>
    /// An inventory built for one architecture must not be presentable as another's, or a CVE triage would read
    /// the wrong image's component list.
    /// </summary>
    [TestMethod]
    public void VerifyManifest_PlatformNamingTheOtherArchitecturesInventory_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var manifest = fixture.ImageManifest(
            DistributionSchemaVersions.ReleaseManifestWithComponentSboms, withInventories: true);
        var image = manifest.Images[0];
        var swapped = manifest with
        {
            Images =
            [
                image with
                {
                    Platforms =
                    [
                        image.Platforms[0] with { ComponentSbomAsset = image.Platforms[1].ComponentSbomAsset },
                        image.Platforms[1] with { ComponentSbomAsset = image.Platforms[0].ComponentSbomAsset }
                    ]
                }
            ]
        };
        var bytes = SigningFixture.Serialize(swapped);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_InstallerReleaseAtTheComponentInventoryVersion_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var bytes = SigningFixture.Serialize(fixture.Manifest() with
        {
            SchemaVersion = DistributionSchemaVersions.ReleaseManifestWithComponentSboms
        });

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_UnsupportedFutureSchemaVersion_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var bytes = SigningFixture.Serialize(fixture.Manifest() with
        {
            SchemaVersion = DistributionSchemaVersions.MaximumReleaseManifest + 1
        });

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    /// <summary>
    /// The compatibility guarantee this change makes is about bytes that were signed before the change existed,
    /// so it is asserted against a literal version 1 document that has no <c>componentSbomAsset</c> member at all.
    /// Serializing the current record would emit that member as null and prove nothing about the historical shape.
    /// </summary>
    [TestMethod]
    public void VerifyManifest_FrozenVersion1BytesWithoutTheComponentSbomMember_AreAccepted()
    {
        using var fixture = SigningFixture.Create();
        var bytes = Encoding.UTF8.GetBytes(FrozenVersion1ImageManifest(fixture.TrustRoot.KeyId));
        StringAssert.DoesNotMatch(
            Encoding.UTF8.GetString(bytes),
            ComponentSbomMember(),
            "The frozen fixture must not contain the member added by this change.");

        var manifest = DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot);

        Assert.AreEqual(DistributionSchemaVersions.ReleaseManifest, manifest.SchemaVersion);
        Assert.AreEqual(2, manifest.Images.Single().Platforms.Count);
        Assert.IsTrue(manifest.Images.Single().Platforms.All(static platform => platform.ComponentSbomAsset is null));
    }

    /// <summary>An unknown member is still rejected, so the frozen fixture is not passing through a loosened parser.</summary>
    [TestMethod]
    public void VerifyManifest_FrozenVersion1BytesWithAnUnknownMember_AreStillRejected()
    {
        using var fixture = SigningFixture.Create();
        var bytes = Encoding.UTF8.GetBytes(
            FrozenVersion1ImageManifest(fixture.TrustRoot.KeyId).Replace(
                "\"manifestKind\"", "\"unexpectedMember\": 1, \"manifestKind\"", StringComparison.Ordinal));

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_InstallerReleaseAtVersion1CarryingAComponentInventory_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var installer = fixture.Manifest();
        var bytes = SigningFixture.Serialize(installer with
        {
            Artifacts =
            [
                .. installer.Artifacts,
                new DistributionArtifact(
                    DistributionArtifactRole.ComponentSbom,
                    "installer-components.spdx.json",
                    "application/spdx+json",
                    1,
                    new string('c', 64))
            ]
        });

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_CatalogReleaseAtTheComponentInventoryVersion_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var bytes = SigningFixture.Serialize(fixture.CatalogManifest("hyg-v4.2-p3-s2-r1") with
        {
            SchemaVersion = DistributionSchemaVersions.ReleaseManifestWithComponentSboms
        });

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    /// <summary>
    /// The verifier ships inside the installer, so a release newer than this build must say that the installer is
    /// what needs upgrading rather than reporting a generic identity failure.
    /// </summary>
    [TestMethod]
    public void VerifyManifest_UnsupportedFutureSchemaVersion_NamesTheSupportedRangeAndTheRemedy()
    {
        using var fixture = SigningFixture.Create();
        var bytes = SigningFixture.Serialize(fixture.Manifest() with
        {
            SchemaVersion = DistributionSchemaVersions.MaximumReleaseManifest + 1
        });

        var exception = Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));

        StringAssert.Contains(exception.Message, "Upgrade the installer", StringComparison.Ordinal);
        StringAssert.Contains(
            exception.Message,
            $"version {DistributionSchemaVersions.MaximumReleaseManifest + 1}",
            StringComparison.Ordinal);
    }

    [System.Text.RegularExpressions.GeneratedRegex("componentSbomAsset")]
    private static partial System.Text.RegularExpressions.Regex ComponentSbomMember();

    /// <summary>
    /// A release manifest in exactly the shape published before per-platform component inventories existed. It is
    /// a literal document rather than a serialization of the current record, so it keeps its historical member set
    /// even as the record gains optional members.
    /// </summary>
    private static string FrozenVersion1ImageManifest(string keyId) =>
        $$"""
        {
          "schemaVersion": 1,
          "manifestKind": "ImageRelease",
          "release": {
            "train": "image",
            "version": "1.0.0",
            "tag": "image-v1.0.0",
            "repository": "RoySalisbury/HVO.SkyMonitor",
            "sourceRevision": "{{new string('a', 40)}}",
            "sourceTree": "{{new string('b', 40)}}",
            "createdUtc": "2026-08-24T00:00:00+00:00"
          },
          "signing": { "algorithm": "ecdsa-p256-sha256-p1363", "keyId": "{{keyId}}" },
          "artifacts": [
            { "role": "ImageArchive", "assetName": "cameraagent-image-v1.0.0-linux-amd64.tar", "mediaType": "application/x-tar", "length": 1, "sha256": "{{new string('c', 64)}}", "operatingSystem": "linux", "architecture": "amd64" },
            { "role": "ImageArchive", "assetName": "cameraagent-image-v1.0.0-linux-arm64.tar", "mediaType": "application/x-tar", "length": 1, "sha256": "{{new string('c', 64)}}", "operatingSystem": "linux", "architecture": "arm64" },
            { "role": "Checksums", "assetName": "SHA256SUMS", "mediaType": "text/plain", "length": 1, "sha256": "{{new string('c', 64)}}", "operatingSystem": null, "architecture": null },
            { "role": "Sbom", "assetName": "image-sbom.spdx.json", "mediaType": "application/spdx+json", "length": 1, "sha256": "{{new string('c', 64)}}", "operatingSystem": null, "architecture": null },
            { "role": "Provenance", "assetName": "image-provenance.json", "mediaType": "application/json", "length": 1, "sha256": "{{new string('c', 64)}}", "operatingSystem": null, "architecture": null },
            { "role": "VulnerabilityScan", "assetName": "image-vulnerability-scan.json", "mediaType": "application/json", "length": 1, "sha256": "{{new string('c', 64)}}", "operatingSystem": null, "architecture": null },
            { "role": "License", "assetName": "THIRD-PARTY-NOTICES.md", "mediaType": "text/markdown", "length": 1, "sha256": "{{new string('c', 64)}}", "operatingSystem": null, "architecture": null }
          ],
          "catalog": null,
          "images": [
            {
              "component": "CameraAgent",
              "repository": "ghcr.io/roysalisbury/hvo.skymonitor/cameraagent",
              "manifestDigest": "sha256:{{new string('1', 64)}}",
              "sourceRevision": "{{new string('a', 40)}}",
              "sourceTree": "{{new string('b', 40)}}",
              "platforms": [
                { "operatingSystem": "linux", "architecture": "amd64", "manifestDigest": "sha256:{{new string('2', 64)}}", "offlineArchiveAsset": "cameraagent-image-v1.0.0-linux-amd64.tar", "offlineArchiveImageId": "sha256:{{new string('4', 64)}}" },
                { "operatingSystem": "linux", "architecture": "arm64", "manifestDigest": "sha256:{{new string('3', 64)}}", "offlineArchiveAsset": "cameraagent-image-v1.0.0-linux-arm64.tar", "offlineArchiveImageId": "sha256:{{new string('5', 64)}}" }
              ],
              "provenanceAsset": "image-provenance.json",
              "sbomAsset": "image-sbom.spdx.json",
              "vulnerabilityScanAsset": "image-vulnerability-scan.json",
              "compatibility": {
                "stateContract": "cameraagent-state-v2",
                "minimumCompatibleRevision": "{{new string('a', 40)}}",
                "identityMigration": "20260827053715_InitialIdentity",
                "rawIngressSchema": 12,
                "catalogManifestVersion": 2,
                "configurationContract": "cameraagent-install-v1",
                "catalogContract": "hyg-v42-production-p3-s2",
                "replayRunnerContract": "local-replay-runner-v1"
              }
            }
          ]
        }
        """;

    private sealed class SigningFixture(ECDsa key, DistributionTrustRoot trustRoot) : IDisposable
    {
        public DistributionTrustRoot TrustRoot { get; } = trustRoot;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The fixture owns and disposes the generated signing key.")]
        public static SigningFixture Create()
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return new SigningFixture(key, DistributionTrustRoot.FromPem(key.ExportSubjectPublicKeyInfoPem()));
        }

        public DistributionReleaseManifest Manifest() => new(
            DistributionSchemaVersions.ReleaseManifest,
            DistributionManifestKind.InstallerRelease,
            new DistributionReleaseIdentity(
                "installer",
                "1.0.0",
                "installer-v1.0.0",
                "RoySalisbury/HVO.SkyMonitor",
                new string('a', 40),
                new string('b', 40),
                DateTimeOffset.Parse("2026-08-24T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, TrustRoot.KeyId),
            [
                Artifact(DistributionArtifactRole.Installer, "hvo-skymonitor-installer-v1.0.0-linux-x64.tar.gz"),
                Artifact(DistributionArtifactRole.Installer, "hvo-skymonitor-installer-v1.0.0-linux-arm64.tar.gz") with
                {
                    OperatingSystem = "linux",
                    Architecture = "arm64"
                },
                Artifact(DistributionArtifactRole.Checksums, "SHA256SUMS"),
                Artifact(DistributionArtifactRole.Sbom, "sbom.spdx.json"),
                Artifact(DistributionArtifactRole.Provenance, "installer-provenance.json")
            ],
            null,
            []);

        public byte[] ManifestBytes()
            => JsonSerializer.SerializeToUtf8Bytes(Manifest(), DistributionJsonContext.Default.DistributionReleaseManifest);

        public DistributionReleaseManifest CatalogManifest(string version) => new(
            DistributionSchemaVersions.ReleaseManifest,
            DistributionManifestKind.CatalogRelease,
            new DistributionReleaseIdentity(
                "catalog",
                version,
                $"catalog-{version}",
                "RoySalisbury/HVO.SkyMonitor",
                new string('a', 40),
                new string('b', 40),
                DateTimeOffset.Parse("2026-08-24T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, TrustRoot.KeyId),
            [
                Artifact(DistributionArtifactRole.CatalogBundle, $"{version}.bundle.tar.gz"),
                Artifact(DistributionArtifactRole.Checksums, "SHA256SUMS"),
                Artifact(DistributionArtifactRole.Sbom, "catalog-sbom.spdx.json"),
                Artifact(DistributionArtifactRole.Provenance, "catalog-provenance.json"),
                Artifact(DistributionArtifactRole.License, "LICENSE-HYG.md"),
                Artifact(DistributionArtifactRole.Attribution, "ATTRIBUTION-HYG.md")
            ],
            new DistributionCatalogIdentity(
                "hyg-v42-production",
                version,
                "production",
                2,
                "2",
                "3",
                new string('d', 64),
                new string('e', 64),
                1,
                1,
                "MIT",
                "LICENSE-HYG.md",
                "ATTRIBUTION-HYG.md",
                "topology",
                new string('f', 64)),
            []);

        /// <summary>
        /// Builds an image release manifest in either published shape. Version 1 is the shape released before
        /// per-platform component inventories existed; version 2 adds one inventory per platform, named by the
        /// platform it describes.
        /// </summary>
        public DistributionReleaseManifest ImageManifest(int schemaVersion, bool withInventories = false) => new(
            schemaVersion,
            DistributionManifestKind.ImageRelease,
            new DistributionReleaseIdentity(
                "image",
                "1.0.0",
                "image-v1.0.0",
                "RoySalisbury/HVO.SkyMonitor",
                new string('a', 40),
                new string('b', 40),
                DateTimeOffset.Parse("2026-08-24T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, TrustRoot.KeyId),
            [
                ImageArtifact(DistributionArtifactRole.ImageArchive, "cameraagent-image-v1.0.0-linux-amd64.tar", "amd64"),
                ImageArtifact(DistributionArtifactRole.ImageArchive, "cameraagent-image-v1.0.0-linux-arm64.tar", "arm64"),
                .. withInventories
                    ? (DistributionArtifact[])
                    [
                        ImageArtifact(DistributionArtifactRole.ComponentSbom, "image-components-linux-amd64.spdx.json", "amd64"),
                        ImageArtifact(DistributionArtifactRole.ComponentSbom, "image-components-linux-arm64.spdx.json", "arm64")
                    ]
                    : [],
                Artifact(DistributionArtifactRole.Checksums, "SHA256SUMS"),
                Artifact(DistributionArtifactRole.Sbom, "image-sbom.spdx.json"),
                Artifact(DistributionArtifactRole.Provenance, "image-provenance.json"),
                Artifact(DistributionArtifactRole.VulnerabilityScan, "image-vulnerability-scan.json"),
                Artifact(DistributionArtifactRole.License, "THIRD-PARTY-NOTICES.md")
            ],
            null,
            [
                new DistributionImageIdentity(
                    "CameraAgent",
                    "ghcr.io/roysalisbury/hvo.skymonitor/cameraagent",
                    "sha256:" + new string('1', 64),
                    new string('a', 40),
                    new string('b', 40),
                    [
                        ImagePlatform("amd64", '2', withInventories),
                        ImagePlatform("arm64", '3', withInventories)
                    ],
                    "image-provenance.json",
                    "image-sbom.spdx.json",
                    "image-vulnerability-scan.json",
                    new DistributionImageCompatibility(
                        "cameraagent-state-v2",
                        new string('a', 40),
                        "20260827053715_InitialIdentity",
                        12,
                        2,
                        "cameraagent-install-v1",
                        "hyg-v42-production-p3-s2",
                        "local-replay-runner-v1"))
            ]);

        public byte[] ImageManifestBytes(int schemaVersion, bool withInventories = false)
            => JsonSerializer.SerializeToUtf8Bytes(
                ImageManifest(schemaVersion, withInventories),
                DistributionJsonContext.Default.DistributionReleaseManifest);

        public static byte[] Serialize(DistributionReleaseManifest manifest)
            => JsonSerializer.SerializeToUtf8Bytes(manifest, DistributionJsonContext.Default.DistributionReleaseManifest);

        private static DistributionImagePlatform ImagePlatform(string architecture, char digestFill, bool withInventory)
            => new(
                "linux",
                architecture,
                "sha256:" + new string(digestFill, 64),
                $"cameraagent-image-v1.0.0-linux-{architecture}.tar",
                "sha256:" + new string(digestFill == '2' ? '4' : '5', 64),
                withInventory ? $"image-components-linux-{architecture}.spdx.json" : null);

        private static DistributionArtifact ImageArtifact(DistributionArtifactRole role, string name, string architecture)
            => new(role, name, "application/octet-stream", 1, new string('c', 64), "linux", architecture);

        public byte[] Sign(ReadOnlySpan<byte> bytes)
            => Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(
                bytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) + "\n");

        public void Dispose() => key.Dispose();

        private static DistributionArtifact Artifact(DistributionArtifactRole role, string name)
            => new(
                role,
                name,
                "application/octet-stream",
                1,
                new string('c', 64),
                role == DistributionArtifactRole.Installer ? "linux" : null,
                role == DistributionArtifactRole.Installer ? "x64" : null);
    }
}
